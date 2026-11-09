namespace GearTray.Effects;

/// <summary>The weather outside now: temperature in °C, WMO weather code, and whether it's daytime.</summary>
public sealed record WeatherNow(double Temperature, int Code, bool IsDay);

public sealed partial class BaseStationScreen
{
    private enum Sky { Clear, PartlyCloudy, Cloudy, Fog, Drizzle, Rain, Snow, Thunder }

    /// <summary>WMO weather codes (as Open-Meteo gives them) to the eight pictures.</summary>
    private static Sky SkyFor(int code) => code switch
    {
        0 => Sky.Clear,
        1 or 2 => Sky.PartlyCloudy,
        45 or 48 => Sky.Fog,
        >= 51 and <= 57 => Sky.Drizzle,
        (>= 61 and <= 67) or (>= 80 and <= 82) => Sky.Rain,
        (>= 71 and <= 77) or 85 or 86 => Sky.Snow,
        >= 95 => Sky.Thunder,
        _ => Sky.Cloudy,
    };

    /// <summary>
    /// A 12×12 one-bit weather picture with its top left at (x, y): sun or moon, sun or moon behind a cloud, a cloud,
    /// fog, or a smaller cloud over drizzle, rain, snow or lightning. Built from circles and dots pixel by pixel, so it
    /// stays crisp.
    /// </summary>
    private static void WeatherIcon(Graphics g, int x, int y, int code, bool day)
    {
        var on = new bool[12, 12];
        void Set(int px, int py) { if (px is >= 0 and < 12 && py is >= 0 and < 12) on[px, py] = true; }
        static bool In(double px, double py, double cx, double cy, double r) => (px - cx) * (px - cx) + (py - cy) * (py - cy) <= r * r;

        // A cloud as three circles on a flat base; "high" is the smaller one above precipitation.
        bool Cloud(int px, int py, bool high) => high
            ? In(px, py, 3, 5, 2.3) || In(px, py, 6, 3.2, 3) || In(px, py, 9, 5, 2.2) || (px is >= 3 and <= 9 && py is >= 5 and <= 7)
            : In(px, py, 3, 7, 2.5) || In(px, py, 6.3, 4.8, 3.3) || In(px, py, 9.2, 7, 2.3) || (px is >= 3 and <= 9 && py is >= 7 and <= 9);
        void Sun(double cx, double cy, double r, double rays)
        {
            for (int py = 0; py < 12; py++) for (int px = 0; px < 12; px++) if (In(px, py, cx, cy, r)) Set(px, py);
            for (int i = 0; i < 8; i++)
            {
                double a = i * Math.PI / 4;
                Set((int)Math.Round(cx + rays * Math.Cos(a)), (int)Math.Round(cy + rays * Math.Sin(a)));
            }
        }
        void Moon(double cx, double cy, double r)
        {
            for (int py = 0; py < 12; py++)
                for (int px = 0; px < 12; px++)
                    if (In(px, py, cx, cy, r) && !In(px, py, cx + r * 0.55, cy - r * 0.45, r * 0.85)) Set(px, py);
        }

        var sky = SkyFor(code);
        switch (sky)
        {
            case Sky.Clear:
                if (day) Sun(5.5, 5.5, 2.6, 5); else Moon(5.5, 5.5, 5);
                break;
            case Sky.PartlyCloudy:
                if (day) Sun(4, 4, 1.9, 3.8); else Moon(4, 4, 3.6);
                // The cloud in front, with a dark rim so the two don't run together.
                for (int py = 0; py < 12; py++)
                    for (int px = 0; px < 12; px++)
                    {
                        bool cloud = In(px, py, 5.2, 8.6, 2.2) || In(px, py, 8.2, 6.8, 2.8) || In(px, py, 10.3, 8.8, 1.6) || (px is >= 5 and <= 11 && py is >= 8 and <= 10);
                        bool rim = !cloud && (In(px, py, 5.2, 8.6, 3.2) || In(px, py, 8.2, 6.8, 3.8) || (px is >= 4 and <= 11 && py is >= 7 and <= 11));
                        if (cloud) on[px, py] = true;
                        else if (rim) on[px, py] = false;
                    }
                break;
            case Sky.Fog:
                for (int px = 1; px <= 10; px++) Set(px, 3);
                for (int px = 0; px <= 11; px++) if (px != 8) Set(px, 6);
                for (int px = 1; px <= 10; px++) if (px != 3) Set(px, 9);
                break;
            default:
                bool high = sky != Sky.Cloudy;
                for (int py = 0; py < 12; py++) for (int px = 0; px < 12; px++) if (Cloud(px, py, high)) Set(px, py);
                switch (sky)
                {
                    case Sky.Drizzle: Set(3, 10); Set(6, 11); Set(9, 10); break;
                    case Sky.Rain: Set(3, 9); Set(2, 10); Set(6, 9); Set(5, 10); Set(9, 9); Set(8, 10); Set(4, 11); Set(7, 11); break;
                    case Sky.Snow:
                        foreach (int cx in new[] { 3, 8 }) { Set(cx, 9); Set(cx - 1, 10); Set(cx, 10); Set(cx + 1, 10); Set(cx, 11); }
                        break;
                    case Sky.Thunder:
                        // Clear a gap in the cloud for the bolt, then the bolt.
                        on[6, 7] = on[5, 7] = false;
                        Set(6, 7); Set(5, 8); Set(6, 8); Set(7, 8); Set(6, 9); Set(5, 10); Set(4, 11);
                        break;
                }
                break;
        }
        for (int py = 0; py < 12; py++)
            for (int px = 0; px < 12; px++)
                if (on[px, py]) g.FillRectangle(Brushes.White, x + px, y + py, 1, 1);
    }

    /// <summary>Preview: every picture, by day and by night, 6× enlarged, in one PNG.</summary>
    public static Bitmap WeatherIconSheet()
    {
        int[] codes = [0, 2, 3, 45, 53, 63, 73, 95];
        string[] names = ["clear", "partly", "cloudy", "fog", "drizzle", "rain", "snow", "thunder"];
        const int s = 6, cell = 12 * s + 24;
        var bmp = new Bitmap(codes.Length * cell + 24, 2 * (cell + 20) + 24);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(45, 45, 48));
        using var font = new Font("Segoe UI", 13f, GraphicsUnit.Pixel);
        for (int row = 0; row < 2; row++)
            for (int i = 0; i < codes.Length; i++)
            {
                using var icon = new Bitmap(12, 12);
                using (var ig = Graphics.FromImage(icon)) { ig.Clear(Color.Black); WeatherIcon(ig, 0, 0, codes[i], day: row == 0); }
                int x = 24 + i * cell, y = 24 + row * (cell + 20);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                g.DrawImage(icon, x, y, 12 * s, 12 * s);
                g.DrawString(names[i] + (row == 0 ? "" : " (night)"), font, Brushes.Gainsboro, x, y + 12 * s + 2);
            }
        return bmp;
    }
}
