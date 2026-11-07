using GearTray.Devices;
using GearTray.Util;

namespace GearTray.UI.Tabs;

/// <summary>
/// Arctis Nova Pro Wireless: noise control, sound, microphone, equalizer, line out / stream mix, Bluetooth, wireless,
/// power and base station (incl. GearTray's own screen), firmware.
/// </summary>
internal sealed class HeadsetTab : SettingsTab
{
    private readonly RadioButton _ncAnc = new() { Text = "Noise cancelling", AutoSize = true };
    private readonly RadioButton _ncTrans = new() { Text = "Transparency", AutoSize = true };
    private readonly RadioButton _ncOff = new() { Text = "Off", AutoSize = true };
    private readonly LabeledSlider _transparency = new(1, 10);
    private readonly LabeledSlider _volume = new(0, 100, v => $"{v}%");
    private readonly ComboBox _gain = Ui.Combo(new Item<bool>("Low", false), new("High (for high-impedance listening)", true));
    private readonly ComboBox _sidetone = Ui.Combo(new Item<int>("Off", 0), new("Low", 1), new("Medium", 2), new("High", 3));
    private readonly LabeledSlider _micVol = new(1, 10);
    private readonly LabeledSlider _micLed = new(1, 10);
    private readonly ComboBox _eq = Ui.Combo(ArctisNovaProWireless.EqPresetNames.Select((n, i) => new Item<int>(n, i)).ToArray());
    private readonly TrackBar[] _eqBands = new TrackBar[10];
    private readonly ComboBox _lineOut = Ui.Combo(new Item<bool>("ChatMix (game and chat)", false), new("Stream mix (what your stream hears)", true));
    private readonly LabeledSlider _streamMain = new(0, 100, v => $"{v}%"), _streamAux = new(0, 100, v => $"{v}%"), _streamMic = new(0, 100, v => $"{v}%");
    private readonly Label _btStatus = new() { AutoSize = true };
    private readonly CheckBox _btAuto = new() { Text = "Turn Bluetooth on together with the headset", AutoSize = true };
    private readonly ComboBox _btCall = Ui.Combo(new Item<int>("Do nothing", 0), new("Lower other audio (−12 dB)", 1), new("Mute other audio", 2));
    private readonly ComboBox _wireless = Ui.Combo(new Item<bool>("Speed (lowest delay)", false), new("Range (farther, more stable)", true));
    private readonly ComboBox _autoOff = Ui.Combo(ArctisNovaProWireless.AutoOffMinutes.Select((m, i) => new Item<int>(m == 0 ? "Never" : $"{m} min", i)).ToArray());
    private readonly LabeledSlider _oled = new(1, 10);
    private readonly ComboBox _dim = Ui.Combo(ArctisNovaProWireless.DimMinutes.Select((m, i) => new Item<int>(m == 0 ? "Never" : $"{m} min", i)).ToArray());
    private readonly ComboBox _home = Ui.Combo(new Item<bool>("Detailed", false), new("Simple", true));
    private readonly Label _chatMix = new() { AutoSize = true };
    private readonly CheckBox _dashboard = new() { Text = "Show GearTray's screen: clock, date, network speed, volume and sound", AutoSize = true };
    private readonly CheckBox _screenMedia = new() { Text = "Now playing, while music or video plays", AutoSize = true };
    private readonly CheckBox _screenStats = new() { Text = "PC stats, while a game runs", AutoSize = true };
    private readonly CheckBox _screenPopups = new() { Text = "Pop-ups for game mode, program profiles and the controller", AutoSize = true };
    private readonly NumericUpDown _secHome = PageSeconds(), _secMedia = PageSeconds(), _secStats = PageSeconds();
    private readonly TextBox _pingHost = new() { Width = 130, PlaceholderText = "e.g. 192.168.50.10" };

    private static NumericUpDown PageSeconds() => new() { Minimum = 2, Maximum = 300, Width = 52, Margin = new Padding(3, 5, 10, 3) };
    private readonly Label _firmware = new() { AutoSize = true, MaximumSize = new Size(560, 0) };
    private readonly Button _apply, _reload;
    private HeadsetState? _have;

