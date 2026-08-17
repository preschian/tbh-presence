using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using TaskbarHero;
using TaskbarHero.Data;
using TaskbarHero.UI;
using TS;
using UnityEngine;
using UnityEngine.UI;

namespace TbhAutoSynth;

[BepInPlugin("com.pres.tbh.autosynth", "TBH Auto Synthesis", AutoSynthPlugin.Version)]
public class AutoSynthPlugin : BasePlugin
{
    internal const string Version = "0.33.0";

    internal static ManualLogSource Logger;
    private static ConfigFile _conf;
    private static ConfigEntry<float> _afterFillE, _afterSynthE, _cycleE, _afterRuneE, _afterChestE,
        _afterAlchemyE, _afterSoulstoneE, _activityIdleE;
    private static ConfigEntry<int> _maxGradeE, _desiredLevelE, _maxRuneUpgradesE, _maxChestOpensE,
        _maxSynthRepeatsE, _alchemyLevelE, _maxAlchemyGradeE, _maxAlchemyBatchesE, _maxOfferingOperationsE,
        _actBossRunsE, _actBossWatchE;
    private static ConfigEntry<bool> _autoStartE, _autoOpenE, _autoRuneE, _autoOpenRuneE, _enableSynthE, _autoChestE,
        _repeatFullSynthE, _autoAlchemyE, _alchemyDryRunE, _autoOfferingE,
        _autoSoulstoneE, _autoOpenPortalE, _soulstoneDryRunE, _pauseOnActivityE;
    private static ConfigEntry<string> _alchemyProtectedE, _soulstoneTiersE;

    internal static float AfterFillDelay => _afterFillE != null ? _afterFillE.Value : 1.0f;
    internal static float AfterSynthDelay => _afterSynthE != null ? _afterSynthE.Value : 4.0f;
    internal static float AfterClearDelay => _cycleE != null ? _cycleE.Value : 300.0f;
    internal static float AfterRuneUpgradeDelay => _afterRuneE != null ? _afterRuneE.Value : 0.5f;
    internal static float AfterChestOpenDelay => _afterChestE != null ? _afterChestE.Value : 1.5f;
    internal static int MaxGrade => _maxGradeE != null ? _maxGradeE.Value : 2;
    // 0 = highest unlocked recipe (default). >0 = exact lower-bound match, else
    // the highest unlocked bracket with lo <= DesiredLevel.
    internal static int DesiredLevel => _desiredLevelE != null ? _desiredLevelE.Value : 0;
    internal static int MaxRuneUpgradesPerCycle => _maxRuneUpgradesE != null ? _maxRuneUpgradesE.Value : 20;
    internal static int MaxChestOpensPerCycle => _maxChestOpensE != null ? _maxChestOpensE.Value : 40;
    internal static int MaxSynthRepeatsPerCycle => _maxSynthRepeatsE != null ? _maxSynthRepeatsE.Value : 10;
    internal static bool RepeatFullSynth => _repeatFullSynthE == null || _repeatFullSynthE.Value;
    internal static bool AutoStart => _autoStartE == null || _autoStartE.Value;
    internal static bool PauseOnActivity => _pauseOnActivityE != null && _pauseOnActivityE.Value;
    internal static float ActivityIdleSeconds =>
        _activityIdleE != null ? Math.Max(5f, _activityIdleE.Value) : 30f;
    internal static bool AutoOpenCube => _autoOpenE == null || _autoOpenE.Value;
    internal static bool AutoUpgradeRune => _autoRuneE != null && _autoRuneE.Value;
    internal static bool AutoOpenRune => _autoOpenRuneE == null || _autoOpenRuneE.Value;
    internal static bool AutoOpenChest => _autoChestE != null && _autoChestE.Value;
    internal static bool EnableSynthesis => _enableSynthE == null || _enableSynthE.Value;
    internal static float AfterAlchemyClickDelay => _afterAlchemyE != null ? _afterAlchemyE.Value : 0.35f;
    internal static bool AutoAlchemy => _autoAlchemyE != null && _autoAlchemyE.Value;
    internal static bool AlchemyDryRun => _alchemyDryRunE != null && _alchemyDryRunE.Value;
    internal static int AlchemyLevelThreshold => _alchemyLevelE != null ? _alchemyLevelE.Value : 0;
    internal static int MaxAlchemyGrade => _maxAlchemyGradeE != null ? _maxAlchemyGradeE.Value : 2;
    internal static int MaxAlchemyBatchesPerCycle => _maxAlchemyBatchesE != null ? _maxAlchemyBatchesE.Value : 5;
    internal static bool AutoOffering => _autoOfferingE != null && _autoOfferingE.Value;
    internal static int MaxOfferingOperationsPerCycle =>
        _maxOfferingOperationsE != null ? Math.Max(1, _maxOfferingOperationsE.Value) : 5;
    internal static bool AutoConsumeSoulstone => _autoSoulstoneE != null && _autoSoulstoneE.Value;
    internal static bool AutoOpenPortal => _autoOpenPortalE == null || _autoOpenPortalE.Value;
    internal static bool SoulstoneDryRun => _soulstoneDryRunE != null && _soulstoneDryRunE.Value;
    internal static int ActBossRunsPerCycle =>
        _actBossRunsE != null ? Math.Max(1, _actBossRunsE.Value) : 5;
    internal static int ActBossWatchMinutes =>
        _actBossWatchE != null ? Math.Max(1, _actBossWatchE.Value) : 10;
    internal static float AfterSoulstoneEnterDelay => _afterSoulstoneE != null ? _afterSoulstoneE.Value : 5.0f;

    // Item keys the loop must never melt, parsed once per distinct cfg value.
    private static string _protectedRaw;
    private static System.Collections.Generic.HashSet<int> _protectedKeys;

    internal static bool IsAlchemyProtected(int itemKey)
    {
        var raw = _alchemyProtectedE != null ? _alchemyProtectedE.Value : "";
        if (_protectedKeys == null || raw != _protectedRaw)
        {
            _protectedRaw = raw;
            _protectedKeys = new System.Collections.Generic.HashSet<int>();
            foreach (var tok in (raw ?? "").Split(','))
            {
                int key;
                if (int.TryParse(tok.Trim(), out key)) _protectedKeys.Add(key);
            }
        }
        return _protectedKeys.Contains(itemKey);
    }

    // Which soulstone tiers the Soulstone phase may spend, as ESTAGEDIFFICULTY
    // values (0=Normal, 1=Nightmare, 2=Hell, 3=Torment). Empty/invalid => all.
    internal static System.Collections.Generic.List<int> EnabledSoulstoneTiers()
    {
        var list = new System.Collections.Generic.List<int>();
        foreach (var tok in (SoulstoneTiersRaw ?? "").Split(','))
        {
            int tier = Array.FindIndex(TierNames,
                n => string.Equals(n, tok.Trim(), StringComparison.OrdinalIgnoreCase));
            if (tier >= 0 && !list.Contains(tier)) list.Add(tier);
        }
        if (list.Count == 0)
            for (int i = 0; i < TierNames.Length; i++) list.Add(i);
        return list;
    }

    // ESTAGEDIFFICULTY order: one soulstone tier per difficulty.
    static readonly string[] TierNames = { "Normal", "Nightmare", "Hell", "Torment" };

    internal static readonly string SoulstoneTiersDefault = string.Join(",", TierNames);

    internal static string SoulstoneTiersRaw =>
        _soulstoneTiersE != null ? _soulstoneTiersE.Value : SoulstoneTiersDefault;

    private static ConfigEntry<string> _typesE;

    // Which synthesis types each cycle runs in order, as EItemSynthesisType
    // values (0=Gear/Equipment, 1=Accessory, 2=Material). Empty/invalid => all.
    // A single Synthesis phase walks every enabled type before the cycle ends.
    internal static System.Collections.Generic.List<int> EnabledTypes()
    {
        var list = new System.Collections.Generic.List<int>();
        var raw = _typesE != null ? _typesE.Value : "Equipment,Materials,Accessories";
        foreach (var tok in raw.Split(','))
        {
            var t = tok.Trim().ToLowerInvariant();
            if (t == "equipment" || t == "gear") { if (!list.Contains(0)) list.Add(0); }
            else if (t == "accessory" || t == "accessories") { if (!list.Contains(1)) list.Add(1); }
            else if (t == "material" || t == "materials") { if (!list.Contains(2)) list.Add(2); }
        }
        if (list.Count == 0) { list.Add(0); list.Add(2); list.Add(1); }
        return list;
    }

