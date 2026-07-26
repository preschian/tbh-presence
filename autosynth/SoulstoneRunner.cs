using System;
using System.Collections.Generic;
using TaskbarHero;
using TaskbarHero.Data;
using TaskbarHero.UI;
using UnityEngine;

namespace TbhAutoSynth;

// Owns the Soulstone phase: spends surplus soulstones by entering an Act Boss
// stage (the `*-10` stages) the account has already cleared, which is where the
// stones are consumed and the red Act Boss chests drop.
//
// The game's own auto-retry keeps re-entering the boss while stones remain, so
// the phase enters once and then just counts the runs going by: one soulstone is
// spent per entry, and a cleared run drops an Act Boss chest. Once
// `ActBossRunsPerCycle` runs are in — or the stones run out, or the watch times
// out — it walks the hero back to the stage it came from.
//
// Which stones may be spent is the player's choice (SoulstoneTiers); each tier
// belongs to one difficulty, so the phase switches the Portal to the difficulty
// of the tier it picked, then walks to the act it needs.
//
// The stage table, the account's stage progress and the soulstone counts are all
// readable while every panel is closed, so a cycle with nothing to spend never
// opens the Portal at all — same pre-check idea as the Rune phase. Entering is
// done by clicking the portal's own StageNode enter button, never by calling the
// game's stage-entry code directly.
internal sealed class SoulstoneRunner
{
    internal enum TickResult { InProgress, Done }

    private enum Step { Precheck, GoToBoss, Watch, GoBack }

    private enum NavResult { Working, Arrived, Failed }

    private const int MaxOpenAttempts = 6;
    private const float OpenTimeoutSeconds = 60f;
    private const int MaxActClicks = 4;
    private const int MaxDifficultyClicks = 4;
    private const int MaxVerifyTicks = 6;
    private const float WatchPollSeconds = 3f;

    // One stage this phase may walk the hero into.
    private readonly struct Target
    {
        internal readonly int StageKey;
        internal readonly int Act;
        internal readonly int StageNo;
        internal readonly ESTAGEDIFFICULTY Difficulty;
        internal readonly int StoneKey;
        internal readonly int StoneCost;
        internal readonly int StonesOwned;

        internal Target(int stageKey, int act, int stageNo, ESTAGEDIFFICULTY difficulty,
            int stoneKey, int stoneCost, int stonesOwned)
        {
            StageKey = stageKey;
            Act = act;
            StageNo = stageNo;
            Difficulty = difficulty;
            StoneKey = stoneKey;
            StoneCost = stoneCost;
            StonesOwned = stonesOwned;
        }

        internal bool Exists => StageKey > 0;

        internal string Label => $"{Difficulty} {Act}-{StageNo} (stage {StageKey})";
    }

    private UI_Portal _portal;

    private Step _step;
    private Target _boss;
    private Target _home;

    private float _phaseEnteredAt;
    private float _nextOpenAttempt;
    private int _openAttempts;
    private int _actClicks;
    private int _difficultyClicks;
    private bool _loggedOpenFailed;

    private int _pendingStageKey = -1;
    private int _pendingStoneKey = -1;
    private int _pendingStonesBefore = -1;
    private int _verifyTicks;

    private float _watchStartedAt;
    private int _boxLast = -1;
    private int _chestsGained;
    private int _stonesAtStart = -1;
    private int _runsDone;

    internal int LastRuns { get; private set; }

    internal void BeginPhase()
    {
        _step = Step.Precheck;
        _boss = default;
        _home = default;
        _chestsGained = 0;
        _boxLast = -1;
        _stonesAtStart = -1;
        _runsDone = 0;
        _phaseEnteredAt = Time.unscaledTime;
        ResetNavigationBudgets();
        ClearPending();
    }

    internal void ResetSession()
    {
        BeginPhase();
        LastRuns = 0;
        _portal = null;
    }

    private void ClearPending()
    {
        _pendingStageKey = -1;
        _pendingStoneKey = -1;
        _pendingStonesBefore = -1;
        _verifyTicks = 0;
    }

