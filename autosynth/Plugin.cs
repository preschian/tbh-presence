using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using System.Reflection;
using Il2CppInterop.Runtime.Injection;
using TaskbarHero;
using TaskbarHero.Data;
using TaskbarHero.UI;
using TaskbarHero.UI.Rune;
using TS;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TbhAutoSynth;

[BepInPlugin("com.pres.tbh.autosynth", "TBH Auto Synthesis", AutoSynthPlugin.Version)]
public class AutoSynthPlugin : BasePlugin
{
    internal const string Version = "0.26.8";
#if RESILIENT
    // Built with /define:RESILIENT for the "-next" edition: obfuscated members are
    // resolved by signature at runtime instead of by hard-coded name, so a game
    // patch that re-randomizes those names no longer needs a manual remap.
    internal const string Variant = " [next/resilient]";
#else
    internal const string Variant = "";
#endif

    internal static ManualLogSource Logger;
    private static ConfigFile _conf;
    private static ConfigEntry<float> _afterFillE, _afterSynthE, _cycleE, _afterRuneE;
    private static ConfigEntry<int> _maxGradeE, _desiredLevelE, _maxRuneUpgradesE;
    private static ConfigEntry<bool> _autoStartE, _autoOpenE, _autoRuneE, _autoOpenRuneE, _enableSynthE;

    internal static float AfterFillDelay => _afterFillE != null ? _afterFillE.Value : 1.0f;
    internal static float AfterSynthDelay => _afterSynthE != null ? _afterSynthE.Value : 4.0f;
    internal static float AfterClearDelay => _cycleE != null ? _cycleE.Value : 300.0f;
    internal static float AfterRuneUpgradeDelay => _afterRuneE != null ? _afterRuneE.Value : 0.5f;
    internal static int MaxGrade => _maxGradeE != null ? _maxGradeE.Value : 2;
    // 0 = highest unlocked recipe (default). >0 = exact lower-bound match, else
    // the highest unlocked bracket with lo <= DesiredLevel.
    internal static int DesiredLevel => _desiredLevelE != null ? _desiredLevelE.Value : 0;
    internal static int MaxRuneUpgradesPerCycle => _maxRuneUpgradesE != null ? _maxRuneUpgradesE.Value : 20;
    internal static bool AutoStart => _autoStartE == null || _autoStartE.Value;
    internal static bool AutoOpenCube => _autoOpenE == null || _autoOpenE.Value;
    internal static bool AutoUpgradeRune => _autoRuneE == null || _autoRuneE.Value;
    internal static bool AutoOpenRune => _autoOpenRuneE == null || _autoOpenRuneE.Value;
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
            int mg = MaxGrade, dl = DesiredLevel, mr = MaxRuneUpgradesPerCycle;
            float ci = AfterClearDelay;
            bool auto = AutoStart, open = AutoOpenCube, rune = AutoUpgradeRune, synth = EnableSynthesis;
            _conf.Reload();
            if (mg != MaxGrade || dl != DesiredLevel || ci != AfterClearDelay || auto != AutoStart
                || open != AutoOpenCube || rune != AutoUpgradeRune || synth != EnableSynthesis
                || mr != MaxRuneUpgradesPerCycle)
                Logger.LogInfo($"config reloaded: MaxGrade={MaxGrade}, DesiredLevel={DesiredLevel}, " +
                               $"CycleIntervalSeconds={AfterClearDelay}, AutoStart={AutoStart}, " +
                               $"EnableSynthesis={EnableSynthesis}, AutoUpgradeRune={AutoUpgradeRune}, " +
                               $"MaxRuneUpgradesPerCycle={MaxRuneUpgradesPerCycle}");
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
        _autoStartE = Config.Bind("General", "AutoStart", true,
            "Arm the auto loop as soon as the game starts, and sync the live loop when the " +
            "companion changes this setting. F8 still toggles the live loop without rewriting the cfg.");
        _enableSynthE = Config.Bind("General", "EnableSynthesis", true,
            "When the loop runs, perform Cube synthesis (fill -> synth -> clear). Turn off to skip the Cube phase.");
        _autoOpenE = Config.Bind("General", "AutoOpenCube", true,
            "While the loop is armed, click the Cube menu button to open the Cube panel when a " +
            "cycle is due. Turn this off to only run while you have the Cube panel open yourself.");
        _autoRuneE = Config.Bind("General", "AutoUpgradeRune", true,
            "After the Cube phase (or at cycle start if synthesis is off), open the Rune panel and " +
            "upgrade the cheapest affordable runes.");
        _autoOpenRuneE = Config.Bind("General", "AutoOpenRune", true,
            "During the Rune phase, click the Rune menu button to open the Rune panel.");
        _maxRuneUpgradesE = Config.Bind("Safety", "MaxRuneUpgradesPerCycle", 20,
            "Maximum rune level-ups to perform in a single cycle (safety cap).");
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
        Logger.LogInfo($"TBH Auto Synthesis {Version}{Variant}: " +
                       "F7 = run one cycle now, F8 = toggle auto loop, F9 = click synth trigger, F10 = dump cube+rune state.");
    }
}

public class AutoSynthBehaviour : MonoBehaviour
{
    public AutoSynthBehaviour(IntPtr ptr) : base(ptr) { }

    private enum Phase { Fill, Synth, Clear, Rune }

