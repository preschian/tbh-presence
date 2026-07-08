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
    // Status & settings window (parchment theme, custom-painted). Live indicators
    // for presence + the in-game auto-synthesis plugin, plus editable settings
    // written to the BepInEx cfg files.
    public class StatusForm : Form
    {
        static readonly string[] Grades =
            { "Common", "Uncommon", "Rare", "Legendary", "Immortal", "Arcana", "Beyond", "Celestial", "Divine", "Cosmic" };

        static readonly string StatusPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "tbh-companion", "autosynth-status.json");

        const int W = 560, TitleH = 72;
        const int PresY = TitleH + 16 + 64 + 16;   // presence card top (below status strip)
        const int PresH = 80;                       // presence card height

        readonly Func<string> _stageLabel;
        readonly Func<bool> _discordConnected;
        readonly Func<string> _diag;
        readonly Func<bool> _presenceEnabled;
        readonly Action<bool> _setPresenceEnabled;
        readonly Timer _timer;
        string _cfgPath, _bepinexCfgPath;
        bool _setupRunning;

        Bitmap _icon;
        Rectangle _closeRect;
        Point _dragOffset; bool _dragging;
        float _s = 1f;
        int Sc(double v) { return (int)Math.Round(v * _s); }

        PillBadge _presencePill, _synthPill;
        StatusCard _cardStage, _cardCycles, _cardLast;
        Card _presenceCard;
        Toggle _presenceToggle;
        Toggle _autoStart, _showConsole;
        TypeTile _tEquip, _tMaterials, _tAccessories;
        SegmentBar _seg;
        Label _rarityValue;
        Stepper _cycleMin, _fillSec, _synthSec;
        FlatButton _saveBtn, _setupBtn;
        Label _cfgNote, _setupNote;
        Card _settingsCard;

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
            // presence card sits below the status strip in both editions; the full
            // edition stacks the auto-synthesis card + save row beneath it.
            int height = Build.Synth ? 604 + PresH + 16 : (PresY + PresH + 20);
            ClientSize = new Size(Sc(W), Sc(height));
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            try { using (var ico = Icon.ExtractAssociatedIcon(Application.ExecutablePath)) _icon = ico.ToBitmap(); } catch { }

            BuildTitleBar();
            BuildStatusStrip();
            BuildPresenceCard();
            if (Build.Synth)
            {
                BuildSettingsCard();
                BuildSaveRow();
            }

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
            using (var p = Theme.Round(new Rectangle(0, 0, Width, Height), Sc(14)))
                Region = new Region(p);
        }

        // ---- title bar ----

        void BuildTitleBar()
        {
            _presencePill = new PillBadge();
            _presencePill.SetBounds(0, 0, Sc(96), Sc(26));
            Controls.Add(_presencePill);
            if (Build.Synth)
            {
                _synthPill = new PillBadge();
                _synthPill.SetBounds(0, 0, Sc(92), Sc(26));
                _synthPill.Location = new Point(Sc(W - 20) - _synthPill.Width, Sc(36));
                _presencePill.Location = new Point(_synthPill.Left - Sc(6) - _presencePill.Width, Sc(36));
                Controls.Add(_synthPill);
                _synthPill.Set("Synth", Theme.TextMuted);
            }
            else
            {
                _presencePill.Location = new Point(Sc(W - 20) - _presencePill.Width, Sc(36));
            }
            _presencePill.Set("Presence", Theme.TextMuted);
        }

        // ---- status strip ----

        void BuildStatusStrip()
        {
            int y = Sc(TitleH + 16), h = Sc(64);
            _cardStage = new StatusCard { Title = "CURRENT STAGE", Radius = 10 };
            if (!Build.Synth)
            {
                _cardStage.SetBounds(Sc(20), y, Sc(520), h);
                Controls.Add(_cardStage);
                return;
            }
            int[] xs = { 20, 197, 373 };
            int[] ws = { 167, 166, 167 };
            _cardCycles = new StatusCard { Title = "CYCLES", Radius = 10 };
            _cardLast = new StatusCard { Title = "LAST SYNTHESIS", Radius = 10 };
            var cards = new[] { _cardStage, _cardCycles, _cardLast };
            for (int i = 0; i < 3; i++)
            {
                cards[i].SetBounds(Sc(xs[i]), y, Sc(ws[i]), h);
                Controls.Add(cards[i]);
            }
        }

        // ---- presence card (both editions) ----

        void BuildPresenceCard()
        {
            _presenceCard = new Card { Radius = 12 };
            _presenceCard.SetBounds(Sc(20), Sc(PresY), Sc(520), Sc(PresH));
            Controls.Add(_presenceCard);
            var c = _presenceCard;

            AddLabel(c, "DISCORD PRESENCE", 16, 14, Theme.Brown, Theme.FSerif(10.5f, FontStyle.Bold));
            AddLabel(c, "Show your current stage in Discord", 16, 40, Theme.TextDark, Theme.F(10f, FontStyle.Regular));
            AddLabel(c, "off clears your activity in Discord", 16, 60, Theme.TextMuted, Theme.F(8.5f, FontStyle.Regular));

            _presenceToggle = new Toggle();
            _presenceToggle.SetBounds(Sc(520 - 16 - 44), Sc(44), Sc(44), Sc(24));
            _presenceToggle.Checked = _presenceEnabled == null || _presenceEnabled();
            _presenceToggle.CheckedChanged += delegate
            {
                if (_setPresenceEnabled != null) _setPresenceEnabled(_presenceToggle.Checked);
            };
            c.Controls.Add(_presenceToggle);
        }

        // ---- settings card ----

        void BuildSettingsCard()
        {
            int cardY = PresY + PresH + 16; // below the presence card (logical)
            _settingsCard = new Card { Radius = 12 };
            _settingsCard.SetBounds(Sc(20), Sc(cardY), Sc(520), Sc(372));
            Controls.Add(_settingsCard);
            var c = _settingsCard;

            AddLabel(c, "AUTO-SYNTHESIS", 16, 14, Theme.Brown, Theme.FSerif(10.5f, FontStyle.Bold));

            // toggle rows
            AddLabel(c, "Start automatically when the game launches", 16, 40, Theme.TextDark, Theme.F(10f, FontStyle.Regular));
            AddLabel(c, "no F8 needed", 16, 60, Theme.TextMuted, Theme.F(8.5f, FontStyle.Regular));
            _autoStart = new Toggle(); _autoStart.SetBounds(Sc(504 - 44), Sc(44), Sc(44), Sc(24));
            c.Controls.Add(_autoStart);

            AddLabel(c, "Show the BepInEx log console", 16, 84, Theme.TextDark, Theme.F(10f, FontStyle.Regular));
            AddLabel(c, "applies on next game start", 16, 104, Theme.TextMuted, Theme.F(8.5f, FontStyle.Regular));
            _showConsole = new Toggle(); _showConsole.SetBounds(Sc(504 - 44), Sc(88), Sc(44), Sc(24));
            c.Controls.Add(_showConsole);

            AddDivider(c, 128);

            // types
            AddLabel(c, "Synthesize which types", 16, 140, Theme.TextDark, Theme.F(10f, FontStyle.Regular));
            AddLabel(c, "rotates each round", 176, 142, Theme.TextMuted, Theme.F(8.5f, FontStyle.Regular));
            _tEquip = new TypeTile { Icon = "⚔", Caption = "Equipment" };
            _tMaterials = new TypeTile { Icon = "◆", Caption = "Materials" };
            _tAccessories = new TypeTile { Icon = "◎", Caption = "Accessories" };
            var tiles = new[] { _tEquip, _tMaterials, _tAccessories };
            int[] tx = { 16, 181, 346 };
            int[] tw = { 157, 157, 158 };
            for (int i = 0; i < 3; i++) { tiles[i].SetBounds(Sc(tx[i]), Sc(164), Sc(tw[i]), Sc(52)); c.Controls.Add(tiles[i]); }

            // rarity
            AddLabel(c, "Max rarity to synthesize", 16, 228, Theme.TextDark, Theme.F(10f, FontStyle.Regular));
            _rarityValue = AddLabelBox(c, "★ Legendary", 300, 227, 188, 20, Theme.Amber, Theme.F(9.5f, FontStyle.Bold), ContentAlignment.MiddleRight);
            _seg = new SegmentBar { Value = 3 };
            _seg.SetBounds(Sc(16), Sc(250), Sc(488), Sc(12));
            _seg.ValueChanged += delegate { UpdateRarityLabel(); };
            c.Controls.Add(_seg);
            AddLabel(c, "Common", 16, 266, Theme.TextMuted, Theme.F(8f, FontStyle.Regular));
            AddLabelBox(c, "Cosmic", 404, 266, 100, 16, Theme.TextMuted, Theme.F(8f, FontStyle.Regular), ContentAlignment.MiddleRight);

            AddDivider(c, 288);

            // timings
            int[] colx = { 16, 182, 348 };
            int[] colw = { 156, 156, 156 };
            AddLabel(c, "Cycle interval (min)", colx[0], 300, Theme.TextMuted, Theme.F(8.5f, FontStyle.Regular));
            AddLabel(c, "After auto-fill (s)", colx[1], 300, Theme.TextMuted, Theme.F(8.5f, FontStyle.Regular));
            AddLabel(c, "After synthesis (s)", colx[2], 300, Theme.TextMuted, Theme.F(8.5f, FontStyle.Regular));
            _cycleMin = new Stepper { Min = 1, Max = 1440, Step = 1, Decimals = 0, Value = 5 };
            _fillSec = new Stepper { Min = 0.5m, Max = 60, Step = 0.5m, Decimals = 1, Value = 1 };
            _synthSec = new Stepper { Min = 0.5m, Max = 60, Step = 0.5m, Decimals = 1, Value = 4 };
            var steps = new[] { _cycleMin, _fillSec, _synthSec };
            for (int i = 0; i < 3; i++) { steps[i].SetBounds(Sc(colx[i]), Sc(318), Sc(colw[i]), Sc(30)); c.Controls.Add(steps[i]); }
        }

        void BuildSaveRow()
        {
            int y = _settingsCard.Bottom + Sc(16);
            _saveBtn = new FlatButton { Text = "Save settings", Fill = Theme.Terracotta };
            _saveBtn.SetBounds(Sc(20), y, Sc(150), Sc(38));
            _saveBtn.Click += delegate { SaveConfig(); };
            Controls.Add(_saveBtn);

            _setupBtn = new FlatButton { Text = "Set up auto-synthesis", Fill = Theme.Brown };
            _setupBtn.SetBounds(Sc(20), y, Sc(200), Sc(38));
            _setupBtn.Click += delegate { RunSetup(); };
            _setupBtn.Visible = false;
            Controls.Add(_setupBtn);

            _cfgNote = new Label
            {
                AutoSize = false,
                Location = new Point(Sc(182), y),
                Size = new Size(Sc(358), Sc(38)),
                ForeColor = Theme.TextMuted,
                BackColor = Theme.FormBg,
                Font = Theme.F(9f, FontStyle.Regular),
                TextAlign = ContentAlignment.MiddleLeft
            };
            Controls.Add(_cfgNote);
            _setupNote = _cfgNote; // shared note line
        }

        // ---- helpers ----

        Label AddLabel(Card card, string text, int x, int y, Color color, Font font)
        {
            var l = new Label
            {
                Text = text, AutoSize = true, Location = new Point(Sc(x), Sc(y)),
                ForeColor = color, BackColor = card.BackColor, Font = font
            };
            card.Controls.Add(l);
            return l;
        }

        Label AddLabelBox(Card card, string text, int x, int y, int w, int h, Color color, Font font, ContentAlignment align)
        {
            var l = new Label
            {
                Text = text, AutoSize = false, Location = new Point(Sc(x), Sc(y)),
                Size = new Size(Sc(w), Sc(h)), ForeColor = color, BackColor = card.BackColor,
                Font = font, TextAlign = align
            };
            card.Controls.Add(l);
            return l;
        }

        void AddDivider(Card card, int y)
        {
            var p = new Panel { BackColor = Theme.Divider, Location = new Point(Sc(16), Sc(y)), Size = new Size(Sc(488), 1) };
            card.Controls.Add(p);
        }

        void UpdateRarityLabel()
        {
            int v = _seg.Value;
            _rarityValue.Text = "★ " + Grades[v];
            _rarityValue.ForeColor = Theme.GradeColors[v];
        }

        // ---- window paint / drag ----

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            int th = Sc(TitleH);
            var full = new Rectangle(0, 0, Width, Height);
            Theme.FillRound(g, full, Sc(14), Theme.FormBg);

            // title bar gradient
            var tb = new Rectangle(0, 0, Width, th);
            using (var clip = new Region(new Rectangle(0, 0, Width, th)))
            {
                g.SetClip(clip, CombineMode.Replace);
                using (var br = new LinearGradientBrush(tb, Theme.TitleTop, Theme.TitleBottom, 90f))
                    g.FillRectangle(br, tb);
                g.ResetClip();
            }
            using (var pen = new Pen(Theme.CardBorder)) g.DrawLine(pen, 0, th, Width, th);

            // icon
            if (_icon != null)
            {
                var ir = new Rectangle(Sc(20), Sc(18), Sc(36), Sc(36));
                using (var pth = Theme.Round(ir, Sc(8))) { g.SetClip(pth); g.DrawImage(_icon, ir); g.ResetClip(); }
                Theme.DrawRoundBorder(g, ir, Sc(8), Theme.CardBorder, 1f);
            }

            // title + subtitle
            using (var f = Theme.FSerif(15f, FontStyle.Bold)) using (var b = new SolidBrush(Theme.TextDark))
                g.DrawString("TBH Companion", f, b, new PointF(Sc(64), Sc(15)));
            using (var f = Theme.F(8.5f, FontStyle.Regular)) using (var b = new SolidBrush(Theme.TextMuted))
                g.DrawString(Build.Synth ? "STATUS  ·  SETTINGS" : "STATUS", f, b, new PointF(Sc(66), Sc(43)));

            // outer border + close
            Theme.DrawRoundBorder(g, full, Sc(14), Theme.CardBorder, 1f);
            _closeRect = new Rectangle(Width - Sc(28), Sc(10), Sc(18), Sc(18));
            using (var f = Theme.F(11f, FontStyle.Bold)) using (var b = new SolidBrush(Theme.TextMuted))
            {
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString("✕", f, b, _closeRect, sf);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (_closeRect.Contains(e.Location)) { Close(); return; }
            if (e.Y <= Sc(TitleH)) { _dragging = true; _dragOffset = e.Location; }
            base.OnMouseDown(e);
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_dragging) Location = new Point(Location.X + e.X - _dragOffset.X, Location.Y + e.Y - _dragOffset.Y);
            base.OnMouseMove(e);
        }
        protected override void OnMouseUp(MouseEventArgs e) { _dragging = false; base.OnMouseUp(e); }

        // ---- one-click BepInEx setup ----

        void RunSetup()
        {
            if (_setupRunning) return;
            if (!BepInExSetup.GameFound)
            {
                MessageBox.Show(this, "Couldn't find the TaskBarHero folder.\n\nStart the game once so it can be located, then try again.",
                    "Set up auto-synthesis", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (BepInExSetup.GameRunning())
            {
                MessageBox.Show(this, "Please close TaskBarHero first, then run setup again.",
                    "Set up auto-synthesis", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var ok = MessageBox.Show(this,
                "This will set up auto-synthesis by:\n\n" +
                "  - backing up your save file\n" +
                "  - downloading BepInEx (the mod loader, ~35 MB)\n" +
                "  - installing it into the TaskBarHero folder\n\n" +
                "The presence feature is unaffected. Continue?",
                "Set up auto-synthesis", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (ok != DialogResult.OK) return;

            _setupRunning = true;
            _setupBtn.Enabled = false;
            _setupNote.Text = "working...";
            var t = new System.Threading.Thread(delegate()
            {
                bool success = BepInExSetup.Install(delegate(string s) { PostNote(s); });
                PostSetupDone(success);
            });
            t.IsBackground = true;
            t.Start();
        }

        void PostNote(string s)
        {
            try { if (!IsDisposed) BeginInvoke((Action)delegate { _setupNote.Text = s; }); }
            catch { }
        }

        void PostSetupDone(bool success)
        {
            try
            {
                if (IsDisposed) return;
                BeginInvoke((Action)delegate
                {
                    _setupRunning = false;
                    _setupBtn.Enabled = true;
                    if (success) LoadConfig();
                });
            }
            catch { }
        }

        // ---- live status ----

        void UpdateStatus()
        {
            // Three independent channels: the current stage, the Discord connection
            // indicator, and a diagnostic line — so a transient message never hides
            // the stage.
            string stage = _stageLabel != null ? _stageLabel() : null;
            bool connected = _discordConnected != null && _discordConnected();
            string diag = _diag != null ? _diag() : null;
            bool presenceOn = _presenceEnabled == null || _presenceEnabled();

            // reflect an out-of-band change (e.g. toggled from the tray menu)
            if (_presenceToggle != null && _presenceToggle.Checked != presenceOn)
                _presenceToggle.Checked = presenceOn;

            if (!presenceOn)
                _presencePill.Set("Off", Theme.TextMuted);
            else
                _presencePill.Set(connected ? "Presence" : "Offline", connected ? Theme.Green : Theme.TextMuted);

            var m = stage != null
                ? Regex.Match(stage, @"(Act\s*\d+\s*-\s*Stage\s*\d+)\s*\(([^)]*)\)")
                : Match.Empty;
            if (m.Success)
            {
                _cardStage.Value = m.Groups[1].Value.Replace("-", "–");
                _cardStage.ValueColor = Theme.TextDark;
                _cardStage.Sub = m.Groups[2].Value.Replace(", ", " · ");
                _cardStage.SubColor = Theme.Terracotta;
            }
            else
            {
                bool waiting = diag != null && diag.IndexOf("waiting", StringComparison.OrdinalIgnoreCase) >= 0;
                _cardStage.Value = waiting ? "Waiting for game" : "—";
                _cardStage.ValueColor = Theme.TextDark;
                _cardStage.Sub = ShortStatus(diag);
                _cardStage.SubColor = Theme.TextMuted;
            }

            if (!Build.Synth) return;   // presence-only edition: no auto-synthesis UI

            bool installed = BepInExSetup.IsInstalled();
            if (!_setupRunning)
            {
                _setupBtn.Visible = !installed;
                _saveBtn.Visible = installed;
            }

            try
            {
                if (!File.Exists(StatusPath)) { SynthIdle("not started"); return; }
                var js = new JavaScriptSerializer();
                var d = js.Deserialize<Dictionary<string, object>>(File.ReadAllText(StatusPath));
                DateTime updated = DateTime.Parse((string)d["updatedUtc"], null, DateTimeStyles.RoundtripKind);
                if ((DateTime.UtcNow - updated).TotalSeconds > 15) { SynthIdle("game not running"); return; }

                bool auto = (bool)d["auto"];
                int cycles = Convert.ToInt32(d["cycles"]);
                int lastCount = Convert.ToInt32(d["lastCount"]);
                int lastGrade = Convert.ToInt32(d["lastGrade"]);
                int cycMin = Math.Max(1, Convert.ToInt32(d["cycleIntervalSeconds"]) / 60);

                _synthPill.Set(auto ? "Synth ON" : "Synth OFF", auto ? Theme.Green : Color.IndianRed);
                _cardCycles.Value = cycles + " this session"; _cardCycles.ValueColor = Theme.TextDark;
                _cardCycles.Sub = "every " + cycMin + " min"; _cardCycles.SubColor = Theme.TextMuted;
                if (lastCount > 0)
                {
                    _cardLast.Value = lastCount + " items"; _cardLast.ValueColor = Theme.TextDark;
                    _cardLast.Sub = GradeName(lastGrade);
                    _cardLast.SubColor = lastGrade >= 0 && lastGrade < 10 ? Theme.GradeColors[lastGrade] : Theme.TextMuted;
                }
                else { _cardLast.Value = "—"; _cardLast.Sub = "none yet"; _cardLast.SubColor = Theme.TextMuted; _cardLast.ValueColor = Theme.TextDark; }
            }
            catch { SynthIdle("status error"); }
        }

        void SynthIdle(string why)
        {
            _synthPill.Set("Synth", Theme.TextMuted);
            _cardCycles.Value = "—"; _cardCycles.Sub = why; _cardCycles.SubColor = Theme.TextMuted; _cardCycles.ValueColor = Theme.TextDark;
            _cardLast.Value = "—"; _cardLast.Sub = why; _cardLast.SubColor = Theme.TextMuted; _cardLast.ValueColor = Theme.TextDark;
        }

        static string ShortStatus(string s)
        {
            if (s == null) return "";
            return s.Length > 30 ? s.Substring(0, 30) + "…" : s;
        }

        static string GradeName(int g) { return g >= 0 && g < Grades.Length ? Grades[g] : "?"; }

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
            _cycleMin.Enabled = on; _fillSec.Enabled = on; _synthSec.Enabled = on;
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
                _autoStart.Checked = !string.Equals(GetVal(text, "AutoStart", "true"), "false", StringComparison.OrdinalIgnoreCase);
                int mg;
                if (!int.TryParse(GetVal(text, "MaxGrade", "2"), out mg) || mg < 0 || mg > 9) mg = 2;
                _seg.Value = mg; UpdateRarityLabel();
                decimal cycleSec = ParseF(GetVal(text, "CycleIntervalSeconds", "300"));
                _cycleMin.SetValue(Math.Round(cycleSec / 60m));
                _fillSec.SetValue(ParseF(GetVal(text, "AfterFillSeconds", "1")));
                _synthSec.SetValue(ParseF(GetVal(text, "AfterSynthesisSeconds", "4")));
                string types = GetVal(text, "SynthesisTypes", "Equipment,Materials,Accessories").ToLowerInvariant();
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
                text = SetVal(text, "AutoStart", _autoStart.Checked ? "true" : "false");
                text = SetVal(text, "MaxGrade", _seg.Value.ToString(CultureInfo.InvariantCulture));
                text = SetVal(text, "CycleIntervalSeconds", (_cycleMin.Value * 60).ToString(CultureInfo.InvariantCulture));
                text = SetVal(text, "AfterFillSeconds", _fillSec.Value.ToString(CultureInfo.InvariantCulture));
                text = SetVal(text, "AfterSynthesisSeconds", _synthSec.Value.ToString(CultureInfo.InvariantCulture));
                var types = new List<string>();
                if (_tEquip.Selected) types.Add("Equipment");
                if (_tMaterials.Selected) types.Add("Materials");
                if (_tAccessories.Selected) types.Add("Accessories");
                if (types.Count == 0) { types.Add("Equipment"); types.Add("Materials"); types.Add("Accessories"); }
                text = SetVal(text, "SynthesisTypes", string.Join(",", types.ToArray()));
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
                _cfgNote.Text = consoleRestart ? "saved — console change needs a game restart"
                                               : "saved — applies in-game within ~10s";
            }
            catch (Exception ex) { _cfgNote.Text = "save failed: " + ex.Message; }
        }

        static string GetVal(string text, string key, string fallback)
        {
            var m = Regex.Match(text, @"(?m)^\s*" + Regex.Escape(key) + @"\s*=\s*(.+?)\s*$");
            return m.Success ? m.Groups[1].Value : fallback;
        }
        static string SetVal(string text, string key, string value)
        {
            var re = new Regex(@"(?m)^(\s*" + Regex.Escape(key) + @"\s*=\s*).+?\s*$");
            if (re.IsMatch(text)) return re.Replace(text, "${1}" + value, 1);
            return text + Environment.NewLine + key + " = " + value + Environment.NewLine;
        }
        static decimal ParseF(string s)
        {
            decimal v;
            return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out v) ? v : 0;
        }
    }

    // Small live status card (title / value / sub), painted like the design.
    class StatusCard : Card
    {
        string _title = "", _value = "", _sub = "";
        public Color ValueColor = Theme.TextDark, SubColor = Theme.TextMuted;
        public StatusCard() { Radius = 10; }
        public string Title { get { return _title; } set { _title = value; Invalidate(); } }
        public string Value { get { return _value; } set { _value = value; Invalidate(); } }
        public string Sub { get { return _sub; } set { _sub = value; Invalidate(); } }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float s = Theme.Scale(g);
            using (var f = Theme.F(8f, FontStyle.Regular)) using (var b = new SolidBrush(Theme.TextMuted))
                g.DrawString(_title, f, b, new PointF(11 * s, 8 * s));
            using (var f = Theme.F(11f, FontStyle.Bold)) using (var b = new SolidBrush(ValueColor))
                g.DrawString(_value, f, b, new PointF(11 * s, 24 * s));
            using (var f = Theme.F(9f, FontStyle.Bold)) using (var b = new SolidBrush(SubColor))
                g.DrawString(_sub, f, b, new PointF(11 * s, 44 * s));
        }
    }
}
