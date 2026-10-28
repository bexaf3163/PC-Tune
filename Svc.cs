// Windows services enumeration, protection list, start-mode switching, and backup/restore.
using System;
using System.Collections.Generic;
using System.ServiceProcess;
using Microsoft.Win32;

namespace PCTune
{
    class SvcInfo
    {
        public string Name, Display, StartMode, State;
        public bool Protected;
    }

    static class Svc
    {
        const string BakKey = @"Software\PCTune\SvcBackup";

        // Security, Windows Update, cryptography, core kernel/session, network, audio,
        // and essential laptop thermal/biometric services that must never be disabled.
        static readonly string[] Locked =
        {
            // Security & Defender
            "windefend", "wdnissvc", "wdfilter", "wdboot", "sense", "securityhealthservice",
            "mdcoresvc", "mpssvc", "bfe", "webthreatdefsvc", "wscsvc", "sgrmbroker",
            // Updates & installer
            "wuauserv", "usosvc", "waasmedicsvc", "bits", "dosvc", "trustedinstaller",
            "msiserver", "cryptsvc", "keyiso",
            // Core OS, boot, session
            "dcomlaunch", "rpcss", "rpceptmapper", "lsm", "samss", "power", "plugplay",
            "profsvc", "usermanager", "systemeventsbroker", "brokerinfrastructure",
            "coremessagingregistrar", "staterepository", "schedule", "eventlog",
            "eventsystem", "sens", "winmgmt", "gpsvc", "deviceinstall", "devicesetupmanager",
            "themes", "shellhwdetection", "seclogon", "termservice", "usosvc",
            // Networking & audio
            "dhcp", "dnscache", "nlasvc", "netprofm", "nsi", "netman", "wlansvc",
            "audiosrv", "audioendpointbuilder", "camsvc", "timebrokersvc",
            // Laptop hardware (Intel DPTF thermal, fingerprint vault, HDCP, WWAN firmware)
            "esifsvc", "hoststoragesvc", "cplspcon", "firmwareswitchservice",
            "jhi_service", "wmiregistrationservice"
        };

        public static bool IsProtected(string name)
        {
            string n = name.ToLowerInvariant();
            foreach (string s in Locked) if (n == s) return true;
            return false;
        }

        public static List<SvcInfo> List()
        {
            var r = new List<SvcInfo>();
            try
            {
                foreach (System.Management.ManagementObject m in
                         new System.Management.ManagementObjectSearcher(
                             "SELECT Name, DisplayName, StartMode, State FROM Win32_Service").Get())
                {
                    string name = "" + m["Name"];
                    r.Add(new SvcInfo
                    {
                        Name = name,
                        Display = "" + m["DisplayName"],
                        StartMode = "" + m["StartMode"],
                        State = "" + m["State"],
                        Protected = IsProtected(name)
                    });
                }
            }
            catch { }
            r.Sort(delegate (SvcInfo a, SvcInfo b)
            {
                int ra = a.State == "Running" ? 0 : 1, rb = b.State == "Running" ? 0 : 1;
                if (ra != rb) return ra - rb;
                return string.Compare(a.Display, b.Display, StringComparison.CurrentCultureIgnoreCase);
            });
            return r;
        }

        public static string ScWord(string mode)
        {
            switch (mode)
            {
                case "Auto": return "auto";
                case "Manual": return "demand";
                case "Disabled": return "disabled";
                default: return null;
            }
        }

        public static string Human(string mode)
        {
            switch (mode)
            {
                case "Auto": return "Automatic";
                case "Manual": return "Manual";
                case "Disabled": return "Disabled";
                case "Boot":
                case "System": return "System";
                default: return mode;
            }
        }

        static void Remember(string name, string wasMode)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(BakKey))
                    if (k.GetValue(name) == null) k.SetValue(name, wasMode, RegistryValueKind.String);
            }
            catch { }
        }

        static int ModeToStartDword(string mode)
        {
            switch (mode)
            {
                case "Auto": return 2;
                case "Manual": return 3;
                case "Disabled": return 4;
                default: return -1;
            }
        }

        public static bool IsAuto(string name)
        {
            return ModeNow(name) == "Auto";
        }

        public static string SetMode(string name, string wasMode, string newMode, bool stop = true)
        {
            if (IsProtected(name)) return "service is protected and not changed";
            string w = ScWord(newMode);
            if (w == null) return "mode \"" + newMode + "\" cannot be changed";

            string o = Sys.RunAdmin("sc.exe", "config \"" + name + "\" start= " + w);

            if (ModeNow(name) != newMode)
            {
                int dword = ModeToStartDword(newMode);
                if (dword >= 2)
                    Sys.SetRegVerify(@"HKLM\SYSTEM\CurrentControlSet\Services\" + name, "Start", dword, RegistryValueKind.DWord);
            }

            if (ModeNow(name) != newMode)
                return o.Trim().Length > 0 ? o.Trim() : "mode did not change (administrator privileges required)";

            Remember(name, wasMode);

            if (stop && (newMode == "Disabled" || newMode == "Manual"))
                Sys.StopService(name, 8);
            Sys.ToFile("SERVICE: " + name + " " + wasMode + " -> " + newMode);
            return null;
        }

        static string ModeNow(string name)
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name))
                {
                    if (k != null)
                    {
                        object v = k.GetValue("Start");
                        if (v != null)
                        {
                            int st = Convert.ToInt32(v);
                            if (st == 2) return "Auto";
                            if (st == 3) return "Manual";
                            if (st == 4) return "Disabled";
                        }
                    }
                }
            }
            catch { }
            try
            {
                using (var sc = new ServiceController(name))
                {
                    switch (sc.StartType)
                    {
                        case ServiceStartMode.Automatic: return "Auto";
                        case ServiceStartMode.Manual: return "Manual";
                        case ServiceStartMode.Disabled: return "Disabled";
                        default: return "" + sc.StartType;
                    }
                }
            }
            catch { return ""; }
        }

        public static int BackupCount()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(BakKey))
                    return k == null ? 0 : k.GetValueNames().Length;
            }
            catch { return 0; }
        }

        public static int RestoreAll(Action<string> log)
        {
            int n = 0;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(BakKey, true))
                {
                    if (k == null) return 0;
                    foreach (string name in k.GetValueNames())
                    {
                        string mode = "" + k.GetValue(name);
                        string w = ScWord(mode);
                        if (w == null) { k.DeleteValue(name, false); continue; }
                        string cur = ModeNow(name);
                        if (cur.Length == 0) { k.DeleteValue(name, false); continue; }
                        if (cur != mode)
                        {
                            Sys.RunAdmin("sc.exe", "config \"" + name + "\" start= " + w);
                            if (ModeNow(name) != mode)
                            {
                                int dword = ModeToStartDword(mode);
                                if (dword >= 2)
                                    Sys.SetRegVerify(@"HKLM\SYSTEM\CurrentControlSet\Services\" + name, "Start", dword, RegistryValueKind.DWord);
                            }
                        }
                        if (ModeNow(name) == mode)
                        {
                            if (log != null) log(name + " -> " + Human(mode));
                            if (cur != mode) n++;
                            k.DeleteValue(name, false);
                        }
                    }
                }
            }
            catch { }
            if (n > 0) Sys.ToFile("SERVICES: restored to original: " + n);
            return n;
        }
    }
}
