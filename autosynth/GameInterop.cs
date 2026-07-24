using System;
using System.Collections.Generic;
using System.Reflection;
using TaskbarHero;
using TaskbarHero.Data;
using TaskbarHero.StatusSystem;
using TaskbarHero.UI;
using TaskbarHero.UI.Rune;
using TS;
using UnityEngine;
using UnityEngine.EventSystems;
using Object = UnityEngine.Object;

namespace TbhAutoSynth;

// Obfuscated-member access for cube/recipe UI and the item/rune DB. Resolves
// members by signature at runtime so a game patch that re-randomizes those
// names no longer needs a manual remap.
internal static class GameInterop
{
    static bool _obfResolved;
    static Exception _resolveError;
    static PropertyInfo _pRecipeType, _pInnerButton, _pIsOn, _pCubeItemData, _pItemInfoData;
    static PropertyInfo _pRuneNodeSave, _pRuneLevelCost, _pRuneSaveLevel;
    static PropertyInfo[] _pRuneNodeLevelInfos;
    static MethodInfo _mRuneTooltipBind, _mSubRecipeOpen, _mSubRecipeLearned;
    static MethodInfo[] _mRuneLevelInfo, _mSubRecipeActions;
    static Type _dbType;
    static Type _boxInvType;
    static PropertyInfo _pBoxInvSingleton;
    static PropertyInfo _pAccountStatus;
    static MethodInfo[] _mBoxCount;
    static MethodInfo _mBoxCountLearned;
    static MethodInfo _mAccountStatusValue;
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
            // Skip property getters/setters — GetMethods order is not a contract and
            // setters (set_*) otherwise match void(T) signatures used for bind/open.
            if (m.IsSpecialName) continue;
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

    static MethodInfo PreferMethod(string role, MethodInfo[] candidates)
    {
        if (candidates == null || candidates.Length == 0) return null;
        if (candidates.Length > 1)
            AutoSynthPlugin.Logger.LogWarning(
                $"interop resolve: {role} has {candidates.Length} matches " +
                $"([{string.Join(",", Array.ConvertAll(candidates, m => m.Name))}]); using {candidates[0].Name}");
        return candidates[0];
    }

    static PropertyInfo[] PropsOfType(Type declaring, Type propType)
    {
        var found = new List<PropertyInfo>();
        foreach (var p in declaring.GetProperties(DeclInstance))
            if (p.PropertyType == propType) found.Add(p);
        return found.ToArray();
    }

    static PropertyInfo FirstPropNamed(Type declaring, string typeName)
    {
        PropertyInfo found = null;
        var extras = new List<string>();
        foreach (var p in declaring.GetProperties(DeclInstance))
        {
            if (p.PropertyType.Name != typeName) continue;
            if (found == null) found = p;
            else extras.Add(p.Name);
        }
        if (found != null && extras.Count > 0)
            AutoSynthPlugin.Logger.LogWarning(
                $"interop resolve: {declaring.Name} has >1 {typeName} property " +
                $"({found.Name}, {string.Join(", ", extras)}); using {found.Name}");
        return found;
    }

    static PropertyInfo IntPropAt(Type declaring, int index)
    {
        var ints = new List<PropertyInfo>();
        foreach (var p in declaring.GetProperties(DeclInstance))
            if (p.PropertyType == typeof(int)) ints.Add(p);
        if (index < 0 || index >= ints.Count)
        {
            AutoSynthPlugin.Logger.LogWarning(
                $"interop resolve: {declaring.Name} int[{index}] missing " +
                $"(have {ints.Count}: [{string.Join(",", ints.ConvertAll(p => p.Name))}])");
            return null;
        }
        return ints[index];
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

    // Box inventory singleton (currently `yx`): static self-property + instance
    // Int32(EBoxType) / OpenBoxStats(EBoxType). Names reshuffle each patch.
    static void ResolveBoxInventory()
    {
        _boxInvType = null;
        _pBoxInvSingleton = null;
        _pAccountStatus = null;
        _mAccountStatusValue = null;
        _mBoxCount = Array.Empty<MethodInfo>();
        _mBoxCountLearned = null;
        Type openStats = typeof(UI_Cube).Assembly.GetType("TaskbarHero.UI.OpenBoxStats");
        Type[] types;
        try { types = typeof(UI_Cube).Assembly.GetTypes(); }
        catch (ReflectionTypeLoadException e) { types = e.Types; }
        foreach (var t in types)
        {
            if (t == null || !typeof(UnityEngine.MonoBehaviour).IsAssignableFrom(t)) continue;
            PropertyInfo self = null;
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                if (p.PropertyType == t && p.CanRead) { self = p; break; }
            }
            if (self == null) continue;
            var counts = Methods(t, typeof(int), typeof(EBoxType));
            if (counts.Length == 0) continue;
            bool hasStats = false;
            if (openStats != null)
            {
                foreach (var m in Methods(t, openStats, typeof(EBoxType)))
                { hasStats = true; break; }
            }
            if (!hasStats && counts.Length < 2) continue;
            _boxInvType = t;
            _pBoxInvSingleton = self;
            _mBoxCount = counts;
            _pAccountStatus = OnlyProp(t, typeof(AccountStatus), false);
            _mAccountStatusValue = PreferMethod(
                "AccountStatus.value(EAccountStatus)",
                Methods(typeof(AccountStatus), typeof(int), typeof(EAccountStatus)));
            return;
        }
    }

