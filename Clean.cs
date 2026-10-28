// Safe disk cleanup: scans and removes temporary files (>24h old), downloaded update caches,
// crash dumps, and browser caches without touching the Recycle Bin, registry, or user sessions.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace PCTune
{
    class CleanTarget
    {
        public string Title = "";
        public string Hint = "";
        public bool Advise;
        public List<string> Roots = new List<string>();
        public string Mask = "*";
        public int MinAgeHours;
        public bool Recurse = true;
        public string NeedClosed = "";

        public long Bytes;
        public int Files;
        public string Problem = "";
    }

    class CleanResult
    {
        public long Bytes;
        public int Files;
        public int Failed;
    }

    static class Clean
    {
        static string Local { get { return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); } }
        static string Win { get { return Environment.GetFolderPath(Environment.SpecialFolder.Windows); } }
        static string ProgData { get { return Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData); } }

        public static List<CleanTarget> Build()
        {
            var r = new List<CleanTarget>();

            r.Add(new CleanTarget
            {
                Title = "User temporary files",
                Hint = "TEMP folder. Only files older than a day: newer ones may be in use by a running program",
                Advise = true,
                MinAgeHours = 24,
                Roots = { Path.GetTempPath() }
            });

            r.Add(new CleanTarget
            {
                Title = "Windows temporary files",
                Hint = "Windows\\Temp. Leftovers of installs and updates",
                Advise = true,
                MinAgeHours = 24,
                Roots = { Path.Combine(Win, "Temp") }
            });

            r.Add(new CleanTarget
            {
                Title = "Downloaded Windows updates",
                Hint = "Installed updates lie here without any need. If something is needed again, Windows downloads it anew",
                Advise = true,
                MinAgeHours = 24,
                Roots = { Path.Combine(Win, "SoftwareDistribution\\Download") }
            });

            r.Add(new CleanTarget
            {
                Title = "Error reports",
                Hint = "Crash report queue and dumps of crashed programs",
                Advise = true,
                Roots =
                {
                    Path.Combine(ProgData, "Microsoft\\Windows\\WER\\ReportQueue"),
                    Path.Combine(ProgData, "Microsoft\\Windows\\WER\\ReportArchive"),
                    Path.Combine(Local, "CrashDumps")
                }
            });

            r.Add(new CleanTarget
            {
                Title = "Explorer thumbnails",
                Hint = "Picture preview cache. Rebuilds itself, the first folder openings will be slightly slower",
                Advise = false,
                Mask = "thumbcache_*.db",
                Recurse = false,
                Roots = { Path.Combine(Local, "Microsoft\\Windows\\Explorer") }
            });

            r.Add(new CleanTarget
            {
                Title = "Maintenance logs",
                Hint = "Windows\\Logs: component installation and integrity check logs",
                Advise = true,
                MinAgeHours = 24,
                Roots = { Path.Combine(Win, "Logs\\CBS"), Path.Combine(Win, "Logs\\DISM") }
            });

            AddBrowser(r, "Brave", "brave", Path.Combine(Local, "BraveSoftware\\Brave-Browser\\User Data"));
            AddBrowser(r, "Microsoft Edge", "msedge", Path.Combine(Local, "Microsoft\\Edge\\User Data"));
            AddBrowser(r, "Google Chrome", "chrome", Path.Combine(Local, "Google\\Chrome\\User Data"));

            var live = new List<CleanTarget>();
            foreach (var t in r)
            {
                bool any = false;
                foreach (string p in t.Roots) if (Directory.Exists(p)) { any = true; break; }
                if (any) live.Add(t);
            }
            return live;
        }

        static void AddBrowser(List<CleanTarget> to, string name, string proc, string userData)
        {
            if (!Directory.Exists(userData)) return;
            var roots = new List<string>();
            try
            {
                foreach (string dir in Directory.GetDirectories(userData))
                {
                    string leaf = Path.GetFileName(dir);
                    if (!leaf.Equals("Default", StringComparison.OrdinalIgnoreCase) &&
                        !leaf.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (string sub in new[] { "Cache\\Cache_Data", "Code Cache", "GPUCache" })
                    {
                        string p = Path.Combine(dir, sub);
                        if (Directory.Exists(p)) roots.Add(p);
                    }
                }
            }
            catch { }
            if (roots.Count == 0) return;

            to.Add(new CleanTarget
            {
                Title = "Cache " + name,
                Hint = "Only pictures and page scripts. Cookies, history, bookmarks and passwords are not touched. The browser must be closed",
                Advise = false,
                NeedClosed = proc,
                Roots = roots
            });
        }

        public static void Scan(CleanTarget t)
        {
            t.Bytes = 0;
            t.Files = 0;
            t.Problem = "";
            if (t.NeedClosed.Length > 0 && Running(t.NeedClosed))
            {
                t.Problem = "program is running";
                return;
            }
            foreach (string root in t.Roots) Walk(root, t, null);
        }

        public static CleanResult Sweep(CleanTarget t, Action<string> log)
        {
            var res = new CleanResult();
            if (t.NeedClosed.Length > 0 && Running(t.NeedClosed))
            {
                if (log != null) log("skipped, program is running: " + t.Title);
                return res;
            }
            foreach (string root in t.Roots) Walk(root, t, res);
            if (log != null)
                log(string.Format("{0}: deleted {1} files, {2}", t.Title, res.Files, Size(res.Bytes)) +
                    (res.Failed > 0 ? ", busy and skipped " + res.Failed : ""));
            return res;
        }

        static void Walk(string dir, CleanTarget t, CleanResult res)
        {
            string[] files;
            try
            {
                if (!Directory.Exists(dir)) return;
                files = Directory.GetFiles(dir, t.Mask);
            }
            catch { return; }

            DateTime edge = DateTime.Now.AddHours(-t.MinAgeHours);
            foreach (string f in files)
            {
                try
                {
                    var fi = new FileInfo(f);
                    if (t.MinAgeHours > 0 && fi.LastWriteTime > edge) continue;
                    long len = fi.Length;
                    if (res == null)
                    {
                        t.Bytes += len;
                        t.Files++;
                        continue;
                    }
                    if ((fi.Attributes & FileAttributes.ReadOnly) != 0)
                        fi.Attributes = FileAttributes.Normal;
                    fi.Delete();
                    res.Bytes += len;
                    res.Files++;
                }
                catch { if (res != null) res.Failed++; }
            }

            if (!t.Recurse) return;
            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch { return; }
            foreach (string s in subs)
            {
                Walk(s, t, res);
                if (res == null) continue;
                try
                {
                    if (Directory.GetFiles(s).Length == 0 && Directory.GetDirectories(s).Length == 0)
                        Directory.Delete(s);
                }
                catch { }
            }
        }

        static bool Running(string proc)
        {
            try { return Process.GetProcessesByName(proc).Length > 0; }
            catch { return false; }
        }

        public static string Size(long b)
        {
            if (b >= 1024L * 1024 * 1024) return string.Format("{0:N1} GB", b / 1024.0 / 1024 / 1024);
            if (b >= 1024L * 1024) return string.Format("{0:N0} MB", b / 1024.0 / 1024);
            if (b >= 1024) return string.Format("{0:N0} KB", b / 1024.0);
            return b + " B";
        }
    }
}
