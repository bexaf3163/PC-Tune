// Standardized lightweight design-system controls: Toggle, NavItem, MetricCard, Btn, and Picker.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace PCTune
{
    class Toggle : Control
    {
        bool on, hot;
        float pos;
        readonly Timer anim;

        public event EventHandler Toggled;

        public Toggle()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                   | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                   | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Size = new Size(Ui.D(42), Ui.D(22));
            Cursor = Cursors.Hand;
            TabStop = false;
            anim = new Timer { Interval = 15 };
            anim.Tick += Step;
        }

        public bool On
        {
            get { return on; }
            set
            {
                if (on == value) return;
                on = value;
                anim.Start();
            }
        }

        public void Init(bool v) { on = v; pos = v ? 1f : 0f; Invalidate(); }

        void Step(object s, EventArgs e)
        {
            float want = on ? 1f : 0f;
            pos += (want - pos) * 0.35f;
            if (Math.Abs(want - pos) < 0.01f) { pos = want; anim.Stop(); }
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnClick(EventArgs e)
        {
            if (!Enabled) return;
            On = !on;
            var h = Toggled;
            if (h != null) h(this, EventArgs.Empty);
            base.OnClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int h = Height - Ui.D(2), y = Ui.D(1);
            var track = new Rectangle(0, y, Width - 1, h);
            Color offCol = hot ? Color.FromArgb(62, 66, 78) : Color.FromArgb(48, 52, 62);
            Color onCol = Enabled ? Ui.Accent : Color.FromArgb(62, 66, 78);
            Color fill = Blend(offCol, onCol, pos);
            if (!Enabled) fill = Color.FromArgb(36, 38, 45);

            using (var path = Ui.Round(track, h / 2))
            using (var b = new SolidBrush(fill))
                g.FillPath(b, path);

            int d = h - Ui.D(6);
            float x = Ui.D(3) + pos * (Width - 1 - d - Ui.D(6));
            using (var b = new SolidBrush(Enabled ? Color.White : Color.FromArgb(130, 134, 144)))
                g.FillEllipse(b, x, y + Ui.D(3), d, d);
        }

        static Color Blend(Color a, Color b, float t)
        {
            if (t < 0) t = 0; if (t > 1) t = 1;
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        protected override void Dispose(bool d)
        {
            if (d && anim != null) { anim.Stop(); anim.Dispose(); }
            base.Dispose(d);
        }
    }

    class NavItem : Control
    {
        public string Glyph = "", Label = "";
        bool hot, sel;

        public NavItem()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                   | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                   | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            Height = Ui.D(38);
            TabStop = false;
        }

        public bool Selected
        {
            get { return sel; }
            set { if (sel != value) { sel = value; Invalidate(); } }
        }

        protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using (var b = new SolidBrush(Ui.Side)) g.FillRectangle(b, ClientRectangle);

            var r = new Rectangle(Ui.D(10), Ui.D(2), Width - Ui.D(20), Height - Ui.D(4));
            if (sel || hot)
            {
                using (var path = Ui.Round(r, Ui.D(8)))
                using (var b = new SolidBrush(sel ? Color.FromArgb(32, 36, 45) : Color.FromArgb(25, 28, 35)))
                    g.FillPath(b, path);
            }
            if (sel)
            {
                using (var path = Ui.Round(new Rectangle(Ui.D(10), Height / 2 - Ui.D(8), Ui.D(3), Ui.D(16)), Ui.D(2)))
                using (var b = new SolidBrush(Ui.Accent))
                    g.FillPath(b, path);
            }

            int tx = Ui.D(22);
            Color fg = sel ? Ui.Text : (hot ? Ui.Text : Ui.Dim);
            using (var f = new Font(Ui.Body, 9.75f, sel ? FontStyle.Bold : FontStyle.Regular))
                TextRenderer.DrawText(g, Label, f, new Rectangle(tx, 0, Math.Max(Ui.D(20), Width - tx - Ui.D(14)), Height),
                    fg, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    class MetricCard : Control
    {
        public string Caption = "", ValueText = "0%", SubText = "";
        public int Percent;
        public Color Tone = Ui.Accent;

        public MetricCard()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                   | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Bg;
            TabStop = false;
        }

        public void Set(int pct, string val, string sub)
        {
            int p = pct < 0 ? 0 : pct > 100 ? 100 : pct;
            if (Percent == p && ValueText == val && SubText == sub) return;
            Percent = p;
            ValueText = val ?? (p + "%");
            SubText = sub ?? "";
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using (var bg = new SolidBrush(Ui.Bg)) g.FillRectangle(bg, ClientRectangle);

            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Ui.Round(r, Ui.D(12)))
            {
                using (var b = new SolidBrush(Ui.Panel)) g.FillPath(b, path);
                using (var p = new Pen(Ui.Line)) g.DrawPath(p, path);
            }

            int pad = Ui.D(16);
            int w = Math.Max(Ui.D(30), Width - pad * 2);
            var flags = TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;

            using (var fc = new Font(Ui.Tiny, 8.5f, FontStyle.Bold))
                TextRenderer.DrawText(g, Caption, fc, new Rectangle(pad, Ui.D(14), w, Ui.D(16)), Ui.Dim, flags);

            using (var fv = new Font(Ui.Disp, 16f, FontStyle.Bold))
                TextRenderer.DrawText(g, ValueText, fv, new Rectangle(pad, Ui.D(32), w, Ui.D(28)), Ui.Text, flags);

            if (!string.IsNullOrEmpty(SubText))
            {
                using (var fs = new Font(Ui.Tiny, 8.25f))
                    TextRenderer.DrawText(g, SubText, fs, new Rectangle(pad, Ui.D(62), w, Ui.D(16)), Ui.Dim, flags);
            }

            int barY = Height - Ui.D(14);
            var barTrack = new Rectangle(pad, barY, w, Ui.D(4));
            using (var path = Ui.Round(barTrack, Ui.D(2)))
            using (var b = new SolidBrush(Color.FromArgb(38, 42, 52)))
                g.FillPath(b, path);

            int fillW = (int)Math.Round(w * (Percent / 100.0));
            if (fillW > Ui.D(3))
            {
                using (var path = Ui.Round(new Rectangle(pad, barY, fillW, Ui.D(4)), Ui.D(2)))
                using (var b = new SolidBrush(Tone))
                    g.FillPath(b, path);
            }
        }
    }

    class Btn : Control
    {
        public string Text2 = "", Glyph = "";
        public Color Tone = Ui.Accent;
        public bool Filled;
        public bool Subtle;
        bool hot, down;

        public Btn()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                   | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                   | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            Height = Ui.D(34);
            TabStop = false;
        }

        protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hot = down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { hot = down = false; Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            Color fill, fg;

            if (Filled)
            {
                fill = !Enabled ? Color.FromArgb(42, 45, 54)
                     : down ? Dark(Tone, 20) : hot ? Light(Tone, 14) : Tone;
                fg = Enabled ? Color.FromArgb(14, 16, 20) : Color.FromArgb(100, 104, 114);
            }
            else if (Subtle)
            {
                fill = hot && Enabled ? Color.FromArgb(28, 31, 39) : Color.Transparent;
                fg = !Enabled ? Color.FromArgb(90, 94, 104) : hot ? Ui.Text : Ui.Dim;
            }
            else
            {
                fill = !Enabled ? Color.FromArgb(22, 24, 30)
                     : down ? Color.FromArgb(30, 33, 41) : hot ? Ui.PanelHi : Ui.Panel;
                fg = Enabled ? Ui.Text : Color.FromArgb(96, 100, 110);
            }

            int rad = Math.Min(Ui.D(8), Height / 2);
            using (var path = Ui.Round(r, rad))
            {
                if (fill != Color.Transparent)
                    using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                if (!Filled && !Subtle)
                    using (var p = new Pen(hot && Enabled ? Color.FromArgb(130, Tone) : Ui.Line))
                        g.DrawPath(p, path);
            }

            float fs = Text2.Length <= 1 ? 12f : 9.25f;
            using (var f = new Font(Ui.Body, fs, Filled ? FontStyle.Bold : FontStyle.Regular))
            {
                int pad = Text2.Length <= 2 ? Ui.D(2) : Ui.D(10);
                var textRect = new Rectangle(pad, 0, Math.Max(Ui.D(8), Width - pad * 2), Height);
                TextRenderer.DrawText(g, Text2, f, textRect, fg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
        }

        static Color Light(Color c, int d)
        {
            return Color.FromArgb(Math.Min(255, c.R + d), Math.Min(255, c.G + d), Math.Min(255, c.B + d));
        }
        static Color Dark(Color c, int d)
        {
            return Color.FromArgb(Math.Max(0, c.R - d), Math.Max(0, c.G - d), Math.Max(0, c.B - d));
        }
    }

    class Picker : Control
    {
        public readonly List<string> Items = new List<string>();
        int sel = -1;
        bool hot;
        ContextMenuStrip dropMenu;

        public event EventHandler Changed;

        public Picker()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                   | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                   | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            Height = Ui.D(30);
            TabStop = false;
        }

        public int SelectedIndex
        {
            get { return sel; }
            set
            {
                int v = value < -1 ? -1 : value >= Items.Count ? Items.Count - 1 : value;
                if (v == sel) return;
                sel = v;
                Invalidate();
            }
        }

        public void Init(int i)
        {
            sel = i < -1 ? -1 : i >= Items.Count ? Items.Count - 1 : i;
            Invalidate();
        }

        public string SelectedText
        {
            get { return sel >= 0 && sel < Items.Count ? Items[sel] : ""; }
        }

        protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            if (!Enabled || Items.Count == 0) return;

            int minW = Width;
            using (var f = new Font(Ui.Body, 9.25f))
            {
                foreach (string s in Items)
                {
                    int w = TextRenderer.MeasureText(s, f).Width + Ui.D(36);
                    if (w > minW) minW = w;
                }
            }

            if (dropMenu == null || dropMenu.IsDisposed)
            {
                dropMenu = new ContextMenuStrip
                {
                    Renderer = new DarkMenu(),
                    BackColor = Ui.Panel,
                    ForeColor = Ui.Text,
                    Font = new Font(Ui.Body, 9.25f),
                    ShowImageMargin = false
                };
            }
            dropMenu.MinimumSize = new Size(minW, 0);
            dropMenu.Items.Clear();

            for (int i = 0; i < Items.Count; i++)
            {
                int idx = i;
                var it = new ToolStripMenuItem(Items[i]);
                it.ForeColor = Ui.Text;
                it.Checked = i == sel;
                it.Click += delegate
                {
                    if (idx == sel) return;
                    sel = idx;
                    Invalidate();
                    var h = Changed;
                    if (h != null) h(this, EventArgs.Empty);
                };
                dropMenu.Items.Add(it);
            }
            dropMenu.Show(this, new Point(0, Height + Ui.D(2)));
        }

        protected override void Dispose(bool d)
        {
            if (d && dropMenu != null) { dropMenu.Dispose(); dropMenu = null; }
            base.Dispose(d);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Ui.Round(r, Ui.D(8)))
            {
                using (var b = new SolidBrush(Enabled ? (hot ? Ui.PanelHi : Color.FromArgb(30, 33, 41))
                                                      : Color.FromArgb(22, 24, 30)))
                    g.FillPath(b, path);
                using (var p = new Pen(Enabled && hot ? Color.FromArgb(140, Ui.Accent) : Ui.Line))
                    g.DrawPath(p, path);
            }

            Color fg = Enabled ? Ui.Text : Color.FromArgb(100, 104, 114);
            using (var f = new Font(Ui.Body, 9.25f))
                TextRenderer.DrawText(g, SelectedText, f, new Rectangle(Ui.D(10), 0, Math.Max(Ui.D(20), Width - Ui.D(32)), Height), fg,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            int cx = Width - Ui.D(16), cy = Height / 2 - Ui.D(1);
            using (var p = new Pen(fg, 1.5f))
            {
                p.StartCap = p.EndCap = LineCap.Round;
                g.DrawLines(p, new[]
                {
                    new Point(cx - Ui.D(4), cy - Ui.D(1)), new Point(cx, cy + Ui.D(3)), new Point(cx + Ui.D(4), cy - Ui.D(1))
                });
            }
        }
    }
}