    // Each leg reopens the Portal from scratch, so the open/act/difficulty budgets
    // belong to the leg rather than to the phase.
    private void ResetNavigationBudgets()
    {
        _openAttempts = 0;
        _actClicks = 0;
        _difficultyClicks = 0;
        _nextOpenAttempt = 0f;
        _phaseEnteredAt = Time.unscaledTime;
        _loggedOpenFailed = false;
        MainMenuAccess.Reset();
    }

    internal TickResult Tick(bool loud, out float nextDelay)
    {
        nextDelay = 1.5f;

        switch (_step)
        {
            case Step.Precheck:
                return TickPrecheck(loud, ref nextDelay);
            case Step.GoToBoss:
                return TickGoToBoss(loud, ref nextDelay);
            case Step.Watch:
                return TickWatch(loud, ref nextDelay);
            default:
                return TickGoBack(loud, ref nextDelay);
        }
    }

    private TickResult TickPrecheck(bool loud, ref float nextDelay)
    {
        var candidates = CollectTargets(out string reason);
        if (candidates.Count == 0)
        {
            AutoSynthPlugin.Logger.LogInfo($"soulstone phase: {reason} — leaving the Portal closed");
            return Finish(loud);
        }

        _boss = candidates[0];
        _home = StageByKey(GameInterop.CurrentStageKey());
        AutoSynthPlugin.Logger.LogInfo(
            $"soulstone phase: {candidates.Count} cleared Act Boss stage(s) affordable; " +
            $"taking {_boss.Label} with {_boss.StonesOwned} stone(s) held (costs {_boss.StoneCost}), " +
            $"farming {AutoSynthPlugin.ActBossRunsPerCycle} run(s) " +
            $"then back to {(_home.Exists ? _home.Label : "wherever the hero ends up")}");
        if (AutoSynthPlugin.SoulstoneDryRun)
            foreach (var c in candidates)
                AutoSynthPlugin.Logger.LogInfo(
                    $"soulstone dry run: candidate {c.Label}, " +
                    $"soulstone item {c.StoneKey} x{c.StoneCost} of {c.StonesOwned} held");

        _step = Step.GoToBoss;
        ResetNavigationBudgets();
        nextDelay = 0.25f;
        return TickResult.InProgress;
    }

    private TickResult TickGoToBoss(bool loud, ref float nextDelay)
    {
        var nav = Navigate(_boss, true, loud, ref nextDelay);
        if (nav == NavResult.Failed) return Finish(loud);
        if (nav == NavResult.Working) return TickResult.InProgress;

        // In the stage: from here the game's auto-retry does the re-entering, and
        // the phase only counts the Act Boss chests that drop.
        _step = Step.Watch;
        _watchStartedAt = Time.unscaledTime;
        _boxLast = GameInterop.BoxCount(EBoxType.ACTBOSS);
        _chestsGained = 0;
        _runsDone = 1;  // the entry itself is run 1
        // That entry already cost a stone, so the baseline is what is left now.
        _stonesAtStart = GameInterop.ItemCount(_boss.StoneKey);
        if (_stonesAtStart < 0 && _boxLast < 0)
            AutoSynthPlugin.Logger.LogWarning(
                "soulstone phase: neither the soulstone count nor the Act Boss chest count is " +
                $"readable — watching for {AutoSynthPlugin.ActBossWatchMinutes} minute(s) instead");
        else
            AutoSynthPlugin.Logger.LogInfo(
                $"soulstone phase: farming {_boss.Label} for " +
                $"{AutoSynthPlugin.ActBossRunsPerCycle} run(s) " +
                $"(stones left {_stonesAtStart}, Act Boss chests held {_boxLast})");
        nextDelay = WatchPollSeconds;
        return TickResult.InProgress;
    }

