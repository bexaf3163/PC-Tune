// AI ping: once an hour sends a one-line message to Claude Code and ChatGPT Codex through their CLIs,
// so the 5-hour usage window starts early. Each CLI talks in its own folder, so the pings stay in one
// dedicated chat per tool (claude -c / codex exec resume --last only look at the current folder).
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace PCTune
{
    static class AiPing
    {
        const string Msg = "ping - reply with one word";
        static Timer timer;
        static int busy;

        static string Dir
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "PC-Tune", "ai-ping");
            }
        }

        // Called on start and whenever the tray toggle changes.
        public static void Apply()
        {
            if (timer != null) { timer.Dispose(); timer = null; }
            if (Cfg.AiPing) timer = new Timer(delegate { PingAll(); }, null, TimeSpan.FromSeconds(30), TimeSpan.FromHours(1));
        }

        static void PingAll()
        {
            if (Interlocked.Exchange(ref busy, 1) == 1) return;
            try
            {
                // Continue the dedicated chat; on the very first run there is none yet, so start it.
                Ping("claude", "claude -p \"" + Msg + "\" -c --model haiku",
                               "claude -p \"" + Msg + "\" --model haiku");
                Ping("codex", "codex exec resume --last --skip-git-repo-check \"" + Msg + "\"",
                              "codex exec --skip-git-repo-check \"" + Msg + "\"");
            }
            finally { busy = 0; }
        }

        static void Ping(string name, string resume, string fresh)
        {
            string dir = Path.Combine(Dir, name);
            string result;
            try
            {
                Directory.CreateDirectory(dir);
                string outText;
                int code = Run(dir, resume, out outText);
                if (code != 0) code = Run(dir, fresh, out outText);
                result = (code == 0 ? "OK   " : "FAIL ") + Oneline(outText);
            }
            catch (Exception e) { result = "FAIL " + e.Message; }
            Log(name + ": " + result);
        }

        static int Run(string dir, string cmd, out string output)
        {
            var psi = new ProcessStartInfo("cmd.exe", "/d /c " + cmd)
            {
                WorkingDirectory = dir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var p = Process.Start(psi))
            {
                p.StandardInput.Close();
                var err = p.StandardError.ReadToEndAsync();
                var outp = p.StandardOutput.ReadToEndAsync();
                if (!p.WaitForExit(180000))
                {
                    try { Process.Start(new ProcessStartInfo("taskkill", "/T /F /PID " + p.Id) { CreateNoWindow = true, UseShellExecute = false }); }
                    catch { }
                    output = "timeout";
                    return -1;
                }
                output = (outp.Result + " " + err.Result).Trim();
                return p.ExitCode;
            }
        }

        static string Oneline(string s)
        {
            s = (s ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
            return s.Length > 200 ? s.Substring(0, 200) : s;
        }

        static void Log(string line)
        {
            try
            {
                string f = Path.Combine(Dir, "ping.log");
                if (File.Exists(f) && new FileInfo(f).Length > 100000) File.Delete(f);
                File.AppendAllText(f, DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "  " + line + Environment.NewLine);
            }
            catch { }
        }
    }
}
