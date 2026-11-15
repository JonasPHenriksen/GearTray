using System.Drawing.Imaging;
using System.Drawing.Text;
using GearTray.Devices;
using GearTray.Util;

namespace GearTray.Effects;

/// <summary>
/// What the base-station screen needs from the tray. Game is the running game's name, if any. The *Seconds values are
/// how long each page stays up when more than one page applies. PingHost is the server to ping (empty: none);
/// WeatherLatitude/Longitude the place for the home page's weather (null: none); MouseBattery the mouse's last known
/// battery (null: no mouse, or not read yet).
/// </summary>
public sealed record DashboardData(string? Game = null, DateTime? GameStarted = null, bool ShowMedia = true, bool ShowStats = true,
    int HomeSeconds = 8, int MediaSeconds = 6, int StatsSeconds = 8, uint GamePid = 0, string? PingHost = null,
    double? WeatherLatitude = null, double? WeatherLongitude = null, BatteryInfo? MouseBattery = null);

public enum ScreenPage { Home, Media, Stats }

/// <summary>
/// GearTray's own screen on the Arctis base station's 128×64 OLED. The home page (clock, date, network speed, volume,
/// headset and mouse battery, weather) is always there; "now playing" joins while something plays and PC stats while a
/// game runs, taking turns for the number of seconds set per page, and a new song jumps straight to its page. Pop-ups
/// (game mode, profile, controller) cut in for a few seconds. Composed five times a second but only sent when the
/// picture changed (the now-playing wave moves; everything else changes about once a second);
/// steps aside for ~3.5 s after a knob is turned (the base station shows its own volume screen then), shifts the
/// picture by a pixel every minute against burn-in, and hands the screen back to the base station when stopped.
/// </summary>
public sealed partial class BaseStationScreen : IDisposable
{
    private const int W = ArctisNovaProWireless.ScreenWidth, H = ArctisNovaProWireless.ScreenHeight;
    private const int TickMs = 200;
    private readonly DeviceManager _devices;
    private readonly Func<DashboardData> _data;
    private readonly object _popupGate = new();
    private System.Threading.Timer? _timer;
    private volatile bool _running;
    private int _busy;
    private ScreenPage _page = ScreenPage.Home;
    private DateTime _pageSince;
    private string? _lastTrack;
    private (string Title, string Text, DateTime Until)? _popup;

    // Readings refreshed once a second (the timer ticks five times a second).
    private DateTime _slowAt;
    private MediaInfo? _media;
    private PcStatsInfo? _stats;
    private (double? Down, double? Up) _net;
    // The sound meter, read every tick while the home or the now-playing page shows, so the now-playing wave (the
    // peaks of the last W/2 ticks) is already full when that page comes up.
    private AudioMeter? _meter;
    private readonly List<float> _wave = new(W / 2 + 1);
    // Headset state read on the device thread every 3 s and after the dial is turned.
    private volatile LiveHeadset? _live;
    private DateTime _liveReadAt, _nextLiveRead;
    // The last picture sent, to skip unchanged frames.
    private bool[]? _lastFrame;
    private DateTime _lastSentAt;
    private volatile bool _forceSend;

    private sealed record LiveHeadset(bool Online, int? Volume, BatteryInfo? Battery);

    // The server pinged for the home page, once a second in the background along with the other readings. It turns
    // Off only after two missed answers in a row, so a single lost packet doesn't flash it.
    private volatile string? _pingHost;
    private volatile bool _pingKnown, _pingOn;
    private int _pinging, _pingMisses;

    // The weather for the home page, asked for in the background every 15 minutes and right away when the place
    // changes. After a failure it tries again in 2 minutes (logged once per run of failures); the last answer is kept
    // for up to 2 hours.
    private volatile string? _weatherPlace;
    private volatile WeatherNow? _weather;
    private DateTime _weatherNext, _weatherGot;
    private int _weatherBusy;
    private volatile bool _weatherFailing;

    public BaseStationScreen(DeviceManager devices, Func<DashboardData> data)
    {
        _devices = devices;
        _data = data;
    }

    public bool Running => _running;

    public void Start()
    {
        if (_running) return;
        _running = true;
        _lastFrame = null;
        _nextLiveRead = DateTime.MinValue;
        _timer = new System.Threading.Timer(_ => Tick(), null, 500, TickMs);
        Log.Info("Base station screen: GearTray screen on");
    }