    private TickResult TickWatch(bool loud, ref float nextDelay)
    {
        nextDelay = WatchPollSeconds;

        // Chest gains only: the game's own auto-open (or the Chest phase) can drain
        // the stack between polls, and that must not read as negative progress. With
        // auto-open on the stack may never grow at all, which is why the stones —
        // one per entry — are the primary counter.
        int now = GameInterop.BoxCount(EBoxType.ACTBOSS);
        if (now >= 0)
        {
            if (_boxLast >= 0 && now > _boxLast) _chestsGained += now - _boxLast;
            _boxLast = now;
        }

        int stones = GameInterop.ItemCount(_boss.StoneKey);
        int spent = stones >= 0 && _stonesAtStart >= 0 ? Math.Max(0, _stonesAtStart - stones) : 0;
        int runs = 1 + Math.Max(spent, _chestsGained);
        if (runs > _runsDone)
        {
            _runsDone = runs;
            AutoSynthPlugin.Logger.LogInfo(
                $"soulstone phase: run {_runsDone}/{AutoSynthPlugin.ActBossRunsPerCycle} on " +
                $"{_boss.Label} (stones left {stones}, chests gained {_chestsGained})");
        }

        if (_runsDone >= AutoSynthPlugin.ActBossRunsPerCycle)
        {
            AutoSynthPlugin.Logger.LogInfo(
                $"soulstone phase: {_runsDone} run(s) done — heading back");
            return LeaveBoss(loud, ref nextDelay);
        }

        if (stones >= 0 && stones < _boss.StoneCost)
        {
            AutoSynthPlugin.Logger.LogInfo(
                $"soulstone phase: out of {_boss.Difficulty} soulstones after " +
                $"{_runsDone} run(s) — heading back");
            return LeaveBoss(loud, ref nextDelay);
        }

        int current = GameInterop.CurrentStageKey();
        if (current >= 0 && current != _boss.StageKey && Time.unscaledTime - _watchStartedAt > 30f)
        {
            AutoSynthPlugin.Logger.LogInfo(
                $"soulstone phase: the hero left {_boss.Label} on its own after " +
                $"{_runsDone} run(s) — ending phase");
            return Finish(loud);
        }

        float budget = Math.Max(1, AutoSynthPlugin.ActBossWatchMinutes) * 60f;
        if (Time.unscaledTime - _watchStartedAt >= budget)
        {
            AutoSynthPlugin.Logger.LogInfo(
                $"soulstone phase: {budget / 60f:0} minute(s) up after {_runsDone} run(s) " +
                "— heading back");
            return LeaveBoss(loud, ref nextDelay);
        }

        return TickResult.InProgress;
    }

    private TickResult LeaveBoss(bool loud, ref float nextDelay)
    {
        LastRuns = _runsDone;
        if (!_home.Exists || _home.StageKey == _boss.StageKey)
        {
            AutoSynthPlugin.Logger.LogInfo(
                "soulstone phase: no stage to go back to — leaving the hero where it is");
            return Finish(loud);
        }
        _step = Step.GoBack;
        ResetNavigationBudgets();
        ClearPending();
        nextDelay = 0.25f;
        return TickResult.InProgress;
    }

    private TickResult TickGoBack(bool loud, ref float nextDelay)
    {
        var nav = Navigate(_home, false, loud, ref nextDelay);
        if (nav == NavResult.Working) return TickResult.InProgress;
        if (nav == NavResult.Arrived)
            AutoSynthPlugin.Logger.LogInfo($"soulstone phase: back on {_home.Label}");
        else
            AutoSynthPlugin.Logger.LogWarning(
                $"soulstone phase: could not walk back to {_home.Label} — " +
                "the hero is still on the Act Boss stage");
        return Finish(loud);
    }

