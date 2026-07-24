using System;
using TaskbarHero;
using TaskbarHero.UI;
using TS;
using UnityEngine;
using UnityEngine.EventSystems;
using Object = UnityEngine.Object;

namespace TbhAutoSynth;

// Single owner for "content row (Stash/Stat/Cube/Rune/Portal) must be visible",
// plus clicking a named row toggle once it is. Opens via UI_Stage.button_ShowMain
// only — never synthesizes Tab / OS keys / direct UIManager panel show.
internal static class MainMenuAccess
{
    internal enum Status
    {
        Open,      // content row is visible
        Waiting,   // settling / HUD not ready / backoff — not a failure
        Failed,    // Show Main budget spent, stuck toggle, or HUD never ready
    }

    // Result of ensuring the row (and optionally clicking a menu toggle).
    internal readonly struct Outcome
    {
        public readonly Status Status;
        public readonly float NextDelay;
        // True only for a real Show Main click / failed click / Failed terminal —
        // callers must not count pure Waiting polls toward open budgets.
        public readonly bool SpentAttempt;

        public Outcome(Status status, float nextDelay, bool spentAttempt)
        {
            Status = status;
            NextDelay = nextDelay;
            SpentAttempt = spentAttempt;
        }
    }

    internal enum PanelResult
    {
        Clicked,   // clicked the Cube/Rune/... menu toggle
        Waiting,   // menu still opening — poll again after NextDelay
        Failed,    // cannot open menu; stop retrying for now
    }

    const int MaxClicks = 3;
    const int MaxFailedClicks = 3;
    const float SettleSeconds = 1.5f;
    const float RetrySeconds = 10f;
    const float HudPollSeconds = 0.5f;
    const float StuckOnSeconds = 8f;
    const float HudWaitDeadlineSeconds = 60f;

    static int _clicks;
    static int _failedClicks;
    static float _awaitUntil;
    static float _nextRetryAt;
    static float _stuckOnSince = -1f;
    static float _hudWaitStarted = -1f;
    static bool _loggedWaitingHud;
    static bool _loggedFailed;

    internal static void Reset()
    {
        _clicks = 0;
        _failedClicks = 0;
        _awaitUntil = 0f;
        _nextRetryAt = 0f;
        _stuckOnSince = -1f;
        _hudWaitStarted = -1f;
        _loggedWaitingHud = false;
        _loggedFailed = false;
    }

    internal static bool IsOpen() => GameInterop.IsMainMenuOpen();

    internal static ToggleButton FindShowMainButton()
    {
        try
        {
            var stage = Object.FindObjectOfType<UI_Stage>(true);
            return stage != null ? stage.button_ShowMain : null;
        }
        catch { return null; }
    }

    // Ensure content row visible, then click the named menu toggle (Cube/Rune/...).
    // spentAttempt is true for a real Show Main / panel click (or terminal Failed),
    // false for settle/HUD Waiting polls — runners must not burn budgets on those.
    internal static PanelResult TryOpenContentPanel(
        string menuName, string clickLogName, bool loud,
        out float nextDelay, out bool spentAttempt)
    {
        spentAttempt = false;
        var btn = GameInterop.FindMenuToggle(menuName);
        if (btn != null && btn.gameObject.activeInHierarchy)
        {
            GameInterop.Click(btn, clickLogName, loud);
            nextDelay = 10f;
            spentAttempt = true;
            return PanelResult.Clicked;
        }

        var ensured = Ensure(loud);
        nextDelay = ensured.NextDelay;
        spentAttempt = ensured.SpentAttempt;

        if (ensured.Status == Status.Failed)
            return PanelResult.Failed;

        if (ensured.Status == Status.Open)
        {
            btn = GameInterop.FindMenuToggle(menuName);
            if (btn != null && btn.gameObject.activeInHierarchy)
            {
                GameInterop.Click(btn, clickLogName, loud);
                nextDelay = 10f;
                spentAttempt = true;
                return PanelResult.Clicked;
            }
            nextDelay = 0.25f;
            return PanelResult.Waiting;
        }

        return PanelResult.Waiting;
    }

