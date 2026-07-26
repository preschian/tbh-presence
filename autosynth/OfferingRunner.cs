using System;
using TaskbarHero;
using TaskbarHero.Data;
using TaskbarHero.UI;
using TS;
using UnityEngine;

namespace TbhAutoSynth;

// Runs the Cube's Offering recipe one coin at a time. Coins are identified from
// the game's MaterialInfoData category (EMaterialType.OFFERING), not by a
// localized name or version-specific item key.
internal sealed class OfferingRunner
{
    internal enum TickResult { InProgress, Done }

    private enum Step { Recipe, Fill, Trigger, Settle, RefreshAway, RefreshBack, Restore, Finished }

    private const int MaxRecipeAttempts = 8;
    private const int MaxSettleAttempts = 4;

    private Step _step;
    private int _recipeAttempts;
    private int _restoreAttempts;
    private int _settleAttempts;
    private int _offerings;
    private int _filledBeforeAdd = -1;
    private int _offeredPosition = -1;
    private int _coinItemKey;
    private int _coinCountBeforeOperation = -1;
    private int _refreshAttempts;
    private float _phaseEnteredAt;
    private bool _recipeSwitched;
    private UI_Hero _hero;

    internal int LastOfferings { get; private set; }

    private static float PhaseBudgetSeconds()
        => Math.Max(1, AutoSynthPlugin.MaxOfferingOperationsPerCycle)
           * (2f * AutoSynthPlugin.AfterSynthDelay + 4f) + 20f;

    internal void BeginPhase()
    {
        ClearState();
        _phaseEnteredAt = Time.unscaledTime;
    }

    internal void ResetSession()
    {
        ClearState();
        LastOfferings = 0;
    }

    private void ClearState()
    {
        _step = Step.Recipe;
        _recipeAttempts = 0;
        _restoreAttempts = 0;
        _settleAttempts = 0;
        _offerings = 0;
        _filledBeforeAdd = -1;
        _offeredPosition = -1;
        _coinItemKey = 0;
        _coinCountBeforeOperation = -1;
        _refreshAttempts = 0;
        _recipeSwitched = false;
        _hero = null;
    }

    internal TickResult Tick(UI_Cube cube, bool loud, out float nextDelay)
    {
        nextDelay = AutoSynthPlugin.AfterFillDelay;
        if (Time.unscaledTime - _phaseEnteredAt >= PhaseBudgetSeconds() && _step != Step.Restore)
        {
            AutoSynthPlugin.Logger.LogWarning(
                $"offering phase: timed out (step={_step}, operations={_offerings}) — restoring Synthesis");
            _step = Step.Restore;
        }

        try
        {
            switch (_step)
            {
                case Step.Recipe: return TickRecipe(loud, ref nextDelay);
                case Step.Fill: return TickFill(cube, ref nextDelay);
                case Step.Trigger: return TickTrigger(cube, loud, ref nextDelay);
                case Step.Settle: return TickSettle(cube, ref nextDelay);
                case Step.RefreshAway: return TickRefreshAway(ref nextDelay);
                case Step.RefreshBack: return TickRefreshBack(ref nextDelay);
                default: return TickRestore(loud, ref nextDelay);
            }
        }
        catch (Exception e)
        {
            AutoSynthPlugin.Logger.LogError($"offering tick failed: {e}");
            _step = Step.Restore;
            return TickResult.InProgress;
        }
    }

