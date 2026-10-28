// Windows power schemes: enumeration, active scheme lookup, switching, and battery power trend.
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using System.Windows.Forms;

namespace PCTune
{
    class Scheme
    {
        public string Guid;
        public string Name;
        public override string ToString() { return Name; }
    }

    static class Power
    {
        // powercfg /list outputs localized text, but the GUID and name in parentheses
        // follow the same pattern across all Windows locales.
        static readonly Regex Row = new Regex(
            @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s*\(([^)]*)\)");

        static List<Scheme> cache;
        static DateTime cacheAt;

        public static void Forget() { cache = null; }

        public static List<Scheme> List()
        {
            if (cache != null && (DateTime.UtcNow - cacheAt).TotalMinutes < 10) return cache;

            var r = new List<Scheme>();
            try
            {
                foreach (Match m in Row.Matches(Sys.Pow("/list")))
                {
                    string g = m.Groups[1].Value.ToLowerInvariant();
                    bool dup = false;
                    foreach (var s in r) if (s.Guid == g) { dup = true; break; }
                    if (!dup) r.Add(new Scheme { Guid = g, Name = m.Groups[2].Value.Trim() });
                }
            }
            catch { }
            if (r.Count > 0) { cache = r; cacheAt = DateTime.UtcNow; }
            return r;
        }

        public static string ActiveGuid()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(
                           @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes"))
                {
                    object v = k == null ? null : k.GetValue("ActivePowerScheme");
                    return v == null ? "" : v.ToString().ToLowerInvariant();
                }
            }
            catch { return ""; }
        }

        public static string ActiveName()
        {
            string g = ActiveGuid();
            if (g.Length == 0) return "unknown";
            foreach (var s in List()) if (s.Guid == g) return s.Name;
            Forget();
            foreach (var s in List()) if (s.Guid == g) return s.Name;
            return g;
        }

        public static bool Activate(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return false;
            Sys.Pow("/setactive " + guid);
            return string.Equals(ActiveGuid(), guid.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase);
        }

        public static bool OnBattery()
        {
            try { return SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline; }
            catch { return false; }
        }

        public static int BatteryPercent()
        {
            try
            {
                float f = SystemInformation.PowerStatus.BatteryLifePercent;
                return f > 1f ? -1 : (int)Math.Round(f * 100);
            }
            catch { return -1; }
        }
    }

    // Estimates battery charge/discharge power (watts) from RemainingCapacity (mWh) delta over time.
    static class BatteryTrend
    {
        static readonly List<KeyValuePair<DateTime, double>> pts = new List<KeyValuePair<DateTime, double>>();
        static DateTime lastTry = DateTime.MinValue;
        static bool lastOnline, busy;
        static string text = "";

        public static void Poll()
        {
            if (busy || (DateTime.UtcNow - lastTry).TotalSeconds < 20) return;
            lastTry = DateTime.UtcNow;
            busy = true;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try { Read(); }
                catch { }
                busy = false;
            });
        }

        public static string Text { get { return text; } }

        static void Read()
        {
            double mwh = -1;
            bool online = false;
            using (var s = new System.Management.ManagementObjectSearcher(
                @"root\wmi", "SELECT RemainingCapacity, PowerOnline FROM BatteryStatus"))
            foreach (System.Management.ManagementObject o in s.Get())
            {
                mwh = Convert.ToDouble(o["RemainingCapacity"]);
                online = Convert.ToBoolean(o["PowerOnline"]);
                break;
            }
            if (mwh <= 0) { text = ""; return; }

            var now = DateTime.UtcNow;
            lock (pts)
            {
                if (pts.Count > 0 && online != lastOnline) pts.Clear();
                lastOnline = online;
                pts.Add(new KeyValuePair<DateTime, double>(now, mwh));
                while (pts.Count > 0 && (now - pts[0].Key).TotalMinutes > 15) pts.RemoveAt(0);

                var first = pts[0];
                double mins = (now - first.Key).TotalMinutes;
                if (mins < 3) { text = ""; return; }

                double w = (mwh - first.Value) / 1000.0 / (mins / 60.0);
                string span = " in " + (int)Math.Round(mins) + " min";
                if (Math.Abs(w) < 0.8) text = "battery holds" + span;
                else if (w < 0) text = string.Format("battery drains: −{0:0.0} W" + span, -w);
                else text = string.Format("battery charges: +{0:0.0} W" + span, w);
            }
        }
    }

    // Watches AC/DC transitions and switches the active Windows power scheme if AutoSwitch is enabled.
    class PowerWatch : IDisposable
    {
        readonly Timer poll;
        bool lastOnBattery;
        public event Action<bool> Changed;

        public PowerWatch()
        {
            lastOnBattery = Power.OnBattery();
            poll = new Timer { Interval = 2000 };
            poll.Tick += (s, e) => Check();
            poll.Start();
            Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerMode;
        }

        void OnPowerMode(object s, Microsoft.Win32.PowerModeChangedEventArgs e)
        {
            if (e.Mode == Microsoft.Win32.PowerModes.StatusChange) Check();
        }

        void Check()
        {
            bool now = Power.OnBattery();
            if (now == lastOnBattery) return;
            lastOnBattery = now;
            Apply(now);
            var h = Changed;
            if (h != null) h(now);
        }

        public static void Apply(bool onBattery)
        {
            if (!Cfg.AutoSwitch) return;
            string want = onBattery ? Cfg.SchemeDc : Cfg.SchemeAc;
            if (string.IsNullOrEmpty(want)) return;
            if (string.Equals(Power.ActiveGuid(), want, StringComparison.OrdinalIgnoreCase)) return;
            Power.Activate(want);
            Sys.ToFile("AUTO: power " + (onBattery ? "on battery" : "on AC") + ", scheme " + want);
        }

        public void Dispose()
        {
            try { Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerMode; }
            catch { }
            if (poll != null) { poll.Stop(); poll.Dispose(); }
        }
    }
}