    // The tray exe edits the cfg file; picking the change up live means no game restart.
    // When AutoStart flips in the cfg, the running loop is armed/disarmed to match —
    // so the companion's "Enable auto synthesis" toggle actually stops/starts the loop.
    // F8 still toggles independently without rewriting the cfg.
    static bool? _prevAutoStart;

    internal static void ReloadConfig()
    {
        if (_conf == null) return;
        try
        {
            string before = Summary();
            _conf.Reload();
            string after = Summary();
            if (after != before) Logger.LogInfo("config reloaded: " + after);
        }
        catch (Exception e) { Logger.LogWarning("config reload failed: " + e.Message); }
    }

    // Every setting the loop reads, in one line. Comparing it across a reload is
    // what decides whether anything changed, so a new option only has to be added
    // here to be both logged and noticed.
    static string Summary()
        => $"MaxGrade={MaxGrade}, DesiredLevel={DesiredLevel}, " +
           $"CycleIntervalSeconds={AfterClearDelay}, AutoStart={AutoStart}, " +
           $"PauseOnActivity={PauseOnActivity}, ActivityIdleSeconds={ActivityIdleSeconds}, " +
           $"EnableSynthesis={EnableSynthesis}, AutoOpenChest={AutoOpenChest}, " +
           $"AutoUpgradeRune={AutoUpgradeRune}, " +
           $"MaxRuneUpgradesPerCycle={MaxRuneUpgradesPerCycle}, " +
           $"MaxChestOpensPerCycle={MaxChestOpensPerCycle}, " +
           $"RepeatFullSynth={RepeatFullSynth}, " +
           $"MaxSynthRepeatsPerCycle={MaxSynthRepeatsPerCycle}, " +
           $"AutoAlchemy={AutoAlchemy}, AlchemyDryRun={AlchemyDryRun}, " +
           $"AlchemyLevelThreshold={AlchemyLevelThreshold}, " +
           $"MaxAlchemyGrade={MaxAlchemyGrade}, " +
           $"MaxAlchemyBatchesPerCycle={MaxAlchemyBatchesPerCycle}, " +
           $"AutoOffering={AutoOffering}, " +
           $"MaxOfferingOperationsPerCycle={MaxOfferingOperationsPerCycle}, " +
           $"AutoConsumeSoulstone={AutoConsumeSoulstone}, " +
           $"SoulstoneDryRun={SoulstoneDryRun}, " +
           $"SoulstoneTiers={SoulstoneTiersRaw}, " +
           $"ActBossRunsPerCycle={ActBossRunsPerCycle}, " +
           $"ActBossWatchMinutes={ActBossWatchMinutes}";

    // null = no change since last check; otherwise the new AutoStart value to apply.
    internal static bool? ConsumeAutoStartChange()
    {
        bool cur = AutoStart;
        if (_prevAutoStart == null) { _prevAutoStart = cur; return null; }
        if (_prevAutoStart.Value == cur) return null;
        _prevAutoStart = cur;
        return cur;
    }

    public override void Load()
    {
        Logger = Log;
        _conf = Config;
        _afterFillE = Config.Bind("Timing", "AfterFillSeconds", 1.0f,
            "Delay after clicking auto-fill before starting synthesis");
        _afterSynthE = Config.Bind("Timing", "AfterSynthesisSeconds", 4.0f,
            "Delay after clicking the trigger, so the synthesis can finish");
        _cycleE = Config.Bind("Timing", "CycleIntervalSeconds", 300.0f,
            "Delay after the Cube+Rune cycle finishes before the next cycle starts (default: 5 minutes)");
        _afterRuneE = Config.Bind("Timing", "AfterRuneUpgradeSeconds", 0.5f,
            "Delay between successive rune level-up clicks within the Rune phase");
        _afterChestE = Config.Bind("Timing", "AfterChestOpenSeconds", 1.5f,
            "Delay after clicking a StageBox chest open button (animation settle)");
        _autoStartE = Config.Bind("General", "AutoStart", true,
            "Arm the auto loop as soon as the game starts, and sync the live loop when the " +
            "companion changes this setting. F8 still toggles the live loop without rewriting the cfg.");
        _pauseOnActivityE = Config.Bind("General", "PauseOnActivity", false,
            "While the loop is armed, stop clicking when the mouse moves or clicks in the focused " +
            "game. Starts a fresh cycle after ActivityIdleSeconds of stillness. Off by default.");
        _activityIdleE = Config.Bind("Timing", "ActivityIdleSeconds", 30f,
            "How long the mouse must stay still before a loop paused by PauseOnActivity starts a new cycle.");
        _enableSynthE = Config.Bind("General", "EnableSynthesis", true,
            "When the loop runs, run the Synthesis phase on the Cube (fill -> synth -> clear). " +
            "Turn it off to skip that phase.");
        _autoOpenE = Config.Bind("General", "AutoOpenCube", true,
            "While the loop is armed, click the Cube menu button to open the Cube panel when a " +
            "cycle is due. Turn this off to only run while you have the Cube panel open yourself.");
        _autoChestE = Config.Bind("General", "AutoOpenChest", false,
            "After the Soulstone phase (or at cycle start if it is off), click StageBox chest " +
            "buttons (Normal / Boss / ActBoss) to open accumulated chests. Does not touch the " +
            "game's built-in auto-open toggle.");
        _afterAlchemyE = Config.Bind("Timing", "AfterAlchemyClickSeconds", 0.35f,
            "Delay between successive items while filling the Alchemy cube");
        _autoAlchemyE = Config.Bind("General", "AutoAlchemy", false,
            "Before the Synthesis phase, select the Cube's Alchemy recipe and melt low-level gear " +
            "from the inventory, 9 items per operation. " +
            "Needs AlchemyLevelThreshold > 0.");
        _alchemyDryRunE = Config.Bind("General", "AlchemyDryRun", false,
            "Run the Alchemy phase without clicking anything: every item that would be melted " +
            "is written to the log instead. Use this once to check the threshold before arming it.");
        _alchemyLevelE = Config.Bind("General", "AlchemyLevelThreshold", 0,
            "Gear with an item level strictly below this is eligible for alchemy (e.g. 80 melts " +
            "levels 1-79). 0 disables the phase — nothing is ever eligible.");
        _maxAlchemyGradeE = Config.Bind("Safety", "MaxAlchemyGrade", 2,
            "Highest item grade the Alchemy phase may melt: 0=COMMON 1=UNCOMMON 2=RARE 3=LEGENDARY ... " +
            "Anything above this is left in the inventory regardless of its level.");
        _maxAlchemyBatchesE = Config.Bind("Safety", "MaxAlchemyBatchesPerCycle", 5,
            "Maximum alchemy operations (up to 9 items each) in a single cycle (safety cap).");
        _alchemyProtectedE = Config.Bind("Safety", "AlchemyProtectedItemKeys", "",
            "Comma-separated item keys the Alchemy phase must never melt, e.g. '1201,1305'. " +
            "Locked and reserved items are always skipped without listing them here.");
        _autoOfferingE = Config.Bind("General", "AutoOffering", false,
            "Before the Synthesis phase, select the Cube's Offering recipe and process one " +
            "offering coin at a time while coins remain.");
        _maxOfferingOperationsE = Config.Bind("Safety", "MaxOfferingOperationsPerCycle", 5,
            "Maximum one-coin Offering operations in a single cycle (safety cap).");
        _autoSoulstoneE = Config.Bind("General", "AutoConsumeSoulstone", false,
            "After the other phases, spend surplus soulstones by entering an Act Boss stage " +
            "(a '*-10' stage) that has already been cleared. The stage is picked from the " +
            "soulstone tiers in SoulstoneTiers, and the Portal is switched to that tier's " +
            "difficulty.");
        _soulstoneTiersE = Config.Bind("General", "SoulstoneTiers", SoulstoneTiersDefault,
            "Which soulstone tiers the Soulstone phase may spend, comma-separated: " +
            "Normal, Nightmare, Hell, Torment. Each tier belongs to the difficulty of the " +
            "same name. e.g. 'Hell,Torment' to leave the lower stones alone.");
        _autoOpenPortalE = Config.Bind("General", "AutoOpenPortal", true,
            "During the Soulstone phase, click the Portal menu button to open the stage map.");
        _soulstoneDryRunE = Config.Bind("General", "SoulstoneDryRun", false,
            "Run the Soulstone phase without spending anything: it opens the Portal and finds " +
            "the stage as usual, then logs what it would have entered instead of entering it.");
        _actBossRunsE = Config.Bind("Safety", "ActBossRunsPerCycle", 5,
            "Act Boss runs to farm before the hero is walked back. The game's own auto-retry " +
            "keeps re-entering the boss while soulstones remain, so the phase enters once and " +
            "then counts runs: soulstones spent, or Act Boss chests gained, whichever is ahead.");
        _actBossWatchE = Config.Bind("Safety", "ActBossWatchMinutes", 10,
            "Give up on the chest target after this many minutes and walk the hero back anyway " +
            "(safety cap).");
        _afterSoulstoneE = Config.Bind("Timing", "AfterSoulstoneEnterSeconds", 5.0f,
            "Delay after clicking a stage's enter button, so the stage transition can finish");
        _autoRuneE = Config.Bind("General", "AutoUpgradeRune", false,
            "After the other phases (or at cycle start if those are off), open the Rune " +
            "panel and upgrade the cheapest affordable runes.");
        _autoOpenRuneE = Config.Bind("General", "AutoOpenRune", true,
            "During the Rune phase, click the Rune menu button to open the Rune panel.");
        _maxRuneUpgradesE = Config.Bind("Safety", "MaxRuneUpgradesPerCycle", 20,
            "Maximum rune level-ups to perform in a single cycle (safety cap).");
        _maxChestOpensE = Config.Bind("Safety", "MaxChestOpensPerCycle", 40,
            "Maximum StageBox chest open clicks in a single cycle (safety cap).");
        _maxSynthRepeatsE = Config.Bind("Safety", "MaxSynthRepeatsPerCycle", 10,
            "Maximum extra passes each synthesis type may run when RepeatFullSynth " +
            "keeps finding a full cube (safety cap per type within one cycle).");
        _repeatFullSynthE = Config.Bind("General", "RepeatFullSynth", true,
            "When auto-fill fills every cube slot, run another fill -> synth -> clear pass for the " +
            "same type instead of moving on. Stops on a partial/empty fill, a grade-limit skip, " +
            "or MaxSynthRepeatsPerCycle, then continues with the next enabled type.");
        _typesE = Config.Bind("General", "SynthesisTypes", "Equipment,Materials,Accessories",
            "Which synthesis item types each cycle runs in order, comma-separated: " +
            "Equipment, Materials, Accessories. e.g. 'Equipment,Accessories' to skip materials.");
        _maxGradeE = Config.Bind("Safety", "MaxGrade", 2,
            "Highest item grade the auto loop may synthesize: 0=COMMON 1=UNCOMMON 2=RARE 3=LEGENDARY 4=IMMORTAL ... " +
            "If any cube slot holds an item above this grade, synthesis is skipped and the cube is cleared.");
        _desiredLevelE = Config.Bind("General", "DesiredLevel", 0,
            "Target synthesis recipe: 0 = highest unlocked (default). " +
            "Otherwise the lower bound of an in-game bracket " +
            "(see companion Target level dropdown). If that bracket is locked, " +
            "uses the highest unlocked bracket with lo <= DesiredLevel.");
        if (!ClassInjector.IsTypeRegisteredInIl2Cpp<AutoSynthBehaviour>())
            ClassInjector.RegisterTypeInIl2Cpp<AutoSynthBehaviour>();
        AddComponent<AutoSynthBehaviour>();
        Logger.LogInfo($"TBH Auto Synthesis {Version}: " +
                       "F7 = run one cycle now, F8 = toggle auto loop, F9 = click synth trigger, " +
                       "F10 = dump soulstone+chest+alchemy+offering+synthesis+rune state.");
    }
}

