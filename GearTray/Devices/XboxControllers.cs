using System.Runtime.InteropServices;
using GearTray.Util;

namespace GearTray.Devices;

[Flags]
public enum PadButtons : ushort
{
    None = 0,
    DpadUp = 0x0001, DpadDown = 0x0002, DpadLeft = 0x0004, DpadRight = 0x0008,
    Start = 0x0010, Back = 0x0020, LeftThumb = 0x0040, RightThumb = 0x0080,
    LeftShoulder = 0x0100, RightShoulder = 0x0200, Guide = 0x0400,
    A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000,
}

/// <summary>One reading of a controller. Sticks are -1…1, triggers 0…1.</summary>
public readonly record struct PadState(uint Packet, PadButtons Buttons, float LeftTrigger, float RightTrigger,
    float LeftX, float LeftY, float RightX, float RightY, short RawLX, short RawLY, short RawRX, short RawRY);

public enum PadBatteryType { Disconnected, Wired, Alkaline, NiMH, Unknown }
public enum PadBatteryLevel { Empty, Low, Medium, Full }

public sealed record PadBattery(PadBatteryType Type, PadBatteryLevel Level)
{
    public bool Wireless => Type is PadBatteryType.Alkaline or PadBatteryType.NiMH or PadBatteryType.Unknown;
    /// <summary>XInput only reports four steps; this is the middle of each.</summary>
    public int ApproxPercent => Level switch { PadBatteryLevel.Full => 100, PadBatteryLevel.Medium => 60, PadBatteryLevel.Low => 25, _ => 5 };
    public override string ToString() => Type switch
    {
        PadBatteryType.Wired => "wired (no battery reading)",
        PadBatteryType.Disconnected => "not reported (cable-connected controllers don't report a battery level)",
        _ => $"{Level.ToString().ToLowerInvariant()} ({(Type == PadBatteryType.NiMH ? "rechargeable" : Type == PadBatteryType.Alkaline ? "AA batteries" : "battery")})",
    };
}

/// <summary>
/// Xbox controllers through XInput (xinput1_4.dll, part of Windows): up to four controllers, wired, Xbox Wireless
/// Adapter or Bluetooth. No driver or app needed. Thread-safe: XInput calls may come from any thread.
/// </summary>
public static class XInput
{
    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_GAMEPAD { public ushort wButtons; public byte bLeftTrigger, bRightTrigger; public short sThumbLX, sThumbLY, sThumbRX, sThumbRY; }
    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE { public uint dwPacketNumber; public XINPUT_GAMEPAD Gamepad; }
    /// <summary>Ordinal #100 writes 4 bytes more than XInputGetState (Chromium/SDL note), so it gets its own struct.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE_EX { public uint dwPacketNumber; public XINPUT_GAMEPAD Gamepad; public uint dwPaddingReserved; }
    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_CAPABILITIES_EX { public XINPUT_CAPABILITIES Caps; public ushort VendorId, ProductId, VersionNumber, Unknown1; public uint Unknown2; }
    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_VIBRATION { public ushort wLeftMotorSpeed, wRightMotorSpeed; }
    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_BATTERY_INFORMATION { public byte BatteryType, BatteryLevel; }
    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_CAPABILITIES { public byte Type, SubType; public ushort Flags; public XINPUT_GAMEPAD Gamepad; public XINPUT_VIBRATION Vibration; }

    [DllImport("xinput1_4.dll")] private static extern uint XInputGetState(uint user, out XINPUT_STATE state);
    [DllImport("xinput1_4.dll", EntryPoint = "#100")] private static extern uint XInputGetStateEx(uint user, out XINPUT_STATE_EX state);
    [DllImport("xinput1_4.dll", EntryPoint = "#108")] private static extern uint XInputGetCapabilitiesEx(uint reserved, uint user, uint flags, out XINPUT_CAPABILITIES_EX caps);
    [DllImport("xinput1_4.dll")] private static extern uint XInputSetState(uint user, ref XINPUT_VIBRATION vibration);
    [DllImport("xinput1_4.dll")] private static extern uint XInputGetBatteryInformation(uint user, byte devType, out XINPUT_BATTERY_INFORMATION info);
    [DllImport("xinput1_4.dll")] private static extern uint XInputGetCapabilities(uint user, uint flags, out XINPUT_CAPABILITIES caps);
    [DllImport("xinput1_4.dll", EntryPoint = "#103")] private static extern uint XInputPowerOffController(uint user);

    private const uint ERROR_SUCCESS = 0;
    private static bool _exAvailable = true, _powerOffAvailable = true;

    public static bool IsConnected(int slot) => XInputGetState((uint)slot, out _) == ERROR_SUCCESS;

    public static IEnumerable<int> ConnectedSlots() => Enumerable.Range(0, 4).Where(IsConnected);

