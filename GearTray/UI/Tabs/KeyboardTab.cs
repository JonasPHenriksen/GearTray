using GearTray.Devices;
using GearTray.Input;
using GearTray.Util;

namespace GearTray.UI.Tabs;

/// <summary>Keyboard: lighting (hardware + software effects), lights-off rules, game mode, remaps, macros.</summary>
internal sealed class KeyboardTab : SettingsTab
{
    private readonly ComboBox _effect = Ui.Combo(
        new Item<KeyboardEffect>("Off", KeyboardEffect.Off), new("Static color", KeyboardEffect.Static),
        new("Breathing", KeyboardEffect.Breathing), new("Breathing (random colors)", KeyboardEffect.BreathingRandom),
        new("Spectrum cycling", KeyboardEffect.Spectrum), new("Wave", KeyboardEffect.Wave),
        new("Reactive (keys light up when pressed)", KeyboardEffect.Reactive), new("Starlight", KeyboardEffect.Starlight),
        new("Starlight (random colors)", KeyboardEffect.StarlightRandom),
        new("Ripple (waves from the keys you press)", KeyboardEffect.Ripple),
        new("Custom (your per-key colors below)", KeyboardEffect.Custom),
        new("Ambient (follows your screen)", KeyboardEffect.Ambient), new("Audio meter (follows what you hear)", KeyboardEffect.AudioMeter));
    private readonly Button _color = Ui.ColorButton();
    private readonly Button _color2 = Ui.ColorButton();
    private readonly KeyboardPainter _painter = new();
    private readonly Button _brush = Ui.ColorButton();
    private readonly LabeledSlider _brightness = new(0, 100, v => $"{v}%");
    private readonly ComboBox _waveDir = Ui.Combo(new Item<bool>("Left to right", false), new("Right to left", true));
    private readonly ComboBox _speed = Ui.Combo(new Item<int>("Fast", 1), new("Medium", 2), new("Slow", 3));
    private readonly ComboBox _monitor = Ui.Combo(Screen.AllScreens.Select((s, i) =>
        new Item<string?>($"Monitor {i + 1}: {s.Bounds.Width}×{s.Bounds.Height}{(s.Primary ? " (main)" : "")}", s.DeviceName)).ToArray());
    private readonly ComboBox _ambientStyle = Ui.Combo(new Item<bool>("Keys show the picture's layout", true), new("Whole keyboard in one color", false));
    private readonly LabeledSlider _saturation = new(0, 150, v => $"+{v}%");
    private readonly Label _effectNote;
    private readonly CheckBox _offDisplay = new() { Text = "When the screen turns off", AutoSize = true };
    private readonly ComboBox _idle = Ui.Combo(new Item<int>("Never", 0), new("1 min", 1), new("5 min", 5), new("10 min", 10), new("15 min", 15), new("30 min", 30));
    private readonly CheckBox _gmManual = new() { Text = "On now", AutoSize = true };
    private readonly CheckBox _gmAuto = new() { Text = "Turn on by itself while a game is in front", AutoSize = true };
    private readonly CheckBox _gmWin = new() { Text = "Windows key", AutoSize = true };
    private readonly CheckBox _gmAltTab = new() { Text = "Alt+Tab", AutoSize = true };
    private readonly CheckBox _gmAltF4 = new() { Text = "Alt+F4", AutoSize = true };
    private readonly TextBox _gmExtra = new() { Width = 340 };
    private readonly TextBox _gmIgnore = new() { Width = 340 };
    private readonly Label _gmStatus;
    private readonly ListBox _remaps = new() { Width = 340, Height = 90, IntegralHeight = false };
    private readonly KeyCaptureBox _remapFrom = new(), _remapTo = new();
    private readonly CheckBox _remapDisable = new() { Text = "Disable the key instead", AutoSize = true };
    private readonly ListBox _macros = new() { Width = 460, Height = 110, IntegralHeight = false };
    private readonly Section _lighting;
    private readonly Button _admin;