    /// <summary>Stop drawing and give the screen back to the base station.</summary>
    public void Stop()
    {
        if (!Halt()) return;
        Restore();
        Log.Info("Base station screen: back to the normal screen");
    }

    /// <summary>
    /// Stop drawing but leave the screen black instead of handing it back (the PC is going to sleep: the base station's
    /// own screen would otherwise stay up for hours and burn in). Turning a knob still shows its volume screen.
    /// </summary>
    public void StopDark()
    {
        if (!Halt()) return;
        _ = _devices.RunAsync(() =>
        {
            try
            {
                if (_devices.Get<ArctisNovaProWireless>() is { Connected: true, CanDrawScreen: true } h) h.DrawScreen(new bool[W * H]);
            }
            catch (Exception ex) { Log.Warn("Base station screen (dark): " + ex.Message); }
        });
        Log.Info("Base station screen: left dark while the PC sleeps");
    }

    private bool Halt()
    {
        if (!_running) return false;
        _running = false;
        _timer?.Dispose();
        _timer = null;
        FpsCounter.Stop();
        _meter?.Dispose();
        _meter = null;
        Interlocked.Exchange(ref _testUntilTicks, 0);
        return true;
    }

    // Burn-in test: every pixel lit until this moment (UTC ticks; 0 = no test).
    private long _testUntilTicks;

    public bool TestPatternShowing => DateTime.UtcNow.Ticks < Interlocked.Read(ref _testUntilTicks);

    /// <summary>Light every pixel for a while: burned-in areas show up as darker shapes.</summary>
    public void ShowTestPattern(TimeSpan length)
    {
        if (!_running) return;
        Interlocked.Exchange(ref _testUntilTicks, DateTime.UtcNow.Add(length).Ticks);
        ThreadPool.QueueUserWorkItem(_ => Tick());
    }

    public void StopTestPattern()
    {
        Interlocked.Exchange(ref _testUntilTicks, 0);
        if (_running) ThreadPool.QueueUserWorkItem(_ => Tick());
    }

    /// <summary>Hand the screen back right now (also used at startup after an unclean exit).</summary>
    public void Restore()
    {
        _ = _devices.RunAsync(() =>
        {
            try
            {
                if (_devices.Get<ArctisNovaProWireless>() is { Connected: true } h) h.RestoreScreen();
            }
            catch (Exception ex) { Log.Warn("Base station screen restore: " + ex.Message); }
        });
    }

    /// <summary>Show a short message ("GAME MODE" / "On") for a few seconds, then carry on.</summary>
    public void Popup(string title, string text, double seconds = 3)
    {
        if (!_running) return;
        lock (_popupGate) _popup = (title, text, DateTime.UtcNow.AddSeconds(seconds));
        ThreadPool.QueueUserWorkItem(_ => Tick());
    }

    private void Tick()
    {
        if (!_running || Interlocked.Exchange(ref _busy, 1) == 1) return;
        bool[] frame;
        try { frame = Compose(_data(), DateTime.Now); }
        catch (Exception ex) { Log.Warn("Base station screen render: " + ex.Message); Volatile.Write(ref _busy, 0); return; }

        // Send only the halves that changed; everything after a pause, after
        // the base station showed its own screen, or every 10 s as a keep-alive.
        var nowUtc = DateTime.UtcNow;
        var last = _lastFrame;
        bool full = _forceSend || last is null || nowUtc - _lastSentAt >= TimeSpan.FromSeconds(10);
        bool sendLeft = full || HalfDiffers(frame, last!, 0), sendRight = full || HalfDiffers(frame, last!, 1);
        bool unchanged = !sendLeft && !sendRight;
        bool liveDue = nowUtc >= _nextLiveRead;
        if (unchanged && !liveDue) { Volatile.Write(ref _busy, 0); return; }

        _ = _devices.RunAsync(() =>
        {
            try
            {
                if (!_running || _devices.Get<ArctisNovaProWireless>() is not { Connected: true, CanDrawScreen: true } h) return;
                h.DrainEvents();
                if (liveDue || h.LastKnobUse > _liveReadAt)
                {
                    var (online, volume, battery) = h.ReadLive();
                    _live = new LiveHeadset(online, volume, battery);
                    _liveReadAt = DateTime.UtcNow;
                    _nextLiveRead = _liveReadAt.AddSeconds(3);
                }
                if (DateTime.UtcNow - h.LastKnobUse < TimeSpan.FromSeconds(3.5))
                {
                    _forceSend = true; // the base station is showing its volume screen; redraw ours once it's done
                    return;
                }
                if (unchanged) return;
                h.DrawScreen(frame, sendLeft, sendRight);
                _lastFrame = frame;
                _lastSentAt = DateTime.UtcNow;
                _forceSend = false;
            }
            catch (Exception ex) { Log.Warn("Base station screen: " + ex.Message); }
            finally { Volatile.Write(ref _busy, 0); }
        });
    }

