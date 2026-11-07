using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GearTray.Hid;

/// <summary>
/// An open HID collection. Output/input reports use overlapped I/O with timeouts;
/// feature reports go through HidD_SetFeature/HidD_GetFeature.
/// Every buffer passed in or returned includes the report ID as byte 0.
/// </summary>
public sealed unsafe class HidConnection : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly bool _overlapped;
    public HidDeviceInfo Info { get; }

    /// <summary>Log every report sent and received (CLI --trace).</summary>
    public static bool Trace { get; set; }

    private string Tag => $"{Info.VendorId:X4}:{Info.ProductId:X4}/{Info.Interface}.{Info.Collection}";

    private HidConnection(HidDeviceInfo info, SafeFileHandle handle, bool overlapped)
    {
        Info = info; _handle = handle; _overlapped = overlapped;
    }

    /// <summary>Open for output/input reports (vendor collections that allow read/write).</summary>
    public static HidConnection OpenReadWrite(HidDeviceInfo info)
    {
        var h = Native.CreateFileW(info.Path, Native.GENERIC_READ | Native.GENERIC_WRITE,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot open {info.Product} MI_{info.Interface:D2} COL{info.Collection:D2}");
        return new HidConnection(info, h, true);
    }

    /// <summary>Open read/write without overlapped I/O, for feature reports on vendor collections.</summary>
    public static HidConnection OpenSync(HidDeviceInfo info)
    {
        var h = Native.CreateFileW(info.Path, Native.GENERIC_READ | Native.GENERIC_WRITE,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot open {info.Product} MI_{info.Interface:D2} COL{info.Collection:D2}");
        return new HidConnection(info, h, false);
    }

    /// <summary>Open with zero access: enough for feature reports on system-owned keyboard/mouse collections.</summary>
    public static HidConnection OpenFeatureOnly(HidDeviceInfo info)
    {
        var h = Native.CreateFileW(info.Path, 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot open {info.Product} MI_{info.Interface:D2} COL{info.Collection:D2}");
        return new HidConnection(info, h, false);
    }

    public void Flush() => Native.HidD_FlushQueue(_handle);

    /// <summary>Write an output report. Shorter buffers are zero-padded to the report length.</summary>
    public void Write(ReadOnlySpan<byte> report, int timeoutMs = 1000)
    {
        if (!_overlapped) throw new InvalidOperationException("Connection was opened for feature reports only.");
        int len = Info.OutputLength;
        if (report.Length > len) throw new ArgumentException($"Report is {report.Length} bytes, device takes {len}.");
        var buf = new byte[len];
        report.CopyTo(buf);
        if (Trace) Util.Log.Info($"{Tag} OUT {Util.Log.Hex(buf, 24)}");
        if (!Io(write: true, buf, timeoutMs, out _))
            throw new TimeoutException($"Write to {Info.Product} timed out.");
    }

    /// <summary>Read one input report, or null on timeout.</summary>
    public byte[]? Read(int timeoutMs)
    {
        if (!_overlapped) throw new InvalidOperationException("Connection was opened for feature reports only.");
        var buf = new byte[Info.InputLength];
        if (!Io(write: false, buf, timeoutMs, out int n)) return null;
        if (Trace) Util.Log.Info($"{Tag} IN  {Util.Log.Hex(buf.AsSpan(0, n), 24)}");
        return buf[..n];
    }

    /// <summary>Write a command, then read input reports until one matches (or the timeout passes).</summary>
    public byte[]? Transact(ReadOnlySpan<byte> command, Func<byte[], bool> isReply, int timeoutMs = 600)
    {
        Flush();
        Write(command);
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            long left = deadline - Environment.TickCount64;
            if (left <= 0) return null;
            var r = Read((int)left);
            if (r is null) return null;
            if (isReply(r)) return r;
        }
    }

    public void SetFeature(ReadOnlySpan<byte> report)
    {
        int len = Math.Max(report.Length, Info.FeatureLength);
        var buf = new byte[len];
        report.CopyTo(buf);
        if (Trace) Util.Log.Info($"{Tag} SETF {Util.Log.Hex(buf, 24)}");
        fixed (byte* p = buf)
            if (!Native.HidD_SetFeature(_handle, p, len))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"SetFeature failed on {Info.Product}");
    }

    public byte[] GetFeature(byte reportId, int length)
    {
        int len = Math.Max(length, Info.FeatureLength);
        var buf = new byte[len];
        buf[0] = reportId;
        fixed (byte* p = buf)
            if (!Native.HidD_GetFeature(_handle, p, len))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"GetFeature failed on {Info.Product}");
        if (Trace) Util.Log.Info($"{Tag} GETF {Util.Log.Hex(buf, 24)}");
        return buf;
    }

    private bool Io(bool write, byte[] buffer, int timeoutMs, out int transferred)
    {
        transferred = 0;
        IntPtr evt = Native.CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
        var ov = (NativeOverlapped*)NativeMemory.AllocZeroed((nuint)sizeof(NativeOverlapped));
        var mem = (byte*)NativeMemory.Alloc((nuint)buffer.Length);
        try
        {
            ov->EventHandle = evt;
            if (write) buffer.AsSpan().CopyTo(new Span<byte>(mem, buffer.Length));
            bool ok = write
                ? Native.WriteFile(_handle, mem, buffer.Length, IntPtr.Zero, ov)
                : Native.ReadFile(_handle, mem, buffer.Length, IntPtr.Zero, ov);
            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                if (err != Native.ERROR_IO_PENDING) throw new Win32Exception(err, $"{(write ? "Write" : "Read")} failed on {Info.Product}");
                if (Native.WaitForSingleObject(evt, (uint)Math.Max(1, timeoutMs)) != Native.WAIT_OBJECT_0)
                    Native.CancelIoEx(_handle, ov);
            }
            // Waits for completion (or for the cancellation to finish) so freeing the buffers is safe.
            if (!Native.GetOverlappedResult(_handle, ov, out transferred, true))
            {
                int err = Marshal.GetLastWin32Error();
                if (err == Native.ERROR_OPERATION_ABORTED) return false;
                throw new Win32Exception(err, $"{(write ? "Write" : "Read")} failed on {Info.Product}");
            }
            if (!write) new Span<byte>(mem, transferred).CopyTo(buffer);
            return true;
        }
        finally
        {
            NativeMemory.Free(mem);
            NativeMemory.Free(ov);
            Native.CloseHandle(evt);
        }
    }

    public void Dispose() => _handle.Dispose();
}
