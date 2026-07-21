using System;
using System.Collections.Generic;
using System.Reflection;
using TaskbarHero;
using TaskbarHero.Data;
using TaskbarHero.UI;
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
    static MethodInfo[] _mRuneLevelInfo;
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
        _dbType = FindDbType();
        _pItemInfoData = _dbType != null ? _dbType.GetProperty("itemInfoData", DeclInstance) : null;
        _mRuneLevelInfo = Methods(_dbType, typeof(RuneLevelInfoData), typeof(int), typeof(int));
        AutoSynthPlugin.Logger.LogInfo(
            "interop resolved: " +
            $"ERecipeType={PName(_pRecipeType)}, innerButton={PName(_pInnerButton)}, " +
            $"isOn={PName(_pIsOn)}, cubeItemData={PName(_pCubeItemData)}, " +
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