    public HeadsetTab(SettingsForm form, TrayContext tray) : base(form, tray, "headset", "Headset")
    {
        var noise = AddSection("noise", "Noise control");
        noise.Row("Mode", Section.Line(_ncAnc, _ncTrans, _ncOff));
        noise.Row("Transparency level", _transparency);

        _gain.Width = 280;
        var sound = AddSection("sound", "Sound");
        sound.Row("Volume", _volume);
        sound.Row("Headphone gain", _gain);
        sound.Note("Volume is the same as turning the base station's dial.");

        var mic = AddSection("mic", "Microphone");
        mic.Row("Mic volume", _micVol);
        mic.Row("Sidetone (hear yourself)", _sidetone);
        mic.Row("Muted-mic light", _micLed);

        var eq = AddSection("eq", "Equalizer");
        eq.Row("Preset", _eq);
        var bands = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(8, 0, 0, 0) };
        for (int i = 0; i < 10; i++)
        {
            var bar = new TrackBar
            {
                Orientation = Orientation.Vertical, Minimum = -20, Maximum = 20, TickStyle = TickStyle.None,
                AutoSize = false, Width = 36, Height = 120, SmallChange = 1, LargeChange = 4, BackColor = Theme.Page,
            };
            var value = new Label { AutoSize = false, Width = 44, Height = 18, TextAlign = ContentAlignment.MiddleCenter, Text = "0" };
            bar.ValueChanged += (_, _) => value.Text = (bar.Value / 2.0).ToString("+0.#;-0.#;0");
            var col = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Margin = new Padding(0) };
            col.Controls.Add(bar);
            col.Controls.Add(value);
            col.Controls.Add(new Label { Text = (i + 1).ToString(), AutoSize = false, Width = 44, Height = 18, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Theme.Dim });
            _eqBands[i] = bar;
            bands.Controls.Add(col);
        }
        eq.Span(bands);
        eq.Note("Custom EQ: 10 bands from bass (1) to treble (10), ±10 dB; choose “Custom” to use them. The game presets are SteelSeries' own tunings.");
        _eq.SelectedIndexChanged += (_, _) => UpdateControls();

        var line = AddSection("lineout", "Line out and stream mix");
        line.Row("Line out sends", _lineOut);
        line.Row("Stream: game", _streamMain);
        line.Row("Stream: aux input", _streamAux);
        line.Row("Stream: microphone", _streamMic);
        line.Note("The base station's line-out jack carries either the ChatMix balance or a separate mix for streaming/recording.");
        _lineOut.SelectedIndexChanged += (_, _) => UpdateControls();

        var bt = AddSection("bluetooth", "Bluetooth");
        bt.Row("Now", _btStatus);
        bt.Row("At power-on", _btAuto);
        bt.Row("During phone calls", _btCall);

        var wireless = AddSection("wireless", "Wireless connection");
        wireless.Row("Mode", _wireless);

        var power = AddSection("power", "Power and base station");
        power.Row("Turn headset off after", _autoOff);
        power.Row("Screen brightness", _oled);
        power.Row("Dim the screen after", _dim);
        power.Row("Home screen", _home);
        power.Span(_chatMix);

        var screen = AddSection("screen", "Base station screen");
        screen.Span(_dashboard);
        foreach (var page in new[] { _screenMedia, _screenStats, _screenPopups })
        {
            page.Margin = new Padding(24, 2, 3, 2);
            screen.Span(page);
        }
        screen.Row("Seconds per page", Section.Line(
            Section.Caption("Home"), _secHome, Section.Caption("Now playing"), _secMedia, Section.Caption("PC stats"), _secStats));
        screen.Row("Server to ping", Section.Line(_pingHost, Section.Caption("IP address or name; leave empty for none")));
        screen.Note("Replaces the base station's normal screen while GearTray runs. The home page (clock, date, download and upload " +
                    "speed, headset volume and a live sound meter) is always there; now playing and PC stats join it only while they " +
                    "apply, and the pages take turns, each staying up for the seconds set above (a new song shows right away). The " +
                    "server is pinged every second and shows next to the date as \"Server: On\", or \"Off\" after two missed answers " +
                    "in a row. The screen steps aside for a few seconds whenever you turn a knob, shifts by a pixel every minute " +
                    "against burn-in, and gives the screen back when you untick it, exit GearTray or the PC sleeps.");
        var screenSettings = Tray.Settings.HeadsetScreen;
        _dashboard.Checked = screenSettings.Dashboard;
        _screenMedia.Checked = screenSettings.ShowNowPlaying;
        _screenStats.Checked = screenSettings.ShowPcStats;
        _screenPopups.Checked = screenSettings.ShowPopups;
        _secHome.Value = Math.Clamp(screenSettings.ClockSeconds, 2, 300);
        _secMedia.Value = Math.Clamp(screenSettings.NowPlayingSeconds, 2, 300);
        _secStats.Value = Math.Clamp(screenSettings.PcStatsSeconds, 2, 300);
        _secHome.ValueChanged += (_, _) => { Tray.Settings.HeadsetScreen.ClockSeconds = (int)_secHome.Value; Tray.SaveSettings(); };
        _secMedia.ValueChanged += (_, _) => { Tray.Settings.HeadsetScreen.NowPlayingSeconds = (int)_secMedia.Value; Tray.SaveSettings(); };
        _secStats.ValueChanged += (_, _) => { Tray.Settings.HeadsetScreen.PcStatsSeconds = (int)_secStats.Value; Tray.SaveSettings(); };
        _pingHost.Text = screenSettings.PingHost;
        _pingHost.TextChanged += (_, _) => { Tray.Settings.HeadsetScreen.PingHost = _pingHost.Text.Trim(); Tray.SaveSettings(); };
        void UpdatePageBoxes() =>
            _screenMedia.Enabled = _screenStats.Enabled = _screenPopups.Enabled =
            _secHome.Enabled = _secMedia.Enabled = _secStats.Enabled = _pingHost.Enabled = _dashboard.Checked;
        UpdatePageBoxes();
        _dashboard.CheckedChanged += (_, _) =>
        {
            Tray.Settings.HeadsetScreen.Dashboard = _dashboard.Checked;
            Tray.SaveSettings();
            Tray.ApplyHeadsetScreen();
            UpdatePageBoxes();
        };
        _screenMedia.CheckedChanged += (_, _) => { Tray.Settings.HeadsetScreen.ShowNowPlaying = _screenMedia.Checked; Tray.SaveSettings(); };
        _screenStats.CheckedChanged += (_, _) => { Tray.Settings.HeadsetScreen.ShowPcStats = _screenStats.Checked; Tray.SaveSettings(); };
        _screenPopups.CheckedChanged += (_, _) => { Tray.Settings.HeadsetScreen.ShowPopups = _screenPopups.Checked; Tray.SaveSettings(); };

        var info = AddSection("firmware", "Firmware");
        info.Span(_firmware);

        _apply = AddButton("Apply to headset", Apply);
        _reload = AddButton("Reload", LoadAsync);
    }

    private ArctisNovaProWireless? Headset => Tray.Devices.Get<ArctisNovaProWireless>() is { Connected: true } h ? h : null;

    private void UpdateControls()
    {
        bool custom = _eq.Enabled && Ui.Value<int>(_eq) == 4;
        foreach (var b in _eqBands) b.Enabled = custom;
        bool stream = _lineOut.Enabled && Ui.Value<bool>(_lineOut);
        _streamMain.Enabled = _streamAux.Enabled = _streamMic.Enabled = stream;
    }

    public override async Task LoadAsync()
    {
        _have = null;
        if (Headset is not { } h) { Unavailable("Base station not connected."); return; }
        try
        {
            var (s, fw) = await Tray.Devices.RunAsync(() =>
            {
                string[]? firmware = null;
                try { firmware = h.ReadFirmware(); } catch (Exception ex) { Log.Warn("Headset firmware: " + ex.Message); }
                return (h.ReadState(), firmware);
            });
            _have = s;
            Info.Text = (s.HeadsetOnline ? $"Headset on · battery {s.BatteryPercent}%{(s.Charging ? " (charging)" : "")}" : "Headset is off")
                + (s.SpareBatteryPercent is int sp ? $" · spare battery in the base station {sp}%" : "");
            _ncAnc.Checked = s.Noise == NoiseMode.NoiseCancelling;
            _ncTrans.Checked = s.Noise == NoiseMode.Transparency;
            _ncOff.Checked = s.Noise == NoiseMode.Off;
            _transparency.Value = s.TransparencyLevel;
            _volume.Value = s.VolumePercent;
            Ui.Select(_gain, s.HighGain);
            Ui.Select(_sidetone, s.Sidetone, $"Level {s.Sidetone}");
            _micVol.Value = s.MicVolume;
            _micLed.Value = s.MicLedBrightness;
            Ui.Select(_eq, s.EqPreset, $"Preset {s.EqPreset}");
            for (int i = 0; i < 10; i++) _eqBands[i].Value = Math.Clamp((int)Math.Round(s.EqBandsDb[i] * 2), -20, 20);
            Ui.Select(_lineOut, s.LineOutStreamMix);
            _streamMain.Value = s.StreamMain;
            _streamAux.Value = s.StreamAux;
            _streamMic.Value = s.StreamMic;
            _btStatus.Text = s.BluetoothMode switch
            {
                4 => s.BluetoothStatus == 1 ? "on, connected" : "on",
                2 => "pairing",
                _ => "off",
            };
            _btAuto.Checked = s.BluetoothAutoOn;
            Ui.Select(_btCall, s.BluetoothCall);
            Ui.Select(_wireless, s.WirelessRange);
            Ui.Select(_autoOff, s.AutoOffIndex, $"Setting {s.AutoOffIndex}");
            _oled.Value = s.OledBrightness;
            Ui.Select(_dim, s.DimTimerIndex, $"Setting {s.DimTimerIndex}");
            Ui.Select(_home, s.HomeScreenSimple);
            _chatMix.Text = $"ChatMix dial: game {s.ChatMixGame}% · chat {s.ChatMixChat}%";
            _firmware.Text = fw is null ? "Not reported." :
                $"Base station: DSP {fw[0]}, controller {fw[1]}. Headset: {fw[2]}, Bluetooth {fw[3]}.";
            _dashboard.Enabled = h.CanDrawScreen;
            EnableInputs(Body, true);
            _dashboard.Enabled = h.CanDrawScreen;
            _apply.Enabled = true;
            UpdateControls();
        }
        catch (Exception ex) { Unavailable("Couldn't read the base station: " + ex.Message); }
    }

    private void Unavailable(string message)
    {
        Info.Text = message;
        EnableInputs(Body, false);
        _apply.Enabled = false;
        _reload.Enabled = true;
    }

    private async Task Apply()
    {
        if (Headset is not { } h || _have is null) return;
        var have = _have;
        var want = have.Clone();
        want.Noise = _ncAnc.Checked ? NoiseMode.NoiseCancelling : _ncTrans.Checked ? NoiseMode.Transparency : NoiseMode.Off;
        want.TransparencyLevel = _transparency.Value;
        want.VolumePercent = _volume.Value;
        want.HighGain = Ui.Value<bool>(_gain);
        want.Sidetone = Ui.Value<int>(_sidetone);
        want.MicVolume = _micVol.Value;
        want.MicLedBrightness = _micLed.Value;
        want.EqPreset = Ui.Value<int>(_eq);
        want.EqBandsDb = _eqBands.Select(b => b.Value / 2.0).ToArray();
        want.LineOutStreamMix = Ui.Value<bool>(_lineOut);
        want.StreamMain = _streamMain.Value;
        want.StreamAux = _streamAux.Value;
        want.StreamMic = _streamMic.Value;
        want.BluetoothAutoOn = _btAuto.Checked;
        want.BluetoothCall = Ui.Value<int>(_btCall);
        want.WirelessRange = Ui.Value<bool>(_wireless);
        want.AutoOffIndex = Ui.Value<int>(_autoOff);
        want.OledBrightness = _oled.Value;
        want.DimTimerIndex = Ui.Value<int>(_dim);
        want.HomeScreenSimple = Ui.Value<bool>(_home);
        if (await Run("Saving to the base station…", () => h.ApplyState(want, have)))
        {
            await LoadAsync();
            Tray.RefreshSoon();
            Status("Saved to the base station.");
        }
    }
}
