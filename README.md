# GearTray

A small tray app (one ~500 KB exe, no dependencies beyond the .NET 10 Desktop
Runtime) that replaces Armoury Crate, Razer Synapse, SteelSeries GG and the
Xbox Accessories app for one desk's peripherals. It talks to the devices
directly, using the byte-level protocols worked out by open-source projects
and the controller APIs that ship with Windows. It sends nothing those
projects don't send.

MIT licensed; the version history is in `CHANGELOG.md`.

## The devices it knows

| Device | USB ID | What GearTray does with it |
|---|---|---|
| ASUS ROG Harpe Ace Aim Lab Edition via the ROG Omni receiver (and the other Omni mice in G-Helper's list) | 0B05:1ACE | battery, the 5 onboard profiles, DPI stages and their colors, DPI stage in use, polling rate, button response, angle snapping, angle tuning, acceleration / deceleration, lift-off distance, button remapping (mouse actions, keys, media keys), sleep timer, low-battery warning, lighting, serial number |
| Razer Huntsman Elite | 1532:0226 | the keyboard's own lighting effects, per-key colors (keys, underglow, wrist rest), Ripple, GearTray's Ambient and Audio-meter effects, game-mode light, layout (read from the keyboard) |
| SteelSeries Arctis Nova Pro Wireless (also the Xbox versions) | 1038:12E0 / 12E5 / 225D | headset and spare battery, noise cancelling / transparency (+ level), volume, headphone gain, mic volume, sidetone, muted-mic light, 19 EQ presets and a 10-band custom EQ, line-out mode and stream mix, Bluetooth at power-up and call behaviour, wireless mode, auto-off, base-station screen brightness, dim timer and home screen, firmware versions, and GearTray's own base-station screen |
| Xbox controllers (tested: Xbox One S Controller by USB cable) | 045E:02EA and others | connection, model and battery, firmware version, live button / stick / trigger test, stick-drift check, rumble test for the main motors and the impulse triggers, Xbox button light (pattern and brightness), turning a wireless controller off |

The motherboard's ASUS Aura controller is deliberately left out: GearTray never
sends it anything.

## Hide what you don't use

Every block of settings has a **hide** link next to its title. Hidden blocks
disappear from the settings window and from the tray menu. The General tab
lists every tab and block with a checkbox, to show them again (or to hide a
whole tab).

## What runs in software

Some of what the vendor apps offer lives in the app, not in the device.
GearTray does those parts itself:

- **Ambient**: the keyboard mirrors the screen. The picture comes from DXGI
  desktop duplication and is shrunk on the graphics card (mipmaps), so only a
  ~50×20 pixel image is read back: a few milliseconds per update, safe to run
  while gaming. If duplication is unavailable it falls back to a GDI copy at a
  lower rate.
- **Audio meter**: left channel on the top key rows, right channel on the
  bottom rows, underglow pulsing with the level, from the Windows Core Audio
  peak meter of the default playback device.
- **Per-key colors**: painted on a picture of the keyboard that shows the key
  names of your Windows layout (Æ, Ø, Å on a Danish one), including the
  underglow and the wrist rest.
- **Ripple**: the keyboard's own if its firmware has it, otherwise drawn by
  GearTray from the keys you press.
- **Game mode**: blocks the Windows key, and optionally Alt+Tab and Alt+F4,
  with a low-level keyboard hook. It can switch on by itself while a game is
  the program in front: anything under a Steam `steamapps\common`, `XboxGames`,
  Epic Games or GOG folder, plus names you add.
- **Key remapping**: any key to another key, or disabled, re-sent as scan codes
  the way AutoHotkey does it.
- **Macros**: recorded key presses (with or without their timing) or a piece
  of text, on any trigger key with Ctrl / Shift / Alt.
- **Program profiles**: while a listed program is in front, GearTray switches
  keyboard lighting, game mode, the mouse's onboard profile and/or the headset
  EQ preset, and switches back afterwards.
- **Lights off** when the screen turns off or after a chosen idle time.
- **Mouse wheel only on low battery**: the wheel stays dark and breathes in a
  warning color once the battery drops below the limit.
- **Base-station screen**: GearTray's own home page on the Arctis base
  station's screen: the clock with seconds, the date, download and upload
  speed, the headset's volume beside a live sound meter (or "Headset off"),
  and, if you set one, whether a server answers ping ("Server: On" or "Off",
  checked every second). Pages take turns with it only while they
  apply: now playing (from Windows' media controls) while something plays, and
  PC stats (the game's FPS, CPU, GPU load and temperature, video memory, RAM,
  the game and how long it has run) while a game runs. FPS comes from
  Windows' own graphics events (ETW: DXGI and Direct3D 9 presents), as
  PresentMon does it. Short pop-ups show game mode, program
  profile and controller changes. Shifted a pixel each minute against burn-in;
  it steps aside while you turn a knob and gives the screen back when switched
  off.
- **Xbox button light**: controllers forget it on every reconnect, so GearTray
  sets it again each time.

## Not possible without the vendor's software

- Razer: Hypershift, and macros or remaps stored on the keyboard, need Razer's
  driver. The Huntsman Elite answers "not supported" to the polling-rate
  command, so that setting is hidden for it.
- SteelSeries: Sonar (spatial audio, mic noise reduction, the per-app mixer) is
  a virtual audio driver, not a headset setting.
- Xbox controllers: button remapping and the Xbox Accessories app's profiles go
  through a Windows API that only Microsoft-approved apps may use; Bluetooth
  connections don't expose the Xbox button light or the firmware version.
  Trigger rumble only works while GearTray's window is in front (a Windows
  rule).
- Firmware updates for any device.

## Install, update, remove

GearTray needs administrator rights for one thing: Windows only lets an
elevated process open the Razer keyboard's control channel. So it is
installed to `C:\Program Files\GearTray` (only administrators can change files
there, so nobody can swap the file that runs elevated) with a Start-menu
shortcut, and a Task Scheduler task starts it with highest privileges at
sign-in, without a UAC prompt.

```bat
build.bat
publish\GearTray.exe --install-startup      (run as administrator)
```

To update, run the same two lines; the installer asks the running GearTray to
exit before replacing it (versions before 0.3.0 have to be exited from the tray
icon first). To remove it, untick "Start GearTray when I sign in" on the General tab,
then delete `C:\Program Files\GearTray`, `%APPDATA%\GearTray` and the
GearTray Start-menu shortcut.

The CPU temperature on the stats page needs the PawnIO driver
([pawnio.eu](https://pawnio.eu), installer from
[namazso/PawnIO.Setup](https://github.com/namazso/PawnIO.Setup/releases)) and
the signed `AMDFamily17.bin` from
[namazso/PawnIO.Modules](https://github.com/namazso/PawnIO.Modules/releases)
next to `GearTray.exe` (`--install-startup` copies it along). Without them the
page simply leaves the CPU temperature out.

Settings and the log are in `%APPDATA%\GearTray` (`settings.json`, `log.txt`).
Mouse and headset settings are stored in the devices themselves; keyboard
lighting is kept in `settings.json` because the keyboard can't report its
current effect, and is put back after sleep, replugging or sign-in.

## Command line

The same exe has a command-line mode, used for testing. Output goes to the
console when redirected, or to a file with `--out FILE`. `--trace` logs every
HID report. Several commands can be chained with `;;` (one UAC prompt for a
whole test run).

```
--probe                              read-only report of every device
--list-hid                           every HID interface Windows has
--mouse dpi1=800 polling=1000 ...    dpi1..dpi4, polling, debounce, angle=on|off, angletune, accel, decel,
                                     lod=low|high, sleep=MIN, lowbatt=PCT, stage
--mouse-light MODE [RRGGBB] [BRIGHT] [--save]
--mouse-button-test                  remaps the wheel click to Back, reads it back, restores it
--kb-light EFFECT [RRGGBB] [BRIGHT] [--save]
--kb-gamemode on|off                 the keyboard's game-mode light
--kb-polling                         read the keyboard's polling rate
--kb-frame-test SECONDS              scrolling rainbow, per key
--kb-keys-test SECONDS               W A S D green, arrows blue, Esc red, left underglow yellow
--kb-effect-test ambient|audiometer|ripple SECONDS
--kb-exit-test                       from a pink frame to static green, then to Wave (leaving per-key mode)
--headset noise=anc sidetone=1 ...   noise, transparency, sidetone, mic, micled, autooff, oled, eq, eqbands
--screen-test SECONDS                GearTray's screen with its page rotation on the base station, then the screen back
--screen-preview FILE.png            every screen page as a picture (no device needed)
--screen-data [N]                    what Now playing and PC stats read, N times a second apart
--screen-speed                       how long a whole and a half picture take to reach the base station
--fps-test SECONDS                   present calls per second of every process (admin)
--cpu-temp                           CPU temperature through PawnIO, three readings (admin)
--xbox                               Xbox controllers: XInput state, battery, ids, firmware
--xbox-light MODE [0-47]             off|on|blinkfast|blinkslow|fadeslow|fadefast
--xbox-light-test                    brightest, very dim, slow fade, then Windows' level again
--sample-test                        screen sampler check (no device needed)
--audio-test                         audio meter check (no device needed)
--install-startup / --uninstall-startup
--screenshot DIR                     render each settings tab to a PNG
```

## How it talks to the devices

| Device | Transport | Sources |
|---|---|---|
| ROG mouse | 64-byte output reports, report ID 0x03, interface 2 collection 3 (usage page 0xFF01); the paired model from collection 1 (`01 A0`) | [G-Helper](https://github.com/seerge/g-helper), [libratbag](https://github.com/libratbag/libratbag) |
| Razer keyboard | 91-byte feature reports (report ID 0 + Razer's 90-byte report, XOR checksum) on interface 2, the mouse-usage collection; extended-matrix class 0x0F, custom frames row by row | [OpenRazer](https://github.com/openrazer/openrazer), [OpenRGB](https://gitlab.com/CalcProgrammer1/OpenRGB), [Polychromatic](https://github.com/polychromatic/polychromatic) |
| Arctis base station | 64-byte output reports, report ID 0x06, interface 4 collection 1 (usage page 0xFFC0); `06 09` saves; the screen takes 1024-byte feature reports | [HeadsetControl](https://github.com/Sapd/HeadsetControl), [Linux-Arctis-Manager](https://github.com/elegos/Linux-Arctis-Manager), [arctis-omni-linux](https://github.com/LDCanetti/arctis-omni-linux) |
| Xbox controller | XInput (`xinput1_4.dll`, including its unnamed exports for the Xbox button, ids and power-off); Windows.Gaming.Input for the trigger motors; Windows' Xbox driver (`\\.\XboxGIP`) for the light and the firmware version, sending only the GIP LED command | [Microsoft XInput docs](https://learn.microsoft.com/windows/win32/api/xinput/), [SDL](https://github.com/libsdl-org/SDL), [xbledctl](https://github.com/Leclowndu93150/xbledctl), [MS-GIPUSB](https://learn.microsoft.com/openspecs/windows_protocols/ms-gipusb/), [Linux xpad](https://github.com/torvalds/linux/blob/master/drivers/input/joystick/xpad.c) |

Every HID operation runs on one device thread, so the UI never blocks and a
device never sees two commands at once. Writes that only need to last until
the next power cycle (re-applying at startup, streamed frames) are sent
without saving, so the devices' flash is written only when you change a
setting.

## Source layout

```
GearTray/
├── Program.cs              entry: tray mode, CLI, single instance
├── Cli.cs                  command-line mode
├── Hid/                    SetupAPI enumeration, overlapped HID I/O, feature reports
├── Devices/                one class per device, the keyboard's LED map, Xbox controllers, the device thread
├── Effects/                Ambient (DXGI / GDI screen sampling), audio meter, ripple, base-station screen
├── Input/                  keyboard hook (game mode, remaps, macros), macro player, game detection
├── UI/                     tray icon, settings window (one file per tab), keyboard painter, controller view
└── Util/                   settings, log, elevation and the logon task
```
