using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace GearTray.UI;

/// <summary>Draws the tray icon: the lowest battery % on a colored badge, or GearTray's logo when unknown.</summary>
public static class IconRenderer
{
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);

    public static Icon Render(int? percent, bool charging)
    {
        // Badges are drawn large and scaled down by Windows; the logo at the tray's own size, which its small-size cog
        // (fewer, chunkier teeth) is made for.
        int trayIcon = Math.Max(16, SystemInformation.SmallIconSize.Width);
        int size = percent is null ? trayIcon : trayIcon * 2;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            if (percent is not int pct) Logo.Draw(g, size);
            else
            {
                Color bg = pct switch
                {
                    < 20 => Color.FromArgb(215, 50, 50),
                    < 50 => Color.FromArgb(225, 150, 20),
                    _ => Color.FromArgb(40, 160, 80),
                };
                using (var path = RoundedRect(new RectangleF(0, 0, size, size), size * 0.22f))
                using (var brush = new SolidBrush(bg))
                    g.FillPath(brush, path);

                string text = pct >= 100 ? "100" : pct.ToString();
                float fontSize = text.Length >= 3 ? size * 0.36f : size * 0.52f;
                using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
                using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(text, font, Brushes.White, new RectangleF(0, size * 0.03f, size, size), sf);
            }

            if (charging)
            {
                // small lightning mark in the top-right corner
                var pts = new[] { new PointF(size * .80f, size * .04f), new PointF(size * .66f, size * .28f), new PointF(size * .78f, size * .28f),
                                  new PointF(size * .70f, size * .48f), new PointF(size * .94f, size * .20f), new PointF(size * .82f, size * .20f) };
                g.FillPolygon(Brushes.Yellow, pts);
            }
        }
        IntPtr h = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(h).Clone(); }
        finally { DestroyIcon(h); }
    }

    internal static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
