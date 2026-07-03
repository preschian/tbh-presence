using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Web.Script.Serialization;

namespace TbhPresence
{
    static class Program
    {
        const string GAME = "TaskBarHero";
        const string DEFAULT_CLIENT_ID = "1522386796078432429";

        static volatile bool _running = true;

        static int Main(string[] argv)
        {
            bool once = false, noCache = false;
            int interval = 5;
            string clientId = DEFAULT_CLIENT_ID;

            for (int i = 0; i < argv.Length; i++)
            {
                switch (argv[i])
                {
                    case "--once": once = true; break;
                    case "--no-cache": noCache = true; break;
                    case "--interval": interval = int.Parse(argv[++i]); break;
                    case "--client-id": clientId = argv[++i]; break;
                    case "-h":
                    case "--help":
                        Console.WriteLine("TbhPresence - TaskBarHero Discord Rich Presence (read-only memory reader)");
                        Console.WriteLine();
                        Console.WriteLine("  TbhPresence.exe                 run presence (default)");
                        Console.WriteLine("  TbhPresence.exe --once          print one state reading as JSON and exit");
                        Console.WriteLine("  --interval <sec>                poll interval (default 5)");
                        Console.WriteLine("  --client-id <id>                Discord application id");
                        Console.WriteLine("  --no-cache                      ignore the address cache, full rescan");
                        return 0;
                    default:
                        Console.Error.WriteLine("unknown argument: " + argv[i]);
                        return 2;
                }
            }
            if (interval < 1) interval = 1;

            string cachePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "tbh-presence", "cache.txt");

            if (once) return RunOnce(noCache, cachePath);
            return RunPresence(noCache, interval, clientId, cachePath);
        }

        static Process FindGame()
        {
            var procs = Process.GetProcessesByName(GAME);
            return procs.Length > 0 ? procs[0] : null;
        }

        static void Log(string msg) { Log(msg, ConsoleColor.Gray); }
        static void Log(string msg, ConsoleColor color)
        {
            Console.ForegroundColor = color;
            Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg);
            Console.ResetColor();
        }

        static int RunOnce(bool noCache, string cachePath)
        {
            var proc = FindGame();
            if (proc == null) { Console.Error.WriteLine("TaskBarHero is not running."); return 1; }
            using (var mem = new Mem(proc.Id))
            {
                var reader = new GameReader(mem, proc, cachePath);
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

        static int RunPresence(bool noCache, int interval, string clientId, string cachePath)
        {
            Console.CancelKeyPress += delegate(object s, ConsoleCancelEventArgs e)
            {
                e.Cancel = true;
                _running = false;
            };
            Log("TaskBarHero Rich Presence - client id " + clientId + ". Ctrl+C to quit.", ConsoleColor.Cyan);

            var discord = new DiscordRpc(clientId);
            string lastSent = null;          // null = nothing sent yet, "" = cleared
            DateTime lastDiscordTry = DateTime.MinValue;

            try
            {
                while (_running)
                {
                    // 1. game process
                    var proc = FindGame();
                    if (proc == null)
                    {
                        if (lastSent != "" && discord.Connected)
                        {
                            try { discord.ClearActivity(); Log("game closed - presence cleared", ConsoleColor.Yellow); }
                            catch { discord.Dispose(); }
                            lastSent = "";
                        }
                        SleepInterruptible(5);
                        continue;
                    }

                    // 2. attach + resolve (retries until the save data exists)
                    Log("attached to " + GAME + " (PID " + proc.Id + ")", ConsoleColor.Cyan);
                    using (var mem = new Mem(proc.Id))
                    {
                        var reader = new GameReader(mem, proc, cachePath);
                        bool resolved = false;
                        while (_running && !proc.HasExited && !resolved)
                        {
                            try
                            {
                                reader.Resolve(noCache, delegate(string m) { Log(m, ConsoleColor.DarkGray); });
                                resolved = true;
                            }
                            catch (Exception ex)
                            {
                                Log("not ready (" + ex.Message + ") - retrying in 10s...", ConsoleColor.Yellow);
                                SleepInterruptible(10);
                            }
                        }
                        if (!resolved) continue;

                        long startEpoch = 0;
                        try { startEpoch = new DateTimeOffset(proc.StartTime).ToUnixTimeSeconds(); } catch { }

                        // 3. poll loop
                        while (_running && !proc.HasExited)
                        {
                            // ensure Discord connection (retry every 30s)
                            if (!discord.Connected && (DateTime.Now - lastDiscordTry).TotalSeconds >= 30)
                            {
                                lastDiscordTry = DateTime.Now;
                                if (discord.Connect()) { Log("connected to Discord", ConsoleColor.Cyan); lastSent = null; }
                                else Log("Discord not running - will retry", ConsoleColor.Yellow);
                            }

                            GameState st = null;
                            try { st = reader.Read(); } catch { }
                            if (st != null && st.StageKey > 0 && discord.Connected)
                            {
                                string sig = st.Details() + "|" + st.PartyLine();
                                if (sig != lastSent)
                                {
                                    try
                                    {
                                        discord.SetActivity(st.Details(), st.PartyLine(), startEpoch);
                                        Log("presence: " + st.Label(), ConsoleColor.Green);
                                        lastSent = sig;
                                    }
                                    catch (Exception ex)
                                    {
                                        Log("Discord lost (" + ex.Message + ") - reconnecting...", ConsoleColor.Yellow);
                                        discord.Dispose();
                                    }
                                }
                            }
                            SleepInterruptible(interval);
                        }
                    }
                    if (_running) Log("game closed - waiting for restart...", ConsoleColor.Yellow);
                }
            }
            finally
            {
                if (discord.Connected)
                {
                    try { discord.ClearActivity(); } catch { }
                }
                discord.Dispose();
            }
            Log("bye");
            return 0;
        }

        static void SleepInterruptible(int seconds)
        {
            for (int i = 0; i < seconds * 2 && _running; i++)
                Thread.Sleep(500);
        }
    }
}
