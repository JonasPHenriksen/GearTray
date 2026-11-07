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

    /// <summary>Text in one font, drawn after a gap; the pieces of a line share one baseline.</summary>
    private readonly record struct Piece(Font Font, string Text, float Gap = 0);

    /// <summary>
    /// The home page: a clock with seconds; the date, short with "Server: On/Off" beside it (each centred in its half,
    /// like the speeds below) or in full when no server is set; download and upload speed; and the headset row: a volume
    /// bar with its percentage and a live sound meter beside it, or "Headset off" with a crossed-out headset. The clock
    /// is 24 px and everything else 11 px semibold, on baselines 14 px apart.
    /// </summary>
    public static bool[] RenderHome(HomeData h, DateTime now) => Draw(now, g =>
    {
        using var text = new Font("Segoe UI Semibold", 11f, GraphicsUnit.Pixel);
        using var icons = new Font("Segoe MDL2 Assets", 12f, GraphicsUnit.Pixel);
        using var clock = new Font("Segoe UI Semibold", 24f, GraphicsUnit.Pixel);
        var format = StringFormat.GenericTypographic;
        const float space = 3; // between a number and its unit, and after "Server:"

        // Baselines: the clock's, then three rows 14 pixels apart.
        const float clockBase = 19, row2 = 31, row3 = 45, row4 = 59;
        // The clock, centred; its digits sit in even cells, so the ticking seconds don't make it shift sideways.
        string time = now.ToString("HH:mm:ss");
        Tabular(g, clock, time, (W - Tabular(g, clock, time)) / 2, clockBase - Ascent(clock));

        if (h.Server == ServerState.NotSet)
        {
            string date = now.ToString("dddd d MMMM");
            if (Pieces(g, 0, 0, false, new Piece(text, date)) > W - 4) date = now.ToString("dddd d MMM"); // "Wednesday 30 Sep"
            Centred(g, 0, W, row2, new Piece(text, date));
        }
        else
        {
            Centred(g, 0, W / 2f, row2, new Piece(text, now.ToString("ddd d MMM")));
            string state = h.Server switch { ServerState.On => "On", ServerState.Off => "Off", _ => "…" };
            Centred(g, W / 2f, W / 2f, row2, new Piece(text, "Server:"), new Piece(text, state, space));
        }

        // Download centred in the left half, upload in the right half.
        NetHalf(g, icons, text, space, IconDown, h.DownMBps, 0, row3);
        NetHalf(g, icons, text, space, IconUp, h.UpMBps, W / 2f, row3);

        // The headset row; the volume bar and the meter are as tall as the capitals.
        float iconTop = IconTop(row4, text);
        g.DrawString(IconHeadset, icons, Brushes.White, 0, iconTop, format);
        if (!h.HeadsetOn)
        {
            g.DrawLine(Pens.White, 1, iconTop + 11, 11, iconTop + 1);
            Pieces(g, 16, row4, true, new Piece(text, "Headset off"));
            return;
        }
        const float barX = 16, barW = 30, barH = 8, barTop = row4 - barH;
        g.DrawRectangle(Pens.White, barX, barTop, barW, barH - 1);
        if (h.Volume is int vol && vol > 0)
            g.FillRectangle(Brushes.White, barX + 1, barTop + 1, (barW - 1) * Math.Clamp(vol, 0, 100) / 100f, barH - 2);
        const float percentX = barX + barW + 3;
        if (h.Volume is int v) Pieces(g, percentX, row4, true, new Piece(text, v.ToString()), new Piece(text, "%"));
        else Pieces(g, percentX, row4, true, new Piece(text, "–"));
        // The meter starts after room for "100%", so the widest value never runs into it, and ends 2 px in like the text.
        float meterX = percentX + Pieces(g, 0, 0, false, new Piece(text, "100"), new Piece(text, "%")) + 3;
        SoundMeter(g, meterX, barTop, W - 2 - meterX, barH, h.SoundLevel);
    });

    /// <summary>Draws the pieces left to right on one baseline from x (or, with draw false, only measures them); returns the width.</summary>
    private static float Pieces(Graphics g, float x, float baseline, bool draw, params Piece[] pieces)
    {
        var format = StringFormat.GenericTypographic;
        float at = x;
        foreach (var p in pieces)
        {
            at += p.Gap;
            if (draw) g.DrawString(p.Text, p.Font, Brushes.White, at, baseline - Ascent(p.Font), format);
            at += g.MeasureString(p.Text, p.Font, PointF.Empty, format).Width;
        }
        return at - x;
    }

    /// <summary>The pieces centred together in the span from left, on the baseline.</summary>
    private static void Centred(Graphics g, float left, float width, float baseline, params Piece[] pieces) =>
        Pieces(g, left + (width - Pieces(g, 0, 0, false, pieces)) / 2, baseline, true, pieces);

    /// <summary>Where a 12 px icon's top goes so it's centred on the capitals of text in this font on this baseline.</summary>
    private static float IconTop(float baseline, Font font) => baseline - 0.35f * font.Size - 6;

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

    /// <summary>An arrow and the speed with its unit, centred together in the half that starts at left.</summary>
    private static void NetHalf(Graphics g, Font icons, Font text, float space, string arrow, double? mbps, float left, float baseline)
    {
        // The arrow's ink is 7 px wide inside its 12 px box (2.5 px empty on its left); count the ink and a 3 px gap.
        const float arrowLead = 2.5f, arrowWidth = 7 + 3;
        var pieces = new[] { new Piece(text, Speed(mbps)), new Piece(text, "MB/s", space) };
        float x = left + (W / 2f - arrowWidth - Pieces(g, 0, 0, false, pieces)) / 2;
        g.DrawString(arrow, icons, Brushes.White, x - arrowLead, IconTop(baseline, text), StringFormat.GenericTypographic);
        Pieces(g, x + arrowWidth, baseline, true, pieces);
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
