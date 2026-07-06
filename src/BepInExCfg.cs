using System;
using System.IO;
using System.Text.RegularExpressions;

namespace TbhCompanion
{
    // Read/write helpers for BepInEx.cfg. Several sections have an "Enabled" key,
    // so the console toggle is edited section-aware under [Logging.Console].
    static class BepInExCfg
    {
        public static string Path(string gameDir)
        {
            if (gameDir == null) return null;
            return System.IO.Path.Combine(gameDir, "BepInEx", "config", "BepInEx.cfg");
        }

        public static bool GetConsoleEnabled(string text)
        {
            var m = Regex.Match(text,
                @"(?ms)^\[Logging\.Console\].*?^\s*Enabled\s*=\s*(\w+)");
            return m.Success && m.Groups[1].Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        public static string SetConsoleEnabled(string text, bool on)
        {
            var re = new Regex(@"(?ms)(^\[Logging\.Console\].*?^\s*Enabled\s*=\s*)\w+");
            if (re.IsMatch(text)) return re.Replace(text, "${1}" + (on ? "true" : "false"), 1);
            return text.TrimEnd() + Environment.NewLine + Environment.NewLine +
                "[Logging.Console]" + Environment.NewLine +
                "Enabled = " + (on ? "true" : "false") + Environment.NewLine;
        }

        // Seed a minimal cfg with the console hidden before BepInEx's first run,
        // so its "on by default" never shows. BepInEx fills in the rest and keeps
        // this value. No-op if a cfg already exists.
        public static void SeedConsoleHidden(string gameDir)
        {
            try
            {
                string path = Path(gameDir);
                if (path == null || File.Exists(path)) return;
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                File.WriteAllText(path,
                    "[Logging.Console]" + Environment.NewLine +
                    "Enabled = false" + Environment.NewLine);
            }
            catch { }
        }

        // For an already-present cfg: force the console off once (marker-guarded),
        // then never touch it again so the user's later choice sticks.
        public static void ApplyHiddenDefaultOnce(string gameDir, Action<string> log)
        {
            try
            {
                string marker = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "tbh-companion", "console-default-applied");
                if (File.Exists(marker)) return;

                string path = Path(gameDir);
                if (path == null || !File.Exists(path)) return; // created on first game run; try later

                string text = File.ReadAllText(path);
                if (GetConsoleEnabled(text))
                {
                    File.WriteAllText(path, SetConsoleEnabled(text, false));
                    if (log != null) log("autosynth: BepInEx console hidden by default (change it in Status & Settings)");
                }
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(marker));
                File.WriteAllText(marker, DateTime.UtcNow.ToString("o"));
            }
            catch { }
        }
    }
}
