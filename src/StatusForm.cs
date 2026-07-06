using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace TbhPresence
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
            "tbh-presence", "autosynth-status.json");

        readonly Func<string> _presenceStatus;
        readonly Timer _timer;
        string _cfgPath;

        Label _presenceDot, _presenceText;
        Label _synthDot, _synthText, _synthDetail;
        CheckBox _autoStart;
        ComboBox _maxGrade;
        NumericUpDown _cycleMin, _fillSec, _synthSec;
        Button _saveBtn;
        Label _cfgNote;

        public StatusForm(Func<string> presenceStatus)
        {
            _presenceStatus = presenceStatus;

            Text = "TBH Tools - Status & Settings";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);   // layout below is designed at 96 dpi
            ClientSize = new Size(420, 388);
            Font = new Font("Segoe UI", 9f);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            int y = 12;
            AddHeader("Discord Presence", ref y);
            _presenceDot = AddDot(ref y);
            _presenceText = AddText("starting...", _presenceDot);
            y += 30;

            AddHeader("Auto Synthesis (in-game)", ref y);
            _synthDot = AddDot(ref y);
            _synthText = AddText("checking...", _synthDot);
            y += 26;
            _synthDetail = new Label();
            _synthDetail.SetBounds(30, y, 380, 18);
            _synthDetail.ForeColor = SystemColors.GrayText;
            Controls.Add(_synthDetail);
            y += 30;

            AddHeader("Auto Synthesis Settings", ref y);

            _autoStart = new CheckBox();
            _autoStart.Text = "Start automatically when the game launches (no F8 needed)";
            _autoStart.SetBounds(16, y, 396, 22);
            Controls.Add(_autoStart);
            y += 28;

            AddLabel("Max rarity to synthesize:", 16, y);
            _maxGrade = new ComboBox();
            _maxGrade.DropDownStyle = ComboBoxStyle.DropDownList;
            for (int i = 0; i < Grades.Length; i++) _maxGrade.Items.Add(Grades[i]);
            _maxGrade.SetBounds(220, y - 3, 130, 24);
            Controls.Add(_maxGrade);
            y += 30;

            AddLabel("Cycle interval (minutes):", 16, y);
            _cycleMin = MakeNum(1, 1440, 0, 220, y);
            _cycleMin.Increment = 1;
            y += 30;

            AddLabel("Delay after auto-fill (seconds):", 16, y);
            _fillSec = MakeNum(0.5m, 60, 1, 220, y);
            y += 30;

            AddLabel("Delay after synthesis (seconds):", 16, y);
            _synthSec = MakeNum(0.5m, 60, 1, 220, y);
            y += 34;

            _saveBtn = new Button();
            _saveBtn.Text = "Save settings";
            _saveBtn.SetBounds(16, y, 110, 28);
            _saveBtn.Click += delegate { SaveConfig(); };
            Controls.Add(_saveBtn);

            _cfgNote = new Label();
            _cfgNote.SetBounds(136, y + 5, 280, 20);
            _cfgNote.ForeColor = SystemColors.GrayText;
            Controls.Add(_cfgNote);

            LoadConfig();
            UpdateStatus();

            _timer = new Timer();
            _timer.Interval = 1000;
            _timer.Tick += delegate { UpdateStatus(); };
            _timer.Start();

            FormClosed += delegate { _timer.Stop(); _timer.Dispose(); };
        }

        // ---- layout helpers ----

        void AddHeader(string text, ref int y)
        {
            var l = new Label();
            l.Text = text;
            l.Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold);
            l.SetBounds(12, y, 396, 20);
            Controls.Add(l);
            y += 24;
        }

        Label AddDot(ref int y)
        {
            var dot = new Label();
            dot.Text = "●";
            dot.ForeColor = Color.Gray;
            dot.SetBounds(14, y, 16, 18);
            Controls.Add(dot);
            return dot;
        }

        Label AddText(string text, Label dot)
        {
            var l = new Label();
            l.Text = text;
            l.SetBounds(30, dot.Top, 380, 18);
            Controls.Add(l);
            return l;
        }

        void AddLabel(string text, int x, int y)
        {
            var l = new Label();
            l.Text = text;
            l.SetBounds(x, y, 200, 20);
            Controls.Add(l);
        }

        NumericUpDown MakeNum(decimal min, decimal max, int decimals, int x, int y)
        {
            var n = new NumericUpDown();
            n.Minimum = min; n.Maximum = max; n.DecimalPlaces = decimals;
            n.Increment = decimals > 0 ? 0.5m : 5m;
            n.SetBounds(x, y - 3, 130, 24);
            Controls.Add(n);
            return n;
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
                File.WriteAllText(_cfgPath, text);
                _cfgNote.Text = "saved - applies in-game within ~10s";
            }
            catch (Exception ex)
            {
                _cfgNote.Text = "save failed: " + ex.Message;
            }
        }

        void SetConfigEnabled(bool on)
        {
            _autoStart.Enabled = on;
            _maxGrade.Enabled = on;
            _cycleMin.Enabled = on;
            _fillSec.Enabled = on;
            _synthSec.Enabled = on;
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
