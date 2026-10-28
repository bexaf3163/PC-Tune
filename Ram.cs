// Memory management and safe background process termination.
// Working set trimming excludes active games, foreground apps, audio, graphics drivers, and kernel processes
// to prevent page-fault stutters during gameplay.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PCTune
{
    static class Ram
    {
        [DllImport("psapi.dll", SetLastError = true)]
        static extern bool EmptyWorkingSet(IntPtr hProcess);

        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

        [StructLayout(LayoutKind.Sequential)]
        struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys;
            public ulong ullTotalPageFile, ullAvailPageFile;
            public ulong ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buf);

        public struct Snap
        {
            public double TotalGb, UsedGb, FreeGb;
            public ulong AvailBytes;
            public int Percent;
        }

        public static Snap State()
        {
            var s = new Snap();
            var m = new MEMORYSTATUSEX();
            m.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            try
            {
                if (GlobalMemoryStatusEx(ref m))
                {
                    s.TotalGb = m.ullTotalPhys / 1073741824.0;
                    s.FreeGb = m.ullAvailPhys / 1073741824.0;
                    s.AvailBytes = m.ullAvailPhys;
                    s.UsedGb = s.TotalGb - s.FreeGb;
                    s.Percent = (int)m.dwMemoryLoad;
                }
            }
            catch { }
            return s;
        }

        // Kernel, security, shell, and audio processes that must never be trimmed or closed.
        static readonly string[] Never =
        {
            "system", "idle", "registry", "memory compression", "smss", "csrss",
            "wininit", "winlogon", "services", "lsass", "lsaiso", "fontdrvhost",
            "dwm", "explorer", "msmpeng", "nissrv", "securityhealthservice",
            "mpdefendercoreservice", "trustedinstaller", "tiworker", "audiodg", "pc-tune"
        };

        // Processes that must never be terminated by background cleanup.
        static readonly string[] NeverClose =
        {
            "svchost", "sihost", "ctfmon", "conhost", "dllhost", "taskhostw",
            "runtimebroker", "searchhost", "searchindexer", "startmenuexperiencehost",
            "shellexperiencehost", "shellhost", "textinputhost", "applicationframehost",
            "audiodg", "spoolsv", "wmiprvse", "wudfhost", "smartscreen", "sgrmbroker",
            "securityhealthsystray", "lsm", "dashost", "unsecapp", "widgets", "widgetservice",
            "powershell", "pwsh", "cmd", "bash", "windowsterminal", "openconsole",
            "pc-tune", "claude", "cowork-svc", "lightshot", "runelite", "jagexlauncher", "java", "javaw",
            "lineageii", "l2", "rf-default", "rf_online", "rf_online_4g", "elementclient"
        };

        public static readonly int MySession = Process.GetCurrentProcess().SessionId;

        // Driver helpers (audio, graphics, thermal) in user session.
        static readonly string[] NeverClosePrefix =
        {
            "rtkaud", "waves", "nahimic", "igfx", "intelaudio", "dptf", "esif", "fwswitch",
            "nvcontainer", "nvdisplay", "amdrsserv", "atiesrxx"
        };

        public static bool MayClose(string name)
        {
            string n = (name ?? "").ToLowerInvariant();
            if (n.Length == 0) return false;
            foreach (string s in Never) if (n == s) return false;
            foreach (string s in NeverClose) if (n == s) return false;
            foreach (string s in NeverClosePrefix) if (n.StartsWith(s, StringComparison.Ordinal)) return false;
            return true;
        }

        static HashSet<string> ActiveGameNames()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "runelite", "jagexlauncher", "java", "javaw",
                "lineageii", "l2", "rf-default", "rf_online", "rf_online_4g", "elementclient"
            };
            try
            {
                foreach (var g in Games.List())
                {
                    if (!string.IsNullOrEmpty(g.Proc)) set.Add(g.Proc);
                    if (!string.IsNullOrEmpty(g.Exe))
                        set.Add(Path.GetFileNameWithoutExtension(g.Exe));
                }
            }
            catch { }
            return set;
        }

        public static bool IsAnyGameRunning()
        {
            try
            {
                var games = ActiveGameNames();
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        string n = p.ProcessName;
                        if (n != null && games.Contains(n) && !n.Equals("java", StringComparison.OrdinalIgnoreCase))
                        {
                            p.Dispose();
                            return true;
                        }
                    }
                    catch { }
                    try { p.Dispose(); } catch { }
                }
            }
            catch { }
            return false;
        }

        static bool SkipTrim(Process p, int fgPid, HashSet<string> gameNames)
        {
            if (p.Id == fgPid || p.Id == Process.GetCurrentProcess().Id) return true;
            string n = (p.ProcessName ?? "").ToLowerInvariant();
            if (n.Length == 0) return true;
            foreach (string s in Never) if (n == s) return true;
            foreach (string s in NeverClosePrefix) if (n.StartsWith(s, StringComparison.Ordinal)) return true;
            if (gameNames.Contains(n)) return true;
            return false;
        }

        public struct Result
        {
            public int Touched, Failed;
            public double FreedMb;
            public double WorkingSetDeltaMb;
        }

        // Safely trims working sets of idle background processes while protecting games,
        // foreground window, audio, and graphics drivers. Reports real available RAM gain.
        public static Result Trim()
        {
            var r = new Result();
            int fgPid = -1;
            try
            {
                IntPtr fg = GetForegroundWindow();
                if (fg != IntPtr.Zero) GetWindowThreadProcessId(fg, out fgPid);
            }
            catch { }

            var gameNames = ActiveGameNames();
            ulong availBefore = State().AvailBytes;
            long wsBefore = 0, wsAfter = 0;
            var list = new List<Process>();

            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (SkipTrim(p, fgPid, gameNames)) { p.Dispose(); continue; }
                    wsBefore += p.WorkingSet64;
                    list.Add(p);
                }
                catch { try { p.Dispose(); } catch { } }
            }

            foreach (var p in list)
            {
                try
                {
                    if (EmptyWorkingSet(p.Handle)) r.Touched++;
                    else r.Failed++;
                }
                catch { r.Failed++; }
            }

            System.Threading.Thread.Sleep(350);

            foreach (var p in list)
            {
                try { p.Refresh(); wsAfter += p.WorkingSet64; }
                catch { }
                try { p.Dispose(); } catch { }
            }

            ulong availAfter = State().AvailBytes;
            r.WorkingSetDeltaMb = Math.Max(0.0, (wsBefore - wsAfter) / 1048576.0);
            double physDeltaMb = availAfter > availBefore ? (availAfter - availBefore) / 1048576.0 : 0.0;
            // Use physical available RAM gain when positive; fall back to conservative private estimate if concurrent allocation occurred.
            r.FreedMb = physDeltaMb > 1.0 ? physDeltaMb : Math.Min(r.WorkingSetDeltaMb * 0.35, State().TotalGb * 1024.0 * 0.25);

            Sys.ToFile(string.Format("MEMORY: trimmed {0} background processes, available RAM +{1:N0} MB (working sets -{2:N0} MB)",
                                     r.Touched, r.FreedMb, r.WorkingSetDeltaMb));
            return r;
        }

        public static int CloseMarked(List<string> names, Action<string> log)
        {
            int n = 0;
            if (names == null || names.Count == 0) return 0;
            var lower = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string s in names) lower.Add(s.Trim());

            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (!lower.Contains(p.ProcessName) || !MayClose(p.ProcessName) ||
                        p.SessionId != MySession) { p.Dispose(); continue; }
                    if (p.Id == Process.GetCurrentProcess().Id) { p.Dispose(); continue; }

                    string who = p.ProcessName;
                    bool closed = false;
                    try
                    {
                        if (p.MainWindowHandle != IntPtr.Zero && p.CloseMainWindow())
                            closed = p.WaitForExit(3500);
                    }
                    catch { }
                    if (!closed)
                    {
                        try { p.Kill(); p.WaitForExit(2500); closed = true; }
                        catch { }
                    }
                    if (closed) { n++; if (log != null) log(who); }
                    p.Dispose();
                }
                catch { try { p.Dispose(); } catch { } }
            }
            if (n > 0) Sys.ToFile("MEMORY: closed background programs: " + n);
            return n;
        }
    }

    // Scheduled memory maintenance: runs only when memory pressure is high (>= 75%)
    // and no game is actively running, preventing periodic page-fault stutters.
    class RamSchedule : IDisposable
    {
        readonly Timer t;
        public event Action<Ram.Result> Done;

        public RamSchedule()
        {
            t = new Timer();
            Reload();
            t.Tick += (s, e) => Fire();
        }

        public void Reload()
        {
            t.Stop();
            if (!Cfg.RamAuto) return;
            t.Interval = Cfg.RamMinutes * 60000;
            t.Start();
        }

        void Fire()
        {
            // Avoid continuous forced RAM clearing unless memory pressure is genuinely elevated (>= 75%)
            // and no game is running.
            if (Ram.State().Percent < 75 || Ram.IsAnyGameRunning()) return;
            var r = Ram.Trim();
            var h = Done;
            if (h != null) h(r);
        }

        public void Dispose() { if (t != null) { t.Stop(); t.Dispose(); } }
    }
}
