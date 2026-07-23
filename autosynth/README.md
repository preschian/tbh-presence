# TBH Auto Synthesis (BepInEx plugin)

Automates TaskBarHero's shared idle cycle: optional **Cube synthesis**, optional
**StageBox chest opens**, and optional **Rune upgrades** — then wait and repeat.

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

With `AutoStart` on (the default) the loop is already armed when the game starts.
Each armed cycle runs enabled phases in order: **Cube → Chest → Rune**. If the main
menu/HUD is closed when Cube or Rune needs the content row, the plugin clicks the
stage-HUD **Show Main** button (next to auto-retry) — never synthesizes Tab.
With `AutoOpenCube` on it then clicks the **Cube** menu button when a Cube cycle
is due. Hotkeys:

| Key | Action |
|-----|--------|
| **F7** | Run one cycle now (cube → chest → rune for enabled phases) |
| **F8** | Toggle the auto loop on/off |
| **F9** | Click the synthesis trigger once |
| **F10** | Dump cube / chest / rune state to `BepInEx\LogOutput.log` |

The Cube phase only acts while the Cube panel is open. With `AutoOpenCube` off
it waits for you to open the panel yourself instead of opening it. The Chest
phase uses the stage HUD StageBox click-detector:

- with **Rune of Opening** (`OpenOneTypeChestAllAtOnce`) → one **right-click**
  per chest type (opens that whole stack)
- with the higher open-all rune (`OpenAllTypeChestAllAtOnce`) → the game's
  open-all key (Space)
- otherwise → left-click one chest at a time

It does **not** flip the game's built-in auto-open toggle.

## Config

`<game>\BepInEx\config\com.pres.tbh.autosynth.cfg` (created on first run):

| Key | Default | Meaning |
|-----|---------|---------|
| `AutoStart` | true | Arm the auto loop at game start, no F8 needed |
| `EnableSynthesis` | true | Include Cube synthesis in the cycle |
| `AutoOpenCube` | true | Click the Cube menu button to open the Cube panel when a cycle is due (at most once every 10s, so it doesn't fight you for the tab) |
| `AutoOpenChest` | false | After Cube (or at cycle start if synthesis is off), click StageBox chests (Normal / Boss / ActBoss) |
| `AutoUpgradeRune` | false | After Cube/Chest, open the Rune panel and upgrade the cheapest affordable runes |
| `AutoOpenRune` | true | During the Rune phase, click the Rune menu button to open the Rune panel |
| `SynthesisTypes` | Equipment,Materials,Accessories | Which item types to synthesize; the loop rotates through them each round. e.g. `Equipment,Materials` to skip accessories. |
| `DesiredLevel` | 0 | Target synthesis recipe. `0` = highest unlocked (default). Otherwise the lower bound of an in-game bracket from the companion Target level dropdown (`1`=`Lv.1~10` … `65`=`Lv.65~80`). If that bracket is locked, uses the highest unlocked bracket with `lo ≤ DesiredLevel`. |
| `MaxGrade` | 3 | Highest rarity the loop may synthesize (0=Common, 1=Uncommon, 2=Rare, 3=Legendary, 4=Immortal, …). Cycles holding anything above this are skipped. |
| `MaxChestOpensPerCycle` | 40 | Safety cap on StageBox open clicks per cycle |
| `MaxRuneUpgradesPerCycle` | 20 | Safety cap on rune level-ups per cycle |
| `CycleIntervalSeconds` | 300 | Pause between cycles |
| `AfterFillSeconds` | 1 | Delay between auto-fill and synthesis |
| `AfterSynthesisSeconds` | 4 | Delay for the synthesis animation to finish |
| `AfterChestOpenSeconds` | 1.5 | Delay after each chest open click |
| `AfterRuneUpgradeSeconds` | 0.5 | Delay between successive rune level-up clicks |

The BepInEx log console window (where these messages appear) can be shown or
hidden via `BepInEx\config\BepInEx.cfg` under `[Logging.Console] → Enabled`, or
with the checkbox in the Status & Settings window (takes effect on the next
game start).

The plugin re-reads its own config every ~10 seconds while the game runs, so
edits apply without a restart. The easiest way to edit it is the **Status & Settings**
window in `TbhCompanion.exe` (double-click the tray icon). The plugin also
reports its live status (on/off, cycles, last synthesis / chests / runes) to
`%LOCALAPPDATA%\tbh-companion\autosynth-status.json`, which that window displays.

How the plugin works internally, and what to check when a game update breaks
it, is documented in [CONTRIBUTING.md](../CONTRIBUTING.md).