    private bool _auto;
    private bool _oneShot; // F7: run a single cycle then disarm
    private Phase _phase;
    private int _cycles;
    private bool _recipeSelected;
    private int _recipeAttempts;
    private bool _recipeListDumped;
    private int _populateStep;
    private bool _typeSelected;
    private int _currentType;
    private float _nextTick;
    private float _nextOpenAttempt;
    private float _nextRuneOpenAttempt;
    private int _openFails;
    private int _runeOpenFails;
    private int _runeUpgradesThisCycle;
    private int _lastRuneUpgrades;
    private int _pendingRuneKey = -1;
    private int _pendingRuneLevel = -1;
    private int _runeFailStreak;
    private UI_Cube _cube;
    private UI_Main _main;
    private UI_Rune _runeUi;
    private RuneTooltip _runeTooltip;
    private bool _legacyInputBroken;
    private bool _autoStartApplied;
    private float _nextConfigReload;
    private float _nextStatusWrite;
    private int _lastSynthCount = -1;
    private int _lastSynthGrade = -1;

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
                ",\"auto\":" + (_auto ? "true" : "false") +
                ",\"phase\":\"" + _phase + "\"" +
                ",\"cycles\":" + _cycles +
                ",\"lastCount\":" + _lastSynthCount +
                ",\"lastGrade\":" + _lastSynthGrade +
                ",\"lastRuneUpgrades\":" + _lastRuneUpgrades +
                ",\"maxGrade\":" + AutoSynthPlugin.MaxGrade +
                ",\"autoUpgradeRune\":" + (AutoSynthPlugin.AutoUpgradeRune ? "true" : "false") +
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
                _auto = true;
                AutoSynthPlugin.Logger.LogInfo(
                    "Auto loop armed on launch (AutoStart=true). " +
                    (AutoSynthPlugin.EnableSynthesis ? "Synthesis ON. " : "Synthesis OFF. ") +
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
            SetAuto(!_auto, null);
        if (KeyDown(KeyCode.F9))
        {
            var cube = FindCube();
            if (CubeOpen(cube)) Click(cube.toggleButton_Trigger, "toggleButton_Trigger", true);
            else AutoSynthPlugin.Logger.LogInfo("F9: cube panel not open");
        }
        if (KeyDown(KeyCode.F10)) DumpState();

        if (!_auto || Time.unscaledTime < _nextTick) return;
        _nextTick = Time.unscaledTime + 1.5f;
        Tick();
    }

    void StartOneShotCycle()
    {
        _oneShot = true;
        _auto = true;
        _phase = Phase.Fill;
        _recipeSelected = false;
        _recipeAttempts = 0;
        _typeSelected = false;
        _runeUpgradesThisCycle = 0;
        _pendingRuneKey = -1;
        _pendingRuneLevel = -1;
        _pendingRuneGold = -1;
        _runeFailStreak = 0;
        _nextTick = 0f;
        _nextOpenAttempt = 0f;
        _nextRuneOpenAttempt = 0f;
        _nextStatusWrite = 0f;
        AutoSynthPlugin.Logger.LogInfo("F7: starting one-shot cycle (cube -> rune), then auto OFF");
    }

    void SetAuto(bool on, string reason)
    {
        _auto = on;
        _oneShot = false;
        _phase = Phase.Fill;
        _cycles = 0;
        _recipeSelected = false;
        _recipeAttempts = 0;
        _typeSelected = false;
        _runeUpgradesThisCycle = 0;
        _pendingRuneKey = -1;
        _pendingRuneLevel = -1;
        _pendingRuneGold = -1;
        _runeFailStreak = 0;
        _nextTick = 0f;
        _nextOpenAttempt = 0f;
        _nextRuneOpenAttempt = 0f;
        _nextStatusWrite = 0f;
        string suffix = string.IsNullOrEmpty(reason) ? "" : " (" + reason + ")";
        AutoSynthPlugin.Logger.LogInfo($"Auto-synthesis: {(_auto ? "ON" : "OFF")}{suffix}");
    }

    // After a finished cycle: either wait for the next auto tick, or disarm after F7.
    void EndCycleAndScheduleNext(bool loud, string detail)
    {
        if (loud || !string.IsNullOrEmpty(detail))
            AutoSynthPlugin.Logger.LogInfo(
                $"cycle {_cycles} done{(string.IsNullOrEmpty(detail) ? "" : " (" + detail + ")")}");
        _phase = Phase.Fill;
        if (_oneShot)
        {
            _oneShot = false;
            _auto = false;
            AutoSynthPlugin.Logger.LogInfo("one-shot cycle finished — auto OFF (press F7 again for another)");
            _nextTick = 0f;
            return;
        }
        _nextTick = Time.unscaledTime + AutoSynthPlugin.AfterClearDelay;
    }

