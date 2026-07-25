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
    static PropertyInfo _pRecipeSlotType, _pInvSlotItem, _pInvItemInfo;
    static PropertyInfo _pCubeSourceType, _pCubeUniqueId;
    static Type _tSlotRef;
    static MethodInfo _mExecuteSlotAction, _mBuildSlotContext, _mDestinationOfAction;
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
        _pRecipeSlotType = OnlyProp(typeof(MainRecipeSlotButton), typeof(ERecipeType), true);
        _pCubeSourceType = OnlyProp(typeof(CubeInData), typeof(ECubeDataType), true);
        _pCubeUniqueId = OnlyProp(typeof(CubeInData), typeof(ulong), true);
        ResolveInventoryItem();
        ResolveSlotInteraction();
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
            $"accountStatus={PName(_pAccountStatus)}, accountValue={MName(_mAccountStatusValue)}, " +
            $"recipeSlotType={PName(_pRecipeSlotType)}, invSlotItem={PName(_pInvSlotItem)}, " +
            $"invItemInfo={PName(_pInvItemInfo)}, " +
            $"cubeSourceType={PName(_pCubeSourceType)}, cubeUniqueId={PName(_pCubeUniqueId)}, " +
            $"slotRef={(_tSlotRef != null ? _tSlotRef.Name : "null")}, " +
            $"execSlotAction={MName(_mExecuteSlotAction)}, buildSlotCtx={MName(_mBuildSlotContext)}, " +
            $"actionDest={MName(_mDestinationOfAction)}");

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

    // InventorySlot holds the live item in one obfuscated-type property; that type is
    // the only one of the slot's properties exposing a read-only ItemInfoData. Both
    // hops are found by shape, so a patch that renames them costs nothing.
    static void ResolveInventoryItem()
    {
        _pInvSlotItem = null;
        _pInvItemInfo = null;
        foreach (var p in typeof(InventorySlot).GetProperties(DeclInstance))
        {
            var holder = p.PropertyType;
            if (holder == null || holder.IsValueType || holder == typeof(string)) continue;
            PropertyInfo info = null;
            foreach (var q in holder.GetProperties(DeclInstance))
            {
                if (q.PropertyType != typeof(ItemInfoData) || q.CanWrite) continue;
                if (info != null) { info = null; break; } // ambiguous, not this one
                info = q;
            }
            if (info == null) continue;
            _pInvSlotItem = p;
            _pInvItemInfo = info;
            return;
        }
        AutoSynthPlugin.Logger.LogWarning(
            "interop resolve: no InventorySlot property leading to ItemInfoData; alchemy is disabled");
    }

    // The item shown in an inventory slot, or null when the slot is empty.
    internal static ItemInfoData InventoryItemInfo(InventorySlot slot)
    {
        Resolve();
        if (slot == null || _pInvSlotItem == null || _pInvItemInfo == null) return null;
        try
        {
            var item = _pInvSlotItem.GetValue(slot);
            return item != null ? _pInvItemInfo.GetValue(item) as ItemInfoData : null;
        }
        catch { return null; }
    }

    internal static bool CanReadInventoryItems => _pInvSlotItem != null && _pInvItemInfo != null;

    internal static ERecipeType MainRecipeTypeOf(MainRecipeSlotButton slot)
    {
        Resolve();
        if (slot == null || _pRecipeSlotType == null) return ERecipeType.NONE;
        try { return (ERecipeType)_pRecipeSlotType.GetValue(slot); }
        catch { return ERecipeType.NONE; }
    }

    static MainRecipeSlotButton FindMainRecipeEntry(ERecipeType type, out int total)
    {
        var slots = Object.FindObjectsOfType<MainRecipeSlotButton>(true);
        total = slots.Length;
        foreach (var slot in slots)
        {
            if (slot == null || MainRecipeTypeOf(slot) != type) continue;
            return slot;
        }
        return null;
    }

    // Picks a recipe (Alchemy / Synthesis / ...) from the Cube's main recipe dropdown.
    // Returns true only once the entry reports itself selected, because clicking an
    // entry the dropdown has not initialised yet silently does nothing — callers keep
    // ticking until this confirms, so a no-op click is never mistaken for a switch.
    internal static bool TrySelectMainRecipe(ERecipeType type, out string detail)
    {
        detail = null;
        try
        {
            Resolve();
            int total;
            var target = FindMainRecipeEntry(type, out total);
            if (target == null)
            {
                detail = $"no main recipe entry for {type} yet ({total} entr(ies) in scene)";
                return false;
            }
            if (target.m_isLocked)
            {
                detail = $"the {type} recipe is locked in-game";
                return false;
            }
            if (target.m_isSelected)
            {
                detail = $"the {type} recipe is selected";
                return true;
            }
            var button = target.m_clickButton;
            if (button == null || button.onClick == null)
            {
                detail = $"the {type} recipe entry has no click button";
                return false;
            }
            button.onClick.Invoke();
            detail = $"clicked the {type} recipe entry, waiting for it to take";
            return false;
        }
        catch (Exception e)
        {
            detail = "recipe select threw: " + e.Message;
            return false;
        }
    }

    // Lists what actually sits on an inventory slot, so a click that goes nowhere can
    // be traced to the component that really handles it instead of being guessed at.
    internal static void DumpSlotComponents(Component slot)
    {
        try
        {
            if (slot == null) return;
            Transform t = slot.transform;
            for (int depth = 0; t != null && depth < 3; t = t.parent, depth++)
            {
                var names = new List<string>();
                foreach (var c in t.GetComponents<Component>())
                {
                    if (c == null) { names.Add("null"); continue; }
                    try { names.Add(c.GetIl2CppType().Name); }
                    catch { names.Add("?"); }
                }
                AutoSynthPlugin.Logger.LogInfo(
                    $"dump: slot ancestor {depth} '{t.name}' -> [{string.Join(",", names)}]");
            }
        }
        catch (Exception e)
        {
            AutoSynthPlugin.Logger.LogWarning("dump slot components failed: " + e.Message);
        }
    }

    internal static void DumpMainRecipes()
    {
        try
        {
            Resolve();
            var slots = Object.FindObjectsOfType<MainRecipeSlotButton>(true);
            AutoSynthPlugin.Logger.LogInfo($"dump: {slots.Length} main recipe entr(ies)");
            foreach (var slot in slots)
            {
                if (slot == null) continue;
                var text = slot.m_text != null ? slot.m_text.text : "(no text)";
                AutoSynthPlugin.Logger.LogInfo(
                    $"dump: recipe {MainRecipeTypeOf(slot)} '{text}' locked={slot.m_isLocked} " +
                    $"selected={slot.m_isSelected} active={slot.gameObject.activeInHierarchy}");
            }
        }
        catch (Exception e)
        {
            AutoSynthPlugin.Logger.LogWarning("dump main recipes failed: " + e.Message);
        }
    }

    // Leaves no dropdown hanging over the inventory after a recipe switch.
    internal static void CloseComboBox(ComboBoxButton combo, string name)
    {
        SetComboBox(combo, name, false);
    }

    // A dropdown entry only reacts once its list has been opened at least once.
    internal static void OpenComboBox(ComboBoxButton combo, string name)
    {
        SetComboBox(combo, name, true);
    }

    static void SetComboBox(ComboBoxButton combo, string name, bool open)
    {
        try
        {
            var dropdown = combo != null ? combo.m_comboBoxObject : null;
            if (dropdown == null || dropdown.activeInHierarchy == open) return;
            Click(combo, name, false);
        }
        catch { }
    }

    // Whether a cube slot holds anything. ItemKey alone is not enough: a stackable
    // material carries one, but a piece of gear — what alchemy consumes — is a unique
    // instance identified by its id, so the slot's source type is the real signal.
    internal static bool CubeSlotOccupied(CubeInData data)
    {
        if (data == null) return false;
        Resolve();
        if (_pCubeSourceType != null)
        {
            try { return (ECubeDataType)_pCubeSourceType.GetValue(data) != ECubeDataType.None; }
            catch { }
        }
        try { if (CubeItemKey(data) > 0) return true; }
        catch { }
        if (_pCubeUniqueId != null)
        {
            try { return (ulong)_pCubeUniqueId.GetValue(data) != 0UL; }
            catch { }
        }
        return false;
    }

    // How many cube slots currently hold an item, and how many slots exist.
    internal static int CubeFilledCount(UI_Cube cube, out int slotCount)
    {
        slotCount = 0;
        if (cube == null) return 0;
        var setter = cube.m_cubeSlotSetter;
        var slots = setter != null ? setter.m_cubeInventorySlots : null;
        if (slots == null) return 0;
        slotCount = slots.Count;
        int filled = 0;
        for (int i = 0; i < slots.Count; i++)
        {
            var data = slots[i] != null ? slots[i]._cubeData : null;
            if (CubeSlotOccupied(data)) filled++;
        }
        return filled;
    }

    // The game routes every slot interaction through SlotInteractionManager: a context
    // describing what was clicked, and a SlotActionResult saying what to do with it.
    // Driving that directly beats faking pointer events — ItemSlot's own click hooks
    // start a drag that a synthetic release never ends, leaving the player holding the
    // item. SlotActionContext/SlotActionResult/ESlotAction keep their real names, so
    // only the three manager methods have to be found by signature.
    static void ResolveSlotInteraction()
    {
        _tSlotRef = null;
        _mExecuteSlotAction = null;
        _mBuildSlotContext = null;
        _mDestinationOfAction = null;
        foreach (var m in typeof(SlotInteractionManager).GetMethods(DeclInstance))
        {
            if (m.IsSpecialName) continue;
            var ps = m.GetParameters();
            if (m.ReturnType == typeof(void) && ps.Length == 3
                && ps[0].ParameterType == typeof(SlotActionResult)
                && ps[2].ParameterType == typeof(SlotActionContext))
            {
                _mExecuteSlotAction = m;
                _tSlotRef = ps[1].ParameterType;
            }
            else if (m.ReturnType == typeof(SlotActionContext) && ps.Length == 3
                && ps[1].ParameterType == typeof(bool) && ps[2].ParameterType == typeof(ulong))
            {
                _mBuildSlotContext = m;
            }
            else if (m.ReturnType == typeof(ESlotType) && ps.Length == 1
                && ps[0].ParameterType == typeof(ESlotAction))
            {
                _mDestinationOfAction = m;
            }
        }
    }

    internal static bool CanMoveItemsToCube
        => _mExecuteSlotAction != null && _tSlotRef != null;

    // Hands the slot to the game as the interface type its own code passes around.
    static object SlotRef(Component slot)
    {
        if (_tSlotRef == null || slot == null) return null;
        return Activator.CreateInstance(_tSlotRef, new object[] { slot.Pointer });
    }

    // Moves one inventory item into the open cube by running the game's own
    // MoveToCube slot action. No pointer events, so no drag can be left dangling.
    internal static bool TryMoveItemToCube(InventorySlot slot, out string detail)
    {
        detail = null;
        try
        {
            Resolve();
            if (!CanMoveItemsToCube)
            {
                detail = "the slot action API is missing on this game build";
                return false;
            }
            if (slot == null || !slot.gameObject.activeInHierarchy)
            {
                detail = "slot is null or inactive";
                return false;
            }
            var manager = Object.FindObjectOfType<SlotInteractionManager>(true);
            if (manager == null)
            {
                detail = "SlotInteractionManager is not in the scene";
                return false;
            }

            var slotRef = SlotRef(slot);
            if (slotRef == null)
            {
                detail = "could not wrap the slot";
                return false;
            }

            // Let the game build the context so Item / SlotType / SlotIndex are filled
            // the way its own handlers do, then mark it as the right click.
            SlotActionContext context;
            if (_mBuildSlotContext != null)
                context = (SlotActionContext)_mBuildSlotContext.Invoke(
                    manager, new object[] { slotRef, false, 0UL });
            else
            {
                context = new SlotActionContext();
                context.SlotType = ESlotType.INVENTORY;
                context.SlotIndex = slot.index;
            }
            context.IsRightClick = true;
            context.IsJustHover = false;

            var destination = _mDestinationOfAction != null
                ? (ESlotType)_mDestinationOfAction.Invoke(manager, new object[] { ESlotAction.MoveToCube })
                : ESlotType.CUBEINVENTORY;

            var result = new SlotActionResult
            {
                Action = ESlotAction.MoveToCube,
                DestinationSlotType = destination,
                AllowTabSwitch = false,
            };

            _mExecuteSlotAction.Invoke(manager, new object[] { result, slotRef, context });
            detail = $"MoveToCube -> {destination}";
            return true;
        }
        catch (Exception e)
        {
            var baseEx = e.GetBaseException();
            detail = $"MoveToCube threw ({baseEx.GetType().Name}: {baseEx.Message})";
            return false;
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
