using System.Runtime.InteropServices;
using GearTray.Devices;
using GearTray.Effects;
using GearTray.Input;
using GearTray.Util;
using Microsoft.Win32;

namespace GearTray.UI;

/// <summary>
/// The tray icon and everything that runs in the background: battery badge and warnings, device watching,
/// keyboard lighting (hardware or software effects, dark when idle / screen off), game mode, key remaps and the
/// mouse's low-battery light.
/// </summary>
public sealed class TrayContext : ApplicationContext
{
    // Created first so WinForms installs its SynchronizationContext before we capture it.
    private readonly ContextMenuStrip _menu = new();
    private readonly SynchronizationContext _ui;
    private readonly NotifyIcon _icon;
    private readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 60_000 };
    private readonly System.Windows.Forms.Timer _rescanTimer = new() { Interval = 1500 };
    private readonly System.Windows.Forms.Timer _idleTimer = new() { Interval = 1000 };
    /// <summary>Waits for device changes to settle before putting the Xbox button light back.</summary>
    private readonly System.Windows.Forms.Timer _lightTimer = new() { Interval = 2500 };
    private readonly DeviceChangeWindow _deviceWatcher;
    private readonly EffectEngine _effects;
    private readonly KeyboardHook _hook = new();
    private readonly GameDetector _games;
    private readonly BaseStationScreen _screen;
    private SettingsForm? _form;
    private Icon? _currentIcon;

    public DeviceManager Devices { get; }
    public AppSettings Settings { get; }
    public Task Started { get; }

    /// <summary>Raised on the UI thread when game mode turns on or off.</summary>
    public event Action? GameModeChanged;
    /// <summary>Raised on the UI thread when the program in front or the active program profile changes.</summary>
    public event Action? ProfileChanged;

    // last known state
    private BatteryInfo? _mouseBattery;
    private bool _mouseAsleep, _mouseWarnShown;
    private HeadsetState? _headset;
    private PadBattery? _controllerBattery;
    private bool _controllerConnected;
    /// <summary>Last states shown as base-station pop-ups (null until first known, so startup shows none).</summary>
    private bool? _gameModeShown, _controllerShown;
    private bool _mouseLowShown, _headsetLowShown, _controllerLowShown, _refreshing;
    private bool _displayOn = true, _idle;
    private bool? _gameModeApplied;
    private int? _mouseProfileBefore, _eqBefore;

    [StructLayout(LayoutKind.Sequential)] private struct LASTINPUTINFO { public uint cbSize, dwTime; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    public TrayContext()
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        Settings = SettingsStore.Load<AppSettings>();
        Devices = DeviceManager.CreateDefault();
        _effects = new EffectEngine(Devices);
        _screen = new BaseStationScreen(Devices, DashboardSnapshot);
        Devices.DevicesChanged += () => _ui.Post(_ => OnDevicesChanged(), null);
        Devices.DeviceArrived += d =>
        {
            if (d is RazerHuntsmanElite) _ui.Post(_ => { _gameModeApplied = null; ApplyKeyboardLook(persist: false); UpdateGameMode(); }, null);
        };
        Log.Info("startup: devices created");

        _icon = new NotifyIcon { Text = "GearTray", ContextMenuStrip = _menu, Visible = true };
        UpdateIcon();
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowSettings(); };
        _menu.Opening += (_, e) => { BuildMenu(); e.Cancel = false; };
        Log.Info("startup: tray icon created");

        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();
        _rescanTimer.Tick += (_, _) => { _rescanTimer.Stop(); _ = RescanAsync(); };
        _idleTimer.Tick += (_, _) => CheckIdle();
        _idleTimer.Start();
        _lightTimer.Tick += (_, _) => { _lightTimer.Stop(); ReapplyControllerLight(); };
        _deviceWatcher = new DeviceChangeWindow(() =>
        {
            _rescanTimer.Stop();
            _rescanTimer.Start();
            if (Settings.Controller.LightCustom) { _lightTimer.Stop(); _lightTimer.Start(); }
        }, ShowSettings, OnDisplayState, () => { Log.Info("Exiting for an update"); ExitThread(); });
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _hook.KeyEvent += (vk, scan, extended, up) =>
        {
            // Ripple: locate the key on the keyboard's LED grid (runs on the hook thread, so keep it cheap).
            if (up || Devices.Get<RazerHuntsmanElite>() is not { Connected: true } k) return;
            if (HuntsmanLayout.Locate(vk, scan, extended, k.IsIso) is var (row, col)) _effects.KeyPressed(row, col);
        };
        _games = new GameDetector();
        _games.Changed += () => { ApplyProgramProfiles(force: false); UpdateGameMode(); ProfileChanged?.Invoke(); };
        Log.Info("startup: watchers registered");

        Started = StartAsync();
    }

    private async Task StartAsync()
    {
        Log.Info($"GearTray {Application.ProductVersion} started (admin rights: {Elevation.IsAdmin})");
        await RescanAsync();
        ApplyKeyboardLook(persist: false);
        ApplyInputFeatures();
        RefreshStatus();
        ApplyHeadsetScreen();
        ReapplyControllerLight();
    }

    // ---------- Xbox controller ----------

    /// <summary>Sets the Xbox button light off the UI thread; returns how many controllers took it (-1: no Xbox driver).</summary>
    public Task<int> SetControllerLightAsync(XboxLedMode mode, int brightness) => Task.Run(() =>
    {
        try { return XboxGip.SetLight(mode, brightness); }
        catch (Exception ex) { Log.Error("Xbox button light", ex); return -1; }
    });

    /// <summary>Windows resets the Xbox button light on every connection, so the user's choice is put back each time.</summary>
    private void ReapplyControllerLight()
    {
        var c = Settings.Controller;
        if (c.LightCustom && XInput.ConnectedSlots().Any()) _ = SetControllerLightAsync(c.LightMode, c.LightBrightness);
    }

    // ---------- base station screen ----------

    /// <summary>Start or stop GearTray's dashboard on the Arctis base station screen, per the setting.</summary>
    public void ApplyHeadsetScreen()
    {
        var s = Settings.HeadsetScreen;
        if (s.Dashboard)
        {
            _screen.Start();
            if (!s.WasShowing) { s.WasShowing = true; SettingsStore.Save(Settings); }
        }
        else
        {
            if (_screen.Running) _screen.Stop();
            else if (s.WasShowing) _screen.Restore(); // left over from a run that didn't exit cleanly
            if (s.WasShowing) { s.WasShowing = false; SettingsStore.Save(Settings); }
        }
    }

    /// <summary>Called on the screen's timer thread every second.</summary>
    private DashboardData DashboardSnapshot()
    {
        var h = _headset;
        var s = Settings.HeadsetScreen;
        string? pad = _controllerConnected ? (ControllerBattery is { } c ? $"Pad {c.Percent}%" : "Pad on cable") : null;
        var game = s.ShowPcStats ? _games.RunningGame() : null; // still counts while alt-tabbed out of it
        return new DashboardData(
            _mouseBattery?.Percent, _mouseBattery?.Charging ?? false,
            h is { HeadsetOnline: true } ? h.BatteryPercent : null, h?.Charging ?? false, h?.SpareBatteryPercent,
            pad, GameModeActive, ActiveProfile?.Name,
            game?.Name, game?.Started, s.ShowNowPlaying, s.ShowPcStats,
            s.ClockSeconds, s.NowPlayingSeconds, s.PcStatsSeconds, game?.Pid ?? 0,
            s.PingHost);
    }

    /// <summary>A pop-up on the base-station screen, if it's GearTray's and pop-ups are on.</summary>
    private void ScreenPopup(string title, string text)
    {
        if (Settings.HeadsetScreen.ShowPopups) _screen.Popup(title, text);
    }

    private async Task RescanAsync()
    {
        try { await Devices.RescanAsync(); }
        catch (Exception ex) { Log.Error("Device scan", ex); }
    }

    private void OnDevicesChanged()
    {
        if (Devices.Get<RazerHuntsmanElite>() is not { Connected: true }) _effects.Stop();
        RefreshStatus();
        _form?.OnDevicesChanged();
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) { _ui.Post(_ => { _effects.Stop(); _screen.Stop(); }, null); return; }
        if (e.Mode != PowerModes.Resume) return;
        _ui.Post(async _ =>
        {
            await Task.Delay(5000);           // let USB devices come back after sleep
            await Devices.ReconnectAllAsync();
            _gameModeApplied = null;
            ApplyKeyboardLook(persist: false);
            UpdateGameMode();
            RefreshStatus();
            ApplyHeadsetScreen();
            ReapplyControllerLight();
        }, null);
    }

    private void OnDisplayState(bool on)
    {
        if (on == _displayOn) return;
        _displayOn = on;
        Log.Info($"Displays {(on ? "on" : "off")}");
        if (Settings.KeyboardFeatures.LightsOffWhenDisplayOff) ApplyKeyboardLook(persist: false);
    }

    private void CheckIdle()
    {
        int minutes = Settings.KeyboardFeatures.LightsOffIdleMinutes;
        bool idle = false;
        if (minutes > 0)
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (GetLastInputInfo(ref info)) idle = unchecked((uint)Environment.TickCount - info.dwTime) > minutes * 60_000u;
        }
        if (idle == _idle) return;
        _idle = idle;
        ApplyKeyboardLook(persist: false);
    }

    // ---------- keyboard: lighting ----------

    public string EffectStatus => _effects.Running ? _effects.Status : "not running";

    private bool KeyboardShouldBeDark =>
        Settings.LightsOff
        || (Settings.KeyboardFeatures.LightsOffWhenDisplayOff && !_displayOn)
        || (Settings.KeyboardFeatures.LightsOffIdleMinutes > 0 && _idle);

    /// <summary>The keyboard look in force: the active program profile's, or the normal one.</summary>
    private KeyboardLightingSettings EffectiveKeyboard => ActiveProfile?.KeyboardLighting ?? Settings.Keyboard;

    /// <summary>
    /// Put the right look on the keyboard: dark (lights-off / screen off / idle), a software effect, or a hardware effect.
    /// persist = also store a hardware effect in the keyboard (user changes); false for automatic re-applies.
    /// </summary>
    public async void ApplyKeyboardLook(bool persist)
    {
        if (Devices.Get<RazerHuntsmanElite>() is not { Connected: true } kb) { _effects.Stop(); return; }
        bool dark = KeyboardShouldBeDark;
        var cfg = EffectiveKeyboard;
        if (!dark && !cfg.Configured) { _effects.Stop(); return; } // never configured: leave the keyboard's own lighting alone
        var look = dark ? new KeyboardLighting(KeyboardEffect.Off, Color.Black, 0) : cfg.ToLighting();
        bool softwareRipple = look.Effect == KeyboardEffect.Ripple && !kb.HardwareRipple;
        _hook.WantKeyEvents = softwareRipple;
        if (look.Effect == KeyboardEffect.Ripple && kb.HardwareRipple)
        {
            _effects.Stop();
            bool keep = persist && ActiveProfile is null;
            Log.Info($"Keyboard lighting: the keyboard's own ripple{(keep ? ", saved in the keyboard" : "")}");
            try { await Devices.RunAsync(() => kb.ApplyHardwareRipple(look.Color, look.Brightness, keep)); }
            catch (Exception ex) { Log.Error("Keyboard ripple", ex); }
            return;
        }
        if (look.Effect == KeyboardEffect.Custom)
        {
            // Per-key colors: one frame, kept by the keyboard until something else is sent.
            _effects.Stop();
            Log.Info("Keyboard lighting: per-key colors");
            var frame = Scale(cfg.CustomFrame(), look.Brightness);
            try { await Devices.RunAsync(() => kb.SendFrame(frame)); }
            catch (Exception ex) { Log.Error("Keyboard per-key colors", ex); }
            return;
        }
        if (look.IsSoftware)
        {
            _effects.Start(look, cfg.ToAmbient(), cfg);
            return;
        }
        _effects.Stop();
        bool store = persist && ActiveProfile is null && (!dark || Settings.LightsOff);
        Log.Info($"Keyboard lighting: {look.Effect}" + (look.Effect == KeyboardEffect.Off ? "" : $", {look.Brightness}%") + (store ? ", saved in the keyboard" : ""));
        try { await Devices.RunAsync(() => kb.Apply(look, store)); }
        catch (Exception ex) { Log.Error("Keyboard lighting", ex); }
    }

    /// <summary>Show a per-key picture right away without saving it (the per-key painter's live preview).</summary>
    public async void PreviewKeyboardFrame(byte[] frame, int brightness)
    {
        if (Devices.Get<RazerHuntsmanElite>() is not { Connected: true } kb) return;
        _effects.Stop();
        _hook.WantKeyEvents = false;
        var scaled = Scale(frame, brightness);
        try { await Devices.RunAsync(() => kb.SendFrame(scaled)); }
        catch (Exception ex) { Log.Warn("Per-key preview: " + ex.Message); }
    }

    private static byte[] Scale(byte[] frame, int brightness)
    {
        double k = Math.Clamp(brightness, 0, 100) / 100.0;
        return frame.Select(b => (byte)Math.Round(b * k)).ToArray();
    }

    // ---------- keyboard: game mode, remaps, macros ----------

    public string? CurrentGame => _games.CurrentGame;
    public string? CurrentProgram => _games.CurrentProgram;

    public bool GameModeActive =>
        ActiveProfile?.GameMode
        ?? (Settings.KeyboardFeatures.GameModeManual || (Settings.KeyboardFeatures.GameModeAuto && _games.CurrentGame is not null));

    /// <summary>Push game-detection lists, blocked keys, remaps and macros to the detector and the keyboard hook.</summary>
    public void ApplyInputFeatures()
    {
        var f = Settings.KeyboardFeatures;
        _games.Configure(f.ExtraGames, f.IgnoredApps);
        UpdateGameMode();
    }

    /// <summary>Send every key press to the macro editor (and nowhere else) until StopMacroRecording.</summary>
    public void StartMacroRecording(Action<int, bool> onKey) => _hook.StartRecording(onKey);
    public void StopMacroRecording() => _hook.StopRecording();

    /// <summary>Key presses for effects that react to typing (raised on the hook thread).</summary>
    public KeyboardHook Hook => _hook;

    private void UpdateGameMode()
    {
        var f = Settings.KeyboardFeatures;
        bool on = GameModeActive;
        _hook.Configure(on, f.BlockWindowsKey, f.BlockAltTab, f.BlockAltF4, f.Remaps, f.Macros);
        if (_gameModeShown is bool shown && shown != on) ScreenPopup("Game mode", on ? "On" : "Off");
        _gameModeShown = on;
        if (_gameModeApplied == on) return;
        _gameModeApplied = on;
        Log.Info($"Game mode {(on ? "on" : "off")}{(_games.CurrentGame is string g && f.GameModeAuto ? $" ({g} in front)" : "")}");
        if (Devices.Get<RazerHuntsmanElite>() is { Connected: true } kb)
            _ = Devices.RunAsync(() =>
            {
                try { kb.SetGameMode(on); } // the keyboard's game-mode light
                catch (Exception ex) { Log.Warn("Keyboard game-mode light: " + ex.Message); }
            });
        GameModeChanged?.Invoke();
    }

    // ---------- program profiles ----------

    /// <summary>The program profile whose program is in front, or null.</summary>
    public ProgramProfile? ActiveProfile { get; private set; }

    /// <summary>Switch to the profile matching the program in front (or back to normal). force = re-apply even if unchanged.</summary>
    public async void ApplyProgramProfiles(bool force)
    {
        string? program = _games.CurrentProgram;
        var next = program is null ? null : Settings.ProgramProfiles.FirstOrDefault(p =>
            p.Enabled && p.Programs.Any(x => x.Equals(program, StringComparison.OrdinalIgnoreCase)));
        if (next == ActiveProfile && !force) return;
        var previous = ActiveProfile;
        ActiveProfile = next;
        if (next != previous)
        {
            Log.Info(next is null ? $"Program profile off ({program ?? "nothing"} in front)" : $"Program profile “{next.Name}” on ({program})");
            ScreenPopup("Profile", next?.Name ?? "Normal");
        }

        // Keyboard look and game mode follow ActiveProfile through EffectiveKeyboard / GameModeActive.
        if (next?.KeyboardLighting is not null || previous?.KeyboardLighting is not null || force) ApplyKeyboardLook(persist: false);
        UpdateGameMode();

        // Mouse onboard profile: remember the one in use, switch, and switch back when the profile ends.
        if (Devices.Get<RogOmniMouse>() is { Connected: true } mouse && (next?.MouseProfile is not null || previous?.MouseProfile is not null))
        {
            try
            {
                int? target = next?.MouseProfile;
                await Devices.RunAsync(() =>
                {
                    if (target is int t)
                    {
                        _mouseProfileBefore ??= mouse.GetProfile();
                        mouse.SwitchProfile(t);
                    }
                    else if (_mouseProfileBefore is int back)
                    {
                        mouse.SwitchProfile(back);
                        _mouseProfileBefore = null;
                    }
                });
            }
            catch (Exception ex) { Log.Warn("Mouse profile switch: " + ex.Message); }
        }

        // Headset EQ preset: same idea.
        if (Devices.Get<ArctisNovaProWireless>() is { Connected: true } headset && (next?.HeadsetEqPreset is not null || previous?.HeadsetEqPreset is not null))
        {
            try
            {
                int? target = next?.HeadsetEqPreset;
                await Devices.RunAsync(() =>
                {
                    var have = headset.ReadState();
                    var want = have.Clone();
                    if (target is int t) { _eqBefore ??= have.EqPreset; want.EqPreset = t; }
                    else if (_eqBefore is int back) { want.EqPreset = back; _eqBefore = null; }
                    headset.ApplyState(want, have);
                });
            }
            catch (Exception ex) { Log.Warn("Headset EQ switch: " + ex.Message); }
        }
        ProfileChanged?.Invoke();
    }

    // ---------- status ----------

    public void RefreshSoon() => RefreshStatus();

    private async void RefreshStatus()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var mouse = Devices.Get<RogOmniMouse>();
            var headset = Devices.Get<ArctisNovaProWireless>();
            var (mb, hs) = await Devices.RunAsync(() =>
            {
                BatteryInfo? m = null; HeadsetState? h = null;
                if (mouse is { Connected: true })
                    try { m = mouse.ReadBattery(); } catch (Exception ex) { Log.Warn("Mouse battery: " + ex.Message); }
                if (headset is { Connected: true })
                    try { h = headset.ReadStatus(); } catch (Exception ex) { Log.Warn("Headset status: " + ex.Message); }
                return (m, h);
            });

            if (mouse is not { Connected: true }) { _mouseBattery = null; _mouseAsleep = false; }
            else if (mb is null) _mouseAsleep = true;   // keep the last known level while it sleeps
            else { _mouseBattery = mb; _mouseAsleep = false; }
            _headset = headset is { Connected: true } ? hs : null;

            int slot = XInput.ConnectedSlots().DefaultIfEmpty(-1).First();
            _controllerConnected = slot >= 0;
            if (_controllerShown is bool wasConnected && wasConnected != _controllerConnected)
                ScreenPopup("Controller", _controllerConnected ? "Connected" : "Disconnected");
            _controllerShown = _controllerConnected;
            _controllerBattery = slot >= 0 ? XInput.GetBattery(slot) : null;
            UpdateIcon();
            CheckLowBattery();
            UpdateMouseBatteryLight();
        }
        catch (Exception ex) { Log.Error("Status refresh", ex); }
        finally { _refreshing = false; }
    }

    /// <summary>"Only when the battery is low" mouse lighting: dark normally, breathing warning color when low.</summary>
    private async void UpdateMouseBatteryLight()
    {
        var cfg = Settings.MouseBatteryLight;
        if (!cfg.Enabled || _mouseAsleep) return;
        if (Devices.Get<RogOmniMouse>() is not { Connected: true, HasLighting: true } m) return;
        bool warn = _mouseBattery is { Charging: false } b && b.Percent < cfg.Threshold;
        if (!warn && !_mouseWarnShown) return;
        var look = warn
            ? new MouseLighting(MouseLightMode.Breathing, ColorUtil.Parse(cfg.Color), 100)
            : new MouseLighting(MouseLightMode.Off, Color.Black, 0);
        try
        {
            // Re-sent every minute while low (live only, nothing stored), so it survives the mouse waking up.
            await Devices.RunAsync(() => m.ApplyLighting(look, persist: false));
            if (warn != _mouseWarnShown) Log.Info(warn ? "Mouse battery low: wheel light on" : "Mouse battery fine: wheel light off");
            _mouseWarnShown = warn;
        }
        catch (Exception ex) { Log.Warn("Mouse battery light: " + ex.Message); }
    }

    private BatteryInfo? HeadsetBattery =>
        _headset is { HeadsetOnline: true, BatteryPercent: int p } ? new BatteryInfo(p, _headset.Charging) : null;

    /// <summary>Wireless controller battery as an approximate percentage (XInput only knows four steps).</summary>
    private BatteryInfo? ControllerBattery =>
        _controllerBattery is { Wireless: true } b ? new BatteryInfo(b.ApproxPercent, false) : null;

    public void UpdateIcon()
    {
        BatteryInfo? shown = Settings.TrayBattery switch
        {
            TrayBatterySource.Mouse => _mouseBattery,
            TrayBatterySource.Headset => HeadsetBattery,
            TrayBatterySource.Controller => ControllerBattery,
            _ => new[]
                {
                    Shown("mouse") ? _mouseBattery : null,
                    Shown("headset") ? HeadsetBattery : null,
                    Shown("controller") ? ControllerBattery : null,
                }.Where(b => b is not null).MinBy(b => b!.Percent),
        };
        var icon = IconRenderer.Render(shown?.Percent, shown?.Charging ?? false);
        _icon.Icon = icon;
        _currentIcon?.Dispose();
        _currentIcon = icon;
        var lines = new List<string> { "GearTray" };
        if (Shown("mouse")) lines.Add(MouseLine());
        if (Shown("headset")) lines.Add(HeadsetLine());
        if (Shown("controller") && _controllerConnected) lines.Add(ControllerLine());
        string tip = string.Join("\n", lines);
        _icon.Text = tip.Length > 127 ? tip[..127] : tip;
    }

    /// <summary>Whether a device tab (or "tab.section") is shown, i.e. not hidden by the user.</summary>
    private bool Shown(string tab, string? section = null) =>
        !Settings.IsHidden($"tab.{tab}") && (section is null || !Settings.IsHidden($"{tab}.{section}"));

    private string ControllerLine() => _controllerBattery switch
    {
        { Wireless: true } b => $"Controller: battery {b.Level.ToString().ToLowerInvariant()}",
        _ => "Controller: connected by cable",
    };

    private string MouseLine()
    {
        var m = Devices.Get<RogOmniMouse>();
        if (m is not { Connected: true }) return "Mouse: not connected";
        if (_mouseBattery is null) return _mouseAsleep ? "Mouse: asleep" : "Mouse: battery unknown";
        return $"Mouse: {_mouseBattery.Percent}%{(_mouseBattery.Charging ? " (charging)" : "")}{(_mouseAsleep ? " (asleep)" : "")}";
    }

    private string HeadsetLine()
    {
        var h = Devices.Get<ArctisNovaProWireless>();
        if (h is not { Connected: true }) return "Headset: base station not connected";
        if (_headset is null) return "Headset: no answer";
        string spare = _headset.SpareBatteryPercent is int s ? $" · spare {s}%" : "";
        return _headset.HeadsetOnline
            ? $"Headset: {_headset.BatteryPercent}%{(_headset.Charging ? " (charging)" : "")}{spare}"
            : $"Headset: off{spare}";
    }

    private string KeyboardLine()
    {
        var k = Devices.Get<RazerHuntsmanElite>();
        return k switch
        {
            { Connected: true } => "Keyboard: ready" + (GameModeActive ? " · game mode on" : ""),
            { Problem: not null } => "Keyboard: " + k.Problem,
            _ => "Keyboard: not connected",
        };
    }

    private void CheckLowBattery()
    {
        if (!Settings.LowBatteryNotifications) return;
        int limit = Settings.LowBatteryPercent;
        Check(_mouseBattery, ref _mouseLowShown, "Mouse", "Charge it soon.");
        string swap = _headset?.SpareBatteryPercent is int s ? $"Swap in the spare battery from the base station ({s}%)." : "Charge it soon.";
        Check(HeadsetBattery, ref _headsetLowShown, "Headset", swap);

        if (Settings.Controller.LowBatteryNotification && _controllerBattery is { Wireless: true } pad)
        {
            bool low = pad.Level is PadBatteryLevel.Low or PadBatteryLevel.Empty;
            if (!low) _controllerLowShown = false;
            else if (!_controllerLowShown)
            {
                _controllerLowShown = true;
                _icon.ShowBalloonTip(10_000, "Controller battery low", $"The Xbox controller's battery is {pad.Level.ToString().ToLowerInvariant()}. Charge it or swap the batteries soon.", ToolTipIcon.Warning);
            }
        }

        void Check(BatteryInfo? b, ref bool shown, string what, string advice)
        {
            if (b is null) return;
            if (b.Charging || b.Percent >= limit + 5) { shown = false; return; }
            if (b.Percent < limit && !shown)
            {
                shown = true;
                _icon.ShowBalloonTip(10_000, $"{what} battery low", $"{what} battery is at {b.Percent}%. {advice}", ToolTipIcon.Warning);
            }
        }
    }

    // ---------- menu ----------

    private void BuildMenu()
    {
        _menu.Items.Clear();
        if (Shown("mouse")) Info(MouseLine());
        if (Shown("headset")) Info(HeadsetLine());
        if (Shown("keyboard")) Info(KeyboardLine());
        if (Shown("controller") && _controllerConnected) Info(ControllerLine());
        if (ActiveProfile is { } profile) Info($"Program profile: {profile.Name}");
        _menu.Items.Add(new ToolStripSeparator());

        if (Shown("headset", "noise") && Devices.Get<ArctisNovaProWireless>() is { Connected: true } && _headset is not null)
        {
            var noise = new ToolStripMenuItem("Noise control");
            foreach (var (mode, label) in new[] { (NoiseMode.NoiseCancelling, "Noise cancelling"), (NoiseMode.Transparency, "Transparency"), (NoiseMode.Off, "Off") })
            {
                var item = new ToolStripMenuItem(label) { Checked = _headset.Noise == mode };
                item.Click += async (_, _) => await SetNoiseAsync(mode);
                noise.DropDownItems.Add(item);
            }
            _menu.Items.Add(noise);
        }

        var f = Settings.KeyboardFeatures;
        if (Shown("keyboard", "gamemode"))
        {
            var game = new ToolStripMenuItem("Game mode") { Checked = f.GameModeManual };
            if (f.GameModeAuto) game.Text += " (also turns on by itself in games)";
            game.Click += (_, _) =>
            {
                f.GameModeManual = !f.GameModeManual;
                SaveSettings();
                UpdateGameMode();
                _form?.OnDevicesChanged();
            };
            _menu.Items.Add(game);
        }

        var kb = Devices.Get<RazerHuntsmanElite>();
        if (kb is { Connected: false, Problem: not null } && !Elevation.IsAdmin)
            _menu.Items.Add("Restart as admin (for the keyboard)", null, (_, _) => RestartElevated());

        if (Shown("keyboard", "lighting") || Shown("mouse", "lighting"))
        {
            var lights = new ToolStripMenuItem("Keyboard and mouse lights off") { Checked = Settings.LightsOff };
            lights.Click += async (_, _) => await SetLightsOffAsync(!Settings.LightsOff);
            _menu.Items.Add(lights);
        }

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Settings…", null, (_, _) => ShowSettings());
        _menu.Items.Add("Exit", null, (_, _) => ExitThread());

        void Info(string text) => _menu.Items.Add(new ToolStripMenuItem(text) { Enabled = false });
    }

    private async Task SetNoiseAsync(NoiseMode mode)
    {
        if (Devices.Get<ArctisNovaProWireless>() is not { Connected: true } h) return;
        await Do(() =>
        {
            var have = h.ReadState();
            var want = have.Clone();
            want.Noise = mode;
            h.ApplyState(want, have);
        });
        RefreshStatus();
        _form?.OnDevicesChanged();
    }

    public async Task SetLightsOffAsync(bool off)
    {
        var mouse = Devices.Get<RogOmniMouse>();
        var mouseBefore = Settings.MouseLightingBeforeOff?.ToLighting();
        MouseLighting? saved = null;
        bool ok = await Do(() =>
        {
            if (mouse is not { Connected: true, HasLighting: true } || Settings.MouseBatteryLight.Enabled) return;
            try
            {
                if (off)
                {
                    var current = mouse.ReadCurrentLighting();
                    if (current.Mode != MouseLightMode.Off) saved = current;
                    mouse.ApplyLighting(new MouseLighting(MouseLightMode.Off, Color.Black, 0), persist: true);
                }
                else if (mouseBefore is not null)
                {
                    mouse.ApplyLighting(mouseBefore, persist: true);
                }
            }
            catch (MouseAsleepException) { Log.Warn("Mouse asleep; its lighting was not changed"); }
        });
        if (!ok) return;
        if (saved is not null) Settings.MouseLightingBeforeOff = MouseLightingSettings.From(saved);
        Settings.LightsOff = off;
        SaveSettings();
        ApplyKeyboardLook(persist: true);
        _form?.OnDevicesChanged();
    }

    private async Task<bool> Do(Action action)
    {
        try { await Devices.RunAsync(action); return true; }
        catch (Exception ex)
        {
            Log.Error("Action failed", ex);
            _icon.ShowBalloonTip(5000, "GearTray", ex.Message, ToolTipIcon.Error);
            return false;
        }
    }

    // ---------- windows, settings, elevation ----------

    public void ShowSettings()
    {
        if (_form is { IsDisposed: false })
        {
            if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
            _form.Activate();
            return;
        }
        _form = new SettingsForm(this);
        _form.FormClosed += (_, _) => _form = null;
        _form.Show();
        _form.Activate();
    }

    public void SaveSettings()
    {
        SettingsStore.Save(Settings);
        UpdateIcon();
    }

    /// <summary>Create or remove the logon task that starts GearTray with admin rights. Prompts for elevation if needed.</summary>
    public bool SetStartup(bool enable)
    {
        try
        {
            if (Elevation.IsAdmin)
            {
                if (enable) Elevation.InstallStartupTask(); else Elevation.UninstallStartupTask();
                return true;
            }
            return Elevation.RunElevated(enable ? "--install-startup" : "--uninstall-startup", wait: true);
        }
        catch (Exception ex)
        {
            Log.Error("Startup task", ex);
            return false;
        }
    }

    /// <summary>Hand over to an elevated instance (via the startup task if it exists, so there is no UAC prompt).</summary>
    public void RestartElevated()
    {
        Program.ReleaseInstanceLock();
        bool started = Elevation.StartupTaskExists() ? Elevation.RunStartupTask() : Elevation.RunElevated("", wait: false);
        if (started) ExitThread();
        else Program.AcquireInstanceLock();
    }

    protected override void ExitThreadCore()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _statusTimer.Dispose();
        _rescanTimer.Dispose();
        _idleTimer.Dispose();
        _lightTimer.Dispose();
        try { XInput.StopAll(); GamepadTriggers.Stop(); }   // never leave a controller rumbling
        catch (Exception ex) { Log.Warn($"Stopping controller vibration: {ex.Message}"); }
        _effects.Dispose();
        if (_screen.Running)
        {
            _screen.Stop();                                   // hand the base station screen back…
            Devices.RunAsync(() => { }).Wait(1500);           // …before the device thread shuts down
            Settings.HeadsetScreen.WasShowing = false;
            SettingsStore.Save(Settings);
        }
        _hook.Dispose();
        _games.Dispose();
        _deviceWatcher.Dispose();
        _form?.Close();
        _icon.Visible = false;
        _icon.Dispose();
        _currentIcon?.Dispose();
        Devices.Dispose();
        Log.Info("GearTray exited");
        base.ExitThreadCore();
    }
}
