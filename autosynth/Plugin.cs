using System;
using BepInEx;
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

[BepInPlugin("com.pres.tbh.autosynth", "TBH Auto Synthesis", "0.11.0")]
public class AutoSynthPlugin : BasePlugin
{
    internal static ManualLogSource Logger;
    internal static float AfterFillDelay = 1.0f;
    internal static float AfterSynthDelay = 4.0f;
    internal static float AfterClearDelay = 1.0f;
    internal static int MaxGrade = 2;
    internal static bool AutoStart = true;

    public override void Load()
    {
        Logger = Log;
        AfterFillDelay = Config.Bind("Timing", "AfterFillSeconds", 1.0f,
            "Delay after clicking auto-fill before starting synthesis").Value;
        AfterSynthDelay = Config.Bind("Timing", "AfterSynthesisSeconds", 4.0f,
            "Delay after clicking the trigger, so the synthesis can finish").Value;
        AfterClearDelay = Config.Bind("Timing", "CycleIntervalSeconds", 300.0f,
            "Delay after emptying the cube before the next cycle starts (default: 5 minutes)").Value;
        AutoStart = Config.Bind("General", "AutoStart", true,
            "Arm the auto loop as soon as the game starts (no F8 needed). " +
            "It only acts while the Cube panel is open; F8 still toggles it.").Value;
        MaxGrade = Config.Bind("Safety", "MaxGrade", 2,
            "Highest item grade the auto loop may synthesize: 0=COMMON 1=UNCOMMON 2=RARE 3=LEGENDARY 4=IMMORTAL ... " +
            "If any cube slot holds an item above this grade, synthesis is skipped and the cube is cleared.").Value;
        if (!ClassInjector.IsTypeRegisteredInIl2Cpp<AutoSynthBehaviour>())
            ClassInjector.RegisterTypeInIl2Cpp<AutoSynthBehaviour>();
        AddComponent<AutoSynthBehaviour>();
        Logger.LogInfo("TBH Auto Synthesis 0.11.0: F8 = toggle auto (select recipe -> fill -> synth -> clear loop), F9 = click trigger once, F10 = dump cube state.");
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
    private float _nextTick;
    private UI_Cube _cube;
    private bool _legacyInputBroken;
    private bool _autoStartApplied;

    private void Update()
    {
        if (!_autoStartApplied)
        {
            _autoStartApplied = true;
            if (AutoSynthPlugin.AutoStart)
            {
                _auto = true;
                AutoSynthPlugin.Logger.LogInfo(
                    "Auto-synthesis armed on launch (AutoStart=true) - open the Cube panel to run it. F8 toggles.");
            }
        }
        if (KeyDown(KeyCode.F8))
        {
            _auto = !_auto;
            _phase = Phase.Fill;
            _cycles = 0;
            _recipeSelected = false;
            _nextTick = 0f;
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
            if (!CubeOpen(cube)) return;

            var loud = _cycles < 2 || _cycles % 20 == 0;
            switch (_phase)
            {
                case Phase.Fill:
                    if (!_recipeSelected)
                    {
                        _recipeSelected = true;
                        SelectHighestUnlockedRecipe();
                        // give the UI a tick to apply the recipe before filling
                        _nextTick = Time.unscaledTime + AutoSynthPlugin.AfterFillDelay;
                        break;
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
                        AutoSynthPlugin.Logger.LogInfo(
                            $"synthesis started: {itemCount} item(s), rarity {GradeName(maxGrade)}");
                    _phase = Phase.Clear;
                    _nextTick = Time.unscaledTime + AutoSynthPlugin.AfterSynthDelay;
                    break;
                case Phase.Clear:
                    ClickTrash(cube.m_trashToggleBtn, loud);
                    _phase = Phase.Fill;
                    _cycles++;
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
        bas db = null;
        try { db = nq<bas>.bsfs; } catch (Exception e) { AutoSynthPlugin.Logger.LogWarning($"nq<bas>.bsfs failed: {e.Message}"); }
        if (db == null) db = UnityEngine.Object.FindObjectOfType<bas>(true);
        if (db == null) { AutoSynthPlugin.Logger.LogWarning("item db (bas) not found"); return; }
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

    private void SelectHighestUnlockedRecipe()
    {
        try
        {
            var combos = UnityEngine.Object.FindObjectsOfType<SubRecipeComboBoxButton>(true);
            SubRecipeComboBoxButton synth = null;
            foreach (var c in combos)
                if (c != null && c.bfyp == ERecipeType.SYNTHESIS) { synth = c; break; }
            if (synth == null)
            {
                AutoSynthPlugin.Logger.LogWarning("recipe select: SYNTHESIS sub-recipe combo box not found");
                return;
            }
            var buttons = synth.m_subRecipeSlotButton;
            if (buttons == null || buttons.Count == 0)
            {
                AutoSynthPlugin.Logger.LogWarning("recipe select: no sub-recipe buttons");
                return;
            }
            for (int i = buttons.Count - 1; i >= 0; i--)
            {
                var b = buttons[i];
                if (b == null || b.m_isLocked) continue;
                var label = b.m_text != null ? b.m_text.text : $"#{i}";
                if (b.m_isSelected)
                {
                    AutoSynthPlugin.Logger.LogInfo($"recipe select: highest unlocked '{label}' already selected");
                    return;
                }
                var btn = b.m_clickButton;
                if (btn != null && btn.onClick != null)
                {
                    btn.onClick.Invoke();
                    AutoSynthPlugin.Logger.LogInfo($"recipe select: picked highest unlocked '{label}'");
                }
                else AutoSynthPlugin.Logger.LogWarning($"recipe select: '{label}' has no click button");
                return;
            }
            AutoSynthPlugin.Logger.LogWarning("recipe select: every sub-recipe is locked");
        }
        catch (Exception e)
        {
            AutoSynthPlugin.Logger.LogError($"recipe select failed: {e}");
        }
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
        try { int key = data.bfda.ItemKey; return key; }
        catch { return data.bstu; }
    }

    private UI_Cube FindCube()
    {
        if (_cube == null)
            _cube = UnityEngine.Object.FindObjectOfType<UI_Cube>(true);
        return _cube;
    }

    private static bool CubeOpen(UI_Cube cube)
        => cube != null && cube.gameObject.activeInHierarchy;

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
        var inner = button.bsfh;
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
                AutoSynthPlugin.Logger.LogInfo($"dump: slot {i} itemKey={key} (bstu={data.bstu}) grade={grade}");
            }
        }
        catch (Exception e)
        {
            AutoSynthPlugin.Logger.LogError($"dump failed: {e}");
        }
    }

    private static string Describe(ToggleButton b)
        => b == null ? "null" : $"[active={b.gameObject.activeInHierarchy} on={b.bsfm}]";

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
