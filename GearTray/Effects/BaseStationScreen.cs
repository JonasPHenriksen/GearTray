using System.Drawing.Imaging;
using System.Drawing.Text;
using GearTray.Devices;
using GearTray.Util;

namespace GearTray.Effects;

/// <summary>What the base-station screen shows; supplied by the tray. Game is the running game's name, if any.</summary>
public sealed record DashboardData(int? Mouse, bool MouseCharging, int? Headset, bool HeadsetCharging, int? Spare,
    string? Controller, bool GameMode, string? Profile,
    string? Game = null, DateTime? GameStarted = null, bool ShowMedia = true, bool ShowStats = true);

public enum ScreenPage { Clock, Media, Stats }

/// <summary>
/// GearTray's own screen on the Arctis base station's 128×64 OLED. The clock page (time, date, batteries) is always
/// there; "now playing" joins while something plays and PC stats while a game runs, taking turns when both apply
/// (stats get the most time while gaming), and a new song jumps straight to its page. Pop-ups (game mode, profile,
/// controller) cut in for a few seconds. Redraws every second, steps aside for ~3.5 s after a knob is turned (the base
/// station shows its own volume screen then), shifts the picture by a pixel every minute against burn-in, and hands
/// the screen back to the base station when stopped.
/// </summary>
public sealed class BaseStationScreen : IDisposable
{
    private const int W = ArctisNovaProWireless.ScreenWidth, H = ArctisNovaProWireless.ScreenHeight;
    private readonly DeviceManager _devices;
    private readonly Func<DashboardData> _data;
    private readonly object _popupGate = new();
    private System.Threading.Timer? _timer;
    private volatile bool _running;
    private int _busy;
    private ScreenPage _page = ScreenPage.Clock;
    private DateTime _pageSince;
    private string? _lastTrack;
    private (string Title, string Text, DateTime Until)? _popup;

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
        _timer = new System.Threading.Timer(_ => Tick(), null, 500, 1000);
        Log.Info("Base station screen: GearTray screen on");
    }

    /// <summary>Stop drawing and give the screen back to the base station.</summary>
    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _timer?.Dispose();
        _timer = null;
        Restore();
        Log.Info("Base station screen: back to the normal screen");
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
        _ = _devices.RunAsync(() =>
        {
            try
            {
                if (!_running || _devices.Get<ArctisNovaProWireless>() is not { Connected: true, CanDrawScreen: true } h) return;
                h.DrainEvents();
                if (DateTime.UtcNow - h.LastKnobUse < TimeSpan.FromSeconds(3.5)) return; // the base station is showing its volume screen
                h.DrawScreen(frame);
            }
            catch (Exception ex) { Log.Warn("Base station screen: " + ex.Message); }
            finally { Volatile.Write(ref _busy, 0); }
        });
    }

    /// <summary>Pick the page for this second and draw it.</summary>
    private bool[] Compose(DashboardData d, DateTime now)
    {
        (string Title, string Text, DateTime Until)? popup;
        lock (_popupGate) popup = _popup;
        if (popup is { } p && DateTime.UtcNow < p.Until) return RenderPopup(p.Title, p.Text, now);

        var media = d.ShowMedia ? MediaSession.Read() : null;
        bool mediaOn = media is { Playing: true } && (media.Title.Length > 0 || media.Artist.Length > 0);
        bool statsOn = d.ShowStats && d.Game is not null;
        var stats = statsOn ? PcStats.Read() : null;

        if (mediaOn)
        {
            string track = media!.Title + "\n" + media.Artist;
            if (track != _lastTrack) { _page = ScreenPage.Media; _pageSince = DateTime.UtcNow; } // new song: show it now
            _lastTrack = track;
        }
        _page = NextPage(_page, mediaOn, statsOn);
        return _page switch
        {
            ScreenPage.Media when mediaOn => RenderMedia(media!, now),
            ScreenPage.Stats when stats is not null => RenderStats(d, stats, now),
            _ => Render(d, now),
        };
    }

    private ScreenPage NextPage(ScreenPage current, bool media, bool stats)
    {
        ScreenPage[] order = stats ? [ScreenPage.Stats, ScreenPage.Clock, ScreenPage.Media] : [ScreenPage.Clock, ScreenPage.Media];
        var eligible = order.Where(p => p == ScreenPage.Clock || (p == ScreenPage.Media && media) || (p == ScreenPage.Stats && stats)).ToList();
        var now = DateTime.UtcNow;
        if (!eligible.Contains(current)) { _pageSince = now; return eligible[0]; }
        if (eligible.Count == 1 || now - _pageSince < Dwell(current, stats)) return current;
        _pageSince = now;
        return eligible[(eligible.IndexOf(current) + 1) % eligible.Count];
    }

    /// <summary>How long each page stays up: while gaming the stats page gets the most time.</summary>
    private static TimeSpan Dwell(ScreenPage page, bool gaming) => TimeSpan.FromSeconds(page switch
    {
        ScreenPage.Stats => 8,
        ScreenPage.Clock => gaming ? 4 : 10,
        _ => 6,
    });

    // ---------- pages ----------

    /// <summary>The clock page: time, date and every battery level.</summary>
    public static bool[] Render(DashboardData d, DateTime now) => Draw(now, g =>
    {
        using var big = new Font("Segoe UI Semibold", 15f, GraphicsUnit.Pixel);
        using var small = new Font("Segoe UI", 10f, GraphicsUnit.Pixel);
        g.DrawString(now.ToString("HH:mm"), big, Brushes.White, -1, -3);
        var date = now.ToString("ddd d MMM");
        var dateSize = g.MeasureString(date, small);
        g.DrawString(date, small, Brushes.White, W - dateSize.Width - 2, 2);

        int y = 19;
        BatteryLine(g, small, ref y, "Mouse", d.Mouse, d.MouseCharging);
        BatteryLine(g, small, ref y, "Headset", d.Headset, d.HeadsetCharging);
        if (d.Spare is int spare) BatteryLine(g, small, ref y, "Spare", spare, false);
        string bottom = d.Profile is not null ? d.Profile : d.GameMode ? "Game mode" : d.Controller ?? "";
        if (bottom.Length > 0 && y <= H - 11) g.DrawString(bottom, small, Brushes.White, 0, y - 1);
    });

    /// <summary>Now playing: title (two lines), artist, elapsed and remaining time, progress bar.</summary>
    public static bool[] RenderMedia(MediaInfo m, DateTime now) => Draw(now, g =>
    {
        using var titleFont = new Font("Segoe UI Semibold", 12f, GraphicsUnit.Pixel);
        using var small = new Font("Segoe UI", 10f, GraphicsUnit.Pixel);
        using var wrap = new StringFormat { Trimming = StringTrimming.EllipsisWord, FormatFlags = StringFormatFlags.LineLimit };
        using var oneLine = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        string title = m.Title.Length > 0 ? m.Title : m.Artist;
        string second = m.Title.Length > 0 && m.Artist.Length > 0 ? m.Artist : m.App;
        g.DrawString(title, titleFont, Brushes.White, new RectangleF(0, -2, W, 32), wrap);
        g.DrawString(second, small, Brushes.White, new RectangleF(0, 30, W, 13), oneLine);
        if (m.Duration > TimeSpan.Zero)
        {
            g.DrawString(Time(m.Position), small, Brushes.White, 0, 43);
            string left = "-" + Time(m.Duration - m.Position);
            g.DrawString(left, small, Brushes.White, W - g.MeasureString(left, small).Width + 2, 43);
            g.DrawLine(Pens.White, 0, 60, W - 1, 60);
            int done = (int)Math.Round((W - 1) * Math.Clamp(m.Position / m.Duration, 0, 1));
            if (done > 0) g.FillRectangle(Brushes.White, 0, 58, done, 5);
        }
        else
        {
            // The player hasn't said where in the track it is (Firefox only does when you pause or skip).
            g.DrawString("playing for " + Time(m.PlayingFor), small, Brushes.White, 0, 43);
            if (m.App.Length > 0 && second != m.App)
                g.DrawString(m.App, small, Brushes.White, W - g.MeasureString(m.App, small).Width + 2, 43);
        }
    });

    /// <summary>PC stats while a game runs: the game and how long it's been running, CPU and GPU load, temperature, memory.</summary>
    public static bool[] RenderStats(DashboardData d, PcStatsInfo s, DateTime now) => Draw(now, g =>
    {
        using var head = new Font("Segoe UI Semibold", 11f, GraphicsUnit.Pixel);
        using var small = new Font("Segoe UI", 10f, GraphicsUnit.Pixel);
        using var big = new Font("Segoe UI Semibold", 20f, GraphicsUnit.Pixel);
        using var oneLine = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        string played = d.GameStarted is DateTime started ? Played(now - started) : "";
        float playedWidth = played.Length > 0 ? g.MeasureString(played, small).Width : 0;
        g.DrawString(d.Game ?? "", head, Brushes.White, new RectangleF(0, -1, W - playedWidth - 1, 15), oneLine);
        if (played.Length > 0) g.DrawString(played, small, Brushes.White, W - playedWidth + 2, 0);

        g.DrawString("CPU", small, Brushes.White, 0, 14);
        g.DrawString($"{s.Cpu}%", big, Brushes.White, -2, 21);
        if (s.Gpu is int gpu)
        {
            g.DrawString(s.GpuTemp is int temp ? $"GPU  {temp}°C" : "GPU", small, Brushes.White, 66, 14);
            g.DrawString($"{gpu}%", big, Brushes.White, 64, 21);
        }

        string ram = $"RAM {s.RamUsedGb:0}/{s.RamTotalGb:0}G";
        string bottom = s.VramUsedGb is double vram ? $"VRAM {vram:0.0}G   {ram}" : ram;
        g.DrawString(bottom, small, Brushes.White, new RectangleF(0, 51, W + 4, 13), oneLine);
    });

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

    /// <summary>"Label  ███████░░░  56%" on one 11-pixel row.</summary>
    private static void BatteryLine(Graphics g, Font font, ref int y, string label, int? percent, bool charging)
    {
        g.DrawString(label, font, Brushes.White, 0, y - 1);
        string value = percent is int p ? $"{p}%{(charging ? "+" : "")}" : "–";
        var size = g.MeasureString(value, font);
        g.DrawString(value, font, Brushes.White, W - size.Width + 2, y - 1);
        if (percent is int pct)
        {
            const int barX = 46, barW = 50;
            g.DrawRectangle(Pens.White, barX, y + 2, barW, 6);
            g.FillRectangle(Brushes.White, barX + 1, y + 3, (barW - 1) * Math.Clamp(pct, 0, 100) / 100, 5);
        }
        y += 11;
    }

    public void Dispose() => Stop();
}
