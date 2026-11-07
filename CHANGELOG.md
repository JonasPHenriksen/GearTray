# Changelog

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
