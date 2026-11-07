using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace GearTray.Util;

/// <summary>
/// CPU temperature (Tctl) of AMD Ryzen CPUs (Zen, families 17h–1Ah) through the PawnIO driver when it's installed:
/// the signed AMDFamily17 module (kept next to GearTray.exe) reads the CPU's SMN thermal register 0x59800, decoded as
/// LibreHardwareMonitor and Linux's k10temp do (0.125 °C steps, −49 °C when the range bit is set). Reads take the
/// "Global\Access_PCI" mutex that hardware monitors share. Needs admin rights. Null when PawnIO, the module or an
/// AMD Zen CPU is missing. Call from one thread at a time.
/// </summary>
public static unsafe class CpuTemperature
{
    private const uint SmnThmTconCurTmp = 0x00059800;
    private const string ModuleFile = "AMDFamily17.bin";

    private static bool _tried;
    private static IntPtr _handle;
    private static string _status = "not tried";
    private static Mutex? _pciMutex;
    private static delegate* unmanaged[Stdcall]<IntPtr, byte*, ulong*, nuint, ulong*, nuint, nuint*, int> _execute;

    /// <summary>What happened when PawnIO was set up (for the log and --cpu-temp).</summary>
    public static string Status => _status;

    /// <summary>The CPU temperature in °C, or null.</summary>
    public static double? Read() => ReadRaw() is uint raw ? Decode(raw) : null;

    /// <summary>The raw THM_TCON_CUR_TMP register, or null.</summary>
    public static uint? ReadRaw()
    {
        if (!Ready() || !AcquirePci()) return null;
        try
        {
            ulong input = SmnThmTconCurTmp, output = 0;
            nuint returned = 0;
            byte* name = stackalloc byte[32];
            Encoding.ASCII.GetBytes("ioctl_read_smn", new Span<byte>(name, 31));
            int hr = _execute(_handle, name, &input, 1, &output, 1, &returned);
            if (hr >= 0) return (uint)output;
            _status = $"SMN read failed (0x{hr:X8})";
            return null;
        }
        finally { ReleasePci(); }
    }

    public static double Decode(uint raw)
    {
        double t = (raw >> 21) * 0.125;
        if ((raw & 0x80000) != 0 || (raw & 0x30000) == 0x30000) t -= 49; // range select / TJ select
        return t;
    }

    /// <summary>Opens PawnIO and loads the AMD module once; later calls just report whether that worked.</summary>
    private static bool Ready()
    {
        if (_tried) return _handle != IntPtr.Zero;
        _tried = true;
        string module = Path.Combine(AppContext.BaseDirectory, ModuleFile);
        if (!File.Exists(module)) return Fail($"{ModuleFile} isn't next to GearTray.exe");
        string lib = Path.Combine(PawnIoDirectory(), "PawnIOLib.dll");
        if (!File.Exists(lib) || !NativeLibrary.TryLoad(lib, out var dll)) return Fail("PawnIO isn't installed");
        try
        {
            var open = (delegate* unmanaged[Stdcall]<IntPtr*, int>)NativeLibrary.GetExport(dll, "pawnio_open");
            var load = (delegate* unmanaged[Stdcall]<IntPtr, byte*, nuint, int>)NativeLibrary.GetExport(dll, "pawnio_load");
            var close = (delegate* unmanaged[Stdcall]<IntPtr, int>)NativeLibrary.GetExport(dll, "pawnio_close");
            _execute = (delegate* unmanaged[Stdcall]<IntPtr, byte*, ulong*, nuint, ulong*, nuint, nuint*, int>)NativeLibrary.GetExport(dll, "pawnio_execute");
            IntPtr handle;
            int hr = open(&handle);
            if (hr < 0) return Fail($"PawnIO didn't open (0x{hr:X8})");
            byte[] blob = File.ReadAllBytes(module);
            fixed (byte* b = blob) hr = load(handle, b, (nuint)blob.Length);
            if (hr < 0)
            {
                close(handle);
                return Fail($"the AMD module didn't load (0x{hr:X8}; not an AMD Zen CPU?)");
            }
            _handle = handle; // kept open: the module stays loaded until GearTray exits
            _status = "ready";
            Log.Info("CPU temperature: PawnIO ready");
            return true;
        }
        catch (EntryPointNotFoundException ex) { return Fail("PawnIOLib is missing a function: " + ex.Message); }
    }

    private static bool Fail(string why)
    {
        _status = why;
        Log.Info("CPU temperature unavailable: " + why);
        return false;
    }

    /// <summary>PawnIO's install folder from its uninstall entry, else Program Files\PawnIO.</summary>
    private static string PawnIoDirectory()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
            if (key?.GetValue("InstallLocation") is string dir && dir.Trim().Length > 0) return dir.Trim().Trim('"');
        }
        catch (Exception) { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO");
    }

    /// <summary>The PCI configuration lock shared by hardware monitors (SMN reads go through PCI config space).</summary>
    private static bool AcquirePci()
    {
        try
        {
            _pciMutex ??= new Mutex(false, @"Global\Access_PCI");
            return _pciMutex.WaitOne(10);
        }
        catch (AbandonedMutexException) { return true; } // its last owner died; we hold it now
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException) { return false; }
    }

    private static void ReleasePci()
    {
        try { _pciMutex?.ReleaseMutex(); }
        catch (ApplicationException) { } // not held (the wait timed out)
    }
}
