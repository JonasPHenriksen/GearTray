using System.Runtime.InteropServices;
using GearTray.Util;

namespace GearTray.Devices;

/// <summary>
/// Impulse-trigger rumble (Xbox One / Series controllers) through Windows.Gaming.Input, which is part of Windows; XInput
/// only reaches the two main motors. Windows passes this on only while one of GearTray's windows is in front. Plain
/// WinRT vtable calls (slots per windows-rs) instead of the Windows SDK projection, which would add ~25 MB.
/// </summary>
public static unsafe class GamepadTriggers
{
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string source, int length, out IntPtr hstring);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr hstring);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);
    [DllImport("ole32.dll")] private static extern int CoIncrementMTAUsage(out IntPtr cookie);

    [StructLayout(LayoutKind.Sequential)]
    private struct GamepadVibration { public double LeftMotor, RightMotor, LeftTrigger, RightTrigger; }

    private static readonly Guid IID_IGamepadStatics = new("8BBCE529-D49C-39E9-9560-E47DDE96B7C8");
    private static IntPtr _statics;
    private static bool _unavailable, _used;

    /// <summary>Starts Windows' gamepad list early: it is empty at first and fills in within a second or two.</summary>
    public static void Warm()
    {
        try { Statics(); }
        catch (Exception ex) { _unavailable = true; Log.Warn($"Windows.Gaming.Input: {ex.Message}"); }
    }

    private static IntPtr Statics()
    {
        if (_statics != IntPtr.Zero || _unavailable) return _statics;
        CoIncrementMTAUsage(out _); // keeps the runtime's helper DLLs loaded (SDL works around the same Windows bug)
        const string cls = "Windows.Gaming.Input.Gamepad";
        if (WindowsCreateString(cls, cls.Length, out var name) < 0) { _unavailable = true; return IntPtr.Zero; }
        var iid = IID_IGamepadStatics;
        int hr = RoGetActivationFactory(name, ref iid, out _statics);
        WindowsDeleteString(name);
        if (hr < 0)
        {
            _unavailable = true;
            _statics = IntPtr.Zero;
            Log.Warn($"Windows.Gaming.Input not available (0x{hr:X8})");
        }
        return _statics;
    }

    /// <summary>
    /// Sets all four motors (0–1: main left/right, trigger left/right) on every gamepad Windows.Gaming.Input lists.
    /// Returns how many took it, 0 when none is listed (yet), -1 when the API is missing.
    /// </summary>
    public static int Set(double leftMotor, double rightMotor, double leftTrigger, double rightTrigger)
    {
        var statics = Statics();
        if (statics == IntPtr.Zero) return -1;
        if (leftMotor > 0 || rightMotor > 0 || leftTrigger > 0 || rightTrigger > 0) _used = true;
        var v = new GamepadVibration
        {
            LeftMotor = Math.Clamp(leftMotor, 0, 1), RightMotor = Math.Clamp(rightMotor, 0, 1),
            LeftTrigger = Math.Clamp(leftTrigger, 0, 1), RightTrigger = Math.Clamp(rightTrigger, 0, 1),
        };
        IntPtr view;
        // IGamepadStatics slot 10: get_Gamepads(out IVectorView<Gamepad>)
        if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)(*(void***)statics)[10])(statics, &view) < 0 || view == IntPtr.Zero) return 0;
        try
        {
            void** vv = *(void***)view;
            uint size;
            if (((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)vv[7])(view, &size) < 0) return 0;   // get_Size
            int n = 0;
            for (uint i = 0; i < size; i++)
            {
                IntPtr pad;
                if (((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)vv[6])(view, i, &pad) < 0 || pad == IntPtr.Zero) continue; // GetAt
                // IGamepad slot 7: put_Vibration(GamepadVibration), the struct passed by value
                if (((delegate* unmanaged[Stdcall]<IntPtr, GamepadVibration, int>)(*(void***)pad)[7])(pad, v) >= 0) n++;
                Release(pad);
            }
            return n;
        }
        finally { Release(view); }
    }

    /// <summary>Stops everything this API started, if GearTray ever started anything.</summary>
    public static void Stop()
    {
        if (!_used) return;
        try { Set(0, 0, 0, 0); }
        catch (Exception ex) { Log.Warn($"Stopping trigger rumble: {ex.Message}"); }
    }

    private static void Release(IntPtr p) => ((delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(void***)p)[2])(p);
}
