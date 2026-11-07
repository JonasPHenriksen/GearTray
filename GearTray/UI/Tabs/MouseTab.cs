using GearTray.Devices;
using GearTray.Util;

namespace GearTray.UI.Tabs;

/// <summary>ROG mouse: onboard profiles, DPI stages, performance, buttons, power, lighting (incl. "only when the battery is low").</summary>
internal sealed class MouseTab : SettingsTab
{
    private readonly ComboBox _profile = Ui.Combo(Enumerable.Range(1, 5).Select(i => new Item<int>($"Profile {i}", i)).ToArray());
    private readonly ComboBox _stage = Ui.Combo(Enumerable.Range(1, 4).Select(i => new Item<int>($"Stage {i}", i)).ToArray());
    private readonly NumericUpDown[] _dpi = new NumericUpDown[4];
    private readonly Button[] _dpiColor = new Button[4];
    private readonly Label[] _dpiActive = new Label[4];
    private readonly ComboBox _polling = Ui.Combo(new Item<int>("125 Hz", 125), new("250 Hz", 250), new("500 Hz", 500), new("1000 Hz", 1000));
    private readonly ComboBox _debounce = Ui.Combo(RogOmniMouse.DebounceTable.Skip(2).Select(ms => new Item<int>($"{ms} ms", ms)).ToArray());
    private readonly CheckBox _angle = new() { Text = "On", AutoSize = true };
    private readonly LabeledSlider _angleTuning = new(-30, 30, v => $"{v:+0;-0;0}°");
    private readonly LabeledSlider _accel = new(0, 9, v => v == 0 ? "off" : v.ToString());
    private readonly LabeledSlider _decel = new(0, 9, v => v == 0 ? "off" : v.ToString());
    private readonly ComboBox _lod = Ui.Combo(new Item<bool>("Low", false), new("High", true));
    private readonly ComboBox[] _buttons = new ComboBox[RogOmniMouse.ButtonSlots.Length];
    private readonly ComboBox _sleep = Ui.Combo(new Item<int>("1 min", 1), new("2 min", 2), new("3 min", 3), new("5 min", 5), new("10 min", 10), new("Never", 0));
    private readonly ComboBox _lowBatt = Ui.Combo(new Item<int>("Off", 0), new("10%", 10), new("20%", 20), new("30%", 30), new("40%", 40), new("50%", 50));
    private readonly ComboBox _light = Ui.Combo(
        new Item<MouseLightMode>("Off", MouseLightMode.Off), new("Static color", MouseLightMode.Static),
        new("Breathing", MouseLightMode.Breathing), new("Color cycle", MouseLightMode.ColorCycle),
        new("Reactive (lights up on click)", MouseLightMode.Reactive), new("Battery level (always on)", MouseLightMode.Battery));
    private readonly Button _lightColor = Ui.ColorButton();
    private readonly LabeledSlider _lightBrightness = new(1, 100, v => $"{v}%");
    private readonly CheckBox _lowOnly = new() { Text = "Only light up when the battery is low", AutoSize = true };
    private readonly NumericUpDown _lowThreshold = new() { Minimum = 5, Maximum = 50, Width = 60 };
    private readonly Button _lowColor = Ui.ColorButton();
    private readonly Button _apply, _reload;
    private MouseSettings? _have;
    private MouseLighting? _lightHave;
    private MouseAction[]? _buttonsHave;
    private int _profileHave;

