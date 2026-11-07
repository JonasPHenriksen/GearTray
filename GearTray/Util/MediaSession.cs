using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GearTray.Util;

/// <summary>
/// What's playing right now. Position is counted forward from the player's last real report while it plays; Duration
/// is zero until the player has reported one for this track. PlayingFor is how long it has been playing without a pause
/// (as far as GearTray has seen), for when the position isn't known.
/// </summary>
public sealed record MediaInfo(string Title, string Artist, string App, bool Playing, TimeSpan Position, TimeSpan Duration, TimeSpan PlayingFor);

/// <summary>
/// The current media session from Windows' own media controls (Windows.Media.Control: Spotify, browsers, most
/// players). Plain WinRT vtable calls, so no Windows SDK projection is needed; the interface ids and slot numbers were
/// read from this PC's Windows.Media.winmd. Asynchronous calls are polled for at most 1.5 s. Call from one thread at a
/// time (the base-station screen's timer).
/// </summary>
public static unsafe class MediaSession
{
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string source, int length, out IntPtr hstring);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr hstring);
    [DllImport("combase.dll")] private static extern char* WindowsGetStringRawBuffer(IntPtr hstring, out uint length);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);
    [DllImport("ole32.dll")] private static extern int CoIncrementMTAUsage(out IntPtr cookie);

    private const string ManagerClass = "Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager";
    private static readonly Guid IID_ManagerStatics = new("2050c4ee-11a0-57de-aed7-c97c70338245");
    private static readonly Guid IID_IAsyncInfo = new("00000036-0000-0000-c000-000000000046");
    private const int AsyncStarted = 0, AsyncCompleted = 1, StatusPlaying = 4;

    private static IntPtr _manager;
    private static DateTime _retryAt;
    private static bool _mtaKept;

    /// <summary>Diagnostics: the raw timeline values of the last read, for --screen-data.</summary>
    public static string LastTimeline { get; private set; } = "";

    // The current track's last real timeline, counted forward while it plays. Firefox reports an all-zero timeline
    // while a video plays and real values only at moments like pausing or seeking, so zeros are never trusted.
    private static string? _track;
    private static TimeSpan _duration, _basePosition;
    private static DateTime _baseAt, _playingSince;
    private static bool _wasPlaying;

    /// <summary>The session Windows considers current, or null when nothing has a media session (or on any failure).</summary>
    public static MediaInfo? Read()
    {
        if (DateTime.UtcNow < _retryAt) return null;
        try
        {
            if (_manager == IntPtr.Zero && !Connect())
            {
                _retryAt = DateTime.UtcNow.AddSeconds(30);
                return null;
            }
            return ReadCurrent();
        }
        catch (Exception ex)
        {
            Log.Warn("Now playing: " + ex.Message);
            Release(_manager);
            _manager = IntPtr.Zero;
            _retryAt = DateTime.UtcNow.AddSeconds(30);
            return null;
        }
    }

    private static bool Connect()
    {
        if (!_mtaKept) { CoIncrementMTAUsage(out _); _mtaKept = true; } // timer threads then count as MTA members
        if (WindowsCreateString(ManagerClass, ManagerClass.Length, out var name) < 0) return false;
        IntPtr statics;
        try
        {
            var iid = IID_ManagerStatics;
            if (RoGetActivationFactory(name, ref iid, out statics) < 0 || statics == IntPtr.Zero) return false;
        }
        finally { WindowsDeleteString(name); }
        try
        {
            // IGlobalSystemMediaTransportControlsSessionManagerStatics slot 6: RequestAsync
            if (GetPtr(statics, 6, out var op) < 0 || op == IntPtr.Zero) return false;
            _manager = AwaitResult(op);
            return _manager != IntPtr.Zero;
        }
        finally { Release(statics); }
    }

    private static MediaInfo? ReadCurrent()
    {
        // IGlobalSystemMediaTransportControlsSessionManager slot 6: GetCurrentSession
        if (GetPtr(_manager, 6, out var session) < 0) throw new COMException("GetCurrentSession failed");
        if (session == IntPtr.Zero) return null;
        try
        {
            // Session slots: 6 SourceAppUserModelId, 7 TryGetMediaPropertiesAsync, 8 GetTimelineProperties, 9 GetPlaybackInfo
            bool playing = false;
            if (GetPtr(session, 9, out var playback) >= 0 && playback != IntPtr.Zero)
            {
                if (GetInt(playback, 7, out int status) >= 0) playing = status == StatusPlaying; // PlaybackInfo slot 7: PlaybackStatus
                Release(playback);
            }

            string title = "", artist = "";
            if (GetPtr(session, 7, out var op) >= 0 && op != IntPtr.Zero)
            {
                var props = AwaitResult(op);
                if (props != IntPtr.Zero)
                {
                    title = HString(props, 6);   // MediaProperties slot 6: Title
                    artist = HString(props, 9);  // slot 9: Artist
                    Release(props);
                }
            }

            long start = 0, end = 0, pos = 0, updated = 0;
            if (GetPtr(session, 8, out var timeline) >= 0 && timeline != IntPtr.Zero)
            {
                // TimelineProperties slots: 6 StartTime, 7 EndTime, 8/9 Min/MaxSeekTime, 10 Position (TimeSpan ticks), 11 LastUpdatedTime (FILETIME)
                GetLong(timeline, 6, out start);
                GetLong(timeline, 7, out end);
                GetLong(timeline, 8, out long minSeek);
                GetLong(timeline, 9, out long maxSeek);
                GetLong(timeline, 10, out pos);
                GetLong(timeline, 11, out updated);
                Release(timeline);
                LastTimeline = $"start {TimeSpan.FromTicks(start)}, end {TimeSpan.FromTicks(end)}, seek {TimeSpan.FromTicks(minSeek)}-{TimeSpan.FromTicks(maxSeek)}, " +
                               $"position {TimeSpan.FromTicks(pos)}, updated {(updated > 0 ? DateTime.FromFileTime(updated).ToString("HH:mm:ss.fff") : "never")}";
            }
            string app = FriendlyApp(HString(session, 6));
            title = title.Trim();
            artist = artist.Trim();
            var (position, duration, playingFor) = Track(app + "\n" + title + "\n" + artist, playing, start, end, pos, updated);
            return new MediaInfo(title, artist, app, playing, position, duration, playingFor);
        }
        finally { Release(session); }
    }

    /// <summary>Keeps the track's last real timeline and counts it forward while playing.</summary>
    private static (TimeSpan Position, TimeSpan Duration, TimeSpan PlayingFor) Track(string key, bool playing, long start, long end, long pos, long updated)
    {
        var now = DateTime.UtcNow;
        if (key != _track)
        {
            _track = key;
            _duration = _basePosition = TimeSpan.Zero;
            _baseAt = _playingSince = now;
            _wasPlaying = playing;
        }
        else if (playing != _wasPlaying)
        {
            // Paused or resumed: freeze the estimate, or start counting from it again.
            _basePosition = Estimate(_wasPlaying, now);
            _baseAt = now;
            _wasPlaying = playing;
            if (playing) _playingSince = now;
        }
        if (end > start)
        {
            // A real timeline: trust it, counted from the moment the player reported it.
            _duration = TimeSpan.FromTicks(end - start);
            _basePosition = TimeSpan.FromTicks(Math.Clamp(pos - start, 0, end - start));
            _baseAt = updated > 0 ? DateTime.FromFileTimeUtc(updated) : now;
            if (_baseAt > now || now - _baseAt > TimeSpan.FromHours(12)) _baseAt = now;
        }
        var position = Estimate(playing, now);
        if (_duration > TimeSpan.Zero && position > _duration) position = _duration;
        return (position, _duration, playing ? now - _playingSince : TimeSpan.Zero);
    }

    private static TimeSpan Estimate(bool playing, DateTime now) => playing ? _basePosition + (now - _baseAt) : _basePosition;

    /// <summary>Waits for an IAsyncOperation, returns its result (or 0) and releases the operation.</summary>
    private static IntPtr AwaitResult(IntPtr op)
    {
        try
        {
            var iid = IID_IAsyncInfo;
            IntPtr info;
            if (((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Vt(op)[0])(op, &iid, &info) < 0 || info == IntPtr.Zero) return IntPtr.Zero;
            try
            {
                var clock = Stopwatch.StartNew();
                int status;
                while (GetInt(info, 7, out status) >= 0 && status == AsyncStarted) // IAsyncInfo slot 7: Status
                {
                    if (clock.ElapsedMilliseconds > 1500)
                    {
                        ((delegate* unmanaged[Stdcall]<IntPtr, int>)Vt(info)[9])(info); // slot 9: Cancel
                        return IntPtr.Zero;
                    }
                    Thread.Sleep(5);
                }
                if (status != AsyncCompleted) return IntPtr.Zero;
                return GetPtr(op, 8, out var result) >= 0 ? result : IntPtr.Zero; // IAsyncOperation slot 8: GetResults
            }
            finally { Release(info); }
        }
        finally { Release(op); }
    }

    private static string FriendlyApp(string aumid)
    {
        string a = aumid.ToLowerInvariant();
        if (a.Contains("spotify")) return "Spotify";
        if (a.Contains("msedge")) return "Edge";
        if (a.Contains("chrome")) return "Chrome";
        if (a.Contains("firefox") || a == "308046b0af4a39cb") return "Firefox";
        if (a.Contains("zunemusic") || a.Contains("mediaplayer")) return "Media Player";
        if (a.Contains("vlc")) return "VLC";
        int bang = aumid.IndexOf('!');
        string name = bang >= 0 ? aumid[(bang + 1)..] : aumid;
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    private static void** Vt(IntPtr p) => *(void***)p;

    private static int GetPtr(IntPtr obj, int slot, out IntPtr result)
    {
        IntPtr r = IntPtr.Zero;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Vt(obj)[slot])(obj, &r);
        result = hr >= 0 ? r : IntPtr.Zero;
        return hr;
    }

    private static int GetInt(IntPtr obj, int slot, out int value)
    {
        int v = 0;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Vt(obj)[slot])(obj, &v);
        value = v;
        return hr;
    }

    private static int GetLong(IntPtr obj, int slot, out long value)
    {
        long v = 0;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, long*, int>)Vt(obj)[slot])(obj, &v);
        value = hr >= 0 ? v : 0;
        return hr;
    }

    private static string HString(IntPtr obj, int slot)
    {
        if (GetPtr(obj, slot, out var h) < 0 || h == IntPtr.Zero) return "";
        try
        {
            char* p = WindowsGetStringRawBuffer(h, out uint length);
            return p == null ? "" : new string(p, 0, (int)length);
        }
        finally { WindowsDeleteString(h); }
    }

    private static void Release(IntPtr p)
    {
        if (p != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Vt(p)[2])(p);
    }
}
