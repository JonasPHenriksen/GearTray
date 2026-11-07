using System.Runtime.InteropServices;

namespace GearTray.Util;

/// <summary>
/// 1 ms timer resolution while a software lighting effect runs. Windows' default (~15.6 ms) turns every
/// Thread.Sleep(1) between keyboard packets into 16 ms, which caps effects at about 5 frames per second.
/// Since Windows 10 2004 this only affects this process.
/// </summary>
public sealed class TimerResolution : IDisposable
{
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);

    private bool _active;

    public TimerResolution() => _active = timeBeginPeriod(1) == 0;

    public void Dispose()
    {
        if (_active) timeEndPeriod(1);
        _active = false;
    }
}
