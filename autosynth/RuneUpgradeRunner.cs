using System;
using TaskbarHero;
using TaskbarHero.Data;
using TaskbarHero.UI;
using TaskbarHero.UI.Rune;
using TS;
using UnityEngine;
using UnityEngine.UI;

namespace TbhAutoSynth;

// Owns the Rune phase: open panel, pick cheapest affordable upgrade, purchase via
// level-up button, verify, then close. Kept out of AutoSynthBehaviour so the cube
// loop stays an orchestrator.
internal sealed class RuneUpgradeRunner
{
    internal enum TickResult { InProgress, Done }

    private enum ConfirmResult { Confirmed, AwaitingLevel, Failed }

    private const int MaxOpenAttempts = 6;
    private const float OpenTimeoutSeconds = 60f;
    private const int FailStreakAbort = 5;
    private const int SoftAwaitTicks = 6; // ~ several AfterRuneUpgradeDelay windows

    private readonly Action<ButtonBase, string, bool> _click;

    private UI_Rune _runeUi;
    private RuneTooltip _runeTooltip;

    internal RuneUpgradeRunner(Action<ButtonBase, string, bool> click)
    {
        _click = click ?? throw new ArgumentNullException(nameof(click));
    }

    private float _nextOpenAttempt;
    private int _openFails;
    private int _openAttempts;
    private float _phaseEnteredAt;

    private int _upgradesThisCycle;
    private int _pendingKey = -1;
    private int _pendingLevel = -1;
    private long _pendingGold = -1;
    private int _pendingCost;
    private bool _pendingCounted;   // counted via gold drop while still awaiting level
    private int _awaitLevelTicks;
    private int _failStreak;
    private string _lastName = "";

    internal int UpgradesThisCycle => _upgradesThisCycle;
    internal int LastUpgrades { get; private set; }

    internal void BeginPhase()
    {
        _upgradesThisCycle = 0;
        ClearPending();
        _failStreak = 0;
        _openFails = 0;
        _openAttempts = 0;
        _nextOpenAttempt = 0f;
        _phaseEnteredAt = Time.unscaledTime;
        _lastName = "";
    }

    internal void ResetSession()
    {
        BeginPhase();
        LastUpgrades = 0;
        _runeUi = null;
        _runeTooltip = null;
    }

    private void ClearPending()
    {
        _pendingKey = -1;
        _pendingLevel = -1;
        _pendingGold = -1;
        _pendingCost = 0;
        _pendingCounted = false;
        _awaitLevelTicks = 0;
    }

    private bool AtUpgradeCap =>
        _upgradesThisCycle >= AutoSynthPlugin.MaxRuneUpgradesPerCycle;

