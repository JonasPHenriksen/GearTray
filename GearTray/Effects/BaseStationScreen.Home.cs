using GearTray.Devices;

namespace GearTray.Effects;

/// <summary>Whether the server set to ping answers. NotSet: no server set; Checking: no answer yet.</summary>
public enum ServerState { NotSet, Checking, On, Off }

/// <summary>
/// What the home page shows besides the time and date. Volume is the base station's dial (0–100). With a weather
/// place set (WeatherSet), the weather goes at the end of the bottom row: Weather, or "…" until the first answer.
/// HeadsetBattery and MouseBattery are null while not known; the mouse is then left out.
/// </summary>
public sealed record HomeData(bool HeadsetOn, int? Volume, double? DownMBps, double? UpMBps,
    ServerState Server = ServerState.NotSet, bool WeatherSet = false, WeatherNow? Weather = null,
    BatteryInfo? HeadsetBattery = null, BatteryInfo? MouseBattery = null);

public sealed partial class BaseStationScreen
{
    // Segoe MDL2 Assets glyphs (part of Windows 10 and 11).
    private const string IconDown = "", IconUp = "";

    /// <summary>Text in one font, drawn after a gap; the pieces of a line share one baseline.</summary>
    private readonly record struct Piece(Font Font, string Text, float Gap = 0);

