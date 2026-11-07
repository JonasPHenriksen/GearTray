using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace GearTray.UI;

/// <summary>
/// GearTray's logo: a white cog on a blue-to-violet rounded square, the shape of the tray's battery badges. Drawn for
/// each size rather than scaled, so small sizes stay crisp (six chunkier teeth below 24 px). GearTray.ico, the exe's
/// icon, is written from this with --write-icon; the windows and the tray draw it directly.
/// </summary>
public static class Logo
{
    /// <summary>The sizes in GearTray.ico: Windows picks from these for every DPI and view.</summary>
    public static readonly int[] IcoSizes = [16, 20, 24, 32, 40, 48, 64, 256];

    private static readonly Color From = Color.FromArgb(0x3B, 0x82, 0xF6), To = Color.FromArgb(0x8B, 0x5C, 0xF6);
    private static readonly Lazy<byte[]> IcoBytes = new(Ico);

    /// <summary>A new icon with every size, for a window's title bar and taskbar button (Windows picks the size).</summary>
    public static Icon CreateIcon() => new(new MemoryStream(IcoBytes.Value));

    public static Bitmap Render(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        Draw(g, size);
        return bmp;
    }

    public static void Draw(Graphics g, int size)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        float inset = size >= 48 ? size * 0.04f : 0;
        var plate = new RectangleF(inset, inset, size - 2 * inset, size - 2 * inset);
        using (var path = IconRenderer.RoundedRect(plate, plate.Width * 0.22f))
        using (var brush = new LinearGradientBrush(plate, From, To, 45f))
            g.FillPath(brush, path);