    static object BoxInventoryInstance()
    {
        Resolve();
        if (_boxInvType == null || _pBoxInvSingleton == null) return null;
        try { return _pBoxInvSingleton.GetValue(null); }
        catch { return null; }
    }

    // Current chest count for a box type, or -1 if unknown.
    // Several Int32(EBoxType) methods exist (live count vs caps / free slots).
    // Rule: among candidates in 0..500, prefer the smallest sum that is still
    // strictly positive on at least one type (live stacks beat flat caps, and
    // beat "free/remaining slots" which can read 0 when the stash is full).
    // If every candidate sums to 0, leave unlearned so callers get -1 and can
    // fall back to the StageBox click detector.
    internal static int BoxCount(EBoxType type)
    {
        try
        {
            Resolve();
            var inv = BoxInventoryInstance();
            if (inv == null || _mBoxCount == null || _mBoxCount.Length == 0) return -1;
            if (_mBoxCountLearned == null)
                _mBoxCountLearned = LearnBoxCountMethod(inv);
            if (_mBoxCountLearned == null) return -1;
            int n;
            try { n = (int)_mBoxCountLearned.Invoke(inv, new object[] { type }); }
            catch { _mBoxCountLearned = null; return -1; }
            if (n < 0 || n > 500) return -1;
            // Sticky wrong learn (e.g. free-slots accessor locked in at capacity):
            // if learned says 0 but another candidate reports >0, forget and use it.
            if (n == 0)
            {
                int alt = ProbePositiveCount(inv, type);
                if (alt > 0)
                {
                    _mBoxCountLearned = null;
                    return alt;
                }
            }
            return n;
        }
        catch { return -1; }
    }

    static int ProbePositiveCount(object inv, EBoxType type)
    {
        int best = 0;
        foreach (var m in _mBoxCount)
        {
            try
            {
                int n = (int)m.Invoke(inv, new object[] { type });
                if (n > best && n <= 500) best = n;
            }
            catch { }
        }
        return best;
    }

    static MethodInfo LearnBoxCountMethod(object inv)
    {
        var types = new[] { EBoxType.NORMAL, EBoxType.BOSS, EBoxType.ACTBOSS };
        MethodInfo best = null;
        int bestSum = int.MaxValue;
        int bestSpread = -1;
        foreach (var m in _mBoxCount)
        {
            int sum = 0;
            int min = int.MaxValue, max = int.MinValue;
            bool ok = true;
            bool anyPositive = false;
            foreach (var t in types)
            {
                try
                {
                    int n = (int)m.Invoke(inv, new object[] { t });
                    if (n < 0 || n > 500) { ok = false; break; }
                    sum += n;
                    if (n > 0) anyPositive = true;
                    if (n < min) min = n;
                    if (n > max) max = n;
                }
                catch { ok = false; break; }
            }
            if (!ok || !anyPositive) continue;
            int spread = max - min;
            if (sum < bestSum || (sum == bestSum && spread > bestSpread))
            {
                bestSum = sum;
                bestSpread = spread;
                best = m;
            }
        }
        if (best != null)
            AutoSynthPlugin.Logger.LogInfo($"interop: learned box-count method {best.Name}");
        return best;
    }

    // AccountStatus level for a flag (e.g. OpenOneTypeChestAllAtOnce). 0 / missing = locked.
    internal static int AccountStatusValue(EAccountStatus status)
    {
        try
        {
            Resolve();
            if (_pAccountStatus == null || _mAccountStatusValue == null) return -1;
            var inv = BoxInventoryInstance();
            if (inv == null) return -1;
            var acc = _pAccountStatus.GetValue(inv) as AccountStatus;
            if (acc == null) return -1;
            return (int)_mAccountStatusValue.Invoke(acc, new object[] { status });
        }
        catch { return -1; }
    }

