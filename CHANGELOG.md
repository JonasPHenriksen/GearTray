# Changelog

## 0.11.0 (2026-11-14)

- **The mouse by cable.** Plugged in to charge, the ROG Harpe Ace Aim Lab
  Edition (0B05:1A92) is now found directly, so its battery, the charging bolt
  on the base station and every mouse setting keep working without the Omni
  receiver. Plugging the cable in or pulling it switches between cable and
  receiver by itself. By cable the mouse's control interface has no report IDs,
  so commands go out with report ID 0 instead of 3; otherwise it is the same
  protocol.
- **Wheel light when charged.** Under "Only light up when the battery is low"
  there is now "Also light up when it has charged to N% in a color" (50–100%,
  default 100% green). Once the battery reaches that level while the cable is
  in, the wheel shines steadily in that color; it goes dark when you unplug.
  The low-battery warning no longer shows while the cable is in.

## 0.10.0 (2026-11-09)

- **Batteries on the base-station home page.** The bottom row is now one tight
  line spread over the width: a speaker and the volume (no more long bar), a
  headset and its battery, a mouse and its battery, and the weather. Small
  pixel pictures mark each, the numbers have no "%", and a lightning bolt
  follows a battery while it charges. With the headset off, "off" takes the
  place of the volume and its battery; the mouse is left out until its battery
  is known (it keeps the last level while the mouse sleeps). When the row gets
  very tight (three 100s and a two-digit temperature) the temperature drops its
  "C", the gaps narrow and, last of all, the bolt goes.
- The headset's battery comes with the volume reading the screen already makes
  every 3 seconds; the mouse's comes from the tray's own once-a-minute reading.
- The home page's live sound meter (shown only without a weather place) is
  gone; the sound is still read for the now-playing wave.

## 0.9.0 (2026-11-08)

- **New now-playing page** on the base-station screen: the title; the artist
  with the player (Spotify, Firefox…) on the right; the sound of the last 13
  seconds as a wave that moves with it (one bar per 200 ms, newest on the
  right, scaled to the loudest moment in view so it fills its band at any
  volume; silence is a dotted line); and the elapsed and remaining time with a
  slim bar and knob between them. Long titles end in "…" (they had two lines).
  While the player hasn't said where in the track it is, "playing for", as
  before.
- The sound meter is now also read while the now-playing page shows (the
  home page already read it), so that page is sent up to five times a second
  while there's sound.
- **Weather on the home page**, in the sound meter's place: a 12-px picture
  (sun or moon, partly cloudy, cloud, fog, drizzle, rain, snow, thunder) and
  the temperature ("7°C"), "…" until the first answer. Type a town or city
  under "Weather for" on the Headset tab; it's looked up a moment after you
  stop typing and shown as found ("Copenhagen, Capital Region, Denmark").
  The weather comes from Open-Meteo (free, no account) every 15 minutes, over
  HTTPS, with answers of at most 1 MB read within 10 seconds; after a failure
  it tries again in 2 minutes and keeps the last answer for up to 2 hours.
  Without a place, the sound meter stays.
- `--weather PLACE` looks a place up and prints its weather now.

## 0.8.0 (2026-11-07)

- **Dark mode**: a switch at the bottom right of the settings window, where
  Wabbajack Wizard has its own, with Wabbajack Wizard's dark colours. The
  title bar is asked for the window's own colours the same way (on Windows 11
  it matches exactly; Windows 10 gets its plain dark bar), the tab strip is
  drawn dark, and the tray menu and the macro and profile editors follow.
  Until the switch is used, GearTray follows Windows' app mode. Switching
  rebuilds the settings window in the same place and on the same tab (edits
  not yet applied to a device are dropped).
- The light look is unchanged.
- Opening GearTray from the Start menu while it runs (or while its startup
  task takes over) no longer creates `%APPDATA%\GearTray` for one log line:
  only the instance that keeps running logs.
- `--theme-preview FILE.png` shows a sample window and menu in both looks and
  saves them as captured from the screen, title bar included.

