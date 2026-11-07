using System.Runtime.InteropServices;
using System.Text;

namespace GearTray.Input;

/// <summary>A game process: display name, process id and creation time (FILETIME, to tell a reused pid apart).</summary>
public sealed record GameSession(string Name, uint Pid, long CreatedFileTime)
{
    public DateTime Started => DateTime.FromFileTime(CreatedFileTime);
}

/// <summary>
/// Watches which program is in front and decides whether it's a game: anything installed in a Steam, Epic,
/// Xbox or GOG game library folder, plus the extra program names the user lists. Must be created on the UI thread.
/// </summary>
public sealed class GameDetector : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 3, WINEVENT_OUTOFCONTEXT = 0;
    private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int obj, int child, uint thread, uint time);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageNameW(IntPtr h, uint flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    private const uint QUERY_LIMITED_INFORMATION = 0x1000, STILL_ACTIVE = 259;

    private static readonly string[] LibraryFolders = [@"\steamapps\common\", @"\XboxGames\", @"\Epic Games\", @"\GOG Galaxy\Games\", @"\GOG Games\"];
    private static readonly string[] NotGames = [@"\Epic Games\Launcher\", @"\wallpaper_engine\", @"\Lossless Scaling\", @"\Steamworks Shared\"];

    private readonly WinEventProc _proc;
    private readonly IntPtr _hook;
    private HashSet<string> _extra = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _ignored = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Exe name of the game in front, or null.</summary>
    public string? CurrentGame { get; private set; }
    /// <summary>Exe name of whatever program is in front (games or not), or null.</summary>
    public string? CurrentProgram { get; private set; }
    /// <summary>Raised on the UI thread when CurrentGame or CurrentProgram changes.</summary>
    public event Action? Changed;

    /// <summary>The last game that was in front: its name (library folder, else product name) and when it started.</summary>
    public GameSession? LastGame { get; private set; }

    /// <summary>LastGame if that game is still running (in front or not), else null. Safe from any thread.</summary>
    public GameSession? RunningGame()
    {
        var g = LastGame;
        if (g is null) return null;
        IntPtr h = OpenProcess(QUERY_LIMITED_INFORMATION, false, g.Pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            return GetExitCodeProcess(h, out uint code) && code == STILL_ACTIVE
                && GetProcessTimes(h, out long created, out _, out _, out _) && created == g.CreatedFileTime ? g : null;
        }
        finally { CloseHandle(h); }
    }

    public GameDetector()
    {
        _proc = OnForeground;
        _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT);
    }

    /// <summary>Extra games and ignored programs, as exe names ("eldenring.exe").</summary>
    public void Configure(IEnumerable<string> extraGames, IEnumerable<string> ignored)
    {
        _extra = new HashSet<string>(extraGames.Select(Normalize).Where(s => s.Length > 0), StringComparer.OrdinalIgnoreCase);
        _ignored = new HashSet<string>(ignored.Select(Normalize).Where(s => s.Length > 0), StringComparer.OrdinalIgnoreCase);
        Evaluate(GetForegroundWindow());
    }

    private static string Normalize(string s)
    {
        s = s.Trim();
        return s.Length == 0 || s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? s : s + ".exe";
    }

    private void OnForeground(IntPtr hook, uint evt, IntPtr hwnd, int obj, int child, uint thread, uint time) => Evaluate(hwnd);

    private void Evaluate(IntPtr hwnd)
    {
        string? path = ProcessPath(hwnd, out bool ownWindow, out uint pid, out long created);
        if (ownWindow) return; // GearTray's own windows don't count as switching away
        string? program = path is null ? null : Path.GetFileName(path);
        string? game = path is not null && IsGame(path) ? program : null;
        if (game is not null && path is not null && (LastGame is null || LastGame.Pid != pid || LastGame.CreatedFileTime != created))
            LastGame = new GameSession(DisplayName(path), pid, created);
        if (string.Equals(game, CurrentGame, StringComparison.OrdinalIgnoreCase)
            && string.Equals(program, CurrentProgram, StringComparison.OrdinalIgnoreCase)) return;
        CurrentGame = game;
        CurrentProgram = program;
        Changed?.Invoke();
    }

    private bool IsGame(string path)
    {
        string exe = Path.GetFileName(path);
        if (_ignored.Contains(exe)) return false;
        if (_extra.Contains(exe)) return true;
        if (NotGames.Any(n => path.Contains(n, StringComparison.OrdinalIgnoreCase))) return false;
        return LibraryFolders.Any(f => path.Contains(f, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ProcessPath(IntPtr hwnd, out bool ownWindow, out uint pid, out long created)
    {
        ownWindow = false;
        pid = 0;
        created = 0;
        if (hwnd == IntPtr.Zero) return null;
        GetWindowThreadProcessId(hwnd, out pid);
        if (pid == Environment.ProcessId) { ownWindow = true; return null; }
        if (pid == 0) return null;
        IntPtr h = OpenProcess(QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            if (GetProcessTimes(h, out long c, out _, out _, out _)) created = c;
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>The game's folder in its library ("ELDEN RING"), else the exe's product name, else the exe name.</summary>
    private static string DisplayName(string path)
    {
        foreach (var folder in LibraryFolders)
        {
            int i = path.IndexOf(folder, StringComparison.OrdinalIgnoreCase);
            if (i < 0) continue;
            int start = i + folder.Length, end = path.IndexOf('\\', start);
            if (end > start) return path[start..end];
        }
        try
        {
            string? product = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).ProductName;
            if (!string.IsNullOrWhiteSpace(product)) return product.Trim();
        }
        catch (Exception) { }
        return Path.GetFileNameWithoutExtension(path);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
    }
}
