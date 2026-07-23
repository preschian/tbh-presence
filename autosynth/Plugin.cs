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
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TbhAutoSynth;

[BepInPlugin("com.pres.tbh.autosynth", "TBH Auto Synthesis", AutoSynthPlugin.Version)]
public class AutoSynthPlugin : BasePlugin
{
    internal const string Version = "0.28.6";

    internal static ManualLogSource Logger;
    private static ConfigFile _conf;
    private static ConfigEntry<float> _afterFillE, _afterSynthE, _cycleE, _afterRuneE, _afterChestE;
    private static ConfigEntry<int> _maxGradeE, _desiredLevelE, _maxRuneUpgradesE, _maxChestOpensE;
    private static ConfigEntry<bool> _autoStartE, _autoOpenE, _autoRuneE, _autoOpenRuneE, _enableSynthE, _autoChestE;

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
    internal static bool AutoStart => _autoStartE == null || _autoStartE.Value;
    internal static bool AutoOpenCube => _autoOpenE == null || _autoOpenE.Value;
    internal static bool AutoUpgradeRune => _autoRuneE != null && _autoRuneE.Value;
    internal static bool AutoOpenRune => _autoOpenRuneE == null || _autoOpenRuneE.Value;
    internal static bool AutoOpenChest => _autoChestE != null && _autoChestE.Value;
    internal static bool EnableSynthesis => _enableSynthE == null || _enableSynthE.Value;

    private static ConfigEntry<string> _typesE;

    // Which synthesis types the loop rotates through, as EItemSynthesisType
    // values (0=Gear/Equipment, 1=Accessory, 2=Material). Empty/invalid => all.
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

    internal static int TypeForCycle(int cycle)
    {
        var types = EnabledTypes();
        return types[cycle % types.Count];
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
            int mg = MaxGrade, dl = DesiredLevel, mr = MaxRuneUpgradesPerCycle, mc = MaxChestOpensPerCycle;
            float ci = AfterClearDelay;
            bool auto = AutoStart, open = AutoOpenCube, rune = AutoUpgradeRune, synth = EnableSynthesis,
                chest = AutoOpenChest;
            _conf.Reload();
            if (mg != MaxGrade || dl != DesiredLevel || ci != AfterClearDelay || auto != AutoStart
                || open != AutoOpenCube || rune != AutoUpgradeRune || synth != EnableSynthesis
                || chest != AutoOpenChest || mr != MaxRuneUpgradesPerCycle || mc != MaxChestOpensPerCycle)
                Logger.LogInfo($"config reloaded: MaxGrade={MaxGrade}, DesiredLevel={DesiredLevel}, " +
                               $"CycleIntervalSeconds={AfterClearDelay}, AutoStart={AutoStart}, " +
                               $"EnableSynthesis={EnableSynthesis}, AutoOpenChest={AutoOpenChest}, " +
                               $"AutoUpgradeRune={AutoUpgradeRune}, " +
                               $"MaxRuneUpgradesPerCycle={MaxRuneUpgradesPerCycle}, " +
                               $"MaxChestOpensPerCycle={MaxChestOpensPerCycle}");
        }
        catch (Exception e) { Logger.LogWarning("config reload failed: " + e.Message); }
    }

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
        _enableSynthE = Config.Bind("General", "EnableSynthesis", true,
            "When the loop runs, perform Cube synthesis (fill -> synth -> clear). Turn off to skip the Cube phase.");
        _autoOpenE = Config.Bind("General", "AutoOpenCube", true,
            "While the loop is armed, click the Cube menu button to open the Cube panel when a " +
            "cycle is due. Turn this off to only run while you have the Cube panel open yourself.");
        _autoChestE = Config.Bind("General", "AutoOpenChest", false,
            "After the Cube phase (or at cycle start if synthesis is off), click StageBox chest " +
            "buttons (Normal / Boss / ActBoss) to open accumulated chests. Does not touch the " +
            "game's built-in auto-open toggle.");
        _autoRuneE = Config.Bind("General", "AutoUpgradeRune", false,
            "After the Cube and Chest phases (or at cycle start if those are off), open the Rune " +
            "panel and upgrade the cheapest affordable runes.");
        _autoOpenRuneE = Config.Bind("General", "AutoOpenRune", true,
            "During the Rune phase, click the Rune menu button to open the Rune panel.");
        _maxRuneUpgradesE = Config.Bind("Safety", "MaxRuneUpgradesPerCycle", 20,
            "Maximum rune level-ups to perform in a single cycle (safety cap).");
        _maxChestOpensE = Config.Bind("Safety", "MaxChestOpensPerCycle", 40,
            "Maximum StageBox chest open clicks in a single cycle (safety cap).");
        _typesE = Config.Bind("General", "SynthesisTypes", "Equipment,Materials,Accessories",
            "Which synthesis item types the loop rotates through, comma-separated: " +
            "Equipment, Materials, Accessories. e.g. 'Equipment,Materials' to skip accessories.");
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
                       "F7 = run one cycle now, F8 = toggle auto loop, F9 = click synth trigger, F10 = dump cube+chest+rune state.");
    }
}

