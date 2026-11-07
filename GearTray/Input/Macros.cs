using System.Runtime.InteropServices;

namespace GearTray.Input;

public sealed record MacroStep(int Vk, bool Up, int DelayMs);

/// <summary>
/// A key (optionally with Ctrl/Shift/Alt) that plays recorded key presses with their timing, or types a text.
/// Runs in software, so it works with any keyboard while GearTray is running.
/// </summary>
public sealed class Macro
{
    public string Name { get; set; } = "Macro";
    public bool Enabled { get; set; } = true;
    public int TriggerVk { get; set; }
    public bool Ctrl { get; set; }
    public bool Shift { get; set; }
    public bool Alt { get; set; }
    /// <summary>Recorded key presses (used when Text is empty).</summary>
    public List<MacroStep> Steps { get; set; } = [];
    /// <summary>Text to type instead of key presses.</summary>
    public string? Text { get; set; }
    /// <summary>Play the recorded delays (false = as fast as possible, 10 ms apart).</summary>
    public bool KeepTiming { get; set; } = true;

    public string TriggerName =>
        (Ctrl ? "Ctrl+" : "") + (Shift ? "Shift+" : "") + (Alt ? "Alt+" : "") + (TriggerVk == 0 ? "?" : KeyNames.Name(TriggerVk));

    public string Summary => !string.IsNullOrEmpty(Text)
        ? $"types “{(Text.Length > 30 ? Text[..30] + "…" : Text)}”"
        : $"{Steps.Count(s => !s.Up)} key presses";

    public override string ToString() => $"{(Enabled ? "" : "(off) ")}{TriggerName}  →  {Name}: {Summary}";
}

/// <summary>Plays macros with SendInput on a background thread. Keys we send are marked so the hook ignores them.</summary>
public static unsafe class MacroPlayer
{
    public static readonly IntPtr Marker = 0x4754_4B42; // "GTKB", shared with KeyboardHook

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT
    {
        [FieldOffset(0)] public int type;
        [FieldOffset(8)] public ushort wVk;
        [FieldOffset(10)] public ushort wScan;
        [FieldOffset(12)] public uint dwFlags;
        [FieldOffset(16)] public uint time;
        [FieldOffset(24)] public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll")] private static extern uint SendInput(uint count, INPUT* inputs, int size);
    [DllImport("user32.dll")] private static extern uint MapVirtualKeyW(uint code, uint mapType);

    private static int _playing;

    /// <summary>Starts playing unless a macro is already playing.</summary>
    public static void Play(Macro m)
    {
        if (Interlocked.Exchange(ref _playing, 1) == 1) return;
        var steps = m.Steps.ToList();
        string? text = m.Text;
        bool timing = m.KeepTiming;
        new Thread(() =>
        {
            try
            {
                Thread.Sleep(30); // let the trigger key's own release go through first
                if (!string.IsNullOrEmpty(text)) TypeText(text);
                else
                    foreach (var s in steps)
                    {
                        Thread.Sleep(timing ? Math.Clamp(s.DelayMs, 0, 10_000) : 10);
                        Key(s.Vk, s.Up);
                    }
            }
            finally { Volatile.Write(ref _playing, 0); }
        }) { IsBackground = true, Name = "GearTray macro" }.Start();
    }

    /// <summary>Press or release a key as its scan code (what games read), like AutoHotkey.</summary>
    public static void Key(int vk, bool up)
    {
        uint scan = MapVirtualKeyW((uint)vk, 4 /* MAPVK_VK_TO_VSC_EX */);
        var input = new INPUT
        {
            type = 1,
            wScan = (ushort)(scan & 0xFF),
            dwFlags = 0x8 /* SCANCODE */ | (up ? 0x2u : 0) | ((scan & 0xFF00) == 0xE000 ? 0x1u : 0),
            dwExtraInfo = Marker,
        };
        if (scan == 0) { input.wVk = (ushort)vk; input.dwFlags &= ~0x8u; } // keys without a scan code (media keys)
        SendInput(1, &input, sizeof(INPUT));
    }

    private static void TypeText(string text)
    {
        foreach (char ch in text.Replace("\r\n", "\n"))
        {
            if (ch == '\n') { Key(0x0D, false); Key(0x0D, true); continue; }
            var down = new INPUT { type = 1, wScan = ch, dwFlags = 0x4 /* UNICODE */, dwExtraInfo = Marker };
            var up = down with { };
            up.dwFlags |= 0x2;
            SendInput(1, &down, sizeof(INPUT));
            SendInput(1, &up, sizeof(INPUT));
            Thread.Sleep(5);
        }
    }
}
