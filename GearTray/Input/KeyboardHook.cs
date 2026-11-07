using System.Runtime.InteropServices;
using System.Text;
using GearTray.Util;

namespace GearTray.Input;

/// <summary>From = virtual-key code pressed; To = virtual-key code sent instead, or 0 to disable the key.</summary>
public sealed record KeyRemap(int From, int To)
{
    public override string ToString() => $"{KeyNames.Name(From)}  →  {(To == 0 ? "disabled" : KeyNames.Name(To))}";
}

/// <summary>
/// Low-level keyboard hook on its own thread: blocks keys while game mode is on, applies remaps, starts macros and
/// records new macros. Keys GearTray sends itself are marked and passed through. Only installed while needed.
/// Works for every keyboard, not just the Razer.
/// </summary>
public sealed unsafe class KeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13, WM_KEYUP = 0x101, WM_SYSKEYUP = 0x105, WM_QUIT = 0x12;
    private const uint LLKHF_ALTDOWN = 0x20;
    private const int VK_TAB = 0x09, VK_F4 = 0x73, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookExW(int id, HookProc proc, IntPtr module, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessageW(byte* msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PostThreadMessageW(uint threadId, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);

    private readonly HookProc _proc;            // kept alive for as long as the hook exists
    private Thread? _thread;
    private uint _threadId;
    private volatile bool _gameMode, _blockWin, _blockAltTab, _blockAltF4;
    private volatile Dictionary<int, int> _remaps = [];
    private volatile Macro[] _macros = [];
    private readonly HashSet<int> _swallowUp = [];
    private volatile Action<int, bool>? _recorder;

    /// <summary>
    /// Raised on the hook thread for every real key event while WantKeyEvents is set (the ripple effect):
    /// virtual key, scan code, extended flag, released.
    /// </summary>
    public event Action<int, int, bool, bool>? KeyEvent;
    private volatile bool _wantKeyEvents;

    public KeyboardHook() => _proc = Proc;

    public void Configure(bool gameMode, bool blockWin, bool blockAltTab, bool blockAltF4, IEnumerable<KeyRemap> remaps, IEnumerable<Macro> macros)
    {
        _gameMode = gameMode;
        _blockWin = blockWin;
        _blockAltTab = blockAltTab;
        _blockAltF4 = blockAltF4;
        _remaps = remaps.Where(r => r.From != 0).GroupBy(r => r.From).ToDictionary(g => g.Key, g => g.Last().To);
        _macros = macros.Where(m => m.Enabled && m.TriggerVk != 0 && (m.Steps.Count > 0 || !string.IsNullOrEmpty(m.Text))).ToArray();
        UpdateRunning();
    }

    /// <summary>Key events for effects (e.g. ripple). Keeps the hook installed while true.</summary>
    public bool WantKeyEvents { get => _wantKeyEvents; set { _wantKeyEvents = value; UpdateRunning(); } }

    /// <summary>While recording, every key goes to the recorder and nowhere else. Stop with StopRecording.</summary>
    public void StartRecording(Action<int, bool> recorder)
    {
        _recorder = recorder;
        UpdateRunning();
    }

    public void StopRecording()
    {
        _recorder = null;
        UpdateRunning();
    }

    private void UpdateRunning()
    {
        bool needed = (_gameMode && (_blockWin || _blockAltTab || _blockAltF4)) || _remaps.Count > 0 || _macros.Length > 0
                      || _recorder is not null || _wantKeyEvents;
        if (needed && _thread is null) Start();
        else if (!needed && _thread is not null) Stop();
    }

    private void Start()
    {
        var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            IntPtr hook = SetWindowsHookExW(WH_KEYBOARD_LL, _proc, GetModuleHandleW(null), 0);
            if (hook == IntPtr.Zero) Log.Error($"Keyboard hook failed (error {Marshal.GetLastWin32Error()})");
            ready.Set();
            byte* msg = stackalloc byte[48];
            while (GetMessageW(msg, IntPtr.Zero, 0, 0) > 0) { }
            if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
        }) { IsBackground = true, Name = "GearTray keyboard hook", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        ready.Wait(2000);
        Log.Info("Keyboard hook on");
    }

    private void Stop()
    {
        if (_thread is null) return;
        for (int i = 0; i < 20 && !PostThreadMessageW(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero); i++) Thread.Sleep(10);
        _thread.Join(2000);
        _thread = null;
        Log.Info("Keyboard hook off");
    }

    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private IntPtr Proc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var k = (KBDLLHOOKSTRUCT*)lParam;
            if (k->dwExtraInfo != MacroPlayer.Marker)
            {
                int vk = (int)k->vkCode;
                bool up = wParam == WM_KEYUP || wParam == WM_SYSKEYUP;

                if (_recorder is { } rec) { rec(vk, up); return 1; }
                if (_wantKeyEvents) KeyEvent?.Invoke(vk, (int)k->scanCode, (k->flags & 0x01) != 0, up);

                if (_gameMode)
                {
                    bool alt = (k->flags & LLKHF_ALTDOWN) != 0;
                    if (_blockWin && vk is VK_LWIN or VK_RWIN) return 1;
                    if (_blockAltTab && alt && vk == VK_TAB) return 1;
                    if (_blockAltF4 && alt && vk == VK_F4) return 1;
                }

                if (up && _swallowUp.Remove(vk)) return 1; // release of a macro trigger
                if (!up)
                {
                    foreach (var m in _macros)
                    {
                        if (m.TriggerVk != vk || m.Ctrl != Down(VK_CONTROL) || m.Shift != Down(VK_SHIFT) || m.Alt != Down(VK_MENU)) continue;
                        _swallowUp.Add(vk);
                        MacroPlayer.Play(m);
                        return 1;
                    }
                }

                if (_remaps.TryGetValue(vk, out int to))
                {
                    if (to != 0) MacroPlayer.Key(to, up);
                    return 1;
                }
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    public void Dispose() => Stop();
}

public static class KeyNames
{
    [DllImport("user32.dll")] private static extern uint MapVirtualKeyW(uint code, uint mapType);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetKeyNameTextW(int lParam, StringBuilder name, int size);

    /// <summary>Readable key name as Windows shows it ("Caps Lock", "Right Ctrl").</summary>
    public static string Name(int vk)
    {
        uint scan = MapVirtualKeyW((uint)vk, 4 /* VK_TO_VSC_EX */);
        if (scan != 0)
        {
            int lParam = (int)((scan & 0xFF) << 16) | ((scan & 0xFF00) == 0xE000 ? 1 << 24 : 0);
            var sb = new StringBuilder(64);
            if (GetKeyNameTextW(lParam, sb, sb.Capacity) > 0) return sb.ToString();
        }
        return ((Keys)vk).ToString();
    }
}
