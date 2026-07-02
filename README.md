# tbh-presence — TaskBarHero Discord Rich Presence

Shows, in real time, **which stage TaskBarHero is on and your deployed party**
on your Discord profile, by reading the running game's memory (read-only — no
injection, no writes, no game files touched).

```
Playing TaskBarHero
Act 3 - Stage 3  (HELL, Lv 72)
Ranger Lv80, Sorcerer Lv23, Priest Lv35
23:41 elapsed
```

## Rich Presence

```powershell
.\Start-TbhPresence.ps1                      # default: poll every 15s
.\Start-TbhPresence.ps1 -IntervalSeconds 30  # slower polling
.\Start-TbhPresence.ps1 -ClientId <id>       # use your own Discord application
```

Talks to the Discord desktop client over its local IPC named pipe
(`discord-ipc-N`) — no libraries needed. It only sends an update when something
changed, survives game and Discord restarts, clears the presence while the game
is closed, and shows elapsed time from the game process start. Ctrl+C to quit
(presence is cleared on exit).

The client id is a Discord *application* id (create one at
https://discord.com/developers/applications — its name is what "Playing …"
shows). No bot token or secret is involved.

## Stage reader (standalone)

The game must be running.

```powershell
# Continuous console monitor (prints when the stage changes)
.\Get-TbhStage.ps1

# One reading as JSON, then exit (call this on a timer from a presence app)
.\Get-TbhStage.ps1 -Once

# Continuously mirror state to a file another process can read
.\Get-TbhStage.ps1 -JsonOut state.json

# Ignore the address cache and do a full rescan
.\Get-TbhStage.ps1 -NoCache
```

The first run scans the game's memory (~1 minute). Results are cached in
`cache.json` next to the script, so later runs start in under a second:

- **Same game process**: cached addresses are validated (PID + process start
  time + class pointer) and reused directly — no scan at all.
- **After a game restart**: addresses are re-resolved (the live object moved),
  but the stage table is reused, roughly halving the scan.
