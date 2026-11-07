using System.Drawing.Drawing2D;
using GearTray.Devices;
using GearTray.Util;

namespace GearTray.UI;

/// <summary>
/// The Huntsman Elite's LED grid as a paintable picture: keys (with their names from the Windows keyboard layout) plus the
/// underglow and wrist-rest strips. Left-click/drag paints, right-click picks a key's color.
/// </summary>
internal sealed class KeyboardPainter : Control
{
    private const int Cell = 27, Gap = 2, X0 = 4, Y0 = 4, GlowH = 12;
    private readonly Color[,] _colors = new Color[RazerHuntsmanElite.MatrixRows, RazerHuntsmanElite.MatrixCols];
    private List<KeyCell> _cells = HuntsmanLayout.For(iso: true).ToList();
    private bool _painting;

    /// <summary>Color used when painting.</summary>

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]

    public Color Brush { get; set; } = Color.Red;
    /// <summary>A key's color was picked with right-click.</summary>
    public event Action<Color>? ColorPicked;
    /// <summary>A paint stroke finished (mouse released).</summary>
    public event Action? Painted;

    public KeyboardPainter()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Size = new Size(X0 * 2 + 22 * (Cell + Gap), Y0 + 6 * (Cell + Gap) + 3 * (GlowH + 16) + 8);
        BackColor = Color.FromArgb(28, 28, 30);
        Cursor = Cursors.Hand;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Iso
    {
        set { _cells = HuntsmanLayout.For(value).ToList(); Invalidate(); }
    }

    public void SetColors(IReadOnlyList<string> hex)
    {
        for (int r = 0; r < RazerHuntsmanElite.MatrixRows; r++)
            for (int c = 0; c < RazerHuntsmanElite.MatrixCols; c++)
            {
                int i = r * RazerHuntsmanElite.MatrixCols + c;
                _colors[r, c] = i < hex.Count ? ColorUtil.Parse("#" + hex[i].TrimStart('#')) : Color.Black;
            }
        Invalidate();
    }

    public List<string> GetColors()
    {
        var list = new List<string>(RazerHuntsmanElite.MatrixRows * RazerHuntsmanElite.MatrixCols);
        for (int r = 0; r < RazerHuntsmanElite.MatrixRows; r++)
            for (int c = 0; c < RazerHuntsmanElite.MatrixCols; c++)
                list.Add(ColorUtil.ToHex(_colors[r, c]).TrimStart('#'));
        return list;
    }

    /// <summary>The picture as frame bytes for the keyboard (rows × cols × RGB).</summary>
    public byte[] Frame()
    {
        var f = new byte[RazerHuntsmanElite.MatrixRows * RazerHuntsmanElite.MatrixCols * 3];
        for (int r = 0; r < RazerHuntsmanElite.MatrixRows; r++)
            for (int c = 0; c < RazerHuntsmanElite.MatrixCols; c++)
            {
                int o = (r * RazerHuntsmanElite.MatrixCols + c) * 3;
                f[o] = _colors[r, c].R; f[o + 1] = _colors[r, c].G; f[o + 2] = _colors[r, c].B;
            }
        return f;
    }

    public void Fill(Color color, bool keys = true, bool underglow = true)
    {
        foreach (var k in _cells)
            if (k.IsUnderglow ? underglow : keys) _colors[k.Row, k.Col] = color;
        Invalidate();
    }

    private Rectangle CellRect(KeyCell k)
    {
        if (!k.IsUnderglow)
            return new Rectangle(X0 + k.Col * (Cell + Gap), Y0 + k.Row * (Cell + Gap), Cell, Cell);
        int stripTop = Y0 + 6 * (Cell + Gap) + 14 + (k.Row - 6) * (GlowH + 16);
        int w = (22 * (Cell + Gap)) / (k.Row == 8 ? 20 : 19);
        return new Rectangle(X0 + k.Col * w, stripTop, w - Gap, GlowH);
    }

    private KeyCell? Hit(Point p) => _cells.FirstOrDefault(k => CellRect(k).Contains(p));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        using var small = new Font(Font.FontFamily, 7f);
        using var caption = new Font(Font.FontFamily, 7.5f);
        string[] strips = ["Underglow: top edge, right side", "Underglow: left side, front edge", "Wrist rest (when attached)"];
        for (int i = 0; i < 3; i++)
            g.DrawString(strips[i], caption, Brushes.Gray, X0, Y0 + 6 * (Cell + Gap) + 1 + i * (GlowH + 16));

        foreach (var k in _cells)
        {
            var rect = CellRect(k);
            var c = _colors[k.Row, k.Col];
            using (var fill = new SolidBrush(c)) g.FillRectangle(fill, rect);
            using (var border = new Pen(Color.FromArgb(90, 90, 95))) g.DrawRectangle(border, rect);
            if (k.IsUnderglow) continue;
            string label = HuntsmanLayout.Label(k);
            bool dark = c.GetBrightness() > 0.55f;
            var size = g.MeasureString(label, small);
            using var text = new SolidBrush(dark ? Color.Black : Color.Gainsboro);
            g.DrawString(label, small, text, rect.X + (rect.Width - size.Width) / 2, rect.Y + (rect.Height - size.Height) / 2);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        var k = Hit(e.Location);
        if (k is null) return;
        if (e.Button == MouseButtons.Right) { ColorPicked?.Invoke(_colors[k.Row, k.Col]); return; }
        _painting = true;
        PaintCell(k);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_painting && Hit(e.Location) is { } k) PaintCell(k);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (!_painting) return;
        _painting = false;
        Painted?.Invoke();
    }

    private void PaintCell(KeyCell k)
    {
        if (_colors[k.Row, k.Col].ToArgb() == Brush.ToArgb()) return;
        _colors[k.Row, k.Col] = ColorUtil.Plain(Brush);
        Invalidate(CellRect(k));
    }
}
