using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace TbhCompanion
{
    static class Program
    {
        const string GAME = "TaskBarHero";
        const string DEFAULT_CLIENT_ID = "1522386796078432429";

        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);
        [DllImport("kernel32.dll")] static extern bool AllocConsole();
        [DllImport("kernel32.dll")] static extern bool FreeConsole();
        const int ATTACH_PARENT_PROCESS = -1;

        static string CachePath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "tbh-companion", "cache.txt");
        }

        [STAThread]
        static int Main(string[] argv)
        {
            bool once = false, noCache = false, console = false;
            int interval = 5;
            string clientId = DEFAULT_CLIENT_ID;

            for (int i = 0; i < argv.Length; i++)
            {
                switch (argv[i])
                {
                    case "--once": once = true; break;
                    case "--console": console = true; break;
                    case "--no-cache": noCache = true; break;
                    case "--interval": interval = int.Parse(argv[++i]); break;
                    case "--client-id": clientId = argv[++i]; break;
                    case "-h":
                    case "--help":
                        return ShowHelp();
                    default:
                        EnsureConsole();
                        Console.Error.WriteLine("unknown argument: " + argv[i]);
                        return 2;
                }
            }
            if (interval < 1) interval = 1;

            if (once) return RunOnce(noCache);

            // One presence at a time: two would fight over Discord.
            bool createdNew;
            using (var mutex = new System.Threading.Mutex(true, "TbhCompanion.SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    EnsureConsole();
                    Console.Error.WriteLine("TbhCompanion is already running (see the system tray).");
                    return 1;
                }
                if (console) return RunConsole(noCache, interval, clientId);
                return RunTray(noCache, interval, clientId);
            }
        }

        // Attach to the parent console (when launched from a terminal) or allocate
        // one, so console modes can print even though the exe is a Windows-subsystem app.
        static void EnsureConsole()
        {
            if (!AttachConsole(ATTACH_PARENT_PROCESS)) AllocConsole();
        }

        static int ShowHelp()
        {
            EnsureConsole();
            Console.WriteLine("TbhCompanion - TaskBarHero Discord Rich Presence (read-only memory reader)");
            Console.WriteLine();
            Console.WriteLine("  TbhCompanion.exe                 run in the system tray (no window)");
            Console.WriteLine("  TbhCompanion.exe --console       run in the console with live logging");
            Console.WriteLine("  TbhCompanion.exe --once          print one state reading as JSON and exit");
            Console.WriteLine();
            Console.WriteLine("  --interval <sec>                poll interval (default 5)");
            Console.WriteLine("  --client-id <id>                Discord application id");
            Console.WriteLine("  --no-cache                      ignore the address cache, full rescan");
            return 0;
        }

        static int RunOnce(bool noCache)
        {
            EnsureConsole();
            var proc = Process.GetProcessesByName(GAME);
            if (proc.Length == 0) { Console.Error.WriteLine("TaskBarHero is not running."); return 1; }
            using (var mem = new Mem(proc[0].Id))
            {
                var reader = new GameReader(mem, proc[0], CachePath());
                reader.Resolve(noCache, delegate(string m) { Console.Error.WriteLine(m); });
                var st = reader.Read();
                var o = new Dictionary<string, object>();
                o["stageKey"] = st.StageKey;
                o["savedWave"] = st.SavedWave;
                o["maxCompletedStage"] = st.MaxStage;
                if (st.Info != null)
                {
                    o["act"] = st.Info.Act; o["stageNo"] = st.Info.No; o["level"] = st.Info.Level;
                    o["waveAmount"] = st.Info.Waves; o["difficulty"] = st.Info.Diff;
                    o["stageType"] = st.Info.Type; o["nameKey"] = st.Info.NameKey;
                }
                var hs = new List<object>();
                foreach (var h in st.Heroes)
                {
                    var ho = new Dictionary<string, object>();
                    ho["key"] = h.Key; ho["name"] = h.Name; ho["level"] = h.Level;
                    hs.Add(ho);
                }
                o["heroes"] = hs;
                o["petKey"] = st.PetKey;
                o["stageSource"] = st.Source;
                o["label"] = st.Label();
                Console.WriteLine(new JavaScriptSerializer().Serialize(o));
            }
            return 0;
        }

        static int RunConsole(bool noCache, int interval, string clientId)
        {
            EnsureConsole();
            AutoSynthDeploy.TryDeploy(delegate(string s)
            {
                Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + s);
            });
            var engine = new PresenceEngine(noCache, interval, clientId, CachePath());
            engine.OnStatus += delegate(string s)
            {
                Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + s);
            };
            Console.CancelKeyPress += delegate(object o, ConsoleCancelEventArgs e)
            {
                e.Cancel = true;
                engine.Stop();
            };
            engine.Run();
            return 0;
        }

        static int RunTray(bool noCache, int interval, string clientId)
        {
            try { SetProcessDPIAware(); } catch { }   // crisp text on high-DPI displays
            Application.EnableVisualStyles();
            AutoSynthDeploy.TryDeploy(delegate(string s) { Debug.WriteLine(s); });
            var engine = new PresenceEngine(noCache, interval, clientId, CachePath());
            Application.Run(new TrayApp(engine));
            return 0;
        }
    }
}