    // Runs one rune tick. When Done, the panel is closed and LastUpgrades is set.
    internal TickResult Tick(bool loud, out float nextDelay)
    {
        nextDelay = 1.5f;

        if (AtUpgradeCap && _pendingKey < 0)
        {
            if (loud)
                AutoSynthPlugin.Logger.LogInfo(
                    $"rune phase: hit MaxRuneUpgradesPerCycle={AutoSynthPlugin.MaxRuneUpgradesPerCycle}");
            return Finish(loud);
        }

        var runeUi = FindRuneUi();
        if (!IsOpen(runeUi))
        {
            if (!AutoSynthPlugin.AutoOpenRune)
            {
                AutoSynthPlugin.Logger.LogWarning(
                    "rune phase: panel closed and AutoOpenRune=false — ending phase");
                return Finish(loud);
            }
            if (Time.unscaledTime - _phaseEnteredAt >= OpenTimeoutSeconds
                || _openAttempts >= MaxOpenAttempts)
            {
                AutoSynthPlugin.Logger.LogWarning(
                    $"rune phase: could not open panel (attempts={_openAttempts}) — ending phase");
                return Finish(loud);
            }
            if (TryOpenRune())
                _openAttempts++;
            nextDelay = 1.5f;
            return TickResult.InProgress;
        }

        var page = runeUi.m_runePage;
        if (page == null)
        {
            AutoSynthPlugin.Logger.LogWarning("rune phase: UI_Rune.m_runePage is null, skipping");
            return Finish(loud);
        }

        // Never buy while a prior purchase is still being confirmed.
        if (_pendingKey >= 0)
        {
            var confirm = ConfirmPending(page, loud);
            if (confirm == ConfirmResult.Failed && _failStreak >= FailStreakAbort)
                return Finish(loud);
            if (confirm == ConfirmResult.AwaitingLevel || confirm == ConfirmResult.Confirmed)
            {
                nextDelay = Math.Max(0.75f, AutoSynthPlugin.AfterRuneUpgradeDelay);
                // Confirmed clears pending; fall through next tick (or same if we continue).
                if (confirm == ConfirmResult.AwaitingLevel)
                    return TickResult.InProgress;
            }
        }

        if (AtUpgradeCap)
        {
            if (loud)
                AutoSynthPlugin.Logger.LogInfo(
                    $"rune phase: hit MaxRuneUpgradesPerCycle={AutoSynthPlugin.MaxRuneUpgradesPerCycle}");
            return Finish(loud);
        }

        long gold = ReadGold(page);
        if (gold < 0)
        {
            AutoSynthPlugin.Logger.LogWarning(
                "rune phase: gold unknown — skipping purchases this cycle");
            return Finish(loud);
        }

        if (!TryFindCheapestUpgradeable(page, gold, out var best, out var cost, out var key, out var level))
        {
            if (loud || _upgradesThisCycle == 0)
                AutoSynthPlugin.Logger.LogInfo(
                    $"rune phase: no affordable upgrade (gold={gold}, upgrades so far={_upgradesThisCycle})");
            return Finish(loud);
        }

        if (loud)
            AutoSynthPlugin.Logger.LogInfo(
                $"rune phase: cheapest key={key} lv={level} cost={cost} gold={gold} name='{_lastName}'");

        if (!TryUpgradeRune(best, key, level, cost, loud))
        {
            AutoSynthPlugin.Logger.LogWarning($"rune phase: upgrade invoke failed for key={key}");
            if (NoteFailure()) return Finish(loud);
            nextDelay = AutoSynthPlugin.AfterRuneUpgradeDelay;
            return TickResult.InProgress;
        }

        _pendingKey = key;
        _pendingLevel = level;
        _pendingGold = gold;
        _pendingCost = cost;
        _pendingCounted = false;
        _awaitLevelTicks = 0;
        nextDelay = Math.Max(0.75f, AutoSynthPlugin.AfterRuneUpgradeDelay);
        return TickResult.InProgress;
    }

    private TickResult Finish(bool loud)
    {
        ClosePanel(loud || _upgradesThisCycle > 0);
        LastUpgrades = _upgradesThisCycle;
        ClearPending();
        return TickResult.Done;
    }

