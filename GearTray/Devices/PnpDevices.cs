using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace GearTray.Devices;

/// <summary>Lists present USB / Bluetooth devices by instance ID (SetupAPI), e.g. to name the connected Xbox controller.</summary>
public static partial class PnpDevices
{
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string? enumerator, IntPtr hwnd, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref SP_DEVINFO_DATA data, StringBuilder id, int size, out int required);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

    private const uint DIGCF_PRESENT = 0x2, DIGCF_ALLCLASSES = 0x4;

    [GeneratedRegex(@"VID(?:_|&)?0*([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex VidPid();

    /// <summary>Instance IDs of present devices whose ID contains the vendor id (e.g. "045E").</summary>
    public static List<string> InstanceIds(string vendorHex)
    {
        var result = new List<string>();
        foreach (var enumerator in new[] { "USB", "BTHENUM", "BTHLEDEVICE", "HID" })
        {
            IntPtr set = SetupDiGetClassDevsW(IntPtr.Zero, enumerator, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
            if (set == new IntPtr(-1)) continue;
            try
            {
                var data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
                var sb = new StringBuilder(512);
                for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
                {
                    if (!SetupDiGetDeviceInstanceIdW(set, ref data, sb, sb.Capacity, out _)) continue;
                    string id = sb.ToString();
                    if (id.Contains(vendorHex, StringComparison.OrdinalIgnoreCase)) result.Add(id);
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }
        return result;
    }

    private static readonly Dictionary<int, string> XboxModels = new()
    {
        [0x02D1] = "Xbox One Controller", [0x02DD] = "Xbox One Controller (2015)", [0x02E3] = "Xbox Elite Controller",
        [0x02EA] = "Xbox One S Controller", [0x02E0] = "Xbox One S Controller (Bluetooth)", [0x02FD] = "Xbox One S Controller (Bluetooth)",
        [0x0B00] = "Xbox Elite Series 2 Controller", [0x0B05] = "Xbox Elite Series 2 Controller (Bluetooth)", [0x0B22] = "Xbox Elite Series 2 Controller (Bluetooth)",
        [0x0B12] = "Xbox Series X|S Controller", [0x0B13] = "Xbox Series X|S Controller (Bluetooth)", [0x0B20] = "Xbox Series X|S Controller (Bluetooth)",
        [0x0B21] = "Xbox Adaptive Controller (Bluetooth)", [0x0B0A] = "Xbox Adaptive Controller", [0x02FE] = "Xbox Wireless Adapter for Windows",
        [0x02E6] = "Xbox Wireless Adapter for Windows", [0x0B11] = "Xbox Wireless Adapter for Windows",
    };

    /// <summary>The model name for a Microsoft Xbox product id, or null.</summary>
    public static string? XboxModel(int productId) => XboxModels.GetValueOrDefault(productId);

    /// <summary>Names of the Xbox controllers (and wireless adapters) Windows has right now, with how they connect.</summary>
    public static List<string> XboxDevices()
    {
        var names = new List<string>();
        foreach (var id in InstanceIds("045E"))
        {
            var m = VidPid().Match(id);
            if (!m.Success || !XboxModels.TryGetValue(Convert.ToInt32(m.Groups[2].Value, 16), out var name)) continue;
            string via = id.StartsWith("BTH", StringComparison.OrdinalIgnoreCase) ? "Bluetooth" : name.Contains("Adapter") ? "" : "USB cable";
            string label = via.Length > 0 && !name.Contains("Bluetooth") ? $"{name} ({via})" : name;
            if (!names.Contains(label)) names.Add(label);
        }
        return names;
    }
}
