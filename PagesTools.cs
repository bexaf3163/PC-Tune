// Advanced tool pages: Memory, Windows Services, System Tweaks (with category/search filter), and Activity Log.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace PCTune
{
    static class DarkList
    {
        public static ListView Make(bool checks)
        {
            var lv = new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                HideSelection = false,
                MultiSelect = false,
                OwnerDraw = true,
                CheckBoxes = checks,
                BorderStyle = BorderStyle.None,
                BackColor = Color.FromArgb(20, 22, 28),
                ForeColor = Ui.Text,
                Font = new Font(Ui.Body, 9.25f)
            };
            lv.HandleCreated += delegate { Ui.DarkScroll(lv); Ui.DarkHeader(lv); };
            lv.DrawColumnHeader += delegate (object s, DrawListViewColumnHeaderEventArgs e)
            {
                using (var b = new SolidBrush(Color.FromArgb(28, 31, 39))) e.Graphics.FillRectangle(b, e.Bounds);
                using (var p = new Pen(Ui.Line)) e.Graphics.DrawLine(p, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                using (var f = new Font(Ui.Tiny, 8.5f, FontStyle.Bold))
                    TextRenderer.DrawText(e.Graphics, e.Header.Text, f,
                        new Rectangle(e.Bounds.X + Ui.D(8), e.Bounds.Y, e.Bounds.Width - Ui.D(10), e.Bounds.Height),
                        Ui.Dim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            };
            lv.DrawItem += delegate (object s, DrawListViewItemEventArgs e)
            {
                bool sel = e.Item.Selected;
                using (var b = new SolidBrush(sel ? Color.FromArgb(38, 52, 76) : Color.FromArgb(20, 22, 28)))
                    e.Graphics.FillRectangle(b, e.Bounds);
            };
            lv.DrawSubItem += delegate (object s, DrawListViewSubItemEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                int x = e.Bounds.X + Ui.D(8), w = e.Bounds.Width - Ui.D(12);

                if (checks && e.ColumnIndex == 0)
                {
                    var box = new Rectangle(e.Bounds.X + Ui.D(8), e.Bounds.Y + (e.Bounds.Height - Ui.D(15)) / 2, Ui.D(15), Ui.D(15));
                    using (var path = Ui.Round(box, Ui.D(4)))
                    {
                        if (e.Item.Checked)
                        {
                            using (var b = new SolidBrush(Ui.Accent)) g.FillPath(b, path);
                            using (var p = new Pen(Color.FromArgb(16, 18, 22), 2f))
                                g.DrawLines(p, new[]
                                {
                                    new Point(box.X + Ui.D(4), box.Y + Ui.D(8)),
                                    new Point(box.X + Ui.D(6), box.Y + Ui.D(10)),
                                    new Point(box.X + Ui.D(11), box.Y + Ui.D(4))
                                });
                        }
                        else using (var p = new Pen(Ui.Line, 1.4f)) g.DrawPath(p, path);
                    }
                    x = box.Right + Ui.D(8);
                    w = e.Bounds.Right - x - Ui.D(4);
                }

                Color fg = e.Item.ForeColor == Color.Empty ? Ui.Text : e.Item.ForeColor;
                if (e.ColumnIndex > 0) fg = Ui.Dim;
                TextRenderer.DrawText(g, e.SubItem.Text, lv.Font,
                    new Rectangle(x, e.Bounds.Y, Math.Max(0, w), e.Bounds.Height), fg,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            };
            return lv;
        }

        public static void Fit(ListView lv)
        {
            if (lv.Columns.Count == 0) return;
            int used = 0;
            for (int i = 0; i < lv.Columns.Count - 1; i++) used += lv.Columns[i].Width;
            int rest = lv.ClientSize.Width - used - Ui.D(4);
            lv.Columns[lv.Columns.Count - 1].Width = rest < Ui.D(60) ? Ui.D(60) : rest;
        }
    }

    // ------------------------------------------------------------------ Memory
    class MemPage : Page
    {
        readonly Panel cAuto, cList;
        readonly Toggle tgAuto;
        readonly Btn bMinus, bPlus, bNow, bClose, bRefresh;
        readonly Label lEvery, lVal, lState, lFoot;
        readonly ListView lv;
        readonly RamSchedule sched;

        public MemPage(RamSchedule s)
            : base("Memory", "Working set trimming and background application control")
        {
            sched = s;

            cAuto = Sect("Working Set Cleanup", 184);
            Host.Controls.Add(cAuto);

            lState = new Label
            {
                Font = new Font(Ui.Body, 9.5f),
                ForeColor = Ui.Dim,
                AutoSize = false,
                AutoEllipsis = true,
                BackColor = Color.Transparent,
                Location = new Point(Ui.D(20), Ui.D(46))
            };
            cAuto.Controls.Add(lState);

            bNow = new Btn { Text2 = "Trim Memory", Tone = Ui.Accent, Filled = true, Width = Ui.D(140) };
            bNow.Click += delegate
            {
                var r = Ram.Trim();
                lState.ForeColor = Ui.Green;
                lState.Text = string.Format("Freed +{0:N0} MB available RAM across {1} background processes", r.FreedMb, r.Touched);
                Refresh2();
            };
            cAuto.Controls.Add(bNow);

            tgAuto = new Toggle();
            tgAuto.Init(Cfg.RamAuto);
            tgAuto.Toggled += delegate
            {
                Cfg.RamAuto = tgAuto.On;
                sched.Reload();
                Interval(Cfg.RamMinutes);
            };
            Row(cAuto, 98, "Trim automatically when RAM usage exceeds 75% and no game is active",
                "Reclaims idle background working sets without touching active games or foreground windows.", tgAuto);

            lEvery = new Label
            {
                Text = "Interval",
                Font = new Font(Ui.Body, 9.25f),
                ForeColor = Ui.Text,
                AutoSize = true,
                BackColor = Color.Transparent,
                Location = new Point(Ui.D(20), Ui.D(148))
            };
            bMinus = new Btn { Text2 = "-", Width = Ui.D(32), Height = Ui.D(28) };
            bPlus = new Btn { Text2 = "+", Width = Ui.D(32), Height = Ui.D(28) };
            lVal = new Label
            {
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font(Ui.Body, 9.25f, FontStyle.Bold),
                ForeColor = Ui.Text,
                BackColor = Ui.PanelHi,
                Size = new Size(Ui.D(80), Ui.D(28))
            };
            bMinus.Click += delegate { Interval(Cfg.RamMinutes <= 10 ? 5 : Cfg.RamMinutes - 5); };
            bPlus.Click += delegate { Interval(Cfg.RamMinutes >= 240 ? 240 : Cfg.RamMinutes + 5); };
            cAuto.Controls.Add(lEvery);
            cAuto.Controls.Add(bMinus);
            cAuto.Controls.Add(lVal);
            cAuto.Controls.Add(bPlus);

            cList = Sect("Closable Background Applications", 0);
            Host.Controls.Add(cList);

            lv = DarkList.Make(true);
            lv.Columns.Add("Application", Ui.D(220));
            lv.Columns.Add("Memory", Ui.D(95));
            lv.Columns.Add("Window Title", Ui.D(260));
            lv.ItemChecked += OnMark;
            cList.Controls.Add(lv);

            lFoot = new Label
            {
                Font = new Font(Ui.Tiny, 8.5f),
                ForeColor = Ui.Dim,
                AutoSize = false,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = Color.Transparent
            };
            cList.Controls.Add(lFoot);

            bRefresh = new Btn { Text2 = "Refresh", Subtle = true, Width = Ui.D(96) };
            bRefresh.Click += delegate { lFoot.Text = ""; Refresh2(); };
            bClose = new Btn { Text2 = "Close Selected", Tone = Ui.Amber, Width = Ui.D(138) };
            bClose.Click += delegate { CloseMarked(); };
            cList.Controls.Add(bRefresh);
            cList.Controls.Add(bClose);

            Interval(Cfg.RamMinutes);
        }

        void Interval(int m)
        {
            Cfg.RamMinutes = m;
            lVal.Text = m + " min";
            bool on = Cfg.RamAuto;
            lEvery.Enabled = bMinus.Enabled = bPlus.Enabled = on;
            lVal.ForeColor = on ? Ui.Text : Ui.Dim;
            sched.Reload();
        }

        void OnMark(object s, ItemCheckedEventArgs e)
        {
            SaveMarks();
            UpdateFooter();
        }

        void SaveMarks()
        {
            var l = new List<string>();
            foreach (ListViewItem it in lv.Items) if (it.Checked) l.Add("" + it.Tag);
            Cfg.KillList = l;
        }

        void UpdateFooter()
        {
            int count = 0;
            foreach (ListViewItem it in lv.Items) if (it.Checked) count++;
            bClose.Enabled = count > 0;
            lFoot.ForeColor = Ui.Dim;
            lFoot.Text = count == 0
                ? "Select non-essential apps to close when freeing memory."
                : count + " application(s) selected";
        }

        void CloseMarked()
        {
            var marks = Cfg.KillList;
            if (marks.Count == 0) return;

            string list = string.Join(", ", marks.ToArray());
            if (MessageBox.Show(this,
                    "Close the following running applications?\r\n\r\n" + list + "\r\n\r\nUnsaved work in those applications may be lost.",
                    "Close Applications", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            var closed = new List<string>();
            int n = Ram.CloseMarked(marks, delegate (string who) { closed.Add(who); });
            Refresh2();
            lFoot.ForeColor = n > 0 ? Ui.Green : Ui.Dim;
            lFoot.Text = n == 0 ? "None of the selected applications were running." : "Closed: " + string.Join(", ", closed.ToArray());
        }

        public override void Refresh2()
        {
            var m = Ram.State();
            if (lState.Text.Length == 0 || lState.Text.StartsWith("In use"))
            {
                lState.ForeColor = Ui.Dim;
                lState.Text = string.Format("In use: {0:N1} of {1:N1} GB ({2}%)", m.UsedGb, m.TotalGb, m.Percent);
            }

            var bytes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    string n = p.ProcessName;
                    if (n.Length > 0 && p.SessionId == Ram.MySession)
                    {
                        long had;
                        bytes[n] = (bytes.TryGetValue(n, out had) ? had : 0L) + p.WorkingSet64;
                        string title = "";
                        try { title = p.MainWindowTitle; } catch { }
                        if (title.Length > 0 && !titles.ContainsKey(n)) titles[n] = title;
                    }
                }
                catch { }
                try { p.Dispose(); } catch { }
            }

            var names = new List<string>(bytes.Keys);
            names.Sort(delegate (string a, string b) { return bytes[b].CompareTo(bytes[a]); });

            var marks = Cfg.KillList;
            lv.BeginUpdate();
            lv.ItemChecked -= OnMark;
            lv.Items.Clear();
            foreach (string n in names)
            {
                if (!Ram.MayClose(n)) continue;
                var it = new ListViewItem(n);
                it.SubItems.Add(Mb(bytes[n]));
                string ttl;
                it.SubItems.Add(titles.TryGetValue(n, out ttl) ? ttl : "");
                it.Tag = n;
                it.Checked = marks.Contains(n);
                lv.Items.Add(it);
            }
            lv.ItemChecked += OnMark;
            lv.EndUpdate();
            DarkList.Fit(lv);
            UpdateFooter();
        }

        static string Mb(long b) { return string.Format("{0:N0} MB", b / 1048576.0); }

        protected override void Relayout()
        {
            base.Relayout();
            int w = Host.ClientSize.Width, h = Host.ClientSize.Height;
            cAuto.Bounds = new Rectangle(Ui.D(0), Ui.D(0), w, Ui.D(184));
            bNow.Location = new Point(w - bNow.Width - Ui.D(20), Ui.D(40));
            lState.Size = new Size(Math.Max(Ui.D(160), bNow.Left - Ui.D(32)), Ui.D(24));
            tgAuto.Location = new Point(w - tgAuto.Width - Ui.D(20), Ui.D(100));
            foreach (Control c in cAuto.Controls)
            {
                var l = c as Label;
                if (l != null && !l.AutoSize && l != lVal && l != lState)
                    l.Size = new Size(Math.Max(Ui.D(160), w - Ui.D(110)), Ui.D(34));
            }
            int sx = Ui.D(20) + lEvery.PreferredWidth + Ui.D(14);
            bMinus.Location = new Point(sx, Ui.D(144));
            lVal.Location = new Point(sx + Ui.D(38), Ui.D(144));
            bPlus.Location = new Point(sx + Ui.D(124), Ui.D(144));

            int top = Ui.D(196);
            cList.Bounds = new Rectangle(Ui.D(0), top, w, Math.Max(Ui.D(190), h - top));
            lv.Bounds = new Rectangle(Ui.D(14), Ui.D(44), w - Ui.D(28), cList.Height - Ui.D(44) - Ui.D(54));
            DarkList.Fit(lv);

            int by = cList.Height - Ui.D(44);
            bClose.Location = new Point(w - bClose.Width - Ui.D(14), by);
            bRefresh.Location = new Point(bClose.Left - bRefresh.Width - Ui.D(8), by);
            lFoot.Bounds = new Rectangle(Ui.D(16), by + Ui.D(2), Math.Max(Ui.D(100), bRefresh.Left - Ui.D(24)), Ui.D(30));
        }
    }

    // ------------------------------------------------------------------ Services
    class SvcPage : Page
    {
        readonly Panel cList;
        readonly ListView lv;
        readonly Btn bAuto, bManual, bOff, bRestore, bRefresh;
        readonly Label lHint, lStatus;
        List<SvcInfo> data = new List<SvcInfo>();
        volatile bool loading;

        public SvcPage()
            : base("Services", "Windows service startup configuration")
        {
            cList = Sect("", 0);
            Host.Controls.Add(cList);

            lHint = new Label
            {
                Text = "Core security, update, and boot services are locked for safety. Highlighted OEM and telemetry services can be set to Manual to reduce background memory use.",
                Font = new Font(Ui.Tiny, 8.75f),
                ForeColor = Ui.Dim,
                AutoSize = false,
                BackColor = Color.Transparent,
                Location = new Point(Ui.D(16), Ui.D(10)),
                Size = new Size(Ui.D(600), Ui.D(32))
            };
            cList.Controls.Add(lHint);

            lv = DarkList.Make(false);
            lv.Columns.Add("Service", Ui.D(260));
            lv.Columns.Add("State", Ui.D(90));
            lv.Columns.Add("Startup", Ui.D(145));
            lv.Columns.Add("Recommendation", Ui.D(200));
            lv.SelectedIndexChanged += delegate { Sync(); };
            cList.Controls.Add(lv);

            bAuto = new Btn { Text2 = "Automatic", Width = Ui.D(102) };
            bManual = new Btn { Text2 = "Manual", Width = Ui.D(92) };
            bOff = new Btn { Text2 = "Disable", Tone = Ui.Amber, Width = Ui.D(92) };
            bRestore = new Btn { Text2 = "Restore", Tone = Ui.Green, Width = Ui.D(118) };
            bRefresh = new Btn { Text2 = "Refresh", Subtle = true, Width = Ui.D(92) };

            bAuto.Click += delegate { Change("Auto"); };
            bManual.Click += delegate { Change("Manual"); };
            bOff.Click += delegate { Change("Disabled"); };
            bRefresh.Click += delegate { lStatus.Text = ""; Refresh2(); };
            bRestore.Click += delegate { RestoreAll(); };

            lStatus = new Label
            {
                Font = new Font(Ui.Tiny, 8.5f),
                ForeColor = Ui.Dim,
                AutoSize = false,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = Color.Transparent
            };

            cList.Controls.Add(bAuto);
            cList.Controls.Add(bManual);
            cList.Controls.Add(bOff);
            cList.Controls.Add(lStatus);
            cList.Controls.Add(bRestore);
            cList.Controls.Add(bRefresh);
        }

        SvcInfo Current()
        {
            if (lv.SelectedItems.Count == 0) return null;
            return lv.SelectedItems[0].Tag as SvcInfo;
        }

        void Sync()
        {
            var s = Current();
            bool can = s != null && !s.Protected && s.StartMode != "Boot" && s.StartMode != "System";
            bAuto.Enabled = bManual.Enabled = bOff.Enabled = can;
            bRestore.Enabled = Svc.BackupCount() > 0;
        }

        void Change(string mode)
        {
            var s = Current();
            if (s == null) return;
            if (mode == "Disabled" && MessageBox.Show(this,
                    "Disable service \"" + s.Display + "\"?\r\n\r\nYou can restore all modified services at any time with Restore.",
                    "Disable Service", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            string err = Svc.SetMode(s.Name, s.StartMode, mode);
            if (err != null)
            {
                lStatus.ForeColor = Ui.Amber;
                lStatus.Text = "Could not change " + s.Name + ": " + err;
            }
            else
            {
                lStatus.ForeColor = Ui.Green;
                lStatus.Text = s.Display + " set to " + mode;
            }
            Refresh2();
        }

        void RestoreAll()
        {
            int had = Svc.BackupCount();
            if (had == 0) return;
            int n = Svc.RestoreAll(delegate { });
            lStatus.ForeColor = Ui.Green;
            lStatus.Text = "Restored " + n + " service(s) to original startup state";
            Refresh2();
        }

        public override void Refresh2()
        {
            if (loading) return;
            loading = true;
            var th = new System.Threading.Thread(delegate ()
            {
                List<SvcInfo> got;
                try { got = Svc.List(); }
                catch { got = new List<SvcInfo>(); }
                try { BeginInvoke((Action<List<SvcInfo>>)Fill, got); }
                catch { }
                loading = false;
            });
            th.IsBackground = true;
            th.Start();
        }

        void Fill(List<SvcInfo> got)
        {
            data = got;
            lv.BeginUpdate();
            lv.Items.Clear();
            foreach (var s in data)
            {
                var it = new ListViewItem(s.Display);
                it.SubItems.Add(s.State == "Running" ? "Running" : "Stopped");
                it.SubItems.Add(Svc.Human(s.StartMode) + (s.Protected ? " (Locked)" : ""));
                bool advise;
                it.SubItems.Add(Advice.ForService(s.Name, out advise));
                it.Tag = s;
                it.ForeColor = s.Protected ? Color.FromArgb(108, 112, 122)
                             : advise ? Ui.Amber : Ui.Text;
                lv.Items.Add(it);
            }
            lv.EndUpdate();
            DarkList.Fit(lv);
            int bk = Svc.BackupCount();
            bRestore.Text2 = bk > 0 ? "Restore (" + bk + ")" : "Restore";
            bRestore.Invalidate();
            Sync();
        }

        protected override void Relayout()
        {
            base.Relayout();
            int w = Host.ClientSize.Width, h = Host.ClientSize.Height;
            cList.Bounds = new Rectangle(Ui.D(0), Ui.D(0), w, Math.Max(Ui.D(240), h));
            lHint.Size = new Size(w - Ui.D(32), Ui.D(32));
            lv.Bounds = new Rectangle(Ui.D(14), Ui.D(46), w - Ui.D(28), cList.Height - Ui.D(46) - Ui.D(54));
            DarkList.Fit(lv);

            int y = cList.Height - Ui.D(44);
            bAuto.Location = new Point(Ui.D(14), y);
            bManual.Location = new Point(bAuto.Right + Ui.D(6), y);
            bOff.Location = new Point(bManual.Right + Ui.D(6), y);

            bRestore.Location = new Point(w - bRestore.Width - Ui.D(14), y);
            bRefresh.Location = new Point(bRestore.Left - bRefresh.Width - Ui.D(6), y);

            int statusLeft = bOff.Right + Ui.D(10);
            int statusW = Math.Max(0, bRefresh.Left - statusLeft - Ui.D(8));
            lStatus.Bounds = new Rectangle(statusLeft, y + Ui.D(2), statusW, Ui.D(30));
            lStatus.Visible = statusW >= Ui.D(80);
        }
    }

    // ------------------------------------------------------------------ Optimization (Tweaks)
    class TweakPage : Page
    {
        readonly Engine eng;
        readonly Panel filterBar, host2;
        readonly TextBox txtSearch;
        readonly List<Btn> catBtns = new List<Btn>();
        readonly List<string> catGroups = new List<string>();
        readonly List<CheckRow> rows = new List<CheckRow>();
        readonly List<Label> heads = new List<Label>();
        readonly Btn bApply, bRevert, bRec, bAll, bDiag;
        readonly Label lFeedback;
        readonly Action<Action> run;
        string activeGroup = "";
        bool allOn;

        public TweakPage(Engine e, Action<Action> runner)
            : base("Tweaks", "Verified system optimizations with instant state inspection")
        {
            eng = e;
            run = runner;

            filterBar = new Panel { BackColor = Ui.Bg };
            Controls.Add(filterBar);

            txtSearch = new TextBox
            {
                BackColor = Ui.Panel,
                ForeColor = Ui.Text,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font(Ui.Body, 9.25f)
            };
            txtSearch.TextChanged += delegate { Rows(); };
            filterBar.Controls.Add(txtSearch);

            AddCategoryButton("All", "");

            string group = null;
            host2 = new Panel { AutoScroll = true, BackColor = Ui.Bg };
            host2.HandleCreated += delegate { Ui.DarkScroll(host2); };
            Host.Controls.Add(host2);

            foreach (var t in eng.tweaks)
            {
                Label h = null;
                if (t.Group != group)
                {
                    group = t.Group;
                    h = Ui.Section(group);
                    host2.Controls.Add(h);
                    AddCategoryButton(ShortGroupName(group), group);
                }
                heads.Add(h);
                var r = new CheckRow { Item = t, Checked = t.Default };
                host2.Controls.Add(r);
                rows.Add(r);
            }

            bApply = new Btn { Text2 = "Apply Selected", Tone = Ui.Green, Filled = true, Width = Ui.D(134) };
            bApply.Click += delegate { Go(false); };
            bRevert = new Btn { Text2 = "Revert", Tone = Ui.Amber, Width = Ui.D(88) };
            bRevert.Click += delegate { Go(true); };
            bRec = new Btn { Text2 = "Recommended", Subtle = true, Width = Ui.D(112) };
            bRec.Click += delegate { SelectRecommended(); };
            bAll = new Btn { Text2 = "Select All", Subtle = true, Width = Ui.D(92) };
            bAll.Click += delegate { ToggleAll(); };
            bDiag = new Btn { Text2 = "Diagnostics", Subtle = true, Width = Ui.D(102) };
            bDiag.Click += delegate { run(delegate { eng.RunDiagnose(); }); };

            lFeedback = new Label
            {
                Font = new Font(Ui.Tiny, 8.5f),
                ForeColor = Ui.Dim,
                AutoSize = true,
                BackColor = Color.Transparent
            };

            Controls.Add(bApply);
            Controls.Add(bRevert);
            Controls.Add(bRec);
            Controls.Add(bAll);
            Controls.Add(bDiag);
            Controls.Add(lFeedback);

            UpdateCategoryHighlight();
        }

        static string ShortGroupName(string g)
        {
            if (g == null) return "Other";
            if (g.IndexOf("Responsiveness", StringComparison.OrdinalIgnoreCase) >= 0 ||
                g.IndexOf("System", StringComparison.OrdinalIgnoreCase) >= 0) return "System";
            if (g.IndexOf("Graphics", StringComparison.OrdinalIgnoreCase) >= 0 ||
                g.IndexOf("GPU", StringComparison.OrdinalIgnoreCase) >= 0 ||
                g.IndexOf("Display", StringComparison.OrdinalIgnoreCase) >= 0) return "Graphics";
            if (g.IndexOf("Memory", StringComparison.OrdinalIgnoreCase) >= 0 ||
                g.IndexOf("Storage", StringComparison.OrdinalIgnoreCase) >= 0 ||
                g.IndexOf("Filesystem", StringComparison.OrdinalIgnoreCase) >= 0) return "Memory & Disk";
            if (g.IndexOf("Telemetry", StringComparison.OrdinalIgnoreCase) >= 0 ||
                g.IndexOf("Background", StringComparison.OrdinalIgnoreCase) >= 0 ||
                g.IndexOf("Privacy", StringComparison.OrdinalIgnoreCase) >= 0) return "Background";
            if (g.IndexOf("Network", StringComparison.OrdinalIgnoreCase) >= 0) return "Network";
            if (g.IndexOf("Hardware", StringComparison.OrdinalIgnoreCase) >= 0 ||
                g.IndexOf("BIOS", StringComparison.OrdinalIgnoreCase) >= 0) return "Hardware";
            if (g.IndexOf("Security", StringComparison.OrdinalIgnoreCase) >= 0 ||
                g.IndexOf("Trade-off", StringComparison.OrdinalIgnoreCase) >= 0 ||
                g.IndexOf("Optional", StringComparison.OrdinalIgnoreCase) >= 0) return "Advanced";
            return g.Length > 14 ? g.Substring(0, 14) : g;
        }

        void AddCategoryButton(string label, string groupKey)
        {
            foreach (string existing in catGroups)
                if (existing == groupKey) return;

            var b = new Btn
            {
                Text2 = label,
                Height = Ui.D(28),
                Width = Math.Max(Ui.D(54), TextRenderer.MeasureText(label, new Font(Ui.Body, 8.75f, FontStyle.Bold)).Width + Ui.D(22))
            };
            string key = groupKey;
            b.Click += delegate
            {
                activeGroup = key;
                UpdateCategoryHighlight();
                Rows();
            };
            catBtns.Add(b);
            catGroups.Add(groupKey);
            filterBar.Controls.Add(b);
        }

        void UpdateCategoryHighlight()
        {
            for (int i = 0; i < catBtns.Count; i++)
            {
                bool active = catGroups[i] == activeGroup;
                catBtns[i].Filled = active;
                catBtns[i].Subtle = !active;
                catBtns[i].Tone = Ui.Accent;
                catBtns[i].Invalidate();
            }
        }

        public override void Refresh2()
        {
            eng.RefreshTweaks();
            Rows();
            foreach (var r in rows) r.Invalidate();
            eng.WarmSlowChecksAsync(delegate
            {
                if (!IsDisposed && IsHandleCreated)
                {
                    BeginInvoke((Action)delegate
                    {
                        eng.RefreshTweaks();
                        Rows();
                        foreach (var r in rows) r.Invalidate();
                    });
                }
            });
        }

        void SelectRecommended()
        {
            allOn = false;
            bAll.Text2 = "Select All";
            bAll.Invalidate();
            foreach (var r in rows)
            {
                r.Checked = r.Item.Default;
                r.Invalidate();
            }
            lFeedback.Text = "Recommended safe defaults selected";
        }

        void ToggleAll()
        {
            allOn = !allOn;
            foreach (var r in rows)
            {
                if (r.Visible) { r.Checked = allOn; r.Invalidate(); }
            }
            bAll.Text2 = allOn ? "Clear All" : "Select All";
            bAll.Invalidate();
            lFeedback.Text = "";
        }

        void Go(bool revert)
        {
            var sel = new List<Tweak>();
            foreach (var r in rows) if (r.Checked) sel.Add(r.Item);
            if (sel.Count == 0)
            {
                lFeedback.ForeColor = Ui.Amber;
                lFeedback.Text = "Select at least one tweak first.";
                return;
            }
            lFeedback.Text = "";
            if (revert) run(delegate { eng.RunRevert(sel); });
            else run(delegate { eng.RunApply(sel); });
        }

        protected override void Relayout()
        {
            base.Relayout();
            int w = ClientSize.Width, h = ClientSize.Height;
            int padX = CompactHeader ? Ui.D(0) : Ui.D(28);
            int topY = CompactHeader ? Ui.D(4) : Ui.D(84);
            int contentW = Math.Max(Ui.D(240), w - padX * 2);

            // Layout filter bar (search + category pills)
            int searchW = Math.Min(Ui.D(190), Math.Max(Ui.D(120), contentW / 4));
            txtSearch.Bounds = new Rectangle(contentW - searchW, Ui.D(2), searchW, Ui.D(26));

            int cx = 0, cy = 0;
            int maxCatRight = txtSearch.Left - Ui.D(10);
            for (int i = 0; i < catBtns.Count; i++)
            {
                var b = catBtns[i];
                if (cx > 0 && cx + b.Width > maxCatRight)
                {
                    cx = 0;
                    cy += Ui.D(32);
                }
                b.Location = new Point(cx, cy);
                cx += b.Width + Ui.D(6);
            }
            int filterH = cy + Ui.D(34);
            filterBar.Bounds = new Rectangle(padX, topY, contentW, filterH);

            int hostTop = filterBar.Bottom + Ui.D(4);
            int bottomBarH = Ui.D(48);
            Host.Bounds = new Rectangle(padX, hostTop, contentW, Math.Max(Ui.D(80), h - hostTop - bottomBarH));
            host2.Bounds = new Rectangle(0, 0, Host.ClientSize.Width, Host.ClientSize.Height);
            Rows();

            int by = h - Ui.D(40);
            bApply.Location = new Point(padX, by);
            bRevert.Location = new Point(bApply.Right + Ui.D(8), by);

            bDiag.Location = new Point(padX + contentW - bDiag.Width, by);
            bAll.Location = new Point(bDiag.Left - bAll.Width - Ui.D(6), by);
            bRec.Location = new Point(bAll.Left - bRec.Width - Ui.D(6), by);

            lFeedback.Location = new Point(bRevert.Right + Ui.D(12), by + Ui.D(8));
            lFeedback.Visible = lFeedback.Right < bRec.Left - Ui.D(8);
        }

        void Rows()
        {
            if (host2 == null) return;
            int w = host2.ClientSize.Width - Ui.D(22);
            if (w < Ui.D(260)) w = Ui.D(260);

            string q = txtSearch != null ? txtSearch.Text.Trim() : "";
            int y = Ui.D(4);
            string shownGroup = null;

            host2.SuspendLayout();
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                var t = r.Item;
                bool matchGroup = string.IsNullOrEmpty(activeGroup) ||
                                  string.Equals(t.Group, activeGroup, StringComparison.OrdinalIgnoreCase);
                bool matchQuery = q.Length == 0 ||
                                  t.Title.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                  t.Detail.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                  t.Group.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;

                bool show = matchGroup && matchQuery;
                r.Visible = show;
                if (heads[i] != null) heads[i].Visible = false;

                if (!show) continue;

                if (t.Group != shownGroup)
                {
                    shownGroup = t.Group;
                    if (heads[i] != null)
                    {
                        y += Ui.D(y <= Ui.D(6) ? 2 : 14);
                        heads[i].Visible = true;
                        heads[i].Location = new Point(Ui.D(14), y);
                        y += Ui.D(24);
                    }
                }

                int hh = r.Measure(w);
                r.Bounds = new Rectangle(Ui.D(4), y, w, hh);
                y += hh;
            }
            host2.ResumeLayout();
        }
    }

    // ------------------------------------------------------------------ Log
    class LogPage : Page
    {
        public readonly RichTextBox Box;

        public LogPage() : base("Log", "Chronological history of applied changes")
        {
            Box = new RichTextBox
            {
                ReadOnly = true,
                BackColor = Color.FromArgb(18, 19, 23),
                ForeColor = Ui.Dim,
                Font = new Font("Cascadia Mono", 9f),
                BorderStyle = BorderStyle.None,
                Dock = DockStyle.Fill
            };
            Box.HandleCreated += delegate { Ui.DarkScroll(Box); };
            Host.Controls.Add(Box);
        }

        public override void Refresh2()
        {
            var lines = Tail(400);
            Box.SuspendLayout();
            Box.Clear();
            string day = "";
            foreach (string raw in lines)
            {
                if (raw.Length < 21 || raw[4] != '-' || raw[10] != ' ') continue;
                string d = raw.Substring(0, 10), tm = raw.Substring(11, 5), s = raw.Substring(21);
                if (s.StartsWith("RUN ") || s.StartsWith("REG ")) continue;
                if (s.Trim().Length == 0) continue;
                if (d != day)
                {
                    day = d;
                    Add((Box.TextLength > 0 ? "\r\n" : "") + "— " + d.Substring(8, 2) + "." + d.Substring(5, 2) + "." + d.Substring(0, 4) + " —", Ui.Accent);
                }
                Color c = s.Contains("[+]") ? Ui.Green
                        : s.Contains("[!]") ? Ui.Amber
                        : s.Contains("[x]") || s.Contains("could not") || s.Contains("failed") ? Color.FromArgb(240, 130, 130)
                        : s.StartsWith("==") ? Ui.Accent : Ui.Dim;
                Add(tm + "  " + s, c);
            }
            if (Box.TextLength == 0) Add("No activity recorded yet. Changes made by PC-Tune will appear here.", Ui.Dim);
            Box.ResumeLayout();
            Box.SelectionStart = Box.TextLength;
            Box.ScrollToCaret();
        }

        void Add(string s, Color c)
        {
            Box.SelectionStart = Box.TextLength;
            Box.SelectionColor = c;
            Box.AppendText(s + "\r\n");
        }

        static List<string> Tail(int n)
        {
            var all = new List<string>();
            try
            {
                using (var fs = new System.IO.FileStream(Sys.LogFile, System.IO.FileMode.Open,
                           System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete))
                using (var r = new System.IO.StreamReader(fs, System.Text.Encoding.UTF8))
                {
                    if (fs.Length > 400000) { fs.Seek(-400000, System.IO.SeekOrigin.End); r.ReadLine(); }
                    string line;
                    while ((line = r.ReadLine()) != null) all.Add(line);
                }
            }
            catch { }
            var keep = new List<string>();
            for (int i = all.Count - 1; i >= 0 && keep.Count < n; i--)
            {
                string s = all[i];
                if (s.Length > 21 && (s.Substring(21).StartsWith("RUN ") || s.Substring(21).StartsWith("REG "))) continue;
                keep.Add(s);
            }
            keep.Reverse();
            return keep;
        }
    }
}
