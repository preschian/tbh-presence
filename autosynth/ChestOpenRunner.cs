using System;
using TaskbarHero;
using TaskbarHero.StatusSystem;
using TaskbarHero.UI;
using UnityEngine;
using UnityEngine.EventSystems;

namespace TbhAutoSynth;

// Owns the Chest phase. Prefers bulk open when the matching account-status
// rune is unlocked:
//   OpenAllTypeChestAllAtOnce  -> InputManager open-all (Space)
//   OpenOneTypeChestAllAtOnce  -> right-click each StageBox once (Rune of Opening)
//   otherwise                  -> left-click one chest at a time
// StageBox sits on the stage HUD and is not covered by Cube/Rune panels.
internal sealed class ChestOpenRunner
{
    internal enum TickResult { InProgress, Done }

    private enum OpenMode { SingleLeft, OneTypeRight, AllTypesKey }

    private static float PhaseBudgetSeconds(OpenMode mode)
    {
        float perOpen = Math.Max(1f, AutoSynthPlugin.AfterChestOpenDelay) + 0.5f;
        if (mode == OpenMode.AllTypesKey)
            return perOpen * 3f + 10f;
        if (mode == OpenMode.OneTypeRight)
            return 3f * perOpen + 15f;
        return AutoSynthPlugin.MaxChestOpensPerCycle * perOpen + 15f;
    }

    private UI_Stage _stageUi;
    private float _phaseEnteredAt;
    private int _opensThisCycle;
    private int _emptyPasses;
    private int _index;
    private OpenMode _mode;
    private bool _allKeyFired;
    private int _allKeySettleTicks;
    private readonly bool[] _bulkClicked = new bool[3];
    private readonly int[] _staleBySlot = new int[3];
    private readonly int[] _countAtClick = { -1, -1, -1 };

    internal int OpensThisCycle => _opensThisCycle;
    internal int LastOpens { get; private set; }

    internal void BeginPhase()
    {
        _opensThisCycle = 0;
        _emptyPasses = 0;
        _index = 0;
        _phaseEnteredAt = Time.unscaledTime;
        _stageUi = null;
        _allKeyFired = false;
        _allKeySettleTicks = 0;
        _mode = DetectMode();
        for (int i = 0; i < 3; i++)
        {
            _bulkClicked[i] = false;
            _staleBySlot[i] = 0;
            _countAtClick[i] = -1;
        }
        AutoSynthPlugin.Logger.LogInfo("chest phase mode: " + ModeLabel(_mode));
    }

    internal void ResetSession()
    {
        BeginPhase();
        LastOpens = 0;
        _stageUi = null;
    }

    private static OpenMode DetectMode()
    {
        if (GameInterop.HasAccountStatus(EAccountStatus.OpenAllTypeChestAllAtOnce))
            return OpenMode.AllTypesKey;
        if (GameInterop.HasAccountStatus(EAccountStatus.OpenOneTypeChestAllAtOnce))
            return OpenMode.OneTypeRight;
        return OpenMode.SingleLeft;
    }

    private static string ModeLabel(OpenMode mode) => mode switch
    {
        OpenMode.AllTypesKey => "OpenAllType (Space / open-all key)",
        OpenMode.OneTypeRight => "OpenOneType (right-click per chest type)",
        _ => "single left-click",
    };

    internal TickResult Tick(bool loud, out float nextDelay)
    {
        nextDelay = Math.Max(0.75f, AutoSynthPlugin.AfterChestOpenDelay);

        if (_mode == OpenMode.SingleLeft
            && _opensThisCycle >= AutoSynthPlugin.MaxChestOpensPerCycle)
        {
            if (loud)
                AutoSynthPlugin.Logger.LogInfo(
                    $"chest phase: hit MaxChestOpensPerCycle={AutoSynthPlugin.MaxChestOpensPerCycle}");
            return Finish(loud);
        }

        float budget = PhaseBudgetSeconds(_mode);
        if (Time.unscaledTime - _phaseEnteredAt >= budget)
        {
            AutoSynthPlugin.Logger.LogWarning(
                $"chest phase: timed out after {budget:0}s " +
                $"(mode={_mode}, opens={_opensThisCycle}) — ending phase");
            return Finish(loud);
        }

        var stage = FindStageUi();
        if (stage == null)
        {
            AutoSynthPlugin.Logger.LogWarning("chest phase: UI_Stage not found — ending phase");
            return Finish(loud);
        }

        var boxes = Boxes(stage);
        if (boxes.Length == 0)
        {
            AutoSynthPlugin.Logger.LogWarning("chest phase: no StageBox slots on UI_Stage — ending phase");
            return Finish(loud);
        }

        if (_mode == OpenMode.AllTypesKey)
            return TickAllTypesKey(boxes, loud, ref nextDelay);
        if (_mode == OpenMode.OneTypeRight)
            return TickOneTypeRight(boxes, loud, ref nextDelay);
        return TickSingleLeft(boxes, loud, ref nextDelay);
    }

