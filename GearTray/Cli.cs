using System.Globalization;
using GearTray.Devices;
using GearTray.Hid;
using GearTray.Util;

namespace GearTray;

/// <summary>
/// Command-line mode, used for testing and scripting. Output goes to stdout (or --out FILE).
///   --probe                                   read-only report of every device
///   --mouse key=value ...                     dpi1..dpi4, polling, debounce, angle=on|off, lod=low|high, sleep=MIN, lowbatt=PCT
///   --mouse-light MODE [RRGGBB] [BRIGHTNESS] [--save]
///                                             MODE = off|static|breathing|colorcycle|rainbow|reactive|battery
///   --kb-light EFFECT [RRGGBB] [BRIGHTNESS] [--save]
///                                             EFFECT = off|static|breathing|breathingrandom|spectrum|wave|wave-reverse|
///                                                      reactive|starlight|starlightrandom
///   --kb-gamemode on|off
///   --headset key=value ...                   noise=off|transparency|anc, transparency=1-10, sidetone=0-3, mic=1-10,
///                                             micled=1-10, autooff=MIN, oled=1-10, eq=0-4, eqbands=dB,dB,…(10)
///   --xbox                                    Xbox controllers: XInput state, ids, battery, GIP devices and firmware
///   --xbox-light MODE [0-47]                  MODE = off|on|blinkfast|blinkslow|fadeslow|fadefast
///   --xbox-light-test                         brightest, very dim, slow fade, then Windows' level again
///   --install-startup / --uninstall-startup   (must run elevated)
///   --open-test PATH                          open PATH through the desktop's Explorer, as the Open buttons do with admin rights
///   --write-icon FILE.ico [PREVIEW.png]       write GearTray.ico (and a preview sheet) from the logo drawing
///   --theme-preview FILE.png                  sample window in the light and the dark look, captured from the screen
///   --weather PLACE                           look a place up and print its weather now (Open-Meteo)
/// Global: --trace (log every HID report), --out FILE. Several commands can be chained with ";;".
/// </summary>
internal static class Cli
{
    public static readonly string[] Commands =
        ["--probe", "--list-hid", "--mouse", "--mouse-light", "--kb-light", "--kb-gamemode", "--headset", "--install-startup", "--uninstall-startup",
         "--sample-test", "--audio-test", "--kb-frame-test", "--kb-effect-test", "--kb-polling", "--screen-preview", "--screen-test",
         "--mouse-button-test", "--kb-keys-test", "--kb-exit-test", "--screen-data", "--screen-speed", "--fps-test", "--cpu-temp", "--xbox", "--xbox-light", "--xbox-light-test",
         "--open-test", "--write-icon", "--theme-preview", "--weather"];

    /// <summary>Runs one command, or several separated by ";;" (one elevation prompt for a whole test sequence).</summary>
    public static int Run(string[] args)
    {
        Log.Echo = true;
        HidConnection.Trace = args.Contains("--trace");
        var all = args.Where(a => a != "--trace").ToList();
        int result = 0;
        while (all.Count > 0)
        {
            int sep = all.IndexOf(";;");
            var one = sep < 0 ? all : all.Take(sep).ToList();
            all = sep < 0 ? [] : all.Skip(sep + 1).ToList();
            if (one.Count == 0) continue;
            Console.WriteLine($">>> {string.Join(' ', one)}");
            result = Math.Max(result, RunOne(one));
        }
        return result;
    }