    private ConfirmResult ConfirmPending(RunePage page, bool loud)
    {
        var pending = FindRuneNode(page, _pendingKey);
        int after = RuneLevel(pending);
        long goldNow = ReadGold(page);
        bool leveled = after > _pendingLevel;
        if (leveled)
        {
            if (!_pendingCounted)
                _upgradesThisCycle++;
            _failStreak = 0;
            if (loud)
                AutoSynthPlugin.Logger.LogInfo(
                    $"rune upgrade ok: key={_pendingKey} lv {_pendingLevel}->{after} " +
                    $"gold {_pendingGold}->{goldNow}");
            ClearPending();
            return ConfirmResult.Confirmed;
        }

        // Gold spent but level lagging: count once, keep awaiting level before next buy.
        long minDrop = _pendingCost > 0 ? _pendingCost : 1;
        bool spent = _pendingGold >= 0 && goldNow >= 0 && goldNow <= _pendingGold - minDrop;
        if (spent || _pendingCounted)
        {
            if (!_pendingCounted)
            {
                _upgradesThisCycle++;
                _pendingCounted = true;
                _failStreak = 0;
                if (loud)
                    AutoSynthPlugin.Logger.LogInfo(
                        $"rune upgrade ok (gold spent, awaiting level): key={_pendingKey} lv={after} " +
                        $"gold {_pendingGold}->{goldNow}");
            }
            _awaitLevelTicks++;
            if (_awaitLevelTicks >= SoftAwaitTicks)
            {
                // Level never caught up; accept gold proof and release the buy lock.
                if (loud)
                    AutoSynthPlugin.Logger.LogInfo(
                        $"rune upgrade: level still lagging for key={_pendingKey} after {_awaitLevelTicks} ticks — continuing");
                ClearPending();
                return ConfirmResult.Confirmed;
            }
            return ConfirmResult.AwaitingLevel;
        }

        NoteFailure();
        AutoSynthPlugin.Logger.LogWarning(
            $"rune upgrade no effect: key={_pendingKey} lv={_pendingLevel}->{after} " +
            $"gold={goldNow} failStreak={_failStreak}");
        ClearPending();
        return ConfirmResult.Failed;
    }

    private bool NoteFailure()
    {
        _failStreak++;
        return _failStreak >= FailStreakAbort;
    }

    private UI_Rune FindRuneUi()
    {
        if (_runeUi == null)
            _runeUi = UnityEngine.Object.FindObjectOfType<UI_Rune>(true);
        return _runeUi;
    }

    private static bool IsOpen(UI_Rune rune)
        => rune != null && rune.gameObject.activeInHierarchy;

    private ToggleButton RuneMenuButton()
    {
        return GameInterop.FindMenuToggle("Rune");
    }

    // Returns true when an open attempt was scheduled/issued (counts toward timeout).
    private bool TryOpenRune()
    {
        if (Time.unscaledTime < _nextOpenAttempt) return false;
        _nextOpenAttempt = Time.unscaledTime + 10f;

        var btn = RuneMenuButton();
        if (btn == null || !btn.gameObject.activeInHierarchy)
        {
            // Content row hidden — click stage-HUD Show Main before the Rune click.
            if (GameInterop.OpenMainMenu())
                AutoSynthPlugin.Logger.LogInfo(
                    "auto-open: clicked Show Main (Rune menu button " +
                    (btn == null ? "null" : "inactive") + ")");
            if (++_openFails == 3)
                AutoSynthPlugin.Logger.LogWarning(
                    "auto-open: Rune menu button not available " +
                    $"(button={(btn == null ? "null" : "inactive")}); " +
                    "open the Rune panel yourself and the loop will continue");
            return true; // still counts as an attempt window
        }
        _openFails = 0;
        _click(btn, "Rune menu button (auto-open)", true);
        return true;
    }

    private void ClosePanel(bool loud)
    {
        try
        {
            var runeUi = FindRuneUi();
            if (!IsOpen(runeUi)) return;
            if (ClickUnityButton(runeUi.Button_Close, "Rune close", loud)) return;
            if (ClickUnityButton(runeUi.Button_Close_Down, "Rune close (down)", loud)) return;
        }
        catch (Exception e)
        {
            AutoSynthPlugin.Logger.LogWarning("rune close failed: " + e.Message);
        }
    }

    private static long ReadGold(RunePage page)
    {
        try
        {
            var tmp = page != null ? page.m_goldText : null;
            if (tmp == null) return -1;
            return ParseGoldText(tmp.text);
        }
        catch { return -1; }
    }