public class AutoSynthBehaviour : MonoBehaviour
{
    private enum LoopMode { Off, Armed, OneShot }
    private enum Phase { Idle, Fill, Synth, Clear, Chest, Rune }
    private enum CycleStep { Cube, Chest, Rune }

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
    private float _nextTick;
    private float _nextOpenAttempt;
    private int _openFails;
    private bool _menuEnsuredThisCycle;
    private float _nextMenuOpenAttempt;
    private int _menuOpenAttempts;
    private readonly ChestOpenRunner _chests;
    private readonly RuneUpgradeRunner _runes;
    private UI_Cube _cube;
    private bool _legacyInputBroken;
    private bool _autoStartApplied;
    private float _nextConfigReload;
    private float _nextStatusWrite;
    private int _lastSynthCount = -1;
    private int _lastSynthGrade = -1;

    public AutoSynthBehaviour(IntPtr ptr) : base(ptr)
    {
        _chests = new ChestOpenRunner();
        _runes = new RuneUpgradeRunner(Click);
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
                ",\"maxGrade\":" + AutoSynthPlugin.MaxGrade +
                ",\"autoUpgradeRune\":" + (AutoSynthPlugin.AutoUpgradeRune ? "true" : "false") +
                ",\"autoOpenChest\":" + (AutoSynthPlugin.AutoOpenChest ? "true" : "false") +
                ",\"enableSynthesis\":" + (AutoSynthPlugin.EnableSynthesis ? "true" : "false") +
                ",\"cycleIntervalSeconds\":" + (int)AutoSynthPlugin.AfterClearDelay +
                ",\"updatedUtc\":\"" + DateTime.UtcNow.ToString("o") + "\"}";
            File.WriteAllText(StatusPath, json);
        }
        catch { }
    }

    private void Update()
    {
        if (Time.unscaledTime >= _nextConfigReload)
        {
            _nextConfigReload = Time.unscaledTime + 10f;
            AutoSynthPlugin.ReloadConfig();
            bool? autoStartChange = AutoSynthPlugin.ConsumeAutoStartChange();
            if (autoStartChange.HasValue)
                SetAuto(autoStartChange.Value, "from companion AutoStart setting");
        }
        if (Time.unscaledTime >= _nextStatusWrite)
        {
            _nextStatusWrite = Time.unscaledTime + 3f;
            WriteStatus();
        }
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
                    (AutoSynthPlugin.EnableSynthesis ? "Synthesis ON. " : "Synthesis OFF. ") +
                    (AutoSynthPlugin.AutoOpenChest ? "Chest opens ON. " : "Chest opens OFF. ") +
                    (AutoSynthPlugin.AutoUpgradeRune ? "Rune upgrades ON. " : "Rune upgrades OFF. ") +
                    "F7 = one cycle, F8 toggles auto.");
            }
            else
            {
                AutoSynthPlugin.Logger.LogInfo(
                    "Auto loop idle (AutoStart=false). Press F7 to run one cycle, or F8 to arm the loop.");
            }
        }
        if (KeyDown(KeyCode.F7))
            StartOneShotCycle();
        if (KeyDown(KeyCode.F8))
            SetAuto(_mode == LoopMode.Off, null);
        if (KeyDown(KeyCode.F9))
        {
            var cube = FindCube();
            if (CubeOpen(cube)) Click(cube.toggleButton_Trigger, "toggleButton_Trigger", true);
            else AutoSynthPlugin.Logger.LogInfo("F9: cube panel not open");
        }
        if (KeyDown(KeyCode.F10)) DumpState();

        if (!LoopRunning || Time.unscaledTime < _nextTick) return;
        _nextTick = Time.unscaledTime + 1.5f;
        Tick();
    }

    void StartOneShotCycle()
    {
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
        AutoSynthPlugin.Logger.LogInfo("F7: starting one-shot cycle (cube -> chest -> rune), then auto OFF");
    }

    void SetAuto(bool on, string reason)
    {
        _mode = on ? LoopMode.Armed : LoopMode.Off;
        _cycles = 0;
        _chests.ResetSession();
        _runes.ResetSession();
        BeginCycleWork();
        string suffix = string.IsNullOrEmpty(reason) ? "" : " (" + reason + ")";
        AutoSynthPlugin.Logger.LogInfo($"Auto-synthesis: {(on ? "ON" : "OFF")}{suffix}");
    }

    void BeginCycleWork()
    {
        _recipeSelected = false;
        _recipeAttempts = 0;
        _typeSelected = false;
        _nextTick = 0f;
        _nextOpenAttempt = 0f;
        _nextStatusWrite = 0f;
        _menuEnsuredThisCycle = false;
        _nextMenuOpenAttempt = 0f;
        _menuOpenAttempts = 0;
        _steps = EnabledSteps();
        _stepIndex = 0;
        if (_steps.Length == 0)
        {
            AutoSynthPlugin.Logger.LogWarning(
                "cycle skipped: EnableSynthesis, AutoOpenChest, and AutoUpgradeRune are all off");
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
        // Cube increments _cycles on Clear; chest/rune-only cycles increment here.
        if (_steps[0] != CycleStep.Cube)
            _cycles++;
        StartStep(_steps[0], true);
    }

    static CycleStep[] EnabledSteps()
    {
        var list = new System.Collections.Generic.List<CycleStep>(3);
        if (AutoSynthPlugin.EnableSynthesis) list.Add(CycleStep.Cube);
        if (AutoSynthPlugin.AutoOpenChest) list.Add(CycleStep.Chest);
        if (AutoSynthPlugin.AutoUpgradeRune) list.Add(CycleStep.Rune);
        return list.ToArray();
    }

    void StartStep(CycleStep step, bool loud)
    {
        switch (step)
        {
            case CycleStep.Cube:
                _phase = Phase.Fill;
                _nextTick = 0f;
                break;
            case CycleStep.Chest:
                StartChestPhase(loud);
                break;
            case CycleStep.Rune:
                StartRunePhase(loud);
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

    void StartChestPhase(bool loud)
    {
        _chests.BeginPhase();
        _phase = Phase.Chest;
        _nextTick = Time.unscaledTime + AutoSynthPlugin.AfterFillDelay;
        if (loud) AutoSynthPlugin.Logger.LogInfo($"cycle {_cycles}: starting chest phase");
    }

    void StartRunePhase(bool loud)
    {
        _runes.BeginPhase();
        _phase = Phase.Rune;
        _nextTick = Time.unscaledTime + AutoSynthPlugin.AfterFillDelay;
        if (loud) AutoSynthPlugin.Logger.LogInfo($"cycle {_cycles}: starting rune phase");
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

            // Before Cube/Chest/Rune: open the Tab menu once if it was closed.
            if (!_menuEnsuredThisCycle)
            {
                if (EnsureMainMenuOpen())
                    _menuEnsuredThisCycle = true;
                else
                {
                    _nextTick = Time.unscaledTime + 1.0f;
                    return;
                }
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

            var cube = FindCube();
            if (!CubeOpen(cube)) { TryOpenCube(); return; }

            var cubeLoud = _cycles < 2 || _cycles % 20 == 0;
            switch (_phase)
            {
                case Phase.Fill:
                    if (!_typeSelected)
                    {
                        _currentType = AutoSynthPlugin.TypeForCycle(_cycles);
                        if (SelectSynthesisType(cube, _currentType, cubeLoud))
                        {
                            _typeSelected = true;
                            _recipeSelected = false;
                            _recipeAttempts = 0;
                            _nextTick = Time.unscaledTime + AutoSynthPlugin.AfterFillDelay;
                            break;
                        }
                        _typeSelected = true;
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
                    Click(cube.m_synthesisAutoFillButton, "auto-fill", cubeLoud);
                    _phase = Phase.Synth;
                    _nextTick = Time.unscaledTime + AutoSynthPlugin.AfterFillDelay;
                    break;
                case Phase.Synth:
                    if (!SlotsWithinGradeLimit(cube, out var offender, out var itemCount, out var maxGrade))
                    {
                        AutoSynthPlugin.Logger.LogWarning(
                            $"grade limit exceeded ({offender}); skipping this cycle");
                        _phase = Phase.Clear;
                        break;
                    }
                    Click(cube.toggleButton_Trigger, "synthesis trigger", false);
                    if (itemCount > 0)
                    {
                        _lastSynthCount = itemCount;
                        _lastSynthGrade = maxGrade;
                        _nextStatusWrite = 0f;
                        AutoSynthPlugin.Logger.LogInfo(
                            $"synthesis started: {TypeName(_currentType)}, {itemCount} item(s), rarity {GradeName(maxGrade)}");
                    }
                    _phase = Phase.Clear;
                    _nextTick = Time.unscaledTime + AutoSynthPlugin.AfterSynthDelay;
                    break;
                case Phase.Clear:
                    ClickTrash(cube.m_trashToggleBtn, cubeLoud);
                    _cycles++;
                    _typeSelected = false;
                    AdvanceAfterStep(cubeLoud, null);
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

    // Select the synthesis item type (Equipment/Accessory/Material) on the cube's
    // type combo. Returns true once done; false if the combo isn't ready yet.
    private bool SelectSynthesisType(UI_Cube cube, int type, bool loud)
    {
        try
        {
            var combo = cube.m_synthesisItemTypeButton;
            if (combo == null) return false;
            var buttons = combo.m_buttons;
            if (buttons == null || buttons.Count == 0) return false;
            for (int i = 0; i < buttons.Count; i++)
            {
                var b = buttons[i];
                if (b == null || (int)b.m_synthesisItemType != type) continue;
                var btn = b.m_button;
                if (btn != null && btn.onClick != null)
                {
                    btn.onClick.Invoke();
                    if (loud) AutoSynthPlugin.Logger.LogInfo($"type select: {TypeName(type)}");
                    return true;
                }
                return false;
            }
            // type not offered by this cube (e.g. accessories locked) — treat as done
            if (loud) AutoSynthPlugin.Logger.LogWarning($"type select: {TypeName(type)} not available, skipping");
            return true;
        }
        catch (Exception e)
        {
            AutoSynthPlugin.Logger.LogError($"type select failed: {e}");
            return true;
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
                    Click(synth, "sub-recipe dropdown (open to populate)", loud);
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
            Click(combo, "sub-recipe dropdown (close)", false);
        }
        catch { }
    }

    private static readonly string[] GradeNames =
        { "COMMON", "UNCOMMON", "RARE", "LEGENDARY", "IMMORTAL", "ARCANA", "BEYOND", "CELESTIAL", "DIVINE", "COSMIC" };

    private static string GradeName(int grade)
        => grade >= 0 && grade < GradeNames.Length ? $"{GradeNames[grade]}({grade})" : $"?({grade})";

    private bool SlotsWithinGradeLimit(UI_Cube cube, out string offender, out int itemCount, out int maxGrade)
    {
        offender = null;
        itemCount = 0;
        maxGrade = -1;
        var setter = cube.m_cubeSlotSetter;
        var slots = setter != null ? setter.m_cubeInventorySlots : null;
        if (slots == null) return true;
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

    // At cycle start only: if the Tab menu/HUD is closed, press Tab once (10s throttle)
    // and wait until the content row is visible. After a few tries, proceed anyway so
    // Cube/Rune auto-open can surface their own warnings.
    private bool EnsureMainMenuOpen()
    {
        if (GameInterop.IsMainMenuOpen())
        {
            _menuOpenAttempts = 0;
            return true;
        }
        const int maxAttempts = 3;
        if (_menuOpenAttempts >= maxAttempts)
        {
            AutoSynthPlugin.Logger.LogWarning(
                "auto-open menu: Tab did not open the main menu after " + maxAttempts +
                " attempts; continuing — open it yourself with Tab if needed");
            return true;
        }
        if (Time.unscaledTime < _nextMenuOpenAttempt) return false;
        _nextMenuOpenAttempt = Time.unscaledTime + 10f;
        _menuOpenAttempts++;
        if (GameInterop.OpenMainMenu())
            AutoSynthPlugin.Logger.LogInfo(
                $"auto-open menu: open attempt {_menuOpenAttempts}/{maxAttempts}");
        else if (_menuOpenAttempts == 1)
            AutoSynthPlugin.Logger.LogWarning(
                "auto-open menu: failed to open; will retry");
        return false;
    }

    // The loop can only act with the Cube panel open, so open it ourselves when a cycle
    // is due. Throttled: if the player is using another panel we take the tab back at
    // most once every 10s instead of every tick, and the loop is idle between cycles
    // anyway, so this only fires when there is actually work to do.
    // When the whole content row is hidden (Tab menu closed), press Tab first; the Cube
    // button click follows on a later tick once the row is visible again.
    private void TryOpenCube()
    {
        if (!AutoSynthPlugin.AutoOpenCube) return;
        if (Time.unscaledTime < _nextOpenAttempt) return;
        _nextOpenAttempt = Time.unscaledTime + 10f;

        var btn = CubeMenuButton();
        if (btn == null || !btn.gameObject.activeInHierarchy)
        {
            // Menu chrome hidden (Tab closed): show Cube via UIManager first (same path
            // Tab uses internally). Fall back to Tab shortcut / keybd if needed.
            var cubeUi = GameInterop.FindCubeUi();
            if (cubeUi != null && GameInterop.TryShowUiPanel(cubeUi))
            {
                _cube = cubeUi;
                _openFails = 0;
                AutoSynthPlugin.Logger.LogInfo("auto-open: showed UI_Cube via UIManager");
                return;
            }
            GameInterop.OpenMainMenu();
            if (++_openFails == 3)
                AutoSynthPlugin.Logger.LogWarning(
                    "auto-open: Cube menu button not available " +
                    $"(button={(btn == null ? "null" : "inactive")}); " +
                    "open the Cube panel yourself and the loop will run");
            return;
        }
        _openFails = 0;
        Click(btn, "Cube menu button (auto-open)", true);
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

    private static void Click(ButtonBase button, string name, bool loud)
    {
        if (button == null)
        {
            AutoSynthPlugin.Logger.LogWarning($"{name}: null");
            return;
        }
        if (!button.gameObject.activeInHierarchy)
        {
            if (loud) AutoSynthPlugin.Logger.LogInfo($"{name}: inactive, skipped");
            return;
        }
        var ped = new PointerEventData(EventSystem.current);
        button.OnPointerClick(ped);
        // ButtonBase.OnPointerClick only handles hover/click effects; game logic is
        // wired to the wrapped UnityEngine.UI.Button, so fire its onClick too.
        var inner = GameInterop.InnerButton(button);
        if (inner != null && inner.onClick != null)
        {
            inner.onClick.Invoke();
            if (loud) AutoSynthPlugin.Logger.LogInfo($"clicked {name} (+inner onClick)");
        }
        else if (loud) AutoSynthPlugin.Logger.LogInfo($"clicked {name} (no inner button!)");
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