    /// <summary>Pick the page for this moment and draw it.</summary>
    private bool[] Compose(DashboardData d, DateTime now)
    {
        if (TestPatternShowing) { var white = new bool[W * H]; Array.Fill(white, true); return white; }
        (string Title, string Text, DateTime Until)? popup;
        lock (_popupGate) popup = _popup;
        if (popup is { } p && DateTime.UtcNow < p.Until) return RenderPopup(p.Title, p.Text, now);

        bool statsOn = d.ShowStats && d.Game is not null;
        if (DateTime.UtcNow - _slowAt >= TimeSpan.FromSeconds(1))
        {
            _slowAt = DateTime.UtcNow;
            _media = d.ShowMedia ? MediaSession.Read() : null;
            FpsCounter.Watch(statsOn ? d.GamePid : 0);
            _stats = null;
            if (statsOn)
            {
                var (fps, low, avg) = FpsCounter.Read();
                _stats = PcStats.Read() with { Fps = fps, FpsLow = low, FpsAvg = avg };
            }
            _net = NetworkSpeed.Read();
            if (!string.IsNullOrWhiteSpace(d.PingHost)) Ping(d.PingHost.Trim());
            else _pingHost = null;
            RefreshWeather(d.WeatherLatitude, d.WeatherLongitude);
        }
        var media = _media;
        bool mediaOn = media is { Playing: true } && (media.Title.Length > 0 || media.Artist.Length > 0);

        if (mediaOn)
        {
            string track = media!.Title + "\n" + media.Artist;
            if (track != _lastTrack) { _page = ScreenPage.Media; _pageSince = DateTime.UtcNow; } // new song: show it now
            _lastTrack = track;
        }
        _page = NextPage(_page, mediaOn, statsOn && _stats is not null, d);
        bool showMedia = _page == ScreenPage.Media && mediaOn, showStats = _page == ScreenPage.Stats && _stats is not null;
        if (!showStats) ReadMeter();
        if (showMedia) return RenderMedia(media!, _wave, now);
        if (showStats) return RenderStats(d, _stats!, now);
        return RenderHome(HomeNow(d), now);
    }

    /// <summary>One reading of the sound meter, for the now-playing wave.</summary>
    private void ReadMeter()
    {
        _meter ??= new AudioMeter();
        var (left, right) = _meter.Read();
        _wave.Add(Math.Max(left, right));
        if (_wave.Count > W / 2) _wave.RemoveAt(0);
    }

    /// <summary>The home page's live values.</summary>
    private HomeData HomeNow(DashboardData d)
    {
        var live = _live;
        var server = string.IsNullOrWhiteSpace(d.PingHost) ? ServerState.NotSet
            : !_pingKnown ? ServerState.Checking : _pingOn ? ServerState.On : ServerState.Off;
        return new HomeData(live?.Online ?? false, live?.Volume, _net.Down, _net.Up, server,
            WeatherSet: _weatherPlace is not null, Weather: _weather, HeadsetBattery: live?.Battery, MouseBattery: d.MouseBattery);
    }

    /// <summary>Called once a second: starts a background request for the weather when one is due.</summary>
    private void RefreshWeather(double? latitude, double? longitude)
    {
        if (latitude is not double lat || longitude is not double lon)
        {
            _weatherPlace = null;
            _weather = null;
            return;
        }
        var now = DateTime.UtcNow;
        string place = FormattableString.Invariant($"{lat:0.####},{lon:0.####}");
        if (place != _weatherPlace)
        {
            _weatherPlace = place;
            _weather = null;
            _weatherNext = now;
        }
        if (_weather is not null && now - _weatherGot > TimeSpan.FromHours(2)) _weather = null; // too old to show
        if (now < _weatherNext || Interlocked.Exchange(ref _weatherBusy, 1) == 1) return;
        _weatherNext = now.AddMinutes(15);
        _ = Task.Run(async () =>
        {
            try
            {
                var weather = await Weather.Current(lat, lon);
                if (place != _weatherPlace) return; // the place was changed meanwhile; its own request follows
                _weather = weather;
                _weatherGot = DateTime.UtcNow;
                _weatherFailing = false;
            }
            catch (Exception ex)
            {
                if (!_weatherFailing) Log.Warn("Weather: " + ex.Message);
                _weatherFailing = true;
                _weatherNext = DateTime.UtcNow.AddMinutes(2);
            }
            finally { Volatile.Write(ref _weatherBusy, 0); }
        });
    }

