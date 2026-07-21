using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace TbhCompanion
{
    // Status & settings window (clean modern theme, side-panel layout).
    // Left rail: brand + live status. Right: settings.
    public class StatusForm : Form
    {
        static readonly string[] Grades =
            { "Common", "Uncommon", "Rare", "Legendary", "Immortal", "Arcana", "Beyond", "Celestial", "Divine", "Cosmic" };

        // In-game synthesis sub-recipe labels (from the Cube dropdown), plus Max.
        // DesiredLevel stores the bracket's lower bound (0 = highest unlocked).
        static readonly string[] RecipeLabels =
        {
            "Max", "Lv.1~10", "Lv.10~20", "Lv.15~30", "Lv.20~40",
            "Lv.30~50", "Lv.40~65", "Lv.50~65", "Lv.65~80"
        };
        static readonly int[] RecipeLevels = { 0, 1, 10, 15, 20, 30, 40, 50, 65 };

        static readonly string StatusPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "tbh-companion", "autosynth-status.json");

        const int W = 640, SideW = 188, H = 456;
        const int MainW = W - SideW - 40; // content width in main pane

        readonly Func<string> _stageLabel;
        readonly Func<bool> _discordConnected;
        readonly Func<string> _diag;
        readonly Func<bool> _presenceEnabled;
        readonly Action<bool> _setPresenceEnabled;
        readonly Timer _timer;
        string _cfgPath, _bepinexCfgPath;
        bool _modOpRunning;          // install or remove in flight
        bool _modsLayoutReady;
        bool _modsPresent;

        Bitmap _icon;
        Rectangle _closeRect;
        Point _dragOffset; bool _dragging;
        float _s = 1f;
        int Sc(double v) { return (int)Math.Round(v * _s); }

        LiveStrip _live;
        Panel _side, _main;
        Toggle _presenceToggle;
        Toggle _autoStart, _showConsole;
        TypeTile _tEquip, _tMaterials, _tAccessories;
        SegmentBar _seg;
        Label _rarityValue;
        Stepper _cycleMin;
        FlatDrop _desiredLevel;
        FlatButton _saveBtn, _setupBtn, _removeBtn;
        Label _cfgNote;

        public StatusForm(Func<string> stageLabel, Func<bool> discordConnected, Func<string> diag,
            Func<bool> presenceEnabled, Action<bool> setPresenceEnabled)
        {
            _stageLabel = stageLabel;
            _discordConnected = discordConnected;
            _diag = diag;
            _presenceEnabled = presenceEnabled;
            _setPresenceEnabled = setPresenceEnabled;

            Text = "TBH Companion";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None;   // fixed 96dpi design; Windows scales the window
            Font = Theme.F(9f, FontStyle.Regular);
            BackColor = Theme.FormBg;
            try { using (var g = Graphics.FromHwnd(IntPtr.Zero)) _s = g.DpiX / 96f; } catch { _s = 1f; }
            int height = Build.Synth ? H : 260;
            ClientSize = new Size(Sc(W), Sc(height));
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            try { using (var ico = Icon.ExtractAssociatedIcon(Application.ExecutablePath)) _icon = ico.ToBitmap(); } catch { }

            BuildSidePanel();
            BuildMainPane();

            LoadConfig();
            UpdateStatus();

            _timer = new Timer { Interval = 1000 };
            _timer.Tick += delegate { UpdateStatus(); };
            _timer.Start();
            FormClosed += delegate { _timer.Stop(); _timer.Dispose(); if (_icon != null) _icon.Dispose(); };

            Load += delegate { ApplyRegion(); };
        }

        void ApplyRegion()
        {
            using (var p = Theme.Round(new Rectangle(0, 0, Width, Height), Sc(12)))
                Region = new Region(p);
        }

        // ---- side panel (brand + pills + status) ----

        void BuildSidePanel()
        {
            _side = new Panel
            {
                BackColor = Theme.SideBg,
                Location = new Point(0, 0),
                Size = new Size(Sc(SideW), Height)
            };
            _side.Paint += PaintSide;
            _side.MouseDown += SideMouseDown;
            _side.MouseMove += SideMouseMove;
            _side.MouseUp += delegate { _dragging = false; };
            Controls.Add(_side);

            // Live status at the bottom: Presence + Synth/cycles as compact cards.
            int rows = Build.Synth ? 2 : 1;
            int statusH = rows * 68 + (rows - 1) * 8;
            _live = new LiveStrip { Columns = rows };
            _live.SetBounds(Sc(12), Height - Sc(14 + statusH), Sc(SideW - 24), Sc(statusH));
            _live.SetRow(0, "Presence", "—", "", "Off", Theme.TextMuted);
            if (Build.Synth)
                _live.SetRow(1, "Synth", "—", "", "Off", Theme.TextMuted);
            _side.Controls.Add(_live);
        }

        void PaintSide(object sender, PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            if (_icon != null)
            {
                var ir = new Rectangle(Sc(16), Sc(18), Sc(32), Sc(32));
                using (var pth = Theme.Round(ir, Sc(7))) { g.SetClip(pth); g.DrawImage(_icon, ir); g.ResetClip(); }
            }
            using (var f = Theme.F(11f, FontStyle.Bold)) using (var b = new SolidBrush(Theme.TextDark))
                g.DrawString("TBH Companion", f, b, new PointF(Sc(56), Sc(24)));

            // Soft rule above the status block.
            int ruleY = _live.Top - Sc(14);
            using (var pen = new Pen(Theme.Divider))
                g.DrawLine(pen, Sc(16), ruleY, _side.Width - Sc(16), ruleY);

            using (var pen = new Pen(Theme.CardBorder))
                g.DrawLine(pen, _side.Width - 1, Sc(12), _side.Width - 1, _side.Height - Sc(12));
        }

        void SideMouseDown(object sender, MouseEventArgs e) { BeginDrag(e.Location); }

        void SideMouseMove(object sender, MouseEventArgs e)
        {
            if (_dragging) Location = new Point(Location.X + e.X - _dragOffset.X, Location.Y + e.Y - _dragOffset.Y);
        }

        void BeginDrag(Point local)
        {
            _dragging = true;
            _dragOffset = local;
        }

        // ---- main pane (settings / presence) ----

        void BuildMainPane()
        {
            _main = new Panel
            {
                BackColor = Theme.FormBg,
                Location = new Point(Sc(SideW), 0),
                Size = new Size(Sc(W - SideW), Height)
            };
            _main.Paint += PaintMain;
            _main.MouseDown += MainMouseDown;
            _main.MouseMove += MainMouseMove;
            _main.MouseUp += delegate { _dragging = false; };
            Controls.Add(_main);

            if (Build.Synth)
            {
                BuildSettings();
            }
            else BuildPresenceOnly();
        }

        void PaintMain(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            _closeRect = new Rectangle(_main.Width - Sc(34), Sc(12), Sc(22), Sc(22));
            using (var f = Theme.F(13f, FontStyle.Regular)) using (var b = new SolidBrush(Theme.TextMuted))
            {
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString("×", f, b, _closeRect, sf);
            }
        }

        void MainMouseDown(object sender, MouseEventArgs e)
        {
            if (_closeRect.Contains(e.Location)) { Close(); return; }
            if (e.Y <= Sc(40)) BeginDrag(e.Location);
        }

        void MainMouseMove(object sender, MouseEventArgs e)
        {
            if (_dragging) Location = new Point(Location.X + e.X - _dragOffset.X, Location.Y + e.Y - _dragOffset.Y);
        }

        void BuildPresenceOnly()
        {
            AddMainLabel("Discord Presence", 20, 28, Theme.TextDark, Theme.F(11f, FontStyle.Bold));
            AddMainLabel("Show your current stage on Discord", 20, 52, Theme.TextMuted, Theme.F(8.5f, FontStyle.Regular));
            _presenceToggle = MakePresenceToggle(MainW - 44, 34);
            _main.Controls.Add(_presenceToggle);
        }

        void BuildSettings()
        {
            int right = MainW - 44;
            int y = 20;

            // ---- 1. Discord Presence ----
            AddMainLabel("Discord Presence", 20, y, Theme.TextDark, Theme.F(10f, FontStyle.Bold));
            y += 28;
            AddMainLabel("Show stage on Discord", 20, y, Theme.TextDark, Theme.F(9.5f, FontStyle.Regular));
            _presenceToggle = MakePresenceToggle(right, y - 2);
            _main.Controls.Add(_presenceToggle);
            y += 36;
            AddMainDivider(y);
            y += 14;

            // ---- 2. Auto synthesis ----
            AddMainLabel("Auto synthesis", 20, y, Theme.TextDark, Theme.F(10f, FontStyle.Bold));
            y += 28;

            AddMainLabel("Enable auto synthesis", 20, y, Theme.TextDark, Theme.F(9.5f, FontStyle.Regular));
            _autoStart = new Toggle(); _autoStart.SetBounds(Sc(right), Sc(y - 2), Sc(44), Sc(24));
            _main.Controls.Add(_autoStart);
            y += 32;

            AddMainLabel("Show BepInEx console", 20, y, Theme.TextDark, Theme.F(9.5f, FontStyle.Regular));
            _showConsole = new Toggle(); _showConsole.SetBounds(Sc(right), Sc(y - 2), Sc(44), Sc(24));
            _main.Controls.Add(_showConsole);
            y += 34;

            AddMainLabel("Types", 20, y, Theme.TextMuted, Theme.F(8.5f, FontStyle.Regular));
            y += 20;
            _tEquip = new TypeTile { Caption = "Equipment" };
            _tMaterials = new TypeTile { Caption = "Materials" };
            _tAccessories = new TypeTile { Caption = "Accessories" };
            var tiles = new[] { _tEquip, _tMaterials, _tAccessories };
            int gap = 8, tw = (MainW - gap * 2) / 3;
            for (int i = 0; i < 3; i++)
            {
                tiles[i].SetBounds(Sc(20 + i * (tw + gap)), Sc(y), Sc(tw), Sc(28));
                _main.Controls.Add(tiles[i]);
            }
            y += 40;

            AddMainLabel("Max rarity", 20, y, Theme.TextMuted, Theme.F(8.5f, FontStyle.Regular));
            _rarityValue = AddMainLabelBox("Legendary", MainW - 140 + 20, y - 1, 140, 18, Theme.Amber, Theme.F(9f, FontStyle.Bold), ContentAlignment.MiddleRight);
            y += 20;
            _seg = new SegmentBar { Value = 3 };
            _seg.SetBounds(Sc(20), Sc(y), Sc(MainW), Sc(8));
            _seg.ValueChanged += delegate { UpdateRarityLabel(); };
            _main.Controls.Add(_seg);
            y += 24;

            AddMainLabel("Target level", 20, y + 4, Theme.TextMuted, Theme.F(8.5f, FontStyle.Regular));
            _desiredLevel = new FlatDrop { Items = RecipeLabels, SelectedIndex = 0 };
            _desiredLevel.SetBounds(Sc(MainW - 8 - 128), Sc(y), Sc(128), Sc(28));
            _main.Controls.Add(_desiredLevel);
            y += 36;

            AddMainLabel("Cycle interval", 20, y + 4, Theme.TextMuted, Theme.F(8.5f, FontStyle.Regular));
            AddMainLabel("min", MainW - 8, y + 6, Theme.TextMuted, Theme.F(8.5f, FontStyle.Regular));
            _cycleMin = new Stepper { Min = 1, Max = 1440, Step = 1, Decimals = 0, Value = 5 };
            _cycleMin.SetBounds(Sc(MainW - 8 - 24 - 100), Sc(y), Sc(100), Sc(28));
            _main.Controls.Add(_cycleMin);
            y += 40;

            _saveBtn = new FlatButton { Text = "Save", Fill = Theme.Accent };
            _saveBtn.SetBounds(Sc(20), Sc(y), Sc(88), Sc(30));
            _saveBtn.Click += delegate { SaveConfig(); };
            _main.Controls.Add(_saveBtn);

            _removeBtn = new FlatButton { Text = "Remove mods", Fill = Theme.Secondary };
            _removeBtn.SetBounds(Sc(116), Sc(y), Sc(120), Sc(30));
            _removeBtn.Click += delegate { RunRemove(); };
            _removeBtn.Visible = false;
            _main.Controls.Add(_removeBtn);

            _setupBtn = new FlatButton { Text = "Install mods", Fill = Theme.Secondary };
            _setupBtn.SetBounds(Sc(20), Sc(y), Sc(120), Sc(30));
            _setupBtn.Click += delegate { RunSetup(); };
            _setupBtn.Visible = false;
            _main.Controls.Add(_setupBtn);

            _cfgNote = new Label
            {
                AutoSize = false,
                Location = new Point(Sc(248), Sc(y)),
                Size = new Size(Sc(MainW - 228), Sc(30)),
                ForeColor = Theme.TextMuted,
                BackColor = Theme.FormBg,
                Font = Theme.F(8.5f, FontStyle.Regular),
                TextAlign = ContentAlignment.MiddleLeft
            };
            _main.Controls.Add(_cfgNote);

            RefreshModsRow(forceLayout: true);
        }

        void RefreshModsRow(bool forceLayout = false)
        {
            bool present = BepInExSetup.HasRemnants();
            _setupBtn.Visible = !present;
            _saveBtn.Visible = present;
            _removeBtn.Visible = present;
            if (!forceLayout && _modsLayoutReady && _modsPresent == present) return;
            _modsLayoutReady = true;
            _modsPresent = present;
            int y = _cfgNote.Top;
            if (present)
            {
                _cfgNote.Location = new Point(Sc(248), y);
                _cfgNote.Size = new Size(Sc(MainW - 228), Sc(30));
            }
            else
            {
                _cfgNote.Location = new Point(Sc(148), y);
                _cfgNote.Size = new Size(Sc(MainW - 128), Sc(30));
            }
        }

        // ---- helpers ----

        Toggle MakePresenceToggle(int x, int y)
        {
            var t = new Toggle();
            t.SetBounds(Sc(x), Sc(y), Sc(44), Sc(24));
            t.Checked = _presenceEnabled == null || _presenceEnabled();
            t.CheckedChanged += delegate
            {
                if (_setPresenceEnabled != null) _setPresenceEnabled(t.Checked);
            };
            return t;
        }

        Label AddMainLabel(string text, int x, int y, Color color, Font font)
        {
            var l = new Label
            {
                Text = text, AutoSize = true, Location = new Point(Sc(x), Sc(y)),
                ForeColor = color, BackColor = Theme.FormBg, Font = font
            };
            _main.Controls.Add(l);
            return l;
        }

        Label AddMainLabelBox(string text, int x, int y, int w, int h, Color color, Font font, ContentAlignment align)
        {
            var l = new Label
            {
                Text = text, AutoSize = false, Location = new Point(Sc(x), Sc(y)),
                Size = new Size(Sc(w), Sc(h)), ForeColor = color, BackColor = Theme.FormBg,
                Font = font, TextAlign = align
            };
            _main.Controls.Add(l);
            return l;
        }

        void AddMainDivider(int y)
        {
            var p = new Panel
            {
                BackColor = Theme.Divider,
                Location = new Point(Sc(20), Sc(y)),
                Size = new Size(Sc(MainW), 1)
            };
            _main.Controls.Add(p);
        }

        void UpdateRarityLabel()
        {
            int v = _seg.Value;
            _rarityValue.Text = Grades[v];
            _rarityValue.ForeColor = Theme.GradeColors[v];
        }

        // ---- window paint / drag ----

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var full = new Rectangle(0, 0, Width, Height);
            Theme.FillRound(g, full, Sc(12), Theme.FormBg);
            Theme.DrawRoundBorder(g, full, Sc(12), Theme.CardBorder, 1f);
        }

        protected override void OnMouseUp(MouseEventArgs e) { _dragging = false; base.OnMouseUp(e); }

        // ---- one-click BepInEx setup / cleanup ----

        void RunSetup()
        {
            ConfirmAndRunModOp(
                "Install mods",
                "This will install mods by:\n\n" +
                "  - backing up your save file\n" +
                "  - downloading BepInEx (the mod loader, ~35 MB)\n" +
                "  - installing it into the TaskBarHero folder\n\n" +
                "The presence feature is unaffected. Continue?",
                delegate { _setupBtn.Enabled = false; },
                BepInExSetup.Install);
        }

        void RunRemove()
        {
            ConfirmAndRunModOp(
                "Remove mods",
                "This will remove mods by deleting BepInEx from the TaskBarHero folder.\n\n" +
                "Your save and Discord presence are unaffected. Continue?",
                delegate { _saveBtn.Enabled = false; _removeBtn.Enabled = false; },
                BepInExSetup.Uninstall);
        }

        void ConfirmAndRunModOp(string title, string body, Action onBusy, Func<Action<string>, bool> work)
        {
            if (_modOpRunning) return;
            if (!BepInExSetup.GameFound)
            {
                MessageBox.Show(this, "Couldn't find the TaskBarHero folder.\n\nStart the game once so it can be located, then try again.",
                    title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (BepInExSetup.GameRunning())
            {
                MessageBox.Show(this, "Please close TaskBarHero first, then try again.",
                    title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show(this, body, title, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;

            _modOpRunning = true;
            onBusy();
            _cfgNote.Text = "working...";
            var t = new System.Threading.Thread(delegate()
            {
                bool success = work(delegate(string s) { PostNote(s); });
                PostModOpDone(success);
            });
            t.IsBackground = true;
            t.Start();
        }

        void PostNote(string s)
        {
            try { if (!IsDisposed) BeginInvoke((Action)delegate { _cfgNote.Text = s; }); }
            catch { }
        }

        void PostModOpDone(bool success)
        {
            try
            {
                if (IsDisposed) return;
                BeginInvoke((Action)delegate
                {
                    _modOpRunning = false;
                    _setupBtn.Enabled = true;
                    _removeBtn.Enabled = true;
                    string note = _cfgNote.Text;
                    LoadConfig();
                    if (!success && !string.IsNullOrEmpty(note) && note != "working...")
                        _cfgNote.Text = note;
                    RefreshModsRow(forceLayout: true);
                });
            }
            catch { }
        }

        // ---- live status ----

        void UpdateStatus()
        {
            string stage = _stageLabel != null ? _stageLabel() : null;
            bool connected = _discordConnected != null && _discordConnected();
            string diag = _diag != null ? _diag() : null;
            bool presenceOn = _presenceEnabled == null || _presenceEnabled();

            if (_presenceToggle != null && _presenceToggle.Checked != presenceOn)
                _presenceToggle.Checked = presenceOn;

            // Presence row: Discord connection state + the activity Discord shows.
            string presenceState;
            Color presenceDot;
            if (!presenceOn) { presenceState = "Off"; presenceDot = Theme.TextMuted; }
            else if (connected) { presenceState = "Live"; presenceDot = Theme.Green; }
            else { presenceState = "Offline"; presenceDot = Theme.TextMuted; }

            var m = stage != null
                ? Regex.Match(stage, @"(Act\s*\d+\s*-\s*Stage\s*\d+)\s*\(([^)]*)\)")
                : Match.Empty;
            if (m.Success)
            {
                _live.SetRow(0, "Presence",
                    m.Groups[1].Value.Replace("-", "–"),
                    m.Groups[2].Value.Replace(", ", " · "),
                    presenceState, presenceDot);
            }
            else
            {
                bool waiting = diag != null && diag.IndexOf("waiting", StringComparison.OrdinalIgnoreCase) >= 0;
                string value = !presenceOn ? "Disabled"
                    : waiting ? "Waiting for game"
                    : "—";
                _live.SetRow(0, "Presence", value, ShortStatus(diag), presenceState, presenceDot);
            }

            if (!Build.Synth) return;

            if (!_modOpRunning) RefreshModsRow();

            try
            {
                if (!File.Exists(StatusPath)) { SynthIdle("not started"); return; }
                var js = new JavaScriptSerializer();
                var d = js.Deserialize<Dictionary<string, object>>(File.ReadAllText(StatusPath));
                DateTime updated = DateTime.Parse((string)d["updatedUtc"], null, DateTimeStyles.RoundtripKind);
                if ((DateTime.UtcNow - updated).TotalSeconds > 15) { SynthIdle("game not running"); return; }

                bool auto = (bool)d["auto"];
                int cycles = Convert.ToInt32(d["cycles"]);
                int cycMin = Math.Max(1, Convert.ToInt32(d["cycleIntervalSeconds"]) / 60);

                Color synthDot = auto ? Theme.Green : Theme.TextMuted;
                string synthState = auto ? "On" : "Off";
                _live.SetRow(1, "Synth",
                    cycles + " cycles",
                    "every " + cycMin + " min",
                    synthState, synthDot);
            }
            catch { SynthIdle("status error"); }
        }

        void SynthIdle(string why)
        {
            _live.SetRow(1, "Synth", "—", why, "Off", Theme.TextMuted);
        }

        static string ShortStatus(string s)
        {
            if (s == null) return "";
            return s.Length > 22 ? s.Substring(0, 22) + "…" : s;
        }

        // ---- config file ----

        static string FindCfgPath()
        {
            string gameDir = AutoSynthDeploy.FindGameDir();
            return gameDir == null ? null : Path.Combine(gameDir, "BepInEx", "config", "com.pres.tbh.autosynth.cfg");
        }

        void SetSettingsEnabled(bool on)
        {
            _autoStart.Enabled = on; _seg.Enabled = on;
            _tEquip.Enabled = on; _tMaterials.Enabled = on; _tAccessories.Enabled = on;
            _desiredLevel.Enabled = on; _cycleMin.Enabled = on;
            _saveBtn.Enabled = on;
        }

        void LoadConfig()
        {
            if (!Build.Synth) return;
            _cfgPath = FindCfgPath();
            if (_cfgPath == null || !File.Exists(_cfgPath))
            {
                SetSettingsEnabled(false);
                if (_cfgNote.Text == "") _cfgNote.Text = "start the game once to create settings";
                return;
            }
            try
            {
                string text = File.ReadAllText(_cfgPath);
                _autoStart.Checked = !string.Equals(GetVal(text, "General", "AutoStart", "true"), "false", StringComparison.OrdinalIgnoreCase);
                int mg;
                if (!int.TryParse(GetVal(text, "Safety", "MaxGrade", "2"), out mg) || mg < 0 || mg > 9) mg = 2;
                _seg.Value = mg; UpdateRarityLabel();
                int dl;
                if (!int.TryParse(GetVal(text, "General", "DesiredLevel", "0"), out dl) || dl < 0) dl = 0;
                _desiredLevel.SelectedIndex = RecipeIndex(dl);
                decimal cycleSec = ParseF(GetVal(text, "Timing", "CycleIntervalSeconds", "300"));
                _cycleMin.SetValue(Math.Round(cycleSec / 60m));
                string types = GetVal(text, "General", "SynthesisTypes", "Equipment,Materials,Accessories").ToLowerInvariant();
                _tEquip.Selected = types.Contains("equipment") || types.Contains("gear");
                _tMaterials.Selected = types.Contains("material");
                _tAccessories.Selected = types.Contains("accessor");
                if (!_tEquip.Selected && !_tMaterials.Selected && !_tAccessories.Selected)
                { _tEquip.Selected = _tMaterials.Selected = _tAccessories.Selected = true; }

                _bepinexCfgPath = BepInExCfg.Path(AutoSynthDeploy.FindGameDir());
                if (_bepinexCfgPath != null && File.Exists(_bepinexCfgPath))
                {
                    _showConsole.Checked = BepInExCfg.GetConsoleEnabled(File.ReadAllText(_bepinexCfgPath));
                    _showConsole.Enabled = true;
                }
                else _showConsole.Enabled = false;

                SetSettingsEnabled(true);
                _cfgNote.Text = "";
            }
            catch (Exception ex)
            {
                SetSettingsEnabled(false);
                _cfgNote.Text = "config unreadable: " + ex.Message;
            }
        }

        void SaveConfig()
        {
            if (_cfgPath == null || !File.Exists(_cfgPath)) { _cfgNote.Text = "start the game once to create settings"; return; }
            try
            {
                string text = File.ReadAllText(_cfgPath);
                text = SetVal(text, "General", "AutoStart", _autoStart.Checked ? "true" : "false");
                // AutoOpenCube / AfterFill / AfterSynthesis are not exposed in the UI — leave cfg values alone.
                text = SetVal(text, "Safety", "MaxGrade", _seg.Value.ToString(CultureInfo.InvariantCulture));
                text = SetVal(text, "General", "DesiredLevel",
                    RecipeLevels[Math.Max(0, Math.Min(RecipeLevels.Length - 1, _desiredLevel.SelectedIndex))]
                        .ToString(CultureInfo.InvariantCulture));
                text = SetVal(text, "Timing", "CycleIntervalSeconds", (_cycleMin.Value * 60).ToString(CultureInfo.InvariantCulture));
                var types = new List<string>();
                if (_tEquip.Selected) types.Add("Equipment");
                if (_tMaterials.Selected) types.Add("Materials");
                if (_tAccessories.Selected) types.Add("Accessories");
                if (types.Count == 0) { types.Add("Equipment"); types.Add("Materials"); types.Add("Accessories"); }
                text = SetVal(text, "General", "SynthesisTypes", string.Join(",", types.ToArray()));
                File.WriteAllText(_cfgPath, text);

                bool consoleRestart = false;
                if (_bepinexCfgPath != null && File.Exists(_bepinexCfgPath))
                {
                    string bx = File.ReadAllText(_bepinexCfgPath);
                    if (BepInExCfg.GetConsoleEnabled(bx) != _showConsole.Checked)
                    {
                        File.WriteAllText(_bepinexCfgPath, BepInExCfg.SetConsoleEnabled(bx, _showConsole.Checked));
                        consoleRestart = true;
                    }
                }
                if (consoleRestart)
                    _cfgNote.Text = "saved — console change needs a game restart";
                else if (PluginSupportsLiveAutoStart())
                    _cfgNote.Text = "saved — applies in-game within ~10s";
                else
                    _cfgNote.Text = "saved — restart the game to apply (plugin update pending)";
            }
            catch (Exception ex) { _cfgNote.Text = "save failed: " + ex.Message; }
        }

        // Live AutoStart sync landed in plugin 0.24.1; older loaded plugins need a restart.
        static bool PluginSupportsLiveAutoStart()
        {
            try
            {
                if (!File.Exists(StatusPath)) return false;
                var js = new JavaScriptSerializer();
                var d = js.Deserialize<Dictionary<string, object>>(File.ReadAllText(StatusPath));
                DateTime updated = DateTime.Parse((string)d["updatedUtc"], null, DateTimeStyles.RoundtripKind);
                if ((DateTime.UtcNow - updated).TotalSeconds > 15) return false;
                object verObj;
                if (!d.TryGetValue("version", out verObj) || verObj == null) return false;
                Version v;
                return Version.TryParse(verObj.ToString(), out v) && v >= new Version(0, 24, 1);
            }
            catch { return false; }
        }

        static Regex KeyLine(string key)
        {
            return new Regex("(?m)^([ \t]*" + Regex.Escape(key) + "[ \t]*=[ \t]*)([^\r\n]*)");
        }

        static void SectionSpan(string text, string section, out int start, out int end)
        {
            start = -1; end = -1;
            var header = Regex.Match(text, @"(?m)^\s*\[" + Regex.Escape(section) + @"\]\s*$");
            if (!header.Success) return;
            start = header.Index + header.Length;
            var next = Regex.Match(text.Substring(start), @"(?m)^\s*\[[^\]\r\n]+\]\s*$");
            end = next.Success ? start + next.Index : text.Length;
        }

        static string GetVal(string text, string section, string key, string fallback)
        {
            int start, end;
            SectionSpan(text, section, out start, out end);
            if (start < 0) return fallback;
            var m = KeyLine(key).Match(text.Substring(start, end - start));
            return m.Success ? m.Groups[2].Value.TrimEnd(' ', '\t') : fallback;
        }

        static string SetVal(string text, string section, string key, string value)
        {
            int start, end;
            SectionSpan(text, section, out start, out end);
            if (start < 0)
            {
                return text.TrimEnd() + Environment.NewLine + Environment.NewLine
                     + "[" + section + "]" + Environment.NewLine
                     + key + " = " + value + Environment.NewLine;
            }
            string body = text.Substring(start, end - start);
            var re = KeyLine(key);
            string updated = re.IsMatch(body)
                ? re.Replace(body, "${1}" + value, 1)
                : body.TrimEnd() + Environment.NewLine + key + " = " + value + Environment.NewLine;
            return text.Substring(0, start) + updated + text.Substring(end);
        }
        static decimal ParseF(string s)
        {
            decimal v;
            return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        static int RecipeIndex(int desiredLevel)
        {
            for (int i = 0; i < RecipeLevels.Length; i++)
                if (RecipeLevels[i] == desiredLevel) return i;
            // Legacy free-form level: map to the bracket whose range contains it.
            if (desiredLevel > 0)
            {
                for (int i = RecipeLevels.Length - 1; i >= 1; i--)
                    if (RecipeLevels[i] <= desiredLevel) return i;
            }
            return 0;
        }
    }
}
