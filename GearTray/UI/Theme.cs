using System.Drawing.Drawing2D;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using GearTray.Util;
using Microsoft.Win32;

namespace GearTray.UI;

/// <summary>
/// Light (Windows' own look, as before) or dark. Dark is Wabbajack Wizard's palette, so the two apps look alike, on
/// top of .NET's dark mode for the parts Windows draws (check boxes, scrollbars, spin buttons). The title bar is asked
/// for the window's own colours, the way Wabbajack Wizard asks for them. Windows already open keep the look they were
/// made with; the settings window is rebuilt when it changes.
/// </summary>
internal static class Theme
{
    // Wabbajack Wizard's DARK palette (wj_gui.py), same names.
    public static readonly Color Bg = Hex(0x1f1f1f);     // the window and its frames
    public static readonly Color Panel = Hex(0x2b2b2b);  // unselected tabs, buttons, menus
    public static readonly Color Field = Hex(0x151515);  // lists, text and number boxes, drop-downs
    public static readonly Color Fg = Hex(0xe4e4e4);
    public static readonly Color Muted = Hex(0x9a9a9a);  // notes and disabled text
    public static readonly Color Border = Hex(0x3d3d3d);
    public static readonly Color Select = Hex(0x2d5b8c); // pressed buttons, checked menu items
    public static readonly Color Accent = Hex(0x4a90d9);

    public static bool Dark { get; private set; }

    /// <summary>Dark, light, or (null) whatever Windows uses for apps. Windows made afterwards get the new look.</summary>
    public static void Set(bool? dark)
    {
        Dark = dark ?? WindowsAppsAreDark();
        try { Application.SetColorMode(Dark ? SystemColorMode.Dark : SystemColorMode.Classic); }
        catch (Exception ex) { Log.Warn("Color mode: " + ex.Message); }
        if (Dark) ToolStripManager.Renderer = new DarkMenuRenderer(); // the tray menu and its submenus
        else ToolStripManager.RenderMode = ToolStripManagerRenderMode.Professional;
    }

    /// <summary>Settings > Personalization > Colors > "Choose your default app mode".</summary>
    public static bool WindowsAppsAreDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception) { return false; }
    }

    // For drawing of our own; the light values are what those places used before.
    public static Color Page => Dark ? Bg : SystemColors.Window;   // a tab page's background
    public static Color Text => Dark ? Fg : SystemColors.ControlText;
    public static Color Dim => Dark ? Muted : SystemColors.GrayText;
    public static Color Line => Dark ? Border : SystemColors.ControlDark;
    public static Color LinkActive => Dark ? Accent : SystemColors.HotTrack;

    private static readonly object Mark = new();
    private static readonly ConditionalWeakTable<Control, object> Kept = new(), Styled = new();

    /// <summary>Leave this control's colours alone: colour swatches show the picked colour as their background.</summary>
    public static T Keep<T>(T control) where T : Control
    {
        Kept.AddOrUpdate(control, Mark);
        return control;
    }

    /// <summary>Paints a window dark, when dark: the title bar, everything in it, and everything added to it later.</summary>
    public static void Apply(Form form)
    {
        if (!Dark) return;
        form.BackColor = Bg;
        form.ForeColor = Fg; // labels, check boxes and panels take the window's colours unless given their own
        if (form.IsHandleCreated) TitleBar(form.Handle);
        form.HandleCreated += (_, _) => TitleBar(form.Handle);
        Style(form);
    }

    private static void Style(Control c)
    {
        if (Styled.TryGetValue(c, out _)) return; // tab pages come and go when tabs are hidden and shown
        Styled.Add(c, Mark);
        if (Kept.TryGetValue(c, out _)) return;
        switch (c)
        {
            case TabPage page:
                page.UseVisualStyleBackColor = false;
                page.BackColor = Bg;
                break;
            case Button b:
                b.FlatStyle = FlatStyle.Flat;
                b.UseVisualStyleBackColor = false;
                b.BackColor = Panel;
                b.ForeColor = Fg;
                b.FlatAppearance.BorderColor = Border;
                b.FlatAppearance.MouseOverBackColor = Border;
                b.FlatAppearance.MouseDownBackColor = Select;
                break;
            case UpDownBase upDown: // its text box and arrows are its own parts: colour it, not them
                upDown.BackColor = Field;
                upDown.ForeColor = Fg;
                upDown.BorderStyle = BorderStyle.FixedSingle;
                return;
            case TextBoxBase text:
                text.BackColor = Field;
                text.ForeColor = Fg;
                text.BorderStyle = BorderStyle.FixedSingle;
                break;
            case ComboBox combo:
                combo.FlatStyle = FlatStyle.Flat;
                combo.BackColor = Field;
                combo.ForeColor = Fg;
                break;
            case ListBox list:
                list.BackColor = Field;
                list.ForeColor = Fg;
                list.BorderStyle = BorderStyle.FixedSingle;
                break;
        }
        if (c is ScrollableControl { AutoScroll: true } or ListBox or TextBoxBase { Multiline: true }) DarkScrollbars(c);
        foreach (Control child in c.Controls) Style(child);
        c.ControlAdded += (_, e) => { if (e.Control is { } added) Style(added); };
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? appName, string? idList);

    /// <summary>Explorer's dark scrollbars on a control that scrolls.</summary>
    private static void DarkScrollbars(Control c)
    {
        if (c.IsHandleCreated) SetWindowTheme(c.Handle, "DarkMode_Explorer", null);
        c.HandleCreated += (_, _) => SetWindowTheme(c.Handle, "DarkMode_Explorer", null);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// As Wabbajack Wizard does it: the dark title bar (DWMWA_USE_IMMERSIVE_DARK_MODE, 20; 19 on Windows 10 before
    /// build 18985), then on Windows 11 the bar, its text and the window border in the window's own colours
    /// (DWMWA_CAPTION_COLOR 35, DWMWA_TEXT_COLOR 36, DWMWA_BORDER_COLOR 34). Windows 10 refuses those three, which is
    /// fine: the dark flag alone already gave it a dark bar.
    /// </summary>
    private static void TitleBar(IntPtr hwnd)
    {
        int on = 1;
        if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0) DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
        int caption = ColorRef(Bg), text = ColorRef(Fg), border = ColorRef(Border);
        DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));
        DwmSetWindowAttribute(hwnd, 36, ref text, sizeof(int));
        DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int));
    }

    private static int ColorRef(Color c) => c.R | c.G << 8 | c.B << 16;
    private static Color Hex(int rgb) => Color.FromArgb(rgb >> 16 & 0xFF, rgb >> 8 & 0xFF, rgb & 0xFF);

    /// <summary>Menus in the dark palette, with a light check mark (the standard one is black).</summary>
    private sealed class DarkMenuRenderer() : ToolStripProfessionalRenderer(new DarkMenuColors())
    {
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Fg : Muted;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Fg;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var r = e.ImageRectangle;
            var g = e.Graphics;
            using (var back = new SolidBrush(Select)) g.FillRectangle(back, r);
            var smoothing = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(Fg, Math.Max(1.6f, r.Height / 9f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                g.DrawLines(pen, [
                    new PointF(r.Left + r.Width * .24f, r.Top + r.Height * .52f),
                    new PointF(r.Left + r.Width * .43f, r.Top + r.Height * .70f),
                    new PointF(r.Left + r.Width * .77f, r.Top + r.Height * .31f)]);
            g.SmoothingMode = smoothing;
        }
    }

    private sealed class DarkMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Panel;
        public override Color ImageMarginGradientBegin => Panel;
        public override Color ImageMarginGradientMiddle => Panel;
        public override Color ImageMarginGradientEnd => Panel;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Border;
        public override Color MenuItemSelected => Border;
        public override Color MenuItemSelectedGradientBegin => Border;
        public override Color MenuItemSelectedGradientEnd => Border;
        public override Color MenuItemPressedGradientBegin => Panel;
        public override Color MenuItemPressedGradientMiddle => Panel;
        public override Color MenuItemPressedGradientEnd => Panel;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Panel;
        public override Color CheckBackground => Select;
        public override Color CheckSelectedBackground => Select;
        public override Color CheckPressedBackground => Select;
    }
}

