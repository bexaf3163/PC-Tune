// Windows startup manager across all 5 sources:
//   1. Registry Run keys (HKCU, HKLM, HKLM Wow6432Node) via StartupApproved binary flag
//   2. User & Common Startup folders via StartupApproved binary flag + .lnk target validation
//   3. Task Scheduler non-Microsoft tasks via schtasks /ENABLE and /DISABLE + target validation
//   4. Microsoft Store (UWP/Packaged) startup tasks via AppModel\SystemAppData State
//   5. Third-party automatic services via Auto vs Manual start mode
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Xml;
using Microsoft.Win32;

namespace PCTune
{
    enum StKind { RunUser, RunMachine, RunMachine32, Folder, Task, FolderCommon, Service, Packaged }

    class AuditCounts
    {
        public int StartupItems;
        public int BackgroundServices;
        public int OrphanedTasks;
        public int TotalBackups;

        public int TotalRecommended
        {
            get { return StartupItems + BackgroundServices + OrphanedTasks; }
        }
    }

    class StItem
    {
        public string Name = "";
        public string Id = "";
        public string Cmd = "";
        public StKind Kind;
        public bool Enabled;
        public string What = "";
        public bool Advise;
        public bool Locked;
        public StCategory Category = StCategory.Unknown;
        public bool IsOrphaned;
        public string MissingTarget = "";
        public int EstRamMb;

        public string CategoryLabel
        {
            get
            {
                if (IsOrphaned) return "Recommended to disable (Orphaned)";
                switch (Category)
                {
                    case StCategory.RecommendedDisable: return "Recommended to disable";
                    case StCategory.Optional:           return "Optional";
                    case StCategory.Keep:               return "Keep";
                    case StCategory.System:             return "System";
                    default:                            return "Unknown";
                }
            }
        }

        public string Where
        {
            get
            {
                switch (Kind)
                {
                    case StKind.RunUser: return "this user";
                    case StKind.RunMachine: return "all users";
                    case StKind.RunMachine32: return "all users, 32-bit";
                    case StKind.Folder: return "startup folder";
                    case StKind.FolderCommon: return "common startup folder";
                    case StKind.Service: return "service";
                    case StKind.Packaged: return "Store app";
                    default: return "scheduler";
                }
            }
        }
    }

    static class Startup
    {
        const string Approved =
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";
        const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Run32Path = @"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Run";
        const string Backup = @"Software\PCTune\StartBackup";
        const string AppData =
            @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";
        const string Repo =
            @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

