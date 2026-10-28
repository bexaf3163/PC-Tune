// Background management pages: Startup programs and Disk Cleanup.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PCTune
{
    // ======================================================== Startup
    class StartPage : Page
    {
        readonly Panel cList;
        readonly ListView lv;
        readonly Btn bOff, bRestore, bRefresh;
        readonly Label lHint, lSum;
        readonly Action<Action> run;
        List<StItem> data = new List<StItem>();
        bool filling;
        bool loading;

        public StartPage(Action<Action> r)
            : base("Startup", "Programs that launch automatically at Windows sign-in")
        {
            run = r;

            cList = Sect("", 0);
            Host.Controls.Add(cList);

            lHint = new Label
            {
                Text = "Checked entries can be safely skipped at sign-in and started manually when needed. Double-click any item to toggle it individually.",
                Font = new Font(Ui.Tiny, 8.75f),
                ForeColor = Ui.Dim,
                AutoSize = false,
                BackColor = Color.Transparent,
                Location = new Point(Ui.D(16), Ui.D(10)),
                Size = new Size(Ui.D(600), Ui.D(32))
            };
            cList.Controls.Add(lHint);

            lv = DarkList.Make(true);
            lv.Columns.Add("Program", Ui.D(200));
            lv.Columns.Add("Recommendation", Ui.D(145));
            lv.Columns.Add("Source", Ui.D(115));
            lv.Columns.Add("State", Ui.D(80));
            lv.Columns.Add("Description", Ui.D(320));
            lv.ItemCheck += Veto;
            lv.DoubleClick += delegate { Toggle(); };
            lv.ItemChecked += delegate { if (!filling) Sum(); };
            cList.Controls.Add(lv);

            lSum = new Label
            {
                Font = new Font(Ui.Tiny, 8.75f),
                ForeColor = Ui.Dim,
                AutoSize = false,
                AutoEllipsis = true,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft,
                Size = new Size(Ui.D(240), Ui.D(30))
            };
            cList.Controls.Add(lSum);

            bOff = new Btn { Text2 = "Disable Selected", Tone = Ui.Amber, Filled = true, Width = Ui.D(142) };
            bOff.Click += delegate { Apply(); };
            bRestore = new Btn { Text2 = "Restore", Tone = Ui.Green, Width = Ui.D(114) };
            bRestore.Click += delegate { Restore(); };
            bRefresh = new Btn { Text2 = "Refresh", Subtle = true, Width = Ui.D(92) };
            bRefresh.Click += delegate { Refresh2(); };
            cList.Controls.Add(bOff);
            cList.Controls.Add(bRestore);
            cList.Controls.Add(bRefresh);
        }

        void Veto(object s, ItemCheckEventArgs e)
        {
            if (filling) return;
            var it = lv.Items[e.Index].Tag as StItem;
            if ((it == null || it.Locked || !it.Enabled) && e.NewValue == CheckState.Checked)
                e.NewValue = CheckState.Unchecked;
        }

        public override void Refresh2()
        {
            if (loading) return;
            loading = true;
            var th = new System.Threading.Thread(delegate ()
            {
                List<StItem> got;
                try { got = Startup.List(); }
                catch { got = new List<StItem>(); }
                try { BeginInvoke((Action<List<StItem>>)Show, got); }
                catch { }
                loading = false;
            });
            th.IsBackground = true;
            th.Start();
        }

        void Show(List<StItem> got)
        {
            data = got;
            Fill();
        }

        string lastNotice = "";
        Color lastNoticeColor = Ui.Dim;

        void Toggle()
        {
            if (lv.SelectedItems.Count == 0) return;
            var it = lv.SelectedItems[0].Tag as StItem;
            if (it == null) return;
            if (it.Locked)
            {
                lSum.ForeColor = Ui.Amber;
                lSum.Text = it.Name + " is protected (" + it.What + ")";
                return;
            }
            bool on = !it.Enabled;

            if (!on && !it.Advise)
            {
                string ramLine = it.EstRamMb >= 5 ? "\r\nActive RAM: ~" + it.EstRamMb + " MB" : "";
                string msg = it.Name + "\r\n" + it.What + ramLine +
                             "\r\n\r\nCategory: " + it.CategoryLabel +
                             "\r\nSource: " + it.Where +
                             "\r\nTarget: " + it.Cmd +
                             "\r\n\r\nDisable at startup?";
                if (MessageBox.Show(this, msg, "Startup Decision", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                    return;
            }

            run(delegate
            {
                bool ok = Startup.SetEnabled(it, on);
                if (!ok)
                {
                    Sys.ToFile("STARTUP: could not " + (on ? "enable " : "disable ") + it.Name);
                    lastNoticeColor = Ui.Amber;
                    lastNotice = "Could not " + (on ? "enable " : "disable ") + it.Name + " (administrator privileges required)";
                }
                else
                {
                    lastNoticeColor = Ui.Green;
                    lastNotice = it.Name + " " + (on ? "enabled" : "disabled");
                }
                try { BeginInvoke((Action)Refresh2); }
                catch { }
            });
        }

        void Fill()
        {
            filling = true;
            lv.BeginUpdate();
            lv.Items.Clear();
            foreach (var it in data)
            {
                var row = new ListViewItem(it.Locked ? it.Name + " (System)" : it.Name);
                row.SubItems.Add(it.CategoryLabel);
                row.SubItems.Add(it.Where);
                row.SubItems.Add(it.Enabled ? "Enabled" : "Disabled");
                row.SubItems.Add(it.What);
                row.Tag = it;
                if (!it.Enabled || it.Locked) row.ForeColor = Ui.Dim;
                else if (it.Advise) row.ForeColor = Ui.Amber;
                lv.Items.Add(row);
                row.Checked = it.Advise && it.Enabled && !it.Locked;
            }
            lv.EndUpdate();
            DarkList.Fit(lv);
            filling = false;
            Sum();
            if (lastNotice.Length > 0)
            {
                lSum.ForeColor = lastNoticeColor;
                lSum.Text = lastNotice;
                lastNotice = "";
            }
        }

        List<StItem> Marked()
        {
            var l = new List<StItem>();
            foreach (ListViewItem r in lv.Items)
            {
                var it = r.Tag as StItem;
                if (r.Checked && it != null && it.Enabled && !it.Locked) l.Add(it);
            }
            return l;
        }

        void Sum()
        {
            int n = Marked().Count;
            int bk = Startup.BackupCount() + Svc.BackupCount();
            lSum.ForeColor = Ui.Dim;
            lSum.Text = n == 0 ? "No startup items selected"
                               : n + " item(s) selected to disable";
            bOff.Enabled = n > 0;
            bRestore.Enabled = bk > 0;
            bRestore.Text2 = bk > 0 ? "Restore (" + bk + ")" : "Restore";
            bRestore.Invalidate();
        }

        void Apply()
        {
            var sel = Marked();
            if (sel.Count == 0) return;
            string names = "";
            foreach (var it in sel) names += "\r\n   • " + it.Name + " (" + it.CategoryLabel + ")";
            if (MessageBox.Show(
                    "Disable " + sel.Count + " startup item(s)?" + names +
                    "\r\n\r\nApplications remain installed and can be launched normally.",
                    "Disable Startup Items", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;

            run(delegate
            {
                int ok = 0;
                foreach (var it in sel) if (Startup.SetEnabled(it, false)) ok++;
                Sys.ToFile("STARTUP: disabled " + ok + " of " + sel.Count);
                lastNoticeColor = ok == sel.Count ? Ui.Green : Ui.Amber;
                lastNotice = "Disabled " + ok + " of " + sel.Count + " startup item(s)";
                try { BeginInvoke((Action)Refresh2); }
                catch { }
            });
        }

        void Restore()
        {
            run(delegate
            {
                int n = Startup.RestoreAll(delegate (string s) { Sys.ToFile("ROLLBACK " + s); });
                int ns = Svc.RestoreAll(delegate (string s) { Sys.ToFile("ROLLBACK SVC " + s); });
                int tot = n + ns;
                Sys.ToFile("STARTUP: entries restored " + tot);
                lastNoticeColor = Ui.Green;
                lastNotice = "Restored " + tot + " startup/background item(s)";
                try { BeginInvoke((Action)Refresh2); }
                catch { }
            });
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
            bRefresh.Location = new Point(w - bRefresh.Width - Ui.D(14), y);
            bRestore.Location = new Point(bRefresh.Left - bRestore.Width - Ui.D(8), y);
            bOff.Location = new Point(bRestore.Left - bOff.Width - Ui.D(8), y);

            lSum.Bounds = new Rectangle(Ui.D(16), y + Ui.D(2), Math.Max(Ui.D(80), bOff.Left - Ui.D(24)), Ui.D(30));
        }
    }

    // ======================================================== Disk Cleanup
    class CleanPage : Page
    {
        readonly Panel cList;
        readonly ListView lv;
        readonly Btn bScan, bSweep;
        readonly Label lHint, lSum;
        readonly Action<Action> run;
        List<CleanTarget> data = new List<CleanTarget>();
        bool filling, scanned;

        public CleanPage(Action<Action> r)
            : base("Cleanup", "Temporary files, shader caches, and diagnostic logs")
        {
            run = r;

            cList = Sect("", 0);
            Host.Controls.Add(cList);

            lHint = new Label
            {
                Text = "Removes safe temporary files and caches. User documents, browser passwords, and the Recycle Bin are never touched.",
                Font = new Font(Ui.Tiny, 8.75f),
                ForeColor = Ui.Dim,
                AutoSize = false,
                BackColor = Color.Transparent,
                Location = new Point(Ui.D(16), Ui.D(10)),
                Size = new Size(Ui.D(700), Ui.D(32))
            };
            cList.Controls.Add(lHint);

            lv = DarkList.Make(true);
            lv.Columns.Add("Category", Ui.D(260));
            lv.Columns.Add("Size", Ui.D(110));
            lv.Columns.Add("Details", Ui.D(380));
            lv.ItemChecked += delegate { if (!filling) Sum(); };
            cList.Controls.Add(lv);

            lSum = new Label
            {
                Font = new Font(Ui.Body, 9.5f, FontStyle.Bold),
                ForeColor = Ui.Text,
                AutoSize = false,
                AutoEllipsis = true,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft,
                Size = new Size(Ui.D(300), Ui.D(30))
            };
            cList.Controls.Add(lSum);

            bScan = new Btn { Text2 = "Scan", Width = Ui.D(110) };
            bScan.Click += delegate { Scan(); };
            bSweep = new Btn { Text2 = "Clean Selected", Tone = Ui.Amber, Filled = true, Width = Ui.D(148) };
            bSweep.Click += delegate { Sweep(); };
            cList.Controls.Add(bScan);
            cList.Controls.Add(bSweep);
        }

        public override void Refresh2()
        {
            if (data.Count == 0)
            {
                data = Clean.Build();
                Fill();
            }
        }

        void Fill()
        {
            filling = true;
            lv.BeginUpdate();
            lv.Items.Clear();
            foreach (var t in data)
            {
                var row = new ListViewItem(t.Title);
                row.SubItems.Add(t.Problem.Length > 0 ? t.Problem
                                : !scanned ? "—" : Clean.Size(t.Bytes));
                row.SubItems.Add(t.Hint);
                row.Tag = t;
                if (t.Problem.Length > 0) row.ForeColor = Ui.Dim;
                lv.Items.Add(row);
                row.Checked = t.Advise;
            }
            lv.EndUpdate();
            DarkList.Fit(lv);
            filling = false;
            Sum();
        }

        List<CleanTarget> Marked()
        {
            var l = new List<CleanTarget>();
            foreach (ListViewItem r in lv.Items)
            {
                var t = r.Tag as CleanTarget;
                if (r.Checked && t != null && t.Problem.Length == 0) l.Add(t);
            }
            return l;
        }

        void Sum()
        {
            long b = 0;
            int n = 0;
            foreach (var t in Marked()) { b += t.Bytes; n += t.Files; }
            lSum.ForeColor = scanned && b > 0 ? Ui.Text : Ui.Dim;
            lSum.Text = !scanned ? "Click Scan to calculate reclaimable space"
                      : b == 0 ? "No temporary files to clean"
                      : "Reclaimable: " + Clean.Size(b) + " (" + n + " files)";
            bSweep.Enabled = scanned && b > 0;
        }

        void Scan()
        {
            run(delegate
            {
                data = Clean.Build();
                foreach (var t in data) Clean.Scan(t);
                scanned = true;
                long b = 0;
                foreach (var t in data) b += t.Bytes;
                Sys.ToFile("CLEANUP: counted " + Clean.Size(b));
                try { BeginInvoke((Action)Fill); }
                catch { }
            });
        }

        void Sweep()
        {
            var sel = Marked();
            if (sel.Count == 0) return;
            long b = 0;
            string what = "";
            foreach (var t in sel) { b += t.Bytes; what += "\r\n   • " + t.Title + " — " + Clean.Size(t.Bytes); }
            if (MessageBox.Show(
                    "Delete " + Clean.Size(b) + " of temporary files?" + what +
                    "\r\n\r\nWindows and applications recreate cache files automatically as needed.",
                    "Confirm Cleanup", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                return;

            run(delegate
            {
                long freed = 0;
                foreach (var t in sel)
                    freed += Clean.Sweep(t, delegate (string s) { Sys.ToFile("CLEANUP " + s); }).Bytes;
                Sys.ToFile("CLEANUP: freed " + Clean.Size(freed));
                foreach (var t in data) Clean.Scan(t);
                try { BeginInvoke((Action)Fill); }
                catch { }
            });
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
            bSweep.Location = new Point(w - bSweep.Width - Ui.D(14), y);
            bScan.Location = new Point(bSweep.Left - bScan.Width - Ui.D(8), y);
            lSum.Bounds = new Rectangle(Ui.D(16), y + Ui.D(2), Math.Max(Ui.D(100), bScan.Left - Ui.D(24)), Ui.D(30));
        }
    }
}
