# TBH Companion

[![GitHub Downloads](https://img.shields.io/github/downloads/preschian/tbh-presence/total)](https://github.com/preschian/tbh-presence/releases)

A tray companion for **TaskBarHero**: shows your progress on Discord, and
automates the game's idle chores.

![The Status & Settings window](docs/settings-window.png)

## Features

- **Discord presence** — your profile shows your live stage, difficulty, and
  party. It reads the game's memory and never writes to it.
- **Auto loop** (needs the mod) — runs every few minutes:
  **Soulstone → Chest → Offering → Alchemy → Synthesis → Rune**.
  - **Pause on mouse** — optional. Stops the loop while you move or click in
    the game, then starts a new cycle after you stay still (default 30s).
  - **Soulstones** — re-enters a cleared Act Boss stage at the highest tier you
    allow (Normal/Nightmare/Hell/Torment), then walks your hero back.
  - **Chests** — opens StageBox chests (Normal / Boss / ActBoss).
  - **Offering** — spends offering coins through the Cube.
  - **Alchemy** — melts junk gear into gold below a level and rarity you set.
    Locked, reserved, and equipped items are never touched.
  - **Synthesis** — cube synthesis for Equipment / Materials / Accessories, up
    to a rarity cap and a target recipe level.
  - **Runes** — auto-upgrades runes.
- **Scheduled restart** — optionally close and relaunch TaskBarHero after N days
  of uptime, to shed RAM on long idle sessions. In both editions.
- **Self-update** — tells you when a game patch has left the mods behind, and
  updates itself when a matching release exists.

## Which download?

The [Releases page](../../releases) has two editions:

| Download | What it does |
|----------|--------------|
| **`TbhCompanion-Presence.exe`** | Discord presence and scheduled restart. No mod, nothing loaded into the game. The safe choice. |
| **`TbhCompanion.exe`** | The above **plus** the in-game automation mod. |

You need Windows 10/11 and the [Discord desktop app](https://discord.com/download)
(the browser version doesn't support presence), with **Settings → Activity
Privacy → "Display current activity as a status message"** turned on. Nothing
to install — it's a single exe.

## Getting started

1. Download an edition and double-click it. A helmet icon appears in your
   system tray.
2. Play TaskBarHero with Discord open — your profile updates within seconds.
3. Double-click the tray icon for **Status & Settings**; right-click → **Quit**
   to stop.

The first run after a game update takes about a minute to read the game; after
that it starts instantly.

> Windows SmartScreen may show "Windows protected your PC" because the app
> isn't code-signed — click **More info → Run anyway**. Antivirus tools may also
> flag it, because it reads the game's memory to see your progress.

## Setting up the mod (one time)

The automation runs inside the game via the free mod loader **BepInEx**:

1. Close TaskBarHero, open Status & Settings, click **Install mods** and
   confirm. Your save is backed up first.
2. Start the game and wait about a minute while BepInEx finishes setting
   itself up.

The button becomes **Remove mods** afterwards, and the app keeps the mod up to
date. Removing it deletes BepInEx from the game folder; your save and Discord
presence are untouched.

The loop then runs on its own, opening the panels it needs — you can leave the
game alone. Everything is configured in Status & Settings; press **Save** and
running settings reach the game within ~10 seconds.

Self-updating needs write access to the app's folder, so keep
`TbhCompanion.exe` somewhere like Downloads rather than under `Program Files`.

## Start it with Windows

Press <kbd>Win</kbd>+<kbd>R</kbd>, type `shell:startup`, and drop a shortcut to
`TbhCompanion.exe` in the folder that opens.

## Something not working?

Open a [GitHub issue](../../issues) — include what you were doing and, if the
mod is involved, the BepInEx log.

---

Building from source, memory-reading internals, the mod's design, and
command-line options live in [CONTRIBUTING.md](CONTRIBUTING.md).

## Disclaimer

Automating item generation is against TaskBarHero's Terms of Service (which
prohibit "macros or auto programs" during item generation) and could, in
principle, lead to item removal or an account ban — especially for items
tradable on the Marketplace. Use `TbhCompanion-Presence.exe` if you'd rather
not take that risk.

The presence feature only ever *reads* the game's memory and is not a game
modification. The automation mod presses the game's own UI buttons and changes
nothing else; it is opt-in and only active when BepInEx is installed. Use at
your own risk.
