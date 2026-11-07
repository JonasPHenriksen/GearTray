using System.Drawing.Drawing2D;
using GearTray.Devices;

namespace GearTray.UI;

/// <summary>Live drawing of an Xbox controller: sticks, triggers, bumpers, d-pad and buttons light up as they're used.</summary>
internal sealed class ControllerView : Control
{
    private PadState? _state;

    public ControllerView()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Size = new Size(540, 270);
        BackColor = Theme.Page;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public PadState? State
    {
        get => _state;
        set { _state = value; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        using var outline = new Pen(Theme.Line, 1.5f);
        using var text = new SolidBrush(Theme.Text);
        using var small = new Font(Font.FontFamily, 8f);
        var s = _state;
        var b = s?.Buttons ?? PadButtons.None;
        bool On(PadButtons x) => (b & x) != 0;

        if (s is null)
        {
            g.DrawString("No controller connected", Font, text, 20, 120);
            return;
        }

        // body
        using (var body = new GraphicsPath())
        {
            body.AddArc(60, 50, 160, 170, 100, 170);
            body.AddLine(150, 52, 390, 52);
            body.AddArc(320, 50, 160, 170, 270, 170);
            body.AddLine(390, 210, 150, 210);
            g.DrawPath(outline, body);
        }

        // triggers and bumpers
        Bar(g, 70, 6, 130, 14, s.Value.LeftTrigger, "LT");
        Bar(g, 340, 6, 130, 14, s.Value.RightTrigger, "RT");
        Pill(g, 70, 26, 130, 18, On(PadButtons.LeftShoulder), "LB", Color.SteelBlue);
        Pill(g, 340, 26, 130, 18, On(PadButtons.RightShoulder), "RB", Color.SteelBlue);

        // sticks
        Stick(g, 135, 110, s.Value.LeftX, s.Value.LeftY, On(PadButtons.LeftThumb));
        Stick(g, 335, 170, s.Value.RightX, s.Value.RightY, On(PadButtons.RightThumb));

        // d-pad
        Pill(g, 192, 140, 22, 22, On(PadButtons.DpadUp), "▲", Color.SteelBlue);
        Pill(g, 192, 186, 22, 22, On(PadButtons.DpadDown), "▼", Color.SteelBlue);
        Pill(g, 169, 163, 22, 22, On(PadButtons.DpadLeft), "◀", Color.SteelBlue);
        Pill(g, 215, 163, 22, 22, On(PadButtons.DpadRight), "▶", Color.SteelBlue);

        // center buttons
        Round(g, 232, 104, 20, On(PadButtons.Back), "", Color.SteelBlue);
        Round(g, 258, 80, 26, On(PadButtons.Guide), "", Color.LimeGreen);
        Round(g, 290, 104, 20, On(PadButtons.Start), "", Color.SteelBlue);
        g.DrawString("view", small, text, 226, 126);
        g.DrawString("menu", small, text, 285, 126);

        // face buttons
        Round(g, 400, 72, 26, On(PadButtons.Y), "Y", Color.Goldenrod);
        Round(g, 372, 100, 26, On(PadButtons.X), "X", Color.RoyalBlue);
        Round(g, 428, 100, 26, On(PadButtons.B), "B", Color.Firebrick);
        Round(g, 400, 128, 26, On(PadButtons.A), "A", Color.ForestGreen);

        string values = $"Left stick {s.Value.LeftX:+0.00;-0.00} {s.Value.LeftY:+0.00;-0.00}   Right stick {s.Value.RightX:+0.00;-0.00} {s.Value.RightY:+0.00;-0.00}   " +
                        $"LT {s.Value.LeftTrigger:P0}  RT {s.Value.RightTrigger:P0}";
        g.DrawString(values, small, text, 20, 238);
    }

    private void Bar(Graphics g, int x, int y, int w, int h, float value, string label)
    {
        using var border = new Pen(Theme.Line);
        using var fill = new SolidBrush(Color.FromArgb(220, 60, 140, 220));
        g.FillRectangle(fill, x, y, w * Math.Clamp(value, 0, 1), h);
        g.DrawRectangle(border, x, y, w, h);
        using var f = new Font(Font.FontFamily, 7.5f);
        using var text = new SolidBrush(Theme.Text);
        g.DrawString(label, f, text, x + 4, y + 1);
    }

    private void Pill(Graphics g, int x, int y, int w, int h, bool on, string label, Color color)
    {
        using var border = new Pen(Theme.Line);
        if (on) using (var fill = new SolidBrush(color)) g.FillRectangle(fill, x, y, w, h);
        g.DrawRectangle(border, x, y, w, h);
        using var f = new Font(Font.FontFamily, 7.5f);
        var size = g.MeasureString(label, f);
        using var text = new SolidBrush(on ? Color.White : Theme.Text);
        g.DrawString(label, f, text, x + (w - size.Width) / 2, y + (h - size.Height) / 2);
    }

    private void Round(Graphics g, int x, int y, int d, bool on, string label, Color color)
    {
        using var border = new Pen(color, 2f);
        if (on) using (var fill = new SolidBrush(color)) g.FillEllipse(fill, x, y, d, d);
        g.DrawEllipse(border, x, y, d, d);
        if (label.Length > 0)
        {
            using var f = new Font(Font.FontFamily, 9f, FontStyle.Bold);
            var size = g.MeasureString(label, f);
            using var brush = new SolidBrush(on ? Color.White : color);
            g.DrawString(label, f, brush, x + (d - size.Width) / 2 + 1, y + (d - size.Height) / 2 + 1);
        }
    }

    private void Stick(Graphics g, int cx, int cy, float x, float y, bool pressed)
    {
        const int r = 34;
        using var ring = new Pen(Theme.Line, 1.5f);
        using var dead = new Pen(Color.FromArgb(90, Color.Gray)) { DashStyle = DashStyle.Dot };
        if (pressed) using (var f = new SolidBrush(Color.FromArgb(70, Color.SteelBlue))) g.FillEllipse(f, cx - r, cy - r, r * 2, r * 2);
        g.DrawEllipse(ring, cx - r, cy - r, r * 2, r * 2);
        int dz = (int)(r * 0.24f); // XInput's suggested left-stick dead zone (7849 / 32767)
        g.DrawEllipse(dead, cx - dz, cy - dz, dz * 2, dz * 2);
        float px = cx + x * r, py = cy - y * r;
        using var dot = new SolidBrush(Color.SteelBlue);
        g.FillEllipse(dot, px - 6, py - 6, 12, 12);
    }
}
