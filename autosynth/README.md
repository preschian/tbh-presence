# TBH Auto Synthesis (BepInEx plugin)

Automates TaskBarHero's shared idle cycle: optional **soulstone spending**,
optional **StageBox chest opens**, optional **Cube alchemy**, optional **Cube
offering**, optional **Cube synthesis**, and optional **Rune upgrades** — then
wait and repeat.

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
Each armed cycle runs enabled phases in order: **Soulstone → Chest → Offering →
Alchemy → Synthesis → Rune**. If the main menu/HUD is closed when a phase needs the content
row, the plugin clicks the stage-HUD **Show Main** button (next to auto-retry) —
never synthesizes Tab. With `AutoOpenCube` on it then clicks the **Cube** menu
button when the Synthesis phase is due. Hotkeys:

| Key | Action |
|-----|--------|
| **F7** | Run one cycle now (soulstone → chest → offering → alchemy → synthesis → rune for enabled phases) |
| **F8** | Toggle the auto loop on/off |
| **F9** | Click the synthesis trigger once |
| **F10** | Dump soulstone / chest / alchemy / offering / synthesis / rune state to `BepInEx\LogOutput.log` |

The Synthesis phase only acts while the Cube panel is open. With `AutoOpenCube`
off it waits for you to open the panel yourself instead of opening it. When
auto-fill fills every cube slot there are usually more materials left, so with
`RepeatFullSynth` on (the default) that type runs another fill → synth → clear
pass right away, up to `MaxSynthRepeatsPerCycle` extra passes, then continues
with the next enabled type (Equipment → Materials → Accessories, skipping any
that are off). The Chest phase uses the stage HUD StageBox click-detector:

- with **Rune of Opening** (`OpenOneTypeChestAllAtOnce`) → one **right-click**
  per chest type (opens that whole stack)
- with the higher open-all rune (`OpenAllTypeChestAllAtOnce`) → the game's
  open-all key (Space)
- otherwise → left-click one chest at a time

It does **not** flip the game's built-in auto-open toggle.

## Alchemy

The Alchemy phase (off by default) melts junk gear into gold. It selects
**Alchemy** on the Cube's recipe dropdown, sends eligible inventory items to the
cube nine at a time, runs the operation — confirming the warning panel if the
game shows one — and repeats while items remain, up to
`MaxAlchemyBatchesPerCycle`. It always hands the Cube back on the **Synthesis**
recipe so the Synthesis phase finds it where it expects.

Items are moved with the game's own `MoveToCube` slot action
(`SlotInteractionManager`), the same one a right click resolves to, rather than
by faking pointer events: `ItemSlot`'s click hooks are really drag handlers, and
a synthetic press with no matching release leaves the player holding the item.

An item is eligible only when **all** of these hold:

- it is gear (materials and chests are never touched),
- its item level is strictly below `AlchemyLevelThreshold`,
- its rarity is at or below `MaxAlchemyGrade`,
- it is not locked or reserved, and its item key is not in `AlchemyProtectedItemKeys`.

Equipped gear lives in the gear slots, not the inventory, so it is never a
candidate. `AlchemyLevelThreshold = 0` (the default) means nothing is eligible.
**Set `AlchemyDryRun = true` for one cycle first** — the phase then only writes
the items it *would* melt to the log, so you can check the threshold before it
destroys anything.

## Offering

The Offering phase (off by default) selects **Offering** on the Cube's main
recipe dropdown, looks for offering coins in the inventory, moves exactly one
coin into the Cube, and clicks the process button. It repeats while another coin
is available, up to `MaxOfferingOperationsPerCycle` operations (`5` by default),
then returns the Cube to **Synthesis**.

Coins are identified through the game's own material table as
`EMaterialType.OFFERING`; the plugin does not depend on a localized item name or
a hard-coded item key. Locked and reserved inventory slots are skipped.

## Runes

The Rune phase repeatedly buys the cheapest affordable level-up until gold runs
out or `MaxRuneUpgradesPerCycle` is reached, then closes the panel.

Before any of that it checks the rune table while the panel is still **closed**:
rune levels, the cost of each next level, and your gold are all readable without
showing anything. When every unlocked rune is maxed, or the cheapest next level
costs more than you have, the phase ends there and the panel is never opened —
no menu flicker, and nothing for you to click back out of. The reason lands in
the log, e.g.:

```
rune phase: every rune is at max level [196/197 runes unlocked] — leaving the panel closed
```

Runes still locked in the tree are ignored by that check: their next level exists
in the game's data, but the panel would not let anyone buy it.

## Soulstones

The Soulstone phase (off by default) spends surplus soulstones the way the game
intends: by entering an **Act Boss** stage — the `*-10` stages — that has
already been cleared. TaskBarHero has one soulstone tier per difficulty (Normal,
Nightmare, Hell, Torment) and every Act Boss stage names the tier and the number
of stones it costs, so the phase never has to guess which stone it is spending.

A stage is a candidate only when **all** of these hold:

- it is an Act Boss stage (`STAGETYPE = ACTBOSS`),
- its stage key is at or below the account's `maxCompletedStage`, i.e. it has
  been cleared at least once,
- its soulstone tier is one you allow in `SoulstoneTiers`, and
- you hold enough stones of that tier to cover its cost.

Among the candidates it takes the highest tier you allowed and the deepest act
within it, switches the Portal to that tier's difficulty, and enters it — once.
The game's own auto-retry keeps re-entering the boss while stones remain, so
there is nothing for the plugin to repeat; instead it counts the runs going by and
walks the hero back to the stage it came from after `ActBossRunsPerCycle` of them.

