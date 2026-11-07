using System.Runtime.InteropServices;
using GearTray.Hid;
using GearTray.Util;
using Microsoft.Win32.SafeHandles;

namespace GearTray.Devices;

/// <summary>A device on Windows' Xbox controller bus (GIP): an Xbox One / Series controller by cable or Xbox Wireless Adapter.</summary>
public sealed record GipDevice(ulong Id, ushort VendorId, ushort ProductId, string? Firmware)
{
    public override string ToString() =>
        $"{Id:X16}" + (VendorId != 0 ? $"  {VendorId:X4}:{ProductId:X4}" : "") + (Firmware is null ? "" : $"  firmware {Firmware}");
}

/// <summary>Xbox button light patterns (the GIP LED command's mode byte).</summary>
public enum XboxLedMode : byte
{
    Off = 0x00, On = 0x01, BlinkFast = 0x02, BlinkSlow = 0x03, BlinkCharging = 0x04, FadeSlow = 0x08, FadeFast = 0x09,
}

/// <summary>
/// The Xbox button light, through Windows' own Xbox controller driver (\\.\XboxGIP, xboxgip.sys), the way xbledctl does it
/// (github.com/Leclowndu93150/xbledctl, MIT). Undocumented interface: open the device, ask the driver to replay its
/// devices' announce messages (IOCTL 0x40001CD0), read them for the device ids, then write a 20-byte header followed by
/// the GIP LED payload. The LED command (0x0A) is the only thing ever written. Works over USB (and probably the Xbox
/// Wireless Adapter), not Bluetooth. Controllers forget the setting when they reconnect, so callers re-apply it.
/// </summary>
public static unsafe class XboxGip
{
    private const string DevicePath = @"\\.\XboxGIP";
    private const uint IOCTL_REENUMERATE = 0x40001CD0, FILE_ATTRIBUTE_NORMAL = 0x80;
    private const int HeaderSize = 20, ReadSize = 4096;
    private const byte CMD_ACK = 0x01, CMD_ANNOUNCE = 0x02, CMD_LED = 0x0A, OPT_INTERNAL = 0x20;
    public const int MaxBrightness = 47, DefaultBrightness = 20;
    private const ushort Microsoft = 0x045E;