    // One leg of the trip: open the Portal, move it to the stage's difficulty and
    // act, then click the node. spendsStone marks the leg that costs a soulstone,
    // which is the only one a dry run must stop short of.
    private NavResult Navigate(Target target, bool spendsStone, bool loud, ref float nextDelay)
    {
        if (_pendingStageKey > 0)
        {
            var confirm = ConfirmPending(loud);
            if (confirm == null)
            {
                nextDelay = Math.Max(1f, AutoSynthPlugin.AfterSoulstoneEnterDelay);
                return NavResult.Working;
            }
            return confirm.Value ? NavResult.Arrived : NavResult.Failed;
        }

        if (GameInterop.CurrentStageKey() == target.StageKey)
            return NavResult.Arrived;

        var portal = FindPortal();
        if (!IsOpen(portal))
        {
            if (!AutoSynthPlugin.AutoOpenPortal)
            {
                AutoSynthPlugin.Logger.LogWarning(
                    "soulstone phase: Portal closed and AutoOpenPortal=false — ending phase");
                return NavResult.Failed;
            }
            if (Time.unscaledTime - _phaseEnteredAt >= OpenTimeoutSeconds
                || _openAttempts >= MaxOpenAttempts)
            {
                if (!_loggedOpenFailed)
                {
                    _loggedOpenFailed = true;
                    AutoSynthPlugin.Logger.LogWarning(
                        $"soulstone phase: could not open the Portal (attempts={_openAttempts})");
                }
                return NavResult.Failed;
            }
            if (TryOpenPortal(loud, out bool spent) && spent)
                _openAttempts++;
            nextDelay = Math.Max(0.25f, _nextOpenAttempt - Time.unscaledTime);
            return NavResult.Working;
        }

        // One difficulty is on screen at a time, and each soulstone tier belongs to
        // one difficulty, so the map has to be moved to the tier being spent.
        ESTAGEDIFFICULTY difficulty;
        try { difficulty = portal.m_currentStageDifficulty; }
        catch { difficulty = target.Difficulty; }
        if (difficulty != target.Difficulty)
        {
            if (_difficultyClicks >= MaxDifficultyClicks)
            {
                AutoSynthPlugin.Logger.LogWarning(
                    $"soulstone phase: the map stayed on {difficulty} instead of {target.Difficulty}");
                return NavResult.Failed;
            }
            if (!TrySelectDifficulty(portal, difficulty, target.Difficulty, loud))
            {
                AutoSynthPlugin.Logger.LogWarning(
                    $"soulstone phase: could not switch the map to {target.Difficulty}");
                return NavResult.Failed;
            }
            _difficultyClicks++;
            _actClicks = 0; // the new map rebuilds its act panels
            nextDelay = 1f;
            return NavResult.Working;
        }

        var node = FindNode(target.StageKey);
        if (node == null)
        {
            if (_actClicks >= MaxActClicks)
            {
                AutoSynthPlugin.Logger.LogWarning(
                    $"soulstone phase: {target.Label} never appeared on the map");
                return NavResult.Failed;
            }
            if (!TryShowAct(portal, target.Act, loud))
            {
                AutoSynthPlugin.Logger.LogWarning(
                    $"soulstone phase: no act selector for act {target.Act}");
                return NavResult.Failed;
            }
            _actClicks++;
            nextDelay = 1f;
            return NavResult.Working;
        }

        if (IsLocked(node))
        {
            AutoSynthPlugin.Logger.LogWarning($"soulstone phase: {target.Label} is locked in-game");
            return NavResult.Failed;
        }

        // Dry run walks the whole path — open, difficulty, act, node lookup — and
        // stops on the doorstep, so the log shows the stage really picked.
        if (spendsStone && AutoSynthPlugin.SoulstoneDryRun)
        {
            AutoSynthPlugin.Logger.LogInfo(
                $"soulstone dry run: would enter {target.Label}, spending {target.StoneCost} " +
                $"of {target.StonesOwned} soulstone(s) held");
            ClosePortal(portal);
            return NavResult.Failed;
        }

        int before = spendsStone ? GameInterop.ItemCount(target.StoneKey) : -1;
        if (!GameInterop.ClickButton(node.button_Enter, $"enter {target.Label}", true))
            return NavResult.Failed;

        _pendingStageKey = target.StageKey;
        _pendingStoneKey = target.StoneKey;
        _pendingStonesBefore = before;
        _verifyTicks = 0;
        if (spendsStone)
            AutoSynthPlugin.Logger.LogInfo(
                $"soulstone phase: entering {target.Label}, spending {target.StoneCost} soulstone(s)");
        nextDelay = Math.Max(1f, AutoSynthPlugin.AfterSoulstoneEnterDelay);
        return NavResult.Working;
    }