    private void Tick()
    {
        try
        {
            // Synthesis disabled: skip Cube and go straight to runes (or end the cycle).
            if (_phase == Phase.Fill && !AutoSynthPlugin.EnableSynthesis)
            {
                _cycles++;
                _runeUpgradesThisCycle = 0;
                _pendingRuneKey = -1;
                _pendingRuneLevel = -1;
                _pendingRuneGold = -1;
                _runeFailStreak = 0;
                if (AutoSynthPlugin.AutoUpgradeRune)
                {
                    _phase = Phase.Rune;
                    _nextTick = Time.unscaledTime + 0.25f;
                    AutoSynthPlugin.Logger.LogInfo($"cycle {_cycles}: synthesis off, starting rune phase");
                }
                else
                {
                    AutoSynthPlugin.Logger.LogWarning(
                        "cycle skipped: EnableSynthesis and AutoUpgradeRune are both off");
                    EndCycleAndScheduleNext(true, "nothing enabled");
                }
                return;
            }

            if (_phase == Phase.Rune)
            {
                TickRune(_cycles < 2 || _cycles % 20 == 0);
                return;
            }

            var cube = FindCube();
            if (!CubeOpen(cube)) { TryOpenCube(); return; }

            var loud = _cycles < 2 || _cycles % 20 == 0;
            switch (_phase)
            {
                case Phase.Fill:
                    if (!_typeSelected)
                    {
                        // Rotate through the enabled synthesis types across cycles
                        // (Equipment/Materials/Accessories), select this cycle's type,
                        // then re-pick the recipe since the bracket list can differ.
                        _currentType = AutoSynthPlugin.TypeForCycle(_cycles);
                        if (SelectSynthesisType(cube, _currentType, loud))
                        {
                            _typeSelected = true;
                            _recipeSelected = false;
                            _recipeAttempts = 0;
                            _nextTick = Time.unscaledTime + AutoSynthPlugin.AfterFillDelay;
                            break;
                        }
                        // couldn't select (type combo not ready) — proceed anyway
                        _typeSelected = true;
                    }
                    if (!_recipeSelected)
                    {
                        // The sub-recipe UI is built lazily (often only after the recipe
                        // dropdown has been opened once), so retry: quickly for the first
                        // few ticks, then once per cycle while running with the recipe
                        // that is currently selected.
                        _recipeAttempts++;
                        _recipeSelected = SelectRecipe(_recipeAttempts <= 3);
                        if (_recipeSelected || _recipeAttempts < 10)
                        {
                            // give the UI a tick to apply the recipe before filling
                            _nextTick = Time.unscaledTime + AutoSynthPlugin.AfterFillDelay;
                            break;
                        }
                        if (_recipeAttempts == 10)
                            AutoSynthPlugin.Logger.LogWarning(
                                "recipe select: UI not available; continuing with the currently selected recipe " +
                                "(will keep checking each cycle - opening the recipe dropdown once in-game also fixes it)");
                    }
                    Click(cube.m_synthesisAutoFillButton, "auto-fill", loud);
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
                    ClickTrash(cube.m_trashToggleBtn, loud);
                    _cycles++;
                    _typeSelected = false;
                    _runeUpgradesThisCycle = 0;
                    _pendingRuneKey = -1;
                    _pendingRuneLevel = -1;
                    _pendingRuneGold = -1;
                    _runeFailStreak = 0;
                    if (AutoSynthPlugin.AutoUpgradeRune)
                    {
                        _phase = Phase.Rune;
                        _nextTick = Time.unscaledTime + AutoSynthPlugin.AfterFillDelay;
                        if (loud) AutoSynthPlugin.Logger.LogInfo($"cycle {_cycles}: cube clear done, starting rune phase");
                    }
                    else
                    {
                        EndCycleAndScheduleNext(loud, null);
                    }
                    break;
            }
        }
        catch (Exception e)
        {
            AutoSynthPlugin.Logger.LogError($"Tick failed: {e}");
        }
    }

    private void FinishRunePhase(bool loud)
    {
        CloseRunePanel(loud || _runeUpgradesThisCycle > 0);
        _lastRuneUpgrades = _runeUpgradesThisCycle;
        _nextStatusWrite = 0f;
        EndCycleAndScheduleNext(loud || _runeUpgradesThisCycle > 0,
            "rune upgrades this cycle: " + _runeUpgradesThisCycle);
    }

    private void TickRune(bool loud)
    {
        if (_runeUpgradesThisCycle >= AutoSynthPlugin.MaxRuneUpgradesPerCycle)
        {
            AutoSynthPlugin.Logger.LogInfo(
                $"rune phase: hit MaxRuneUpgradesPerCycle={AutoSynthPlugin.MaxRuneUpgradesPerCycle}");
            FinishRunePhase(loud);
            return;
        }

        var runeUi = FindRuneUi();
        if (!RuneOpen(runeUi))
        {
            TryOpenRune();
            return;
        }

        var page = runeUi.m_runePage;
        if (page == null)
        {
            AutoSynthPlugin.Logger.LogWarning("rune phase: UI_Rune.m_runePage is null, skipping");
            FinishRunePhase(loud);
            return;
        }

        // Verify the previous upgrade on the following tick (save/UI may lag one frame).
        if (_pendingRuneKey >= 0)
        {
            var pending = FindRuneNode(page, _pendingRuneKey);
            int after = RuneLevel(pending);
            long goldNow = ReadGold(page);
            bool leveled = after > _pendingRuneLevel;
            if (leveled)
            {
                _runeUpgradesThisCycle++;
                _runeFailStreak = 0;
                AutoSynthPlugin.Logger.LogInfo(
                    $"rune upgrade ok: key={_pendingRuneKey} lv {_pendingRuneLevel}->{after} " +
                    $"gold {_pendingRuneGold}->{goldNow}");
                _pendingRuneKey = -1;
                _pendingRuneLevel = -1;
                _pendingRuneGold = -1;
            }
            else
            {
                // Level field can lag a tick; if gold dropped, still count it as a successful click
                // and keep bursting — the user wants repeated upgrades in one cycle.
                bool spent = _pendingRuneGold >= 0 && goldNow >= 0 && goldNow < _pendingRuneGold - 1000;
                if (spent)
                {
                    _runeUpgradesThisCycle++;
                    _runeFailStreak = 0;
                    AutoSynthPlugin.Logger.LogInfo(
                        $"rune upgrade ok (gold spent): key={_pendingRuneKey} lv={after} " +
                        $"gold {_pendingRuneGold}->{goldNow}");
                }
                else
                {
                    _runeFailStreak++;
                    AutoSynthPlugin.Logger.LogWarning(
                        $"rune upgrade no effect: key={_pendingRuneKey} lv={_pendingRuneLevel}->{after} " +
                        $"gold={goldNow} failStreak={_runeFailStreak}");
                    if (_runeFailStreak >= 5)
                    {
                        _pendingRuneKey = -1;
                        _pendingRuneLevel = -1;
                        _pendingRuneGold = -1;
                        FinishRunePhase(loud);
                        return;
                    }
                }
                _pendingRuneKey = -1;
                _pendingRuneLevel = -1;
                _pendingRuneGold = -1;
            }
        }

        long gold = ReadGold(page);
        if (_runeUpgradesThisCycle == 0 && _runeFailStreak == 0)
            LogRuneEconomy(page, gold);

        if (!TryFindCheapestUpgradeable(page, gold, out var best, out var cost, out var key, out var level))
        {
            AutoSynthPlugin.Logger.LogInfo(
                $"rune phase: no affordable upgrade (gold={gold}, upgrades so far={_runeUpgradesThisCycle})");
            FinishRunePhase(loud);
            return;
        }

        AutoSynthPlugin.Logger.LogInfo(
            $"rune phase: cheapest key={key} lv={level} cost={cost} gold={gold} name='{_lastCheapestName}'");

        if (!TryUpgradeRune(best, key, level, cost, _runeFailStreak, true))
        {
            AutoSynthPlugin.Logger.LogWarning($"rune phase: upgrade invoke failed for key={key}");
            _runeFailStreak++;
            if (_runeFailStreak >= 5) { FinishRunePhase(loud); return; }
            _nextTick = Time.unscaledTime + AutoSynthPlugin.AfterRuneUpgradeDelay;
            return;
        }

        _pendingRuneKey = key;
        _pendingRuneLevel = level;
        _pendingRuneGold = gold;
        _nextTick = Time.unscaledTime + Math.Max(0.75f, AutoSynthPlugin.AfterRuneUpgradeDelay);
    }

