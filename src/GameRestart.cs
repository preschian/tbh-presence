using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace TbhCompanion
{
    // Closes and relaunches TaskBarHero after a configurable uptime, so long
    // idle sessions can shed accumulated RAM. Opt-in via AppSettings.
    static class GameRestart
    {
        const int SteamAppId = 3678970;
        static DateTime _cooldownUntilUtc = DateTime.MinValue;

        public static bool IsDue(Process proc)
        {
            if (!AppSettings.AutoRestartEnabled) return false;
            if (DateTime.UtcNow < _cooldownUntilUtc) return false;
            int days = AppSettings.AutoRestartDays;
            if (days < 1 || proc == null) return false;
            try
            {
                return (DateTime.Now - proc.StartTime) >= TimeSpan.FromDays(days);
            }
            catch { return false; }
        }

        // Kill the running game and start it again. Returns true if a relaunch
        // was attempted. Sets a cooldown so a failed launch cannot loop.
        public static bool TryRestart(Process proc, Action<string> log)
        {
            int days = AppSettings.AutoRestartDays;
            _cooldownUntilUtc = DateTime.UtcNow.AddMinutes(10);

            string exePath = null;
            try { if (proc != null) exePath = proc.MainModule.FileName; } catch { }
            if (exePath == null)
            {
                string dir = AutoSynthDeploy.FindGameDir();
                if (dir != null) exePath = Path.Combine(dir, "TaskBarHero.exe");
            }

            if (log != null)
                log("scheduled restart after " + days + " day(s) — closing TaskBarHero...");

            if (!CloseGame(proc))
            {
                if (log != null) log("scheduled restart: could not close the game");
                return false;
            }

            // Brief pause so Steam / file locks settle before relaunch.
            Thread.Sleep(2000);

            if (LaunchViaSteam(log)) return true;
            if (LaunchExe(exePath, log)) return true;

            if (log != null) log("scheduled restart: game closed, but relaunch failed");
            return false;
        }

        static bool CloseGame(Process proc)
        {
            if (proc == null) return false;
            try
            {
                if (proc.HasExited) return true;
                // Prefer a graceful close so the game can autosave; fall back to Kill.
                bool closed = false;
                try { closed = proc.CloseMainWindow(); } catch { }
                if (closed)
                {
                    if (proc.WaitForExit(20000)) return true;
                }
                if (!proc.HasExited)
                {
                    proc.Kill();
                    proc.WaitForExit(15000);
                }
                return proc.HasExited;
            }
            catch
            {
                try { if (!proc.HasExited) { proc.Kill(); proc.WaitForExit(10000); } }
                catch { return false; }
                try { return proc.HasExited; } catch { return false; }
            }
        }

        static bool LaunchViaSteam(Action<string> log)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "steam://rungameid/" + SteamAppId,
                    UseShellExecute = true
                });
                if (log != null) log("scheduled restart: launching via Steam...");
                return true;
            }
            catch { return false; }
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
    }
}