    public MouseTab(SettingsForm form, TrayContext tray) : base(form, tray, "mouse", "Mouse")
    {
        var profiles = AddSection("profiles", "Onboard profiles");
        profiles.Row("Profile in use", Section.Line(_profile, Ui.Button("Switch", async (_, _) => await SwitchProfile())));
        profiles.Note("The mouse keeps 5 profiles, each with its own DPI stages, buttons, lighting and performance settings. " +
                      "Everything below belongs to the profile in use; switching reloads this tab. The Programs tab can switch profiles per game.");

        var dpi = AddSection("dpi", "DPI stages");
        for (int i = 0; i < 4; i++)
        {
            _dpi[i] = new NumericUpDown { Minimum = 50, Maximum = 42000, Increment = 50, Width = 90, ThousandsSeparator = true };
            _dpiColor[i] = Ui.ColorButton();
            _dpiActive[i] = new Label { AutoSize = true, Margin = new Padding(10, 8, 0, 0), ForeColor = SystemColors.GrayText };
            dpi.Row($"Stage {i + 1}", Section.Line(_dpi[i], Section.Caption("color"), _dpiColor[i], _dpiActive[i]));
        }
        dpi.Row("Stage in use", _stage);
        dpi.Note("Switch stages with the button behind the scroll wheel; the wheel briefly shows the stage's color.");

        var perf = AddSection("performance", "Performance");
        perf.Row("Polling rate", _polling);
        perf.Row("Button response", _debounce);
        perf.Row("Angle snapping", _angle);
        perf.Row("Angle tuning", _angleTuning);
        perf.Row("Acceleration", _accel);
        perf.Row("Deceleration", _decel);
        perf.Row("Lift-off distance", _lod);
        perf.Note("Angle tuning rotates the sensor to match how you hold the mouse. Acceleration/deceleration 0 = off (raw input). " +
                  "Changing the lift-off distance also resets the mouse's surface calibration.");

        var buttons = AddSection("buttons", "Buttons");
        for (int i = 0; i < _buttons.Length; i++)
        {
            _buttons[i] = Ui.Combo(MouseAction.All.Select(a => new Item<MouseAction>(a.Name, a)).ToArray());
            _buttons[i].Width = 280;
            _buttons[i].DropDownHeight = 400;
            buttons.Row(RogOmniMouse.ButtonSlots[i].Name, _buttons[i]);
        }
        buttons.Span(Ui.Button("Restore default buttons", async (_, _) => await RestoreButtons()));
        buttons.Note("The left button always stays left click, so you can't lock yourself out. Bind a button to F13–F24 and use that key as a macro trigger on the Keyboard tab for anything more complex.");

        var power = AddSection("power", "Power");
        power.Row("Sleep after", _sleep);
        power.Row("Low-battery warning at", _lowBatt);

        var light = AddSection("lighting", "Lighting");
        light.Span(Section.Line(_lowOnly, Section.Caption("below"), _lowThreshold, Section.Caption("% in"), _lowColor));
        light.Row("Effect", _light);
        light.Row("Color", _lightColor);
        light.Row("Brightness", _lightBrightness);
        light.Note("“Only when the battery is low” keeps the wheel dark and makes it breathe in the chosen color once the battery drops below the limit (GearTray has to be running).");
        _light.SelectedIndexChanged += (_, _) => UpdateLightControls();
        _lowOnly.CheckedChanged += (_, _) => UpdateLightControls();

        _apply = AddButton("Apply to mouse", Apply);
        _reload = AddButton("Reload", LoadAsync);
    }

    private RogOmniMouse? Mouse => Tray.Devices.Get<RogOmniMouse>() is { Connected: true } m ? m : null;

    private void UpdateLightControls()
    {
        bool usable = _lowOnly.Enabled;
        bool lowOnly = _lowOnly.Checked;
        var mode = Ui.Value<MouseLightMode>(_light);
        _lowThreshold.Enabled = _lowColor.Enabled = usable && lowOnly;
        _light.Enabled = usable && !lowOnly;
        _lightColor.Enabled = usable && !lowOnly && MouseLighting.UsesColor.Contains(mode);
        _lightBrightness.Enabled = usable && !lowOnly && mode != MouseLightMode.Off;
    }

    public override async Task LoadAsync()
    {
        _have = null;
        if (Mouse is not { } m) { Unavailable("Mouse receiver not connected."); return; }
        try
        {
            var (s, light, batt, buttons, serial) = await Tray.Devices.RunAsync(() =>
            {
                MouseAction[]? b = null;
                string? sn = null;
                try { b = m.ReadButtons(); } catch (Exception ex) { Log.Warn("Mouse buttons: " + ex.Message); }
                try { sn = m.ReadSerial(); } catch (Exception ex) { Log.Warn("Mouse serial: " + ex.Message); }
                return (m.ReadSettings(), m.HasLighting ? m.ReadCurrentLighting() : null, m.ReadBattery(), b, sn);
            });
            _have = s;
            _lightHave = light;
            _buttonsHave = buttons;
            _profileHave = s.ProfileRaw + 1;
            Info.Text = m.Name + (batt is null ? "" : $" · battery {batt.Percent}%{(batt.Charging ? " (charging)" : "")}")
                + (string.IsNullOrEmpty(serial) ? "" : $" · serial {serial}")
                + (m.CanWriteSettings ? "" : "\nThis mouse model's settings are shown read-only.");
            Ui.Select(_profile, _profileHave, $"Profile {_profileHave}");
            for (int i = 0; i < 4; i++)
            {
                _dpi[i].Minimum = m.Model?.MinDpi ?? 50;
                _dpi[i].Maximum = m.Model?.MaxDpi ?? 42000;
                _dpi[i].Value = Math.Clamp(s.Dpi[i], (int)_dpi[i].Minimum, (int)_dpi[i].Maximum);
                _dpiColor[i].BackColor = s.DpiColors[i];
                _dpiActive[i].Text = s.ActiveStage == i + 1 ? "← in use now" : "";
            }
            Ui.Select(_stage, s.ActiveStage, $"Stage {s.ActiveStage}");
            Ui.Select(_polling, s.PollingHz, $"{s.PollingHz} Hz");
            Ui.Select(_debounce, s.DebounceMs, $"{s.DebounceMs} ms");
            _angle.Checked = s.AngleSnapping;
            _angleTuning.Value = s.AngleTuning;
            _accel.Value = s.Acceleration;
            _decel.Value = s.Deceleration;
            Ui.Select(_lod, s.LiftOffHigh);
            if (buttons is not null)
                for (int i = 0; i < _buttons.Length; i++)
                {
                    var current = buttons[i];
                    var match = MouseAction.All.FirstOrDefault(a => a.Code == current.Code && a.Type == current.Type) ?? current;
                    Ui.Select(_buttons[i], match, match.Name);
                }
            Ui.Select(_sleep, s.SleepMinutes, $"{s.SleepMinutes} min");
            Ui.Select(_lowBatt, s.LowBatteryWarning, $"{s.LowBatteryWarning}%");
            var low = Tray.Settings.MouseBatteryLight;
            _lowOnly.Checked = low.Enabled;
            _lowThreshold.Value = Math.Clamp(low.Threshold, 5, 50);
            _lowColor.BackColor = ColorUtil.Parse(low.Color);
            if (light is not null)
            {
                Ui.Select(_light, light.Mode, light.Mode.ToString());
                _lightColor.BackColor = light.Color;
                _lightBrightness.Value = light.Brightness;
            }
            EnableInputs(Body, m.CanWriteSettings);
            _apply.Enabled = m.CanWriteSettings;
            _polling.Enabled = m.CanWriteSettings && !m.Booster;
            _buttons[0].Enabled = false; // left button stays left click
            foreach (var b in _buttons) b.Enabled &= buttons is not null;
            if (!m.HasLighting) _lowOnly.Enabled = false;
            UpdateLightControls();
        }
        catch (Exception ex)
        {
            Unavailable(ex is MouseAsleepException ? ex.Message : "Couldn't read the mouse: " + ex.Message);
        }
    }

