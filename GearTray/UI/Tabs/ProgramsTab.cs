using GearTray.Util;

namespace GearTray.UI.Tabs;

/// <summary>Program profiles: settings that switch automatically while a given program is in front.</summary>
internal sealed class ProgramsTab : SettingsTab
{
    private readonly ListBox _list = new() { Width = 560, Height = 200, IntegralHeight = false };
    private readonly Label _now;

    public ProgramsTab(SettingsForm form, TrayContext tray) : base(form, tray, "programs", "Programs")
    {
        Info.Text = "Like Synapse's and Armoury Crate's linked programs: while one of a profile's programs is the program in front, " +
                    "its settings apply, and your normal settings come back when you switch away.";
        var profiles = AddSection("profiles", "Program profiles");
        profiles.Span(_list);
        profiles.Span(Section.Line(
            Ui.Button("New…", (_, _) => Edit(null)),
            Ui.Button("Edit…", (_, _) => { if (_list.SelectedItem is ProgramProfile p) Edit(p); }),
            Ui.Button("On / off", (_, _) => Toggle()),
            Ui.Button("Remove", (_, _) => Remove())));
        _now = profiles.Note("");
        _list.DoubleClick += (_, _) => { if (_list.SelectedItem is ProgramProfile p) Edit(p); };
        Tray.ProfileChanged += ShowNow;
    }

    private void Edit(ProgramProfile? existing)
    {
        using var dlg = new ProfileEditorForm(existing, Tray.Settings.Keyboard);
        if (dlg.ShowDialog(Form) != DialogResult.OK) return;
        var list = Tray.Settings.ProgramProfiles;
        int i = existing is null ? -1 : list.IndexOf(existing);
        if (i >= 0) list[i] = dlg.Result; else list.Add(dlg.Result);
        Save();
    }

    private void Toggle()
    {
        if (_list.SelectedItem is not ProgramProfile p) return;
        p.Enabled = !p.Enabled;
        Save();
    }

    private void Remove()
    {
        if (_list.SelectedItem is not ProgramProfile p) return;
        Tray.Settings.ProgramProfiles.Remove(p);
        Save();
    }

    private void Save()
    {
        Tray.SaveSettings();
        Tray.ApplyProgramProfiles(force: true);
        _ = LoadAsync();
        Status("Program profiles saved.");
    }

    private void ShowNow()
    {
        if (_now.IsDisposed) { Tray.ProfileChanged -= ShowNow; return; }
        _now.Text = Tray.ActiveProfile is { } p
            ? $"Now: {Tray.CurrentProgram} is in front, so “{p.Name}” is active."
            : $"Now: {Tray.CurrentProgram ?? "nothing"} is in front; no profile is active.";
    }

    public override Task LoadAsync()
    {
        _list.Items.Clear();
        foreach (var p in Tray.Settings.ProgramProfiles) _list.Items.Add(p);
        if (_list.Items.Count == 0) _list.Items.Add("(no profiles yet: press New…)");
        ShowNow();
        return Task.CompletedTask;
    }
}