- **After a game update**: the cache is invalidated automatically (keyed on the
  game binary's timestamp/size) and everything is rebuilt.

In continuous mode the script also survives game restarts: when the game exits
it waits for a new process and re-attaches automatically.

Example output:

```json
{"stageKey":3305,"savedWave":20,"maxCompletedStage":3307,"act":3,"stageNo":5,
 "level":74,"waveAmount":27,"difficulty":"HELL","stageType":"NORMAL",
 "nameKey":"StageName_1305",
 "heroes":[{"key":201,"name":"Ranger","level":80},{"key":301,"name":"Sorcerer","level":23},
           {"key":401,"name":"Priest","level":35}],
 "petKey":1005,
 "label":"Act 3 - Stage 5  (Lv 74, HELL, 27 waves)  |  Ranger Lv80, Sorcerer Lv23, Priest Lv35"}
```

## What each field means

| Field | Source | Live? |
|---|---|---|
| `stageKey` | `CommonSaveData.currentStageKey` | Updates when you enter a stage |
| `act`, `stageNo`, `level`, `waveAmount`, `difficulty`, `stageType`, `nameKey` | `StageInfoData` table, looked up by `stageKey` | Static definition |
| `savedWave` | `CommonSaveData.currentStageWave` | **Only updates on game save**, not per wave |
| `maxCompletedStage` | `CommonSaveData.maxCompletedStage` | Updates on clear |
| `heroes` | `CommonSaveData.arrangedHeroKey` (int[]) + `HeroInfoData` table | Updates when you re-arrange your party |
| `heroes[].level` | `PlayerSaveData.heroSaveDatas` (`HeroSaveData.HeroLevel`) | Updates on level-up |
| `petKey` | `CommonSaveData.ArrangedPetKey` | Updates when you swap pets |

Hero names come from `EEquipClassType` — each hero maps 1:1 to a class:
Knight (101), Ranger (201), Sorcerer (301), Priest (401), Hunter (501), Slayer (601).
Pet names are only available as localization keys, so `petKey` stays numeric.

`nameKey` (e.g. `StageName_1305`) is a localization key; the display name comes
from the game's language table, which this tool does not read.

### About real-time wave progress
The current wave *within* a stage (1..27) lives in `StageManager` as an
Anti-Cheat-Toolkit `ObscuredInt` (`hidden ^ cryptoKey`, re-obfuscated each read),
so it is not exposed here. The **stage identity** is the reliable live signal and
is what "which stage am I on" means for a Rich Presence. If you later need the
live wave counter, hook it via a BepInEx/MelonLoader plugin instead of reading it
cold from memory.

## How it works

TaskBarHero is Unity **IL2CPP** (metadata v31), and its `global-metadata.dat` is
**not** obfuscated, so class names survive. On each launch the script:

1. Attaches to the `TaskBarHero` process with `PROCESS_VM_READ` only.
2. Finds the `PlayerSaveData` and `CommonSaveData` `Il2CppClass*` by scanning for
   the class-name strings and the objects that reference them
   (`Il2CppClass`: `+0x10` name, `+0x18` namespace).
3. Walks `PlayerSaveData -> commonSaveData` to get the live `CommonSaveData`
   object address (fixed for the process lifetime → cheap to re-read each poll).
4. Enumerates all `StageInfoData` instances once to build a
   `stageKey -> {act, stageNo, level, waveAmount, difficulty}` table.
5. Polls `currentStageKey` and looks up the details.

Because addresses are resolved by class name every launch, the tool keeps working
across game restarts. A **game update** can change struct field offsets — if the
output looks wrong after a patch, re-dump and update the offsets below.

## Field offsets (Il2CppDumper, current game build)

Object instance fields begin at `+0x10` (klass ptr `+0x0`, monitor `+0x8`).

```
PlayerSaveData.commonSaveData     +0x10
CommonSaveData.maxCompletedStage  +0x54   (int)
CommonSaveData.currentStageKey    +0x58   (int)
CommonSaveData.currentStageWave   +0x5C   (int)
CommonSaveData.ArrangedPetKey     +0x40   (int)
CommonSaveData.arrangedHeroKey    +0x48   (int[]: length +0x18, elements +0x20)
HeroInfoData.HeroKey              +0x30   (int)
HeroInfoData.HeroNameKey          +0x38   (System.String)
HeroInfoData.ClassType            +0x48   (EEquipClassType: 1 Knight, 2 Ranger, 3 Sorcerer, 4 Priest, 5 Hunter, 6 Slayer)
PlayerSaveData.heroSaveDatas      +0x58   (List<HeroSaveData>: items +0x10, count +0x18; element ptrs from items+0x20)
HeroSaveData.heroKey              +0x10   (int)
HeroSaveData.HeroLevel            +0x14   (int)
HeroSaveData.IsUnLock             +0x18   (bool)
HeroSaveData.HeroExp              +0x1C   (float)
StageInfoData.StageKey            +0x30   (int)
StageInfoData.StageNameKey        +0x38   (System.String)
StageInfoData.STAGETYPE           +0x40   (EStageType:   0 NORMAL, 1 ACTBOSS)
StageInfoData.STAGEDIFFICULTY     +0x44   (ESTAGEDIFFICULTY: 0 NORMAL,1 NIGHTMARE,2 HELL,3 TORMENT)
StageInfoData.Act                 +0x48   (int)
StageInfoData.StageNo             +0x4C   (int)
StageInfoData.StageLevel          +0x50   (int)
StageInfoData.WaveAmount          +0x54   (int)
```

### Re-dumping after a game update

```
Il2CppDumper.exe GameAssembly.dll TaskBarHero_Data/il2cpp_data/Metadata/global-metadata.dat out
```

Then check the `CommonSaveData` and `StageInfoData` classes in `out/dump.cs` and
update the offsets in `Get-TbhStage.ps1` (`$OFF`) if they moved.

## Files

- `Start-TbhPresence.ps1` — Discord Rich Presence (polls the reader, pushes to Discord IPC).
- `Get-TbhStage.ps1` — the monitor (resolve + poll + report/JSON, address cache, auto-reattach).
- `TbhMemory.cs` — read-only memory reader + IL2CPP class/object locator (compiled via `Add-Type`).
- `cache.json` — generated at runtime (gitignored); delete it or pass `-NoCache` to force a rescan.

## Notes

- Read-only and non-invasive; it never modifies the game. Still, this is a
  single-player idle game — mind the game's terms if any online leaderboard exists.
- Requires Windows PowerShell 5.1+ (uses `Add-Type`, kernel32 P/Invoke).
