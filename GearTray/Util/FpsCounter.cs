using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GearTray.Util;

/// <summary>
/// Frames per second of one process, counted from Windows' own graphics events (ETW): DXGI and Direct3D 9 present
/// calls, the way PresentMon counts them. Covers DirectX 9–12 games; Vulkan and OpenGL games only show up when the
/// driver presents through DXGI. Needs admin rights (GearTray runs elevated). The real-time trace session only runs
/// while a game is watched, and is stopped again afterwards (or replaced at the next start if GearTray crashed).
/// </summary>
public static unsafe class FpsCounter
{
    private const string SessionName = "GearTray FPS";
    private static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3D9Provider = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    private const ushort DxgiPresentStart = 42, DxgiPresentMpoStart = 55, D3D9PresentStart = 1;

    // x64 layouts (evntrace.h / evntcons.h)
    private const int PropertiesSize = 120, LoggerNameOffsetField = 116, LogfileSize = 448;
    private const uint WNODE_FLAG_TRACED_GUID = 0x00020000, EVENT_TRACE_REAL_TIME_MODE = 0x100;
    private const uint PROCESS_TRACE_MODE_REAL_TIME = 0x100, PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000;
    private const uint EVENT_CONTROL_CODE_ENABLE_PROVIDER = 1, EVENT_TRACE_CONTROL_STOP = 1, ERROR_ALREADY_EXISTS = 183;
    private const ulong INVALID_PROCESSTRACE_HANDLE = ulong.MaxValue;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern uint StartTraceW(out ulong handle, string name, byte* properties);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern uint ControlTraceW(ulong handle, string? name, byte* properties, uint control);
    [DllImport("advapi32.dll")] private static extern uint EnableTraceEx2(ulong handle, Guid* provider, uint control, byte level, ulong matchAny, ulong matchAll, uint timeout, IntPtr parameters);
    [DllImport("advapi32.dll")] private static extern ulong OpenTraceW(byte* logfile);
    [DllImport("advapi32.dll")] private static extern uint ProcessTrace(ulong* handles, uint count, IntPtr start, IntPtr end);
    [DllImport("advapi32.dll")] private static extern uint CloseTrace(ulong handle);

    private static readonly object Gate = new();       // session start/stop
    private static readonly object StampGate = new();  // the counters, also taken by the ETW callback
    private static ulong _session, _trace;
    private static Thread? _thread;
    private static IntPtr _loggerName;
    private static byte* _logfile;                     // EVENT_TRACE_LOGFILEW, kept until the trace is closed
    private static bool _failed;
    private static DateTime _idleSince = DateTime.MaxValue;

    // Present timestamps (QPC) of the watched process, newest last; or per-process counts while surveying.
    private static volatile uint _pid;
    private static readonly long[] Stamps = new long[4096];
    private static int _stampCount;
    private static Dictionary<uint, int>? _survey;
    // For the average: frames and time while the game kept drawing (gaps over a second, like alt-tab, don't count).
    private static long _activeFrames, _activeTicks, _lastStamp;

    /// <summary>Count frames of this process (0 = none). The trace stops after ~10 s without a process to watch.</summary>
    public static void Watch(uint pid)
    {
        lock (Gate)
        {
            if (pid == 0)
            {
                _pid = 0;
                if (_session == 0) return;
                if (_idleSince == DateTime.MaxValue) _idleSince = DateTime.UtcNow;
                else if (DateTime.UtcNow - _idleSince > TimeSpan.FromSeconds(10)) StopLocked();
                return;
            }
            _idleSince = DateTime.MaxValue;
            if (pid != _pid)
            {
                lock (StampGate)
                {
                    _pid = pid;
                    _stampCount = 0;
                    _activeFrames = _activeTicks = _lastStamp = 0;
                }
            }
            if (_session == 0 && !_failed) StartLocked();
        }
    }

