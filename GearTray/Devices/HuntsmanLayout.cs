using System.Runtime.InteropServices;
using System.Text;

namespace GearTray.Devices;

/// <summary>One LED of the Huntsman Elite's matrix. ScanCode/Extended locate it from a key press (0 = no key).</summary>
public sealed record KeyCell(int Row, int Col, string Name, int ScanCode, bool Extended, int Vk, KeyCellKind Kind = KeyCellKind.All)
{
    public bool IsUnderglow => Row >= 6;
}

public enum KeyCellKind { All, AnsiOnly, IsoOnly }

/// <summary>
/// Razer Huntsman Elite LED matrix (9 rows × 22 columns): rows 0–5 keys, rows 6–7 the keyboard's underglow, row 8 the
/// wrist rest. Positions per OpenRGB's huntsman_elite_keymap, OpenRazer and Polychromatic; scan codes are Windows' set-1
/// codes as seen in a low-level keyboard hook. Key names come from the active Windows keyboard layout when possible.
/// </summary>
public static class HuntsmanLayout
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetKeyNameTextW(int lParam, StringBuilder name, int size);

    public static readonly IReadOnlyList<KeyCell> Cells = Build();

    private static List<KeyCell> Build()
    {
        var c = new List<KeyCell>();
        void K(int row, int col, string name, int scan = 0, bool ext = false, int vk = 0, KeyCellKind kind = KeyCellKind.All) =>
            c.Add(new KeyCell(row, col, name, scan, ext, vk, kind));

        // row 0
        K(0, 1, "Esc", 0x01);
        for (int i = 0; i < 10; i++) K(0, 3 + i, $"F{i + 1}", 0x3B + i);
        K(0, 13, "F11", 0x57); K(0, 14, "F12", 0x58);
        K(0, 15, "PrtSc", 0x37, true); K(0, 16, "ScrLk", 0x46); K(0, 17, "Pause", vk: 0x13);
        K(0, 18, "⏮", vk: 0xB1); K(0, 19, "⏯", vk: 0xB3); K(0, 20, "⏭", vk: 0xB0); K(0, 21, "Dial", vk: 0xAD);
        // row 1
        K(1, 1, "`", 0x29);
        for (int i = 0; i < 10; i++) K(1, 2 + i, ((i + 1) % 10).ToString(), 0x02 + i);
        K(1, 12, "-", 0x0C); K(1, 13, "=", 0x0D); K(1, 14, "Bksp", 0x0E);
        K(1, 15, "Ins", 0x52, true); K(1, 16, "Home", 0x47, true); K(1, 17, "PgUp", 0x49, true);
        K(1, 18, "Num", vk: 0x90); K(1, 19, "/", 0x35, true); K(1, 20, "*", 0x37); K(1, 21, "-", 0x4A);
        // row 2
        K(2, 1, "Tab", 0x0F);
        string q = "QWERTYUIOP";
        for (int i = 0; i < q.Length; i++) K(2, 2 + i, q[i].ToString(), 0x10 + i);
        K(2, 12, "[", 0x1A); K(2, 13, "]", 0x1B); K(2, 14, "\\", 0x2B, kind: KeyCellKind.AnsiOnly);
        K(2, 15, "Del", 0x53, true); K(2, 16, "End", 0x4F, true); K(2, 17, "PgDn", 0x51, true);
        K(2, 18, "7", 0x47); K(2, 19, "8", 0x48); K(2, 20, "9", 0x49); K(2, 21, "+", 0x4E);
        // row 3
        K(3, 1, "Caps", 0x3A);
        string a = "ASDFGHJKL";
        for (int i = 0; i < a.Length; i++) K(3, 2 + i, a[i].ToString(), 0x1E + i);
        K(3, 11, ";", 0x27); K(3, 12, "'", 0x28); K(3, 13, "#", 0x2B, kind: KeyCellKind.IsoOnly); K(3, 14, "Enter", 0x1C);
        K(3, 18, "4", 0x4B); K(3, 19, "5", 0x4C); K(3, 20, "6", 0x4D);
        // row 4
        K(4, 1, "Shift", 0x2A); K(4, 2, "<", 0x56, kind: KeyCellKind.IsoOnly);
        string z = "ZXCVBNM";
        for (int i = 0; i < z.Length; i++) K(4, 3 + i, z[i].ToString(), 0x2C + i);
        K(4, 10, ",", 0x33); K(4, 11, ".", 0x34); K(4, 12, "/", 0x35); K(4, 14, "Shift", 0x36);
        K(4, 16, "▲", 0x48, true);
        K(4, 18, "1", 0x4F); K(4, 19, "2", 0x50); K(4, 20, "3", 0x51); K(4, 21, "Ent", 0x1C, true);
        // row 5
        K(5, 1, "Ctrl", 0x1D); K(5, 2, "Win", 0x5B, true); K(5, 3, "Alt", 0x38); K(5, 7, "Space", 0x39);
        K(5, 11, "AltGr", 0x38, true); K(5, 12, "Fn"); K(5, 13, "Menu", 0x5D, true); K(5, 14, "Ctrl", 0x1D, true);
        K(5, 15, "◀", 0x4B, true); K(5, 16, "▼", 0x50, true); K(5, 17, "▶", 0x4D, true);
        K(5, 19, "0", 0x52); K(5, 20, ".", 0x53);
        // underglow (no keys): rows 6–7 cols 0–18 around the keyboard, row 8 cols 0–19 the wrist rest
        for (int col = 0; col <= 18; col++) K(6, col, "");
        for (int col = 0; col <= 18; col++) K(7, col, "");
        for (int col = 0; col <= 19; col++) K(8, col, "");
        return c;
    }

    /// <summary>ISO keyboards (extra key beside left Shift, tall Enter) — from the keyboard's own layout byte.</summary>
    public static bool IsIso(int layoutId) => layoutId is not (0x01 or 0x05 or 0x08 or 0x0A or 0x0B or 0x14 or 0x81 or 0);

    public static string LayoutName(int id) => id switch
    {
        0x01 => "US", 0x02 => "Greek", 0x03 => "German", 0x04 => "French", 0x05 => "Russian", 0x06 => "UK", 0x07 => "Nordic",
        0x08 => "Chinese (Traditional)", 0x09 => "Korean", 0x0A => "Turkish", 0x0B => "Thai", 0x0C => "Japanese", 0x0D => "Portuguese (Brazil)",
        0x0E => "Spanish (Latin America)", 0x0F => "Swiss", 0x10 => "Spanish", 0x11 => "Italian", 0x12 => "Portuguese", 0x13 => "Hebrew",
        0x14 => "Arabic", 0x81 => "US (Mac)", _ => $"layout {id:X2}",
    };

    /// <summary>The cells that exist on a keyboard with this layout.</summary>
    public static IEnumerable<KeyCell> For(bool iso) =>
        Cells.Where(k => k.Kind == KeyCellKind.All || (iso ? k.Kind == KeyCellKind.IsoOnly : k.Kind == KeyCellKind.AnsiOnly));

    /// <summary>The matrix position of a key press seen by the keyboard hook, or null.</summary>
    public static (int Row, int Col)? Locate(int vk, int scanCode, bool extended, bool iso)
    {
        if (vk is 0x13 or 0x90 or 0xB0 or 0xB1 or 0xB3 or 0xAD or 0xAE or 0xAF)
        {
            int dialVk = vk is 0xAE or 0xAF ? 0xAD : vk;
            var byVk = Cells.FirstOrDefault(k => k.Vk == dialVk);
            return byVk is null ? null : (byVk.Row, byVk.Col);
        }
        if (scanCode == 0x2B) return iso ? (3, 13) : (2, 14);
        var cell = Cells.FirstOrDefault(k => k.ScanCode == scanCode && k.Extended == extended && k.Row < 6);
        return cell is null ? null : (cell.Row, cell.Col);
    }

    /// <summary>The key's name in the active Windows keyboard layout (e.g. Å, Ø, Æ on Nordic), else the US name.</summary>
    public static string Label(KeyCell k)
    {
        // Only single-character keys (letters, digits, punctuation) change with the keyboard layout.
        if (k.ScanCode == 0 || k.Name.Length != 1 || !char.IsAscii(k.Name[0])) return k.Name;
        var sb = new StringBuilder(32);
        int lParam = (k.ScanCode << 16) | (k.Extended ? 1 << 24 : 0);
        if (GetKeyNameTextW(lParam, sb, sb.Capacity) > 0)
        {
            string s = sb.ToString();
            if (s.Length == 1) return s.ToUpperInvariant();
        }
        return k.Name;
    }
}
