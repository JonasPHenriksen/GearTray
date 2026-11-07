using System.Runtime.InteropServices;

namespace GearTray.Util;

/// <summary>One reading of the PC's load. GPU values are null without an NVIDIA graphics card.</summary>
public sealed record PcStatsInfo(int Cpu, double RamUsedGb, double RamTotalGb, int? Gpu, int? GpuTemp, double? VramUsedGb, double? VramTotalGb);

/// <summary>
/// CPU load (GetSystemTimes, since the previous reading), memory (GlobalMemoryStatusEx) and, on NVIDIA cards, GPU
/// load, temperature and video memory through NVML, the library the NVIDIA driver installs (nvml.dll in System32).
/// CPU temperature isn't here: Windows has no driver-free way to read it on Ryzen. Call from one thread at a time.
/// </summary>
public static unsafe class PcStats
{
    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NvUtilization { public uint Gpu, Memory; }
    [StructLayout(LayoutKind.Sequential)] private struct NvMemory { public ulong Total, Free, Used; }

    private const double GiB = 1024.0 * 1024 * 1024;
    private static long _idle, _kernel, _user;
    private static bool _nvmlTried;
    private static IntPtr _gpu;
    private static delegate* unmanaged[Cdecl]<IntPtr, NvUtilization*, int> _utilization;
    private static delegate* unmanaged[Cdecl]<IntPtr, int, uint*, int> _temperature;
    private static delegate* unmanaged[Cdecl]<IntPtr, NvMemory*, int> _memory;

    public static PcStatsInfo Read()
    {
        int cpu = 0;
        if (GetSystemTimes(out long idle, out long kernel, out long user))
        {
            long di = idle - _idle, total = kernel - _kernel + (user - _user); // kernel time includes idle time
            if (_kernel != 0 && total > 0) cpu = (int)Math.Round(100.0 * (total - di) / total);
            (_idle, _kernel, _user) = (idle, kernel, user);
        }

        var m = new MEMORYSTATUSEX { dwLength = (uint)sizeof(MEMORYSTATUSEX) };
        GlobalMemoryStatusEx(ref m);

        int? gpu = null, temp = null;
        double? vramUsed = null, vramTotal = null;
        if (Nvml())
        {
            NvUtilization u;
            if (_utilization(_gpu, &u) == 0) gpu = (int)u.Gpu;
            uint t;
            if (_temperature(_gpu, 0 /* NVML_TEMPERATURE_GPU */, &t) == 0) temp = (int)t;
            NvMemory mem;
            if (_memory(_gpu, &mem) == 0) { vramUsed = mem.Used / GiB; vramTotal = mem.Total / GiB; }
        }
        return new PcStatsInfo(Math.Clamp(cpu, 0, 100), (m.ullTotalPhys - m.ullAvailPhys) / GiB, m.ullTotalPhys / GiB, gpu, temp, vramUsed, vramTotal);
    }

    /// <summary>Loads NVML from its fixed install paths (never the search path) and picks the first GPU.</summary>
    private static bool Nvml()
    {
        if (_nvmlTried) return _gpu != IntPtr.Zero;
        _nvmlTried = true;
        string[] paths = [Path.Combine(Environment.SystemDirectory, "nvml.dll"), @"C:\Program Files\NVIDIA Corporation\NVSMI\nvml.dll"];
        foreach (var path in paths)
        {
            if (!File.Exists(path) || !NativeLibrary.TryLoad(path, out var lib)) continue;
            try
            {
                var init = (delegate* unmanaged[Cdecl]<int>)NativeLibrary.GetExport(lib, "nvmlInit_v2");
                if (init() != 0) { Log.Info("NVML didn't start; no GPU stats"); return false; }
                var byIndex = (delegate* unmanaged[Cdecl]<uint, IntPtr*, int>)NativeLibrary.GetExport(lib, "nvmlDeviceGetHandleByIndex_v2");
                IntPtr device;
                if (byIndex(0, &device) != 0 || device == IntPtr.Zero) return false;
                _utilization = (delegate* unmanaged[Cdecl]<IntPtr, NvUtilization*, int>)NativeLibrary.GetExport(lib, "nvmlDeviceGetUtilizationRates");
                _temperature = (delegate* unmanaged[Cdecl]<IntPtr, int, uint*, int>)NativeLibrary.GetExport(lib, "nvmlDeviceGetTemperature");
                _memory = (delegate* unmanaged[Cdecl]<IntPtr, NvMemory*, int>)NativeLibrary.GetExport(lib, "nvmlDeviceGetMemoryInfo");
                _gpu = device;
                return true;
            }
            catch (EntryPointNotFoundException ex)
            {
                Log.Info("NVML is missing a function: " + ex.Message);
                return false;
            }
        }
        return false;
    }
}