    /// <summary>
    /// Pings the server in the background (a 1 s timeout, so a server that's down never stalls the screen); called once
    /// a second, and skipped while the last ping is still out.
    /// </summary>
    private void Ping(string host)
    {
        if (host != _pingHost)
        {
            _pingHost = host;
            _pingKnown = _pingOn = false;
            _pingMisses = 0;
        }
        if (Interlocked.Exchange(ref _pinging, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            bool answered = false;
            try
            {
                using var ping = new System.Net.NetworkInformation.Ping();
                answered = (await ping.SendPingAsync(host, 1000)).Status == System.Net.NetworkInformation.IPStatus.Success;
            }
            catch (Exception) { } // unknown name, no network
            finally { Volatile.Write(ref _pinging, 0); }
            if (host != _pingHost) return; // the address was changed meanwhile; its own ping follows
            if (answered) { _pingMisses = 0; _pingOn = _pingKnown = true; }
            else if (++_pingMisses >= 2) { _pingOn = false; _pingKnown = true; }
        });
    }

    private static bool HalfDiffers(bool[] a, bool[] b, int half)
    {
        for (int y = 0; y < H; y++)
            if (!a.AsSpan(y * W + half * 64, 64).SequenceEqual(b.AsSpan(y * W + half * 64, 64))) return true;
        return false;
    }

    private ScreenPage NextPage(ScreenPage current, bool media, bool stats, DashboardData d)
    {
        ScreenPage[] order = stats ? [ScreenPage.Stats, ScreenPage.Home, ScreenPage.Media] : [ScreenPage.Home, ScreenPage.Media];
        var eligible = order.Where(p => p == ScreenPage.Home || (p == ScreenPage.Media && media) || (p == ScreenPage.Stats && stats)).ToList();
        var now = DateTime.UtcNow;
        if (!eligible.Contains(current)) { _pageSince = now; return eligible[0]; }
        if (eligible.Count == 1 || now - _pageSince < Dwell(current, d)) return current;
        _pageSince = now;
        return eligible[(eligible.IndexOf(current) + 1) % eligible.Count];
    }

    /// <summary>How long a page stays up, as set on the Headset tab (2–300 s).</summary>
    private static TimeSpan Dwell(ScreenPage page, DashboardData d) => TimeSpan.FromSeconds(Math.Clamp(page switch
    {
        ScreenPage.Stats => d.StatsSeconds,
        ScreenPage.Media => d.MediaSeconds,
        _ => d.HomeSeconds,
    }, 2, 300));

    // ---------- pages ----------

    // Now playing: BaseStationScreen.Media.cs

    /// <summary>
    /// PC stats while a game runs: the game and how long it has been running; CPU | GPU | FPS columns with the
    /// temperatures (or the 1% low) beside the labels and the numbers as big as their column allows; VRAM, RAM and
    /// the average FPS under them.
    /// </summary>
    public static bool[] RenderStats(DashboardData d, PcStatsInfo s, DateTime now) => Draw(now, g =>
    {
        using var head = new Font("Segoe UI Semibold", 11f, GraphicsUnit.Pixel);
        using var small = new Font("Segoe UI", 10f, GraphicsUnit.Pixel);
        using var oneLine = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        using var centred = new StringFormat { Alignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };

        string played = d.GameStarted is DateTime started ? Played(now - started) : "";
        float playedWidth = played.Length > 0 ? g.MeasureString(played, small).Width : 0;
        g.DrawString(d.Game ?? "", head, Brushes.White, new RectangleF(0, -1, W - playedWidth - 3, 15), oneLine);
        if (played.Length > 0) g.DrawString(played, small, Brushes.White, W - playedWidth, 0); // two pixels in from the edge

        Column(g, small, 0, s.CpuTemp is int ct ? $"CPU {ct}°" : "CPU", s.Cpu.ToString(), "%");
        Column(g, small, 44, s.GpuTemp is int gt ? $"GPU {gt}°" : "GPU", s.Gpu is int gp ? gp.ToString() : "–", s.Gpu is null ? "" : "%");
        Column(g, small, 88, s.FpsLow is int low ? $"1% {low}" : "FPS", s.Fps is int fp ? fp.ToString() : "–", "");

        // Bottom line in three parts, one under each column: VRAM and RAM in GB (no unit, to fit) and the average FPS.
        string avg = s.FpsAvg is int a ? $"avg {a}" : "avg –";
        string ram = $"RAM {s.RamUsedGb:0.0}";
        if (s.VramUsedGb is double vram)
        {
            g.DrawString($"VRAM {vram:0.0}", small, Brushes.White, new RectangleF(-2, 51, 48, 13), centred);
            g.DrawString(ram, small, Brushes.White, new RectangleF(42, 51, 48, 13), centred);
            g.DrawString(avg, small, Brushes.White, new RectangleF(86, 51, 44, 13), centred);
        }
        else
        {
            g.DrawString(ram, small, Brushes.White, new RectangleF(0, 51, W / 2f, 13), centred);
            g.DrawString(avg, small, Brushes.White, new RectangleF(W / 2f, 51, W / 2f, 13), centred);
        }
    });

