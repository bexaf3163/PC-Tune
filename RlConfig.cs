// RuneLite config schema read from the installed jars, and safe edits of RuneLite profile files.
//
// Compatibility rule for every preset that writes RuneLite or plugin settings: a value is written only
// when the installed plugin version declares that key with a matching type (boolean, int within
// @Range, or one of the enum's constants). Plugins change types between versions (117 HD turned
// tiledLighting from boolean into the DefaultBoolean enum), so presets are never trusted blindly.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace PCTune
{
    sealed class CfgItem
    {
        public char Kind = '?';          // Z boolean, I int, E enum, S string, ? unknown
        public HashSet<string> Values;   // enum constants
        public long Min = long.MinValue, Max = long.MaxValue;
    }

    static class RlSchema
    {
        static readonly Encoding Latin1 = Encoding.GetEncoding(28591);
        static readonly Dictionary<string, Dictionary<string, CfgItem>> cache = new Dictionary<string, Dictionary<string, CfgItem>>();
        internal static string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);   // tests point it elsewhere

        // Jar that declares each config group PC-Tune edits.
        internal static string JarFor(string group)
        {
            if (group == "hd") return PluginJar("117hd");
            if (group == "gpu" || group == "fpscontrol") return ClientJar();
            return null;
        }

        // The plugin-hub jar RuneLite actually loads. With several versions on disk, the client log
        // names the loaded one; without that evidence there is no schema and nothing is written.
        internal static string PluginJar(string id)
        {
            try
            {
                string[] jars = Directory.GetFiles(Path.Combine(Home, @".runelite\plugins"), id + "_*.jar");
                if (jars.Length <= 1) return jars.Length == 1 ? jars[0] : null;
                string log = Path.Combine(Home, @".runelite\logs\client.log");
                string text;
                using (var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var r = new StreamReader(fs, Latin1)) text = r.ReadToEnd();
                MatchCollection m = Regex.Matches(text, "Loading external plugin \"" + Regex.Escape(id) + "\" jar \"([^\"]+)\"");
                if (m.Count == 0) return null;
                string want = id + "_" + m[m.Count - 1].Groups[1].Value + ".jar";
                foreach (string j in jars) if (string.Equals(Path.GetFileName(j), want, StringComparison.OrdinalIgnoreCase)) return j;
            }
            catch { }
            return null;
        }

        static string ClientJar()
        {
            string best = null;
            Version bv = null;
            try
            {
                foreach (string j in Directory.GetFiles(Path.Combine(Home, @".runelite\repository2"), "client-*.jar"))
                {
                    Version v;
                    if (Version.TryParse(Path.GetFileNameWithoutExtension(j).Substring(7), out v) && (bv == null || v > bv)) { bv = v; best = j; }
                }
            }
            catch { }
            return best;
        }

        // key name -> type for a config group, or null when it cannot be established.
        internal static Dictionary<string, CfgItem> For(string group)
        {
            string jar = JarFor(group);
            if (jar == null) return null;
            string id;
            try { id = jar + "|" + File.GetLastWriteTimeUtc(jar).Ticks + "|" + group; } catch { return null; }
            lock (cache) if (cache.ContainsKey(id)) return cache[id];
            Dictionary<string, CfgItem> s = null;
            try { s = Load(jar, group); } catch { }
            lock (cache) cache[id] = s;
            return s;
        }

        // Reads the @ConfigGroup(group) interface: @ConfigItem keyName -> return type and @Range limits.
        internal static Dictionary<string, CfgItem> Load(string jar, string group)
        {
            using (var z = ZipFile.OpenRead(jar))
                foreach (var e in z.Entries)
                {
                    if (!e.FullName.EndsWith("Config.class", StringComparison.Ordinal)) continue;
                    var c = new ClassFile(Bytes(e));
                    if (c.Group != group) continue;
                    var r = new Dictionary<string, CfgItem>();
                    foreach (var m in c.Items)
                    {
                        var it = new CfgItem { Min = m.Min, Max = m.Max };
                        if (m.Ret == "Z") it.Kind = 'Z';
                        else if (m.Ret == "I") it.Kind = 'I';
                        else if (m.Ret == "Ljava/lang/String;") it.Kind = 'S';
                        else if (m.Ret.StartsWith("L", StringComparison.Ordinal))
                        {
                            var en = z.GetEntry(m.Ret.Substring(1, m.Ret.Length - 2) + ".class");
                            var ec = en == null ? null : new ClassFile(Bytes(en));
                            if (ec != null && ec.IsEnum) { it.Kind = 'E'; it.Values = ec.EnumConstants; }
                        }
                        r[m.Key] = it;
                    }
                    return r;
                }
            return null;
        }

        static byte[] Bytes(ZipArchiveEntry e)
        {
            using (var s = e.Open()) using (var ms = new MemoryStream()) { s.CopyTo(ms); return ms.ToArray(); }
        }

        // null when val is valid for key in the installed plugin version, otherwise why it is not.
        internal static string Check(string key, string val)
        {
            if (val == null || Regex.IsMatch(val, @"[^\x20-\x7E]")) return "malformed value";
            if (val == RlProfile.Missing) return null;   // restoring a key that was absent
            if (key == "runelite.clientBounds")
                return Regex.IsMatch(val, @"^-?\d+\\:-?\d+\\:\d+\\:\d+(\\:g)?$") ? null : "malformed window bounds";
            int dot = key.IndexOf('.');
            if (dot <= 0) return "unknown setting";
            string group = key.Substring(0, dot);
            var s = For(group);
            if (s == null) return "the installed " + group + " settings schema could not be read";
            CfgItem it;
            if (!s.TryGetValue(key.Substring(dot + 1), out it)) return "not offered by the installed plugin version";
            long n;
            switch (it.Kind)
            {
                case 'Z': return val == "true" || val == "false" ? null : "expects true/false";
                case 'I': return long.TryParse(val, out n) && n >= it.Min && n <= it.Max ? null : "expects a whole number in " + it.Min + ".." + it.Max;
                case 'E': return it.Values.Contains(val) ? null : "expects one of " + string.Join("/", it.Values);
                case 'S': return null;
                default: return "its type could not be established";
            }
        }
    }

    // Minimal Java class-file reader: constant pool, enum constants and RuneLite config annotations.
    sealed class ClassFile
    {
        internal sealed class Item { public string Key, Ret; public long Min = long.MinValue, Max = long.MaxValue; }

        public bool IsEnum;
        public string Group;
        public readonly HashSet<string> EnumConstants = new HashSet<string>(StringComparer.Ordinal);
        public readonly List<Item> Items = new List<Item>();
        readonly byte[] b;
        readonly object[] cp;
        int p;

        public ClassFile(byte[] data)
        {
            b = data;
            if (U4() != 0xCAFEBABE) throw new InvalidDataException("not a class file");
            p += 4;
            int n = U2();
            cp = new object[n];
            for (int i = 1; i < n; i++)
            {
                byte tag = b[p++];
                switch (tag)
                {
                    case 1: { int len = U2(); cp[i] = Encoding.UTF8.GetString(b, p, len); p += len; break; }
                    case 3: cp[i] = (long)(int)U4(); break;
                    case 4: p += 4; break;
                    case 5: case 6: p += 8; i++; break;
                    case 7: case 8: case 16: case 19: case 20: p += 2; break;
                    case 9: case 10: case 11: case 12: case 17: case 18: p += 4; break;
                    case 15: p += 3; break;
                    default: throw new InvalidDataException("constant pool tag " + tag);
                }
            }
            IsEnum = (U2() & 0x4000) != 0;
            p += 4;
            int ifaces = U2();
            p += 2 * ifaces;
            int fc = U2();
            for (int i = 0; i < fc; i++)
            {
                int access = U2();
                string name = Utf(U2());
                p += 2;
                if ((access & 0x4000) != 0) EnumConstants.Add(name);
                Attributes(null);
            }
            int mc = U2();
            for (int i = 0; i < mc; i++)
            {
                p += 4;
                string desc = Utf(U2());
                var it = new Item { Ret = desc.Substring(desc.IndexOf(')') + 1) };
                Attributes(it);
                if (it.Key != null && desc.StartsWith("()", StringComparison.Ordinal)) Items.Add(it);
            }
            Attributes(null);
        }

        int U2() { int v = (b[p] << 8) | b[p + 1]; p += 2; return v; }
        uint U4() { uint v = ((uint)b[p] << 24) | ((uint)b[p + 1] << 16) | ((uint)b[p + 2] << 8) | b[p + 3]; p += 4; return v; }
        string Utf(int i) { return cp[i] as string; }

        // it == null reads class-level annotations (ConfigGroup); otherwise a method's.
        void Attributes(Item it)
        {
            int n = U2();
            for (int a = 0; a < n; a++)
            {
                string name = Utf(U2());
                int len = (int)U4();
                int end = p + len;
                if (name == "RuntimeVisibleAnnotations") Annotations(it);
                p = end;
            }
        }

        void Annotations(Item it)
        {
            int n = U2();
            for (int i = 0; i < n; i++)
            {
                string type = Utf(U2());
                bool range = type == "Lnet/runelite/client/config/Range;";
                if (range && it != null) { it.Min = 0; it.Max = int.MaxValue; }
                int pairs = U2();
                for (int k = 0; k < pairs; k++)
                {
                    string name = Utf(U2());
                    object v = Value();
                    if (it != null && type == "Lnet/runelite/client/config/ConfigItem;" && name == "keyName") it.Key = v as string;
                    else if (it == null && type == "Lnet/runelite/client/config/ConfigGroup;" && name == "value") Group = v as string;
                    else if (it != null && range && v is long) { if (name == "min") it.Min = (long)v; else if (name == "max") it.Max = (long)v; }
                }
            }
        }

        // A string or int constant; other element values are skipped.
        object Value()
        {
            char tag = (char)b[p++];
            switch (tag)
            {
                case 's': return Utf(U2());
                case 'I': case 'B': case 'C': case 'S': case 'Z': return cp[U2()];
                case 'J': case 'D': case 'F': case 'c': p += 2; return null;
                case 'e': p += 4; return null;
                case '@': { p += 2; int pairs = U2(); for (int k = 0; k < pairs; k++) { p += 2; Value(); } return null; }
                case '[': { int c = U2(); for (int k = 0; k < c; k++) Value(); return null; }
                default: throw new InvalidDataException("annotation tag " + tag);
            }
        }
    }

    static class RlProfile
    {
        public const string Missing = "__MISSING__";
        static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

        // Applies {key, value} edits (value Missing removes the key) in one atomic replacement.
        // The file stays byte-for-byte as it was when an edit is malformed, the profile is malformed,
        // the client is running, or anything else changed the file meanwhile. The replaced original
        // is kept as <profile>.pctune-last.bak.
        internal static bool Commit(string file, IList<string[]> edits, Action<string> log, Func<bool> clientRunning)
        {
            foreach (var kv in edits)
                if (!Regex.IsMatch(kv[0], @"^[A-Za-z0-9_.\-]+$") || kv[1] == null || Regex.IsMatch(kv[1], @"[^\x20-\x7E]"))
                {
                    log("  [x] " + kv[0] + ": malformed setting - profile left unchanged");
                    return false;
                }
            byte[] before;
            try { before = File.ReadAllBytes(file); }
            catch (Exception ex) { log("  [x] profile not readable: " + ex.Message); return false; }
            string text = Latin1.GetString(before);
            string nl = text.Contains("\r\n") ? "\r\n" : "\n";
            var lines = new List<string>(text.Replace("\r\n", "\n").Split('\n'));
            bool trailing = lines[lines.Count - 1].Length == 0;
            if (trailing) lines.RemoveAt(lines.Count - 1);

            var done = new List<string>();
            foreach (var kv in edits)
            {
                if (Count(lines, kv[0]) > 1) { log("  [x] " + kv[0] + " appears twice in the profile - profile left unchanged"); return false; }
                int at = Find(lines, kv[0]);
                string was = at < 0 ? null : lines[at].Substring(lines[at].IndexOf('=') + 1).Trim();
                if (kv[1] == Missing)
                {
                    if (at < 0) continue;
                    lines.RemoveAt(at);
                    done.Add("  [+] " + kv[0] + ": removed (restored default)");
                }
                else if (at >= 0)
                {
                    if (was == kv[1]) continue;
                    lines[at] = kv[0] + "=" + kv[1];
                    done.Add("  [+] " + kv[0] + ": " + was + " -> " + kv[1]);
                }
                else
                {
                    lines.Add(kv[0] + "=" + kv[1]);
                    done.Add("  [+] " + kv[0] + " = " + kv[1]);
                }
            }
            if (done.Count == 0) return true;
            string after = string.Join(nl, lines) + (trailing ? nl : "");
            if (!Holds(after, edits)) { log("  [x] edited profile failed validation - profile left unchanged"); return false; }

            string tmp = file + ".pctune-tmp";
            try
            {
                if (clientRunning != null && clientRunning()) { log("  [!] RuneLite is running - profile left unchanged"); return false; }
                if (!Same(File.ReadAllBytes(file), before)) { log("  [!] the profile changed while settings were being prepared - nothing written, press Optimize again"); return false; }
                File.WriteAllBytes(tmp, Latin1.GetBytes(after));
                File.Replace(tmp, file, file + ".pctune-last.bak");
            }
            catch (Exception ex)
            {
                try { File.Delete(tmp); } catch { }
                log("  [x] profile not written: " + ex.Message);
                return false;
            }
            foreach (string d in done) log(d);
            return true;
        }

        static int Find(List<string> lines, string key)
        {
            for (int i = 0; i < lines.Count; i++) if (KeyOf(lines[i]) == key) return i;
            return -1;
        }

        static int Count(List<string> lines, string key)
        {
            int n = 0;
            foreach (string l in lines) if (KeyOf(l) == key) n++;
            return n;
        }

        static string KeyOf(string line)
        {
            string t = line.TrimStart();
            if (t.Length == 0 || t[0] == '#' || t[0] == '!') return null;
            int eq = line.IndexOf('=');
            return eq > 0 ? line.Substring(0, eq).Trim() : null;
        }

        static bool Holds(string text, IList<string[]> edits)
        {
            var lines = new List<string>(text.Replace("\r\n", "\n").Split('\n'));
            foreach (var kv in edits)
            {
                int at = Find(lines, kv[0]);
                if (kv[1] == Missing ? at >= 0 : at < 0 || lines[at].Substring(lines[at].IndexOf('=') + 1).Trim() != kv[1]) return false;
            }
            return true;
        }

        static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }
}
