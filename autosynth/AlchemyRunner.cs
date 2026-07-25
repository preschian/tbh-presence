using System;
using System.Collections.Generic;
using TaskbarHero;
using TaskbarHero.Data;
using TaskbarHero.UI;
using UnityEngine;

namespace TbhAutoSynth;

// Owns the Alchemy phase: pick the Alchemy recipe on the Cube's main dropdown,
// left-click low-level inventory items into the cube, run the alchemy, repeat for
// as long as eligible items remain, then hand the cube back on the Synthesis
// recipe so the Cube phase finds it the way it left it.
//
// Eligibility is deliberately narrow: gear only, unlocked, item level strictly
// below AlchemyLevelThreshold, grade at or below MaxAlchemyGrade, and not in
// AlchemyProtectedItemKeys. Everything else is skipped, and the game still gets
// the final say — an item it refuses simply never lands in a cube slot.
internal sealed class AlchemyRunner
{
    internal enum TickResult { InProgress, Done }

    private enum Step { Recipe, Fill, Trigger, Confirm, Settle, Restore, Finished }

    // The in-game alchemy operation takes at most nine items at once.
    private const int MaxItemsPerBatch = 9;
    private const int MaxRecipeAttempts = 8;
    private const int MaxConfirmAttempts = 4;
    // Consecutive clicks that failed to land in a cube slot before the batch is cut.
    private const int MaxRejects = 5;

    private Step _step;
    private int _recipeAttempts;
    private int _restoreAttempts;
    private int _confirmAttempts;
    private int _batches;
    private int _rejects;
    private int _clickedThisBatch;
    private int _alchemizedThisCycle;
    private int _filledBeforeClick = -1;
    private int _pendingSlot = -1;
    private InventorySlot _pendingSlotObject;
    private bool _dumpedSlot;
    private float _phaseEnteredAt;
    private UI_Hero _hero;
    private bool _recipeSwitched;
    // Inventory position -> clicks spent on it this batch. A slot is retired once it
    // lands in the cube or every click strategy has been tried on it, so the probe
    // gets a shot with each strategy before an item is written off as refused.
    private readonly Dictionary<int, int> _slotTries = new Dictionary<int, int>();

    // MoveToCube is a toggle, so a second attempt on the same item would pull it back
    // out of the cube. One attempt each, and the game gets the final say.
    private const int MaxTriesPerSlot = 1;

    internal int LastAlchemized { get; private set; }

    private static float PhaseBudgetSeconds()
    {
        float perClick = Math.Max(0.2f, AutoSynthPlugin.AfterAlchemyClickDelay);
        float perBatch = MaxItemsPerBatch * perClick + 2f * AutoSynthPlugin.AfterSynthDelay + 6f;
        return AutoSynthPlugin.MaxAlchemyBatchesPerCycle * perBatch + 20f;
    }

    internal void BeginPhase()
    {
        ClearState();
        _phaseEnteredAt = Time.unscaledTime;
    }

    internal void ResetSession()
    {
        ClearState();
        LastAlchemized = 0;
        _hero = null;
    }

    private void ClearState()
    {
        _step = Step.Recipe;
        _recipeAttempts = 0;
        _restoreAttempts = 0;
        _confirmAttempts = 0;
        _batches = 0;
        _alchemizedThisCycle = 0;
        _dumpedSlot = false;
        _recipeSwitched = false;
        _hero = null;
        BeginBatch();
    }

    private void BeginBatch()
    {
        _rejects = 0;
        _clickedThisBatch = 0;
        _filledBeforeClick = -1;
        _pendingSlot = -1;
        _pendingSlotObject = null;
        _slotTries.Clear();
    }

    internal TickResult Tick(UI_Cube cube, bool loud, out float nextDelay)
    {
        nextDelay = Math.Max(0.2f, AutoSynthPlugin.AfterAlchemyClickDelay);

        if (Time.unscaledTime - _phaseEnteredAt >= PhaseBudgetSeconds() && _step != Step.Restore)
        {
            AutoSynthPlugin.Logger.LogWarning(
                $"alchemy phase: timed out (step={_step}, batches={_batches}) — restoring the Synthesis recipe");
            _step = Step.Restore;
        }

        try
        {
            switch (_step)
            {
                case Step.Recipe: return TickRecipe(loud, ref nextDelay);
                case Step.Fill: return TickFill(cube, loud, ref nextDelay);
                case Step.Trigger: return TickTrigger(cube, loud, ref nextDelay);
                case Step.Confirm: return TickConfirm(cube, loud, ref nextDelay);
                case Step.Settle: return TickSettle(cube, loud, ref nextDelay);
                default: return TickRestore(loud, ref nextDelay);
            }
        }
        catch (Exception e)
        {
            AutoSynthPlugin.Logger.LogError($"alchemy tick failed: {e}");
            _step = Step.Restore;
            return TickResult.InProgress;
        }
    }

