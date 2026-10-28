// Regression tests for RuneLite profile edits. Built with every source file by test.cmd.
using System;
using System.Collections.Generic;
using System.IO;

namespace PCTune
{
    static class RlTests
    {
        static int failed;
        static readonly List<string> log = new List<string>();
        static readonly Action<string> Log = s => log.Add(s);
        static string fixtureJar;

        static int Main(string[] args)
        {
            fixtureJar = Path.GetFullPath(args.Length > 0 ? args[0] : @"tests\fixtures\117hd_TESTHASH.jar");
            Games.ClientBusy = () => false;
            Run("boolean true is rejected for an enum setting", BooleanRejectedForEnum);
            Run("valid enum, boolean and ranged int values are accepted", ValidValuesAccepted);
            Run("unknown keys and unidentified plugin versions are skipped", UnknownSkipped);
            Run("preset write skips invalid values and writes the rest", SetKeysSkipsInvalid);
            Run("malformed input leaves the profile byte-for-byte intact", MalformedLeavesFile);
            Run("restore puts back only PC-Tune's keys", RestorePreservesUnrelated);
            Run("restore leaves originals the plugin no longer accepts", RestoreSkipsIncompatible);
            Run("a concurrent profile change is never overwritten", ConcurrentChangeProtected);
            Run("a running client blocks the write", RunningClientBlocks);
            Run("updates only to a strictly newer release", UpdateVersionCompare);
            Run("an update must match both GitHub's digest and SHA256SUMS", UpdateVerification);
            Console.WriteLine(failed == 0 ? "ALL TESTS PASSED" : failed + " TEST(S) FAILED");
            return failed == 0 ? 0 : 1;
        }

