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

namespace TbhAutoSynth;

[BepInPlugin("com.pres.tbh.autosynth", "TBH Auto Synthesis", AutoSynthPlugin.Version)]
public class AutoSynthPlugin : BasePlugin
{
    internal const string Version = "0.24.0";

    internal static ManualLogSource Logger;
    private static ConfigFile _conf;
    private static ConfigEntry<float> _afterFillE, _afterSynthE, _cycleE;
    private static ConfigEntry<int> _maxGradeE;
    private static ConfigEntry<bool> _autoStartE, _autoOpenE;

    internal static float AfterFillDelay => _afterFillE != null ? _afterFillE.Value : 1.0f;
    internal static float AfterSynthDelay => _afterSynthE != null ? _afterSynthE.Value : 4.0f;
    internal static float AfterClearDelay => _cycleE != null ? _cycleE.Value : 300.0f;
    internal static int MaxGrade => _maxGradeE != null ? _maxGradeE.Value : 2;
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
    internal static void ReloadConfig()
    {
        if (_conf == null) return;
        try
        {
            int mg = MaxGrade; float ci = AfterClearDelay; bool auto = AutoStart; bool open = AutoOpenCube;
            _conf.Reload();
            if (mg != MaxGrade || ci != AfterClearDelay || auto != AutoStart || open != AutoOpenCube)
                Logger.LogInfo($"config reloaded: MaxGrade={MaxGrade}, CycleIntervalSeconds={AfterClearDelay}, " +
                               $"AutoStart={AutoStart}, AutoOpenCube={AutoOpenCube}");
        }
        catch (Exception e) { Logger.LogWarning("config reload failed: " + e.Message); }
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
            "Arm the auto loop as soon as the game starts (no F8 needed). F8 still toggles it.");
        _autoOpenE = Config.Bind("General", "AutoOpenCube", true,
            "While the loop is armed, click the Cube menu button to open the Cube panel when a " +
            "cycle is due. Turn this off to only run while you have the Cube panel open yourself.");
        _typesE = Config.Bind("General", "SynthesisTypes", "Equipment,Materials,Accessories",
            "Which synthesis item types the loop rotates through, comma-separated: " +
            "Equipment, Materials, Accessories. e.g. 'Equipment,Materials' to skip accessories.");
        _maxGradeE = Config.Bind("Safety", "MaxGrade", 2,
            "Highest item grade the auto loop may synthesize: 0=COMMON 1=UNCOMMON 2=RARE 3=LEGENDARY 4=IMMORTAL ... " +
            "If any cube slot holds an item above this grade, synthesis is skipped and the cube is cleared.");
        if (!ClassInjector.IsTypeRegisteredInIl2Cpp<AutoSynthBehaviour>())
            ClassInjector.RegisterTypeInIl2Cpp<AutoSynthBehaviour>();
        AddComponent<AutoSynthBehaviour>();
        Logger.LogInfo($"TBH Auto Synthesis {Version}: F8 = toggle auto (select recipe -> fill -> synth -> clear loop), F9 = click trigger once, F10 = dump cube state.");
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
        {
            _auto = !_auto;
            _phase = Phase.Fill;
            _cycles = 0;
            _recipeSelected = false;
            _recipeAttempts = 0;
            _typeSelected = false;
            _nextTick = 0f;
            _nextOpenAttempt = 0f;
            _nextStatusWrite = 0f;
            AutoSynthPlugin.Logger.LogInfo($"Auto-synthesis: {(_auto ? "ON" : "OFF")}");
        }
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
                        _recipeSelected = SelectHighestUnlockedRecipe(_recipeAttempts <= 3);
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
        bal db = null;
        try { db = nq<bal>.bsen; } catch (Exception e) { AutoSynthPlugin.Logger.LogWarning($"nq<bal>.bsen failed: {e.Message}"); }
        if (db == null) db = UnityEngine.Object.FindObjectOfType<bal>(true);
        if (db == null) { AutoSynthPlugin.Logger.LogWarning("item db (bal) not found"); return; }
        var list = db.itemInfoData;
        if (list == null || list.Count == 0) { AutoSynthPlugin.Logger.LogWarning("item db found but itemInfoData empty"); return; }
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

    private bool SelectHighestUnlockedRecipe(bool loud)
    {
        try
        {
            var combos = UnityEngine.Object.FindObjectsOfType<SubRecipeComboBoxButton>(true);
            SubRecipeComboBoxButton synth = null;
            foreach (var c in combos)
                if (c != null && c.bfxh == ERecipeType.SYNTHESIS) { synth = c; break; }
            if (synth == null)
            {
                // second path: the main recipe button holds a reference to its sub combo
                var mains = UnityEngine.Object.FindObjectsOfType<MainRecipeComboBoxButton>(true);
                foreach (var m in mains)
                {
                    var sc = m != null ? m.m_subRecipeComboBoxButton : null;
                    if (sc != null && sc.bfxh == ERecipeType.SYNTHESIS) { synth = sc; break; }
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
            // Pick the unlocked bracket with the highest lower level bound, so a
            // specific "Lv.65-80" beats the catch-all "Lv.1~ Lv.99". Fall back to
            // list position when a label has no parsable numbers.
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
            for (int i = 0; i < buttons.Count; i++)
            {
                var b = buttons[i];
                if (b == null || b.m_isLocked) continue;
                var label = b.m_text != null ? b.m_text.text : $"#{i}";
                int lo = -1, hi = -1;
                var nums = System.Text.RegularExpressions.Regex.Matches(label, @"\d+");
                if (nums.Count >= 1) lo = int.Parse(nums[0].Value);
                if (nums.Count >= 2) hi = int.Parse(nums[1].Value);
                bool better = best == null
                    || lo > bestLo
                    || (lo == bestLo && hi > bestHi)
                    || (lo == bestLo && hi == bestHi && i > bestIdx);
                if (better) { best = b; bestLabel = label; bestLo = lo; bestHi = hi; bestIdx = i; }
            }
            if (best == null)
            {
                AutoSynthPlugin.Logger.LogWarning("recipe select: every sub-recipe is locked");
                return true; // nothing selectable; don't keep retrying
            }
            if (best.m_isSelected)
            {
                AutoSynthPlugin.Logger.LogInfo($"recipe select: highest unlocked '{bestLabel}' already selected");
                CloseDropdown(synth);
                return true;
            }
            var btn = best.m_clickButton;
            if (btn != null && btn.onClick != null)
            {
                btn.onClick.Invoke();
                AutoSynthPlugin.Logger.LogInfo($"recipe select: picked highest unlocked '{bestLabel}'");
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
        try { int key = data.bfbr.ItemKey; return key; }
        catch { return data.bssu; }
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
        var inner = button.bsec;
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
                AutoSynthPlugin.Logger.LogInfo($"dump: slot {i} itemKey={key} (bssu={data.bssu}) grade={grade}");
            }
        }
        catch (Exception e)
        {
            AutoSynthPlugin.Logger.LogError($"dump failed: {e}");
        }
    }

    private static string Describe(ToggleButton b)
        => b == null ? "null" : $"[active={b.gameObject.activeInHierarchy} on={b.bseh}]";

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
