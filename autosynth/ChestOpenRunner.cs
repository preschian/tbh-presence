using System;
using TaskbarHero;
using TaskbarHero.StatusSystem;
using TaskbarHero.UI;
using UnityEngine;
using UnityEngine.EventSystems;

namespace TbhAutoSynth;

// Owns the Chest phase. One scanner + a small open policy:
//   AllTypesKey  -> InputManager open-all (Space), retry while counts remain
//   OneTypeRight -> right-click each StageBox once (Rune of Opening)
//   SingleLeft   -> left-click one chest at a time
// StageBox sits on the stage HUD and is not covered by Cube/Rune panels.
internal sealed class ChestOpenRunner
{
    internal enum TickResult { InProgress, Done }

    private enum OpenMode { SingleLeft, OneTypeRight, AllTypesKey }

    private const int StaleAbort = 3;
    private const int KeySettleTicks = 4;
    private const int KeyMaxRetries = 2;

    private static float PhaseBudgetSeconds(OpenMode mode)
    {
        float perOpen = Math.Max(1f, AutoSynthPlugin.AfterChestOpenDelay) + 0.5f;
        if (mode == OpenMode.AllTypesKey)
            return perOpen * (1 + KeyMaxRetries) * KeySettleTicks + 15f;
        if (mode == OpenMode.OneTypeRight)
            return 3f * perOpen + 15f;
        return AutoSynthPlugin.MaxChestOpensPerCycle * perOpen + 15f;
    }

    private UI_Stage _stageUi;
    private float _phaseEnteredAt;
    private int _opensThisCycle;
    private int _index;
    private OpenMode _mode;
    private bool _keyFired;
    private int _keyRetries;
    private int _keySettleTicks;
    private readonly bool[] _slotDone = new bool[3];
    private readonly int[] _staleFails = new int[3];
    private readonly int[] _countBeforeClick = { -1, -1, -1 };

    internal int OpensThisCycle => _opensThisCycle;
    internal int LastOpens { get; private set; }

    internal void BeginPhase()
    {
        ClearState();
        _phaseEnteredAt = Time.unscaledTime;
        _mode = DetectMode();
        AutoSynthPlugin.Logger.LogInfo("chest phase mode: " + ModeLabel(_mode));
    }

    // Clear counters without DetectMode / logging (F8 / session reset).
    internal void ResetSession()
    {
        ClearState();
        LastOpens = 0;
        _stageUi = null;
    }

    private void ClearState()
    {
        _opensThisCycle = 0;
        _index = 0;
        _keyFired = false;
        _keyRetries = 0;
        _keySettleTicks = 0;
        _stageUi = null;
        for (int i = 0; i < 3; i++)
        {
            _slotDone[i] = false;
            _staleFails[i] = 0;
            _countBeforeClick[i] = -1;
        }
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

        bool onePerSlot = _mode == OpenMode.OneTypeRight;
        var button = onePerSlot
            ? PointerEventData.InputButton.Right
            : PointerEventData.InputButton.Left;
        return TickSlots(boxes, button, onePerSlot, loud, ref nextDelay);
    }

    private TickResult TickAllTypesKey(StageBox[] boxes, bool loud, ref float nextDelay)
    {
        int remaining = TotalCount(boxes);
        if (!_keyFired)
        {
            if (remaining == 0)
                return Finish(loud);
            if (!GameInterop.TryInvokeOpenAllBoxes())
            {
                // Space unlock ≠ right-click unlock — only fall back to the privilege held.
                if (GameInterop.HasAccountStatus(EAccountStatus.OpenOneTypeChestAllAtOnce))
                {
                    AutoSynthPlugin.Logger.LogWarning(
                        "chest phase: open-all key unavailable — falling back to right-click");
                    _mode = OpenMode.OneTypeRight;
                }
                else
                {
                    AutoSynthPlugin.Logger.LogWarning(
                        "chest phase: open-all key unavailable — falling back to left-click");
                    _mode = OpenMode.SingleLeft;
                }
                return Tick(loud, out nextDelay);
            }
            _keyFired = true;
            _opensThisCycle += Math.Max(1, remaining);
            if (loud)
                AutoSynthPlugin.Logger.LogInfo(
                    $"chest open-all key pressed (about {remaining} chest(s))");
            nextDelay = Math.Max(nextDelay, 2.5f);
            return TickResult.InProgress;
        }

        if (remaining <= 0)
            return Finish(loud);

        _keySettleTicks++;
        if (_keySettleTicks < KeySettleTicks)
        {
            nextDelay = Math.Max(nextDelay, 1.5f);
            return TickResult.InProgress;
        }

        // Still remaining after settle — retry the key a few times before giving up.
        if (_keyRetries < KeyMaxRetries)
        {
            _keyRetries++;
            _keySettleTicks = 0;
            if (GameInterop.TryInvokeOpenAllBoxes())
            {
                if (loud)
                    AutoSynthPlugin.Logger.LogInfo(
                        $"chest open-all retry {_keyRetries}/{KeyMaxRetries} (remaining≈{remaining})");
                nextDelay = Math.Max(nextDelay, 2.5f);
                return TickResult.InProgress;
            }
        }

        AutoSynthPlugin.Logger.LogWarning(
            $"chest phase: open-all left ≈{remaining} chest(s) after retries — ending phase");
        return Finish(loud);
    }