    /// <summary>Stops the main rumble motors on every connected controller.</summary>
    public static void StopAll()
    {
        foreach (int slot in ConnectedSlots()) SetVibration(slot, 0, 0);
    }

    /// <summary>Reads the controller, including the Guide (Xbox) button when Windows offers the extended call.</summary>
    public static PadState? GetState(int slot)
    {
        XINPUT_GAMEPAD g;
        uint rc, packet;
        if (_exAvailable)
        {
            try
            {
                rc = XInputGetStateEx((uint)slot, out var ex);
                g = ex.Gamepad; packet = ex.dwPacketNumber;
            }
            catch (EntryPointNotFoundException)
            {
                _exAvailable = false;
                rc = XInputGetState((uint)slot, out var s);
                g = s.Gamepad; packet = s.dwPacketNumber;
            }
        }
        else
        {
            rc = XInputGetState((uint)slot, out var s);
            g = s.Gamepad; packet = s.dwPacketNumber;
        }
        if (rc != ERROR_SUCCESS) return null;
        return new PadState(packet, (PadButtons)g.wButtons, g.bLeftTrigger / 255f, g.bRightTrigger / 255f,
            Axis(g.sThumbLX), Axis(g.sThumbLY), Axis(g.sThumbRX), Axis(g.sThumbRY), g.sThumbLX, g.sThumbLY, g.sThumbRX, g.sThumbRY);
    }

    private static float Axis(short v) => Math.Max(-1f, v / 32767f);

    /// <summary>Strength 0–1 for the left (heavy) and right (light) rumble motors.</summary>
    public static bool SetVibration(int slot, double left, double right)
    {
        var v = new XINPUT_VIBRATION
        {
            wLeftMotorSpeed = (ushort)Math.Round(Math.Clamp(left, 0, 1) * 65535),
            wRightMotorSpeed = (ushort)Math.Round(Math.Clamp(right, 0, 1) * 65535),
        };
        return XInputSetState((uint)slot, ref v) == ERROR_SUCCESS;
    }

    public static PadBattery? GetBattery(int slot, bool headset = false)
    {
        if (XInputGetBatteryInformation((uint)slot, (byte)(headset ? 1 : 0), out var b) != ERROR_SUCCESS) return null;
        var type = b.BatteryType switch { 0 => PadBatteryType.Disconnected, 1 => PadBatteryType.Wired, 2 => PadBatteryType.Alkaline, 3 => PadBatteryType.NiMH, _ => PadBatteryType.Unknown };
        return new PadBattery(type, (PadBatteryLevel)Math.Min((int)b.BatteryLevel, 3));
    }

    /// <summary>What XInput says the device is (gamepad, wheel, arcade stick…) and what it supports.</summary>
    public static string? Describe(int slot)
    {
        if (XInputGetCapabilities((uint)slot, 0, out var c) != ERROR_SUCCESS) return null;
        string sub = c.SubType switch
        {
            1 => "gamepad", 2 => "racing wheel", 3 => "arcade stick", 4 => "flight stick", 5 => "dance pad",
            6 => "guitar", 7 => "alternate guitar", 8 => "drum kit", 0x0B => "guitar bass", 0x13 => "arcade pad", _ => "controller",
        };
        var features = new List<string>();
        if ((c.Flags & 0x0002) != 0) features.Add("wireless");
        if ((c.Flags & 0x0004) != 0) features.Add("plug-in modules");
        if ((c.Flags & 0x0008) != 0) features.Add("no navigation buttons");
        if ((c.Flags & 0x0001) != 0) features.Add("headset/voice");
        if (c.Vibration.wLeftMotorSpeed != 0 || c.Vibration.wRightMotorSpeed != 0) features.Add("rumble");
        return sub + (features.Count > 0 ? $" ({string.Join(", ", features)})" : "");
    }

    /// <summary>USB vendor/product id of the controller in a slot (hidden ordinal #108), or null.</summary>
    public static (ushort Vid, ushort Pid)? GetIds(int slot)
    {
        try
        {
            return XInputGetCapabilitiesEx(1, (uint)slot, 0, out var c) == ERROR_SUCCESS && c.VendorId != 0 ? (c.VendorId, c.ProductId) : null;
        }
        catch (EntryPointNotFoundException) { return null; }
    }

    /// <summary>
    /// Turns a wireless controller off (as holding the Xbox button would). Works for Xbox 360-era controllers; Xbox One and
    /// Series controllers generally refuse it, and a cable-connected one has nothing to switch off.
    /// </summary>
    public static bool PowerOff(int slot)
    {
        if (!_powerOffAvailable) return false;
        try { return XInputPowerOffController((uint)slot) == ERROR_SUCCESS; }
        catch (EntryPointNotFoundException)
        {
            _powerOffAvailable = false;
            Log.Warn("This Windows version's XInput has no power-off function");
            return false;
        }
    }
}
