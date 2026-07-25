using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace TbhCompanion
{
    // Reads the installed TaskBarHero build number (e.g. "1.01.02") without
    // launching the game. Unity stores PlayerSettings.bundleVersion near the top
    // of TaskBarHero_Data\globalgamemanagers as a length-prefixed ASCII string,
    // so a short scan of the header is enough.
    static class GameVersion
    {
        // The bundleVersion sits ~1.8 KB in; read a generous slice and stop.
        const int ScanBytes = 16 * 1024;

        // Three-part numeric strings only: the neighbouring PlayerSettings fields
        // are two-part ("1.0") and the engine version carries letters ("6000.0.72f1").
        static readonly Regex Shape = new Regex(@"^\d{1,4}\.\d{1,4}\.\d{1,4}$");

        // The engine version ("6000.0.72f1") is a null-terminated string in the
        // serialized-file header and is the anchor the scan starts from.
        static readonly Regex EngineShape = new Regex(@"\d+\.\d+\.\d+[a-z]\d+");
        const int HeaderScan = 512;

        static string _cached;
        static DateTime _cachedAt = DateTime.MinValue;
        static string _cachedFor;

        // "1.01.02", or null when the game folder or the string cannot be found.
        // Cached for a minute so poll loops stay cheap; re-reads after a patch.
        public static string Read()
        {
            string gameDir = AutoSynthDeploy.FindGameDir();
            if (gameDir == null) return null;
            if (_cachedFor == gameDir && (DateTime.UtcNow - _cachedAt).TotalMinutes < 1)
                return _cached;

            string value = ReadFrom(gameDir);
            _cached = value; _cachedFor = gameDir; _cachedAt = DateTime.UtcNow;
            return value;
        }

        public static void InvalidateCache() { _cachedAt = DateTime.MinValue; }

        internal static string ReadFrom(string gameDir)
        {
            try
            {
                string path = Path.Combine(gameDir, "TaskBarHero_Data", "globalgamemanagers");
                if (!File.Exists(path)) return null;

                byte[] buf;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    int want = (int)Math.Min(ScanBytes, fs.Length);
                    buf = new byte[want];
                    int read = 0;
                    while (read < want)
                    {
                        int n = fs.Read(buf, read, want - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    if (read < buf.Length) Array.Resize(ref buf, read);
                }
                return FindBundleVersion(buf);
            }
            catch { return null; }
        }

        // bundleVersion is the first plain "X.Y.Z" length-prefixed string (int32 LE
        // length + ASCII bytes, 4-byte aligned) after the engine version. Anchoring
        // on the engine version keeps this stable if unrelated numeric strings are
        // added further into the file.
        internal static string FindBundleVersion(byte[] buf)
        {
            int start = 0;
            var anchor = EngineShape.Match(
                Encoding.ASCII.GetString(buf, 0, Math.Min(buf.Length, HeaderScan)));
            if (anchor.Success) start = anchor.Index + anchor.Length;

            for (int i = (start + 3) & ~3; i + 4 <= buf.Length; i += 4)
            {
                int len = buf[i] | (buf[i + 1] << 8) | (buf[i + 2] << 16) | (buf[i + 3] << 24);
                if (len < 3 || len > 24 || i + 4 + len > buf.Length) continue;

                bool ascii = true;
                for (int j = i + 4; j < i + 4 + len; j++)
                {
                    byte b = buf[j];
                    if (b != (byte)'.' && (b < (byte)'0' || b > (byte)'9')) { ascii = false; break; }
                }
                if (!ascii) continue;

                string s = Encoding.ASCII.GetString(buf, i + 4, len);
                if (Shape.IsMatch(s)) return s;
            }
            return null;
        }
    }
}
