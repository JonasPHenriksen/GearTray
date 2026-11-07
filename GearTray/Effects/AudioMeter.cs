using System.Runtime.InteropServices;

namespace GearTray.Effects;

/// <summary>
/// Peak level of the default playback device (whatever you hear, e.g. the headset), read from the Windows
/// Core Audio meter. No audio is recorded; this is the same number the volume mixer's green bar shows.
/// </summary>
internal sealed class AudioMeter : IDisposable
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorClass { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        int GetPeakValue(out float peak);
        int GetMeteringChannelCount(out int count);
        int GetChannelsPeakValues(int count, [MarshalAs(UnmanagedType.LPArray), Out] float[] values);
    }

    private IAudioMeterInformation? _meter;
    private DateTime _nextReopen, _openedAt;

    /// <summary>Left and right peak (0–1). Returns (0, 0) when nothing is playing or no device is available.</summary>
    public (float Left, float Right) Read()
    {
        try
        {
            // Re-open now and then so a change of default device (headset off → speakers) is followed.
            if (_meter is not null && DateTime.UtcNow - _openedAt > TimeSpan.FromSeconds(10)) _meter = null;
            if (_meter is null)
            {
                if (DateTime.UtcNow < _nextReopen) return (0, 0);
                _nextReopen = DateTime.UtcNow.AddSeconds(3);
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorClass();
                Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(0 /* render */, 1 /* multimedia */, out var device));
                var iid = typeof(IAudioMeterInformation).GUID;
                Marshal.ThrowExceptionForHR(device.Activate(ref iid, 0x17 /* CLSCTX_ALL */, IntPtr.Zero, out var meter));
                _meter = (IAudioMeterInformation)meter;
                _openedAt = DateTime.UtcNow;
            }
            _meter.GetMeteringChannelCount(out int channels);
            if (channels >= 2)
            {
                var v = new float[channels];
                _meter.GetChannelsPeakValues(channels, v);
                return (v[0], v[1]);
            }
            _meter.GetPeakValue(out float p);
            return (p, p);
        }
        catch
        {
            // Default device changed or went away: open it again shortly.
            _meter = null;
            return (0, 0);
        }
    }

    public void Dispose() => _meter = null;
}
