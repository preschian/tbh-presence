using System;
using System.Diagnostics;
using System.Threading;

namespace TbhCompanion
{
    // The presence loop, independent of any UI. Reports human-readable status
    // via OnStatus (for the tray tooltip / console). Call Run() on a thread and
    // Stop() to end it.
    public class PresenceEngine
    {
        const string GAME = "TaskBarHero";

        readonly bool _noCache;
        readonly int _interval;
        readonly string _clientId;
        readonly string _cachePath;

        volatile bool _running = true;

        public event Action<string> OnStatus;

        public PresenceEngine(bool noCache, int interval, string clientId, string cachePath)
        {
            _noCache = noCache;
            _interval = interval < 1 ? 1 : interval;
            _clientId = clientId;
            _cachePath = cachePath;
        }

        public void Stop() { _running = false; }

        void Status(string s)
        {
            var h = OnStatus;
            if (h != null) { try { h(s); } catch { } }
        }

        static Process FindGame()
        {
            var p = Process.GetProcessesByName(GAME);
            return p.Length > 0 ? p[0] : null;
        }

        public void Run()
        {
            Status("client id " + _clientId + " - waiting for game");
            var discord = new DiscordRpc(_clientId);
            string lastSent = null;                 // null = none, "" = cleared
            DateTime lastDiscordTry = DateTime.MinValue;

            try
            {
                while (_running)
                {
                    var proc = FindGame();
                    if (proc == null)
                    {
                        if (lastSent != "" && discord.Connected)
                        {
                            try { discord.ClearActivity(); } catch { discord.Dispose(); }
                            lastSent = "";
                        }
                        // game closed = plugin dll unlocked; good moment to (re)deploy
                        AutoSynthDeploy.TryDeployThrottled(Status);
                        Status("waiting for TaskBarHero...");
                        Sleep(5);
                        continue;
                    }

                    Status("attached (PID " + proc.Id + ") - resolving...");
                    using (var mem = new Mem(proc.Id))
                    {
                        var reader = new GameReader(mem, proc, _cachePath);
                        bool resolved = false;
                        while (_running && !proc.HasExited && !resolved)
                        {
                            try { reader.Resolve(_noCache, Status); resolved = true; }
                            catch (Exception ex)
                            {
                                Status("not ready (" + ex.Message + ") - retry 10s");
                                Sleep(10);
                            }
                        }
                        if (!resolved) continue;

                        long startEpoch = 0;
                        try { startEpoch = new DateTimeOffset(proc.StartTime).ToUnixTimeSeconds(); } catch { }

                        while (_running && !proc.HasExited)
                        {
                            if (!discord.Connected && (DateTime.Now - lastDiscordTry).TotalSeconds >= 30)
                            {
                                lastDiscordTry = DateTime.Now;
                                if (discord.Connect()) lastSent = null;
                                else Status("Discord not running - will retry");
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
                                        Status(st.Label());
                                        lastSent = sig;
                                    }
                                    catch (Exception ex)
                                    {
                                        Status("Discord lost (" + ex.Message + ") - reconnecting");
                                        discord.Dispose();
                                    }
                                }
                            }
                            Sleep(_interval);
                        }
                    }
                    if (_running) Status("game closed - waiting for restart...");
                }
            }
            finally
            {
                if (discord.Connected) { try { discord.ClearActivity(); } catch { } }
                discord.Dispose();
            }
        }

        void Sleep(int seconds)
        {
            for (int i = 0; i < seconds * 2 && _running; i++)
                Thread.Sleep(500);
        }
    }
}
