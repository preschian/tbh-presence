using System;
using System.Collections.Generic;
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
            set { SetBool("PresenceEnabled", value); }
        }

        static bool GetBool(string key, bool fallback)
        {
            try
            {
                if (!File.Exists(FilePath)) return fallback;
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    if (line.Substring(0, eq).Trim() == key)
                    {
                        var v = line.Substring(eq + 1).Trim();
                        return !v.Equals("false", StringComparison.OrdinalIgnoreCase) && v != "0";
                    }
                }
            }
            catch { }
            return fallback;
        }

        static void SetBool(string key, bool value)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var lines = File.Exists(FilePath) ? new List<string>(File.ReadAllLines(FilePath)) : new List<string>();
                string entry = key + " = " + (value ? "true" : "false");
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
