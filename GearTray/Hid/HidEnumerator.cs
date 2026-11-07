using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace GearTray.Hid;

/// <summary>One HID top-level collection (one openable device path).</summary>
public sealed record HidDeviceInfo(
    string Path,
    ushort VendorId,
    ushort ProductId,
    ushort Version,
    string Manufacturer,
    string Product,
    int Interface,      // MI_xx, -1 if not a composite device
    int Collection,     // COLxx, 0 if the interface has a single collection
    ushort UsagePage,
    ushort Usage,
    int InputLength,
    int OutputLength,
    int FeatureLength,
    bool CapsKnown)     // false when the path could not be opened at all (e.g. access denied)
{
    public override string ToString() =>
        $"{VendorId:X4}:{ProductId:X4} MI_{Interface:D2} COL{Collection:D2} UP=0x{UsagePage:X4} U=0x{Usage:X4} " +
        $"In={InputLength} Out={OutputLength} Feat={FeatureLength} {(CapsKnown ? "" : "(not openable)")} {Product}";
}

public static unsafe partial class HidEnumerator
{
    [GeneratedRegex(@"vid_([0-9a-f]{4})&pid_([0-9a-f]{4})(?:&mi_([0-9a-f]{2}))?(?:&col([0-9a-f]{2}))?", RegexOptions.IgnoreCase)]
    private static partial Regex PathRegex();

    public static List<HidDeviceInfo> Enumerate(Func<ushort, ushort, bool>? filter = null)
    {
        var result = new List<HidDeviceInfo>();
        Native.HidD_GetHidGuid(out var guid);
        IntPtr set = Native.SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
        if (set == new IntPtr(-1)) return result;
        try
        {
            for (uint i = 0; ; i++)
            {
                var data = new Native.SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<Native.SP_DEVICE_INTERFACE_DATA>() };
                if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data)) break;
                string? path = GetPath(set, ref data);
                if (path is null) continue;

                var m = PathRegex().Match(path);
                if (!m.Success) continue;
                ushort vid = Convert.ToUInt16(m.Groups[1].Value, 16), pid = Convert.ToUInt16(m.Groups[2].Value, 16);
                if (filter != null && !filter(vid, pid)) continue;
                int mi = m.Groups[3].Success ? Convert.ToInt32(m.Groups[3].Value, 16) : -1;
                int col = m.Groups[4].Success ? Convert.ToInt32(m.Groups[4].Value, 16) : 0;

                result.Add(Describe(path, vid, pid, mi, col));
            }
        }
        finally { Native.SetupDiDestroyDeviceInfoList(set); }
        return result;
    }

    private static string? GetPath(IntPtr set, ref Native.SP_DEVICE_INTERFACE_DATA data)
    {
        Native.SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out int required, IntPtr.Zero);
        if (required <= 0) return null;
        IntPtr buf = Marshal.AllocHGlobal(required);
        try
        {
            Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
            return Native.SetupDiGetDeviceInterfaceDetailW(set, ref data, buf, required, out _, IntPtr.Zero)
                ? Marshal.PtrToStringUni(IntPtr.Add(buf, 4))
                : null;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static HidDeviceInfo Describe(string path, ushort vid, ushort pid, int mi, int col)
    {
        // Zero desired access is enough to read attributes/caps, even for system-owned keyboards.
        using var h = Native.CreateFileW(path, 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid)
            return new HidDeviceInfo(path, vid, pid, 0, "", "", mi, col, 0, 0, 0, 0, 0, false);

        var attr = new Native.HIDD_ATTRIBUTES { Size = sizeof(Native.HIDD_ATTRIBUTES) };
        Native.HidD_GetAttributes(h, ref attr);
        string product = ReadString(h, true), manufacturer = ReadString(h, false);

        ushort up = 0, u = 0; int inLen = 0, outLen = 0, featLen = 0;
        if (Native.HidD_GetPreparsedData(h, out var pp))
        {
            if (Native.HidP_GetCaps(pp, out var caps) >= 0)
            {
                up = caps.UsagePage; u = caps.Usage;
                inLen = caps.InputReportByteLength; outLen = caps.OutputReportByteLength; featLen = caps.FeatureReportByteLength;
            }
            Native.HidD_FreePreparsedData(pp);
        }
        return new HidDeviceInfo(path, vid, pid, attr.VersionNumber, manufacturer, product, mi, col, up, u, inLen, outLen, featLen, true);
    }

    private static string ReadString(Microsoft.Win32.SafeHandles.SafeFileHandle h, bool product)
    {
        char* buf = stackalloc char[128];
        bool ok = product ? Native.HidD_GetProductString(h, buf, 256) : Native.HidD_GetManufacturerString(h, buf, 256);
        return ok ? new string(buf).Trim() : "";
    }
}
