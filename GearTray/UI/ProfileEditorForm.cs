using System.Diagnostics;
using GearTray.Devices;
using GearTray.Util;

namespace GearTray.UI;

/// <summary>Edit a program profile: which programs, and what changes while one of them is in front.</summary>
internal sealed class ProfileEditorForm : Form
{
    protected override void OnLoad(EventArgs e)
    {
        Theme.Apply(this);
        base.OnLoad(e);
    }

    private readonly TextBox _name = new() { Width = 300 };
    private readonly TextBox _programs = new() { Width = 300 };
    private readonly ComboBox _running = new() { Width = 300, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _kbChange = new() { Text = "Change the keyboard's lighting", AutoSize = true };
    private readonly ComboBox _kbEffect = Ui.Combo(
        new Item<KeyboardEffect>("Off", KeyboardEffect.Off), new("Static color", KeyboardEffect.Static),
        new("Breathing", KeyboardEffect.Breathing), new("Spectrum cycling", KeyboardEffect.Spectrum), new("Wave", KeyboardEffect.Wave),
        new("Reactive", KeyboardEffect.Reactive), new("Starlight", KeyboardEffect.Starlight),
        new("Ambient (follows your screen)", KeyboardEffect.Ambient), new("Audio meter", KeyboardEffect.AudioMeter));
    private readonly Button _kbColor = Ui.ColorButton();
    private readonly LabeledSlider _kbBrightness = new(0, 100, v => $"{v}%");
    private readonly ComboBox _gameMode = Ui.Combo(new Item<bool?>("Don't change", null), new("On", true), new("Off", false));
    private readonly ComboBox _mouseProfile = Ui.Combo(new[] { new Item<int?>("Don't change", null) }
        .Concat(Enumerable.Range(1, 5).Select(i => new Item<int?>($"Onboard profile {i}", i))).ToArray());
    private readonly ComboBox _eq = Ui.Combo(new[] { new Item<int?>("Don't change", null) }
        .Concat(ArctisNovaProWireless.EqPresetNames.Select((n, i) => new Item<int?>(n, i))).ToArray());

    public ProgramProfile Result { get; private set; }

    public ProfileEditorForm(ProgramProfile? existing, KeyboardLightingSettings current)
    {
        Result = existing ?? new ProgramProfile();
        Text = existing is null ? "New program profile" : "Edit program profile";
        Font = SystemFonts.MessageBoxFont ?? Font;
        AutoScaleMode = AutoScaleMode.Font;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        Icon = Logo.CreateIcon(); // for its taskbar button and Alt+Tab
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        var g = Ui.Grid();
        g.Dock = DockStyle.None;
        Ui.Row(g, "Name", _name);
        Ui.Row(g, "Programs", _programs);
        Ui.Row(g, "Add a running program", _running);
        Ui.Span(g, "Program names separated by commas, e.g. SkyrimSE.exe, ForzaHorizon6.exe.");
        Ui.Span(g, "While one of them is in front", heading: true);
        Ui.Row(g, "Keyboard", _kbChange);
        Ui.Row(g, "Effect", _kbEffect);
        Ui.Row(g, "Color", _kbColor);
        Ui.Row(g, "Brightness", _kbBrightness);
        Ui.Row(g, "Game mode", _gameMode);
        Ui.Row(g, "Mouse", _mouseProfile);
        Ui.Row(g, "Headset EQ", _eq);
        var ok = new Button { Text = "Save", AutoSize = true };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        Ui.Row(g, "", Section.Line(ok, cancel));
        Controls.Add(g);
        CancelButton = cancel;

        _running.Items.Add("(pick one)");
        foreach (var name in Process.GetProcesses().Where(p => p.MainWindowHandle != IntPtr.Zero)
                     .Select(p => { try { return p.ProcessName + ".exe"; } catch { return null; } })
                     .Where(n => n is not null).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
            _running.Items.Add(name!);
        _running.SelectedIndex = 0;
        _running.SelectedIndexChanged += (_, _) =>
        {
            if (_running.SelectedIndex <= 0) return;
            var list = _programs.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            if (!list.Contains((string)_running.SelectedItem!, StringComparer.OrdinalIgnoreCase)) list.Add((string)_running.SelectedItem!);
            _programs.Text = string.Join(", ", list);
            _running.SelectedIndex = 0;
        };

        _name.Text = Result.Name;
        _programs.Text = string.Join(", ", Result.Programs);
        var kb = Result.KeyboardLighting ?? current;
        _kbChange.Checked = Result.KeyboardLighting is not null;
        Ui.Select(_kbEffect, kb.Effect);
        if (_kbEffect.SelectedIndex < 0) _kbEffect.SelectedIndex = 1;
        _kbColor.BackColor = ColorUtil.Parse(kb.Color);
        _kbBrightness.Value = kb.Brightness;
        Ui.Select(_gameMode, Result.GameMode);
        Ui.Select(_mouseProfile, Result.MouseProfile);
        Ui.Select(_eq, Result.HeadsetEqPreset);
        _kbChange.CheckedChanged += (_, _) => UpdateKb();
        UpdateKb();

        ok.Click += (_, _) =>
        {
            var programs = _programs.Text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p : p + ".exe").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (programs.Count == 0) { MessageBox.Show(this, "Add at least one program.", "GearTray"); return; }
            KeyboardLightingSettings? look = null;
            if (_kbChange.Checked)
            {
                look = current.Clone();
                look.Configured = true;
                look.Effect = Ui.Value<KeyboardEffect>(_kbEffect);
                look.Color = ColorUtil.ToHex(_kbColor.BackColor);
                look.Brightness = _kbBrightness.Value;
            }
            Result = new ProgramProfile
            {
                Name = string.IsNullOrWhiteSpace(_name.Text) ? programs[0] : _name.Text.Trim(),
                Enabled = Result.Enabled, Programs = programs, KeyboardLighting = look,
                GameMode = Ui.Value<bool?>(_gameMode), MouseProfile = Ui.Value<int?>(_mouseProfile), HeadsetEqPreset = Ui.Value<int?>(_eq),
            };
            DialogResult = DialogResult.OK;
            Close();
        };
    }

    private void UpdateKb() => _kbEffect.Enabled = _kbColor.Enabled = _kbBrightness.Enabled = _kbChange.Checked;
}