    private string _lastCheapestName = "";
    private long _pendingRuneGold = -1;

    private static RuneNode FindRuneNode(RunePage page, int key)
    {
        var list = page != null ? page.m_listRuneNode : null;
        if (list == null) return null;
        for (int i = 0; i < list.Count; i++)
        {
            var n = list[i];
            if (n != null && n.m_runeKey == key) return n;
        }
        return null;
    }

    private System.Collections.Generic.Dictionary<int, int> _gradeByItemKey;

    private void EnsureGradeMap()
    {
        if (_gradeByItemKey != null) return;
        Il2CppSystem.Collections.Generic.List<ItemInfoData> list = null;
        try { list = ItemInfoList(); }
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
                if (c != null && RecipeTypeOf(c) == ERecipeType.SYNTHESIS) { synth = c; break; }
            if (synth == null)
            {
                // second path: the main recipe button holds a reference to its sub combo
                var mains = UnityEngine.Object.FindObjectsOfType<MainRecipeComboBoxButton>(true);
                foreach (var m in mains)
                {
                    var sc = m != null ? m.m_subRecipeComboBoxButton : null;
                    if (sc != null && RecipeTypeOf(sc) == ERecipeType.SYNTHESIS) { synth = sc; break; }
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
                // Clicking the combo does not populate the list, so try the combo's
                // own methods, one per attempt, until the entries appear.
                _populateStep++;
                switch (_populateStep)
                {
                    case 1:
                        try { synth.kxi(); AutoSynthPlugin.Logger.LogInfo($"recipe select: called kxi() (dropdown open={open})"); }
                        catch (Exception e) { AutoSynthPlugin.Logger.LogWarning($"kxi() failed: {e.Message}"); }
                        break;
                    case 2:
                        try { synth.lag(); AutoSynthPlugin.Logger.LogInfo($"recipe select: called lag() (dropdown open={open})"); }
                        catch (Exception e) { AutoSynthPlugin.Logger.LogWarning($"lag() failed: {e.Message}"); }
                        break;
                    case 3:
                        try { synth.kxk(); AutoSynthPlugin.Logger.LogInfo($"recipe select: called kxk() (dropdown open={open})"); }
                        catch (Exception e) { AutoSynthPlugin.Logger.LogWarning($"kxk() failed: {e.Message}"); }
                        break;
                    default:
                        if (!open) Click(synth, "sub-recipe dropdown (open to populate)", loud);
                        else if (loud) AutoSynthPlugin.Logger.LogInfo("recipe select: dropdown open, waiting for entries");
                        break;
                }
                return false;
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
            try { combo.kxk(); } catch { }
            if (dropdown.activeInHierarchy)
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
        try { return CubeItemKey(data); }
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
        if (_main == null) _main = UnityEngine.Object.FindObjectOfType<UI_Main>(true);
        var entry = _main != null ? _main.button_Cube : null;
        return entry != null ? entry.toggleButton : null;
    }

    // The loop can only act with the Cube panel open, so open it ourselves when a cycle
    // is due. Throttled: if the player is using another panel we take the tab back at
    // most once every 10s instead of every tick, and the loop is idle between cycles
    // anyway, so this only fires when there is actually work to do.
    private void TryOpenCube()
    {
        if (!AutoSynthPlugin.AutoOpenCube) return;
        if (Time.unscaledTime < _nextOpenAttempt) return;
        _nextOpenAttempt = Time.unscaledTime + 10f;

        var btn = CubeMenuButton();
        if (btn == null || !btn.gameObject.activeInHierarchy)
        {
            // The main window is still being built for the first seconds after launch,
            // so a few misses are normal; only speak up once it stays unavailable.
            if (++_openFails == 3)
                AutoSynthPlugin.Logger.LogWarning(
                    "auto-open: Cube menu button not available " +
                    $"(mainUi={(_main == null ? "null" : "found")}, button={(btn == null ? "null" : "inactive")}); " +
                    "open the Cube panel yourself and the loop will run");
            _main = null; // re-find next time; the main UI may not be built yet
            return;
        }
        _openFails = 0;
        Click(btn, "Cube menu button (auto-open)", true);
    }

    private UI_Rune FindRuneUi()
    {
        if (_runeUi == null)
            _runeUi = UnityEngine.Object.FindObjectOfType<UI_Rune>(true);
        return _runeUi;
    }

    private static bool RuneOpen(UI_Rune rune)
        => rune != null && rune.gameObject.activeInHierarchy;

    private ToggleButton RuneMenuButton()
    {
        if (_main == null) _main = UnityEngine.Object.FindObjectOfType<UI_Main>(true);
        var entry = _main != null ? _main.button_Rune : null;
        return entry != null ? entry.toggleButton : null;
    }

    private void TryOpenRune()
    {
        if (!AutoSynthPlugin.AutoOpenRune) return;
        if (Time.unscaledTime < _nextRuneOpenAttempt) return;
        _nextRuneOpenAttempt = Time.unscaledTime + 10f;

        var btn = RuneMenuButton();
        if (btn == null || !btn.gameObject.activeInHierarchy)
        {
            if (++_runeOpenFails == 3)
                AutoSynthPlugin.Logger.LogWarning(
                    "auto-open: Rune menu button not available " +
                    $"(mainUi={(_main == null ? "null" : "found")}, button={(btn == null ? "null" : "inactive")}); " +
                    "open the Rune panel yourself and the loop will continue");
            _main = null;
            return;
        }
        _runeOpenFails = 0;
        Click(btn, "Rune menu button (auto-open)", true);
    }

    private void CloseRunePanel(bool loud)
    {
        try
        {
            var runeUi = FindRuneUi();
            if (!RuneOpen(runeUi)) return;
            if (ClickUnityButton(runeUi.Button_Close, "Rune close", loud)) return;
            if (ClickUnityButton(runeUi.Button_Close_Down, "Rune close (down)", loud)) return;
            try { runeUi.hls(); if (loud) AutoSynthPlugin.Logger.LogInfo("rune close: called hls()"); } catch { }
        }
        catch (Exception e)
        {
            AutoSynthPlugin.Logger.LogWarning("rune close failed: " + e.Message);
        }
    }

    // Prefer live gold from the save manager (ban.mrv); fall back to the Rune page TMP label.
    private static long ReadGold(RunePage page)
    {
        long fromSave = ReadPlayerGold();
        if (fromSave >= 0) return fromSave;
        try
        {
            var tmp = page != null ? page.m_goldText : null;
            if (tmp == null) return -1;
            return ParseGoldText(tmp.text);
        }
        catch { return -1; }
    }

    private static long ReadPlayerGold()
    {
        try
        {
            ban mgr = null;
            try { mgr = nq<ban>.bsen; } catch { }
            if (mgr == null) mgr = UnityEngine.Object.FindObjectOfType<ban>(true);
            if (mgr == null) return -1;
            try
            {
                long g = mgr.mrv();
                if (g >= 0) return g;
            }
            catch { }
            // Fallback: currency save list (gold is typically Key == 1).
            try
            {
                var psd = mgr.btct ?? mgr.bglm;
                var list = psd != null ? psd.currenySaveDatas : null;
                if (list != null)
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        var c = list[i];
                        if (c != null && c.Key == 1) return c.Quantity;
                    }
                }
            }
            catch { }
        }
        catch (Exception e)
        {
            AutoSynthPlugin.Logger.LogWarning("ReadPlayerGold failed: " + e.Message);
        }
        return -1;
    }

