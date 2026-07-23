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

        // In-game synthesis sub-recipe brackets (Cube dropdown labels), plus Max.
        // DesiredLevel stores the bracket's lower bound (0 = highest unlocked).
        struct RecipeTier { public string Label; public int Lo; }
        static readonly RecipeTier[] Recipes =
        {
            new RecipeTier { Label = "Max", Lo = 0 },
            new RecipeTier { Label = "Lv.1~10", Lo = 1 },
            new RecipeTier { Label = "Lv.10~20", Lo = 10 },
            new RecipeTier { Label = "Lv.15~30", Lo = 15 },
            new RecipeTier { Label = "Lv.20~40", Lo = 20 },
            new RecipeTier { Label = "Lv.30~50", Lo = 30 },
            new RecipeTier { Label = "Lv.40~65", Lo = 40 },
            new RecipeTier { Label = "Lv.50~65", Lo = 50 },
            new RecipeTier { Label = "Lv.65~80", Lo = 65 }
        };

        static readonly string StatusPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "tbh-companion", "autosynth-status.json");

        // 16:9 window. Synth settings use two columns in the right pane
        // (general / mods | runes / synthesis) so everything fits without stretch.
        const int W = 896, SideW = 188, H = 504;
        const int PadX = 20;
        const int ColW = 318;
        const int ColGap = 20;
        const int Col0X = PadX;
        const int Col1X = PadX + ColW + ColGap;
        const int TopChrome = 40; // fixed close/drag strip above the scroll area
        const int RowH = 32;
        const int ControlH = 28;
        const int ToggleH = 24;
        const int SectionGap = 14;
        const int HeaderAfter = 22;

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
        // Outer ring left for form-owned chrome (Inset pen needs ceil(_s) px).
        int BorderInset() { return Math.Max(1, (int)Math.Ceiling(_s)); }
        float BorderWidth() { return Math.Max(1f, _s); }

        LiveStrip _live;
        Panel _side, _main;
        VertScrollPanel _scroll;
        WheelRedirectFilter _wheelFilter;
        Toggle _presenceToggle;
        Toggle _autoRestart;
        Toggle _autoLoop, _enableSynth, _autoChest, _autoRune, _showConsole;
        TypeTile _tEquip, _tMaterials, _tAccessories;
        SegmentBar _seg;
        Label _rarityValue;
        Stepper _cycleMin, _restartDays;
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
            ClientSize = new Size(Sc(W), Sc(H));
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
            FormClosed += delegate
            {
                _timer.Stop(); _timer.Dispose();
                if (_wheelFilter != null) { Application.RemoveMessageFilter(_wheelFilter); _wheelFilter = null; }
                if (_icon != null) _icon.Dispose();
            };

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
            int b = BorderInset();
            _side = new Panel
            {
                BackColor = Theme.SideBg,
                Location = new Point(b, b),
                Size = new Size(Sc(SideW) - b, Height - 2 * b)
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
            _live.SetBounds(Sc(12), _side.Height - Sc(14 + statusH), Sc(SideW - 24), Sc(statusH));
            _live.SetRow(0, "Presence", "—", "", "Off", Theme.TextMuted);
            if (Build.Synth)
                _live.SetRow(1, "Loop", "—", "", "Off", Theme.TextMuted);
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
            int b = BorderInset();
            _main = new Panel
            {
                BackColor = Theme.FormBg,
                Location = new Point(Sc(SideW), b),
                Size = new Size(Width - Sc(SideW) - b, Height - 2 * b)
            };
            _main.Paint += PaintMain;
            _main.MouseDown += MainMouseDown;
            _main.MouseMove += MainMouseMove;
            _main.MouseUp += delegate { _dragging = false; };
            Controls.Add(_main);

            // AutoScroll host for settings (two columns in the synth edition).
            _scroll = new VertScrollPanel
            {
                BackColor = Theme.FormBg,
                Location = new Point(0, Sc(TopChrome)),
                Size = new Size(_main.Width, _main.Height - Sc(TopChrome)),
                AutoScroll = true
            };
            _scroll.MouseDown += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) BeginDrag(e.Location); };
            _scroll.MouseMove += MainMouseMove;
            _scroll.MouseUp += delegate { _dragging = false; };
            _main.Controls.Add(_scroll);

            if (Build.Synth) BuildSettings();
            else BuildPresenceOnly();

            FinishContent();
            Shown += delegate { FinishContent(); };

            // WinForms sends wheel to the *focused* control; redirect when the
            // cursor is over the settings pane so scrolling always works.
            _wheelFilter = new WheelRedirectFilter(_scroll);
            Application.AddMessageFilter(_wheelFilter);
        }

        void PaintMain(object sender, PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
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
            if (e.Y <= Sc(TopChrome)) BeginDrag(e.Location);
        }

        void AddContent(Control c) { _scroll.Controls.Add(c); }

        void FinishContent()
        {
            int bottom = 0;
            foreach (Control c in _scroll.Controls)
            {
                int btm = c.Bottom;
                if (btm > bottom) bottom = btm;
            }
            _scroll.SetScrollContentSize(Sc(Col1X + ColW + PadX), bottom + Sc(16));
        }

        // Keeps the last row clear of the bottom edge.
        void EndContent(int y)
        {
            var pad = new Panel
            {
                BackColor = Theme.FormBg,
                Location = new Point(0, Sc(y + 16)),
                Size = new Size(1, Sc(8))
            };
            AddContent(pad);
        }

        void MainMouseMove(object sender, MouseEventArgs e)
        {
            if (_dragging) Location = new Point(Location.X + e.X - _dragOffset.X, Location.Y + e.Y - _dragOffset.Y);
        }

        void BuildPresenceOnly()
        {
            const int toggleW = 44;
            const int fieldW = 120;
            int toggleX = Col0X + ColW - toggleW;
            int fieldX = Col0X + ColW - fieldW;
            int y = 18;

            y = AddSectionHeader("Discord Presence", Col0X, y);
            y = AddToggleRow("Show stage on Discord", Col0X, ref _presenceToggle, toggleX, y);
            WirePresenceToggle();
            y = AddSectionDivider(Col0X, ColW, y);

            y = AddRestartSection(Col0X, ColW, y, toggleX, fieldX, fieldW);
            EndContent(y);
        }

        void BuildSettings()
        {
            const int toggleW = 44;
            const int fieldW = 120;
            int y0 = 18, y1 = 18;

            // Soft rule between the two columns.
            var split = new Panel
            {
                BackColor = Theme.Divider,
                Location = new Point(Sc(Col1X - ColGap / 2), Sc(18)),
                Size = new Size(Math.Max(1, Sc(1)), Sc(400))
            };
            AddContent(split);

            // ---- left: Discord / Restart / Mods ----
            int t0 = Col0X + ColW - toggleW;
            int f0 = Col0X + ColW - fieldW;
            y0 = AddSectionHeader("Discord Presence", Col0X, y0);
            y0 = AddToggleRow("Show stage on Discord", Col0X, ref _presenceToggle, t0, y0);
            WirePresenceToggle();
            y0 = AddSectionDivider(Col0X, ColW, y0);

            y0 = AddRestartSection(Col0X, ColW, y0, t0, f0, fieldW);

            y0 = AddSectionHeader("Enable Mods", Col0X, y0);
            y0 = AddToggleRow("Auto Loop", Col0X, ref _autoLoop, t0, y0);
            y0 = AddToggleRow("Show BepInEx console", Col0X, ref _showConsole, t0, y0);
            y0 = AddFieldRow("Cycle interval", "min", Col0X, y0, f0, fieldW, out _cycleMin);
            _cycleMin.Min = 1; _cycleMin.Max = 1440; _cycleMin.Step = 1; _cycleMin.Decimals = 0; _cycleMin.Value = 5;

            // ---- right: Chests / Runes / Synthesis ----
            int t1 = Col1X + ColW - toggleW;
            int f1 = Col1X + ColW - fieldW;
            y1 = AddSectionHeader("Chests", Col1X, y1);
            y1 = AddToggleRow("Enabled", Col1X, ref _autoChest, t1, y1);
            y1 = AddSectionDivider(Col1X, ColW, y1);

            y1 = AddSectionHeader("Runes", Col1X, y1);
            y1 = AddToggleRow("Enabled", Col1X, ref _autoRune, t1, y1);
            y1 = AddSectionDivider(Col1X, ColW, y1);

            y1 = AddSectionHeader("Synthesis", Col1X, y1);
            y1 = AddToggleRow("Enabled", Col1X, ref _enableSynth, t1, y1);

            AddMainLabel("Types", Col1X, y1, Theme.TextDark, Theme.F(9.5f, FontStyle.Regular));
            y1 += 18;
            _tEquip = new TypeTile { Caption = "Equipment" };
            _tMaterials = new TypeTile { Caption = "Materials" };
            _tAccessories = new TypeTile { Caption = "Accessories" };
            var tiles = new[] { _tEquip, _tMaterials, _tAccessories };
            int gap = 6, tw = (ColW - gap * 2) / 3;
            for (int i = 0; i < 3; i++)
            {
                tiles[i].SetBounds(Sc(Col1X + i * (tw + gap)), Sc(y1), Sc(tw), Sc(ControlH));
                AddContent(tiles[i]);
            }
            y1 += ControlH + 12;

            AddRowLabel("Max rarity", Col1X, y1);
            _rarityValue = AddMainLabelBox("Legendary", f1, y1, fieldW, ControlH, Theme.Amber, Theme.F(9f, FontStyle.Bold), ContentAlignment.MiddleRight);
            y1 += RowH;
            _seg = new SegmentBar { Value = 2 };
            _seg.SetBounds(Sc(Col1X), Sc(y1), Sc(ColW), Sc(8));
            _seg.ValueChanged += delegate { UpdateRarityLabel(); };
            AddContent(_seg);
            y1 += 16;

            var recipeLabels = new string[Recipes.Length];
            for (int i = 0; i < Recipes.Length; i++) recipeLabels[i] = Recipes[i].Label;
            y1 = AddDropdownRow("Target level", recipeLabels, Col1X, y1, f1, fieldW, out _desiredLevel);

            // Action row under both columns.
            int y = Math.Max(y0, y1) + 18;
            split.Height = Sc(Math.Max(40, y - 30));

            _saveBtn = new FlatButton { Text = "Save", Fill = Theme.Accent };
            _saveBtn.SetBounds(Sc(Col0X), Sc(y), Sc(88), Sc(30));
            _saveBtn.Click += delegate { SaveConfig(); };
            AddContent(_saveBtn);

            _removeBtn = new FlatButton { Text = "Remove mods", Fill = Theme.Secondary };
            _removeBtn.SetBounds(Sc(Col0X + 96), Sc(y), Sc(120), Sc(30));
            _removeBtn.Click += delegate { RunRemove(); };
            _removeBtn.Visible = false;
            AddContent(_removeBtn);

            _setupBtn = new FlatButton { Text = "Install mods", Fill = Theme.Secondary };
            _setupBtn.SetBounds(Sc(Col0X), Sc(y), Sc(120), Sc(30));
            _setupBtn.Click += delegate { RunSetup(); };
            _setupBtn.Visible = false;
            AddContent(_setupBtn);

            _cfgNote = new Label
            {
                AutoSize = false,
                Location = new Point(Sc(Col0X + 228), Sc(y)),
                Size = new Size(Sc(Col1X + ColW - (Col0X + 228)), Sc(30)),
                ForeColor = Theme.TextMuted,
                BackColor = Theme.FormBg,
                Font = Theme.F(8.5f, FontStyle.Regular),
                TextAlign = ContentAlignment.MiddleLeft
            };
            AddContent(_cfgNote);

            EndContent(y + 30);
            RefreshModsRow(forceLayout: true);
        }

        int AddSectionHeader(string title, int colX, int y)
        {
            AddMainLabel(title, colX, y, Theme.TextDark, Theme.F(10f, FontStyle.Bold));
            return y + HeaderAfter;
        }

        int AddRestartSection(int colX, int colW, int y, int toggleX, int fieldX, int fieldW)
        {
            y = AddSectionHeader("Scheduled Restart", colX, y);
            y = AddToggleRow("Restart after uptime", colX, ref _autoRestart, toggleX, y);
            y = AddFieldRow("Uptime limit", "days", colX, y, fieldX, fieldW, out _restartDays);
            _restartDays.Min = 1; _restartDays.Max = 30; _restartDays.Step = 1; _restartDays.Decimals = 0;
            _restartDays.SetValue(AppSettings.AutoRestartDays);
            _autoRestart.Checked = AppSettings.AutoRestartEnabled;
            _restartDays.Enabled = _autoRestart.Checked;
            _autoRestart.CheckedChanged += delegate
            {
                // Setter arms/clears the restart clock (no instant kill on enable).
                AppSettings.AutoRestartEnabled = _autoRestart.Checked;
                _restartDays.Enabled = _autoRestart.Checked;
            };
            _restartDays.ValueChanged += delegate
            {
                // Tightening days re-arms via the setter.
                AppSettings.AutoRestartDays = (int)_restartDays.Value;
            };
            return AddSectionDivider(colX, colW, y);
        }

        void WirePresenceToggle()
        {
            _presenceToggle.Checked = _presenceEnabled == null || _presenceEnabled();
            _presenceToggle.CheckedChanged += delegate
            {
                if (_setPresenceEnabled != null) _setPresenceEnabled(_presenceToggle.Checked);
            };
        }

        int AddSectionDivider(int colX, int colW, int y)
        {
            y += 6;
            AddMainDivider(colX, colW, y);
            return y + SectionGap;
        }

        void AddRowLabel(string label, int colX, int y)
        {
            AddMainLabel(label, colX, y + (ControlH - 14) / 2, Theme.TextDark, Theme.F(9.5f, FontStyle.Regular));
        }

        int AddToggleRow(string label, int colX, ref Toggle toggle, int toggleX, int y)
        {
            AddRowLabel(label, colX, y);
            toggle = new Toggle();
            int ty = y + (ControlH - ToggleH) / 2;
            toggle.SetBounds(Sc(toggleX), Sc(ty), Sc(44), Sc(ToggleH));
            AddContent(toggle);
            return y + RowH;
        }

        int AddFieldRow(string label, string suffix, int colX, int y, int fieldX, int fieldW, out Stepper stepper)
        {
            AddRowLabel(label, colX, y);
            stepper = new Stepper();
            stepper.SetBounds(Sc(fieldX), Sc(y), Sc(fieldW), Sc(ControlH));
            AddContent(stepper);
            if (!string.IsNullOrEmpty(suffix))
                AddMainLabel(suffix, fieldX - 26, y + (ControlH - 12) / 2, Theme.TextMuted, Theme.F(8.5f, FontStyle.Regular));
            return y + RowH;
        }

        int AddDropdownRow(string label, string[] items, int colX, int y, int fieldX, int fieldW, out FlatDrop drop)
        {
            AddRowLabel(label, colX, y);
            drop = new FlatDrop { Items = items, SelectedIndex = 0 };
            drop.SetBounds(Sc(fieldX), Sc(y), Sc(fieldW), Sc(ControlH));
            AddContent(drop);
            return y + RowH;
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
            int right = Col1X + ColW;
            if (present)
            {
                _cfgNote.Location = new Point(Sc(Col0X + 228), y);
                _cfgNote.Size = new Size(Sc(right - (Col0X + 228)), Sc(30));
            }
            else
            {
                _cfgNote.Location = new Point(Sc(Col0X + 128), y);
                _cfgNote.Size = new Size(Sc(right - (Col0X + 128)), Sc(30));
            }
        }

        // ---- helpers ----

        Label AddMainLabel(string text, int x, int y, Color color, Font font)
        {
            var l = new Label
            {
                Text = text, AutoSize = true, Location = new Point(Sc(x), Sc(y)),
                ForeColor = color, BackColor = Theme.FormBg, Font = font
            };
            AddContent(l);
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
            AddContent(l);
            return l;
        }

        void AddMainDivider(int colX, int colW, int y)
        {
            var p = new Panel
            {
                BackColor = Theme.Divider,
                Location = new Point(Sc(colX), Sc(y)),
                Size = new Size(Sc(colW), 1)
            };
            AddContent(p);
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
            int rad = Sc(12);
            var full = new Rectangle(0, 0, Width, Height);
            Theme.FillRound(g, full, rad, Theme.FormBg);
            // Left rail color into the form-owned border ring (panels are inset).
            using (var p = Theme.SidePath(new Rectangle(0, 0, Sc(SideW), Height), rad, true, false))
            using (var br = new SolidBrush(Theme.SideBg))
                g.FillPath(br, p);
            Theme.DrawRoundBorder(g, full, rad, Theme.CardBorder, BorderWidth());
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
                int lastRunes = d.ContainsKey("lastRuneUpgrades") ? Convert.ToInt32(d["lastRuneUpgrades"]) : 0;
                int lastChests = d.ContainsKey("lastChestOpens") ? Convert.ToInt32(d["lastChestOpens"]) : 0;
                bool runeOn = d.ContainsKey("autoUpgradeRune") && (bool)d["autoUpgradeRune"];
                bool chestOn = d.ContainsKey("autoOpenChest") && (bool)d["autoOpenChest"];
                bool synthOn = !d.ContainsKey("enableSynthesis") || (bool)d["enableSynthesis"];

                Color synthDot = auto ? Theme.Green : Theme.TextMuted;
                string synthState = auto ? "On" : "Off";
                string detail = "every " + cycMin + " min";
                if (lastRunes > 0) detail = lastRunes + " runes · " + detail;
                if (lastChests > 0) detail = lastChests + " chests · " + detail;
                else if (lastRunes == 0 && !synthOn && chestOn) detail = "chests · " + detail;
                else if (lastRunes == 0 && !synthOn && runeOn) detail = "runes · " + detail;
                _live.SetRow(1, "Loop",
                    cycles + " cycles",
                    detail,
                    synthState, synthDot);
            }
            catch { SynthIdle("status error"); }
        }

        void SynthIdle(string why)
        {
            _live.SetRow(1, "Loop", "—", why, "Off", Theme.TextMuted);
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
            _autoLoop.Enabled = on; _enableSynth.Enabled = on; _autoChest.Enabled = on;
            _autoRune.Enabled = on; _seg.Enabled = on;
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
                _autoLoop.Checked = !string.Equals(GetVal(text, "General", "AutoStart", "true"), "false", StringComparison.OrdinalIgnoreCase);
                _enableSynth.Checked = !string.Equals(GetVal(text, "General", "EnableSynthesis", "true"), "false", StringComparison.OrdinalIgnoreCase);
                _autoChest.Checked = !string.Equals(GetVal(text, "General", "AutoOpenChest", "false"), "false", StringComparison.OrdinalIgnoreCase);
                _autoRune.Checked = !string.Equals(GetVal(text, "General", "AutoUpgradeRune", "false"), "false", StringComparison.OrdinalIgnoreCase);
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
                text = SetVal(text, "General", "AutoStart", _autoLoop.Checked ? "true" : "false");
                text = SetVal(text, "General", "EnableSynthesis", _enableSynth.Checked ? "true" : "false");
                text = SetVal(text, "General", "AutoOpenChest", _autoChest.Checked ? "true" : "false");
                text = SetVal(text, "General", "AutoUpgradeRune", _autoRune.Checked ? "true" : "false");
                // AutoOpenCube / AutoOpenRune / AfterFill / AfterSynthesis / AfterChestOpen are not exposed in the UI — leave cfg values alone.
                text = SetVal(text, "Safety", "MaxGrade", _seg.Value.ToString(CultureInfo.InvariantCulture));
                text = SetVal(text, "General", "DesiredLevel",
                    Recipes[Math.Max(0, Math.Min(Recipes.Length - 1, _desiredLevel.SelectedIndex))]
                        .Lo.ToString(CultureInfo.InvariantCulture));
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

        // Map a cfg DesiredLevel to a dropdown index. Unknown values fall back to Max
        // (0) — DesiredLevel is a discrete enum of known lowers, not free-form.
        static int RecipeIndex(int desiredLevel)
        {
            for (int i = 0; i < Recipes.Length; i++)
                if (Recipes[i].Lo == desiredLevel) return i;
            return 0;
        }
    }
}
