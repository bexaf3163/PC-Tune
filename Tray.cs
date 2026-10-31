// Minimal system tray integration: Open, Prepare for Game / Return to Normal, Mode switch, Show/Hide Widget, and Exit.
using System;
using System.Drawing;
using System.Windows.Forms;

namespace PCTune
{
    class DarkMenu : ToolStripProfessionalRenderer
    {
        public DarkMenu() : base(new Colors()) { }

        class Colors : ProfessionalColorTable
        {
            public override Color MenuItemSelected { get { return Color.FromArgb(40, 46, 60); } }
            public override Color MenuItemSelectedGradientBegin { get { return Color.FromArgb(40, 46, 60); } }
            public override Color MenuItemSelectedGradientEnd { get { return Color.FromArgb(40, 46, 60); } }
            public override Color MenuItemBorder { get { return Color.FromArgb(52, 60, 78); } }
            public override Color MenuBorder { get { return Ui.Line; } }
            public override Color ToolStripDropDownBackground { get { return Ui.Panel; } }
            public override Color ImageMarginGradientBegin { get { return Ui.Panel; } }
            public override Color ImageMarginGradientMiddle { get { return Ui.Panel; } }
            public override Color ImageMarginGradientEnd { get { return Ui.Panel; } }
            public override Color SeparatorDark { get { return Ui.Line; } }
            public override Color SeparatorLight { get { return Ui.Line; } }
            public override Color CheckBackground { get { return Color.FromArgb(40, 46, 60); } }
        }
    }

    static class Tray
    {
        static NotifyIcon ni;
        static ContextMenuStrip menu;
        static ToolStripMenuItem itHead, itOpen, itBoost, itWidget, itAiPing, itExit;
        static ToolStripMenuItem itBal, itGame, itComp, itPerf, itEco, itCar;
        static Shell shell;
        static Action openWindow;

        public static void Init(Shell s, Action open)
        {
            shell = s;
            openWindow = open;
            Refresh2();
        }

        public static void Refresh2()
        {
            if (!Cfg.TrayEnabled)
            {
                Kill();
                return;
            }
            if (ni != null && menu != null && !menu.IsDisposed)
            {
                Tip();
                return;
            }

            menu = new ContextMenuStrip
            {
                Renderer = new DarkMenu(),
                BackColor = Ui.Panel,
                ForeColor = Ui.Text,
                Font = new Font(Ui.Body, 9.25f),
                ShowImageMargin = false
            };

            itHead = Item("PC-Tune", null);
            itHead.Enabled = false;
            menu.Items.Add(itHead);
            menu.Items.Add(new ToolStripSeparator());

            itOpen = Item("Open PC-Tune", delegate { if (openWindow != null) openWindow(); });
            menu.Items.Add(itOpen);

            itBoost = Item("Prepare for Game", delegate
            {
                if (shell == null) return;
                if (GameMode.Active)
                    Job(delegate { GameMode.Leave(shell.Eng, shell.Eng.Echo); }, "Returned to normal mode");
                else
                    Job(delegate { GameMode.Enter(shell.Eng, shell.Eng.Echo); }, "Gaming Mode active");
            });
            menu.Items.Add(itBoost);

            menu.Items.Add(new ToolStripSeparator());
            itBal  = Item("Mode: Balanced",    delegate { if (shell != null) Job(delegate { shell.Eng.RunBalanced(); }, "Balanced mode active"); });
            itGame = Item("Mode: Gaming",      delegate { if (shell != null) Job(delegate { shell.Eng.RunGaming(); }, "Gaming power profile active"); });
            itComp = Item("Mode: Competitive", delegate { if (shell != null) Job(delegate { shell.Eng.RunCompetitive(); }, "Competitive mode active"); });
            itPerf = Item("Mode: Max Speed",   delegate { if (shell != null) Job(delegate { shell.Eng.RunPerf(); }, "Max Speed mode active"); });
            itEco  = Item("Mode: Economy",     delegate { if (shell != null) Job(delegate { shell.Eng.RunEco(); }, "Economy mode active"); });
            itCar  = Item("Mode: In the Car",  delegate { if (shell != null) Job(delegate { shell.Eng.RunCar(); }, "Car adapter mode active"); });
            menu.Items.Add(itBal);
            menu.Items.Add(itGame);
            menu.Items.Add(itComp);
            menu.Items.Add(itPerf);
            menu.Items.Add(itEco);
            menu.Items.Add(itCar);

            menu.Items.Add(new ToolStripSeparator());
            itWidget = Item("Hide Widget", delegate { if (shell != null) shell.ToggleWidget(); });
            menu.Items.Add(itWidget);
            itAiPing = Item("AI ping every hour (Claude, Codex)", delegate
            {
                Cfg.AiPing = !Cfg.AiPing;
                AiPing.Apply();
            });
            menu.Items.Add(itAiPing);

            menu.Items.Add(new ToolStripSeparator());
            itExit = Item("Exit", delegate { if (shell != null) shell.Quit(); });
            menu.Items.Add(itExit);

            menu.Opening += delegate { SyncMenuItems(); };

            ni = new NotifyIcon
            {
                Icon = AppIcon(),
                Visible = true,
                ContextMenuStrip = menu
            };
            ni.DoubleClick += delegate { if (openWindow != null) openWindow(); };
            Tip();
            Cfg.ApplyPromoted();
        }