    private bool TryUpgradeRune(RuneNode node, int key, int level, int cost, bool loud)
    {
        if (node == null) return false;
        try
        {
            var tip = FindRuneTooltip();
            if (tip != null)
            {
                GameInterop.ShowRuneTooltip(tip, node);
                tip.Show();
                try
                {
                    if (tip.m_runeNameText != null)
                        _lastName = tip.m_runeNameText.text ?? _lastName;
                }
                catch { }
            }
        }
        catch { }

        try
        {
            if (!ClickUnityButton(node.m_levelUpButton, "Rune level-up", loud))
                return false;
            if (loud)
                AutoSynthPlugin.Logger.LogInfo(
                    $"rune upgrade: clicked level-up key={key} lv={level} cost={cost} name='{_lastName}'");
            return true;
        }
        catch (Exception e)
        {
            AutoSynthPlugin.Logger.LogWarning($"rune upgrade failed: {e.Message}");
            return false;
        }
    }

    private RuneTooltip FindRuneTooltip()
    {
        if (_runeTooltip == null)
            _runeTooltip = UnityEngine.Object.FindObjectOfType<RuneTooltip>(true);
        return _runeTooltip;
    }

    private static int RuneUpgradeCost(RuneNode node)
    {
        if (node == null) return -1;
        int key = node.m_runeKey;
        int level = RuneLevel(node);
        try
        {
            if (level >= 0)
            {
                var next = GameInterop.LookupRuneLevelInfo(key, level + 1);
                int cost = GameInterop.RuneLevelCost(next);
                if (cost > 0) return cost;
            }
        }
        catch { }
        try
        {
            int cost = GameInterop.RuneLevelCost(GameInterop.RuneLevelInfoOf(node));
            if (cost > 0) return cost;
        }
        catch { }
        return -1;
    }

    private static int RuneLevel(RuneNode node)
    {
        if (node == null) return -1;
        return GameInterop.RuneLevel(node);
    }

    private static bool RuneCanUpgrade(RuneNode node)
    {
        if (node == null || !node.isActiveAndEnabled) return false;
        int level = RuneLevel(node);
        if (level < 0) return false;
        var next = GameInterop.LookupRuneLevelInfo(node.m_runeKey, level + 1);
        return GameInterop.RuneLevelCost(next) > 0;
    }

    private static RuneNode FindRuneNode(RunePage page, int key)
    {
        var list = page != null ? page.m_listRuneNode : null;
        if (list == null) return null;
        for (int i = 0; i < list.Count; i++)
        {
            var n = list[i];
            if (n != null && n.m_runeKey == key) return n;
        }
        return null;
    }

    // Cost/can-upgrade from DB only — no per-node tooltip bind while scanning.
    private bool TryFindCheapestUpgradeable(RunePage page, long gold,
        out RuneNode best, out int bestCost, out int bestKey, out int bestLevel)
    {
        best = null;
        bestCost = int.MaxValue;
        bestKey = -1;
        bestLevel = -1;
        _lastName = "";
        var list = page != null ? page.m_listRuneNode : null;
        if (list == null || gold < 0) return false;

        for (int i = 0; i < list.Count; i++)
        {
            var node = list[i];
            if (!RuneCanUpgrade(node)) continue;
            int cost = RuneUpgradeCost(node);
            if (cost <= 0 || cost > gold) continue;
            if (cost > bestCost) continue;
            if (cost == bestCost && best != null && node.m_runeKey >= bestKey) continue;
            best = node;
            bestCost = cost;
            bestKey = node.m_runeKey;
            bestLevel = RuneLevel(node);
        }
        return best != null;
    }

