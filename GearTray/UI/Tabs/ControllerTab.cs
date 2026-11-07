using GearTray.Devices;
using GearTray.Util;

namespace GearTray.UI.Tabs;

/// <summary>
/// Xbox controller: connection, battery and firmware, live button/stick test, stick-drift check, rumble test (main motors
/// and impulse triggers), the Xbox button light, power off.
/// </summary>
internal sealed class ControllerTab : SettingsTab
{
    private readonly ComboBox _slot = Ui.Combo<int>();
    private readonly Label _type = new() { AutoSize = true, MaximumSize = new Size(420, 0) };
    private readonly Label _battery = new() { AutoSize = true };
    private readonly Label _firmware = new() { AutoSize = true, MaximumSize = new Size(420, 0), Text = "shown when you open this tab" };
    private readonly ControllerView _view = new();
    private readonly Label _drift = new() { AutoSize = true, MaximumSize = new Size(560, 0) };
    private readonly LabeledSlider _strength = new(10, 100, v => $"{v}%");
    private readonly ComboBox _lightMode = Ui.Combo(
        new Item<XboxLedMode>("Steady", XboxLedMode.On),
        new Item<XboxLedMode>("Off", XboxLedMode.Off),
        new Item<XboxLedMode>("Slow fade", XboxLedMode.FadeSlow),
        new Item<XboxLedMode>("Fast fade", XboxLedMode.FadeFast),
        new Item<XboxLedMode>("Slow blink", XboxLedMode.BlinkSlow),
        new Item<XboxLedMode>("Fast blink", XboxLedMode.BlinkFast));
    private readonly LabeledSlider _lightLevel = new(1, XboxGip.MaxBrightness,
        v => v == XboxGip.DefaultBrightness ? $"{v} of {XboxGip.MaxBrightness} (Windows' level)" : $"{v} of {XboxGip.MaxBrightness}");
    private readonly CheckBox _lightKeep = new() { Text = "Keep it: set it again whenever the controller connects", AutoSize = true };
    private readonly CheckBox _vibLeft = new() { Text = "Left (heavy) motor", AutoSize = true, Checked = true };
    private readonly CheckBox _vibRight = new() { Text = "Right (light) motor", AutoSize = true, Checked = true };
    private readonly CheckBox _vibLeftTrigger = new() { Text = "Left trigger", AutoSize = true };
    private readonly CheckBox _vibRightTrigger = new() { Text = "Right trigger", AutoSize = true };
    private readonly Button _vibStart, _vibStop;
    /// <summary>Re-sends the vibration every 200 ms while the test runs.</summary>
    private readonly System.Windows.Forms.Timer _vibTimer = new() { Interval = 200 };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
    private readonly Button _powerOff;
    private readonly List<(float L, float R)> _driftSamples = [];
    private DateTime _driftUntil, _vibUntil;
    private PadBattery? _lastBattery;
    private int _vibSlot = -1;
    private bool _firmwareRead, _firmwareBusy, _triggerWarned, _vibViaWgi;
    private static readonly TimeSpan VibrationLimit = TimeSpan.FromMinutes(2);