    private TickResult TickAllTypesKey(StageBox[] boxes, bool loud, ref float nextDelay)
    {
        int remaining = TotalCount(boxes);
        if (!_allKeyFired)
        {
            if (remaining == 0)
                return Finish(loud);
            if (!GameInterop.TryInvokeOpenAllBoxes())
            {
                AutoSynthPlugin.Logger.LogWarning(
                    "chest phase: open-all key unavailable — falling back to right-click");
                _mode = OpenMode.OneTypeRight;
                return TickOneTypeRight(boxes, loud, ref nextDelay);
            }
            _allKeyFired = true;
            _opensThisCycle += Math.Max(1, remaining);
            if (loud)
                AutoSynthPlugin.Logger.LogInfo(
                    $"chest open-all key pressed (about {remaining} chest(s))");
            nextDelay = Math.Max(nextDelay, 2.5f);
            return TickResult.InProgress;
        }

        _allKeySettleTicks++;
        if (remaining <= 0 || _allKeySettleTicks >= 3)
            return Finish(loud);
        nextDelay = Math.Max(nextDelay, 1.5f);
        return TickResult.InProgress;
    }

    private TickResult TickOneTypeRight(StageBox[] boxes, bool loud, ref float nextDelay)
    {
        bool anyPending = false;
        for (int n = 0; n < boxes.Length; n++)
        {
            int i = (_index + n) % boxes.Length;
            if (_bulkClicked[i]) continue;
            var box = boxes[i];
            if (!SlotHasChests(box, out string why))
            {
                _bulkClicked[i] = true; // nothing to do for this type
                if (loud && why != null && why != "inactive")
                    AutoSynthPlugin.Logger.LogInfo($"chest skip {Label(box)}: {why}");
                continue;
            }
            anyPending = true;
            _index = (i + 1) % boxes.Length;
            int before = Math.Max(1, GameInterop.BoxCount(box.m_boxType));
            if (TryClickOpen(box, PointerEventData.InputButton.Right, loud))
            {
                _bulkClicked[i] = true;
                _opensThisCycle += before;
                nextDelay = Math.Max(nextDelay, OpenDelay(box));
                return TickResult.InProgress;
            }
            _bulkClicked[i] = true; // don't spin forever on a failed detector
        }

        if (!anyPending)
            return Finish(loud);

        _emptyPasses++;
        if (_emptyPasses >= 3) return Finish(loud);
        nextDelay = 1.5f;
        return TickResult.InProgress;
    }

    private TickResult TickSingleLeft(StageBox[] boxes, bool loud, ref float nextDelay)
    {
        bool anyRemaining = false;
        for (int n = 0; n < boxes.Length; n++)
        {
            int i = (_index + n) % boxes.Length;
            var box = boxes[i];
            if (!HasChestsSingle(box, i, out string whySkip))
            {
                if (loud && whySkip != null)
                    AutoSynthPlugin.Logger.LogInfo($"chest skip {Label(box)}: {whySkip}");
                continue;
            }
            anyRemaining = true;
            _index = (i + 1) % boxes.Length;
            int before = GameInterop.BoxCount(box.m_boxType);
            if (TryClickOpen(box, PointerEventData.InputButton.Left, loud))
            {
                _opensThisCycle++;
                _emptyPasses = 0;
                _countAtClick[i] = before;
                nextDelay = Math.Max(nextDelay, OpenDelay(box));
                return TickResult.InProgress;
            }
        }

        if (!anyRemaining)
        {
            _emptyPasses++;
            if (_emptyPasses >= 2 || _opensThisCycle > 0)
                return Finish(loud);
            nextDelay = 1.0f;
            return TickResult.InProgress;
        }

        _emptyPasses++;
        if (_emptyPasses >= 3)
        {
            AutoSynthPlugin.Logger.LogWarning(
                "chest phase: open clicks failing — ending phase");
            return Finish(loud);
        }
        nextDelay = 1.5f;
        return TickResult.InProgress;
    }

    private TickResult Finish(bool loud)
    {
        LastOpens = _opensThisCycle;
        if (loud || _opensThisCycle > 0)
            AutoSynthPlugin.Logger.LogInfo($"chest phase done: opened {_opensThisCycle}");
        return TickResult.Done;
    }

    private UI_Stage FindStageUi()
    {
        if (_stageUi == null)
            _stageUi = UnityEngine.Object.FindObjectOfType<UI_Stage>(true);
        return _stageUi;
    }

    private static StageBox[] Boxes(UI_Stage stage)
    {
        if (stage == null) return Array.Empty<StageBox>();
        return new[] { stage.m_normalBox, stage.m_bossBox, stage.m_actBossBox };
    }