    private TickResult TickRecipe(bool loud, ref float nextDelay)
    {
        if (AutoSynthPlugin.AlchemyLevelThreshold <= 0)
        {
            AutoSynthPlugin.Logger.LogWarning(
                "alchemy phase: AlchemyLevelThreshold is 0, so no item is eligible — skipping " +
                "(set it to the level below which items should be melted, e.g. 80)");
            return Finish(loud);
        }
        if (!GameInterop.CanReadInventoryItems)
        {
            AutoSynthPlugin.Logger.LogWarning(
                "alchemy phase: inventory item data is unreadable on this game build — skipping");
            return Finish(loud);
        }
        if (!GameInterop.CanMoveItemsToCube)
        {
            AutoSynthPlugin.Logger.LogWarning(
                "alchemy phase: the game's MoveToCube slot action could not be resolved — skipping");
            return Finish(loud);
        }

        _recipeAttempts++;
        if (_recipeAttempts == 1)
        {
            OpenRecipeDropdown();
            nextDelay = AutoSynthPlugin.AfterFillDelay;
            return TickResult.InProgress;
        }

        string detail;
        if (GameInterop.TrySelectMainRecipe(ERecipeType.ALCHEMY, out detail))
        {
            _recipeSwitched = true;
            AutoSynthPlugin.Logger.LogInfo("alchemy phase: " + detail);
            CloseRecipeDropdown();
            _step = Step.Fill;
            nextDelay = AutoSynthPlugin.AfterFillDelay;
            return TickResult.InProgress;
        }

        if (detail != null && detail.IndexOf("locked", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            AutoSynthPlugin.Logger.LogWarning("alchemy phase: " + detail + " — skipping");
            return Finish(loud);
        }
        if (_recipeAttempts >= MaxRecipeAttempts)
        {
            AutoSynthPlugin.Logger.LogWarning($"alchemy phase: {detail} — giving up for this cycle");
            GameInterop.DumpMainRecipes();
            return Finish(loud);
        }
        AutoSynthPlugin.Logger.LogInfo($"alchemy phase: {detail}");
        OpenRecipeDropdown();
        nextDelay = AutoSynthPlugin.AfterFillDelay;
        return TickResult.InProgress;
    }

    private TickResult TickFill(UI_Cube cube, bool loud, ref float nextDelay)
    {
        int slotCount;
        int filled = GameInterop.CubeFilledCount(cube, out slotCount);
        int capacity = slotCount > 0 ? Math.Min(slotCount, MaxItemsPerBatch) : MaxItemsPerBatch;

        // Did the previous click actually land? The answer also tells GameInterop
        // whether it is invoking the right ItemSlot click handler.
        if (_filledBeforeClick >= 0)
        {
            if (filled > _filledBeforeClick)
                _rejects = 0;
            else
            {
                _rejects++;
                // First miss of the phase: record what the slot is actually built from,
                // so a refusal can be told apart from a wiring problem.
                if (!_dumpedSlot && _pendingSlotObject != null)
                {
                    _dumpedSlot = true;
                    AutoSynthPlugin.Logger.LogWarning(
                        "alchemy: the item did not reach the cube — dumping the slot's components");
                    GameInterop.DumpSlotComponents(_pendingSlotObject);
                }
            }
            _slotTries[_pendingSlot] = MaxTriesPerSlot; // one attempt per item, either way
            _filledBeforeClick = -1;
            _pendingSlot = -1;
            _pendingSlotObject = null;
        }

        if (filled >= capacity)
        {
            _step = Step.Trigger;
            nextDelay = AutoSynthPlugin.AfterFillDelay;
            return TickResult.InProgress;
        }
        if (_rejects >= MaxRejects)
        {
            AutoSynthPlugin.Logger.LogWarning(
                $"alchemy fill: {MaxRejects} click(s) in a row did not reach the cube " +
                $"(the game refused them) — closing this batch with {filled} item(s)");
            _step = filled > 0 ? Step.Trigger : Step.Restore;
            return TickResult.InProgress;
        }

        InventorySlot slot;
        ItemInfoData info;
        int position;
        if (!NextEligible(out slot, out info, out position))
        {
            if (filled > 0)
            {
                _step = Step.Trigger;
                nextDelay = AutoSynthPlugin.AfterFillDelay;
            }
            else
            {
                if (_batches == 0)
                    AutoSynthPlugin.Logger.LogInfo(
                        $"alchemy phase: no eligible items " +
                        $"(level < {AutoSynthPlugin.AlchemyLevelThreshold}, " +
                        $"grade <= {AutoSynthPlugin.MaxAlchemyGrade})");
                _step = Step.Restore;
            }
            return TickResult.InProgress;
        }

        int tries;
        _slotTries.TryGetValue(position, out tries);
        _slotTries[position] = tries + 1;

        string label = $"'{info.NameKey}' key={info.ItemKey} lv={info.Level} grade={info.GRADE}";
        if (AutoSynthPlugin.AlchemyDryRun)
        {
            _slotTries[position] = MaxTriesPerSlot;
            AutoSynthPlugin.Logger.LogInfo($"alchemy dry run: would melt {label}");
            return TickResult.InProgress;
        }

        AutoSynthPlugin.Logger.LogInfo($"alchemy: adding {label}");
        _filledBeforeClick = filled;
        _pendingSlot = position;
        _pendingSlotObject = slot;
        string moveDetail;
        if (GameInterop.TryMoveItemToCube(slot, out moveDetail))
            _clickedThisBatch++;
        else
            AutoSynthPlugin.Logger.LogWarning($"alchemy: slot {position} — {moveDetail}");
        return TickResult.InProgress;
    }

    private TickResult TickTrigger(UI_Cube cube, bool loud, ref float nextDelay)
    {
        if (AutoSynthPlugin.AlchemyDryRun)
        {
            _step = Step.Restore;
            return TickResult.InProgress;
        }
        int slotCount;
        int filled = GameInterop.CubeFilledCount(cube, out slotCount);
        if (filled <= 0)
        {
            _step = Step.Restore;
            return TickResult.InProgress;
        }
        AutoSynthPlugin.Logger.LogInfo($"alchemy: running batch {_batches + 1} with {filled} item(s)");
        GameInterop.Click(cube.toggleButton_Trigger, "alchemy trigger", loud);
        _confirmAttempts = 0;
        _step = Step.Confirm;
        nextDelay = AutoSynthPlugin.AfterSynthDelay;
        return TickResult.InProgress;
    }

    // The game asks for confirmation unless the player disabled the warning; the
    // toggle itself is left alone so the player's setting survives.
    private TickResult TickConfirm(UI_Cube cube, bool loud, ref float nextDelay)
    {
        var panel = cube.m_alchemyWarningPanel;
        bool open = panel != null && panel.activeInHierarchy;
        if (!open)
        {
            _alchemizedThisCycle += _clickedThisBatch;
            _batches++;
            _step = Step.Settle;
            nextDelay = AutoSynthPlugin.AfterSynthDelay;
            return TickResult.InProgress;
        }
        if (_confirmAttempts >= MaxConfirmAttempts)
        {
            AutoSynthPlugin.Logger.LogWarning(
                "alchemy: the confirmation panel stayed open — restoring the Synthesis recipe");
            _step = Step.Restore;
            return TickResult.InProgress;
        }
        _confirmAttempts++;
        var confirm = cube.m_button_confirmAlchemy;
        if (confirm != null && confirm.onClick != null)
        {
            confirm.onClick.Invoke();
            AutoSynthPlugin.Logger.LogInfo("alchemy: confirmed the warning panel");
        }
        else
        {
            AutoSynthPlugin.Logger.LogWarning("alchemy: confirmation panel has no confirm button");
            _step = Step.Restore;
        }
        nextDelay = AutoSynthPlugin.AfterSynthDelay;
        return TickResult.InProgress;
    }

    private TickResult TickSettle(UI_Cube cube, bool loud, ref float nextDelay)
    {
        int slotCount;
        int filled = GameInterop.CubeFilledCount(cube, out slotCount);
        if (filled > 0)
        {
            AutoSynthPlugin.Logger.LogWarning(
                $"alchemy: {filled} item(s) are still in the cube after the batch — " +
                "leaving them alone and ending the phase");
            _step = Step.Restore;
            return TickResult.InProgress;
        }
        if (_batches >= AutoSynthPlugin.MaxAlchemyBatchesPerCycle)
        {
            AutoSynthPlugin.Logger.LogInfo(
                $"alchemy: reached MaxAlchemyBatchesPerCycle ({AutoSynthPlugin.MaxAlchemyBatchesPerCycle})");
            _step = Step.Restore;
            return TickResult.InProgress;
        }
        BeginBatch();
        _step = Step.Fill;
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
            nextDelay = AutoSynthPlugin.AfterFillDelay;
            return TickResult.InProgress;
        }

        string detail;
        if (GameInterop.TrySelectMainRecipe(ERecipeType.SYNTHESIS, out detail))
        {
            if (loud) AutoSynthPlugin.Logger.LogInfo("alchemy: cube returned to the Synthesis recipe");
        }
        else if (_restoreAttempts < MaxRecipeAttempts)
        {
            OpenRecipeDropdown();
            nextDelay = AutoSynthPlugin.AfterFillDelay;
            return TickResult.InProgress;
        }
        else
            AutoSynthPlugin.Logger.LogWarning(
                "alchemy: could not switch the cube back to Synthesis (" + detail + ")");

        CloseRecipeDropdown();
        _recipeSwitched = false;
        return Finish(loud);
    }

