using System.Diagnostics;
using GearTray.Devices;
using GearTray.Input;
using GearTray.Util;

namespace GearTray.UI;

/// <summary>Settings window: Mouse / Keyboard / Headset / General tabs. All device I/O runs on the device thread.</summary>
public sealed class SettingsForm : Form
{
    private readonly TrayContext _tray;
    private DeviceManager Devices => _tray.Devices;
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 30, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 0, 10, 0) };
    private bool _loading;

    // ---- mouse ----
    private readonly Label _mouseInfo = new() { AutoSize = true, MaximumSize = new Size(560, 0) };
    private readonly NumericUpDown[] _dpi = new NumericUpDown[4];
    private readonly Button[] _dpiColor = new Button[4];
    private readonly Label[] _dpiActive = new Label[4];
    private readonly ComboBox _polling = Ui.Combo(new Item<int>("125 Hz", 125), new("250 Hz", 250), new("500 Hz", 500), new("1000 Hz", 1000));
    private readonly ComboBox _debounce = Ui.Combo(RogOmniMouse.DebounceTable.Skip(1).Select(ms => new Item<int>($"{ms} ms", ms)).ToArray());
    private readonly CheckBox _angle = new() { Text = "On", AutoSize = true };
    private readonly ComboBox _lod = Ui.Combo(new Item<bool>("Low", false), new("High", true));
    private readonly ComboBox _sleep = Ui.Combo(new Item<int>("1 min", 1), new("2 min", 2), new("3 min", 3), new("5 min", 5), new("10 min", 10), new("Never", 0));
    private readonly ComboBox _lowBatt = Ui.Combo(new Item<int>("10%", 10), new("20%", 20), new("30%", 30), new("40%", 40), new("50%", 50));
    private readonly ComboBox _mouseLight = Ui.Combo(
        new Item<MouseLightMode>("Off", MouseLightMode.Off), new("Static color", MouseLightMode.Static),
        new("Breathing", MouseLightMode.Breathing), new("Color cycle", MouseLightMode.ColorCycle),
        new("Rainbow", MouseLightMode.Rainbow), new("Reactive (lights up on click)", MouseLightMode.Reactive),
        new("Battery level (always on)", MouseLightMode.Battery));
    private readonly Button _mouseLightColor = Ui.ColorButton();
    private readonly LabeledSlider _mouseLightBrightness = new(1, 100, v => $"{v}%");
    private readonly CheckBox _mouseLowOnly = new() { Text = "Only light up when the battery is low", AutoSize = true };
    private readonly NumericUpDown _mouseLowThreshold = new() { Minimum = 5, Maximum = 50, Width = 60 };
    private readonly Button _mouseLowColor = Ui.ColorButton();
    private Control _mousePage = null!;
    private Button _mouseApply = null!, _mouseReload = null!;
    private MouseSettings? _mouseHave;
    private MouseLighting? _mouseLightHave;

    // ---- keyboard ----
    private readonly Label _kbInfo = new() { AutoSize = true, MaximumSize = new Size(560, 0) };
    private readonly ComboBox _kbEffect = Ui.Combo(
        new Item<KeyboardEffect>("Off", KeyboardEffect.Off), new("Static color", KeyboardEffect.Static),
        new("Breathing", KeyboardEffect.Breathing), new("Breathing (random colors)", KeyboardEffect.BreathingRandom),
        new("Spectrum cycling", KeyboardEffect.Spectrum), new("Wave", KeyboardEffect.Wave),
        new("Reactive (keys light up when pressed)", KeyboardEffect.Reactive), new("Starlight", KeyboardEffect.Starlight),
        new("Starlight (random colors)", KeyboardEffect.StarlightRandom),
        new("Ambient (follows your screen)", KeyboardEffect.Ambient), new("Audio meter (follows what you hear)", KeyboardEffect.AudioMeter));
    private readonly Button _kbColor = Ui.ColorButton();
    private readonly LabeledSlider _kbBrightness = new(0, 100, v => $"{v}%");
    private readonly ComboBox _kbWaveDir = Ui.Combo(new Item<bool>("Left to right", false), new("Right to left", true));
    private readonly ComboBox _kbSpeed = Ui.Combo(new Item<int>("Fast", 1), new("Medium", 2), new("Slow", 3));
    private readonly ComboBox _kbMonitor = Ui.Combo(Screen.AllScreens.Select((s, i) =>
        new Item<string?>($"Monitor {i + 1}: {s.Bounds.Width}×{s.Bounds.Height}{(s.Primary ? " (main)" : "")}", s.DeviceName)).ToArray());
    private readonly ComboBox _kbAmbientStyle = Ui.Combo(new Item<bool>("Keys show the picture's layout", true), new("Whole keyboard in one color", false));
    private readonly LabeledSlider _kbSaturation = new(0, 150, v => $"+{v}%");
    private readonly Label _kbEffectNote = new() { AutoSize = true, MaximumSize = new Size(560, 0), ForeColor = SystemColors.GrayText };
    private readonly ComboBox _kbPolling = Ui.Combo(new Item<int>("125 Hz", 125), new("500 Hz", 500), new("1000 Hz", 1000));
    private readonly CheckBox _kbOffDisplay = new() { Text = "When the screen turns off", AutoSize = true };
    private readonly ComboBox _kbIdle = Ui.Combo(new Item<int>("Never", 0), new("1 min", 1), new("5 min", 5), new("10 min", 10), new("15 min", 15), new("30 min", 30));
    private readonly CheckBox _gmManual = new() { Text = "On now", AutoSize = true };
    private readonly CheckBox _gmAuto = new() { Text = "Turn on by itself while a game is in front", AutoSize = true };
    private readonly CheckBox _gmWin = new() { Text = "Windows key", AutoSize = true };
    private readonly CheckBox _gmAltTab = new() { Text = "Alt+Tab", AutoSize = true };
    private readonly CheckBox _gmAltF4 = new() { Text = "Alt+F4", AutoSize = true };
    private readonly TextBox _gmExtra = new() { Width = 330 };
    private readonly TextBox _gmIgnore = new() { Width = 330 };
    private readonly Label _gmStatus = new() { AutoSize = true, MaximumSize = new Size(560, 0), ForeColor = SystemColors.GrayText };
    private readonly ListBox _remapList = new() { Width = 330, Height = 96, IntegralHeight = false };
    private readonly KeyCaptureBox _remapFrom = new(), _remapTo = new();
    private readonly CheckBox _remapDisable = new() { Text = "Disable the key instead", AutoSize = true };
    private Control _kbLightingPanel = null!;
    private Label _kbPollingRowLabel = null!;
    private Button _kbAdmin = null!;
    private int? _kbPollingHave;

    // ---- headset ----
    private readonly Label _hsInfo = new() { AutoSize = true, MaximumSize = new Size(560, 0) };
    private readonly RadioButton _ncAnc = new() { Text = "Noise cancelling", AutoSize = true };
    private readonly RadioButton _ncTrans = new() { Text = "Transparency", AutoSize = true };
    private readonly RadioButton _ncOff = new() { Text = "Off", AutoSize = true };
    private readonly LabeledSlider _transparency = new(1, 10);
    private readonly ComboBox _sidetone = Ui.Combo(new Item<int>("Off", 0), new("Low", 1), new("Medium", 2), new("High", 3));
    private readonly LabeledSlider _micVol = new(1, 10);
    private readonly LabeledSlider _micLed = new(1, 10);
    private readonly ComboBox _eq = Ui.Combo(Enumerable.Range(0, 5).Select(i => new Item<int>(ArctisNovaProWireless.EqPresetNames[i], i)).ToArray());
    private readonly TrackBar[] _eqBands = new TrackBar[10];
    private readonly ComboBox _autoOff = Ui.Combo(ArctisNovaProWireless.AutoOffMinutes.Select((m, i) => new Item<int>(m == 0 ? "Never" : $"{m} min", i)).ToArray());
    private readonly LabeledSlider _oled = new(1, 10);
    private readonly Label _chatMix = new() { AutoSize = true };
    private Control _hsPage = null!;
    private Button _hsReload = null!;
    private HeadsetState? _hsHave;

    // ---- general ----
    private readonly CheckBox _startup = new() { Text = "Start GearTray when I sign in, with admin rights (needed for the keyboard)", AutoSize = true };
    private readonly ComboBox _trayBattery = Ui.Combo(
        new Item<TrayBatterySource>("Lowest of mouse and headset", TrayBatterySource.Lowest),
        new("Mouse", TrayBatterySource.Mouse), new("Headset", TrayBatterySource.Headset));
    private readonly NumericUpDown _lowPct = new() { Minimum = 5, Maximum = 50, Width = 70 };
    private readonly CheckBox _lowNotify = new() { Text = "Show a notification", AutoSize = true };

    public SettingsForm(TrayContext tray)
    {
        _tray = tray;
        Text = "GearTray";
        Font = SystemFonts.MessageBoxFont ?? Font;
        AutoScaleMode = AutoScaleMode.Font;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(680, 760);
        MinimumSize = new Size(600, 500);
        Icon = IconRenderer.Render(null, false);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(Page("Mouse", BuildMouse()));
        tabs.TabPages.Add(Page("Keyboard", BuildKeyboard()));
        tabs.TabPages.Add(Page("Headset", BuildHeadset()));
        tabs.TabPages.Add(Page("General", BuildGeneral()));
        Controls.Add(tabs);
        Controls.Add(_status);
        Shown += async (_, _) => await ReloadAll();
        _tray.GameModeChanged += UpdateGameModeStatus;
        FormClosed += (_, _) => _tray.GameModeChanged -= UpdateGameModeStatus;
    }

    /// <summary>Diagnostics (--screenshot): render every tab to a PNG after the devices were read.</summary>
    public async Task CaptureTabsAsync(string dir)
    {
        await _tray.Started;
        await ReloadAll();
        var tabs = Controls.OfType<TabControl>().First();
        foreach (TabPage page in tabs.TabPages)
        {
            tabs.SelectedTab = page;
            await Task.Delay(300);
            var scroll = page.Controls[0];
            var content = scroll.Controls[0];
            // Render the whole (scrollable) page, not just the visible part.
            using var bmp = new Bitmap(Math.Max(content.Width, 10), Math.Max(content.Height, 10));
            content.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
            bmp.Save(Path.Combine(dir, $"tab-{page.Text.ToLowerInvariant()}.png"), System.Drawing.Imaging.ImageFormat.Png);
        }
    }

    /// <summary>Called by the tray when devices connect/disconnect or another part of the app changed them.</summary>
    public async void OnDevicesChanged()
    {
        if (!_loading && Visible) await ReloadAll();
    }

    private async Task ReloadAll()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            await LoadMouse();
            await LoadKeyboard();
            await LoadHeadset();
            Status(Elevation.IsAdmin ? "Ready." : "Ready. Running without admin rights, so the keyboard's lighting can't be controlled.");
        }
        finally { _loading = false; }
    }

    // ================= mouse =================

    private Control BuildMouse()
    {
        var g = Ui.Grid();
        Span(g, _mouseInfo);
        Ui.Span(g, "DPI stages (switch with the button behind the scroll wheel)", heading: true);
        for (int i = 0; i < 4; i++)
        {
            _dpi[i] = new NumericUpDown { Minimum = 50, Maximum = 42000, Increment = 50, Width = 90, ThousandsSeparator = true };
            _dpiColor[i] = Ui.ColorButton();
            _dpiActive[i] = new Label { AutoSize = true, Margin = new Padding(10, 8, 0, 0), ForeColor = SystemColors.GrayText };
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            row.Controls.AddRange([_dpi[i], new Label { Text = "color", AutoSize = true, Margin = new Padding(14, 8, 4, 0) }, _dpiColor[i], _dpiActive[i]]);
            Ui.Row(g, $"Stage {i + 1}", row);
        }
        Ui.Span(g, "Performance", heading: true);
        Ui.Row(g, "Polling rate", _polling);
        Ui.Row(g, "Button response", _debounce);
        Ui.Row(g, "Angle snapping", _angle);
        Ui.Row(g, "Lift-off distance", _lod);
        Ui.Span(g, "Changing the lift-off distance also resets the mouse's surface calibration.");
        Ui.Span(g, "Power", heading: true);
        Ui.Row(g, "Sleep after", _sleep);
        Ui.Row(g, "Low-battery warning at", _lowBatt);
        Ui.Span(g, "Lighting", heading: true);
        var lowRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        lowRow.Controls.AddRange([_mouseLowOnly, new Label { Text = "below", AutoSize = true, Margin = new Padding(8, 8, 4, 0) }, _mouseLowThreshold,
            new Label { Text = "% in", AutoSize = true, Margin = new Padding(4, 8, 4, 0) }, _mouseLowColor]);
        Span(g, lowRow);
        Ui.Row(g, "Effect", _mouseLight);
        Ui.Row(g, "Color", _mouseLightColor);
        Ui.Row(g, "Brightness", _mouseLightBrightness);
        Ui.Span(g, "“Only when the battery is low” keeps the wheel dark and makes it breathe in the chosen color once the battery drops below the limit (GearTray has to be running).");
        _mouseLight.SelectedIndexChanged += (_, _) => UpdateMouseLightControls();
        _mouseLowOnly.CheckedChanged += (_, _) => UpdateMouseLightControls();
        _mouseApply = Ui.Button("Apply to mouse", async (_, _) => await ApplyMouse());
        _mouseReload = Ui.Button("Reload", async (_, _) => await LoadMouse());
        Buttons(g, _mouseApply, _mouseReload);
        return _mousePage = g;
    }

    private void UpdateMouseLightControls()
    {
        bool usable = _mouseLowOnly.Enabled;
        bool lowOnly = _mouseLowOnly.Checked;
        var mode = Ui.Value<MouseLightMode>(_mouseLight);
        _mouseLowThreshold.Enabled = usable && lowOnly;
        _mouseLowColor.Enabled = usable && lowOnly;
        _mouseLight.Enabled = usable && !lowOnly;
        _mouseLightColor.Enabled = usable && !lowOnly && MouseLighting.UsesColor.Contains(mode);
        _mouseLightBrightness.Enabled = usable && !lowOnly && mode != MouseLightMode.Off;
    }

    private async Task LoadMouse()
    {
        var m = Devices.Get<RogOmniMouse>();
        _mouseHave = null;
        if (m is not { Connected: true }) { MouseUnavailable("Mouse receiver not connected."); return; }
        try
        {
            var (s, light, batt) = await Devices.RunAsync(() => (m.ReadSettings(), m.HasLighting ? m.ReadCurrentLighting() : null, m.ReadBattery()));
            _mouseHave = s;
            _mouseLightHave = light;
            _mouseInfo.Text = m.Name + (batt is null ? "" : $" · battery {batt.Percent}%{(batt.Charging ? " (charging)" : "")}")
                + (m.CanWriteSettings ? "" : "\nThis mouse model's settings are shown read-only.");
            for (int i = 0; i < 4; i++)
            {
                _dpi[i].Minimum = m.Model?.MinDpi ?? 50;
                _dpi[i].Maximum = m.Model?.MaxDpi ?? 42000;
                _dpi[i].Value = Math.Clamp(s.Dpi[i], (int)_dpi[i].Minimum, (int)_dpi[i].Maximum);
                _dpiColor[i].BackColor = s.DpiColors[i];
                _dpiActive[i].Text = s.ActiveStage == i + 1 ? "← in use now" : "";
            }
            Ui.Select(_polling, s.PollingHz, $"{s.PollingHz} Hz");
            Ui.Select(_debounce, s.DebounceMs, $"{s.DebounceMs} ms");
            _angle.Checked = s.AngleSnapping;
            Ui.Select(_lod, s.LiftOffHigh);
            Ui.Select(_sleep, s.SleepMinutes, $"{s.SleepMinutes} min");
            Ui.Select(_lowBatt, s.LowBatteryWarning, $"{s.LowBatteryWarning}%");
            var low = _tray.Settings.MouseBatteryLight;
            _mouseLowOnly.Checked = low.Enabled;
            _mouseLowThreshold.Value = Math.Clamp(low.Threshold, 5, 50);
            _mouseLowColor.BackColor = ColorUtil.Parse(low.Color);
            if (light is not null)
            {
                Ui.Select(_mouseLight, light.Mode);
                _mouseLightColor.BackColor = light.Color;
                _mouseLightBrightness.Value = light.Brightness;
            }
            EnableInputs(_mousePage, m.CanWriteSettings, _mouseReload);
            _polling.Enabled = m.CanWriteSettings && !m.Booster;
            if (!m.HasLighting) _mouseLowOnly.Enabled = false;
            UpdateMouseLightControls();
        }
        catch (Exception ex)
        {
            MouseUnavailable(ex is MouseAsleepException ? ex.Message : "Couldn't read the mouse: " + ex.Message);
        }
    }

    private void MouseUnavailable(string message)
    {
        _mouseInfo.Text = message;
        EnableInputs(_mousePage, false, _mouseReload);
    }

    private async Task ApplyMouse()
    {
        if (Devices.Get<RogOmniMouse>() is not { Connected: true } m || _mouseHave is null) return;
        var have = _mouseHave;
        var want = have.Clone();
        for (int i = 0; i < 4; i++)
        {
            want.Dpi[i] = (int)_dpi[i].Value;
            want.DpiColors[i] = ColorUtil.Plain(_dpiColor[i].BackColor);
        }
        want.PollingHz = Ui.Value<int>(_polling);
        want.DebounceMs = Ui.Value<int>(_debounce);
        want.AngleSnapping = _angle.Checked;
        want.LiftOffHigh = Ui.Value<bool>(_lod);
        want.SleepMinutes = Ui.Value<int>(_sleep);
        want.LowBatteryWarning = Ui.Value<int>(_lowBatt);

        bool lowOnly = m.HasLighting && _mouseLowOnly.Checked;
        // In low-battery-only mode the wheel's stored look is "off"; GearTray lights it when needed.
        var light = !m.HasLighting ? null
            : lowOnly ? new MouseLighting(MouseLightMode.Off, Color.Black, 0)
            : new MouseLighting(Ui.Value<MouseLightMode>(_mouseLight), ColorUtil.Plain(_mouseLightColor.BackColor), _mouseLightBrightness.Value);
        var haveLight = _mouseLightHave;
        bool lightChanged = light is not null && (haveLight is null || light.Mode != haveLight.Mode
            || (light.Mode != MouseLightMode.Off && (light.Brightness != haveLight.Brightness || light.Color.ToArgb() != haveLight.Color.ToArgb())));

        if (await Run("Saving to the mouse…", () =>
        {
            m.ApplySettings(want, have);
            if (lightChanged) m.ApplyLighting(light!, persist: true);
        }))
        {
            var s = _tray.Settings;
            s.MouseBatteryLight = new MouseBatteryLightSettings
            {
                Enabled = lowOnly, Threshold = (int)_mouseLowThreshold.Value, Color = ColorUtil.ToHex(_mouseLowColor.BackColor),
            };
            if (lightChanged && !lowOnly && light!.Mode != MouseLightMode.Off) s.LightsOff = false;
            _tray.SaveSettings();
            _tray.RefreshSoon(); // lights the wheel right away if the battery is already low
            await LoadMouse();
            Status("Saved to the mouse.");
        }
    }

    // ================= keyboard =================

    private Control BuildKeyboard()
    {
        var g = Ui.Grid();
        Span(g, _kbInfo);
        _kbAdmin = Ui.Button("Restart GearTray with admin rights", (_, _) => _tray.RestartElevated());
        Span(g, _kbAdmin);

        // Lighting (needs the Razer + admin rights)
        var light = Ui.Grid();
        light.Padding = new Padding(0);
        light.Margin = new Padding(0);
        Ui.Span(light, "Lighting (keys and underglow)", heading: true);
        Ui.Row(light, "Effect", _kbEffect);
        Ui.Row(light, "Color", _kbColor);
        Ui.Row(light, "Brightness", _kbBrightness);
        Ui.Row(light, "Wave direction", _kbWaveDir);
        Ui.Row(light, "Speed", _kbSpeed);
        Ui.Row(light, "Screen to follow", _kbMonitor);
        Ui.Row(light, "Ambient style", _kbAmbientStyle);
        Ui.Row(light, "Color boost", _kbSaturation);
        var note = _kbEffectNote;
        note.Margin = new Padding(3, 4, 3, 4);
        light.Controls.Add(note);
        light.SetColumnSpan(note, 2);
        Ui.Span(light, "Performance", heading: true);
        _kbPollingRowLabel = new Label { Text = "Polling rate", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 16, 3) };
        light.Controls.Add(_kbPollingRowLabel);
        _kbPolling.Anchor = AnchorStyles.Left;
        light.Controls.Add(_kbPolling);
        _kbLightingPanel = light;
        Span(g, light);

        Ui.Span(g, "Turn the lights off", heading: true);
        Ui.Row(g, "Automatically", _kbOffDisplay);
        Ui.Row(g, "After no input for", _kbIdle);

        Ui.Span(g, "Game mode", heading: true);
        Ui.Row(g, "Game mode", _gmManual);
        Ui.Row(g, "", _gmAuto);
        var blocks = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        blocks.Controls.AddRange([_gmWin, _gmAltTab, _gmAltF4]);
        Ui.Row(g, "Blocks", blocks);
        Ui.Row(g, "More games", _gmExtra);
        Ui.Row(g, "Never a game", _gmIgnore);
        Ui.Span(g, "Games installed through Steam, Epic, Xbox or GOG are recognised automatically. Add other programs by name, separated by commas (e.g. eldenring.exe).");
        Span(g, _gmStatus);

        Ui.Span(g, "Key remapping", heading: true);
        var add = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        add.Controls.AddRange([_remapFrom, new Label { Text = "→", AutoSize = true, Margin = new Padding(6, 8, 6, 0) }, _remapTo,
            Ui.Button("Add", (_, _) => AddRemap())]);
        Ui.Row(g, "Key", add);
        Ui.Row(g, "", _remapDisable);
        Ui.Row(g, "Remaps", _remapList);
        Ui.Row(g, "", Ui.Button("Remove selected", (_, _) => { if (_remapList.SelectedItem is not null) _remapList.Items.Remove(_remapList.SelectedItem); }));
        Ui.Span(g, "Remaps work for any keyboard while GearTray runs. A few games with strict anti-cheat ignore remapped keys.");
        _remapDisable.CheckedChanged += (_, _) => _remapTo.Enabled = !_remapDisable.Checked;

        _kbEffect.SelectedIndexChanged += (_, _) => UpdateKbControls();
        Buttons(g, Ui.Button("Apply", async (_, _) => await ApplyKeyboard()), Ui.Button("Reload", async (_, _) => await LoadKeyboard()));
        return g;
    }

    private void AddRemap()
    {
        if (_remapFrom.Vk == 0 || (!_remapDisable.Checked && _remapTo.Vk == 0))
        {
            Status("Click a box and press the key, then press Add.");
            return;
        }
        var remap = new KeyRemap(_remapFrom.Vk, _remapDisable.Checked ? 0 : _remapTo.Vk);
        foreach (var existing in _remapList.Items.Cast<KeyRemap>().Where(r => r.From == remap.From).ToList()) _remapList.Items.Remove(existing);
        _remapList.Items.Add(remap);
        _remapFrom.Clear();
        _remapTo.Clear();
        Status("Added. Press Apply to use it.");
    }

    private void UpdateKbControls()
    {
        var e = Ui.Value<KeyboardEffect>(_kbEffect);
        bool on = _kbEffect.Enabled;
        _kbColor.Enabled = on && KeyboardLighting.UsesColor.Contains(e);
        _kbBrightness.Enabled = on && e != KeyboardEffect.Off;
        _kbWaveDir.Enabled = on && e == KeyboardEffect.Wave;
        _kbSpeed.Enabled = on && e is KeyboardEffect.Reactive or KeyboardEffect.Starlight or KeyboardEffect.StarlightRandom;
        _kbMonitor.Enabled = _kbAmbientStyle.Enabled = _kbSaturation.Enabled = on && e == KeyboardEffect.Ambient;
        _kbEffectNote.Text = e switch
        {
            KeyboardEffect.Ambient => "Ambient mirrors the colors of the chosen screen, like Razer's Ambient Awareness. It runs while GearTray runs and uses the graphics card's desktop duplication, so it costs almost no CPU.",
            KeyboardEffect.AudioMeter => "Top three rows: left channel, bottom three: right channel, underglow pulses in the chosen color. Follows your default playback device.",
            _ => "This effect runs inside the keyboard and is saved there.",
        };
        if (e is KeyboardEffect.Ambient or KeyboardEffect.AudioMeter && _tray.Settings.Keyboard.Effect == e)
            _kbEffectNote.Text += $"\nNow: {_tray.EffectStatus}.";
    }

    private void UpdateGameModeStatus()
    {
        _gmStatus.Text = _tray.GameModeActive
            ? "Game mode is on" + (_tray.CurrentGame is string g && _tray.Settings.KeyboardFeatures.GameModeAuto ? $" ({g} is in front)." : ".")
            : "Game mode is off.";
    }

    private async Task LoadKeyboard()
    {
        var k = Devices.Get<RazerHuntsmanElite>();
        var cfg = _tray.Settings.Keyboard;
        var f = _tray.Settings.KeyboardFeatures;

        // Software features: always editable.
        _kbOffDisplay.Checked = f.LightsOffWhenDisplayOff;
        Ui.Select(_kbIdle, f.LightsOffIdleMinutes, $"{f.LightsOffIdleMinutes} min");
        _gmManual.Checked = f.GameModeManual;
        _gmAuto.Checked = f.GameModeAuto;
        _gmWin.Checked = f.BlockWindowsKey;
        _gmAltTab.Checked = f.BlockAltTab;
        _gmAltF4.Checked = f.BlockAltF4;
        _gmExtra.Text = string.Join(", ", f.ExtraGames);
        _gmIgnore.Text = string.Join(", ", f.IgnoredApps);
        _remapList.Items.Clear();
        foreach (var r in f.Remaps) _remapList.Items.Add(r);
        UpdateGameModeStatus();

        // Lighting values from the saved settings (the keyboard can't report its effect).
        Ui.Select(_kbEffect, cfg.Configured ? cfg.Effect : KeyboardEffect.Spectrum);
        _kbColor.BackColor = ColorUtil.Parse(cfg.Color);
        _kbBrightness.Value = cfg.Brightness;
        Ui.Select(_kbWaveDir, cfg.WaveRightToLeft);
        Ui.Select(_kbSpeed, cfg.Speed);
        Ui.Select(_kbMonitor, cfg.AmbientMonitor ?? Screen.PrimaryScreen?.DeviceName);
        if (_kbMonitor.SelectedIndex < 0 && _kbMonitor.Items.Count > 0) _kbMonitor.SelectedIndex = 0;
        Ui.Select(_kbAmbientStyle, cfg.AmbientFollowPicture);
        _kbSaturation.Value = cfg.AmbientSaturation;

        _kbAdmin.Visible = k is { Connected: false, Problem: not null } && !Elevation.IsAdmin;
        if (k is not { Connected: true })
        {
            _kbInfo.Text = (k?.Problem is { } p ? $"Razer Huntsman Elite: {p}." : "Razer keyboard not connected.")
                + " Lighting needs the keyboard; game mode and remaps work anyway.";
            EnableInputs(_kbLightingPanel, false);
            return;
        }
        try
        {
            var (brightness, polling) = await Devices.RunAsync(() => (k.GetBrightness(), k.GetPollingRate()));
            _kbPollingHave = polling;
            if (!cfg.Configured) _kbBrightness.Value = brightness;
            _kbInfo.Text = $"Razer Huntsman Elite · {k.Detail}"
                + (cfg.Configured ? "" : "\nThe keyboard can't report which effect it's showing. Pick one and press Apply.");
            EnableInputs(_kbLightingPanel, true);
            if (polling is int hz) Ui.Select(_kbPolling, hz, $"{hz} Hz");
            _kbPolling.Visible = _kbPollingRowLabel.Visible = polling is not null;
            UpdateKbControls();
        }
        catch (Exception ex)
        {
            _kbInfo.Text = "Couldn't read the keyboard: " + ex.Message;
            EnableInputs(_kbLightingPanel, false);
        }
    }

    private async Task ApplyKeyboard()
    {
        var s = _tray.Settings;
        var kb = Devices.Get<RazerHuntsmanElite>();
        if (kb is { Connected: true } && _kbEffect.Enabled)
        {
            var look = new KeyboardLighting(Ui.Value<KeyboardEffect>(_kbEffect), ColorUtil.Plain(_kbColor.BackColor),
                _kbBrightness.Value, Ui.Value<bool>(_kbWaveDir), Ui.Value<int>(_kbSpeed));
            s.Keyboard = s.Keyboard.With(look);
            s.Keyboard.AmbientMonitor = Ui.Value<string?>(_kbMonitor);
            s.Keyboard.AmbientFollowPicture = Ui.Value<bool>(_kbAmbientStyle);
            s.Keyboard.AmbientSaturation = _kbSaturation.Value;
            if (look.Effect != KeyboardEffect.Off) s.LightsOff = false;
        }
        var f = s.KeyboardFeatures;
        f.LightsOffWhenDisplayOff = _kbOffDisplay.Checked;
        f.LightsOffIdleMinutes = Ui.Value<int>(_kbIdle);
        f.GameModeManual = _gmManual.Checked;
        f.GameModeAuto = _gmAuto.Checked;
        f.BlockWindowsKey = _gmWin.Checked;
        f.BlockAltTab = _gmAltTab.Checked;
        f.BlockAltF4 = _gmAltF4.Checked;
        f.ExtraGames = SplitNames(_gmExtra.Text);
        f.IgnoredApps = SplitNames(_gmIgnore.Text);
        f.Remaps = _remapList.Items.Cast<KeyRemap>().ToList();
        _tray.SaveSettings();
        _tray.ApplyKeyboardLook(persist: true);
        _tray.ApplyInputFeatures();

        if (kb is { Connected: true } && _kbPolling.Visible && _kbPolling.Enabled && Ui.Value<int>(_kbPolling) is int hz && hz != _kbPollingHave)
        {
            if (await Run("Setting the keyboard's polling rate…", () => kb.SetPollingRate(hz))) _kbPollingHave = hz;
        }
        Status("Saved.");
        await Task.Delay(1200);
        UpdateKbControls();       // shows how the software effect started
        UpdateGameModeStatus();
    }

    private static List<string> SplitNames(string text) =>
        text.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    // ================= headset =================

    private Control BuildHeadset()
    {
        var g = Ui.Grid();
        Span(g, _hsInfo);
        Ui.Span(g, "Noise control", heading: true);
        var nc = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        nc.Controls.AddRange([_ncAnc, _ncTrans, _ncOff]);
        Ui.Row(g, "Mode", nc);
        Ui.Row(g, "Transparency level", _transparency);
        Ui.Span(g, "Microphone", heading: true);
        Ui.Row(g, "Mic volume", _micVol);
        Ui.Row(g, "Sidetone (hear yourself)", _sidetone);
        Ui.Row(g, "Muted-mic light", _micLed);
        Ui.Span(g, "Equalizer", heading: true);
        Ui.Row(g, "Preset", _eq);
        var bands = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(8, 0, 0, 0) };
        for (int i = 0; i < 10; i++)
        {
            var bar = new TrackBar
            {
                Orientation = Orientation.Vertical, Minimum = -20, Maximum = 20, TickStyle = TickStyle.None,
                AutoSize = false, Width = 36, Height = 120, SmallChange = 1, LargeChange = 4,
                BackColor = SystemColors.Window,
            };
            var value = new Label { AutoSize = false, Width = 44, Height = 18, TextAlign = ContentAlignment.MiddleCenter, Text = "0" };
            bar.ValueChanged += (_, _) => value.Text = (bar.Value / 2.0).ToString("+0.#;-0.#;0");
            var col = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Margin = new Padding(0) };
            col.Controls.Add(bar);
            col.Controls.Add(value);
            col.Controls.Add(new Label { Text = (i + 1).ToString(), AutoSize = false, Width = 44, Height = 18, TextAlign = ContentAlignment.MiddleCenter, ForeColor = SystemColors.GrayText });
            _eqBands[i] = bar;
            bands.Controls.Add(col);
        }
        Span(g, bands);
        Ui.Span(g, "Custom EQ: 10 bands from bass (1) to treble (10), ±10 dB. Choose “Custom” to use them.");
        _eq.SelectedIndexChanged += (_, _) => UpdateEqControls();
        Ui.Span(g, "Power and base station", heading: true);
        Ui.Row(g, "Turn headset off after", _autoOff);
        Ui.Row(g, "Screen brightness", _oled);
        Span(g, _chatMix);
        var apply = Ui.Button("Apply to headset", async (_, _) => await ApplyHeadset());
        _hsReload = Ui.Button("Reload", async (_, _) => await LoadHeadset());
        Buttons(g, apply, _hsReload);
        return _hsPage = g;
    }

    private void UpdateEqControls()
    {
        bool custom = _eq.Enabled && Ui.Value<int>(_eq) == 4;
        foreach (var b in _eqBands) b.Enabled = custom;
    }

    private async Task LoadHeadset()
    {
        var h = Devices.Get<ArctisNovaProWireless>();
        _hsHave = null;
        if (h is not { Connected: true })
        {
            _hsInfo.Text = "Base station not connected.";
            EnableInputs(_hsPage, false, _hsReload);
            return;
        }
        try
        {
            var s = await Devices.RunAsync(h.ReadState);
            _hsHave = s;
            _hsInfo.Text = (s.HeadsetOnline ? $"Headset on · battery {s.BatteryPercent}%{(s.Charging ? " (charging)" : "")}" : "Headset is off")
                + (s.SpareBatteryPercent is int sp ? $" · spare battery in the base station {sp}%" : "");
            _ncAnc.Checked = s.Noise == NoiseMode.NoiseCancelling;
            _ncTrans.Checked = s.Noise == NoiseMode.Transparency;
            _ncOff.Checked = s.Noise == NoiseMode.Off;
            _transparency.Value = s.TransparencyLevel;
            Ui.Select(_sidetone, s.Sidetone, $"Level {s.Sidetone}");
            _micVol.Value = s.MicVolume;
            _micLed.Value = s.MicLedBrightness;
            Ui.Select(_eq, s.EqPreset, $"Preset {s.EqPreset} (set in SteelSeries GG)");
            for (int i = 0; i < 10; i++) _eqBands[i].Value = Math.Clamp((int)Math.Round(s.EqBandsDb[i] * 2), -20, 20);
            Ui.Select(_autoOff, s.AutoOffIndex, $"Setting {s.AutoOffIndex}");
            _oled.Value = s.OledBrightness;
            _chatMix.Text = $"ChatMix dial: game {s.ChatMixGame}% · chat {s.ChatMixChat}%";
            EnableInputs(_hsPage, true);
            UpdateEqControls();
        }
        catch (Exception ex)
        {
            _hsInfo.Text = "Couldn't read the base station: " + ex.Message;
            EnableInputs(_hsPage, false, _hsReload);
        }
    }

    private async Task ApplyHeadset()
    {
        if (Devices.Get<ArctisNovaProWireless>() is not { Connected: true } h || _hsHave is null) return;
        var have = _hsHave;
        var want = have.Clone();
        want.Noise = _ncAnc.Checked ? NoiseMode.NoiseCancelling : _ncTrans.Checked ? NoiseMode.Transparency : NoiseMode.Off;
        want.TransparencyLevel = _transparency.Value;
        want.Sidetone = Ui.Value<int>(_sidetone);
        want.MicVolume = _micVol.Value;
        want.MicLedBrightness = _micLed.Value;
        want.EqPreset = Ui.Value<int>(_eq);
        want.EqBandsDb = _eqBands.Select(b => b.Value / 2.0).ToArray();
        want.AutoOffIndex = Ui.Value<int>(_autoOff);
        want.OledBrightness = _oled.Value;
        if (await Run("Saving to the base station…", () => h.ApplyState(want, have)))
        {
            await LoadHeadset();
            _tray.RefreshSoon();
            Status("Saved to the base station.");
        }
    }

    // ================= general =================

    private Control BuildGeneral()
    {
        var g = Ui.Grid();
        Ui.Span(g, "Startup", heading: true);
        Span(g, _startup);
        _startup.Checked = Elevation.StartupTaskExists();
        _startup.Click += (_, _) =>
        {
            bool want = _startup.Checked;
            bool ok = _tray.SetStartup(want);
            _startup.Checked = Elevation.StartupTaskExists();
            Status(!ok ? "The startup task wasn't changed." :
                want ? "GearTray will start with Windows, with admin rights." : "GearTray won't start with Windows anymore.");
        };
        Ui.Span(g, Elevation.IsAdmin ? "GearTray is running with admin rights." : "GearTray is running without admin rights, so the keyboard's lighting can't be controlled.");

        Ui.Span(g, "Tray icon and battery warnings", heading: true);
        Ui.Select(_trayBattery, _tray.Settings.TrayBattery);
        _trayBattery.SelectedIndexChanged += (_, _) => { _tray.Settings.TrayBattery = Ui.Value<TrayBatterySource>(_trayBattery); _tray.SaveSettings(); };
        Ui.Row(g, "Battery shown in the icon", _trayBattery);
        _lowPct.Value = Math.Clamp(_tray.Settings.LowBatteryPercent, 5, 50);
        _lowPct.ValueChanged += (_, _) => { _tray.Settings.LowBatteryPercent = (int)_lowPct.Value; _tray.SaveSettings(); };
        Ui.Row(g, "Low battery below (%)", _lowPct);
        _lowNotify.Checked = _tray.Settings.LowBatteryNotifications;
        _lowNotify.CheckedChanged += (_, _) => { _tray.Settings.LowBatteryNotifications = _lowNotify.Checked; _tray.SaveSettings(); };
        Ui.Row(g, "When a battery is low", _lowNotify);

        Ui.Span(g, "Files", heading: true);
        Buttons(g,
            Ui.Button("Open log", (_, _) => OpenFile(Log.FilePath)),
            Ui.Button("Open settings folder", (_, _) => OpenFile(Log.Directory)));

        Ui.Span(g, "About", heading: true);
        Ui.Span(g, $"GearTray {Application.ProductVersion}. Device protocols follow these open-source projects: G-Helper and libratbag (ROG mouse), " +
                   "OpenRazer and OpenRGB (Razer keyboard), HeadsetControl, Linux-Arctis-Manager and arctis-omni-linux (Arctis headset). " +
                   "GearTray only sends commands those projects use. It never talks to the motherboard.");
        return g;
    }

    private void OpenFile(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Status(ex.Message); }
    }

    // ================= helpers =================

    private async Task<bool> Run(string message, Action action)
    {
        Status(message);
        UseWaitCursor = true;
        try
        {
            await Devices.RunAsync(action);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(message, ex);
            Status("Didn't work: " + ex.Message);
            return false;
        }
        finally { UseWaitCursor = false; }
    }

    private void Status(string text) => _status.Text = text;

    private static TabPage Page(string title, Control content)
    {
        var page = new TabPage(title) { UseVisualStyleBackColor = true };
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        scroll.Controls.Add(content);
        page.Controls.Add(scroll);
        return page;
    }

    private static void Span(TableLayoutPanel g, Control c)
    {
        c.Margin = new Padding(3, 6, 3, 6);
        g.Controls.Add(c);
        g.SetColumnSpan(c, 2);
    }

    private static void Buttons(TableLayoutPanel g, params Button[] buttons)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 14, 0, 6) };
        row.Controls.AddRange(buttons);
        g.Controls.Add(row);
        g.SetColumnSpan(row, 2);
    }

    /// <summary>Enable/disable every input under root, except the given controls (e.g. Reload).</summary>
    private static void EnableInputs(Control root, bool enabled, params Control[] except)
    {
        foreach (Control c in root.Controls)
        {
            if (except.Contains(c)) { c.Enabled = true; continue; }
            if (c is ComboBox or NumericUpDown or CheckBox or RadioButton or TrackBar or Button) c.Enabled = enabled;
            else EnableInputs(c, enabled, except);
        }
    }
}