    private static int TotalCount(StageBox[] boxes)
    {
        int sum = 0;
        bool any = false;
        foreach (var box in boxes)
        {
            if (!SlotHasChests(box, out _)) continue;
            int c = GameInterop.BoxCount(box.m_boxType);
            if (c > 0) { sum += c; any = true; }
            else if (c < 0) any = true; // unknown but looks openable
        }
        return any && sum == 0 ? 1 : sum;
    }

    private static string Label(StageBox box)
    {
        if (box == null) return "?";
        try
        {
            return box.m_boxType switch
            {
                EBoxType.NORMAL => "Normal",
                EBoxType.BOSS => "Boss",
                EBoxType.ACTBOSS => "ActBoss",
                _ => box.m_boxType.ToString(),
            };
        }
        catch { return box.name ?? "StageBox"; }
    }

    private static bool SlotHasChests(StageBox box, out string whySkip)
    {
        whySkip = null;
        if (box == null || !box.gameObject.activeInHierarchy)
        {
            whySkip = "inactive";
            return false;
        }
        try
        {
            var notice = box.m_noticeInventoryFull;
            if (notice != null && notice.activeInHierarchy)
            {
                whySkip = "inventory full";
                return false;
            }
        }
        catch { }

        int count = GameInterop.BoxCount(box.m_boxType);
        if (count == 0)
        {
            whySkip = "count=0";
            return false;
        }
        if (count > 0) return true;

        var detector = box.m_clickDetector;
        if (detector == null || !detector.gameObject.activeInHierarchy)
        {
            whySkip = "click detector missing";
            return false;
        }
        return true;
    }

    private bool HasChestsSingle(StageBox box, int slot, out string whySkip)
    {
        if (!SlotHasChests(box, out whySkip))
            return false;
        if (_staleBySlot[slot] >= 3)
        {
            whySkip = "count not decreasing after clicks";
            return false;
        }

        int count = GameInterop.BoxCount(box.m_boxType);
        if (_countAtClick[slot] >= 0 && count >= 0 && count >= _countAtClick[slot])
        {
            _staleBySlot[slot]++;
            whySkip = $"stale count={count} (was {_countAtClick[slot]})";
            return _staleBySlot[slot] < 3 && count > 0;
        }
        if (_countAtClick[slot] >= 0 && count >= 0 && count < _countAtClick[slot])
        {
            _staleBySlot[slot] = 0;
            _countAtClick[slot] = -1;
        }
        return true;
    }

    private static bool TryClickOpen(StageBox box, PointerEventData.InputButton button, bool loud)
    {
        try
        {
            var detector = box != null ? box.m_clickDetector : null;
            if (detector == null)
            {
                AutoSynthPlugin.Logger.LogWarning($"chest open ({Label(box)}): click detector null");
                return false;
            }
            if (!detector.gameObject.activeInHierarchy)
            {
                if (loud) AutoSynthPlugin.Logger.LogInfo($"chest open ({Label(box)}): detector inactive");
                return false;
            }

            var ped = new PointerEventData(EventSystem.current)
            {
                button = button,
                clickCount = 1,
            };
            detector.OnPointerClick(ped);
            if (loud)
                AutoSynthPlugin.Logger.LogInfo(
                    $"clicked chest open ({Label(box)}) via {button.ToString().ToLowerInvariant()} pointer");
            return true;
        }
        catch (Exception e)
        {
            AutoSynthPlugin.Logger.LogWarning($"chest open failed: {e.Message}");
            return false;
        }
    }

    private static float OpenDelay(StageBox box)
    {
        float configured = AutoSynthPlugin.AfterChestOpenDelay;
        try
        {
            if (box != null && box.m_openBoxAnimationDelayTime > 0f)
                return Math.Max(configured, box.m_openBoxAnimationDelayTime + 0.25f);
        }
        catch { }
        return configured;
    }

    internal void Dump()
    {
        try
        {
            var stage = FindStageUi();
            int one = GameInterop.AccountStatusValue(EAccountStatus.OpenOneTypeChestAllAtOnce);
            int all = GameInterop.AccountStatusValue(EAccountStatus.OpenAllTypeChestAllAtOnce);
            AutoSynthPlugin.Logger.LogInfo(
                $"dump: stageUi={(stage != null)} autoOpenChest={AutoSynthPlugin.AutoOpenChest} " +
                $"OpenOneType={one} OpenAllType={all} mode={DetectMode()}");
            if (stage == null) return;
            foreach (var box in Boxes(stage))
            {
                if (box == null)
                {
                    AutoSynthPlugin.Logger.LogInfo("dump: StageBox null");
                    continue;
                }
                int count = GameInterop.BoxCount(box.m_boxType);
                var det = box.m_clickDetector;
                string detState = det == null ? "null"
                    : $"active={det.gameObject.activeInHierarchy}";
                AutoSynthPlugin.Logger.LogInfo(
                    $"dump: chest {Label(box)} type={box.m_boxType} count={count} detector=[{detState}]");
            }
        }
        catch (Exception e)
        {
            AutoSynthPlugin.Logger.LogError($"dump chest failed: {e}");
        }
    }
}