    // Confirmed working purchase path: RuneNode.mba() (one call only).
    private bool TryUpgradeRune(RuneNode node, int key, int level, int cost, int attempt, bool loud)
    {
        if (node == null) return false;
        try
        {
            var tip = FindRuneTooltip();
            if (tip != null)
            {
                tip.mbt(node);
                tip.Show();
            }
        }
        catch { }

        try
        {
            node.mba();
            if (loud)
                AutoSynthPlugin.Logger.LogInfo(
                    $"rune upgrade: node.mba() key={key} lv={level} cost={cost} name='{_lastCheapestName}'");
            return true;
        }
        catch (Exception e)
        {
            AutoSynthPlugin.Logger.LogWarning($"rune upgrade mba() failed: {e.Message}");
            return false;
        }
    }

    private RuneTooltip FindRuneTooltip()
    {
        if (_runeTooltip == null)
            _runeTooltip = UnityEngine.Object.FindObjectOfType<RuneTooltip>(true);
        return _runeTooltip;
    }

    // RuneLevelInfoData.bgpx on the NEXT level row is the gold cost.
    private static int RuneUpgradeCost(RuneNode node)
    {
        if (node == null) return -1;
        int key = node.m_runeKey;
        int level = RuneLevel(node);
        try
        {
            if (level >= 0)
            {
                var next = LookupRuneLevelInfo(key, level + 1);
                if (next != null && next.bgpx > 0) return next.bgpx;
            }
        }
        catch { }
        try
        {
            var info = node.btby ?? node.bgir;
            if (info != null && info.bgpx > 0) return info.bgpx;
        }
        catch { }
        return -1;
    }

