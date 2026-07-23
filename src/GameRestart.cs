using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace TbhCompanion
{
    // TaskBarHero process lifecycle: user launch, and optional scheduled
    // close/relaunch after a configurable uptime (AppSettings). Heavy work
    // runs on a background thread so the presence loop stays responsive.
    static class GameRestart
    {
        const string GAME = "TaskBarHero";
        const int SteamAppId = 3678970;
        const int SteamAppearSeconds = 45;
        const int ExeAppearSeconds = 20;

        static DateTime _cooldownUntilUtc = DateTime.MinValue;
        static int _busy; // 0 idle, 1 launch/restart in flight

        public static bool IsBusy { get { return _busy != 0; } }

        public static bool IsGameRunning() { return FindGame() != null; }

        // True while a launch/restart is in flight and the game process is not up yet.
        public static bool IsLaunching() { return _busy != 0 && FindGame() == null; }

        // Queue Steam → wait → exe fallback on a worker. Returns false if the game
        // is already running or another launch/restart holds the busy lock.
        public static bool TryBeginLaunch(Action<string> log = null)
        {
            if (FindGame() != null) return false;
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return false;

            string exePath = ResolveExePath(null);
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { LaunchAndConfirm(exePath, log, delegate { return true; }); }
                finally { Interlocked.Exchange(ref _busy, 0); }
            });
            return true;
        }

        // Block until an in-flight restart finishes (tray shutdown).
        public static void WaitIdle(int timeoutMs)
        {
            int waited = 0;
            while (_busy != 0 && waited < timeoutMs)
            {
                Thread.Sleep(100);
                waited += 100;
            }
        }

        public static bool IsDue(Process proc)
        {
            if (!AppSettings.AutoRestartEnabled) return false;
            AppSettings.EnsureRestartArmed();
            if (DateTime.UtcNow < _cooldownUntilUtc) return false;
            if (_busy != 0) return false;
            int days = AppSettings.AutoRestartDays;
            if (days < 1 || proc == null) return false;
            try
            {
                // Local StartTime → UTC; arm stamp is UTC. Use the later origin so
                // enabling mid-session waits a full period from the arm time.
                DateTime originUtc = proc.StartTime.ToUniversalTime();
                DateTime? armed = AppSettings.AutoRestartArmedUtc;
                if (armed.HasValue && armed.Value > originUtc) originUtc = armed.Value;
                return (DateTime.UtcNow - originUtc) >= TimeSpan.FromDays(days);
            }
            catch { return false; }
        }

        // If a restart is due: run beforeClose, hand work to a background thread,
        // and return true so the caller can detach. Returns false when not due.
        public static bool TryRestartIfDue(Process proc, Action beforeClose, Action<string> log, Func<bool> keepGoing)
        {
            if (!IsDue(proc)) return false;
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return false;

            _cooldownUntilUtc = DateTime.UtcNow.AddMinutes(10);
            if (beforeClose != null)
            {
                try { beforeClose(); } catch { }
            }

            // Capture what we need — Process may dispose after the caller breaks out.
            int pid = 0;
            try { if (proc != null) pid = proc.Id; } catch { }
            string exePath = ResolveExePath(proc);
            int days = AppSettings.AutoRestartDays;
            Func<bool> alive = keepGoing ?? (delegate { return true; });

            ThreadPool.QueueUserWorkItem(delegate
            {
                try { RunRestart(pid, exePath, days, log, alive); }
                finally { Interlocked.Exchange(ref _busy, 0); }
            });
            return true;
        }

        static void RunRestart(int pid, string exePath, int days, Action<string> log, Func<bool> keepGoing)
        {
            if (log != null)
                log("scheduled restart after " + days + " day(s) — closing TaskBarHero...");

            Process proc = null;
            try { if (pid > 0) proc = Process.GetProcessById(pid); } catch { }

            bool closed = CloseGame(proc, keepGoing);
            if (!closed)
            {
                // Process may already be gone (exited while we looked it up).
                try { closed = proc == null || proc.HasExited; } catch { closed = FindGame() == null; }
            }
            if (!closed)
            {
                if (log != null) log("scheduled restart: could not close the game");
                return;
            }

            // Always attempt relaunch once closed — even if the companion is quitting —
            // so tray shutdown cannot leave the game dead.
            SleepInterruptible(2000, keepGoing);

            if (LaunchAndConfirm(exePath, log, keepGoing)) return;
            if (log != null) log("scheduled restart: game closed, but relaunch failed");
        }

        static bool CloseGame(Process proc, Func<bool> keepGoing)
        {
            if (proc == null) return false;
            try
            {
                if (proc.HasExited) return true;
                bool closed = false;
                try { closed = proc.CloseMainWindow(); } catch { }
                if (closed)
                    WaitForExitInterruptible(proc, 20000, keepGoing);
                if (!proc.HasExited)
                {
                    try { proc.Kill(); } catch { }
                    // Finish the kill even if the companion is stopping.
                    WaitForExitInterruptible(proc, 15000, delegate { return true; });
                }
                return proc.HasExited;
            }
            catch
            {
                try
                {
                    if (!proc.HasExited) { proc.Kill(); WaitForExitInterruptible(proc, 10000, delegate { return true; }); }
                    return proc.HasExited;
                }
                catch { return false; }
            }
        }

        // Steam protocol open is not proof of launch — wait for the process, then
        // fall back to the exe if it never appears.
        static bool LaunchAndConfirm(string exePath, Action<string> log, Func<bool> keepGoing)
        {
            bool steamOpened = false;
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "steam://rungameid/" + SteamAppId,
                    UseShellExecute = true
                });
                steamOpened = true;
                if (log != null) log("scheduled restart: launching via Steam...");
            }
            catch { }

            // Always wait the full window for the process — even if the companion
            // is quitting — so we don't abandon relaunch mid-flight.
            if (steamOpened && WaitForGame(SteamAppearSeconds))
                return true;

            if (steamOpened && log != null)
                log("scheduled restart: Steam didn't start the game — trying exe...");

            if (!LaunchExe(exePath, log)) return false;
            return WaitForGame(ExeAppearSeconds);
        }

        static bool LaunchExe(string exePath, Action<string> log)
        {
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return false;
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    WorkingDirectory = Path.GetDirectoryName(exePath),
                    UseShellExecute = true
                });
                if (log != null) log("scheduled restart: launching TaskBarHero...");
                return true;
            }
            catch { return false; }
        }

        static string ResolveExePath(Process proc)
        {
            try { if (proc != null) return proc.MainModule.FileName; } catch { }
            string dir = AutoSynthDeploy.FindGameDir();
            return dir != null ? Path.Combine(dir, "TaskBarHero.exe") : null;
        }

        static bool WaitForGame(int seconds)
        {
            for (int i = 0; i < seconds * 2; i++)
            {
                if (FindGame() != null) return true;
                Thread.Sleep(500);
            }
            return FindGame() != null;
        }

        static Process FindGame()
        {
            try
            {
                var p = Process.GetProcessesByName(GAME);
                return p.Length > 0 ? p[0] : null;
            }
            catch { return null; }
        }

        static void WaitForExitInterruptible(Process proc, int timeoutMs, Func<bool> keepGoing)
        {
            int slice = 500;
            int waited = 0;
            while (waited < timeoutMs)
            {
                try { if (proc.HasExited) return; } catch { return; }
                if (keepGoing != null && !keepGoing()) return;
                try { if (proc.WaitForExit(slice)) return; } catch { return; }
                waited += slice;
            }
        }

        static void SleepInterruptible(int ms, Func<bool> keepGoing)
        {
            int waited = 0;
            while (waited < ms)
            {
                if (keepGoing != null && !keepGoing()) return;
                Thread.Sleep(100);
                waited += 100;
            }
        }
    }
}