    public KeyboardTab(SettingsForm form, TrayContext tray) : base(form, tray, "keyboard", "Keyboard")
    {
        _admin = Ui.Button("Restart GearTray with admin rights", (_, _) => Tray.RestartElevated());
        Body.Controls.Add(_admin);

        _lighting = AddSection("lighting", "Lighting (keys and underglow)");
        _lighting.Row("Effect", _effect);
        _lighting.Row("Color", _color);
        _lighting.Row("Background", _color2);
        _lighting.Row("Brightness", _brightness);
        _lighting.Row("Wave direction", _waveDir);
        _lighting.Row("Speed", _speed);
        _lighting.Row("Screen to follow", _monitor);
        _lighting.Row("Ambient style", _ambientStyle);
        _lighting.Row("Color boost", _saturation);
        _effectNote = _lighting.Note("");
        _effect.SelectedIndexChanged += (_, _) => UpdateControls();

        var perKey = AddSection("perkey", "Per-key colors");
        perKey.Span(_painter);
        _brush.BackColor = Color.Red;
        _painter.Brush = Color.Red;
        _brush.BackColorChanged += (_, _) => _painter.Brush = _brush.BackColor;
        perKey.Span(Section.Line(
            Section.Caption("Paint with"), _brush,
            Ui.Button("Fill keys", (_, _) => { _painter.Fill(_brush.BackColor, underglow: false); Preview(); }),
            Ui.Button("Fill underglow", (_, _) => { _painter.Fill(_brush.BackColor, keys: false); Preview(); }),
            Ui.Button("Clear all", (_, _) => { _painter.Fill(Color.Black); Preview(); })));
        perKey.Note("Click or drag to paint keys, right-click a key to pick up its color. Choose “Custom” as the effect and press Apply to keep it; " +
                    "while Custom is selected, painting shows on the keyboard right away.");
        _painter.ColorPicked += c => _brush.BackColor = c;
        _painter.Painted += Preview;

        var off = AddSection("lightsoff", "Turn the lights off");
        off.Row("Automatically", _offDisplay);
        off.Row("After no input for", _idle);

        var gm = AddSection("gamemode", "Game mode");
        gm.Row("Game mode", _gmManual);
        gm.Row("", _gmAuto);
        gm.Row("Blocks", Section.Line(_gmWin, _gmAltTab, _gmAltF4));
        gm.Row("More games", _gmExtra);
        gm.Row("Never a game", _gmIgnore);
        gm.Note("Games installed through Steam, Epic, Xbox or GOG are recognised automatically. Add other programs by name, separated by commas (e.g. eldenring.exe).");
        _gmStatus = gm.Note("");

        var remap = AddSection("remap", "Key remapping");
        remap.Row("Key", Section.Line(_remapFrom, Section.Caption("→"), _remapTo, Ui.Button("Add", (_, _) => AddRemap())));
        remap.Row("", _remapDisable);
        remap.Row("Remaps", _remaps);
        remap.Row("", Ui.Button("Remove selected", (_, _) => { if (_remaps.SelectedItem is not null) _remaps.Items.Remove(_remaps.SelectedItem); }));
        remap.Note("Remaps and macros work for any keyboard while GearTray runs. A few games with strict anti-cheat ignore keys sent by software.");
        _remapDisable.CheckedChanged += (_, _) => _remapTo.Enabled = !_remapDisable.Checked;

        var macros = AddSection("macros", "Macros");
        macros.Span(_macros);
        macros.Span(Section.Line(
            Ui.Button("New…", (_, _) => EditMacro(null)),
            Ui.Button("Edit…", (_, _) => { if (_macros.SelectedItem is Macro m) EditMacro(m); }),
            Ui.Button("On / off", (_, _) => ToggleMacro()),
            Ui.Button("Remove", (_, _) => { if (_macros.SelectedItem is not null) _macros.Items.Remove(_macros.SelectedItem); })));
        macros.Note("A macro plays recorded key presses (with their timing) or types a text when you press its trigger key. Press Apply to save changes.");
        _macros.DoubleClick += (_, _) => { if (_macros.SelectedItem is Macro m) EditMacro(m); };

        AddButton("Apply", Apply);
        AddButton("Reload", LoadAsync);
        Tray.GameModeChanged += UpdateGameModeStatus;
    }

    private RazerHuntsmanElite? Keyboard => Tray.Devices.Get<RazerHuntsmanElite>() is { Connected: true } k ? k : null;

    private void EditMacro(Macro? existing)
    {
        using var dlg = new MacroEditorForm(Tray, existing);
        if (dlg.ShowDialog(Form) != DialogResult.OK) return;
        int index = existing is null ? -1 : _macros.Items.IndexOf(existing);
        if (index >= 0) _macros.Items[index] = dlg.Result; else _macros.Items.Add(dlg.Result);
        Status("Macro ready. Press Apply to use it.");
    }