    private static long ParseGoldText(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return -1;
        var s = raw.Trim().Replace(",", "").Replace(" ", "").Replace("\u00A0", "");
        if (s.Length == 0) return -1;
        char suffix = char.ToUpperInvariant(s[s.Length - 1]);
        double mult = 1;
        if (suffix == 'K') { mult = 1_000; s = s.Substring(0, s.Length - 1); }
        else if (suffix == 'M') { mult = 1_000_000; s = s.Substring(0, s.Length - 1); }
        else if (suffix == 'B') { mult = 1_000_000_000; s = s.Substring(0, s.Length - 1); }
        var cleaned = new System.Text.StringBuilder(s.Length);
        bool sawDot = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c >= '0' && c <= '9') cleaned.Append(c);
            else if (c == '.' && !sawDot) { cleaned.Append(c); sawDot = true; }
        }
        if (cleaned.Length == 0) return -1;
        if (!double.TryParse(cleaned.ToString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var n))
            return -1;
        return (long)(n * mult);
    }

    private static bool ClickUnityButton(Button button, string name, bool loud)
    {
        if (button == null)
        {
            AutoSynthPlugin.Logger.LogWarning($"{name}: null");
            return false;
        }
        if (!button.gameObject.activeInHierarchy)
        {
            if (loud) AutoSynthPlugin.Logger.LogInfo($"{name}: inactive, skipped");
            return false;
        }
        if (!button.interactable)
        {
            if (loud) AutoSynthPlugin.Logger.LogInfo($"{name}: not interactable, skipped");
            return false;
        }
        if (button.onClick != null)
        {
            button.onClick.Invoke();
            if (loud) AutoSynthPlugin.Logger.LogInfo($"clicked {name}");
            return true;
        }
        AutoSynthPlugin.Logger.LogWarning($"{name}: no onClick");
        return false;
    }

    internal void Dump(Func<ToggleButton, string> describe)
    {
        try
        {
            var runeUi = FindRuneUi();
            AutoSynthPlugin.Logger.LogInfo(
                $"dump: runeOpen={(runeUi != null && runeUi.gameObject.activeInHierarchy)} " +
                $"runeMenuBtn={describe(RuneMenuButton())} autoUpgradeRune={AutoSynthPlugin.AutoUpgradeRune}");
            if (runeUi == null)
            {
                AutoSynthPlugin.Logger.LogInfo("dump: UI_Rune not found");
                return;
            }
            var page = runeUi.m_runePage;
            if (page == null)
            {
                AutoSynthPlugin.Logger.LogInfo("dump: RunePage null");
                return;
            }
            long gold = ReadGold(page);
            string goldRaw = page.m_goldText != null ? page.m_goldText.text : "(null)";
            AutoSynthPlugin.Logger.LogInfo($"dump: goldParsed={gold} goldText='{goldRaw}'");
            var list = page.m_listRuneNode;
            if (list == null)
            {
                AutoSynthPlugin.Logger.LogInfo("dump: m_listRuneNode null");
                return;
            }
            int shown = 0;
            for (int i = 0; i < list.Count; i++)
            {
                var node = list[i];
                if (node == null) continue;
                int cost = RuneUpgradeCost(node);
                int level = RuneLevel(node);
                var btn = node.m_levelUpButton;
                string btnState = btn == null ? "null"
                    : $"active={btn.gameObject.activeInHierarchy} interactable={btn.interactable}";
                bool can = RuneCanUpgrade(node);
                string levelInfo = "?";
                try
                {
                    var info = GameInterop.RuneLevelInfoOf(node);
                    if (info != null)
                        levelInfo = $"cost={GameInterop.RuneLevelCost(info)}";
                }
                catch { }
                if (!can && shown >= 12 && cost > gold && gold >= 0) continue;
                AutoSynthPlugin.Logger.LogInfo(
                    $"dump: rune[{i}] key={node.m_runeKey} lv={level} cost={cost} can={can} " +
                    $"btn=[{btnState}] levelInfo=[{levelInfo}]");
                shown++;
                if (shown >= 40) break;
            }
            AutoSynthPlugin.Logger.LogInfo($"dump: rune nodes listed={shown}/{list.Count}");
        }
        catch (Exception e)
        {
            AutoSynthPlugin.Logger.LogError($"dump rune failed: {e}");
        }
    }
}
