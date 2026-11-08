using GearTray.Util;

namespace GearTray.Effects;

public sealed partial class BaseStationScreen
{
    /// <summary>
    /// Now playing: the title on the first line; the artist and, on the right, the player on the second; the sound of
    /// the last ~13 s as a moving wave under them; and the elapsed and remaining time with a slim bar between them, or
    /// how long it has been playing while the player hasn't said where in the track it is. <paramref name="peaks"/>
    /// are the sound meter's peaks (0–1), one per screen tick, newest last.
    /// </summary>
    public static bool[] RenderMedia(MediaInfo m, IReadOnlyList<float> peaks, DateTime now) => Draw(now, g =>
    {
        using var titleFont = new Font("Segoe UI Semibold", 12f, GraphicsUnit.Pixel);
        using var small = new Font("Segoe UI", 10f, GraphicsUnit.Pixel);
        using var oneLine = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        string title = m.Title.Length > 0 ? m.Title : m.Artist;
        string artist = m.Title.Length > 0 ? m.Artist : "";
        g.DrawString(title, titleFont, Brushes.White, new RectangleF(0, -2, W + 2, 16), oneLine);
        float appWidth = m.App.Length > 0 ? g.MeasureString(m.App, small).Width : 0;
        if (m.App.Length > 0) g.DrawString(m.App, small, Brushes.White, W - appWidth + 2, 12);
        if (artist.Length > 0) g.DrawString(artist, small, Brushes.White, new RectangleF(0, 12, W - appWidth - 2, 13), oneLine);

        Wave(g, peaks, top: 26, bottom: 50);

        if (m.Duration > TimeSpan.Zero)
        {
            string played = Time(m.Position), left = "-" + Time(m.Duration - m.Position);
            float playedW = g.MeasureString(played, small).Width, leftW = g.MeasureString(left, small).Width;
            g.DrawString(played, small, Brushes.White, -1, 51);
            g.DrawString(left, small, Brushes.White, W - leftW + 2, 51);
            ProgressBar(g, playedW + 1, 58, W - leftW - playedW - 2, m.Position / m.Duration);
        }
        else g.DrawString("playing for " + Time(m.PlayingFor), small, Brushes.White, -1, 51); // Firefox reports only on pause or skip
    });

    /// <summary>
    /// The peaks as mirrored bars, one every 2 px, newest on the right. Scaled to the loudest moment in view, the way
    /// audio editors show a waveform, so it fills the band at any volume and keeps the ups and downs (on the meter's
    /// decibel scale most music sits near the top). Silence, and the part not heard yet, is a dotted line.
    /// </summary>
    private static void Wave(Graphics g, IReadOnlyList<float> peaks, int top, int bottom)
    {
        int mid = (top + bottom) / 2, half = (bottom - top) / 2, bars = W / 2;
        float loudest = 0;
        for (int i = Math.Max(0, peaks.Count - bars); i < peaks.Count; i++) loudest = Math.Max(loudest, peaks[i]);
        for (int i = 0; i < bars; i++)
        {
            float peak = i < peaks.Count ? peaks[peaks.Count - 1 - i] : 0;
            int h = loudest >= 0.01f ? (int)Math.Round(Math.Clamp(peak / loudest, 0, 1) * half) : 0;
            g.FillRectangle(Brushes.White, W - 2 - i * 2, mid - h, 1, h * 2 + 1);
        }
    }

    /// <summary>A 1-px track, dotted where still to come, the played part 3 px thick, and a 5-px knob.</summary>
    private static void ProgressBar(Graphics g, float x, float y, float width, double fraction)
    {
        int x0 = (int)Math.Round(x) + 2, x1 = (int)Math.Round(x + width) - 3, cy = (int)y;
        if (x1 <= x0) return;
        int done = x0 + (int)Math.Round((x1 - x0) * Math.Clamp(fraction, 0, 1));
        if (done > x0) g.FillRectangle(Brushes.White, x0, cy - 1, done - x0, 3);
        for (int px = done + 3; px <= x1; px += 2) g.FillRectangle(Brushes.White, px, cy, 1, 1);
        g.FillRectangle(Brushes.White, done - 2, cy - 1, 5, 3);
        g.FillRectangle(Brushes.White, done - 1, cy - 2, 3, 5);
    }
}
