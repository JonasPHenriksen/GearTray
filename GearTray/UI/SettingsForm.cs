using GearTray.UI.Tabs;
using GearTray.Util;

namespace GearTray.UI;

/// <summary>
/// Settings window: one tab per device plus Programs and General. Every block can be hidden ("hide" link or the
/// General tab's list); hidden tabs are removed from the window.
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly TrayContext _tray;
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 30, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 0, 10, 0) };
    private readonly List<SettingsTab> _all;
    private readonly GeneralTab _general;
    private SettingsTab? _active;
    private bool _loading;

    internal SettingsForm(TrayContext tray)
    {
        _tray = tray;
        Text = "GearTray";
        Font = SystemFonts.MessageBoxFont ?? Font;
        AutoScaleMode = AutoScaleMode.Font;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(720, 780);
        MinimumSize = new Size(620, 520);
        Icon = Logo.CreateIcon();

        _general = new GeneralTab(this, tray);
        _all =
        [
            new MouseTab(this, tray),
            new KeyboardTab(this, tray),
            new HeadsetTab(this, tray),
            new ControllerTab(this, tray),
            new ProgramsTab(this, tray),
            _general,
        ];
        Controls.Add(_tabs);
        Controls.Add(_status);
        ApplyVisibility();
        _general.FillVisibility(_all);

        _tabs.SelectedIndexChanged += (_, _) => SwitchTab();
        Shown += async (_, _) => { await ReloadAll(); SwitchTab(); };
        FormClosing += (_, _) => _active?.Deactivated();
    }

    private void SwitchTab()
    {
        var next = _all.FirstOrDefault(t => t.Page == _tabs.SelectedTab);
        if (next == _active) return;
        _active?.Deactivated();
        _active = next;
        _active?.Activated();
    }

    // ---------- visibility ----------

    internal void HideSection(Section s)
    {
        SetVisible(s.Id, false);
        Status($"Hid “{s.Title}”. The General tab lists it, to show it again.");
    }

    internal void SetVisible(string id, bool visible)
    {
        var hidden = _tray.Settings.HiddenSections;
        hidden.RemoveAll(x => x.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (!visible) hidden.Add(id);
        _tray.SaveSettings();
        ApplyVisibility();
        _general.FillVisibility(_all);
    }

    internal void ShowEverything()
    {
        _tray.Settings.HiddenSections.Clear();
        _tray.SaveSettings();
        ApplyVisibility();
        _general.FillVisibility(_all);
        Status("Everything is shown again.");
    }

    private void ApplyVisibility()
    {
        var selected = _tabs.SelectedTab;
        _tabs.SuspendLayout();
        _tabs.TabPages.Clear();
        foreach (var tab in _all)
        {
            foreach (var s in tab.Sections) s.Visible = !_tray.Settings.IsHidden(s.Id);
            if (tab == _general || !_tray.Settings.IsHidden($"tab.{tab.Key}")) _tabs.TabPages.Add(tab.Page);
        }
        if (selected is not null && _tabs.TabPages.Contains(selected)) _tabs.SelectedTab = selected;
        _tabs.ResumeLayout();
    }

    // ---------- loading, status, device I/O ----------

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
            foreach (var tab in _all)
            {
                try { await tab.LoadAsync(); }
                catch (Exception ex) { Log.Error($"Loading the {tab.Title} tab", ex); }
            }
            Status(Elevation.IsAdmin ? "Ready." : "Ready. Running without admin rights, so the keyboard's lighting can't be controlled.");
        }
        finally { _loading = false; }
    }

    internal void Status(string text) => _status.Text = text;

    internal async Task<bool> Run(string message, Action action)
    {
        Status(message);
        UseWaitCursor = true;
        try
        {
            await _tray.Devices.RunAsync(action);
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

    /// <summary>Diagnostics (--screenshot): render every tab's full content to a PNG after the devices were read.</summary>
    public async Task CaptureTabsAsync(string dir)
    {
        await _tray.Started;
        await ReloadAll();
        ClientSize = new Size(ClientSize.Width, 2600); // tall enough that nothing scrolls (the window is off-screen)
        foreach (var tab in _all)
        {
            if (!_tabs.TabPages.Contains(tab.Page)) continue;
            _tabs.SelectedTab = tab.Page;
            await Task.Delay(500);
            var body = tab.Page.Controls.OfType<FlowLayoutPanel>().First(p => p.Dock == DockStyle.Fill);
            int h = body.Controls.Cast<Control>().Where(c => c.Visible).Select(c => c.Bottom).DefaultIfEmpty(100).Max() + 10;
            using var bmp = new Bitmap(body.Width, Math.Min(h, body.Height));
            body.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
            bmp.Save(Path.Combine(dir, $"tab-{tab.Key}.png"), System.Drawing.Imaging.ImageFormat.Png);
        }
    }
}