A run is counted from **soulstones spent** — one per entry, which the game's
auto-retry does on its own — or from **Act Boss chests gained**, whichever is
ahead. The stones are the reliable half: with the game's auto-open on, the chest
stack may never grow at all, and only chest *gains* count so a drain in between
never reads as negative progress. The watch also ends early if the tier's stones
run out or the hero leaves the boss stage on its own, and after
`ActBossWatchMinutes` it gives up on the target and heads back anyway.

**Entering a boss stage moves your hero off whatever it was farming**, which is
the point of the feature but worth knowing before arming it — set
`SoulstoneDryRun = true` for one cycle first and the phase walks the whole path
(open the Portal, switch difficulty and act, find the node) and stops on the
doorstep, writing the stage it *would* have entered to the log.

Everything the decision needs — the stage table, `maxCompletedStage`, and the
soulstone counts — reads fine while every panel is closed, so a cycle with
nothing to spend never opens the Portal at all, same as the Rune phase:

```
soulstone phase: no soulstones for the 9 cleared Act Boss stage(s) on the enabled tier(s) (Hell,Torment) — leaving the Portal closed
```

Both moves go through the Portal's own controls — the difficulty dropdown and
the act selector. The dropdown's entries are calibrated against the one selected
right now rather than assumed to be in `ESTAGEDIFFICULTY` order, so an account
that has not unlocked every difficulty still lands on the right map, and a tier
the dropdown does not offer is reported instead of guessed at.

## Config

`<game>\BepInEx\config\com.pres.tbh.autosynth.cfg` (created on first run):

| Key | Default | Meaning |
|-----|---------|---------|
| `AutoStart` | true | Arm the auto loop at game start, no F8 needed |
| `EnableSynthesis` | true | Include the Synthesis phase (Cube fill → synth → clear) in the cycle |
| `AutoOpenCube` | true | Click the Cube menu button to open the Cube panel when the Synthesis phase is due (at most once every 10s, so it doesn't fight you for the tab) |
| `AutoOpenChest` | false | After the Soulstone phase (or at cycle start if it is off), click StageBox chests (Normal / Boss / ActBoss) |
| `AutoUpgradeRune` | false | After the other phases, open the Rune panel and upgrade the cheapest affordable runes |
| `AutoOpenRune` | true | During the Rune phase, click the Rune menu button to open the Rune panel |
| `AutoConsumeSoulstone` | false | After the other phases, enter a cleared Act Boss stage to spend surplus soulstones |
| `SoulstoneTiers` | Normal,Nightmare,Hell,Torment | Which soulstone tiers may be spent; each tier is the difficulty of the same name. e.g. `Hell,Torment` to leave the lower stones alone. |
| `AutoOpenPortal` | true | During the Soulstone phase, click the Portal menu button to open the stage map |
| `SoulstoneDryRun` | false | Log the Act Boss stages the Soulstone phase would enter instead of entering them |
| `ActBossRunsPerCycle` | 5 | Act Boss runs to farm before the hero is walked back to the stage it came from |
| `ActBossWatchMinutes` | 10 | Give up on the chest target after this long and walk back anyway |
| `AfterSoulstoneEnterSeconds` | 5 | Delay after clicking a stage's enter button |
| `AutoAlchemy` | false | Run the Alchemy phase before the Synthesis phase |
| `AlchemyDryRun` | false | Log the items the Alchemy phase would melt instead of clicking them |
| `AlchemyLevelThreshold` | 0 | Gear below this item level is eligible for alchemy (e.g. `80` melts levels 1–79). `0` disables the phase. |
| `MaxAlchemyGrade` | 2 | Highest rarity the Alchemy phase may melt (same scale as `MaxGrade`) |
| `MaxAlchemyBatchesPerCycle` | 5 | Safety cap on alchemy operations (9 items each) per cycle |
| `AlchemyProtectedItemKeys` | *(empty)* | Comma-separated item keys the Alchemy phase must never melt |
| `AfterAlchemyClickSeconds` | 0.35 | Delay between successive items while filling the Alchemy cube |
| `AutoOffering` | false | Run the Offering phase before Alchemy |
| `MaxOfferingOperationsPerCycle` | 5 | Safety cap on one-coin Offering operations per cycle |
| `SynthesisTypes` | Equipment,Materials,Accessories | Which item types each cycle synthesizes in order. e.g. `Equipment,Accessories` to skip materials. |
| `DesiredLevel` | 0 | Target synthesis recipe. `0` = highest unlocked (default). Otherwise the lower bound of an in-game bracket from the companion Target level dropdown (`1`=`Lv.1~10` … `65`=`Lv.65~80`). If that bracket is locked, uses the highest unlocked bracket with `lo ≤ DesiredLevel`. |
| `MaxGrade` | 3 | Highest rarity the loop may synthesize (0=Common, 1=Uncommon, 2=Rare, 3=Legendary, 4=Immortal, …). Cycles holding anything above this are skipped. |
| `RepeatFullSynth` | true | When auto-fill fills every cube slot, run another fill → synth → clear pass for the same type instead of moving on. Stops on a partial or empty fill, a grade-limit skip, or `MaxSynthRepeatsPerCycle`, then continues with the next enabled type. |
| `MaxSynthRepeatsPerCycle` | 10 | Safety cap on the extra passes `RepeatFullSynth` may run per synthesis type in one cycle |
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
