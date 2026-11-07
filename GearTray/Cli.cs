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
///   --install-startup / --uninstall-startup   (must run elevated)
/// Global: --trace (log every HID report), --out FILE. Several commands can be chained with ";;".
/// </summary>
internal static class Cli
{
    public static readonly string[] Commands =
        ["--probe", "--list-hid", "--mouse", "--mouse-light", "--kb-light", "--kb-gamemode", "--headset", "--install-startup", "--uninstall-startup",
         "--sample-test", "--audio-test", "--kb-frame-test", "--kb-effect-test", "--kb-polling"];

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
                case "--sample-test":
                    return SampleTest();
                case "--audio-test":
                    return AudioTest();
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

    private static RazerHuntsmanElite Keyboard(DeviceManager m) =>
        m.Get<RazerHuntsmanElite>() is { Connected: true } k ? k : throw new InvalidOperationException("Keyboard not available: " + m.Get<RazerHuntsmanElite>()?.Problem);

    private static RogOmniMouse Mouse(DeviceManager m) =>
        m.Get<RogOmniMouse>() is { Connected: true } mm ? mm : throw new InvalidOperationException("Mouse receiver not connected");

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
        Keyboard(mgr);
        using var engine = new Effects.EffectEngine(mgr);
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
                    default: throw new ArgumentException("unknown mouse key " + k);
                }
            }
            m.ApplySettings(want, have);
            var now = m.ReadSettings();
            Console.WriteLine($"Mouse now: DPI {string.Join("/", now.Dpi)}, polling {now.PollingHz} Hz, button response {now.DebounceMs} ms, " +
                              $"angle snapping {now.AngleSnapping}, lift-off {(now.LiftOffHigh ? "high" : "low")}, sleep {now.SleepMinutes} min, low-batt {now.LowBatteryWarning}%");
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
