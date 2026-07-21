using System;
using System.IO;
using System.IO.Compression;
using System.Net;

namespace TbhCompanion
{
    // One-click installer/uninstaller for the BepInEx mod loader, so users don't
    // have to download and extract it by hand before using auto-synthesis.
    // Everything here is opt-in: nothing runs until the user clicks a button.
    static class BepInExSetup
    {
        // Pinned to the bleeding-edge build this project was validated against.
        // Bump when a newer build is verified against a game update.
        const string BepInExUrl =
            "https://builds.bepinex.dev/projects/bepinex_be/785/" +
            "BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.785%2B6abdba4.zip";

        // Paths dropped by the BepInEx Unity IL2CPP / Doorstop package.
        static readonly string[] RemnantDirs = { "BepInEx", "dotnet" };
        static readonly string[] RemnantFiles =
            { "winhttp.dll", "doorstop_config.ini", ".doorstop_version", "changelog.txt" };

        public static bool GameFound { get { return AutoSynthDeploy.FindGameDir() != null; } }

        // Complete install (winhttp + BepInEx/) — used to skip re-download.
        public static bool IsInstalled()
        {
            string dir = AutoSynthDeploy.FindGameDir();
            return dir != null && IsInstalledAt(dir);
        }

        // Any BepInEx/Doorstop remnant — UI uses this for Install vs Remove visibility.
        public static bool HasRemnants()
        {
            string dir = AutoSynthDeploy.FindGameDir();
            return dir != null && HasRemnantsAt(dir);
        }

        static bool IsInstalledAt(string gameDir)
        {
            return File.Exists(Path.Combine(gameDir, "winhttp.dll"))
                && Directory.Exists(Path.Combine(gameDir, "BepInEx"));
        }

        static bool HasRemnantsAt(string gameDir)
        {
            foreach (string name in RemnantDirs)
                if (Directory.Exists(Path.Combine(gameDir, name))) return true;
            foreach (string name in RemnantFiles)
                if (File.Exists(Path.Combine(gameDir, name))) return true;
            return false;
        }

        public static bool GameRunning()
        {
            try { return System.Diagnostics.Process.GetProcessesByName("TaskBarHero").Length > 0; }
            catch { return false; }
        }

        // Shared preflight for Install/Uninstall. Returns false after logging why.
        static bool TryResolveGameDir(Action<string> log, out string gameDir)
        {
            gameDir = AutoSynthDeploy.FindGameDir();
            if (gameDir == null)
            {
                log("Could not find the TaskBarHero folder. Start the game once, then try again.");
                return false;
            }
            if (GameRunning())
            {
                log("Please close TaskBarHero first, then try again.");
                return false;
            }
            return true;
        }

        // Trailing-separator prefix so "C:\gameExtra" does not match "C:\game\".
        static string RootPrefix(string dir)
        {
            string root = Path.GetFullPath(dir);
            char sep = Path.DirectorySeparatorChar;
            if (root.Length == 0 || root[root.Length - 1] != sep)
                root += sep;
            return root;
        }

        static bool IsUnderRoot(string path, string rootPrefix)
        {
            return Path.GetFullPath(path).StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase);
        }

        // Runs on a background thread. Reports progress via log; returns true on success.
        public static bool Install(Action<string> log)
        {
            try
            {
                string gameDir;
                if (!TryResolveGameDir(log, out gameDir)) return false;
                if (IsInstalledAt(gameDir))
                {
                    log("BepInEx is already installed.");
                    return true;
                }

                BackupSave(log);

                string tmpZip = Path.Combine(Path.GetTempPath(), "bepinex_tbh.zip");
                log("Downloading BepInEx (~35 MB)...");
                Download(BepInExUrl, tmpZip);

                log("Installing into the game folder...");
                ExtractOver(tmpZip, gameDir);
                try { File.Delete(tmpZip); } catch { }

                // hide the BepInEx console by default (before its first run)
                BepInExCfg.SeedConsoleHidden(gameDir);

                if (!IsInstalledAt(gameDir))
                {
                    log("Install finished but files look incomplete - please try again.");
                    return false;
                }

                // deploy the plugin now so the very next game launch has it
                AutoSynthDeploy.TryDeploy(log);

                log("Done. Start TaskBarHero once to finish setup, then open the Cube panel.");
                return true;
            }
            catch (Exception ex)
            {
                log("Setup failed: " + ex.Message);
                return false;
            }
        }

