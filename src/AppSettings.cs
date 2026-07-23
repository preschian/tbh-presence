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
        // Enabling (or tightening the day limit) arms a timer so a long-lived
        // session is not killed on the next poll.
        public static bool AutoRestartEnabled
        {
            get { return GetBool("AutoRestartEnabled", false); }
            set
            {
                bool was = GetBool("AutoRestartEnabled", false);
                Set("AutoRestartEnabled", value ? "true" : "false");
                if (value && !was) ArmRestartClock();
                else if (!value) ClearRestartArm();
            }
        }

        // Days of continuous uptime before a scheduled restart (1–30).
        public static int AutoRestartDays
        {
            get { return ClampDays(GetInt("AutoRestartDays", 1)); }
            set
            {
                int prev = AutoRestartDays;
                int d = ClampDays(value);
                Set("AutoRestartDays", d.ToString(CultureInfo.InvariantCulture));
                // Tightening the limit re-arms so we don't immediately kill.
                if (AutoRestartEnabled && d < prev) ArmRestartClock();
            }
        }

        // UTC instant when the restart clock was last armed. Due-ness uses
        // max(process start, armed) so enabling mid-session waits a full period.
        public static DateTime? AutoRestartArmedUtc
        {
            get
            {
                string v = Get("AutoRestartArmedUtc");
                DateTime dt;
                if (v != null && DateTime.TryParse(v, null, DateTimeStyles.RoundtripKind, out dt))
                    return dt.ToUniversalTime();
                return null;
            }
        }

        public static void ArmRestartClock()
        {
            Set("AutoRestartArmedUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        }

        public static void ClearRestartArm()
        {
            Set("AutoRestartArmedUtc", "");
        }

        // Older settings files may have Enabled=true without an arm stamp — arm
        // on first read so we don't instantly kill a long-running game.
        public static void EnsureRestartArmed()
        {
            if (AutoRestartEnabled && AutoRestartArmedUtc == null)
                ArmRestartClock();
        }

        static int ClampDays(int d)
        {
            if (d < 1) return 1;
            if (d > 30) return 30;
            return d;
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
