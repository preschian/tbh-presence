# TBH Companion

A companion app for **TaskBarHero**. One small tray program, two features:

1. **Discord presence** — your Discord profile shows what you're doing in the
   game, live: the stage you're on and the heroes you've deployed.
2. **Auto-synthesis** — the game's Cube synthesis runs by itself: pick
   materials, synthesize, empty the cube, repeat — hands-free.

```
Playing TaskBarHero
Act 3 - Stage 3  (HELL, Lv 72)
Ranger Lv80, Sorcerer Lv23, Priest Lv35
23:41 elapsed
```

## What you need

| For | You need | Notes |
|-----|----------|-------|
| The app itself | Windows 10/11 | Nothing to install — it's a single exe. |
| Discord presence | The [Discord desktop app](https://discord.com/download), logged in | The browser version doesn't support presence. |
| Auto-synthesis | **BepInEx** installed in the game folder (one-time, steps below) | Optional. Without it, only presence runs and the game is never touched. |

## Getting started

1. Download `TbhCompanion.exe` from the [Releases page](../../releases).
2. Double-click it. A small helmet icon appears in your system tray (near the
   clock) — that's it running.
3. Play TaskBarHero with Discord open. Your profile shows your current stage
   and party within a few seconds.

That's all for presence. The first run after a game update takes about a
minute to read the game; after that it starts instantly.

To stop the app, right-click the tray icon and choose **Quit**.

> **First-run warning:** Windows SmartScreen may show "Windows protected your
> PC" because the app isn't code-signed. Click **More info → Run anyway**. Some
> antivirus tools may also flag it, because it reads another program's memory to
> see your progress — that's expected for this kind of tool.

## Setting up auto-synthesis (one time)

The auto-synthesis feature is a small mod that runs inside the game, which
needs the free mod loader **BepInEx** installed once:

1. **Back up your save** (copy this file somewhere safe):
   `%USERPROFILE%\AppData\LocalLow\TesseractStudio\TaskbarHero\SaveFile_Live.es3`
2. Download BepInEx: get the newest
   `BepInEx-Unity.IL2CPP-win-x64-*.zip` from
   [builds.bepinex.dev/projects/bepinex_be](https://builds.bepinex.dev/projects/bepinex_be).
3. Extract the zip **into the game folder**
   (`...\Steam\steamapps\common\TaskbarHero`), so `winhttp.dll` and the
   `BepInEx` folder sit next to `TaskBarHero.exe`.
4. Start the game once and wait about a minute (BepInEx sets itself up), then
   close it.
5. Make sure `TbhCompanion.exe` is running — it installs and updates the mod
   into the game automatically from now on.
6. Start the game again. Done.

## Using auto-synthesis

Open the **Cube** panel in the game — that's it. The loop starts on its own:
it picks the highest cube level you've unlocked, fills the cube with
materials, runs the synthesis, empties the cube, then waits for the next
round (every 5 minutes by default). The Cube panel must stay open while it
works.

In-game keys:

| Key | Action |
|-----|--------|
| **F8** | Turn the auto loop on/off |
| **F9** | Run the synthesis once, manually |
| **F10** | Write a status report to the log (for troubleshooting) |

**Safety:** items above your chosen rarity limit are never synthesized — if
one ends up in the cube, that round is skipped and the cube is emptied. The
mod only presses the game's own buttons; it never edits your items, save, or
memory.

## The Status & Settings window

**Double-click the tray icon** (or right-click → *Status & Settings...*):

- Live indicators: is presence connected? is the auto-synthesis loop running?
  how many rounds so far, and what was synthesized last?
- Auto-synthesis settings you can change anytime:
  - start automatically when the game launches (on by default),
  - **max rarity to synthesize** (default: Legendary),
  - how often a round runs (minutes),
  - small timing tweaks.

Press **Save settings** — changes reach the running game within ~10 seconds,
no restart needed.

## Start it with Windows

Press <kbd>Win</kbd>+<kbd>R</kbd>, type `shell:startup`, press Enter, and put a
shortcut to `TbhCompanion.exe` in the folder that opens. It will sit quietly
and wait for the game whenever you log in.

## Troubleshooting

- **Nothing shows on my Discord profile.** In Discord: **Settings → Activity
  Privacy → "Display current activity as a status message"** must be on, and
  Streamer Mode must not be hiding it. Use the desktop app, not the browser.
- **Auto-synthesis says "game is not running" / "plugin has not reported".**
  The mod isn't loaded yet: check BepInEx is installed (step above), then
  restart the game while `TbhCompanion.exe` is running.
- **Auto-synthesis is ON but nothing happens.** The Cube panel must be open
  in the game — the loop pauses while it's closed.
- **Rounds keep getting skipped.** An item above your rarity limit keeps
  landing in the cube. Raise *Max rarity to synthesize* in Status & Settings,
  or move that item out of reach.
- **Wrong stage shown after a game update.** Give the first run a minute to
  re-read the game. If it stays wrong, the game's internals changed — see
  [CONTRIBUTING.md](CONTRIBUTING.md).

## Privacy & fair use

The presence feature only ever *reads* the game. The auto-synthesis mod
presses the game's own UI buttons and changes nothing else; it's opt-in and
only installed when BepInEx is present. TaskBarHero is a single-player game —
still, mind the game's terms if any online leaderboard exists.

---

Everything technical — building from source, how the memory reading works,
the mod's internals, using your own Discord application, command-line options
— lives in [CONTRIBUTING.md](CONTRIBUTING.md).
