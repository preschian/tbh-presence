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
        const string ReleasesApi = "https://api.github.com/repos/preschian/tbh-presence/releases?per_page=100";
        const int ModsMajor = 3;

        // ---- version parsing ----

        internal struct Ver
        {
            public int X, Y, Hotfix;   // Hotfix = -1 when the tag has no "-n"
            public string Text;
            public bool Ok;
        }

        static readonly Regex GameShape = new Regex(@"^v?1\.(\d+)\.(\d+)$");
        static readonly Regex ModsShape = new Regex(@"^v?3\.(\d+)\.(\d+)(?:-(\d+))?$");

        static int Num(string s) { return int.Parse(s, CultureInfo.InvariantCulture); }

        internal static Ver ParseGame(string s)
        {
            var v = new Ver { Hotfix = -1, Text = s };
            if (string.IsNullOrEmpty(s)) return v;
            var m = GameShape.Match(s.Trim());
            if (!m.Success) return v;
            v.X = Num(m.Groups[1].Value); v.Y = Num(m.Groups[2].Value); v.Ok = true;
            return v;
        }

        internal static Ver ParseMods(string s)
        {
            var v = new Ver { Hotfix = -1, Text = s };
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
            Ahead           // mods are newer than the installed game (nothing to do)
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
                if (!force && _last != null && (DateTime.UtcNow - _lastAt).TotalMinutes < 30)
                    return _last;
            }
            if (force) GameVersion.InvalidateCache();

            var st = Evaluate();
            lock (_gate) { _last = st; _lastAt = DateTime.UtcNow; }
            return st;
        }

        // The presence-only edition ships no plugin, so "mods" would misname it.
        public static string Noun { get { return Build.Synth ? "Mods" : "Companion"; } }
        static string noun { get { return Build.Synth ? "mods" : "companion"; } }

        static Status Evaluate()
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
                st.Message = noun + " version unknown (dev build) — game v" + st.GameVersion;
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
                st.State = State.WaitingRelease;
                st.Message = "Update check failed: " + ex.Message;
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
                return url.ToString();
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

                string exePath = Application.ExecutablePath;
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
        static bool StartSwap(string exePath, string staged, Action<string> log)
        {
            try
            {
                string bat = Path.Combine(Path.GetTempPath(),
                    "TbhCompanion_update_" + Guid.NewGuid().ToString("N") + ".cmd");
                int pid = Process.GetCurrentProcess().Id;

                string script =
                    "@echo off\r\n" +
                    "setlocal\r\n" +
                    ":wait\r\n" +
                    "tasklist /fi \"PID eq " + pid + "\" 2>nul | find \"" + pid + "\" >nul\r\n" +
                    "if not errorlevel 1 (\r\n" +
                    "  ping -n 2 127.0.0.1 >nul\r\n" +
                    "  goto wait\r\n" +
                    ")\r\n" +
                    "copy /y \"" + staged + "\" \"" + exePath + "\" >nul\r\n" +
                    "if errorlevel 1 (\r\n" +
                    "  ping -n 3 127.0.0.1 >nul\r\n" +
                    "  copy /y \"" + staged + "\" \"" + exePath + "\" >nul\r\n" +
                    ")\r\n" +
                    "del \"" + staged + "\" >nul 2>&1\r\n" +
                    "start \"\" \"" + exePath + "\"\r\n" +
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

        static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
