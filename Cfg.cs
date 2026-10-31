// Application configuration stored in HKCU\Software\PCTune.
using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace PCTune
{
    static class Cfg
    {
        const string Key = @"Software\PCTune";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "PC-Tune";

        static object Get(string name, object def)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(Key))
                    return k == null ? def : (k.GetValue(name) ?? def);
            }
            catch { return def; }
        }

        static void Set(string name, object val, RegistryValueKind kind)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(Key))
                    k.SetValue(name, val, kind);
            }
            catch { }
        }

        static bool Flag(string name, bool def)
        {
            try { return Convert.ToInt32(Get(name, def ? 1 : 0)) != 0; }
            catch { return def; }
        }

        static void SetFlag(string name, bool v) { Set(name, v ? 1 : 0, RegistryValueKind.DWord); }

        static int Num(string name, int def)
        {
            try { return Convert.ToInt32(Get(name, def)); }
            catch { return def; }
        }

        static string Str(string name, string def)
        {
            object v = Get(name, def);
            return v == null ? def : v.ToString();
        }

        // ---------------------------------------------------------- power
        public static bool AutoSwitch
        {
            get { return Flag("AutoSwitch", false); }
            set { SetFlag("AutoSwitch", value); }
        }

        public static string SchemeAc
        {
            get { return Str("SchemeAc", ""); }
            set { Set("SchemeAc", value, RegistryValueKind.String); }
        }

        public static string SchemeDc
        {
            get { return Str("SchemeDc", ""); }
            set { Set("SchemeDc", value, RegistryValueKind.String); }
        }

        // Active preset id: perf, game, comp, bal, eco, car
        public static string Profile
        {
            get { return Str("Profile", ""); }
            set { Set("Profile", value ?? "", RegistryValueKind.String); }
        }

        public static string ProfileTitle
        {
            get
            {
                switch (Profile)
                {
                    case "perf": return "Max Speed";
                    case "game": return "Gaming";
                    case "comp": return "Competitive";
                    case "bal":  return "Balanced";
                    case "eco":  return "Economy";
                    case "car":  return "In the Car";
                    default:     return "Balanced";
                }
            }
        }

        public static bool PerfOnStart
        {
            get { return Flag("PerfOnStart", false); }
            set { SetFlag("PerfOnStart", value); }
        }

        // AC display timeout in minutes; 0 = Never turn off screen on AC.
        // Earlier builds defaulted to 30 and wrote 30 into HKCU\Software\PCTune on startup.
        // ScreenMinutesV2 migrates that legacy 30-minute trap to 0 (Never) once,
        // while still allowing the user to explicitly choose a non-zero timeout later.
        public static int ScreenMinutes
        {
            get
            {
                if (!Flag("ScreenMinutesV2", false))
                {
                    SetFlag("ScreenMinutesV2", true);
                    if (Num("ScreenMinutes", 0) == 30)
                        Set("ScreenMinutes", 0, RegistryValueKind.DWord);
                }
                int v = Num("ScreenMinutes", 0);
                return v < 0 ? 0 : v > 240 ? 240 : v;
            }
            set
            {
                SetFlag("ScreenMinutesV2", true);
                Set("ScreenMinutes", value < 0 ? 0 : value, RegistryValueKind.DWord);
            }
        }

        // ---------------------------------------------------------- game mode options
        // Safe by default: close OEM/peripheral/wallpaper utilities, keep user browsers & chat open unless ticked.
        public static bool GameCloseBrowsers
        {
            get { return Flag("GameCloseBrowsers", false); }
            set { SetFlag("GameCloseBrowsers", value); }
        }

        public static bool GameCloseChat
        {
            get { return Flag("GameCloseChat", false); }
            set { SetFlag("GameCloseChat", value); }
        }

        // ---------------------------------------------------------- memory
        public static bool RamAuto
        {
            get { return Flag("RamAuto", false); }
            set { SetFlag("RamAuto", value); }
        }

        public static int RamMinutes
        {
            get { int v = Num("RamMinutes", 30); return v < 5 ? 5 : v > 720 ? 720 : v; }
            set { Set("RamMinutes", value, RegistryValueKind.DWord); }
        }

        public static List<string> KillList
        {
            get
            {
                var r = new List<string>();
                foreach (string s in Str("KillList", "").Split(';'))
                    if (s.Trim().Length > 0) r.Add(s.Trim());
                return r;
            }
            set { Set("KillList", string.Join(";", value.ToArray()), RegistryValueKind.String); }
        }

        public static int DpiMode
        {
            get { return Num("DpiMode", 2); }
            set { Set("DpiMode", value, RegistryValueKind.DWord); }
        }

        // ---------------------------------------------------------- window, startup & widget
        public static bool TrayEnabled
        {
            get { return Flag("Tray", true); }
            set { SetFlag("Tray", value); }
        }

        // Hourly one-line ping to Claude Code and Codex (see AiPing.cs). Off until the user turns it on.
        public static bool AiPing
        {
            get { return Flag("AiPing", false); }
            set { SetFlag("AiPing", value); }
        }

        public static bool WidgetOn
        {
            get { return Flag("Widget", true); }
            set { SetFlag("Widget", value); }
        }

        public static bool ShowWidgetOnStartup
        {
            get { return Flag("ShowWidgetOnStartup", true); }
            set { SetFlag("ShowWidgetOnStartup", value); }
        }

        public static bool OpenMainOnStartup
        {
            get { return Flag("OpenMainOnStartup", false); }
            set { SetFlag("OpenMainOnStartup", value); }
        }

        public static bool WidgetCollapsed
        {
            get { return Flag("WidgetCollapsed", false); }
            set { SetFlag("WidgetCollapsed", value); }
        }

        public static int WidgetX
        {
            get { return Num("WidgetX", int.MinValue); }
            set { Set("WidgetX", value, RegistryValueKind.DWord); }
        }

        public static int WidgetY
        {
            get { return Num("WidgetY", int.MinValue); }
            set { Set("WidgetY", value, RegistryValueKind.DWord); }
        }

        public static System.Drawing.Point ClampWidgetLocation(int width, int height)
        {
            int x = WidgetX, y = WidgetY;
            var primary = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
            if (x == int.MinValue || y == int.MinValue || x == -1 || y == -1)
            {
                return new System.Drawing.Point(
                    primary.Right - width - Ui.D(20),
                    primary.Bottom - height - Ui.D(20));
            }

            var rect = new System.Drawing.Rectangle(x, y, width, height);
            foreach (var scr in System.Windows.Forms.Screen.AllScreens)
            {
                var wa = scr.WorkingArea;
                if (wa.IntersectsWith(rect))
                {
                    int cx = Math.Max(wa.Left + Ui.D(6), Math.Min(x, wa.Right - width - Ui.D(6)));
                    int cy = Math.Max(wa.Top + Ui.D(6), Math.Min(y, wa.Bottom - height - Ui.D(6)));
                    return new System.Drawing.Point(cx, cy);
                }
            }

            return new System.Drawing.Point(
                primary.Right - width - Ui.D(20),
                primary.Bottom - height - Ui.D(20));
        }

        public static bool CloseToTray
        {
            get { return Flag("CloseToTray", true); }
            set { SetFlag("CloseToTray", value); }
        }

        const string TrayKey = @"Control Panel\NotifyIconSettings";

        public static bool TrayPromoted
        {
            get { return Flag("TrayPromoted", true); }
            set { SetFlag("TrayPromoted", value); Promote(value); }
        }

        public static void ApplyPromoted() { Promote(TrayPromoted); }

        static string ExePath()
        {
            string loc = typeof(Cfg).Assembly.Location;
            return !string.IsNullOrEmpty(loc) ? loc : System.Windows.Forms.Application.ExecutablePath;
        }

        static void Promote(bool on)
        {
            try
            {
                string exe = ExePath();
                using (var root = Registry.CurrentUser.OpenSubKey(TrayKey, true))
                {
                    if (root == null) return;
                    foreach (string name in root.GetSubKeyNames())
                    {
                        using (var k = root.OpenSubKey(name, true))
                        {
                            if (k == null) continue;
                            object path = k.GetValue("ExecutablePath");
                            if (path == null) continue;
                            if (!string.Equals(path.ToString(), exe, StringComparison.OrdinalIgnoreCase)) continue;
                            if (on) k.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                            else k.DeleteValue("IsPromoted", false);
                        }
                    }
                }
            }
            catch { }
        }

        const string ApprovedRunKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

        static string ExpectedRunCommand()
        {
            return "\"" + ExePath() + "\" /startup";
        }

        static bool IsApprovedEnabled()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(ApprovedRunKey))
                {
                    if (k == null) return true;
                    byte[] b = k.GetValue(RunName) as byte[];
                    if (b == null || b.Length == 0) return true;
                    return (b[0] & 0x01) == 0 && (b[0] & 0x04) == 0;
                }
            }
            catch { return true; }
        }

        // Initializes default startup/widget preferences once and heals moved portable .exe paths.
        public static void EnsureStartupDefaults()
        {
            try
            {
                if (!Flag("StartupInitV3", false))
                {
                    SetFlag("StartupInitV3", true);
                    SetFlag("AutostartPref", true);
                    SetFlag("Widget", true);
                    SetFlag("ShowWidgetOnStartup", true);
                    SetFlag("OpenMainOnStartup", false);
                    SetFlag("CloseToTray", true);
                    Autostart = true;
                    return;
                }

                if (!IsApprovedEnabled())
                {
                    SetFlag("AutostartPref", false);
                    return;
                }

                // If user has autostart enabled (or existing Run key), heal stale portable path if moved.
                bool pref = Flag("AutostartPref", true);
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k != null)
                    {
                        object cur = k.GetValue(RunName);
                        if (cur != null || pref)
                        {
                            string want = ExpectedRunCommand();
                            if (cur == null || !string.Equals(cur.ToString(), want, StringComparison.OrdinalIgnoreCase))
                                k.SetValue(RunName, want, RegistryValueKind.String);
                        }
                    }
                }
            }
            catch { }
        }

        public static bool Autostart
        {
            get
            {
                try
                {
                    if (!IsApprovedEnabled()) return false;
                    using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    {
                        if (k == null) return false;
                        object v = k.GetValue(RunName);
                        if (v == null) return false;
                        string raw = v.ToString().Trim();
                        return raw.Length > 0;
                    }
                }
                catch { return false; }
            }
            set
            {
                SetFlag("AutostartPref", value);
                try
                {
                    using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                    {
                        if (k == null) return;
                        if (value)
                        {
                            k.SetValue(RunName, ExpectedRunCommand(), RegistryValueKind.String);
                            using (var ak = Registry.CurrentUser.OpenSubKey(ApprovedRunKey, true))
                            {
                                if (ak != null && ak.GetValue(RunName) != null)
                                {
                                    byte[] b = new byte[12];
                                    b[0] = 0x02;
                                    ak.SetValue(RunName, b, RegistryValueKind.Binary);
                                }
                            }
                        }
                        else
                        {
                            k.DeleteValue(RunName, false);
                            using (var ak = Registry.CurrentUser.OpenSubKey(ApprovedRunKey, true))
                            {
                                if (ak != null) ak.DeleteValue(RunName, false);
                            }
                        }
                    }
                }
                catch { }
            }
        }
    }
}
