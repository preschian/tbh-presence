# Contributing / technical notes

How the tool works internally, the memory offsets it depends on, and how to
rebuild them after a game update. For end-user instructions see the
[README](README.md).

## Overview

TaskBarHero is a Unity **IL2CPP** game (metadata v31) whose
`global-metadata.dat` is **not** obfuscated, so class and field names survive.
The tool reads the running game's memory (read-only, `PROCESS_VM_READ` only —
never writes) to find where you are, and pushes that to Discord.

Two independent pieces:

- **Reader** — resolves the game's object graph and reads the current state.
- **Presence** — polls the reader and sends `SET_ACTIVITY` frames to the Discord
  desktop client over its local IPC named pipe (`discord-ipc-N`). No libraries.

## How resolution works

On each launch the reader:

1. Attaches to the `TaskBarHero` process with `PROCESS_VM_READ` only.
2. Finds `Il2CppClass*` pointers by class name: it scans memory for the
   class-name string, then for qwords that point at it. In `Il2CppClass` the
   name is at `+0x10` and the namespace at `+0x18`, which disambiguates.
3. Walks `PlayerSaveData -> commonSaveData` to get the live `CommonSaveData`
   object (fixed for the process lifetime → cheap to re-read each poll).
4. Enumerates all `StageInfoData` and `HeroInfoData` instances once to build
   lookup tables (`stageKey -> {...}`, `heroKey -> class name`).
5. For the **live** current stage it also resolves the `ux.uq` static-field
   block and reads `StageCache -> StageInfoData` from it — see below.

Because everything is resolved by class name, the tool keeps working across game
restarts. A **game update** can shift struct offsets; if output looks wrong after
a patch, re-dump (below) and update the offsets.

### Live stage vs. saved stage

`CommonSaveData.currentStageKey` only updates when the game autosaves, so it lags
behind stage changes. The reader prefers the **live stage system**: the `ux.uq`
class holds a static `StageCache` (`bfan`) for the currently loaded stage, and
that flips the instant a new stage loads.

`ux.uq` has no unique class-name string to scan for (it's an obfuscated short
name that the obfuscator re-randomizes on updates — `vb.uu` → `uw.up` @1.00.27 →
`ux.uq` @1.01.01), so it's located by:

1. scanning for the `"uq"` name string (also tries recent `"up"` / `"uu"`),
2. reading each referencing class's static-field block (`Il2CppClass.static_fields`
   at `+0xB8`),
3. accepting the block only if its `+0x88` slot points at a valid `StageCache`
   instance (self-validating).

The reader then reads `StageCache.StageInfoData (+0x10) -> StageKey` live and
falls back to save data if the block can't be found. The JSON field
`stageSource` reports `"live"` or `"save"`.

### Real-time wave progress (not exposed)

The current wave *within* a stage (1..27) lives in `StageManager` as an
Anti-Cheat-Toolkit `ObscuredInt` (`hiddenValue ^ currentCryptoKey`, struct
`hash/hidden/key/fake` at `+0/+4/+8/+C`, re-obfuscated each read). It's not read
here — the stage identity is the reliable live signal. To get the live wave
counter, hook it via a BepInEx/MelonLoader plugin rather than reading it cold.

## State fields

`--once` prints the full state as JSON:

```json
{"stageKey":3305,"savedWave":20,"maxCompletedStage":3307,"act":3,"stageNo":5,
 "level":74,"waveAmount":27,"difficulty":"HELL","stageType":"NORMAL",
 "nameKey":"StageName_1305",
 "heroes":[{"key":201,"name":"Ranger","level":80},{"key":301,"name":"Sorcerer","level":23},
           {"key":401,"name":"Priest","level":35}],
 "petKey":1005,"stageSource":"live",
 "label":"Act 3 - Stage 5  (HELL, Lv 74)  |  Ranger Lv80, Sorcerer Lv23, Priest Lv35"}
```

| Field | Source | Live? |
|---|---|---|
| `stageKey` | live `StageCache`, else `CommonSaveData.currentStageKey` | Updates on stage load (live) |
| `act`, `stageNo`, `level`, `waveAmount`, `difficulty`, `stageType`, `nameKey` | `StageInfoData` table, keyed by `stageKey` | Static definition |
| `savedWave` | `CommonSaveData.currentStageWave` | **Only on autosave**, not per wave |
| `maxCompletedStage` | `CommonSaveData.maxCompletedStage` | Updates on clear |
| `heroes` | `CommonSaveData.arrangedHeroKey` (int[]) + `HeroInfoData` table | Updates on party change |
| `heroes[].level` | `PlayerSaveData.heroSaveDatas` (`HeroSaveData.HeroLevel`) | Updates on level-up |
| `petKey` | `CommonSaveData.ArrangedPetKey` | Updates on pet swap |