    private static RuneLevelInfoData LookupRuneLevelInfo(int runeKey, int level)
    {
        try
        {
            bal db = null;
            try { db = nq<bal>.bsen; } catch { }
            if (db == null) db = UnityEngine.Object.FindObjectOfType<bal>(true);
            if (db == null) return null;
            // Several remapped names for GetRuneLevelInfo(key, level) — try in order.
            try { var r = db.mfg(runeKey, level); if (r != null) return r; } catch { }
            try { var r = db.ocl(runeKey, level); if (r != null) return r; } catch { }
            try { var r = db.nfm(runeKey, level); if (r != null) return r; } catch { }
            try { var r = db.sj(runeKey, level); if (r != null) return r; } catch { }
        }
        catch { }
        return null;
    }

    private static int RuneLevel(RuneNode node)
    {
        if (node == null) return -1;
        try
        {
            var save = node.bgis;
            if (save != null) return save.Level;
        }
        catch { }
        return -1;
    }

    // True when a next-level row exists (not maxed) and optional UI flags agree.
    private static bool RuneCanUpgrade(RuneNode node)
    {
        if (node == null || !node.isActiveAndEnabled) return false;
        int level = RuneLevel(node);
        if (level < 0) return false;
        var next = LookupRuneLevelInfo(node.m_runeKey, level + 1);
        if (next == null || next.bgpx <= 0) return false; // no next level / maxed
        return true;
    }

    private bool TryFindCheapestUpgradeable(RunePage page, long gold,
        out RuneNode best, out int bestCost, out int bestKey, out int bestLevel)
    {
        best = null;
        bestCost = int.MaxValue;
        bestKey = -1;
        bestLevel = -1;
        _lastCheapestName = "";
        var list = page != null ? page.m_listRuneNode : null;
        if (list == null) return false;

        var tip = FindRuneTooltip();
        for (int i = 0; i < list.Count; i++)
        {
            var node = list[i];
            if (!RuneCanUpgrade(node)) continue;
            int cost = RuneUpgradeCost(node);
            if (cost <= 0) continue;
            if (gold >= 0 && cost > gold) continue;

            string name = "";
            bool uiBlocks = false;
            try
            {
                if (tip != null)
                {
                    tip.mbt(node);
                    // Don't Show() for every node (expensive); read after bind.
                    if (tip.m_maxLevelPanel != null && tip.m_maxLevelPanel.activeSelf)
                        uiBlocks = true;
                    if (tip.m_runeNameText != null) name = tip.m_runeNameText.text ?? "";
                    // Prefer the tooltip's displayed cost when parsable.
                    if (tip.m_runeCostValueText != null)
                    {
                        long parsed = ParseGoldText(tip.m_runeCostValueText.text);
                        if (parsed > 0) cost = (int)Math.Min(parsed, int.MaxValue);
                    }
                }
            }
            catch { }
            if (uiBlocks) continue;
            if (gold >= 0 && cost > gold) continue;

            if (cost > bestCost) continue;
            if (cost == bestCost && best != null && node.m_runeKey >= bestKey) continue;
            best = node;
            bestCost = cost;
            bestKey = node.m_runeKey;
            bestLevel = RuneLevel(node);
            _lastCheapestName = name;
        }
        return best != null;
    }

    private void LogRuneEconomy(RunePage page, long gold)
    {
        try
        {
            string goldText = "?";
            try { goldText = page.m_goldText != null ? page.m_goldText.text : "(null)"; } catch { }
            AutoSynthPlugin.Logger.LogInfo($"rune economy: gold={gold} goldText='{goldText}'");

            var list = page.m_listRuneNode;
            if (list == null) return;
            int listed = 0, upgradeable = 0;
            RuneNode cheapest = null;
            int cheapestCost = int.MaxValue;
            string cheapestName = "";
            var tip = FindRuneTooltip();
            for (int i = 0; i < list.Count; i++)
            {
                var node = list[i];
                if (node == null) continue;
                int level = RuneLevel(node);
                bool can = RuneCanUpgrade(node);
                int cost = RuneUpgradeCost(node);
                string name = "";
                try
                {
                    if (tip != null)
                    {
                        tip.mbt(node);
                        if (tip.m_runeNameText != null) name = tip.m_runeNameText.text ?? "";
                        if (tip.m_runeCostValueText != null)
                        {
                            long parsed = ParseGoldText(tip.m_runeCostValueText.text);
                            if (parsed > 0) cost = (int)Math.Min(parsed, int.MaxValue);
                        }
                    }
                }
                catch { }

                bool affordable = can && cost > 0 && (gold < 0 || cost <= gold);
                if (affordable) upgradeable++;

                // Always log affordable ones; otherwise the first few + anything named Hoarding.
                bool interesting = affordable
                    || listed < 10
                    || (name != null && name.IndexOf("Hoard", StringComparison.OrdinalIgnoreCase) >= 0)
                    || (cost >= 10_000_000 && cost <= 15_000_000);
                if (interesting)
                {
                    AutoSynthPlugin.Logger.LogInfo(
                        $"rune economy: key={node.m_runeKey} lv={level} cost={cost} can={can} " +
                        $"affordable={affordable} name='{name}'");
                    listed++;
                }
                if (affordable && cost < cheapestCost)
                {
                    cheapest = node;
                    cheapestCost = cost;
                    cheapestName = name;
                }
            }
            if (cheapest != null)
                AutoSynthPlugin.Logger.LogInfo(
                    $"rune economy: cheapest affordable key={cheapest.m_runeKey} cost={cheapestCost} " +
                    $"name='{cheapestName}' gold={gold} upgradeableCount={upgradeable}");
            else
                AutoSynthPlugin.Logger.LogInfo(
                    $"rune economy: no affordable upgradeable rune (gold={gold} upgradeableCount={upgradeable})");

            WriteRuneProbe(gold, goldText, cheapest != null ? cheapest.m_runeKey : -1,
                cheapest != null ? cheapestCost : -1, cheapestName);
        }
        catch (Exception e)
        {
            AutoSynthPlugin.Logger.LogWarning("LogRuneEconomy failed: " + e.Message);
        }
    }

