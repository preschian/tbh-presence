using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TbhCompanion
{
    // Clean modern theme + custom-painted controls for the settings window.
    static class Theme
    {
        public static readonly Color FormBg      = Hex("f4f5f7");
        public static readonly Color SideBg      = Hex("ffffff");
        public static readonly Color StatusCard  = Hex("f4f5f7");
        public static readonly Color CardBg      = Hex("ffffff");
        public static readonly Color CardBorder  = Hex("e5e7eb");
        public static readonly Color TextDark    = Hex("111827");
        public static readonly Color TextMuted   = Hex("6b7280");
        public static readonly Color Accent      = Hex("2563eb");
        public static readonly Color AccentSoft  = Hex("eff6ff");
        public static readonly Color Secondary   = Hex("374151");
        public static readonly Color Green       = Hex("16a34a");
        public static readonly Color Divider     = Hex("eef0f3");
        public static readonly Color StepBtnBg   = Hex("f3f4f6");
        public static readonly Color TypeSelBg   = Hex("eff6ff");
        public static readonly Color ToggleOff   = Hex("d1d5db");
        public static readonly Color SegEmpty    = Hex("e5e7eb");
        public static readonly Color Amber       = Hex("d97706");

        public static readonly Color[] GradeColors =
        {
            Hex("9aa0a6"), Hex("16a34a"), Hex("2563eb"), Hex("d97706"), Hex("dc2626"),
            Hex("7c3aed"), Hex("ea580c"), Hex("0d9488"), Hex("db2777"), Hex("4338ca")
        };

        public static Font F(float size, FontStyle style) { return new Font("Segoe UI", size, style); }

        // DPI scale for a paint surface (1.0 at 96 dpi, 1.25 at 125%, ...).
        public static float Scale(Graphics g) { return g.DpiX / 96f; }

        static Color Hex(string h)
        {
            return Color.FromArgb(
                Convert.ToInt32(h.Substring(0, 2), 16),
                Convert.ToInt32(h.Substring(2, 2), 16),
                Convert.ToInt32(h.Substring(4, 2), 16));
        }

        public static GraphicsPath Round(Rectangle r, int radius)
        {
            int d = radius * 2;
            var p = new GraphicsPath();
            if (d <= 0) { p.AddRectangle(r); p.CloseFigure(); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Rectangle r, int radius, Color fill)
        {
            using (var p = Round(r, radius)) using (var b = new SolidBrush(fill)) g.FillPath(b, p);
        }

        // Border stroked inside the fill edge (PenAlignment.Inset) using the SAME
        // rectangle as the fill, so no 1px seam shows between fill and border.
        public static void DrawRoundBorder(Graphics g, Rectangle r, int radius, Color border, float width)
        {
            using (var p = Round(r, radius))
            using (var pen = new Pen(border, width) { Alignment = PenAlignment.Inset })
                g.DrawPath(pen, p);
        }

        // Fill a rectangle rounding only the requested (left / right) corners.
        public static GraphicsPath SidePath(Rectangle r, int rad, bool left, bool right)
        {
            var p = new GraphicsPath();
            int d = rad * 2;
            if (left) p.AddArc(r.X, r.Y, d, d, 180, 90); else p.AddLine(r.X, r.Y, r.X, r.Y);
            if (right) p.AddArc(r.Right - d, r.Y, d, d, 270, 90); else p.AddLine(r.Right, r.Y, r.Right, r.Y);
            if (right) p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); else p.AddLine(r.Right, r.Bottom, r.Right, r.Bottom);
            if (left) p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); else p.AddLine(r.X, r.Bottom, r.X, r.Bottom);
            p.CloseFigure();
            return p;
        }
    }

    // iOS-style pill toggle.
    class Toggle : Control
    {
        bool _on;
        public event EventHandler CheckedChanged;
        public bool Checked { get { return _on; } set { if (_on != value) { _on = value; Invalidate(); if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty); } } }
        public Toggle()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            Size = new Size(44, 24); Cursor = Cursors.Hand;
        }
        protected override void OnClick(EventArgs e) { if (Enabled) Checked = !Checked; base.OnClick(e); }
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float s = Theme.Scale(g);
            if (Parent != null) using (var b = new SolidBrush(Parent.BackColor)) g.FillRectangle(b, ClientRectangle);
            int h = Height, w = Width;
            var track = new Rectangle(0, 0, w, h);
            Theme.FillRound(g, track, h / 2, Enabled ? (_on ? Theme.Accent : Theme.ToggleOff) : Theme.ToggleOff);
            int pad = (int)Math.Round(3 * s);
            int kd = h - pad * 2;
            int kx = _on ? w - kd - pad : pad;
            using (var b = new SolidBrush(Color.White))
                g.FillEllipse(b, kx, pad, kd, kd);
        }
    }

    // Clickable synthesis-type chip (text only).
    class TypeTile : Control
    {
        bool _sel;
        public string Caption = "";
        public event EventHandler SelectedChanged;
        public bool Selected { get { return _sel; } set { if (_sel != value) { _sel = value; Invalidate(); if (SelectedChanged != null) SelectedChanged(this, EventArgs.Empty); } } }
        public TypeTile()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand; Height = 36;
        }
        protected override void OnClick(EventArgs e) { if (Enabled) Selected = !Selected; base.OnClick(e); }
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float s = Theme.Scale(g);
            using (var b = new SolidBrush(Parent != null ? Parent.BackColor : Theme.CardBg)) g.FillRectangle(b, ClientRectangle);
            var r = new Rectangle(0, 0, Width, Height);
            Color fill = _sel ? Theme.TypeSelBg : Theme.FormBg;
            Color border = _sel ? Theme.Accent : Theme.CardBorder;
            Color text = _sel ? Theme.Accent : Theme.TextMuted;
            Theme.FillRound(g, r, (int)(8 * s), fill);
            Theme.DrawRoundBorder(g, r, (int)(8 * s), border, 1f * s);
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using (var f = Theme.F(9.5f, _sel ? FontStyle.Bold : FontStyle.Regular)) using (var b = new SolidBrush(text))
                g.DrawString(Caption, f, b, r, sf);
        }
    }

    // 10-segment rarity scale; click a segment to set the max grade.
    class SegmentBar : Control
    {
        int _value = 3;
        public int Value { get { return _value; } set { int v = Math.Max(0, Math.Min(9, value)); if (_value != v) { _value = v; Invalidate(); if (ValueChanged != null) ValueChanged(this, EventArgs.Empty); } } }
        public event EventHandler ValueChanged;
        public SegmentBar()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Height = 10; Cursor = Cursors.Hand;
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (Enabled) { int seg = (int)((float)e.X / Width * 10); Value = seg; }
            base.OnMouseDown(e);
        }
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float s = Theme.Scale(g);
            using (var b = new SolidBrush(Parent != null ? Parent.BackColor : Theme.CardBg)) g.FillRectangle(b, ClientRectangle);
            int n = 10; float gap = 3 * s; int rad = (int)(4 * s);
            float step = (Width + gap) / (float)n;
            float segW = step - gap;
            for (int i = 0; i < n; i++)
            {
                var r = new Rectangle((int)Math.Round(i * step), 0, (int)Math.Round(segW), Height);
                Color c = i <= _value ? Theme.GradeColors[i] : Theme.SegEmpty;
                bool left = i == 0, right = i == n - 1;
                using (var p = Theme.SidePath(r, rad, left, right)) using (var b = new SolidBrush(c)) g.FillPath(b, p);
            }
        }
    }

    // Rounded −/+ stepper.
    class Stepper : Control
    {
        public decimal Value = 5, Min = 1, Max = 1440, Step = 1;
        public int Decimals = 0;
        public event EventHandler ValueChanged;
        Rectangle _minus, _plus;
        public Stepper()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Height = 30;
        }
        public void SetValue(decimal v) { v = Math.Max(Min, Math.Min(Max, v)); if (v != Value) { Value = v; Invalidate(); if (ValueChanged != null) ValueChanged(this, EventArgs.Empty); } else { Value = v; Invalidate(); } }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (!Enabled) return;
            if (_minus.Contains(e.Location)) SetValue(Value - Step);
            else if (_plus.Contains(e.Location)) SetValue(Value + Step);
            base.OnMouseDown(e);
        }
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float s = Theme.Scale(g);
            using (var b = new SolidBrush(Parent != null ? Parent.BackColor : Theme.CardBg)) g.FillRectangle(b, ClientRectangle);
            var box = new Rectangle(0, 0, Width, Height);
            Theme.FillRound(g, box, (int)(8 * s), Theme.CardBg);
            Theme.DrawRoundBorder(g, box, (int)(8 * s), Theme.CardBorder, 1f);
            string val = Value.ToString(Decimals > 0 ? "0.0" : "0", System.Globalization.CultureInfo.InvariantCulture);
            using (var f = Theme.F(10.5f, FontStyle.Regular)) using (var b = new SolidBrush(Theme.TextDark))
                g.DrawString(val, f, b, new PointF(10 * s, (Height - f.Height) / 2f));
            int pad = (int)Math.Round(4 * s);
            int bs = Height - pad * 2;
            _plus = new Rectangle(Width - bs - pad, pad, bs, bs);
            _minus = new Rectangle(Width - bs * 2 - pad - (int)(2 * s), pad, bs, bs);
            DrawStepBtn(g, _minus, "−", s);
            DrawStepBtn(g, _plus, "+", s);
        }
        void DrawStepBtn(Graphics g, Rectangle r, string sym, float s)
        {
            Theme.FillRound(g, r, (int)(6 * s), Theme.StepBtnBg);
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using (var f = Theme.F(11f, FontStyle.Bold)) using (var b = new SolidBrush(Theme.TextMuted))
                g.DrawString(sym, f, b, r, sf);
        }
    }

    // Flat dropdown matching the settings window theme.
    class FlatDrop : Control
    {
        string[] _items = new string[0];
        int _sel;
        ContextMenuStrip _menu;
        public event EventHandler SelectedIndexChanged;
        public string[] Items
        {
            get { return _items; }
            set
            {
                _items = value ?? new string[0];
                if (_sel >= _items.Length) _sel = _items.Length > 0 ? 0 : -1;
                Invalidate();
            }
        }
        public int SelectedIndex
        {
            get { return _sel; }
            set
            {
                int v = _items.Length == 0 ? -1 : Math.Max(0, Math.Min(_items.Length - 1, value));
                if (_sel == v) return;
                _sel = v;
                Invalidate();
                if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
            }
        }
        public string SelectedText
        {
            get { return _sel >= 0 && _sel < _items.Length ? _items[_sel] : ""; }
        }
        public FlatDrop()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Height = 30; Cursor = Cursors.Hand;
            Disposed += delegate { if (_menu != null) { _menu.Dispose(); _menu = null; } };
        }
        protected override void OnClick(EventArgs e)
        {
            if (!Enabled || _items.Length == 0) { base.OnClick(e); return; }
            if (_menu != null) { _menu.Dispose(); _menu = null; }
            _menu = new ContextMenuStrip();
            _menu.Font = Theme.F(9.5f, FontStyle.Regular);
            _menu.ShowImageMargin = false;
            for (int i = 0; i < _items.Length; i++)
            {
                int idx = i;
                var item = new ToolStripMenuItem(_items[i]) { Checked = i == _sel };
                item.Click += delegate { SelectedIndex = idx; };
                _menu.Items.Add(item);
            }
            _menu.Show(this, new Point(0, Height));
            base.OnClick(e);
        }
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float s = Theme.Scale(g);
            using (var b = new SolidBrush(Parent != null ? Parent.BackColor : Theme.CardBg)) g.FillRectangle(b, ClientRectangle);
            var box = new Rectangle(0, 0, Width, Height);
            Theme.FillRound(g, box, (int)(8 * s), Enabled ? Theme.CardBg : Theme.StepBtnBg);
            Theme.DrawRoundBorder(g, box, (int)(8 * s), Theme.CardBorder, 1f);
            string text = SelectedText;
            using (var f = Theme.F(10f, FontStyle.Regular))
            using (var b = new SolidBrush(Enabled ? Theme.TextDark : Theme.TextMuted))
            {
                var tr = new RectangleF(10 * s, 0, Width - 28 * s, Height);
                var sf = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
                g.DrawString(text, f, b, tr, sf);
            }
            using (var f = Theme.F(8f, FontStyle.Regular))
            using (var b = new SolidBrush(Theme.TextMuted))
            {
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString("▾", f, b, new RectangleF(Width - 22 * s, 0, 18 * s, Height), sf);
            }
        }
    }

    // Flat rounded button (filled).
    class FlatButton : Control
    {
        public Color Fill = Theme.Accent;
        public Color TextColor = Color.White;
        public FlatButton()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand; Height = 36;
        }
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float s = Theme.Scale(g);
            using (var b = new SolidBrush(Parent != null ? Parent.BackColor : Theme.FormBg)) g.FillRectangle(b, ClientRectangle);
            var r = new Rectangle(0, 0, Width, Height);
            Theme.FillRound(g, r, (int)(8 * s), Enabled ? Fill : Theme.ToggleOff);
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using (var f = Theme.F(10f, FontStyle.Bold)) using (var b = new SolidBrush(Enabled ? TextColor : Theme.TextMuted))
                g.DrawString(Text, f, b, r, sf);
        }
    }

    // Live status cards for the side panel (Presence / Synth).
    class LiveStrip : Control
    {
        struct Row
        {
            public string Title, Value, Sub, State;
            public Color StateColor;
        }

        readonly Row[] _rows = new Row[3];
        public int Columns = 1;

        public LiveStrip()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        public void SetRow(int i, string title, string value, string sub, string state, Color stateColor)
        {
            if (i < 0 || i >= _rows.Length) return;
            _rows[i].Title = title;
            _rows[i].Value = value;
            _rows[i].Sub = sub;
            _rows[i].State = state;
            _rows[i].StateColor = stateColor;
            Invalidate();
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float s = Theme.Scale(g);
            Color bg = Parent != null ? Parent.BackColor : Theme.SideBg;
            using (var b = new SolidBrush(bg)) g.FillRectangle(b, ClientRectangle);

            int n = Math.Max(1, Math.Min(3, Columns));
            float gap = 8 * s;
            float rowH = (Height - gap * (n - 1)) / n;
            int rad = (int)(8 * s);

            for (int i = 0; i < n; i++)
            {
                float y = i * (rowH + gap);
                var card = new Rectangle(0, (int)Math.Round(y), Width, (int)Math.Round(rowH));
                Theme.FillRound(g, card, rad, Theme.StatusCard);

                float pad = 10 * s;
                float tx = pad;
                float ty = card.Y + 8 * s;

                using (var f = Theme.F(7.5f, FontStyle.Bold)) using (var b = new SolidBrush(Theme.TextMuted))
                    g.DrawString(_rows[i].Title ?? "", f, b, new PointF(tx, ty));

                string state = _rows[i].State ?? "";
                if (state.Length > 0)
                {
                    using (var f = Theme.F(7.5f, FontStyle.Bold))
                    {
                        var sz = g.MeasureString(state, f);
                        float bw = sz.Width + 10 * s;
                        float bh = Math.Max(16 * s, sz.Height + 2 * s);
                        var badge = new Rectangle(
                            (int)(Width - pad - bw),
                            (int)(card.Y + 7 * s),
                            (int)bw, (int)bh);
                        Color fill = Color.FromArgb(28, _rows[i].StateColor);
                        Theme.FillRound(g, badge, (int)(bh / 2), fill);
                        using (var b = new SolidBrush(_rows[i].StateColor))
                        {
                            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                            g.DrawString(state, f, b, badge, sf);
                        }
                    }
                }

                using (var f = Theme.F(10.5f, FontStyle.Bold)) using (var b = new SolidBrush(Theme.TextDark))
                    g.DrawString(_rows[i].Value ?? "", f, b, new PointF(tx, card.Y + 26 * s));

                if (!string.IsNullOrEmpty(_rows[i].Sub))
                {
                    using (var f = Theme.F(8f, FontStyle.Regular)) using (var b = new SolidBrush(Theme.TextMuted))
                        g.DrawString(_rows[i].Sub, f, b, new PointF(tx, card.Y + 46 * s));
                }
            }
        }
    }
}
