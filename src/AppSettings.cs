using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace TbhCompanion
{
    // Persisted settings for the companion app itself (distinct from the BepInEx
    // auto-synthesis cfg, which lives in the game folder). Stored next to the
    // address cache in %LocalAppData%\tbh-companion. A tiny "key = value" file so
    // it stays hand-readable and needs no serializer.
    static class AppSettings
    {
        static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "tbh-companion");
        static readonly string FilePath = Path.Combine(Dir, "settings.txt");

        // Master switch for pushing Rich Presence to Discord. Default on.
        public static bool PresenceEnabled
        {
            get { return GetBool("PresenceEnabled", true); }
            set { Set("PresenceEnabled", value ? "true" : "false"); }
        }

        // Close and relaunch TaskBarHero after it has been running this long.
        // Off by default — opt-in for long idle sessions that accumulate RAM.
        public static bool AutoRestartEnabled
        {
            get { return GetBool("AutoRestartEnabled", false); }
            set { Set("AutoRestartEnabled", value ? "true" : "false"); }
        }

        // Days of continuous uptime before a scheduled restart (1–30).
        public static int AutoRestartDays
        {
            get
            {
                int d = GetInt("AutoRestartDays", 1);
                if (d < 1) return 1;
                if (d > 30) return 30;
                return d;
            }
            set
            {
                int d = value < 1 ? 1 : (value > 30 ? 30 : value);
                Set("AutoRestartDays", d.ToString(CultureInfo.InvariantCulture));
            }
        }

        static bool GetBool(string key, bool fallback)
        {
            string v = Get(key);
            if (v == null) return fallback;
            return !v.Equals("false", StringComparison.OrdinalIgnoreCase) && v != "0";
        }

        static int GetInt(string key, int fallback)
        {
            string v = Get(key);
            int n;
            return v != null && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)
                ? n : fallback;
        }

        static string Get(string key)
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    if (line.Substring(0, eq).Trim() == key)
                        return line.Substring(eq + 1).Trim();
                }
            }
            catch { }
            return null;
        }

        static void Set(string key, string value)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var lines = File.Exists(FilePath) ? new List<string>(File.ReadAllLines(FilePath)) : new List<string>();
                string entry = key + " = " + value;
                bool replaced = false;
                for (int i = 0; i < lines.Count; i++)
                {
                    int eq = lines[i].IndexOf('=');
                    if (eq > 0 && lines[i].Substring(0, eq).Trim() == key) { lines[i] = entry; replaced = true; break; }
                }
                if (!replaced) lines.Add(entry);
                File.WriteAllText(FilePath, string.Join(Environment.NewLine, lines.ToArray()) + Environment.NewLine);
            }
            catch { }
        }
    }
}