public class AutoSynthBehaviour : MonoBehaviour
{
    private enum LoopMode { Off, Armed, OneShot }
    private enum Phase { Idle, Fill, Synth, Clear, Chest, Rune, Alchemy, Offering, Soulstone }
    private enum CycleStep { Alchemy, Offering, Synthesis, Chest, Rune, Soulstone }
    private enum TypeSelectResult { Pending, Selected, Unavailable }

    // Game UI (UIManager / EventSystem / stage HUD) is not reliable right after
    // BepInEx loads — wait before AutoStart / Show Main / any click automation.
    const float BootDelaySeconds = 30f;

    private LoopMode _mode;
    private Phase _phase;
    private CycleStep[] _steps = Array.Empty<CycleStep>();
    private int _stepIndex;
    private int _cycles;
    private bool _recipeSelected;
    private int _recipeAttempts;
    private bool _recipeListDumped;
    private int _populateStep;
    private string _lastPopulateMethod;
    private bool _typeSelected;
    private int _currentType;
    // The types this Synthesis phase will run, snapshotted at phase start like
    // _steps is per cycle, plus the index of the one being synthesized now. One
    // Synthesis phase walks every enabled type before the cycle advances.
    private System.Collections.Generic.List<int> _types;
    private int _typeIndex;
    private float _nextTick;
    private float _nextOpenAttempt;
    private bool _loggedCubeOpenFailed;
    private int _cubePanelClicks;
    private readonly ChestOpenRunner _chests;
    private readonly RuneUpgradeRunner _runes;
    private readonly AlchemyRunner _alchemy;
    private readonly OfferingRunner _offering;
    private readonly SoulstoneRunner _soulstones;
    private UI_Cube _cube;
    private bool _legacyInputBroken;
    private bool _autoStartApplied;
    private float _bootReadyAt = -1f;
    private float _nextConfigReload;
    private float _nextStatusWrite;
    private bool _paused;
    private float _resumeAt;
    private bool _haveMouse;
    private Vector3 _lastMouse;
    private int _lastSynthCount = -1;
    private int _lastSynthGrade = -1;
    // Cube-phase repeat state: how many extra passes this cycle already ran, and
    // whether the pass we just synthesized had filled every cube slot.
    private int _synthRepeats;
    private bool _lastFillFull;

    public AutoSynthBehaviour(IntPtr ptr) : base(ptr)
    {
        _chests = new ChestOpenRunner();
        _runes = new RuneUpgradeRunner(GameInterop.Click);
        _alchemy = new AlchemyRunner();
        _offering = new OfferingRunner();
        _soulstones = new SoulstoneRunner();
    }

    private bool LoopRunning => _mode != LoopMode.Off;