    private void ToggleMacro()
    {
        if (_macros.SelectedItem is not Macro m) return;
        m.Enabled = !m.Enabled;
        _macros.Items[_macros.SelectedIndex] = m; // refresh text
    }

    private void AddRemap()
    {
        if (_remapFrom.Vk == 0 || (!_remapDisable.Checked && _remapTo.Vk == 0))
        {
            Status("Click a box and press the key, then press Add.");
            return;
        }
        var remap = new KeyRemap(_remapFrom.Vk, _remapDisable.Checked ? 0 : _remapTo.Vk);
        foreach (var existing in _remaps.Items.Cast<KeyRemap>().Where(r => r.From == remap.From).ToList()) _remaps.Items.Remove(existing);
        _remaps.Items.Add(remap);
        _remapFrom.Clear();
        _remapTo.Clear();
        Status("Added. Press Apply to use it.");
    }

    private void UpdateControls()
    {
        var e = Ui.Value<KeyboardEffect>(_effect);
        bool on = _effect.Enabled;
        _color.Enabled = on && KeyboardLighting.UsesColor.Contains(e);
        _color2.Enabled = on && e == KeyboardEffect.Ripple && Keyboard is { HardwareRipple: false };
        _brightness.Enabled = on && e != KeyboardEffect.Off;
        _waveDir.Enabled = on && e == KeyboardEffect.Wave;
        _speed.Enabled = on && e is KeyboardEffect.Reactive or KeyboardEffect.Starlight or KeyboardEffect.StarlightRandom;
        _monitor.Enabled = _ambientStyle.Enabled = _saturation.Enabled = on && e == KeyboardEffect.Ambient;
        _effectNote.Text = e switch
        {
            KeyboardEffect.Ambient => "Ambient mirrors the colors of the chosen screen, like Razer's Ambient Awareness. It runs while GearTray runs and uses the graphics card, so it costs almost no CPU.",
            KeyboardEffect.AudioMeter => "Top three rows: left channel, bottom three: right channel, underglow pulses in the chosen color. Follows your default playback device.",
            KeyboardEffect.Ripple => Keyboard is { HardwareRipple: true }
                ? "Your keyboard's firmware has its own ripple, so this runs inside the keyboard."
                : "Rings spread from every key you press, over the background color. GearTray draws it while it runs.",
            KeyboardEffect.Custom => "Shows your per-key colors from the block below. GearTray puts them back after sleep or replugging.",
            _ => "This effect runs inside the keyboard and is saved there.",
        };
        if (e is KeyboardEffect.Ambient or KeyboardEffect.AudioMeter or KeyboardEffect.Ripple && Tray.Settings.Keyboard.Effect == e && Tray.EffectStatus != "not running")
            _effectNote.Text += $"\nNow: {Tray.EffectStatus}.";
    }

    /// <summary>While Custom is the selected effect, show paint strokes on the keyboard immediately (not saved).</summary>
    private void Preview()
    {
        if (Ui.Value<KeyboardEffect>(_effect) != KeyboardEffect.Custom || Keyboard is null) return;
        Tray.PreviewKeyboardFrame(_painter.Frame(), _brightness.Value);
    }

    private void UpdateGameModeStatus()
    {
        if (_gmStatus.IsDisposed) { Tray.GameModeChanged -= UpdateGameModeStatus; return; }
        _gmStatus.Text = Tray.GameModeActive
            ? "Game mode is on" + (Tray.CurrentGame is string g && Tray.Settings.KeyboardFeatures.GameModeAuto ? $" ({g} is in front)." : ".")
            : "Game mode is off.";
    }

