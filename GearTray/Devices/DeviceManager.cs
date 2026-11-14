using System.Collections.Concurrent;
using GearTray.Hid;
using GearTray.Util;

namespace GearTray.Devices;

/// <summary>
/// Owns all device drivers and runs every HID operation on one background thread,
/// so the UI never blocks and devices never see concurrent commands.
/// </summary>
public sealed class DeviceManager : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _worker;

    public IReadOnlyList<GearDevice> Devices { get; }

    /// <summary>Raised (on the worker thread) after a rescan changed which devices are connected.</summary>
    public event Action? DevicesChanged;

    /// <summary>The supported peripherals. The motherboard is intentionally not included.</summary>
    public static DeviceManager CreateDefault() => new(
    [
        new RogOmniMouse(),
        new RazerHuntsmanElite(),
        new ArctisNovaProWireless(),
    ]);

    public DeviceManager(IEnumerable<GearDevice> devices)
    {
        Devices = devices.ToList();
        _worker = new Thread(Run) { IsBackground = true, Name = "GearTray devices" };
        _worker.Start();
    }

    public T? Get<T>() where T : GearDevice => Devices.OfType<T>().FirstOrDefault();

    public Task RunAsync(Action action) => RunAsync(() => { action(); return true; });

    public Task<T> RunAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    /// <summary>Synchronous version for the CLI.</summary>
    public T Run<T>(Func<T> func) => RunAsync(func).GetAwaiter().GetResult();

    public Task RescanAsync() => RunAsync(Rescan);

    /// <summary>Close every handle and scan again (after sleep, handles can silently go stale).</summary>
    public Task ReconnectAllAsync() => RunAsync(() =>
    {
        foreach (var d in Devices)
        {
            try { d.Detach(); } catch (Exception ex) { Log.Warn($"{d.Name}: detach: {ex.Message}"); }
        }
        Rescan();
    });

    private bool _scannedOnce;

    /// <summary>Raised (on the worker thread) for each device that connected after the first scan.</summary>
    public event Action<GearDevice>? DeviceArrived;

    private void Rescan()
    {
        bool hotPlug = _scannedOnce;
        _scannedOnce = true;
        var all = HidEnumerator.Enumerate((vid, pid) => Devices.Any(d => d.Matches(vid, pid)));
        bool changed = false;
        foreach (var dev in Devices)
        {
            var mine = all.Where(i => dev.Matches(i.VendorId, i.ProductId)).ToList();
            bool wasConnected = dev.Connected;
            string? oldProblem = dev.Problem, oldDetail = dev.Detail;
            try
            {
                if (mine.Count == 0)
                {
                    if (dev.Connected) dev.Detach();
                }
                else if (!dev.Connected)
                {
                    dev.Attach(mine, hotPlug);
                    if (dev.Connected && hotPlug) DeviceArrived?.Invoke(dev);
                }
                else if (dev.NeedsReattach(mine))
                {
                    dev.Detach();
                    dev.Attach(mine, hotPlug);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"{dev.Name}: attach failed", ex);
                try { dev.Detach(); } catch { }
            }
            if (dev.Connected != wasConnected || dev.Problem != oldProblem || (dev.Connected && dev.Detail != oldDetail))
            {
                changed = true;
                Log.Info($"{dev.Name}: {(dev.Connected ? "connected" : "not connected")}{(dev.Detail is null ? "" : " — " + dev.Detail)}{(dev.Problem is null ? "" : " — " + dev.Problem)}");
            }
        }
        if (changed) DevicesChanged?.Invoke();
    }

    private void Run()
    {
        foreach (var action in _queue.GetConsumingEnumerable())
        {
            try { action(); }
            catch (Exception ex) { Log.Error("Device worker", ex); }
        }
    }

    public void Dispose()
    {
        _queue.Add(() => { foreach (var d in Devices) { try { d.Detach(); } catch { } } });
        _queue.CompleteAdding();
        _worker.Join(2000);
    }
}
