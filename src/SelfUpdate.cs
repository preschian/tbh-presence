using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace TbhCompanion
{
    // Keeps the companion in step with the game build it was made for.
    //
    // Versioning rule: the mods track the game's X.Y under a different major.
    //   game  v1.X.Y          e.g. 1.01.02
    //   mods  v3.X.Y[-n]      e.g. 3.01.02, 3.01.02-1  (-n is a mods-only hotfix)
    // Only X.Y is compared; the hotfix suffix never affects matching.
    static class SelfUpdate
    {
        const string Repo = "preschian/tbh-presence";
        const string ReleasesApi = "https://api.github.com/repos/" + Repo + "/releases?per_page=100";

        // Release assets must come from this repo's own release downloads — the URL
        // is taken from an API response and ends up being executed as this app.
        const string AssetPrefix = "https://github.com/" + Repo + "/releases/download/";

        const int GameMajor = 1;
        const int ModsMajor = 3;

        // ---- version parsing ----

        internal struct Ver
        {
            public int X, Y, Hotfix;   // Hotfix = -1 when the tag has no "-n"
            public bool Ok;
        }

        static readonly Regex GameShape = new Regex(@"^v?" + GameMajor + @"\.(\d+)\.(\d+)$");
        static readonly Regex ModsShape = new Regex(@"^v?" + ModsMajor + @"\.(\d+)\.(\d+)(?:-(\d+))?$");

        static int Num(string s) { return int.Parse(s, CultureInfo.InvariantCulture); }

        internal static Ver ParseGame(string s)
        {
            var v = new Ver { Hotfix = -1 };
            if (string.IsNullOrEmpty(s)) return v;
            var m = GameShape.Match(s.Trim());
            if (!m.Success) return v;
            v.X = Num(m.Groups[1].Value); v.Y = Num(m.Groups[2].Value); v.Ok = true;
            return v;
        }

        internal static Ver ParseMods(string s)
        {
            var v = new Ver { Hotfix = -1 };
            if (string.IsNullOrEmpty(s)) return v;
            var m = ModsShape.Match(s.Trim());
            if (!m.Success) return v;
            v.X = Num(m.Groups[1].Value); v.Y = Num(m.Groups[2].Value);
            if (m.Groups[3].Success) v.Hotfix = Num(m.Groups[3].Value);
            v.Ok = true;
            return v;
        }

        // Game build compared against the mods build: >0 when the game is ahead.
        internal static int CompareBuild(Ver game, Ver mods)
        {
            if (game.X != mods.X) return game.X < mods.X ? -1 : 1;
            if (game.Y != mods.Y) return game.Y < mods.Y ? -1 : 1;
            return 0;
        }

        // The running companion's own version (stamped by build.ps1). Dev builds
        // are 1.0.0, which does not parse as v3.X.Y and so reports "unknown".
        public static string InstalledModsVersion()
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                var attrs = asm.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false);
                if (attrs.Length > 0)
                {
                    string s = ((AssemblyInformationalVersionAttribute)attrs[0]).InformationalVersion;
                    if (!string.IsNullOrEmpty(s)) return s.Trim();
                }
                return asm.GetName().Version.ToString();
            }
            catch { return null; }
        }

        // ---- status ----

        public enum State
        {
            Unknown,        // game folder or a version string could not be read
            Matched,        // mods already track this game build
            UpdateReady,    // game is ahead and GitHub has a matching release
            WaitingRelease, // game is ahead but no v3.X.Y release exists yet
            Ahead,          // mods are newer than the installed game (nothing to do)
            CheckFailed     // the release lookup itself failed (network, rate limit)
        }

        // A failed lookup says nothing about the release, so retry it sooner than
        // a settled answer.
        public const int RetryMinutes = 5;
        public const int ThrottleMinutes = 30;

        public static int MinutesBetweenChecks(Status s)
        {
            return s != null && s.State == State.CheckFailed ? RetryMinutes : ThrottleMinutes;
        }

        public sealed class Status
        {
            public State State;
            public string GameVersion;      // "1.01.02" or null
            public string ModsVersion;      // "3.01.02-1" or null
            public string ReleaseTag;       // matching release tag, when found
            public string DownloadUrl;      // asset url for this edition
            public string Message;          // one-line summary for the UI

            public bool CanUpdate { get { return State == State.UpdateReady && DownloadUrl != null; } }
        }

        static Status _last;
        static DateTime _lastAt = DateTime.MinValue;
        static readonly object _gate = new object();

        public static Status LastStatus { get { lock (_gate) return _last; } }

        // Blocking (network) — call from a background thread. Throttled so the
        // poll loop can call it freely; pass force after an install/patch.
        public static Status Check(bool force)
        {
            lock (_gate)
            {
                if (!force && _last != null
                    && (DateTime.UtcNow - _lastAt).TotalMinutes < MinutesBetweenChecks(_last))
                    return _last;
            }
            if (force) GameVersion.InvalidateCache();

            var st = Evaluate();
            lock (_gate) { _last = st; _lastAt = DateTime.UtcNow; }
            return st;
        }

        // The presence-only edition ships no plugin, so "mods" would misname it.
        public static string Noun { get { return Build.Synth ? "Mods" : "Companion"; } }
        static string LowerNoun { get { return Build.Synth ? "mods" : "companion"; } }

        static Status Evaluate()
        {
            var st = EvaluateCore();
            // A swap that failed after we exited is the most useful thing to say.
            string failed = TakeFailureMarker();
            if (failed != null) st.Message = failed;
            return st;
        }

        static Status EvaluateCore()
        {
            var st = new Status();
            st.GameVersion = GameVersion.Read();
            st.ModsVersion = InstalledModsVersion();

            Ver game = ParseGame(st.GameVersion);
            Ver mods = ParseMods(st.ModsVersion);

            if (!game.Ok)
            {
                st.State = State.Unknown;
                st.Message = AutoSynthDeploy.FindGameDir() == null
                    ? "game not found — start TaskBarHero once"
                    : "game version unreadable";
                return st;
            }
            if (!mods.Ok)
            {
                st.State = State.Unknown;
                st.Message = LowerNoun + " version unknown (dev build) — game v" + st.GameVersion;
                return st;
            }

            int cmp = CompareBuild(game, mods);
            if (cmp == 0)
            {
                st.State = State.Matched;
                st.Message = Noun + " matched (v" + st.ModsVersion + " ↔ game v" + st.GameVersion + ")";
                return st;
            }
            if (cmp < 0)
            {
                st.State = State.Ahead;
                st.Message = Noun + " v" + st.ModsVersion + " newer than game v" + st.GameVersion;
                return st;
            }

            string tag, url;
            try
            {
                FindRelease(game, out tag, out url);
            }
            catch (Exception ex)
            {
                // Not the same as "no release yet" — say so, and retry sooner.
                st.State = State.CheckFailed;
                st.Message = "Update check failed (retrying): " + Short(ex.Message);
                return st;
            }

            if (tag == null)
            {
                st.State = State.WaitingRelease;
                st.Message = "Waiting for release v" + ModsMajor + "." + game.X.ToString("00") + "."
                    + game.Y.ToString("00") + " (game v" + st.GameVersion + ")";
                return st;
            }

            st.State = State.UpdateReady;
            st.ReleaseTag = tag;
            st.DownloadUrl = url;
            st.Message = "Update available: game v" + st.GameVersion + " → release " + tag;
            if (url == null)
            {
                st.State = State.WaitingRelease;
                st.Message = "Release " + tag + " has no " + AssetName() + " asset yet";
            }
            return st;
        }

        // ---- GitHub releases ----

        internal static string AssetName()
        {
            return Build.Synth ? "TbhCompanion.exe" : "TbhCompanion-Presence.exe";
        }

        // Newest v3.X.Y[-n] release for the game's X.Y (highest -n wins, plain tag
        // counts as -0). Leaves tag null when nothing matches yet.
        static void FindRelease(Ver game, out string tag, out string assetUrl)
        {
            tag = null; assetUrl = null;
            var releases = GetJsonArray(ReleasesApi);
            int bestHotfix = int.MinValue;

            foreach (var item in releases)
            {
                var rel = item as Dictionary<string, object>;
                if (rel == null) continue;
                if (Flag(rel, "draft")) continue;

                object tagObj;
                if (!rel.TryGetValue("tag_name", out tagObj) || tagObj == null) continue;
                Ver v = ParseMods(tagObj.ToString());
                if (!v.Ok || v.X != game.X || v.Y != game.Y) continue;

                int hotfix = v.Hotfix < 0 ? 0 : v.Hotfix;
                if (hotfix <= bestHotfix) continue;

                bestHotfix = hotfix;
                tag = tagObj.ToString();
                assetUrl = FindAsset(rel);
            }
        }

        static bool Flag(Dictionary<string, object> d, string key)
        {
            object o;
            return d.TryGetValue(key, out o) && o is bool && (bool)o;
        }

        static string FindAsset(Dictionary<string, object> rel)
        {
            object assetsObj;
            if (!rel.TryGetValue("assets", out assetsObj)) return null;
            var assets = assetsObj as System.Collections.IEnumerable;
            if (assets == null) return null;

            string want = AssetName();
            foreach (var a in assets)
            {
                var asset = a as Dictionary<string, object>;
                if (asset == null) continue;
                object name, url;
                if (!asset.TryGetValue("name", out name) || name == null) continue;
                if (!string.Equals(name.ToString(), want, StringComparison.OrdinalIgnoreCase)) continue;
                if (!asset.TryGetValue("browser_download_url", out url) || url == null) continue;
                string href = url.ToString();
                // The API supplies this URL and we execute what it returns, so only
                // accept this repo's own release-download host and path.
                if (!href.StartsWith(AssetPrefix, StringComparison.Ordinal)) continue;
                return href;
            }
            return null;
        }

        static System.Collections.IEnumerable GetJsonArray(string url)
        {
            ServicePointManager.SecurityProtocol =
                SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
            string json;
            using (var wc = new WebClient())
            {
                wc.Headers.Add("User-Agent", "TbhCompanion");
                wc.Headers.Add("Accept", "application/vnd.github+json");
                json = wc.DownloadString(url);
            }
            var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var arr = js.DeserializeObject(json) as System.Collections.IEnumerable;
            if (arr == null) throw new InvalidOperationException("unexpected releases response");
            return arr;
        }

        // ---- apply ----

        // Downloads the matching build and swaps it in for the running exe.
        // Runs on a background thread; returns true when the handoff script was
        // started, in which case the caller should exit the app.
        public static bool Apply(Status st, Action<string> log)
        {
            try
            {
                if (st == null || !st.CanUpdate) { log("Nothing to update."); return false; }
                if (!st.DownloadUrl.StartsWith(AssetPrefix, StringComparison.Ordinal))
                {
                    log("Refusing an update from an unexpected location.");
                    return false;
                }

                string exePath = Application.ExecutablePath;

                // The swap runs after this process exits, so a folder we cannot
                // write would fail silently there. Find out now, before quitting.
                string why;
                if (!FolderWritable(Path.GetDirectoryName(exePath), out why))
                {
                    log("Cannot update in place: " + why + ". Move the app to a writable folder.");
                    return false;
                }

                string staged = Path.Combine(Path.GetTempPath(),
                    "TbhCompanion_update_" + Guid.NewGuid().ToString("N") + ".exe");

                log("Downloading " + st.ReleaseTag + "...");
                ServicePointManager.SecurityProtocol =
                    SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
                using (var wc = new WebClient())
                {
                    wc.Headers.Add("User-Agent", "TbhCompanion");
                    wc.DownloadFile(st.DownloadUrl, staged);
                }

                var info = new FileInfo(staged);
                if (!info.Exists || info.Length < 64 * 1024)
                {
                    TryDelete(staged);
                    log("Download looks incomplete — please try again.");
                    return false;
                }

                if (!StartSwap(exePath, staged, log)) { TryDelete(staged); return false; }
                log("Restarting to finish the update...");
                return true;
            }
            catch (Exception ex)
            {
                log("Update failed: " + ex.Message);
                return false;
            }
        }

        // A running exe cannot overwrite itself, so hand the swap to a throwaway
        // batch file that waits for this process to exit, copies, and relaunches.
        // The new exe redeploys TbhAutoSynth.dll on its own next poll.
        //
        // The old exe is left in place if the copy fails, so the relaunch always
        // brings the app back; the marker file is how the restarted app learns the
        // swap did not take, instead of silently reporting the update again.
        static bool StartSwap(string exePath, string staged, Action<string> log)
        {
            try
            {
                string bat = Path.Combine(Path.GetTempPath(),
                    "TbhCompanion_update_" + Guid.NewGuid().ToString("N") + ".cmd");
                int pid = Process.GetCurrentProcess().Id;

                // cmd expands %VAR% even inside quotes, so a path containing '%'
                // (legal on Windows) has to be doubled before it goes in the script.
                string q_staged = Bat(staged);
                string q_exe = Bat(exePath);
                string marker = FailureMarkerPath();
                try { Directory.CreateDirectory(Path.GetDirectoryName(marker)); } catch { }
                string q_marker = Bat(marker);

                string script =
                    "@echo off\r\n" +
                    "setlocal\r\n" +
                    ":wait\r\n" +
                    "tasklist /fi \"PID eq " + pid + "\" 2>nul | find \"" + pid + "\" >nul\r\n" +
                    "if not errorlevel 1 (\r\n" +
                    "  ping -n 2 127.0.0.1 >nul\r\n" +
                    "  goto wait\r\n" +
                    ")\r\n" +
                    "copy /y \"" + q_staged + "\" \"" + q_exe + "\" >nul\r\n" +
                    "if errorlevel 1 (\r\n" +
                    "  ping -n 3 127.0.0.1 >nul\r\n" +
                    "  copy /y \"" + q_staged + "\" \"" + q_exe + "\" >nul\r\n" +
                    ")\r\n" +
                    "if errorlevel 1 (\r\n" +
                    "  >\"" + q_marker + "\" echo Last update could not replace the app - check folder permissions.\r\n" +
                    ")\r\n" +
                    "del \"" + q_staged + "\" >nul 2>&1\r\n" +
                    "start \"\" \"" + q_exe + "\"\r\n" +
                    "del \"%~f0\" >nul 2>&1\r\n";
                File.WriteAllText(bat, script);

                var psi = new ProcessStartInfo("cmd.exe", "/c \"" + bat + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetTempPath()
                };
                Process.Start(psi);
                return true;
            }
            catch (Exception ex)
            {
                log("Could not start the updater: " + ex.Message);
                return false;
            }
        }

        // Escape for use inside a double-quoted batch argument.
        static string Bat(string path)
        {
            return path == null ? "" : path.Replace("%", "%%");
        }

        static string FailureMarkerPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "tbh-companion", "update-failed.txt");
        }

        // Reads and clears the marker the swap script leaves behind, so a failed
        // swap is reported once by the app that comes back up.
        static string TakeFailureMarker()
        {
            try
            {
                string path = FailureMarkerPath();
                if (!File.Exists(path)) return null;
                string text = File.ReadAllText(path).Trim();
                TryDelete(path);
                return string.IsNullOrEmpty(text)
                    ? "Last update could not replace the app."
                    : text;
            }
            catch { return null; }
        }

        static string Short(string s)
        {
            if (string.IsNullOrEmpty(s)) return "unknown error";
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length > 90 ? s.Substring(0, 89) + "…" : s;
        }

        // A probe write is the only reliable test: the running exe itself cannot be
        // opened for writing, but the swap only needs the folder.
        static bool FolderWritable(string dir, out string why)
        {
            why = null;
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                {
                    why = "app folder not found";
                    return false;
                }
                string probe = Path.Combine(dir, ".tbh-update-probe-" + Guid.NewGuid().ToString("N"));
                using (var fs = new FileStream(probe, FileMode.CreateNew, FileAccess.Write)) { fs.WriteByte(0); }
                TryDelete(probe);
                return true;
            }
            catch (Exception ex)
            {
                why = Short(ex.Message);
                return false;
            }
        }

        static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