    private static void WriteRuneProbe(long gold, string goldText, int cheapestKey, int cheapestCost, string cheapestName)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "tbh-companion", "rune-probe.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string safeName = (cheapestName ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
            File.WriteAllText(path,
                "{\"gold\":" + gold +
                ",\"goldText\":\"" + (goldText ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"" +
                ",\"cheapestKey\":" + cheapestKey +
                ",\"cheapestCost\":" + cheapestCost +
                ",\"cheapestName\":\"" + safeName + "\"" +
                ",\"updatedUtc\":\"" + DateTime.UtcNow.ToString("o") + "\"}");
        }
        catch { }
    }

    private static long ParseGoldText(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return -1;
        var s = raw.Trim().Replace(",", "").Replace(" ", "").Replace("\u00A0", "");
        if (s.Length == 0) return -1;
        char suffix = char.ToUpperInvariant(s[s.Length - 1]);
        double mult = 1;
        if (suffix == 'K') { mult = 1_000; s = s.Substring(0, s.Length - 1); }
        else if (suffix == 'M') { mult = 1_000_000; s = s.Substring(0, s.Length - 1); }
        else if (suffix == 'B') { mult = 1_000_000_000; s = s.Substring(0, s.Length - 1); }
        var cleaned = new System.Text.StringBuilder(s.Length);
        bool sawDot = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c >= '0' && c <= '9') cleaned.Append(c);
            else if (c == '.' && !sawDot) { cleaned.Append(c); sawDot = true; }
        }
        if (cleaned.Length == 0) return -1;
        if (!double.TryParse(cleaned.ToString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var n))
            return -1;
        return (long)(n * mult);
    }

    private static bool ClickUnityButton(Button button, string name, bool loud)
    {
        if (button == null)
        {
            AutoSynthPlugin.Logger.LogWarning($"{name}: null");
            return false;
        }
        if (!button.gameObject.activeInHierarchy)
        {
            if (loud) AutoSynthPlugin.Logger.LogInfo($"{name}: inactive, skipped");
            return false;
        }
        if (!button.interactable)
        {
            if (loud) AutoSynthPlugin.Logger.LogInfo($"{name}: not interactable, skipped");
            return false;
        }
        if (button.onClick != null)
        {
            button.onClick.Invoke();
            if (loud) AutoSynthPlugin.Logger.LogInfo($"clicked {name}");
            return true;
        }
        AutoSynthPlugin.Logger.LogWarning($"{name}: no onClick");
        return false;
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
        var inner = InnerButton(button);
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

            DumpRuneState();
        }
        catch (Exception e)
        {
            AutoSynthPlugin.Logger.LogError($"dump failed: {e}");
        }
    }

    private void DumpRuneState()
    {
        try
        {
            var runeUi = FindRuneUi();
            AutoSynthPlugin.Logger.LogInfo(
                $"dump: runeOpen={(runeUi != null && runeUi.gameObject.activeInHierarchy)} " +
                $"runeMenuBtn={Describe(RuneMenuButton())} autoUpgradeRune={AutoSynthPlugin.AutoUpgradeRune}");
            if (runeUi == null)
            {
                AutoSynthPlugin.Logger.LogInfo("dump: UI_Rune not found");
                return;
            }
            var page = runeUi.m_runePage;
            if (page == null)
            {
                AutoSynthPlugin.Logger.LogInfo("dump: RunePage null");
                return;
            }
            long gold = ReadGold(page);
            string goldRaw = page.m_goldText != null ? page.m_goldText.text : "(null)";
            AutoSynthPlugin.Logger.LogInfo($"dump: goldParsed={gold} goldText='{goldRaw}'");
            var list = page.m_listRuneNode;
            if (list == null)
            {
                AutoSynthPlugin.Logger.LogInfo("dump: m_listRuneNode null");
                return;
            }
            int shown = 0;
            for (int i = 0; i < list.Count; i++)
            {
                var node = list[i];
                if (node == null) continue;
                int cost = RuneUpgradeCost(node);
                int level = RuneLevel(node);
                var btn = node.m_levelUpButton;
                string btnState = btn == null ? "null"
                    : $"active={btn.gameObject.activeInHierarchy} interactable={btn.interactable}";
                bool can = RuneCanUpgrade(node);
                string levelInfo = "?";
                try
                {
                    var info = node.btby ?? node.bgir;
                    if (info != null)
                        levelInfo = $"bgpu={info.bgpu} bgpv={info.bgpv} bgpw={info.bgpw} bgpx={info.bgpx} bgpz={info.bgpz}";
                }
                catch { }
                bool btcb = false, btcd = false;
                try { btcb = node.btcb; } catch { }
                try { btcd = node.btcd; } catch { }
                // Prefer logging upgradeable / low-cost nodes; always log first few.
                if (!can && shown >= 12 && cost > gold && gold >= 0) continue;
                AutoSynthPlugin.Logger.LogInfo(
                    $"dump: rune[{i}] key={node.m_runeKey} lv={level} cost={cost} can={can} " +
                    $"btcb={btcb} btcd={btcd} btn=[{btnState}] levelInfo=[{levelInfo}]");
                shown++;
                if (shown >= 40) break;
            }
            AutoSynthPlugin.Logger.LogInfo($"dump: rune nodes listed={shown}/{list.Count}");
        }
        catch (Exception e)
        {
            AutoSynthPlugin.Logger.LogError($"dump rune failed: {e}");
        }
    }

    private static string Describe(ToggleButton b)
        => b == null ? "null" : $"[active={b.gameObject.activeInHierarchy} on={IsOn(b)}]";

    // ---- obfuscated-member access -------------------------------------------
    // The game's obfuscator re-randomizes short member names every patch. The
    // default build binds them by name (fast, but breaks each update). The
    // "-next" edition (built with /define:RESILIENT) instead resolves each one by
    // signature at runtime, so those patches no longer need a manual remap. The
    // real (un-obfuscated) names - class names, `m_` fields, ItemKey/GRADE,
    // itemInfoData - are used directly in both builds.