Hero names come from `EEquipClassType` — each hero maps 1:1 to a class:
Knight (101), Ranger (201), Sorcerer (301), Priest (401), Hunter (501),
Slayer (601). Pet names and `nameKey` (e.g. `StageName_1305`) are localization
keys living in compressed Addressables bundles, which this tool does not read, so
`petKey` stays numeric.

## Address cache

The exe caches resolved addresses in `%LOCALAPPDATA%\tbh-companion\cache.txt`
(the PowerShell reader uses `cache.json` next to the script):

- **Same game process** — cached addresses are validated (PID + process start
  time + class pointer) and reused directly, no scan.
- **After a game restart** — addresses are re-resolved (the live object moved),
  but the static tables are reused, roughly halving the scan.
- **After a game update** — the cache is invalidated (keyed on the game binary's
  timestamp/size) and everything is rebuilt.

Pass `--no-cache` (exe) / `-NoCache` (scripts) to force a full rescan.

## Field offsets (Il2CppDumper, game build 1.01.01)

Object instance fields begin at `+0x10` (klass ptr `+0x0`, monitor `+0x8`).

```
PlayerSaveData.commonSaveData     +0x10
PlayerSaveData.heroSaveDatas      +0x60   (List<HeroSaveData>: items +0x10, count +0x18; element ptrs from items+0x20)
CommonSaveData.ArrangedPetKey     +0x40   (int)
CommonSaveData.arrangedHeroKey    +0x48   (int[]: length +0x18, elements +0x20)
CommonSaveData.maxCompletedStage  +0x54   (int)
CommonSaveData.currentStageKey    +0x58   (int)
CommonSaveData.currentStageWave   +0x5C   (int)
HeroInfoData.HeroKey              +0x30   (int)
HeroInfoData.HeroNameKey          +0x38   (System.String)
HeroInfoData.ClassType            +0x48   (EEquipClassType: 1 Knight, 2 Ranger, 3 Sorcerer, 4 Priest, 5 Hunter, 6 Slayer)
HeroSaveData.heroKey              +0x10   (int)
HeroSaveData.HeroLevel            +0x14   (int)
HeroSaveData.IsUnLock             +0x18   (bool)
HeroSaveData.HeroExp              +0x20   (double)
StageInfoData.StageKey            +0x30   (int)
StageInfoData.StageNameKey        +0x38   (System.String)
StageInfoData.STAGETYPE           +0x40   (EStageType:   0 NORMAL, 1 ACTBOSS)
StageInfoData.STAGEDIFFICULTY     +0x44   (ESTAGEDIFFICULTY: 0 NORMAL,1 NIGHTMARE,2 HELL,3 TORMENT)
StageInfoData.Act                 +0x48   (int)
StageInfoData.StageNo             +0x4C   (int)
StageInfoData.StageLevel          +0x50   (int)
StageInfoData.WaveAmount          +0x54   (int)
ux.StageCache.StageInfoData       +0x10
Il2CppClass.static_fields         +0xB8
ux.uq static block -> StageCache  +0x88
```

### Re-dumping after a game update

```
Il2CppDumper.exe GameAssembly.dll TaskBarHero_Data/il2cpp_data/Metadata/global-metadata.dat out
```

Check the relevant classes in `out/dump.cs` and update the offsets — in
`src/Game.cs` (`GameReader`) for the exe, and in `legacy/Get-TbhStage.ps1` (`$OFF`) for
the scripts. Bump `CACHE_VERSION` so stale caches are discarded.

## Building

Uses the C# compiler that ships with Windows (.NET Framework 4.x) — no SDK:

```powershell
.\build.ps1     # -> TbhCompanion.exe (single self-contained file)
```

Compiled as `/target:winexe` so there's no console window in tray mode; console
modes (`--console`, `--once`, `--help`) attach a console on demand. The TBH logo
is embedded via `/win32icon:assets\app.ico`.

Building the auto-synthesis plugin needs the .NET 8 SDK and a game folder with
BepInEx already initialized (the interop assemblies it references are generated
by BepInEx on first game launch):

```powershell
dotnet build autosynth/TbhAutoSynth.csproj -c Release
```

