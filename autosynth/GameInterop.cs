using System;
using System.Reflection;
using TaskbarHero;
using TaskbarHero.Data;
using TaskbarHero.UI;
using TS;
using UnityEngine;

namespace TbhAutoSynth;

// Obfuscated-member access for cube/recipe UI. Default build binds by name; the
// "-next" edition (/define:RESILIENT) resolves by signature at runtime.
internal static class GameInterop
{
#if RESILIENT
    static bool _obfResolved;
    static PropertyInfo _pRecipeType, _pInnerButton, _pIsOn, _pCubeItemData, _pItemInfoData;
    static Type _dbType;

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

    static string PName(PropertyInfo p) => p != null ? p.Name : "null";
#endif

    internal static ERecipeType RecipeTypeOf(SubRecipeComboBoxButton c)
    {
#if RESILIENT
        Resolve();
        return (ERecipeType)_pRecipeType.GetValue(c);
#else
        return c.bfxh;
#endif
    }

    internal static UnityEngine.UI.Button InnerButton(ButtonBase b)
    {
#if RESILIENT
        Resolve();
        return (UnityEngine.UI.Button)_pInnerButton.GetValue(b);
#else
        return b.bsec;
#endif
    }

    internal static bool IsOn(ToggleButton b)
    {
#if RESILIENT
        Resolve();
        return (bool)_pIsOn.GetValue(b);
#else
        return b.bseh;
#endif
    }

    internal static int CubeItemKey(CubeInData data)
    {
#if RESILIENT
        Resolve();
        var cid = (CubeItemData)_pCubeItemData.GetValue(data);
        return cid.ItemKey;
#else
        return data.bfbr.ItemKey;
#endif
    }

    internal static Il2CppSystem.Collections.Generic.List<ItemInfoData> ItemInfoList()
    {
#if RESILIENT
        Resolve();
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
}
