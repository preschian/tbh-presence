using System;
using System.Collections.Generic;
using System.Reflection;
using TaskbarHero;
using TaskbarHero.Data;
using TaskbarHero.UI;
using TaskbarHero.UI.Rune;
using TS;
using UnityEngine;

namespace TbhAutoSynth;

// Obfuscated-member access for cube/recipe UI and the item/rune DB. Resolves
// members by signature at runtime so a game patch that re-randomizes those
// names no longer needs a manual remap.
internal static class GameInterop
{
    static bool _obfResolved;
    static Exception _resolveError;
    static PropertyInfo _pRecipeType, _pInnerButton, _pIsOn, _pCubeItemData, _pItemInfoData;
    static PropertyInfo _pRuneNodeLevelInfo, _pRuneNodeSave, _pRuneLevelCost, _pRuneSaveLevel;
    static MethodInfo _mRuneTooltipBind, _mSubRecipeOpen;
    static MethodInfo[] _mRuneLevelInfo, _mSubRecipeActions;
    static Type _dbType;
    static bool _runeMenuFallbackLogged;

    const BindingFlags DeclInstance =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    static PropertyInfo OnlyProp(Type declaring, Type propType, bool readOnly)
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

    static MethodInfo[] Methods(Type declaring, Type returnType, params Type[] args)
    {
        var found = new List<MethodInfo>();
        if (declaring == null) return found.ToArray();
        foreach (var m in declaring.GetMethods(DeclInstance))
        {
            if (m.ReturnType != returnType) continue;
            var ps = m.GetParameters();
            if (ps.Length != args.Length) continue;
            bool match = true;
            for (int i = 0; i < args.Length; i++)
            {
                if (ps[i].ParameterType != args[i]) { match = false; break; }
            }
            if (match) found.Add(m);
        }
        return found.ToArray();
    }

    static PropertyInfo FirstProp(Type declaring, Type propType)
    {
        foreach (var p in declaring.GetProperties(DeclInstance))
            if (p.PropertyType == propType) return p;
        return null;
    }

    static PropertyInfo FirstPropNamed(Type declaring, string typeName)
    {
        foreach (var p in declaring.GetProperties(DeclInstance))
            if (p.PropertyType.Name == typeName) return p;
        return null;
    }

    static PropertyInfo IntPropAt(Type declaring, int index)
    {
        int seen = 0;
        foreach (var p in declaring.GetProperties(DeclInstance))
        {
            if (p.PropertyType != typeof(int)) continue;
            if (seen++ == index) return p;
        }
        return null;
    }

    static MethodInfo[] SubRecipeActions()
    {
        // Include subclass and base handlers. The exact names are randomized every
        // game patch, but their parameterless shape is stable.
        var result = new List<MethodInfo>();
        Type[] types = { typeof(SubRecipeComboBoxButton), typeof(ComboBoxButton) };
        foreach (var type in types)
        {
            foreach (var m in type.GetMethods(DeclInstance))
            {
                if (m.IsSpecialName || m.ReturnType != typeof(void) || m.GetParameters().Length != 0)
                    continue;
                bool duplicate = false;
                foreach (var old in result)
                    if (old.Name == m.Name && old.DeclaringType == m.DeclaringType) { duplicate = true; break; }
                if (!duplicate) result.Add(m);
            }
        }
        return result.ToArray();
    }

    static Type FindDbType()
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

    static void Resolve()
    {
        if (_obfResolved)
        {
            if (_resolveError != null) throw _resolveError;
            return;
        }
        _obfResolved = true;
        _pRecipeType = OnlyProp(typeof(SubRecipeComboBoxButton), typeof(ERecipeType), false);
        _pInnerButton = OnlyProp(typeof(ButtonBase), typeof(UnityEngine.UI.Button), true);
        _pIsOn = OnlyProp(typeof(ToggleButton), typeof(bool), true);
        _pCubeItemData = OnlyProp(typeof(CubeInData), typeof(CubeItemData), false);
        _pRuneNodeLevelInfo = FirstProp(typeof(RuneNode), typeof(RuneLevelInfoData));
        _pRuneNodeSave = FirstPropNamed(typeof(RuneNode), "RuneSaveData");
        _pRuneLevelCost = IntPropAt(typeof(RuneLevelInfoData), 3);
        _pRuneSaveLevel = _pRuneNodeSave != null
            ? _pRuneNodeSave.PropertyType.GetProperty("Level", DeclInstance)
            : null;
        var tooltipBind = Methods(typeof(RuneTooltip), typeof(void), typeof(RuneNode));
        _mRuneTooltipBind = tooltipBind.Length > 0 ? tooltipBind[0] : null;
        _mSubRecipeActions = SubRecipeActions();
        var subRecipeOpen = Methods(typeof(ComboBoxButton), typeof(void), typeof(bool));
        _mSubRecipeOpen = subRecipeOpen.Length > 0 ? subRecipeOpen[0] : null;
        _dbType = FindDbType();
        _pItemInfoData = _dbType != null ? _dbType.GetProperty("itemInfoData", DeclInstance) : null;
        _mRuneLevelInfo = Methods(_dbType, typeof(RuneLevelInfoData), typeof(int), typeof(int));
        AutoSynthPlugin.Logger.LogInfo(
            "interop resolved: " +
            $"ERecipeType={PName(_pRecipeType)}, innerButton={PName(_pInnerButton)}, " +
            $"isOn={PName(_pIsOn)}, cubeItemData={PName(_pCubeItemData)}, " +
            $"runeNodeInfo={PName(_pRuneNodeLevelInfo)}, runeNodeSave={PName(_pRuneNodeSave)}, " +
            $"runeCost={PName(_pRuneLevelCost)}, runeTooltipBind={MName(_mRuneTooltipBind)}, " +
            $"subRecipeOpen={MName(_mSubRecipeOpen)}, " +
            $"subRecipeActions=[{string.Join(",", Array.ConvertAll(_mSubRecipeActions, m => m.Name))}], " +
            $"itemDb={(_dbType != null ? _dbType.Name : "null")}, " +
            $"runeLevelInfo=[{string.Join(",", Array.ConvertAll(_mRuneLevelInfo, m => m.Name))}]");

        if (_pRecipeType == null || _pInnerButton == null || _pIsOn == null || _pCubeItemData == null)
        {
            _resolveError = new InvalidOperationException(
                "required interop property missing: " +
                $"ERecipeType={PName(_pRecipeType)}, innerButton={PName(_pInnerButton)}, " +
                $"isOn={PName(_pIsOn)}, cubeItemData={PName(_pCubeItemData)}");
            throw _resolveError;
        }
    }

