// PC-Tune - Hardware-aware Windows 10/11 & Gaming Optimization Utility
// Compiles with the built-in .NET Framework 4.8 csc.exe compiler with zero external dependencies.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.IO.Pipes;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

namespace PCTune
{
    enum TweakState
    {
        Unknown = 0,
        NotApplied = 1,
        Applied = 2,
        Unsupported = 3,
        Incompatible = 4
    }

    // ---------------------------------------------------------------- system infrastructure
    static class Sys
    {
        public static string LogFile = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "PC-Tune.log");

        public static void ToFile(string s)
        {
            try { File.AppendAllText(LogFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + s + "\r\n", Encoding.UTF8); }
            catch { }
        }

        // Trim log file on startup if it exceeds 2 MB, keeping the last 1 MB aligned to a full line.
        public static void TrimLog()
        {
            try
            {
                var fi = new FileInfo(LogFile);
                if (!fi.Exists || fi.Length < 2 * 1024 * 1024) return;
                byte[] all = File.ReadAllBytes(LogFile);
                int from = all.Length - 1024 * 1024;
                while (from < all.Length && all[from] != (byte)'\n') from++;
                from++;
                if (from >= all.Length) return;
                byte[] tail = new byte[all.Length - from];
                Array.Copy(all, from, tail, 0, tail.Length);
                File.WriteAllBytes(LogFile, tail);
            }
            catch { }
        }

        // Read-only commands are not logged to avoid cluttering the log file during polling.
        static bool ReadOnly(string exe, string args)
        {
            string a = (args ?? "").TrimStart().ToLowerInvariant();
            return a.StartsWith("/list") || a.StartsWith("/q") || a.StartsWith("/getactivescheme")
                || a.StartsWith("/a") || a.StartsWith("qc ") || a.StartsWith("query")
                || a.StartsWith("-list") || a.StartsWith("behavior query");
        }

        static bool? isAdminCached;
        public static bool IsAdmin
        {
            get
            {
                if (!isAdminCached.HasValue)
                {
                    try
                    {
                        using (var id = WindowsIdentity.GetCurrent())
                            isAdminCached = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
                    }
                    catch { isAdminCached = false; }
                }
                return isAdminCached.Value;
            }
        }

        static bool NeedsElevation(string exe, string args)
        {
            string e = Path.GetFileNameWithoutExtension(exe ?? "").ToLowerInvariant();
            string a = (args ?? "").Trim().ToLowerInvariant();
            if (e == "bcdedit" || e == "dism" || e == "sfc" || e == "defrag" || e == "cctk") return true;
            if (e == "sc" && !a.StartsWith("qc ") && !a.StartsWith("query")) return true;
            if (e == "schtasks" && a.Contains("/change")) return true;
            if (e == "netsh" && a.Contains("set ")) return true;
            if (e == "powercfg" && a.StartsWith("/hibernate")) return true;
            if (e == "powershell" && (a.Contains("mmagent") || a.Contains("computerrestore") || a.Contains("checkpoint-computer"))) return true;
            return false;
        }

        public static string Run(string exe, string args)
        {
            int code;
            return RunEx(exe, args, out code);
        }

        public static string RunAdmin(string exe, string args)
        {
            int code;
            return RunAdminEx(exe, args, out code);
        }

        public static string RunAdminEx(string exe, string args, out int exitCode, int timeoutMs = 120000)
        {
            exitCode = -1;
            if (!IsAdmin)
            {
                string resp = ElevatedCall("RUN", exe, args ?? "", timeoutMs.ToString(), "");
                if (resp != null && resp.StartsWith("OK|"))
                {
                    string[] parts = resp.Split('|');
                    if (parts.Length >= 3)
                    {
                        int.TryParse(parts[1], out exitCode);
                        if (!ReadOnly(exe, args)) ToFile("RUN(elev) " + exe + " " + args);
                        return FromB64(parts[2]);
                    }
                }
                if (resp != null && resp.StartsWith("ERR|"))
                    return "ERROR: " + FromB64(resp.Substring(4));
            }
            return RunLocalEx(exe, args, out exitCode, timeoutMs);
        }

        public static string RunEx(string exe, string args, out int exitCode, int timeoutMs = 120000)
        {
            exitCode = -1;
            if (!IsAdmin && !ReadOnly(exe, args) && NeedsElevation(exe, args))
                return RunAdminEx(exe, args, out exitCode, timeoutMs);
            return RunLocalEx(exe, args, out exitCode, timeoutMs);
        }

