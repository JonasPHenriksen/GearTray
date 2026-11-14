using GearTray.Hid;

namespace GearTray.Devices;

public enum DeviceKind { Mouse, Keyboard, Headset }

public sealed record BatteryInfo(int Percent, bool Charging);

/// <summary>
/// Base class for one physical peripheral. All methods are called on the DeviceManager worker thread,
/// never on the UI thread, so implementations may block on HID I/O.
/// </summary>
public abstract class GearDevice : IDisposable
{
    public abstract string Name { get; }
    public abstract DeviceKind Kind { get; }

    /// <summary>True for any HID interface that belongs to this device (by VID/PID).</summary>
    public abstract bool Matches(ushort vid, ushort pid);

    public bool Connected { get; protected set; }
    /// <summary>Model / firmware text for the UI, once known.</summary>
    public string? Detail { get; protected set; }
    /// <summary>Why the device is present but not usable (e.g. needs admin), or null.</summary>
    public string? Problem { get; protected set; }

    /// <summary>
    /// Open the needed interfaces. Called with every HID collection matching this device.
    /// hotPlug = the device just appeared (as opposed to being present when the app started).
    /// </summary>
    public abstract void Attach(IReadOnlyList<HidDeviceInfo> interfaces, bool hotPlug);

    /// <summary>
    /// While connected: true when the matching interfaces changed in a way that needs a fresh Attach
    /// (e.g. a wireless mouse got plugged in by cable).
    /// </summary>
    public virtual bool NeedsReattach(IReadOnlyList<HidDeviceInfo> interfaces) => false;

    /// <summary>Close handles; called when the device disappears or the app exits.</summary>
    public virtual void Detach() { Connected = false; }

    public void Dispose() => Detach();
}

public interface IHasBattery
{
    /// <summary>Null when unknown (e.g. headset switched off, mouse asleep).</summary>
    BatteryInfo? ReadBattery();
}