    private void Unavailable(string message)
    {
        Info.Text = message;
        EnableInputs(Body, false);
        _apply.Enabled = false;
        _reload.Enabled = true;
    }

    private async Task SwitchProfile()
    {
        if (Mouse is not { } m) return;
        int p = Ui.Value<int>(_profile);
        if (p == _profileHave) { Status($"Profile {p} is already in use."); return; }
        if (await Run($"Switching to profile {p}…", () => m.SwitchProfileAndSave(p)))
        {
            await LoadAsync();
            Status($"Profile {p} is in use now.");
        }
    }

    private async Task RestoreButtons()
    {
        if (Mouse is not { } m) return;
        if (await Run("Restoring the default buttons…", m.RestoreDefaultButtons))
        {
            await LoadAsync();
            Status("Buttons are back to their defaults.");
        }
    }

    private async Task Apply()
    {
        if (Mouse is not { } m || _have is null) return;
        var have = _have;
        var want = have.Clone();
        for (int i = 0; i < 4; i++)
        {
            want.Dpi[i] = (int)_dpi[i].Value;
            want.DpiColors[i] = ColorUtil.Plain(_dpiColor[i].BackColor);
        }
        want.ActiveStage = Ui.Value<int>(_stage);
        want.PollingHz = Ui.Value<int>(_polling);
        want.DebounceMs = Ui.Value<int>(_debounce);
        want.AngleSnapping = _angle.Checked;
        want.AngleTuning = _angleTuning.Value;
        want.Acceleration = _accel.Value;
        want.Deceleration = _decel.Value;
        want.LiftOffHigh = Ui.Value<bool>(_lod);
        want.SleepMinutes = Ui.Value<int>(_sleep);
        want.LowBatteryWarning = Ui.Value<int>(_lowBatt);

        var buttonChanges = new Dictionary<int, MouseAction>();
        if (_buttonsHave is not null)
            for (int i = 1; i < _buttons.Length; i++)
                if (_buttons[i].SelectedItem is Item<MouseAction> it && (it.Value.Code != _buttonsHave[i].Code || it.Value.Type != _buttonsHave[i].Type))
                    buttonChanges[i] = it.Value;

        bool lowOnly = m.HasLighting && _lowOnly.Checked;
        // In low-battery-only mode the wheel's stored look is "off"; GearTray lights it when needed.
        var light = !m.HasLighting ? null
            : lowOnly ? new MouseLighting(MouseLightMode.Off, Color.Black, 0)
            : new MouseLighting(Ui.Value<MouseLightMode>(_light), ColorUtil.Plain(_lightColor.BackColor), _lightBrightness.Value);
        var haveLight = _lightHave;
        bool lightChanged = light is not null && (haveLight is null || light.Mode != haveLight.Mode
            || (light.Mode != MouseLightMode.Off && (light.Brightness != haveLight.Brightness || light.Color.ToArgb() != haveLight.Color.ToArgb())));

        if (await Run("Saving to the mouse…", () =>
        {
            m.ApplySettings(want, have);
            if (buttonChanges.Count > 0) m.WriteButtons(buttonChanges);
            if (lightChanged) m.ApplyLighting(light!, persist: true);
        }))
        {
            var s = Tray.Settings;
            s.MouseBatteryLight = new MouseBatteryLightSettings
            {
                Enabled = lowOnly, Threshold = (int)_lowThreshold.Value, Color = ColorUtil.ToHex(_lowColor.BackColor),
            };
            if (lightChanged && !lowOnly && light!.Mode != MouseLightMode.Off) s.LightsOff = false;
            Tray.SaveSettings();
            Tray.RefreshSoon();
            await LoadAsync();
            Status("Saved to the mouse.");
        }
    }
}