        static void Run(string name, Action t)
        {
            log.Clear();
            try { t(); Console.WriteLine("ok   " + name); }
            catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); foreach (var l in log) Console.WriteLine("     " + l); }
        }

        static void Assert(bool cond, string what) { if (!cond) throw new Exception(what); }

        // A temporary user profile holding the fixture 117 HD jar (or several jars) and a RuneLite profile.
        static string Home(int jars = 1)
        {
            string h = Path.Combine(Path.GetTempPath(), "pctune-test-" + Guid.NewGuid().ToString("N"));
            string plugins = Path.Combine(h, @".runelite\plugins");
            Directory.CreateDirectory(plugins);
            File.Copy(fixtureJar, Path.Combine(plugins, "117hd_TESTHASH.jar"));
            if (jars > 1) File.Copy(fixtureJar, Path.Combine(plugins, "117hd_OTHERHASH.jar"));
            RlSchema.Home = h;
            return h;
        }

        static string Profile(string home, string text)
        {
            string f = Path.Combine(home, "default-1.properties");
            File.WriteAllText(f, text);
            return f;
        }

        const string Sample = "#Comment line\nhd.tiledLighting=DEFAULT\nhd.unlockFps=false\nhd.fpsTarget=50\nquesthelper.solid=true\n";

        static void BooleanRejectedForEnum()
        {
            Home();
            Assert(RlSchema.Check("hd.tiledLighting", "true") != null, "true accepted for DefaultBoolean");
            Assert(RlSchema.Check("hd.tiledLighting", "TRUE") != null, "TRUE accepted for DefaultBoolean");
        }

        static void ValidValuesAccepted()
        {
            Home();
            Assert(RlSchema.Check("hd.tiledLighting", "ON") == null, "ON rejected");
            Assert(RlSchema.Check("hd.tiledLighting", "DEFAULT") == null, "DEFAULT rejected");
            Assert(RlSchema.Check("hd.unlockFps", "true") == null, "boolean rejected");
            Assert(RlSchema.Check("hd.fpsTarget", "60") == null, "int rejected");
            Assert(RlSchema.Check("hd.fpsTarget", "1000") != null, "int above @Range accepted");
            Assert(RlSchema.Check("hd.drawDistance", "-1") != null, "int below @Range accepted");
            Assert(RlSchema.Check("hd.unlockFps", "yes") != null, "non-boolean accepted");
        }

        static void UnknownSkipped()
        {
            Home();
            Assert(RlSchema.Check("hd.environmentalLighting", "true") != null, "removed key accepted");
            Home(2);   // two 117 HD versions and no client log naming the loaded one
            Assert(RlSchema.Check("hd.unlockFps", "true") != null, "ambiguous plugin version accepted");
        }

        static void SetKeysSkipsInvalid()
        {
            string h = Home();
            string f = Profile(h, Sample);
            Assert(Games.SetKeys(f, new List<string[]> { new[] { "hd.tiledLighting", "true" }, new[] { "hd.unlockFps", "true" } }, Log), "SetKeys failed");
            Assert(Games.Prop(f, "hd.tiledLighting") == "DEFAULT", "invalid enum value written");
            Assert(Games.Prop(f, "hd.unlockFps") == "true", "valid value not written");
            Assert(File.Exists(f + ".pctune-last.bak") && File.ReadAllText(f + ".pctune-last.bak") == Sample, "exact backup missing");

            h = Home(2);
            f = Profile(h, Sample);
            Assert(Games.SetKeys(f, new List<string[]> { new[] { "hd.unlockFps", "true" } }, Log), "SetKeys failed");
            Assert(File.ReadAllText(f) == Sample, "wrote although the plugin version is unknown");
        }

        static void MalformedLeavesFile()
        {
            string h = Home();
            string f = Profile(h, Sample);
            Assert(!RlProfile.Commit(f, new List<string[]> { new[] { "hd.unlockFps", "true\nhd.fpsTarget=1" } }, Log, () => false), "newline value accepted");
            Assert(!RlProfile.Commit(f, new List<string[]> { new[] { "bad key", "1" } }, Log, () => false), "malformed key accepted");
            Assert(File.ReadAllText(f) == Sample, "profile changed by rejected edits");

            string dup = Sample + "hd.unlockFps=true\n";
            File.WriteAllText(f, dup);
            Assert(!RlProfile.Commit(f, new List<string[]> { new[] { "hd.unlockFps", "true" } }, Log, () => false), "duplicate key accepted");
            Assert(File.ReadAllText(f) == dup, "malformed profile was rewritten");
        }

        static void RestorePreservesUnrelated()
        {
            string h = Home();
            string f = Profile(h, Sample);
            Assert(Games.SetKeys(f, new List<string[]> { new[] { "hd.unlockFps", "true" }, new[] { "hd.fpsTarget", "60" }, new[] { "hd.drawDistance", "70" } }, Log), "apply failed");
            // The user changes an unrelated setting after optimizing.
            Assert(RlProfile.Commit(f, new List<string[]> { new[] { "questhelper.solid", "false" } }, Log, () => false), "user edit failed");
            Assert(Games.RestoreKeys(f, Log), "restore failed");
            Assert(Games.Prop(f, "hd.unlockFps") == "false" && Games.Prop(f, "hd.fpsTarget") == "50", "originals not restored");
            Assert(Games.Prop(f, "hd.drawDistance") == "?", "key absent before was not removed");
            Assert(Games.Prop(f, "questhelper.solid") == "false", "unrelated user change reverted");
            Assert(Games.Prop(f, "hd.tiledLighting") == "DEFAULT", "untouched key changed");
            Assert(File.ReadAllText(f).StartsWith("#Comment line\n"), "comment lost");
        }

        static void RestoreSkipsIncompatible()
        {
            string h = Home();
            string f = Profile(h, Sample);
            // A backup taken under an older 117 HD, where tiledLighting was still a boolean.
            File.WriteAllText(f + ".pctune.keys", "hd.tiledLighting=true\nhd.fpsTarget=40\n");
            Assert(Games.RestoreKeys(f, Log), "restore failed");
            Assert(Games.Prop(f, "hd.tiledLighting") == "DEFAULT", "incompatible original restored");
            Assert(Games.Prop(f, "hd.fpsTarget") == "40", "compatible original not restored");
        }

        static void ConcurrentChangeProtected()
        {
            string h = Home();
            string f = Profile(h, Sample);
            string external = Sample + "runelite.someOtherSetting=1\n";
            // RuneLite (or anything else) writes the profile between PC-Tune's read and its replace.
            bool ok = RlProfile.Commit(f, new List<string[]> { new[] { "hd.unlockFps", "true" } }, Log,
                () => { File.WriteAllText(f, external); return false; });
            Assert(!ok, "commit reported success over a concurrent change");
            Assert(File.ReadAllText(f) == external, "concurrent change overwritten");
        }

        static void UpdateVersionCompare()
        {
            Assert(Updater.Newer("v1.10", "1.9"), "1.10 not newer than 1.9");
            Assert(!Updater.Newer("v1.2", "1.2"), "same version treated as newer");
            Assert(!Updater.Newer("v1.1", "1.2"), "older version treated as newer");
            Assert(!Updater.Newer("v1.3", "dev"), "dev build would self-update");
            Assert(!Updater.Newer("nightly", "1.2"), "unparsable tag treated as newer");
        }

        static void UpdateVerification()
        {
            byte[] exe = { 1, 2, 3 };
            const string hash = "039058c6f2c0cb492c533b0a4d14ef77cc0f78abccced5287d84a1a2011cfb81";
            string sums = hash + "  PC-Tune.exe\r\n";
            Assert(Updater.Verified(exe, "sha256:" + hash, sums), "matching update rejected");
            Assert(!Updater.Verified(exe, "sha256:" + new string('0', 64), sums), "wrong GitHub digest accepted");
            Assert(!Updater.Verified(exe, "sha256:" + hash, new string('0', 64) + "  PC-Tune.exe\n"), "wrong SHA256SUMS accepted");
            Assert(!Updater.Verified(exe, "sha256:" + hash, hash + "  Other.exe\n"), "missing SHA256SUMS line accepted");
        }

        static void RunningClientBlocks()
        {
            string h = Home();
            string f = Profile(h, Sample);
            Assert(!RlProfile.Commit(f, new List<string[]> { new[] { "hd.unlockFps", "true" } }, Log, () => true), "wrote while client running");
            Assert(File.ReadAllText(f) == Sample, "profile changed while client running");
        }
    }
}
