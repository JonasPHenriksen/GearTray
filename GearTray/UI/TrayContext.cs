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
    private readonly DeviceChangeWindow _deviceWatcher;
    private readonly EffectEngine _effects;
    private readonly KeyboardHook _hook = new();
    private readonly GameDetector _games;
    private SettingsForm? _form;
    private Icon? _currentIcon;

    public DeviceManager Devices { get; }
    public AppSettings Settings { get; }
    public Task Started { get; }

    /// <summary>Raised on the UI thread when game mode turns on or off.</summary>
    public event Action? GameModeChanged;

    // last known state
    private BatteryInfo? _mouseBattery;
    private bool _mouseAsleep, _mouseWarnShown;
    private HeadsetState? _headset;
    private bool _mouseLowShown, _headsetLowShown, _refreshing;
    private bool _displayOn = true, _idle;
    private bool? _gameModeApplied;

    [StructLayout(LayoutKind.Sequential)] private struct LASTINPUTINFO { public uint cbSize, dwTime; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    public TrayContext()
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        Settings = SettingsStore.Load<AppSettings>();
        Devices = DeviceManager.CreateDefault();
        _effects = new EffectEngine(Devices);
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
        _deviceWatcher = new DeviceChangeWindow(() => { _rescanTimer.Stop(); _rescanTimer.Start(); }, ShowSettings, OnDisplayState);
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _games = new GameDetector();
        _games.Changed += UpdateGameMode;
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
        if (e.Mode == PowerModes.Suspend) { _ui.Post(_ => _effects.Stop(), null); return; }
        if (e.Mode != PowerModes.Resume) return;
        _ui.Post(async _ =>
        {
            await Task.Delay(5000);           // let USB devices come back after sleep
            await Devices.ReconnectAllAsync();
            _gameModeApplied = null;
            ApplyKeyboardLook(persist: false);
            UpdateGameMode();
            RefreshStatus();
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

    /// <summary>
    /// Put the right look on the keyboard: dark (lights-off / screen off / idle), a software effect, or a hardware effect.
    /// persist = also store a hardware effect in the keyboard (user changes); false for automatic re-applies.
    /// </summary>
    public async void ApplyKeyboardLook(bool persist)
    {
        if (Devices.Get<RazerHuntsmanElite>() is not { Connected: true } kb) { _effects.Stop(); return; }
        bool dark = KeyboardShouldBeDark;
        var cfg = Settings.Keyboard;
        if (!dark && !cfg.Configured) { _effects.Stop(); return; } // never configured: leave the keyboard's own lighting alone
        var look = dark ? new KeyboardLighting(KeyboardEffect.Off, Color.Black, 0) : cfg.ToLighting();
        if (look.IsSoftware)
        {
            _effects.Start(look, cfg.ToAmbient());
            return;
        }
        _effects.Stop();
        bool store = persist && (!dark || Settings.LightsOff);
        try { await Devices.RunAsync(() => kb.Apply(look, store)); }
        catch (Exception ex) { Log.Error("Keyboard lighting", ex); }
    }

    // ---------- keyboard: game mode and remaps ----------

    public string? CurrentGame => _games.CurrentGame;

    public bool GameModeActive =>
        Settings.KeyboardFeatures.GameModeManual || (Settings.KeyboardFeatures.GameModeAuto && _games.CurrentGame is not null);

    /// <summary>Push game-detection lists, blocked keys and remaps to the detector and the keyboard hook.</summary>
    public void ApplyInputFeatures()
    {
        var f = Settings.KeyboardFeatures;
        _games.Configure(f.ExtraGames, f.IgnoredApps);
        UpdateGameMode();
    }

    private void UpdateGameMode()
    {
        var f = Settings.KeyboardFeatures;
        bool on = GameModeActive;
        _hook.Configure(on, f.BlockWindowsKey, f.BlockAltTab, f.BlockAltF4, f.Remaps);
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

    public void UpdateIcon()
    {
        BatteryInfo? shown = Settings.TrayBattery switch
        {
            TrayBatterySource.Mouse => _mouseBattery,
            TrayBatterySource.Headset => HeadsetBattery,
            _ => new[] { _mouseBattery, HeadsetBattery }.Where(b => b is not null).MinBy(b => b!.Percent),
        };
        var icon = IconRenderer.Render(shown?.Percent, shown?.Charging ?? false);
        _icon.Icon = icon;
        _currentIcon?.Dispose();
        _currentIcon = icon;
        string tip = $"GearTray\n{MouseLine()}\n{HeadsetLine()}";
        _icon.Text = tip.Length > 127 ? tip[..127] : tip;
    }

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
        Info(MouseLine());
        Info(HeadsetLine());
        Info(KeyboardLine());
        _menu.Items.Add(new ToolStripSeparator());

        if (Devices.Get<ArctisNovaProWireless>() is { Connected: true } && _headset is not null)
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

        var kb = Devices.Get<RazerHuntsmanElite>();
        if (kb is { Connected: false, Problem: not null } && !Elevation.IsAdmin)
            _menu.Items.Add("Restart as admin (for the keyboard)", null, (_, _) => RestartElevated());

        var lights = new ToolStripMenuItem("Keyboard and mouse lights off") { Checked = Settings.LightsOff };
        lights.Click += async (_, _) => await SetLightsOffAsync(!Settings.LightsOff);
        _menu.Items.Add(lights);

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
        _effects.Dispose();
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
