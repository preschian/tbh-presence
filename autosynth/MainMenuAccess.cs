using UnityEngine;

namespace TbhAutoSynth;

// Single owner for "content row (Stash/Stat/Cube/Rune/Portal) must be visible".
// Opens via UI_Stage.button_ShowMain only — never synthesizes Tab / OS keys.
internal static class MainMenuAccess
{
    internal enum Status
    {
        Open,      // content row is visible
        Waiting,   // clicked recently or settling — do not click again
        Failed,    // cannot open right now (missing button or click budget spent)
    }

    const int MaxClicks = 3;
    const float SettleSeconds = 1.5f;
    const float RetrySeconds = 10f;
    const float HudPollSeconds = 0.5f;

    static int _clicks;
    static float _awaitUntil;
    static float _nextRetryAt;
    static bool _loggedWaitingHud;

    internal static void Reset()
    {
        _clicks = 0;
        _awaitUntil = 0f;
        _nextRetryAt = 0f;
        _loggedWaitingHud = false;
    }

    internal static bool IsOpen() => GameInterop.IsMainMenuOpen();

    // Honest status: Open only when the content row is actually visible.
    // After a Show Main click, returns Waiting until settle so we never toggle it shut.
    internal static Status Ensure(bool loud)
    {
        if (IsOpen())
        {
            Reset();
            return Status.Open;
        }

        if (Time.unscaledTime < _awaitUntil)
            return Status.Waiting;

        if (_clicks >= MaxClicks)
            return Status.Failed;

        if (Time.unscaledTime < _nextRetryAt)
            return Status.Waiting;

        // Show Main already "on" but chrome not ready — wait, do not toggle off.
        var showMain = GameInterop.FindShowMainButton();
        if (showMain != null && showMain.gameObject.activeInHierarchy && GameInterop.IsOn(showMain))
        {
            _awaitUntil = Time.unscaledTime + SettleSeconds;
            return Status.Waiting;
        }

        if (!GameInterop.IsHudReadyForShowMain())
        {
            if (loud && !_loggedWaitingHud)
            {
                _loggedWaitingHud = true;
                AutoSynthPlugin.Logger.LogInfo(
                    "auto-open menu: waiting for UIManager/UI_Hero before Show Main");
            }
            _awaitUntil = Time.unscaledTime + HudPollSeconds;
            return Status.Waiting;
        }

        if (!GameInterop.TryClickShowMain())
        {
            // Issued or not — give the HUD time; do not burn the click budget on a throw.
            _awaitUntil = Time.unscaledTime + SettleSeconds;
            _nextRetryAt = Time.unscaledTime + RetrySeconds;
            return Status.Waiting;
        }

        _clicks++;
        _awaitUntil = Time.unscaledTime + SettleSeconds;
        _nextRetryAt = Time.unscaledTime + RetrySeconds;
        if (loud)
            AutoSynthPlugin.Logger.LogInfo(
                $"auto-open menu: Show Main click {_clicks}/{MaxClicks}");
        return Status.Waiting;
    }
}
