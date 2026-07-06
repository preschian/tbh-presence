# TBH Auto Synthesis (BepInEx plugin)

Automates TaskBarHero's **Cube synthesis**: pick the highest unlocked recipe,
auto-fill the cube, run the synthesis, empty the cube, wait, repeat.

> **Unlike the presence app, this is a game mod.** It runs *inside* the game
> via [BepInEx](https://github.com/BepInEx/BepInEx) and clicks the game's own
> UI buttons for you. It never writes game memory or edits your save, but the
> game folder gets BepInEx files added. Back up your save first
> (`%USERPROFILE%\AppData\LocalLow\TesseractStudio\TaskbarHero\SaveFile_Live.es3`).

## Install

1. Download **BepInEx 6 bleeding-edge** (`BepInEx-Unity.IL2CPP-win-x64-*.zip`
   from [builds.bepinex.dev](https://builds.bepinex.dev/projects/bepinex_be))
   and extract it into the game folder
   (`...\Steam\steamapps\common\TaskbarHero`).
2. Launch the game once and wait ~1 minute — BepInEx generates its interop
   assemblies into `BepInEx\interop` on first run.
3. Build the plugin (needs the interop assemblies from step 2):

   ```powershell
   dotnet build autosynth/TbhAutoSynth.csproj -c Release
   ```

4. Copy `autosynth\bin\Release\TbhAutoSynth.dll` into
   `<game>\BepInEx\plugins\` and restart the game — or skip this step entirely:
   `TbhPresence.exe` bundles the plugin and deploys/updates it automatically
   whenever BepInEx is present (retrying while the game is closed).

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

Config is read at game start — restart the game after editing.

## How it works / maintenance

The game's class names survive IL2CPP (methods are obfuscated), so the plugin
drives the real UI components (`UI_Cube`, `TS.ButtonBase`) and reads slot item
grades from the game's own item table (`ItemKey → GRADE`). Two gotchas when a
game update breaks things:

- Obfuscated member names differ between Il2CppDumper output and BepInEx's
  Cpp2IL interop (e.g. dump `bsfb` → interop `bsfm`). Always check the interop
  assemblies (`BepInEx\interop\Assembly-CSharp.dll`) when renaming.
- Clicking a `ButtonBase` needs both `OnPointerClick(...)` *and* the wrapped
  `Button.onClick.Invoke()` — the former only plays hover/click effects.
