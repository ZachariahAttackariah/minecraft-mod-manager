// What's inside a jar (mod ids, versions, the mods it needs, mods bundled inside it), read from
// META-INF/neoforge.mods.toml (or mods.toml / fabric.mod.json), and the pre-launch check built on it:
// missing or turned-off required mods, wrong versions, the same mod twice, mods for another
// Minecraft version or loader. NeoForge refuses to start in most of these cases, so catching them
// before Play / Start saves a slow launch that ends in an error screen.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace MinecraftModManager
{
    sealed class ModInfo
    {
        public string Id, Version, Name, Description;
    }

    sealed class DepInfo
    {
        public string Owner, Id, Type, Range, Side; // Type: required | optional | incompatible | discouraged
    }

    sealed class JarMeta
    {
        public string Kind = "none"; // neoforge | forge | fabric | quilt | none (a plain library)
        public readonly List<ModInfo> Mods = new List<ModInfo>();
        public readonly List<DepInfo> Deps = new List<DepInfo>();
        public readonly List<ModInfo> Nested = new List<ModInfo>(); // mods bundled inside (jar-in-jar)
        public bool Broken;
    }

    static partial class Program
    {
        static readonly Dictionary<string, JarMeta> JarMetaCache = new Dictionary<string, JarMeta>();

        static JarMeta GetJarMeta(FileInfo file)
        {
            string key = Regex.Replace(file.FullName, @"\.disabled$", "", RegexOptions.IgnoreCase) + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks;
            lock (JarMetaCache)
            {
                JarMeta cached;
                if (JarMetaCache.TryGetValue(key, out cached)) return cached;
            }
            var meta = new JarMeta();
            try
            {
                using (var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                    ReadJar(zip, meta, true);
            }
            catch (Exception)
            {
                meta.Broken = true;
            }
            lock (JarMetaCache) JarMetaCache[key] = meta;
            return meta;
        }

        // The jar's own mod, or for a wrapper jar with no metadata of its own (Kotlin for Forge,
        // Sinytra Connector) the first mod bundled inside it.
        static ModInfo MainMod(JarMeta meta)
        {
            return meta.Mods.FirstOrDefault() ?? (meta.Kind == "none" ? meta.Nested.FirstOrDefault() : null);
        }

        static string EntryText(ZipArchive zip, string name)
        {
            ZipArchiveEntry entry = zip.GetEntry(name);
            if (entry == null) return null;
            using (var reader = new StreamReader(entry.Open(), Encoding.UTF8)) return reader.ReadToEnd();
        }

        static void ReadJar(ZipArchive zip, JarMeta meta, bool top)
        {
            string manifest = EntryText(zip, "META-INF/MANIFEST.MF");
            string jarVersion = null;
            if (manifest != null)
            {
                Match m = Regex.Match(manifest, @"(?m)^Implementation-Version:\s*(\S+)");
                if (m.Success) jarVersion = m.Groups[1].Value.Trim();
            }

            // Some jars (Sinytra Connector, Kotlin for Forge) are only a wrapper: the mod is a jar inside.
            if (top) ReadNested(zip, meta, JarJarPaths(zip));

            string toml = EntryText(zip, "META-INF/neoforge.mods.toml");
            string kind = "neoforge";
            if (toml == null) { toml = EntryText(zip, "META-INF/mods.toml"); kind = "forge"; }
            if (toml != null)
            {
                meta.Kind = kind;
                foreach (KeyValuePair<string, Dictionary<string, string>> section in ParseToml(toml))
                {
                    Dictionary<string, string> v = section.Value;
                    if (section.Key == "mods")
                    {
                        string id = Val(v, "modId");
                        if (id == null) continue;
                        string version = Val(v, "version");
                        if (version != null && version.Contains("${file.jarVersion}")) version = jarVersion;
                        if (version != null && version.Contains("${")) version = null;
                        meta.Mods.Add(new ModInfo { Id = id, Version = version, Name = Val(v, "displayName") ?? id, Description = Val(v, "description") });
                    }
                    else if (section.Key.StartsWith("dependencies.", StringComparison.OrdinalIgnoreCase))
                    {
                        string id = Val(v, "modId");
                        if (id == null) continue;
                        string type = Val(v, "type");
                        if (type == null)
                        {
                            string mandatory = Val(v, "mandatory");
                            type = mandatory == null || mandatory == "true" ? "required" : "optional";
                        }
                        meta.Deps.Add(new DepInfo
                        {
                            Owner = section.Key.Substring("dependencies.".Length),
                            Id = id,
                            Type = type.ToLowerInvariant(),
                            Range = Val(v, "versionRange"),
                            Side = (Val(v, "side") ?? "BOTH").ToUpperInvariant()
                        });
                    }
                }
                return;
            }

            string fabric = EntryText(zip, "fabric.mod.json");
            string quilt = fabric == null ? EntryText(zip, "quilt.mod.json") : null;
            if (fabric != null || quilt != null)
            {
                meta.Kind = fabric != null ? "fabric" : "quilt";
                Dictionary<string, object> json = null;
                try { json = new JavaScriptSerializer().DeserializeObject(fabric ?? quilt) as Dictionary<string, object>; } catch { }
                if (quilt != null && json != null) json = D(Get(json, "quilt_loader"));
                if (json != null)
                {
                    string id = S(json, "id");
                    if (id != null) meta.Mods.Add(new ModInfo { Id = id, Version = S(json, "version"), Name = S(json, "name") ?? S(D(Get(json, "metadata")), "name") ?? id });
                    foreach (object alias in A(Get(json, "provides")))
                    {
                        string a = alias as string ?? S(D(alias), "id");
                        if (a != null) meta.Mods.Add(new ModInfo { Id = a, Version = S(json, "version"), Name = id });
                    }
                    if (top)
                    {
                        var paths = new List<string>();
                        foreach (object j in A(Get(json, "jars"))) { string p = S(D(j), "file"); if (p != null) paths.Add(p); }
                        ReadNested(zip, meta, paths);
                    }
                }
                else
                {
                    Match m = Regex.Match(fabric ?? quilt, "\"id\"\\s*:\\s*\"([^\"]+)\"");
                    if (m.Success) meta.Mods.Add(new ModInfo { Id = m.Groups[1].Value, Name = m.Groups[1].Value });
                }
            }
        }

        static List<string> JarJarPaths(ZipArchive zip)
        {
            var paths = new List<string>();
            string text = EntryText(zip, "META-INF/jarjar/metadata.json");
            if (text == null) return paths;
            try
            {
                var json = new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string, object>;
                foreach (object j in A(Get(json, "jars"))) { string p = S(D(j), "path"); if (p != null) paths.Add(p); }
            }
            catch { }
            return paths;
        }

        static void ReadNested(ZipArchive zip, JarMeta meta, List<string> paths)
        {
            foreach (string path in paths)
            {
                try
                {
                    ZipArchiveEntry entry = zip.GetEntry(path);
                    if (entry == null || entry.Length > 64L * 1024 * 1024) continue;
                    var copy = new MemoryStream();
                    using (Stream s = entry.Open()) s.CopyTo(copy);
                    copy.Position = 0;
                    var inner = new JarMeta();
                    using (var nested = new ZipArchive(copy, ZipArchiveMode.Read)) ReadJar(nested, inner, false);
                    meta.Nested.AddRange(inner.Mods);
                }
                catch { }
            }
        }

        static string Val(Dictionary<string, string> d, string key)
        {
            string v;
            return d.TryGetValue(key, out v) && v.Length > 0 ? v : null;
        }

        // A small TOML reader: enough for mods.toml files (tables, arrays of tables, strings, booleans).
        static List<KeyValuePair<string, Dictionary<string, string>>> ParseToml(string text)
        {
            text = Regex.Replace(text, "(?s)\"\"\".*?\"\"\"", "\"\"");
            text = Regex.Replace(text, "(?s)'''.*?'''", "''");
            var sections = new List<KeyValuePair<string, Dictionary<string, string>>>();
            var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            sections.Add(new KeyValuePair<string, Dictionary<string, string>>("", current));
            foreach (string raw in text.Split('\n'))
            {
                string line = StripTomlComment(raw).Trim();
                if (line.Length == 0) continue;
                Match h = Regex.Match(line, @"^\[\[\s*(.+?)\s*\]\]$");
                if (!h.Success) h = Regex.Match(line, @"^\[\s*([^\[\]]+?)\s*\]$");
                if (h.Success)
                {
                    current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    string name = Regex.Replace(h.Groups[1].Value, "[\"'\\s]", "");
                    sections.Add(new KeyValuePair<string, Dictionary<string, string>>(name, current));
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                current[line.Substring(0, eq).Trim().Trim('"', '\'')] = TomlValue(line.Substring(eq + 1).Trim());
            }
            return sections;
        }

        static string StripTomlComment(string line)
        {
            char quote = '\0';
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (quote != '\0')
                {
                    if (c == '\\' && quote == '"') i++;
                    else if (c == quote) quote = '\0';
                }
                else if (c == '"' || c == '\'') quote = c;
                else if (c == '#') return line.Substring(0, i);
            }
            return line;
        }

        static string TomlValue(string raw)
        {
            if (raw.StartsWith("'"))
            {
                int end = raw.IndexOf('\'', 1);
                return end < 0 ? raw.Substring(1) : raw.Substring(1, end - 1);
            }
            if (!raw.StartsWith("\"")) return raw.Trim();
            var sb = new StringBuilder();
            for (int i = 1; i < raw.Length; i++)
            {
                char c = raw[i];
                if (c == '"') break;
                if (c == '\\' && i + 1 < raw.Length)
                {
                    char n = raw[++i];
                    if (n == 'n') sb.Append('\n');
                    else if (n == 't') sb.Append('\t');
                    else if (n == 'u' && i + 4 < raw.Length)
                    {
                        int code;
                        if (int.TryParse(raw.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code)) { sb.Append((char)code); i += 4; }
                    }
                    else sb.Append(n);
                    continue;
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        // ---------- Versions: Maven's ComparableVersion rules, which NeoForge uses for mods.toml ranges ----------
        // "." separates items, "-" (and a switch between digits and letters) starts a nested list, so
        // 1.21.1-3.6.4 is [1, 21, 1, [3, 6, 4]] and sorts after 1.21-3.1.0c = [1, 21, [3, 1, 0, [c]]].

        sealed class VInt { public string Digits; } // without leading zeros; "" is 0
        sealed class VStr { public string Value; }

        static readonly string[] Qualifiers = { "alpha", "beta", "milestone", "rc", "snapshot", "", "sp" };

        static object MavenItem(bool digit, string text, bool followedByDigit)
        {
            if (digit) return new VInt { Digits = text.TrimStart('0') };
            if (followedByDigit && text.Length == 1) text = text == "a" ? "alpha" : text == "b" ? "beta" : text == "m" ? "milestone" : text;
            if (text == "ga" || text == "final" || text == "release") text = "";
            else if (text == "cr") text = "rc";
            return new VStr { Value = text };
        }

        static List<object> ParseMaven(string version)
        {
            version = (version ?? "").ToLowerInvariant();
            var root = new List<object>();
            List<object> list = root;
            var stack = new Stack<List<object>>();
            stack.Push(root);
            bool isDigit = false;
            int start = 0;
            for (int i = 0; i < version.Length; i++)
            {
                char c = version[i];
                if (c == '.' || c == '-')
                {
                    list.Add(i == start ? (object)new VInt { Digits = "" } : MavenItem(isDigit, version.Substring(start, i - start), false));
                    start = i + 1;
                    if (c == '-')
                    {
                        var sub = new List<object>();
                        list.Add(sub);
                        list = sub;
                        stack.Push(sub);
                    }
                }
                else if (char.IsDigit(c))
                {
                    if (!isDigit && i > start)
                    {
                        list.Add(MavenItem(false, version.Substring(start, i - start), true));
                        start = i;
                        var sub = new List<object>();
                        list.Add(sub);
                        list = sub;
                        stack.Push(sub);
                    }
                    isDigit = true;
                }
                else
                {
                    if (isDigit && i > start)
                    {
                        list.Add(MavenItem(true, version.Substring(start, i - start), false));
                        start = i;
                        var sub = new List<object>();
                        list.Add(sub);
                        list = sub;
                        stack.Push(sub);
                    }
                    isDigit = false;
                }
            }
            if (version.Length > start) list.Add(MavenItem(isDigit, version.Substring(start), false));
            while (stack.Count > 0) Normalize(stack.Pop());
            return root;
        }

        static bool IsNullItem(object item)
        {
            var n = item as VInt;
            if (n != null) return n.Digits.Length == 0;
            var s = item as VStr;
            if (s != null) return s.Value.Length == 0;
            return ((List<object>)item).Count == 0;
        }

        static void Normalize(List<object> list)
        {
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (IsNullItem(list[i])) list.RemoveAt(i);
                else if (!(list[i] is List<object>)) break;
            }
        }

        static int QualifierCompare(string a, string b)
        {
            int x = Array.IndexOf(Qualifiers, a), y = Array.IndexOf(Qualifiers, b);
            if (x >= 0 && y >= 0) return x.CompareTo(y);
            if (x >= 0) return -1; // unknown qualifiers sort after all known ones
            if (y >= 0) return 1;
            return string.CompareOrdinal(a, b);
        }

        // Compares item a (never null) with b (may be null, meaning "nothing there").
        static int CompareItem(object a, object b)
        {
            var ai = a as VInt;
            if (ai != null)
            {
                if (b == null) return ai.Digits.Length == 0 ? 0 : 1;
                var bi = b as VInt;
                if (bi == null) return 1; // a number beats a qualifier or a sub-list
                return ai.Digits.Length != bi.Digits.Length ? ai.Digits.Length.CompareTo(bi.Digits.Length) : Math.Sign(string.CompareOrdinal(ai.Digits, bi.Digits));
            }
            var asv = a as VStr;
            if (asv != null)
            {
                if (b == null) return Math.Sign(QualifierCompare(asv.Value, ""));
                if (b is VInt) return -1;
                var bs = b as VStr;
                if (bs == null) return -1;
                return Math.Sign(QualifierCompare(asv.Value, bs.Value));
            }
            var al = (List<object>)a;
            if (b == null) return al.Count == 0 ? 0 : CompareItem(al[0], null);
            if (b is VInt) return -1;
            if (b is VStr) return 1;
            var bl = (List<object>)b;
            for (int i = 0; i < Math.Max(al.Count, bl.Count); i++)
            {
                object l = i < al.Count ? al[i] : null, r = i < bl.Count ? bl[i] : null;
                int c = l == null ? (r == null ? 0 : -CompareItem(r, null)) : CompareItem(l, r);
                if (c != 0) return c;
            }
            return 0;
        }

        static int CompareVersions(string a, string b)
        {
            return CompareItem(ParseMaven(a), ParseMaven(b));
        }

        static bool Unknown(string version) { return string.IsNullOrEmpty(version) || version.Contains("${"); }

        // Maven version ranges: [1.0,2.0)  [1.0,)  (,1.0]  [1.0]  and several joined with commas.
        // A bare version ("1.0") or "*" accepts anything, as NeoForge treats it.
        static bool InRange(string version, string range)
        {
            if (Unknown(version) || string.IsNullOrWhiteSpace(range)) return true;
            range = range.Replace(" ", "");
            if (range == "*" || (!range.StartsWith("[") && !range.StartsWith("("))) return true;
            bool any = false;
            foreach (Match m in Regex.Matches(range, @"([\[\(])([^\[\]\(\)]*)([\]\)])"))
            {
                any = true;
                string body = m.Groups[2].Value;
                int comma = body.IndexOf(',');
                if (comma < 0)
                {
                    if (CompareVersions(version, body) == 0) return true;
                    continue;
                }
                string lo = body.Substring(0, comma), hi = body.Substring(comma + 1);
                bool ok = true;
                if (lo.Length > 0) { int c = CompareVersions(version, lo); ok &= m.Groups[1].Value == "[" ? c >= 0 : c > 0; }
                if (hi.Length > 0) { int c = CompareVersions(version, hi); ok &= m.Groups[3].Value == "]" ? c <= 0 : c < 0; }
                if (ok) return true;
            }
            return !any;
        }

        // NeoForge loads mods made for 1.21 on 1.21.1 (a hotfix release) even when their range stops just
        // short of it, e.g. [1.21,1.21.1). Seen in practice with More Culling, JEI, Iris and Towntalk.
        static bool MinecraftHotfixFits(string version, string range)
        {
            Match m = Regex.Match(version ?? "", @"^(\d+\.\d+)\.\d+$");
            return m.Success && InRange(m.Groups[1].Value, range);
        }

        static string DescribeRange(string range)
        {
            if (string.IsNullOrWhiteSpace(range)) return "any version";
            Match m = Regex.Match(range.Replace(" ", ""), @"^([\[\(])([^,\[\]\(\)]*)(,([^\[\]\(\)]*))?([\]\)])$");
            if (!m.Success) return range;
            string lo = m.Groups[2].Value, hi = m.Groups[4].Value;
            if (!m.Groups[3].Success) return lo;
            if (lo.Length > 0 && hi.Length == 0) return lo + (m.Groups[1].Value == "[" ? " or newer" : " (newer than)");
            if (lo.Length == 0 && hi.Length > 0) return hi + (m.Groups[5].Value == "]" ? " or older" : " (older than)");
            return lo + " to " + hi + (m.Groups[5].Value == ")" ? " (not including " + hi + ")" : "");
        }

        // ---------- The pre-launch check ----------

        sealed class Provider
        {
            public string File, Version, Name;
            public bool Nested;
        }

        // { minecraft version, NeoForge version } from the Prism instance's mmc-pack.json, when there is one.
        static string[] PackVersions(string modsFolder)
        {
            var result = new string[2];
            try
            {
                DirectoryInfo instance = new DirectoryInfo(modsFolder).Parent.Parent;
                string pack = Path.Combine(instance.FullName, "mmc-pack.json");
                if (!File.Exists(pack)) return result;
                var json = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(pack)) as Dictionary<string, object>;
                foreach (object c in A(Get(json, "components")))
                {
                    string uid = S(D(c), "uid");
                    if (uid == "net.minecraft") result[0] = S(D(c), "version");
                    if (uid == "net.neoforged") result[1] = S(D(c), "version");
                }
            }
            catch { }
            return result;
        }

        static Dictionary<string, object> CheckMods(string side)
        {
            string dir, mc, nf, where;
            if (side == "server")
            {
                string server = RequireServerDir();
                dir = Path.Combine(server, "mods");
                mc = gameVersion;
                nf = ServerLoaderVersion(server);
                where = "the server";
            }
            else
            {
                dir = modsDir;
                string[] pack = PackVersions(dir);
                mc = pack[0] ?? gameVersion;
                nf = pack[1];
                where = "the game";
            }
            if (!Directory.Exists(dir)) throw new UserError("The mods folder " + dir + " doesn't exist.");
            var jars = new DirectoryInfo(dir).GetFiles().Where(f => JarName.IsMatch(f.Name)).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var metas = jars.ToDictionary(f => f.Name, f => GetJarMeta(f));
            var enabled = jars.Where(f => !f.Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)).ToList();
            var disabled = jars.Where(f => f.Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)).ToList();

            Dictionary<string, List<Provider>> providers = ProviderMap(enabled, metas);
            Dictionary<string, List<Provider>> offProviders = ProviderMap(disabled, metas);
            var builtin = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            builtin["minecraft"] = mc;
            if (loader == "neoforge") builtin["neoforge"] = nf;
            bool connector = providers.ContainsKey("connector");
            Dictionary<string, List<Provider>> serverProviders = null; // read only when something is missing

            var issues = new List<object>();
            // The same mod twice: NeoForge stops with "duplicate mods found".
            foreach (KeyValuePair<string, List<Provider>> p in providers)
            {
                List<string> files = p.Value.Where(x => !x.Nested).Select(x => x.File).Distinct().ToList();
                if (files.Count < 2) continue;
                issues.Add(Issue("error", "duplicate", "Two jars contain the same mod (" + p.Key + "), and the game refuses to start with both.",
                    files, Obj("action", "disable-one", "files", files)));
            }

            foreach (FileInfo jar in enabled)
            {
                JarMeta meta = metas[jar.Name];
                string owner = meta.Mods.Count > 0 ? meta.Mods[0].Name : jar.Name;
                if (meta.Broken)
                {
                    issues.Add(Issue("error", "broken", jar.Name + " isn't a readable jar (it may be a broken download).", new[] { jar.Name }, null));
                    continue;
                }
                if ((meta.Kind == "fabric" || meta.Kind == "quilt") && loader == "neoforge" && !connector)
                {
                    issues.Add(Issue("error", "wrong-loader", owner + " is a " + (meta.Kind == "fabric" ? "Fabric" : "Quilt") + " mod. NeoForge can only load it through Sinytra Connector, which isn't installed.",
                        new[] { jar.Name }, Obj("action", "search", "query", "Sinytra Connector")));
                    continue;
                }
                if (meta.Kind == "forge" && loader == "neoforge" && CompareVersions(mc, "1.20.5") >= 0)
                {
                    issues.Add(Issue("warning", "wrong-loader", owner + " looks like a Forge mod (it has no neoforge.mods.toml). NeoForge for Minecraft " + mc + " skips it.",
                        new[] { jar.Name }, null));
                    continue;
                }
                if (meta.Kind != "neoforge" && meta.Kind != "forge") continue;

                foreach (DepInfo dep in meta.Deps)
                {
                    if (dep.Side == "CLIENT" && side == "server" || dep.Side == "SERVER" && side == "client") continue;
                    if (meta.Mods.Any(m => m.Id.Equals(dep.Id, StringComparison.OrdinalIgnoreCase))) continue;
                    string have;
                    if (builtin.TryGetValue(dep.Id, out have))
                    {
                        bool isMc = dep.Id.Equals("minecraft", StringComparison.OrdinalIgnoreCase);
                        if (dep.Type == "incompatible" || dep.Type == "discouraged" || InRange(have, dep.Range)) continue;
                        if (isMc && MinecraftHotfixFits(have, dep.Range)) continue;
                        issues.Add(Issue("error", isMc ? "wrong-minecraft" : "wrong-loader-version",
                            isMc ? owner + " is made for Minecraft " + DescribeRange(dep.Range) + ", not " + mc + "."
                                 : owner + " needs NeoForge " + DescribeRange(dep.Range) + ", and " + where + " has " + (have ?? "an unknown version") + ".",
                            new[] { jar.Name }, null));
                        continue;
                    }
                    List<Provider> found;
                    providers.TryGetValue(dep.Id, out found);
                    bool present = found != null && found.Count > 0;
                    bool fits = present && found.Any(p => InRange(p.Version, dep.Range));
                    string depName = present ? found[0].Name : dep.Id;
                    if (dep.Type == "incompatible")
                    {
                        if (fits) issues.Add(Issue("error", "incompatible", owner + " doesn't work together with " + depName + ".",
                            new[] { jar.Name }.Concat(found.Where(p => !p.Nested).Select(p => p.File)).Distinct().ToList(), null));
                        continue;
                    }
                    if (dep.Type == "discouraged")
                    {
                        if (fits) issues.Add(Issue("warning", "discouraged", owner + " advises against using " + depName + " alongside it.",
                            new[] { jar.Name }.Concat(found.Where(p => !p.Nested).Select(p => p.File)).Distinct().ToList(), null));
                        continue;
                    }
                    if (present && !fits)
                    {
                        Provider p = found[0];
                        issues.Add(Issue("error", "dep-version", owner + " needs " + p.Name + " " + DescribeRange(dep.Range) + ", but " + (p.Version ?? "another version") +
                            " is installed" + (p.Nested ? " (bundled inside " + p.File + ")" : "") + ".", new[] { jar.Name, p.File }.Distinct().ToList(), null));
                        continue;
                    }
                    if (present || dep.Type != "required") continue;
                    List<Provider> off, onServer = null;
                    if (side == "client" && serverProviders == null) serverProviders = ServerProviders();
                    if (offProviders.TryGetValue(dep.Id, out off) && off.Count > 0)
                        issues.Add(Issue("error", "missing", owner + " needs " + off[0].Name + ", which is turned off.", new[] { jar.Name, off[0].File }.Distinct().ToList(),
                            Obj("action", "enable", "file", off[0].File)));
                    else if (serverProviders != null && serverProviders.TryGetValue(dep.Id, out onServer) && onServer.Any(p => !p.Nested && InRange(p.Version, dep.Range)))
                    {
                        Provider p = onServer.First(x => !x.Nested && InRange(x.Version, dep.Range));
                        issues.Add(Issue("error", "missing", owner + " needs " + p.Name + ", which isn't installed in the game. The server has it (" + p.File + ").",
                            new[] { jar.Name }, Obj("action", "copy-from-server", "file", p.File)));
                    }
                    else
                        issues.Add(Issue("error", "missing", owner + " needs " + dep.Id + (dep.Range != null && dep.Range != "*" ? " (" + DescribeRange(dep.Range) + ")" : "") + ", which isn't installed.",
                            new[] { jar.Name }, Obj("action", "search", "query", dep.Id)));
                }
            }
            int errors = issues.Count(i => (string)((Dictionary<string, object>)i)["level"] == "error");
            return Obj("ok", true, "side", side, "folder", dir, "minecraft", mc, "neoforge", nf, "jars", jars.Count, "enabled", enabled.Count,
                "errors", errors, "warnings", issues.Count - errors, "issues", issues);
        }

        static Dictionary<string, List<Provider>> ServerProviders()
        {
            try
            {
                string dir = serverDir == null ? null : Path.Combine(serverDir, "mods");
                if (dir == null || !Directory.Exists(dir)) return new Dictionary<string, List<Provider>>();
                List<FileInfo> jars = new DirectoryInfo(dir).GetFiles("*.jar").ToList();
                return ProviderMap(jars, jars.ToDictionary(f => f.Name, f => GetJarMeta(f)));
            }
            catch { return new Dictionary<string, List<Provider>>(); }
        }

        // The fix for "needs X, which the server has": copies the server's jar into the game's mods folder
        // and tags it client + server.
        static Dictionary<string, object> CopyFromServer(string name)
        {
            string serverMods = Path.Combine(RequireServerDir(), "mods");
            if (string.IsNullOrEmpty(name) || name != Path.GetFileName(name) || !name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                throw new UserError("That isn't a jar name.");
            string from = Path.Combine(serverMods, name);
            if (!File.Exists(from)) throw new UserError(name + " isn't in the server's mods folder any more.");
            lock (FileLock)
            {
                string to = Path.Combine(modsDir, name);
                if (File.Exists(to) || File.Exists(to + ".disabled")) throw new UserError(name + " is already in the game's mods folder.");
                File.Copy(from, to + ".copying", true);
                File.Move(to + ".copying", to);
                Log("Copied    " + name + " from the server's mods folder");
            }
            try { UpdateTags(new List<string> { name }, new List<string> { "client", "server" }, new List<string>()); } catch (UserError) { }
            return Obj("ok", true, "file", name);
        }

        static Dictionary<string, List<Provider>> ProviderMap(List<FileInfo> jars, Dictionary<string, JarMeta> metas)
        {
            var map = new Dictionary<string, List<Provider>>(StringComparer.OrdinalIgnoreCase);
            foreach (FileInfo jar in jars)
            {
                JarMeta meta = metas[jar.Name];
                foreach (ModInfo m in meta.Mods) AddProvider(map, m, jar.Name, false);
                foreach (ModInfo m in meta.Nested) AddProvider(map, m, jar.Name, true);
            }
            return map;
        }

        static void AddProvider(Dictionary<string, List<Provider>> map, ModInfo mod, string file, bool nested)
        {
            List<Provider> list;
            if (!map.TryGetValue(mod.Id, out list)) map[mod.Id] = list = new List<Provider>();
            list.Add(new Provider { File = file, Version = mod.Version, Name = mod.Name ?? mod.Id, Nested = nested });
        }

        static Dictionary<string, object> Issue(string level, string kind, string message, IEnumerable<string> files, Dictionary<string, object> fix)
        {
            return Obj("level", level, "kind", kind, "message", message, "files", files.ToList(), "fix", fix);
        }

        // The mod ids each enabled jar needs (required, for the given side), as jar names: used to keep
        // libraries switched on while their users are on, e.g. when finding a problem mod.
        static Dictionary<string, HashSet<string>> RequiredJars(string dir, IEnumerable<FileInfo> enabledJars, string side)
        {
            List<FileInfo> jars = enabledJars.ToList();
            var metas = jars.ToDictionary(f => f.Name, f => GetJarMeta(f));
            Dictionary<string, List<Provider>> providers = ProviderMap(jars, metas);
            var needs = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (FileInfo jar in jars)
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (DepInfo dep in metas[jar.Name].Deps)
                {
                    if (dep.Type != "required" || dep.Side == "CLIENT" && side == "server" || dep.Side == "SERVER" && side == "client") continue;
                    List<Provider> found;
                    if (!providers.TryGetValue(dep.Id, out found)) continue;
                    foreach (Provider p in found) if (!p.File.Equals(jar.Name, StringComparison.OrdinalIgnoreCase)) set.Add(p.File);
                }
                needs[jar.Name] = set;
            }
            return needs;
        }
    }
}
