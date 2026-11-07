using System.Runtime.InteropServices;
using GearTray.Input;

namespace GearTray.UI;

/// <summary>Click it, press a key: remembers that key (left/right Shift, Ctrl and Alt are told apart).</summary>
internal sealed class KeyCaptureBox : TextBox
{
    private const int WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104;
    [DllImport("user32.dll")] private static extern uint MapVirtualKeyW(uint code, uint mapType);

    public int Vk { get; private set; }

    public KeyCaptureBox()
    {
        ReadOnly = true;
        Width = 170;
        ShortcutsEnabled = false;
        Clear();
    }

    public new void Clear()
    {
        Vk = 0;
        Text = "click here, press a key";
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (msg.Msg is WM_KEYDOWN or WM_SYSKEYDOWN)
        {
            int vk = (int)msg.WParam;
            long lp = msg.LParam.ToInt64();
            int scan = (int)(lp >> 16) & 0xFF;
            bool extended = (lp & (1 << 24)) != 0;
            vk = vk switch
            {
                0x10 => (int)MapVirtualKeyW((uint)scan, 3 /* VSC_TO_VK_EX */),  // left/right Shift
                0x11 => extended ? 0xA3 : 0xA2,                                // right/left Ctrl
                0x12 => extended ? 0xA5 : 0xA4,                                // right/left Alt
                _ => vk,
            };
            Vk = vk;
            Text = KeyNames.Name(vk);
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
