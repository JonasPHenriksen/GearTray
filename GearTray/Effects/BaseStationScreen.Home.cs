namespace GearTray.Effects;

/// <summary>Whether the server set to ping answers. NotSet: no server set; Checking: no answer yet.</summary>
public enum ServerState { NotSet, Checking, On, Off }

/// <summary>What the home page shows besides the time and date. SoundLevel is 0–1 (left and right combined).</summary>
public sealed record HomeData(bool HeadsetOn, int? Volume, double? DownMBps, double? UpMBps, float SoundLevel,
    ServerState Server = ServerState.NotSet);

public sealed partial class BaseStationScreen
{
    // Segoe MDL2 Assets glyphs (part of Windows 10 and 11).
    private const string IconHeadset = "", IconDown = "", IconUp = "";

    /// <summary>
    /// The home page: a big clock with seconds; the date, short with "Server: On/Off" beside it (each centred in its half,
    /// like the speeds below) or in full when no server is set; download and upload speed; and the headset row: a short
    /// volume bar with its percentage and a live sound meter beside it, or "Headset off" with a crossed-out headset.
    /// </summary>
    public static bool[] RenderHome(HomeData h, DateTime now) => Draw(now, g =>
    {
        using var small = new Font("Segoe UI", 10f, GraphicsUnit.Pixel);
        using var icons = new Font("Segoe MDL2 Assets", 11f, GraphicsUnit.Pixel);
        using var clock = new Font("Segoe UI Semibold", 28f, GraphicsUnit.Pixel);
        using var centre = new StringFormat { Alignment = StringAlignment.Center };

        // The clock, centred; its digits sit in even cells, so the ticking seconds don't make it shift sideways.
        string time = now.ToString("HH:mm:ss");
        Tabular(g, clock, time, (W - Tabular(g, clock, time)) / 2, -7);

        string? server = h.Server switch
        {
            ServerState.On => "Server: On",
            ServerState.Off => "Server: Off",
            ServerState.Checking => "Server: …",
            _ => null,
        };
        if (server is null)
            g.DrawString(now.ToString("dddd d MMMM"), small, Brushes.White, new RectangleF(0, 24, W, 13), centre);
        else
        {
            g.DrawString(now.ToString("ddd d MMM"), small, Brushes.White, new RectangleF(0, 24, W / 2f, 13), centre);
            g.DrawString(server, small, Brushes.White, new RectangleF(W / 2f, 24, W / 2f, 13), centre);
        }

        // Download centred in the left half, upload in the right half.
        NetHalf(g, small, icons, IconDown, h.DownMBps, 0, 38);
        NetHalf(g, small, icons, IconUp, h.UpMBps, W / 2f, 38);

        const float y2 = 51;
        g.DrawString(IconHeadset, icons, Brushes.White, 0, y2 + 2);
        if (!h.HeadsetOn)
        {
            g.DrawLine(Pens.White, 1, y2 + 13, 11, y2 + 3);
            g.DrawString("Headset off", small, Brushes.White, 15, y2);
            return;
        }
        const float barX = 15, barW = 34;
        g.DrawRectangle(Pens.White, barX, y2 + 3, barW, 6);
        if (h.Volume is int vol && vol > 0) g.FillRectangle(Brushes.White, barX + 1, y2 + 4, (barW - 1) * Math.Clamp(vol, 0, 100) / 100f, 5);
        g.DrawString(h.Volume is int v ? $"{v}%" : "–", small, Brushes.White, barX + barW + 2, y2);
        // The meter starts after room for "100%", so the widest value never runs into it.
        float meterX = barX + barW + 2 + g.MeasureString("100%", small).Width;
        SoundMeter(g, meterX, y2 + 3, W - meterX, 7, h.SoundLevel);
    });

    /// <summary>
    /// Text with every digit in a cell as wide as the widest digit (Segoe UI's "1" is narrower), so a ticking clock
    /// keeps its place. Draws at (x, y) when x is given; returns the width either way.
    /// </summary>
    private static float Tabular(Graphics g, Font font, string text, float? x = null, float y = 0)
    {
        var format = StringFormat.GenericTypographic;
        float cell = 0;
        for (char c = '0'; c <= '9'; c++) cell = Math.Max(cell, g.MeasureString(c.ToString(), font, PointF.Empty, format).Width);
        float at = x ?? 0;
        foreach (char c in text)
        {
            string s = c.ToString();
            float width = g.MeasureString(s, font, PointF.Empty, format).Width;
            float slot = char.IsAsciiDigit(c) ? cell : width;
            if (x is not null) g.DrawString(s, font, Brushes.White, at + (slot - width) / 2, y, format);
            at += slot;
        }
        return at - (x ?? 0);
    }

    private static string Speed(double? mbps) => mbps is double v ? (v >= 10 ? $"{v:0}" : $"{v:0.0}") : "–";

    /// <summary>An arrow and "12 MB/s", centred together in the half that starts at x.</summary>
    private static void NetHalf(Graphics g, Font small, Font icons, string arrow, double? mbps, float x, float y)
    {
        string text = $"{Speed(mbps)} MB/s";
        const float arrowWidth = 11;
        float width = arrowWidth + g.MeasureString(text, small).Width;
        float start = x + (W / 2f - width) / 2;
        g.DrawString(arrow, icons, Brushes.White, start, y + 2);
        g.DrawString(text, small, Brushes.White, start + arrowWidth, y);
    }

    /// <summary>Segments that light up with the sound level (0–1), with a faint track where they're dark.</summary>
    private static void SoundMeter(Graphics g, float x, float y, float width, float height, float level)
    {
        int segments = (int)(width / 5);
        int lit = (int)Math.Round(Math.Clamp(level, 0, 1) * segments);
        for (int s = 0; s < segments; s++)
        {
            float sx = x + s * 5;
            if (s < lit) g.FillRectangle(Brushes.White, sx, y, 4, height);
            else g.FillRectangle(Brushes.White, sx + 1, y + height / 2, 2, 1);
        }
    }
}