        bool small = size < 24;
        float outer = plate.Width * (small ? 0.40f : 0.36f);
        using var cog = Cog(size / 2f, size / 2f, outer, outer * (small ? 0.72f : 0.77f), outer * (small ? 0.34f : 0.36f), small ? 6 : 8, small);
        if (size >= 48)
        {
            // a soft shadow under the cog, a little lower
            using var shadow = (GraphicsPath)cog.Clone();
            using (var down = new Matrix()) { down.Translate(0, size * 0.02f); shadow.Transform(down); }
            using var shade = new SolidBrush(Color.FromArgb(70, 30, 20, 80));
            g.FillPath(shade, shadow);
        }
        g.FillPath(Brushes.White, cog);
    }

    /// <summary>A cog: trapezoid teeth on a root circle, with a round hole (alternate fill leaves it open).</summary>
    private static GraphicsPath Cog(float cx, float cy, float outer, float root, float hole, int teeth, bool chunky)
    {
        double pitch = 2 * Math.PI / teeth;
        double halfRoot = pitch * (chunky ? 0.30 : 0.28), halfTip = pitch * (chunky ? 0.20 : 0.17);
        PointF At(double r, double a) => new((float)(cx + r * Math.Cos(a)), (float)(cy + r * Math.Sin(a)));
        var points = new List<PointF>();
        for (int i = 0; i < teeth; i++)
        {
            double a = -Math.PI / 2 + i * pitch; // a tooth straight up
            points.Add(At(root, a - halfRoot));
            points.Add(At(outer, a - halfTip));
            points.Add(At(outer, a));
            points.Add(At(outer, a + halfTip));
            points.Add(At(root, a + halfRoot));
            double from = a + halfRoot, to = a + pitch - halfRoot;
            for (int s = 1; s < 4; s++) points.Add(At(root, from + (to - from) * s / 4)); // the root up to the next tooth
        }
        var path = new GraphicsPath(FillMode.Alternate);
        path.AddPolygon(points.ToArray());
        path.AddEllipse(cx - hole, cy - hole, hole * 2, hole * 2);
        return path;
    }

    /// <summary>
    /// GearTray.ico's bytes: 32-bit DIBs below 256 px, which every icon reader understands (.NET Framework's can't
    /// draw PNG frames), and a PNG for 256 px, where a DIB would be 260 KB.
    /// </summary>
    public static byte[] Ico()
    {
        var images = IcoSizes.Select(s => (Size: s, Data: s >= 256 ? Png(s) : Dib(s))).ToList();
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)images.Count); // ICONDIR: reserved, type icon, count
        int offset = 6 + 16 * images.Count;
        foreach (var (size, data) in images)
        {
            byte side = (byte)(size >= 256 ? 0 : size); // 0 means 256
            w.Write(side); w.Write(side); w.Write((byte)0); w.Write((byte)0); // width, height, colours, reserved
            w.Write((ushort)1); w.Write((ushort)32); w.Write(data.Length); w.Write(offset);
            offset += data.Length;
        }
        foreach (var (_, data) in images) w.Write(data);
        return ms.ToArray();
    }

    private static byte[] Png(int size)
    {
        using var bmp = Render(size);
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    /// <summary>BITMAPINFOHEADER, bottom-up BGRA rows with straight alpha, then the 1-bit mask (set where transparent).</summary>
    private static byte[] Dib(int size)
    {
        using var bmp = Render(size);
        var bits = bmp.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int maskStride = (size + 31) / 32 * 4;
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(40); w.Write(size); w.Write(size * 2); // header size, width, height of colour + mask
            w.Write((ushort)1); w.Write((ushort)32); w.Write(0); w.Write(size * size * 4 + maskStride * size);
            w.Write(0); w.Write(0); w.Write(0); w.Write(0);
            var row = new byte[size * 4];
            var mask = new byte[maskStride * size];
            for (int y = size - 1, line = 0; y >= 0; y--, line++)
            {
                Marshal.Copy(bits.Scan0 + y * bits.Stride, row, 0, row.Length);
                w.Write(row);
                for (int x = 0; x < size; x++)
                    if (row[x * 4 + 3] == 0) mask[line * maskStride + x / 8] |= (byte)(0x80 >> (x % 8));
            }
            w.Write(mask);
            return ms.ToArray();
        }
        finally { bmp.UnlockBits(bits); }
    }

    /// <summary>
    /// Every icon size at 1x on light and dark, the small ones enlarged, and every size read back from the .ico and
    /// drawn by Windows (DrawIconEx), to check the drawing and the file.
    /// </summary>
    public static Bitmap Preview()
    {
        const int gap = 16, zoom = 6;
        int[] small = [16, 20, 24, 32];
        int rowH = IcoSizes.Max() + 2 * gap, zoomH = small.Max() * zoom + 2 * gap;
        int width = Math.Max(IcoSizes.Sum() + gap * (IcoSizes.Length + 1), small.Sum() * zoom + gap * (small.Length + 1));
        var sheet = new Bitmap(width, rowH * 3 + zoomH);
        using var g = Graphics.FromImage(sheet);
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        void Row(int top, int height, Color background, int[] sizes, int scale)
        {
            using (var b = new SolidBrush(background)) g.FillRectangle(b, 0, top, width, height);
            int x = gap;
            foreach (int s in sizes)
            {
                using var img = Render(s);
                int w = s * scale;
                g.DrawImage(img, new Rectangle(x, top + height - gap - w, w, w));
                x += w + gap;
            }
        }
        Row(0, rowH, Color.FromArgb(243, 243, 243), IcoSizes, 1);
        Row(rowH, rowH, Color.FromArgb(32, 32, 32), IcoSizes, 1);
        Row(rowH * 2, zoomH, Color.FromArgb(243, 243, 243), small, zoom);

        // The .ico as a window gets it: each size picked out of the file by .NET and drawn by Windows. Not 256 px:
        // .NET's Icon doesn't pick that frame (it stretches 64 px), and only Windows' shell, reading the exe, uses it.
        int top = rowH * 2 + zoomH, x0 = gap;
        using (var b = new SolidBrush(Color.FromArgb(32, 32, 32))) g.FillRectangle(b, 0, top, width, rowH);
        using var all = CreateIcon();
        foreach (int s in IcoSizes.Where(s => s < 256))
        {
            using var one = new Icon(all, s, s);
            g.DrawIcon(one, new Rectangle(x0, top + rowH - gap - s, s, s));
            x0 += s + gap;
        }
        return sheet;
    }
}
