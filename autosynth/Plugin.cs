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
using TS;
using UnityEngine;
using UnityEngine.EventSystems;

namespace TbhAutoSynth;

[BepInPlugin("com.pres.tbh.autosynth", "TBH Auto Synthesis", AutoSynthPlugin.Version)]
public class AutoSynthPlugin : BasePlugin
{
    internal const string Version = "0.25.0";
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
    private static ConfigEntry<float> _afterFillE, _afterSynthE, _cycleE;
    private static ConfigEntry<int> _maxGradeE, _desiredLevelE;
    private static ConfigEntry<bool> _autoStartE, _autoOpenE;

    internal static float AfterFillDelay => _afterFillE != null ? _afterFillE.Value : 1.0f;
    internal static float AfterSynthDelay => _afterSynthE != null ? _afterSynthE.Value : 4.0f;
    internal static float AfterClearDelay => _cycleE != null ? _cycleE.Value : 300.0f;
    internal static int MaxGrade => _maxGradeE != null ? _maxGradeE.Value : 2;
    // 0 = highest unlocked recipe (default). >0 = exact lower-bound match, else
    // the highest unlocked bracket with lo <= DesiredLevel.
    internal static int DesiredLevel => _desiredLevelE != null ? _desiredLevelE.Value : 0;
    internal static bool AutoStart => _autoStartE == null || _autoStartE.Value;
    internal static bool AutoOpenCube => _autoOpenE == null || _autoOpenE.Value;

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
            int mg = MaxGrade, dl = DesiredLevel;
            float ci = AfterClearDelay; bool auto = AutoStart; bool open = AutoOpenCube;
            _conf.Reload();
            if (mg != MaxGrade || dl != DesiredLevel || ci != AfterClearDelay || auto != AutoStart || open != AutoOpenCube)
                Logger.LogInfo($"config reloaded: MaxGrade={MaxGrade}, DesiredLevel={DesiredLevel}, " +
                               $"CycleIntervalSeconds={AfterClearDelay}, AutoStart={AutoStart}, AutoOpenCube={AutoOpenCube}");
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
            "Delay after emptying the cube before the next cycle starts (default: 5 minutes)");
        _autoStartE = Config.Bind("General", "AutoStart", true,
            "Arm the auto loop as soon as the game starts, and sync the live loop when the " +
            "companion changes this setting. F8 still toggles the live loop without rewriting the cfg.");
        _autoOpenE = Config.Bind("General", "AutoOpenCube", true,
            "While the loop is armed, click the Cube menu button to open the Cube panel when a " +
            "cycle is due. Turn this off to only run while you have the Cube panel open yourself.");
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
        Logger.LogInfo($"TBH Auto Synthesis {Version}{Variant}: F8 = toggle auto (select recipe -> fill -> synth -> clear loop), F9 = click trigger once, F10 = dump cube state.");
    }
}

public class AutoSynthBehaviour : MonoBehaviour
{
    public AutoSynthBehaviour(IntPtr ptr) : base(ptr) { }

    private enum Phase { Fill, Synth, Clear }

    private bool _auto;
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
    private int _openFails;
    private UI_Cube _cube;
    private UI_Main _main;
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
                ",\"maxGrade\":" + AutoSynthPlugin.MaxGrade +
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
                    "Auto-synthesis armed on launch (AutoStart=true). " +
                    (AutoSynthPlugin.AutoOpenCube
                        ? "The Cube panel is opened automatically when a cycle is due."
                        : "AutoOpenCube=false - open the Cube panel yourself to run it.") +
                    " F8 toggles.");
            }
        }
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

    void SetAuto(bool on, string reason)
    {
        _auto = on;
        _phase = Phase.Fill;
        _cycles = 0;
        _recipeSelected = false;
        _recipeAttempts = 0;
        _typeSelected = false;
        _nextTick = 0f;
        _nextOpenAttempt = 0f;
        _nextStatusWrite = 0f;
        string suffix = string.IsNullOrEmpty(reason) ? "" : " (" + reason + ")";
        AutoSynthPlugin.Logger.LogInfo($"Auto-synthesis: {(_auto ? "ON" : "OFF")}{suffix}");
    }

    private void Tick()
    {
        try
        {
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
                    _phase = Phase.Fill;
                    _cycles++;
                    _typeSelected = false;
                    if (loud) AutoSynthPlugin.Logger.LogInfo($"cycle {_cycles} done");
                    _nextTick = Time.unscaledTime + AutoSynthPlugin.AfterClearDelay;
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
            if (cube == null) { AutoSynthPlugin.Logger.LogInfo("dump: UI_Cube not found"); return; }
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
            if (slots == null) { AutoSynthPlugin.Logger.LogInfo("dump: no slot setter/slots"); return; }
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
        catch (Exception e)
        {
            AutoSynthPlugin.Logger.LogError($"dump failed: {e}");
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
            KeyCode.F8 => kb.f8Key.wasPressedThisFrame,
            KeyCode.F9 => kb.f9Key.wasPressedThisFrame,
            KeyCode.F10 => kb.f10Key.wasPressedThisFrame,
            _ => false,
        };
    }
}