    private static readonly object Gate = new();
    private static byte _seq = 1;
    private static List<GipDevice>? _cache;
    private static DateTime _cacheTime;
    private static bool _announceLogged;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, int inSize, IntPtr outBuf, int outSize, out int returned, IntPtr overlapped);

    /// <summary>The devices the driver knows (empty when there are none or the driver isn't there). Cached for 30 s.</summary>
    public static IReadOnlyList<GipDevice> Devices(bool fresh = false)
    {
        lock (Gate)
        {
            if (!fresh && _cache is not null && DateTime.UtcNow - _cacheTime < TimeSpan.FromSeconds(30)) return _cache;
            using var h = Open();
            _cache = h is null ? [] : Discover(h);
            _cacheTime = DateTime.UtcNow;
            return _cache;
        }
    }

    /// <summary>Sets the Xbox button light on every Xbox controller the driver knows; returns how many took it, or -1 without the driver.</summary>
    public static int SetLight(XboxLedMode mode, int brightness)
    {
        brightness = mode == XboxLedMode.Off ? 0 : Math.Clamp(brightness, 0, MaxBrightness);
        lock (Gate)
        {
            using var h = Open();
            if (h is null) return -1;
            var devices = Discover(h);
            _cache = devices;
            _cacheTime = DateTime.UtcNow;
            // Microsoft controllers only, when the announce messages say who is who; otherwise every device (as xbledctl does).
            var targets = devices.Any(d => d.VendorId != 0) ? devices.Where(d => d.VendorId == Microsoft).ToList() : devices;
            int ok = targets.Count(d => WriteLed(h, d.Id, mode, brightness));
            Log.Info($"Xbox button light {mode} {brightness}/{MaxBrightness}: {ok} of {targets.Count} controller(s)");
            return ok;
        }
    }

    private static SafeFileHandle? Open()
    {
        var h = Native.CreateFileW(DevicePath, Native.GENERIC_READ | Native.GENERIC_WRITE, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
            IntPtr.Zero, Native.OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL | Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (!h.IsInvalid) return h;
        Log.Info($"{DevicePath} can't be opened (error {Marshal.GetLastPInvokeError()}); no Xbox controller driver?");
        h.Dispose();
        return null;
    }

    private static List<GipDevice> Discover(SafeFileHandle h)
    {
        var found = new List<GipDevice>();
        // Have the driver replay its devices' announce messages to this handle. No buffers; xbledctl sends it the same way.
        DeviceIoControl(h, IOCTL_REENUMERATE, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);

        byte* buf = (byte*)NativeMemory.AllocZeroed(ReadSize);
        var ov = (NativeOverlapped*)NativeMemory.AllocZeroed((nuint)sizeof(NativeOverlapped));
        IntPtr ev = Native.CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
        bool canFree = true;
        try
        {
            var until = DateTime.UtcNow.AddSeconds(3);
            for (int i = 0; i < 16 && DateTime.UtcNow < until; i++)
            {
                *ov = default;
                ov->EventHandle = ev;
                bool timedOut = false;
                if (!Native.ReadFile(h, buf, ReadSize, IntPtr.Zero, ov))
                {
                    int err = Marshal.GetLastPInvokeError();
                    if (err != Native.ERROR_IO_PENDING) { Log.Info($"{DevicePath}: read failed (error {err})"); break; }
                    if (Native.WaitForSingleObject(ev, 300) != Native.WAIT_OBJECT_0)
                    {
                        timedOut = true;
                        Native.CancelIoEx(h, ov);
                        if (Native.WaitForSingleObject(ev, 1000) != Native.WAIT_OBJECT_0) { canFree = false; break; }
                    }
                }
                if (Native.GetOverlappedResult(h, ov, out int read, false)) Parse(buf, read, found);
                if (timedOut && found.Count > 0) break;   // the replayed announce messages have all arrived
            }
        }
        finally
        {
            // A read the driver wouldn't cancel may still complete into these, so they're only freed when it's done.
            if (canFree) { NativeMemory.Free(buf); NativeMemory.Free(ov); Native.CloseHandle(ev); }
        }
        return found;
    }

    private static void Parse(byte* m, int length, List<GipDevice> found)
    {
        if (length < HeaderSize) return;
        byte cmd = m[8];
        if (cmd is not (CMD_ACK or CMD_ANNOUNCE)) return;
        ulong id = *(ulong*)m;
        int payload = (int)Math.Min(*(uint*)(m + 12), (uint)(length - HeaderSize));
        byte* p = m + HeaderSize;
        ushort vid = 0, pid = 0;
        string? firmware = null;
        if (cmd == CMD_ANNOUNCE)
        {
            if (!_announceLogged)
            {
                _announceLogged = true;
                Log.Info($"GIP announce from {id:X16}: {Convert.ToHexString(new ReadOnlySpan<byte>(p, payload))}");
            }
            // Laid out as the GIP announce packet (Linux xone's gip_pkt_announce): address[6], u16, vendor id, product id,
            // firmware major/minor/build/revision, hardware version. Only trusted when the vendor id is plausible.
            if (payload >= 20)
            {
                vid = (ushort)(p[8] | p[9] << 8);
                pid = (ushort)(p[10] | p[11] << 8);
                int major = p[12] | p[13] << 8, minor = p[14] | p[15] << 8, build = p[16] | p[17] << 8, revision = p[18] | p[19] << 8;
                if (vid == 0 || vid == 0xFFFF) vid = pid = 0;
                else if (major < 100 && minor < 1000) firmware = $"{major}.{minor}.{build}.{revision}";
            }
        }
        int at = found.FindIndex(d => d.Id == id);
        if (at < 0) found.Add(new GipDevice(id, vid, pid, firmware));
        else if (vid != 0) found[at] = new GipDevice(id, vid, pid, firmware);
    }

    private static bool WriteLed(SafeFileHandle h, ulong id, XboxLedMode mode, int brightness)
    {
        const int size = HeaderSize + 3;
        byte* pkt = (byte*)NativeMemory.AllocZeroed(size);
        var ov = (NativeOverlapped*)NativeMemory.AllocZeroed((nuint)sizeof(NativeOverlapped));
        IntPtr ev = Native.CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
        ov->EventHandle = ev;
        bool canFree = true;
        try
        {
            *(ulong*)pkt = id;            // [0..7] device id
            pkt[8] = CMD_LED;             // [8] command
            pkt[9] = OPT_INTERNAL;        // [9] flags
            pkt[10] = NextSequence();     // [10] sequence 1–255; [11] 0
            *(uint*)(pkt + 12) = 3;       // [12..15] payload length; [16..19] 0
            pkt[20] = 0x00;               // payload: sub-command, pattern, intensity
            pkt[21] = (byte)mode;
            pkt[22] = (byte)brightness;
            if (!Native.WriteFile(h, pkt, size, IntPtr.Zero, ov))
            {
                int err = Marshal.GetLastPInvokeError();
                if (err != Native.ERROR_IO_PENDING) { Log.Info($"{DevicePath}: light command failed (error {err})"); return false; }
                if (Native.WaitForSingleObject(ev, 2000) != Native.WAIT_OBJECT_0)
                {
                    Native.CancelIoEx(h, ov);
                    if (Native.WaitForSingleObject(ev, 1000) != Native.WAIT_OBJECT_0) { canFree = false; return false; }
                }
            }
            return Native.GetOverlappedResult(h, ov, out int written, false) && written == size;
        }
        finally
        {
            if (canFree) { NativeMemory.Free(pkt); NativeMemory.Free(ov); Native.CloseHandle(ev); }
        }
    }

    private static byte NextSequence()
    {
        byte s = _seq;
        _seq = (byte)(_seq % 255 + 1);
        return s;
    }
}
