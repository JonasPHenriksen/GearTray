using System.Runtime.InteropServices;
using System.Text;

namespace GearTray.Input;

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

    private static readonly string[] LibraryFolders = [@"\steamapps\common\", @"\XboxGames\", @"\Epic Games\", @"\GOG Galaxy\Games\", @"\GOG Games\"];
    private static readonly string[] NotGames = [@"\Epic Games\Launcher\", @"\wallpaper_engine\", @"\Lossless Scaling\", @"\Steamworks Shared\"];

    private readonly WinEventProc _proc;
    private readonly IntPtr _hook;
    private HashSet<string> _extra = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _ignored = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Exe name of the game in front, or null.</summary>
    public string? CurrentGame { get; private set; }
    /// <summary>Raised on the UI thread when CurrentGame changes.</summary>
    public event Action? Changed;

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
        string? path = ProcessPath(hwnd);
        string? game = path is not null && IsGame(path) ? Path.GetFileName(path) : null;
        if (string.Equals(game, CurrentGame, StringComparison.OrdinalIgnoreCase)) return;
        CurrentGame = game;
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

    private static string? ProcessPath(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0 || pid == Environment.ProcessId) return null;
        IntPtr h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
    }
}
