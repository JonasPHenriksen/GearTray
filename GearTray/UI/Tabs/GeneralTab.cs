using System.Diagnostics;
using GearTray.Util;

namespace GearTray.UI.Tabs;

/// <summary>Startup, tray icon, which tabs/blocks are shown, files, about.</summary>
internal sealed class GeneralTab : SettingsTab
{
    private readonly CheckBox _startup = new() { Text = "Start GearTray when I sign in, with admin rights (needed for the keyboard)", AutoSize = true };
    private readonly ComboBox _trayBattery = Ui.Combo(
        new Item<TrayBatterySource>("Lowest of mouse, headset and controller", TrayBatterySource.Lowest),
        new("Mouse", TrayBatterySource.Mouse), new("Headset", TrayBatterySource.Headset), new("Controller", TrayBatterySource.Controller));
    private readonly NumericUpDown _lowPct = new() { Minimum = 5, Maximum = 50, Width = 70 };
    private readonly CheckBox _lowNotify = new() { Text = "Show a notification", AutoSize = true };
    private readonly CheckedListBox _shown = new() { Width = 420, Height = 300, CheckOnClick = true, IntegralHeight = false };
    private bool _filling;

    private sealed record Entry(string Id, string Text)
    {
        public override string ToString() => Text;
    }

    public GeneralTab(SettingsForm form, TrayContext tray) : base(form, tray, "general", "General")
    {
        Info.Text = Elevation.IsAdmin
            ? "GearTray is running with admin rights."
            : "GearTray is running without admin rights, so the keyboard's lighting can't be controlled.";

        var startup = AddSection("startup", "Startup");
        startup.Span(_startup);
        _startup.Checked = Elevation.StartupTaskExists();
        _startup.Click += (_, _) =>
        {
            bool want = _startup.Checked;
            bool ok = Tray.SetStartup(want);
            _startup.Checked = Elevation.StartupTaskExists();
            Status(!ok ? "The startup task wasn't changed." :
                want ? "GearTray will start with Windows, with admin rights." : "GearTray won't start with Windows anymore.");
        };

        var trayIcon = AddSection("tray", "Tray icon and battery warnings");
        _trayBattery.Width = 300;
        Ui.Select(_trayBattery, Tray.Settings.TrayBattery);
        _trayBattery.SelectedIndexChanged += (_, _) => { Tray.Settings.TrayBattery = Ui.Value<TrayBatterySource>(_trayBattery); Tray.SaveSettings(); };
        trayIcon.Row("Battery shown in the icon", _trayBattery);
        _lowPct.Value = Math.Clamp(Tray.Settings.LowBatteryPercent, 5, 50);
        _lowPct.ValueChanged += (_, _) => { Tray.Settings.LowBatteryPercent = (int)_lowPct.Value; Tray.SaveSettings(); };
        trayIcon.Row("Low battery below (%)", _lowPct);
        _lowNotify.Checked = Tray.Settings.LowBatteryNotifications;
        _lowNotify.CheckedChanged += (_, _) => { Tray.Settings.LowBatteryNotifications = _lowNotify.Checked; Tray.SaveSettings(); };
        trayIcon.Row("When a battery is low", _lowNotify);

        var shown = AddSection("shown", "Shown in the settings window");
        shown.Span(_shown);
        shown.Span(Ui.Button("Show everything again", (_, _) => Form.ShowEverything()));
        shown.Note("Untick what you don't use. Hidden blocks also leave the tray menu. Every block's “hide” link does the same.");
        _shown.ItemCheck += (_, e) =>
        {
            if (_filling || _shown.Items[e.Index] is not Entry entry) return;
            bool visible = e.NewValue == CheckState.Checked;
            BeginInvokeSafe(() => Form.SetVisible(entry.Id, visible));
        };

        var files = AddSection("files", "Files");
        files.Span(Section.Line(
            Ui.Button("Open log", (_, _) => Open(Log.FilePath)),
            Ui.Button("Open settings folder", (_, _) => Open(Log.Directory))));

        var about = AddSection("about", "About");
        about.Note($"GearTray {Application.ProductVersion}. Device protocols follow these open-source projects: G-Helper and libratbag (ROG mouse), " +
                   "OpenRazer and OpenRGB (Razer keyboard), HeadsetControl, Linux-Arctis-Manager, arctis-omni-linux and ggoled (Arctis headset). " +
                   "The Xbox controller goes through XInput, which is part of Windows. GearTray only sends commands those projects use, and never talks to the motherboard.");
    }

    private void BeginInvokeSafe(Action a)
    {
        if (Page.IsHandleCreated) Page.BeginInvoke(a); else a();
    }

    /// <summary>Rebuild the list of tabs and blocks with their current visibility.</summary>
    public void FillVisibility(IEnumerable<SettingsTab> tabs)
    {
        _filling = true;
        try
        {
            _shown.Items.Clear();
            foreach (var tab in tabs.Where(t => t != this))
            {
                _shown.Items.Add(new Entry($"tab.{tab.Key}", $"{tab.Title} tab"), !Tray.Settings.IsHidden($"tab.{tab.Key}"));
                foreach (var s in tab.Sections)
                    _shown.Items.Add(new Entry(s.Id, $"      {s.Title}"), !Tray.Settings.IsHidden(s.Id));
            }
        }
        finally { _filling = false; }
    }

    public override Task LoadAsync() => Task.CompletedTask;

    private void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Status(ex.Message); }
    }
}