    /// <summary>
    /// The home page: a clock with seconds; the date, short with "Server: On/Off" beside it (each centred in its half,
    /// like the speeds below) or in full when no server is set; download and upload speed; and the bottom row: volume,
    /// headset battery, mouse battery and the weather (see BottomRow). The clock is 24 px and everything else 11 px
    /// semibold, on baselines 14 px apart.
    /// </summary>
    public static bool[] RenderHome(HomeData h, DateTime now) => Draw(now, g =>
    {
        using var text = new Font("Segoe UI Semibold", 11f, GraphicsUnit.Pixel);
        using var icons = new Font("Segoe MDL2 Assets", 12f, GraphicsUnit.Pixel);
        using var clock = new Font("Segoe UI Semibold", 24f, GraphicsUnit.Pixel);
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

        BottomRow(g, text, row4, h);
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

    // ---------- the bottom row: volume, headset and mouse battery, weather ----------

    // 9 px tall pictures drawn pixel by pixel ('#' lit), so they stay crisp; they stand on the text's baseline.
    private static readonly string[] SpeakerPixels =
    [
        "....#...",
        "...##...",
        "..###.#.",
        "#####..#",
        "#####..#",
        "#####..#",
        "..###.#.",
        "...##...",
        "....#...",
    ];
    private static readonly string[] HeadsetPixels =
    [
        "..#####..",
        ".#.....#.",
        "#.......#",
        "#.......#",
        "##.....##",
        "###...###",
        "###...###",
        "###...###",
        ".##...##.",
    ];
    private static readonly string[] MousePixels =
    [
        ".#####.",
        "#..#..#",
        "#..#..#",
        "#######",
        "#.....#",
        "#.....#",
        "#.....#",
        "#.....#",
        ".#####.",
    ];
    private static readonly string[] BoltPixels =
    [
        "...##",
        "..##.",
        ".##..",
        "#####",
        "..##.",
        ".##..",
        ".#...",
        "#....",
    ];

    /// <summary>One part of the bottom row: how wide it is, and how to draw it from a left edge.</summary>
    private readonly record struct Part(float Width, Action<int> Draw);

    /// <summary>
    /// The bottom row, spread over the width with equal gaps: a speaker and the volume, a headset and its battery level
    /// (or "off", then without the volume), a mouse and its battery level (left out while not known), each with a
    /// lightning bolt while charging, and the weather when a place is set. No "%" signs, so it all fits.
    /// </summary>
    private static void BottomRow(Graphics g, Font text, float baseline, HomeData h)
    {
        List<Part> Parts(bool shortTemperature, bool bolts)
        {
            var parts = new List<Part>();
            if (!h.HeadsetOn) parts.Add(IconText(g, text, baseline, HeadsetPixels, "off"));
            else
            {
                parts.Add(IconText(g, text, baseline, SpeakerPixels, h.Volume?.ToString() ?? "–"));
                parts.Add(Battery(h.HeadsetBattery, HeadsetPixels, bolts));
            }
            if (h.MouseBattery is not null) parts.Add(Battery(h.MouseBattery, MousePixels, bolts));
            if (h.WeatherSet) parts.Add(WeatherPart(g, text, baseline, h.Weather, shortTemperature));
            return parts;
        }
        Part Battery(BatteryInfo? b, string[] icon, bool bolt) =>
            IconText(g, text, baseline, icon, b?.Percent.ToString() ?? "–", bolt && b?.Charging == true);

        // When it gets tight (three 100s and a two-digit temperature): "12°" instead of "12°C", then narrower gaps,
        // then no charging bolt.
        const float left = 1, right = W - 2;
        List<Part> row = null!;
        float gap = 0;
        foreach (var (shortTemperature, bolts, minGap) in new[] { (false, true, 4f), (true, true, 4f), (true, true, 2f), (true, false, 2f) })
        {
            row = Parts(shortTemperature, bolts);
            gap = Gap(row);
            if (gap >= minGap) break;
        }
        gap = Math.Max(gap, 1);
        float x = left;
        foreach (var part in row)
        {
            part.Draw((int)Math.Round(x));
            x += part.Width + gap;
        }

        static float Gap(List<Part> parts) =>
            parts.Count < 2 ? 0 : (right - left - parts.Sum(p => p.Width)) / (parts.Count - 1);
    }

    /// <summary>A pixel picture, then the text 2 px after it, then a lightning bolt when charging.</summary>
    private static Part IconText(Graphics g, Font text, float baseline, string[] icon, string value, bool charging = false)
    {
        var piece = new Piece(text, value);
        int iconW = icon[0].Length;
        float textW = Pieces(g, 0, 0, false, piece);
        float width = iconW + 2 + textW + (charging ? 2 + BoltPixels[0].Length : 0);
        return new Part(width, x =>
        {
            PixelIcon(g, icon, x, baseline);
            Pieces(g, x + iconW + 2, baseline, true, piece);
            if (charging) PixelIcon(g, BoltPixels, (int)Math.Round(x + iconW + 2 + textW + 2), baseline);
        });
    }

    /// <summary>The weather picture and the temperature ("7°C", or "7°" when short), or "…" before the first answer.</summary>
    private static Part WeatherPart(Graphics g, Font text, float baseline, WeatherNow? w, bool shortTemperature)
    {
        if (w is null)
        {
            var waiting = new Piece(text, "…");
            return new Part(Pieces(g, 0, 0, false, waiting), x => Pieces(g, x, baseline, true, waiting));
        }
        // Whole degrees, never "-0"; a hyphen for minus (Segoe UI's minus sign leaves a gap before the digits).
        int degrees = (int)Math.Round(w.Temperature, MidpointRounding.AwayFromZero);
        var temperature = new Piece(text, degrees.ToString(System.Globalization.CultureInfo.InvariantCulture) + (shortTemperature ? "°" : "°C"));
        float textW = Pieces(g, 0, 0, false, temperature);
        return new Part(12 + 3 + textW, x =>
        {
            WeatherIcon(g, x, (int)Math.Round(baseline - 10), w.Code, w.IsDay);
            Pieces(g, x + 12 + 3, baseline, true, temperature);
        });
    }

    /// <summary>Draws a pixel picture with its left edge at x and its last row just above the baseline.</summary>
    private static void PixelIcon(Graphics g, string[] rows, int x, float baseline)
    {
        int top = (int)Math.Round(baseline) - rows.Length;
        for (int y = 0; y < rows.Length; y++)
            for (int px = 0; px < rows[y].Length; px++)
                if (rows[y][px] == '#') g.FillRectangle(Brushes.White, x + px, top + y, 1, 1);
    }
}