    private static ComboBoxButton MainRecipeCombo()
    {
        var cube = UnityEngine.Object.FindObjectOfType<UI_Cube>(true);
        return cube != null ? cube.m_mainRecipeToggleBtn : null;
    }

    private static void CloseRecipeDropdown()
    {
        GameInterop.CloseComboBox(MainRecipeCombo(), "main recipe dropdown (close)");
    }

    private static void OpenRecipeDropdown()
    {
        GameInterop.OpenComboBox(MainRecipeCombo(), "main recipe dropdown (open)");
    }

    private TickResult Finish(bool loud)
    {
        LastAlchemized = _alchemizedThisCycle;
        _step = Step.Finished;
        if (loud || _alchemizedThisCycle > 0)
            AutoSynthPlugin.Logger.LogInfo(
                $"alchemy phase done: melted {_alchemizedThisCycle} item(s) in {_batches} batch(es)");
        return TickResult.Done;
    }

    // Walks the inventory in slot order and returns the first item that passes every
    // guard. Slots that already landed in the cube are skipped so a second click can
    // never pull an item back out of it.
    private bool NextEligible(out InventorySlot slot, out ItemInfoData info, out int position)
    {
        slot = null;
        info = null;
        position = -1;

        var slots = InventorySlots();
        if (slots == null) return false;
        for (int i = 0; i < slots.Count; i++)
        {
            int tries;
            if (_slotTries.TryGetValue(i, out tries) && tries >= MaxTriesPerSlot) continue;
            var candidate = slots[i];
            if (candidate == null || !candidate.gameObject.activeInHierarchy) continue;
            var item = GameInterop.InventoryItemInfo(candidate);
            if (item == null) continue;
            if (!IsEligible(candidate, item)) continue;
            slot = candidate;
            info = item;
            position = i;
            return true;
        }
        return false;
    }