    // Shared scanner for OneTypeRight and SingleLeft. onePerSlot=true means one
    // click drains the whole stack (right-click rune); false means one chest each.
    private TickResult TickSlots(StageBox[] boxes, PointerEventData.InputButton button,
        bool onePerSlot, bool loud, ref float nextDelay)
    {
        bool anyWork = false;
        for (int n = 0; n < boxes.Length; n++)
        {
            int i = (_index + n) % boxes.Length;
            if (_slotDone[i] || _staleFails[i] >= StaleAbort) continue;

            var box = boxes[i];
            NotePriorClickResult(i, box);

            if (_slotDone[i] || _staleFails[i] >= StaleAbort) continue;
            if (!SlotOpenable(box, out string why))
            {
                if (onePerSlot) _slotDone[i] = true;
                if (loud && why != null && why != "inactive")
                    AutoSynthPlugin.Logger.LogInfo($"chest skip {Label(box)}: {why}");
                continue;
            }

            anyWork = true;
            _index = (i + 1) % boxes.Length;
            int before = GameInterop.BoxCount(box.m_boxType);
            if (!TryClickOpen(box, button, loud))
            {
                if (onePerSlot) _slotDone[i] = true;
                continue;
            }

            _opensThisCycle += onePerSlot ? Math.Max(1, before) : 1;
            _countBeforeClick[i] = before;
            if (onePerSlot) _slotDone[i] = true;
            nextDelay = Math.Max(nextDelay, OpenDelay(box));
            return TickResult.InProgress;
        }

        if (!anyWork)
            return Finish(loud);
        nextDelay = 1.5f;
        return TickResult.InProgress;
    }

    // After a prior click, compare live count. Pure SlotOpenable stays free of this.
    private void NotePriorClickResult(int slot, StageBox box)
    {
        if (_countBeforeClick[slot] < 0 || box == null) return;
        int now = GameInterop.BoxCount(box.m_boxType);
        if (now < 0) { _countBeforeClick[slot] = -1; return; }

        if (now < _countBeforeClick[slot])
        {
            _staleFails[slot] = 0;
            _countBeforeClick[slot] = -1;
            return;
        }

        // Count did not drop — click was ineffective for this accessor/UI state.
        _staleFails[slot]++;
        _countBeforeClick[slot] = -1;
        if (_staleFails[slot] >= StaleAbort)
            _slotDone[slot] = true;
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
        bool anyUnknown = false;
        foreach (var box in boxes)
        {
            if (!SlotOpenable(box, out _)) continue;
            int c = GameInterop.BoxCount(box.m_boxType);
            if (c > 0) sum += c;
            else if (c < 0) anyUnknown = true;
        }
        if (sum > 0) return sum;
        return anyUnknown ? 1 : 0;
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

    // Pure openability check — no mutation of stale/done state.
    private static bool SlotOpenable(StageBox box, out string whySkip)
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
        if (count > 0) return true;
        if (count == 0)
        {
            whySkip = "count=0";
            return false;
        }

        // Unknown (-1): allow a click if the detector is live.
        var detector = box.m_clickDetector;
        if (detector == null || !detector.gameObject.activeInHierarchy)
        {
            whySkip = "click detector missing";
            return false;
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
