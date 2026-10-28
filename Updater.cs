// Self-update from GitHub releases. A newer PC-Tune.exe is downloaded next to the running one,
// verified against GitHub's SHA-256 asset digest and the release's SHA256SUMS.txt, then swapped in
// and restarted - only while no job and no Game Mode session is running.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Web.Script.Serialization;

namespace PCTune
{
    static class Updater
    {
        const string Api = "https://api.github.com/repos/bexaf3163/PC-Tune/releases/latest";
        static readonly TimeSpan Every = TimeSpan.FromHours(6);

        static string Exe { get { return Process.GetCurrentProcess().MainModule.FileName; } }
        static string Staged { get { return Path.Combine(Path.GetDirectoryName(Exe), "PC-Tune.update.exe"); } }
        static string Old { get { return Path.Combine(Path.GetDirectoryName(Exe), "PC-Tune.old.exe"); } }

        // busy: true while an update must wait. restart: exits this instance after the new one started.
        public static void Start(Func<bool> busy, Action restart)
        {
            try { File.Delete(Old); } catch { }
            if (AppInfo.Version == "dev") return;   // local builds never replace themselves
            var t = new Thread(delegate ()
            {
                Thread.Sleep(TimeSpan.FromSeconds(30));
                while (true)
                {
                    try { if (Check() && WaitIdle(busy) && Swap()) { restart(); return; } }
                    catch (Exception ex) { Sys.ToFile("UPDATE check failed: " + ex.Message); }
                    Thread.Sleep(Every);
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        // true when a verified newer exe is staged.
        static bool Check()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var rel = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(Get(Api, true));
            string tag = rel["tag_name"] as string;
            if (!Newer(tag, AppInfo.Version)) return false;
            string exeUrl = null, digest = null, sumsUrl = null;
            foreach (Dictionary<string, object> a in (object[])rel["assets"])
            {
                string name = a["name"] as string;
                if (name == "PC-Tune.exe") { exeUrl = a["browser_download_url"] as string; object d; if (a.TryGetValue("digest", out d)) digest = d as string; }
                else if (name == "SHA256SUMS.txt") sumsUrl = a["browser_download_url"] as string;
            }
            if (exeUrl == null || digest == null || sumsUrl == null) { Sys.ToFile("UPDATE " + tag + ": release lacks exe, digest or checksums - skipped"); return false; }
            string sums = Get(sumsUrl, false);
            using (var wc = new WebClient()) { wc.Headers["User-Agent"] = "PC-Tune"; wc.DownloadFile(exeUrl, Staged); }
            if (!Verified(File.ReadAllBytes(Staged), digest, sums))
            {
                File.Delete(Staged);
                Sys.ToFile("UPDATE " + tag + ": downloaded exe failed SHA-256 verification - deleted");
                return false;
            }
            AssemblyName.GetAssemblyName(Staged);   // throws when it is not a .NET executable
            Sys.ToFile("UPDATE " + tag + " downloaded and verified");
            return true;
        }

        static string Get(string url, bool json)
        {
            var r = (HttpWebRequest)WebRequest.Create(url);
            r.UserAgent = "PC-Tune";
            r.Timeout = 15000;
            if (json) r.Accept = "application/vnd.github+json";
            using (var resp = r.GetResponse()) using (var s = new StreamReader(resp.GetResponseStream())) return s.ReadToEnd();
        }

        // "v1.12" newer than "1.9"; anything unparsable is never newer.
        internal static bool Newer(string tag, string current)
        {
            Version a, b;
            return tag != null && Version.TryParse(tag.TrimStart('v', 'V'), out a) && Version.TryParse(current, out b) && a > b;
        }

        // Both GitHub's asset digest ("sha256:<hex>") and the SHA256SUMS.txt line must match the bytes.
        internal static bool Verified(byte[] exe, string digest, string sums)
        {
            string hash;
            using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(exe)).Replace("-", "").ToLowerInvariant();
            if (!string.Equals(digest, "sha256:" + hash, StringComparison.OrdinalIgnoreCase)) return false;
            foreach (string line in sums.Split('\n'))
            {
                string[] p = line.Trim().Split(new[] { ' ', '*' }, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length == 2 && p[1] == "PC-Tune.exe") return string.Equals(p[0], hash, StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        static bool WaitIdle(Func<bool> busy)
        {
            while (busy()) Thread.Sleep(TimeSpan.FromMinutes(1));
            return true;
        }

        // Windows allows renaming a running exe, so the old one moves aside and the new one takes its name.
        static bool Swap()
        {
            string exe = Exe;
            try
            {
                if (File.Exists(Old)) File.Delete(Old);
                File.Move(exe, Old);
                try { File.Move(Staged, exe); }
                catch { File.Move(Old, exe); throw; }
                Process.Start(new ProcessStartInfo(exe, "--after-update " + Process.GetCurrentProcess().Id + " /tray") { UseShellExecute = false });
                Sys.ToFile("UPDATE installed, restarting");
                return true;
            }
            catch (Exception ex) { Sys.ToFile("UPDATE install failed: " + ex.Message); return false; }
        }

        // Called first by the restarted instance: the old one must release the single-instance mutex.
        public static void WaitForPrevious(string[] args)
        {
            int pid;
            int i = Array.IndexOf(args, "--after-update");
            if (i < 0 || i + 1 >= args.Length || !int.TryParse(args[i + 1], out pid)) return;
            try { Process.GetProcessById(pid).WaitForExit(20000); } catch { }
        }
    }
}