    internal static bool HasAccountStatus(EAccountStatus status)
        => AccountStatusValue(status) > 0;

    // Rune of Opening (higher tier): InputManager fires Space / open-all-types.
    internal static bool TryInvokeOpenAllBoxes()
    {
        try
        {
            var im = Object.FindObjectOfType<TaskbarHero.InputManager>(true);
            if (im == null || im.OnOpenAllBoxKeyPressed == null) return false;
            im.OnOpenAllBoxKeyPressed.Invoke();
            return true;
        }
        catch (Exception e)
        {
            AutoSynthPlugin.Logger.LogWarning("open-all boxes invoke failed: " + e.Message);
            return false;
        }
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
        _pRuneNodeLevelInfos = PropsOfType(typeof(RuneNode), typeof(RuneLevelInfoData));
        _pRuneNodeSave = FirstPropNamed(typeof(RuneNode), "RuneSaveData");
        _pRuneLevelCost = IntPropAt(typeof(RuneLevelInfoData), 3);
        _pRuneSaveLevel = _pRuneNodeSave != null
            ? _pRuneNodeSave.PropertyType.GetProperty("Level", DeclInstance)
            : null;
        _mRuneTooltipBind = PreferMethod(
            "RuneTooltip.bind(RuneNode)", Methods(typeof(RuneTooltip), typeof(void), typeof(RuneNode)));
        _mSubRecipeActions = SubRecipeActions();
        _mSubRecipeOpen = PreferMethod(
            "ComboBoxButton.open(bool)", Methods(typeof(ComboBoxButton), typeof(void), typeof(bool)));
        _mSubRecipeLearned = null;
        _dbType = FindDbType();
        _pItemInfoData = _dbType != null ? _dbType.GetProperty("itemInfoData", DeclInstance) : null;
        _mRuneLevelInfo = Methods(_dbType, typeof(RuneLevelInfoData), typeof(int), typeof(int));
        ResolveBoxInventory();
        string runeNodeInfos = _pRuneNodeLevelInfos.Length == 0
            ? "null"
            : string.Join(",", Array.ConvertAll(_pRuneNodeLevelInfos, p => p.Name));
        AutoSynthPlugin.Logger.LogInfo(
            "interop resolved: " +
            $"ERecipeType={PName(_pRecipeType)}, innerButton={PName(_pInnerButton)}, " +
            $"isOn={PName(_pIsOn)}, cubeItemData={PName(_pCubeItemData)}, " +
            $"runeNodeInfo=[{runeNodeInfos}], runeNodeSave={PName(_pRuneNodeSave)}, " +
            $"runeCost={PName(_pRuneLevelCost)}, runeTooltipBind={MName(_mRuneTooltipBind)}, " +
            $"subRecipeOpen={MName(_mSubRecipeOpen)}, " +
            $"subRecipeActions=[{string.Join(",", Array.ConvertAll(_mSubRecipeActions, m => m.Name))}], " +
            $"itemDb={(_dbType != null ? _dbType.Name : "null")}, " +
            $"runeLevelInfo=[{string.Join(",", Array.ConvertAll(_mRuneLevelInfo, m => m.Name))}], " +
            $"boxInv={(_boxInvType != null ? _boxInvType.Name : "null")}, " +
            $"boxCount=[{string.Join(",", Array.ConvertAll(_mBoxCount ?? Array.Empty<MethodInfo>(), m => m.Name))}], " +
            $"accountStatus={PName(_pAccountStatus)}, accountValue={MName(_mAccountStatusValue)}");

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

    // RuneNode exposes two RuneLevelInfoData props (current vs next-tier style);
    // try each like the old btby ?? bgir chain.
    internal static RuneLevelInfoData RuneLevelInfoOf(RuneNode node)
    {
        Resolve();
        if (node == null || _pRuneNodeLevelInfos == null) return null;
        foreach (var prop in _pRuneNodeLevelInfos)
        {
            try
            {
                var info = prop.GetValue(node) as RuneLevelInfoData;
                if (info != null) return info;
            }
            catch { }
        }
        return null;
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
    // slots are populated. Once a call coincides with a successful populate, that
    // method is remembered so later cycles skip the shotgun fan-out.
    internal static bool TryPopulateSubRecipes(SubRecipeComboBoxButton combo, int attempt,
        out string methodName, out string error)
    {
        Resolve();
        methodName = null; error = null;
        if (combo == null || attempt < 0) return false;

        if (_mSubRecipeLearned != null)
        {
            methodName = _mSubRecipeLearned.Name;
            try
            {
                if (_mSubRecipeLearned.GetParameters().Length == 1)
                    _mSubRecipeLearned.Invoke(combo, new object[] { true });
                else
                    _mSubRecipeLearned.Invoke(combo, null);
                return true;
            }
            catch (Exception e) { error = e.GetBaseException().Message; return false; }
        }

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

    // Called by the recipe loop once the sub-recipe slots look populated, so the
    // last successful populate attempt can be reused next cycle.
    internal static void RememberSubRecipePopulate(string methodName)
    {
        if (string.IsNullOrEmpty(methodName) || _mSubRecipeLearned != null) return;
        string bare = methodName.EndsWith("(true)", StringComparison.Ordinal)
            ? methodName.Substring(0, methodName.Length - 6) : methodName;
        if (_mSubRecipeOpen != null && _mSubRecipeOpen.Name == bare)
        {
            _mSubRecipeLearned = _mSubRecipeOpen;
            AutoSynthPlugin.Logger.LogInfo($"recipe populate: learned {_mSubRecipeLearned.Name}(true)");
            return;
        }
        if (_mSubRecipeActions == null) return;
        foreach (var m in _mSubRecipeActions)
        {
            if (m.Name != bare) continue;
            _mSubRecipeLearned = m;
            AutoSynthPlugin.Logger.LogInfo($"recipe populate: learned {_mSubRecipeLearned.Name}()");
            return;
        }
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

        // Main-content order is Stash/Stat/Cube/Rune/Portal — Rune is immediately
        // right of Cube (left of Cube is Stat).
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
            if (cubeIndex >= 0 && cubeIndex + 1 < siblings.Count && siblings.Count >= 5)
                return siblings[cubeIndex + 1];
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
                    AutoSynthPlugin.Logger.LogInfo("menu resolve: selected the active tab right of Cube as Rune");
                }
                return rune;
            }
        }
        return inactiveMatch;
    }