    /// <summary>
    /// Frames in the last second of the watched process's presents; its 1% low: the frame rate of the slowest 1% of
    /// frames (the 99th-percentile frame time) over the last 10 seconds; and its average since it was first seen, over
    /// the time it kept drawing. Null when it isn't presenting (or no trace); Low and Avg need enough frames first.
    /// </summary>
    public static (int? Fps, int? Low, int? Avg) Read()
    {
        lock (StampGate)
        {
            if (_session == 0 || _stampCount == 0) return (null, null, null);
            int mask = Stamps.Length - 1, oldest = Math.Max(0, _stampCount - Stamps.Length);
            long latest = Stamps[(_stampCount - 1) & mask], freq = Stopwatch.Frequency;
            if (Stopwatch.GetTimestamp() - latest > 3 * freq) return (null, null, null); // nothing for 3 s: minimized, paused or not DXGI
            int? avg = _activeTicks >= 5 * freq ? (int)Math.Round(_activeFrames * (double)freq / _activeTicks) : null;
            int frames = 0;
            var intervals = new List<long>();
            for (int i = _stampCount - 1; i > oldest; i--)
            {
                long t = Stamps[i & mask];
                if (t > latest - freq) frames++;
                if (t <= latest - 10 * freq) break;
                intervals.Add(t - Stamps[(i - 1) & mask]);
            }
            int? low = null;
            if (intervals.Count >= 100)
            {
                intervals.Sort();
                long p99 = intervals[(int)Math.Ceiling(intervals.Count * 0.99) - 1];
                if (p99 > 0) low = (int)Math.Round(freq / (double)p99);
            }
            return (frames, low, avg);
        }
    }

    /// <summary>Diagnostics: presents per second of every process, measured for the given time.</summary>
    public static List<(uint Pid, double PerSecond)> Survey(int seconds)
    {
        lock (StampGate) _survey = [];
        lock (Gate) if (_session == 0) StartLocked();
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        List<(uint, double)> result;
        lock (StampGate)
        {
            result = _survey!.Select(kv => (kv.Key, kv.Value / (double)seconds)).OrderByDescending(x => x.Item2).ToList();
            _survey = null;
        }
        Stop();
        return result;
    }

    public static void Stop()
    {
        lock (Gate) StopLocked();
    }

