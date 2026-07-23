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

        // Structured live state for the UI (separate channels so a transient
        // diagnostic never clobbers the stage or the connection indicator).
        public volatile string LastStatus = "starting...";
        public volatile string LastStageLabel = null;   // null = no stage read
        public volatile bool DiscordConnected = false;

        // Master switch: when off, the loop still reads the stage for the window
        // but never connects to Discord / clears any activity it had set.
        public volatile bool PresenceEnabled;

        public PresenceEngine(bool noCache, int interval, string clientId, string cachePath)
        {
            _noCache = noCache;
            _interval = interval < 1 ? 1 : interval;
            _clientId = clientId;
            _cachePath = cachePath;
            PresenceEnabled = AppSettings.PresenceEnabled;
        }

        public void Stop() { _running = false; }

        // Flip presence on/off and persist the choice for next launch.
        public void SetPresenceEnabled(bool on)
        {
            PresenceEnabled = on;
            AppSettings.PresenceEnabled = on;
        }

        void Status(string s)
        {
            LastStatus = s;
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
            string lastSent = null;                 // null = none, "" = cleared (Discord)
            string lastStageSig = null;             // last stage surfaced to the UI/status
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
                        LastStageLabel = null;
                        lastStageSig = null;
                        DiscordConnected = discord.Connected;
                        // game closed = plugin dll unlocked; good moment to (re)deploy
                        if (Build.Synth) AutoSynthDeploy.TryDeployThrottled(Status);
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
                            if (GameRestart.IsDue(proc))
                            {
                                // Clear Discord before killing so the profile doesn't stick.
                                if (discord.Connected && lastSent != "")
                                {
                                    try { discord.ClearActivity(); lastSent = ""; } catch { discord.Dispose(); }
                                }
                                GameRestart.TryRestart(proc, Status);
                                break;
                            }

                            if (PresenceEnabled)
                            {
                                if (!discord.Connected && (DateTime.Now - lastDiscordTry).TotalSeconds >= 30)
                                {
                                    lastDiscordTry = DateTime.Now;
                                    if (discord.Connect()) lastSent = null;
                                    else Status("Discord not running - will retry");
                                }
                            }
                            else if (discord.Connected && lastSent != "")
                            {
                                // turned off while running: clear what we last showed
                                try { discord.ClearActivity(); lastSent = ""; } catch { discord.Dispose(); }
                            }

                            GameState st = null;
                            try { st = reader.Read(); } catch { }
                            if (st != null && st.StageKey > 0)
                            {
                                LastStageLabel = st.Label();   // structured channel for the UI
                                string sig = st.Details() + "|" + st.PartyLine();
                                if (sig != lastStageSig) { Status(st.Label()); lastStageSig = sig; }
                                if (PresenceEnabled && discord.Connected && sig != lastSent)
                                {
                                    try
                                    {
                                        discord.SetActivity(st.Details(), st.PartyLine(), startEpoch);
                                        lastSent = sig;
                                    }
                                    catch (Exception ex)
                                    {
                                        Status("Discord lost (" + ex.Message + ") - reconnecting");
                                        discord.Dispose();
                                    }
                                }
                            }
                            DiscordConnected = discord.Connected;
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