    static string PName(PropertyInfo p) => p != null ? p.Name : "null";
    static string MName(MethodInfo m) => m != null ? m.Name : "null";

    static object DbInstance()
    {
        Resolve();
        if (_dbType == null) return null;
        var t = Il2CppInterop.Runtime.Il2CppType.From(_dbType);
        var all = UnityEngine.Resources.FindObjectsOfTypeAll(t);
        if (all == null || all.Length == 0) return null;
        return Activator.CreateInstance(_dbType, new object[] { all[0].Pointer });
    }

    internal static ERecipeType RecipeTypeOf(SubRecipeComboBoxButton c)
    {
        Resolve();
        return (ERecipeType)_pRecipeType.GetValue(c);
    }

    internal static UnityEngine.UI.Button InnerButton(ButtonBase b)
    {
        Resolve();
        return (UnityEngine.UI.Button)_pInnerButton.GetValue(b);
    }

    internal static bool IsOn(ToggleButton b)
    {
        Resolve();
        return (bool)_pIsOn.GetValue(b);
    }

    internal static int CubeItemKey(CubeInData data)
    {
        Resolve();
        var cid = (CubeItemData)_pCubeItemData.GetValue(data);
        return cid.ItemKey;
    }

    // These fields used to have names such as bgpx / bgis. Their semantic type
    // and order are stable, while the generated names change every patch.
    internal static int RuneLevel(RuneNode node)
    {
        Resolve();
        if (node == null || _pRuneNodeSave == null || _pRuneSaveLevel == null) return -1;
        try
        {
            object save = _pRuneNodeSave.GetValue(node);
            return save != null ? (int)_pRuneSaveLevel.GetValue(save) : -1;
        }
        catch { return -1; }
    }

    internal static RuneLevelInfoData RuneLevelInfoOf(RuneNode node)
    {
        Resolve();
        try { return node != null && _pRuneNodeLevelInfo != null
            ? _pRuneNodeLevelInfo.GetValue(node) as RuneLevelInfoData : null; }
        catch { return null; }
    }

    internal static int RuneLevelCost(RuneLevelInfoData info)
    {
        Resolve();
        try { return info != null && _pRuneLevelCost != null ? (int)_pRuneLevelCost.GetValue(info) : -1; }
        catch { return -1; }
    }

    internal static void ShowRuneTooltip(RuneTooltip tooltip, RuneNode node)
    {
        Resolve();
        if (tooltip == null || node == null || _mRuneTooltipBind == null) return;
        _mRuneTooltipBind.Invoke(tooltip, new object[] { node });
    }

    // Invokes one candidate only. The caller retries until the game's sub-recipe
    // slots are populated, so an update cannot permanently bind us to a renamed
    // method that merely toggles the dropdown.
    internal static bool TryPopulateSubRecipes(SubRecipeComboBoxButton combo, int attempt,
        out string methodName, out string error)
    {
        Resolve();
        methodName = null; error = null;
        if (combo == null || attempt < 0) return false;
        if (_mSubRecipeOpen != null && attempt == 0)
        {
            methodName = _mSubRecipeOpen.Name + "(true)";
            try { _mSubRecipeOpen.Invoke(combo, new object[] { true }); return true; }
            catch (Exception e) { error = e.GetBaseException().Message; return false; }
        }
        int actionIndex = attempt - (_mSubRecipeOpen != null ? 1 : 0);
        if (_mSubRecipeActions == null || actionIndex >= _mSubRecipeActions.Length) return false;
        var method = _mSubRecipeActions[actionIndex];
        methodName = method.Name;
        try { method.Invoke(combo, null); return true; }
        catch (Exception e) { error = e.GetBaseException().Message; return false; }
    }

