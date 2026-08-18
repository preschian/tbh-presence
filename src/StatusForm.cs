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

        // Tile captions double as the cfg tokens the plugin writes back. Reading is
        // matched on a stem instead, because the plugin also accepts the singular
        // spellings ("Material", "Accessory") and "Gear" for Equipment.
        static readonly string[] SynthesisTypes = { "Equipment", "Materials", "Accessories" };
        static readonly string[] SynthesisTypeStems = { "equipment", "material", "accessor" };
        static readonly string[] Tiers = { "Normal", "Nightmare", "Hell", "Torment" };
        static readonly string[] TierStems = { "normal", "nightmare", "hell", "torment" };

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
        string _modOpNote;           // last progress/error from the background op
        bool _verCheckRunning;       // background release lookup in flight
        bool _updateOpRunning;       // self-update download in flight
        DateTime _verCheckedAt = DateTime.MinValue;

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
        Toggle _autoLoop, _enableSynth, _autoChest, _autoRune, _showConsole,
            _autoAlchemy, _autoOffering, _autoSoulstone, _pauseOnMouse;
        TypeTile[] _typeTiles, _tierTiles;
        SegmentBar _seg;
        Label _rarityValue;
        Stepper _cycleMin, _restartDays, _alchemyLevel, _offeringMax, _actBossRuns, _idleSec;
        FlatDrop _desiredLevel, _alchemyRarity;
        FlatButton _saveBtn, _modsBtn, _launchBtn, _updateBtn;
        Label _cfgNote, _verNote;
        readonly ToolTip _verTip = new ToolTip();

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
                _verTip.Dispose();
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
            _live = new LiveStrip { Columns = rows };
            _live.SetRow(0, "Presence", "—", "", "Off", Theme.TextMuted);
            if (Build.Synth)
                _live.SetRow(1, "Loop", "—", "", "Off", Theme.TextMuted);
            _side.Controls.Add(_live);

            // Launch sits just above the status rule. Synth edition stacks
            // Install/Remove mods directly above it (setup → play).
            _launchBtn = new FlatButton { Text = "Launch game", Fill = Theme.Accent };
            _launchBtn.Click += delegate { LaunchGame(); };
            _side.Controls.Add(_launchBtn);
            RefreshLaunchButton();

            if (Build.Synth)
            {
                _modsBtn = new FlatButton { Fill = Theme.Secondary };
                _modsBtn.Click += delegate
                {
                    if (BepInExSetup.HasRemnants()) RunRemove();
                    else RunSetup();
                };
                _side.Controls.Add(_modsBtn);
                RefreshModsButton();
            }

            PinSideBottom();
            AddVersionBlock();
        }

        // Pins status cards, Launch, and (synth) mods to the rail's bottom edge.
        void PinSideBottom()
        {
            int rows = Build.Synth ? 2 : 1;
            int statusH = rows * 68 + (rows - 1) * 8;
            _live.SetBounds(Sc(12), _side.Height - Sc(14 + statusH), Sc(SideW - 24), Sc(statusH));
            const int btnH = 30;
            _launchBtn.SetBounds(Sc(12), _live.Top - Sc(14 + 10 + btnH), Sc(SideW - 24), Sc(btnH));
            if (_modsBtn != null)
                _modsBtn.SetBounds(Sc(12), _launchBtn.Top - Sc(8 + btnH), Sc(SideW - 24), Sc(btnH));
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

            // Soft rule between the launch button and the status block.
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
            y0 = AddToggleRow("Pause on mouse", Col0X, ref _pauseOnMouse, t0, y0);
            y0 = AddFieldRow("Resume after", "sec", Col0X, y0, f0, fieldW, out _idleSec);
            _idleSec.Min = 5; _idleSec.Max = 300; _idleSec.Step = 5; _idleSec.Decimals = 0; _idleSec.Value = 30;
            _idleSec.Enabled = false;
            _pauseOnMouse.CheckedChanged += delegate { _idleSec.Enabled = _pauseOnMouse.Enabled && _pauseOnMouse.Checked; };
            y0 = AddToggleRow("Show BepInEx console", Col0X, ref _showConsole, t0, y0);
            y0 = AddFieldRow("Cycle interval", "min", Col0X, y0, f0, fieldW, out _cycleMin);
            _cycleMin.Min = 1; _cycleMin.Max = 1440; _cycleMin.Step = 1; _cycleMin.Decimals = 0; _cycleMin.Value = 5;
            y0 = AddSectionDivider(Col0X, ColW, y0);

            // Alchemy melts inventory gear below a level threshold into gold.
            y0 = AddSectionHeader("Alchemy", Col0X, y0);
            y0 = AddToggleRow("Enabled", Col0X, ref _autoAlchemy, t0, y0);
            y0 = AddFieldRow("Melt below level", "", Col0X, y0, f0, fieldW, out _alchemyLevel);
            _alchemyLevel.Min = 0; _alchemyLevel.Max = 9999; _alchemyLevel.Step = 5; _alchemyLevel.Decimals = 0;
            _alchemyLevel.Value = 0;
            y0 = AddDropdownRow("Max rarity", Grades, Col0X, y0, f0, fieldW, out _alchemyRarity);
            _alchemyRarity.SelectedIndex = 2;
            y0 = AddSectionDivider(Col0X, ColW, y0);

            // Offering consumes one offering coin per operation.
            y0 = AddSectionHeader("Offering", Col0X, y0);
            y0 = AddToggleRow("Enabled", Col0X, ref _autoOffering, t0, y0);
            y0 = AddFieldRow("Max per cycle", "", Col0X, y0, f0, fieldW, out _offeringMax);
            _offeringMax.Min = 1; _offeringMax.Max = 99; _offeringMax.Step = 1;
            _offeringMax.Decimals = 0; _offeringMax.Value = 5;

            // ---- right: Chests / Runes / Synthesis ----
            int t1 = Col1X + ColW - toggleW;
            int f1 = Col1X + ColW - fieldW;
            y1 = AddSectionHeader("Chests", Col1X, y1);
            y1 = AddToggleRow("Open chests", Col1X, ref _autoChest, t1, y1);
            y1 = AddSectionDivider(Col1X, ColW, y1);

            y1 = AddSectionHeader("Runes", Col1X, y1);
            y1 = AddToggleRow("Upgrade runes", Col1X, ref _autoRune, t1, y1);
            y1 = AddSectionDivider(Col1X, ColW, y1);

            // Re-enters a cleared Act Boss stage to spend surplus soulstones.
            y1 = AddSectionHeader("Soulstones", Col1X, y1);
            y1 = AddToggleRow("Spend on Act Bosses", Col1X, ref _autoSoulstone, t1, y1);

            y1 = AddTileRow("Tiers", Tiers, Col1X, y1, out _tierTiles);

            y1 = AddFieldRow("Runs per cycle", "", Col1X, y1, f1, fieldW, out _actBossRuns);
            _actBossRuns.Min = 1; _actBossRuns.Max = 99; _actBossRuns.Step = 1;
            _actBossRuns.Decimals = 0; _actBossRuns.Value = 5;
            y1 = AddSectionDivider(Col1X, ColW, y1);

            y1 = AddSectionHeader("Synthesis", Col1X, y1);
            y1 = AddToggleRow("Synthesize items", Col1X, ref _enableSynth, t1, y1);

            y1 = AddTileRow("Types", SynthesisTypes, Col1X, y1, out _typeTiles);

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

            _cfgNote = new Label
            {
                AutoSize = false,
                Location = new Point(Sc(Col0X + 96), Sc(y)),
                Size = new Size(Sc(Col1X + ColW - (Col0X + 96)), Sc(30)),
                ForeColor = Theme.TextMuted,
                BackColor = Theme.FormBg,
                Font = Theme.F(8.5f, FontStyle.Regular),
                TextAlign = ContentAlignment.MiddleLeft
            };
            AddContent(_cfgNote);

            EndContent(y + 30);
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

        // Mods-vs-game build status plus the one-click self-update button. Lives in
        // the side rail so it is always visible and never pushes the settings pane.
        void AddVersionBlock()
        {
            var head = new Label
            {
                Text = "Version", AutoSize = true, Location = new Point(Sc(16), Sc(72)),
                ForeColor = Theme.TextDark, BackColor = Theme.SideBg, Font = Theme.F(9.5f, FontStyle.Bold)
            };
            _side.Controls.Add(head);

            _verNote = new Label
            {
                Text = "checking for updates...", AutoSize = false,
                Location = new Point(Sc(16), Sc(92)),
                Size = new Size(Sc(SideW - 32), Sc(74)),
                ForeColor = Theme.TextMuted, BackColor = Theme.SideBg,
                Font = Theme.F(8.5f, FontStyle.Regular), TextAlign = ContentAlignment.TopLeft
            };
            _side.Controls.Add(_verNote);

            _updateBtn = new FlatButton { Text = UpdateTitle, Fill = Theme.Accent };
            _updateBtn.SetBounds(Sc(12), Sc(170), Sc(SideW - 24), Sc(30));
            _updateBtn.Click += delegate { RunUpdate(); };
            _updateBtn.Visible = false;
            _side.Controls.Add(_updateBtn);

            EnsureVersionCheck(false);
        }

        // Release lookup hits the network, so it runs off the UI thread. SelfUpdate
        // throttles internally; this only guards against overlapping threads.
        void EnsureVersionCheck(bool force)
        {
            if (_verCheckRunning || _updateOpRunning) return;
            var last = SelfUpdate.LastStatus;
            if (!force && last != null
                && (DateTime.UtcNow - _verCheckedAt).TotalMinutes < SelfUpdate.MinutesBetweenChecks(last)) return;

            _verCheckRunning = true;
            _verCheckedAt = DateTime.UtcNow;
            var t = new System.Threading.Thread(delegate()
            {
                try { SelfUpdate.Check(force); }
                catch { }
                try
                {
                    if (IsDisposed) { _verCheckRunning = false; return; }
                    BeginInvoke((Action)delegate { _verCheckRunning = false; RefreshVersionRow(); });
                }
                catch { _verCheckRunning = false; }
            });
            t.IsBackground = true;
            t.Start();
        }

        void RefreshVersionRow()
        {
            if (_verNote == null || _updateOpRunning) return;
            var st = SelfUpdate.LastStatus;
            if (st == null)
            {
                _verNote.Text = "checking for updates...";
                _verNote.ForeColor = Theme.TextMuted;
                _updateBtn.Visible = false;
                return;
            }
            _verNote.Text = st.Message;
            _verNote.ForeColor =
                st.State == SelfUpdate.State.UpdateReady ? Theme.Amber :
                st.State == SelfUpdate.State.Matched ? Theme.Green : Theme.TextMuted;
            // The rail is narrow, so keep the full text reachable on hover.
            _verTip.SetToolTip(_verNote, st.Message);
            _updateBtn.Visible = st.CanUpdate;
        }

        static string UpdateTitle
        {
            get { return "Update " + SelfUpdate.Noun.ToLowerInvariant(); }
        }

        // The presence-only edition ships no plugin, so it has nothing to redeploy.
        static string PluginNote()
        {
            if (!Build.Synth) return "";
            return GameRestart.IsGameRunning()
                ? "TaskBarHero is running, so the in-game plugin is refreshed once you close the game.\n\n"
                : "The in-game plugin is redeployed automatically after the restart.\n\n";
        }

        void RunUpdate()
        {
            if (_updateOpRunning || _modOpRunning) return;
            var st = SelfUpdate.LastStatus;
            if (st == null || !st.CanUpdate) { EnsureVersionCheck(true); return; }

            string body =
                "Update the companion to " + st.ReleaseTag + " for game v" + st.GameVersion + "?\n\n" +
                "  - downloads " + st.ReleaseTag + " from GitHub\n" +
                "  - replaces this app and restarts it\n\n" +
                PluginNote() +
                "Your save and settings are unaffected. Continue?";
            if (MessageBox.Show(this, body, UpdateTitle, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;

            _updateOpRunning = true;
            _updateBtn.Enabled = false;
            _verNote.Text = "working...";
            var t = new System.Threading.Thread(delegate()
            {
                bool restarting = SelfUpdate.Apply(st, delegate(string s) { PostVerNote(s); });
                PostUpdateDone(restarting);
            });
            t.IsBackground = true;
            t.Start();
        }

        void PostVerNote(string s)
        {
            try { if (!IsDisposed) BeginInvoke((Action)delegate { if (_verNote != null) _verNote.Text = s; }); }
            catch { }
        }

        void PostUpdateDone(bool restarting)
        {
            try
            {
                if (IsDisposed) return;
                BeginInvoke((Action)delegate
                {
                    _updateOpRunning = false;
                    _updateBtn.Enabled = true;
                    if (restarting) Application.Exit();   // the swap script relaunches us
                    else EnsureVersionCheck(true);
                });
            }
            catch { }
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

        // A captioned row of equal-width toggle tiles (synthesis types, soulstone tiers).
        int AddTileRow(string caption, string[] captions, int colX, int y, out TypeTile[] tiles)
        {
            AddMainLabel(caption, colX, y, Theme.TextDark, Theme.F(9.5f, FontStyle.Regular));
            y += 18;
            const int gap = 6;
            int tw = (ColW - gap * (captions.Length - 1)) / captions.Length;
            tiles = new TypeTile[captions.Length];
            for (int i = 0; i < captions.Length; i++)
            {
                tiles[i] = new TypeTile { Caption = captions[i] };
                tiles[i].SetBounds(Sc(colX + i * (tw + gap)), Sc(y), Sc(tw), Sc(ControlH));
                AddContent(tiles[i]);
            }
            return y + ControlH + 12;
        }

        int AddDropdownRow(string label, string[] items, int colX, int y, int fieldX, int fieldW, out FlatDrop drop)
        {
            AddRowLabel(label, colX, y);
            drop = new FlatDrop { Items = items, SelectedIndex = 0 };
            drop.SetBounds(Sc(fieldX), Sc(y), Sc(fieldW), Sc(ControlH));
            AddContent(drop);
            return y + RowH;
        }

        void RefreshModsButton()
        {
            if (_modsBtn == null || _modOpRunning) return;
            _modsBtn.Enabled = true;
            _modsBtn.Text = BepInExSetup.HasRemnants() ? "Remove mods" : "Install mods";
            _modsBtn.Invalidate();
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
                "Installing…",
                BepInExSetup.Install);
        }

        void RunRemove()
        {
            ConfirmAndRunModOp(
                "Remove mods",
                "This will remove mods by deleting BepInEx from the TaskBarHero folder.\n\n" +
                "Your save and Discord presence are unaffected. Continue?",
                "Removing…",
                BepInExSetup.Uninstall);
        }

        void ConfirmAndRunModOp(string title, string body, string busyLabel, Func<Action<string>, bool> work)
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
            _modOpNote = null;
            if (_modsBtn != null)
            {
                _modsBtn.Enabled = false;
                _modsBtn.Text = busyLabel;
                _modsBtn.Invalidate();
            }
            RefreshLaunchButton();
            var t = new System.Threading.Thread(delegate()
            {
                work(delegate(string s) { _modOpNote = s; });
                PostModOpDone();
            });
            t.IsBackground = true;
            t.Start();
        }

        void PostModOpDone()
        {
            try
            {
                if (IsDisposed) return;
                BeginInvoke((Action)delegate
                {
                    _modOpRunning = false;
                    string note = _modOpNote;
                    LoadConfig();
                    if (!string.IsNullOrEmpty(note))
                        _cfgNote.Text = note;
                    RefreshModsButton();
                    RefreshLaunchButton();
                    EnsureVersionCheck(true);
                });
            }
            catch { }
        }

        // ---- launch game ----

        void LaunchGame()
        {
            if (_modOpRunning || GameRestart.IsGameRunning() || GameRestart.IsBusy)
            {
                RefreshLaunchButton();
                return;
            }
            GameRestart.TryBeginLaunch();
            RefreshLaunchButton();
        }

        void RefreshLaunchButton()
        {
            if (_launchBtn == null) return;
            bool running = GameRestart.IsGameRunning();
            bool launching = GameRestart.IsLaunching();
            _launchBtn.Enabled = !running && !launching && !_modOpRunning;
            _launchBtn.Text = running ? "Running" : launching ? "Launching…" : "Launch game";
            _launchBtn.Invalidate();
        }

        // Dev (--shot): freeze the idle launch label so docs don't depend on a live
        // game, and grow the window so every setting fits without a scrollbar.
        internal void PrepareDocsShot()
        {
            if (_timer != null) _timer.Stop();
            if (_launchBtn != null)
            {
                _launchBtn.Enabled = true;
                _launchBtn.Text = "Launch game";
                _launchBtn.Invalidate();
            }
            if (_modsBtn != null)
            {
                _modOpRunning = false;
                RefreshModsButton();
            }
            GrowToFitContent();
        }

        // Resizes the form to the settings pane's natural height and re-runs the
        // absolute layout that normally depends on the fixed design height.
        void GrowToFitContent()
        {
            if (_scroll == null || _main == null || _side == null) return;

            int contentBottom = 0;
            foreach (Control c in _scroll.Controls)
                if (c.Bottom > contentBottom) contentBottom = c.Bottom;
            if (contentBottom <= 0) return;

            int b = BorderInset();
            int wanted = Sc(TopChrome) + contentBottom + Sc(16) + 2 * b;
            if (wanted <= ClientSize.Height) return;
            ClientSize = new Size(ClientSize.Width, wanted);

            _side.Size = new Size(_side.Width, Height - 2 * b);
            _main.Size = new Size(_main.Width, Height - 2 * b);
            _scroll.Size = new Size(_main.Width, _main.Height - Sc(TopChrome));

            PinSideBottom();

            FinishContent();
            ApplyRegion();
            _side.Invalidate();
            _main.Invalidate();
        }

        // ---- live status ----

        void UpdateStatus()
        {
            string stage = _stageLabel != null ? _stageLabel() : null;
            bool connected = _discordConnected != null && _discordConnected();
            string diag = _diag != null ? _diag() : null;
            bool presenceOn = _presenceEnabled == null || _presenceEnabled();

            RefreshLaunchButton();

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

            EnsureVersionCheck(false);   // no-op until the 30 min throttle expires
            RefreshVersionRow();         // also covers a check that finished pre-handle

            if (!Build.Synth) return;

            if (!_modOpRunning) RefreshModsButton();

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
                int lastBossRuns = d.ContainsKey("lastActBossRuns") ? Convert.ToInt32(d["lastActBossRuns"]) : 0;
                int lastOfferings = d.ContainsKey("lastOfferings") ? Convert.ToInt32(d["lastOfferings"]) : 0;
                bool runeOn = d.ContainsKey("autoUpgradeRune") && (bool)d["autoUpgradeRune"];
                bool chestOn = d.ContainsKey("autoOpenChest") && (bool)d["autoOpenChest"];
                bool offeringOn = d.ContainsKey("autoOffering") && (bool)d["autoOffering"];
                bool synthOn = !d.ContainsKey("enableSynthesis") || (bool)d["enableSynthesis"];

                Color synthDot = auto ? Theme.Green : Theme.TextMuted;
                string synthState = auto ? "On" : "Off";
                if (auto && d.ContainsKey("paused") && (bool)d["paused"])
                {
                    synthDot = Theme.Amber;
                    synthState = "Paused";
                }
                var bits = new List<string>();
                if (lastChests > 0) bits.Add(lastChests + " chests");
                if (lastRunes > 0) bits.Add(lastRunes + " runes");
                if (lastBossRuns > 0) bits.Add(lastBossRuns + " boss runs");
                if (lastOfferings > 0) bits.Add(lastOfferings + " offerings");
                if (bits.Count == 0 && !synthOn)
                {
                    if (chestOn) bits.Add("chests");
                    else if (runeOn) bits.Add("runes");
                    else if (offeringOn) bits.Add("offering");
                }
                bits.Add("every " + cycMin + " min");
                string detail = string.Join(" · ", bits.ToArray());
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
            _pauseOnMouse.Enabled = on;
            _idleSec.Enabled = on && _pauseOnMouse.Checked;
            _autoAlchemy.Enabled = on; _alchemyLevel.Enabled = on; _alchemyRarity.Enabled = on;
            _autoOffering.Enabled = on; _offeringMax.Enabled = on;
            _autoSoulstone.Enabled = on; _actBossRuns.Enabled = on;
            foreach (var tile in _tierTiles) tile.Enabled = on;
            foreach (var tile in _typeTiles) tile.Enabled = on;
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
                _pauseOnMouse.Checked = string.Equals(GetVal(text, "General", "PauseOnActivity", "false"), "true", StringComparison.OrdinalIgnoreCase);
                decimal idleSec = ParseF(GetVal(text, "Timing", "ActivityIdleSeconds", "30"));
                _idleSec.SetValue(Math.Max(5, Math.Min(300, Math.Round(idleSec > 0 ? idleSec : 30))));
                _enableSynth.Checked = !string.Equals(GetVal(text, "General", "EnableSynthesis", "true"), "false", StringComparison.OrdinalIgnoreCase);
                _autoChest.Checked = !string.Equals(GetVal(text, "General", "AutoOpenChest", "false"), "false", StringComparison.OrdinalIgnoreCase);
                _autoRune.Checked = !string.Equals(GetVal(text, "General", "AutoUpgradeRune", "false"), "false", StringComparison.OrdinalIgnoreCase);
                _autoAlchemy.Checked = !string.Equals(GetVal(text, "General", "AutoAlchemy", "false"), "false", StringComparison.OrdinalIgnoreCase);
                _autoOffering.Checked = !string.Equals(GetVal(text, "General", "AutoOffering", "false"), "false", StringComparison.OrdinalIgnoreCase);
                _autoSoulstone.Checked = !string.Equals(GetVal(text, "General", "AutoConsumeSoulstone", "false"), "false", StringComparison.OrdinalIgnoreCase);
                int runs;
                if (!int.TryParse(GetVal(text, "Safety", "ActBossRunsPerCycle", "5"), out runs) || runs < 1) runs = 5;
                _actBossRuns.SetValue(runs);
                SelectTiles(_tierTiles, TierStems,
                    GetVal(text, "General", "SoulstoneTiers", string.Join(",", Tiers)));
                int al;
                if (!int.TryParse(GetVal(text, "General", "AlchemyLevelThreshold", "0"), out al) || al < 0) al = 0;
                _alchemyLevel.SetValue(al);
                int ag;
                if (!int.TryParse(GetVal(text, "Safety", "MaxAlchemyGrade", "2"), out ag) || ag < 0 || ag > 9) ag = 2;
                _alchemyRarity.SelectedIndex = ag;
                int offeringMax;
                if (!int.TryParse(GetVal(text, "Safety", "MaxOfferingOperationsPerCycle", "5"), out offeringMax)
                    || offeringMax < 1) offeringMax = 5;
                _offeringMax.SetValue(offeringMax);
                int mg;
                if (!int.TryParse(GetVal(text, "Safety", "MaxGrade", "2"), out mg) || mg < 0 || mg > 9) mg = 2;
                _seg.Value = mg; UpdateRarityLabel();
                int dl;
                if (!int.TryParse(GetVal(text, "General", "DesiredLevel", "0"), out dl) || dl < 0) dl = 0;
                _desiredLevel.SelectedIndex = RecipeIndex(dl);
                decimal cycleSec = ParseF(GetVal(text, "Timing", "CycleIntervalSeconds", "300"));
                _cycleMin.SetValue(Math.Round(cycleSec / 60m));
                string types = GetVal(text, "General", "SynthesisTypes", string.Join(",", SynthesisTypes));
                SelectTiles(_typeTiles, SynthesisTypeStems, types);
                if (types.IndexOf("gear", StringComparison.OrdinalIgnoreCase) >= 0)
                    _typeTiles[0].Selected = true;

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

        // A tile is on when the cfg list names it; a list that names none of them
        // means "all of them", which is how the plugin reads it too.
        static void SelectTiles(TypeTile[] tiles, string[] stems, string raw)
        {
            string list = (raw ?? "").ToLowerInvariant();
            bool any = false;
            for (int i = 0; i < tiles.Length; i++)
            {
                tiles[i].Selected = list.Contains(stems[i]);
                any |= tiles[i].Selected;
            }
            if (!any) foreach (var tile in tiles) tile.Selected = true;
        }

        static string SelectedTiles(TypeTile[] tiles, string[] names)
        {
            var picked = new List<string>();
            for (int i = 0; i < tiles.Length; i++)
                if (tiles[i].Selected) picked.Add(names[i]);
            if (picked.Count == 0) picked.AddRange(names);
            return string.Join(",", picked.ToArray());
        }

        void SaveConfig()
        {
            if (_cfgPath == null || !File.Exists(_cfgPath)) { _cfgNote.Text = "start the game once to create settings"; return; }
            try
            {
                string text = File.ReadAllText(_cfgPath);
                text = SetVal(text, "General", "AutoStart", _autoLoop.Checked ? "true" : "false");
                text = SetVal(text, "General", "PauseOnActivity", _pauseOnMouse.Checked ? "true" : "false");
                text = SetVal(text, "Timing", "ActivityIdleSeconds",
                    ((int)_idleSec.Value).ToString(CultureInfo.InvariantCulture));
                text = SetVal(text, "General", "EnableSynthesis", _enableSynth.Checked ? "true" : "false");
                text = SetVal(text, "General", "AutoOpenChest", _autoChest.Checked ? "true" : "false");
                text = SetVal(text, "General", "AutoUpgradeRune", _autoRune.Checked ? "true" : "false");
                text = SetVal(text, "General", "AutoAlchemy", _autoAlchemy.Checked ? "true" : "false");
                text = SetVal(text, "General", "AutoOffering", _autoOffering.Checked ? "true" : "false");
                text = SetVal(text, "General", "AutoConsumeSoulstone", _autoSoulstone.Checked ? "true" : "false");
                text = SetVal(text, "Safety", "ActBossRunsPerCycle",
                    ((int)_actBossRuns.Value).ToString(CultureInfo.InvariantCulture));
                text = SetVal(text, "General", "AlchemyLevelThreshold",
                    ((int)_alchemyLevel.Value).ToString(CultureInfo.InvariantCulture));
                text = SetVal(text, "Safety", "MaxAlchemyGrade",
                    Math.Max(0, Math.Min(Grades.Length - 1, _alchemyRarity.SelectedIndex))
                        .ToString(CultureInfo.InvariantCulture));
                text = SetVal(text, "Safety", "MaxOfferingOperationsPerCycle",
                    ((int)_offeringMax.Value).ToString(CultureInfo.InvariantCulture));
                // AutoOpenCube / AutoOpenRune / AfterFill / AfterSynthesis / AfterChestOpen are not exposed in the UI — leave cfg values alone.
                text = SetVal(text, "Safety", "MaxGrade", _seg.Value.ToString(CultureInfo.InvariantCulture));
                text = SetVal(text, "General", "DesiredLevel",
                    Recipes[Math.Max(0, Math.Min(Recipes.Length - 1, _desiredLevel.SelectedIndex))]
                        .Lo.ToString(CultureInfo.InvariantCulture));
                text = SetVal(text, "Timing", "CycleIntervalSeconds", (_cycleMin.Value * 60).ToString(CultureInfo.InvariantCulture));
                text = SetVal(text, "General", "SynthesisTypes", SelectedTiles(_typeTiles, SynthesisTypes));
                text = SetVal(text, "General", "SoulstoneTiers", SelectedTiles(_tierTiles, Tiers));
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
