# Sysoptimizer 1.19.3 — What's new, in the studio's format (22.10.2026)

1. **The ask.** Every Protagonist Labs app is moving to the same What's new: releases on the left under the
   month they came out, the chosen one on the right under a 960 × 170 banner, notes built into the app so it
   works offline. Sysoptimizer had only a notes-only mode of its update dialog, which nothing ever opened.
2. **What changed.** A What's new window (WhatsNewWindow.cs, the same layout as Frostpane's and File Labs'),
   in the current theme's colours and font, with acrylic like the other windows. Its notes are `whatsnew.txt`,
   built into the exe: one entry per x.y release from 1.0 to 1.19, its patches folded in, written from the
   commit history. 1.19 shows a picture made from the studio's banner template (October's light and leaves,
   live graphs, CPU/GPU tiles, temperatures, FPS, batteries and the History chart, no text); older releases
   get a drawn banner in Sysoptimizer's teal with a graph line, seeded by the version so it never changes.
3. **Where it opens.** The tray menu (What's new, under Open Sysoptimizer), Settings (a What's new link under
   Check for updates), and once after an update: the version seen is kept in `HKCU\Software\Sysoptimizer`
   `LastSeenVersion`; a PC with no value but history from before today is an update (everything up to
   1.19.2 never wrote one), a fresh install shows nothing.
4. **Dropped.** The update dialog's unused notes-only mode.
5. **Tested.**
   - Self-test: 135 checks pass, two new ones on `whatsnew.txt` (every header parses, newest first).
   - A patched copy in the scratchpad (its own mutex, `Software\SysoptimizerTest`, history, log, ecosystem
     and report folders, asInvoker, recording off), never the installed app: with `LastSeenVersion` 1.19.2
     it opened What's new by itself on start and wrote 1.19.3; 1.19 showed its picture at full size, 1.10
     the drawn banner; the Settings link opened the window again; with no value and no history (a fresh
     install) nothing opened.
   - `installer\build.ps1` → `dist\Sysoptimizer-1.19.3-setup.exe` (49.2 MB).
6. **Not tested.** The tray menu item by a real click (it is the same `WhatsNewWindow.Open()` the Settings
   link calls), and a real update from 1.19.2 through the installer.
