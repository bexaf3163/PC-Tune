// "Prepare for Game" — intelligent, reversible gaming session preparation with before/after verification.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using Microsoft.Win32;

namespace PCTune
{
    static class GameMode
    {
        const string Key = @"Software\PCTune\GameMode";

        // Non-essential third-party/OEM services that can be temporarily stopped during a gaming session.
        static readonly string[] ServiceMasks =
        {
            "dell", "supportassist", "techhub",
            "steelseries", "wallpaper engine", "razer", "corsair", "icue",
            "logi_", "lamparray", "armoury", "mystic"
        };

        // Non-essential Windows background services safe to pause during gameplay.
        static readonly string[] ServiceNames =
        {
            "WSearch",          // Windows Search indexer
            "MapsBroker",       // Downloaded Maps Manager
            "DoSvc",            // Delivery Optimization
            "PhoneSvc", "WpnService", "RetailDemo"
        };

        // Safe background utilities, OEM telemetry, peripheral configurators, live wallpapers, and torrents.
        static readonly string[] BgUtilityApps =
        {
            // OEM background suites
            "dell.", "delltech", "dellupdate", "supportassist", "alienware", "serviceshell",

            // Peripheral / RGB panels (device profiles remain in hardware onboard memory)
            "steelseries", "razer", "synapse", "rzsdk", "icue", "corsair",
            "logitech", "lghub", "logioptions", "openrgb", "signalrgb",
            "armourycrate", "mysticlight",

            // Live desktop wallpapers
            "wallpaper32", "wallpaper64", "wallpaperengine", "lively", "rainmeter",

            // Torrent clients (consume disk I/O and network bandwidth)
            "qbittorrent", "utorrent", "bittorrent", "transmission", "deluge",

            // Cloud sync & background updaters
            "onedrive", "dropbox", "googledrive", "yandexdisk", "megasync", "icloud", "nextcloud",
            "msedgewebview2", "acrotray", "creativecloud", "ccxprocess",
            "jusched", "crossdeviceresume", "tabtip"
        };

        // Optional: Messengers and media players (controlled by Cfg.GameCloseChat or Cfg.KillList)
        static readonly string[] ChatAndMediaApps =
        {
            "discord", "telegram", "whatsapp", "viber", "skype", "slack", "teams", "zoom",
            "spotify", "itunes", "yandexmusic", "aimp", "foobar"
        };

        // Optional: Web browsers (controlled by Cfg.GameCloseBrowsers or Cfg.KillList)
        static readonly string[] BrowserApps =
        {
            "brave", "chrome", "msedge", "firefox", "opera", "vivaldi", "yandex"
        };

        // Processes that must never be closed by GameMode.
        static readonly string[] Never =
        {
            "pc-tune", "runelite", "jagexlauncher", "lineageii", "l2",
            "rf-default", "rf_online", "rf_online_4g", "elementclient",
            "java", "javaw", "claude", "explorer", "dwm", "csrss", "winlogon",
            "services", "lsass", "smss", "wininit", "fontdrvhost", "sihost",
            "msmpeng", "nissrv", "securityhealthservice", "trustedinstaller",
            "lightshot", "audiodg"
        };

        static List<string> GameProcs()
        {
            var r = new List<string>();
            try
            {
                foreach (var g in Games.List())
                {
                    if (g.Proc.Length > 0) r.Add(g.Proc.ToLowerInvariant());
                    if (g.Exe.Length > 0)
                        r.Add(Path.GetFileNameWithoutExtension(g.Exe).ToLowerInvariant());
                }
            }
            catch { }
            return r;
        }

        static readonly string[] ShellHosts =
        {
            "searchhost", "startmenuexperiencehost", "shellexperiencehost",
            "textinputhost", "explorer", "widgets", "widgetservice"
        };

