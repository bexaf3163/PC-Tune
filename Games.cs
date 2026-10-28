// Game client discovery and safe per-game configuration tuning.
//
// Design principles:
//   - Never inject DLLs, d3d9/DXVK wrappers, or IFEO hooks into protected game
//     clients (Frost, HackShield, Fireguard). Any invasive hook risks an account ban.
//   - Modify only documented game configuration files, Windows fullscreen
//     optimization layers, and DirectX per-application GPU preferences.
//   - Back up every modified file (*.pctune.bak or per-key backup for RuneLite)
//     so all changes can be cleanly restored.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace PCTune
{
    class GameInfo
    {
        public string Id = "";
        public string Title = "";
        public string Exe = "";
        public string[] ExtraExes = new string[0];
        public string Config = "";
        public string Proc = "";
        public string[] ExtraProcs = new string[0];
        public bool Found;
        public string Now = "";
        public string Plan = "";
        public bool Done;
    }

    static class Games
    {
        const string Key = @"Software\PCTune\Games";
        const string Layers = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
        const string GpuPrefs = @"Software\Microsoft\DirectX\UserGpuPreferences";

        static string Local { get { return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); } }
        static string Home { get { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); } }

        const string OsrsDefaultFps = "60";

        // ---------------------------------------------------------------- 117 HD quality rungs
        // Windowed Full HD is the play size; rungs are tuned for that canvas.
        const int OsrsWinW = 1920, OsrsWinH = 1080;

        // Best first. Each step down drops the worst cost/benefit options first (POM, shadow
        // transparency, DETAILED shadows, 4096 shadow maps, far draw distance, MSAA level).
        // AF 16x, textures, normal maps, ground blending, lighting and water stay on every rung.
        static readonly string[] RungNames = { "Ultra", "Very High", "High", "Balanced", "Efficient", "Light" };
        static readonly string[] RungKeys = {
            "hd.antiAliasingMode", "hd.shadowMode", "hd.shadowResolution", "hd.shadowDistance", "hd.enableShadowTransparency",
            "hd.drawDistance", "hd.detailDistance", "hd.dynamicLights", "hd.npcLights", "hd.projectileLights",
            "hd.parallaxOcclusionMappingToggle", "hd.characterDisplacement", "hd.windDisplacement" };
        static readonly string[][] Rungs = {
            new[] { "MSAA_8",   "DETAILED", "RES_4096", "DISTANCE_60", "true",  "90", "70", "MANY", "true",  "true",  "true",  "true",  "true" },
            new[] { "MSAA_4",   "DETAILED", "RES_4096", "DISTANCE_50", "true",  "80", "60", "MANY", "true",  "true",  "false", "true",  "true" },
            new[] { "MSAA_4",   "FAST",     "RES_2048", "DISTANCE_50", "false", "70", "60", "SOME", "true",  "true",  "false", "true",  "true" },
            new[] { "MSAA_2",   "FAST",     "RES_2048", "DISTANCE_40", "false", "60", "50", "SOME", "false", "true",  "false", "false", "true" },
            new[] { "DISABLED", "FAST",     "RES_2048", "DISTANCE_30", "false", "50", "50", "SOME", "false", "true",  "false", "false", "true" },
            new[] { "DISABLED", "FAST",     "RES_1024", "DISTANCE_30", "false", "40", "40", "FEW",  "false", "false", "false", "false", "false" },
        };
        static readonly string[][] HdFixed = {
            new[] { "hd.anisotropicFilteringLevel", "16" }, new[] { "hd.textureResolution", "RES_256" },
            new[] { "hd.groundTextures", "true" }, new[] { "hd.objectTextures", "true" },
            new[] { "hd.groundBlending", "true" }, new[] { "hd.normalMapping", "true" },
            // tiledLighting is left to 117 HD: it became a DEFAULT/ON/OFF enum, and environmentalLighting was removed.
            new[] { "hd.underwaterCaustics", "true" }, new[] { "hd.sceneResolutionScale", "100" },
            new[] { "hd.expandShadowDraw", "false" },
            // One frame limiter: the 117 HD cap. Vsync off so it cannot fight the cap on a 100+ Hz panel.
            new[] { "hd.unlockFps", "true" }, new[] { "hd.vsyncMode", "OFF" },
        };

        static List<string[]> HdPlan(int rung, string fps)
        {
            var r = new List<string[]>();
            for (int i = 0; i < RungKeys.Length; i++) r.Add(new[] { RungKeys[i], Rungs[rung][i] });
            r.AddRange(HdFixed);
            r.Add(new[] { "hd.fpsTarget", fps });
            return r;
        }

        static string RungSummary(int rung)
        {
            string[] v = Rungs[rung];
            return "AA " + Human(v[0]) + ", " + Human(v[1]) + " " + v[2].Replace("RES_", "") + " shadows @" + v[3].Replace("DISTANCE_", "") +
                   ", draw " + v[5] + ", " + v[7].ToLowerInvariant() + " lights, AF 16x";
        }

        static bool HdMatches(string c, int rung, string fps, bool car)
        {
            if (!HasHd(c) || RlSchema.For("hd") == null) return false;
            foreach (var kv in HdPlan(rung, fps))
            {
                if ((car && CarValue(kv[0]) != null) || RlSchema.Check(kv[0], kv[1]) != null) continue;
                if (!string.Equals(Prop(c, kv[0]), kv[1], StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }

        static double DedicatedVramGb()
        {
            double best = 0;
            try
            {
                using (var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                    foreach (string n in cls.GetSubKeyNames())
                        try
                        {
                            using (var k = cls.OpenSubKey(n))
                            {
                                object v = k == null ? null : k.GetValue("HardwareInformation.qwMemorySize");
                                if (v is long) best = Math.Max(best, (long)v / 1073741824.0);
                            }
                        }
                        catch { }
            }
            catch { }
            return best;
        }

        // Starting rung from the GPU class at the Full HD window size.
        static int BaseRung()
        {
            HwInfo.Ensure();
            string n = HwInfo.GpuName.ToLowerInvariant();
            double vram = DedicatedVramGb();
            int r;
            if (HwInfo.HasDiscreteGpu) r = vram >= 10 ? 0 : vram >= 6 ? 1 : vram >= 4 ? 2 : 3;
            else if (n.Contains("680m") || n.Contains("780m") || n.Contains("880m") || n.Contains("890m") || n.Contains("arc")) r = 3;
            else if (n.Contains("iris") || n.Contains("radeon")) r = 4;
            else r = 5;
            double mp = Math.Min(OsrsWinW, HwInfo.MonitorWidth) * (double)Math.Min(OsrsWinH, HwInfo.MonitorHeight) / (1920.0 * 1080);
            if (mp < 0.7) r--;
            return Math.Max(0, Math.Min(Rungs.Length - 1, r));
        }

        // Base rung, moved one step at a time by Measure results on this hardware.
        static int OsrsRung()
        {
            int b = BaseRung(), r;
            if (Saved("osrsBase") != b.ToString()) return b;
            return int.TryParse(Saved("osrsRung"), out r) && r >= 0 && r < Rungs.Length ? r : b;
        }

        // Consumes the last measurement: at most one step per test, and never back up to a rung
        // that already failed, so tuning converges instead of oscillating.
        static int TuneRung(Action<string> log)
        {
            int b = BaseRung(), rung = OsrsRung(), mr, gpu, core, fail;
            string[] m = Saved("osrsMeasure").Split(';');
            if (m.Length < 3 || !int.TryParse(m[0], out mr) || mr != rung || !int.TryParse(m[1], out gpu) || !int.TryParse(m[2], out core))
                return rung;
            if (Saved("osrsBase") != b.ToString() || !int.TryParse(Saved("osrsFail"), out fail)) fail = -1;
            int next = rung;
            string verdict;
            if (gpu >= 90) { fail = Math.Max(fail, rung); next++; verdict = "GPU-bound at the cap"; }
            else if (core >= 90 && gpu < 75) { fail = Math.Max(fail, rung); next++; verdict = "CPU-bound (one core saturated)"; }
            else if (gpu <= 65 && rung - 1 > fail) { next--; verdict = "GPU headroom"; }
            else verdict = "holds the cap with headroom";
            next = Math.Max(0, Math.Min(Rungs.Length - 1, next));
            verdict += " (GPU p95 " + gpu + "%, busiest core p95 " + core + "%)" + (next != rung ? ", " + RungNames[rung] + " -> " + RungNames[next] : "");
            Remember("osrsMeasure", "");
            Remember("osrsBase", b.ToString());
            Remember("osrsRung", next.ToString());
            Remember("osrsFail", fail.ToString());
            Remember("osrsVerdict", verdict);
            log("  [+] Last measurement: " + verdict);
            return next;
        }

        // The profile is tuned for this canvas, so size the client window to match (never beyond the monitor).
        static void FitWindow(string c, Action<string> log)
        {
            string[] p = Prop(c, "runelite.clientBounds").Replace("\\:", ":").Split(':');
            int x, y, w, h;
            if (p.Length < 4 || p.Length > 5 || !int.TryParse(p[0], out x) || !int.TryParse(p[1], out y) || !int.TryParse(p[2], out w) || !int.TryParse(p[3], out h)) return;
            // A trailing ":g" means RuneLite stores the game canvas size, so the frame adds title bar and sidebar.
            string tail = p.Length == 5 ? "\\:" + p[4] : "";
            HwInfo.Ensure();
            int mw = HwInfo.MonitorWidth - (tail.Length > 0 ? 260 : 0), mh = HwInfo.MonitorHeight - (tail.Length > 0 ? 100 : 48);
            int W = Math.Min(OsrsWinW, mw), H = Math.Min(OsrsWinH, mh);
            if (w == W && h == H) return;
            if (x >= 0 && x + W > mw) x = mw - W;
            if (y >= 0 && y + H > mh) y = Math.Max(0, mh - H);
            SetKey(c, "runelite.clientBounds", x + "\\:" + y + "\\:" + W + "\\:" + H + tail, log);
        }

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr h, out int pid);

        static int P95(List<float> v)
        {
            if (v.Count == 0) return 0;
            v.Sort();
            return (int)Math.Round(v[Math.Min(v.Count - 1, (int)(v.Count * 0.95))]);
        }

        // RuneLite.exe, or javaw.exe from RuneLite's bundled JRE when another launcher (OSRS Path) starts the client.
        static HashSet<int> OsrsPids()
        {
            var r = new HashSet<int>();
            foreach (var p in Process.GetProcessesByName("RuneLite")) r.Add(p.Id);
            foreach (var p in Process.GetProcessesByName("javaw"))
                try { if (p.MainModule.FileName.IndexOf(@"\RuneLite\", StringComparison.OrdinalIgnoreCase) >= 0) r.Add(p.Id); }
                catch { }
            return r;
        }

        // Samples RuneLite while the user plays: GPU 3D load, busiest core, CPU clock and RAM.
        // With the 60 FPS cap active, GPU busy time shows how much of the 16.7 ms budget a frame uses.
        public static string MeasureOsrs(int seconds, Action<string> log)
        {
            var pids = OsrsPids();
            if (pids.Count == 0) { log("  [!] RuneLite is not running - start it, go to a busy area, then press Measure"); return "RuneLite is not running"; }
            int rung = OsrsRung();
            log("== Measuring RuneLite for " + seconds + " s at " + RungNames[rung] + " - keep the game focused and play normally (Grand Exchange or a city is best)");
            var eng = new Dictionary<string, PerformanceCounter>();
            var cores = new PerformanceCounter[Environment.ProcessorCount];
            PerformanceCounter clock = null;
            var gpu = new List<float>(); var core = new List<float>(); var clk = new List<float>();
            long ram = 0;
            try
            {
                for (int i = 0; i < cores.Length; i++) { cores[i] = new PerformanceCounter("Processor", "% Processor Time", i.ToString()); cores[i].NextValue(); }
                try { clock = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total"); clock.NextValue(); } catch { }
                for (int t = 0; t < seconds; t++)
                {
                    if (t % 5 == 0)
                        foreach (string inst in new PerformanceCounterCategory("GPU Engine").GetInstanceNames())
                        {
                            if (eng.ContainsKey(inst) || inst.IndexOf("engtype_3D", StringComparison.Ordinal) < 0) continue;
                            int a = inst.IndexOf("pid_") + 4, z = inst.IndexOf('_', a), pid;
                            if (a < 4 || z < 0 || !int.TryParse(inst.Substring(a, z - a), out pid) || !pids.Contains(pid)) continue;
                            var pc = new PerformanceCounter("GPU Engine", "Utilization Percentage", inst);
                            pc.NextValue();
                            eng[inst] = pc;
                        }
                    Thread.Sleep(1000);
                    float g = 0, cmax = 0;
                    foreach (var pc in eng.Values) try { g += pc.NextValue(); } catch { }
                    foreach (var pc in cores) cmax = Math.Max(cmax, pc.NextValue());
                    float ck = clock != null ? clock.NextValue() : 0;
                    int fg;
                    GetWindowThreadProcessId(GetForegroundWindow(), out fg);
                    if (!pids.Contains(fg)) continue;   // unfocused frames run at the 15 FPS background cap
                    gpu.Add(Math.Min(100, g)); core.Add(cmax); if (ck > 0) clk.Add(ck);
                    foreach (int id in pids) try { ram = Math.Max(ram, Process.GetProcessById(id).WorkingSet64); } catch { }
                }
            }
            catch (Exception ex) { log("  [x] Measure: " + ex.Message); }
            finally
            {
                foreach (var pc in eng.Values) pc.Dispose();
                foreach (var pc in cores) if (pc != null) pc.Dispose();
                if (clock != null) clock.Dispose();
            }
            if (gpu.Count < seconds / 2)
            {
                log("  [!] RuneLite was focused for only " + gpu.Count + " s - keep the game window in front and measure again");
                return "Measurement incomplete - keep RuneLite focused";
            }
            double gAvg = 0; foreach (float v in gpu) gAvg += v; gAvg /= gpu.Count;
            double cAvg = 0; foreach (float v in clk) cAvg += v; if (clk.Count > 0) cAvg /= clk.Count;
            int g95 = P95(gpu), c95 = P95(core);
            string bottleneck = g95 >= 90 ? "GPU-bound: the iGPU/GPU needs nearly the whole 16.7 ms frame budget"
                              : c95 >= 90 && g95 < 75 ? "CPU-bound: one core is saturated"
                              : g95 <= 65 ? "headroom: a higher-quality rung should still hold 60 FPS"
                              : "balanced: holds the cap with headroom";
            log("  [+] GPU 3D load avg " + (int)gAvg + "%, p95 " + g95 + "%  ·  busiest core p95 " + c95 + "%" +
                (cAvg > 0 ? "  ·  CPU clock " + (int)cAvg + "% of base" : "") + "  ·  RuneLite RAM " + (ram >> 20) + " MB");
            log("  [+] " + bottleneck);
            log("  [ ] Close RuneLite and press Optimize to apply the result (RuneLite rewrites its profile on exit)");
            Remember("osrsMeasure", rung + ";" + g95 + ";" + c95);
            return "Measured: GPU p95 " + g95 + "%, core p95 " + c95 + "% - close RuneLite, then Optimize";
        }

        public static string OsrsTargetFpsString
        {
            get { return Cfg.Profile == "car" ? CarFps : OsrsDefaultFps; }
        }

        // ---------------------------------------------------------- discovery
        public static List<GameInfo> List()
        {
            var r = new List<GameInfo>();
            r.Add(L2());
            r.Add(Rf());
            r.Add(Pw());
            r.Add(Osrs());
            r.Add(Custom());
            return r;
        }

        static string Saved(string id)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(Key))
                {
                    if (k == null) return "";
                    object v = k.GetValue(id);
                    return v == null ? "" : v.ToString();
                }
            }
            catch { return ""; }
        }

        public static void Remember(string id, string exe)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(Key))
                    if (k != null) k.SetValue(id, exe, RegistryValueKind.String);
            }
            catch { }
        }

        static string Pick(string id, string[] guesses)
        {
            string s = Saved(id);
            if (s.Length > 0 && File.Exists(s)) return s;
            foreach (string g in guesses)
                if (File.Exists(g)) return g;
            return "";
        }

        static GameInfo L2()
        {
            var g = new GameInfo { Id = "l2", Title = "Lineage II Essence", Proc = "LineageII" };
            g.Exe = Pick(g.Id, new[]
            {
                @"C:\Games\LineageII Essence\system\Frost\LineageII.exe",
                @"C:\Games\LineageII Essence EU\system\Frost\LineageII.exe"
            });
            if (g.Exe.Length == 0) return g;
            g.Found = true;

            string sys = Path.GetDirectoryName(Path.GetDirectoryName(g.Exe));
            string altExe = Path.Combine(sys, "L2.exe");
            if (File.Exists(altExe)) g.ExtraExes = new[] { altExe };
            g.ExtraProcs = new[] { "L2" };

            g.Config = Path.Combine(sys, "Option.ini");
            if (!File.Exists(g.Config))
            {
                g.Done = HasLayer(g.Exe) && HasGpuPref(g.Exe);
                g.Now = g.Done
                    ? "Windows gaming flags applied (Option.ini will be tuned after first game launch)"
                    : "Option.ini not created yet - Windows executable flags can still be applied";
                g.Plan = g.Done
                    ? "fullscreen optimizations off, DXGI flip-model & high-perf GPU set"
                    : "set DXGI flip-model, high-perf GPU & IFEO priority on LineageII.exe";
                return g;
            }

            string w = Get(g.Config, "GamePlayViewportX"), h = Get(g.Config, "GamePlayViewportY");
            g.Now = w + "x" + h + ", textures " + Get(g.Config, "TextureDetail") +
                    ", effects " + Get(g.Config, "EffectDetail") +
                    ", characters " + Get(g.Config, "RENDERCHARACTERCOUNT");
            g.Done = Get(g.Config, "YebisGlow") == "0" &&
                     Eq(Get(g.Config, "YebisDOF"), "False") &&
                     Eq(Get(g.Config, "YebisAntiAliasing"), "False") &&
                     Eq(Get(g.Config, "PawnShadow"), "False") &&
                     Get(g.Config, "TextureDetail") == "0" &&
                     HasLayer(g.Exe) && HasGpuPref(g.Exe);
            g.Plan = g.Done
                ? "optimized (Yebis/shadows off, FSO off, DXGI flip-model & high-perf GPU set)"
                : "turn off Yebis post-processing, shadows & detail; set DXGI flip-model & disable FSO";
            return g;
        }

        static GameInfo Rf()
        {
            var g = new GameInfo
            {
                Id = "rf",
                Title = "RF Online",
                Proc = "rf-default",
                ExtraProcs = new[] { "RF_Online", "RF_Online_4g" }
            };
            g.Exe = Pick(g.Id, new[] { @"C:\Games\RF-Default\rf-default.exe" });
            if (g.Exe.Length == 0) return g;
            g.Found = true;

            string dir = Path.GetDirectoryName(g.Exe);
            var extras = new List<string>();
            foreach (string bin in new[] { "RF_Online.bin", "RF_Online_4g.bin" })
            {
                string p = Path.Combine(dir, bin);
                if (File.Exists(p)) extras.Add(p);
            }
            g.ExtraExes = extras.ToArray();

            g.Config = Path.Combine(dir, "R3Engine.ini");
            if (!File.Exists(g.Config))
            {
                g.Done = HasLayer(g.Exe) && HasGpuPref(g.Exe);
                g.Now = g.Done
                    ? "Windows gaming flags applied (R3Engine.ini will be tuned after first game launch)"
                    : "R3Engine.ini not created yet - Windows executable flags can still be applied";
                g.Plan = g.Done
                    ? "fullscreen optimizations off, DXGI flip-model & high-perf GPU set"
                    : "set DXGI flip-model, high-perf GPU & IFEO priority on RF Online binaries";
                return g;
            }

            g.Now = Get(g.Config, "ScreenXSize") + "x" + Get(g.Config, "ScreenYSize") +
                    ", light " + Get(g.Config, "DynamicLight") +
                    ", shadows " + Get(g.Config, "ShadowDetail") +
                    ", glow " + Get(g.Config, "BboShasi");
            g.Done = Get(g.Config, "TextureDetail") == "0" &&
                     Get(g.Config, "ShadowDetail") == "0" &&
                     Get(g.Config, "DynamicLight") == "1" &&
                     Get(g.Config, "BboShasi") == "0" &&
                     Eq(Get(g.Config, "bDetailTexture"), "FALSE") &&
                     HasLayer(g.Exe) && HasGpuPref(g.Exe);
            g.Plan = g.Done
                ? "optimized (detail/shadows/glow off, raw mouse on, FSO & GPU preference set on all binaries)"
                : "disable shadows, post-glow & mouse accel, dynamic light to 1, set FSO & GPU pref on RF_Online.bin";
            return g;
        }

        static GameInfo Pw()
        {
            var g = new GameInfo { Id = "pw", Title = "Perfect World", Proc = "elementclient" };
            g.Exe = Pick(g.Id, new[]
            {
                @"C:\Program Files (x86)\Perfect World Entertainment\Perfect World\element\elementclient.exe",
                @"C:\Games\Perfect World\element\elementclient.exe",
                @"C:\PWI\element\elementclient.exe"
            });
            if (g.Exe.Length == 0)
            {
                g.Now = "not found - specify elementclient.exe with the button below";
                return g;
            }
            g.Found = true;
            g.Config = "";
            g.Done = HasLayer(g.Exe) && HasGpuPref(g.Exe);
            g.Now = g.Done ? "client found (FSO disabled & GPU preference set)" : "client found";
            g.Plan = g.Done
                ? "already configured (fullscreen optimizations off, high-perf GPU preference set)"
                : "disable fullscreen optimizations & set DXGI flip-model / high-perf GPU preference";
            return g;
        }

        static GameInfo Osrs()
        {
            var g = new GameInfo { Id = "osrs", Title = "Old School RuneScape (RuneLite + 117 HD)", Proc = "RuneLite" };
            g.Exe = Pick(g.Id, new[]
            {
                Path.Combine(Local, @"RuneLite\RuneLite.exe"),
                @"C:\Program Files\RuneLite\RuneLite.exe",
                @"C:\Program Files (x86)\RuneLite\RuneLite.exe"
            });
            if (g.Exe.Length > 0)
            {
                string jreJavaw = Path.Combine(Path.GetDirectoryName(g.Exe), @"jre\bin\javaw.exe");
                if (File.Exists(jreJavaw)) g.ExtraExes = new[] { jreJavaw };
            }
            g.Config = Profile();
            if (g.Exe.Length == 0 && g.Config.Length == 0) return g;
            g.Found = true;

            if (g.Config.Length == 0) { g.Now = "settings profile not found"; return g; }
            RetireWholeFileBackup(g.Config);

            string wantFps = OsrsTargetFpsString;
            string mode = Renderer(g.Config);
            if (mode == "hd")
            {
                bool car = Cfg.Profile == "car";
                int heapMb = GetRuneLiteHeapMb(g.Exe);
                bool heapOk = string.IsNullOrEmpty(RuneLiteLauncherConfig(g.Exe)) || heapMb >= 1024;
                int rung = OsrsRung();
                g.Done = HdMatches(g.Config, rung, wantFps, car) && (!car || CarDone(g.Config)) && heapOk;

                string profileName = car ? "In the Car (Battery)" : "Max Quality / Stable 60";
                g.Now = "Target: " + wantFps + " FPS  ·  Profile: " + profileName + "  ·  Status: " + (g.Done ? "Optimized" : "Not Optimized");
                string verdict = Saved("osrsVerdict");
                g.Plan = car ? "In the Car: " + CarFps + " FPS, scene " + CarScale + "%, 1024 shadows"
                             : (g.Done ? "" : "Optimize applies ") + RungNames[rung] + " for " + HwInfo.GpuName + " @ " + OsrsWinW + "x" + OsrsWinH +
                               " window  ·  " + RungSummary(rung) + (heapMb > 0 ? "  ·  " + heapMb + " MB JVM" : "") +
                               (verdict.Length > 0 ? "  ·  last test: " + verdict : "");
                int skipped = 0;
                foreach (var kv in HdPlan(rung, wantFps)) if (RlSchema.Check(kv[0], kv[1]) != null) skipped++;
                if (RlSchema.For("hd") == null) g.Plan = "117 HD settings could not be read from the installed plugin - Optimize leaves them unchanged";
                else if (skipped > 0) g.Plan += "  ·  " + skipped + " preset settings not offered by this 117 HD version (left unchanged)";
            }
            else if (mode == "gpu")
            {
                g.Now = "Target: " + wantFps + " FPS  ·  Profile: GPU Plugin  ·  Status: " + (g.Done ? "Optimized" : "Not Optimized");
                g.Done = Num(Prop(g.Config, "gpu.drawDistance")) <= 90 &&
                         MsaaAtMost4(Prop(g.Config, "gpu.antiAliasingMode")) &&
                         Prop(g.Config, "gpu.fpsTarget") == wantFps;
                g.Plan = g.Done ? "GPU plugin tuned (" + wantFps + " FPS locked, distance <= 90, AA <= x4)"
                                : "Lock to " + wantFps + " FPS, draw distance <= 90, AA <= x4, background 15 FPS cap";
            }
            else
            {
                g.Now = "Target: 60 FPS  ·  Profile: Max Quality / Stable 60  ·  Status: 117 HD Disabled";
                g.Done = false;
                g.Plan = "Enable the 117 HD or GPU plugin inside RuneLite so hardware acceleration is active";
            }
            return g;
        }

        static GameInfo Custom()
        {
            var g = new GameInfo { Id = "custom", Title = "Custom Game (.exe)" };
            g.Exe = Pick(g.Id, new string[0]);
            if (g.Exe.Length == 0)
            {
                g.Now = "specify any game .exe with the button below to apply Windows gaming optimizations";
                return g;
            }
            g.Found = true;
            g.Proc = Path.GetFileNameWithoutExtension(g.Exe);
            g.Title = "Custom: " + Path.GetFileName(g.Exe);
            g.Done = HasLayer(g.Exe) && HasGpuPref(g.Exe);
            g.Now = g.Exe;
            g.Plan = g.Done
                ? "fullscreen optimizations disabled, high-performance GPU & DXGI flip-model enabled"
                : "disable fullscreen optimizations, enable DXGI flip-model & high-performance GPU preference";
            return g;
        }

        static string Renderer(string profile)
        {
            if (Prop(profile, "runelite.117hdplugin") == "true" || Prop(profile, "runelite.hdplugin") == "true") return "hd";
            if (Prop(profile, "runelite.gpuplugin") == "true") return "gpu";
            string ext = Prop(profile, "runelite.externalPlugins");
            if (ext != "?" && ext.IndexOf("117hd", StringComparison.OrdinalIgnoreCase) >= 0) return "hd";
            return "";
        }

        public static bool HasHd(string profile) { return Renderer(profile) == "hd"; }

        public static string RuneLiteLauncherConfig(string exe)
        {
            try
            {
                if (!string.IsNullOrEmpty(exe))
                {
                    string p = Path.Combine(Path.GetDirectoryName(exe), "config.json");
                    if (File.Exists(p)) return p;
                }
                string def = Path.Combine(Local, @"RuneLite\config.json");
                return File.Exists(def) ? def : "";
            }
            catch { return ""; }
        }

        public static int GetRuneLiteHeapMb(string exe)
        {
            try
            {
                string cfg = RuneLiteLauncherConfig(exe);
                if (cfg.Length == 0) return 0;
                string txt = File.ReadAllText(cfg, Encoding.UTF8);
                var m = Regex.Match(txt, @"-Xmx(\d+)([mMgG])");
                if (!m.Success) return 0;
                int v = int.Parse(m.Groups[1].Value);
                if (string.Equals(m.Groups[2].Value, "g", StringComparison.OrdinalIgnoreCase)) v *= 1024;
                return v;
            }
            catch { return 0; }
        }

        static void OptRuneLiteLauncher(string exe, Action<string> log)
        {
            try
            {
                string cfg = RuneLiteLauncherConfig(exe);
                if (cfg.Length == 0) return;
                int curMb = GetRuneLiteHeapMb(exe);
                if (curMb >= 1024) return;

                string bak = cfg + ".pctune.bak";
                if (!File.Exists(bak)) File.Copy(cfg, bak);

                string txt = File.ReadAllText(cfg, Encoding.UTF8);
                string updated = Regex.Replace(txt, @"-Xmx\d+[mMgG]", "-Xmx1024m");
                if (updated != txt)
                {
                    File.WriteAllText(cfg, updated, new UTF8Encoding(false));
                    if (log != null)
                        log("  [+] RuneLite JVM heap limit: " + curMb + " MB -> 1024 MB (768 MB default runs tight with quest-helper/shortest-path, causing GC pauses)");
                }
            }
            catch (Exception ex)
            {
                if (log != null) log("  [x] RuneLite config.json: " + ex.Message);
            }
        }

        static int Num(string s)
        {
            int n;
            return int.TryParse(s, out n) ? n : int.MaxValue;
        }

        static int ResNum(string s)
        {
            if (string.IsNullOrEmpty(s) || !s.StartsWith("RES_", StringComparison.OrdinalIgnoreCase))
                return int.MaxValue;
            int n;
            return int.TryParse(s.Substring(4), out n) ? n : int.MaxValue;
        }

        static bool MsaaAtMost4(string s)
        {
            return s == "DISABLED" || s == "MSAA_2" || s == "MSAA_4";
        }

        static string Human(string v)
        {
            switch (v)
            {
                case "DISABLED": case "OFF": return "off";
                case "FAST": return "fast";
                case "DETAILED": return "detailed";
                case "?": return "?";
                default: return v.StartsWith("MSAA_") ? "x" + v.Substring(5) : v.ToLowerInvariant();
            }
        }

        public static string Profile()
        {
            try
            {
                string dir = Path.Combine(Home, @".runelite\profiles2");
                if (Directory.Exists(dir))
                {
                    string json = Path.Combine(dir, "profiles.json");
                    if (File.Exists(json))
                    {
                        foreach (Match m in Regex.Matches(File.ReadAllText(json), @"\{[^{}]*\}"))
                        {
                            string o = m.Value;
                            if (!Regex.IsMatch(o, @"""active""\s*:\s*true")) continue;
                            var id = Regex.Match(o, @"""id""\s*:\s*(-?\d+)");
                            var nm = Regex.Match(o, @"""name""\s*:\s*""([^""]*)""");
                            if (!id.Success || !nm.Success) continue;
                            string f = Path.Combine(dir, nm.Groups[1].Value + "-" + id.Groups[1].Value + ".properties");
                            if (File.Exists(f)) return f;
                        }
                    }

                    string best = "";
                    DateTime newest = DateTime.MinValue;
                    foreach (string f in Directory.GetFiles(dir, "*.properties"))
                    {
                        if (Path.GetFileName(f).StartsWith("$")) continue;
                        var t = File.GetLastWriteTime(f);
                        if (t > newest) { newest = t; best = f; }
                    }
                    if (best.Length > 0) return best;
                }
                string legacy = Path.Combine(Home, @".runelite\settings.properties");
                return File.Exists(legacy) ? legacy : "";
            }
            catch { return ""; }
        }

        static string KeysFile(string profile) { return profile + ".pctune.keys"; }

        static void RetireWholeFileBackup(string profile)
        {
            try
            {
                string bak = profile + ".pctune.bak";
                if (!File.Exists(bak)) return;
                string old = profile + ".pctune-old.bak";
                if (File.Exists(old)) File.Delete(old);
                File.Move(bak, old);
                Sys.ToFile("GAMES: old RuneLite profile copy moved to " + old);
            }
            catch { }
        }

        // Records each key's value before PC-Tune first changes it. false = no backup, so nothing may change.
        static bool RememberKeys(string profile, List<string> keys)
        {
            try
            {
                string f = KeysFile(profile);
                var had = File.Exists(f) ? new List<string>(File.ReadAllLines(f)) : new List<string>();
                int n = had.Count;
                foreach (string key in keys)
                {
                    if (had.Exists(l => l.StartsWith(key + "="))) continue;
                    string v = Prop(profile, key);
                    had.Add(key + "=" + (v == "?" ? RlProfile.Missing : v));
                }
                if (had.Count != n) File.WriteAllLines(f, had.ToArray(), new UTF8Encoding(false));
                return true;
            }
            catch { return false; }
        }

        // Checked right before every profile write; tests replace it.
        internal static Func<bool> ClientBusy = () => OsrsPids().Count > 0;

        static bool SetKey(string profile, string key, string val, Action<string> log)
        {
            return SetKeys(profile, new List<string[]> { new[] { key, val } }, log);
        }

        // Writes only values the installed plugin version accepts, backs up the originals, then commits
        // all edits in one atomic replacement.
        internal static bool SetKeys(string profile, List<string[]> edits, Action<string> log)
        {
            var ok = new List<string[]>();
            var keys = new List<string>();
            foreach (var kv in edits)
            {
                string why = RlSchema.Check(kv[0], kv[1]);
                if (why == null) { ok.Add(kv); keys.Add(kv[0]); }
                else log("  [ ] " + kv[0] + "=" + kv[1] + " skipped: " + why);
            }
            if (ok.Count == 0) return true;
            if (!RememberKeys(profile, keys)) { log("  [x] current values could not be backed up - profile left unchanged"); return false; }
            return RlProfile.Commit(profile, ok, log, ClientBusy);
        }

        // Puts back the values recorded before PC-Tune changed them; other settings stay as they are.
        // An original the installed plugin no longer accepts is left alone and reported.
        internal static bool RestoreKeys(string profile, Action<string> log)
        {
            string kf = KeysFile(profile);
            if (!File.Exists(kf)) return true;
            var edits = new List<string[]>();
            foreach (string l in File.ReadAllLines(kf))
            {
                int eq = l.IndexOf('=');
                if (eq <= 0) continue;
                string key = l.Substring(0, eq), val = l.Substring(eq + 1), why = RlSchema.Check(key, val);
                if (why == null) edits.Add(new[] { key, val });
                else log("  [ ] " + key + "=" + val + " not restored: " + why);
            }
            if (!RlProfile.Commit(profile, edits, log, ClientBusy)) return false;
            File.Delete(kf);
            return true;
        }

        public static void SetOsrsKey(string profile, string key, string val, Action<string> log)
        {
            if (string.IsNullOrEmpty(profile)) return;
            SetKey(profile, key, val, log);
        }

        // ---------------------------------------------------------- optimization
        public static bool Running(GameInfo g)
        {
            try
            {
                if (g.Id == "osrs" && OsrsPids().Count > 0) return true;
                if (g.Proc.Length > 0 && Process.GetProcessesByName(g.Proc).Length > 0)
                    return true;
                if (g.ExtraProcs != null)
                {
                    foreach (string p in g.ExtraProcs)
                        if (p.Length > 0 && Process.GetProcessesByName(p).Length > 0)
                            return true;
                }
            }
            catch { }
            return false;
        }

        static IEnumerable<string> AllExes(GameInfo g)
        {
            if (!string.IsNullOrEmpty(g.Exe)) yield return g.Exe;
            if (g.ExtraExes != null)
            {
                foreach (string x in g.ExtraExes)
                    if (!string.IsNullOrEmpty(x)) yield return x;
            }
        }

        public static void Optimize(GameInfo g, bool hardL2, Action<string> log)
        {
            if (!g.Found) return;
            if (Running(g))
            {
                log("  [!] " + g.Title + " is running - close it first, otherwise it will overwrite its settings");
                return;
            }
            log("== " + g.Title);

            foreach (string exe in AllExes(g))
            {
                if (Layer(exe))
                    log("  [+] fullscreen optimizations disabled: " + Path.GetFileName(exe));
                if (GpuPref(exe))
                    log("  [+] high-performance GPU & DXGI swapchain upgrade enabled: " + Path.GetFileName(exe));
                if (SetPerfOptions(exe, g.Id == "osrs" ? 6 : 3))
                    log("  [+] " + (g.Id == "osrs" ? "Above Normal" : "High") + " CPU / I/O / RAM page priority registered in IFEO: " + Path.GetFileName(exe));
            }

            if (g.Id == "l2") OptL2(g, hardL2, log);
            else if (g.Id == "rf") OptRf(g, log);
            else if (g.Id == "osrs") OptOsrs(g, log);
            else log("  [+] Windows gaming flags applied");
        }

        static bool HasLayer(string exe)
        {
            if (string.IsNullOrEmpty(exe)) return false;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(Layers))
                {
                    if (k == null) return false;
                    object had = k.GetValue(exe);
                    return had != null && had.ToString().IndexOf("DISABLEDXMAXIMIZEDWINDOWEDMODE",
                        StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { return false; }
        }

        static bool Layer(string exe)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(Layers))
                {
                    if (k == null) return false;
                    object had = k.GetValue(exe);
                    string want = "~ DISABLEDXMAXIMIZEDWINDOWEDMODE";
                    if (had != null && had.ToString().IndexOf("DISABLEDXMAXIMIZEDWINDOWEDMODE",
                        StringComparison.OrdinalIgnoreCase) >= 0) return false;
                    k.SetValue(exe, want, RegistryValueKind.String);
                    Sys.ToFile("GAMES layer " + exe);
                    return true;
                }
            }
            catch { return false; }
        }

        static bool HasGpuPref(string exe)
        {
            if (string.IsNullOrEmpty(exe)) return false;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(GpuPrefs))
                {
                    if (k == null) return false;
                    object had = k.GetValue(exe);
                    return had != null && had.ToString().IndexOf("GpuPreference=2",
                        StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { return false; }
        }

        static bool GpuPref(string exe)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(GpuPrefs))
                {
                    if (k == null) return false;
                    object had = k.GetValue(exe);
                    const string want = "GpuPreference=2;SwapEffectUpgradeEnable=1;";
                    if (had != null && string.Equals(had.ToString(), want, StringComparison.OrdinalIgnoreCase))
                        return false;
                    k.SetValue(exe, want, RegistryValueKind.String);
                    Sys.ToFile("GAMES gpuPref " + exe);
                    return true;
                }
            }
            catch { return false; }
        }

        const string IfeoBase = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\";

        static bool HasPerfOptions(string exe)
        {
            if (string.IsNullOrEmpty(exe)) return false;
            try
            {
                string fn = Path.GetFileName(exe);
                using (var k = Registry.LocalMachine.OpenSubKey(IfeoBase + fn + @"\PerfOptions"))
                    return k != null && k.GetValue("CpuPriorityClass") != null;
            }
            catch { return false; }
        }

        // cls: IFEO CpuPriorityClass (3 = High, 6 = Above Normal)
        static bool SetPerfOptions(string exe, int cls)
        {
            if (string.IsNullOrEmpty(exe)) return false;
            try
            {
                string fn = Path.GetFileName(exe);
                string regPath = @"HKLM\" + IfeoBase + fn + @"\PerfOptions";
                object cur = Sys.GetReg(regPath, "CpuPriorityClass");
                bool ok1 = Sys.SetRegVerify(regPath, "CpuPriorityClass", cls, RegistryValueKind.DWord);
                bool ok2 = Sys.SetRegVerify(regPath, "IoPriority", 3, RegistryValueKind.DWord);
                bool ok3 = Sys.SetRegVerify(regPath, "PagePriority", 5, RegistryValueKind.DWord);
                if (!ok1 || !ok2 || !ok3) return false;
                Sys.ToFile("GAMES IFEO PerfOptions " + fn);
                return cur == null || Convert.ToInt32(cur) != cls;
            }
            catch { return false; }
        }

        public static void BoostRunningGames(Action<string> log)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "runelite", "lineageii", "l2", "rf-default", "rf_online", "rf_online_4g", "elementclient"
            };
            foreach (var g in List())
            {
                foreach (string exe in AllExes(g))
                {
                    string n = Path.GetFileNameWithoutExtension(exe);
                    if (!string.IsNullOrEmpty(n)) names.Add(n);
                }
            }
            foreach (string procName in names)
            {
                try
                {
                    foreach (var p in Process.GetProcessesByName(procName))
                    {
                        using (p)
                        {
                            try
                            {
                                // RuneLite runs windowed, so DWM must still compose its frames: Above Normal, not High.
                                p.PriorityClass = string.Equals(procName, "runelite", StringComparison.OrdinalIgnoreCase)
                                    ? ProcessPriorityClass.AboveNormal : ProcessPriorityClass.High;
                                p.PriorityBoostEnabled = true;
                                Sys.DisableProcessEcoQoS(p);
                                if (log != null) log("  [+] Active game boosted (" + p.PriorityClass + " CPU priority, EcoQoS off): " + p.ProcessName);
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
        }

        static void OptL2(GameInfo g, bool hard, Action<string> log)
        {
            if (g.Config.Length == 0 || !File.Exists(g.Config))
            {
                log("  [ ] Option.ini not created yet (launch the game once); Windows executable flags applied");
                return;
            }
            Backup(g.Config);
            Pair(g.Config, "YebisGlow", "0", log);
            Pair(g.Config, "YebisDOF", "False", log);
            Pair(g.Config, "YebisAntiAliasing", "False", log);
            Pair(g.Config, "PawnShadow", "False", log);
            Pair(g.Config, "RenderDeco", "False", log);
            Pair(g.Config, "TextureDetail", "0", log);
            Pair(g.Config, "EffectDetail", "0", log);

            if (hard)
            {
                Pair(g.Config, "RENDERCHARACTERCOUNT", "50", log);
                log("  [!] character limit set to 50 - improves FPS in mass PvP/sieges");
            }
        }

        static void OptRf(GameInfo g, Action<string> log)
        {
            if (g.Config.Length == 0 || !File.Exists(g.Config))
            {
                log("  [ ] R3Engine.ini not created yet (launch the game once); Windows executable flags applied");
                return;
            }
            Backup(g.Config);
            Pair(g.Config, "TextureDetail", "0", log);
            Pair(g.Config, "ShadowDetail", "0", log);
            Pair(g.Config, "bDetailTexture", "FALSE", log);
            Pair(g.Config, "DynamicLight", "1", log);
            Pair(g.Config, "BboShasi", "0", log);
            Pair(g.Config, "bMouseAccelation", "FALSE", log);
        }

        // ------------------------------------------------ "In the car" profile
        const string CarFps = "30";
        const int CarScale = 75;
        const string CarShadow = "RES_1024";

        static string CarValue(string key)
        {
            switch (key)
            {
                case "hd.fpsTarget":
                case "gpu.fpsTarget": return CarFps;
                case "hd.sceneResolutionScale": return "" + CarScale;
                case "hd.shadowResolution": return CarShadow;
                default: return null;
            }
        }

        static bool CarDone(string c)
        {
            return Num(Prop(c, "hd.sceneResolutionScale")) <= CarScale &&
                   ResNum(Prop(c, "hd.shadowResolution")) <= 1024;
        }

        static void ApplyCarKeys(string c, string mode, Action<string> log)
        {
            if (mode != "hd") return;
            if (Num(Prop(c, "hd.sceneResolutionScale")) > CarScale)
                SetKey(c, "hd.sceneResolutionScale", "" + CarScale, log);
            if (ResNum(Prop(c, "hd.shadowResolution")) > 1024)
                SetKey(c, "hd.shadowResolution", CarShadow, log);
        }

        static void ReleaseCarKeys(string c, Action<string> log)
        {
            try
            {
                string f = KeysFile(c);
                if (!File.Exists(f)) return;
                var keep = new List<string>();
                var edits = new List<string[]>();
                foreach (string l in File.ReadAllLines(f))
                {
                    int eq = l.IndexOf('=');
                    string k = eq > 0 ? l.Substring(0, eq) : "";
                    string car = CarValue(k);
                    if (car == null) { keep.Add(l); continue; }
                    string orig = l.Substring(eq + 1);
                    if (Prop(c, k) == car && RlSchema.Check(k, orig) == null) edits.Add(new[] { k, orig });
                }
                if (!RlProfile.Commit(c, edits, log, ClientBusy)) return;
                if (keep.Count == 0) File.Delete(f);
                else File.WriteAllLines(f, keep.ToArray());
            }
            catch { }
        }

        public static void SyncCar(Action<string> log)
        {
            try
            {
                if (Cfg.Profile == "car") return;
                string c = Profile();
                if (c.Length == 0) return;
                string f = KeysFile(c);
                if (!File.Exists(f)) return;

                bool pending = false;
                foreach (string l in File.ReadAllLines(f))
                {
                    int eq = l.IndexOf('=');
                    if (eq > 0 && CarValue(l.Substring(0, eq)) != null) { pending = true; break; }
                }
                if (!pending) return;
                if (OsrsPids().Count > 0) return;

                log("== RuneLite is closed - restoring standard graphics settings");
                ReleaseCarKeys(c, log);
            }
            catch { }
        }

        public static void CarTweaks(bool on, Action<string> log)
        {
            try
            {
                string c = Profile();
                if (c.Length == 0) return;
                string mode = Renderer(c);
                if (mode.Length == 0) return;

                bool pending = on ? true : File.Exists(KeysFile(c));
                if (!pending) return;

                if (OsrsPids().Count > 0)
                {
                    log(on ? "  [!] RuneLite is running - close it and press «Optimize» on the «Games» tab " +
                             "to switch the client to " + CarFps + " fps"
                           : "  [!] RuneLite is running - standard settings will be restored automatically once you close it");
                    return;
                }

                if (on)
                {
                    log("  [ ] RuneLite: " + CarFps + " fps" + (mode == "hd" ? ", scene " + CarScale + "%, shadows <= 1024" : ""));
                    SetKey(c, mode == "hd" ? "hd.unlockFps" : "gpu.unlockFps", "true", log);
                    SetKey(c, mode == "hd" ? "hd.fpsTarget" : "gpu.fpsTarget", CarFps, log);
                    ApplyCarKeys(c, mode, log);
                }
                else
                {
                    ReleaseCarKeys(c, log);
                }
            }
            catch (Exception ex) { log("  [x] RuneLite: " + ex.Message); }
        }

        static void OptOsrs(GameInfo g, Action<string> log)
        {
            if (g.Config.Length == 0) return;
            string mode = Renderer(g.Config);
            string c = g.Config;
            bool car = Cfg.Profile == "car";
            if (!car) ReleaseCarKeys(c, log);
            string fps = OsrsTargetFpsString;

            // If the FPS Control plugin is active, prevent its foreground cap from fighting GPU/117HD
            // while capping unfocused FPS to 15 to save power when alt-tabbed.
            if (Prop(c, "runelite.fpsplugin") == "true")
            {
                SetKey(c, "fpscontrol.limitFps", "false", log);
                SetKey(c, "fpscontrol.limitFpsUnfocused", "true", log);
                if (Num(Prop(c, "fpscontrol.maxFpsUnfocused")) > 15)
                    SetKey(c, "fpscontrol.maxFpsUnfocused", "15", log);
            }

            if (mode == "hd")
            {
                int rung = TuneRung(log);
                OptRuneLiteLauncher(g.Exe, log);
                FitWindow(c, log);
                var plan = HdPlan(rung, fps);
                string hdJar = RlSchema.JarFor("hd");
                if (RlSchema.For("hd") == null)
                {
                    log("  [!] the installed 117 HD settings could not be read" + (hdJar == null ? " (plugin jar not identified)" : "") +
                        " - 117 HD settings left unchanged");
                    return;
                }
                log("  [ ] 117 HD settings checked against " + Path.GetFileName(hdJar));
                if (!SetKeys(c, plan, log)) return;
                if (car) ApplyCarKeys(c, mode, log);
                // Read the profile back: success means the file holds the values, not that a write ran.
                int ok = 0, total = 0;
                foreach (var kv in plan)
                {
                    if (RlSchema.Check(kv[0], kv[1]) != null || (car && CarValue(kv[0]) != null)) continue;
                    total++;
                    if (string.Equals(Prop(c, kv[0]), kv[1], StringComparison.OrdinalIgnoreCase)) ok++;
                    else log("  [x] " + kv[0] + " did not persist");
                }
                log("  [" + (ok == total ? "+" : "!") + "] 117 HD " + RungNames[rung] + " verified: " + ok + "/" + total +
                    " keys  ·  " + RungSummary(rung) + "  ·  " + fps + " FPS via 117 HD limiter only");
                return;
            }

            if (mode == "gpu")
            {
                if (Num(Prop(c, "gpu.drawDistance")) > 90) SetKey(c, "gpu.drawDistance", "90", log);
                if (!MsaaAtMost4(Prop(c, "gpu.antiAliasingMode"))) SetKey(c, "gpu.antiAliasingMode", "MSAA_4", log);
                SetKey(c, "gpu.uiScalingMode", "LINEAR", log);
                SetKey(c, "gpu.unlockFps", "true", log);
                SetKey(c, "gpu.fpsTarget", fps, log);
                SetKey(c, "gpu.vsyncMode", "OFF", log);
                log("  [ ] GPU plugin target set to " + fps + " FPS (matching display refresh rate)");
                return;
            }

            log("  [ ] neither GPU nor 117HD is enabled - enable one of them inside RuneLite");
        }

        // ---------------------------------------------------------- rollback
        public static void Restore(GameInfo g, Action<string> log)
        {
            if (!g.Found) return;
            if (Running(g))
            {
                log("  [!] " + g.Title + " is running - close it and repeat");
                return;
            }
            log("== " + g.Title);
            bool any = false;
            if (g.Id == "osrs" && g.Config.Length > 0)
            {
                string kf = KeysFile(g.Config);
                if (File.Exists(kf))
                {
                    try
                    {
                        if (RestoreKeys(g.Config, log))
                            log("  [+] values changed by PC-Tune restored; other plugin settings untouched");
                        else
                            log("  [!] RuneLite settings not restored - close RuneLite and press Restore again");
                        // Only the heap value comes back: RuneLite updates rewrite the rest of config.json.
                        string lc = RuneLiteLauncherConfig(g.Exe);
                        if (lc.Length > 0 && File.Exists(lc + ".pctune.bak"))
                        {
                            Match old = Regex.Match(File.ReadAllText(lc + ".pctune.bak"), @"-Xmx\d+[mMgG]");
                            if (old.Success)
                                File.WriteAllText(lc, Regex.Replace(File.ReadAllText(lc), @"-Xmx\d+[mMgG]", old.Value), new UTF8Encoding(false));
                            File.Delete(lc + ".pctune.bak");
                            log("  [+] RuneLite JVM heap restored: " + old.Value);
                        }
                        any = true;
                    }
                    catch (Exception ex) { log("  [x] " + ex.Message); }
                }
            }
            else if (g.Config.Length > 0)
            {
                string bak = g.Config + ".pctune.bak";
                if (File.Exists(bak))
                {
                    try
                    {
                        File.Copy(bak, g.Config, true);
                        File.Delete(bak);
                        log("  [+] settings restored from backup");
                        any = true;
                    }
                    catch (Exception ex) { log("  [x] " + ex.Message); }
                }
            }
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(Layers, true))
                {
                    if (k != null)
                    {
                        foreach (string exe in AllExes(g))
                        {
                            if (k.GetValue(exe) != null)
                            {
                                k.DeleteValue(exe, false);
                                log("  [+] fullscreen optimizations restored: " + Path.GetFileName(exe));
                                any = true;
                            }
                        }
                    }
                }
            }
            catch { }
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(GpuPrefs, true))
                {
                    if (k != null)
                    {
                        foreach (string exe in AllExes(g))
                        {
                            if (k.GetValue(exe) != null)
                            {
                                k.DeleteValue(exe, false);
                                log("  [+] DirectX GPU preference restored: " + Path.GetFileName(exe));
                                any = true;
                            }
                        }
                    }
                }
            }
            catch { }
            try
            {
                foreach (string exe in AllExes(g))
                {
                    string fn = Path.GetFileName(exe);
                    if (string.IsNullOrEmpty(fn)) continue;
                    if (HasPerfOptions(exe))
                    {
                        if (Sys.DelRegSubKeyTree(@"HKLM\" + IfeoBase + fn, "PerfOptions") && !HasPerfOptions(exe))
                        {
                            log("  [+] IFEO priority restored: " + fn);
                            any = true;
                        }
                    }
                }
            }
            catch { }
            if (!any) log("  [ ] nothing to restore");
        }

        public static bool HasBackup(GameInfo g)
        {
            if (g.Id == "osrs" && g.Config.Length > 0 && File.Exists(KeysFile(g.Config))) return true;
            if (g.Id != "osrs" && g.Config.Length > 0 && File.Exists(g.Config + ".pctune.bak")) return true;
            foreach (string exe in AllExes(g))
                if (HasLayer(exe) || HasGpuPref(exe) || HasPerfOptions(exe)) return true;
            return false;
        }

        // ---------------------------------------------------------- files
        static void Backup(string file)
        {
            try
            {
                string bak = file + ".pctune.bak";
                if (!File.Exists(bak)) File.Copy(file, bak);
            }
            catch { }
        }

        public static string Get(string file, string key)
        {
            try
            {
                string t = File.ReadAllText(file, Encoding.Default);
                var m = Regex.Match(t, @"(?m)^\s*" + Regex.Escape(key) + @"\s*=\s*(.*)$");
                return m.Success ? m.Groups[1].Value.Trim() : "?";
            }
            catch { return "?"; }
        }

        static void Pair(string file, string key, string val, Action<string> log)
        {
            try
            {
                string t = File.ReadAllText(file, Encoding.Default);
                string pat = @"(?m)^(\s*" + Regex.Escape(key) + @"\s*=).*$";
                if (!Regex.IsMatch(t, pat)) return;
                string was = Get(file, key);
                if (string.Equals(was, val, StringComparison.OrdinalIgnoreCase)) return;
                File.WriteAllText(file, Regex.Replace(t, pat, "${1}" + val), Encoding.Default);
                log("  [+] " + key + ": " + was + " -> " + val);
            }
            catch (Exception ex) { log("  [x] " + key + ": " + ex.Message); }
        }

        // ---------------------------------------------------------- properties
        public static string Prop(string file, string key)
        {
            try
            {
                foreach (string line in File.ReadAllLines(file))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0 && string.Equals(line.Substring(0, eq).Trim(), key,
                        StringComparison.Ordinal))
                        return line.Substring(eq + 1).Trim();
                }
            }
            catch { }
            return "?";
        }

        static bool Eq(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