    /// <summary>
    /// One column of the stats page: the small label, then the number in the largest size (up to 30 px) that
    /// fits 37 of the column's 44 pixels, with its unit ("%") at half size on the same baseline. "100%" just comes out
    /// a little smaller than "97%".
    /// </summary>
    private static void Column(Graphics g, Font small, float x, string caption, string number, string unit)
    {
        g.DrawString(caption, small, Brushes.White, x, 13);
        var format = StringFormat.GenericTypographic;
        for (float size = 30; size >= 12; size -= 1)
        {
            using var font = new Font("Segoe UI Semibold", size, GraphicsUnit.Pixel);
            using var unitFont = new Font("Segoe UI Semibold", Math.Max(9f, size * 0.5f), GraphicsUnit.Pixel);
            float numberWidth = g.MeasureString(number, font, PointF.Empty, format).Width;
            float unitWidth = unit.Length > 0 ? g.MeasureString(unit, unitFont, PointF.Empty, format).Width + 1 : 0;
            if (numberWidth + unitWidth > 37 && size > 12) continue;
            // Everything sits on one baseline, just above the memory line.
            const float baseline = 47;
            g.DrawString(number, font, Brushes.White, x, baseline - Ascent(font), format);
            if (unit.Length > 0) g.DrawString(unit, unitFont, Brushes.White, x + numberWidth + 1, baseline - Ascent(unitFont), format);
            return;
        }
    }

    private static float Ascent(Font f) => f.FontFamily.GetCellAscent(f.Style) * f.Size / f.FontFamily.GetEmHeight(f.Style);

    /// <summary>A short message in the middle of the screen.</summary>
    public static bool[] RenderPopup(string title, string text, DateTime now) => Draw(now, g =>
    {
        using var small = new Font("Segoe UI", 10f, GraphicsUnit.Pixel);
        using var big = new Font("Segoe UI Semibold", 18f, GraphicsUnit.Pixel);
        using var center = new StringFormat
        {
            Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap,
        };
        g.DrawString(title.ToUpperInvariant(), small, Brushes.White, new RectangleF(0, 6, W, 14), center);
        g.DrawString(text, big, Brushes.White, new RectangleF(0, 22, W, 30), center);
    });

    // ---------- drawing helpers ----------

    private static string Time(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

    private static string Played(TimeSpan t) => t.TotalMinutes < 1 ? "now" : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : $"{t.Minutes}m";

    /// <summary>Draw into a 1-bit 128×64 frame (pixels[x + y*128]), moved by a pixel each minute against burn-in.</summary>
    private static bool[] Draw(DateTime now, Action<Graphics> paint)
    {
        using var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Black);
            g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
            int shift = now.Minute % 4;
            g.TranslateTransform(shift is 1 or 2 ? 1 : 0, shift >= 2 ? 1 : 0);
            paint(g);
        }
        var data = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new bool[W * H];
            unsafe
            {
                for (int y = 0; y < H; y++)
                {
                    byte* row = (byte*)data.Scan0 + y * data.Stride;
                    for (int x = 0; x < W; x++) pixels[x + y * W] = row[x * 4] + row[x * 4 + 1] + row[x * 4 + 2] > 384;
                }
            }
            return pixels;
        }
        finally { bmp.UnlockBits(data); }
    }

    public void Dispose() => Stop();
}