    public ControllerTab(SettingsForm form, TrayContext tray) : base(form, tray, "controller", "Controller")
    {
        var status = AddSection("status", "Connection and battery");
        status.Row("Controller", _slot);
        status.Row("Type", _type);
        status.Row("Battery", _battery);
        status.Row("Firmware", _firmware);
        _powerOff = status.Span(Ui.Button("Turn the controller off", (_, _) => PowerOff()));
        status.Note("Turning off uses Windows' hidden XInput call. Some wireless controllers obey it; Xbox One and Series controllers may ignore it, and a cable-connected controller stays on anyway.");

        var test = AddSection("tester", "Button and stick test");
        test.Span(_view);
        test.Note("Everything you press or move shows up here. The dotted ring is the usual stick dead zone games use.");

        var drift = AddSection("drift", "Stick drift check");
        drift.Span(_drift);
        drift.Span(Ui.Button("Measure (don't touch the sticks for 3 seconds)", (_, _) => StartDrift()));
        _drift.Text = "Measures how far the sticks sit off-center on their own. A few percent is normal; more than the game's dead zone (often 10–25%) causes drift.";

        var rumble = AddSection("rumble", "Vibration test");
        rumble.Row("Strength", _strength);
        rumble.Row("Motors", Section.Line(_vibLeft, _vibRight, _vibLeftTrigger, _vibRightTrigger));
        _vibStart = Ui.Button("Start", (_, _) => StartVibration());
        _vibStop = Ui.Button("Stop", (_, _) => StopVibration());
        _vibStop.Enabled = false;
        rumble.Span(Section.Line(_vibStart, _vibStop));
        rumble.Note("Runs until you press Stop, leave this tab or close the window (at most two minutes). Strength and motors can be changed while it runs. The trigger motors (Xbox One and Series controllers) go through Windows.Gaming.Input, which only works while this window is in front.");
        _vibTimer.Tick += (_, _) => VibrationTick();

        var light = AddSection("light", "Xbox button light");
        light.Row("Light", _lightMode);
        light.Row("Brightness", _lightLevel);
        light.Span(_lightKeep);
        light.Span(Section.Line(
            Ui.Button("Apply", async (_, _) => await ApplyLight()),
            Ui.Button("Back to Windows' light", async (_, _) => await ResetLight())));
        light.Note("Works by USB cable (and probably the Xbox Wireless Adapter), not Bluetooth. Controllers don't remember it, so with \"Keep it\" ticked GearTray sets it again each time the controller connects. It goes through Windows' own Xbox controller driver, the way the open-source tool xbledctl does; only the light command is sent.");

        _strength.Value = Tray.Settings.Controller.TestStrength;
        _strength.ValueChanged += (_, _) => { Tray.Settings.Controller.TestStrength = _strength.Value; Tray.SaveSettings(); };
        _slot.SelectedIndexChanged += (_, _) =>
        {
            if (_vibSlot >= 0 && _vibSlot != Slot) StopVibration();
            UpdateStatus();
        };
        _lightMode.SelectedIndexChanged += (_, _) => _lightLevel.Enabled = Ui.Value<XboxLedMode>(_lightMode) != XboxLedMode.Off;
        _timer.Tick += (_, _) => Tick();
        AddButton("Refresh", async () => { await LoadAsync(); LoadFirmware(fresh: true); });
        LoadLight();
    }

    private int Slot => _slot.SelectedItem is Item<int> it ? it.Value : -1;

    public override Task LoadAsync()
    {
        int keep = Slot;
        _slot.Items.Clear();
        foreach (int s in XInput.ConnectedSlots())
            _slot.Items.Add(new Item<int>($"Controller {s + 1}", s));
        var names = PnpDevices.XboxDevices();
        LoadLight();
        if (_slot.Items.Count == 0)
        {
            Info.Text = "No Xbox controller connected. Connect it by USB cable, Xbox Wireless Adapter or Bluetooth.";
            StopVibration();
            EnableInputs(Body, false);
            _type.Text = _battery.Text = "";
            _firmware.Text = "";
            _view.State = null;
            return Task.CompletedTask;
        }
        EnableInputs(Body, true);
        _lightLevel.Enabled = Ui.Value<XboxLedMode>(_lightMode) != XboxLedMode.Off;
        _vibStart.Enabled = _vibSlot < 0;
        _vibStop.Enabled = _vibSlot >= 0;
        Ui.Select(_slot, keep >= 0 ? keep : _slot.Items[0] is Item<int> first ? first.Value : 0);
        if (_slot.SelectedIndex < 0) _slot.SelectedIndex = 0;
        Info.Text = names.Count > 0 ? string.Join(" · ", names) : "Xbox controller";
        UpdateStatus();
        return Task.CompletedTask;
    }