        static string RunLocalEx(string exe, string args, out int exitCode, int timeoutMs)
        {
            exitCode = -1;
            try
            {
                var psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                psi.StandardOutputEncoding = Encoding.GetEncoding(866);
                psi.StandardErrorEncoding = Encoding.GetEncoding(866);
                using (var p = Process.Start(psi))
                {
                    var sbOut = new StringBuilder();
                    var sbErr = new StringBuilder();
                    p.OutputDataReceived += delegate (object s, DataReceivedEventArgs e)
                    {
                        if (e.Data != null) lock (sbOut) sbOut.AppendLine(e.Data);
                    };
                    p.ErrorDataReceived += delegate (object s, DataReceivedEventArgs e)
                    {
                        if (e.Data != null) lock (sbErr) sbErr.AppendLine(e.Data);
                    };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        return "ERROR: Command timed out (" + exe + ")";
                    }
                    p.WaitForExit();
                    exitCode = p.ExitCode;
                    if (!ReadOnly(exe, args)) ToFile("RUN " + exe + " " + args);
                    string o;
                    lock (sbOut) o = sbOut.ToString();
                    lock (sbErr) o += sbErr.ToString();
                    return o;
                }
            }
            catch (Exception e) { return "ERROR: " + e.Message; }
        }

        // ---- On-demand UAC elevated helper IPC
        static readonly object elevLock = new object();
        static NamedPipeClientStream elevPipe;
        static StreamReader elevIn;
        static StreamWriter elevOut;
        static Process elevProc;
        static DateTime lastUacDeclinedUtc = DateTime.MinValue;

        static string ToB64(string s) { return Convert.ToBase64String(Encoding.UTF8.GetBytes(s ?? "")); }
        static string FromB64(string s)
        {
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(s ?? "")); }
            catch { return ""; }
        }

        static bool EnsureElevatedHelper()
        {
            if (elevPipe != null && elevPipe.IsConnected && elevProc != null && !elevProc.HasExited)
                return true;

            if ((DateTime.UtcNow - lastUacDeclinedUtc).TotalSeconds < 3.0)
                return false;

            CloseElevatedHelper();
            try
            {
                int myPid = Process.GetCurrentProcess().Id;
                string pipeName = "PCTune.Elev." + myPid + "." + Environment.TickCount;
                var psi = new ProcessStartInfo(Application.ExecutablePath)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    Arguments = "--elevated-helper " + myPid + " " + pipeName,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                elevProc = Process.Start(psi);
                if (elevProc == null) { lastUacDeclinedUtc = DateTime.UtcNow; return false; }

                elevPipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None);
                elevPipe.Connect(8000);
                elevIn = new StreamReader(elevPipe, Encoding.UTF8);
                elevOut = new StreamWriter(elevPipe, Encoding.UTF8) { AutoFlush = true };
                ToFile("ELEV: started on-demand elevated helper (PID " + elevProc.Id + ")");
                return true;
            }
            catch
            {
                lastUacDeclinedUtc = DateTime.UtcNow;
                ToFile("ELEV: UAC elevation declined or failed");
                CloseElevatedHelper();
                return false;
            }
        }

        public static void CloseElevatedHelper()
        {
            lock (elevLock)
            {
                try { if (elevOut != null) elevOut.WriteLine("QUIT"); } catch { }
                try { if (elevPipe != null) elevPipe.Dispose(); } catch { }
                try { if (elevProc != null && !elevProc.HasExited) elevProc.Kill(); } catch { }
                elevPipe = null; elevIn = null; elevOut = null; elevProc = null;
            }
        }

        static string ElevatedCall(string op, string a1, string a2, string a3, string a4)
        {
            lock (elevLock)
            {
                if (!EnsureElevatedHelper())
                    return "ERR|" + ToB64("Administrator permission required (UAC declined).");
                try
                {
                    elevOut.WriteLine(op + "|" + ToB64(a1) + "|" + ToB64(a2) + "|" + ToB64(a3) + "|" + ToB64(a4));
                    return elevIn.ReadLine();
                }
                catch (Exception ex)
                {
                    CloseElevatedHelper();
                    return "ERR|" + ToB64(ex.Message);
                }
            }
        }

        static bool ElevatedCall(string op, string a1, string a2, string a3, string a4, out string err)
        {
            err = null;
            string resp = ElevatedCall(op, a1, a2, a3, a4);
            if (resp != null && resp.StartsWith("OK|"))
                return true;
            if (resp != null && resp.StartsWith("ERR|"))
                err = FromB64(resp.Substring(4));
            else
                err = "Administrator helper did not respond.";
            return false;
        }

        public static void RunElevatedServer(int parentPid, string pipeName)
        {
            var watcher = new Thread(delegate ()
            {
                while (true)
                {
                    Thread.Sleep(1500);
                    try
                    {
                        var p = Process.GetProcessById(parentPid);
                        if (p.HasExited) Environment.Exit(0);
                    }
                    catch { Environment.Exit(0); }
                }
            }) { IsBackground = true };
            watcher.Start();

            try
            {
                using (var srv = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte))
                {
                    srv.WaitForConnection();
                    using (var r = new StreamReader(srv, Encoding.UTF8))
                    using (var w = new StreamWriter(srv, Encoding.UTF8) { AutoFlush = true })
                    {
                        string line;
                        while ((line = r.ReadLine()) != null)
                        {
                            if (line == "QUIT") break;
                            string[] p = line.Split('|');
                            if (p.Length < 5) { w.WriteLine("ERR|" + ToB64("bad frame")); continue; }
                            string op = p[0];
                            string a1 = FromB64(p[1]), a2 = FromB64(p[2]), a3 = FromB64(p[3]), a4 = FromB64(p[4]);
                            try
                            {
                                if (op == "RUN")
                                {
                                    int tms = 120000;
                                    int.TryParse(a3, out tms);
                                    int code;
                                    string o = RunLocalEx(a1, a2, out code, tms);
                                    w.WriteLine("OK|" + code + "|" + ToB64(o));
                                }
                                else if (op == "SETREG")
                                {
                                    RegistryValueKind kind = (RegistryValueKind)Enum.Parse(typeof(RegistryValueKind), a3);
                                    object val = a4;
                                    if (kind == RegistryValueKind.DWord) val = int.Parse(a4);
                                    else if (kind == RegistryValueKind.QWord) val = long.Parse(a4);
                                    else if (kind == RegistryValueKind.Binary) val = Convert.FromBase64String(a4);
                                    string sub = a1;
                                    using (var k = RootOf(ref sub).CreateSubKey(sub))
                                        k.SetValue(a2, val, kind);
                                    w.WriteLine("OK|1");
                                }
                                else if (op == "DELREG")
                                {
                                    string sub = a1;
                                    using (var k = RootOf(ref sub).OpenSubKey(sub, true))
                                        if (k != null) k.DeleteValue(a2, false);
                                    w.WriteLine("OK|1");
                                }
                                else if (op == "DELREGTREE")
                                {
                                    string sub = a1;
                                    using (var k = RootOf(ref sub).OpenSubKey(sub, true))
                                        if (k != null) k.DeleteSubKeyTree(a2, false);
                                    w.WriteLine("OK|1");
                                }
                                else if (op == "SVCSTOP")
                                {
                                    int tsec = 6;
                                    int.TryParse(a2, out tsec);
                                    using (var sc = new ServiceController(a1))
                                    {
                                        if (sc.Status != ServiceControllerStatus.Stopped &&
                                            sc.Status != ServiceControllerStatus.StopPending)
                                            sc.Stop();
                                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(tsec));
                                        if (sc.Status != ServiceControllerStatus.Stopped)
                                            throw new Exception("Service did not stop in time");
                                    }
                                    w.WriteLine("OK|1");
                                }
                                else if (op == "SVCSTART")
                                {
                                    int tsec = 6;
                                    int.TryParse(a2, out tsec);
                                    using (var sc = new ServiceController(a1))
                                    {
                                        if (sc.Status == ServiceControllerStatus.Stopped)
                                            sc.Start();
                                        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(tsec));
                                        if (sc.Status != ServiceControllerStatus.Running)
                                            throw new Exception("Service did not start in time");
                                    }
                                    w.WriteLine("OK|1");
                                }
                                else w.WriteLine("ERR|" + ToB64("unknown op"));
                            }
                            catch (Exception ex) { w.WriteLine("ERR|" + ToB64(ex.Message)); }
                        }
                    }
                }
            }
            catch { }
        }

        public static string Pow(string args) { return Run("powercfg.exe", args); }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern uint SetThreadExecutionState(uint esFlags);

        [DllImport("winmm.dll", SetLastError = true)]
        static extern uint timeBeginPeriod(uint uPeriod);

        [DllImport("winmm.dll", SetLastError = true)]
        static extern uint timeEndPeriod(uint uPeriod);

        [DllImport("ntdll.dll", SetLastError = true)]
        static extern int NtSetTimerResolution(uint DesiredResolution, bool SetResolution, out uint CurrentResolution);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetProcessInformation(IntPtr hProcess, int ProcessInformationClass, ref PROCESS_POWER_THROTTLING_STATE ProcessInformation, int ProcessInformationSize);

        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_POWER_THROTTLING_STATE
        {
            public uint Version;
            public uint ControlMask;
            public uint StateMask;
        }

        const int ProcessPowerThrottling = 4;
        const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;
        const uint PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION = 0x4;

        public static void DisableProcessEcoQoS(Process p)
        {
            try
            {
                var st = new PROCESS_POWER_THROTTLING_STATE
                {
                    Version = 1,
                    ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED | PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION,
                    StateMask = 0
                };
                SetProcessInformation(p.Handle, ProcessPowerThrottling, ref st, Marshal.SizeOf(st));
            }
            catch { }
        }

        static bool timerLocked;

        public static void SetHighResTimer(bool enable)
        {
            try
            {
                uint cur;
                if (enable && !timerLocked)
                {
                    timeBeginPeriod(1);
                    NtSetTimerResolution(5000, true, out cur);
                    timerLocked = true;
                }
                else if (!enable && timerLocked)
                {
                    NtSetTimerResolution(5000, false, out cur);
                    timeEndPeriod(1);
                    timerLocked = false;
                }
            }
            catch { }
        }

        public const uint ES_CONTINUOUS = 0x80000000;
        public const uint ES_SYSTEM_REQUIRED = 0x00000001;
        public const uint ES_DISPLAY_REQUIRED = 0x00000002;

        // Lightweight native PDH wildcard GPU 3D engine utilization (~1ms, zero WMI).
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        static extern uint PdhOpenQuery(IntPtr src, IntPtr data, out IntPtr q);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        static extern uint PdhAddEnglishCounter(IntPtr q, string path, IntPtr data, out IntPtr c);
        [DllImport("pdh.dll")]
        static extern uint PdhCollectQueryData(IntPtr q);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        static extern uint PdhGetFormattedCounterArray(IntPtr c, uint fmt, ref uint bufSize, out uint count, IntPtr buf);

        [StructLayout(LayoutKind.Sequential)]
        struct PDH_FMT_COUNTERVALUE { public uint CStatus; public double doubleValue; }
        [StructLayout(LayoutKind.Sequential)]
        struct PDH_FMT_COUNTERVALUE_ITEM { public IntPtr szName; public PDH_FMT_COUNTERVALUE FmtValue; }

        static IntPtr gpuQuery, gpuCounter;
        static readonly object gpuLock = new object();

        public static int SampleGpuPercent()
        {
            lock (gpuLock)
            {
                try
                {
                    if (gpuQuery == IntPtr.Zero)
                    {
                        if (PdhOpenQuery(IntPtr.Zero, IntPtr.Zero, out gpuQuery) != 0) return 0;
                        if (PdhAddEnglishCounter(gpuQuery, @"\GPU Engine(*engtype_3D)\Utilization Percentage", IntPtr.Zero, out gpuCounter) != 0)
                            return 0;
                        PdhCollectQueryData(gpuQuery);
                        return 0;
                    }
                    if (PdhCollectQueryData(gpuQuery) != 0) return 0;
                    uint sz = 0, cnt = 0;
                    PdhGetFormattedCounterArray(gpuCounter, 0x00000200, ref sz, out cnt, IntPtr.Zero);
                    if (sz == 0 || cnt == 0) return 0;
                    IntPtr buf = Marshal.AllocHGlobal((int)sz);
                    try
                    {
                        if (PdhGetFormattedCounterArray(gpuCounter, 0x00000200, ref sz, out cnt, buf) != 0) return 0;
                        double sum = 0;
                        int step = Marshal.SizeOf(typeof(PDH_FMT_COUNTERVALUE_ITEM));
                        long basePtr = buf.ToInt64();
                        for (int i = 0; i < cnt; i++)
                        {
                            var it = (PDH_FMT_COUNTERVALUE_ITEM)Marshal.PtrToStructure(new IntPtr(basePtr + i * step), typeof(PDH_FMT_COUNTERVALUE_ITEM));
                            if (it.FmtValue.CStatus == 0 && !double.IsNaN(it.FmtValue.doubleValue))
                                sum += it.FmtValue.doubleValue;
                        }
                        int pct = (int)Math.Round(sum);
                        return pct < 0 ? 0 : pct > 100 ? 100 : pct;
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
                catch { return 0; }
            }
        }

        static RegistryKey RootOf(ref string path)
        {
            if (path.StartsWith("HKLM\\")) { path = path.Substring(5); return Registry.LocalMachine; }
            if (path.StartsWith("HKCU\\")) { path = path.Substring(5); return Registry.CurrentUser; }
            throw new ArgumentException("registry path: " + path);
        }

        public const string CCTK = @"C:\Program Files (x86)\Dell\Command Configure\X86_64\cctk.exe";
        public static bool HasBios { get { return File.Exists(CCTK); } }

        // Cache Dell BIOS values so UI/diagnostics do not block repeatedly on cctk.exe.
        static readonly Dictionary<string, string> biosCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static string BiosGet(string opt, bool forceRefresh = false)
        {
            if (!HasBios) return "";
            lock (biosCache)
            {
                string cached;
                if (!forceRefresh && biosCache.TryGetValue(opt, out cached)) return cached;
            }
            string s = Run(CCTK, "--" + opt).Trim();
            int eq = s.IndexOf('=');
            string val = eq >= 0 && s.StartsWith(opt, StringComparison.OrdinalIgnoreCase)
                       ? s.Substring(eq + 1).Trim() : "";
            lock (biosCache) { biosCache[opt] = val; }
            return val;
        }

        public static bool BiosSet(string opt, string val)
        {
            if (!HasBios) return false;
            RunAdmin(CCTK, "--" + opt + "=" + val);
            string now = BiosGet(opt, true);
            return string.Equals(now, val, StringComparison.OrdinalIgnoreCase);
        }

        public static void Unhide(string sub, string setting)
        {
            Pow("-attributes " + sub + " " + setting + " -ATTRIB_HIDE");
        }

        public static bool Ac(string sub, string setting, int v, bool backup = true)
        {
            if (backup) BackupPowerValue(sub, setting);
            Unhide(sub, setting);
            Pow("/setacvalueindex SCHEME_CURRENT " + sub + " " + setting + " " + v);
            int now = PowIdx(sub, setting, true);
            return now < 0 || now == v;
        }

        public static bool Dc(string sub, string setting, int v, bool backup = true)
        {
            if (backup) BackupPowerValue(sub, setting);
            Unhide(sub, setting);
            Pow("/setdcvalueindex SCHEME_CURRENT " + sub + " " + setting + " " + v);
            int now = PowIdx(sub, setting, false);
            return now < 0 || now == v;
        }

        public static string ActiveSchemeGuid()
        {
            object v = GetReg(@"HKLM\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes", "ActivePowerScheme");
            return v == null ? null : v.ToString().ToLowerInvariant();
        }

        public static int PowIdx(string sub, string setting, bool ac)
        {
            string s = ActiveSchemeGuid();
            if (string.IsNullOrEmpty(s)) return -1;
            string p = @"HKLM\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\" + s + "\\" + sub + "\\" + setting;
            object v = GetReg(p, ac ? "ACSettingIndex" : "DCSettingIndex");
            try { return v == null ? -1 : Convert.ToInt32(v); }
            catch { return -1; }
        }

        // Fallback query via powercfg /q when a setting has no local override in registry yet.
        public static int[] CurIndex(string sub, string setting)
        {
            int ac = PowIdx(sub, setting, true);
            int dc = PowIdx(sub, setting, false);
            if (ac >= 0 && dc >= 0) return new[] { ac, dc };

            var r = new[] { ac, dc };
            try
            {
                var ms = Regex.Matches(
                    Pow("/q SCHEME_CURRENT " + sub + " " + setting), @"0x([0-9a-fA-F]{8})");
                if (ms.Count >= 2)
                {
                    r[0] = Convert.ToInt32(ms[ms.Count - 2].Groups[1].Value, 16);
                    r[1] = Convert.ToInt32(ms[ms.Count - 1].Groups[1].Value, 16);
                }
            }
            catch { }
            return r;
        }

        const string PwrBackupKey = @"Software\PCTune\PowerBackup";
        const string RegBackupKey = @"Software\PCTune\RegBackup";

        public static void BackupPowerValue(string sub, string setting)
        {
            try
            {
                string scheme = ActiveSchemeGuid();
                if (string.IsNullOrEmpty(scheme)) return;
                using (var k = Registry.CurrentUser.CreateSubKey(PwrBackupKey))
                {
                    if (k == null) return;
                    if (k.GetValue("OriginalScheme") == null)
                        k.SetValue("OriginalScheme", scheme, RegistryValueKind.String);
                    if (k.GetValue("OriginalOverlay") == null)
                    {
                        string ovr = (GetReg(@"HKLM\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes", "ActiveOverlayAcPowerScheme") ?? "").ToString();
                        k.SetValue("OriginalOverlay", ovr, RegistryValueKind.String);
                    }
                    string id = scheme + "|" + sub + "|" + setting;
                    if (k.GetValue(id) != null) return;
                    int[] cur = CurIndex(sub, setting);
                    if (cur[0] >= 0 || cur[1] >= 0)
                        k.SetValue(id, cur[0] + "," + cur[1], RegistryValueKind.String);
                }
            }
            catch { }
        }

        public static int RestorePowerBackup(Action<string> log)
        {
            int count = 0;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(PwrBackupKey, true))
                {
                    if (k == null) return 0;
                    foreach (string name in k.GetValueNames())
                    {
                        if (!name.Contains("|")) continue;
                        string[] parts = name.Split('|');
                        if (parts.Length != 3) continue;
                        string scheme = parts[0], sub = parts[1], setting = parts[2];
                        string val = (k.GetValue(name) ?? "").ToString();
                        string[] acdc = val.Split(',');
                        if (acdc.Length != 2) continue;
                        int ac, dc;
                        if (int.TryParse(acdc[0], out ac) && ac >= 0)
                            Pow("/setacvalueindex " + scheme + " " + sub + " " + setting + " " + ac);
                        if (int.TryParse(acdc[1], out dc) && dc >= 0)
                            Pow("/setdcvalueindex " + scheme + " " + sub + " " + setting + " " + dc);
                        count++;
                        k.DeleteValue(name, false);
                    }
                    string origScheme = k.GetValue("OriginalScheme") as string;
                    if (!string.IsNullOrEmpty(origScheme))
                    {
                        Pow("/setactive " + origScheme);
                        k.DeleteValue("OriginalScheme", false);
                    }
                    else
                    {
                        Pow("/setactive SCHEME_CURRENT");
                    }
                    string origOvr = k.GetValue("OriginalOverlay") as string;
                    if (origOvr != null)
                    {
                        Pow("/overlaysetactive " + (origOvr.Length > 0 ? origOvr : OVERLAY_BAL));
                        k.DeleteValue("OriginalOverlay", false);
                    }
                }
            }
            catch (Exception ex)
            {
                if (log != null) log("  [!] Power backup restore warning: " + ex.Message);
            }
            return count;
        }

        public static void BackupRegValue(string path, string name)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RegBackupKey))
                {
                    if (k == null) return;
                    string id = path + "|" + name;
                    if (k.GetValue(id) != null) return;
                    object cur = GetReg(path, name);
                    if (cur == null)
                        k.SetValue(id, "__MISSING__", RegistryValueKind.String);
                    else if (cur is byte[])
                        k.SetValue(id, "Byte[]:" + Convert.ToBase64String((byte[])cur), RegistryValueKind.String);
                    else
                        k.SetValue(id, cur.GetType().Name + ":" + cur.ToString(), RegistryValueKind.String);
                }
            }
            catch { }
        }

        static bool NeedsAdminForPath(string path)
        {
            if (IsAdmin || string.IsNullOrEmpty(path)) return false;
            return !path.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase) &&
                   !path.StartsWith(@"HKEY_CURRENT_USER\", StringComparison.OrdinalIgnoreCase);
        }

        public static void SetReg(string path, string name, object value, RegistryValueKind kind)
        {
            BackupRegValue(path, name);
            if (NeedsAdminForPath(path))
            {
                string valStr;
                if (kind == RegistryValueKind.Binary && value is byte[])
                    valStr = Convert.ToBase64String((byte[])value);
                else
                    valStr = value != null ? value.ToString() : "";
                string err;
                if (!ElevatedCall("SETREG", path, name ?? "", kind.ToString(), valStr, out err))
                    throw new UnauthorizedAccessException(err ?? "Administrator privileges required.");
                ToFile("REG (ELEV) " + path + "\\" + name + " = " + valStr);
                return;
            }
            string p = path;
            using (var k = RootOf(ref p).CreateSubKey(p))
                k.SetValue(name, value, kind);
            ToFile("REG " + path + "\\" + name + " = " + value);
        }

        public static bool SetRegVerify(string path, string name, object value, RegistryValueKind kind)
        {
            try
            {
                SetReg(path, name, value, kind);
                object now = GetReg(path, name);
                if (now == null) return false;
                if (kind == RegistryValueKind.DWord || kind == RegistryValueKind.QWord)
                    return Convert.ToInt64(now) == Convert.ToInt64(value);
                if (kind == RegistryValueKind.Binary && now is byte[] && value is byte[])
                {
                    byte[] a = (byte[])now, b = (byte[])value;
                    if (a.Length != b.Length) return false;
                    for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
                    return true;
                }
                return string.Equals(now.ToString(), value.ToString(), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public static object GetReg(string path, string name)
        {
            string p = path;
            try { using (var k = RootOf(ref p).OpenSubKey(p)) return k == null ? null : k.GetValue(name); }
            catch { return null; }
        }

        public static void DelReg(string path, string name)
        {
            BackupRegValue(path, name);
            if (NeedsAdminForPath(path))
            {
                string err;
                ElevatedCall("DELREG", path, name ?? "", "", "", out err);
                ToFile("REG DEL (ELEV) " + path + "\\" + name);
                return;
            }
            string p = path;
            try
            {
                using (var k = RootOf(ref p).OpenSubKey(p, true))
                    if (k != null) k.DeleteValue(name, false);
                ToFile("REG DEL " + path + "\\" + name);
            }
            catch { }
        }

        public static bool DelRegSubKeyTree(string parentPath, string subKeyName)
        {
            try
            {
                if (NeedsAdminForPath(parentPath))
                {
                    string err;
                    return ElevatedCall("DELREGTREE", parentPath, subKeyName ?? "", "", "", out err);
                }
                string p = parentPath;
                using (var k = RootOf(ref p).OpenSubKey(p, true))
                {
                    if (k != null) k.DeleteSubKeyTree(subKeyName, false);
                }
                return true;
            }
            catch { return false; }
        }

        public static bool StopService(string name, int timeoutSec = 6)
        {
            try
            {
                if (!IsAdmin)
                {
                    string err;
                    return ElevatedCall("SVCSTOP", name, timeoutSec.ToString(), "", "", out err);
                }
                using (var sc = new ServiceController(name))
                {
                    if (sc.Status != ServiceControllerStatus.Stopped &&
                        sc.Status != ServiceControllerStatus.StopPending)
                    {
                        sc.Stop();
                    }
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(timeoutSec));
                    return sc.Status == ServiceControllerStatus.Stopped;
                }
            }
            catch { return false; }
        }

        public static bool StartService(string name, int timeoutSec = 6)
        {
            try
            {
                if (!IsAdmin)
                {
                    string err;
                    return ElevatedCall("SVCSTART", name, timeoutSec.ToString(), "", "", out err);
                }
                using (var sc = new ServiceController(name))
                {
                    if (sc.Status == ServiceControllerStatus.Stopped)
                        sc.Start();
                    sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(timeoutSec));
                    return sc.Status == ServiceControllerStatus.Running;
                }
            }
            catch { return false; }
        }

        // Explicit GUIDs for all powercfg subgroups and settings (enables direct registry verification).
        public const string SUB_PROC       = "54533251-82be-4824-96c1-47b60b740d00";
        public const string PROCMIN        = "893dee8e-2bef-41e0-89c6-b55d0929964c";
        public const string PROCMAX        = "bc5038f7-23e0-4960-96da-33abaf5935ec";
        public const string BOOST          = "be337238-0d82-4146-a960-4f3749d470c7";
        public const string CPMINCORES     = "0cc5b647-c1df-4637-891a-dec35c318583";
        public const string CPMAXCORES     = "ea062031-0e34-4ff1-9b6d-eb1059334028";
        public const string PERFEPP        = "36687f9e-e3a5-4dbf-b1dc-15eb381c6863";
        public const string IDLEDISABLE    = "5d76a2ca-e8c0-402f-a133-2158492d58ad";

        public const string SUB_USB        = "2a737441-1930-4402-8d77-b2bebba308a3";
        public const string USBSUSP        = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226";

        public const string SUB_PCIEXPRESS = "501a4d13-42af-4429-9fd1-a8218c268e20";
        public const string ASPM           = "ee12f906-d277-404b-b6da-e5fa1a576df5";

        public const string SUB_VIDEO      = "7516b95f-f776-4464-8c53-06167f40cc99";
        public const string VIDEOIDLE      = "3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e";
        public const string BRIGHTNESS     = "aded5e82-b909-4619-9949-f5d71dac0bcb";

        public const string SUB_BUTTONS    = "4f971e89-eebd-4455-a8de-9e59040e7347";
        public const string LIDACTION      = "5ca83367-6e45-459f-a27b-476b1d01c936";

        public const string SUB_WIFI       = "19cbb8fa-5279-450e-9fac-8a3d5fedd0c1";
        public const string WIFISAVE       = "12bbebe6-58d6-4636-95bb-3217ef867c1a";

        public const string SUB_SLEEP      = "238c9fa8-0aad-41ed-83f4-97be242c8f20";
        public const string STANDBYIDLE    = "29f6c1db-86da-48c5-9fdb-f2b67b1f44da";
        public const string UNATTENDSLEEP  = "7bc4a2f9-d8fc-4469-b07b-33eb785aaca0";
        public const string HYBRIDSLEEP    = "94ac6d29-73ce-41a6-809f-6363ba21b47e";
        public const string HIBERNATEIDLE  = "9d7815a6-7ee4-497e-8888-515a05f02364";
        public const string RTCWAKE        = "bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d";

        public const string SUB_DISK       = "0012ee47-9041-4b5d-9b77-535fba8b1442";
        public const string DISKIDLE       = "6738e2c4-e8a5-4a42-b16a-e040e769756e";

        public const string SUB_BATTERY    = "e73a048d-bf27-4f12-9731-8b2076e8891f";
        public const string BATACTIONCRIT  = "637ea02f-bbcb-4015-8e2c-a1c7b9c0b546";
        public const string BATLEVELCRIT   = "9a66d8d7-4ff7-4ef9-b5a2-5a326ca2a469";

        public const string SUB_INTELGFX   = "44f3beca-a7c0-460e-9df2-bb8b99e0cba6";
        public const string INTELGFXPLAN   = "3619c3f2-afb2-4afc-b0e9-e7fef372de36";

        public const string PWRTHROTTLE    = @"HKLM\SYSTEM\CurrentControlSet\Control\Power\PowerThrottling";
        public const string OVERLAY_MAX    = "ded574b5-45a0-4f42-8737-46345c09c238";
        public const string OVERLAY_ECO    = "961cc777-2547-4f9d-8174-7d86181b8a7a";
        public const string OVERLAY_BAL    = "00000000-0000-0000-0000-000000000000";

        public static bool OnBattery()
        {
            try { return SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline; }
            catch { return false; }
        }
    }

    // ---------------------------------------------------------------- hardware detection
    static class HwInfo
    {
        static bool initialized;
        static readonly object sync = new object();

        public static string OsCaption = "";
        public static int OsBuild;
        public static bool IsWin11;
        public static bool IsWin11_22H2Plus;

        public static string Manufacturer = "";
        public static string Model = "";
        public static string BiosVersion = "";
        public static bool IsDell;
        public static bool IsLaptop;

        public static string CpuName = "";
        public static int PhysicalCores = 4;
        public static int LogicalCores = Environment.ProcessorCount;
        public static bool IsIntelCpu;
        public static bool IsAmdCpu;
        public static bool IsHybridCpu;

        public static string GpuName = "";
        public static string GpuDriver = "";
        public static double GpuVramGb = 0;
        public static bool HasNvidiaGpu;
        public static bool HasAmdGpu;
        public static bool HasIntelGpu;
        public static bool HasDiscreteGpu;
        public static bool IsIntegratedOnly;

        public static int MonitorWidth = 1920;
        public static int MonitorHeight = 1080;
        public static int MonitorRefreshHz = 60;
        public static int MonitorMaxRefreshHz = 60;

        public static double TotalRamGb;
        public static int RamSticks;
        public static int RamSpeedMts;
        public static bool IsDualChannel;

        public static string DriveModel = "";
        public static string DriveType = "SSD"; // NVMe, SSD, HDD
        public static bool IsSsd = true;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmDeviceName;
            public short dmSpecVersion;
            public short dmDriverVersion;
            public short dmSize;
            public short dmDriverExtra;
            public int dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public int dmDisplayOrientation;
            public int dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel;
            public int dmPelsWidth;
            public int dmPelsHeight;
            public int dmDisplayFlags;
            public int dmDisplayFrequency;
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern bool EnumDisplaySettings(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

        public static void Ensure()
        {
            if (initialized) return;
            lock (sync)
            {
                if (initialized) return;
                initialized = true;
                ProbeFast();
            }
        }

        static void ProbeFast()
        {
            try
            {
                object bld = Sys.GetReg(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuildNumber");
                if (bld != null) int.TryParse(bld.ToString(), out OsBuild);
                if (OsBuild == 0) OsBuild = Environment.OSVersion.Version.Build;
                IsWin11 = OsBuild >= 22000;
                IsWin11_22H2Plus = OsBuild >= 22621;
                object prod = Sys.GetReg(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName");
                OsCaption = prod != null ? prod.ToString() : "Windows";
                if (IsWin11 && OsCaption.IndexOf("Windows 10", StringComparison.OrdinalIgnoreCase) >= 0)
                    OsCaption = OsCaption.Replace("Windows 10", "Windows 11");
            }
            catch { }

            try
            {
                Manufacturer = (Sys.GetReg(@"HKLM\HARDWARE\DESCRIPTION\System\BIOS", "SystemManufacturer") ?? "").ToString().Trim();
                Model = (Sys.GetReg(@"HKLM\HARDWARE\DESCRIPTION\System\BIOS", "SystemProductName") ?? "").ToString().Trim();
                BiosVersion = (Sys.GetReg(@"HKLM\HARDWARE\DESCRIPTION\System\BIOS", "BIOSVersion") ?? "").ToString().Trim();
                IsDell = Manufacturer.IndexOf("Dell", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { }

            try
            {
                var ps = SystemInformation.PowerStatus;
                IsLaptop = (ps.BatteryChargeStatus & BatteryChargeStatus.NoSystemBattery) == 0;
            }
            catch { }

            try
            {
                CpuName = (Sys.GetReg(@"HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString") ?? "").ToString().Trim();
                IsIntelCpu = CpuName.IndexOf("Intel", StringComparison.OrdinalIgnoreCase) >= 0;
                IsAmdCpu = CpuName.IndexOf("AMD", StringComparison.OrdinalIgnoreCase) >= 0 || CpuName.IndexOf("Ryzen", StringComparison.OrdinalIgnoreCase) >= 0;
                // Intel 12th+ Gen Core (12xxx, 13xxx, 14xxx, Core Ultra) uses hybrid P+E core architecture.
                if (IsIntelCpu && (Regex.IsMatch(CpuName, @"i[3579]-1[2345]\d{2,3}") || CpuName.IndexOf("Core(TM) Ultra", StringComparison.OrdinalIgnoreCase) >= 0))
                    IsHybridCpu = true;
            }
            catch { }

            try
            {
                var dm = new DEVMODE();
                dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
                if (EnumDisplaySettings(null, -1, ref dm)) // ENUM_CURRENT_SETTINGS = -1
                {
                    if (dm.dmPelsWidth > 0) MonitorWidth = dm.dmPelsWidth;
                    if (dm.dmPelsHeight > 0) MonitorHeight = dm.dmPelsHeight;
                    if (dm.dmDisplayFrequency > 0) MonitorRefreshHz = dm.dmDisplayFrequency;
                    MonitorMaxRefreshHz = MonitorRefreshHz;
                }
                int mode = 0;
                while (EnumDisplaySettings(null, mode++, ref dm))
                {
                    if (dm.dmPelsWidth == MonitorWidth && dm.dmPelsHeight == MonitorHeight && dm.dmDisplayFrequency > MonitorMaxRefreshHz)
                        MonitorMaxRefreshHz = dm.dmDisplayFrequency;
                }
            }
            catch { }

            TotalRamGb = Ram.State().TotalGb;

            // Inspect video controllers from registry Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}
            try
            {
                using (var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                {
                    if (cls != null)
                    {
                        foreach (string sub in cls.GetSubKeyNames())
                        {
                            if (!Regex.IsMatch(sub, @"^\d{4}$")) continue;
                            using (var k = cls.OpenSubKey(sub))
                            {
                                if (k == null) continue;
                                string desc = (k.GetValue("DriverDesc") ?? "").ToString().Trim();
                                if (desc.Length == 0) continue;
                                string drv = (k.GetValue("DriverVersion") ?? "").ToString().Trim();
                                if (desc.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    HasNvidiaGpu = true;
                                    HasDiscreteGpu = true;
                                    GpuName = desc;
                                    GpuDriver = drv;
                                }
                                else if (desc.IndexOf("AMD", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                         desc.IndexOf("Radeon", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    HasAmdGpu = true;
                                    if (desc.IndexOf("RX ", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                        desc.IndexOf("XT", StringComparison.OrdinalIgnoreCase) >= 0)
                                        HasDiscreteGpu = true;
                                    if (GpuName.Length == 0 || HasDiscreteGpu) { GpuName = desc; GpuDriver = drv; }
                                }
                                else if (desc.IndexOf("Intel", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    HasIntelGpu = true;
                                    if (desc.IndexOf("Arc", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                        desc.IndexOf("Graphics", StringComparison.OrdinalIgnoreCase) < 0)
                                        HasDiscreteGpu = true;
                                    if (GpuName.Length == 0) { GpuName = desc; GpuDriver = drv; }
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            IsIntegratedOnly = !HasDiscreteGpu;
        }

        // Full WMI probe for detailed hardware diagnostics (CPU core count, RAM sticks, physical disks).
        static bool wmiProbed;
        public static void ProbeWmi()
        {
            Ensure();
            if (wmiProbed) return;
            wmiProbed = true;
            try
            {
                foreach (ManagementObject m in new ManagementObjectSearcher("SELECT NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor").Get())
                {
                    PhysicalCores = Convert.ToInt32(m["NumberOfCores"]);
                    LogicalCores = Convert.ToInt32(m["NumberOfLogicalProcessors"]);
                    break;
                }
            }
            catch { }

            try
            {
                int sticks = 0, speed = 0;
                foreach (ManagementObject m in new ManagementObjectSearcher("SELECT Capacity, ConfiguredClockSpeed FROM Win32_PhysicalMemory").Get())
                {
                    sticks++;
                    int sp = Convert.ToInt32(m["ConfiguredClockSpeed"]);
                    if (sp > speed) speed = sp;
                }
                RamSticks = sticks;
                RamSpeedMts = speed;
                IsDualChannel = sticks >= 2;
            }
            catch { }

            try
            {
                var sc = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
                sc.Connect();
                var q = new ObjectQuery("SELECT FriendlyName, MediaType, BusType FROM MSFT_PhysicalDisk");
                foreach (ManagementObject d in new ManagementObjectSearcher(sc, q).Get())
                {
                    DriveModel = (d["FriendlyName"] ?? "").ToString().Trim();
                    int media = d["MediaType"] != null ? Convert.ToInt32(d["MediaType"]) : 0; // 3=HDD, 4=SSD
                    int bus = d["BusType"] != null ? Convert.ToInt32(d["BusType"]) : 0;       // 17=NVMe
                    if (bus == 17) { DriveType = "NVMe SSD"; IsSsd = true; }
                    else if (media == 3) { DriveType = "HDD"; IsSsd = false; }
                    else { DriveType = "SSD"; IsSsd = true; }
                    break;
                }
            }
            catch { }
        }
    }

    // ---------------------------------------------------------------- tweak descriptor
    class Tweak
    {
        public string Group, Title, Detail;
        public string Impact = "Medium impact";
        public string HardwareNote = "";
        public bool Default = true;
        public Action<Action<string>> Apply;
        public Action<Action<string>> Revert;
        public Func<TweakState> Check;
        public TweakState State = TweakState.Unknown;
        public bool Slow;
        public bool Risky;
        public bool RequiresRestart;
        public bool RequiresLogoff;
        public bool PendingRestart;
        public bool RunFirst;

        public void RefreshState()
        {
            if (Check == null) { State = TweakState.Unknown; return; }
            try { State = Check(); }
            catch { State = TweakState.Unknown; }
        }
    }

    // ---------------------------------------------------------------- UI theme & DPI
    static class Ui
    {
        public static readonly Color Bg      = Color.FromArgb(15, 16, 20);
        public static readonly Color Side    = Color.FromArgb(19, 21, 26);
        public static readonly Color Panel   = Color.FromArgb(24, 26, 32);
        public static readonly Color Card    = Color.FromArgb(24, 26, 32);
        public static readonly Color PanelHi = Color.FromArgb(32, 35, 43);
        public static readonly Color Line    = Color.FromArgb(38, 41, 50);
        public static readonly Color Text    = Color.FromArgb(244, 245, 247);
        public static readonly Color Dim     = Color.FromArgb(136, 141, 153);
        public static readonly Color Subtle  = Color.FromArgb(108, 114, 126);
        public static readonly Color Accent  = Color.FromArgb(88, 154, 255);
        public static readonly Color Green   = Color.FromArgb(62, 196, 134);
        public static readonly Color Amber   = Color.FromArgb(238, 180, 92);
        public static readonly Color Violet  = Color.FromArgb(156, 134, 248);

        public static readonly string Disp = Face("Segoe UI Variable Display", "Segoe UI Semibold");
        public static readonly string Body = Face("Segoe UI Variable Text", "Segoe UI");
        public static readonly string Tiny = Face("Segoe UI Variable Small", "Segoe UI");

        static string Face(string want, string fallback)
        {
            using (var f = new Font(want, 12f))
                return string.Equals(f.Name, want, StringComparison.OrdinalIgnoreCase) ? want : fallback;
        }

        public static readonly string Icons = Pick();
        static string Pick()
        {
            foreach (string n in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
                using (var f = new Font(n, 12f))
                    if (string.Equals(f.Name, n, StringComparison.OrdinalIgnoreCase)) return n;
            return null;
        }

        public static int Dpi = 96;
        public static float S = 1f;

        public static void UseDpi(int dpi)
        {
            if (dpi < 96) dpi = 96;
            if (dpi > 480) dpi = 480;
            Dpi = dpi;
            S = dpi / 96f;
        }

        public static int D(int px)
        {
            if (S == 1f) return px;
            return (int)Math.Round(px * S, MidpointRounding.AwayFromZero);
        }

        [DllImport("user32.dll")]
        static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
        [DllImport("shcore.dll")]
        static extern int SetProcessDpiAwareness(int value);
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        static extern int GetDpiForSystem();
        [DllImport("user32.dll")]
        static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")]
        static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")]
        static extern int GetDeviceCaps(IntPtr dc, int index);

        public static void DeclareDpiAware()
        {
            int mode = Cfg.DpiMode;
            if (mode <= 0) return;

            if (mode >= 2)
            {
                try { if (SetProcessDpiAwarenessContext(new IntPtr(-4))) { Detect(); return; } }
                catch { }
                try { if (SetProcessDpiAwareness(2) == 0) { Detect(); return; } }
                catch { }
            }
            try { SetProcessDPIAware(); }
            catch { }
            Detect();
        }

        static void Detect()
        {
            try { UseDpi(GetDpiForSystem()); return; }
            catch { }
            try
            {
                IntPtr dc = GetDC(IntPtr.Zero);
                int d = GetDeviceCaps(dc, 88); // LOGPIXELSX
                ReleaseDC(IntPtr.Zero, dc);
                if (d > 0) UseDpi(d);
            }
            catch { }
        }

        public static GraphicsPath Round(Rectangle r, int rad)
        {
            var p = new GraphicsPath();
            int d = rad * 2;
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr h, string app, string id);

        public static void DarkScroll(Control c)
        {
            try { if (c.IsHandleCreated) SetWindowTheme(c.Handle, "DarkMode_Explorer", null); }
            catch { }
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        public static void DarkHeader(ListView lv)
        {
            try
            {
                if (!lv.IsHandleCreated) return;
                IntPtr h = SendMessage(lv.Handle, 0x101F, IntPtr.Zero, IntPtr.Zero); // LVM_GETHEADER
                if (h != IntPtr.Zero) SetWindowTheme(h, "DarkMode_ItemsView", null);
            }
            catch { }
        }

        public static void DarkTitle(Form f)
        {
            int on = 1;
            try { if (DwmSetWindowAttribute(f.Handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(f.Handle, 19, ref on, 4); }
            catch { }
        }

        public static Label Section(string s)
        {
            return new Label
            {
                Text = s,
                Font = new Font(Body, 9.25f, FontStyle.Bold),
                ForeColor = Text,
                AutoSize = true,
                BackColor = Color.Transparent
            };
        }
    }

    // Minimal typographic card for power/mode selection
    class Card : Control
    {
        public string Glyph = "", Title = "", Sub = "";
        public Color Tone = Ui.Accent;
        public bool Active = false;
        bool hot, down;

        public Card()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                   | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            TabStop = false;
        }

        protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hot = down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { hot = down = false; Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using (var bg = new SolidBrush(Ui.Panel)) g.FillRectangle(bg, ClientRectangle);

            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            Color fill = Active ? Color.FromArgb(30, 35, 46) : Color.FromArgb(20, 22, 28);
            if (!Enabled) fill = Color.FromArgb(18, 19, 24);
            else if (down) fill = Color.FromArgb(Math.Max(0, fill.R - 4), Math.Max(0, fill.G - 4), Math.Max(0, fill.B - 4));
            else if (hot) fill = Ui.PanelHi;

            using (var path = Ui.Round(r, Ui.D(10)))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                Color edge = Active ? Color.FromArgb(200, Tone) : (hot && Enabled ? Color.FromArgb(110, Tone) : Ui.Line);
                using (var pen = new Pen(edge, Active ? 1.5f : 1f)) g.DrawPath(pen, path);
            }

            if (Active)
            {
                int dot = Ui.D(7);
                using (var b = new SolidBrush(Tone))
                    g.FillEllipse(b, Width - Ui.D(18), Ui.D(12), dot, dot);
            }

            Color titleCol = Enabled ? Ui.Text : Color.FromArgb(100, 104, 114);
            Color subCol = Enabled ? Ui.Dim : Color.FromArgb(80, 84, 92);
            int tx = Ui.D(16);
            int availW = Math.Max(Ui.D(40), Width - tx - Ui.D(24));

            using (var ft = new Font(Ui.Body, 10f, FontStyle.Bold))
            using (var fs = new Font(Ui.Tiny, 8.5f))
            {
                int th = TextRenderer.MeasureText("Ag", ft).Height;
                int sh = TextRenderer.MeasureText("Ag", fs).Height;
                int top = Math.Max(Ui.D(8), (Height - th - sh - Ui.D(3)) / 2);
                var flags = TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;
                TextRenderer.DrawText(g, Title, ft, new Rectangle(tx, top, availW, th + Ui.D(2)), titleCol, flags);
                if (Sub.Length > 0)
                    TextRenderer.DrawText(g, Sub, fs, new Rectangle(tx, top + th + Ui.D(3), availW, sh + Ui.D(2)), subCol, flags);
            }
        }
    }

    // Clean, responsive optimization item row with overflow-safe badges
    class CheckRow : Control
    {
        public Tweak Item;
        public bool Checked;
        bool hot;

        public CheckRow()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                   | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnClick(EventArgs e) { Checked = !Checked; Invalidate(); base.OnClick(e); }

        public static int TextLeft { get { return Ui.D(44); } }
        public static readonly Font FTitle = new Font(Ui.Body, 9.75f, FontStyle.Bold);
        public static readonly Font FSub = new Font(Ui.Tiny, 8.75f);
        public static readonly Font FPill = new Font(Ui.Tiny, 8f);

        public int Measure(int w)
        {
            int tw = Math.Max(Ui.D(120), w - TextLeft - Ui.D(20));
            var fl = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding;
            int h1 = TextRenderer.MeasureText(Item.Title, FTitle, new Size(tw, int.MaxValue), fl).Height;
            int h2 = TextRenderer.MeasureText(Item.Detail, FSub, new Size(tw, int.MaxValue), fl).Height;
            return h1 + h2 + Ui.D(48);
        }

        static int DrawPill(Graphics g, int x, int y, int maxRight, string text, Color tone)
        {
            var sz = TextRenderer.MeasureText(text, FPill, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
            int pw = sz.Width + Ui.D(12);
            if (x + pw > maxRight) return x;
            var pill = new Rectangle(x, y, pw, sz.Height + Ui.D(4));
            using (var path = Ui.Round(pill, Ui.D(5)))
            using (var b = new SolidBrush(Color.FromArgb(32, tone)))
                g.FillPath(b, path);
            TextRenderer.DrawText(g, text, FPill,
                new Rectangle(pill.X + Ui.D(6), pill.Y + Ui.D(2), sz.Width + Ui.D(2), sz.Height + Ui.D(2)),
                tone, TextFormatFlags.NoPadding);
            return pill.Right + Ui.D(6);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using (var b = new SolidBrush(hot ? Color.FromArgb(24, 26, 33) : Ui.Bg))
                g.FillRectangle(b, ClientRectangle);

            var box = new Rectangle(Ui.D(14), Ui.D(13), Ui.D(18), Ui.D(18));
            using (var path = Ui.Round(box, Ui.D(5)))
            {
                if (Checked)
                {
                    using (var b = new SolidBrush(Ui.Accent)) g.FillPath(b, path);
                    using (var pen = new Pen(Color.FromArgb(16, 18, 22), 2f))
                        g.DrawLines(pen, new[]
                        {
                            new Point(box.X + Ui.D(4), box.Y + Ui.D(9)),
                            new Point(box.X + Ui.D(7), box.Y + Ui.D(12)),
                            new Point(box.X + Ui.D(13), box.Y + Ui.D(5))
                        });
                }
                else using (var pen = new Pen(hot ? Ui.Dim : Ui.Line, 1.3f)) g.DrawPath(pen, path);
            }

            int tw = Math.Max(Ui.D(120), Width - TextLeft - Ui.D(20));
            var flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding;
            int h1 = TextRenderer.MeasureText(Item.Title, FTitle, new Size(tw, int.MaxValue), flags).Height;
            TextRenderer.DrawText(g, Item.Title, FTitle, new Rectangle(TextLeft, Ui.D(10), tw, h1 + Ui.D(2)), Ui.Text, flags);

            int y = Ui.D(10) + h1 + Ui.D(4);
            int px = TextLeft;
            int maxR = Width - Ui.D(14);

            if (Item.PendingRestart)
                px = DrawPill(g, px, y, maxR, "Restart required", Ui.Amber);
            else if (Item.State == TweakState.Applied)
                px = DrawPill(g, px, y, maxR, "Applied", Ui.Green);
            else if (Item.State == TweakState.Unsupported || Item.State == TweakState.Incompatible)
                px = DrawPill(g, px, y, maxR, "Unsupported", Ui.Dim);

            if (Item.Default && !Item.Risky)
                px = DrawPill(g, px, y, maxR, "Recommended", Ui.Accent);
            if (Item.RequiresRestart && !Item.PendingRestart)
                px = DrawPill(g, px, y, maxR, "Restart needed", Ui.Dim);
            if (Item.Risky)
                px = DrawPill(g, px, y, maxR, "Advanced / Trade-off", Ui.Amber);

            y += Ui.D(22);

            int h2 = TextRenderer.MeasureText(Item.Detail, FSub, new Size(tw, int.MaxValue), flags).Height;
            TextRenderer.DrawText(g, Item.Detail, FSub, new Rectangle(TextLeft, y, tw, h2 + Ui.D(2)), Ui.Dim, flags);
        }
    }

    // ---------------------------------------------------------------- entry point
    static class Program
    {
        const string MutexName = @"Local\PCTune.Single";
        const string WakeName = @"Local\PCTune.Wake";

        [STAThread]
        static void Main(string[] args)
        {
            if (args != null && args.Length >= 3 &&
                string.Equals(args[0], "--elevated-helper", StringComparison.OrdinalIgnoreCase))
            {
                int parentPid;
                if (int.TryParse(args[1], out parentPid))
                    Sys.RunElevatedServer(parentPid, args[2]);
                return;
            }

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate (object s, ThreadExceptionEventArgs e)
            {
                if (e != null && e.Exception != null)
                    Sys.ToFile("UI EXCEPTION: " + e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e)
            {
                if (e != null && e.ExceptionObject != null)
                    Sys.ToFile("DOMAIN EXCEPTION: " + e.ExceptionObject);
            };

            Ui.DeclareDpiAware();
            Sys.TrimLog();
            HwInfo.Ensure();
            Cfg.EnsureStartupDefaults();

            bool isStartup = false;
            bool noElevate = false;
            if (args != null)
            {
                foreach (string a in args)
                {
                    if (string.Equals(a, "/tray", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(a, "/startup", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(a, "--startup", StringComparison.OrdinalIgnoreCase))
                        isStartup = true;
                    if (string.Equals(a, "--no-elevate", StringComparison.OrdinalIgnoreCase))
                        noElevate = true;
                }
            }

            bool first;
            using (var mtx = new Mutex(true, MutexName, out first))
            {
                if (!first)
                {
                    try
                    {
                        using (var ev = EventWaitHandle.OpenExisting(WakeName))
                            ev.Set();
                    }
                    catch { }
                    return;
                }

                // Do not block Windows login with a UAC dialog if launched via /startup without admin rights;
                // when launched interactively by the user, elevate if possible, or continue gracefully if declined.
                if (!isStartup && !noElevate && !Sys.IsAdmin)
                {
                    try
                    {
                        var psi = new ProcessStartInfo(Application.ExecutablePath);
                        psi.UseShellExecute = true;
                        psi.Verb = "runas";
                        psi.Arguments = "";
                        Process.Start(psi);
                        return;
                    }
                    catch { }
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                var ctx = new ApplicationContext();
                var shell = new Shell(isStartup);

                Action open = delegate
                {
                    shell.Show();
                    if (shell.WindowState == FormWindowState.Minimized)
                        shell.WindowState = FormWindowState.Normal;
                    shell.BringToFront();
                    shell.Activate();
                };

                Tray.Init(shell, open);
                shell.Closed2 += delegate { Tray.Kill(); Sys.CloseElevatedHelper(); ctx.ExitThread(); };

                bool made;
                var wake = new EventWaitHandle(
                    false, EventResetMode.AutoReset, WakeName, out made);
                var waiter = new Thread(delegate ()
                {
                    while (true)
                    {
                        wake.WaitOne();
                        try { shell.BeginInvoke(open); }
                        catch { break; }
                    }
                });
                waiter.IsBackground = true;
                waiter.Start();

                bool showMain = isStartup ? Cfg.OpenMainOnStartup : true;
                if (showMain) shell.Show();
                Application.Run(ctx);
                Tray.Kill();
                Sys.CloseElevatedHelper();
            }
        }
    }
}
