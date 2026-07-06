using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace TbhCompanion
{
    // Status & settings window: live indicators for the presence loop and the
    // in-game auto-synthesis plugin, plus editable plugin settings. Settings are
    // written to the BepInEx cfg file; the plugin hot-reloads it within ~10s,
    // so no game restart is needed.
    public class StatusForm : Form
    {
        static readonly string[] Grades =
            { "Common", "Uncommon", "Rare", "Legendary", "Immortal", "Arcana", "Beyond", "Celestial", "Divine", "Cosmic" };

        static readonly string StatusPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "tbh-companion", "autosynth-status.json");

        readonly Func<string> _presenceStatus;
        readonly Timer _timer;
        string _cfgPath;

        Label _presenceDot, _presenceText;
        Label _synthDot, _synthText, _synthDetail;
        FlowLayoutPanel _setupPanel;
        Button _setupBtn;
        Label _setupNote;
        bool _setupRunning;
        CheckBox _autoStart;
        CheckBox _showConsole;
        CheckBox _tEquip, _tMaterials, _tAccessories;
        string _bepinexCfgPath;
        ComboBox _maxGrade;
        NumericUpDown _cycleMin, _fillSec, _synthSec;
        Button _saveBtn;
        Label _cfgNote;

        TableLayoutPanel _root;

        public StatusForm(Func<string> presenceStatus)
        {
            _presenceStatus = presenceStatus;

            Text = "TBH Companion - Status & Settings";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Font;
            AutoScaleDimensions = new SizeF(7f, 15f);   // Segoe UI 9pt at 96 dpi
            Font = new Font("Segoe UI", 9f);
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            // Single-column stack; every row sizes itself, so nothing can overlap
            // regardless of DPI scaling.
            _root = new TableLayoutPanel();
            _root.ColumnCount = 1;
            _root.AutoSize = true;
            _root.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _root.Padding = new Padding(14, 12, 14, 12);
            Controls.Add(_root);

            AddHeader("Discord Presence");
            _presenceDot = MakeDot();
            _presenceText = MakeText("starting...");
            AddDotRow(_presenceDot, _presenceText);
            AddSpacer(10);

            AddHeader("Auto Synthesis (in-game)");
            _synthDot = MakeDot();
            _synthText = MakeText("checking...");
            AddDotRow(_synthDot, _synthText);
            _synthDetail = MakeText("");
            _synthDetail.ForeColor = SystemColors.GrayText;
            _synthDetail.Margin = new Padding(24, 2, 0, 0);
            _root.Controls.Add(_synthDetail);

            // Shown only when BepInEx is missing: one-click setup.
            _setupPanel = new FlowLayoutPanel();
            _setupPanel.AutoSize = true;
            _setupPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _setupPanel.WrapContents = false;
            _setupPanel.Margin = new Padding(24, 4, 0, 0);
            _setupBtn = new Button();
            _setupBtn.Text = "Set up auto-synthesis";
            _setupBtn.AutoSize = true;
            _setupBtn.Click += delegate { RunSetup(); };
            _setupPanel.Controls.Add(_setupBtn);
            _setupNote = new Label();
            _setupNote.AutoSize = true;
            _setupNote.MaximumSize = new Size(300, 0);
            _setupNote.ForeColor = SystemColors.GrayText;
            _setupNote.Margin = new Padding(10, 6, 0, 0);
            _setupPanel.Controls.Add(_setupNote);
            _root.Controls.Add(_setupPanel);

            AddSpacer(10);

            AddHeader("Auto Synthesis Settings");

            _autoStart = new CheckBox();
            _autoStart.Text = "Start automatically when the game launches (no F8 needed)";
            _autoStart.AutoSize = true;
            _autoStart.Margin = new Padding(3, 4, 3, 2);
            _root.Controls.Add(_autoStart);

            _showConsole = new CheckBox();
            _showConsole.Text = "Show the BepInEx log console (needs game restart)";
            _showConsole.AutoSize = true;
            _showConsole.Margin = new Padding(3, 2, 3, 6);
            _root.Controls.Add(_showConsole);

            var typeLabel = new Label();
            typeLabel.Text = "Synthesize which types (rotates each round):";
            typeLabel.AutoSize = true;
            typeLabel.Margin = new Padding(3, 2, 3, 2);
            _root.Controls.Add(typeLabel);

            var typeRow = new FlowLayoutPanel();
            typeRow.AutoSize = true;
            typeRow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            typeRow.WrapContents = false;
            typeRow.Margin = new Padding(12, 0, 3, 6);
            _tEquip = MakeTypeCheck("Equipment");
            _tMaterials = MakeTypeCheck("Materials");
            _tAccessories = MakeTypeCheck("Accessories");
            typeRow.Controls.Add(_tEquip);
            typeRow.Controls.Add(_tMaterials);
            typeRow.Controls.Add(_tAccessories);
            _root.Controls.Add(typeRow);

            var grid = new TableLayoutPanel();
            grid.ColumnCount = 2;
            grid.AutoSize = true;
            grid.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _root.Controls.Add(grid);

            _maxGrade = new ComboBox();
            _maxGrade.DropDownStyle = ComboBoxStyle.DropDownList;
            for (int i = 0; i < Grades.Length; i++) _maxGrade.Items.Add(Grades[i]);
            _maxGrade.Width = 140;
            AddSettingRow(grid, "Max rarity to synthesize:", _maxGrade);

            _cycleMin = MakeNum(1, 1440, 0);
            _cycleMin.Increment = 1;
            AddSettingRow(grid, "Cycle interval (minutes):", _cycleMin);

            _fillSec = MakeNum(0.5m, 60, 1);
            AddSettingRow(grid, "Delay after auto-fill (seconds):", _fillSec);

            _synthSec = MakeNum(0.5m, 60, 1);
            AddSettingRow(grid, "Delay after synthesis (seconds):", _synthSec);

            var saveRow = new FlowLayoutPanel();
            saveRow.AutoSize = true;
            saveRow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            saveRow.WrapContents = false;
            saveRow.Margin = new Padding(0, 8, 0, 0);
            _saveBtn = new Button();
            _saveBtn.Text = "Save settings";
            _saveBtn.AutoSize = true;
            _saveBtn.Click += delegate { SaveConfig(); };
            saveRow.Controls.Add(_saveBtn);
            _cfgNote = new Label();
            _cfgNote.AutoSize = true;
            _cfgNote.MaximumSize = new Size(280, 0);
            _cfgNote.ForeColor = SystemColors.GrayText;
            _cfgNote.Margin = new Padding(10, 8, 0, 0);
            saveRow.Controls.Add(_cfgNote);
            _root.Controls.Add(saveRow);

            LoadConfig();
            UpdateStatus();

            _timer = new Timer();
            _timer.Interval = 1000;
            _timer.Tick += delegate { UpdateStatus(); };
            _timer.Start();

            FormClosed += delegate { _timer.Stop(); _timer.Dispose(); };
        }

        // ---- layout helpers ----

        void AddHeader(string text)
        {
            var l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold);
            l.Margin = new Padding(0, 0, 0, 4);
            _root.Controls.Add(l);
        }

        void AddSpacer(int h)
        {
            var p = new Panel();
            p.Height = h;
            p.Width = 1;
            p.Margin = new Padding(0);
            _root.Controls.Add(p);
        }

        static Label MakeDot()
        {
            var dot = new Label();
            dot.Text = "●";
            dot.ForeColor = Color.Gray;
            dot.AutoSize = true;
            dot.Margin = new Padding(3, 0, 0, 0);
            return dot;
        }

        static Label MakeText(string text)
        {
            var l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.MaximumSize = new Size(380, 0);   // wrap long status lines
            l.Margin = new Padding(3, 0, 3, 0);
            return l;
        }

        void AddDotRow(Label dot, Label text)
        {
            var flow = new FlowLayoutPanel();
            flow.AutoSize = true;
            flow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            flow.WrapContents = false;
            flow.Margin = new Padding(0);
            flow.Controls.Add(dot);
            flow.Controls.Add(text);
            _root.Controls.Add(flow);
        }

        static void AddSettingRow(TableLayoutPanel grid, string label, Control control)
        {
            var l = new Label();
            l.Text = label;
            l.AutoSize = true;
            l.Anchor = AnchorStyles.Left;
            l.Margin = new Padding(3, 6, 12, 6);
            int row = grid.RowCount++;
            grid.Controls.Add(l, 0, row);
            control.Anchor = AnchorStyles.Left;
            control.Margin = new Padding(3, 3, 3, 3);
            grid.Controls.Add(control, 1, row);
        }

        static CheckBox MakeTypeCheck(string text)
        {
            var c = new CheckBox();
            c.Text = text;
            c.AutoSize = true;
            c.Margin = new Padding(3, 3, 12, 3);
            return c;
        }

        static NumericUpDown MakeNum(decimal min, decimal max, int decimals)
        {
            var n = new NumericUpDown();
            n.Minimum = min; n.Maximum = max; n.DecimalPlaces = decimals;
            n.Increment = decimals > 0 ? 0.5m : 5m;
            n.Width = 140;
            return n;
        }

        // ---- one-click BepInEx setup ----

        void RunSetup()
        {
            if (_setupRunning) return;

            if (!BepInExSetup.GameFound)
            {
                MessageBox.Show(this,
                    "Couldn't find the TaskBarHero game folder.\n\nStart the game once so it can be located, then try again.",
                    "Set up auto-synthesis", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (BepInExSetup.GameRunning())
            {
                MessageBox.Show(this,
                    "Please close TaskBarHero first, then run setup again.",
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
                bool success = BepInExSetup.Install(delegate(string s) { PostSetupNote(s); });
                PostSetupDone(success);
            });
            t.IsBackground = true;
            t.Start();
        }

        void PostSetupNote(string s)
        {
            try
            {
                if (IsDisposed) return;
                BeginInvoke((Action)delegate { _setupNote.Text = s; });
            }
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
                    if (success)
                    {
                        _setupPanel.Visible = false;
                        LoadConfig(); // config appears after the game's first run; refresh if present
                    }
                });
            }
            catch { }
        }

        // ---- live status ----

        void UpdateStatus()
        {
            // presence: green once attached/connected, yellow while waiting
            string p = _presenceStatus != null ? _presenceStatus() : null;
            if (p == null) p = "starting...";
            bool waiting = p.IndexOf("waiting", StringComparison.OrdinalIgnoreCase) >= 0;
            _presenceDot.ForeColor = waiting ? Color.Goldenrod : Color.ForestGreen;
            _presenceText.Text = p;

            // offer one-click setup while BepInEx isn't installed (skip during a run)
            if (!_setupRunning)
                _setupPanel.Visible = !BepInExSetup.IsInstalled();

            // auto-synthesis: read the status file the plugin refreshes every ~3s
            try
            {
                if (!File.Exists(StatusPath))
                {
                    SetSynth(Color.Gray, "plugin has not reported yet (game not started?)", "");
                    return;
                }
                var js = new JavaScriptSerializer();
                var d = js.Deserialize<Dictionary<string, object>>(File.ReadAllText(StatusPath));
                DateTime updated = DateTime.Parse((string)d["updatedUtc"], null, DateTimeStyles.RoundtripKind);
                if ((DateTime.UtcNow - updated).TotalSeconds > 15)
                {
                    SetSynth(Color.Gray, "game is not running", "");
                    return;
                }
                bool auto = (bool)d["auto"];
                int cycles = Convert.ToInt32(d["cycles"]);
                int lastCount = Convert.ToInt32(d["lastCount"]);
                int lastGrade = Convert.ToInt32(d["lastGrade"]);
                string last = lastCount > 0
                    ? "last synthesis: " + lastCount + " item(s), " + GradeName(lastGrade)
                    : "no synthesis yet this session";
                SetSynth(
                    auto ? Color.ForestGreen : Color.IndianRed,
                    auto ? "ON - " + cycles + " cycle(s) this session" : "OFF (press F8 in game to enable)",
                    last + "   |   plugin v" + d["version"]);
            }
            catch (Exception ex)
            {
                SetSynth(Color.Gray, "status unreadable: " + ex.Message, "");
            }
        }

        void SetSynth(Color c, string text, string detail)
        {
            _synthDot.ForeColor = c;
            _synthText.Text = text;
            _synthDetail.Text = detail;
        }

        static string GradeName(int g)
        {
            return g >= 0 && g < Grades.Length ? Grades[g] : "?";
        }

        // ---- config file ----

        static string FindCfgPath()
        {
            string gameDir = AutoSynthDeploy.FindGameDir();
            if (gameDir == null) return null;
            return Path.Combine(gameDir, "BepInEx", "config", "com.pres.tbh.autosynth.cfg");
        }

        static string FindBepInExCfgPath()
        {
            return BepInExCfg.Path(AutoSynthDeploy.FindGameDir());
        }

        void LoadConfig()
        {
            _cfgPath = FindCfgPath();
            if (_cfgPath == null || !File.Exists(_cfgPath))
            {
                SetConfigEnabled(false);
                _cfgNote.Text = "config not found - start the game once first";
                return;
            }
            try
            {
                string text = File.ReadAllText(_cfgPath);
                _autoStart.Checked = !string.Equals(GetVal(text, "AutoStart", "true"), "false", StringComparison.OrdinalIgnoreCase);
                int mg;
                if (!int.TryParse(GetVal(text, "MaxGrade", "2"), out mg) || mg < 0 || mg > 9) mg = 2;
                _maxGrade.SelectedIndex = mg;
                decimal cycleSec = ParseF(GetVal(text, "CycleIntervalSeconds", "300"));
                _cycleMin.Value = Clamp(Math.Round(cycleSec / 60m), _cycleMin);
                _fillSec.Value = Clamp(ParseF(GetVal(text, "AfterFillSeconds", "1")), _fillSec);
                _synthSec.Value = Clamp(ParseF(GetVal(text, "AfterSynthesisSeconds", "4")), _synthSec);
                string types = GetVal(text, "SynthesisTypes", "Equipment,Materials,Accessories").ToLowerInvariant();
                _tEquip.Checked = types.Contains("equipment") || types.Contains("gear");
                _tMaterials.Checked = types.Contains("material");
                _tAccessories.Checked = types.Contains("accessor");
                if (!_tEquip.Checked && !_tMaterials.Checked && !_tAccessories.Checked)
                { _tEquip.Checked = _tMaterials.Checked = _tAccessories.Checked = true; }

                _bepinexCfgPath = FindBepInExCfgPath();
                if (_bepinexCfgPath != null && File.Exists(_bepinexCfgPath))
                {
                    _showConsole.Checked = BepInExCfg.GetConsoleEnabled(File.ReadAllText(_bepinexCfgPath));
                    _showConsole.Enabled = true;
                }
                else { _showConsole.Enabled = false; }

                SetConfigEnabled(true);
                _cfgNote.Text = "";
            }
            catch (Exception ex)
            {
                SetConfigEnabled(false);
                _cfgNote.Text = "config unreadable: " + ex.Message;
            }
        }

        void SaveConfig()
        {
            if (_cfgPath == null || !File.Exists(_cfgPath))
            {
                _cfgNote.Text = "config not found - start the game once first";
                return;
            }
            try
            {
                string text = File.ReadAllText(_cfgPath);
                text = SetVal(text, "AutoStart", _autoStart.Checked ? "true" : "false");
                text = SetVal(text, "MaxGrade", _maxGrade.SelectedIndex.ToString(CultureInfo.InvariantCulture));
                text = SetVal(text, "CycleIntervalSeconds", (_cycleMin.Value * 60).ToString(CultureInfo.InvariantCulture));
                text = SetVal(text, "AfterFillSeconds", _fillSec.Value.ToString(CultureInfo.InvariantCulture));
                text = SetVal(text, "AfterSynthesisSeconds", _synthSec.Value.ToString(CultureInfo.InvariantCulture));
                var types = new List<string>();
                if (_tEquip.Checked) types.Add("Equipment");
                if (_tMaterials.Checked) types.Add("Materials");
                if (_tAccessories.Checked) types.Add("Accessories");
                if (types.Count == 0) { types.Add("Equipment"); types.Add("Materials"); types.Add("Accessories"); }
                text = SetVal(text, "SynthesisTypes", string.Join(",", types.ToArray()));
                File.WriteAllText(_cfgPath, text);

                bool consoleNeedsRestart = false;
                if (_bepinexCfgPath != null && File.Exists(_bepinexCfgPath))
                {
                    string bx = File.ReadAllText(_bepinexCfgPath);
                    if (BepInExCfg.GetConsoleEnabled(bx) != _showConsole.Checked)
                    {
                        File.WriteAllText(_bepinexCfgPath, BepInExCfg.SetConsoleEnabled(bx, _showConsole.Checked));
                        consoleNeedsRestart = true;
                    }
                }
                _cfgNote.Text = consoleNeedsRestart
                    ? "saved - console change needs a game restart"
                    : "saved - applies in-game within ~10s";
            }
            catch (Exception ex)
            {
                _cfgNote.Text = "save failed: " + ex.Message;
            }
        }

        void SetConfigEnabled(bool on)
        {
            _autoStart.Enabled = on;
            // _showConsole enabled state is managed in LoadConfig (separate cfg file)
            _maxGrade.Enabled = on;
            _cycleMin.Enabled = on;
            _fillSec.Enabled = on;
            _synthSec.Enabled = on;
            _tEquip.Enabled = on;
            _tMaterials.Enabled = on;
            _tAccessories.Enabled = on;
            _saveBtn.Enabled = on;
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
            if (decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out v)) return v;
            return 0;
        }

        static decimal Clamp(decimal v, NumericUpDown n)
        {
            if (v < n.Minimum) return n.Minimum;
            if (v > n.Maximum) return n.Maximum;
            return v;
        }
    }
}