        // Runs on a background thread. Removes BepInEx/Doorstop from the game folder.
        // Idempotent: succeeds when nothing is left to remove. Does not touch saves.
        public static bool Uninstall(Action<string> log)
        {
            try
            {
                string gameDir;
                if (!TryResolveGameDir(log, out gameDir)) return false;
                if (!HasRemnantsAt(gameDir))
                {
                    log("Nothing to remove — BepInEx is not installed.");
                    return true;
                }

                log("Removing BepInEx...");
                string root = RootPrefix(gameDir);

                foreach (string name in RemnantDirs)
                    TryDeleteDir(Path.Combine(gameDir, name), root, log);
                foreach (string name in RemnantFiles)
                    TryDeleteFile(Path.Combine(gameDir, name), root, log);

                if (HasRemnantsAt(gameDir))
                {
                    log("Cleanup unfinished — close TaskBarHero and retry.");
                    return false;
                }

                log("Done. Mods removed. Presence still works.");
                return true;
            }
            catch (Exception ex)
            {
                log("Cleanup failed: " + ex.Message);
                return false;
            }
        }

        static void TryDeleteDir(string path, string rootPrefix, Action<string> log)
        {
            try
            {
                if (!IsUnderRoot(path, rootPrefix)) return;
                if (!Directory.Exists(path)) return;
                Directory.Delete(path, true);
                log("Removed " + Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) + "/");
            }
            catch (Exception ex)
            {
                log("Could not remove " + Path.GetFileName(path) + ": " + ex.Message);
            }
        }

        static void TryDeleteFile(string path, string rootPrefix, Action<string> log)
        {
            try
            {
                if (!IsUnderRoot(path, rootPrefix)) return;
                if (!File.Exists(path)) return;
                File.Delete(path);
                log("Removed " + Path.GetFileName(path));
            }
            catch (Exception ex)
            {
                log("Could not remove " + Path.GetFileName(path) + ": " + ex.Message);
            }
        }

        static void BackupSave(Action<string> log)
        {
            try
            {
                string save = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "..", "LocalLow", "TesseractStudio", "TaskbarHero", "SaveFile_Live.es3");
                save = Path.GetFullPath(save);
                if (!File.Exists(save)) return;
                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string dst = Path.Combine(Path.GetDirectoryName(save),
                    "SaveFile_Live_backup_" + stamp + ".es3");
                File.Copy(save, dst, false);
                log("Backed up your save to " + Path.GetFileName(dst));
            }
            catch (Exception ex)
            {
                log("(Could not back up the save automatically: " + ex.Message + ")");
            }
        }

        static void Download(string url, string dest)
        {
            ServicePointManager.SecurityProtocol =
                SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
            using (var wc = new WebClient())
            {
                wc.Headers.Add("User-Agent", "TbhCompanion");
                wc.DownloadFile(url, dest);
            }
        }

        // Extract every entry into gameDir, overwriting existing files (unlike
        // ZipFile.ExtractToDirectory, which throws when a file already exists).
        static void ExtractOver(string zipPath, string gameDir)
        {
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                string root = RootPrefix(gameDir);
                foreach (var entry in zip.Entries)
                {
                    string target = Path.GetFullPath(Path.Combine(gameDir, entry.FullName));
                    if (!IsUnderRoot(target, root))
                        continue; // guard against zip-slip
                    if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\"))
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    entry.ExtractToFile(target, true);
                }
            }
        }
    }
}