    private static void StartLocked()
    {
        try
        {
            uint rc = StartSession(out _session);
            if (rc == ERROR_ALREADY_EXISTS)
            {
                StopSession(); // left over from a GearTray that didn't exit cleanly
                rc = StartSession(out _session);
            }
            if (rc != 0) { Fail($"StartTrace failed ({rc})"); return; }

            foreach (var provider in new[] { DxgiProvider, D3D9Provider })
            {
                var g = provider;
                rc = EnableTraceEx2(_session, &g, EVENT_CONTROL_CODE_ENABLE_PROVIDER, 5 /* verbose */, ulong.MaxValue, 0, 0, IntPtr.Zero);
                if (rc != 0) Log.Warn($"FPS: enabling provider {provider} failed ({rc})");
            }

            _loggerName = Marshal.StringToHGlobalUni(SessionName);
            _logfile = (byte*)NativeMemory.AllocZeroed(LogfileSize);
            *(IntPtr*)(_logfile + 8) = _loggerName;                                                        // LoggerName
            *(uint*)(_logfile + 28) = PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD;      // ProcessTraceMode
            *(IntPtr*)(_logfile + 424) = (IntPtr)(delegate* unmanaged<byte*, void>)&OnEvent;              // EventRecordCallback
            _trace = OpenTraceW(_logfile);
            if (_trace == INVALID_PROCESSTRACE_HANDLE) { Fail("OpenTrace failed"); StopLocked(); return; }

            ulong trace = _trace;
            _thread = new Thread(() =>
            {
                ulong h = trace;
                ProcessTrace(&h, 1, IntPtr.Zero, IntPtr.Zero); // returns when the session stops
            }) { IsBackground = true, Name = "GearTray FPS trace" };
            _thread.Start();
            Log.Info("FPS counter started");
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    private static uint StartSession(out ulong session)
    {
        int size = PropertiesSize + (SessionName.Length + 1) * 2;
        byte* p = stackalloc byte[size];
        new Span<byte>(p, size).Clear();
        *(uint*)(p + 0) = (uint)size;                     // Wnode.BufferSize
        *(uint*)(p + 40) = 1;                             // Wnode.ClientContext: QPC timestamps
        *(uint*)(p + 44) = WNODE_FLAG_TRACED_GUID;        // Wnode.Flags
        *(uint*)(p + 48) = 64;                            // BufferSize (KB)
        *(uint*)(p + 52) = 4;                             // MinimumBuffers
        *(uint*)(p + 56) = 16;                            // MaximumBuffers
        *(uint*)(p + 64) = EVENT_TRACE_REAL_TIME_MODE;    // LogFileMode
        *(uint*)(p + 68) = 1;                             // FlushTimer (s)
        *(uint*)(p + LoggerNameOffsetField) = PropertiesSize;
        return StartTraceW(out session, SessionName, p);
    }

    private static void StopSession()
    {
        int size = PropertiesSize + (SessionName.Length + 1) * 2 + 1024;
        byte* p = stackalloc byte[size];
        new Span<byte>(p, size).Clear();
        *(uint*)(p + 0) = (uint)size;
        *(uint*)(p + LoggerNameOffsetField) = PropertiesSize;
        ControlTraceW(0, SessionName, p, EVENT_TRACE_CONTROL_STOP);
    }

    private static void StopLocked()
    {
        if (_session != 0)
        {
            StopSession();
            _session = 0;
        }
        bool ended = _thread?.Join(2000) ?? true;
        _thread = null;
        if (_trace != 0 && _trace != INVALID_PROCESSTRACE_HANDLE) CloseTrace(_trace);
        _trace = 0;
        if (ended)
        {
            // Only freed once the trace thread is done with them; otherwise a few hundred bytes are left behind.
            if (_loggerName != IntPtr.Zero) Marshal.FreeHGlobal(_loggerName);
            if (_logfile != null) NativeMemory.Free(_logfile);
        }
        _loggerName = IntPtr.Zero;
        _logfile = null;
        lock (StampGate) _stampCount = 0;
        _idleSince = DateTime.MaxValue;
    }

    private static void Fail(string why)
    {
        _failed = true; // don't retry every second; GearTray's next start tries again
        Log.Warn("FPS counter unavailable: " + why);
    }

    /// <summary>ETW callback (on the trace thread): EVENT_RECORD.EventHeader has ProcessId at 12, TimeStamp at 16, ProviderId at 24, Id at 40.</summary>
    [UnmanagedCallersOnly]
    private static void OnEvent(byte* record)
    {
        ushort id = *(ushort*)(record + 40);
        if (id != DxgiPresentStart && id != DxgiPresentMpoStart && id != D3D9PresentStart) return;
        Guid provider = *(Guid*)(record + 24);
        bool present = provider == DxgiProvider ? id != D3D9PresentStart : provider == D3D9Provider && id == D3D9PresentStart;
        if (!present) return;
        uint pid = *(uint*)(record + 12);
        lock (StampGate)
        {
            if (_survey is not null)
            {
                _survey[pid] = _survey.GetValueOrDefault(pid) + 1;
                return;
            }
            if (pid != _pid) return;
            long stamp = *(long*)(record + 16);
            Stamps[_stampCount & (Stamps.Length - 1)] = stamp;
            _stampCount++;
            long gap = stamp - _lastStamp;
            if (_lastStamp != 0 && gap > 0 && gap < Stopwatch.Frequency)
            {
                _activeTicks += gap;
                _activeFrames++;
            }
            _lastStamp = stamp;
        }
    }
}