    private void UpdateStatus()
    {
        int slot = Slot;
        if (slot < 0) return;
        string type = XInput.Describe(slot) ?? "controller";
        if (XInput.GetIds(slot) is (0x045E, var pid) && PnpDevices.XboxModel(pid) is { } model) type = $"{model}: {type}";
        _type.Text = type;
        _lastBattery = XInput.GetBattery(slot);
        _battery.Text = _lastBattery?.ToString() ?? "unknown";
        _powerOff.Enabled = _lastBattery?.Wireless == true;
    }

    /// <summary>Firmware comes from the driver's announce messages, so it's only read when the tab is looked at.</summary>
    private async void LoadFirmware(bool fresh = false)
    {
        if (_firmwareBusy || _slot.Items.Count == 0) return;
        _firmwareBusy = true;
        _firmwareRead = true;
        _firmware.Text = "reading…";
        try
        {
            var devices = await Task.Run(() => XboxGip.Devices(fresh));
            var parts = devices.Where(d => d.Firmware is not null)
                .Select(d => d.Firmware!).Distinct().ToList();
            _firmware.Text = parts.Count > 0 ? string.Join(", ", parts)
                : devices.Count > 0 ? "not reported"
                : "not available (only by USB cable or Xbox Wireless Adapter)";
        }
        catch (Exception ex)
        {
            _firmware.Text = "unknown";
            Log.Error("Reading the controller firmware", ex);
        }
        finally { _firmwareBusy = false; }
    }

    public override void Activated()
    {
        _timer.Start();
        GamepadTriggers.Warm();
        if (!_firmwareRead) LoadFirmware();
    }

    public override void Deactivated()
    {
        _timer.Stop();
        StopVibration();
    }

    private void Tick()
    {
        int slot = Slot;
        var s = slot >= 0 ? XInput.GetState(slot) : null;
        _view.State = s;
        if (s is null || DateTime.UtcNow > _driftUntil) return;
        _driftSamples.Add((Mag(s.Value.LeftX, s.Value.LeftY), Mag(s.Value.RightX, s.Value.RightY)));
        if (DateTime.UtcNow.AddMilliseconds(20) >= _driftUntil) FinishDrift();
    }

    private static float Mag(float x, float y) => MathF.Sqrt(x * x + y * y);

    private void StartDrift()
    {
        if (Slot < 0) return;
        _driftSamples.Clear();
        _driftUntil = DateTime.UtcNow.AddSeconds(3);
        _drift.Text = "Measuring… keep your hands off the sticks.";
        _timer.Start();
    }

    private void FinishDrift()
    {
        _driftUntil = DateTime.MinValue;
        if (_driftSamples.Count == 0) return;
        float l = _driftSamples.Max(x => x.L) * 100, r = _driftSamples.Max(x => x.R) * 100;
        static string Verdict(float p) => p < 6 ? "fine" : p < 12 ? "a little off-center, fine with normal dead zones" : p < 24 ? "noticeable: may drift in games with small dead zones" : "drifting: likely to move on its own in games";
        _drift.Text = $"Left stick rests at {l:0.0}% ({Verdict(l)}). Right stick rests at {r:0.0}% ({Verdict(r)}).";
        Log.Info($"Stick drift: left {l:0.0}%, right {r:0.0}%");
    }

    private void StartVibration()
    {
        int slot = Slot;
        if (slot < 0) return;
        if (!(_vibLeft.Checked || _vibRight.Checked || _vibLeftTrigger.Checked || _vibRightTrigger.Checked))
        {
            Status("Tick at least one motor first.");
            return;
        }
        _vibSlot = slot;
        _vibUntil = DateTime.UtcNow + VibrationLimit;
        _triggerWarned = false;
        _vibStart.Enabled = false;
        _vibStop.Enabled = true;
        Status("Vibrating. Press Stop to end it.");
        VibrationTick();
        _vibTimer.Start();
    }

