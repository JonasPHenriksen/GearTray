using System.Drawing.Text;
using GearTray.Devices;
using GearTray.Util;

namespace GearTray.Effects;

/// <summary>What the base-station dashboard shows; supplied by the tray.</summary>
public sealed record DashboardData(int? Mouse, bool MouseCharging, int? Headset, bool HeadsetCharging, int? Spare,
    string? Controller, bool GameMode, string? Profile);

/// <summary>
/// Draws GearTray's own screen on the Arctis base station's 128×64 OLED: clock, date and every battery level.
/// Redraws every second, steps aside for ~3.5 s after the dial or ChatMix knob is used (the base station shows its own
/// volume screen then), shifts the picture by a pixel every minute against burn-in, and hands the screen back to the
/// base station when stopped.
/// </summary>
public sealed class BaseStationScreen : IDisposable
{
    private const int W = ArctisNovaProWireless.ScreenWidth, H = ArctisNovaProWireless.ScreenHeight;
    private readonly DeviceManager _devices;
    private readonly Func<DashboardData> _data;
    private System.Threading.Timer? _timer;
    private volatile bool _running;
    private int _busy;

    public BaseStationScreen(DeviceManager devices, Func<DashboardData> data)
    {
        _devices = devices;
        _data = data;
    }

    public bool Running => _running;

    public void Start()
    {
        if (_running) return;
        _running = true;
        _timer = new System.Threading.Timer(_ => Tick(), null, 500, 1000);
        Log.Info("Base station screen: GearTray dashboard on");
    }

    /// <summary>Stop drawing and give the screen back to the base station.</summary>
    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _timer?.Dispose();
        _timer = null;
        Restore();
        Log.Info("Base station screen: back to the normal screen");
    }

    /// <summary>Hand the screen back right now (also used at startup after an unclean exit).</summary>
    public void Restore()
    {
        _ = _devices.RunAsync(() =>
        {
            try
            {
                if (_devices.Get<ArctisNovaProWireless>() is { Connected: true } h) h.RestoreScreen();
            }
            catch (Exception ex) { Log.Warn("Base station screen restore: " + ex.Message); }
        });
    }

    private void Tick()
    {
        if (!_running || Interlocked.Exchange(ref _busy, 1) == 1) return;
        bool[] frame;
        try { frame = Render(_data(), DateTime.Now); }
        catch (Exception ex) { Log.Warn("Base station screen render: " + ex.Message); Volatile.Write(ref _busy, 0); return; }
        _ = _devices.RunAsync(() =>
        {
            try
            {
                if (!_running || _devices.Get<ArctisNovaProWireless>() is not { Connected: true, CanDrawScreen: true } h) return;
                h.DrainEvents();
                if (DateTime.UtcNow - h.LastKnobUse < TimeSpan.FromSeconds(3.5)) return; // the base station is showing its volume screen
                h.DrawScreen(frame);
            }
            catch (Exception ex) { Log.Warn("Base station screen: " + ex.Message); }
            finally { Volatile.Write(ref _busy, 0); }
        });
    }

    /// <summary>Draw the dashboard into a 1-bit 128×64 frame (pixels[x + y*128]).</summary>
    public static bool[] Render(DashboardData d, DateTime now)
    {
        using var bmp = new Bitmap(W, H);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Black);
            g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
            // Burn-in protection: move everything by a pixel each minute.
            int shift = now.Minute % 4;
            g.TranslateTransform(shift is 1 or 2 ? 1 : 0, shift >= 2 ? 1 : 0);

            using var big = new Font("Segoe UI Semibold", 15f, GraphicsUnit.Pixel);
            using var small = new Font("Segoe UI", 10f, GraphicsUnit.Pixel);
            var white = Brushes.White;
            g.DrawString(now.ToString("HH:mm"), big, white, -1, -3);
            var date = now.ToString("ddd d MMM");
            var dateSize = g.MeasureString(date, small);
            g.DrawString(date, small, white, W - dateSize.Width - 2, 2);

            int y = 19;
            Line(g, small, ref y, "Mouse", d.Mouse, d.MouseCharging);
            Line(g, small, ref y, "Headset", d.Headset, d.HeadsetCharging);
            if (d.Spare is int spare) Line(g, small, ref y, "Spare", spare, false);
            string bottom = d.Profile is not null ? d.Profile : d.GameMode ? "Game mode" : d.Controller ?? "";
            if (bottom.Length > 0 && y <= H - 11) g.DrawString(bottom, small, white, 0, y - 1);
        }

        var pixels = new bool[W * H];
        for (int py = 0; py < H; py++)
            for (int px = 0; px < W; px++)
                pixels[px + py * W] = bmp.GetPixel(px, py).GetBrightness() > 0.5f;
        return pixels;
    }

    /// <summary>"Label  ███████░░░  56%" on one 11-pixel row.</summary>
    private static void Line(Graphics g, Font font, ref int y, string label, int? percent, bool charging)
    {
        g.DrawString(label, font, Brushes.White, 0, y - 1);
        string value = percent is int p ? $"{p}%{(charging ? "+" : "")}" : "–";
        var size = g.MeasureString(value, font);
        g.DrawString(value, font, Brushes.White, W - size.Width + 2, y - 1);
        if (percent is int pct)
        {
            const int barX = 46, barW = 50;
            g.DrawRectangle(Pens.White, barX, y + 2, barW, 6);
            g.FillRectangle(Brushes.White, barX + 1, y + 3, (barW - 1) * Math.Clamp(pct, 0, 100) / 100, 5);
        }
        y += 11;
    }

    public void Dispose() => Stop();
}
