using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace TbhCompanion
{
    public class StageInfo
    {
        public int Key, Act, No, Level, Waves;
        public string Diff, Type, NameKey;
    }

    public class HeroInfo
    {
        public int Key, Level;
        public string Name;
    }

    public class GameState
    {
        public int StageKey, SavedWave, MaxStage, PetKey;
        public StageInfo Info;          // null when the key isn't in the table
        public List<HeroInfo> Heroes = new List<HeroInfo>();
        public string Source;           // "live" or "save"

        public string Details()
        {
            if (Info == null) return "Stage " + StageKey;
            return string.Format("Act {0} - Stage {1}  ({2}, Lv {3})", Info.Act, Info.No, Info.Diff, Info.Level);
        }

        public string PartyLine()
        {
            if (Heroes.Count == 0) return null;
            var parts = new List<string>();
            foreach (var h in Heroes)
                parts.Add(h.Level > 0 ? h.Name + " Lv" + h.Level : h.Name);
            return string.Join(", ", parts.ToArray());
        }

        public string Label()
        {
            string s = Details();
            string p = PartyLine();
            return p == null ? s : s + "  |  " + p;
        }
    }

    // Resolves the game's IL2CPP object graph (with an on-disk address cache)
    // and reads the current state. All offsets from Il2CppDumper (metadata v31).
    public class GameReader
    {
        // object fields start at +0x10 (klass +0x0, monitor +0x8)
        const long PSD_common    = 0x10;   // PlayerSaveData.commonSaveData
        const long PSD_heroSaves = 0x60;   // PlayerSaveData.heroSaveDatas (List<HeroSaveData>)
        const long CSD_playTime  = 0x20;
        const long CSD_petKey    = 0x40;   // CommonSaveData.ArrangedPetKey
        const long CSD_heroKeys  = 0x48;   // CommonSaveData.arrangedHeroKey (int[])
        const long CSD_maxStage  = 0x54;
        const long CSD_stageKey  = 0x58;
        const long CSD_stageWave = 0x5C;
        const long SID_StageKey  = 0x30;   // StageInfoData
        const long SID_NameKey   = 0x38;
        const long SID_Type      = 0x40;
        const long SID_Diff      = 0x44;
        const long SID_Act       = 0x48;
        const long SID_No        = 0x4C;
        const long SID_Level     = 0x50;
        const long SID_Waves     = 0x54;
        const long HID_HeroKey   = 0x30;   // HeroInfoData
        const long HID_NameKey   = 0x38;
        const long HID_ClassType = 0x48;
        const long HSD_heroKey   = 0x10;   // HeroSaveData
        const long HSD_level     = 0x14;
        const long UU_currentCache = 0x88; // uw.up statics: current StageCache (beyk)
        const long SC_infoData   = 0x10;   // uw.StageCache.beyo (StageInfoData)
        const long KLASS_staticFields = 0xB8; // Il2CppClass.static_fields

        static readonly string[] DIFFS = { "NORMAL", "NIGHTMARE", "HELL", "TORMENT" };
        static readonly string[] STYPES = { "NORMAL", "ACTBOSS" };
        static readonly string[] HCLASS = { "All", "Knight", "Ranger", "Sorcerer", "Priest", "Hunter", "Slayer" };
        const int CACHE_VERSION = 6;

        readonly Mem _mem;
        readonly Process _proc;
        readonly string _cachePath;

        long _psd, _csd, _csdKlass, _uuStatics, _scKlass;
        Dictionary<int, StageInfo> _stages = new Dictionary<int, StageInfo>();
        Dictionary<int, string> _heroNames = new Dictionary<int, string>();

        public GameReader(Mem mem, Process proc, string cachePath)
        {
            _mem = mem;
            _proc = proc;
            _cachePath = cachePath;
        }

        string GameStamp()
        {
            string exe = _proc.MainModule.FileName;
            var fi = new FileInfo(exe);
            return exe + "|" + fi.LastWriteTimeUtc.Ticks + "|" + fi.Length;
        }

        string BootId()
        {
            return _proc.Id + "|" + _proc.StartTime.ToFileTimeUtc();
        }

        // ---- cache: simple line-based format, one file ----

        void SaveCache()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("v=" + CACHE_VERSION);
                sb.AppendLine("stamp=" + GameStamp());
                sb.AppendLine("boot=" + BootId());
                sb.AppendLine("psd=" + _psd);
                sb.AppendLine("csd=" + _csd);
                sb.AppendLine("csdKlass=" + _csdKlass);
                sb.AppendLine("uu=" + _uuStatics);
                sb.AppendLine("scKlass=" + _scKlass);
                foreach (var kv in _stages)
                {
                    var s = kv.Value;
                    sb.AppendLine(string.Join("|", new string[] {
                        "S", s.Key.ToString(), s.Act.ToString(), s.No.ToString(), s.Level.ToString(),
                        s.Waves.ToString(), s.Diff, s.Type, s.NameKey ?? "" }));
                }
                foreach (var kv in _heroNames)
                    sb.AppendLine("H|" + kv.Key + "|" + kv.Value);
                Directory.CreateDirectory(Path.GetDirectoryName(_cachePath));
                File.WriteAllText(_cachePath, sb.ToString());
            }
            catch { }
        }

        // Returns true if a full valid same-boot cache was loaded (addresses + tables).
        // Tables alone survive a game restart (same stamp).
        bool LoadCache(bool noCache, out bool sameBoot)
        {
            sameBoot = false;
            if (noCache || !File.Exists(_cachePath)) return false;
            try
            {
                var kv = new Dictionary<string, string>();
                var stages = new Dictionary<int, StageInfo>();
                var heroes = new Dictionary<int, string>();
                foreach (string line in File.ReadAllLines(_cachePath))
                {
                    if (line.StartsWith("S|"))
                    {
                        var f = line.Split('|');
                        if (f.Length < 9) continue;
                        var s = new StageInfo();
                        s.Key = int.Parse(f[1]); s.Act = int.Parse(f[2]); s.No = int.Parse(f[3]);
                        s.Level = int.Parse(f[4]); s.Waves = int.Parse(f[5]);
                        s.Diff = f[6]; s.Type = f[7]; s.NameKey = f[8];
                        stages[s.Key] = s;
                    }
                    else if (line.StartsWith("H|"))
                    {
                        var f = line.Split('|');
                        if (f.Length < 3) continue;
                        heroes[int.Parse(f[1])] = f[2];
                    }
                    else
                    {
                        int eq = line.IndexOf('=');
                        if (eq > 0) kv[line.Substring(0, eq)] = line.Substring(eq + 1);
                    }
                }
                if (!kv.ContainsKey("v") || kv["v"] != CACHE_VERSION.ToString()) return false;
                if (!kv.ContainsKey("stamp") || kv["stamp"] != GameStamp()) return false;

                _stages = stages;
                _heroNames = heroes;

                if (kv.ContainsKey("boot") && kv["boot"] == BootId())
                {
                    long psd = long.Parse(kv["psd"]), csd = long.Parse(kv["csd"]);
                    long csdKlass = long.Parse(kv["csdKlass"]);
                    long uu = long.Parse(kv["uu"]), scKlass = long.Parse(kv["scKlass"]);
                    // validate the cached pointers against live memory
                    if (_mem.ReadPtr(csd) == csdKlass && _mem.ReadPtr(psd + PSD_common) == csd)
                    {
                        int key = _mem.ReadInt(csd + CSD_stageKey);
                        if (key >= 0 && key < 1000000)
                        {
                            _psd = psd; _csd = csd; _csdKlass = csdKlass;
                            _uuStatics = uu; _scKlass = scKlass;
                            if (_uuStatics != 0)
                            {
                                long obj = _mem.ReadPtr(_uuStatics + UU_currentCache);
                                if (obj == 0 || _mem.ReadPtr(obj) != _scKlass) _uuStatics = 0;
                            }
                            sameBoot = true;
                        }
                    }
                }
                return true;
            }
            catch { return false; }
        }

        // ---- scanning ----

        void FindSaveData()
        {
            long psdKlass = _mem.FindClass("PlayerSaveData", "TaskbarHero");
            long csdKlass = _mem.FindClass("CommonSaveData", "TaskbarHero");
            if (psdKlass == 0 || csdKlass == 0)
                throw new Exception("save-data classes not found (game still loading?)");
            foreach (long r in _mem.FindInstances(psdKlass, 4096))
            {
                long c = _mem.ReadPtr(r + PSD_common);
                if (c != 0 && _mem.ReadPtr(c) == csdKlass)
                {
                    _psd = r; _csd = c; _csdKlass = csdKlass;
                    return;
                }
            }
            throw new Exception("CommonSaveData instance not found");
        }

        void BuildStageTable()
        {
            long klass = _mem.FindClass("StageInfoData", "TaskbarHero.Data");
            if (klass == 0) return;
            foreach (long r in _mem.FindInstances(klass, 4096))
            {
                int key = _mem.ReadInt(r + SID_StageKey);
                int act = _mem.ReadInt(r + SID_Act);
                int no = _mem.ReadInt(r + SID_No);
                int lvl = _mem.ReadInt(r + SID_Level);
                if (key <= 1000 || key >= 99999 || act < 0 || act > 50 || no < 0 || no > 99 || lvl <= 0 || lvl >= 100000)
                    continue;
                if (_stages.ContainsKey(key)) continue;
                var s = new StageInfo();
                s.Key = key; s.Act = act; s.No = no; s.Level = lvl;
                s.Waves = _mem.ReadInt(r + SID_Waves);
                int d = _mem.ReadInt(r + SID_Diff);
                s.Diff = (d >= 0 && d < DIFFS.Length) ? DIFFS[d] : d.ToString();
                int t = _mem.ReadInt(r + SID_Type);
                s.Type = (t >= 0 && t < STYPES.Length) ? STYPES[t] : t.ToString();
                s.NameKey = _mem.ReadIl2CppString(_mem.ReadPtr(r + SID_NameKey), 64);
                _stages[key] = s;
            }
        }

        void BuildHeroTable()
        {
            long klass = _mem.FindClass("HeroInfoData", "TaskbarHero.Data");
            if (klass == 0) return;
            foreach (long r in _mem.FindInstances(klass, 4096))
            {
                int key = _mem.ReadInt(r + HID_HeroKey);
                if (key <= 0 || key > 99999) continue;
                string nameKey = _mem.ReadIl2CppString(_mem.ReadPtr(r + HID_NameKey), 64);
                if (nameKey == null) continue;   // false positives have no name key
                int ct = _mem.ReadInt(r + HID_ClassType);
                string name = (ct >= 1 && ct < HCLASS.Length) ? HCLASS[ct] : nameKey;
                if (!_heroNames.ContainsKey(key)) _heroNames[key] = name;
            }
        }

        void FindLiveStageStatics()
        {
            // The static class 'up' holds the live stage system. Self-validated: the
            // static block is only accepted if its +0x88 slot points at a StageCache
            // instance. NOTE: 'up' is an obfuscated class name that the game's obfuscator
            // re-randomizes on updates (was 'uu' pre-1.00.27); re-dump and update the
            // scan bytes below if the live stage source stops resolving after a patch.
            _uuStatics = 0; _scKlass = 0;
            long scKlass = _mem.FindClass("StageCache", "");
            if (scKlass == 0) return;
            var strHits = _mem.FindBytes(new byte[] { 0x00, 0x75, 0x70, 0x00 }, 256); // "\0up\0"
            if (strHits.Count == 0) return;
            var targets = new HashSet<long>();
            foreach (long s in strHits) targets.Add(s + 1);
            foreach (long r in _mem.FindQwordRefs(targets, 512))
            {
                long klass = r - 0x10;
                long statics = _mem.ReadPtr(klass + KLASS_staticFields);
                if (statics == 0) continue;
                long obj = _mem.ReadPtr(statics + UU_currentCache);
                if (obj != 0 && _mem.ReadPtr(obj) == scKlass)
                {
                    _uuStatics = statics;
                    _scKlass = scKlass;
                    return;
                }
            }
        }

        // ---- public API ----

        public void Resolve(bool noCache, Action<string> log)
        {
            bool sameBoot;
            bool haveTables = LoadCache(noCache, out sameBoot);
            if (sameBoot)
            {
                log("address cache hit - no scan needed");
                return;
            }
            log(haveTables
                ? "tables cached - scanning live objects only (~30s)..."
                : "first run for this game build - full memory scan (~90s)...");
            FindSaveData();
            if (!haveTables || _stages.Count == 0) BuildStageTable();
            if (!haveTables || _heroNames.Count == 0) BuildHeroTable();
            FindLiveStageStatics();
            if (_uuStatics == 0) log("live stage statics not found - stage falls back to save data");
            SaveCache();
        }

        public GameState Read()
        {
            var st = new GameState();

            // stage identity: prefer the live loaded stage; save data lags until autosave
            st.Source = "save";
            if (_uuStatics != 0)
            {
                long sc = _mem.ReadPtr(_uuStatics + UU_currentCache);
                if (sc != 0)
                {
                    long sid = _mem.ReadPtr(sc + SC_infoData);
                    if (sid != 0)
                    {
                        int k = _mem.ReadInt(sid + SID_StageKey);
                        if (k > 0 && k < 1000000) { st.StageKey = k; st.Source = "live"; }
                    }
                }
            }
            if (st.StageKey == 0) st.StageKey = _mem.ReadInt(_csd + CSD_stageKey);
            st.SavedWave = _mem.ReadInt(_csd + CSD_stageWave);
            st.MaxStage = _mem.ReadInt(_csd + CSD_maxStage);
            StageInfo info;
            if (_stages.TryGetValue(st.StageKey, out info)) st.Info = info;

            // hero levels (List<HeroSaveData>: items +0x10, count +0x18, ptrs from items+0x20)
            var levels = new Dictionary<int, int>();
            long hlist = _mem.ReadPtr(_psd + PSD_heroSaves);
            if (hlist != 0)
            {
                long items = _mem.ReadPtr(hlist + 0x10);
                int n = _mem.ReadInt(hlist + 0x18);
                if (items != 0 && n > 0 && n <= 64)
                {
                    for (int i = 0; i < n; i++)
                    {
                        long h = _mem.ReadPtr(items + 0x20 + 8 * i);
                        if (h == 0) continue;
                        levels[_mem.ReadInt(h + HSD_heroKey)] = _mem.ReadInt(h + HSD_level);
                    }
                }
            }

            // deployed heroes (int[]: length +0x18, elements +0x20)
            long arr = _mem.ReadPtr(_csd + CSD_heroKeys);
            if (arr != 0)
            {
                int len = _mem.ReadInt(arr + 0x18);
                if (len > 0 && len <= 16)
                {
                    for (int i = 0; i < len; i++)
                    {
                        int hk = _mem.ReadInt(arr + 0x20 + 4 * i);
                        if (hk <= 0) continue;
                        var h = new HeroInfo();
                        h.Key = hk;
                        string name;
                        h.Name = _heroNames.TryGetValue(hk, out name) ? name : "Hero_" + hk;
                        int lvl;
                        h.Level = levels.TryGetValue(hk, out lvl) ? lvl : 0;
                        st.Heroes.Add(h);
                    }
                }
            }
            st.PetKey = _mem.ReadInt(_csd + CSD_petKey);
            return st;
        }
    }
}