    // null = still waiting, true = entered, false = the entry did not take.
    private bool? ConfirmPending(bool loud)
    {
        int current = GameInterop.CurrentStageKey();
        if (current == _pendingStageKey)
        {
            if (loud)
                AutoSynthPlugin.Logger.LogInfo($"soulstone phase: stage {current} entered");
            ClearPending();
            return true;
        }

        int now = GameInterop.ItemCount(_pendingStoneKey);
        if (_pendingStonesBefore >= 0 && now >= 0 && now < _pendingStonesBefore)
        {
            AutoSynthPlugin.Logger.LogInfo(
                $"soulstone phase: soulstone spent ({_pendingStonesBefore} -> {now})");
            ClearPending();
            return true;
        }

        _verifyTicks++;
        if (_verifyTicks < MaxVerifyTicks) return null;

        AutoSynthPlugin.Logger.LogWarning(
            $"soulstone phase: stage {_pendingStageKey} did not start " +
            "(chest space, or the game refused the entry)");
        ClearPending();
        return false;
    }

    // Cleared Act Boss stages on an enabled tier that the account can pay for,
    // best first (highest tier, then the deepest act).
    private List<Target> CollectTargets(out string reason)
    {
        var found = new List<Target>();
        reason = null;

        var stages = GameInterop.StageInfoList();
        if (stages == null || stages.Count == 0)
        {
            reason = "the stage table is unavailable";
            return found;
        }
        if (!GameInterop.CanReadStageProgress)
        {
            reason = "stage progress is unreadable on this game build";
            return found;
        }
        int maxCompleted = GameInterop.MaxCompletedStage();
        if (maxCompleted <= 0)
        {
            reason = "no stage progress recorded yet";
            return found;
        }

        var tiers = AutoSynthPlugin.EnabledSoulstoneTiers();
        var counts = new Dictionary<int, int>();
        int cleared = 0, unaffordable = 0, unknownStones = 0, offTier = 0;
        for (int i = 0; i < stages.Count; i++)
        {
            var stage = stages[i];
            if (stage == null || stage.STAGETYPE != EStageType.ACTBOSS) continue;
            if (stage.StageKey > maxCompleted) continue; // never entered / never cleared
            int stoneKey = stage.SoulStoneItemKey;
            if (stoneKey <= 0) continue;
            cleared++;
            if (!tiers.Contains((int)stage.STAGEDIFFICULTY)) { offTier++; continue; }

            if (!counts.TryGetValue(stoneKey, out int owned))
            {
                owned = GameInterop.ItemCount(stoneKey);
                counts[stoneKey] = owned;
            }
            if (owned < 0) { unknownStones++; continue; }

            int cost = Math.Max(1, stage.SoulStoneAmount);
            if (owned < cost) { unaffordable++; continue; }

            found.Add(new Target(stage.StageKey, stage.Act, stage.StageNo, stage.STAGEDIFFICULTY,
                stoneKey, cost, owned));
        }

        // Highest tier first, then the deepest act within it: the stones a tier
        // buys are the same either way, so spend them where the drops are best.
        found.Sort((a, b) =>
            a.Difficulty != b.Difficulty ? (int)b.Difficulty - (int)a.Difficulty
            : a.Act != b.Act ? b.Act - a.Act
            : b.StageKey - a.StageKey);

        if (found.Count == 0)
        {
            if (cleared == 0) reason = "no Act Boss stage has been cleared yet";
            else if (unknownStones > 0) reason = "soulstone counts are unreadable on this game build";
            else if (unaffordable == 0 && offTier > 0)
                reason = $"every cleared Act Boss stage is on a tier SoulstoneTiers leaves out " +
                         $"({AutoSynthPlugin.SoulstoneTiersRaw})";
            else reason = $"no soulstones for the {unaffordable} cleared Act Boss stage(s) " +
                          $"on the enabled tier(s) ({AutoSynthPlugin.SoulstoneTiersRaw})";
        }
        return found;
    }