    private TickResult TickRecipe(bool loud, ref float nextDelay)
    {
        if (!GameInterop.CanReadInventoryItems || !GameInterop.CanIdentifyOfferingMaterials)
        {
            AutoSynthPlugin.Logger.LogWarning(
                "offering phase: coin data is unreadable on this game build — skipping");
            return Finish(loud);
        }
        if (!GameInterop.CanMoveItemsToCube)
        {
            AutoSynthPlugin.Logger.LogWarning(
                "offering phase: the game's MoveToCube slot action is unavailable — skipping");
            return Finish(loud);
        }

        _recipeAttempts++;
        if (_recipeAttempts == 1)
        {
            OpenRecipeDropdown();
            return TickResult.InProgress;
        }

        string detail;
        if (GameInterop.TrySelectMainRecipe(ERecipeType.OFFERING, out detail))
        {
            _recipeSwitched = true;
            AutoSynthPlugin.Logger.LogInfo("offering phase: " + detail);
            CloseRecipeDropdown();
            _step = Step.Fill;
            return TickResult.InProgress;
        }
        if (detail != null && detail.IndexOf("locked", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            AutoSynthPlugin.Logger.LogWarning("offering phase: " + detail + " — skipping");
            return Finish(loud);
        }
        if (_recipeAttempts >= MaxRecipeAttempts)
        {
            AutoSynthPlugin.Logger.LogWarning($"offering phase: {detail} — giving up for this cycle");
            GameInterop.DumpMainRecipes();
            return Finish(loud);
        }
        OpenRecipeDropdown();
        return TickResult.InProgress;
    }

    private TickResult TickFill(UI_Cube cube, ref float nextDelay)
    {
        int filled = GameInterop.CubeFilledCount(cube, out _);

        if (_filledBeforeAdd >= 0)
        {
            if (filled > _filledBeforeAdd)
            {
                _filledBeforeAdd = -1;
                _step = Step.Trigger;
                nextDelay = AutoSynthPlugin.AfterFillDelay;
                return TickResult.InProgress;
            }
            AutoSynthPlugin.Logger.LogWarning(
                $"offering: game refused coin in inventory slot {_offeredPosition} — ending phase");
            _filledBeforeAdd = -1;
            _step = Step.Restore;
            return TickResult.InProgress;
        }

        // Never process a cube item that this phase did not add itself.
        if (filled > 0)
        {
            AutoSynthPlugin.Logger.LogWarning(
                $"offering: cube already contains {filled} item(s) — leaving them alone");
            _step = Step.Restore;
            return TickResult.InProgress;
        }

        InventorySlot slot;
        ItemInfoData info;
        int position;
        if (!NextCoin(out slot, out info, out position))
        {
            if (_offerings == 0) AutoSynthPlugin.Logger.LogInfo("offering phase: no coin in inventory");
            _step = Step.Restore;
            return TickResult.InProgress;
        }

        _offeredPosition = position;
        _coinItemKey = info.ItemKey;
        // Capture this before MoveToCube: Offering keeps a stackable coin
        // selected in the Cube after processing, so an empty Cube is not a
        // reliable completion signal.
        _coinCountBeforeOperation = GameInterop.ItemCount(_coinItemKey);
        _filledBeforeAdd = filled;
        AutoSynthPlugin.Logger.LogInfo(
            $"offering: adding 1 coin '{info.NameKey}' key={info.ItemKey} from slot {position}");
        string detail;
        if (!GameInterop.TryMoveItemToCube(slot, out detail))
        {
            AutoSynthPlugin.Logger.LogWarning($"offering: slot {position} — {detail}");
            _filledBeforeAdd = -1;
            _step = Step.Restore;
        }
        return TickResult.InProgress;
    }

    private TickResult TickTrigger(UI_Cube cube, bool loud, ref float nextDelay)
    {
        int filled = GameInterop.CubeFilledCount(cube, out _);
        if (filled != 1)
        {
            AutoSynthPlugin.Logger.LogWarning(
                $"offering: expected exactly 1 coin in cube, found {filled} item(s) — ending phase");
            _step = Step.Restore;
            return TickResult.InProgress;
        }

        AutoSynthPlugin.Logger.LogInfo($"offering: running operation {_offerings + 1}");
        GameInterop.Click(cube.toggleButton_Trigger, "offering trigger", loud);
        _settleAttempts = 0;
        _step = Step.Settle;
        nextDelay = AutoSynthPlugin.AfterSynthDelay;
        return TickResult.InProgress;
    }

    private TickResult TickSettle(UI_Cube cube, ref float nextDelay)
    {
        int filled = GameInterop.CubeFilledCount(cube, out _);
        int remaining = GameInterop.ItemCount(_coinItemKey);
        bool countReadable = _coinCountBeforeOperation >= 0 && remaining >= 0;
        bool consumed = countReadable
            ? remaining < _coinCountBeforeOperation
            : filled == 0;

        if (!consumed)
        {
            _settleAttempts++;
            if (_settleAttempts < MaxSettleAttempts)
            {
                nextDelay = AutoSynthPlugin.AfterSynthDelay;
                return TickResult.InProgress;
            }
            AutoSynthPlugin.Logger.LogWarning(
                countReadable
                    ? $"offering: inventory count for key {_coinItemKey} did not decrease " +
                      $"({_coinCountBeforeOperation} -> {remaining}) — ending phase"
                    : "offering: could not confirm that the coin was consumed — ending phase");
            _step = Step.Restore;
            return TickResult.InProgress;
        }

        _offerings++;
        AutoSynthPlugin.Logger.LogInfo(
            countReadable
                ? $"offering: operation {_offerings} complete, coin count {_coinCountBeforeOperation} -> {remaining}"
                : $"offering: operation {_offerings} complete");
        _coinCountBeforeOperation = -1;
        if (_offerings >= AutoSynthPlugin.MaxOfferingOperationsPerCycle)
        {
            AutoSynthPlugin.Logger.LogInfo(
                $"offering: reached MaxOfferingOperationsPerCycle ({AutoSynthPlugin.MaxOfferingOperationsPerCycle})");
            _step = Step.Restore;
        }
        else
        {
            // The inventory slot still points at the unique item instance that
            // Offering just removed. Switching away and back forces the game's
            // inventory UI to bind the slot to the next live coin instance.
            _refreshAttempts = 0;
            _step = Step.RefreshAway;
        }
        return TickResult.InProgress;
    }

    private TickResult TickRefreshAway(ref float nextDelay)
        => TickRecipeTransition(
            ERecipeType.SYNTHESIS, Step.RefreshBack,
            "could not refresh inventory via Synthesis", false, ref nextDelay);

    private TickResult TickRefreshBack(ref float nextDelay)
        => TickRecipeTransition(
            ERecipeType.OFFERING, Step.Fill,
            "could not return to Offering after inventory refresh", true, ref nextDelay);

    private TickResult TickRecipeTransition(ERecipeType recipe, Step nextStep,
        string failure, bool inventoryRefreshed, ref float nextDelay)
    {
        _refreshAttempts++;
        if (_refreshAttempts == 1)
        {
            OpenRecipeDropdown();
            return TickResult.InProgress;
        }

        string detail;
        if (GameInterop.TrySelectMainRecipe(recipe, out detail))
        {
            CloseRecipeDropdown();
            _refreshAttempts = 0;
            _step = nextStep;
            nextDelay = AutoSynthPlugin.AfterFillDelay;
            if (inventoryRefreshed)
            {
                _offeredPosition = -1;
                _coinItemKey = 0;
                _hero = null;
                AutoSynthPlugin.Logger.LogInfo("offering: inventory refreshed for the next coin");
            }
            return TickResult.InProgress;
        }
        if (_refreshAttempts >= MaxRecipeAttempts)
        {
            AutoSynthPlugin.Logger.LogWarning($"offering: {failure} ({detail})");
            _step = Step.Restore;
            return TickResult.InProgress;
        }
        OpenRecipeDropdown();
        nextDelay = AutoSynthPlugin.AfterFillDelay;
        return TickResult.InProgress;
    }

    private TickResult TickRestore(bool loud, ref float nextDelay)
    {
        if (!_recipeSwitched) return Finish(loud);
        _restoreAttempts++;
        if (_restoreAttempts == 1)
        {
            OpenRecipeDropdown();
            return TickResult.InProgress;
        }

        string detail;
        if (GameInterop.TrySelectMainRecipe(ERecipeType.SYNTHESIS, out detail))
        {
            if (loud) AutoSynthPlugin.Logger.LogInfo("offering: cube returned to the Synthesis recipe");
        }
        else if (_restoreAttempts < MaxRecipeAttempts)
        {
            OpenRecipeDropdown();
            return TickResult.InProgress;
        }
        else
        {
            AutoSynthPlugin.Logger.LogWarning(
                "offering: could not switch the cube back to Synthesis (" + detail + ")");
        }

        CloseRecipeDropdown();
        _recipeSwitched = false;
        return Finish(loud);
    }

    private bool NextCoin(out InventorySlot slot, out ItemInfoData info, out int position)
    {
        slot = null;
        info = null;
        position = -1;
        if (_hero == null) _hero = UnityEngine.Object.FindObjectOfType<UI_Hero>(true);
        var slots = _hero != null ? _hero.inventorySlots : null;
        if (slots == null) return false;

        for (int i = 0; i < slots.Count; i++)
        {
            var candidate = slots[i];
            if (candidate == null || !candidate.gameObject.activeInHierarchy) continue;
            var item = GameInterop.InventoryItemInfo(candidate);
            if (item == null || item.ITEMTYPE != EItemType.MATERIAL) continue;
            if (!GameInterop.IsOfferingMaterial(item.ItemKey)) continue;
            try
            {
                if (candidate.Go_ManualLock != null && candidate.Go_ManualLock.activeInHierarchy) continue;
                if (candidate.m_reservedIcon != null && candidate.m_reservedIcon.activeInHierarchy) continue;
            }
            catch { continue; }
            slot = candidate;
            info = item;
            position = i;
            return true;
        }
        return false;
    }

    private static ComboBoxButton MainRecipeCombo()
    {
        var cube = UnityEngine.Object.FindObjectOfType<UI_Cube>(true);
        return cube != null ? cube.m_mainRecipeToggleBtn : null;
    }

    private static void OpenRecipeDropdown()
        => GameInterop.OpenComboBox(MainRecipeCombo(), "main recipe dropdown (open)");

    private static void CloseRecipeDropdown()
        => GameInterop.CloseComboBox(MainRecipeCombo(), "main recipe dropdown (close)");

    private TickResult Finish(bool loud)
    {
        LastOfferings = _offerings;
        _step = Step.Finished;
        if (loud || _offerings > 0)
            AutoSynthPlugin.Logger.LogInfo($"offering phase done: {_offerings} operation(s)");
        return TickResult.Done;
    }

    internal void Dump()
    {
        try
        {
            int coins = 0;
            var itemKeys = new System.Collections.Generic.HashSet<int>();
            if (_hero == null) _hero = UnityEngine.Object.FindObjectOfType<UI_Hero>(true);
            var slots = _hero != null ? _hero.inventorySlots : null;
            if (slots != null)
            {
                for (int i = 0; i < slots.Count; i++)
                {
                    var info = GameInterop.InventoryItemInfo(slots[i]);
                    if (info == null || !GameInterop.IsOfferingMaterial(info.ItemKey)
                        || !itemKeys.Add(info.ItemKey)) continue;
                    int count = GameInterop.ItemCount(info.ItemKey);
                    if (count > 0) coins += count;
                }
            }
            AutoSynthPlugin.Logger.LogInfo(
                $"dump: autoOffering={AutoSynthPlugin.AutoOffering} " +
                $"maxOperations={AutoSynthPlugin.MaxOfferingOperationsPerCycle} " +
                $"offeringCoins={coins} coinTypes={itemKeys.Count} " +
                $"materialDataReadable={GameInterop.CanIdentifyOfferingMaterials}");
        }
        catch (Exception e)
        {
            AutoSynthPlugin.Logger.LogError($"dump offering failed: {e}");
        }
    }
}