    public override async Task LoadAsync()
    {
        var cfg = Tray.Settings.Keyboard;
        var f = Tray.Settings.KeyboardFeatures;

        _offDisplay.Checked = f.LightsOffWhenDisplayOff;
        Ui.Select(_idle, f.LightsOffIdleMinutes, $"{f.LightsOffIdleMinutes} min");
        _gmManual.Checked = f.GameModeManual;
        _gmAuto.Checked = f.GameModeAuto;
        _gmWin.Checked = f.BlockWindowsKey;
        _gmAltTab.Checked = f.BlockAltTab;
        _gmAltF4.Checked = f.BlockAltF4;
        _gmExtra.Text = string.Join(", ", f.ExtraGames);
        _gmIgnore.Text = string.Join(", ", f.IgnoredApps);
        _remaps.Items.Clear();
        foreach (var r in f.Remaps) _remaps.Items.Add(r);
        _macros.Items.Clear();
        foreach (var m in f.Macros) _macros.Items.Add(m);
        UpdateGameModeStatus();

        Ui.Select(_effect, cfg.Configured ? cfg.Effect : KeyboardEffect.Spectrum);
        _color.BackColor = ColorUtil.Parse(cfg.Color);
        _color2.BackColor = ColorUtil.Parse(cfg.Color2);
        _painter.SetColors(cfg.CustomColors);
        _brightness.Value = cfg.Brightness;
        Ui.Select(_waveDir, cfg.WaveRightToLeft);
        Ui.Select(_speed, cfg.Speed);
        Ui.Select(_monitor, cfg.AmbientMonitor ?? Screen.PrimaryScreen?.DeviceName);
        if (_monitor.SelectedIndex < 0 && _monitor.Items.Count > 0) _monitor.SelectedIndex = 0;
        Ui.Select(_ambientStyle, cfg.AmbientFollowPicture);
        _saturation.Value = cfg.AmbientSaturation;

        var k = Tray.Devices.Get<RazerHuntsmanElite>();
        _admin.Visible = k is { Connected: false, Problem: not null } && !Elevation.IsAdmin;
        if (k is not { Connected: true })
        {
            Info.Text = (k?.Problem is { } p ? $"Razer Huntsman Elite: {p}." : "Razer keyboard not connected.")
                + " Lighting needs the keyboard; game mode, remaps and macros work anyway.";
            EnableInputs(_lighting, false);
            return;
        }
        try
        {
            int brightness = await Tray.Devices.RunAsync(k.GetBrightness);
            if (!cfg.Configured) _brightness.Value = brightness;
            _painter.Iso = k.IsIso;
            Info.Text = $"Razer Huntsman Elite · {k.Detail}"
                + (cfg.Configured ? "" : "\nThe keyboard can't report which effect it's showing. Pick one and press Apply.");
            EnableInputs(_lighting, true);
            UpdateControls();
        }
        catch (Exception ex)
        {
            Info.Text = "Couldn't read the keyboard: " + ex.Message;
            EnableInputs(_lighting, false);
        }
    }

    private async Task Apply()
    {
        var s = Tray.Settings;
        if (Keyboard is not null && _effect.Enabled)
        {
            var look = new KeyboardLighting(Ui.Value<KeyboardEffect>(_effect), ColorUtil.Plain(_color.BackColor),
                _brightness.Value, Ui.Value<bool>(_waveDir), Ui.Value<int>(_speed));
            s.Keyboard = s.Keyboard.With(look);
            s.Keyboard.AmbientMonitor = Ui.Value<string?>(_monitor);
            s.Keyboard.AmbientFollowPicture = Ui.Value<bool>(_ambientStyle);
            s.Keyboard.AmbientSaturation = _saturation.Value;
            s.Keyboard.Color2 = ColorUtil.ToHex(_color2.BackColor);
            s.Keyboard.CustomColors = _painter.GetColors();
            if (look.Effect != KeyboardEffect.Off) s.LightsOff = false;
        }
        var f = s.KeyboardFeatures;
        f.LightsOffWhenDisplayOff = _offDisplay.Checked;
        f.LightsOffIdleMinutes = Ui.Value<int>(_idle);
        f.GameModeManual = _gmManual.Checked;
        f.GameModeAuto = _gmAuto.Checked;
        f.BlockWindowsKey = _gmWin.Checked;
        f.BlockAltTab = _gmAltTab.Checked;
        f.BlockAltF4 = _gmAltF4.Checked;
        f.ExtraGames = SplitNames(_gmExtra.Text);
        f.IgnoredApps = SplitNames(_gmIgnore.Text);
        f.Remaps = _remaps.Items.Cast<KeyRemap>().ToList();
        f.Macros = _macros.Items.Cast<Macro>().ToList();
        Tray.SaveSettings();
        Tray.ApplyKeyboardLook(persist: true);
        Tray.ApplyInputFeatures();
        Status("Saved.");
        await Task.Delay(1200);
        UpdateControls();
        UpdateGameModeStatus();
    }

    private static List<string> SplitNames(string text) =>
        text.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
