using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using TaskbarHero;
using TaskbarHero.Data;
using TaskbarHero.StatusSystem;
using TaskbarHero.UI;
using TaskbarHero.UI.Rune;
using TS;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
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

    static int _tabAttempt;
    static Type _shortcutMgrType;
    static PropertyInfo _shortcutListProp;
    static PropertyInfo _shortcutBindProp;
    static PropertyInfo[] _shortcutActionProps;
    static bool _shortcutResolveTried;

    // Main content row (Stash/Stat/Cube/Rune/Portal) is visible only while the Tab
    // menu/HUD is open. Cube is enough: the whole row hides together when Tab closes it.
    internal static bool IsMainMenuOpen()
    {
        try
        {
            var cube = FindMenuToggle("Cube");
            return cube != null && cube.gameObject.activeInHierarchy;
        }
        catch { return false; }
    }

    // Open the Tab menu/HUD when the content-row buttons are inactive.
    // Order: Tab shortcut Action → UIManager show(ui_main) → Win32 Tab (with focus).
    internal static bool OpenMainMenu()
    {
        try
        {
            if (IsMainMenuOpen()) return true;

            if (TryInvokeTabShortcut() && IsMainMenuOpen())
                return true;

            // UI_Main is not a UI_Base; activate the chrome directly if still closed.
            var uim = Object.FindObjectOfType<UIManager>(true);
            if (uim != null)
            {
                bool activated = false;
                if (uim.ui_main != null && !uim.ui_main.gameObject.activeInHierarchy)
                {
                    uim.ui_main.gameObject.SetActive(true);
                    activated = true;
                }
                if (uim.canvas_Main != null && !uim.canvas_Main.gameObject.activeInHierarchy)
                {
                    uim.canvas_Main.gameObject.SetActive(true);
                    activated = true;
                }
                if (activated)
                {
                    AutoSynthPlugin.Logger.LogInfo("auto-open menu: activated UI_Main/canvas_Main");
                    if (IsMainMenuOpen()) return true;
                }
            }

            if (TapTabKeybd() && IsMainMenuOpen())
                return true;

            _tabAttempt++;
            if (_tabAttempt % 2 == 0 && TapTabInputSystem() && IsMainMenuOpen())
                return true;

            return IsMainMenuOpen();
        }
        catch (Exception e)
        {
            AutoSynthPlugin.Logger.LogWarning("auto-open menu failed: " + e.Message);
            return TapTabKeybd();
        }
    }

    // Show a panel through UIManager's void(UI_Base) helpers (hhb/… — names obfuscated).
    // Used for Tab menu (ui_main) and for opening Cube/Rune when the menu button is hidden.
    internal static bool TryShowUiPanel(UI_Base panel)
    {
        var uim = Object.FindObjectOfType<UIManager>(true);
        return uim != null && TryShowUiPanel(uim, panel);
    }

    static bool TryShowUiPanel(UIManager uim, UI_Base panel)
    {
        if (uim == null || panel == null) return false;
        if (panel.gameObject.activeInHierarchy) return true;

        foreach (var m in typeof(UIManager).GetMethods(DeclInstance))
        {
            if (m.IsSpecialName || m.ReturnType != typeof(void)) continue;
            var ps = m.GetParameters();
            if (ps.Length != 1 || ps[0].ParameterType != typeof(UI_Base)) continue;
            try
            {
                m.Invoke(uim, new object[] { panel });
                AutoSynthPlugin.Logger.LogInfo(
                    $"auto-open menu: UIManager.{m.Name}({panel.GetIl2CppType().Name})");
                if (panel.gameObject.activeInHierarchy) return true;
            }
            catch (Exception e)
            {
                AutoSynthPlugin.Logger.LogWarning(
                    $"auto-open menu: UIManager.{m.Name} failed: " +
                    (e.InnerException ?? e).Message);
            }
        }

        if (!panel.gameObject.activeInHierarchy)
        {
            panel.gameObject.SetActive(true);
            AutoSynthPlugin.Logger.LogInfo(
                $"auto-open menu: SetActive({panel.GetIl2CppType().Name})");
        }
        return panel.gameObject.activeInHierarchy;
    }

    internal static UI_Cube FindCubeUi()
    {
        try
        {
            var uim = Object.FindObjectOfType<UIManager>(true);
            if (uim != null && uim.Ui_Cube != null) return uim.Ui_Cube;
        }
        catch { /* fall through */ }
        return Object.FindObjectOfType<UI_Cube>(true);
    }

    // Find the MonoBehaviour shortcut table (obfuscated type) that holds
    // List<entry> where entry has ShortcutBinding + Action callbacks; fire Tab.
    static bool TryInvokeTabShortcut()
    {
        try
        {
            ResolveShortcutManager();
            if (_shortcutMgrType == null || _shortcutListProp == null || _shortcutBindProp == null)
                return false;

            object mgr = GetShortcutManagerInstance();
            if (mgr == null) return false;

            var listObj = _shortcutListProp.GetValue(mgr);
            if (listObj == null) return false;

            // Il2Cpp List: Count + get_Item(int)
            var listType = listObj.GetType();
            var countProp = listType.GetProperty("Count");
            var itemGetter = listType.GetMethod("get_Item", new[] { typeof(int) });
            if (countProp == null || itemGetter == null) return false;
            int count = (int)countProp.GetValue(listObj);
            Type entryType = _shortcutBindProp.DeclaringType;
            for (int i = 0; i < count; i++)
            {
                var entry = itemGetter.Invoke(listObj, new object[] { i });
                if (entry == null) continue;
                if (entry is Il2CppObjectBase iob && entryType != null)
                {
                    try
                    {
                        var cast = typeof(Il2CppObjectBase).GetMethod("Cast", Type.EmptyTypes)
                            ?.MakeGenericMethod(entryType);
                        if (cast != null) entry = cast.Invoke(iob, null);
                    }
                    catch { /* keep raw entry */ }
                }

                object bindingObj;
                try { bindingObj = _shortcutBindProp.GetValue(entry); }
                catch { continue; }
                if (bindingObj == null) continue;
                var binding = (ShortcutBinding)bindingObj;
                if (binding.m_mainKey != KeyCode.Tab) continue;

                // Only the first working Action — the entry has several callbacks and
                // firing all of them NREs inside UI_Hero during partial UI states.
                if (_shortcutActionProps != null)
                {
                    foreach (var ap in _shortcutActionProps)
                    {
                        object act;
                        try { act = ap.GetValue(entry); }
                        catch { continue; }
                        if (act == null) continue;
                        try
                        {
                            if (act is Il2CppSystem.Action il2) il2.Invoke();
                            else
                            {
                                var invoke = act.GetType().GetMethod("Invoke", Type.EmptyTypes);
                                if (invoke == null) continue;
                                invoke.Invoke(act, null);
                            }
                            AutoSynthPlugin.Logger.LogInfo(
                                $"auto-open menu: invoked Tab shortcut action ({ap.Name})");
                            return true;
                        }
                        catch (Exception ex)
                        {
                            AutoSynthPlugin.Logger.LogWarning(
                                $"auto-open menu: Tab action {ap.Name} failed: " +
                                (ex.InnerException ?? ex).Message);
                        }
                    }
                }
            }
        }
        catch (Exception e)
        {
            AutoSynthPlugin.Logger.LogWarning("auto-open menu: Tab shortcut invoke failed: " + e.Message);
        }
        return false;
    }

    static object GetShortcutManagerInstance()
    {
        // Prefer the nr<T> singleton static Instance props (names obfuscated).
        for (var bt = _shortcutMgrType.BaseType; bt != null; bt = bt.BaseType)
        {
            foreach (var p in bt.GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (p.PropertyType != _shortcutMgrType) continue;
                try
                {
                    var inst = p.GetValue(null);
                    if (inst != null) return CastToType(inst, _shortcutMgrType);
                }
                catch { /* try next */ }
            }
        }

        var found = Object.FindObjectOfType(Il2CppType.From(_shortcutMgrType), true);
        return found == null ? null : CastToType(found, _shortcutMgrType);
    }

    static object CastToType(object obj, Type target)
    {
        if (obj == null || target == null) return obj;
        if (target.IsInstanceOfType(obj)) return obj;
        if (obj is Il2CppObjectBase iob)
        {
            try
            {
                var cast = typeof(Il2CppObjectBase).GetMethod("Cast", Type.EmptyTypes)
                    ?.MakeGenericMethod(target);
                if (cast != null) return cast.Invoke(iob, null);
            }
            catch { /* fall through */ }
        }
        return obj;
    }

    static void ResolveShortcutManager()
    {
        if (_shortcutResolveTried) return;
        _shortcutResolveTried = true;
        try
        {
            foreach (var t in typeof(UIManager).Assembly.GetTypes())
            {
                if (t == null || !typeof(MonoBehaviour).IsAssignableFrom(t)) continue;
                PropertyInfo listProp = null;
                PropertyInfo bindProp = null;
                Type entryType = null;
                foreach (var p in t.GetProperties(DeclInstance))
                {
                    if (!p.PropertyType.IsGenericType) continue;
                    var args = p.PropertyType.GetGenericArguments();
                    if (args.Length != 1) continue;
                    var bp = args[0].GetProperty("bfls", DeclInstance)
                             ?? FindPropOfType(args[0], typeof(ShortcutBinding));
                    if (bp == null) continue;
                    listProp = p;
                    bindProp = bp;
                    entryType = args[0];
                    break;
                }
                if (listProp == null || entryType == null) continue;

                var actions = new List<PropertyInfo>();
                foreach (var p in entryType.GetProperties(DeclInstance))
                {
                    // Il2CppSystem.Action or System.Action — name ends with Action / is multicast delegate
                    if (p.PropertyType.Name == "Action" || p.PropertyType.Name.StartsWith("Action`", StringComparison.Ordinal))
                        actions.Add(p);
                }
                if (actions.Count == 0) continue;

                _shortcutMgrType = t;
                _shortcutListProp = listProp;
                _shortcutBindProp = bindProp;
                _shortcutActionProps = actions.ToArray();
                AutoSynthPlugin.Logger.LogInfo(
                    $"auto-open menu: shortcut manager={t.Name}, list={listProp.Name}, actions={actions.Count}");
                return;
            }
        }
        catch (Exception e)
        {
            AutoSynthPlugin.Logger.LogWarning("auto-open menu: shortcut resolve failed: " + e.Message);
        }
    }

    static PropertyInfo FindPropOfType(Type declaring, Type propType)
    {
        foreach (var p in declaring.GetProperties(DeclInstance))
            if (p.PropertyType == propType) return p;
        return null;
    }

    static bool TapTabKeybd()
    {
        try
        {
            FocusGameWindow();
            keybd_event(VkTab, 0, 0, UIntPtr.Zero);
            keybd_event(VkTab, 0, KeyeventfKeyup, UIntPtr.Zero);
            AutoSynthPlugin.Logger.LogInfo("auto-open menu: pressed Tab (keybd_event)");
            return true;
        }
        catch (Exception e)
        {
            AutoSynthPlugin.Logger.LogWarning("auto-open menu: keybd_event Tab failed: " + e.Message);
            return false;
        }
    }

    static void FocusGameWindow()
    {
        try
        {
            var proc = Process.GetCurrentProcess();
            if (proc.MainWindowHandle != IntPtr.Zero)
            {
                ShowWindow(proc.MainWindowHandle, SwRestore);
                SetForegroundWindow(proc.MainWindowHandle);
            }
        }
        catch { /* best-effort */ }
    }

    static bool TapTabInputSystem()
    {
        try
        {
            var kb = Keyboard.current;
            if (kb == null) return false;
            InputSystem.QueueStateEvent(kb, new KeyboardState(Key.Tab));
            InputSystem.Update();
            InputSystem.QueueStateEvent(kb, new KeyboardState());
            InputSystem.Update();
            AutoSynthPlugin.Logger.LogInfo("auto-open menu: pressed Tab (Input System)");
            return true;
        }
        catch (Exception e)
        {
            AutoSynthPlugin.Logger.LogWarning("auto-open menu: Input System Tab failed: " + e.Message);
            return false;
        }
    }

    const byte VkTab = 0x09;
    const uint KeyeventfKeyup = 0x0002;
    const int SwRestore = 9;

    [DllImport("user32.dll")]
    static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

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
