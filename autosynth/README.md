# TBH Auto Synthesis (BepInEx plugin)

Automates TaskBarHero's **Cube synthesis**: pick the highest unlocked recipe,
auto-fill the cube, run the synthesis, empty the cube, wait, repeat.

> **Unlike the presence app, this is a game mod.** It runs *inside* the game
> via [BepInEx](https://github.com/BepInEx/BepInEx) and clicks the game's own
> UI buttons for you. It never writes game memory or edits your save, but the
> game folder gets BepInEx files added. Back up your save first
> (`%USERPROFILE%\AppData\LocalLow\TesseractStudio\TaskbarHero\SaveFile_Live.es3`).

## Install

Follow **"Setting up auto-synthesis"** in the [main README](../README.md):
install BepInEx into the game folder once, and `TbhCompanion.exe` installs and
updates this plugin automatically. (Building it from source is covered in
[CONTRIBUTING.md](../CONTRIBUTING.md).)

## Use

Open the **Cube** panel, then:

With `AutoStart` on (the default) the loop is already armed when the game
starts — just open the Cube panel and it runs. Hotkeys:

| Key | Action |
|-----|--------|
| **F8** | Toggle the auto loop: select highest unlocked recipe → auto-fill → grade check → synthesis → clear cube → wait → repeat |
| **F9** | Click the synthesis trigger once |
| **F10** | Dump button states and cube-slot item grades to `BepInEx\LogOutput.log` |

The Cube panel must stay open while the loop runs.

## Config

`<game>\BepInEx\config\com.pres.tbh.autosynth.cfg` (created on first run):

| Key | Default | Meaning |
|-----|---------|---------|
| `AutoStart` | true | Arm the auto loop at game start, no F8 needed |
| `MaxGrade` | 3 | Highest rarity the loop may synthesize (0=Common, 1=Uncommon, 2=Rare, 3=Legendary, 4=Immortal, …). Cycles holding anything above this are skipped. |
| `CycleIntervalSeconds` | 300 | Pause between cycles |
| `AfterFillSeconds` | 1 | Delay between auto-fill and synthesis |
| `AfterSynthesisSeconds` | 4 | Delay for the synthesis animation to finish |

The plugin re-reads the config every ~10 seconds while the game runs, so edits
apply without a restart. The easiest way to edit it is the **Status & Settings**
window in `TbhCompanion.exe` (double-click the tray icon). The plugin also
reports its live status (on/off, cycles, last synthesis) to
`%LOCALAPPDATA%\tbh-companion\autosynth-status.json`, which that window displays.

How the plugin works internally, and what to check when a game update breaks
it, is documented in [CONTRIBUTING.md](../CONTRIBUTING.md).