        static bool ShellOwned(string proc)
        {
            if (!string.Equals(proc, "msedgewebview2", StringComparison.OrdinalIgnoreCase))
                return false;
            try
            {
                var parent = new Dictionary<int, int>();
                using (var s = new System.Management.ManagementObjectSearcher(
                    "SELECT ProcessId, ParentProcessId FROM Win32_Process WHERE Name = 'msedgewebview2.exe'"))
                foreach (System.Management.ManagementObject o in s.Get())
                    parent[Convert.ToInt32(o["ProcessId"])] = Convert.ToInt32(o["ParentProcessId"]);

                if (parent.Count == 0) return false;

                foreach (int pid in parent.Keys)
                {
                    int cur = pid;
                    string owner = null;
                    for (int hop = 0; hop < 8; hop++)
                    {
                        int up;
                        if (!parent.TryGetValue(cur, out up)) break;
                        try { owner = Process.GetProcessById(up).ProcessName.ToLowerInvariant(); }
                        catch { owner = null; break; }
                        if (owner != "msedgewebview2") break;
                        cur = up;
                    }
                    if (owner == null) return false;
                    if (Array.IndexOf(ShellHosts, owner) < 0) return false;
                }
                return true;
            }
            catch { return false; }
        }

        static bool Matches(string proc, bool closeBrowsers, bool closeChat)
        {
            string n = proc.ToLowerInvariant();
            foreach (string m in BgUtilityApps)
                if (n.IndexOf(m, StringComparison.Ordinal) >= 0) return true;
            if (closeBrowsers)
                foreach (string m in BrowserApps)
                    if (n.IndexOf(m, StringComparison.Ordinal) >= 0) return true;
            if (closeChat)
                foreach (string m in ChatAndMediaApps)
                    if (n.IndexOf(m, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        static int SampleCpuPercent()
        {
            long i1, k1, u1, i2, k2, u2;
            if (!Sys.GetSystemTimes(out i1, out k1, out u1)) return 0;
            System.Threading.Thread.Sleep(250);
            if (!Sys.GetSystemTimes(out i2, out k2, out u2)) return 0;
            long di = i2 - i1, tot = (k2 - k1) + (u2 - u1);
            if (tot <= 0) return 0;
            int pct = (int)Math.Round(100.0 * (tot - di) / tot);
            return pct < 0 ? 0 : pct > 100 ? 100 : pct;
        }

        static int SafeProcCount()
        {
            try
            {
                var ps = Process.GetProcesses();
                int n = ps.Length;
                foreach (var p in ps) { try { p.Dispose(); } catch { } }
                return n;
            }
            catch { return 0; }
        }

        // ---------------------------------------------------------- enter game mode
        public static void Enter(Engine eng, Action<string> log)
        {
            log("== Preparing system for gaming");

            // Snapshot "Before" metrics for comparison
            int cpuBefore = SampleCpuPercent();
            var ramBefore = Ram.State();
            int procsBefore = SafeProcCount();
            string prevProfile = Cfg.Profile;

            SavePrevProfile(prevProfile);
            SetActiveFlag(true);

            // 1. Power & Execution State
            if (prevProfile == "car")
            {
                log("  [ ] Profile \"In the Car\" preserved (20-30 W adapter protection)");
            }
            else if (prevProfile == "comp")
            {
                eng.RunComp();
            }
            else
            {
                eng.RunGaming();
            }

            // Guarantee AC display & sleep timeouts are Never (0) while Game Mode is active
            Sys.Ac(Sys.SUB_VIDEO, Sys.VIDEOIDLE, 0);
            Sys.Ac(Sys.SUB_SLEEP, Sys.STANDBYIDLE, 0);
            Sys.Ac(Sys.SUB_SLEEP, Sys.HIBERNATEIDLE, 0);
            Sys.Pow("/setactive SCHEME_CURRENT");

            // Prevent Windows display dimming or idle sleep at thread/session level while Game Mode is active
            ReassertRuntimeLocks();
            log("  [+] High-resolution kernel timer locked (0.5 ms scheduler quantum)");
            Games.BoostRunningGames(log);

            int acVideo = Sys.PowIdx(Sys.SUB_VIDEO, Sys.VIDEOIDLE, true);
            int acSleep = Sys.PowIdx(Sys.SUB_SLEEP, Sys.STANDBYIDLE, true);
            int acHib   = Sys.PowIdx(Sys.SUB_SLEEP, Sys.HIBERNATEIDLE, true);
            if (acVideo == 0 && acSleep == 0 && (acHib <= 0))
                log("  [+] Verified AC timeouts: Display = Never, Sleep = Never, Hibernate = Never");
            else
                log(string.Format("  [!] AC timeout check: Display={0}s, Sleep={1}s, Hibernate={2}s", acVideo, acSleep, acHib));

            // 2. Services: stop non-essential background services (startup type is untouched)
            int svcOk = 0;
            var stopped = new List<string>();
            foreach (var s in Svc.List())
            {
                if (s.Protected) continue;
                if (!string.Equals(s.State, "Running", StringComparison.OrdinalIgnoreCase)) continue;
                if (!Wanted(s.Name, s.Display)) continue;
                if (StopSvc(s.Name, log)) { svcOk++; stopped.Add(s.Name); }
            }
            if (stopped.Count > 0) SaveStopped(stopped);
            log(svcOk > 0 ? "  [+] Non-essential services paused: " + svcOk
                          : "  [ ] Non-essential services already stopped");

            // 3. Background processes
            var skip = new HashSet<string>(Never, StringComparer.OrdinalIgnoreCase);
            foreach (string gp in GameProcs()) skip.Add(gp);

            bool closeBrowsers = Cfg.GameCloseBrowsers;
            bool closeChat = Cfg.GameCloseChat;

            var live = BackgroundApps(skip, closeBrowsers, closeChat);

            foreach (string extra in Cfg.KillList)
            {
                if (skip.Contains(extra)) continue;
                if (!live.Contains(extra)) live.Add(extra);
            }

            if (live.Count > 0)
            {
                live.Sort();
                log("  [ ] Closing background apps: " + string.Join(", ", live.ToArray()));
                Ram.CloseMarked(live, null);

                // Second pass for companion processes that respawn briefly on exit
                System.Threading.Thread.Sleep(1800);
                var again = new List<string>();
                foreach (string n in live)
                {
                    if (ShellOwned(n)) continue;
                    try
                    {
                        var ps = Process.GetProcessesByName(n);
                        if (ps.Length > 0) again.Add(n);
                        foreach (var p in ps) { try { p.Dispose(); } catch { } }
                    }
                    catch { }
                }
                if (again.Count > 0)
                    Ram.CloseMarked(again, null);
                log("  [+] Closed background applications: " + live.Count);
            }
            else
            {
                log("  [ ] No background bloat running");
            }

            // 4. Trim idle background working sets (games, foreground apps, and audio/GPU drivers are excluded)
            var trimRes = Ram.Trim();
            var ramAfter = Ram.State();
            int procsAfter = SafeProcCount();
            int cpuAfter = SampleCpuPercent();

            // 5. Before / After summary
            log("== Before / After Summary");
            log(string.Format("  [+] Available RAM : {0:N2} GB -> {1:N2} GB (used {2}% -> {3}%, +{4:N0} MB available)",
                ramBefore.FreeGb, ramAfter.FreeGb, ramBefore.Percent, ramAfter.Percent,
                Math.Max(0.0, (ramAfter.FreeGb - ramBefore.FreeGb) * 1024.0 > 1.0 ? (ramAfter.FreeGb - ramBefore.FreeGb) * 1024.0 : trimRes.FreedMb)));
            log(string.Format("  [+] Processes     : {0} -> {1} ({2} paused services)",
                procsBefore, procsAfter, svcOk));
            log(string.Format("  [+] CPU Idle Load : {0}% -> {1}%", cpuBefore, cpuAfter));

            if (Power.OnBattery())
                log(Cfg.Profile == "car"
                    ? "  [!] Running on battery power - weak adapter disconnected or overloaded"
                    : "  [!] Running on battery power - connect AC power for full CPU/GPU clocks");
            if (ramAfter.Percent > 75 && HwInfo.IsIntegratedOnly)
                log("  [!] RAM usage is above 75%: integrated graphics shares system RAM for VRAM");

            log("== Ready for gaming");
            StartUpkeep();
        }

        static List<string> BackgroundApps(HashSet<string> skip, bool closeBrowsers, bool closeChat)
        {
            var live = new List<string>();
            try
            {
                foreach (var pr in Process.GetProcesses())
                {
                    string n;
                    try { n = pr.ProcessName; }
                    catch { try { pr.Dispose(); } catch { } continue; }
                    try { pr.Dispose(); } catch { }
                    if (string.IsNullOrEmpty(n)) continue;
                    if (skip.Contains(n)) continue;
                    if (!Matches(n, closeBrowsers, closeChat)) continue;
                    if (ShellOwned(n)) continue;
                    if (!live.Contains(n)) live.Add(n);
                }
            }
            catch { }
            return live;
        }

        // While Game Mode is on, a dedicated thread repeats the upkeep once a minute. It also holds the
        // display/sleep lock, which SetThreadExecutionState ties to the calling thread and so was lost when
        // the short-lived job thread that ran Enter() ended.
        static System.Threading.Thread upkeep;
        static DateTime lastTrim;
        static readonly System.Threading.AutoResetEvent upkeepWake = new System.Threading.AutoResetEvent(false);

        public static void StartUpkeep()
        {
            lock (upkeepWake)
            {
                if (upkeep != null && upkeep.IsAlive) return;
                upkeepWake.Reset();   // a Leave() with no thread running must not end the next session at once
                upkeep = new System.Threading.Thread(delegate ()
                {
                    ReassertRuntimeLocks();
                    while (!upkeepWake.WaitOne(60000) && Active)
                        try { Upkeep(); } catch { }
                    Sys.SetThreadExecutionState(Sys.ES_CONTINUOUS);
                }) { IsBackground = true };
                upkeep.Start();
            }
        }

        // Browsers and chat apps are closed only on entry, so apps the player opens during the session stay.
        static void Upkeep()
        {
            ReassertRuntimeLocks();
            Games.BoostRunningGames(null);

            // Never raise a UAC prompt in the middle of a game: without elevation, services wait for the next entry.
            var stopped = new List<string>();
            if (Sys.ElevatedReady) foreach (var s in Svc.List())
            {
                if (s.Protected || !string.Equals(s.State, "Running", StringComparison.OrdinalIgnoreCase)) continue;
                if (Wanted(s.Name, s.Display) && StopSvc(s.Name, Sys.ToFile)) stopped.Add(s.Name);
            }
            if (stopped.Count > 0) SaveStopped(stopped);

            var skip = new HashSet<string>(Never, StringComparer.OrdinalIgnoreCase);
            foreach (string gp in GameProcs()) skip.Add(gp);
            var live = BackgroundApps(skip, false, false);
            if (live.Count > 0) Ram.CloseMarked(live, null);

            // Trimming makes background processes fault their pages back in, so it runs only under real
            // pressure and at most every 10 minutes; trimming every minute at 75% would itself cause hitches.
            if (Ram.State().Percent >= 85 && DateTime.UtcNow - lastTrim > TimeSpan.FromMinutes(10))
            {
                lastTrim = DateTime.UtcNow;
                var r = Ram.Trim();
                Sys.ToFile(string.Format("GAME: upkeep trimmed memory, +{0:N0} MB available", r.FreedMb));
            }
        }

        public static void ReassertRuntimeLocks()
        {
            Sys.SetThreadExecutionState(Sys.ES_CONTINUOUS | Sys.ES_SYSTEM_REQUIRED | Sys.ES_DISPLAY_REQUIRED);
            Sys.SetHighResTimer(true);
        }

        static bool Wanted(string name, string display)
        {
            string n = (name + " " + display).ToLowerInvariant();
            foreach (string m in ServiceMasks)
                if (n.IndexOf(m, StringComparison.Ordinal) >= 0) return true;
            foreach (string s in ServiceNames)
                if (string.Equals(name, s, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static bool StopSvc(string name, Action<string> log)
        {
            try
            {
                using (var sc = new ServiceController(name))
                {
                    if (!sc.CanStop) return false;
                }
                if (Sys.StopService(name, 8))
                {
                    Sys.ToFile("GAME: service stopped " + name);
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                log("  [!] " + name + ": " + ex.Message);
                return false;
            }
        }

        static void SetActiveFlag(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(Key))
                {
                    if (k == null) return;
                    if (on) k.SetValue("Active", 1, RegistryValueKind.DWord);
                    else k.DeleteValue("Active", false);
                }
            }
            catch { }
        }

        static void SavePrevProfile(string prof)
        {
            try
            {
                string save = (prof == "eco" || prof == "car" || prof == "perf" || prof == "bal") ? prof : "bal";
                using (var k = Registry.CurrentUser.CreateSubKey(Key))
                    if (k != null && k.GetValue("PrevProfile") == null)
                        k.SetValue("PrevProfile", save, RegistryValueKind.String);
            }
            catch { }
        }

        static void SaveStopped(List<string> names)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(Key))
                {
                    if (k == null) return;
                    var all = new List<string>();
                    var old = k.GetValue("Stopped") as string[];
                    if (old != null) all.AddRange(old);
                    foreach (string n in names)
                    {
                        bool have = false;
                        foreach (string o in all)
                            if (string.Equals(o, n, StringComparison.OrdinalIgnoreCase)) { have = true; break; }
                        if (!have) all.Add(n);
                    }
                    k.SetValue("Stopped", all.ToArray(), RegistryValueKind.MultiString);
                }
            }
            catch { }
        }

        public static bool Active
        {
            get
            {
                if (Pending() > 0) return true;
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(Key))
                    {
                        if (k == null) return false;
                        object act = k.GetValue("Active");
                        if (act != null && Convert.ToInt32(act) != 0) return true;
                        return k.GetValue("PrevProfile") != null;
                    }
                }
                catch { return false; }
            }
        }

