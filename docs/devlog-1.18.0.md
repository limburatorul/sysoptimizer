# Sysoptimizer 1.18.0 — controllers' batteries for Shelf (20.10.2026)

## What
- `data\sysoptimizer.controllers.json` in `%APPDATA%\ProtagonistLabs`, written after every peripheral-battery
  reading, only while Shelf's card exists and its exe is on disk (`Ecosystem.Card("shelf")`):
  `{schema:1, updated, controllers:[{name, via, percent|null, level|null, charging, readAt}]}`.
  `percent` is an integer 0–100 or null; `level` is Full/Good/Medium/Low/Empty/Wired or null; `readAt` and
  `updated` are UTC `yyyy-MM-ddTHH:mm:ssZ`. No controllers connected = `controllers: []`.
- Controllers = XInput pads, DualShock 4 / DualSense (`via` "PlayStation, USB|Bluetooth"), and Bluetooth devices
  whose name matches controller|gamepad|dualsense|dualshock|xbox|8bitdo (a name heuristic, marked ponytail).
- The refresh no longer waits for keyboard/mouse input: only the Logitech HID++ layer does (a sleeping Lightspeed
  mouse doesn't answer its receiver). Bluetooth, XInput and PlayStation read every 5 minutes regardless.
- About → Works with: the Shelf line now also says "and your controller's battery in Big Picture".

## Why
Plan item 1 (Shelf Big Picture shows "Controller 64%"). Before, the whole refresh was gated on
`GetLastInputInfo` (input in the last second); a gamepad never counts as input there, so someone playing only
on a pad kept the reading from whenever they last touched the mouse.

## How tested
- Self-test (`dotnet run --project Sysoptimizer.Tests`, temp folders only, `Ecosystem.Folder` redirected):
  132 checks, all pass (4 new): nothing written while Shelf's card points at a missing exe; only the pads
  listed (a Bluetooth headset and a Logitech mouse left out); percent/level null when not said, charging,
  readAt in UTC; an empty list when nothing is connected.
- Live, on this PC, in a scratch harness compiling the real `PeripheralBatteryService` + `Ecosystem` with
  `Ecosystem.Folder` in the scratchpad (the user's running copy and real folder untouched): with nobody
  touching keyboard or mouse, `Latest()` read the devices and wrote the file 1.5 s after start; it held one
  controller, an Xbox Wireless Controller over Bluetooth at 100%; two other Bluetooth devices were left out.
- `dotnet build -c Release`: 0 errors.

## Untested
- The Logitech path after the split (no input during the harness run, so the Logitech layer wasn't exercised);
  its logic is the old one, with its own 5-minute / 30-s-retry clock.
- A PlayStation pad and an XInput pad on the wireless adapter (none connected here); covered by the parsers' tests.
- The installed app's UI (Works with text) — not launched, per the isolated-test rule.
