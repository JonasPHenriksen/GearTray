# Changelog

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
