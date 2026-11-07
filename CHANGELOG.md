# Changelog

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