    // The stage the hero is on right now, so the phase can put it back afterwards.
    private static Target StageByKey(int stageKey)
    {
        if (stageKey <= 0) return default;
        try
        {
            var stages = GameInterop.StageInfoList();
            if (stages == null) return default;
            for (int i = 0; i < stages.Count; i++)
            {
                var stage = stages[i];
                if (stage == null || stage.StageKey != stageKey) continue;
                return new Target(stage.StageKey, stage.Act, stage.StageNo, stage.STAGEDIFFICULTY,
                    stage.SoulStoneItemKey, Math.Max(1, stage.SoulStoneAmount), 0);
            }
        }
        catch { }
        return default;
    }

    private bool TryOpenPortal(bool loud, out bool spent)
    {
        var result = MainMenuAccess.TryOpenContentPanel(
            "Portal", "Portal menu button (soulstone phase)", loud, out float delay, out spent);
        _nextOpenAttempt = Time.unscaledTime + delay;
        if (result != MainMenuAccess.PanelResult.Failed) return true;

        if (!_loggedOpenFailed)
        {
            _loggedOpenFailed = true;
            AutoSynthPlugin.Logger.LogWarning(
                "soulstone phase: could not open the main menu for the Portal");
        }
        _openAttempts = MaxOpenAttempts;
        return false;
    }

    // The difficulty dropdown may list only the difficulties the account has
    // unlocked, so its indices are calibrated against the entry that is selected
    // right now rather than assumed to be in ESTAGEDIFFICULTY order.
    private static bool TrySelectDifficulty(UI_Portal portal, ESTAGEDIFFICULTY current,
        ESTAGEDIFFICULTY wanted, bool loud)
    {
        try
        {
            var dropdown = portal != null ? portal.stagetDiff_dropDown : null;
            if (dropdown == null) return false;
            var options = dropdown.options;
            int count = options != null ? options.Count : 0;
            if (count == 0) return false;

            int index = dropdown.value + ((int)wanted - (int)current);
            if (index < 0 || index >= count)
            {
                AutoSynthPlugin.Logger.LogWarning(
                    $"soulstone phase: {wanted} is not on the difficulty dropdown " +
                    $"(entries={count}, current={current} at {dropdown.value})");
                return false;
            }
            dropdown.value = index;
            if (loud)
                AutoSynthPlugin.Logger.LogInfo(
                    $"soulstone phase: switching the map from {current} to {wanted} (entry {index})");
            return true;
        }
        catch (Exception e)
        {
            AutoSynthPlugin.Logger.LogWarning($"soulstone phase: difficulty select failed: {e.Message}");
            return false;
        }
    }

    // The map only builds nodes for the act on screen, so switch acts through the
    // portal's own act selector rather than reaching into its node pool.
    private static bool TryShowAct(UI_Portal portal, int act, bool loud)
    {
        try
        {
            if (portal != null && portal.currentAct == act) return true;
            var slots = UnityEngine.Object.FindObjectsOfType<ActSlot>(true);
            foreach (var slot in slots)
            {
                if (slot == null || slot.act != act) continue;
                return GameInterop.ClickButton(slot.btn, $"portal act {act}", loud);
            }
            return false;
        }
        catch (Exception e)
        {
            AutoSynthPlugin.Logger.LogWarning($"soulstone phase: act select failed: {e.Message}");
            return false;
        }
    }

