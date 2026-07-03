using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Text;
using System.Web.Script.Serialization;

namespace TbhPresence
{
    // Minimal Discord Rich Presence over the desktop client's IPC named pipe.
    public class DiscordRpc : IDisposable
    {
        readonly string _clientId;
        NamedPipeClientStream _pipe;
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        public DiscordRpc(string clientId) { _clientId = clientId; }

        public bool Connected { get { return _pipe != null && _pipe.IsConnected; } }

        public bool Connect()
        {
            Dispose();
            for (int i = 0; i < 10; i++)
            {
                var pipe = new NamedPipeClientStream(".", "discord-ipc-" + i, PipeDirection.InOut);
                try { pipe.Connect(500); }
                catch { pipe.Dispose(); continue; }
                try
                {
                    var hs = new Dictionary<string, object>();
                    hs["v"] = 1;
                    hs["client_id"] = _clientId;
                    SendFrame(pipe, 0, Json.Serialize(hs));
                    string reply = ReadFrame(pipe);
                    if (reply != null && reply.Contains("\"evt\"") && reply.Contains("READY"))
                    {
                        _pipe = pipe;
                        return true;
                    }
                    pipe.Dispose();
                }
                catch { pipe.Dispose(); }
            }
            return false;
        }

        // activity == null clears the presence
        public void SetActivity(string details, string state, long startEpochSeconds)
        {
            var args = new Dictionary<string, object>();
            args["pid"] = System.Diagnostics.Process.GetCurrentProcess().Id;
            if (details != null)
            {
                var activity = new Dictionary<string, object>();
                activity["details"] = details;
                if (state != null) activity["state"] = state;
                if (startEpochSeconds > 0)
                {
                    var ts = new Dictionary<string, object>();
                    ts["start"] = startEpochSeconds;
                    activity["timestamps"] = ts;
                }
                var assets = new Dictionary<string, object>();
                assets["large_image"] = "logo_2x";   // uploaded in the Discord dev portal (Rich Presence > Art Assets)
                assets["large_text"] = "TaskBarHero";
                activity["assets"] = assets;
                args["activity"] = activity;
            }
            var cmd = new Dictionary<string, object>();
            cmd["cmd"] = "SET_ACTIVITY";
            cmd["args"] = args;
            cmd["nonce"] = Guid.NewGuid().ToString();
            SendFrame(_pipe, 1, Json.Serialize(cmd));
            string reply = ReadFrame(_pipe);
            if (reply != null && reply.Contains("\"evt\":\"ERROR\""))
                throw new Exception("Discord rejected activity: " + reply);
        }

        public void ClearActivity() { SetActivity(null, null, 0); }

        static void SendFrame(NamedPipeClientStream pipe, int op, string json)
        {
            byte[] payload = Encoding.UTF8.GetBytes(json);
            byte[] buf = new byte[8 + payload.Length];
            BitConverter.GetBytes(op).CopyTo(buf, 0);
            BitConverter.GetBytes(payload.Length).CopyTo(buf, 4);
            payload.CopyTo(buf, 8);
            pipe.Write(buf, 0, buf.Length);
            pipe.Flush();
        }

        static string ReadFrame(NamedPipeClientStream pipe)
        {
            for (int guard = 0; guard < 4; guard++)   // skip over PING frames
            {
                byte[] hdr = ReadExact(pipe, 8);
                int op = BitConverter.ToInt32(hdr, 0);
                int len = BitConverter.ToInt32(hdr, 4);
                if (len < 0 || len > 1 << 20) throw new Exception("bad frame length");
                byte[] payload = ReadExact(pipe, len);
                string json = Encoding.UTF8.GetString(payload);
                if (op == 3) { SendFrame(pipe, 4, json); continue; }   // PING -> PONG
                return json;
            }
            return null;
        }

        static byte[] ReadExact(NamedPipeClientStream pipe, int n)
        {
            byte[] buf = new byte[n];
            int got = 0;
            while (got < n)
            {
                int r = pipe.Read(buf, got, n - got);
                if (r <= 0) throw new Exception("Discord pipe closed");
                got += r;
            }
            return buf;
        }

        public void Dispose()
        {
            if (_pipe != null)
            {
                try { _pipe.Dispose(); } catch { }
                _pipe = null;
            }
        }
    }
}
