// Games page: per-game client detection, safe config optimization, and session preferences.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace PCTune
{
    sealed class GameRowCard : Panel
    {
        public GameInfo Info;
        readonly Label lTitle, lState, lPlan, lHard;
        readonly Btn bOpt, bRestore, bLocate, bMeasure;
        public Action<GameInfo> OnMeasure;
        readonly Toggle tgHard;
        readonly Action<GameInfo, bool> onOptimize;
        readonly Action<GameInfo> onRestore;
        readonly Action<GameInfo> onLocate;

        public GameRowCard(
            GameInfo info,
            Action<GameInfo, bool> optimize,
            Action<GameInfo> restore,
            Action<GameInfo> locate)
        {
            Info = info;
            onOptimize = optimize;
            onRestore = restore;
            onLocate = locate;

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Ui.Card;

            lTitle = new Label
            {
                Text = info.Title,
                Font = new Font(Ui.Disp, 11f, FontStyle.Bold),
                ForeColor = Ui.Text,
                AutoSize = true,
                BackColor = Color.Transparent
            };
            Controls.Add(lTitle);

            lState = new Label
            {
                Font = new Font(Ui.Tiny, 8.75f),
                ForeColor = Ui.Dim,
                AutoSize = false,
                AutoEllipsis = true,
                BackColor = Color.Transparent
            };
            Controls.Add(lState);

            lPlan = new Label
            {
                Font = new Font(Ui.Tiny, 8.75f),
                ForeColor = Ui.Subtle,
                AutoSize = false,
                AutoEllipsis = true,
                BackColor = Color.Transparent
            };
            Controls.Add(lPlan);

            tgHard = new Toggle();
            tgHard.Init(false);
            Controls.Add(tgHard);

            lHard = new Label
            {
                Text = "Cap visible characters to 50 for mass PvP / sieges",
                Font = new Font(Ui.Tiny, 8.5f),
                ForeColor = Ui.Dim,
                AutoSize = true,
                BackColor = Color.Transparent
            };
            Controls.Add(lHard);

            bOpt = new Btn
            {
                Text2 = "Optimize",
                Tone = Ui.Accent,
                Filled = true,
                Width = Ui.D(104),
                Height = Ui.D(32)
            };
            bOpt.Click += delegate { if (onOptimize != null) onOptimize(Info, tgHard.On); };
            Controls.Add(bOpt);

            bRestore = new Btn
            {
                Text2 = "Restore",
                Width = Ui.D(92),
                Height = Ui.D(32)
            };
            bRestore.Click += delegate { if (onRestore != null) onRestore(Info); };
            Controls.Add(bRestore);

            bLocate = new Btn
            {
                Text2 = "Locate...",
                Subtle = info.Found,
                Width = Ui.D(92),
                Height = Ui.D(32)
            };
            bLocate.Click += delegate { if (onLocate != null) onLocate(Info); };
            Controls.Add(bLocate);

            bMeasure = new Btn
            {
                Text2 = "Measure",
                Subtle = true,
                Width = Ui.D(92),
                Height = Ui.D(32)
            };
            bMeasure.Click += delegate { if (OnMeasure != null) OnMeasure(Info); };
            Controls.Add(bMeasure);

            Bind(info);
        }

        public void Bind(GameInfo g)
        {
            Info = g;
            lTitle.Text = g.Title;

            bool hasBackup = g.Found && Games.HasBackup(g);
            bool running = g.Found && Games.Running(g);

            if (!g.Found)
            {
                lState.Text = "Executable not detected automatically.";
                lPlan.Text = g.Plan;
            }
            else
            {
                lState.Text = Path.GetFileName(g.Exe) + "  ·  " + g.Now;
                lPlan.Text = running
                    ? "Close the game before optimizing so settings are not overwritten on exit."
                    : g.Plan;
            }

            bool showL2Cap = g.Id == "l2" && g.Found;
            tgHard.Visible = showL2Cap;
            lHard.Visible = showL2Cap;

            bOpt.Visible = g.Found;
            bOpt.Enabled = g.Found && !running;
            bOpt.Text2 = g.Done ? "Re-apply" : "Optimize";
            bOpt.Filled = !g.Done;
            bOpt.Tone = g.Done ? Ui.Green : Ui.Accent;

            bRestore.Visible = hasBackup;
            bRestore.Enabled = hasBackup && !running;

            bLocate.Subtle = g.Found;
            bLocate.Text2 = g.Found ? "Change Path" : "Locate .exe";
            bLocate.Width = Ui.D(g.Found ? 104 : 108);
            // Measure runs while RuneLite is open, so it stays enabled when the other buttons lock.
            bMeasure.Visible = g.Id == "osrs" && g.Found && Games.HasHd(g.Config);

            Height = Ui.D(showL2Cap ? 114 : 88);
            LayoutCard();
            Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutCard();
        }

        void LayoutCard()
        {
            int w = ClientSize.Width;
            if (w <= 0) return;

            int pad = Ui.D(18);
            int rightX = w - pad;

            bLocate.Location = new Point(rightX - bLocate.Width, Ui.D(16));
            rightX = bLocate.Left - Ui.D(8);

            if (bMeasure.Visible)
            {
                bMeasure.Location = new Point(rightX - bMeasure.Width, Ui.D(16));
                rightX = bMeasure.Left - Ui.D(8);
            }

            if (bRestore.Visible)
            {
                bRestore.Location = new Point(rightX - bRestore.Width, Ui.D(16));
                rightX = bRestore.Left - Ui.D(8);
            }

            if (bOpt.Visible)
            {
                bOpt.Location = new Point(rightX - bOpt.Width, Ui.D(16));
                rightX = bOpt.Left - Ui.D(12);
            }

            lTitle.Location = new Point(pad, Ui.D(14));
            int textW = Math.Max(Ui.D(160), rightX - pad);
            lState.Bounds = new Rectangle(pad, Ui.D(38), textW, Ui.D(18));
            lPlan.Bounds = new Rectangle(pad, Ui.D(58), w - pad * 2, Ui.D(20));

            if (tgHard.Visible)
            {
                tgHard.Location = new Point(pad, Ui.D(84));
                lHard.Location = new Point(pad + tgHard.Width + Ui.D(10), Ui.D(86));
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Ui.Round(r, Ui.D(8)))
            using (var b = new SolidBrush(Ui.Card))
            using (var p = new Pen(Ui.Line))
            {
                g.FillPath(b, path);
                g.DrawPath(p, path);
            }

            if (Info != null)
            {
                string badge = !Info.Found ? "Not Detected"
                             : Info.Done ? "Optimized"
                             : "Detected";
                Color tone = !Info.Found ? Ui.Subtle
                           : Info.Done ? Ui.Green
                           : Ui.Accent;

                int bx = lTitle.Right + Ui.D(10);
                int by = Ui.D(15);
                using (var f = new Font(Ui.Tiny, 7.75f, FontStyle.Bold))
                {
                    var sz = TextRenderer.MeasureText(g, badge, f, Size.Empty, TextFormatFlags.NoPadding);
                    var br = new Rectangle(bx, by, sz.Width + Ui.D(14), Ui.D(18));
                    using (var path = Ui.Round(br, Ui.D(4)))
                    using (var fill = new SolidBrush(Color.FromArgb(26, tone)))
                    using (var pen = new Pen(Color.FromArgb(95, tone)))
                    {
                        g.FillPath(fill, path);
                        g.DrawPath(pen, path);
                    }
                    TextRenderer.DrawText(g, badge, f, br, tone,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }
            }
        }
    }

    class GamesPage : Page
    {
        readonly Engine eng;
        readonly Action<Action> run;
        readonly Panel cPrefs;
        readonly Toggle tgBrowsers, tgChat;
        readonly Btn bRefresh;
        readonly Label lNote, lStatus;
        readonly List<GameRowCard> cards = new List<GameRowCard>();

        public GamesPage(Engine e, Action<Action> r)
            : base("Games", "Per-game configuration profiles and Gaming Mode session preferences")
        {
            eng = e;
            run = r;

            lNote = new Label
            {
                Text = "Optimizes documented game configuration files (with automatic .pctune.bak backups) and Windows DirectX GPU preferences. Protected game binaries and anti-cheat files are never modified.",
                Font = new Font(Ui.Tiny, 8.75f),
                ForeColor = Ui.Dim,
                AutoSize = false,
                BackColor = Color.Transparent
            };
            Host.Controls.Add(lNote);

            bRefresh = new Btn
            {
                Text2 = "Rescan",
                Subtle = true,
                Width = Ui.D(88),
                Height = Ui.D(30)
            };
            bRefresh.Click += delegate { Refresh2(); };
            Host.Controls.Add(bRefresh);

            lStatus = new Label
            {
                Font = new Font(Ui.Tiny, 8.5f),
                ForeColor = Ui.Green,
                AutoSize = true,
                BackColor = Color.Transparent
            };
            Host.Controls.Add(lStatus);

            cPrefs = Sect("Gaming Mode Session Preferences", 128);
            Host.Controls.Add(cPrefs);

            tgBrowsers = new Toggle();
            tgBrowsers.Init(Cfg.GameCloseBrowsers);
            tgBrowsers.Toggled += delegate { Cfg.GameCloseBrowsers = tgBrowsers.On; };
            Row(cPrefs, 44, "Close web browsers when entering Gaming Mode",
                "Frees memory held by background browser tabs before launching a game.", tgBrowsers);

            tgChat = new Toggle();
            tgChat.Init(Cfg.GameCloseChat);
            tgChat.Toggled += delegate { Cfg.GameCloseChat = tgChat.On; };
            Row(cPrefs, 86, "Close Discord and chat overlays when entering Gaming Mode",
                "Optional for low-memory systems; leave off if you use voice chat during games.", tgChat);
        }

        public override void Refresh2()
        {
            tgBrowsers.Init(Cfg.GameCloseBrowsers);
            tgChat.Init(Cfg.GameCloseChat);

            var list = Games.List();
            while (cards.Count < list.Count)
            {
                var card = new GameRowCard(list[cards.Count], ApplyGame, RestoreGame, PickExe);
                card.OnMeasure = MeasureGame;
                cards.Add(card);
                Host.Controls.Add(card);
            }
            for (int i = 0; i < cards.Count; i++)
            {
                if (i < list.Count)
                {
                    cards[i].Visible = true;
                    cards[i].Bind(list[i]);
                }
                else
                {
                    cards[i].Visible = false;
                }
            }
            Relayout();
        }

        void PickExe(GameInfo g)
        {
            if (g == null) return;
            using (var d = new OpenFileDialog())
            {
                d.Title = "Locate executable for " + g.Title;
                d.Filter = "Game executable (*.exe)|*.exe";
                d.CheckFileExists = true;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                Games.Remember(g.Id, d.FileName);
                Sys.ToFile("GAMES path " + g.Id + " = " + d.FileName);
                lStatus.Text = "Updated path for " + g.Title;
            }
            Refresh2();
        }

        void ApplyGame(GameInfo g, bool hard)
        {
            if (g == null || !g.Found) return;
            bool useHard = hard && g.Id == "l2";
            run(delegate
            {
                bool wasRunning = Games.Running(g);
                Games.Optimize(g, useHard, eng.Echo);
                try
                {
                    BeginInvoke((Action)delegate
                    {
                        if (wasRunning)
                        {
                            lStatus.ForeColor = Ui.Amber;
                            lStatus.Text = g.Title + " is running — close the game first, then click Optimize.";
                        }
                        else
                        {
                            lStatus.ForeColor = Ui.Green;
                            lStatus.Text = "Optimized " + g.Title;
                        }
                        Refresh2();
                    });
                }
                catch { }
            });
        }

        void MeasureGame(GameInfo g)
        {
            run(delegate
            {
                string r = Games.MeasureOsrs(60, eng.Echo);
                try
                {
                    BeginInvoke((Action)delegate
                    {
                        lStatus.ForeColor = Ui.Accent;
                        lStatus.Text = r;
                        Refresh2();
                    });
                }
                catch { }
            });
        }

        void RestoreGame(GameInfo g)
        {
            if (g == null || !g.Found) return;
            run(delegate
            {
                bool wasRunning = Games.Running(g);
                Games.Restore(g, eng.Echo);
                try
                {
                    BeginInvoke((Action)delegate
                    {
                        if (wasRunning)
                        {
                            lStatus.ForeColor = Ui.Amber;
                            lStatus.Text = g.Title + " is running — close the game first, then click Restore.";
                        }
                        else
                        {
                            lStatus.ForeColor = Ui.Green;
                            lStatus.Text = "Restored original settings for " + g.Title;
                        }
                        Refresh2();
                    });
                }
                catch { }
            });
        }

        protected override void Relayout()
        {
            base.Relayout();
            int w = Host.ClientSize.Width - (Host.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0);
            if (w < Ui.D(360)) w = Host.ClientSize.Width;

            bRefresh.Location = new Point(w - bRefresh.Width, Ui.D(0));
            lNote.Bounds = new Rectangle(Ui.D(0), Ui.D(2), Math.Max(Ui.D(200), bRefresh.Left - Ui.D(16)), Ui.D(34));

            int y = Ui.D(42);
            if (lStatus.Text.Length > 0)
            {
                lStatus.Location = new Point(Ui.D(0), y);
                y += Ui.D(24);
            }

            foreach (var c in cards)
            {
                if (!c.Visible) continue;
                c.Bounds = new Rectangle(Ui.D(0), y, w, c.Height);
                y += c.Height + Ui.D(10);
            }

            y += Ui.D(6);
            cPrefs.Bounds = new Rectangle(Ui.D(0), y, w, Ui.D(132));
            PositionRows(cPrefs, w);
        }

        static void PositionRows(Panel card, int w)
        {
            foreach (Control c in card.Controls)
            {
                if (c is Toggle)
                    c.Location = new Point(w - c.Width - Ui.D(20), c.Top);
                else if (c is Label && !c.AutoSize)
                    c.Width = Math.Max(Ui.D(180), w - Ui.D(110));
            }
        }
    }
}