    private static int RunOne(List<string> rest)
    {
        string cmd = rest[0].ToLowerInvariant();
        try
        {
            switch (cmd)
            {
                case "--list-hid":
                    foreach (var i in HidEnumerator.Enumerate()) Console.WriteLine(i);
                    return 0;
                case "--install-startup":
                    Elevation.InstallStartupTask();
                    Console.WriteLine("Startup task installed.");
                    return 0;
                case "--uninstall-startup":
                    Elevation.UninstallStartupTask();
                    Console.WriteLine("Startup task removed.");
                    return 0;
                case "--open-test":
                    ShellOpen.ThroughExplorer(rest[1]);
                    Console.WriteLine($"Explorer was asked to open {rest[1]}");
                    return 0;
                case "--weather":
                {
                    string name = string.Join(' ', rest.Skip(1));
                    var place = Weather.FindPlace(name).GetAwaiter().GetResult();
                    if (place is null) { Console.WriteLine($"No place found for \"{name}\""); return 1; }
                    Console.WriteLine(FormattableString.Invariant($"{place.Label}: {place.Latitude}, {place.Longitude}"));
                    var w = Weather.Current(place.Latitude, place.Longitude).GetAwaiter().GetResult();
                    Console.WriteLine(FormattableString.Invariant($"Now: {w.Temperature} °C, weather code {w.Code}, {(w.IsDay ? "day" : "night")}"));
                    return 0;
                }
                case "--theme-preview":
                    ApplicationConfiguration.Initialize(); // DPI awareness, so the screen capture lines up
                    using (var sheet = UI.ThemePreview.Capture()) sheet.Save(rest[1], System.Drawing.Imaging.ImageFormat.Png);
                    Console.WriteLine($"Wrote {rest[1]} (light and dark)");
                    return 0;
                case "--write-icon":
                    File.WriteAllBytes(rest[1], UI.Logo.Ico());
                    Console.WriteLine($"Wrote {rest[1]} ({string.Join(", ", UI.Logo.IcoSizes)} px)");
                    if (rest.Count > 2)
                    {
                        using var preview = UI.Logo.Preview();
                        preview.Save(rest[2], System.Drawing.Imaging.ImageFormat.Png);
                        Console.WriteLine($"Preview: {rest[2]}");
                    }
                    return 0;
                case "--sample-test":
                    return SampleTest();
                case "--screen-preview":
                    return ScreenPreview(rest[1]);
                case "--audio-test":
                    return AudioTest();
                case "--screen-data":
                    return ScreenData(rest.Count > 1 ? int.Parse(rest[1]) : 1);
                case "--fps-test":
                    return FpsTest(rest.Count > 1 ? int.Parse(rest[1]) : 5);
                case "--cpu-temp":
                    return CpuTempTest();
                case "--xbox":
                    return XboxReport();
                case "--xbox-light":
                    return XboxLight(rest);
                case "--xbox-light-test":
                    return XboxLightTest();
            }

            using var mgr = CreateManager();
            mgr.RescanAsync().GetAwaiter().GetResult();
            return cmd switch
            {
                "--probe" => Probe(mgr),
                "--mouse" => MouseCmd(mgr, rest),
                "--mouse-light" => MouseLight(mgr, rest),
                "--kb-light" => KbLight(mgr, rest),
                "--kb-gamemode" => mgr.Run(() =>
                {
                    Keyboard(mgr).SetGameMode(rest[1] == "on");
                    Console.WriteLine($"Game mode now {(Keyboard(mgr).GetGameMode() ? "on" : "off")}");
                    return 0;
                }),
                "--headset" => HeadsetCmd(mgr, rest),
                "--kb-polling" => mgr.Run(() => { Console.WriteLine($"Keyboard polling rate: {Keyboard(mgr).GetPollingRate()?.ToString() ?? "not supported"}"); return 0; }),
                "--kb-frame-test" => FrameTest(mgr, rest.Count > 1 ? int.Parse(rest[1]) : 4),
                "--kb-effect-test" => EffectTest(mgr, rest),
                "--screen-test" => ScreenTest(mgr, rest.Count > 1 ? int.Parse(rest[1]) : 6),
                "--mouse-button-test" => MouseButtonTest(mgr),
                "--kb-keys-test" => KeysTest(mgr, rest.Count > 1 ? int.Parse(rest[1]) : 8),
                "--kb-exit-test" => ExitFramesTest(mgr),
                "--screen-speed" => ScreenSpeed(mgr),
                _ => Usage(),
            };
        }
        catch (Exception ex)
        {
            Log.Error($"{cmd} failed", ex);
            return 1;
        }
    }

    private static DeviceManager CreateManager() => DeviceManager.CreateDefault();

    private static int Usage()
    {
        Console.WriteLine("Unknown command. See the comment at the top of Cli.cs.");
        return 2;
    }

