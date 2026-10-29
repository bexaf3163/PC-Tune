// Core pages: Home (minimal hero + 3 metric cards), Advanced (responsive sub-navigation container),
// Power sub-page, and Settings (application preferences).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace PCTune
{
    class Page : Panel
    {
        public string Title = "", Sub = "";
        protected readonly Panel Host;
        readonly Label lt, ls;
        bool compactHeader;

        public bool CompactHeader
        {
            get { return compactHeader; }
            set
            {
                if (compactHeader == value) return;
                compactHeader = value;
                lt.Visible = ls.Visible = !value;
                Again();
            }
        }

        public Page(string title, string sub)
        {
            Title = title; Sub = sub;
            Dock = DockStyle.Fill;
            BackColor = Ui.Bg;

            lt = new Label
            {
                Text = title,
                Font = new Font(Ui.Disp, 16.5f, FontStyle.Bold),
                ForeColor = Ui.Text,
                AutoSize = true,
                Location = new Point(Ui.D(28), Ui.D(22)),
                BackColor = Color.Transparent
            };
            ls = new Label
            {
                Text = sub,
                Font = new Font(Ui.Body, 9f),
                ForeColor = Ui.Dim,
                AutoSize = true,
                Location = new Point(Ui.D(29), Ui.D(50)),
                BackColor = Color.Transparent
            };
            Controls.Add(lt);
            Controls.Add(ls);

            Host = new Panel
            {
                BackColor = Ui.Bg,
                Location = new Point(Ui.D(28), Ui.D(78)),
                AutoScroll = true
            };
            Host.HandleCreated += delegate { Ui.DarkScroll(Host); };
            Controls.Add(Host);
            Resize += delegate { Again(); };
            Host.ClientSizeChanged += delegate { Again(); };
        }

        public void Relayout2() { Again(); }

        bool laying;

        void Again()
        {
            if (laying) return;
            laying = true;
            try { Relayout(); }
            finally { laying = false; }
        }

        protected virtual void Relayout()
        {
            int top = compactHeader ? Ui.D(12) : Ui.D(78);
            int pad = Ui.D(28);
            Host.Bounds = new Rectangle(
                pad,
                top,
                Math.Max(Ui.D(200), ClientSize.Width - pad * 2),
                Math.Max(Ui.D(80), ClientSize.Height - top - Ui.D(16)));
        }

        public virtual void Refresh2() { }

        protected static Panel Sect(string caption, int height)
        {
            var p = new Panel { BackColor = Ui.Panel, Height = Ui.D(height) };
            p.Paint += delegate (object s, PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(Ui.Bg)) g.FillRectangle(b, p.ClientRectangle);
                using (var path = Ui.Round(new Rectangle(0, 0, p.Width - 1, p.Height - 1), Ui.D(12)))
                {
                    using (var br = new SolidBrush(Ui.Panel)) g.FillPath(br, path);
                    using (var pen = new Pen(Ui.Line)) g.DrawPath(pen, path);
                }
                if (caption.Length > 0)
                    using (var f = new Font(Ui.Body, 9.75f, FontStyle.Bold))
                        TextRenderer.DrawText(g, caption, f, new Rectangle(Ui.D(20), Ui.D(14), Math.Max(Ui.D(40), p.Width - Ui.D(40)), Ui.D(22)),
                            Ui.Text, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            };
            return p;
        }

        protected static void Row(Panel host, int y, string label, string hint, Control right)
        {
            var l = new Label
            {
                Text = label,
                Font = new Font(Ui.Body, 9.5f),
                ForeColor = Ui.Text,
                AutoSize = false,
                BackColor = Color.Transparent,
                Location = new Point(Ui.D(20), Ui.D(y)),
                Size = new Size(Ui.D(400), Ui.D(20)),
                AutoEllipsis = true
            };
            l.Tag = "rowTitle";
            host.Controls.Add(l);
            if (!string.IsNullOrEmpty(hint))
            {
                var h = new Label
                {
                    Text = hint,
                    Font = new Font(Ui.Tiny, 8.5f),
                    ForeColor = Ui.Dim,
                    AutoSize = false,
                    BackColor = Color.Transparent,
                    Location = new Point(Ui.D(20), Ui.D(y + 20)),
                    Size = new Size(Ui.D(400), Ui.D(30))
                };
                h.Tag = "rowHint";
                host.Controls.Add(h);
            }
            if (right != null)
            {
                right.Tag = "right";
                right.Top = Ui.D(y + 2);
                host.Controls.Add(right);
            }
        }
    }

    // ------------------------------------------------------------------ Home Screen
    // Minimal, immediate visual hierarchy: State, Primary Action (Prepare for Game / Return to Normal),
    // 3 responsive metric cards (CPU / GPU / RAM), and a subtle status/shortcut footer.
    class HomePage : Page
    {
        readonly Engine eng;
        readonly Action<Action> run;
        readonly Action<int> navTo;
        readonly Action onReviewStartup;
        readonly Panel cHero, cCleanup, cFooter;
        readonly Label lStateTag, lHeroTitle, lHeroSub;
        readonly Btn bPrimary;
        readonly MetricCard mCpu, mGpu, mRam;
        readonly Label lCleanupTag, lCleanupSummary;
        readonly Btn bReview;
        readonly Label lFooterStatus;
        readonly Btn bAdv, bSet;
        readonly Timer tick;
        long lastIdle, lastKernel, lastUser;
        bool scanningAudit;

        public HomePage(Engine e, Action<Action> runner, Action<int> navigate, Action reviewStartup = null)
            : base("PC-Tune", HwInfo.CpuName + " · " + HwInfo.GpuName)
        {
            eng = e;
            run = runner;
            navTo = navigate;
            onReviewStartup = reviewStartup;

            // 1. Primary State & Action Card
            cHero = Sect("", 188);
            Host.Controls.Add(cHero);

            lStateTag = new Label
            {
                Font = new Font(Ui.Tiny, 8.5f, FontStyle.Bold),
                ForeColor = Ui.Accent,
                AutoSize = true,
                BackColor = Color.Transparent,
                Location = new Point(Ui.D(24), Ui.D(22))
            };
            lHeroTitle = new Label
            {
                Font = new Font(Ui.Disp, 19f, FontStyle.Bold),
                ForeColor = Ui.Text,
                AutoSize = false,
                AutoEllipsis = true,
                BackColor = Color.Transparent,
                Location = new Point(Ui.D(22), Ui.D(44)),
                Size = new Size(Ui.D(400), Ui.D(34))
            };
            lHeroSub = new Label
            {
                Font = new Font(Ui.Body, 9.25f),
                ForeColor = Ui.Dim,
                AutoSize = false,
                BackColor = Color.Transparent,
                Location = new Point(Ui.D(24), Ui.D(82)),
                Size = new Size(Ui.D(520), Ui.D(38))
            };
            bPrimary = new Btn
            {
                Text2 = "Prepare for Game",
                Tone = Ui.Accent,
                Filled = true,
                Width = Ui.D(200),
                Height = Ui.D(40),
                Location = new Point(Ui.D(24), Ui.D(128))
            };
            bPrimary.Click += delegate
            {
                if (GameMode.Active) run(delegate { GameMode.Leave(eng, eng.Echo); });
                else run(delegate { GameMode.Enter(eng, eng.Echo); });
            };

            cHero.Controls.Add(lStateTag);
            cHero.Controls.Add(lHeroTitle);
            cHero.Controls.Add(lHeroSub);
            cHero.Controls.Add(bPrimary);

            // 2. Three Responsive Metric Cards (CPU / GPU / RAM)
            mCpu = new MetricCard { Caption = "CPU", Tone = Ui.Accent };
            mGpu = new MetricCard { Caption = "GPU", Tone = Ui.Violet };
            mRam = new MetricCard { Caption = "RAM", Tone = Ui.Green };
            Host.Controls.Add(mCpu);
            Host.Controls.Add(mGpu);
            Host.Controls.Add(mRam);

            // 3. Minimal Background Load / Startup Cleanup Card
            cCleanup = Sect("", 68);
            Host.Controls.Add(cCleanup);

            lCleanupTag = new Label
            {
                Text = "BACKGROUND LOAD · RECOMMENDED CLEANUP",
                Font = new Font(Ui.Tiny, 8f, FontStyle.Bold),
                ForeColor = Ui.Amber,
                AutoSize = true,
                BackColor = Color.Transparent,
                Location = new Point(Ui.D(20), Ui.D(13))
            };
            lCleanupSummary = new Label
            {
                Text = "Checking startup entries, background services, and scheduled tasks...",
                Font = new Font(Ui.Body, 9.25f),
                ForeColor = Ui.Text,
                AutoSize = false,
                AutoEllipsis = true,
                BackColor = Color.Transparent,
                Location = new Point(Ui.D(20), Ui.D(33)),
                Size = new Size(Ui.D(420), Ui.D(22))
            };
            bReview = new Btn
            {
                Text2 = "Review",
                Tone = Ui.Amber,
                Width = Ui.D(96),
                Height = Ui.D(32)
            };
            bReview.Click += delegate
            {
                if (onReviewStartup != null) onReviewStartup();
                else if (navTo != null) navTo(2);
            };
            cCleanup.Controls.Add(lCleanupTag);
            cCleanup.Controls.Add(lCleanupSummary);
            cCleanup.Controls.Add(bReview);

            // 4. Compact Status & Shortcut Bar
            cFooter = Sect("", 58);
            Host.Controls.Add(cFooter);

            lFooterStatus = new Label
            {
                Font = new Font(Ui.Body, 9f),
                ForeColor = Ui.Dim,
                AutoSize = false,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = Color.Transparent,
                Location = new Point(Ui.D(20), Ui.D(14)),
                Size = new Size(Ui.D(300), Ui.D(30))
            };
            cFooter.Controls.Add(lFooterStatus);

            bAdv = new Btn { Text2 = "Advanced", Width = Ui.D(96), Height = Ui.D(30) };
            bAdv.Click += delegate { if (navTo != null) navTo(2); };
            bSet = new Btn { Text2 = "Settings", Width = Ui.D(92), Height = Ui.D(30) };
            bSet.Click += delegate { if (navTo != null) navTo(3); };
            cFooter.Controls.Add(bAdv);
            cFooter.Controls.Add(bSet);

            tick = new Timer { Interval = 2000 };
            tick.Tick += delegate { if (Visible) Sample(); };
            Sample();
            tick.Start();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) { Refresh2(); tick.Start(); }
            else tick.Stop();
        }

        void Sample()
        {
            int cpu = mCpu.Percent;
            long i, k, u;
            if (Sys.GetSystemTimes(out i, out k, out u))
            {
                long di = i - lastIdle, tot = (k - lastKernel) + (u - lastUser);
                if (lastKernel != 0 && tot > 0)
                    cpu = Math.Max(0, Math.Min(100, (int)(100.0 * (tot - di) / tot)));
                lastIdle = i; lastKernel = k; lastUser = u;
            }
            mCpu.Set(cpu, cpu + "%", HwInfo.PhysicalCores + "C / " + HwInfo.LogicalCores + "T");

            int gpu = Sys.SampleGpuPercent();
            mGpu.Set(gpu, gpu + "%", HwInfo.MonitorRefreshHz + " Hz display");

            var m = Ram.State();
            mRam.Set(m.Percent, m.Percent + "%", string.Format("{0:N1} / {1:N0} GB", m.UsedGb, m.TotalGb));

            bool bat = Power.OnBattery();
            int bp = Power.BatteryPercent();
            lFooterStatus.Text = "Mode: " + Cfg.ProfileTitle + "   ·   "
                               + (bat ? "Battery" : "AC Power")
                               + (bp >= 0 ? " (" + bp + "%)" : "");
        }

        public override void Refresh2()
        {
            bool active = GameMode.Active;
            if (active)
            {
                lStateTag.Text = "GAMING MODE ACTIVE";
                lStateTag.ForeColor = Ui.Green;
                lHeroTitle.Text = "Gaming Mode Active";
                lHeroSub.Text = "AC display & sleep locked to Never · 0.5 ms timer active · background load paused.";
                bPrimary.Text2 = "Return to Normal";
                bPrimary.Tone = Ui.Green;
                bPrimary.Filled = false;
            }
            else
            {
                lStateTag.Text = "CURRENT STATE · NORMAL";
                lStateTag.ForeColor = Ui.Accent;
                lHeroTitle.Text = "Ready for Gaming";
                lHeroSub.Text = "Prepares power, locks AC display & sleep to Never, boosts game priority, and pauses background bloat.";
                bPrimary.Text2 = "Prepare for Game";
                bPrimary.Tone = Ui.Accent;
                bPrimary.Filled = true;
            }
            bPrimary.Invalidate();
            Sample();
            RefreshAuditAsync();
            Relayout();
        }

        void RefreshAuditAsync()
        {
            if (scanningAudit) return;
            scanningAudit = true;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    var sum = Startup.GetAuditSummary();
                    int svcBak = Svc.BackupCount();
                    BeginInvoke((Action)delegate
                    {
                        if (sum.TotalRecommended > 0)
                        {
                            lCleanupTag.Text = "BACKGROUND LOAD · RECOMMENDED CLEANUP";
                            lCleanupTag.ForeColor = Ui.Amber;
                            lCleanupSummary.Text = string.Format(
                                "{0} unnecessary startup item(s)   ·   {1} background service(s)   ·   {2} scheduled task(s)",
                                sum.StartupItems, sum.BackgroundServices, sum.OrphanedTasks);
                            bReview.Tone = Ui.Amber;
                            bReview.Filled = true;
                        }
                        else
                        {
                            int bk = sum.TotalBackups + svcBak;
                            lCleanupTag.Text = "BACKGROUND LOAD · STARTUP CLEAN";
                            lCleanupTag.ForeColor = Ui.Green;
                            lCleanupSummary.Text = bk > 0
                                ? "All recommended startup/background bloat cleaned (" + bk + " item(s) backed up and restorable)."
                                : "No unnecessary startup items, orphaned tasks, or bloat services detected.";
                            bReview.Tone = Ui.Accent;
                            bReview.Filled = false;
                        }
                        bReview.Invalidate();
                    });
                }
                catch { }
                finally { scanningAudit = false; }
            });
        }

        protected override void Relayout()
        {
            base.Relayout();
            int w = Host.ClientSize.Width;

            cHero.Bounds = new Rectangle(0, 0, w, Ui.D(188));
            lHeroTitle.Width = Math.Max(Ui.D(180), w - Ui.D(48));
            lHeroSub.Width = Math.Max(Ui.D(180), w - Ui.D(48));

            int gap = Ui.D(14);
            int cardY = Ui.D(204);
            int cardH = Ui.D(104);
            int cw = (w - gap * 2) / 3;
            mCpu.Bounds = new Rectangle(0, cardY, cw, cardH);
            mGpu.Bounds = new Rectangle(cw + gap, cardY, cw, cardH);
            mRam.Bounds = new Rectangle((cw + gap) * 2, cardY, w - (cw + gap) * 2, cardH);

            int cleanY = cardY + cardH + Ui.D(16);
            cCleanup.Bounds = new Rectangle(0, cleanY, w, Ui.D(68));
            bReview.Location = new Point(w - bReview.Width - Ui.D(16), Ui.D(18));
            lCleanupSummary.Width = Math.Max(Ui.D(120), bReview.Left - Ui.D(28));

            int footY = cleanY + Ui.D(68) + Ui.D(14);
            cFooter.Bounds = new Rectangle(0, footY, w, Ui.D(58));
            bSet.Location = new Point(w - bSet.Width - Ui.D(16), Ui.D(14));
            bAdv.Location = new Point(bSet.Left - bAdv.Width - Ui.D(8), Ui.D(14));
            lFooterStatus.Width = Math.Max(Ui.D(80), bAdv.Left - Ui.D(28));
        }

        protected override void Dispose(bool d)
        {
            if (d && tick != null) { tick.Stop(); tick.Dispose(); }
            base.Dispose(d);
        }
    }

    // ------------------------------------------------------------------ Advanced Section
    // Responsive flow-wrapped sub-navigation housing all expert subsystems without header duplication.
    class AdvancedPage : Panel
    {
        readonly Panel tabBar, subHost;
        readonly List<Btn> tabs = new List<Btn>();
        readonly List<Page> subPages = new List<Page>();
        int activeIdx;

        public AdvancedPage(Engine eng, Action<Action> run, RamSchedule ramSched, LogPage logPage)
        {
            Dock = DockStyle.Fill;
            BackColor = Ui.Bg;

            tabBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = Ui.D(52),
                BackColor = Ui.Bg
            };
            Controls.Add(tabBar);

            subHost = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Ui.Bg
            };
            Controls.Add(subHost);
            subHost.BringToFront();

            AddSub("Tweaks", new TweakPage(eng, run));
            AddSub("Power", new PowerPage(eng, run));
            AddSub("Memory", new MemPage(ramSched));
            AddSub("Startup", new StartPage(run));
            AddSub("Services", new SvcPage());
            AddSub("Cleanup", new CleanPage(run));
            AddSub("Log", logPage);

            tabBar.Resize += delegate { LayoutTabs(); };
            LayoutTabs();
            SelectSub(0);
        }

        void AddSub(string title, Page p)
        {
            int idx = subPages.Count;
            p.CompactHeader = true;
            var b = new Btn
            {
                Text2 = title,
                Height = Ui.D(30),
                Width = Ui.D(84),
                Tone = Ui.Accent
            };
            b.Click += delegate { SelectSub(idx); };
            tabBar.Controls.Add(b);
            tabs.Add(b);

            p.Visible = false;
            subHost.Controls.Add(p);
            subPages.Add(p);
        }

        void LayoutTabs()
        {
            int pad = Ui.D(28);
            int maxW = Math.Max(Ui.D(300), tabBar.ClientSize.Width - pad);
            int x = pad, y = Ui.D(14);
            using (var f = new Font(Ui.Body, 9.25f, FontStyle.Bold))
            {
                foreach (var b in tabs)
                {
                    int w = TextRenderer.MeasureText(b.Text2, f).Width + Ui.D(24);
                    if (x > pad && x + w > maxW)
                    {
                        x = pad;
                        y += Ui.D(34);
                    }
                    b.Bounds = new Rectangle(x, y, w, Ui.D(30));
                    x += w + Ui.D(6);
                }
            }
            int neededH = y + Ui.D(36);
            if (tabBar.Height != neededH) tabBar.Height = neededH;
        }

        public void SelectSub(int idx)
        {
            if (idx < 0 || idx >= subPages.Count) return;
            activeIdx = idx;
            for (int i = 0; i < subPages.Count; i++)
            {
                bool sel = i == idx;
                tabs[i].Filled = sel;
                tabs[i].Invalidate();
                subPages[i].Visible = sel;
            }
            subPages[idx].Refresh2();
            subPages[idx].BringToFront();
        }

        public void RefreshActive()
        {
            if (activeIdx >= 0 && activeIdx < subPages.Count)
                subPages[activeIdx].Refresh2();
        }

        public void RelayoutAll()
        {
            LayoutTabs();
            foreach (var p in subPages) p.Relayout2();
        }
    }

    // ------------------------------------------------------------------ Power Sub-Page
    class PowerPage : Page
    {
        readonly Engine eng;
        readonly Panel cModes, cAuto, cScreen;
        readonly Card kPerf, kGame, kComp, kBal, kEco, kCar;
        readonly Toggle tgAuto;
        readonly Picker cbAc, cbDc;
        readonly Label lScr;
        readonly Btn bMinus, bPlus, bRestorePow;
        readonly Label lScrVal;
        List<Scheme> schemes = new List<Scheme>();
        bool loading;

        public PowerPage(Engine e, Action<Action> run)
            : base("Power", "Power profiles, AC display timeout, and scheme automation")
        {
            eng = e;

            cModes = Sect("Power Profile", 206);
            Host.Controls.Add(cModes);

            kPerf = new Card { Title = "Plugged in", Sub = "Max CPU & turbo", Tone = Ui.Green };
            kPerf.Click += delegate { run(delegate { eng.RunPerf(); }); };

            kGame = new Card { Title = "Gaming", Sub = "Steady FPS & TDP", Tone = Ui.Violet };
            kGame.Click += delegate { run(delegate { eng.RunGaming(); }); };

            kComp = new Card { Title = "Competitive", Sub = "Lowest input latency", Tone = Ui.Green };
            kComp.Click += delegate { run(delegate { eng.RunCompetitive(); }); };

            kBal = new Card { Title = "Balanced", Sub = "Daily recommended", Tone = Ui.Accent };
            kBal.Click += delegate { run(delegate { eng.RunBalanced(); }); };

            kEco = new Card { Title = "Economy", Sub = "Longer battery life", Tone = Ui.Accent };
            kEco.Click += delegate { run(delegate { eng.RunEco(); }); };

            kCar = new Card { Title = "In the Car", Sub = "20-30W adapter cap", Tone = Ui.Amber };
            kCar.Click += delegate { run(delegate { eng.RunCar(); }); };

            cModes.Controls.Add(kPerf);
            cModes.Controls.Add(kGame);
            cModes.Controls.Add(kComp);
            cModes.Controls.Add(kBal);
            cModes.Controls.Add(kEco);
            cModes.Controls.Add(kCar);

            cAuto = Sect("Scheme Automation", 184);
            Host.Controls.Add(cAuto);

            tgAuto = new Toggle();
            tgAuto.Init(Cfg.AutoSwitch);
            tgAuto.Toggled += delegate
            {
                Cfg.AutoSwitch = tgAuto.On;
                cbAc.Enabled = cbDc.Enabled = tgAuto.On;
                if (tgAuto.On) PowerWatch.Apply(Power.OnBattery());
            };
            Row(cAuto, 46, "Switch Windows scheme automatically on AC / Battery",
                "Activates your chosen AC scheme when plugged in and battery scheme when unplugged.", tgAuto);

            cbAc = new Picker { Width = Ui.D(220) };
            cbDc = new Picker { Width = Ui.D(220) };
            cbAc.Changed += delegate { Pick(cbAc, true); };
            cbDc.Changed += delegate { Pick(cbDc, false); };
            cAuto.Controls.Add(cbAc);
            cAuto.Controls.Add(cbDc);

            var lAc = new Label { Text = "On AC power", Font = new Font(Ui.Body, 9.25f), ForeColor = Ui.Text, AutoSize = true, BackColor = Color.Transparent, Name = "lAc" };
            var lDc = new Label { Text = "On battery", Font = new Font(Ui.Body, 9.25f), ForeColor = Ui.Text, AutoSize = true, BackColor = Color.Transparent, Name = "lDc" };
            cAuto.Controls.Add(lAc); cAuto.Controls.Add(lDc);

            cScreen = Sect("Display Timeout & Rollback", 94);
            Host.Controls.Add(cScreen);
            lScr = new Label
            {
                Text = "AC display timeout",
                Font = new Font(Ui.Body, 9.25f),
                ForeColor = Ui.Text,
                AutoSize = true,
                BackColor = Color.Transparent,
                Location = new Point(Ui.D(20), Ui.D(50))
            };
            cScreen.Controls.Add(lScr);
            bMinus = new Btn { Text2 = "−", Width = Ui.D(34), Height = Ui.D(30) };
            bPlus = new Btn { Text2 = "+", Width = Ui.D(34), Height = Ui.D(30) };
            lScrVal = new Label
            {
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font(Ui.Body, 9.5f, FontStyle.Bold),
                ForeColor = Ui.Text,
                BackColor = Ui.PanelHi,
                Size = new Size(Ui.D(84), Ui.D(30))
            };
            bMinus.Click += delegate { ApplyScreenTimeout(Cfg.ScreenMinutes <= 5 ? 0 : Cfg.ScreenMinutes - 5); };
            bPlus.Click += delegate { ApplyScreenTimeout(Cfg.ScreenMinutes >= 180 ? 180 : Cfg.ScreenMinutes + 5); };

            bRestorePow = new Btn { Text2 = "Reset Power", Width = Ui.D(126), Height = Ui.D(30) };
            bRestorePow.Click += delegate { run(delegate { eng.RunRestorePower(); }); };

            cScreen.Controls.Add(bMinus);
            cScreen.Controls.Add(lScrVal);
            cScreen.Controls.Add(bPlus);
            cScreen.Controls.Add(bRestorePow);
            SyncScreenLabel();
        }

        void SyncScreenLabel()
        {
            int m = Cfg.ScreenMinutes;
            eng.screenMinutes = m;
            lScrVal.Text = m == 0 ? "Never" : m + " min";
        }

        void ApplyScreenTimeout(int m)
        {
            Cfg.ScreenMinutes = m;
            eng.screenMinutes = m;
            lScrVal.Text = m == 0 ? "Never" : m + " min";
            int sec = m * 60;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                Sys.Ac(Sys.SUB_VIDEO, Sys.VIDEOIDLE, sec);
                Sys.Pow("/setactive SCHEME_CURRENT");
                eng.Echo("  [+] AC display timeout set to " + (m == 0 ? "Never (0 sec)" : m + " min"));
            });
        }

        void Pick(Picker cb, bool ac)
        {
            if (loading || cb.SelectedIndex < 0 || cb.SelectedIndex >= schemes.Count) return;
            string g = schemes[cb.SelectedIndex].Guid;
            if (ac) Cfg.SchemeAc = g; else Cfg.SchemeDc = g;
            if (Cfg.AutoSwitch) PowerWatch.Apply(Power.OnBattery());
        }

        public override void Refresh2()
        {
            loading = true;
            schemes = Power.List();
            cbAc.Items.Clear(); cbDc.Items.Clear();
            foreach (var s in schemes) { cbAc.Items.Add(s.Name); cbDc.Items.Add(s.Name); }
            cbAc.Invalidate(); cbDc.Invalidate();
            Select(cbAc, Cfg.SchemeAc, true);
            Select(cbDc, Cfg.SchemeDc, false);
            cbAc.Enabled = cbDc.Enabled = Cfg.AutoSwitch;
            tgAuto.Init(Cfg.AutoSwitch);
            SyncScreenLabel();
            string prof = string.IsNullOrEmpty(Cfg.Profile) ? "bal" : Cfg.Profile;
            kPerf.Active = prof == "perf"; kPerf.Invalidate();
            kGame.Active = prof == "game"; kGame.Invalidate();
            kComp.Active = prof == "comp"; kComp.Invalidate();
            kBal.Active  = prof == "bal";  kBal.Invalidate();
            kEco.Active  = prof == "eco";  kEco.Invalidate();
            kCar.Active  = prof == "car";  kCar.Invalidate();
            loading = false;
        }

        void Select(Picker cb, string guid, bool ac)
        {
            bool stored = !string.IsNullOrEmpty(guid);
            if (!stored) guid = Fallback(ac);

            int idx = -1;
            for (int i = 0; i < schemes.Count; i++)
                if (string.Equals(schemes[i].Guid, guid, StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
            if (idx < 0 && schemes.Count > 0) idx = 0;
            if (idx < 0) return;

            cb.Init(idx);
            if (!stored || !string.Equals(guid, schemes[idx].Guid, StringComparison.OrdinalIgnoreCase))
            {
                if (ac) Cfg.SchemeAc = schemes[idx].Guid;
                else Cfg.SchemeDc = schemes[idx].Guid;
            }
        }

        string Fallback(bool ac)
        {
            if (ac) return Power.ActiveGuid();
            string[] prefer =
            {
                "a1841308-3541-4fab-bc81-f71556f20b4a",
                "381b4222-f694-41f0-9685-ff5bb260df2e"
            };
            foreach (string want in prefer)
                foreach (var s in schemes)
                    if (string.Equals(s.Guid, want, StringComparison.OrdinalIgnoreCase)) return s.Guid;
            return Power.ActiveGuid();
        }

        protected override void Relayout()
        {
            base.Relayout();
            int w = Host.ClientSize.Width;
            cModes.Bounds = new Rectangle(0, 0, w, Ui.D(206));
            int gap = Ui.D(14);
            int cw = (w - Ui.D(40) - gap * 2) / 3;
            int lastW = w - Ui.D(40) - (cw + gap) * 2;
            kPerf.Bounds = new Rectangle(Ui.D(20), Ui.D(46), cw, Ui.D(66));
            kGame.Bounds = new Rectangle(Ui.D(20) + cw + gap, Ui.D(46), cw, Ui.D(66));
            kComp.Bounds = new Rectangle(Ui.D(20) + (cw + gap) * 2, Ui.D(46), lastW, Ui.D(66));

            kBal.Bounds = new Rectangle(Ui.D(20), Ui.D(124), cw, Ui.D(66));
            kEco.Bounds = new Rectangle(Ui.D(20) + cw + gap, Ui.D(124), cw, Ui.D(66));
            kCar.Bounds = new Rectangle(Ui.D(20) + (cw + gap) * 2, Ui.D(124), lastW, Ui.D(66));

            cAuto.Bounds = new Rectangle(0, Ui.D(220), w, Ui.D(184));
            tgAuto.Location = new Point(w - Ui.D(62), Ui.D(48));
            foreach (Control c in cAuto.Controls)
            {
                var l = c as Label;
                if (l != null && (string)l.Tag == "rowTitle") l.Width = Math.Max(Ui.D(120), w - Ui.D(96));
                if (l != null && (string)l.Tag == "rowHint") l.Width = Math.Max(Ui.D(120), w - Ui.D(96));
                if (l != null && l.Name == "lAc") l.Location = new Point(Ui.D(20), Ui.D(112));
                if (l != null && l.Name == "lDc") l.Location = new Point(Ui.D(20), Ui.D(146));
            }
            cbAc.Location = new Point(w - cbAc.Width - Ui.D(20), Ui.D(106));
            cbDc.Location = new Point(w - cbDc.Width - Ui.D(20), Ui.D(140));

            cScreen.Bounds = new Rectangle(0, Ui.D(418), w, Ui.D(94));
            int sx = Ui.D(20) + lScr.PreferredWidth + Ui.D(16);
            bMinus.Location = new Point(sx, Ui.D(46));
            lScrVal.Location = new Point(sx + Ui.D(40), Ui.D(46));
            bPlus.Location = new Point(sx + Ui.D(130), Ui.D(46));
            bRestorePow.Location = new Point(w - bRestorePow.Width - Ui.D(20), Ui.D(46));
        }
    }

    // ------------------------------------------------------------------ Settings Page
    // Strictly application-level preferences (Startup, Widget, Window/Tray, Exit).
    class SettingsPage : Page
    {
        readonly Panel cStart, cWidget, cWindow;
        readonly Toggle tgAuto, tgWidgetStart, tgMainStart, tgPerf;
        readonly Toggle tgWidget, tgCollapsed;
        readonly Toggle tgClose, tgTray, tgPromo;
        readonly Btn bResetPos, bExitApp, bUpdate;
        readonly Action<bool> widgetToggle;
        readonly Action onResetWidgetPos;
        readonly Action onExitApp;

        public SettingsPage(Action<bool> onWidget, Action resetWidget, Action exitApp)
            : base("Settings", "Startup behavior, floating widget, and window preferences")
        {
            widgetToggle = onWidget;
            onResetWidgetPos = resetWidget;
            onExitApp = exitApp;

            // 1. Startup section
            cStart = Sect("Startup", 284);
            Host.Controls.Add(cStart);

            tgAuto = new Toggle();
            tgAuto.Init(Cfg.Autostart);
            tgAuto.Toggled += delegate { Cfg.Autostart = tgAuto.On; tgAuto.Init(Cfg.Autostart); };
            Row(cStart, 46, "Launch with Windows",
                "Starts quietly at sign-in and automatically updates the registration if the portable .exe is moved.", tgAuto);

            tgWidgetStart = new Toggle();
            tgWidgetStart.Init(Cfg.ShowWidgetOnStartup);
            tgWidgetStart.Toggled += delegate { Cfg.ShowWidgetOnStartup = tgWidgetStart.On; };
            Row(cStart, 106, "Show widget on startup",
                "Displays the compact performance pill automatically when starting with Windows.", tgWidgetStart);

            tgMainStart = new Toggle();
            tgMainStart.Init(Cfg.OpenMainOnStartup);
            tgMainStart.Toggled += delegate { Cfg.OpenMainOnStartup = tgMainStart.On; };
            Row(cStart, 166, "Open main window on startup",
                "Keep off to start silently in the background without opening the main window.", tgMainStart);

            tgPerf = new Toggle();
            tgPerf.Init(Cfg.PerfOnStart);
            tgPerf.Toggled += delegate { Cfg.PerfOnStart = tgPerf.On; };
            Row(cStart, 226, "Re-apply active power profile on startup",
                "Restores your last selected power profile automatically at launch.", tgPerf);

            // 2. Floating Widget section
            cWidget = Sect("Floating Widget", 210);
            Host.Controls.Add(cWidget);

            tgWidget = new Toggle();
            tgWidget.Init(Cfg.WidgetOn);
            tgWidget.Toggled += delegate { Cfg.WidgetOn = tgWidget.On; if (widgetToggle != null) widgetToggle(tgWidget.On); };
            Row(cWidget, 46, "Show floating widget",
                "Displays CPU, GPU, RAM, active state, and one-click Boost on the desktop.", tgWidget);

            tgCollapsed = new Toggle();
            tgCollapsed.Init(Cfg.WidgetCollapsed);
            tgCollapsed.Toggled += delegate
            {
                Cfg.WidgetCollapsed = tgCollapsed.On;
                if (widgetToggle != null && Cfg.WidgetOn) widgetToggle(true);
            };
            Row(cWidget, 106, "Collapsed mini-pill mode",
                "Shrinks the widget to a compact CPU indicator. Click the pill anytime to expand.", tgCollapsed);

            bResetPos = new Btn { Text2 = "Reset Position", Width = Ui.D(140), Height = Ui.D(30) };
            bResetPos.Click += delegate
            {
                Cfg.WidgetX = int.MinValue;
                Cfg.WidgetY = int.MinValue;
                if (onResetWidgetPos != null) onResetWidgetPos();
            };
            cWidget.Controls.Add(bResetPos);

            // 3. Window & System Tray section
            cWindow = Sect("Window & System Tray", 262);
            Host.Controls.Add(cWindow);

            tgClose = new Toggle();
            tgClose.Init(Cfg.CloseToTray);
            tgClose.Toggled += delegate { Cfg.CloseToTray = tgClose.On; };
            Row(cWindow, 46, "Closing main window keeps PC-Tune running",
                "Minimizes to the system tray instead of exiting background monitoring.", tgClose);

            tgTray = new Toggle();
            tgTray.Init(Cfg.TrayEnabled);
            tgTray.Toggled += delegate { Cfg.TrayEnabled = tgTray.On; Tray.Refresh2(); };
            Row(cWindow, 106, "Show system tray icon",
                "Quick tray menu for Open, Prepare for Game, Show/Hide Widget, and Exit.", tgTray);

            tgPromo = new Toggle();
            tgPromo.Init(Cfg.TrayPromoted);
            tgPromo.Toggled += delegate { Cfg.TrayPromoted = tgPromo.On; };
            Row(cWindow, 166, "Keep tray icon always visible on taskbar",
                "Shows the PC-Tune icon next to the clock instead of hiding it in the overflow menu.", tgPromo);

            bExitApp = new Btn { Text2 = "Exit PC-Tune", Tone = Ui.Amber, Width = Ui.D(140), Height = Ui.D(30) };
            bExitApp.Click += delegate { if (onExitApp != null) onExitApp(); };
            cWindow.Controls.Add(bExitApp);

            bUpdate = new Btn { Text2 = "Check for updates", Width = Ui.D(260), Height = Ui.D(30) };
            bUpdate.Click += delegate
            {
                Updater.CheckNow(s =>
                {
                    try { bUpdate.BeginInvoke((Action)delegate { bUpdate.Text2 = s; bUpdate.Invalidate(); }); } catch { }
                });
            };
            cWindow.Controls.Add(bUpdate);
        }

        public override void Refresh2()
        {
            tgAuto.Init(Cfg.Autostart);
            tgWidgetStart.Init(Cfg.ShowWidgetOnStartup);
            tgMainStart.Init(Cfg.OpenMainOnStartup);
            tgPerf.Init(Cfg.PerfOnStart);
            tgWidget.Init(Cfg.WidgetOn);
            tgCollapsed.Init(Cfg.WidgetCollapsed);
            tgClose.Init(Cfg.CloseToTray);
            tgTray.Init(Cfg.TrayEnabled);
            tgPromo.Init(Cfg.TrayPromoted);
        }

        protected override void Relayout()
        {
            base.Relayout();
            int w = Host.ClientSize.Width;
            cStart.Bounds = new Rectangle(0, 0, w, Ui.D(284));
            cWidget.Bounds = new Rectangle(0, Ui.D(298), w, Ui.D(210));
            cWindow.Bounds = new Rectangle(0, Ui.D(522), w, Ui.D(262));

            Place(cStart, w, new int[] { 46, 106, 166, 226 });
            Place(cWidget, w, new int[] { 46, 106 });
            Place(cWindow, w, new int[] { 46, 106, 166 });

            bResetPos.Location = new Point(Ui.D(20), Ui.D(164));
            bExitApp.Location = new Point(Ui.D(20), Ui.D(218));
            bUpdate.Location = new Point(Ui.D(172), Ui.D(218));
        }

        static void Place(Panel host, int w, int[] rows)
        {
            int i = 0;
            foreach (Control c in host.Controls)
            {
                var t = c as Toggle;
                if (t != null && i < rows.Length) { t.Location = new Point(w - Ui.D(62), Ui.D(rows[i] + 2)); i++; }
                var l = c as Label;
                if (l != null && ((string)l.Tag == "rowTitle" || (string)l.Tag == "rowHint"))
                    l.Width = Math.Max(Ui.D(120), w - Ui.D(96));
            }
        }
    }
}
