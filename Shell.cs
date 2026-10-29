// Main application shell: minimal 4-item navigation (Home, Games, Advanced, Settings),
// background power/widget management, and clean window close vs application exit separation.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PCTune
{
    class Shell : Form
    {
        readonly Engine eng = new Engine();
        readonly List<NavItem> nav = new List<NavItem>();
        readonly List<Control> views = new List<Control>();
        readonly Panel side, host, status;
        readonly Label lStatus;
        readonly Btn bWidgetToggle;
        readonly LogPage log;
        readonly HomePage homePage;
        readonly GamesPage gamesPage;
        readonly AdvancedPage advPage;
        readonly SettingsPage settingsPage;
        readonly RamSchedule ramSched;
        readonly PowerWatch watch;
        Widget widget;
        volatile bool busy;
        bool reallyClose;
        int currentView;

        public Engine Eng { get { return eng; } }
        public bool IsBusy { get { return busy; } }
        public event Action Closed2;

        public bool IsWidgetVisible
        {
            get { return widget != null && !widget.IsDisposed && widget.Visible; }
        }

        public Shell(bool isStartup = false)
        {
            Text = "PC-Tune v" + AppInfo.Version;
            var wa = Screen.PrimaryScreen.WorkingArea;
            Size = new Size(Math.Min(Ui.D(960), wa.Width - Ui.D(40)),
                            Math.Min(Ui.D(640), wa.Height - Ui.D(40)));
            MinimumSize = new Size(Math.Min(Ui.D(780), wa.Width - Ui.D(20)),
                                   Math.Min(Ui.D(500), wa.Height - Ui.D(20)));
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Ui.Bg;
            Font = new Font(Ui.Body, 9.5f);
            Icon = Tray.AppIcon();
            HandleCreated += delegate { Ui.DarkTitle(this); };
            Shown += delegate { Ui.DarkTitle(this); };

            eng.screenMinutes = Cfg.ScreenMinutes;
            eng.Sink = OnLog;

            // ---- Minimal Left Sidebar
            side = new Panel { Dock = DockStyle.Left, Width = Ui.D(186), BackColor = Ui.Side };
            Controls.Add(side);

            var brand = new Label
            {
                Text = "PC-Tune",
                Font = new Font(Ui.Disp, 15.5f, FontStyle.Bold),
                ForeColor = Ui.Text,
                AutoSize = true,
                BackColor = Color.Transparent,
                Location = new Point(Ui.D(22), Ui.D(24))
            };
            side.Controls.Add(brand);
            var sub = new Label
            {
                Text = "system utility  ·  v" + AppInfo.Version,
                Font = new Font(Ui.Tiny, 8.25f),
                ForeColor = Ui.Dim,
                AutoSize = true,
                BackColor = Color.Transparent,
                Location = new Point(Ui.D(23), Ui.D(50))
            };
            side.Controls.Add(sub);

            bWidgetToggle = new Btn
            {
                Text2 = Cfg.WidgetOn ? "Hide Widget" : "Show Widget",
                Subtle = true,
                Height = Ui.D(30)
            };
            bWidgetToggle.Click += delegate { ToggleWidget(); };
            side.Controls.Add(bWidgetToggle);

            // ---- Subtle Bottom Status Bar
            status = new Panel { Dock = DockStyle.Bottom, Height = Ui.D(30), BackColor = Ui.Side };
            Controls.Add(status);
            lStatus = new Label
            {
                Dock = DockStyle.Fill,
                Font = new Font(Ui.Tiny, 8.5f),
                ForeColor = Ui.Dim,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(Ui.D(16), 0, 0, 0),
                Text = "Ready"
            };
            status.Controls.Add(lStatus);

            // ---- Pages (Only 4 top-level sections: Home, Games, Advanced, Settings)
            ramSched = new RamSchedule();
            ramSched.Done += delegate (Ram.Result r)
            {
                Tray.Notify("Memory cleanup", string.Format("+{0:N0} MB available RAM ({1} background apps trimmed)", r.FreedMb, r.Touched));
            };

            log = new LogPage();
            advPage = new AdvancedPage(eng, Run, ramSched, log);
            homePage = new HomePage(eng, Run, Show, delegate { Show(2); advPage.SelectSub(3); });
            gamesPage = new GamesPage(eng, Run);
            settingsPage = new SettingsPage(
                delegate (bool on) { ShowWidget(on); },
                delegate () { ResetWidgetPosition(); },
                delegate () { Quit(); });

            Add("", "Home", homePage);
            Add("", "Games", gamesPage);
            Add("", "Advanced", advPage);
            Add("", "Settings", settingsPage);
            NoMnemonics(this);

            host = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg };
            Controls.Add(host);
            host.BringToFront();
            foreach (var v in views) { v.Visible = false; host.Controls.Add(v); }

            side.Resize += delegate { LayoutNav(); };
            LayoutNav();
            Show(0);

            // ---- Power state watcher
            watch = new PowerWatch();
            watch.Changed += delegate (bool onBat)
            {
                try
                {
                    BeginInvoke((Action)delegate
                    {
                        Tray.PowerChanged();
                        Status(onBat ? "Switched to battery power" : "Connected to AC power");
                        RefreshCurrentView();
                        if (widget != null && !widget.IsDisposed) widget.Invalidate();
                    });
                }
                catch { }
            };

            var carSync = new Timer { Interval = 15000 };
            carSync.Tick += delegate
            {
                System.Threading.ThreadPool.QueueUserWorkItem(delegate { Games.SyncCar(eng.Echo); });
            };
            carSync.Start();

            // Show floating widget according to startup vs normal launch preferences
            bool wantWidget = isStartup ? (Cfg.ShowWidgetOnStartup && Cfg.WidgetOn) : Cfg.WidgetOn;
            if (wantWidget) ShowWidget(true);
            else UpdateWidgetButton();

            if (GameMode.Active)
                GameMode.StartUpkeep();

            if (Cfg.PerfOnStart && !GameMode.Active)
            {
                Run(delegate
                {
                    string p = Cfg.Profile;
                    if (p == "car") eng.RunCar();
                    else if (p == "game") eng.RunGaming();
                    else if (p == "comp") eng.RunCompetitive();
                    else if (p == "bal") eng.RunBalanced();
                    else if (p == "eco") eng.RunEco();
                    else eng.RunPerf();
                });
            }
        }

        void Add(string glyph, string label, Control v)
        {
            var n = new NavItem { Glyph = glyph, Label = label };
            int idx = views.Count;
            n.Click += delegate { Show(idx); };
            side.Controls.Add(n);
            nav.Add(n);
            views.Add(v);
        }

        void LayoutNav()
        {
            int y = Ui.D(88);
            foreach (var n in nav)
            {
                n.Bounds = new Rectangle(Ui.D(0), y, side.ClientSize.Width, Ui.D(40));
                y += Ui.D(44);
            }
            bWidgetToggle.Bounds = new Rectangle(Ui.D(16), side.ClientSize.Height - Ui.D(46), side.ClientSize.Width - Ui.D(32), Ui.D(30));
        }

        public void Show(int i)
        {
            if (i < 0 || i >= views.Count) return;
            currentView = i;
            for (int k = 0; k < views.Count; k++)
            {
                nav[k].Selected = k == i;
                views[k].Visible = k == i;
            }
            RefreshCurrentView();
            views[i].BringToFront();
        }

        void RefreshCurrentView()
        {
            if (currentView < 0 || currentView >= views.Count) return;
            var p = views[currentView] as Page;
            if (p != null) p.Refresh2();
            else if (views[currentView] == advPage) advPage.RefreshActive();
        }

        public void ToggleWidget()
        {
            bool next = !IsWidgetVisible;
            Cfg.WidgetOn = next;
            ShowWidget(next);
        }

        public void ShowWidget(bool on)
        {
            if (on)
            {
                if (widget == null || widget.IsDisposed)
                {
                    widget = new Widget(
                        eng,
                        delegate (Action act) { RunAction(act); },
                        delegate () { return busy; },
                        delegate ()
                        {
                            Show();
                            if (WindowState == FormWindowState.Minimized)
                                WindowState = FormWindowState.Normal;
                            BringToFront();
                            Activate();
                        },
                        delegate ()
                        {
                            UpdateWidgetButton();
                            if (currentView == 3) settingsPage.Refresh2();
                        });
                }
                widget.SetCollapsed(Cfg.WidgetCollapsed);
                widget.Show();
            }
            else if (widget != null && !widget.IsDisposed)
            {
                widget.Hide();
            }
            UpdateWidgetButton();
            if (currentView == 3) settingsPage.Refresh2();
        }

        void ResetWidgetPosition()
        {
            if (widget != null && !widget.IsDisposed)
            {
                widget.Location = Cfg.ClampWidgetLocation(widget.Width, widget.Height);
                Cfg.WidgetX = widget.Location.X;
                Cfg.WidgetY = widget.Location.Y;
            }
            if (!IsWidgetVisible)
            {
                Cfg.WidgetOn = true;
                ShowWidget(true);
            }
        }

        void UpdateWidgetButton()
        {
            if (bWidgetToggle == null) return;
            bWidgetToggle.Text2 = IsWidgetVisible ? "Hide Widget" : "Show Widget";
            bWidgetToggle.Invalidate();
        }

        void OnLog(string s, Lv lv)
        {
            if (!IsHandleCreated) return;
            if (log.Box.InvokeRequired)
            {
                try { log.Box.BeginInvoke((Action)delegate { OnLog(s, lv); }); }
                catch { }
                return;
            }
            Color c = lv == Lv.Ok ? Ui.Green
                    : lv == Lv.Warn ? Ui.Amber
                    : lv == Lv.Bad ? Color.FromArgb(240, 130, 130)
                    : lv == Lv.Head ? Ui.Accent : Ui.Dim;
            log.Box.SelectionStart = log.Box.TextLength;
            log.Box.SelectionColor = c;
            log.Box.AppendText(s + "\r\n");
            log.Box.ScrollToCaret();
            if (s.Trim().Length > 0) Status(s.Trim());
        }

        void Status(string s)
        {
            if (s.Length > 140) s = s.Substring(0, 140);
            lStatus.Text = s;
        }

        void Run(Action work) { RunAction(work, null); }

        public void RunAction(Action work, string notifyText = null)
        {
            if (busy) { Status("Operation in progress..."); return; }
            busy = true;
            foreach (var n in nav) n.Enabled = false;
            if (widget != null && !widget.IsDisposed) widget.Invalidate();
            var th = new System.Threading.Thread(delegate ()
            {
                Exception err = null;
                try { work(); }
                catch (Exception ex) { err = ex; OnLog("  [x] " + ex.Message, Lv.Bad); }
                finally
                {
                    busy = false;
                    Action finishUi = delegate
                    {
                        foreach (var n in nav) n.Enabled = true;
                        Status("Ready");
                        Tray.PowerChanged();
                        RefreshCurrentView();
                        if (widget != null && !widget.IsDisposed) widget.Invalidate();
                        if (!string.IsNullOrEmpty(notifyText))
                            Tray.Notify("PC-Tune", err == null ? notifyText : ("Failed: " + err.Message));
                    };
                    try
                    {
                        if (IsHandleCreated) BeginInvoke(finishUi);
                        else finishUi();
                    }
                    catch { }
                }
            });
            th.IsBackground = true;
            th.Start();
        }

        // Labels treat "&" as a shortcut marker, so "Window & System Tray" showed as "Window _System Tray".
        static void NoMnemonics(Control c)
        {
            var l = c as Label;
            if (l != null) l.UseMnemonic = false;
            foreach (Control k in c.Controls) NoMnemonics(k);
            c.ControlAdded += (s, e) => NoMnemonics(e.Control);
        }

        public void Quit()
        {
            reallyClose = true;
            // Started in the tray, the window has no handle: Close() then only disposes it, FormClosed never
            // fires and the process stays alive with no window or icon.
            if (IsHandleCreated) { Close(); return; }
            Dispose();
            var h = Closed2;
            if (h != null) h();
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            try
            {
                Ui.UseDpi(e.DeviceDpiNew);
                var wa = Screen.FromControl(this).WorkingArea;
                MinimumSize = new Size(Math.Min(Ui.D(780), wa.Width - Ui.D(20)),
                                       Math.Min(Ui.D(500), wa.Height - Ui.D(20)));
                side.Width = Ui.D(186);
                status.Height = Ui.D(30);
                LayoutNav();
                homePage.Relayout2();
                gamesPage.Relayout2();
                advPage.RelayoutAll();
                settingsPage.Relayout2();
            }
            catch { }
            base.OnDpiChanged(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            var h = Closed2;
            if (h != null) h();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!reallyClose && Cfg.CloseToTray &&
                e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                if (!Cfg.TrayEnabled)
                {
                    Cfg.TrayEnabled = true;
                    Tray.Refresh2();
                }
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool d)
        {
            if (d)
            {
                if (watch != null) watch.Dispose();
                if (ramSched != null) ramSched.Dispose();
                if (widget != null && !widget.IsDisposed) { widget.Dispose(); }
                Tray.Kill();
            }
            base.Dispose(d);
        }
    }
}