`build.ps1` embeds the plugin dll into the exe (the exe deploys it to the
game's `BepInEx\plugins` at runtime). It uses a fresh `autosynth\bin\Release\`
build if present, otherwise the committed `autosynth\prebuilt\TbhAutoSynth.dll`.
CI can't build the plugin (no game to generate the interop assemblies), so it
relies on the prebuilt copy — **after changing `autosynth\Plugin.cs`, rebuild
the plugin and refresh `autosynth\prebuilt\TbhAutoSynth.dll` before tagging a
release**, or the shipped exe carries a stale plugin.

## Installing BepInEx manually

The Status & Settings window has an **Install mods** button that downloads and
installs BepInEx automatically (`src/BepInExSetup.cs`, pinned to a validated
bleeding-edge build), and a **Remove mods** button that deletes the same
BepInEx/Doorstop files from the game folder. To do it by hand instead:

1. Back up your save:
   `%USERPROFILE%\AppData\LocalLow\TesseractStudio\TaskbarHero\SaveFile_Live.es3`
2. Download the newest `BepInEx-Unity.IL2CPP-win-x64-*.zip` from
   [builds.bepinex.dev/projects/bepinex_be](https://builds.bepinex.dev/projects/bepinex_be).
3. Extract it into the game folder so `winhttp.dll` and `BepInEx\` sit next to
   `TaskBarHero.exe`.
4. Launch the game once (BepInEx generates its interop assemblies), then close.
5. With `TbhCompanion.exe` running, the plugin is deployed automatically.

## Command-line options

```
TbhCompanion.exe                 run in the system tray (default)
TbhCompanion.exe --console       run in a console with live logging
TbhCompanion.exe --once          print the current game state as JSON and exit
  --interval <sec>              poll interval (default 5)
  --client-id <id>              use your own Discord application
  --no-cache                    ignore the address cache, full rescan
```

## Using your own Discord application

The presence works out of the box with a shared app id, but the "Playing…"
name and logo are owned by the Discord *application*:

1. In the [Discord Developer Portal](https://discord.com/developers/applications),
   create an application named **TaskBarHero** (that name shows after "Playing…").
2. Under **Rich Presence → Art Assets**, add an image named exactly **`tbh`**
   (`assets/tbh.jpg` works) for the large logo.
3. Run the exe with the application's id: `TbhCompanion.exe --client-id <id>`.

Changes can take minutes — and a Discord restart — to show up.

## Releasing

Push a version tag; a Windows runner builds the exe and attaches it to a GitHub
Release (`.github/workflows/release.yml`):

```powershell
git tag v1.0.0
git push origin v1.0.0
```

`workflow_dispatch` from the **Actions** tab produces a plain build artifact
without cutting a release. The exe is gitignored — distribute via Releases, not
by committing the binary.

## Layout

**Portable exe** (`src/`, compiled by `build.ps1`):

- `Memory.cs` — read-only process-memory reader + IL2CPP class/object locator.
- `Game.cs` — offsets, address cache, and state reading (`GameReader`).
- `Discord.cs` — Discord Rich Presence over the IPC named pipe.
- `PresenceEngine.cs` — the UI-agnostic poll/update loop.
- `Tray.cs` — system-tray host (icon, status menu, quit).
- `Program.cs` — CLI parsing and run modes.

**Auto-synthesis plugin** (`autosynth/`, BepInEx, built with the .NET SDK):

- `Plugin.cs` — cycle orchestrator (Cube → Chest → Rune); usage/config in `autosynth/README.md`.
- `ChestOpenRunner.cs` — StageBox chest-open phase (`UI_Stage` / `StageBox.m_boxButton`).
- `RuneUpgradeRunner.cs` — rune upgrade phase.
- `GameInterop.cs` — signature-based access to obfuscated members (including box counts).

The plugin drives the game's real UI components (`UI_Cube`, `StageBox`,
`TS.ButtonBase`) and reads cube-slot item grades from the game's own item table
(`ItemKey → GRADE` via the `bas` singleton). Gotchas when a game update breaks
it:

- Obfuscated member names differ between Il2CppDumper output and BepInEx's
  Cpp2IL interop (e.g. dump `bsfb` → interop `bsfm`). Always verify names
  against `BepInEx\interop\Assembly-CSharp.dll`.
- Clicking a `ButtonBase` needs both `OnPointerClick(...)` *and* the wrapped
  `Button.onClick.Invoke()` — the former only plays hover/click effects.
- The sub-recipe (cube level) dropdown entries carry prefab-default labels
  until populated; the plugin triggers population and picks by
  `DesiredLevel` (0 = highest unlocked lower bound; otherwise exact lower
  bound match, else the highest unlocked `lo ≤ DesiredLevel` — same discrete
  tiers as the companion Target level dropdown).

**Legacy PowerShell prototype** (`legacy/`, for development/inspection):

- `Get-TbhStage.ps1` — the reader (resolve + poll + report/JSON).
- `Start-TbhPresence.ps1` — the original presence loop.
- `TbhMemory.cs` — reader compiled at runtime via `Add-Type`.
- Requires Windows PowerShell 5.1+.

**Assets:**

- `assets/app.ico` — embedded exe/tray icon (generated from `tbh.jpg`).
- `assets/tbh.jpg` — the `tbh` Rich Presence art asset to upload to Discord.