    // Honest status: Open only when the content row is actually visible.
    internal static Outcome Ensure(bool loud)
    {
        if (IsOpen())
        {
            Reset();
            return new Outcome(Status.Open, 0.25f, false);
        }

        if (Time.unscaledTime < _awaitUntil)
            return new Outcome(Status.Waiting, Math.Max(0.05f, _awaitUntil - Time.unscaledTime), false);

        if (_clicks >= MaxClicks || _failedClicks >= MaxFailedClicks)
            return Fail(loud, "Show Main click budget spent");

        if (Time.unscaledTime < _nextRetryAt)
            return new Outcome(Status.Waiting, Math.Max(0.05f, _nextRetryAt - Time.unscaledTime), false);

        // Show Main already "on" but chrome not ready — wait, do not toggle off.
        var showMain = FindShowMainButton();
        if (showMain != null && showMain.gameObject.activeInHierarchy && GameInterop.IsOn(showMain))
        {
            if (_stuckOnSince < 0f)
                _stuckOnSince = Time.unscaledTime;
            if (Time.unscaledTime - _stuckOnSince >= StuckOnSeconds)
                return Fail(loud, "Show Main is on but content row never appeared");

            _awaitUntil = Time.unscaledTime + SettleSeconds;
            return new Outcome(Status.Waiting, SettleSeconds, false);
        }
        _stuckOnSince = -1f;

        if (!TryGetReadyShowMain(out showMain))
        {
            if (_hudWaitStarted < 0f)
                _hudWaitStarted = Time.unscaledTime;
            if (Time.unscaledTime - _hudWaitStarted >= HudWaitDeadlineSeconds)
                return Fail(loud, "UIManager/UI_Hero/Show Main never became ready");

            if (loud && !_loggedWaitingHud)
            {
                _loggedWaitingHud = true;
                AutoSynthPlugin.Logger.LogInfo(
                    "auto-open menu: waiting for UIManager/UI_Hero before Show Main");
            }
            _awaitUntil = Time.unscaledTime + HudPollSeconds;
            return new Outcome(Status.Waiting, HudPollSeconds, false);
        }
        _hudWaitStarted = -1f;

        if (!TryClickShowMain(showMain))
        {
            _failedClicks++;
            _awaitUntil = Time.unscaledTime + SettleSeconds;
            _nextRetryAt = Time.unscaledTime + RetrySeconds;
            if (_failedClicks >= MaxFailedClicks)
                return Fail(loud, "Show Main click failed repeatedly");
            return new Outcome(Status.Waiting, RetrySeconds, true);
        }

        _clicks++;
        _stuckOnSince = -1f;
        _awaitUntil = Time.unscaledTime + SettleSeconds;
        _nextRetryAt = Time.unscaledTime + RetrySeconds;
        if (loud)
            AutoSynthPlugin.Logger.LogInfo(
                $"auto-open menu: Show Main click {_clicks}/{MaxClicks}");
        return new Outcome(Status.Waiting, SettleSeconds, true);
    }

    static Outcome Fail(bool loud, string reason)
    {
        if (loud && !_loggedFailed)
        {
            _loggedFailed = true;
            AutoSynthPlugin.Logger.LogWarning($"auto-open menu: {reason}");
        }
        return new Outcome(Status.Failed, RetrySeconds, true);
    }

    // EventSystem + UIManager + UI_Hero + Show Main button all present.
    static bool TryGetReadyShowMain(out ToggleButton button)
    {
        button = null;
        try
        {
            if (EventSystem.current == null) return false;
            var um = Object.FindObjectOfType<UIManager>(true);
            if (um == null || um.ui_main == null || um.Ui_Hero == null)
                return false;
            var stage = Object.FindObjectOfType<UI_Stage>(true);
            if (stage == null) return false;
            button = stage.button_ShowMain;
            return button != null && button.gameObject.activeInHierarchy;
        }
        catch
        {
            button = null;
            return false;
        }
    }

    // Game handler often NREs inside UI_Hero after chrome is already up; if the row
    // is visible afterward, treat as success.
    static bool TryClickShowMain(ToggleButton btn)
    {
        if (btn == null) return false;
        try
        {
            var inner = GameInterop.InnerButton(btn);
            if (inner == null || inner.onClick == null)
            {
                AutoSynthPlugin.Logger.LogWarning("auto-open menu: Show Main has no inner onClick");
                return false;
            }
            inner.onClick.Invoke();
            AutoSynthPlugin.Logger.LogInfo("clicked Show Main (stage HUD) (+inner onClick)");
            return true;
        }
        catch (Exception)
        {
            if (IsOpen())
            {
                AutoSynthPlugin.Logger.LogInfo("clicked Show Main (stage HUD)");
                return true;
            }
            return false;
        }
    }
}