        static string FolderBackupDir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"PCTune\Backup\StartupFolder");
            }
        }

        public static List<StItem> List()
        {
            var r = new List<StItem>();
            var ramByExe = MeasureRunningProcessRam();

            ReadRun(r, Registry.CurrentUser, RunPath, StKind.RunUser);
            ReadRun(r, Registry.LocalMachine, RunPath, StKind.RunMachine);
            ReadRun(r, Registry.LocalMachine, Run32Path, StKind.RunMachine32);
            ReadFolder(r, Environment.GetFolderPath(Environment.SpecialFolder.Startup), StKind.Folder);
            ReadFolder(r, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), StKind.FolderCommon);
            ReadTasks(r);
            ReadPackaged(r);
            ReadServices(r);

            foreach (var it in r)
            {
                PopulateOrphanAndRam(it, ramByExe);
                Advice.Fill(it);
            }

            r.Sort(delegate (StItem a, StItem b)
            {
                if (a.Advise != b.Advise) return a.Advise ? -1 : 1;
                if (a.Category != b.Category) return ((int)a.Category).CompareTo((int)b.Category);
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            return r;
        }

        public static AuditCounts GetAuditSummary()
        {
            var c = new AuditCounts();
            try
            {
                var list = List();
                foreach (var it in list)
                {
                    if (!it.Enabled || !it.Advise) continue;
                    if (it.IsOrphaned || it.Kind == StKind.Task)
                        c.OrphanedTasks++;
                    else if (it.Kind == StKind.Service)
                        c.BackgroundServices++;
                    else
                        c.StartupItems++;
                }
                c.TotalBackups = BackupCount();
            }
            catch { }
            return c;
        }

        static Dictionary<string, int> MeasureRunningProcessRam()
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        string nm = p.ProcessName;
                        int mb = (int)(p.PrivateMemorySize64 / (1024 * 1024));
                        int prev;
                        if (map.TryGetValue(nm, out prev)) map[nm] = prev + mb;
                        else map[nm] = mb;
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
            }
            catch { }
            return map;
        }

        static void PopulateOrphanAndRam(StItem it, Dictionary<string, int> ramByExe)
        {
            string exe = "";
            if (it.Kind == StKind.Folder || it.Kind == StKind.FolderCommon)
            {
                if (it.Cmd.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                    exe = ResolveShortcutTarget(it.Cmd);
                else
                    exe = it.Cmd;
            }
            else if (it.Kind != StKind.Packaged)
            {
                exe = ExeOf(it.Cmd);
            }

            if (exe.Length > 0 && (it.Kind == StKind.Task || it.Kind == StKind.Folder || it.Kind == StKind.FolderCommon || it.Kind == StKind.Service))
            {
                if (!File.Exists(exe) && !Directory.Exists(exe))
                {
                    it.IsOrphaned = true;
                    it.MissingTarget = exe;
                }
            }

            if (exe.Length > 0)
            {
                string stem = Path.GetFileNameWithoutExtension(exe);
                int mb;
                if (!string.IsNullOrEmpty(stem) && ramByExe.TryGetValue(stem, out mb))
                    it.EstRamMb = mb;
            }
        }

        static string ResolveShortcutTarget(string lnkPath)
        {
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return "";
                object shell = Activator.CreateInstance(t);
                object sc = t.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                if (sc == null) return "";
                object target = sc.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, sc, null);
                return target == null ? "" : target.ToString();
            }
            catch { return ""; }
        }

        static void ReadRun(List<StItem> to, RegistryKey hive, string path, StKind kind)
        {
            try
            {
                using (var k = hive.OpenSubKey(path))
                {
                    if (k == null) return;
                    foreach (string name in k.GetValueNames())
                    {
                        object v = k.GetValue(name);
                        if (v == null) continue;
                        to.Add(new StItem
                        {
                            Name = name,
                            Id = name,
                            Cmd = v.ToString(),
                            Kind = kind,
                            Enabled = IsEnabled(kind, name)
                        });
                    }
                }
            }
            catch { }
        }

        static void ReadFolder(List<StItem> to, string dir, StKind kind)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
                foreach (string f in Directory.GetFiles(dir))
                {
                    string nm = Path.GetFileName(f);
                    if (string.Equals(nm, "desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    to.Add(new StItem
                    {
                        Name = nm,
                        Id = nm,
                        Cmd = f,
                        Kind = kind,
                        Enabled = IsEnabled(kind, nm)
                    });
                }
            }
            catch { }
        }

        static string TasksDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Tasks"); }
        }

        static void ReadTasks(List<StItem> to)
        {
            try
            {
                string dir = TasksDir;
                if (!Directory.Exists(dir)) return;

                foreach (string f in Directory.GetFiles(dir))
                {
                    string name = Path.GetFileName(f);
                    bool on;
                    string cmd;
                    if (!ReadTaskXml(f, out on, out cmd)) continue;
                    to.Add(new StItem { Name = name, Id = name, Cmd = cmd, Kind = StKind.Task, Enabled = on });
                }

                foreach (string sub in Directory.GetDirectories(dir))
                {
                    if (string.Equals(Path.GetFileName(sub), "Microsoft", StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (string f in Directory.GetFiles(sub, "*", SearchOption.AllDirectories))
                    {
                        bool on;
                        string cmd;
                        if (!ReadTaskXml(f, out on, out cmd)) continue;
                        string exe = ExeOf(cmd);
                        if (exe.Length > 0 && File.Exists(exe) && IsMicrosoft(exe)) continue;
                        string rel = f.Substring(dir.Length).TrimStart('\\');
                        to.Add(new StItem
                        {
                            Name = rel,
                            Id = "\\" + rel,
                            Cmd = cmd,
                            Kind = StKind.Task,
                            Enabled = on
                        });
                    }
                }
            }
            catch { }
        }

        static bool ReadTaskXml(string file, out bool enabled, out string cmd)
        {
            enabled = true;
            cmd = "";
            try
            {
                var doc = new XmlDocument();
                doc.Load(file);
                var ns = new XmlNamespaceManager(doc.NameTable);
                ns.AddNamespace("t", "http://schemas.microsoft.com/windows/2004/02/mit/task");
                var en = doc.SelectSingleNode("//t:Settings/t:Enabled", ns);
                if (en != null) enabled = !string.Equals(en.InnerText, "false",
                    StringComparison.OrdinalIgnoreCase);
                var ex = doc.SelectSingleNode("//t:Actions/t:Exec/t:Command", ns);
                if (ex != null) cmd = ex.InnerText;
                return ex != null;
            }
            catch { return false; }
        }

        static void ReadPackaged(List<StItem> to)
        {
            try
            {
                using (var root = Registry.CurrentUser.OpenSubKey(AppData))
                {
                    if (root == null) return;
                    foreach (string pkg in root.GetSubKeyNames())
                    {
                        using (var pk = root.OpenSubKey(pkg))
                        {
                            if (pk == null) continue;
                            foreach (string task in pk.GetSubKeyNames())
                            {
                                using (var tk = pk.OpenSubKey(task))
                                {
                                    if (tk == null) continue;
                                    object st = tk.GetValue("State");
                                    if (!(st is int)) continue;
                                    int s = (int)st;
                                    to.Add(new StItem
                                    {
                                        Name = PackageName(pkg),
                                        Id = pkg + "\\" + task,
                                        Cmd = pkg,
                                        Kind = StKind.Packaged,
                                        Enabled = s == 2 || s == 4,
                                        Locked = s == 3 || s == 4
                                    });
                                }
                            }
                        }
                    }
                }
            }
            catch { }
        }

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        static extern int SHLoadIndirectString(string src, StringBuilder buf, int cch, IntPtr reserved);

        static string PackageName(string family)
        {
            int us = family.LastIndexOf('_');
            string name = us > 0 ? family.Substring(0, us) : family;
            string pub = us > 0 ? family.Substring(us + 1) : "";
            try
            {
                using (var repo = Registry.CurrentUser.OpenSubKey(Repo))
                {
                    if (repo != null)
                        foreach (string full in repo.GetSubKeyNames())
                        {
                            if (!full.StartsWith(name + "_", StringComparison.OrdinalIgnoreCase)) continue;
                            if (pub.Length > 0 && !full.EndsWith("_" + pub, StringComparison.OrdinalIgnoreCase)) continue;
                            using (var k = repo.OpenSubKey(full))
                            {
                                string dn = k == null ? null : k.GetValue("DisplayName") as string;
                                if (string.IsNullOrEmpty(dn)) continue;

                                string src = null;
                                if (dn.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                                {
                                    string res = dn.Substring(12);
                                    string uri = res.StartsWith("//") ? "ms-resource:" + res
                                        : "ms-resource://" + name + "/" + (res.IndexOf('/') >= 0 ? res : "Resources/" + res);
                                    src = "@{" + full + "?" + uri + "}";
                                }
                                else if (dn.StartsWith("@")) src = dn;
                                else return dn;

                                var sb = new StringBuilder(512);
                                if (SHLoadIndirectString(src, sb, sb.Capacity, IntPtr.Zero) == 0 && sb.Length > 0)
                                    return sb.ToString();
                            }
                        }
                }
            }
            catch { }
            return Readable(name);
        }

        static string Readable(string pkgName)
        {
            int dot = pkgName.LastIndexOf('.');
            string s = dot >= 0 ? pkgName.Substring(dot + 1) : pkgName;
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (i > 0 && char.IsUpper(s[i]) && char.IsLower(s[i - 1])) sb.Append(' ');
                sb.Append(s[i]);
            }
            return sb.ToString();
        }

        static void ReadServices(List<StItem> to)
        {
            var mine = BackedUp(StKind.Service);
            ServiceController[] all;
            try { all = ServiceController.GetServices(); }
            catch { return; }

            foreach (var sc in all)
            {
                try
                {
                    string id = sc.ServiceName;
                    bool auto = Svc.IsAuto(id);
                    if (!auto && !mine.Contains(id.ToLowerInvariant())) continue;

                    string exe = ServiceExe(id);
                    if (exe.Length == 0 || (File.Exists(exe) && IsMicrosoft(exe))) continue;

                    to.Add(new StItem
                    {
                        Name = sc.DisplayName,
                        Id = id,
                        Cmd = exe,
                        Kind = StKind.Service,
                        Enabled = auto,
                        Locked = Svc.IsProtected(id)
                    });
                }
                catch { }
                finally { sc.Dispose(); }
            }
        }

        static string ServiceExe(string id)
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + id))
                {
                    if (k == null) return "";
                    string p = k.GetValue("ImagePath") as string;
                    if (string.IsNullOrEmpty(p)) return "";
                    if (p.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
                        p = "%SystemRoot%\\" + p.Substring(12);
                    return ExeOf(p);
                }
            }
            catch { return ""; }
        }

        static string ExeOf(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return "";
            string s = Environment.ExpandEnvironmentVariables(cmd.Trim());
            if (s.StartsWith("\""))
            {
                int q = s.IndexOf('"', 1);
                return q > 1 ? s.Substring(1, q - 1) : s.Trim('"');
            }
            int e = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (e > 0) return s.Substring(0, e + 4);
            int sp = s.IndexOf(' ');
            return sp > 0 ? s.Substring(0, sp) : s;
        }

        static bool IsMicrosoft(string exe)
        {
            try
            {
                string co = FileVersionInfo.GetVersionInfo(exe).CompanyName;
                return co != null && co.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        static string ApprovedLeaf(StKind k)
        {
            switch (k)
            {
                case StKind.RunUser:
                case StKind.RunMachine: return "Run";
                case StKind.RunMachine32: return "Run32";
                default: return "StartupFolder";
            }
        }

        static RegistryKey Hive(StKind k)
        {
            return k == StKind.RunUser || k == StKind.Folder ? Registry.CurrentUser : Registry.LocalMachine;
        }

        static bool IsEnabled(StKind kind, string name)
        {
            try
            {
                using (var k = Hive(kind).OpenSubKey(Approved + "\\" + ApprovedLeaf(kind)))
                {
                    if (k == null) return true;
                    var b = k.GetValue(name) as byte[];
                    if (b == null || b.Length < 1) return true;
                    return (b[0] & 0x01) == 0 && (b[0] & 0x04) == 0;
                }
            }
            catch { return true; }
        }

        public static bool SetEnabled(StItem it, bool on)
        {
            if (it.Locked) return false;
            switch (it.Kind)
            {
                case StKind.Task: return SetTask(it, on);
                case StKind.Service: return SetService(it, on);
                case StKind.Packaged: return SetPackaged(it, on);
            }
            try
            {
                string rootPrefix = (it.Kind == StKind.RunUser || it.Kind == StKind.Folder) ? @"HKCU\" : @"HKLM\";
                string regPath = rootPrefix + Approved + "\\" + ApprovedLeaf(it.Kind);
                byte[] b = new byte[12];
                if (on)
                {
                    b[0] = 0x02;
                }
                else
                {
                    b[0] = 0x03;
                    long ft = DateTime.UtcNow.ToFileTimeUtc();
                    for (int i = 0; i < 8; i++) b[4 + i] = (byte)(ft >> (8 * i));
                }
                if (!Sys.SetRegVerify(regPath, it.Id, b, RegistryValueKind.Binary))
                    return false;
                if (IsEnabled(it.Kind, it.Id) != on) return false;
                if (on && it.Kind == StKind.RunUser && string.Equals(it.Id, "PC-Tune", StringComparison.OrdinalIgnoreCase))
                    Cfg.Autostart = true;
                Remember(it, on);
                it.Enabled = on;
                Sys.ToFile("STARTUP " + it.Name + " -> " + (on ? "on" : "off"));
                return true;
            }
            catch { return false; }
        }

        static bool SetTask(StItem it, bool on)
        {
            string tn = it.Id.StartsWith("\\") ? it.Id : "\\" + it.Id;
            Sys.RunAdmin("schtasks.exe", "/Change /TN \"" + tn + "\"" + (on ? " /ENABLE" : " /DISABLE"));

            bool now;
            string cmd;
            string file = Path.Combine(TasksDir, it.Id.TrimStart('\\'));
            if (!ReadTaskXml(file, out now, out cmd)) return false;
            if (now != on)
            {
                // Fallback for orphaned task XML files in System32\Tasks not registered in TaskCache
                SetTaskXmlEnabled(file, on);
                if (!ReadTaskXml(file, out now, out cmd) || now != on) return false;
            }

            Remember(it, on);
            it.Enabled = on;
            Sys.ToFile("TASK " + it.Id + " -> " + (on ? "enabled" : "disabled"));
            return true;
        }

        static void SetTaskXmlEnabled(string file, bool on)
        {
            try
            {
                var doc = new XmlDocument();
                doc.PreserveWhitespace = true;
                doc.Load(file);
                var ns = new XmlNamespaceManager(doc.NameTable);
                ns.AddNamespace("t", "http://schemas.microsoft.com/windows/2004/02/mit/task");
                var en = doc.SelectSingleNode("//t:Settings/t:Enabled", ns);
                if (en != null)
                {
                    en.InnerText = on ? "true" : "false";
                }
                else
                {
                    var settings = doc.SelectSingleNode("//t:Settings", ns);
                    if (settings != null)
                    {
                        var node = doc.CreateElement("Enabled", "http://schemas.microsoft.com/windows/2004/02/mit/task");
                        node.InnerText = on ? "true" : "false";
                        settings.AppendChild(node);
                    }
                }
                doc.Save(file);
            }
            catch { }
        }

        static bool SetService(StItem it, bool on)
        {
            string err = Svc.SetMode(it.Id, it.Enabled ? "Auto" : "Manual", on ? "Auto" : "Manual", false);
            if (err != null) return false;
            if (Svc.IsAuto(it.Id) != on) return false;

            Remember(it, on);
            it.Enabled = on;
            Sys.ToFile("STARTUP service " + it.Id + " -> " + (on ? "Auto" : "Manual"));
            return true;
        }

        static bool SetPackaged(StItem it, bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(AppData + "\\" + it.Id, true))
                {
                    if (k == null) return false;
                    object st = k.GetValue("State");
                    int s = st is int ? (int)st : 0;
                    if (s == 3 || s == 4) return false;
                    k.SetValue("State", on ? 2 : 1, RegistryValueKind.DWord);
                    object now = k.GetValue("State");
                    if (!(now is int) || (int)now != (on ? 2 : 1)) return false;
                }
                Remember(it, on);
                it.Enabled = on;
                Sys.ToFile("STARTUP app " + it.Id + " -> " + (on ? "enabled" : "disabled"));
                return true;
            }
            catch { return false; }
        }

        static void Remember(StItem it, bool newState)
        {
            try
            {
                string key = ((int)it.Kind) + "|" + it.Id;
                using (var k = Registry.CurrentUser.CreateSubKey(Backup))
                {
                    if (k == null) return;
                    if (k.GetValue(key) != null) return;
                    k.SetValue(key, newState ? 0 : 1, RegistryValueKind.DWord);
                }
            }
            catch { }
        }

        static HashSet<string> BackedUp(StKind kind)
        {
            var r = new HashSet<string>();
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(Backup))
                {
                    if (k == null) return r;
                    string pref = ((int)kind) + "|";
                    foreach (string key in k.GetValueNames())
                        if (key.StartsWith(pref)) r.Add(key.Substring(pref.Length).ToLowerInvariant());
                }
            }
            catch { }
            return r;
        }

        public static int BackupCount()
        {
            int count = 0;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(Backup))
                    if (k != null) count += k.GetValueNames().Length;
            }
            catch { }
            try
            {
                if (Directory.Exists(FolderBackupDir))
                    count += Directory.GetFiles(FolderBackupDir, "*.lnk").Length;
            }
            catch { }
            return count;
        }

        public static int RestoreAll(Action<string> log)
        {
            int n = 0;
            try
            {
                // Restore backed-up CommonStartup shortcuts first if any
                if (Directory.Exists(FolderBackupDir))
                {
                    string commonDir = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
                    foreach (string f in Directory.GetFiles(FolderBackupDir, "*.lnk"))
                    {
                        try
                        {
                            string nm = Path.GetFileName(f);
                            string dest = Path.Combine(commonDir, nm);
                            File.Copy(f, dest, true);
                            if (File.Exists(dest))
                            {
                                File.Delete(f);
                                n++;
                                if (log != null) log(nm + " -> restored to startup folder");
                            }
                        }
                        catch { }
                    }
                }

                var all = List();
                using (var k = Registry.CurrentUser.OpenSubKey(Backup, true))
                {
                    if (k != null)
                    {
                        foreach (string key in k.GetValueNames())
                        {
                            int bar = key.IndexOf('|');
                            if (bar < 1) { k.DeleteValue(key, false); continue; }
                            int kind;
                            if (!int.TryParse(key.Substring(0, bar), out kind)) { k.DeleteValue(key, false); continue; }
                            string id = key.Substring(bar + 1);
                            bool wasOn = Convert.ToInt32(k.GetValue(key, 1)) != 0;

                            bool found = false;
                            bool ok = false;
                            foreach (var it in all)
                                if ((int)it.Kind == kind &&
                                    string.Equals(it.Id, id, StringComparison.OrdinalIgnoreCase))
                                {
                                    found = true;
                                    bool lk = it.Locked;
                                    it.Locked = false;
                                    if (it.Enabled == wasOn)
                                    {
                                        ok = true;
                                    }
                                    else if (SetEnabled(it, wasOn))
                                    {
                                        ok = true;
                                        n++;
                                        if (log != null)
                                            log(it.Name + " -> " + (wasOn ? "on" : "off"));
                                    }
                                    it.Locked = lk;
                                    break;
                                }

                            // Also handle Microsoft tasks that are not in the non-Microsoft List()
                            if (!found && kind == (int)StKind.Task)
                            {
                                string tn = id.StartsWith("\\") ? id : "\\" + id;
                                Sys.RunAdmin("schtasks.exe", "/Change /TN \"" + tn + "\"" + (wasOn ? " /ENABLE" : " /DISABLE"));
                                bool now;
                                string cmd;
                                string file = Path.Combine(TasksDir, id.TrimStart('\\'));
                                if (!ReadTaskXml(file, out now, out cmd) || now != wasOn)
                                    SetTaskXmlEnabled(file, wasOn);
                                if (ReadTaskXml(file, out now, out cmd) && now == wasOn)
                                {
                                    found = true;
                                    ok = true;
                                    n++;
                                    if (log != null) log(id + " -> " + (wasOn ? "enabled" : "disabled"));
                                }
                            }

                            if (!found || ok)
                                k.DeleteValue(key, false);
                        }
                    }
                }
            }
            catch { }
            return n;
        }
    }
}
