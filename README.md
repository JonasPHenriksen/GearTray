# GearTray

A small tray app (one ~360 KB exe, no dependencies beyond the .NET 10 Desktop
Runtime) that replaces Armoury Crate, Razer Synapse and SteelSeries GG for one
desk's peripherals. It talks to the devices directly over USB HID, using the
byte-level protocols worked out by open-source projects, and sends nothing
those projects don't send.

MIT licensed; the version history is in `CHANGELOG.md`.

## The devices it knows

| Device | USB ID | What GearTray does with it |
|---|---|---|
| ASUS ROG Harpe Ace Aim Lab Edition via the ROG Omni receiver (and the other Omni mice in G-Helper's list) | 0B05:1ACE | battery, DPI stages and their colors, polling rate, button response, angle snapping, lift-off distance, sleep timer, low-battery warning, lighting |
| Razer Huntsman Elite | 1532:0226 | lighting effects in the keyboard, plus GearTray's own Ambient and Audio-meter effects streamed per key; game-mode light |
| SteelSeries Arctis Nova Pro Wireless (also the Xbox versions) | 1038:12E0 / 12E5 / 225D | headset battery, spare battery in the base station, noise cancelling / transparency (+ level), mic volume, sidetone, muted-mic light, EQ presets and a 10-band custom EQ, auto-off, base-station screen brightness |

The motherboard's ASUS Aura controller is deliberately left out: GearTray never
sends it anything.

## What runs in software

Some of what Synapse offers lives in Synapse, not in the keyboard. GearTray
does those parts itself, for any keyboard:

- **Ambient**: the keyboard mirrors the screen. The picture comes from DXGI
  desktop duplication and is shrunk on the graphics card (mipmaps), so only a
  ~50×20 pixel image is read back: a few milliseconds per update, safe to run
  while gaming. If duplication is unavailable it falls back to a GDI copy at a
  lower rate.
- **Audio meter**: left channel on the top key rows, right channel on the
  bottom rows, underglow pulsing with the level, from the Windows Core Audio
  peak meter of the default playback device.
- **Game mode**: blocks the Windows key, and optionally Alt+Tab and Alt+F4,
  with a low-level keyboard hook. It can switch on by itself while a game is
  the program in front: anything under a Steam `steamapps\common`, `XboxGames`,
  Epic Games or GOG folder, plus names you add.
- **Key remapping**: any key to another key, or disabled, re-sent as scan codes
  the way AutoHotkey does it.
- **Lights off** when the screen turns off or after a chosen idle time.
- **Mouse wheel only on low battery**: the wheel stays dark and breathes in a
  warning color once the battery drops below the limit.

Not possible without Razer's own driver: Hypershift, macros or remaps stored
on the keyboard, and Chroma Studio's per-key editor. The Huntsman Elite
answers "not supported" to the polling-rate command, so that setting is hidden
for it.

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

To update, exit the running GearTray (tray icon → Exit) and run the same two
lines. To remove it, untick "Start GearTray when I sign in" on the General tab,
then delete `C:\Program Files\GearTray`, `%APPDATA%\GearTray` and the
GearTray Start-menu shortcut.

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
--mouse dpi1=800 polling=1000 ...    dpi1..dpi4, polling, debounce, angle=on|off, lod=low|high, sleep=MIN, lowbatt=PCT
--mouse-light MODE [RRGGBB] [BRIGHT] [--save]
--kb-light EFFECT [RRGGBB] [BRIGHT] [--save]
--kb-gamemode on|off                 the keyboard's game-mode light
--kb-polling                         read the keyboard's polling rate
--kb-frame-test SECONDS              scrolling rainbow, per key
--kb-effect-test ambient|audiometer SECONDS
--headset noise=anc sidetone=1 ...   noise, transparency, sidetone, mic, micled, autooff, oled, eq, eqbands
--sample-test                        screen sampler check (no device needed)
--audio-test                         audio meter check (no device needed)
--install-startup / --uninstall-startup
--screenshot DIR                     render each settings tab to a PNG
```

## How it talks to the devices

| Device | Transport | Sources |
|---|---|---|
| ROG mouse | 64-byte output reports, report ID 0x03, interface 2 collection 3 (usage page 0xFF01); the paired model from collection 1 (`01 A0`) | [G-Helper](https://github.com/seerge/g-helper), [libratbag](https://github.com/libratbag/libratbag) |
| Razer keyboard | 91-byte feature reports (report ID 0 + Razer's 90-byte report, XOR checksum) on interface 2, the mouse-usage collection; extended-matrix class 0x0F | [OpenRazer](https://github.com/openrazer/openrazer), [OpenRGB](https://gitlab.com/CalcProgrammer1/OpenRGB) |
| Arctis base station | 64-byte output reports, report ID 0x06, interface 4 collection 1 (usage page 0xFFC0); `06 09` saves | [HeadsetControl](https://github.com/Sapd/HeadsetControl), [Linux-Arctis-Manager](https://github.com/elegos/Linux-Arctis-Manager), [arctis-omni-linux](https://github.com/LDCanetti/arctis-omni-linux) |

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
├── Devices/                one class per device + the device thread (DeviceManager)
├── Effects/                Ambient (DXGI / GDI screen sampling), audio meter, effect engine
├── Input/                  keyboard hook (game mode, remaps), game detection
├── UI/                     tray icon, settings window, controls
└── Util/                   settings, log, elevation and the logon task
```
