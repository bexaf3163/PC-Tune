// Automatic continuation ping for OpenAI Codex Desktop App ("ChatGPT.exe" / "codex.exe").
// Monitors active Codex session rollout files in %USERPROFILE%\.codex\sessions, detects when a turn
// stops (task_complete, turn_aborted, usage limit window reset, or stall), and automatically queues
// and wakes the conversation with a continuation prompt (e.g., "Продолжай") on the configured interval.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace PCTune
{
    static class CodexPing
    {
        public class SessionState
        {
            public string ThreadId = "";
            public string RolloutPath = "";
            public string Title = "";
            public bool IsLockedByDesktop;
            public bool IsRunning;
            public bool IsStopped;
            public bool IsUsageLimit;
            public string LastEvent = "";
            public string ErrorMessage = "";
            public DateTime LastActivityUtc = DateTime.MinValue;
            public DateTime RateLimitResetUtc = DateTime.MinValue;
        }

        static readonly object sync = new object();
        static DateTime lastPingUtc = DateTime.MinValue;
        static string lastPingThreadId = "";
        static string lastStatusText = "Monitoring idle";
        static bool pingInFlight;

        public static DateTime LastPingUtc { get { lock (sync) return lastPingUtc; } }
        public static string LastStatusText { get { lock (sync) return lastStatusText; } }

        static string CodexHome()
        {
            string env = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (!string.IsNullOrEmpty(env) && Directory.Exists(env)) return env;
            string prof = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(prof, ".codex");
        }

        public static string FindCodexExe()
        {
            try
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string binRoot = Path.Combine(local, @"OpenAI\Codex\bin");
                if (Directory.Exists(binRoot))
                {
                    string best = null;
                    DateTime bestTime = DateTime.MinValue;
                    foreach (string dir in Directory.GetDirectories(binRoot))
                    {
                        string cand = Path.Combine(dir, "codex.exe");
                        if (File.Exists(cand))
                        {
                            DateTime wt = File.GetLastWriteTimeUtc(cand);
                            if (wt > bestTime) { bestTime = wt; best = cand; }
                        }
                    }
                    if (!string.IsNullOrEmpty(best)) return best;
                }
            }
            catch { }

            try
            {
                string wa = @"C:\Program Files\WindowsApps";
                if (Directory.Exists(wa))
                {
                    string best = null;
                    DateTime bestTime = DateTime.MinValue;
                    foreach (string dir in Directory.GetDirectories(wa, "OpenAI.Codex_*"))
                    {
                        string cand = Path.Combine(dir, @"app\resources\codex.exe");
                        if (File.Exists(cand))
                        {
                            DateTime wt = File.GetLastWriteTimeUtc(cand);
                            if (wt > bestTime) { bestTime = wt; best = cand; }
                        }
                    }
                    if (!string.IsNullOrEmpty(best)) return best;
                }
            }
            catch { }

            return null;
        }

        static bool IsThreadLockHeld(string codexHome, string threadId)
        {
            if (string.IsNullOrEmpty(threadId)) return false;
            string lockPath = Path.Combine(codexHome, "thread-writer-locks", threadId + ".lock");
            if (!File.Exists(lockPath)) return false;
            try
            {
                using (var fs = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    return false;
                }
            }
            catch (IOException)
            {
                return true;
            }
            catch
            {
                return false;
            }
        }

        static readonly Regex rxThreadId = new Regex(@"([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex rxTimestamp = new Regex(@"""timestamp""\s*:\s*""([^""]+)""", RegexOptions.Compiled);
        static readonly Regex rxTryAgain = new Regex(@"try again at\s+([A-Za-z0-9,\s:]+?)(?:\.|$|"")", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        static string ExtractThreadIdFromFilename(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            var m = rxThreadId.Match(name);
            return m.Success ? m.Groups[1].Value : "";
        }

        static Dictionary<string, string> ReadSessionTitles(string codexHome)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string idxPath = Path.Combine(codexHome, "session_index.jsonl");
            if (!File.Exists(idxPath)) return map;
            try
            {
                using (var fs = new FileStream(idxPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs, Encoding.UTF8))
                {
                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        var mId = Regex.Match(line, @"""id""\s*:\s*""([0-9a-f\-]{36})""", RegexOptions.IgnoreCase);
                        var mTitle = Regex.Match(line, @"""thread_name""\s*:\s*""((?:\\.|[^""])*)""");
                        if (mId.Success && mTitle.Success)
                        {
                            map[mId.Groups[1].Value] = Regex.Unescape(mTitle.Groups[1].Value);
                        }
                    }
                }
            }
            catch { }
            return map;
        }

        public static SessionState InspectActiveSession()
        {
            try
            {
                string home = CodexHome();
                string sessRoot = Path.Combine(home, "sessions");
                if (!Directory.Exists(sessRoot)) return null;

                var files = Directory.GetFiles(sessRoot, "rollout-*.jsonl", SearchOption.AllDirectories);
                if (files == null || files.Length == 0) return null;

                Array.Sort(files, delegate (string a, string b)
                {
                    return File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a));
                });

                var titles = ReadSessionTitles(home);
                SessionState bestLocked = null;
                SessionState bestRecent = null;

                int limit = Math.Min(files.Length, 12);
                for (int i = 0; i < limit; i++)
                {
                    string f = files[i];
                    string tid = ExtractThreadIdFromFilename(f);
                    if (string.IsNullOrEmpty(tid)) continue;

                    var st = ParseRolloutTail(f, tid, home, titles);
                    if (st == null) continue;

                    if (st.IsLockedByDesktop && bestLocked == null)
                        bestLocked = st;
                    if (bestRecent == null)
                        bestRecent = st;
                    if (bestLocked != null) break;
                }

                return bestLocked ?? bestRecent;
            }
            catch
            {
                return null;
            }
        }

        static SessionState ParseRolloutTail(string path, string threadId, string codexHome, Dictionary<string, string> titles)
        {
            try
            {
                byte[] buf;
                DateTime fileWriteUtc = File.GetLastWriteTimeUtc(path);
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long len = fs.Length;
                    if (len == 0) return null;
                    int readLen = (int)Math.Min(len, 65536L);
                    fs.Seek(len - readLen, SeekOrigin.Begin);
                    buf = new byte[readLen];
                    int got = 0;
                    while (got < readLen)
                    {
                        int n = fs.Read(buf, got, readLen - got);
                        if (n <= 0) break;
                        got += n;
                    }
                }

                string text = Encoding.UTF8.GetString(buf);
                // Skip dummy limit-ping CLI test sessions
                if (text.IndexOf("limit-ping", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    text.IndexOf("reply with one word", StringComparison.OrdinalIgnoreCase) >= 0)
                    return null;

                string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length == 0) return null;

                var st = new SessionState
                {
                    ThreadId = threadId,
                    RolloutPath = path,
                    IsLockedByDesktop = IsThreadLockHeld(codexHome, threadId),
                    LastActivityUtc = fileWriteUtc
                };

                string title;
                if (titles != null && titles.TryGetValue(threadId, out title))
                    st.Title = title;
                if (string.IsNullOrEmpty(st.Title))
                    st.Title = threadId.Substring(0, 8);

                bool sawTurnEnd = false;
                bool sawTurnWork = false;
                DateTime lastTs = fileWriteUtc;

                // Walk lines from the end to find the most recent lifecycle state
                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    string ln = lines[i];
                    if (ln.Length < 10 || ln[0] != '{') continue;

                    if (lastTs == fileWriteUtc)
                    {
                        var mts = rxTimestamp.Match(ln);
                        if (mts.Success)
                        {
                            DateTime parsed;
                            if (DateTime.TryParse(mts.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out parsed))
                                lastTs = parsed;
                        }
                    }

                    // Ignore passive thread_settings_applied events when determining whether a turn is running or stopped
                    if (ln.IndexOf("\"thread_settings_applied\"", StringComparison.Ordinal) >= 0)
                        continue;

                    if (ln.IndexOf("\"task_complete\"", StringComparison.Ordinal) >= 0 ||
                        ln.IndexOf("\"turn_aborted\"", StringComparison.Ordinal) >= 0)
                    {
                        sawTurnEnd = true;
                        st.LastEvent = ln.IndexOf("\"turn_aborted\"", StringComparison.Ordinal) >= 0 ? "turn_aborted" : "task_complete";
                        if (ln.IndexOf("usage_limit_exceeded", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            ln.IndexOf("usage limit", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            st.IsUsageLimit = true;
                            st.RateLimitResetUtc = ParseTryAgainUtc(ln, lastTs);
                        }
                        var mErr = Regex.Match(ln, @"""error""\s*:\s*\{\s*""message""\s*:\s*""((?:\\.|[^""])*)""");
                        if (mErr.Success)
                            st.ErrorMessage = Regex.Unescape(mErr.Groups[1].Value);
                        break;
                    }

                    if (ln.IndexOf("\"task_started\"", StringComparison.Ordinal) >= 0 ||
                        ln.IndexOf("\"item_completed\"", StringComparison.Ordinal) >= 0 ||
                        ln.IndexOf("\"token_count\"", StringComparison.Ordinal) >= 0 ||
                        ln.IndexOf("\"response_item\"", StringComparison.Ordinal) >= 0)
                    {
                        sawTurnWork = true;
                        st.LastEvent = "in_progress";
                        break;
                    }
                }

                st.LastActivityUtc = lastTs;
                double idleMinutes = (DateTime.UtcNow - st.LastActivityUtc).TotalMinutes;

                if (sawTurnEnd)
                {
                    st.IsStopped = true;
                    st.IsRunning = false;
                }
                else if (sawTurnWork)
                {
                    // If no event has been written for over 20 minutes while in_progress, treat as stalled/stopped
                    if (idleMinutes >= 20.0)
                    {
                        st.IsStopped = true;
                        st.IsRunning = false;
                        st.LastEvent = "stalled";
                    }
                    else
                    {
                        st.IsRunning = true;
                        st.IsStopped = false;
                    }
                }
                else
                {
                    st.IsStopped = idleMinutes >= 2.0;
                    st.IsRunning = !st.IsStopped;
                }

                return st;
            }
            catch
            {
                return null;
            }
        }

        static DateTime ParseTryAgainUtc(string line, DateTime eventUtc)
        {
            try
            {
                var m = rxTryAgain.Match(line);
                if (!m.Success) return DateTime.MinValue;
                string raw = m.Groups[1].Value.Trim();
                // Clean ordinal suffixes: 1st, 2nd, 3rd, 4th...
                raw = Regex.Replace(raw, @"(\d+)(st|nd|rd|th)\b", "$1", RegexOptions.IgnoreCase);

                DateTime localEvent = eventUtc.ToLocalTime();
                DateTime parsedLocal;
                string[] formats = new[]
                {
                    "h:mm tt", "hh:mm tt", "H:mm", "HH:mm",
                    "MMM d, yyyy h:mm tt", "MMM d, yyyy hh:mm tt",
                    "MMMM d, yyyy h:mm tt", "MMMM d, yyyy hh:mm tt"
                };

                if (DateTime.TryParseExact(raw, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out parsedLocal))
                {
                    // If only time of day was given, anchor it to localEvent's date
                    if (raw.IndexOf(',') < 0 && raw.Length <= 10)
                    {
                        parsedLocal = new DateTime(
                            localEvent.Year, localEvent.Month, localEvent.Day,
                            parsedLocal.Hour, parsedLocal.Minute, 0, DateTimeKind.Local);
                        if (parsedLocal < localEvent.AddMinutes(-5))
                            parsedLocal = parsedLocal.AddDays(1);
                    }
                    else
                    {
                        parsedLocal = DateTime.SpecifyKind(parsedLocal, DateTimeKind.Local);
                    }
                    return parsedLocal.ToUniversalTime();
                }
            }
            catch { }
            return DateTime.MinValue;
        }

        public static bool SendPingNow(Action<string> log, bool manual = false)
        {
            lock (sync)
            {
                if (pingInFlight) return false;
                pingInFlight = true;
            }

            try
            {
                var st = InspectActiveSession();
                if (st == null || string.IsNullOrEmpty(st.ThreadId))
                {
                    SetStatus("No Codex session found in .codex/sessions");
                    if (log != null) log("  [!] Codex Auto-Ping: no active session found");
                    return false;
                }

                if (!manual && st.IsRunning)
                {
                    SetStatus("Codex is actively working (" + st.Title + ")");
                    return false;
                }

                string codexExe = FindCodexExe();
                if (string.IsNullOrEmpty(codexExe) || !File.Exists(codexExe))
                {
                    SetStatus("codex.exe not found");
                    if (log != null) log("  [!] Codex Auto-Ping: codex.exe not found");
                    return false;
                }

                string prompt = Cfg.CodexPingPrompt;
                if (string.IsNullOrEmpty(prompt)) prompt = "Продолжай";

                // 1. Enqueue continuation message into queue_1.sqlite via codex.exe queue
                string args = string.Format("queue --thread {0} --message \"{1}\"",
                    st.ThreadId, prompt.Replace("\"", "\\\""));

                var psi = new ProcessStartInfo(codexExe, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                int code = -1;
                string stdout = "", stderr = "";
                using (var p = Process.Start(psi))
                {
                    stdout = p.StandardOutput.ReadToEnd();
                    stderr = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } }
                    else code = p.ExitCode;
                }

                if (code != 0)
                {
                    string err = (stderr ?? stdout ?? "").Trim();
                    if (err.Length > 120) err = err.Substring(0, 120);
                    SetStatus("Queue failed: " + err);
                    if (log != null) log("  [!] Codex Auto-Ping failed: " + err);
                    return false;
                }

                // 2. Wake the thread in Codex Desktop App via Windows shell deeplink (codex://threads/wake/<threadId>).
                // Using explorer.exe ensures Windows launches/routes to the non-elevated single-instance MSIX package.
                try
                {
                    var wakePsi = new ProcessStartInfo("explorer.exe", "codex://threads/wake/" + st.ThreadId)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using (var wp = Process.Start(wakePsi)) { }
                }
                catch { }

                lock (sync)
                {
                    lastPingUtc = DateTime.UtcNow;
                    lastPingThreadId = st.ThreadId;
                }

                string msg = string.Format("Pinged \"{0}\" ({1}) at {2:HH:mm:ss}",
                    st.Title, prompt, DateTime.Now);
                SetStatus(msg);
                Sys.ToFile("CODEX PING: " + msg + " [thread=" + st.ThreadId + "]");
                if (log != null) log("  [+] Codex Auto-Ping: " + msg);
                return true;
            }
            catch (Exception ex)
            {
                SetStatus("Error: " + ex.Message);
                if (log != null) log("  [!] Codex Auto-Ping error: " + ex.Message);
                return false;
            }
            finally
            {
                lock (sync) pingInFlight = false;
            }
        }

        public static string DescribeStatus()
        {
            var st = InspectActiveSession();
            if (st == null) return "No Codex session detected";

            DateTime nowUtc = DateTime.UtcNow;
            int idleMin = (int)Math.Max(0, (nowUtc - st.LastActivityUtc).TotalMinutes);

            if (st.IsRunning)
                return string.Format("Working: \"{0}\" (active {1}m ago)", st.Title, idleMin);

            if (st.IsUsageLimit && st.RateLimitResetUtc > nowUtc)
            {
                return string.Format("Rate limit until {0:HH:mm} · \"{1}\"",
                    st.RateLimitResetUtc.ToLocalTime(), st.Title);
            }

            DateTime lp = LastPingUtc;
            if (lp != DateTime.MinValue && (nowUtc - lp).TotalMinutes < Cfg.CodexPingMinutes)
            {
                int nextMin = Math.Max(1, (int)Math.Ceiling(Cfg.CodexPingMinutes - (nowUtc - lp).TotalMinutes));
                return string.Format("Idle ({0}m) · Last ping {1:HH:mm}, next in ~{2}m",
                    idleMin, lp.ToLocalTime(), nextMin);
            }

            return string.Format("Stopped ({0}, {1}m ago) · \"{2}\"",
                string.IsNullOrEmpty(st.LastEvent) ? "idle" : st.LastEvent, idleMin, st.Title);
        }

        static void SetStatus(string s)
        {
            lock (sync) lastStatusText = s;
        }

        public static void CheckAndPingIfDue(Action<string> log)
        {
            if (!Cfg.CodexPingOn) return;

            var st = InspectActiveSession();
            if (st == null)
            {
                SetStatus("No Codex session found");
                return;
            }

            if (st.IsRunning)
            {
                SetStatus("Working: \"" + st.Title + "\"");
                return;
            }

            if (!st.IsStopped) return;

            DateTime nowUtc = DateTime.UtcNow;

            // If stopped due to a usage limit with a known reset timestamp in the future, wait until reset time
            if (st.IsUsageLimit && st.RateLimitResetUtc > nowUtc)
            {
                SetStatus(string.Format("Waiting for rate limit reset at {0:HH:mm} (\"{1}\")",
                    st.RateLimitResetUtc.ToLocalTime(), st.Title));
                return;
            }

            int intervalMin = Cfg.CodexPingMinutes;
            double stoppedMinutes = (nowUtc - st.LastActivityUtc).TotalMinutes;
            double sinceLastPingMinutes = (nowUtc - LastPingUtc).TotalMinutes;

            // Wait at least 1 minute after task_complete (or intervalMin if not a rate-limit wakeup)
            // and always enforce intervalMin cooldown between our own pings.
            if (sinceLastPingMinutes < intervalMin)
                return;

            bool rateLimitJustExpired = st.IsUsageLimit && st.RateLimitResetUtc != DateTime.MinValue && nowUtc >= st.RateLimitResetUtc;
            if (!rateLimitJustExpired && stoppedMinutes < intervalMin)
                return;

            SendPingNow(log, false);
        }
    }

    class CodexSchedule : IDisposable
    {
        readonly Timer t;
        readonly Action<string> log;
        public event Action Pinged;

        public CodexSchedule(Action<string> logger)
        {
            log = logger;
            t = new Timer { Interval = 20000 }; // Check every 20 seconds
            t.Tick += delegate { Tick(); };
            Reload();
        }

        public void Reload()
        {
            if (Cfg.CodexPingOn)
            {
                if (!t.Enabled) t.Start();
            }
            else
            {
                t.Stop();
            }
        }

        void Tick()
        {
            if (!Cfg.CodexPingOn) return;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                DateTime before = CodexPing.LastPingUtc;
                CodexPing.CheckAndPingIfDue(log);
                if (CodexPing.LastPingUtc > before)
                {
                    var h = Pinged;
                    if (h != null) h();
                }
            });
        }

        public void Dispose()
        {
            if (t != null) { t.Stop(); t.Dispose(); }
        }
    }
}
