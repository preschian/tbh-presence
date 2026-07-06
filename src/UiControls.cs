using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace TbhCompanion
{
    // Parchment theme + custom-painted controls to match the TBH Companion design.
    static class Theme
    {
        public static readonly Color FormBg      = Hex("faf8f4");
        public static readonly Color TitleTop    = Hex("fdfcf9");
        public static readonly Color TitleBottom = Hex("f6f2ea");
        public static readonly Color CardBg      = Hex("ffffff");
        public static readonly Color CardBorder  = Hex("e2dcd0");
        public static readonly Color TextDark    = Hex("2b241a");
        public static readonly Color TextMuted   = Hex("968971");
        public static readonly Color Brown       = Hex("8a5a2b");
        public static readonly Color Terracotta  = Hex("a5442e");
        public static readonly Color Green       = Hex("4e8c3a");
        public static readonly Color Blue        = Hex("2a6fdb");
        public static readonly Color BadgeBg     = Hex("eef3ea");
        public static readonly Color BadgeBorder = Hex("cfe0c6");
        public static readonly Color BadgeText   = Hex("3e6631");
        public static readonly Color Divider     = Hex("efe9dd");
        public static readonly Color StepBtnBg   = Hex("f3efe7");
        public static readonly Color TypeSelBg   = Hex("fdf2ef");
        public static readonly Color ToggleOff   = Hex("d9d2c4");
        public static readonly Color SegEmpty    = Hex("e6dfd2");
        public static readonly Color Amber       = Hex("b8860b");

        public static readonly Color[] GradeColors =
        {
            Hex("9aa0a6"), Hex("4e8c3a"), Hex("2a6fdb"), Hex("b8860b"), Hex("b03a3a"),
            Hex("7a4bbf"), Hex("c26a1f"), Hex("2a9d8f"), Hex("c94f7c"), Hex("3a3a8c")
        };

        static string _serif;
        // Prefer a serif close to the design's Alegreya; fall back to what Windows has.
        public static string Serif
        {
            get
            {
                if (_serif == null)
                {
                    _serif = "Segoe UI";
                    foreach (var name in new[] { "Alegreya", "Cambria", "Georgia", "Constantia" })
                        if (FontInstalled(name)) { _serif = name; break; }
                }
                return _serif;
            }
        }

        public static Font F(float size, FontStyle style) { return new Font("Segoe UI", size, style); }
        public static Font FSerif(float size, FontStyle style) { return new Font(Serif, size, style); }

        // DPI scale for a paint surface (1.0 at 96 dpi, 1.25 at 125%, ...).
        public static float Scale(Graphics g) { return g.DpiX / 96f; }
        public static float ScaleOf(Control c) { return c.DeviceDpi / 96f; }

        static bool FontInstalled(string name)
        {
            try { using (var f = new Font(name, 10f)) return string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

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

    // Rounded card background with 1px border.
    class Card : Panel
    {
        public int Radius = 12;
        public Color Border = Theme.CardBorder;
        public float BorderWidth = 1f;
        public Card()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Theme.CardBg;
        }
        protected override void OnPaintBackground(PaintEventArgs e) { /* painted in OnPaint */ }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float s = Theme.Scale(g);
            var r = ClientRectangle;
            using (var b = new SolidBrush(Parent != null ? Parent.BackColor : Theme.FormBg)) g.FillRectangle(b, r);
            Theme.FillRound(g, r, (int)(Radius * s), BackColor);
            Theme.DrawRoundBorder(g, r, (int)(Radius * s), Border, BorderWidth * s);
            base.OnPaint(e);
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
            Theme.FillRound(g, track, h / 2, Enabled ? (_on ? Theme.Green : Theme.ToggleOff) : Theme.ToggleOff);
            int pad = (int)Math.Round(3 * s);
            int kd = h - pad * 2;
            int kx = _on ? w - kd - pad : pad;
            using (var b = new SolidBrush(Color.White))
                g.FillEllipse(b, kx, pad, kd, kd);
        }
    }

    // Clickable synthesis-type tile with icon + label.
    class TypeTile : Control
    {
        bool _sel;
        public string Icon = "";
        public string Caption = "";
        public event EventHandler SelectedChanged;
        public bool Selected { get { return _sel; } set { if (_sel != value) { _sel = value; Invalidate(); if (SelectedChanged != null) SelectedChanged(this, EventArgs.Empty); } } }
        public TypeTile()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand; Height = 52;
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
            Color border = _sel ? Theme.Terracotta : Theme.CardBorder;
            Color text = _sel ? Theme.Terracotta : Theme.TextMuted;
            Theme.FillRound(g, r, (int)(9 * s), fill);
            Theme.DrawRoundBorder(g, r, (int)(9 * s), border, (_sel ? 2f : 1f) * s);
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using (var f = Theme.F(14f, FontStyle.Regular)) using (var b = new SolidBrush(text))
                g.DrawString(Icon, f, b, new RectangleF(0, 6 * s, Width, 22 * s), sf);
            using (var f = Theme.F(9.5f, _sel ? FontStyle.Bold : FontStyle.Regular)) using (var b = new SolidBrush(text))
                g.DrawString(Caption, f, b, new RectangleF(0, 28 * s, Width, 20 * s), sf);
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
            Height = 12; Cursor = Cursors.Hand;
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
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float s = Theme.Scale(g);
            using (var b = new SolidBrush(Parent != null ? Parent.BackColor : Theme.CardBg)) g.FillRectangle(b, ClientRectangle);
            int n = 10; float gap = 4 * s; int rad = (int)(5 * s);
            float step = (Width + gap) / (float)n;      // segment pitch incl. gap
            float segW = step - gap;
            for (int i = 0; i < n; i++)
            {
                var r = new Rectangle((int)Math.Round(i * step), 0, (int)Math.Round(segW), Height);
                Color c = i <= _value ? Theme.GradeColors[i] : Theme.SegEmpty;
                bool left = i == 0, right = i == n - 1;
                using (var p = Theme.SidePath(r, rad, left, right)) using (var b = new SolidBrush(c)) g.FillPath(b, p);
                if (i == _value)
                    using (var p = Theme.SidePath(r, rad, left, right)) using (var pen = new Pen(Theme.TextDark, 2f * s))
                        g.DrawPath(pen, p);
            }
        }
    }

    // Rounded −/+ stepper with a caption above.
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
            using (var f = Theme.F(11f, FontStyle.Bold)) using (var b = new SolidBrush(Theme.Brown))
                g.DrawString(sym, f, b, r, sf);
        }
    }

    // Pill status badge (dot + text) for the title bar.
    class PillBadge : Control
    {
        Color _dot = Theme.Green;
        string _text = "";
        public void Set(string text, Color dot) { _text = text; _dot = dot; Invalidate(); }
        public PillBadge()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Height = 26;
        }
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float s = Theme.Scale(g);
            using (var b = new SolidBrush(Parent != null ? Parent.BackColor : Theme.TitleBottom)) g.FillRectangle(b, ClientRectangle);
            var r = new Rectangle(0, 0, Width, Height);
            Theme.FillRound(g, r, Height / 2, Theme.BadgeBg);
            Theme.DrawRoundBorder(g, r, Height / 2, Theme.BadgeBorder, 1f);
            int dd = (int)Math.Round(8 * s);
            using (var b = new SolidBrush(_dot)) g.FillEllipse(b, (int)(11 * s), (Height - dd) / 2, dd, dd);
            using (var f = Theme.F(9f, FontStyle.Regular)) using (var b = new SolidBrush(Theme.BadgeText))
                g.DrawString(_text, f, b, new PointF(22 * s, (Height - f.Height) / 2f));
        }
    }

    // Flat rounded button (filled).
    class FlatButton : Control
    {
        public Color Fill = Theme.Terracotta;
        public Color TextColor = Color.FromArgb(253, 246, 240);
        public FlatButton()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand; Height = 38;
        }
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float s = Theme.Scale(g);
            using (var b = new SolidBrush(Parent != null ? Parent.BackColor : Theme.FormBg)) g.FillRectangle(b, ClientRectangle);
            var r = new Rectangle(0, 0, Width, Height);
            Theme.FillRound(g, r, (int)(10 * s), Enabled ? Fill : Theme.ToggleOff);
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using (var f = Theme.F(10.5f, FontStyle.Bold)) using (var b = new SolidBrush(TextColor))
                g.DrawString(Text, f, b, r, sf);
        }
    }
}