    private static readonly string StatusPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "tbh-companion", "autosynth-status.json");

    private void WriteStatus()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatusPath));
            var json =
                "{\"version\":\"" + AutoSynthPlugin.Version + "\"" +
                ",\"auto\":" + (LoopRunning ? "true" : "false") +
                ",\"phase\":\"" + _phase + "\"" +
                ",\"cycles\":" + _cycles +
                ",\"lastCount\":" + _lastSynthCount +
                ",\"lastGrade\":" + _lastSynthGrade +
                ",\"lastRuneUpgrades\":" + _runes.LastUpgrades +
                ",\"lastChestOpens\":" + _chests.LastOpens +
                ",\"lastAlchemized\":" + _alchemy.LastAlchemized +
                ",\"lastOfferings\":" + _offering.LastOfferings +
                ",\"lastActBossRuns\":" + _soulstones.LastRuns +
                ",\"autoConsumeSoulstone\":" + (AutoSynthPlugin.AutoConsumeSoulstone ? "true" : "false") +
                ",\"autoAlchemy\":" + (AutoSynthPlugin.AutoAlchemy ? "true" : "false") +
                ",\"autoOffering\":" + (AutoSynthPlugin.AutoOffering ? "true" : "false") +
                ",\"alchemyLevelThreshold\":" + AutoSynthPlugin.AlchemyLevelThreshold +
                ",\"maxGrade\":" + AutoSynthPlugin.MaxGrade +
                ",\"autoUpgradeRune\":" + (AutoSynthPlugin.AutoUpgradeRune ? "true" : "false") +
                ",\"autoOpenChest\":" + (AutoSynthPlugin.AutoOpenChest ? "true" : "false") +
                ",\"enableSynthesis\":" + (AutoSynthPlugin.EnableSynthesis ? "true" : "false") +
                ",\"pauseOnActivity\":" + (AutoSynthPlugin.PauseOnActivity ? "true" : "false") +
                ",\"paused\":" + (_paused ? "true" : "false") +
                ",\"cycleIntervalSeconds\":" + (int)AutoSynthPlugin.AfterClearDelay +
                ",\"updatedUtc\":\"" + DateTime.UtcNow.ToString("o") + "\"}";
            File.WriteAllText(StatusPath, json);
        }
        catch { }
    }

    private void Update()
    {
        if (_bootReadyAt < 0f)
        {
            _bootReadyAt = Time.unscaledTime + BootDelaySeconds;
            AutoSynthPlugin.Logger.LogInfo(
                $"Waiting {BootDelaySeconds:0}s for game UI before starting automation...");
        }
        bool bootReady = Time.unscaledTime >= _bootReadyAt;

        // Config / status / hotkeys stay live during the boot wait so the companion
        // and F7–F10 are not dead for 30s. Only AutoStart + the Tick loop wait.
        if (Time.unscaledTime >= _nextConfigReload)
        {
            _nextConfigReload = Time.unscaledTime + 10f;
            AutoSynthPlugin.ReloadConfig();
            bool? autoStartChange = AutoSynthPlugin.ConsumeAutoStartChange();
            if (autoStartChange.HasValue && bootReady)
                SetAuto(autoStartChange.Value, "from companion AutoStart setting");
        }
        if (Time.unscaledTime >= _nextStatusWrite)
        {
            _nextStatusWrite = Time.unscaledTime + 3f;
            WriteStatus();
        }
        if (KeyDown(KeyCode.F7))
            StartOneShotCycle();
        if (KeyDown(KeyCode.F8))
            SetAuto(_mode == LoopMode.Off, null);
        if (KeyDown(KeyCode.F9))
        {
            var cube = FindCube();
            if (CubeOpen(cube))
                GameInterop.Click(cube.toggleButton_Trigger, "toggleButton_Trigger", true);
            else AutoSynthPlugin.Logger.LogInfo("F9: cube panel not open");
        }
        if (KeyDown(KeyCode.F10)) DumpState();

        if (!bootReady) return;

        if (!_autoStartApplied)
        {
            _autoStartApplied = true;
            if (AutoSynthPlugin.AutoStart)
            {
                _mode = LoopMode.Armed;
                _phase = Phase.Idle;
                BeginCycleWork();
                AutoSynthPlugin.Logger.LogInfo(
                    "Auto loop armed on launch (AutoStart=true). " +
                    (AutoSynthPlugin.AutoAlchemy ? "Alchemy ON. " : "Alchemy OFF. ") +
                    (AutoSynthPlugin.AutoOffering ? "Offering ON. " : "Offering OFF. ") +
                    (AutoSynthPlugin.EnableSynthesis ? "Synthesis ON. " : "Synthesis OFF. ") +
                    (AutoSynthPlugin.AutoOpenChest ? "Chest opens ON. " : "Chest opens OFF. ") +
                    (AutoSynthPlugin.AutoConsumeSoulstone ? "Soulstones ON. " : "Soulstones OFF. ") +
                    (AutoSynthPlugin.AutoUpgradeRune ? "Rune upgrades ON. " : "Rune upgrades OFF. ") +
                    "F7 = one cycle, F8 toggles auto.");
            }
            else
            {
                AutoSynthPlugin.Logger.LogInfo(
                    "Auto loop idle (AutoStart=false). Press F7 to run one cycle, or F8 to arm the loop.");
            }
        }

        WatchActivity();
        if (_paused) return;

        if (!LoopRunning || Time.unscaledTime < _nextTick) return;
        _nextTick = Time.unscaledTime + 1.5f;
        Tick();
    }

    void StartOneShotCycle()
    {
        ClearPause();
        // F7 while already armed: restart without switching to OneShot
        // (avoids desyncing companion Auto Loop / AutoStart cfg).
        if (_mode == LoopMode.Armed)
        {
            BeginCycleWork();
            AutoSynthPlugin.Logger.LogInfo("F7: restarting cycle now (auto stays ON)");
            return;
        }
        _mode = LoopMode.OneShot;
        BeginCycleWork();
        AutoSynthPlugin.Logger.LogInfo(
            $"F7: starting one-shot cycle ({StepList()}), then auto OFF");
    }

    void SetAuto(bool on, string reason)
    {
        ClearPause();
        _mode = on ? LoopMode.Armed : LoopMode.Off;
        _cycles = 0;
        _chests.ResetSession();
        _runes.ResetSession();
        _alchemy.ResetSession();
        _offering.ResetSession();
        _soulstones.ResetSession();
        BeginCycleWork();
        string suffix = string.IsNullOrEmpty(reason) ? "" : " (" + reason + ")";
        AutoSynthPlugin.Logger.LogInfo($"Auto-synthesis: {(on ? "ON" : "OFF")}{suffix}");
    }

    // Overlay on Armed/OneShot — does not rewrite AutoStart / F8. Off unless PauseOnActivity is on.
    void WatchActivity()
    {
        if (!AutoSynthPlugin.PauseOnActivity)
        {
            if (_paused) ResumeFromPause("PauseOnActivity off");
            return;
        }
        if (!LoopRunning)
        {
            ClearPause();
            return;
        }
        if (MouseBusy())
        {
            _resumeAt = Time.unscaledTime + AutoSynthPlugin.ActivityIdleSeconds;
            if (!_paused)
            {
                _paused = true;
                _nextStatusWrite = 0f;
                AutoSynthPlugin.Logger.LogInfo("auto loop paused (mouse activity)");
            }
            return;
        }
        if (_paused && Time.unscaledTime >= _resumeAt)
            ResumeFromPause("idle");
    }

    void ResumeFromPause(string reason)
    {
        ClearPause();
        if (LoopRunning) BeginCycleWork();
        AutoSynthPlugin.Logger.LogInfo("auto loop resumed (" + reason + ")");
    }

    void ClearPause()
    {
        _paused = false;
        _resumeAt = 0f;
        _nextStatusWrite = 0f;
    }

    // ponytail: mouse only; add keyboard if people play without the mouse
    bool MouseBusy()
    {
        if (!Application.isFocused)
        {
            _haveMouse = false;
            return false;
        }
        if (!_legacyInputBroken)
        {
            try
            {
                var pos = Input.mousePosition;
                bool moved = _haveMouse && (pos - _lastMouse).sqrMagnitude > 4f;
                _lastMouse = pos;
                _haveMouse = true;
                return moved
                    || Input.GetMouseButton(0)
                    || Input.GetMouseButton(1)
                    || Input.GetMouseButton(2);
            }
            catch { _legacyInputBroken = true; }
        }
        var mouse = UnityEngine.InputSystem.Mouse.current;
        if (mouse == null) return false;
        var d = mouse.delta.ReadValue();
        return d.sqrMagnitude > 4f
            || mouse.leftButton.isPressed
            || mouse.rightButton.isPressed
            || mouse.middleButton.isPressed;
    }

    void BeginCycleWork()
    {
        _recipeSelected = false;
        _recipeAttempts = 0;
        _typeSelected = false;
        _typeIndex = 0;
        _nextTick = 0f;
        _nextOpenAttempt = 0f;
        _loggedCubeOpenFailed = false;
        _cubePanelClicks = 0;
        _synthRepeats = 0;
        _lastFillFull = false;
        _nextStatusWrite = 0f;
        MainMenuAccess.Reset();
        _steps = EnabledSteps();
        _stepIndex = 0;
        if (_steps.Length == 0)
        {
            AutoSynthPlugin.Logger.LogWarning(
                "cycle skipped: AutoConsumeSoulstone, AutoOpenChest, AutoAlchemy, AutoOffering, EnableSynthesis, " +
                "and AutoUpgradeRune are all off");
            _phase = Phase.Idle;
            if (_mode == LoopMode.OneShot)
            {
                _mode = LoopMode.Off;
                AutoSynthPlugin.Logger.LogInfo("one-shot cycle finished — auto OFF (press F7 again for another)");
                _nextTick = 0f;
            }
            else if (_mode == LoopMode.Armed)
                _nextTick = Time.unscaledTime + AutoSynthPlugin.AfterClearDelay;
            else
                _nextTick = 0f;
            return;
        }
        // Cube increments _cycles on Clear; cycles without a Cube step increment here.
        if (Array.IndexOf(_steps, CycleStep.Synthesis) < 0)
            _cycles++;
        StartStep(_steps[0], true);
    }

    // The phases this cycle will actually run, in order — read from _steps so the
    // log can never drift from EnabledSteps.
    string StepList()
        => _steps.Length == 0
            ? "nothing enabled"
            : string.Join(" -> ", Array.ConvertAll(_steps, s => s.ToString().ToLowerInvariant()));

    static CycleStep[] EnabledSteps()
    {
        var list = new System.Collections.Generic.List<CycleStep>(6);
        // Soulstones run first: the boss runs are what fill the chest stack and the
        // inventory, so the phases that process the haul come after them. Chests are
        // opened next, Offering spends coins before Alchemy melts the junk that fell
        // out of the chests. Both Cube phases return to Synthesis for the next step.
        if (AutoSynthPlugin.AutoConsumeSoulstone) list.Add(CycleStep.Soulstone);
        if (AutoSynthPlugin.AutoOpenChest) list.Add(CycleStep.Chest);
        if (AutoSynthPlugin.AutoOffering) list.Add(CycleStep.Offering);
        if (AutoSynthPlugin.AutoAlchemy) list.Add(CycleStep.Alchemy);
        if (AutoSynthPlugin.EnableSynthesis) list.Add(CycleStep.Synthesis);
        if (AutoSynthPlugin.AutoUpgradeRune) list.Add(CycleStep.Rune);
        return list.ToArray();
    }

    void StartStep(CycleStep step, bool loud)
    {
        switch (step)
        {
            case CycleStep.Alchemy:
                StartAlchemyPhase(loud);
                break;
            case CycleStep.Offering:
                StartOfferingPhase(loud);
                break;
            case CycleStep.Synthesis:
                StartSynthesisPhase();
                break;
            case CycleStep.Chest:
                StartChestPhase(loud);
                break;
            case CycleStep.Rune:
                StartRunePhase(loud);
                break;
            case CycleStep.Soulstone:
                StartSoulstonePhase(loud);
                break;
        }
    }

    void AdvanceAfterStep(bool loud, string detailIfEnd)
    {
        _stepIndex++;
        if (_stepIndex >= _steps.Length)
        {
            EndCycleAndScheduleNext(loud, detailIfEnd);
            return;
        }
        StartStep(_steps[_stepIndex], loud);
    }

    // Snapshot the enabled types once per Synthesis phase — same reason _steps is
    // snapshotted per cycle: a mid-cycle config reload must not shift _typeIndex
    // under the walk.
    void StartSynthesisPhase()
    {
        _types = AutoSynthPlugin.EnabledTypes();
        _typeIndex = 0;
        BeginSynthesisTypePass(0f);
    }

    // Reset the per-type pass state and re-enter Fill for _types[_typeIndex].
    void BeginSynthesisTypePass(float delay)
    {
        _typeSelected = false;
        _recipeSelected = false;
        _recipeAttempts = 0;
        _synthRepeats = 0;
        _lastFillFull = false;
        _phase = Phase.Fill;
        _nextTick = delay <= 0f ? 0f : Time.unscaledTime + delay;
    }

    void StartChestPhase(bool loud)
    {
        _chests.BeginPhase();
        _phase = Phase.Chest;
        _nextTick = Time.unscaledTime + AutoSynthPlugin.AfterFillDelay;
        if (loud) AutoSynthPlugin.Logger.LogInfo($"cycle {_cycles}: starting chest phase");
    }

    void StartAlchemyPhase(bool loud)
    {
        _alchemy.BeginPhase();
        _phase = Phase.Alchemy;
        _nextTick = Time.unscaledTime + AutoSynthPlugin.AfterFillDelay;
        if (loud) AutoSynthPlugin.Logger.LogInfo($"cycle {_cycles}: starting alchemy phase");
    }

    void StartOfferingPhase(bool loud)
    {
        _offering.BeginPhase();
        _phase = Phase.Offering;
        _nextTick = Time.unscaledTime + AutoSynthPlugin.AfterFillDelay;
        if (loud) AutoSynthPlugin.Logger.LogInfo($"cycle {_cycles}: starting offering phase");
    }

    void StartRunePhase(bool loud)
    {
        _runes.BeginPhase();
        _phase = Phase.Rune;
        _nextTick = Time.unscaledTime + AutoSynthPlugin.AfterFillDelay;
        if (loud) AutoSynthPlugin.Logger.LogInfo($"cycle {_cycles}: starting rune phase");
    }

    void StartSoulstonePhase(bool loud)
    {
        _soulstones.BeginPhase();
        _phase = Phase.Soulstone;
        _nextTick = Time.unscaledTime + AutoSynthPlugin.AfterFillDelay;
        if (loud) AutoSynthPlugin.Logger.LogInfo($"cycle {_cycles}: starting soulstone phase");
    }

    void EndCycleAndScheduleNext(bool loud, string detail)
    {
        if (loud || !string.IsNullOrEmpty(detail))
            AutoSynthPlugin.Logger.LogInfo(
                $"cycle {_cycles} done{(string.IsNullOrEmpty(detail) ? "" : " (" + detail + ")")}");
        if (_mode == LoopMode.OneShot)
        {
            _mode = LoopMode.Off;
            _phase = Phase.Idle;
            AutoSynthPlugin.Logger.LogInfo("one-shot cycle finished — auto OFF (press F7 again for another)");
            _nextTick = 0f;
            return;
        }
        // Armed: park on Idle; next tick starts a fresh cycle via BeginCycleWork.
        _phase = Phase.Idle;
        _nextTick = Time.unscaledTime + AutoSynthPlugin.AfterClearDelay;
    }

    private void Tick()
    {
        try
        {
            if (_phase == Phase.Idle)
            {
                BeginCycleWork();
                return;
            }

            if (_phase == Phase.Chest)
            {
                var loud = _cycles < 2 || _cycles % 20 == 0;
                var result = _chests.Tick(loud, out float delay);
                if (result == ChestOpenRunner.TickResult.Done)
                {
                    _nextStatusWrite = 0f;
                    AdvanceAfterStep(loud || _chests.LastOpens > 0,
                        "chest opens this cycle: " + _chests.LastOpens);
                }
                else
                    _nextTick = Time.unscaledTime + delay;
                return;
            }

            if (_phase == Phase.Rune)
            {
                var loud = _cycles < 2 || _cycles % 20 == 0;
                var result = _runes.Tick(loud, out float delay);
                if (result == RuneUpgradeRunner.TickResult.Done)
                {
                    _nextStatusWrite = 0f;
                    AdvanceAfterStep(loud || _runes.LastUpgrades > 0,
                        "rune upgrades this cycle: " + _runes.LastUpgrades);
                }
                else
                    _nextTick = Time.unscaledTime + delay;
                return;
            }

            if (_phase == Phase.Soulstone)
            {
                var loud = _cycles < 2 || _cycles % 20 == 0;
                var result = _soulstones.Tick(loud, out float delay);
                if (result == SoulstoneRunner.TickResult.Done)
                {
                    _nextStatusWrite = 0f;
                    AdvanceAfterStep(loud || _soulstones.LastRuns > 0,
                        "act boss runs this cycle: " + _soulstones.LastRuns);
                }
                else
                    _nextTick = Time.unscaledTime + delay;
                return;
            }

            var cube = FindCube();
            if (!CubeOpen(cube)) { TryOpenCube(); return; }

            var cubeLoud = _cycles < 2 || _cycles % 20 == 0;

            if (_phase == Phase.Alchemy)
            {
                var result = _alchemy.Tick(cube, cubeLoud, out float delay);
                if (result == AlchemyRunner.TickResult.Done)
                {
                    _nextStatusWrite = 0f;
                    AdvanceAfterStep(cubeLoud || _alchemy.LastAlchemized > 0,
                        "alchemy items this cycle: " + _alchemy.LastAlchemized);
                }
                else
                    _nextTick = Time.unscaledTime + delay;
                return;
            }

            if (_phase == Phase.Offering)
            {
                var result = _offering.Tick(cube, cubeLoud, out float delay);
                if (result == OfferingRunner.TickResult.Done)
                {
                    _nextStatusWrite = 0f;
                    AdvanceAfterStep(cubeLoud || _offering.LastOfferings > 0,
                        "offering operations this cycle: " + _offering.LastOfferings);
                }
                else
                    _nextTick = Time.unscaledTime + delay;
                return;
            }

            switch (_phase)
            {
                case Phase.Fill:
                    if (!_typeSelected)
                    {
                        _currentType = _types[_typeIndex];
                        var pick = SelectSynthesisType(cube, _currentType, cubeLoud);
                        if (pick == TypeSelectResult.Unavailable)
                        {
                            // Don't auto-fill under the previous type's UI selection.
                            AdvanceToNextSynthesisType(cubeLoud);
                            break;
                        }
                        // Selected => fill next tick; Pending => retry the combo.
                        _typeSelected = pick == TypeSelectResult.Selected;
                        _nextTick = Time.unscaledTime + AutoSynthPlugin.AfterFillDelay;
                        break;
                    }
                    if (!_recipeSelected)
                    {
                        _recipeAttempts++;
                        _recipeSelected = SelectRecipe(_recipeAttempts <= 3);
                        if (_recipeSelected || _recipeAttempts < 10)
                        {
                            _nextTick = Time.unscaledTime + AutoSynthPlugin.AfterFillDelay;
                            break;
                        }
                        if (_recipeAttempts == 10)
                            AutoSynthPlugin.Logger.LogWarning(
                                "recipe select: UI not available; continuing with the currently selected recipe " +
                                "(will keep checking each cycle - opening the recipe dropdown once in-game also fixes it)");
                    }
                    GameInterop.Click(cube.m_synthesisAutoFillButton, "auto-fill", cubeLoud);
                    _phase = Phase.Synth;
                    _nextTick = Time.unscaledTime + AutoSynthPlugin.AfterFillDelay;
                    break;
                case Phase.Synth:
                    if (!SlotsWithinGradeLimit(cube, out var offender, out var itemCount, out var maxGrade,
                            out var slotCount))
                    {
                        AutoSynthPlugin.Logger.LogWarning(
                            $"grade limit exceeded ({offender}); skipping this synthesis pass");
                        _lastFillFull = false;
                        _phase = Phase.Clear;
                        break;
                    }
                    if (itemCount == 0)
                    {
                        AutoSynthPlugin.Logger.LogInfo(
                            $"synthesis skip: {TypeName(_currentType)} auto-fill put 0 item(s) — next type");
                        _lastFillFull = false;
                        _phase = Phase.Clear;
                        break;
                    }
                    // Full cube (typically 9/9) => repeat this type; partial => next type.
                    _lastFillFull = itemCount >= slotCount;
                    GameInterop.Click(cube.toggleButton_Trigger, "synthesis trigger", false);
                    _lastSynthCount = itemCount;
                    _lastSynthGrade = maxGrade;
                    _nextStatusWrite = 0f;
                    AutoSynthPlugin.Logger.LogInfo(
                        $"synthesis started: {TypeName(_currentType)}, {itemCount}/{slotCount} slot(s) filled, " +
                        $"rarity {GradeName(maxGrade)}");
                    _phase = Phase.Clear;
                    _nextTick = Time.unscaledTime + AutoSynthPlugin.AfterSynthDelay;
                    break;
                case Phase.Clear:
                    ClickTrash(cube.m_trashToggleBtn, cubeLoud);
                    // Full fill (9/9) usually means more of this type remains — repeat it.
                    // Partial fill still clears, then moves on to Materials / Accessories / etc.
                    if (_lastFillFull && AutoSynthPlugin.RepeatFullSynth
                        && _synthRepeats < AutoSynthPlugin.MaxSynthRepeatsPerCycle)
                    {
                        _synthRepeats++;
                        _lastFillFull = false;
                        AutoSynthPlugin.Logger.LogInfo(
                            $"cube was full; repeating {TypeName(_currentType)} synthesis " +
                            $"({_synthRepeats}/{AutoSynthPlugin.MaxSynthRepeatsPerCycle})");
                        // Same type again: keep the type/recipe UI selection as-is.
                        _phase = Phase.Fill;
                        _nextTick = Time.unscaledTime + AutoSynthPlugin.AfterFillDelay;
                        break;
                    }
                    if (_lastFillFull && AutoSynthPlugin.RepeatFullSynth)
                        AutoSynthPlugin.Logger.LogInfo(
                            $"cube still full but MaxSynthRepeatsPerCycle " +
                            $"({AutoSynthPlugin.MaxSynthRepeatsPerCycle}) reached for " +
                            $"{TypeName(_currentType)}; moving to next type");
                    _lastFillFull = false;
                    AdvanceToNextSynthesisType(cubeLoud);
                    break;
            }
        }
        catch (Exception e)
        {
            AutoSynthPlugin.Logger.LogError($"Tick failed: {e}");
        }
    }