## 0.7.1 (2026-11-07)

- **App icon**: a white cog on a blue-to-violet rounded square, the shape of
  the battery badges, in Explorer, the Start menu, Installed apps, the UAC
  prompt, the taskbar and the window title bars (the settings window had the
  grey "G" badge, the editors WinForms' default icon). Drawn for every size,
  with six chunkier teeth below 24 px so small sizes stay crisp.
- The tray shows the logo while no battery level is known, instead of the
  grey "G".
- The installer tells Explorer that the exe and the Start-menu shortcut
  changed, so a new icon shows at once.
- `--write-icon FILE.ico [PREVIEW.png]` writes `GearTray.ico` (and a sheet of
  all its sizes) from the drawing code in `UI/Logo.cs`.

## 0.7.0 (2026-11-07)

- **Installed apps**: `--install-startup` also adds GearTray to Settings >
  Apps > Installed apps (version, publisher, size), and **Uninstall** there
  removes it: it asks whether your settings and log should go too, then,
  after one admin prompt, removes the logon task, the Start-menu shortcut, the
  entry and `C:\Program Files\GearTray`. The exe the uninstaller runs from is
  renamed and deleted by Windows at the next restart (renamed, so a GearTray
  installed again before then isn't deleted with it). `%APPDATA%\GearTray` is
  deleted with your normal rights, never with admin rights. `--uninstall` does
  the same from the command line.
- **Settings and log with admin rights** are kept in
  `C:\Program Files\GearTray\Data`, which only administrators can change,
  instead of `%APPDATA%\GearTray`, which every program you run can change: one
  could have swapped the folder for a link and made the elevated GearTray
  write or move other files, or planted a macro for it to type. The first
  start with admin rights imports the settings from `%APPDATA%\GearTray`; runs
  without admin rights keep using that folder.
- **Open log** and **Open settings folder** open through the desktop's
  Explorer, so the program showing them runs with your normal rights instead
  of GearTray's admin rights (which program opens a file type is set in
  per-user registry keys any program can change). If that fails, the path is
  copied instead.
- **Built to ignore** `DOTNET_STARTUP_HOOKS` and `DOTNET_ROOT`: the logon task
  hands GearTray your environment variables, and these two could have loaded
  other code into it with admin rights.
- Safer install: the task definition is written to the install folder instead
  of `%TEMP%`, and `schtasks.exe` is started by its full path instead of being
  looked up.
- `--open-test PATH` opens a file through Explorer the way those buttons do.

## 0.6.2 (2026-11-07)

- The screen no longer gathers the battery, controller, game-mode and profile
  details it stopped showing in 0.6.0 (they were collected five times a
  second).
- Headset tab: "Seconds per page" calls the home page "Home" instead of
  "Clock"; the saved value carries over.
- `--screen-preview` draws every page at a fixed time, so its pictures only
  change when the drawing does.

## 0.6.1 (2026-11-07)

- Home page rebalanced: a smaller clock (24 px, was 28) and everything else
  bigger and bolder (11 px semibold, was 10 px regular), with bigger arrows
  and headset icon, a taller volume bar and sound meter, and the rows evenly
  spaced. The meter now ends 2 px from the edge, like the text.
- Without a server to ping, a date too long for its line ("Wednesday 30
  September") shortens the month.

## 0.6.0 (2026-11-07)

- **New home page** on the base-station screen instead of the battery list
  (batteries stay in the tray): the clock with seconds, its digits in even
  cells so it doesn't shift as they tick; the date; download and upload speed,
  each centred in its half; and the headset row: the volume as a short bar
  with its percentage beside a live sound meter (left and right combined), or
  "Headset off" with a crossed-out headset. The volume is read every 3 s and
  right after the knob is turned.
- **Server to ping** (Headset tab): an IP address or name, pinged every second
  and shown beside a short date as "Server: On", or "Off" after two missed
  answers in a row. Without one, the full date shows.
- The screen sends only the half of the picture that changed (about 20 ms
  over USB, against 36 ms for the whole picture), and nothing when it's
  unchanged.
- `--screen-speed` times pictures sent to the base station.

## 0.5.2 (2026-11-07)

- **Average FPS** of the game, over the time it kept drawing (pauses over a
  second, like alt-tab, don't count), in the stats page's bottom line, which
  is now in three parts under the columns: "VRAM 9.1  RAM 18.4  avg 141".
  Until there are 5 seconds of frames it shows "avg –".

## 0.5.1 (2026-11-07)

- **CPU temperature** (Tctl) on the stats page for AMD Ryzen, through the
  PawnIO driver (namazso, the one LibreHardwareMonitor and FanControl use)
  and its signed AMDFamily17 module, which GearTray keeps next to its exe.
  It reads the CPU's SMN register 0x59800 under the shared "Global\Access_PCI"
  lock. Without PawnIO the label just says "CPU".
- `--cpu-temp` prints three readings; `--install-startup` also copies the
  module.

## 0.5.0 (2026-11-07)

- **FPS** and the **1% low** (the frame rate of the slowest 1% of frames over
  the last 10 s) of the game in front, counted from Windows' own graphics
  events (DXGI and Direct3D 9 present calls, as PresentMon does). Nothing to
  install; the trace runs only while a game does. Vulkan and OpenGL games
  whose frames can't be counted show "–".
- New PC stats layout: CPU | GPU | FPS columns with the numbers as big as
  their column allows, temperatures (and the 1% low) beside the labels, and
  VRAM and RAM with decimals, mirrored around the middle.
- `--fps-test N` lists every process's present rate over N seconds.

## 0.4.2 (2026-11-07)

- Base-station screen: seconds per page for the clock, Now playing and PC
  stats, set on the Headset tab (2–300 s; defaults 8, 6 and 8).

## 0.4.1 (2026-11-07)

- Now playing keeps the track's position. Firefox reports an all-zero
  timeline while a video plays and the real one only when you pause or skip,
  so GearTray keeps the last real position and counts forward from it. Until
  a player has reported one, the page shows how long it has been playing
  instead of the app's name.
- `--screen-data N` reads N times a second apart and shows the raw timeline.

## 0.4.0 (2026-11-07)

- Base-station screen pages that take turns with the clock, only while they
  apply:
  - **Now playing** while music or video plays (title, artist, time, progress
    bar), from Windows' own media controls, so Spotify, browsers and most
    players work. A new song shows right away.
  - **PC stats** while a game runs, even when you alt-tab out of it: the game's
    name and how long it has been running, CPU and GPU load, GPU temperature,
    video memory and RAM. GPU values come from NVIDIA's driver library
    (nvml.dll). While gaming this page gets the most time.
  - **Pop-ups** for a few seconds: game mode on/off, program profile, and the
    controller connecting or disconnecting.
  - Each can be switched off on the Headset tab.
- Game detection remembers the game's name (its library folder) and start time.
- `--screen-preview` renders every page; `--screen-test` runs the real
  rotation; `--screen-data` prints what Now playing and PC stats read.

## 0.3.2 (2026-11-07)

- Controller tab: the vibration test has Start and Stop buttons and runs until
  you stop it (at most two minutes), on the motors you tick: left, right, left
  trigger, right trigger. Strength and motors can be changed while it runs.

## 0.3.1 (2026-11-07)

- Fixed: switching the keyboard from one of GearTray's effects (Ambient, Audio
  meter, Ripple, per-key colors) to one of the keyboard's own (Wave, Spectrum,
  Static…) left the last frame frozen on the keys. Found by testing on the
  Huntsman Elite, three things were behind it:
  - effects saved to a profile don't show over per-key frames, so they are
    now sent live first;
  - a brightness command right after an effect undoes it, so it waits 300 ms;
  - Wave needs direction 1 or 2; the keyboard ignores 0, which OpenRazer
    sends for this model.
- Effects are saved to the keyboard's active onboard profile, read from the
  keyboard (class 05 / id 84), instead of always profile 1, where they were
  stored but never shown when another profile was active. The Keyboard tab
  shows which profile is in use.
- A per-key preview is undone when you leave the Keyboard tab or close the
  window without pressing Apply.
- Every keyboard lighting change is written to the log.

## 0.3.0 (2026-11-07)

- **Hide what you don't use.** Every settings block has a "hide" link, and the
  General tab lists all tabs and blocks with checkboxes to show them again.
  Hidden blocks also leave the tray menu.
- **Xbox controller** tab (no driver or app needed):
  - connection, model, battery (low-battery warning, tray icon option) and
    firmware version;
  - live button / stick / trigger test, stick-drift check;
  - rumble test for the main motors and the impulse triggers;
  - **Xbox button light**: pattern and brightness, set again whenever the
    controller connects (USB cable or Xbox Wireless Adapter);
  - turning a wireless controller off.
  - Vibration is always stopped when the tab is left or GearTray exits.
- **Programs** tab: program profiles. While a listed program is in front,
  GearTray switches keyboard lighting, game mode, the mouse's onboard profile
  and/or the headset EQ preset, and switches back afterwards.
- Keyboard:
  - **per-key colors**, painted on a picture of the keyboard with the key
    names of your Windows layout, including underglow and wrist rest;
  - **Ripple** (the firmware's own if it has one, otherwise drawn by GearTray);
  - **macros** (recorded key presses with timing, or text), on any trigger key
    with Ctrl/Shift/Alt;
  - the keyboard's layout (US/ISO) is read from the keyboard itself.
- Mouse:
  - acceleration and deceleration, angle tuning;
  - the 5 onboard profiles, and the DPI stage in use;
  - **button remapping** (8 buttons; the left button always stays left
    click), restore defaults;
  - serial number.
  - Lighting "Off" now uses the mouse's own off mode.
  - Button response limited to 12–32 ms, as G-Helper does for this model.
- Headset:
  - volume, headphone gain, line-out mode and stream mix;
  - Bluetooth on at power-up and call behaviour, Bluetooth status;
  - wireless mode (speed/range), screen dim timer, home screen;
  - all 19 EQ presets, firmware versions.
  - **GearTray's own base-station screen**: clock, date and every battery,
    with burn-in protection; it steps aside when a knob is turned and gives
    the screen back when switched off.
- Settings window split into one file per tab, with a fixed Apply/Reload bar.
- `--install-startup` asks a running GearTray to exit before replacing it.

## 0.2.0 (2026-11-07)

- Keyboard: **Ambient** effect. The keys follow the screen (or the whole
  keyboard takes its average color), with a color boost. Uses DXGI desktop
  duplication with GPU downscaling, GDI as a fallback.
- Keyboard: **Audio meter** effect from the default playback device.
- Keyboard: **game mode** in software. Blocks the Windows key, optionally
  Alt+Tab and Alt+F4. Can switch on by itself while a Steam, Epic, Xbox or GOG
  game is in front, plus extra and ignored program names. The keyboard's
  game-mode light follows it.
- Keyboard: **key remapping** (to another key, or disabled).
- Keyboard: lights off when the screen turns off, or after a chosen idle time.
- Keyboard: polling rate shown when the keyboard supports the command (the
  Huntsman Elite doesn't).
- Mouse: lighting option "only light up when the battery is low".
- Starting GearTray while it already runs opens its settings window.
- Per-key frames are sent with a 1 ms timer resolution while an effect runs
  (Windows' default 15.6 ms capped effects at 5 updates per second).

## 0.1.0 (2026-11-07)

- First version: tray icon with the lowest battery as a colored badge, and
  low-battery notifications.
- ROG mouse via the Omni receiver: DPI stages and colors, polling rate, button
  response, angle snapping, lift-off distance, sleep timer, low-battery
  warning, lighting.
- Razer Huntsman Elite: lighting effects, brightness, game-mode light.
- Arctis Nova Pro Wireless: noise control and transparency level, mic volume,
  sidetone, muted-mic light, EQ presets and custom bands, auto-off,
  base-station screen brightness, spare battery.
- Installed to Program Files with an elevated logon task.