/// <summary>
/// A TabControl that, when dark, draws its own tab strip (Windows draws it light whatever the colours): tabs in the
/// panel colour, the hovered one lighter, the selected one in the window's colour and open into its page, like the
/// notebook tabs in Wabbajack Wizard.
/// </summary>
internal sealed class ThemedTabControl : TabControl
{
    private int _hot = -1;

    public ThemedTabControl()
    {
        if (Theme.Dark)
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (!Theme.Dark) { base.OnPaint(e); return; }
        var g = e.Graphics;
        g.Clear(Theme.Bg);
        using var line = new Pen(Theme.Border);
        var page = DisplayRectangle;
        g.DrawRectangle(line, page.X - 1, page.Y - 1, page.Width + 1, page.Height + 1);
        for (int i = 0; i < TabCount; i++)
        {
            var r = GetTabRect(i);
            bool selected = i == SelectedIndex;
            if (selected) r = Rectangle.FromLTRB(r.Left, r.Top - 2, r.Right, page.Top); // a little taller, open into the page
            using (var fill = new SolidBrush(selected ? Theme.Bg : i == _hot ? Theme.Border : Theme.Panel))
                g.FillRectangle(fill, r);
            g.DrawLine(line, r.Left, r.Bottom - 1, r.Left, r.Top);
            g.DrawLine(line, r.Left, r.Top, r.Right - 1, r.Top);
            g.DrawLine(line, r.Right - 1, r.Top, r.Right - 1, r.Bottom - 1);
            if (selected) // no border between the selected tab and its page
                using (var gap = new Pen(Theme.Bg)) g.DrawLine(gap, r.Left + 1, page.Top - 1, r.Right - 2, page.Top - 1);
            TextRenderer.DrawText(g, TabPages[i].Text, Font, r, Theme.Fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hot = -1;
        for (int i = 0; i < TabCount; i++) if (GetTabRect(i).Contains(e.Location)) { hot = i; break; }
        if (hot != _hot) { _hot = hot; if (Theme.Dark) Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hot != -1) { _hot = -1; if (Theme.Dark) Invalidate(); }
    }
}