    private static StageNode FindNode(int stageKey)
    {
        try
        {
            var nodes = UnityEngine.Object.FindObjectsOfType<StageNode>(true);
            foreach (var node in nodes)
            {
                if (node == null || !node.gameObject.activeInHierarchy) continue;
                var info = GameInterop.StageInfoOfNode(node);
                if (info != null && info.StageKey == stageKey) return node;
            }
        }
        catch (Exception e)
        {
            AutoSynthPlugin.Logger.LogWarning($"soulstone phase: node scan failed: {e.Message}");
        }
        return null;
    }

    // Only the dry run has to tidy up after itself: a real entry closes the map.
    private static void ClosePortal(UI_Portal portal)
    {
        try
        {
            if (IsOpen(portal) && portal.button_Close != null)
                GameInterop.ClickButton(portal.button_Close, "portal close", false);
        }
        catch { }
    }

    private static bool IsLocked(StageNode node)
    {
        try
        {
            var icon = node != null ? node.m_lockIcon : null;
            return icon != null && icon.activeInHierarchy;
        }
        catch { return false; }
    }

    private UI_Portal FindPortal()
    {
        if (_portal == null)
            _portal = UnityEngine.Object.FindObjectOfType<UI_Portal>(true);
        return _portal;
    }

    private static bool IsOpen(UI_Portal portal)
        => portal != null && portal.gameObject.activeInHierarchy;

    private TickResult Finish(bool loud)
    {
        LastRuns = _runsDone;
        if (loud || _runsDone > 0)
            AutoSynthPlugin.Logger.LogInfo(
                $"soulstone phase done: {_runsDone} Act Boss run(s) this cycle");
        return TickResult.Done;
    }

    internal void Dump()
    {
        try
        {
            var portal = FindPortal();
            AutoSynthPlugin.Logger.LogInfo(
                $"dump: autoConsumeSoulstone={AutoSynthPlugin.AutoConsumeSoulstone} " +
                $"tiers={AutoSynthPlugin.SoulstoneTiersRaw} " +
                $"dryRun={AutoSynthPlugin.SoulstoneDryRun} " +
                $"runsPerCycle={AutoSynthPlugin.ActBossRunsPerCycle} " +
                $"watchMinutes={AutoSynthPlugin.ActBossWatchMinutes} " +
                $"step={_step} runsDone={_runsDone} chestsGained={_chestsGained} " +
                $"portalOpen={IsOpen(portal)} " +
                $"actBossBoxes={GameInterop.BoxCount(EBoxType.ACTBOSS)} " +
                $"maxCompletedStage={GameInterop.MaxCompletedStage()} " +
                $"currentStageKey={GameInterop.CurrentStageKey()} " +
                $"canReadNodes={GameInterop.CanReadPortalNodes}");

            // One line per soulstone tier: which difficulty spends it and how many
            // the account holds, so all four tiers are visible in the log.
            var stages = GameInterop.StageInfoList();
            if (stages == null)
            {
                AutoSynthPlugin.Logger.LogInfo("dump: stage table unavailable");
                return;
            }
            var seen = new Dictionary<int, ESTAGEDIFFICULTY>();
            for (int i = 0; i < stages.Count; i++)
            {
                var stage = stages[i];
                if (stage == null || stage.STAGETYPE != EStageType.ACTBOSS) continue;
                int key = stage.SoulStoneItemKey;
                if (key <= 0 || seen.ContainsKey(key)) continue;
                seen[key] = stage.STAGEDIFFICULTY;
                AutoSynthPlugin.Logger.LogInfo(
                    $"dump: soulstone item {key} ({stage.STAGEDIFFICULTY}) held={GameInterop.ItemCount(key)}");
            }

            var targets = CollectTargets(out string reason);
            if (targets.Count == 0)
            {
                AutoSynthPlugin.Logger.LogInfo($"dump: no soulstone target ({reason})");
                return;
            }
            foreach (var t in targets)
                AutoSynthPlugin.Logger.LogInfo(
                    $"dump: soulstone target {t.Label} costs {t.StoneCost} of {t.StonesOwned} held");
        }
        catch (Exception e)
        {
            AutoSynthPlugin.Logger.LogError($"dump soulstone failed: {e}");
        }
    }
}
