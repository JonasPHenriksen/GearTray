using System.Diagnostics;
using GearTray.Devices;
using GearTray.Util;

namespace GearTray.Effects;

public sealed record AmbientOptions(string? MonitorDeviceName, bool FollowPicture, int SaturationBoost);

/// <summary>
/// Runs a software keyboard effect (Ambient or AudioMeter) on its own thread and streams frames to the keyboard
/// through the device thread. Frames are dropped, never queued, if the keyboard is still busy with the last one.
/// </summary>
public sealed class EffectEngine : IDisposable
{
    private const int Rows = RazerHuntsmanElite.MatrixRows, Cols = RazerHuntsmanElite.MatrixCols, KeyRows = 6;
    private readonly DeviceManager _devices;
    private Thread? _thread;
    private volatile bool _stop;
    private int _inFlight;
    private long _lastErrorLog;

    public EffectEngine(DeviceManager devices) => _devices = devices;

    /// <summary>Human-readable state for the settings window.</summary>
    public string Status { get; private set; } = "not running";
    public bool Running => _thread is { IsAlive: true };

    public void Start(KeyboardLighting look, AmbientOptions ambient, Util.KeyboardLightingSettings? settings = null)
    {
        Stop();
        _stop = false;
        _settings = settings;
        _thread = new Thread(() => Run(look, ambient)) { IsBackground = true, Name = "GearTray keyboard effect", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    private Util.KeyboardLightingSettings? _settings;

    public void Stop()
    {
        if (_thread is null) return;
        _stop = true;
        _thread.Join(2000);
        _thread = null;
        Status = "not running";
        _ = _devices.RunAsync(() => _devices.Get<RazerHuntsmanElite>()?.EndFrames());
    }

    private void Run(KeyboardLighting look, AmbientOptions opt)
    {
        IScreenSampler? sampler = null;
        AudioMeter? meter = null;
        using var timer = new TimerResolution();
        try
        {
            int fps;
            if (look.Effect == KeyboardEffect.Ambient)
            {
                var screen = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == opt.MonitorDeviceName) ?? Screen.PrimaryScreen!;
                sampler = ScreenSamplers.Create(screen);
                fps = sampler is DesktopDuplicationSampler ? 20 : 8;
                Status = $"Ambient: {sampler.Method}, {fps} updates/s, monitor {screen.DeviceName.TrimStart('\\', '.')}";
            }
            else if (look.Effect == KeyboardEffect.Ripple)
            {
                fps = 30;
                Status = "Ripple: waves from the keys you press";
            }
            else
            {
                meter = new AudioMeter();
                fps = 30;
                Status = "Audio meter: following the default playback device";
            }
            Log.Info($"Keyboard effect started. {Status}");
            var background = _settings is null ? Color.Black : Util.ColorUtil.Parse(_settings.Color2);
            var ripples = new List<(int Row, int Col, long StartMs)>();

            var grid = new byte[Cols * KeyRows * 3];
            var target = new float[Rows * Cols * 3];
            var shown = new float[Rows * Cols * 3];
            var frame = new byte[Rows * Cols * 3];
            float smoothing = sampler is not null ? 0.35f : 1f;
            double brightness = Math.Clamp(look.Brightness, 0, 100) / 100.0;
            float left = 0, right = 0;
            var clock = Stopwatch.StartNew();

            while (!_stop)
            {
                long start = clock.ElapsedMilliseconds;
                if (sampler is not null)
                {
                    if (sampler.Sample(grid, Cols, KeyRows)) BuildAmbient(grid, target, opt);
                }
                else if (look.Effect == KeyboardEffect.Ripple)
                {
                    long now = clock.ElapsedMilliseconds;
                    while (_presses.TryDequeue(out var p)) ripples.Add((p.Row, p.Col, now));
                    ripples.RemoveAll(r => now - r.StartMs > RippleLifeMs);
                    BuildRipple(target, ripples, now, look.Color, background);
                }
                else
                {
                    var (l, r) = meter!.Read();
                    left = Math.Max(Level(l), left * 0.82f);    // fast attack, smooth fall
                    right = Math.Max(Level(r), right * 0.82f);
                    BuildMeter(target, left, right, look.Color);
                }
                for (int i = 0; i < frame.Length; i++)
                {
                    shown[i] += (target[i] - shown[i]) * smoothing;
                    frame[i] = (byte)Math.Clamp(shown[i] * brightness, 0, 255);
                }
                Send(frame);
                int wait = (int)(1000 / fps - (clock.ElapsedMilliseconds - start));
                if (wait > 0) Thread.Sleep(wait);
            }
        }
        catch (Exception ex)
        {
            Status = "stopped: " + ex.Message;
            Log.Error("Keyboard effect stopped", ex);
        }
        finally
        {
            sampler?.Dispose();
            meter?.Dispose();
        }
    }

    private void Send(byte[] frame)
    {
        if (Interlocked.Exchange(ref _inFlight, 1) == 1) return; // keyboard still busy: skip this frame
        var copy = (byte[])frame.Clone();
        _ = _devices.RunAsync(() =>
        {
            try
            {
                if (!_stop && _devices.Get<RazerHuntsmanElite>() is { Connected: true } kb) kb.SendFrame(copy);
            }
            catch (Exception ex)
            {
                if (Environment.TickCount64 - _lastErrorLog > 30_000)
                {
                    _lastErrorLog = Environment.TickCount64;
                    Log.Warn("Keyboard frame: " + ex.Message);
                }
            }
            finally { Volatile.Write(ref _inFlight, 0); }
        });
    }

    // ---------- ripple ----------

    private const int RippleLifeMs = 1100;
    private readonly System.Collections.Concurrent.ConcurrentQueue<(int Row, int Col)> _presses = new();

    /// <summary>A key at this matrix position was pressed (called from the keyboard hook thread).</summary>
    public void KeyPressed(int row, int col)
    {
        if (_presses.Count < 64) _presses.Enqueue((row, col));
    }

    /// <summary>Rings that spread from each pressed key at ~16 keys per second and fade out; the underglow follows the front row.</summary>
    private static void BuildRipple(float[] target, List<(int Row, int Col, long StartMs)> ripples, long now, Color wave, Color background)
    {
        for (int row = 0; row < Rows; row++)
            for (int col = 0; col < Cols; col++)
            {
                float k = 0;
                int r = row < KeyRows ? row : KeyRows - 1; // underglow: as if it were the front row
                foreach (var p in ripples)
                {
                    float age = (now - p.StartMs) / 1000f;
                    float radius = age * 16f;
                    float d = MathF.Sqrt((col - p.Col) * (col - p.Col) + (r - p.Row) * (r - p.Row) * 1.4f);
                    float ring = Math.Max(0, 1 - MathF.Abs(d - radius) / 1.6f) * (1 - age * 1000f / RippleLifeMs);
                    k = Math.Max(k, ring);
                }
                int t = (row * Cols + col) * 3;
                target[t] = background.R + (wave.R - background.R) * k;
                target[t + 1] = background.G + (wave.G - background.G) * k;
                target[t + 2] = background.B + (wave.B - background.B) * k;
            }
    }

    // ---------- ambient ----------

    private static void BuildAmbient(byte[] grid, float[] target, AmbientOptions opt)
    {
        float boost = 1 + Math.Clamp(opt.SaturationBoost, 0, 200) / 100f;
        if (!opt.FollowPicture)
        {
            // Whole keyboard = average of the picture.
            long r = 0, g = 0, b = 0;
            for (int i = 0; i < grid.Length; i += 3) { r += grid[i]; g += grid[i + 1]; b += grid[i + 2]; }
            int n = grid.Length / 3;
            var (ar, ag, ab) = Saturate(r / (float)n, g / (float)n, b / (float)n, boost);
            for (int i = 0; i < target.Length; i += 3) { target[i] = ar; target[i + 1] = ag; target[i + 2] = ab; }
            return;
        }
        // Keys: the picture laid over the keyboard (top of the screen = function-key row).
        for (int row = 0; row < KeyRows; row++)
            for (int col = 0; col < Cols; col++)
            {
                int s = (row * Cols + col) * 3, t = (row * Cols + col) * 3;
                (target[t], target[t + 1], target[t + 2]) = Saturate(grid[s], grid[s + 1], grid[s + 2], boost);
            }
        // Underglow: each column's average over the whole height, so the edges glow with the screen's sides.
        for (int col = 0; col < Cols; col++)
        {
            float r = 0, g = 0, b = 0;
            for (int row = 0; row < KeyRows; row++) { int s = (row * Cols + col) * 3; r += grid[s]; g += grid[s + 1]; b += grid[s + 2]; }
            var (ur, ug, ub) = Saturate(r / KeyRows, g / KeyRows, b / KeyRows, boost);
            for (int row = KeyRows; row < Rows; row++)
            {
                int t = (row * Cols + col) * 3;
                target[t] = ur; target[t + 1] = ug; target[t + 2] = ub;
            }
        }
    }

    /// <summary>Push colors away from grey so the keyboard doesn't look washed out.</summary>
    private static (float, float, float) Saturate(float r, float g, float b, float boost)
    {
        float max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        if (max <= 0 || max - min < 1) return (r, g, b);
        float s = (max - min) / max, s2 = Math.Min(1f, s * boost), k = s2 / s;
        return (max - (max - r) * k, max - (max - g) * k, max - (max - b) * k);
    }

    // ---------- audio meter ----------

    /// <summary>Peak 0–1 → 0–1 on a -48…0 dB scale, which matches how loud things feel.</summary>
    private static float Level(float peak) =>
        peak <= 0.0001f ? 0 : Math.Clamp((20f * MathF.Log10(peak) + 48f) / 48f, 0f, 1f);

    private static void BuildMeter(float[] target, float left, float right, Color under)
    {
        for (int row = 0; row < KeyRows; row++)
        {
            float lit = (row < KeyRows / 2 ? left : right) * Cols;
            for (int col = 0; col < Cols; col++)
            {
                var (r, g, b) = Hue(120f * (1f - col / (float)(Cols - 1)));  // green → yellow → red
                float k = Math.Clamp(lit - col, 0f, 1f);
                k = Math.Max(k, 0.06f);                                      // faint track for unlit keys
                int t = (row * Cols + col) * 3;
                target[t] = r * k; target[t + 1] = g * k; target[t + 2] = b * k;
            }
        }
        float glow = 0.15f + 0.85f * Math.Max(left, right);
        for (int row = KeyRows; row < Rows; row++)
            for (int col = 0; col < Cols; col++)
            {
                int t = (row * Cols + col) * 3;
                target[t] = under.R * glow; target[t + 1] = under.G * glow; target[t + 2] = under.B * glow;
            }
    }

    private static (float, float, float) Hue(float h)
    {
        float x = 255f * (1 - Math.Abs(h / 60f % 2 - 1));
        return h switch
        {
            < 60 => (255, x, 0),
            < 120 => (x, 255, 0),
            _ => (0, 255, x),
        };
    }

    public void Dispose() => Stop();
}
