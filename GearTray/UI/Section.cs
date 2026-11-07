namespace GearTray.UI;

/// <summary>
/// A titled block of settings in a tab. Every block has a "hide" link; hidden blocks are listed on the General tab,
/// where they can be shown again. Rows are label | control, like a form.
/// </summary>
internal sealed class Section : TableLayoutPanel
{
    public string Id { get; }
    public string Title { get; }
    public string TabTitle { get; }

    public Section(string id, string title, string tabTitle, Action<Section> hide)
    {
        Id = id;
        Title = title;
        TabTitle = tabTitle;
        ColumnCount = 2;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Margin = new Padding(0, 0, 0, 2);
        Padding = new Padding(10, 0, 10, 4);
        ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var header = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 12, 0, 2) };
        var label = new Label { Text = title, AutoSize = true, Margin = new Padding(3, 0, 12, 0) };
        label.Font = new Font(label.Font, FontStyle.Bold);
        var link = new LinkLabel
        {
            Text = "hide", AutoSize = true, Margin = new Padding(0, 1, 0, 0),
            LinkColor = SystemColors.GrayText, ActiveLinkColor = SystemColors.HotTrack, LinkBehavior = LinkBehavior.HoverUnderline,
        };
        link.LinkClicked += (_, _) => hide(this);
        header.Controls.Add(label);
        header.Controls.Add(link);
        Controls.Add(header);
        SetColumnSpan(header, 2);
    }

    public T Row<T>(string label, T control) where T : Control
    {
        Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 16, 3) });
        control.Anchor = AnchorStyles.Left;
        Controls.Add(control);
        return control;
    }

    public Label Note(string text)
    {
        var l = new Label
        {
            Text = text, AutoSize = true, MaximumSize = new Size(580, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(3, 4, 3, 4),
        };
        Controls.Add(l);
        SetColumnSpan(l, 2);
        return l;
    }

    public T Span<T>(T control) where T : Control
    {
        if (control.Margin == new Padding(3)) control.Margin = new Padding(3, 4, 3, 4);
        Controls.Add(control);
        SetColumnSpan(control, 2);
        return control;
    }

    /// <summary>A row of controls side by side.</summary>
    public static FlowLayoutPanel Line(params Control[] controls)
    {
        var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        foreach (var c in controls) p.Controls.Add(c);
        return p;
    }

    /// <summary>A short inline label for use inside Line(...).</summary>
    public static Label Caption(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(6, 8, 4, 0) };
}

/// <summary>Base for one tab of the settings window: a scrolling body of sections and a fixed button bar.</summary>
internal abstract class SettingsTab
{
    protected TrayContext Tray { get; }
    protected SettingsForm Form { get; }
    public string Key { get; }
    public string Title { get; }
    public TabPage Page { get; }
    public List<Section> Sections { get; } = [];
    protected Label Info { get; } = new() { AutoSize = true, MaximumSize = new Size(600, 0), Margin = new Padding(13, 10, 10, 2) };
    protected FlowLayoutPanel Body { get; }
    protected FlowLayoutPanel Bar { get; }

    protected SettingsTab(SettingsForm form, TrayContext tray, string key, string title)
    {
        Form = form;
        Tray = tray;
        Key = key;
        Title = title;
        Page = new TabPage(title) { UseVisualStyleBackColor = true };
        Body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        Bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(6, 2, 6, 2) };
        Body.Controls.Add(Info);
        Page.Controls.Add(Body);
        Page.Controls.Add(Bar);
    }

    protected Section AddSection(string id, string title)
    {
        var s = new Section($"{Key}.{id}", title, Title, Form.HideSection);
        Sections.Add(s);
        Body.Controls.Add(s);
        return s;
    }

    protected Button AddButton(string text, Func<Task> onClick)
    {
        var b = Ui.Button(text, async (_, _) => await onClick());
        Bar.Controls.Add(b);
        return b;
    }

    /// <summary>Read the devices and fill the controls.</summary>
    public abstract Task LoadAsync();
    /// <summary>The tab became the visible one.</summary>
    public virtual void Activated() { }
    /// <summary>Another tab was selected or the window is closing.</summary>
    public virtual void Deactivated() { }

    protected void Status(string text) => Form.Status(text);
    protected Task<bool> Run(string message, Action action) => Form.Run(message, action);

    /// <summary>Enable/disable every input under root, except the given controls.</summary>
    protected static void EnableInputs(Control root, bool enabled, params Control[] except)
    {
        foreach (Control c in root.Controls)
        {
            if (except.Contains(c)) { c.Enabled = true; continue; }
            if (c is ComboBox or NumericUpDown or CheckBox or RadioButton or TrackBar or Button or TextBox or ListBox) c.Enabled = enabled;
            else EnableInputs(c, enabled, except);
        }
    }
}