    private static bool IsEligible(InventorySlot slot, ItemInfoData info)
    {
        // Alchemy melts gear; materials and boxes are never candidates.
        if (info.ITEMTYPE != EItemType.GEAR) return false;
        if (info.Level <= 0 || info.Level >= AutoSynthPlugin.AlchemyLevelThreshold) return false;
        if ((int)info.GRADE > AutoSynthPlugin.MaxAlchemyGrade) return false;
        if (AutoSynthPlugin.IsAlchemyProtected(info.ItemKey)) return false;
        try
        {
            var manualLock = slot.Go_ManualLock;
            if (manualLock != null && manualLock.activeInHierarchy) return false;
            var reserved = slot.m_reservedIcon;
            if (reserved != null && reserved.activeInHierarchy) return false;
        }
        catch { return false; }
        return true;
    }

    private Il2CppSystem.Collections.Generic.List<InventorySlot> InventorySlots()
    {
        if (_hero == null)
            _hero = UnityEngine.Object.FindObjectOfType<UI_Hero>(true);
        return _hero != null ? _hero.inventorySlots : null;
    }

    internal void Dump()
    {
        try
        {
            var slots = InventorySlots();
            AutoSynthPlugin.Logger.LogInfo(
                $"dump: autoAlchemy={AutoSynthPlugin.AutoAlchemy} " +
                $"levelThreshold={AutoSynthPlugin.AlchemyLevelThreshold} " +
                $"maxGrade={AutoSynthPlugin.MaxAlchemyGrade} " +
                $"dryRun={AutoSynthPlugin.AlchemyDryRun} " +
                $"inventorySlots={(slots != null ? slots.Count : -1)} " +
                $"itemDataReadable={GameInterop.CanReadInventoryItems} " +
                $"canMoveToCube={GameInterop.CanMoveItemsToCube}");
            GameInterop.DumpMainRecipes();
            if (slots == null) return;
            int shown = 0, eligible = 0;
            for (int i = 0; i < slots.Count; i++)
            {
                var slot = slots[i];
                var info = GameInterop.InventoryItemInfo(slot);
                if (info == null) continue;
                bool ok = IsEligible(slot, info);
                if (ok) eligible++;
                if (shown < 12)
                {
                    shown++;
                    AutoSynthPlugin.Logger.LogInfo(
                        $"dump: inv {i} key={info.ItemKey} type={info.ITEMTYPE} " +
                        $"lv={info.Level} grade={info.GRADE} eligible={ok}");
                }
            }
            AutoSynthPlugin.Logger.LogInfo($"dump: alchemy-eligible items = {eligible}");
        }
        catch (Exception e)
        {
            AutoSynthPlugin.Logger.LogError($"dump alchemy failed: {e}");
        }
    }
}