    // Main content row (Stash/Stat/Cube/Rune/Portal) is visible only while the main
    // menu/HUD is open. Cube is enough: the whole row hides together when it closes.
    internal static bool IsMainMenuOpen()
    {
        try
        {
            var cube = FindMenuToggle("Cube");
            return cube != null && cube.gameObject.activeInHierarchy;
        }
        catch { return false; }
    }

    // Show Main's onClick walks UIManager → UI_Hero; clicking before those exist
    // throws a game-side NRE (ux+tv.isk). Wait until the graph is wired.
    internal static bool IsHudReadyForShowMain()
    {
        try
        {
            if (EventSystem.current == null) return false;
            var um = Object.FindObjectOfType<UIManager>(true);
            if (um == null || um.ui_main == null || um.Ui_Hero == null)
                return false;
            var stage = Object.FindObjectOfType<UI_Stage>(true);
            var btn = stage != null ? stage.button_ShowMain : null;
            return btn != null && btn.gameObject.activeInHierarchy;
        }
        catch { return false; }
    }

    // UI_Stage.button_ShowMain — opens the content row (Stash/Stat/Cube/Rune/Portal).
    // Game handler often NREs inside UI_Hero after chrome is already up; if the row is
    // visible afterward, treat as success and do not spam a warning.
    internal static bool TryClickShowMain()
    {
        if (!IsHudReadyForShowMain())
            return false;
        var stage = Object.FindObjectOfType<UI_Stage>(true);
        var btn = stage.button_ShowMain;
        try
        {
            var inner = InnerButton(btn);
            if (inner == null || inner.onClick == null)
            {
                AutoSynthPlugin.Logger.LogWarning("auto-open menu: Show Main has no inner onClick");
                return false;
            }
            inner.onClick.Invoke();
            AutoSynthPlugin.Logger.LogInfo("clicked Show Main (stage HUD) (+inner onClick)");
            return true;
        }
        catch (Exception)
        {
            if (IsMainMenuOpen())
            {
                AutoSynthPlugin.Logger.LogInfo("clicked Show Main (stage HUD)");
                return true;
            }
            return false;
        }
    }

    internal static ToggleButton FindShowMainButton()
    {
        try
        {
            var stage = Object.FindObjectOfType<UI_Stage>(true);
            return stage != null ? stage.button_ShowMain : null;
        }
        catch { return null; }
    }

    // Canonical ButtonBase click (pointer + inner onClick). Used by Plugin / runners.
    // Game handlers (UIManager etc.) can NRE during HUD transitions; swallow so Tick
    // keeps running — the click was still issued.
    internal static void Click(ButtonBase button, string name, bool loud)
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
        try
        {
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
        catch (Exception e)
        {
            AutoSynthPlugin.Logger.LogWarning($"{name}: game click threw ({e.GetType().Name}: {e.Message})");
        }
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