        public static int Pending()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(Key))
                {
                    if (k == null) return 0;
                    var v = k.GetValue("Stopped") as string[];
                    return v == null ? 0 : v.Length;
                }
            }
            catch { return 0; }
        }

        public static void ReleaseRuntimeAndServices(Action<string> log)
        {
            Sys.SetThreadExecutionState(Sys.ES_CONTINUOUS);
            Sys.SetHighResTimer(false);
            upkeepWake.Set();

            string[] names = null;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(Key))
                {
                    if (k != null)
                        names = k.GetValue("Stopped") as string[];
                }
            }
            catch { }

            if (names != null && names.Length > 0)
            {
                int ok = 0;
                foreach (string n in names)
                {
                    if (Sys.StartService(n, 8))
                    {
                        ok++;
                        Sys.ToFile("GAME: service restored " + n);
                    }
                    else if (log != null)
                    {
                        log("  [!] Could not restart service: " + n);
                    }
                }
                if (log != null)
                    log("  [+] Services restarted: " + ok + " of " + names.Length);
            }
            else if (log != null)
            {
                log("  [ ] No paused services to restore");
            }

            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(Key, true))
                {
                    if (k != null)
                    {
                        k.DeleteValue("Active", false);
                        k.DeleteValue("Stopped", false);
                        k.DeleteValue("PrevProfile", false);
                    }
                }
            }
            catch { }
        }

        public static void Leave(Action<string> log) { Leave(null, log); }

        public static void Leave(Engine eng, Action<string> log)
        {
            if (log != null) log("== Restoring system after gaming");

            string prevProf = null;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(Key))
                {
                    if (k != null)
                        prevProf = k.GetValue("PrevProfile") as string;
                }
            }
            catch { }

            ReleaseRuntimeAndServices(log);

            if (eng != null)
            {
                if (prevProf == "eco") eng.RunEco();
                else if (prevProf == "car") eng.RunCar();
                else if (prevProf == "perf") eng.RunPerf();
                else eng.RunBalanced();
            }
        }
    }
}
