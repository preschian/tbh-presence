using System;
using System.Drawing;
using System.Windows.Forms;

namespace TbhPresence
{
    // System-tray host: runs the presence loop on a background thread and shows
    // a tray icon with the current status and a Quit option. No console window.
    public class TrayApp : ApplicationContext
    {
        readonly NotifyIcon _icon;
        readonly PresenceEngine _engine;
        readonly System.Threading.Thread _worker;
        string _lastStatus = "starting...";
        StatusForm _form;

        public TrayApp(PresenceEngine engine)
        {
            _engine = engine;

            var menu = new ContextMenuStrip();
            var status = new ToolStripMenuItem("Starting...") { Enabled = false };
            menu.Items.Add(status);
            menu.Items.Add(new ToolStripSeparator());
            var open = new ToolStripMenuItem("Status && Settings...");
            open.Click += delegate { OpenForm(); };
            menu.Items.Add(open);
            var quit = new ToolStripMenuItem("Quit");
            quit.Click += delegate { ExitThread(); };
            menu.Items.Add(quit);

            _icon = new NotifyIcon();
            _icon.Icon = LoadIcon();
            _icon.Text = "TaskBarHero Presence";   // tooltip (<= 63 chars)
            _icon.Visible = true;
            _icon.ContextMenuStrip = menu;
            _icon.DoubleClick += delegate { OpenForm(); };

            // engine reports status text -> reflect in tooltip + menu (marshal to UI thread)
            _engine.OnStatus += delegate(string s)
            {
                try
                {
                    if (menu.IsDisposed) return;
                    menu.BeginInvoke((Action)delegate
                    {
                        _lastStatus = s;
                        status.Text = s;
                        _icon.Text = Truncate("TBH: " + s, 63);
                    });
                }
                catch { }
            };

            _worker = new System.Threading.Thread(delegate() { _engine.Run(); });
            _worker.IsBackground = true;
            _worker.Start();

            ThreadExit += delegate { Shutdown(); };
        }

        void OpenForm()
        {
            if (_form == null || _form.IsDisposed)
            {
                _form = new StatusForm(delegate { return _lastStatus; });
                _form.Show();
            }
            else
            {
                if (_form.WindowState == FormWindowState.Minimized)
                    _form.WindowState = FormWindowState.Normal;
                _form.Activate();
            }
        }

        void Shutdown()
        {
            _engine.Stop();
            try { _worker.Join(3000); } catch { }
            _icon.Visible = false;
            _icon.Dispose();
        }

        static string Truncate(string s, int max)
        {
            return s != null && s.Length > max ? s.Substring(0, max) : s;
        }

        // The exe's own icon (embedded TBH logo via /win32icon); drawn fallback
        // keeps things working if extraction ever fails.
        static Icon LoadIcon()
        {
            try
            {
                var ico = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (ico != null) return ico;
            }
            catch { }
            return MakeIcon();
        }

        // Draw a tiny icon at runtime so the exe stays a single self-contained file.
        static Icon MakeIcon()
        {
            using (var bmp = new Bitmap(16, 16))
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                using (var bg = new SolidBrush(Color.FromArgb(88, 101, 242)))   // Discord blurple
                    g.FillRectangle(bg, 2, 2, 12, 12);
                using (var fg = new SolidBrush(Color.White))
                {
                    g.FillRectangle(fg, 4, 9, 2, 3);   // three "hero" bars
                    g.FillRectangle(fg, 7, 6, 2, 6);
                    g.FillRectangle(fg, 10, 8, 2, 4);
                }
                IntPtr h = bmp.GetHicon();
                using (var tmp = Icon.FromHandle(h))
                    return (Icon)tmp.Clone();
            }
        }
    }
}
