// Minimal floating performance pill widget: dynamic DPI/text-fit sizing,
// clean Normal vs Gaming state indicator, collapsed pill mode, and zero-emoji vector controls.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace PCTune
{
    class Widget : Form
    {
        readonly Engine eng;
        readonly Action<Action> runAction;
        readonly Func<bool> isBusy;
        readonly Action openMain;
        readonly Action onHide;
        readonly Timer tick;
        ContextMenuStrip ctxMenu;

        bool collapsed;
        bool dragging, movedDuringDrag;
        Point grabScreen, startFormPos;

        int cpu, gpu, ram;
        long lastIdle, lastKernel, lastUser;

        Rectangle rcCollapse, rcBoost, rcMenu, rcClose;
        int hoverZone; // 0=none, 1=collapse, 2=boost, 3=menu, 4=close

        public Widget(Engine e, Action<Action> runner, Func<bool> busyCheck, Action onOpenMain, Action onHideWidget)
        {
            eng = e;
            runAction = runner;
            isBusy = busyCheck;
            openMain = onOpenMain;
            onHide = onHideWidget;
            collapsed = Cfg.WidgetCollapsed;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(18, 20, 25);
            DoubleBuffered = true;

            ApplySizeAndRegion();
            Location = Cfg.ClampWidgetLocation(Width, Height);

            tick = new Timer { Interval = 2500 };
            tick.Tick += delegate { Sample(); };
            Sample();
            if (Visible) tick.Start();
        }

        public void SetCollapsed(bool c)
        {
            if (collapsed == c) return;
            collapsed = c;
            Cfg.WidgetCollapsed = c;
            ApplySizeAndRegion();
            Location = Cfg.ClampWidgetLocation(Width, Height);
            Invalidate();
            if (onHide != null) onHide();
        }

        void ApplySizeAndRegion()
        {
            if (collapsed)
            {
                using (var f = new Font(Ui.Body, 8.5f, FontStyle.Bold))
                {
                    int tw = TextRenderer.MeasureText("CPU 100%", f).Width;
                    Size = new Size(Math.Max(Ui.D(92), tw + Ui.D(34)), Ui.D(30));
                }
            }
            else
            {
                using (var fm = new Font(Ui.Body, 8.5f))
                using (var fs = new Font(Ui.Tiny, 8.25f, FontStyle.Bold))
                {
                    int mw = TextRenderer.MeasureText("CPU 100%   GPU 100%   RAM 100%", fm).Width;
                    int sw = TextRenderer.MeasureText("Gaming", fs).Width + Ui.D(26);
                    int actionsW = Ui.D(60) + Ui.D(26) + Ui.D(24) + Ui.D(16);
                    int totalW = Math.Max(Ui.D(384), sw + mw + actionsW + Ui.D(24));
                    int h = Ui.D(36);
                    Size = new Size(totalW, h);

                    rcClose = new Rectangle(Width - Ui.D(28), Ui.D(4), Ui.D(24), h - Ui.D(8));
                    rcMenu = new Rectangle(rcClose.Left - Ui.D(28), Ui.D(4), Ui.D(26), h - Ui.D(8));
                    rcBoost = new Rectangle(rcMenu.Left - Ui.D(64), Ui.D(5), Ui.D(60), h - Ui.D(10));
                    rcCollapse = new Rectangle(Ui.D(6), Ui.D(4), sw, h - Ui.D(8));
                }
            }
            try
            {
                using (var path = Ui.Round(new Rectangle(0, 0, Width, Height), Ui.D(10)))
                    Region = new Region(path);
            }
            catch { }
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible)
            {
                collapsed = Cfg.WidgetCollapsed;
                ApplySizeAndRegion();
                Location = Cfg.ClampWidgetLocation(Width, Height);
                Sample();
                tick.Start();
            }
            else
            {
                tick.Stop();
            }
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            try
            {
                Ui.UseDpi(e.DeviceDpiNew);
                ApplySizeAndRegion();
                Location = Cfg.ClampWidgetLocation(Width, Height);
                Invalidate();
            }
            catch { }
            base.OnDpiChanged(e);
        }

        int HitZone(Point pt)
        {
            if (collapsed) return 1;
            if (rcClose.Contains(pt)) return 4;
            if (rcMenu.Contains(pt)) return 3;
            if (rcBoost.Contains(pt)) return 2;
            if (rcCollapse.Contains(pt)) return 1;
            return 0;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Right)
            {
                ShowContextMenu(e.Location);
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            dragging = true;
            movedDuringDrag = false;
            grabScreen = Control.MousePosition;
            startFormPos = Location;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging)
            {
                Point cur = Control.MousePosition;
                int dx = cur.X - grabScreen.X;
                int dy = cur.Y - grabScreen.Y;
                if (Math.Abs(dx) > 3 || Math.Abs(dy) > 3) movedDuringDrag = true;
                if (movedDuringDrag)
                    Location = new Point(startFormPos.X + dx, startFormPos.Y + dy);
                return;
            }
            int z = HitZone(e.Location);
            if (z != hoverZone)
            {
                hoverZone = z;
                Cursor = z > 0 ? Cursors.Hand : Cursors.SizeAll;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hoverZone != 0) { hoverZone = 0; Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            bool wasMoved = movedDuringDrag;
            dragging = false;
            movedDuringDrag = false;

            if (wasMoved)
            {
                Cfg.WidgetX = Location.X;
                Cfg.WidgetY = Location.Y;
                Location = Cfg.ClampWidgetLocation(Width, Height);
                Cfg.WidgetX = Location.X;
                Cfg.WidgetY = Location.Y;
                return;
            }

            if (collapsed)
            {
                SetCollapsed(false);
                return;
            }

            int z = HitZone(e.Location);
            if (z == 4) HideWidget();
            else if (z == 3) ShowContextMenu(new Point(rcMenu.Left, Height));
            else if (z == 2) TriggerBoost();
            else if (z == 1) SetCollapsed(true);
        }

        void HideWidget()
        {
            Cfg.WidgetOn = false;
            Hide();
            if (onHide != null) onHide();
        }

        void TriggerBoost()
        {
            if (isBusy != null && isBusy()) return;
            if (runAction != null)
            {
                runAction(delegate
                {
                    if (GameMode.Active) GameMode.Leave(eng, eng.Echo);
                    else GameMode.Enter(eng, eng.Echo);
                });
            }
        }

        void RunProfile(Action act)
        {
            if (isBusy != null && isBusy()) return;
            if (runAction != null) runAction(act);
        }

        void ShowContextMenu(Point pt)
        {
            if (ctxMenu == null || ctxMenu.IsDisposed)
            {
                ctxMenu = new ContextMenuStrip
                {
                    Renderer = new DarkMenu(),
                    BackColor = Ui.Panel,
                    ForeColor = Ui.Text,
                    Font = new Font(Ui.Body, 9f),
                    ShowImageMargin = false
                };
            }
            ctxMenu.Items.Clear();

            bool active = GameMode.Active;
            var itBoost = new ToolStripMenuItem(active ? "Return to Normal" : "Prepare for Game");
            itBoost.ForeColor = active ? Ui.Green : Ui.Accent;
            itBoost.Click += delegate { TriggerBoost(); };
            ctxMenu.Items.Add(itBoost);
            ctxMenu.Items.Add(new ToolStripSeparator());

            AddModeItem(ctxMenu, "Mode: Balanced", "bal", delegate { eng.RunBalanced(); });
            AddModeItem(ctxMenu, "Mode: Gaming", "game", delegate { eng.RunGaming(); });
            AddModeItem(ctxMenu, "Mode: Competitive", "comp", delegate { eng.RunCompetitive(); });
            AddModeItem(ctxMenu, "Mode: Max Speed", "perf", delegate { eng.RunPerf(); });
            AddModeItem(ctxMenu, "Mode: Economy", "eco", delegate { eng.RunEco(); });
            AddModeItem(ctxMenu, "Mode: In the Car", "car", delegate { eng.RunCar(); });

            ctxMenu.Items.Add(new ToolStripSeparator());
            var itCol = new ToolStripMenuItem(collapsed ? "Expand Widget" : "Collapse Widget");
            itCol.ForeColor = Ui.Text;
            itCol.Click += delegate { SetCollapsed(!collapsed); };
            ctxMenu.Items.Add(itCol);

            var itOpen = new ToolStripMenuItem("Open PC-Tune");
            itOpen.ForeColor = Ui.Text;
            itOpen.Click += delegate { if (openMain != null) openMain(); };
            ctxMenu.Items.Add(itOpen);

            var itHide = new ToolStripMenuItem("Hide Widget");
            itHide.ForeColor = Ui.Dim;
            itHide.Click += delegate { HideWidget(); };
            ctxMenu.Items.Add(itHide);

            ctxMenu.Show(this, pt);
        }

        void AddModeItem(ContextMenuStrip m, string text, string id, Action act)
        {
            string cur = string.IsNullOrEmpty(Cfg.Profile) ? "bal" : Cfg.Profile;
            var it = new ToolStripMenuItem(text);
            it.ForeColor = cur == id ? Ui.Accent : Ui.Text;
            it.Click += delegate { RunProfile(act); };
            m.Items.Add(it);
        }

        void Sample()
        {
            if (!Visible) return;
            int prevCpu = cpu, prevGpu = gpu, prevRam = ram;
            long i, k, u;
            if (Sys.GetSystemTimes(out i, out k, out u))
            {
                long di = i - lastIdle, tot = (k - lastKernel) + (u - lastUser);
                if (lastKernel != 0 && tot > 0) cpu = Math.Max(0, Math.Min(100, (int)(100.0 * (tot - di) / tot)));
                lastIdle = i; lastKernel = k; lastUser = u;
            }
            if (!collapsed)
            {
                gpu = Sys.SampleGpuPercent();
                ram = Ram.State().Percent;
            }
            if (cpu != prevCpu || gpu != prevGpu || ram != prevRam)
                Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            bool isGaming = GameMode.Active || Cfg.Profile == "game" || Cfg.Profile == "comp";
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Ui.Round(r, Ui.D(10)))
            {
                using (var b = new SolidBrush(Color.FromArgb(19, 21, 26)))
                    g.FillPath(b, path);
                using (var p = new Pen(GameMode.Active ? Color.FromArgb(90, Ui.Green) : Color.FromArgb(42, 46, 56), 1f))
                    g.DrawPath(p, path);
            }

            Color dotCol = GameMode.Active ? Ui.Green : isGaming ? Ui.Accent : Color.FromArgb(110, 116, 128);

            if (collapsed)
            {
                int dy = (Height - Ui.D(6)) / 2;
                using (var b = new SolidBrush(dotCol))
                    g.FillEllipse(b, Ui.D(11), dy, Ui.D(6), Ui.D(6));
                using (var f = new Font(Ui.Body, 8.5f, FontStyle.Bold))
                    TextRenderer.DrawText(g, "CPU " + cpu + "%", f,
                        new Rectangle(Ui.D(22), 0, Width - Ui.D(26), Height),
                        Ui.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                return;
            }

            // 1. Left state indicator (click to collapse)
            if (hoverZone == 1)
            {
                using (var p = Ui.Round(rcCollapse, Ui.D(6)))
                using (var b = new SolidBrush(Color.FromArgb(28, 31, 39)))
                    g.FillPath(b, p);
            }
            int dotY = (Height - Ui.D(6)) / 2;
            using (var b = new SolidBrush(dotCol))
                g.FillEllipse(b, Ui.D(12), dotY, Ui.D(6), Ui.D(6));

            string stateLabel = GameMode.Active ? "Gaming" : (isGaming ? "Gaming" : "Normal");
            using (var f = new Font(Ui.Tiny, 8.25f, FontStyle.Bold))
                TextRenderer.DrawText(g, stateLabel, f,
                    new Rectangle(Ui.D(22), 0, rcCollapse.Width - Ui.D(18), Height),
                    GameMode.Active ? Ui.Green : Ui.Dim,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            // 2. Center metrics: CPU  GPU  RAM
            string metrics = string.Format("CPU {0}%   GPU {1}%   RAM {2}%", cpu, gpu, ram);
            var rcMetrics = new Rectangle(rcCollapse.Right + Ui.D(4), 0, Math.Max(Ui.D(40), rcBoost.Left - rcCollapse.Right - Ui.D(8)), Height);
            using (var f = new Font(Ui.Body, 8.5f))
                TextRenderer.DrawText(g, metrics, f, rcMetrics, Ui.Text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

            // 3. Primary action button: Boost / Normal
            bool active = GameMode.Active;
            bool busyNow = isBusy != null && isBusy();
            Color boostBg = active
                ? (hoverZone == 2 ? Color.FromArgb(46, 168, 114) : Color.FromArgb(34, 134, 90))
                : (hoverZone == 2 ? Color.FromArgb(64, 124, 214) : Color.FromArgb(34, 38, 48));
            using (var p = Ui.Round(rcBoost, Ui.D(6)))
            using (var b = new SolidBrush(boostBg))
                g.FillPath(b, p);

            using (var f = new Font(Ui.Tiny, 8f, FontStyle.Bold))
                TextRenderer.DrawText(g, busyNow ? "..." : (active ? "Normal" : "Boost"), f,
                    rcBoost, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            // 4. Menu button: •••
            if (hoverZone == 3)
            {
                using (var p = Ui.Round(rcMenu, Ui.D(6)))
                using (var b = new SolidBrush(Color.FromArgb(30, 34, 42)))
                    g.FillPath(b, p);
            }
            using (var f = new Font(Ui.Body, 8.25f, FontStyle.Bold))
                TextRenderer.DrawText(g, "•••", f, rcMenu, hoverZone == 3 ? Ui.Text : Ui.Dim,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            // 5. Hide button: ×
            if (hoverZone == 4)
            {
                using (var p = Ui.Round(rcClose, Ui.D(6)))
                using (var b = new SolidBrush(Color.FromArgb(42, 32, 36)))
                    g.FillPath(b, p);
            }
            using (var f = new Font(Ui.Body, 10f))
                TextRenderer.DrawText(g, "×", f, rcClose, hoverZone == 4 ? Ui.Text : Ui.Dim,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideWidget();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool d)
        {
            if (d)
            {
                if (tick != null) { tick.Stop(); tick.Dispose(); }
                if (ctxMenu != null) { ctxMenu.Dispose(); ctxMenu = null; }
            }
            base.Dispose(d);
        }
    }
}