    /// <summary>
    /// Leaving per-key frame mode through GearTray's own Apply: from an all-pink frame to green (the startup path, live
    /// only), then from pink to Wave (the settings window's path, live and stored). Whoever watches sees which worked.
    /// </summary>
    private static int ExitFramesTest(DeviceManager mgr) => mgr.Run(() =>
    {
        var kb = Keyboard(mgr);
        var pink = Enumerable.Range(0, RazerHuntsmanElite.MatrixRows * RazerHuntsmanElite.MatrixCols)
            .SelectMany(_ => new byte[] { 255, 0, 150 }).ToArray();
        Console.WriteLine($"Active onboard profile: {kb.ActiveProfile()}");
        (string Name, Action Try)[] tries =
        [
            ("GREEN: Apply static green, live only", () => kb.Apply(new KeyboardLighting(KeyboardEffect.Static, Color.Lime, 100), persist: false)),
            ("WAVE: Apply wave left to right, live and saved", () => kb.Apply(new KeyboardLighting(KeyboardEffect.Wave, Color.White, 100), persist: true)),
        ];
        foreach (var (name, attempt) in tries)
        {
            kb.SendFrame(pink);
            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} pink frame");
            Thread.Sleep(2500);
            attempt();
            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {name}");
            Thread.Sleep(4000);
        }
        return 0;
    });

    private static int XboxReport()
    {
        var pnp = PnpDevices.XboxDevices();
        Console.WriteLine($"Windows devices: {(pnp.Count > 0 ? string.Join(" · ", pnp) : "none")}");
        foreach (int s in XInput.ConnectedSlots())
        {
            string ids = XInput.GetIds(s) is (var vid, var pid) ? $"{vid:X4}:{pid:X4}" : "n/a";
            Console.WriteLine($"XInput slot {s}: {XInput.Describe(s)}; ids {ids}; battery {XInput.GetBattery(s)}");
            Console.WriteLine($"   state {XInput.GetState(s)}");
        }
        var gip = XboxGip.Devices(fresh: true);
        Console.WriteLine(gip.Count == 0 ? "XboxGIP: no devices" : "XboxGIP devices:");
        foreach (var d in gip) Console.WriteLine($"   {d}  {PnpDevices.XboxModel(d.ProductId)}");
        return 0;
    }

    private static int XboxLight(List<string> rest)
    {
        var mode = rest.Count > 1 ? Enum.Parse<XboxLedMode>(rest[1], ignoreCase: true) : XboxLedMode.On;
        int level = rest.Count > 2 ? int.Parse(rest[2], CultureInfo.InvariantCulture) : XboxGip.DefaultBrightness;
        int n = XboxGip.SetLight(mode, level);
        Console.WriteLine(n < 0 ? "XboxGIP isn't available." : $"Xbox button {mode} {level}: {n} controller(s) took it.");
        return n > 0 ? 0 : 1;
    }

    /// <summary>Brightest, very dim, slow fade, then Windows' own level again, 3 s each, for watching the Xbox button.</summary>
    private static int XboxLightTest()
    {
        foreach (var d in XboxGip.Devices(fresh: true)) Console.WriteLine($"GIP device {d}");
        (XboxLedMode Mode, int Level, string Text)[] steps =
        [
            (XboxLedMode.On, XboxGip.MaxBrightness, "brightest"),
            (XboxLedMode.On, 2, "very dim"),
            (XboxLedMode.FadeSlow, XboxGip.MaxBrightness, "slow fade"),
            (XboxLedMode.On, XboxGip.DefaultBrightness, "Windows' level again"),
        ];
        int result = 0;
        for (int i = 0; i < steps.Length; i++)
        {
            var (mode, level, text) = steps[i];
            int n = XboxGip.SetLight(mode, level);
            Console.WriteLine($"Xbox button: {text} ({mode} {level}) -> {(n < 0 ? "no XboxGIP driver" : $"{n} controller(s)")}");
            if (n <= 0) result = 1;
            if (i < steps.Length - 1) Thread.Sleep(3000);
        }
        return result;
    }

    private static RazerHuntsmanElite Keyboard(DeviceManager m) =>
        m.Get<RazerHuntsmanElite>() is { Connected: true } k ? k : throw new InvalidOperationException("Keyboard not available: " + m.Get<RazerHuntsmanElite>()?.Problem);

    private static RogOmniMouse Mouse(DeviceManager m) =>
        m.Get<RogOmniMouse>() is { Connected: true } mm ? mm : throw new InvalidOperationException("Mouse not connected");

    private static int Probe(DeviceManager mgr)
    {
        Console.WriteLine($"Elevated: {Elevation.IsAdmin}");
        Console.WriteLine($"Log file: {Log.FilePath}  (APPDATA env: {Environment.GetEnvironmentVariable("APPDATA")}, cwd: {Environment.CurrentDirectory})");
        foreach (var dev in mgr.Devices)
        {
            Console.WriteLine();
            Console.WriteLine($"== {dev.Name} [{dev.Kind}] ==");
            Console.WriteLine($"   connected: {dev.Connected}   {dev.Detail}   {(dev.Problem is null ? "" : "problem: " + dev.Problem)}");
            if (!dev.Connected) continue;
            mgr.Run(() =>
            {
                if (dev is IHasBattery b) Section("battery", () => Fmt(b.ReadBattery()));
                switch (dev)
                {
                    case RogOmniMouse m:
                        Section("paired IDs", () => string.Join(", ", m.PairedPids.Select(p => p.ToString("X4"))));
                        Section("booster", () => m.Booster.ToString());
                        Section("lighting", () => string.Join("; ", m.ReadLighting()));
                        Section("settings", () =>
                        {
                            var s = m.ReadSettings();
                            return $"profile(raw) {s.ProfileRaw}, active DPI stage {s.ActiveStage}, DPI {string.Join("/", s.Dpi)}, " +
                                   $"stage colors {string.Join(" ", s.DpiColors.Select(c => $"#{c.R:X2}{c.G:X2}{c.B:X2}"))}, polling {s.PollingHz} Hz, " +
                                   $"button response {s.DebounceMs} ms, angle snapping {s.AngleSnapping}, lift-off {(s.LiftOffHigh ? "high" : "low")}, " +
                                   $"sleep {(s.SleepMinutes == 0 ? "never" : s.SleepMinutes + " min")}, low-battery warning {s.LowBatteryWarning}%";
                        });
                        break;
                    case RazerHuntsmanElite k:
                        Section("brightness", () => k.GetBrightness() + "%");
                        Section("game mode", () => k.GetGameMode() ? "on" : "off");
                        break;
                    case ArctisNovaProWireless h:
                        Section("state", () =>
                        {
                            var s = h.ReadState();
                            return $"headset {(s.HeadsetOnline ? "on" : "off")}, battery {s.BatteryPercent?.ToString() ?? "?"}%{(s.Charging ? " (charging)" : "")}, " +
                                   $"spare battery {s.SpareBatteryPercent?.ToString() ?? "?"}%, noise {s.Noise}, transparency {s.TransparencyLevel}, " +
                                   $"mic {(s.MicMuted ? "muted" : "live")} vol {s.MicVolume}, mic LED {s.MicLedBrightness}, sidetone {s.Sidetone}, " +
                                   $"EQ {s.EqPreset} [{string.Join(" ", s.EqBandsDb)}], ChatMix {s.ChatMixGame}/{s.ChatMixChat}, " +
                                   $"auto-off idx {s.AutoOffIndex}, OLED {s.OledBrightness}";
                        });
                        break;
                }
                return 0;
            });
        }
        return 0;
    }

    private static void Section(string label, Func<string> read)
    {
        try { Console.WriteLine($"   {label}: {read()}"); }
        catch (Exception ex) { Console.WriteLine($"   {label}: ERROR {ex.GetType().Name}: {ex.Message}"); }
    }

    private static string Fmt(BatteryInfo? b) => b is null ? "unknown" : $"{b.Percent}%{(b.Charging ? " charging" : "")}";

    private static Color ParseColor(string s) =>
        Color.FromArgb(int.Parse(s.TrimStart('#'), NumberStyles.HexNumber) | unchecked((int)0xFF000000));

    private static int MouseLight(DeviceManager mgr, List<string> a)
    {
        bool save = a.Remove("--save");
        var mode = Enum.Parse<MouseLightMode>(a[1], ignoreCase: true);
        var color = a.Count > 2 ? ParseColor(a[2]) : Color.White;
        int brightness = a.Count > 3 ? int.Parse(a[3]) : 100;
        return mgr.Run(() =>
        {
            Mouse(mgr).ApplyLighting(new MouseLighting(mode, color, brightness), save);
            Console.WriteLine($"Mouse lighting: {Mouse(mgr).ReadLighting()[0]}{(save ? " (saved)" : "")}");
            return 0;
        });
    }

    private static int KbLight(DeviceManager mgr, List<string> a)
    {
        bool save = a.Remove("--save");
        string name = a[1].ToLowerInvariant();
        var effect = name == "wave-reverse" ? KeyboardEffect.Wave : Enum.Parse<KeyboardEffect>(name, ignoreCase: true);
        var color = a.Count > 2 ? ParseColor(a[2]) : Color.White;
        int brightness = a.Count > 3 ? int.Parse(a[3]) : 100;
        return mgr.Run(() =>
        {
            Keyboard(mgr).Apply(new KeyboardLighting(effect, color, brightness, name == "wave-reverse"), save);
            Console.WriteLine($"Keyboard lighting: {name} #{color.R:X2}{color.G:X2}{color.B:X2} {brightness}%{(save ? " (saved)" : "")}");
            return 0;
        });
    }

    private static readonly Effects.DashboardData SampleDashboard = new("ELDEN RING", DateTime.Now.AddMinutes(-83), MouseBattery: new(64, false));

    /// <summary>
    /// Render every base-station page to PNGs (4× size) without touching the device: FILE is the home page, and
    /// FILE-home / -media / -stats / -popup.png the others, with sample data.
    /// </summary>
    private static int ScreenPreview(string file)
    {
        var now = new DateTime(2026, 11, 7, 21, 47, 12); // fixed, so the pictures only change when the drawing does
        var media = new MediaInfo("Everlong (Acoustic Version)", "Foo Fighters", "Spotify", true, TimeSpan.FromSeconds(83), TimeSpan.FromSeconds(231), TimeSpan.FromSeconds(83));
        var noTimeline = media with { Title = "Alysa Liu wins the Olympic gold medal", Artist = "Team USA", App = "Firefox", Duration = TimeSpan.Zero, PlayingFor = TimeSpan.FromSeconds(42) };
        var stats = new PcStatsInfo(23, 18.4, 63.6, 97, 64, 9.1, 16, CpuTemp: 61, Fps: 143, FpsLow: 98, FpsAvg: 141);
        string stem = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(file))!, Path.GetFileNameWithoutExtension(file));
        // A server, a weather place, and both batteries known.
        var home = new Effects.HomeData(true, 38, 12.3, 0.4, Effects.ServerState.On, WeatherSet: true, Weather: new(7.4, 2, true),
            HeadsetBattery: new(88, false), MouseBattery: new(64, false));
        var homeTime = now;
        // The widest of everything: 23:58:58 on "Wed 30 May" (drawn a pixel right and down by the burn-in shift),
        // "Server: Off", 118 MB/s, and 100 for the volume and both batteries with 12 °C rain.
        var lateTime = new DateTime(2029, 5, 30, 23, 58, 58);
        var widest = home with
        {
            Server = Effects.ServerState.Off, Volume = 100, DownMBps = 118, UpMBps = 9.9,
            HeadsetBattery = new(100, false), MouseBattery = new(100, false), Weather = new(12.2, 63, true),
        };
        // No server: the longest full date ("Wednesday 30 September" doesn't fit, so the month is shortened).
        var longDate = new DateTime(2026, 9, 30, 23, 58, 58);
        var bare = home with { Server = Effects.ServerState.NotSet, HeadsetOn = false };
        SavePreview(Effects.BaseStationScreen.RenderHome(home, homeTime), file);
        SavePreview(Effects.BaseStationScreen.RenderHome(widest, lateTime), stem + "-home-widest.png");
        var wave = SampleWave();
        SavePreview(Effects.BaseStationScreen.RenderMedia(media, wave, now), stem + "-media.png");
        SavePreview(Effects.BaseStationScreen.RenderMedia(noTimeline, wave, now), stem + "-media-no-timeline.png");
        SaveGrid(
        [
            ("Now playing", Effects.BaseStationScreen.RenderMedia(media, wave, now)),
            ("Firefox, position not known yet", Effects.BaseStationScreen.RenderMedia(noTimeline, wave, now)),
            ("A quiet moment (silence is a dotted line)", Effects.BaseStationScreen.RenderMedia(media, wave.Select((p, i) => i < 40 ? p : 0f).ToArray(), now)),
            ("First seconds (the wave fills in from the right)", Effects.BaseStationScreen.RenderMedia(media, wave[^12..], now)),
            ("A long artist name", Effects.BaseStationScreen.RenderMedia(media with { Artist = "Florence + The Machine & Kendrick Lamar" }, wave, now)),
            ("No artist (some players)", Effects.BaseStationScreen.RenderMedia(noTimeline with { Artist = "" }, wave, now)),
        ], stem + "-media-grid.png");
        SaveGrid(
        [
            ("Home", Effects.BaseStationScreen.RenderHome(home, homeTime)),
            ("Widest values", Effects.BaseStationScreen.RenderHome(widest, lateTime)),
            ("No server, headset off", Effects.BaseStationScreen.RenderHome(bare, longDate)),
            ("First second (no answers yet)", Effects.BaseStationScreen.RenderHome(home with { Server = Effects.ServerState.Checking, Weather = null, HeadsetBattery = null }, homeTime)),
        ], stem + "-home.png");
        SaveGrid(
        [
            ("PC stats", Effects.BaseStationScreen.RenderStats(Played83(1), stats, PreviewTime(1))),
            ("No average yet (first seconds, or frames can't be counted)", Effects.BaseStationScreen.RenderStats(Played83(1), stats with { FpsAvg = null }, PreviewTime(1))),
        ], stem + "-stats.png");
        SavePreview(Effects.BaseStationScreen.RenderPopup("Game mode", "On", now), stem + "-popup.png");

        // The bottom row (volume, headset battery, mouse battery, weather) in its other states.
        SaveGrid(
        [
            ("Mouse charging, 41", Effects.BaseStationScreen.RenderHome(home with { MouseBattery = new(41, true) }, homeTime)),
            ("Headset charging, 50", Effects.BaseStationScreen.RenderHome(home with { HeadsetBattery = new(50, true) }, homeTime)),
            ("Headset off, clear night, 4 °C", Effects.BaseStationScreen.RenderHome(home with { HeadsetOn = false, Weather = new(3.6, 0, false) }, homeTime)),
            ("No mouse (or not read yet)", Effects.BaseStationScreen.RenderHome(home with { MouseBattery = null }, homeTime)),
            ("No weather place set", Effects.BaseStationScreen.RenderHome(home with { WeatherSet = false, Weather = null }, homeTime)),
            ("Extreme: three 100s, charging, snow at -28 °C", Effects.BaseStationScreen.RenderHome(widest with { MouseBattery = new(100, true), Weather = new(-27.6, 73, true) }, lateTime)),
        ], stem + "-home-bottom.png");
        using (var icons = Effects.BaseStationScreen.WeatherIconSheet()) icons.Save(stem + "-weather-icons.png");
        return 0;
    }

    /// <summary>
    /// 13 s of made-up sound-meter peaks for the now-playing wave (one per 200 ms tick), in the range the real meter
    /// gave for a video: about 0.03 to 0.35.
    /// </summary>
    private static float[] SampleWave()
    {
        var r = new Random(7);
        return Enumerable.Range(0, 64)
            .Select(i => (float)Math.Clamp(0.06 + 0.26 * Math.Abs(Math.Sin(i * 0.45)) * (0.7 + 0.3 * Math.Sin(i * 0.11)) + (r.NextDouble() - 0.5) * 0.08, 0.02, 0.35))
            .ToArray();
    }

    /// <summary>Several screen pages side by side (4× size, two per row), each with a caption, in one PNG.</summary>
    private static void SaveGrid(List<(string Caption, bool[] Pixels)> tiles, string file)
    {
        const int s = 4, gap = 24, caption = 34;
        int tw = ArctisNovaProWireless.ScreenWidth * s, th = ArctisNovaProWireless.ScreenHeight * s;
        int rows = (tiles.Count + 1) / 2;
        using var bmp = new Bitmap(2 * tw + 3 * gap, rows * (th + caption + gap) + gap);
        using (var g = Graphics.FromImage(bmp))
        using (var font = new Font("Segoe UI Semibold", 18f, GraphicsUnit.Pixel))
        {
            g.Clear(Color.FromArgb(45, 45, 48));
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            for (int i = 0; i < tiles.Count; i++)
            {
                int x0 = gap + (i % 2) * (tw + gap), y0 = gap + (i / 2) * (th + caption + gap);
                g.DrawString(tiles[i].Caption, font, Brushes.White, x0, y0);
                g.FillRectangle(new SolidBrush(Color.FromArgb(10, 10, 10)), x0, y0 + caption, tw, th);
                var px = tiles[i].Pixels;
                for (int y = 0; y < ArctisNovaProWireless.ScreenHeight; y++)
                    for (int x = 0; x < ArctisNovaProWireless.ScreenWidth; x++)
                        if (px[x + y * ArctisNovaProWireless.ScreenWidth]) g.FillRectangle(Brushes.White, x0 + x * s, y0 + caption + y * s, s - 1, s - 1);
            }
        }
        bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
        Console.WriteLine($"Saved {file}");
    }

    private static DateTime PreviewTime(int second) => new(2026, 11, 7, 20, 0, second);
    private static Effects.DashboardData Played83(int second) => SampleDashboard with { GameStarted = PreviewTime(second).AddMinutes(-83) };

    private static void SavePreview(bool[] px, string file)
    {
        const int s = 4;
        using var bmp = new Bitmap(ArctisNovaProWireless.ScreenWidth * s, ArctisNovaProWireless.ScreenHeight * s);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(20, 20, 20));
            for (int y = 0; y < ArctisNovaProWireless.ScreenHeight; y++)
                for (int x = 0; x < ArctisNovaProWireless.ScreenWidth; x++)
                    if (px[x + y * ArctisNovaProWireless.ScreenWidth]) g.FillRectangle(Brushes.White, x * s, y * s, s - 1, s - 1);
        }
        bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
        Console.WriteLine($"Saved {file}");
    }

    /// <summary>
    /// Run GearTray's real base-station screen for N seconds with sample batteries and a sample game (so the PC stats
    /// page takes part) plus whatever is really playing, then hand the screen back.
    /// </summary>
    private static int ScreenTest(DeviceManager mgr, int seconds)
    {
        var h = mgr.Get<ArctisNovaProWireless>() is { Connected: true } hh ? hh : throw new InvalidOperationException("Base station not connected");
        if (!h.CanDrawScreen) throw new NotSupportedException("No screen channel");
        using var screen = new Effects.BaseStationScreen(mgr, () => SampleDashboard);
        screen.Start();
        Thread.Sleep(TimeSpan.FromSeconds(Math.Max(3, seconds) / 2.0));
        screen.Popup("Game mode", "On");
        Thread.Sleep(TimeSpan.FromSeconds(Math.Max(3, seconds) / 2.0));
        screen.Stop();
        mgr.RunAsync(() => { }).Wait(2000); // let the restore go out
        Console.WriteLine("Base station screen test done; normal screen restored.");
        return 0;
    }

    /// <summary>The CPU temperature through PawnIO, three readings a second apart (admin).</summary>
    private static int CpuTempTest()
    {
        for (int i = 0; i < 3; i++)
        {
            if (i > 0) Thread.Sleep(1000);
            var raw = CpuTemperature.ReadRaw();
            Console.WriteLine(raw is uint r
                ? $"CPU (Tctl): {CpuTemperature.Decode(r):0.0} °C   (THM_TCON_CUR_TMP 0x{r:X8})"
                : $"CPU temperature unavailable: {CpuTemperature.Status}");
        }
        return CpuTemperature.Status == "ready" ? 0 : 1;
    }

    /// <summary>Present calls per second of every process over N seconds, from the same events the FPS counter uses (admin).</summary>
    private static int FpsTest(int seconds)
    {
        Console.WriteLine($"Counting DXGI / Direct3D 9 presents for {seconds} s…");
        var rates = FpsCounter.Survey(seconds);
        if (rates.Count == 0) Console.WriteLine("No presents seen (needs admin rights; nothing drawing with DirectX?).");
        foreach (var (pid, perSecond) in rates.Take(10))
        {
            string name;
            try { name = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
            catch (ArgumentException) { name = "(exited)"; }
            Console.WriteLine($"   {perSecond,7:0.0}/s  {name} ({pid})");
        }
        return 0;
    }

    /// <summary>How long one picture takes to reach the base station: 25 home pages as fast as possible, then its own screen.</summary>
    private static int ScreenSpeed(DeviceManager mgr)
    {
        var h = mgr.Get<ArctisNovaProWireless>() is { Connected: true, CanDrawScreen: true } hh ? hh : throw new InvalidOperationException("Base station screen not available");
        var full = new List<double>();
        var half = new List<double>();
        var clock = new System.Diagnostics.Stopwatch();
        for (int i = 0; i < 30; i++)
        {
            var frame = Effects.BaseStationScreen.RenderHome(new Effects.HomeData(true, 40, 1.0, 0.1), DateTime.Now);
            bool both = i < 12; // the first pictures whole, then only the right half
            mgr.Run(() => { clock.Restart(); h.DrawScreen(frame, left: both, right: true); clock.Stop(); return 0; });
            (both ? full : half).Add(clock.Elapsed.TotalMilliseconds);
        }
        mgr.Run(() => { h.RestoreScreen(); return 0; });
        var live = mgr.Run(() => h.ReadLive());
        Console.WriteLine($"Headset connected: {live.Online}, volume {live.Volume?.ToString() ?? "?"}%, battery {Fmt(live.Battery)}");
        Console.WriteLine($"Whole screen: average {full.Average():0.0} ms (fastest {full.Min():0.0}, slowest {full.Max():0.0})");
        Console.WriteLine($"Right half only: average {half.Average():0.0} ms (fastest {half.Min():0.0}, slowest {half.Max():0.0})");
        Console.WriteLine($"Sound meter at 5 updates a second: {half.Average() * 5 / 10:0.0}% of the device thread's time.");
        return 0;
    }

    /// <summary>What the screen's pages would show: the media session and PC stats, read N times a second apart.</summary>
    private static int ScreenData(int count)
    {
        PcStats.Read();
        for (int i = 0; i < Math.Max(1, count); i++)
        {
            Thread.Sleep(1000);
            var m = MediaSession.Read();
            Console.WriteLine(m is null ? "Now playing: no media session" : $"Now playing: {m}");
            if (m is not null) Console.WriteLine($"   timeline: {MediaSession.LastTimeline}");
        }
        Console.WriteLine($"PC stats: {PcStats.Read()}");
        return 0;
    }

    /// <summary>Re-bind the wheel click to Back, read it back, then restore it to middle click and read again.</summary>
    private static int MouseButtonTest(DeviceManager mgr)
    {
        var m = Mouse(mgr);
        return mgr.Run(() =>
        {
            Console.WriteLine("Before:  " + string.Join(", ", m.ReadButtons().Select(a => a.Name)));
            m.WriteButtons(new Dictionary<int, MouseAction> { [2] = MouseAction.All.First(a => a.Name == "Back") });
            Console.WriteLine("Changed: " + string.Join(", ", m.ReadButtons().Select(a => a.Name)));
            m.WriteButtons(new Dictionary<int, MouseAction> { [2] = MouseAction.All.First(a => a.Name.StartsWith("Middle click")) });
            Console.WriteLine("Restored:" + string.Join(", ", m.ReadButtons().Select(a => a.Name)));
            return 0;
        });
    }

    /// <summary>Light W, A, S, D green, the arrow keys blue, Esc red, the underglow's left half yellow — to check the key map.</summary>
    private static int KeysTest(DeviceManager mgr, int seconds)
    {
        var kb = Keyboard(mgr);
        const int cols = RazerHuntsmanElite.MatrixCols;
        var frame = new byte[RazerHuntsmanElite.MatrixRows * cols * 3];
        void Set(int row, int col, Color c) { int o = (row * cols + col) * 3; frame[o] = c.R; frame[o + 1] = c.G; frame[o + 2] = c.B; }
        foreach (int sc in new[] { 0x11, 0x1E, 0x1F, 0x20 }) // W A S D
            if (HuntsmanLayout.Locate(0, sc, false, kb.IsIso) is var (r, c)) Set(r, c, Color.Lime);
        foreach (int sc in new[] { 0x48, 0x4B, 0x50, 0x4D }) // arrows
            if (HuntsmanLayout.Locate(0, sc, true, kb.IsIso) is var (r, c)) Set(r, c, Color.Blue);
        Set(0, 1, Color.Red);
        foreach (var cell in HuntsmanLayout.Cells.Where(x => x.IsUnderglow && x.Col < 10)) Set(cell.Row, cell.Col, Color.Yellow);
        Console.WriteLine($"Keyboard layout: {HuntsmanLayout.LayoutName(kb.Layout)} ({(kb.IsIso ? "ISO" : "ANSI")}), firmware ripple: {kb.HardwareRipple}");
        mgr.Run(() => { kb.SendFrame(frame); return 0; });
        Console.WriteLine("Showing: W A S D green, arrows blue, Esc red, left part of the underglow yellow.");
        Thread.Sleep(seconds * 1000);
        return 0;
    }

    /// <summary>Screen sampler check (no device needed): which method, how fast, what color.</summary>
    private static int SampleTest()
    {
        // 1) A changing picture: a square flashing red/blue in the middle of the main screen.
        var main = Screen.PrimaryScreen!;
        using (var flash = new Form
        {
            FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, TopMost = true, ShowInTaskbar = false,
            Bounds = new Rectangle(main.Bounds.X + main.Bounds.Width / 4, main.Bounds.Y + main.Bounds.Height / 4, main.Bounds.Width / 2, main.Bounds.Height / 2),
            BackColor = Color.Red,
        })
        using (var sampler = Effects.ScreenSamplers.Create(main))
        using (var gdi = new Effects.GdiSampler(main))
        {
            flash.Show();
            var grid = new byte[22 * 6 * 3];
            var gdiGrid = new byte[22 * 6 * 3];
            int c = (2 * 22 + 11) * 3; // a cell in the middle of the screen
            for (int i = 0; i < 6; i++)
            {
                flash.BackColor = i % 2 == 0 ? Color.Red : Color.Blue;
                flash.Refresh();
                // Keep the window responsive while the compositor picks the change up.
                bool ok = false;
                for (int k = 0; k < 10; k++) { Application.DoEvents(); ok = sampler.Sample(grid, 22, 6); }
                gdi.Sample(gdiGrid, 22, 6);
                string counters = sampler is Effects.DesktopDuplicationSampler d ? $" (frames {d.FramesAcquired}, with picture {d.PictureFrames})" : "";
                Console.WriteLine($"flash {(i % 2 == 0 ? "red " : "blue")}: {sampler.Method} ok={ok} center #{grid[c]:X2}{grid[c + 1]:X2}{grid[c + 2]:X2}{counters} | GDI center #{gdiGrid[c]:X2}{gdiGrid[c + 1]:X2}{gdiGrid[c + 2]:X2}");
            }
        }
        // 2) Speed on the real screen.
        foreach (var (screen, gdi) in Screen.AllScreens.SelectMany(s => new[] { (s, false), (s, true) }))
        {
            using Effects.IScreenSampler sampler = gdi ? new Effects.GdiSampler(screen) : Effects.ScreenSamplers.Create(screen);
            var grid = new byte[22 * 6 * 3];
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int ok = 0;
            for (int i = 0; i < 40; i++) if (sampler.Sample(grid, 22, 6)) ok++;
            double ms = sw.Elapsed.TotalMilliseconds / 40;
            long r = 0, g = 0, b = 0;
            for (int i = 0; i < grid.Length; i += 3) { r += grid[i]; g += grid[i + 1]; b += grid[i + 2]; }
            int n = grid.Length / 3;
            Console.WriteLine($"{screen.DeviceName} {screen.Bounds.Width}x{screen.Bounds.Height}: {sampler.Method}, {ok}/40 frames, " +
                              $"{ms:0.0} ms per call (includes waiting for screen changes), average color #{r / n:X2}{g / n:X2}{b / n:X2}");
        }
        return 0;
    }

    private static int AudioTest()
    {
        using var meter = new Effects.AudioMeter();
        for (int i = 0; i < 10; i++)
        {
            var (l, r) = meter.Read();
            Console.WriteLine($"peak L {l:0.000}  R {r:0.000}");
            Thread.Sleep(200);
        }
        return 0;
    }

    /// <summary>Per-key frame check: a rainbow scrolls across keys and underglow for N seconds.</summary>
    private static int FrameTest(DeviceManager mgr, int seconds)
    {
        var kb = Keyboard(mgr);
        using var timer = new TimerResolution();
        var frame = new byte[RazerHuntsmanElite.MatrixRows * RazerHuntsmanElite.MatrixCols * 3];
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int frames = 0;
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            double t = clock.Elapsed.TotalSeconds;
            for (int row = 0; row < RazerHuntsmanElite.MatrixRows; row++)
                for (int col = 0; col < RazerHuntsmanElite.MatrixCols; col++)
                {
                    double h = (col * 16 + row * 6 + t * 180) % 360;
                    var c = FromHue(h);
                    int o = (row * RazerHuntsmanElite.MatrixCols + col) * 3;
                    frame[o] = c.R; frame[o + 1] = c.G; frame[o + 2] = c.B;
                }
            mgr.Run(() => { kb.SendFrame(frame); return 0; });
            frames++;
            Thread.Sleep(30);
        }
        Console.WriteLine($"Sent {frames} frames in {clock.Elapsed.TotalSeconds:0.0} s ({frames / clock.Elapsed.TotalSeconds:0} fps)");
        return 0;
    }

    /// <summary>Runs a software effect (ambient or audiometer) for N seconds through the real effect engine.</summary>
    private static int EffectTest(DeviceManager mgr, List<string> a)
    {
        var effect = Enum.Parse<KeyboardEffect>(a[1], ignoreCase: true);
        int seconds = a.Count > 2 ? int.Parse(a[2]) : 8;
        var kb = Keyboard(mgr);
        using var engine = new Effects.EffectEngine(mgr);
        using var hook = new Input.KeyboardHook();
        if (effect == KeyboardEffect.Ripple)
        {
            if (kb.HardwareRipple)
            {
                mgr.Run(() => { kb.ApplyHardwareRipple(Color.DeepSkyBlue, 100, persist: false); return 0; });
                Console.WriteLine("Firmware ripple on: type something.");
                Thread.Sleep(seconds * 1000);
                return 0;
            }
            hook.KeyEvent += (vk, scan, ext, up) =>
            {
                if (!up && HuntsmanLayout.Locate(vk, scan, ext, kb.IsIso) is var (r, c)) engine.KeyPressed(r, c);
            };
            hook.WantKeyEvents = true;
        }
        engine.Start(new KeyboardLighting(effect, Color.DeepSkyBlue, 100), new Effects.AmbientOptions(null, true, 40));
        Thread.Sleep(1500);
        Console.WriteLine($"Effect running: {engine.Status}");
        Thread.Sleep(Math.Max(0, seconds * 1000 - 1500));
        engine.Stop();
        Console.WriteLine("Effect stopped");
        return 0;
    }

    private static Color FromHue(double h)
    {
        double x = 1 - Math.Abs(h / 60 % 2 - 1);
        var (r, g, b) = (int)(h / 60) switch { 0 => (1, x, 0d), 1 => (x, 1, 0d), 2 => (0d, 1, x), 3 => (0d, x, 1d), 4 => (x, 0d, 1d), _ => (1d, 0d, x) };
        return Color.FromArgb((int)(r * 255), (int)(g * 255), (int)(b * 255));
    }

    private static Dictionary<string, string> KeyValues(List<string> a) =>
        a.Skip(1).Select(x => x.Split('=', 2)).ToDictionary(p => p[0].ToLowerInvariant(), p => p.Length > 1 ? p[1] : "");

    private static int MouseCmd(DeviceManager mgr, List<string> a)
    {
        var kv = KeyValues(a);
        var m = Mouse(mgr);
        return mgr.Run(() =>
        {
            var have = m.ReadSettings();
            var want = have.Clone();
            foreach (var (k, v) in kv)
            {
                switch (k)
                {
                    case "dpi1" or "dpi2" or "dpi3" or "dpi4": want.Dpi[k[3] - '1'] = int.Parse(v); break;
                    case "polling": want.PollingHz = int.Parse(v); break;
                    case "debounce": want.DebounceMs = int.Parse(v); break;
                    case "angle": want.AngleSnapping = v == "on"; break;
                    case "lod": want.LiftOffHigh = v == "high"; break;
                    case "sleep": want.SleepMinutes = int.Parse(v); break;
                    case "lowbatt": want.LowBatteryWarning = int.Parse(v); break;
                    case "accel": want.Acceleration = int.Parse(v); break;
                    case "decel": want.Deceleration = int.Parse(v); break;
                    case "angletune": want.AngleTuning = int.Parse(v); break;
                    case "stage": want.ActiveStage = int.Parse(v); break;
                    default: throw new ArgumentException("unknown mouse key " + k);
                }
            }
            m.ApplySettings(want, have);
            var now = m.ReadSettings();
            Console.WriteLine($"Mouse now: DPI {string.Join("/", now.Dpi)}, stage {now.ActiveStage}, polling {now.PollingHz} Hz, button response {now.DebounceMs} ms, " +
                              $"angle snapping {now.AngleSnapping}, angle tuning {now.AngleTuning}, accel {now.Acceleration}, decel {now.Deceleration}, " +
                              $"lift-off {(now.LiftOffHigh ? "high" : "low")}, sleep {now.SleepMinutes} min, low-batt {now.LowBatteryWarning}%");
            return 0;
        });
    }

    private static int HeadsetCmd(DeviceManager mgr, List<string> a)
    {
        var kv = KeyValues(a);
        var h = mgr.Get<ArctisNovaProWireless>() is { Connected: true } hh ? hh : throw new InvalidOperationException("Base station not connected");
        return mgr.Run(() =>
        {
            var have = h.ReadState();
            var want = have.Clone();
            foreach (var (k, v) in kv)
            {
                switch (k)
                {
                    case "noise": want.Noise = v switch { "off" => NoiseMode.Off, "transparency" => NoiseMode.Transparency, "anc" => NoiseMode.NoiseCancelling, _ => throw new ArgumentException(v) }; break;
                    case "transparency": want.TransparencyLevel = int.Parse(v); break;
                    case "sidetone": want.Sidetone = int.Parse(v); break;
                    case "mic": want.MicVolume = int.Parse(v); break;
                    case "micled": want.MicLedBrightness = int.Parse(v); break;
                    case "autooff": want.AutoOffIndex = Array.IndexOf(ArctisNovaProWireless.AutoOffMinutes, int.Parse(v)); break;
                    case "oled": want.OledBrightness = int.Parse(v); break;
                    case "eq": want.EqPreset = int.Parse(v); break;
                    case "eqbands": want.EqPreset = 4; want.EqBandsDb = v.Split(',').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray(); break;
                    default: throw new ArgumentException("unknown headset key " + k);
                }
            }
            if (want.AutoOffIndex < 0) throw new ArgumentException("autooff must be 0, 1, 5, 10, 15, 30 or 60");
            h.ApplyState(want, have);
            var now = h.ReadState();
            Console.WriteLine($"Headset now: noise {now.Noise}, transparency {now.TransparencyLevel}, sidetone {now.Sidetone}, mic {now.MicVolume}, " +
                              $"mic LED {now.MicLedBrightness}, EQ {now.EqPreset}, auto-off idx {now.AutoOffIndex}, OLED {now.OledBrightness}");
            return 0;
        });
    }
}