private System.Collections.Generic.Dictionary<int, int> _gradeByItemKey;

    private void EnsureGradeMap()
    {
        if (_gradeByItemKey != null) return;
        Il2CppSystem.Collections.Generic.List<ItemInfoData> list = null;
        try { list = GameInterop.ItemInfoList(); }
        catch (Exception e) { AutoSynthPlugin.Logger.LogWarning($"item db lookup failed: {e.Message}"); }
        if (list == null || list.Count == 0) { AutoSynthPlugin.Logger.LogWarning("item db not found / itemInfoData empty"); return; }
        _gradeByItemKey = new System.Collections.Generic.Dictionary<int, int>();
        for (int i = 0; i < list.Count; i++)
        {
            var info = list[i];
            if (info != null) _gradeByItemKey[info.ItemKey] = (int)info.GRADE;
        }
        AutoSynthPlugin.Logger.LogInfo($"item grade map built: {_gradeByItemKey.Count} items");
    }

    private static readonly string[] TypeNames = { "Equipment", "Accessory", "Material" };

    private static string TypeName(int t) => t >= 0 && t < TypeNames.Length ? TypeNames[t] : "?";

    // After finishing (or skipping) the current synthesis type: either start the
    // next enabled type in this same cycle, or leave the Synthesis phase.
    void AdvanceToNextSynthesisType(bool loud)
    {
        _typeIndex++;
        if (_typeIndex < _types.Count)
        {
            AutoSynthPlugin.Logger.LogInfo(
                $"synthesis type done; next: {TypeName(_types[_typeIndex])} " +
                $"({_typeIndex + 1}/{_types.Count})");
            BeginSynthesisTypePass(AutoSynthPlugin.AfterFillDelay);
            return;
        }
        _cycles++;
        AdvanceAfterStep(loud, null);
    }

    // Select the synthesis item type (Equipment/Accessory/Material) on the cube's
    // type combo. Pending = UI not ready yet; Unavailable = type not offered.
    private TypeSelectResult SelectSynthesisType(UI_Cube cube, int type, bool loud)
    {
        try
        {
            var combo = cube.m_synthesisItemTypeButton;
            if (combo == null) return TypeSelectResult.Pending;
            var buttons = combo.m_buttons;
            if (buttons == null || buttons.Count == 0) return TypeSelectResult.Pending;
            for (int i = 0; i < buttons.Count; i++)
            {
                var b = buttons[i];
                if (b == null || (int)b.m_synthesisItemType != type) continue;
                var btn = b.m_button;
                if (btn != null && btn.onClick != null)
                {
                    btn.onClick.Invoke();
                    if (loud) AutoSynthPlugin.Logger.LogInfo($"type select: {TypeName(type)}");
                    return TypeSelectResult.Selected;
                }
                return TypeSelectResult.Pending;
            }
            // type not offered by this cube (e.g. accessories locked)
            if (loud) AutoSynthPlugin.Logger.LogWarning(
                $"type select: {TypeName(type)} not available, skipping");
            return TypeSelectResult.Unavailable;
        }
        catch (Exception e)
        {
            AutoSynthPlugin.Logger.LogError($"type select failed: {e}");
            return TypeSelectResult.Unavailable;
        }
    }

    private bool SelectRecipe(bool loud)
    {
        try
        {
            var combos = UnityEngine.Object.FindObjectsOfType<SubRecipeComboBoxButton>(true);
            SubRecipeComboBoxButton synth = null;
            foreach (var c in combos)
                if (c != null && GameInterop.RecipeTypeOf(c) == ERecipeType.SYNTHESIS) { synth = c; break; }
            if (synth == null)
            {
                // second path: the main recipe button holds a reference to its sub combo
                var mains = UnityEngine.Object.FindObjectsOfType<MainRecipeComboBoxButton>(true);
                foreach (var m in mains)
                {
                    var sc = m != null ? m.m_subRecipeComboBoxButton : null;
                    if (sc != null && GameInterop.RecipeTypeOf(sc) == ERecipeType.SYNTHESIS) { synth = sc; break; }
                }
                if (synth == null)
                {
                    // bfyp is set lazily; with a single sub combo in the scene and the
                    // cube showing its synthesis UI, that one combo must be ours
                    var cube = FindCube();
                    bool synthUiActive = cube != null && cube.m_synthesisToggleButtonParent != null
                        && cube.m_synthesisToggleButtonParent.activeInHierarchy;
                    if (combos.Length == 1 && combos[0] != null && synthUiActive)
                    {
                        synth = combos[0];
                        AutoSynthPlugin.Logger.LogInfo(
                            "recipe select: single sub-recipe combo present while synthesis UI is active, using it");
                    }
                    else
                    {
                        if (loud)
                            AutoSynthPlugin.Logger.LogWarning(
                                $"recipe select: SYNTHESIS sub-recipe combo not found yet " +
                                $"(sub combos: {combos.Length}, main combos: {mains.Length}, synthUi: {synthUiActive}), will retry");
                        return false;
                    }
                }
            }
            var buttons = synth.m_subRecipeSlotButton;
            if (buttons == null || buttons.Count == 0)
            {
                if (loud) AutoSynthPlugin.Logger.LogWarning("recipe select: no sub-recipe buttons yet, will retry");
                return false;
            }
            // Pick by DesiredLevel (0 = highest unlocked). Fall back to list position
            // when a label has no parsable numbers.
            // The slot buttons carry prefab defaults until the dropdown has been
            // opened once (all same label, nothing selected). Open it ourselves and
            // retry; once populated, pick and close.
            bool initialized = false;
            string firstLabel = null;
            for (int i = 0; i < buttons.Count; i++)
            {
                var b = buttons[i];
                if (b == null) continue;
                if (b.m_isSelected) { initialized = true; break; }
                var t = b.m_text != null ? b.m_text.text : null;
                if (firstLabel == null) firstLabel = t;
                else if (t != firstLabel) { initialized = true; break; }
            }
            if (!initialized)
            {
                var dropdown = synth.m_comboBoxObject;
                bool open = dropdown != null && dropdown.activeInHierarchy;
                // Clicking the combo does not always populate the list, so try the
                // combo's reflected no-argument handlers, one per attempt, until
                // the entries appear. Their generated names change every patch.
                _populateStep++;
                string method, error;
                if (GameInterop.TryPopulateSubRecipes(synth, _populateStep - 1, out method, out error))
                {
                    _lastPopulateMethod = method;
                    AutoSynthPlugin.Logger.LogInfo(
                        $"recipe select: called {method}() (dropdown open={open})");
                }
                else if (!string.IsNullOrEmpty(error))
                {
                    AutoSynthPlugin.Logger.LogWarning($"recipe select: {method}() failed: {error}");
                }
                else if (!open)
                {
                    GameInterop.Click(synth, "sub-recipe dropdown (open to populate)", loud);
                }
                else if (loud)
                {
                    AutoSynthPlugin.Logger.LogInfo("recipe select: dropdown open, waiting for entries");
                }
                return false;
            }

            if (!string.IsNullOrEmpty(_lastPopulateMethod))
            {
                GameInterop.RememberSubRecipePopulate(_lastPopulateMethod);
                _lastPopulateMethod = null;
            }

            if (!_recipeListDumped)
            {
                _recipeListDumped = true;
                for (int i = 0; i < buttons.Count; i++)
                {
                    var b = buttons[i];
                    if (b == null) { AutoSynthPlugin.Logger.LogInfo($"recipe list: #{i} null"); continue; }
                    var t = b.m_text != null ? b.m_text.text : "(no text)";
                    AutoSynthPlugin.Logger.LogInfo(
                        $"recipe list: #{i} '{t}' locked={b.m_isLocked} selected={b.m_isSelected}");
                }
            }
            RecipeSlotButton best = null;
            string bestLabel = null;
            int bestLo = -1, bestHi = -1, bestIdx = -1;
            int desired = AutoSynthPlugin.DesiredLevel;
            for (int i = 0; i < buttons.Count; i++)
            {
                var b = buttons[i];
                if (b == null || b.m_isLocked) continue;
                var label = b.m_text != null ? b.m_text.text : $"#{i}";
                int lo = -1, hi = -1;
                var nums = System.Text.RegularExpressions.Regex.Matches(label, @"\d+");
                if (nums.Count >= 1) lo = int.Parse(nums[0].Value);
                if (nums.Count >= 2) hi = int.Parse(nums[1].Value);
                if (BetterRecipe(desired, lo, hi, i, bestLo, bestHi, bestIdx, best == null))
                { best = b; bestLabel = label; bestLo = lo; bestHi = hi; bestIdx = i; }
            }
            if (best == null)
            {
                AutoSynthPlugin.Logger.LogWarning("recipe select: every sub-recipe is locked");
                return true; // nothing selectable; don't keep retrying
            }
            string why = desired <= 0
                ? "highest unlocked"
                : $"desired level {desired}";
            if (best.m_isSelected)
            {
                AutoSynthPlugin.Logger.LogInfo($"recipe select: {why} '{bestLabel}' already selected");
                CloseDropdown(synth);
                return true;
            }
            var btn = best.m_clickButton;
            if (btn != null && btn.onClick != null)
            {
                btn.onClick.Invoke();
                AutoSynthPlugin.Logger.LogInfo($"recipe select: picked {why} '{bestLabel}'");
                CloseDropdown(synth);
                return true;
            }
            if (loud) AutoSynthPlugin.Logger.LogWarning($"recipe select: '{bestLabel}' has no click button, will retry");
            return false;
        }
        catch (Exception e)
        {
            AutoSynthPlugin.Logger.LogError($"recipe select failed: {e}");
            return false;
        }
    }

    // Pick among unlocked brackets.
    // desired <= 0: highest lo (then highest hi) — previous "Max" behavior.
    // desired > 0: exact lo match, else highest unlocked lo <= desired, else lowest lo.
    private static bool BetterRecipe(int desired, int lo, int hi, int idx,
        int bestLo, int bestHi, int bestIdx, bool noBestYet)
    {
        if (noBestYet) return true;
        if (desired <= 0)
        {
            return lo > bestLo
                || (lo == bestLo && hi > bestHi)
                || (lo == bestLo && hi == bestHi && idx > bestIdx);
        }
        bool candExact = lo == desired;
        bool bestExact = bestLo == desired;
        if (candExact != bestExact) return candExact;
        if (candExact)
            return (hi >= 0 && (bestHi < 0 || hi < bestHi))
                || (hi == bestHi && idx > bestIdx);

        bool candBelow = lo >= 0 && lo <= desired;
        bool bestBelow = bestLo >= 0 && bestLo <= desired;
        if (candBelow != bestBelow) return candBelow;
        if (candBelow)
            return lo > bestLo
                || (lo == bestLo && hi > bestHi)
                || (lo == bestLo && hi == bestHi && idx > bestIdx);
        return lo < bestLo
            || (lo == bestLo && hi < bestHi)
            || (lo == bestLo && hi == bestHi && idx < bestIdx);
    }

    private static void CloseDropdown(SubRecipeComboBoxButton combo)
    {
        try
        {
            var dropdown = combo != null ? combo.m_comboBoxObject : null;
            if (dropdown == null || !dropdown.activeInHierarchy) return;
            GameInterop.Click(combo, "sub-recipe dropdown (close)", false);
        }
        catch { }
    }

    private static readonly string[] GradeNames =
        { "COMMON", "UNCOMMON", "RARE", "LEGENDARY", "IMMORTAL", "ARCANA", "BEYOND", "CELESTIAL", "DIVINE", "COSMIC" };

    private static string GradeName(int grade)
        => grade >= 0 && grade < GradeNames.Length ? $"{GradeNames[grade]}({grade})" : $"?({grade})";

    private bool SlotsWithinGradeLimit(UI_Cube cube, out string offender, out int itemCount, out int maxGrade,
        out int slotCount)
    {
        offender = null;
        itemCount = 0;
        maxGrade = -1;
        slotCount = 0;
        var setter = cube.m_cubeSlotSetter;
        var slots = setter != null ? setter.m_cubeInventorySlots : null;
        if (slots == null) return true;
        slotCount = slots.Count;
        EnsureGradeMap();
        for (int i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            var data = slot != null ? slot._cubeData : null;
            if (data == null) continue;
            int key = GetItemKey(data);
            if (key <= 0) continue; // empty slot
            if (_gradeByItemKey == null || !_gradeByItemKey.TryGetValue(key, out var grade))
            {
                offender = $"slot {i}: itemKey {key} has unknown grade";
                return false; // safety: never synthesize what we can't identify
            }
            if (grade > AutoSynthPlugin.MaxGrade)
            {
                offender = $"slot {i}: itemKey {key} grade {grade} > max {AutoSynthPlugin.MaxGrade}";
                return false;
            }
            itemCount++;
            if (grade > maxGrade) maxGrade = grade;
        }
        return true;
    }

    private static int GetItemKey(CubeInData data)
    {
        // Primary path uses the real (un-obfuscated) CubeItemData.ItemKey field via
        // CubeItemKey; on the rare read failure report 0 (empty) rather than guess.
        try { return GameInterop.CubeItemKey(data); }
        catch { return 0; }
    }

    private UI_Cube FindCube()
    {
        if (_cube == null)
            _cube = UnityEngine.Object.FindObjectOfType<UI_Cube>(true);
        return _cube;
    }

    private static bool CubeOpen(UI_Cube cube)
        => cube != null && cube.gameObject.activeInHierarchy;

    // The Cube menu button in the main window's content row (Stash/Stat/Cube/Rune/Portal).
    private ToggleButton CubeMenuButton()
    {
        return GameInterop.FindMenuToggle("Cube");
    }

    // The loop can only act with the Cube panel open, so open it ourselves when a cycle
    // is due. MainMenuAccess owns Show Main → content row → Cube button click.
    // Early accounts (Cube locked) get a few clicks then a long backoff + clear warning.
    private void TryOpenCube()
    {
        if (!AutoSynthPlugin.AutoOpenCube) return;
        if (Time.unscaledTime < _nextOpenAttempt) return;

        var result = MainMenuAccess.TryOpenContentPanel(
            "Cube", "Cube menu button (auto-open)", true, out float delay, out bool spent);
        _nextOpenAttempt = Time.unscaledTime + delay;

        if (result == MainMenuAccess.PanelResult.Failed && !_loggedCubeOpenFailed)
        {
            _loggedCubeOpenFailed = true;
            AutoSynthPlugin.Logger.LogWarning(
                "auto-open: could not open main menu for Cube; " +
                "open the Cube panel yourself and the loop will run");
            return;
        }

        if (result == MainMenuAccess.PanelResult.Clicked || spent)
            _cubePanelClicks++;

        if (_cubePanelClicks >= 3 && !CubeOpen(FindCube()) && !_loggedCubeOpenFailed)
        {
            _loggedCubeOpenFailed = true;
            _nextOpenAttempt = Time.unscaledTime + AutoSynthPlugin.AfterClearDelay;
            AutoSynthPlugin.Logger.LogWarning(
                "auto-open: Cube panel did not open after menu clicks " +
                "(locked on low-level accounts, or UI blocked) — " +
                "backing off until the next cycle interval");
        }
    }

    private static void ClickTrash(CubeSlotResetButton trash, bool loud)
    {
        if (trash == null || !trash.gameObject.activeInHierarchy)
        {
            if (loud) AutoSynthPlugin.Logger.LogInfo("clear cube: button null/inactive, skipped");
            return;
        }
        var btn = trash.m_button;
        if (btn != null && btn.onClick != null)
        {
            btn.onClick.Invoke();
            if (loud) AutoSynthPlugin.Logger.LogInfo("clicked clear cube");
        }
        else if (loud) AutoSynthPlugin.Logger.LogWarning("clear cube: no inner button!");
    }

    private void DumpState()
    {
        try
        {
            var cube = FindCube();
            if (cube == null) AutoSynthPlugin.Logger.LogInfo("dump: UI_Cube not found");
            else
            {
                AutoSynthPlugin.Logger.LogInfo(
                    $"dump: cubeOpen={cube.gameObject.activeInHierarchy} " +
                    $"showMainBtn={Describe(MainMenuAccess.FindShowMainButton())} " +
                    $"cubeMenuBtn={Describe(CubeMenuButton())} " +
                    $"autoFillBtn={Describe(cube.m_synthesisAutoFillButton)} " +
                    $"autoFillToggle={Describe(cube.toggleButton_AutoFill)} " +
                    $"trigger={Describe(cube.toggleButton_Trigger)} " +
                    $"useStorage={Describe(cube.toggleButton_UseStorage)}");
                EnsureGradeMap();
                var setter = cube.m_cubeSlotSetter;
                var slots = setter != null ? setter.m_cubeInventorySlots : null;
                if (slots == null) AutoSynthPlugin.Logger.LogInfo("dump: no slot setter/slots");
                else
                {
                    for (int i = 0; i < slots.Count; i++)
                    {
                        var slot = slots[i];
                        var data = slot != null ? slot._cubeData : null;
                        if (data == null) continue;
                        var key = GetItemKey(data);
                        if (key <= 0) continue;
                        var grade = _gradeByItemKey != null && _gradeByItemKey.TryGetValue(key, out var g) ? g.ToString() : "?";
                        AutoSynthPlugin.Logger.LogInfo($"dump: slot {i} itemKey={key} grade={grade}");
                    }
                }
            }

            _chests.Dump();
            _runes.Dump(Describe);
            _alchemy.Dump();
            _offering.Dump();
            _soulstones.Dump();
        }
        catch (Exception e)
        {
            AutoSynthPlugin.Logger.LogError($"dump failed: {e}");
        }
    }

    private static string Describe(ToggleButton b)
        => b == null ? "null" : $"[active={b.gameObject.activeInHierarchy} on={GameInterop.IsOn(b)}]";

    private bool KeyDown(KeyCode key)
    {
        if (!_legacyInputBroken)
        {
            try { return Input.GetKeyDown(key); }
            catch { _legacyInputBroken = true; }
        }
        var kb = UnityEngine.InputSystem.Keyboard.current;
        if (kb == null) return false;
        return key switch
        {
            KeyCode.F7 => kb.f7Key.wasPressedThisFrame,
            KeyCode.F8 => kb.f8Key.wasPressedThisFrame,
            KeyCode.F9 => kb.f9Key.wasPressedThisFrame,
            KeyCode.F10 => kb.f10Key.wasPressedThisFrame,
            _ => false,
        };
    }
}