    // UI_Main keeps stable button_* names across patches; only the wrapper type
    // (currently `zm`) is obfuscated, and it still exposes toggleButton.
    static ToggleButton FromMainUi(string label)
    {
        try
        {
            var main = UnityEngine.Object.FindObjectOfType<UI_Main>(true);
            if (main == null) return null;
            zm entry = null;
            if (string.Equals(label, "Cube", StringComparison.OrdinalIgnoreCase)) entry = main.button_Cube;
            else if (string.Equals(label, "Rune", StringComparison.OrdinalIgnoreCase)) entry = main.button_Rune;
            else if (string.Equals(label, "Stash", StringComparison.OrdinalIgnoreCase)) entry = main.button_Stash;
            else if (string.Equals(label, "Stat", StringComparison.OrdinalIgnoreCase)) entry = main.button_Stat;
            else if (string.Equals(label, "Portal", StringComparison.OrdinalIgnoreCase)) entry = main.button_Portal;
            return entry != null ? entry.toggleButton : null;
        }
        catch (Exception e)
        {
            AutoSynthPlugin.Logger.LogWarning($"menu resolve via UI_Main failed: {e.Message}");
            return null;
        }
    }

    static bool MatchesMenuLabel(ToggleButton button, string label)
    {
        for (Transform t = button.transform; t != null; t = t.parent)
            if (t.name.IndexOf(label, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

        var texts = button.GetComponentsInChildren<TMPro.TextMeshProUGUI>(true);
        foreach (var text in texts)
        {
            string value = text != null ? text.text : null;
            if (!string.IsNullOrEmpty(value)
                && value.IndexOf(label, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        return false;
    }

    static ToggleButton RuneAfterCube(ToggleButton[] buttons)
    {
        ToggleButton cube = null;
        foreach (var button in buttons)
            if (button != null && button.gameObject.activeInHierarchy && MatchesMenuLabel(button, "Cube"))
            { cube = button; break; }
        if (cube == null) return null;

        // The main-content tabs keep their stable order even though the entry
        // object and the Rune label are obfuscated. Rune sits immediately left of Cube.
        for (Transform parent = cube.transform.parent; parent != null; parent = parent.parent)
        {
            var siblings = new List<ToggleButton>();
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                var button = child.GetComponent<ToggleButton>();
                if (button == null) button = child.GetComponentInChildren<ToggleButton>(true);
                if (button != null && button.gameObject.activeInHierarchy)
                    siblings.Add(button);
            }
            int cubeIndex = siblings.IndexOf(cube);
            if (cubeIndex > 0 && siblings.Count >= 5)
                return siblings[cubeIndex - 1];
        }
        return null;
    }

    internal static ToggleButton FindMenuToggle(string label)
    {
        // Prefer UI_Main: button_Rune's on-screen label is localized/obfuscated, so
        // text/hierarchy matching fails even though the field name stays stable.
        var fromMain = FromMainUi(label);
        if (fromMain != null) return fromMain;

        var buttons = UnityEngine.Object.FindObjectsOfType<ToggleButton>(true);
        ToggleButton inactiveMatch = null;
        foreach (var button in buttons)
        {
            if (button == null) continue;
            if (!MatchesMenuLabel(button, label)) continue;
            if (button.gameObject.activeInHierarchy) return button;
            if (inactiveMatch == null) inactiveMatch = button;
        }
        if (string.Equals(label, "Rune", StringComparison.OrdinalIgnoreCase))
        {
            var rune = RuneAfterCube(buttons);
            if (rune != null)
            {
                if (!_runeMenuFallbackLogged)
                {
                    _runeMenuFallbackLogged = true;
                    AutoSynthPlugin.Logger.LogInfo("menu resolve: selected the active tab left of Cube as Rune");
                }
                return rune;
            }
        }
        return inactiveMatch;
    }

    internal static Il2CppSystem.Collections.Generic.List<ItemInfoData> ItemInfoList()
    {
        Resolve();
        if (_dbType == null || _pItemInfoData == null) return null;
        var db = DbInstance();
        if (db == null) return null;
        return _pItemInfoData.GetValue(db) as Il2CppSystem.Collections.Generic.List<ItemInfoData>;
    }

    // Several (int,int)->RuneLevelInfoData methods exist on the DB type; try each
    // until one returns a row (same fan-out as before, discovered by signature).
    internal static RuneLevelInfoData LookupRuneLevelInfo(int runeKey, int level)
    {
        try
        {
            Resolve();
            if (_mRuneLevelInfo == null || _mRuneLevelInfo.Length == 0) return null;
            var db = DbInstance();
            if (db == null) return null;
            object[] args = { runeKey, level };
            foreach (var m in _mRuneLevelInfo)
            {
                try
                {
                    var r = m.Invoke(db, args) as RuneLevelInfoData;
                    if (r != null) return r;
                }
                catch { }
            }
        }
        catch { }
        return null;
    }
}