        public static Icon AppIcon()
        {
            try
            {
                string ico = System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "PC-Tune.ico");
                if (System.IO.File.Exists(ico)) return new Icon(ico);
            }
            catch { }
            try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { return SystemIcons.Application; }
        }

        static void Tip()
        {
            if (ni == null) return;
            try
            {
                string mode = GameMode.Active ? "Gaming Mode Active" : Cfg.ProfileTitle;
                string s = "PC-Tune · " + mode;
                if (s.Length > 63) s = s.Substring(0, 63);
                ni.Text = s;
            }
            catch { }
        }

        static ToolStripMenuItem Item(string text, EventHandler click)
        {
            var i = new ToolStripMenuItem(text);
            i.ForeColor = Ui.Text;
            i.Padding = new Padding(Ui.D(6), Ui.D(3), Ui.D(6), Ui.D(3));
            if (click != null) i.Click += click;
            return i;
        }

        static void SyncMenuItems()
        {
            if (menu == null || menu.IsDisposed) return;

            bool active = GameMode.Active;
            string state = active ? "Gaming Mode Active" : ("Mode: " + Cfg.ProfileTitle);
            itHead.Text = "PC-Tune · " + state;

            itBoost.Text = active ? "Return to Normal" : "Prepare for Game";
            itBoost.ForeColor = active ? Ui.Green : Ui.Accent;

            string prof = string.IsNullOrEmpty(Cfg.Profile) ? "bal" : Cfg.Profile;
            itBal.ForeColor  = prof == "bal"  ? Ui.Accent : Ui.Text;
            itGame.ForeColor = prof == "game" ? Ui.Accent : Ui.Text;
            itComp.ForeColor = prof == "comp" ? Ui.Accent : Ui.Text;
            itPerf.ForeColor = prof == "perf" ? Ui.Accent : Ui.Text;
            itEco.ForeColor  = prof == "eco"  ? Ui.Accent : Ui.Text;
            itCar.ForeColor  = prof == "car"  ? Ui.Accent : Ui.Text;

            bool vis = shell != null && shell.IsWidgetVisible;
            itWidget.Text = vis ? "Hide Widget" : "Show Widget";
            itAiPing.Checked = Cfg.AiPing;
        }

        static void Job(Action work, string done)
        {
            if (shell != null)
            {
                shell.RunAction(work, done);
            }
        }

        public static void Notify(string title, string text)
        {
            if (ni == null) return;
            try
            {
                ni.BalloonTipTitle = title;
                ni.BalloonTipText = text;
                ni.BalloonTipIcon = ToolTipIcon.Info;
                ni.ShowBalloonTip(3000);
            }
            catch { }
        }

        public static void PowerChanged() { Tip(); }

        public static void Kill()
        {
            if (ni != null) { ni.Visible = false; ni.Dispose(); ni = null; }
            if (menu != null) { menu.Dispose(); menu = null; }
        }
    }
}
