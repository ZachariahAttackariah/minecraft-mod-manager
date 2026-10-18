// The deep part of the pre-launch check: what mods use from each other, read from their class files.
// A mod's declared dependencies only say which mods must be there, and some mods get them wrong, so this
// checks what the code itself needs:
//  - a mod's main class (the one NeoForge constructs) and its mixins use classes that no installed mod has
//    (Inline Tooltips uses the Inline library without listing it), or that are in a turned-off jar;
//  - a mod calls into another installed mod, and the class or method it calls isn't there: it was made for
//    a different version (Let Me Despawn 1.5 calls Almanac.loadConfig, gone in Almanac 1.26).
// Anything that can't be confirmed (code inherited from Minecraft, Java or a library on the game's own
// classpath) is left alone, so it only speaks up when a jar here plainly lacks what another one calls.
// Each jar's class files are read once and kept in memory (keyed by size and date).

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace MinecraftModManager
{
    static partial class Program
    {
        sealed class ClassInfo
        {
            public string Name, Super;
            public string[] Interfaces = new string[0];
            public HashSet<string> Methods = new HashSet<string>(StringComparer.Ordinal); // name + descriptor
            public bool IsModEntry;
            public List<string> MixinTargets = new List<string>();                          // from @Mixin(value / targets)
            public HashSet<string> Strings = new HashSet<string>(StringComparer.Ordinal);   // text constants ("thirst" in ModList.isLoaded("thirst"))
            public bool ChecksLoaded;                                                       // calls ModList.isLoaded (or similar) somewhere
            public HashSet<string> Uses = new HashSet<string>(StringComparer.Ordinal);     // every class it uses
        }

        sealed class ClassRef
        {
            public string From, Owner, Name, Desc; // Name/Desc null for a plain class use
        }

        sealed class JarCode
        {
            public readonly Dictionary<string, ClassInfo> Classes = new Dictionary<string, ClassInfo>(StringComparer.Ordinal);
            public readonly List<ClassRef> Refs = new List<ClassRef>();             // uses of classes outside this jar
            public readonly HashSet<string> MixinClasses = new HashSet<string>(StringComparer.Ordinal);
            public readonly HashSet<string> GuardedMixins = new HashSet<string>(StringComparer.Ordinal); // configs with a plugin that can skip them
            public readonly HashSet<string> Mentions = new HashSet<string>(StringComparer.Ordinal); // every text constant and mods.toml word, squashed (ad_astra -> adastra)
        }

        static readonly Dictionary<string, JarCode> JarCodeCache = new Dictionary<string, JarCode>(StringComparer.OrdinalIgnoreCase);

        // Packages that come with the game, the loader or Java: never "missing".
        static readonly Regex PlatformPackage = new Regex(@"^(java|javax|jdk|sun|com/sun|net/minecraft|com/mojang|net/neoforged|net/minecraftforge|cpw/mods|org/lwjgl|org/joml|com/google|org/slf4j|org/apache|io/netty|it/unimi|org/spongepowered|com/llamalad7|org/objectweb|kotlin|kotlinx|org/jetbrains|org/intellij|com/electronwill|net/jodah|org/openjdk|org/w3c|org/xml|oshi|com/ibm/icu|org/checkerframework|javassist|net/sf/jopt|org/antlr|com/github/oshi|net/fabricmc/loader|org/codehaus|org/yaml|com/machinezoo|ca/weblite|org/lz4|com/typesafe|org/tukaani|net/java)/");

        // Fabric's intermediary names, which Connector maps to real ones as the game loads.
        static readonly HashSet<string> CommonPackageWords = new HashSet<string>(StringComparer.Ordinal) { "com", "net", "org", "dev", "api", "client", "common", "core", "util", "utils", "mixin", "mixins", "impl", "init", "item", "items", "block", "blocks", "entity", "world", "config", "content", "data", "neoforge", "forge", "fabric", "main", "mod", "mods", "lib", "github", "gitlab" };
        static readonly Regex Intermediary = new Regex(@"(^|[/$])(class|method|field|comp)_\d+");

        static HashSet<string> libraryRoots;

        // Package roots (two levels) of every library the launcher keeps: on the game's classpath, so not "missing".
        static HashSet<string> LibraryRoots()
        {
            if (libraryRoots != null) return libraryRoots;
            var roots = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                string dir = Path.Combine(PrismDataDir(), "libraries");
                if (Directory.Exists(dir))
                    foreach (string jar in Directory.EnumerateFiles(dir, "*.jar", SearchOption.AllDirectories))
                        try
                        {
                            using (ZipArchive zip = ZipFile.OpenRead(jar))
                                foreach (ZipArchiveEntry e in zip.Entries)
                                    if (e.FullName.EndsWith(".class", StringComparison.Ordinal)) { string r = Root(e.FullName); if (r != null) roots.Add(r); }
                        }
                        catch (Exception) { }
            }
            catch (Exception) { }
            return libraryRoots = roots;
        }

        static string Root(string internalName)
        {
            // Three levels where there are (io/github is shared by many projects; com/samsthenerd/inline is one).
            string[] parts = internalName.Split('/');
            return parts.Length >= 4 ? parts[0] + "/" + parts[1] + "/" + parts[2] : parts.Length == 3 ? parts[0] + "/" + parts[1] : null;
        }

        // A mod id or package word with case, _ and - removed, so ad_astra and adastra match.
        static string Squash(string s) { return Regex.Replace(s.ToLowerInvariant(), @"[_\-]", ""); }

        static string Dotted(string internalName) { return internalName.Replace('/', '.'); }
        static string ShortClass(string internalName) { string n = internalName.Substring(internalName.LastIndexOf('/') + 1); return n.Replace('$', '.'); }

        // ---------- Reading class files ----------

        static ClassInfo ReadClass(byte[] b, Action<ClassRef> onRef)
        {
            int p = 0;
            Func<int> u2 = () => { int v = (b[p] << 8) | b[p + 1]; p += 2; return v; };
            Func<int> u4 = () => { int v = (b[p] << 24) | (b[p + 1] << 16) | (b[p + 2] << 8) | b[p + 3]; p += 4; return v; };
            if (b.Length < 10 || u4() != unchecked((int)0xCAFEBABE)) return null;
            p += 4; // version
            int count = u2();
            var utf = new string[count];
            var cls = new int[count];       // Class -> name index
            var refs = new int[count, 2];   // Methodref/InterfaceMethodref -> class index, name-and-type index
            var nat = new int[count, 2];    // NameAndType -> name, descriptor
            var kind = new byte[count];
            var str = new int[count];       // String -> utf index
            for (int i = 1; i < count; i++)
            {
                byte tag = b[p++];
                kind[i] = tag;
                switch (tag)
                {
                    case 1: { int len = u2(); utf[i] = Encoding.UTF8.GetString(b, p, len); p += len; break; }
                    case 7: cls[i] = u2(); break;
                    case 9: case 10: case 11: refs[i, 0] = u2(); refs[i, 1] = u2(); break;
                    case 12: nat[i, 0] = u2(); nat[i, 1] = u2(); break;
                    case 3: case 4: case 17: case 18: p += 4; break;
                    case 5: case 6: p += 8; i++; break;
                    case 8: str[i] = u2(); break;
                    case 16: case 19: case 20: p += 2; break;
                    case 15: p += 3; break;
                    default: return null;
                }
            }
            p += 2; // access flags
            var info = new ClassInfo();
            int self = u2(), super = u2();
            info.Name = utf[cls[self]];
            info.Super = super == 0 ? null : utf[cls[super]];
            int ifaces = u2();
            info.Interfaces = new string[ifaces];
            for (int i = 0; i < ifaces; i++) info.Interfaces[i] = utf[cls[u2()]];
            var memberDescs = new List<string>();
            for (int pass = 0; pass < 2; pass++) // fields, then methods
            {
                int n = u2();
                for (int i = 0; i < n; i++)
                {
                    p += 2;
                    int name = u2(), desc = u2();
                    if (pass == 1) info.Methods.Add(utf[name] + utf[desc]);
                    if (utf[desc] != null) memberDescs.Add(utf[desc]);
                    int attrs = u2();
                    // (Not "p += u4()": that reads p before u4 moves it on.)
                    for (int a = 0; a < attrs; a++) { p += 2; int length = u4(); p += length; }
                }
            }
            // Class attributes: the @Mixin annotation names what the mixin changes.
            int classAttrs = u2();
            for (int a = 0; a < classAttrs; a++)
            {
                string attr = utf[u2()];
                int length = u4(), end = p + length;
                if (attr == "RuntimeInvisibleAnnotations" || attr == "RuntimeVisibleAnnotations")
                {
                    int q = p, n = (b[q] << 8) | b[q + 1];
                    q += 2;
                    for (int k = 0; k < n; k++) q = ReadAnnotation(b, q, utf, info, false);
                }
                p = end;
            }
            for (int i = 1; i < count; i++) if (kind[i] == 8 && utf[str[i]] != null) info.Strings.Add(utf[str[i]]);
            for (int i = 1; i < count; i++)
                if ((kind[i] == 10 || kind[i] == 11) && Regex.IsMatch(utf[nat[refs[i, 1], 0]] ?? "", @"^(isLoaded|isModLoaded|getModContainerById|isModPresent)$")) info.ChecksLoaded = true;
            // @Mod is marked by its annotation descriptor in the constant pool.
            for (int i = 1; i < count; i++) if (kind[i] == 1 && utf[i] == "Lnet/neoforged/fml/common/Mod;") info.IsModEntry = true;
            // Every class this one uses: class constants, and the types in its own and its calls' signatures
            // (the JVM loads those too when it checks the code: that's how Inline Tooltips failed).
            for (int i = 1; i < count; i++)
            {
                if (kind[i] == 7 && utf[cls[i]] != null && !utf[cls[i]].StartsWith("[")) info.Uses.Add(utf[cls[i]]);
                if (kind[i] == 12 && utf[nat[i, 1]] != null) memberDescs.Add(utf[nat[i, 1]]);
            }
            foreach (string d in memberDescs)
                foreach (Match t in Regex.Matches(d, @"L([^;<]+);")) info.Uses.Add(t.Groups[1].Value);
            if (onRef != null)
                foreach (string used in info.Uses) if (used != info.Name) onRef(new ClassRef { From = info.Name, Owner = used });
            if (onRef != null)
                for (int i = 1; i < count; i++)
                {
                    if (kind[i] == 7) continue; // already in Uses
                    else if (kind[i] == 10 || kind[i] == 11)
                    {
                        string owner = utf[cls[refs[i, 0]]];
                        if (owner == null || owner.StartsWith("[")) continue;
                        int nt = refs[i, 1];
                        onRef(new ClassRef { From = info.Name, Owner = owner, Name = utf[nat[nt, 0]], Desc = utf[nat[nt, 1]] });
                    }
                }
            return info;
        }

        // One annotation (JVM spec 4.7.16); for @Mixin, its class values and target names are kept.
        static int ReadAnnotation(byte[] b, int p, string[] utf, ClassInfo info, bool inMixin)
        {
            string type = utf[(b[p] << 8) | b[p + 1]];
            int pairs = (b[p + 2] << 8) | b[p + 3];
            p += 4;
            bool mixin = type == "Lorg/spongepowered/asm/mixin/Mixin;";
            for (int i = 0; i < pairs; i++)
            {
                p += 2; // element name
                p = ReadElement(b, p, utf, info, mixin);
            }
            return p;
        }

        static int ReadElement(byte[] b, int p, string[] utf, ClassInfo info, bool collect)
        {
            char tag = (char)b[p++];
            int index = (b[p] << 8) | b[p + 1];
            switch (tag)
            {
                case 's':
                    if (collect && utf[index] != null) info.MixinTargets.Add(utf[index].Replace('.', '/'));
                    return p + 2;
                case 'c':
                    if (collect && utf[index] != null) info.MixinTargets.Add(Regex.Replace(utf[index], @"^L|;$", ""));
                    return p + 2;
                case 'e': return p + 4;
                case '@': return ReadAnnotation(b, p, utf, info, false);
                case '[':
                    p += 2;
                    for (int i = 0; i < index; i++) p = ReadElement(b, p, utf, info, collect);
                    return p;
                default: return p + 2; // B C D F I J S Z: a constant
            }
        }

        static byte[] ReadAll(Stream s)
        {
            using (var ms = new MemoryStream()) { s.CopyTo(ms); return ms.ToArray(); }
        }

        static void ReadJarCode(ZipArchive zip, JarCode code, bool nested, string side)
        {
            var pending = new List<ClassRef>();
            foreach (ZipArchiveEntry e in zip.Entries)
            {
                string n = e.FullName;
                if (n.EndsWith(".class", StringComparison.Ordinal) && !n.StartsWith("META-INF/", StringComparison.Ordinal))
                {
                    ClassInfo info;
                    try { using (Stream s = e.Open()) info = ReadClass(ReadAll(s), nested ? null : (Action<ClassRef>)(r => pending.Add(r))); }
                    catch (Exception) { continue; }
                    if (info != null && info.Name != null && !code.Classes.ContainsKey(info.Name)) code.Classes[info.Name] = info;
                }
                else if (n.StartsWith("META-INF/", StringComparison.Ordinal) && n.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) // jarjar/, jars/...
                {
                    try
                    {
                        using (Stream s = e.Open())
                        using (var ms = new MemoryStream(ReadAll(s)))
                        using (var inner = new ZipArchive(ms, ZipArchiveMode.Read)) ReadJarCode(inner, code, true, side);
                    }
                    catch (Exception) { }
                }
            }
            if (nested) return;
            // Every mod id this jar mentions anywhere (ModList.isLoaded("thirst"), compat registries, its mods.toml).
            foreach (ClassInfo c in code.Classes.Values)
                foreach (string s in c.Strings)
                    if (s.Length >= 2 && s.Length <= 64) code.Mentions.Add(Squash(s));
            try
            {
                ZipArchiveEntry tomlEntry = zip.GetEntry("META-INF/neoforge.mods.toml") ?? zip.GetEntry("META-INF/mods.toml");
                if (tomlEntry != null)
                    using (var reader = new StreamReader(tomlEntry.Open()))
                        // Only mod ids (its own and its dependencies'): a description's words prove nothing.
                        foreach (Match w in Regex.Matches(reader.ReadToEnd(), @"modId\s*=\s*""([^""]+)""")) code.Mentions.Add(Squash(w.Groups[1].Value));
            }
            catch (Exception) { }
            // Uses of classes outside this jar (and outside Java, Minecraft and the loader).
            foreach (ClassRef r in pending)
                if (!code.Classes.ContainsKey(r.Owner) && !PlatformPackage.IsMatch(r.Owner)) code.Refs.Add(r);
            // The mixins this jar applies on the given side, from its mixin configs.
            var configs = new List<string>();
            try
            {
                ZipArchiveEntry toml = zip.GetEntry("META-INF/neoforge.mods.toml") ?? zip.GetEntry("META-INF/mods.toml");
                if (toml != null)
                    using (var reader = new StreamReader(toml.Open()))
                        foreach (Match m in Regex.Matches(reader.ReadToEnd(), @"\[\[mixins\]\]\s*config\s*=\s*""([^""]+)"""))
                            configs.Add(m.Groups[1].Value);
                ZipArchiveEntry manifest = zip.GetEntry("META-INF/MANIFEST.MF");
                if (manifest != null)
                    using (var reader = new StreamReader(manifest.Open()))
                    {
                        Match m = Regex.Match(reader.ReadToEnd(), @"MixinConfigs:\s*([^\r\n]+)");
                        if (m.Success) configs.AddRange(m.Groups[1].Value.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0));
                    }
            }
            catch (Exception) { }
            var json = new JavaScriptSerializer();
            foreach (string config in configs.Distinct())
            {
                try
                {
                    ZipArchiveEntry entry = zip.GetEntry(config);
                    if (entry == null) continue;
                    Dictionary<string, object> c;
                    using (var reader = new StreamReader(entry.Open())) c = json.DeserializeObject(reader.ReadToEnd()) as Dictionary<string, object>;
                    string package = (S(c, "package") ?? "").Replace('.', '/');
                    bool guarded = !string.IsNullOrEmpty(S(c, "plugin"));
                    foreach (string list in new[] { "mixins", side == "server" ? "server" : "client" })
                        foreach (object m in A(Get(c, list)))
                        {
                            string name = package + "/" + Convert.ToString(m).Replace('.', '/');
                            code.MixinClasses.Add(name);
                            if (guarded) code.GuardedMixins.Add(name);
                        }
                }
                catch (Exception) { }
            }
        }

        static JarCode GetJarCode(FileInfo jar, string side)
        {
            string key = jar.FullName + "|" + jar.Length + "|" + jar.LastWriteTimeUtc.Ticks + "|" + side;
            lock (JarCodeCache)
            {
                JarCode cached;
                if (JarCodeCache.TryGetValue(key, out cached)) return cached;
            }
            var code = new JarCode();
            try { using (ZipArchive zip = ZipFile.OpenRead(jar.FullName)) ReadJarCode(zip, code, false, side); }
            catch (Exception) { }
            lock (JarCodeCache) JarCodeCache[key] = code;
            return code;
        }

        // ---------- The checks ----------

        static void DeepCheckMods(List<FileInfo> enabled, List<FileInfo> disabled, Dictionary<string, JarMeta> metas, string side, List<object> issues)
        {
            var codes = new Dictionary<string, JarCode>(StringComparer.OrdinalIgnoreCase);
            System.Threading.Tasks.Parallel.ForEach(enabled, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 4 }, f =>
            {
                JarCode c = GetJarCode(f, side);
                lock (codes) codes[f.Name] = c;
            });
            // Which jar has each class, and which jar each package root belongs to.
            var owner = new Dictionary<string, string>(StringComparer.Ordinal);
            var copies = new Dictionary<string, List<string>>(StringComparer.Ordinal); // a library bundled in several jars
            var rootOwner = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, JarCode> kv in codes.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                foreach (string c in kv.Value.Classes.Keys)
                {
                    if (!owner.ContainsKey(c)) { owner[c] = kv.Key; copies[c] = new List<string> { kv.Key }; }
                    else copies[c].Add(kv.Key);
                    string r = Root(c);
                    if (r != null && !rootOwner.ContainsKey(r)) rootOwner[r] = kv.Key;
                }
            // Each package and the jar that has it, for "this mod's package is here, but not this class".
            var packageOwner = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, JarCode> kv in codes.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                foreach (string c in kv.Value.Classes.Keys)
                {
                    int slash = c.LastIndexOf('/');
                    if (slash > 0 && !packageOwner.ContainsKey(c.Substring(0, slash))) packageOwner[c.Substring(0, slash)] = kv.Key;
                }
            // Turned-off jars: only which package roots they hold (so "it's turned off" can be said).
            var offRoots = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (FileInfo f in disabled)
                foreach (string p in JarPackages(f))
                {
                    string r = Root(p.Replace('.', '/') + "/x");
                    if (r != null && !rootOwner.ContainsKey(r) && !offRoots.ContainsKey(r)) offRoots[r] = f.Name;
                }
            HashSet<string> libs = LibraryRoots();
            Func<string, string> nameOf = file =>
            {
                JarMeta m;
                ModInfo main = metas.TryGetValue(file, out m) ? MainMod(m) : null;
                return main != null && main.Name != null ? main.Name : file;
            };
            // Does the class (or what it extends, all inside these jars) have the method? null: can't tell.
            // With a class in several jars (a bundled library), any copy that has it counts.
            Func<string, string, bool?> hasMethod = (c, sig) =>
            {
                var seen = new HashSet<string>();
                var queue = new Queue<string>(new[] { c });
                bool unknown = false;
                while (queue.Count > 0)
                {
                    string at = queue.Dequeue();
                    if (!seen.Add(at)) continue;
                    if (at == "java/lang/Object") { if (sig.StartsWith("<", StringComparison.Ordinal) || Regex.IsMatch(sig, @"^(equals|hashCode|toString|getClass|clone|notify|notifyAll|wait|finalize)\(")) return true; continue; }
                    List<string> where;
                    if (!copies.TryGetValue(at, out where)) { unknown = true; continue; } // inherits from code outside these jars
                    foreach (string jar in where)
                    {
                        ClassInfo ci = codes[jar].Classes[at];
                        if (ci.Methods.Contains(sig)) return true;
                        if (ci.Super != null) queue.Enqueue(ci.Super);
                        foreach (string i in ci.Interfaces) queue.Enqueue(i);
                    }
                }
                return unknown ? (bool?)null : false;
            };

            var reported = new HashSet<string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, JarCode> kv in codes.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                string file = kv.Key;
                JarCode code = kv.Value;
                string who = nameOf(file);
                // The mods it declares (required or optional) and which jars provide them.
                var requiredFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var declaredFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var declaredIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                JarMeta meta;
                if (metas.TryGetValue(file, out meta))
                    foreach (DepInfo d in meta.Deps.Where(x => x.Id != null && (x.Type == "required" || x.Type == "optional")))
                    {
                        declaredIds.Add(d.Id);
                        foreach (KeyValuePair<string, JarMeta> other in metas)
                            if (other.Key != file && other.Value.Mods.Concat(other.Value.Nested).Any(m => string.Equals(m.Id, d.Id, StringComparison.OrdinalIgnoreCase)))
                            {
                                declaredFiles.Add(other.Key);
                                if (d.Type == "required") requiredFiles.Add(other.Key);
                            }
                    }

                // What runs as the mod starts: its main class and the mod's own classes that reaches in two steps
                // (compat and integration code is left out: it normally only runs when the other mod is there).
                var startup = new HashSet<string>(code.Classes.Values.Where(c => c.IsModEntry).Select(c => c.Name), StringComparer.Ordinal);
                var frontier = startup.ToList();
                for (int step = 0; step < 2 && frontier.Count > 0; step++)
                {
                    var next = new List<string>();
                    foreach (string c in frontier)
                        foreach (string u in code.Classes[c].Uses)
                            if (code.Classes.ContainsKey(u) && !Regex.IsMatch(u, @"/(compat|integration|integrations|plugin|plugins)/", RegexOptions.IgnoreCase) && startup.Add(u)) next.Add(u);
                    frontier = next;
                }

                var missingHere = new List<object>();
                var missingRoots = new HashSet<string>(StringComparer.Ordinal);
                foreach (ClassRef r in code.Refs)
                {
                    // Fabric-internal names (class_1234, method_5678) are translated by Connector as the game loads.
                    if (Intermediary.IsMatch(r.Owner) || r.Name != null && (Intermediary.IsMatch(r.Name) || Intermediary.IsMatch(r.Desc ?? ""))) continue;
                    ClassInfo from;
                    code.Classes.TryGetValue(r.From, out from);
                    bool entry = startup.Contains(r.From);
                    bool mixin = code.MixinClasses.Contains(r.From);
                    string target;
                    if (owner.TryGetValue(r.Owner, out target))
                    {
                        // The class is there: does it have the method this mod calls? Only judged against the mods it
                        // declares (a partial copy bundled in some other jar proves nothing).
                        if (r.Name == null || target == file) continue;
                        string provider = copies[r.Owner].FirstOrDefault(declaredFiles.Contains);
                        if (provider == null) continue;
                        bool? has = hasMethod(r.Owner, r.Name + r.Desc);
                        if (has != false || !reported.Add("method|" + file + "|" + provider)) continue;
                        bool required = requiredFiles.Contains(provider);
                        issues.Add(Issue(required || entry || mixin ? "error" : "warning", "api-mismatch",
                            who + " was made for a different version of " + nameOf(provider) + ": it calls " + ShortClass(r.Owner) + "." + r.Name + DescribeArgs(r.Desc) +
                            ", which " + nameOf(provider) + " " + JarVersion(metas, provider) + " doesn't have." +
                            (required || entry || mixin ? "" : " (It only matters when that part of " + who + " runs, so the game may still start.)"),
                            new[] { file, provider }, Obj("action", "versions", "file", provider, "for", file)));
                        continue;
                    }
                    // A class missing from a mod that IS installed, in that mod's own package (net/satisfy/vinery in
                    // Vinery): any code for it runs, since its "is it loaded?" check passes. That's how the MineColonies
                    // compatibility addon broke on Vinery 1.5.5, which renamed RecipeTypesRegistry.
                    string installedOwner = PackageOwner(r.Owner, packageOwner);
                    if (installedOwner != null && installedOwner != file && OwnsPackage(metas, installedOwner, r.Owner))
                    {
                        // Startup code and mixins: the game won't load. Other code: it breaks when that part runs.
                        // (Only the @Mod class itself or a mixin: code the main class merely reaches may never run.)
                        bool atStartup = from != null && from.IsModEntry || mixin;
                        if (reported.Add("class|" + file + "|" + installedOwner))
                            issues.Add(Issue(atStartup ? "error" : "warning", "api-mismatch", who + " was made for a different version of " + nameOf(installedOwner) + ": it uses " +
                                Dotted(r.Owner) + ", which " + nameOf(installedOwner) + " " + JarVersion(metas, installedOwner) + " doesn't have. " +
                                (atStartup ? "The game won't load like this." : "Its " + nameOf(installedOwner) + " support runs because that mod is installed, so it may crash when that part is used (or as the game loads)."),
                                new[] { file, installedOwner }, Obj("action", "versions", "file", installedOwner, "for", file)));
                        continue;
                    }
                    // A class no jar here has. Only the main class and mixins are checked: other code may only touch
                    // optional mods when they're installed.
                    if (!entry && !mixin) continue;
                    string root = Root(r.Owner);
                    if (root == null || libs.Contains(root)) continue;
                    string[] segments = r.Owner.Split('/');
                    // Guarded: the class checks for that mod by id first (ModList.isLoaded("thirst")), or lists it as optional.
                    // (Only package words that can be a mod id count, and the text only when the class does check.)
                    var idWords = segments.Take(segments.Length - 1).Skip(1).Where(s => s.Length >= 3 && !CommonPackageWords.Contains(s)).ToList();
                    // Guarded: the jar mentions that mod's id somewhere (a ModList.isLoaded check, a compat list, its mods.toml).
                    if (idWords.Any(w => code.Mentions.Contains(Squash(w)) || declaredIds.Any(d => Squash(d) == Squash(w)))) continue;
                    // A mixin whose target isn't here is skipped as the game loads, so what it uses doesn't matter.
                    if (mixin && from != null && from.MixinTargets.Any(t => !PlatformPackage.IsMatch(t) && !owner.ContainsKey(t))) continue;
                    // The package is in a jar it declares, but not this class: made for another version of that mod.
                    string pkgJar = PackageOwner(r.Owner, packageOwner);
                    if (pkgJar != null)
                    {
                        if (pkgJar == file || !declaredFiles.Contains(pkgJar) || !reported.Add("class|" + file + "|" + pkgJar)) continue;
                        issues.Add(Issue("error", "api-mismatch", who + " was made for a different version of " + nameOf(pkgJar) + ": it uses " + Dotted(r.Owner) +
                            ", which " + nameOf(pkgJar) + " " + JarVersion(metas, pkgJar) + " doesn't have.", new[] { file, pkgJar },
                            Obj("action", "versions", "file", pkgJar, "for", file)));
                        continue;
                    }
                    if (!reported.Add("missing|" + file + "|" + root)) continue;
                    string off;
                    if (offRoots.TryGetValue(root, out off))
                    {
                        issues.Add(Issue("error", "missing", who + " uses code from " + nameOf(off) + " (" + Dotted(r.Owner) + "), which is turned off.",
                            new[] { file, off }, Obj("action", "enable", "file", off)));
                        continue;
                    }
                    bool guarded = mixin && code.GuardedMixins.Contains(r.From);
                    string guess = segments.Length >= 4 ? segments[2] : segments.Length >= 2 ? segments[1] : null;
                    missingRoots.Add(root);
                    missingHere.Add(Issue(guarded ? "warning" : "error", "missing",
                        who + (entry ? " can't start" : "'s mixin " + ShortClass(r.From) + " can't be applied") + ": it uses " + Dotted(r.Owner) +
                        ", and no installed mod has it. It needs a mod it doesn't list" + (guess != null ? " (probably one called \"" + guess + "\")" : "") + "." +
                        (guarded ? " Its mixin plugin may skip this when that mod isn't there." : ""),
                        new[] { file }, Obj("action", "search", "query", guess ?? root)));
                }
                // A jar that reaches into four or more mods that aren't here is a compatibility pack: its code for
                // them only runs when they're installed. One or two missing mods is the telling case.
                if (missingRoots.Count < 4) issues.AddRange(missingHere);
            }
        }

        // Is the package really that mod's (one of its mod ids is a word of the package), not a partial copy
        // some other jar carries?
        static bool OwnsPackage(Dictionary<string, JarMeta> metas, string jar, string internalName)
        {
            JarMeta meta;
            if (!metas.TryGetValue(jar, out meta)) return false;
            var words = new HashSet<string>(internalName.Split('/').Select(Squash), StringComparer.Ordinal);
            return meta.Mods.Any(m => m.Id != null && words.Contains(Squash(m.Id)));
        }

        // The jar with the closest package to a class (at least three levels deep, so com/github alone never counts).
        static string PackageOwner(string internalName, Dictionary<string, string> packageOwner)
        {
            string p = internalName.Contains("/") ? internalName.Substring(0, internalName.LastIndexOf('/')) : null;
            while (p != null && p.Count(c => c == '/') >= 2)
            {
                string jar;
                if (packageOwner.TryGetValue(p, out jar)) return jar;
                p = p.Substring(0, p.LastIndexOf('/'));
            }
            return null;
        }

        static string JarVersion(Dictionary<string, JarMeta> metas, string file)
        {
            JarMeta m;
            ModInfo main = metas.TryGetValue(file, out m) ? MainMod(m) : null;
            return main != null && main.Version != null && !main.Version.Contains("${") ? main.Version : "(this version)";
        }

        // (Ljava/io/File;Ljava/lang/Class;)Ljava/lang/Object; -> (File, Class)
        static string DescribeArgs(string desc)
        {
            Match m = Regex.Match(desc ?? "", @"^\(([^)]*)\)");
            if (!m.Success) return "";
            var args = new List<string>();
            foreach (Match a in Regex.Matches(m.Groups[1].Value, @"\[*(?:L([^;]+);|[ZBCSIJFD])"))
            {
                string t = a.Groups[1].Success ? a.Groups[1].Value.Substring(a.Groups[1].Value.LastIndexOf('/') + 1)
                    : new Dictionary<char, string> { { 'Z', "boolean" }, { 'B', "byte" }, { 'C', "char" }, { 'S', "short" }, { 'I', "int" }, { 'J', "long" }, { 'F', "float" }, { 'D', "double" } }[a.Value.TrimStart('[')[0]];
                args.Add(t + string.Concat(Enumerable.Repeat("[]", a.Value.TakeWhile(ch => ch == '[').Count())));
            }
            return "(" + string.Join(", ", args) + ")";
        }
    }
}
