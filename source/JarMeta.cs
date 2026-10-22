// What's inside a jar (mod ids, versions, the mods it needs, mods bundled inside it), read from
// META-INF/neoforge.mods.toml (or mods.toml / fabric.mod.json), and the pre-launch check built on it:
// missing or turned-off required mods, wrong versions, the same mod twice (Duplicates.cs), mods for another
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
        public string Logo; // path of its logo inside the jar, if it names one
    }

    sealed class DepInfo
    {
        public string Owner, Id, Type, Range, Side; // Type: required | optional | incompatible | discouraged
        public bool OldOptional; // "mandatory = false" in a neoforge.mods.toml: meant as optional, but NeoForge reads it as required
    }

    sealed class JarMeta
    {
        public string Kind = "none"; // neoforge | forge | fabric | quilt | none (a plain library)
        public readonly List<ModInfo> Mods = new List<ModInfo>();
        public readonly List<DepInfo> Deps = new List<DepInfo>();
        public readonly List<ModInfo> Nested = new List<ModInfo>(); // mods bundled inside (jar-in-jar)
        public readonly List<DepInfo> NestedDeps = new List<DepInfo>(); // what those bundled mods require
        public bool Broken;
        public string Side, SideReason; // "client" or "server" when the jar itself says it belongs on one side only
        public bool NoServerNeeded;     // displayTest IGNORE_SERVER_VERSION: a hint at client-only, but server-side mods use it too

        // Everything the game needs for this jar to load: its own requirements and those of the mods bundled inside
        // it (Create Aeronautics is a shell whose bundled parts need Create, Flywheel and Sable), less what the jar
        // carries itself.
        public IEnumerable<DepInfo> AllDeps()
        {
            return Deps.Concat(NestedDeps.Where(d => d.Id != null && !Mods.Concat(Nested).Any(m => string.Equals(m.Id, d.Id, StringComparison.OrdinalIgnoreCase))));
        }
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
                List<KeyValuePair<string, Dictionary<string, string>>> sections = ParseToml(toml);
                string topLogo = Val(sections[0].Value, "logoFile"); // some jars name it once for all their mods
                if (string.Equals(Val(sections[0].Value, "clientSideOnly"), "true", StringComparison.OrdinalIgnoreCase))
                {
                    meta.Side = "client";
                    meta.SideReason = "its " + (kind == "forge" ? "mods.toml" : "neoforge.mods.toml") + " says clientSideOnly";
                }
                var tomlDeps = new List<DepInfo>();
                var tomlMods = new HashSet<string>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, Dictionary<string, string>> section in sections)
                {
                    Dictionary<string, string> v = section.Value;
                    if (section.Key == "mods")
                    {
                        string id = Val(v, "modId");
                        if (id == null) continue;
                        tomlMods.Add(id);
                        // IGNORE_SERVER_VERSION only says the server needn't have it. Client-only mods set it, but so
                        // do some server-side ones (Axes Are Weapons), so it's a hint, not an answer.
                        if (string.Equals(Val(v, "displayTest"), "IGNORE_SERVER_VERSION", StringComparison.OrdinalIgnoreCase))
                            meta.NoServerNeeded = true;
                        string version = Val(v, "version");
                        if (version != null && version.Contains("${file.jarVersion}")) version = jarVersion;
                        if (version != null && version.Contains("${")) version = null;
                        meta.Mods.Add(new ModInfo { Id = id, Version = version, Name = Val(v, "displayName") ?? id, Description = Val(v, "description"), Logo = Val(v, "logoFile") ?? topLogo });
                    }
                    else if (section.Key.StartsWith("dependencies.", StringComparison.OrdinalIgnoreCase))
                    {
                        string id = Val(v, "modId");
                        if (id == null) continue;
                        string type = Val(v, "type");
                        bool oldOptional = false;
                        if (type == null)
                        {
                            // Old Forge's "mandatory". NeoForge doesn't read it: without a "type" its dependency is
                            // required, so "mandatory = false" there still stops the game (Let's Do Camping and Curios;
                            // FancyModLoader 4's ModVersion only reads "type", defaulting to REQUIRED).
                            string mandatory = Val(v, "mandatory");
                            type = kind == "neoforge" || mandatory == null || mandatory == "true" ? "required" : "optional";
                            oldOptional = kind == "neoforge" && mandatory == "false";
                        }
                        tomlDeps.Add(new DepInfo
                        {
                            Owner = section.Key.Substring("dependencies.".Length).Trim().Trim('"', '\''),
                            Id = id,
                            Type = type.ToLowerInvariant(),
                            Range = Val(v, "versionRange"),
                            Side = (Val(v, "side") ?? "BOTH").ToUpperInvariant(),
                            OldOptional = oldOptional
                        });
                    }
                }
                // The loader reads [[dependencies.<id>]] only for the mods this file declares. Blocks for another id are
                // leftovers from a template (Mobs Use Shields and NeuroLib carry Survivor's Delight's, which needs
                // Farmer's Delight) and never stop the game.
                meta.Deps.AddRange(tomlDeps.Where(d => tomlMods.Contains(d.Owner)));
                // Modrinth's generated datapack jars write their mod as one inline table:
                // mods = [ { modId = '...', version = '...', displayName = '...' } ]
                if (meta.Mods.Count == 0)
                {
                    Match inline = Regex.Match(toml, @"(?s)\bmods\s*=\s*\[\s*\{(.*?)\}");
                    if (inline.Success)
                    {
                        Func<string, string> field = k =>
                        {
                            Match f = Regex.Match(inline.Groups[1].Value, @"\b" + k + @"\s*=\s*[""']([^""']*)[""']");
                            return f.Success && f.Groups[1].Value.Length > 0 ? f.Groups[1].Value : null;
                        };
                        string id = field("modId");
                        if (id != null) meta.Mods.Add(new ModInfo { Id = id, Version = field("version"), Name = field("displayName") ?? id, Description = field("description"), Logo = field("logoFile") });
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
                    string environment = fabric != null ? S(json, "environment") : null;
                    if (environment == "client" || environment == "server")
                    {
                        meta.Side = environment;
                        meta.SideReason = "its fabric.mod.json says environment \"" + environment + "\"";
                    }
                    // Fabric's "depends" (id -> version spec): kept without a range, since Fabric's specs
                    // aren't Maven ranges. Used by Detect to send libraries wherever their users go.
                    string ownId = S(json, "id");
                    Dictionary<string, object> depends = D(Get(json, "depends"));
                    if (depends != null && ownId != null && top)
                        foreach (string depId in depends.Keys)
                            meta.Deps.Add(new DepInfo { Owner = ownId, Id = depId, Type = "required", Side = environment == "client" ? "CLIENT" : environment == "server" ? "SERVER" : "BOTH" });
                    string id = S(json, "id");
                    if (id != null) meta.Mods.Add(new ModInfo { Id = id, Version = S(json, "version"), Name = S(json, "name") ?? S(D(Get(json, "metadata")), "name") ?? id,
                        Logo = FabricIcon(Get(json, "icon") ?? Get(D(Get(json, "metadata")), "icon")) });
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

        // Fabric / Quilt name one icon, or several by size ({"16": ..., "128": ...}): the one nearest 128 px.
        static string FabricIcon(object icon)
        {
            if (icon is string) return (string)icon;
            Dictionary<string, object> sizes = D(icon);
            if (sizes == null) return null;
            int best = -1;
            string path = null;
            foreach (KeyValuePair<string, object> kv in sizes)
            {
                int size;
                if (!int.TryParse(kv.Key, out size) || !(kv.Value is string)) continue;
                if (path == null || Math.Abs(size - 128) < Math.Abs(best - 128)) { best = size; path = (string)kv.Value; }
            }
            return path;
        }

        // The logo a jar names for its own mod, as image bytes, or null (no logo, not an image, or too big).
        static byte[] JarLogo(FileInfo file, out string type)
        {
            type = null;
            ModInfo main = GetJarMeta(file).Mods.FirstOrDefault();
            string logo = main == null || main.Logo == null ? null : main.Logo.Replace('\\', '/').TrimStart('/');
            if (logo == null) return null;
            string ext = Path.GetExtension(logo).ToLowerInvariant();
            type = ext == ".png" ? "image/png" : ext == ".jpg" || ext == ".jpeg" ? "image/jpeg" : ext == ".gif" ? "image/gif" : null;
            if (type == null) return null;
            try
            {
                using (var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    ZipArchiveEntry entry = zip.GetEntry(logo);
                    if (entry == null || entry.Length > 2 * 1024 * 1024) return null;
                    using (Stream s = entry.Open())
                    using (var copy = new MemoryStream())
                    {
                        s.CopyTo(copy);
                        return copy.ToArray();
                    }
                }
            }
            catch { return null; }
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

        static void ReadNested(ZipArchive zip, JarMeta meta, List<string> paths, int level = 1)
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
                    using (var nested = new ZipArchive(copy, ZipArchiveMode.Read))
                    {
                        ReadJar(nested, inner, false);
                        // Jars inside jars inside jars: LambDynamicLights carries its NeoForge build, which carries its
                        // API and SpruceUI. What those provide counts, or the build's own requirements look unmet.
                        if (level < 3)
                        {
                            var deeper = JarJarPaths(nested);
                            foreach (ZipArchiveEntry e in nested.Entries)
                                if (e.FullName.StartsWith("META-INF/jars/", StringComparison.Ordinal) && e.FullName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) && !deeper.Contains(e.FullName))
                                    deeper.Add(e.FullName);
                            ReadNested(nested, inner, deeper, level + 1);
                        }
                    }
                    meta.Nested.AddRange(inner.Mods);
                    meta.Nested.AddRange(inner.Nested);
                    meta.NestedDeps.AddRange(inner.Deps);
                    meta.NestedDeps.AddRange(inner.NestedDeps);
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
            var missingSeen = new Dictionary<string, MissingNeed>(StringComparer.OrdinalIgnoreCase);
            // The same mod twice (on or off): one issue per mod, with the copy to keep already picked (Duplicates.cs).
            List<DupGroup> dupes = FindDuplicates(jars, metas, mc);
            foreach (DupGroup g in dupes) issues.Add(DuplicateIssue(g, side == "client"));
            // Two different jars that both provide one mod id (one lists the other's id as an alias):
            // NeoForge stops with "duplicate mods found", but which to drop is the user's call.
            foreach (KeyValuePair<string, List<Provider>> p in providers)
            {
                List<string> files = p.Value.Where(x => !x.Nested).Select(x => x.File).Distinct().ToList();
                if (files.Count < 2 || dupes.Any(g => files.All(f => g.Copies.Any(c => c.File.Name == f)))) continue;
                issues.Add(Issue("error", "duplicate", "Two jars contain the same mod (" + p.Key + "), and the game refuses to start with both.",
                    files, Obj("action", "disable-one", "files", files)));
            }

            foreach (FileInfo jar in enabled)
            {
                JarMeta meta = metas[jar.Name];
                string jarOwner = meta.Mods.Count > 0 ? meta.Mods[0].Name : jar.Name;
                if (meta.Broken)
                {
                    issues.Add(Issue("error", "broken", jar.Name + " isn't a readable jar (it may be a broken download).", new[] { jar.Name }, null));
                    continue;
                }
                if ((meta.Kind == "fabric" || meta.Kind == "quilt") && loader == "neoforge" && !connector)
                {
                    issues.Add(Issue("error", "wrong-loader", jarOwner + " is a " + (meta.Kind == "fabric" ? "Fabric" : "Quilt") + " mod. NeoForge can only load it through Sinytra Connector, which isn't installed.",
                        new[] { jar.Name }, Obj("action", "search", "query", "Sinytra Connector")));
                    continue;
                }
                if (meta.Kind == "forge" && loader == "neoforge" && CompareVersions(mc, "1.20.5") >= 0)
                {
                    issues.Add(Issue("warning", "wrong-loader", jarOwner + " looks like a Forge mod (it has no neoforge.mods.toml). NeoForge for Minecraft " + mc + " skips it.",
                        new[] { jar.Name }, null));
                    continue;
                }
                if (meta.Kind != "neoforge" && meta.Kind != "forge") continue;

                // A bundled mod that's also installed on its own is checked from its own jar (the newest copy loads).
                var checks = meta.Deps.Select(d => new KeyValuePair<DepInfo, string>(d, jarOwner)).ToList();
                foreach (DepInfo d in meta.AllDeps().Skip(meta.Deps.Count))
                {
                    List<Provider> standalone;
                    if (providers.TryGetValue(d.Owner ?? "", out standalone) && standalone.Any(p => !p.Nested)) continue;
                    ModInfo part = meta.Nested.FirstOrDefault(m => string.Equals(m.Id, d.Owner, StringComparison.OrdinalIgnoreCase));
                    string partName = part != null ? part.Name ?? part.Id : d.Owner;
                    checks.Add(new KeyValuePair<DepInfo, string>(d, partName != null && partName != jarOwner ? partName + " (bundled in " + jarOwner + ")" : jarOwner));
                }
                foreach (KeyValuePair<DepInfo, string> check in checks)
                {
                    DepInfo dep = check.Key;
                    string owner = check.Value;
                    // A server never runs client code, so client-only needs are skipped there. The other way round
                    // isn't safe: single player runs a server inside the game, so "needed on servers" counts in the game too.
                    if (dep.Side == "CLIENT" && side == "server") continue;
                    string sideNote = dep.Side == "SERVER" && side == "client" ? " It's listed as needed on servers only, but single player runs a server inside the game, so the game needs it too." : null;
                    if (dep.OldOptional)
                        sideNote += " " + owner + " marks it optional the old Forge way (mandatory = false), which NeoForge doesn't read, so the game still won't start without it.";
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
                            new[] { jar.Name }, side == "client" ? Obj("action", "version", "file", jar.Name) : null));
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
                            " is installed" + (p.Nested ? " (bundled inside " + p.File + ")" : "") + ".", new[] { jar.Name, p.File }.Distinct().ToList(),
                            side == "client" ? Obj("action", "version", "file", jar.Name, "dep", dep.Id, "provider", p.Nested ? null : p.File, "nestedIn", p.Nested ? p.File : null) : null));
                        continue;
                    }
                    if (present || dep.Type != "required") continue;
                    List<Provider> off, onServer = null;
                    if (side == "client" && serverProviders == null) serverProviders = ServerProviders();
                    // One issue per missing mod, however many mods need it (NoteMissing).
                    if (offProviders.TryGetValue(dep.Id, out off) && off.Count > 0)
                        NoteMissing(issues, missingSeen, "enable:" + off[0].File, off[0].Name, ", which is turned off", sideNote, owner, jar.Name, off[0].File,
                            Obj("action", "enable", "file", off[0].File));
                    else if (serverProviders != null && serverProviders.TryGetValue(dep.Id, out onServer) && onServer.Any(p => !p.Nested && InRange(p.Version, dep.Range)))
                    {
                        Provider p = onServer.First(x => !x.Nested && InRange(x.Version, dep.Range));
                        NoteMissing(issues, missingSeen, "copy:" + p.File, p.Name, ", which isn't installed in the game", " The server has it (" + p.File + ")." + (sideNote ?? ""), owner, jar.Name, null,
                            Obj("action", "copy-from-server", "file", p.File));
                    }
                    else
                        NoteMissing(issues, missingSeen, "install:" + dep.Id.ToLowerInvariant(),
                            dep.Id + (dep.Range != null && dep.Range != "*" ? " (" + DescribeRange(dep.Range) + ")" : ""), ", which isn't installed", sideNote, owner, jar.Name, null,
                            Obj("action", "install", "id", dep.Id, "for", jar.Name, "query", dep.Id));
                }
            }
            // What the mods' code itself needs, beyond what they declare (DeepCheck.cs). A mod already reported
            // for a missing or wrong-version requirement isn't reported again for the same gap.
            try
            {
                var already = new HashSet<string>(issues.Select(i => (Dictionary<string, object>)i)
                    .Where(i => (string)i["kind"] == "missing" || (string)i["kind"] == "dep-version")
                    .SelectMany(i => ((List<string>)i["files"]).Take(1)), StringComparer.OrdinalIgnoreCase);
                var deep = new List<object>();
                DeepCheckMods(enabled, disabled, metas, side, deep);
                issues.AddRange(deep.Where(i => !already.Contains(((List<string>)((Dictionary<string, object>)i)["files"])[0])));
            }
            catch (Exception ex) { Log("Check     the deep check failed: " + ex.Message); }
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
            TagLibraryByLookup(name, null);
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

        sealed class MissingNeed
        {
            public Dictionary<string, object> Issue;
            public string What, Why, After;
            public readonly List<string> Owners = new List<string>();
            public readonly List<string> Files = new List<string>();
        }

        // A missing mod is one problem however many mods need it: the first mod that needs it makes the issue,
        // later ones are added to its "needed by" list (and files), so counts and "Add all missing" see it once.
        static void NoteMissing(List<object> issues, Dictionary<string, MissingNeed> seen, string key, string what, string why, string after,
            string owner, string ownerFile, string extraFile, Dictionary<string, object> fix)
        {
            MissingNeed m;
            if (!seen.TryGetValue(key, out m))
            {
                m = new MissingNeed { What = what, Why = why, After = after, Issue = Issue("error", "missing", "", new string[0], fix) };
                if (extraFile != null) m.Files.Add(extraFile);
                seen[key] = m;
                issues.Add(m.Issue);
            }
            if (!m.Owners.Contains(owner)) m.Owners.Add(owner);
            if (!m.Files.Contains(ownerFile)) m.Files.Insert(m.Files.Count - (extraFile != null ? 1 : 0), ownerFile);
            string who = m.Owners.Count == 1 ? m.Owners[0]
                : m.Owners.Count <= 3 ? string.Join(", ", m.Owners.Take(m.Owners.Count - 1)) + " and " + m.Owners.Last()
                : string.Join(", ", m.Owners.Take(3)) + " and " + (m.Owners.Count - 3) + " more";
            m.Issue["message"] = m.Owners.Count == 1
                ? who + " needs " + m.What + m.Why + "." + (m.After ?? "")
                : m.What + m.Why.Replace(", which", "") + ", and " + who + " need it." + (m.After ?? ""); // "X is turned off, and A and B need it."
            m.Issue["files"] = m.Files.ToList();
            m.Issue["neededBy"] = m.Owners.ToList();
        }

        static Dictionary<string, object> Issue(string level, string kind, string message, IEnumerable<string> files, Dictionary<string, object> fix)
        {
            return Obj("level", level, "kind", kind, "message", message, "files", files.ToList(), "fix", fix);
        }

        // The mod ids each enabled jar needs (required, for the given side), as jar names: used to keep
        // libraries switched on while their users are on, e.g. when finding a problem mod.
        // Turning mods on: the turned-off jars in the folder that provide what they require (and what those
        // require in turn), when nothing that's on provides it already. Where several turned-off jars provide
        // the same mod, the newest version is picked. Returns their file names.
        static List<string> TurnedOffNeeds(List<string> files)
        {
            var jars = new DirectoryInfo(modsDir).GetFiles().Where(f => JarName.IsMatch(f.Name)).ToList();
            var metas = jars.ToDictionary(f => f.Name, f => GetJarMeta(f), StringComparer.OrdinalIgnoreCase);
            Func<string, bool> isOff = n => n.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
            Func<JarMeta, IEnumerable<ModInfo>> ids = m => m.Mods.Concat(m.Nested);
            var turningOn = new HashSet<string>(files.Where(metas.ContainsKey), StringComparer.OrdinalIgnoreCase);
            var provided = new HashSet<string>(jars.Where(f => !isOff(f.Name) || turningOn.Contains(f.Name)).SelectMany(f => ids(metas[f.Name])).Select(m => m.Id),
                StringComparer.OrdinalIgnoreCase);
            // A server's own mods folder doesn't need client-side dependencies. A game needs all of them: single player runs a server inside it.
            bool serverFolder = serverDir != null && SamePath(Path.Combine(serverDir, "mods"), modsDir);
            var added = new List<string>();
            var queue = new Queue<string>(turningOn);
            while (queue.Count > 0)
            {
                JarMeta meta = metas[queue.Dequeue()];
                foreach (DepInfo dep in meta.AllDeps())
                {
                    if (dep.Type != "required" || dep.Id == null || provided.Contains(dep.Id)) continue;
                    if (serverFolder && dep.Side == "CLIENT") continue; // single player needs "server" ones in the game too
                    string id = dep.Id;
                    FileInfo pick = jars.Where(f => isOff(f.Name) && !turningOn.Contains(f.Name) && ids(metas[f.Name]).Any(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)))
                        .OrderByDescending(f => ids(metas[f.Name]).First(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)).Version ?? "",
                            Comparer<string>.Create(CompareVersions))
                        .FirstOrDefault();
                    if (pick == null) continue; // not in the folder at all: the pre-launch check reports it
                    turningOn.Add(pick.Name);
                    added.Add(pick.Name);
                    foreach (ModInfo m in ids(metas[pick.Name])) provided.Add(m.Id);
                    queue.Enqueue(pick.Name);
                }
            }
            return added;
        }

        static Dictionary<string, HashSet<string>> RequiredJars(string dir, IEnumerable<FileInfo> enabledJars, string side)
        {
            List<FileInfo> jars = enabledJars.ToList();
            var metas = jars.ToDictionary(f => f.Name, f => GetJarMeta(f));
            Dictionary<string, List<Provider>> providers = ProviderMap(jars, metas);
            var needs = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (FileInfo jar in jars)
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (DepInfo dep in metas[jar.Name].AllDeps())
                {
                    if (dep.Type != "required" || dep.Side == "CLIENT" && side == "server") continue;
                    List<Provider> found;
                    if (!providers.TryGetValue(dep.Id, out found)) continue;
                    foreach (Provider p in found) if (!p.File.Equals(jar.Name, StringComparison.OrdinalIgnoreCase)) set.Add(p.File);
                }
                needs[jar.Name] = set;
            }
            return needs;
        }
        // Before jars are turned off or deleted: which mods that stay on require something only these jars
        // provide (their own mod ids or ones bundled inside them). Empty when nothing would break.
        static Dictionary<string, object> DependentsOf(List<string> files)
        {
            string dir = modsDir;
            var leaving = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
            var jars = new DirectoryInfo(dir).GetFiles().Where(f => JarName.IsMatch(f.Name)).ToList();
            var staying = jars.Where(f => !leaving.Contains(f.Name) && !f.Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)).ToList();
            var metas = jars.ToDictionary(f => f.Name, f => GetJarMeta(f), StringComparer.OrdinalIgnoreCase);
            var stillProvided = new HashSet<string>(staying.SelectMany(f => metas[f.Name].Mods.Concat(metas[f.Name].Nested)).Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
            var result = new List<object>();
            foreach (string file in files)
            {
                JarMeta meta;
                if (!metas.TryGetValue(file, out meta)) continue;
                var ids = new HashSet<string>(meta.Mods.Concat(meta.Nested).Select(m => m.Id).Where(id => !stillProvided.Contains(id)), StringComparer.OrdinalIgnoreCase);
                if (ids.Count == 0) continue;
                var needers = new List<string>();
                foreach (FileInfo other in staying)
                {
                    JarMeta om = metas[other.Name];
                    if (om.AllDeps().Any(d => d.Type == "required" && ids.Contains(d.Id ?? "")))
                    {
                        ModInfo main = MainMod(om);
                        needers.Add((main != null ? main.Name ?? main.Id : null) ?? other.Name);
                    }
                }
                if (needers.Count > 0)
                {
                    ModInfo self = MainMod(meta);
                    result.Add(Obj("file", file, "name", (self != null ? self.Name ?? self.Id : null) ?? file, "neededBy", needers.Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()));
                }
            }
            return Obj("ok", true, "dependents", result);
        }
    }
}