    /// <summary>
    /// Sent again every 200 ms: picks up strength and motor changes, and a focus change can cut vibration short.
    /// With a trigger ticked, all four motors go through Windows.Gaming.Input so the two APIs don't fight over the
    /// main motors; otherwise XInput, which also works while the window is in the background.
    /// </summary>
    private void VibrationTick()
    {
        if (_vibSlot < 0) return;
        if (DateTime.UtcNow > _vibUntil)
        {
            StopVibration();
            Status("The vibration test stopped after two minutes.");
            return;
        }
        double k = _strength.Value / 100.0;
        double left = _vibLeft.Checked ? k : 0, right = _vibRight.Checked ? k : 0;
        if (_vibLeftTrigger.Checked || _vibRightTrigger.Checked)
        {
            if (!_vibViaWgi) XInput.SetVibration(_vibSlot, 0, 0); // hand the main motors over first
            int n = GamepadTriggers.Set(left, right, _vibLeftTrigger.Checked ? k : 0, _vibRightTrigger.Checked ? k : 0);
            _vibViaWgi = n > 0;
            if (_vibViaWgi) return;
            if (!_triggerWarned)
            {
                _triggerWarned = true;
                Status(n < 0 ? "Windows.Gaming.Input isn't available on this PC, so the trigger motors can't be reached."
                             : "Windows hasn't listed the controller for trigger rumble yet. Keep this window in front; it usually takes a second.");
            }
        }
        else if (_vibViaWgi)
        {
            GamepadTriggers.Stop();
            _vibViaWgi = false;
        }
        if (!XInput.SetVibration(_vibSlot, left, right))
        {
            StopVibration();
            Status("The controller didn't accept the vibration command.");
        }
    }

    private void StopVibration()
    {
        _vibTimer.Stop();
        if (_vibSlot >= 0) XInput.SetVibration(_vibSlot, 0, 0);
        GamepadTriggers.Stop();
        _vibSlot = -1;
        _vibViaWgi = false;
        _vibStart.Enabled = Slot >= 0;
        _vibStop.Enabled = false;
    }

    private void LoadLight()
    {
        var c = Tray.Settings.Controller;
        Ui.Select(_lightMode, c.LightMode);
        if (_lightMode.SelectedIndex < 0) _lightMode.SelectedIndex = 0;
        _lightLevel.Value = c.LightBrightness;
        _lightKeep.Checked = c.LightCustom;
    }

    private async Task ApplyLight()
    {
        var c = Tray.Settings.Controller;
        c.LightMode = Ui.Value<XboxLedMode>(_lightMode);
        c.LightBrightness = _lightLevel.Value;
        c.LightCustom = _lightKeep.Checked;
        Tray.SaveSettings();
        Status("Setting the Xbox button light…");
        Status(LightResult(await Tray.SetControllerLightAsync(c.LightMode, c.LightBrightness),
            c.LightCustom ? "Xbox button light set. GearTray sets it again whenever the controller connects." : "Xbox button light set (until the controller reconnects)."));
    }

    private async Task ResetLight()
    {
        var c = Tray.Settings.Controller;
        c.LightCustom = false;
        c.LightMode = XboxLedMode.On;
        c.LightBrightness = XboxGip.DefaultBrightness;
        Tray.SaveSettings();
        LoadLight();
        Status("Setting the Xbox button light back…");
        Status(LightResult(await Tray.SetControllerLightAsync(XboxLedMode.On, XboxGip.DefaultBrightness),
            "The Xbox button light is back to Windows' level, and GearTray leaves it alone."));
    }

    private static string LightResult(int controllers, string ok) => controllers switch
    {
        < 0 => "Windows' Xbox controller driver isn't available, so the light can't be set.",
        0 => "No controller took the light command. It works by USB cable or Xbox Wireless Adapter, not Bluetooth.",
        _ => ok,
    };

    private void PowerOff()
    {
        int slot = Slot;
        if (slot < 0) return;
        Status(XInput.PowerOff(slot) ? "Asked the controller to turn off." : "This controller can't be turned off from the PC.");
        _ = Task.Delay(1500).ContinueWith(_ => Form.BeginInvoke(() => LoadAsync()));
    }
}
