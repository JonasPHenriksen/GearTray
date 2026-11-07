using System.Runtime.InteropServices;

namespace GearTray.UI;

/// <summary>
/// --theme-preview: a sample of every kind of control the settings window uses, built from the same pieces, shown on
/// screen for a moment in the light and the dark look and captured with its title bar (DrawToBitmap can't show what
/// Windows draws: the title bar, themed check boxes, scrollbars). Saved side by side.
/// </summary>
internal static class ThemePreview
{
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);

    public static Bitmap Capture()
    {
        using var light = Shot(dark: false);
        using var dark = Shot(dark: true);
        var sheet = new Bitmap(light.Width + dark.Width + 48, Math.Max(light.Height, dark.Height) + 32);
        using var g = Graphics.FromImage(sheet);
        g.Clear(Color.FromArgb(128, 128, 128));
        g.DrawImage(light, 16, 16);
        g.DrawImage(dark, light.Width + 32, 16);
        return sheet;
    }

    /// <summary>The sample window, and beside it a menu like the tray's.</summary>
    private static Bitmap Shot(bool dark)
    {
        Theme.Set(dark);
        using var form = Sample();
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(60, 60);
        form.TopMost = true;
        form.Show();
        Pump(800);
        // DWMWA_EXTENDED_FRAME_BOUNDS: the visible frame, without the invisible resize borders
        var r = DwmGetWindowAttribute(form.Handle, 9, out RECT f, Marshal.SizeOf<RECT>()) == 0
            ? Rectangle.FromLTRB(f.Left, f.Top, f.Right, f.Bottom) : form.Bounds;
        using var window = Grab(r);

        using var menu = SampleMenu();
        menu.Show(new Point(r.Right + 24, r.Top));
        Pump(500);
        using var menuShot = Grab(menu.Bounds);
        menu.Close();
        form.Close();

        var both = new Bitmap(window.Width + 16 + menuShot.Width, Math.Max(window.Height, menuShot.Height));
        using var g = Graphics.FromImage(both);
        g.Clear(Color.FromArgb(128, 128, 128));
        g.DrawImage(window, 0, 0);
        g.DrawImage(menuShot, window.Width + 16, 0);
        return both;
    }

    private static void Pump(int ms)
    {
        for (var until = DateTime.UtcNow.AddMilliseconds(ms); DateTime.UtcNow < until; Thread.Sleep(15)) Application.DoEvents();
    }

    private static Bitmap Grab(Rectangle r)
    {
        var bmp = new Bitmap(r.Width, r.Height);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(r.Location, Point.Empty, r.Size);
        return bmp;
    }

    private static ContextMenuStrip SampleMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Mouse: 80%") { Enabled = false });
        menu.Items.Add(new ToolStripMenuItem("Headset: 45%, charging") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        var noise = new ToolStripMenuItem("Noise control");
        noise.DropDownItems.Add(new ToolStripMenuItem("Noise cancelling") { Checked = true });
        menu.Items.Add(noise);
        menu.Items.Add(new ToolStripMenuItem("Game mode") { Checked = true });
        menu.Items.Add(new ToolStripMenuItem("Keyboard and mouse lights off"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings…");
        menu.Items.Add("Exit");
        return menu;
    }

    private static Form Sample()
    {
        var form = new SampleForm
        {
            Text = "GearTray", Font = SystemFonts.MessageBoxFont ?? Control.DefaultFont, AutoScaleMode = AutoScaleMode.Font,
            ClientSize = new Size(560, 520), Icon = Logo.CreateIcon(),
        };
        var tabs = new ThemedTabControl { Dock = DockStyle.Fill };
        foreach (var title in new[] { "Mouse", "Keyboard", "Headset", "General" })
            tabs.TabPages.Add(new TabPage(title) { UseVisualStyleBackColor = true });

        var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        var lighting = new Section("p.light", "Lighting", "Mouse", _ => { });
        var effect = Ui.Combo(new Item<int>("Static", 1), new Item<int>("Breathing", 2));
        effect.SelectedIndex = 0;
        lighting.Row("Effect", effect);
        var swatch = Ui.ColorButton();
        swatch.BackColor = Color.FromArgb(0, 170, 255);
        lighting.Row("Colour", swatch);
        lighting.Row("Brightness", new LabeledSlider(0, 100, v => $"{v}%") { Value = 60 });
        lighting.Row("Sleep after (minutes)", new NumericUpDown { Value = 5, Width = 70 });
        lighting.Row("Trigger key", new KeyCaptureBox());
        lighting.Span(new CheckBox { Text = "Show a notification", Checked = true, AutoSize = true });
        lighting.Span(new CheckBox { Text = "Angle snapping", AutoSize = true });
        lighting.Span(Section.Line(new RadioButton { Text = "Play recorded keys", Checked = true, AutoSize = true },
                                   new RadioButton { Text = "Type a text", AutoSize = true }));
        lighting.Note("A note in grey, like the ones under the settings.");
        body.Controls.Add(lighting);
        var shown = new Section("p.shown", "Shown in the settings window", "Mouse", _ => { });
        var list = new CheckedListBox { Width = 320, Height = 64, CheckOnClick = true, IntegralHeight = false };
        list.Items.Add("Mouse tab", true);
        list.Items.Add("      Lighting", false);
        list.Items.Add("Keyboard tab", true);
        list.Items.Add("Headset tab", true);
        shown.Span(list);
        body.Controls.Add(shown);

        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(6, 2, 6, 2) };
        bar.Controls.Add(Ui.Button("Apply to mouse", (_, _) => { }));
        bar.Controls.Add(Ui.Button("Reload", (_, _) => { }));
        tabs.TabPages[0].Controls.Add(body);
        tabs.TabPages[0].Controls.Add(bar);

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 30 };
        bottom.Controls.Add(new Label { Text = "Ready.", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 0, 10, 0) });
        bottom.Controls.Add(new CheckBox { Text = "Dark mode", Checked = Theme.Dark, AutoSize = true, Dock = DockStyle.Right, Padding = new Padding(0, 0, 10, 0) });
        form.Controls.Add(tabs);
        form.Controls.Add(bottom);
        return form;
    }

    private sealed class SampleForm : Form
    {
        protected override void OnLoad(EventArgs e)
        {
            Theme.Apply(this);
            base.OnLoad(e);
        }
    }
}
