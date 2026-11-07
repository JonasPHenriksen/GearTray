using GearTray.Util;

namespace GearTray.UI;

/// <summary>Combo box entry with a display text and a typed value.</summary>
internal sealed record Item<T>(string Text, T Value)
{
    public override string ToString() => Text;
}

internal static class Ui
{
    public static ComboBox Combo<T>(params Item<T>[] items)
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
        c.Items.AddRange(items);
        return c;
    }

    public static void Select<T>(ComboBox c, T value, string? fallbackText = null)
    {
        foreach (var o in c.Items)
            if (o is Item<T> it && EqualityComparer<T>.Default.Equals(it.Value, value)) { c.SelectedItem = o; return; }
        if (fallbackText is not null)
        {
            var extra = new Item<T>(fallbackText, value);
            c.Items.Add(extra);
            c.SelectedItem = extra;
        }
    }

    public static T Value<T>(ComboBox c) => c.SelectedItem is Item<T> it ? it.Value : default!;

    /// <summary>Two-column grid: label | control. Rows grow with content.</summary>
    public static TableLayoutPanel Grid()
    {
        var g = new TableLayoutPanel
        {
            ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top, Padding = new Padding(10, 6, 10, 6),
        };
        g.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        g.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        return g;
    }

    public static void Row(TableLayoutPanel g, string label, Control c)
    {
        g.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 16, 3) });
        c.Anchor = AnchorStyles.Left;
        g.Controls.Add(c);
    }

    /// <summary>A full-width row (heading or note) spanning both columns.</summary>
    public static Label Span(TableLayoutPanel g, string text, bool heading = false)
    {
        var l = new Label
        {
            Text = text, AutoSize = true, MaximumSize = new Size(520, 0),
            Margin = new Padding(3, heading ? 14 : 4, 3, 4),
        };
        if (heading) l.Font = new Font(l.Font, FontStyle.Bold);
        g.Controls.Add(l);
        g.SetColumnSpan(l, 2);
        return l;
    }

    public static Button ColorButton()
    {
        var b = new Button { Width = 64, Height = 26, FlatStyle = FlatStyle.Flat, Text = "" };
        b.Click += (_, _) =>
        {
            using var d = new ColorDialog { Color = b.BackColor, FullOpen = true, AnyColor = true };
            if (d.ShowDialog(b.FindForm()) == DialogResult.OK) b.BackColor = ColorUtil.Plain(d.Color);
        };
        return b;
    }

    public static Button Button(string text, EventHandler onClick)
    {
        var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 2, 8, 2), Margin = new Padding(6) };
        b.Click += onClick;
        return b;
    }
}

/// <summary>Horizontal slider with its current value shown next to it.</summary>
internal sealed class LabeledSlider : FlowLayoutPanel
{
    private readonly Label _label = new() { AutoSize = true, Margin = new Padding(4, 8, 0, 0) };
    private readonly Func<int, string> _format;
    public TrackBar Bar { get; }
    public event EventHandler? ValueChanged;

    public LabeledSlider(int min, int max, Func<int, string>? format = null, int width = 220)
    {
        _format = format ?? (v => v.ToString());
        AutoSize = true; WrapContents = false; Margin = new Padding(0);
        Bar = new TrackBar
        {
            Minimum = min, Maximum = max, TickStyle = TickStyle.None, AutoSize = false,
            Width = width, Height = 32, LargeChange = Math.Max(1, (max - min) / 10), SmallChange = 1,
            BackColor = SystemColors.Window, // match the tab page instead of a grey box
        };
        Bar.ValueChanged += (_, _) => { _label.Text = _format(Bar.Value); ValueChanged?.Invoke(this, EventArgs.Empty); };
        Controls.Add(Bar);
        Controls.Add(_label);
        _label.Text = _format(Bar.Value);
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Value
    {
        get => Bar.Value;
        set => Bar.Value = Math.Clamp(value, Bar.Minimum, Bar.Maximum);
    }
}

/// <summary>
/// Invisible top-level window that receives WM_DEVICECHANGE broadcasts (message-only windows don't),
/// and a "show settings" message from a second launch of GearTray.
/// </summary>
internal sealed class DeviceChangeWindow : NativeWindow, IDisposable
{
    private const string Caption = "GearTray device watcher";
    private const int WM_DEVICECHANGE = 0x0219, WM_POWERBROADCAST = 0x0218, PBT_POWERSETTINGCHANGE = 0x8013;
    private const int WM_SHOW_SETTINGS = 0x8000 + 0x47; // WM_APP + 0x47
    private static readonly Guid GUID_CONSOLE_DISPLAY_STATE = new("6FE69556-704A-47A0-8F24-C28D936FDA47");
    private readonly Action _onChange, _onShow;
    private readonly Action<bool> _onDisplay;
    private readonly IntPtr _powerNotify;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint msg, uint action, IntPtr changeInfo);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? className, string title);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, in Guid setting, int flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool UnregisterPowerSettingNotification(IntPtr handle);

    /// <param name="onDisplay">Called with false when the displays turn off, true when they come back.</param>
    public DeviceChangeWindow(Action onChange, Action onShow, Action<bool> onDisplay)
    {
        _onChange = onChange;
        _onShow = onShow;
        _onDisplay = onDisplay;
        CreateHandle(new CreateParams { Caption = Caption });
        // Let a non-elevated second launch reach this (possibly elevated) window.
        ChangeWindowMessageFilterEx(Handle, WM_SHOW_SETTINGS, 1 /* MSGFLT_ALLOW */, IntPtr.Zero);
        _powerNotify = RegisterPowerSettingNotification(Handle, GUID_CONSOLE_DISPLAY_STATE, 0 /* window handle */);
    }

    protected override unsafe void WndProc(ref Message m)
    {
        if (m.Msg == WM_DEVICECHANGE) _onChange();
        else if (m.Msg == WM_SHOW_SETTINGS) _onShow();
        else if (m.Msg == WM_POWERBROADCAST && (int)m.WParam == PBT_POWERSETTINGCHANGE && m.LParam != IntPtr.Zero)
        {
            // POWERBROADCAST_SETTING: GUID, DWORD length, data (0 = off, 1 = on, 2 = dimmed)
            var setting = *(Guid*)m.LParam;
            if (setting == GUID_CONSOLE_DISPLAY_STATE) _onDisplay(*(int*)((byte*)m.LParam + 20) != 0);
        }
        base.WndProc(ref m);
    }

    /// <summary>Ask the already running GearTray to open its settings window.</summary>
    public static bool SignalRunningInstance()
    {
        var h = FindWindowW(null, Caption);
        return h != IntPtr.Zero && PostMessageW(h, WM_SHOW_SETTINGS, IntPtr.Zero, IntPtr.Zero);
    }

    public void Dispose()
    {
        if (_powerNotify != IntPtr.Zero) UnregisterPowerSettingNotification(_powerNotify);
        DestroyHandle();
    }
}