#if RESILIENT
    private static bool _obfResolved;
    private static PropertyInfo _pRecipeType, _pInnerButton, _pIsOn, _pCubeItemData, _pItemInfoData;
    private static Type _dbType;

    private const BindingFlags DeclInstance =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    // The single property of a given type declared on `declaring`. With readOnly,
    // only get-only properties qualify (distinguishes a computed getter from the
    // serialized get/set field of the same type).
    private static PropertyInfo OnlyProp(Type declaring, Type propType, bool readOnly)
    {
        PropertyInfo found = null;
        foreach (var p in declaring.GetProperties(DeclInstance))
        {
            if (p.PropertyType != propType) continue;
            if (readOnly && p.CanWrite) continue;
            if (found != null)
            {
                AutoSynthPlugin.Logger.LogWarning(
                    $"interop resolve: {declaring.Name} has >1 {propType.Name}" +
                    $"{(readOnly ? " read-only" : "")} property ({found.Name}, {p.Name}); using {found.Name}");
                break;
            }
            found = p;
        }
        if (found == null)
            AutoSynthPlugin.Logger.LogWarning(
                $"interop resolve: no {propType.Name}{(readOnly ? " read-only" : "")} property on {declaring.Name}");
        return found;
    }

    // The item DB is the singleton carrying every info-data list. Match on several
    // real (un-obfuscated) list names so a coroutine state machine that merely
    // mentions itemInfoData can't be mistaken for it.
    private static Type FindDbType()
    {
        Type[] types;
        try { types = typeof(UI_Cube).Assembly.GetTypes(); }
        catch (ReflectionTypeLoadException e) { types = e.Types; }
        foreach (var t in types)
        {
            if (t == null) continue;
            if (t.GetProperty("itemInfoData", DeclInstance) != null
                && t.GetProperty("heroInfoData", DeclInstance) != null
                && t.GetProperty("stageInfoData", DeclInstance) != null)
                return t;
        }
        return null;
    }

    private static void ResolveInterop()
    {
        if (_obfResolved) return;
        _obfResolved = true;
        _pRecipeType = OnlyProp(typeof(SubRecipeComboBoxButton), typeof(ERecipeType), false);
        _pInnerButton = OnlyProp(typeof(ButtonBase), typeof(UnityEngine.UI.Button), true);
        _pIsOn = OnlyProp(typeof(ToggleButton), typeof(bool), true);
        _pCubeItemData = OnlyProp(typeof(CubeInData), typeof(CubeItemData), false);
        _dbType = FindDbType();
        _pItemInfoData = _dbType != null ? _dbType.GetProperty("itemInfoData", DeclInstance) : null;
        AutoSynthPlugin.Logger.LogInfo(
            "interop resolved (RESILIENT): " +
            $"ERecipeType={PName(_pRecipeType)}, innerButton={PName(_pInnerButton)}, " +
            $"isOn={PName(_pIsOn)}, cubeItemData={PName(_pCubeItemData)}, itemDb={(_dbType != null ? _dbType.Name : "null")}");
    }

    private static string PName(PropertyInfo p) => p != null ? p.Name : "null";
#endif

    private static ERecipeType RecipeTypeOf(SubRecipeComboBoxButton c)
    {
#if RESILIENT
        ResolveInterop();
        return (ERecipeType)_pRecipeType.GetValue(c);
#else
        return c.bfxh;
#endif
    }

    private static UnityEngine.UI.Button InnerButton(ButtonBase b)
    {
#if RESILIENT
        ResolveInterop();
        return (UnityEngine.UI.Button)_pInnerButton.GetValue(b);
#else
        return b.bsec;
#endif
    }

    private static bool IsOn(ToggleButton b)
    {
#if RESILIENT
        ResolveInterop();
        return (bool)_pIsOn.GetValue(b);
#else
        return b.bseh;
#endif
    }

    private static int CubeItemKey(CubeInData data)
    {
#if RESILIENT
        ResolveInterop();
        var cid = (CubeItemData)_pCubeItemData.GetValue(data);
        return cid.ItemKey;
#else
        return data.bfbr.ItemKey;
#endif
    }

    private static Il2CppSystem.Collections.Generic.List<ItemInfoData> ItemInfoList()
    {
#if RESILIENT
        ResolveInterop();
        if (_dbType == null || _pItemInfoData == null) return null;
        var t = Il2CppInterop.Runtime.Il2CppType.From(_dbType);
        var all = UnityEngine.Resources.FindObjectsOfTypeAll(t);
        if (all == null || all.Length == 0) return null;
        var db = Activator.CreateInstance(_dbType, new object[] { all[0].Pointer });
        return _pItemInfoData.GetValue(db) as Il2CppSystem.Collections.Generic.List<ItemInfoData>;
#else
        bal db = null;
        try { db = nq<bal>.bsen; } catch (Exception e) { AutoSynthPlugin.Logger.LogWarning($"nq<bal>.bsen failed: {e.Message}"); }
        if (db == null) db = UnityEngine.Object.FindObjectOfType<bal>(true);
        return db != null ? db.itemInfoData : null;
#endif
    }

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
