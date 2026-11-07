using System.Diagnostics;
using GearTray.Input;

namespace GearTray.UI;

/// <summary>Create or edit a macro: trigger key, then either recorded key presses (with timing) or a text to type.</summary>
internal sealed class MacroEditorForm : Form
{
    protected override void OnLoad(EventArgs e)
    {
        Theme.Apply(this);
        base.OnLoad(e);
    }

    private readonly TrayContext _tray;
    private readonly TextBox _name = new() { Width = 260 };
    private readonly KeyCaptureBox _trigger = new();
    private readonly CheckBox _ctrl = new() { Text = "Ctrl", AutoSize = true };
    private readonly CheckBox _shift = new() { Text = "Shift", AutoSize = true };
    private readonly CheckBox _alt = new() { Text = "Alt", AutoSize = true };
    private readonly RadioButton _modeKeys = new() { Text = "Play recorded key presses", AutoSize = true, Checked = true };
    private readonly RadioButton _modeText = new() { Text = "Type this text", AutoSize = true };
    private readonly Button _record = new() { Text = "Record", AutoSize = true };
    private readonly ListBox _steps = new() { Width = 420, Height = 150, IntegralHeight = false };
    private readonly CheckBox _timing = new() { Text = "Keep the recorded timing", AutoSize = true, Checked = true };
    private readonly TextBox _text = new() { Width = 420, Height = 80, Multiline = true, ScrollBars = ScrollBars.Vertical };
    private readonly List<MacroStep> _recorded = [];
    private readonly Stopwatch _clock = new();
    private long _lastMs;
    private bool _recording;

    public Macro Result { get; private set; }

    public MacroEditorForm(TrayContext tray, Macro? existing)
    {
        _tray = tray;
        Result = existing ?? new Macro();
        Text = existing is null ? "New macro" : "Edit macro";
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
        Ui.Row(g, "Trigger key", Section.Line(_trigger, _ctrl, _shift, _alt));
        Ui.Row(g, "Does", Section.Line(_modeKeys, _modeText));
        Ui.Row(g, "", _record);
        Ui.Row(g, "Key presses", _steps);
        Ui.Row(g, "", _timing);
        Ui.Row(g, "Text", _text);
        var ok = new Button { Text = "Save", AutoSize = true, DialogResult = DialogResult.None };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        Ui.Row(g, "", Section.Line(ok, cancel));
        Controls.Add(g);
        CancelButton = cancel;

        _name.Text = Result.Name;
        if (Result.TriggerVk != 0) _trigger.SetKey(Result.TriggerVk);
        _ctrl.Checked = Result.Ctrl;
        _shift.Checked = Result.Shift;
        _alt.Checked = Result.Alt;
        _recorded.AddRange(Result.Steps);
        _timing.Checked = Result.KeepTiming;
        _text.Text = Result.Text ?? "";
        if (!string.IsNullOrEmpty(Result.Text)) _modeText.Checked = true;
        ShowSteps();
        UpdateMode();

        _modeKeys.CheckedChanged += (_, _) => UpdateMode();
        _record.Click += (_, _) => ToggleRecording();
        ok.Click += (_, _) => Save();
        FormClosing += (_, _) => { if (_recording) StopRecording(); };
    }

    private void UpdateMode()
    {
        bool keys = _modeKeys.Checked;
        _record.Enabled = _steps.Enabled = _timing.Enabled = keys;
        _text.Enabled = !keys;
    }

    private void ToggleRecording()
    {
        if (_recording) { StopRecording(); return; }
        _recorded.Clear();
        _steps.Items.Clear();
        _recording = true;
        _record.Text = "Stop recording";
        _lastMs = 0;
        _clock.Restart();
        _tray.StartMacroRecording((vk, up) => BeginInvoke(() => Recorded(vk, up)));
    }

    private void StopRecording()
    {
        _tray.StopMacroRecording();
        _recording = false;
        _record.Text = "Record again";
        ShowSteps();
    }

    private void Recorded(int vk, bool up)
    {
        if (!_recording) return;
        // ignore auto-repeat: a key-down for a key that is already down
        if (!up && _recorded.LastOrDefault(s => s.Vk == vk) is { Up: false }) return;
        long now = _clock.ElapsedMilliseconds;
        _recorded.Add(new MacroStep(vk, up, (int)(_recorded.Count == 0 ? 0 : now - _lastMs)));
        _lastMs = now;
        _steps.Items.Add(Describe(_recorded[^1]));
        _steps.TopIndex = Math.Max(0, _steps.Items.Count - 1);
        if (_recorded.Count >= 400) StopRecording();
    }

    private static string Describe(MacroStep s) => $"{(s.DelayMs > 0 ? $"wait {s.DelayMs} ms, " : "")}{(s.Up ? "release" : "press")} {KeyNames.Name(s.Vk)}";

    private void ShowSteps()
    {
        _steps.Items.Clear();
        foreach (var s in _recorded) _steps.Items.Add(Describe(s));
        if (_recorded.Count == 0) _steps.Items.Add("(nothing yet: press Record, type the keys, press Stop)");
    }

    private void Save()
    {
        if (_recording) StopRecording();
        if (_trigger.Vk == 0) { MessageBox.Show(this, "Click the trigger box and press the key that should start the macro.", "GearTray"); return; }
        if (_modeKeys.Checked && _recorded.Count == 0) { MessageBox.Show(this, "Record some key presses first.", "GearTray"); return; }
        if (_modeText.Checked && string.IsNullOrEmpty(_text.Text)) { MessageBox.Show(this, "Enter the text to type.", "GearTray"); return; }
        Result = new Macro
        {
            Name = string.IsNullOrWhiteSpace(_name.Text) ? "Macro" : _name.Text.Trim(),
            Enabled = Result.Enabled,
            TriggerVk = _trigger.Vk, Ctrl = _ctrl.Checked, Shift = _shift.Checked, Alt = _alt.Checked,
            Steps = _modeKeys.Checked ? [.. _recorded] : [],
            Text = _modeText.Checked ? _text.Text : null,
            KeepTiming = _timing.Checked,
        };
        DialogResult = DialogResult.OK;
        Close();
    }
}
