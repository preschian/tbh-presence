using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace TbhPresence
{
    // Keeps the TbhAutoSynth BepInEx plugin installed in the game folder, so
    // launching TbhPresence.exe is enough to have both features active.
    // Requires BepInEx to already be installed in the game folder; if it is
    // not, this does nothing (the presence app itself stays read-only).
    static class AutoSynthDeploy
    {
        static DateTime _nextAttempt = DateTime.MinValue;

        // Cheap to call from a poll loop: does real work at most every 10 minutes.
        public static void TryDeployThrottled(Action<string> log)
        {
            if (DateTime.UtcNow < _nextAttempt) return;
            _nextAttempt = DateTime.UtcNow.AddMinutes(10);
            TryDeploy(log);
        }

        public static void TryDeploy(Action<string> log)
        {
            try
            {
                string gameDir = FindGameDir();
                if (gameDir == null) { log("autosynth: game folder not found, skipped"); return; }

                string plugins = Path.Combine(gameDir, "BepInEx", "plugins");
                if (!Directory.Exists(plugins))
                {
                    log("autosynth: BepInEx not installed in " + gameDir + ", skipped (see autosynth/README.md)");
                    return;
                }

                byte[] src = LoadPluginBytes();
                if (src == null) { log("autosynth: plugin dll not bundled, skipped"); return; }

                string target = Path.Combine(plugins, "TbhAutoSynth.dll");
                if (File.Exists(target) && SameBytes(File.ReadAllBytes(target), src))
                    return; // already up to date

                File.WriteAllBytes(target, src);
                log("autosynth: plugin deployed to " + target + " (active after next game start)");
            }
            catch (IOException)
            {
                // dll locked by the running game - an update will land on a later start
                log("autosynth: plugin update pending (game is running)");
            }
            catch (Exception ex)
            {
                log("autosynth: deploy skipped: " + ex.Message);
            }
        }

        static string FindGameDir()
        {
            // 1) a running game tells us exactly where it lives
            try
            {
                var p = Process.GetProcessesByName("TaskBarHero");
                if (p.Length > 0) return Path.GetDirectoryName(p[0].MainModule.FileName);
            }
            catch { }

            // 2) walk the Steam libraries
            try
            {
                var steam = Microsoft.Win32.Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
                if (steam != null)
                {
                    steam = steam.Replace('/', '\\');
                    string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                    if (File.Exists(vdf))
                    {
                        foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                        {
                            string lib = m.Groups[1].Value.Replace("\\\\", "\\");
                            string dir = Path.Combine(lib, "steamapps", "common", "TaskbarHero");
                            if (File.Exists(Path.Combine(dir, "TaskBarHero.exe"))) return dir;
                        }
                    }
                    string fallback = Path.Combine(steam, "steamapps", "common", "TaskbarHero");
                    if (File.Exists(Path.Combine(fallback, "TaskBarHero.exe"))) return fallback;
                }
            }
            catch { }
            return null;
        }

        static byte[] LoadPluginBytes()
        {
            // a dll sitting next to the exe wins over the embedded copy
            try
            {
                string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TbhAutoSynth.dll");
                if (File.Exists(local)) return File.ReadAllBytes(local);
            }
            catch { }

            try
            {
                using (var s = typeof(AutoSynthDeploy).Assembly.GetManifestResourceStream("TbhAutoSynth.dll"))
                {
                    if (s == null) return null;
                    using (var ms = new MemoryStream())
                    {
                        s.CopyTo(ms);
                        return ms.ToArray();
                    }
                }
            }
            catch { return null; }
        }

        static bool SameBytes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }
}
