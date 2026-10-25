// Features: the structures and mobs the mods add, where they turn up (dimension and biomes), and a switch to
// turn each one off. Everything is read from the jars' own data: structures from worldgen/structure and the
// structure sets that place them, mobs from their loot tables and spawn eggs, spawns from the biome modifiers
// (add_spawns), biome groups from the biome tags, and modded dimensions from their dimension files. The game's own
// jar and NeoForge's (from Prism's libraries) add the vanilla biomes and the full shared biome groups, so the page
// can filter by any biome; the game's own structures, biomes' spawn lists and ores are listed too. Ores are placed
// features of an ore type, from add_features rules and biomes' own feature lists. Where a mod's config names a
// feature, its row links to that setting (ConfigFor); a biome's on/off setting is its switch (BiomeSwitch).
//
// Turning something off writes one small data-only mod into the mods folder (FeatureJarName). It carries:
//   - a copy of each turned-off structure with no biomes, so it never generates in new chunks;
//   - a remove_spawns rule for each turned-off modded mob, covering every biome;
//   - a "none" modifier in place of a mod's extra spawn rule for a vanilla mob (a mod adding End phantoms);
//   - a remove_features rule for each turned-off ore, covering every biome.
// Biomes are the exception: they switch in their mods' configs (SetBiomes), not in this jar.
// Its mods.toml loads it after the mods it overrides, so its copies win. The list of what's off is stored inside
// it, so it travels with the jar (to the server too). With nothing off, the jar is removed.

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
    sealed class SpawnRule
    {
        public string Path, Ns, Entity, Category; // Path: the file inside the jar (or the biome); Ns: the namespace it's in
        public int Weight, Min, Max;
        public object Biomes;
    }

    sealed class FeatureRule
    {
        public string Path, Ns, Step;
        public object Features, Biomes;
    }

    // What one jar carries (its bundled jars included).
    sealed class JarFeatures
    {
        public readonly Dictionary<string, Dictionary<string, object>> Structures = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
        public readonly List<KeyValuePair<string, Dictionary<string, object>>> Sets = new List<KeyValuePair<string, Dictionary<string, object>>>();
        public readonly Dictionary<string, List<object>> BiomeTags = new Dictionary<string, List<object>>(StringComparer.Ordinal);
        public readonly HashSet<string> Biomes = new HashSet<string>(StringComparer.Ordinal);
        public readonly HashSet<string> Entities = new HashSet<string>(StringComparer.Ordinal);
        public readonly List<SpawnRule> Spawns = new List<SpawnRule>();
        public readonly HashSet<string> OwnSpawnTypes = new HashSet<string>(StringComparer.Ordinal); // namespaces with their own spawn modifier types
        public readonly Dictionary<string, Dictionary<string, object>> Placed = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
        public readonly Dictionary<string, Dictionary<string, object>> Configured = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal); // ore ones only
        public readonly List<FeatureRule> FeatureRules = new List<FeatureRule>(); // add_features
        public readonly Dictionary<string, List<SpawnRule>> BiomeSpawns = new Dictionary<string, List<SpawnRule>>(StringComparer.Ordinal); // a biome's own spawn list
        public readonly Dictionary<string, List<string>> BiomeFeatures = new Dictionary<string, List<string>>(StringComparer.Ordinal); // a biome's own placed features
        public readonly Dictionary<string, string> Names = new Dictionary<string, string>(StringComparer.Ordinal); // lang keys
        public readonly Dictionary<string, string> DimensionOfBiome = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    // A biome list (a holder set) made readable: single biomes, groups left as their tag, everything, or everything but.
    sealed class BiomeSet
    {
        public readonly HashSet<string> Ids = new HashSet<string>(StringComparer.Ordinal);
        public readonly HashSet<string> Groups = new HashSet<string>(StringComparer.Ordinal);
        public bool All;
        public BiomeSet Except;
    }

    static partial class Program
    {
        const string FeatureJarName = "mod-manager-feature-toggles.jar";
        const string FeatureModId = "modmanager_feature_toggles";
        const string FeatureListEntry = "mod-manager/features-off.json";
        static readonly Dictionary<string, JarFeatures> JarFeaturesCache = new Dictionary<string, JarFeatures>();
        static readonly object FeatureLock = new object();

        static readonly Regex DataEntry = new Regex(@"^data/([a-z0-9_.-]+)/(.+)\.json$");
        static readonly Regex EggEntry = new Regex(@"^assets/([a-z0-9_.-]+)/(?:models/item|items)/([a-z0-9_.-]+)_spawn_egg\.json$");
        static readonly Regex LangEntry = new Regex(@"^assets/[a-z0-9_.-]+/lang/en_us\.json$");
        // "ore", "scattered_ore", "layered_ore" and the like, as a word: not the "ore" in "forest".
        static readonly Regex OreType = new Regex(@"""type""\s*:\s*""[a-z0-9_.-]+:(?:[a-z0-9_/]*[_/])?ores?(?:[_/][a-z0-9_/]*)?""");
        // Blocks an ore feature replaces or scatters that aren't ores: the stone around them, and vanilla's blobs of
        // andesite, gravel, dirt and the like (ore features too, but nobody looks for them as ores).
        static readonly Regex NotOreBlock = new Regex(":(stone|deepslate|netherrack|end_stone|air|water|lava|tuff|granite|diorite|andesite|basalt|blackstone|calcite|dirt|gravel|clay|sand|red_sand|soul_sand|soul_soil|magma_block|infested_[a-z_]+)$");
        static readonly Regex LangLine = new Regex(@"""((?:entity|biome|dimension|structure|block)\.[a-z0-9_.-]+\.[a-z0-9_./-]+)""\s*:\s*""((?:[^""\\]|\\.)*)""");
        static readonly Regex ModIdOk = new Regex(@"^[a-z][a-z0-9_]{1,63}$");
        static readonly string[] CommonTagSpaces = { "minecraft", "c", "neoforge", "forge" };
        static readonly HashSet<string> VanillaNether = new HashSet<string> { "minecraft:nether_wastes", "minecraft:soul_sand_valley", "minecraft:crimson_forest", "minecraft:warped_forest", "minecraft:basalt_deltas" };
        static readonly HashSet<string> VanillaEnd = new HashSet<string> { "minecraft:the_end", "minecraft:end_highlands", "minecraft:end_midlands", "minecraft:small_end_islands", "minecraft:end_barrens" };
        const string Overworld = "minecraft:overworld", Nether = "minecraft:the_nether", End = "minecraft:the_end";

        // "neoforge" or "forge": whose biome modifier types the toggle jar uses. Null where it can't work (Fabric, Quilt).
        static string FeatureStyle()
        {
            if (loader == "neoforge") return gameVersion != null && gameVersion.StartsWith("1.20.1") ? "forge" : "neoforge";
            if (loader == "forge") return "forge";
            return null;
        }

        // ---------- Reading the jars ----------

        static JarFeatures GetJarFeatures(FileInfo file)
        {
            string key = file.FullName + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks;
            lock (JarFeaturesCache)
            {
                JarFeatures cached;
                if (JarFeaturesCache.TryGetValue(key, out cached)) return cached;
            }
            var jf = new JarFeatures();
            try { using (ZipArchive zip = ZipFile.OpenRead(file.FullName)) ReadFeatureEntries(zip, jf, 0); }
            catch (Exception) { } // a broken jar just adds nothing
            lock (JarFeaturesCache) JarFeaturesCache[key] = jf;
            return jf;
        }

        static void ReadFeatureEntries(ZipArchive zip, JarFeatures jf, int depth)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            foreach (ZipArchiveEntry e in zip.Entries)
            {
                string n = e.FullName;
                if (depth == 0 && n.StartsWith("META-INF/jarjar/", StringComparison.Ordinal) && n.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) && e.Length < 64L << 20)
                {
                    // Bundled mods carry their own data (Create Aeronautics is a shell around several).
                    try
                    {
                        var mem = new MemoryStream();
                        using (Stream s = e.Open()) s.CopyTo(mem);
                        mem.Position = 0;
                        using (var inner = new ZipArchive(mem, ZipArchiveMode.Read)) ReadFeatureEntries(inner, jf, depth + 1);
                    }
                    catch (Exception) { }
                    continue;
                }
                Match m = EggEntry.Match(n);
                if (m.Success) { jf.Entities.Add(m.Groups[1].Value + ":" + m.Groups[2].Value); continue; }
                if (LangEntry.IsMatch(n))
                {
                    if (e.Length > 8L << 20) continue;
                    foreach (Match line in LangLine.Matches(ReadEntryText(e)))
                        if (!jf.Names.ContainsKey(line.Groups[1].Value))
                            try { jf.Names[line.Groups[1].Value] = json.Deserialize<string>("\"" + line.Groups[2].Value + "\""); } catch (Exception) { }
                    continue;
                }
                m = DataEntry.Match(n);
                if (!m.Success) continue;
                string ns = m.Groups[1].Value, rest = m.Groups[2].Value;
                Match r;
                if ((r = Regex.Match(rest, @"^loot_tables?/entities/([a-z0-9_.-]+)$")).Success) { jf.Entities.Add(ns + ":" + r.Groups[1].Value); continue; }
                if ((r = Regex.Match(rest, @"^worldgen/biome/(.+)$")).Success)
                {
                    string biome = ns + ":" + r.Groups[1].Value;
                    jf.Biomes.Add(biome);
                    try { ReadBiomeContent(biome, json.DeserializeObject(ReadEntryText(e)) as Dictionary<string, object>, jf); } catch (Exception) { }
                    continue;
                }
                bool structure = rest.StartsWith("worldgen/structure/", StringComparison.Ordinal);
                bool set = rest.StartsWith("worldgen/structure_set/", StringComparison.Ordinal);
                bool tag = rest.StartsWith("tags/worldgen/biome/", StringComparison.Ordinal);
                bool dim = rest.StartsWith("dimension/", StringComparison.Ordinal);
                bool placed = rest.StartsWith("worldgen/placed_feature/", StringComparison.Ordinal);
                bool configured = rest.StartsWith("worldgen/configured_feature/", StringComparison.Ordinal);
                Match bm = Regex.Match(rest, @"^(?:neoforge|forge)/biome_modifiers?/");
                if (!structure && !set && !tag && !dim && !placed && !configured && !bm.Success) continue;
                string text;
                try { text = ReadEntryText(e); } catch (Exception) { continue; }
                // Configured features are only wanted when they're ores (their type says so); there are thousands of others.
                if (configured && !OreType.IsMatch(text)) continue;
                Dictionary<string, object> d;
                try { d = json.DeserializeObject(text) as Dictionary<string, object>; }
                catch (Exception) { continue; }
                if (d == null) continue;
                if (placed) jf.Placed[ns + ":" + rest.Substring("worldgen/placed_feature/".Length)] = d;
                else if (configured) jf.Configured[ns + ":" + rest.Substring("worldgen/configured_feature/".Length)] = d;
                else if (structure) jf.Structures[ns + ":" + rest.Substring("worldgen/structure/".Length)] = d;
                else if (set) jf.Sets.Add(new KeyValuePair<string, Dictionary<string, object>>(ns + ":" + rest.Substring("worldgen/structure_set/".Length), d));
                else if (tag)
                {
                    string id = ns + ":" + rest.Substring("tags/worldgen/biome/".Length);
                    List<object> list;
                    if (!jf.BiomeTags.TryGetValue(id, out list)) jf.BiomeTags[id] = list = new List<object>();
                    list.AddRange(A(Get(d, "values")));
                }
                else if (dim) ReadDimensionBiomes(ns + ":" + rest.Substring("dimension/".Length), d, jf);
                else ReadBiomeModifier("data/" + ns + "/" + rest + ".json", ns, d, jf);
            }
        }

        // A biome's own lists: the mobs that spawn in it (by category) and the features it places, ores among them.
        static void ReadBiomeContent(string biome, Dictionary<string, object> d, JarFeatures jf)
        {
            if (d == null || jf.BiomeSpawns.ContainsKey(biome)) return;
            var spawns = new List<SpawnRule>();
            Dictionary<string, object> spawners = D(Get(d, "spawners"));
            if (spawners != null)
                foreach (KeyValuePair<string, object> category in spawners)
                    foreach (Dictionary<string, object> s in A(category.Value).Select(D).Where(x => x != null && S(x, "type") != null))
                        spawns.Add(new SpawnRule { Path = biome, Ns = biome.Substring(0, biome.IndexOf(':')), Entity = NormId(S(s, "type")), Biomes = biome, Category = category.Key,
                            Weight = IntOf(Get(s, "weight")), Min = IntOf(Get(s, "minCount")), Max = IntOf(Get(s, "maxCount")) });
            jf.BiomeSpawns[biome] = spawns;
            var features = new List<string>();
            foreach (object step in A(Get(d, "features")))
                foreach (object f in A(step)) if (f is string) features.Add(NormId((string)f));
            jf.BiomeFeatures[biome] = features;
        }

        static string ReadEntryText(ZipArchiveEntry e)
        {
            using (var reader = new StreamReader(e.Open(), Encoding.UTF8)) return reader.ReadToEnd();
        }

        static void ReadDimensionBiomes(string dimension, Dictionary<string, object> d, JarFeatures jf)
        {
            Dictionary<string, object> source = D(Get(D(Get(d, "generator")), "biome_source"));
            if (source == null) return;
            var biomes = new List<string>();
            string one = S(source, "biome");
            if (one != null) biomes.Add(one);
            foreach (object b in A(Get(source, "biomes")))
            {
                if (b is string) biomes.Add((string)b);
                else if (S(D(b), "biome") != null) biomes.Add(S(D(b), "biome"));
            }
            foreach (string b in biomes)
                if (!b.StartsWith("#") && !jf.DimensionOfBiome.ContainsKey(NormId(b))) jf.DimensionOfBiome[NormId(b)] = dimension;
        }

        static void ReadBiomeModifier(string path, string ns, Dictionary<string, object> d, JarFeatures jf)
        {
            string type = S(d, "type") ?? "";
            if (type == "neoforge:add_spawns" || type == "forge:add_spawns")
            {
                object spawners = Get(d, "spawners");
                IEnumerable<object> list = spawners is object[] ? (object[])spawners : new[] { spawners };
                foreach (Dictionary<string, object> s in list.Select(D).Where(x => x != null && S(x, "type") != null))
                    jf.Spawns.Add(new SpawnRule { Path = path, Ns = ns, Entity = NormId(S(s, "type")), Biomes = Get(d, "biomes"),
                        Weight = IntOf(Get(s, "weight")), Min = IntOf(Get(s, "minCount")), Max = IntOf(Get(s, "maxCount")) });
            }
            else if (type == "neoforge:add_features" || type == "forge:add_features")
                jf.FeatureRules.Add(new FeatureRule { Path = path, Ns = ns, Features = Get(d, "features"), Biomes = Get(d, "biomes"), Step = S(d, "step") });
            else if (type.IndexOf(':') > 0 && type.IndexOf("spawn", StringComparison.OrdinalIgnoreCase) >= 0 && !type.StartsWith("neoforge:") && !type.StartsWith("forge:"))
                jf.OwnSpawnTypes.Add(type.Substring(0, type.IndexOf(':')));
        }

        static int IntOf(object v)
        {
            try { return v == null ? 0 : Convert.ToInt32(v, CultureInfo.InvariantCulture); } catch (Exception) { return 0; }
        }

        static string NormId(string id) { return id.IndexOf(':') < 0 ? "minecraft:" + id : id; }

        // ---------- Putting it together ----------

        sealed class FeatureJar { public FileInfo File; public JarMeta Meta; public JarFeatures Data; }

        sealed class FeatureWorld
        {
            public List<FeatureJar> Jars;
            public List<JarFeatures> Base; // the game's and NeoForge's own data
            public readonly Dictionary<string, List<object>> Tags = new Dictionary<string, List<object>>(StringComparer.Ordinal);
            public readonly HashSet<string> Biomes = new HashSet<string>(StringComparer.Ordinal);
            public readonly Dictionary<string, string> Names = new Dictionary<string, string>(StringComparer.Ordinal);
            public readonly Dictionary<string, string> DimOfBiome = new Dictionary<string, string>(StringComparer.Ordinal);
            public readonly Dictionary<string, BiomeSet> Expanded = new Dictionary<string, BiomeSet>(StringComparer.Ordinal);
            public readonly Dictionary<string, List<string>> Members = new Dictionary<string, List<string>>(StringComparer.Ordinal); // a shared group's biomes
            public readonly HashSet<string> UsedGroups = new HashSet<string>(StringComparer.Ordinal);
            public readonly Dictionary<string, FeatureJar> BiomeOwner = new Dictionary<string, FeatureJar>(StringComparer.Ordinal);
            public readonly HashSet<string> VanillaBiomes = new HashSet<string>(StringComparer.Ordinal);
        }

        // The game's own data (vanilla biomes, their names, and the biome groups mods add to) and NeoForge's c: groups,
        // from Prism's libraries. Without them the shared groups only hold what the mods add.
        static List<JarFeatures> BaseFeatureJars()
        {
            var list = new List<JarFeatures>();
            string libs = Path.Combine(PrismDataDir(), "libraries");
            string v = gameVersion ?? "";
            string vanilla = Path.Combine(libs, "com", "mojang", "minecraft", v, "minecraft-" + v + "-client.jar");
            if (File.Exists(vanilla)) list.Add(GetJarFeatures(new FileInfo(vanilla)));
            if (loader == "neoforge")
            {
                string neo = NeoForgeUniversalJar(libs);
                if (neo != null) list.Add(GetJarFeatures(new FileInfo(neo)));
            }
            return list;
        }

        // This instance's NeoForge (from Prism's mmc-pack.json), else the newest one Prism has for this Minecraft version.
        static string NeoForgeUniversalJar(string libs)
        {
            string root = Path.Combine(libs, "net", "neoforged", "neoforge");
            if (!Directory.Exists(root)) return null;
            string version = null;
            try
            {
                string pack = Path.Combine(Path.GetDirectoryName(GameDir().TrimEnd('\\')), "mmc-pack.json");
                if (File.Exists(pack))
                {
                    var d = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(pack)) as Dictionary<string, object>;
                    Dictionary<string, object> neo = A(Get(d, "components")).Select(D).FirstOrDefault(c => S(c, "uid") == "net.neoforged");
                    version = S(neo, "version");
                }
            }
            catch (Exception) { }
            Func<string, string> jar = ver => Path.Combine(root, ver, "neoforge-" + ver + "-universal.jar");
            if (version != null && File.Exists(jar(version))) return jar(version);
            string mc = gameVersion ?? "";
            string prefix = mc.StartsWith("1.") ? mc.Substring(2) : mc;
            if (prefix.Count(ch => ch == '.') == 0) prefix += ".0";
            return new DirectoryInfo(root).GetDirectories().Select(x => x.Name).Where(n => n.StartsWith(prefix + ".") && File.Exists(jar(n)))
                .OrderByDescending(n => IntOf(n.Substring(prefix.Length + 1).Split('-')[0])).Select(jar).FirstOrDefault();
        }

        static List<string> GroupMembers(FeatureWorld w, string tag)
        {
            List<string> list;
            if (w.Members.TryGetValue(tag, out list)) return list;
            var set = new BiomeSet();
            ExpandTag(w, tag, set, true, new HashSet<string>());
            w.Members[tag] = list = set.Ids.OrderBy(x => x, StringComparer.Ordinal).ToList();
            return list;
        }

        static List<FeatureJar> FeatureJars()
        {
            if (!Directory.Exists(modsDir)) return new List<FeatureJar>();
            return new DirectoryInfo(modsDir).GetFiles("*.jar")
                .Where(f => f.Name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) && !f.Name.Equals(FeatureJarName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(f => new FeatureJar { File = f, Meta = GetJarMeta(f), Data = GetJarFeatures(f) }).ToList();
        }

        static FeatureWorld BuildFeatureWorld()
        {
            var w = new FeatureWorld { Jars = FeatureJars() };
            List<JarFeatures> based = w.Base = BaseFeatureJars();
            // The mods first, so their names and dimension files win over the game's.
            foreach (JarFeatures data in w.Jars.Select(j => j.Data).Concat(based))
            {
                foreach (KeyValuePair<string, List<object>> t in data.BiomeTags)
                {
                    List<object> list;
                    if (!w.Tags.TryGetValue(t.Key, out list)) w.Tags[t.Key] = list = new List<object>();
                    list.AddRange(t.Value);
                }
                w.Biomes.UnionWith(data.Biomes);
                foreach (KeyValuePair<string, string> n in data.Names) if (!w.Names.ContainsKey(n.Key)) w.Names[n.Key] = n.Value;
                foreach (KeyValuePair<string, string> b in data.DimensionOfBiome) if (!w.DimOfBiome.ContainsKey(b.Key)) w.DimOfBiome[b.Key] = b.Value;
            }
            foreach (JarFeatures data in based) w.VanillaBiomes.UnionWith(data.Biomes.Where(b => b.StartsWith("minecraft:")));
            foreach (FeatureJar j in w.Jars)
                foreach (string b in j.Data.Biomes)
                    if (!b.StartsWith("minecraft:") && !w.BiomeOwner.ContainsKey(b)) w.BiomeOwner[b] = j;
            // The is_nether / is_end / is_overworld tags tell modded biomes' dimension.
            foreach (var pair in new[] { new[] { "is_nether", Nether }, new[] { "is_end", End }, new[] { "is_overworld", Overworld } })
                foreach (string space in CommonTagSpaces)
                {
                    var members = new BiomeSet();
                    ExpandTag(w, space + ":" + pair[0], members, true, new HashSet<string>());
                    foreach (string b in members.Ids) if (!w.DimOfBiome.ContainsKey(b)) w.DimOfBiome[b] = pair[1];
                }
            return w;
        }

        static bool IsCommonTag(string tag) { return CommonTagSpaces.Contains(tag.Substring(0, Math.Max(0, tag.IndexOf(':')))); }

        // A mod's own tags are opened up to their biomes; the shared ones (minecraft, c, neoforge) stay as a group,
        // since the jars only hold what mods add to them, not the whole list.
        static void ExpandTag(FeatureWorld w, string tag, BiomeSet into, bool openCommon, HashSet<string> seen)
        {
            if (!seen.Add(tag)) return;
            List<object> entries;
            if (!w.Tags.TryGetValue(tag, out entries)) { if (!openCommon) into.Groups.Add(tag); return; }
            foreach (object entry in entries)
            {
                string id = entry as string;
                bool required = true;
                if (id == null && D(entry) != null) { id = S(D(entry), "id"); required = !(Get(D(entry), "required") is bool) || (bool)Get(D(entry), "required"); }
                if (id == null) continue;
                if (id.StartsWith("#"))
                {
                    string child = NormId(id.Substring(1));
                    if (!openCommon && IsCommonTag(child)) into.Groups.Add(child);
                    else ExpandTag(w, child, into, openCommon, seen);
                    continue;
                }
                id = NormId(id);
                // A biome from a mod that isn't here, or from a newer Minecraft (once the game's own list is known).
                if (!required && !w.Biomes.Contains(id) && (!id.StartsWith("minecraft:") || w.VanillaBiomes.Count > 0)) continue;
                into.Ids.Add(id);
            }
        }

        static BiomeSet DescribeBiomes(FeatureWorld w, object holder)
        {
            var set = new BiomeSet();
            AddBiomes(w, holder, set);
            return set;
        }

        static void AddBiomes(FeatureWorld w, object holder, BiomeSet set)
        {
            if (holder is string)
            {
                string s = (string)holder;
                if (!s.StartsWith("#")) { set.Ids.Add(NormId(s)); return; }
                string tag = NormId(s.Substring(1));
                if (IsCommonTag(tag)) { set.Groups.Add(tag); return; }
                BiomeSet open;
                if (!w.Expanded.TryGetValue(tag, out open))
                {
                    open = new BiomeSet();
                    ExpandTag(w, tag, open, false, new HashSet<string>());
                    w.Expanded[tag] = open;
                }
                set.Ids.UnionWith(open.Ids);
                set.Groups.UnionWith(open.Groups);
                return;
            }
            if (holder is object[]) { foreach (object o in (object[])holder) AddBiomes(w, o, set); return; }
            Dictionary<string, object> d = D(holder);
            if (d == null) return;
            string type = S(d, "type") ?? "";
            string kind = type.Substring(type.IndexOf(':') + 1);
            if (kind == "any") set.All = true;
            else if (kind == "not") { set.All = true; set.Except = set.Except ?? new BiomeSet(); AddBiomes(w, Get(d, "value"), set.Except); }
            else foreach (object o in A(Get(d, "values"))) AddBiomes(w, o, set); // or / and (and is shown as the union)
        }

        static string DimOf(FeatureWorld w, string biome)
        {
            string dim;
            if (w.DimOfBiome.TryGetValue(biome, out dim)) return dim;
            if (VanillaNether.Contains(biome)) return Nether;
            if (VanillaEnd.Contains(biome)) return End;
            return Overworld;
        }

        static string DimOfGroup(string tag)
        {
            string path = tag.Substring(tag.IndexOf(':') + 1);
            if (Regex.IsMatch(path, "nether|bastion|fortress")) return Nether;
            if (Regex.IsMatch(path, @"(^|[/_])end($|[/_])|end_city|the_end")) return End;
            return Overworld;
        }

        static string FeatureTitle(string path)
        {
            string words = Regex.Replace(path.Replace('/', ' ').Replace('_', ' ').Replace('-', ' '), @"\s+", " ").Trim();
            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(words);
        }

        static string NameOf(FeatureWorld w, string kind, string id)
        {
            string name;
            int c = id.IndexOf(':');
            if (w.Names.TryGetValue(kind + "." + id.Substring(0, c) + "." + id.Substring(c + 1).Replace('/', '.'), out name) && name.Trim().Length > 0) return name;
            return FeatureTitle(id.Substring(c + 1));
        }

        static string DimName(FeatureWorld w, string dim)
        {
            if (dim == Overworld) return "Overworld";
            if (dim == Nether) return "Nether";
            if (dim == End) return "End";
            return NameOf(w, "dimension", dim);
        }

        static string GroupLabel(string tag)
        {
            string path = tag.Substring(tag.IndexOf(':') + 1);
            path = Regex.Replace(path, @"^(has_structure/|is_)", "");
            return FeatureTitle(path) + " biomes";
        }

        // Where a biome list points, as the page shows it.
        static object[] PlaceFields(FeatureWorld w, BiomeSet set)
        {
            var dims = new HashSet<string>();
            if (set.All) { dims.Add(Overworld); dims.Add(Nether); dims.Add(End); }
            foreach (string b in set.Ids) dims.Add(DimOf(w, b));
            foreach (string g in set.Groups.Concat(set.Except != null ? set.Except.Groups : Enumerable.Empty<string>()))
            {
                w.UsedGroups.Add(g);
                if (set.Except != null && set.Except.Groups.Contains(g)) continue;
                List<string> members = GroupMembers(w, g);
                if (members.Count == 0) dims.Add(DimOfGroup(g));
                else foreach (string b in members) dims.Add(DimOf(w, b));
            }
            Func<BiomeSet, List<object>> biomes = s => s.Ids.Select(b => (object)Obj("id", b, "name", NameOf(w, "biome", b)))
                .Concat(s.Groups.Select(g => (object)Obj("id", "#" + g, "name", GroupLabel(g), "group", true)))
                .OrderBy(o => (string)((Dictionary<string, object>)o)["name"], StringComparer.OrdinalIgnoreCase).ToList();
            var order = new[] { Overworld, Nether, End };
            return new object[] {
                "dims", dims.OrderBy(x => Array.IndexOf(order, x) < 0 ? 9 : Array.IndexOf(order, x)).ThenBy(x => x).Select(x => DimName(w, x)).ToList(),
                "biomes", biomes(set),
                "allBiomes", set.All,
                "except", set.Except != null ? biomes(set.Except) : null };
        }

        static string ModNameOf(FeatureJar j)
        {
            return j.Meta.Mods.Count > 0 && !string.IsNullOrEmpty(j.Meta.Mods[0].Name) ? j.Meta.Mods[0].Name : null;
        }

        static IEnumerable<string> ModIdsOf(FeatureJar j)
        {
            return j.Meta.Mods.Concat(j.Meta.Nested).Select(m => m.Id).Where(id => id != null && ModIdOk.IsMatch(id) && id != "minecraft" && id != "neoforge" && id != "forge" && id != FeatureModId);
        }

        static Dictionary<string, object> Fields(params object[][] parts)
        {
            return Obj(parts.SelectMany(p => p).ToArray());
        }

        // ---------- The mods' own config settings for a feature ----------
        // Many mods set spawns, structure spacing or biome weights in their config instead of (or on top of) their data.
        // A feature's mod's config files (by owner, as on the Configs page, or by the mod id in the file's path) are
        // searched for a setting naming the feature; failing that, a file with settings of its kind stands in.

        sealed class FeatureConfig
        {
            public string Side, Rel, Norm, Owner, Lower, Flat; // Flat: the text squashed to letters and digits
            public FileInfo File;
            public List<TomlEntry> Entries;
            public bool Loaded;
        }

        static readonly Regex MobConfigWords = new Regex("spawn|weight|enable|disable|groupsize|mincount|maxcount");
        static readonly Regex StructureConfigWords = new Regex("structure|spacing|separation|generat|spawn|enable|disable|chance|frequency|rarity|weight");
        static readonly Regex BiomeConfigWords = new Regex("biome|weight|enable|disable|generat|region");

        static string Flatten(string s) { return Regex.Replace((s ?? "").ToLowerInvariant(), "[^a-z0-9]", ""); }

        static List<FeatureConfig> FeatureConfigs(ICollection<string> modIds)
        {
            var list = new List<FeatureConfig>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sides = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("game", Path.Combine(GameDir(), "config")) };
            if (!string.IsNullOrEmpty(serverDir) && Directory.Exists(serverDir)) sides.Add(new KeyValuePair<string, string>("server", Path.Combine(serverDir, "config")));
            foreach (KeyValuePair<string, string> side in sides)
                foreach (KeyValuePair<string, FileInfo> f in ShareableConfigs(side.Value))
                    if (seen.Add(f.Key)) // the game's copy stands for both
                        list.Add(new FeatureConfig { Side = side.Key, Rel = f.Key, Norm = Flatten(f.Key), Owner = ConfigOwner(f.Key, modIds), File = f.Value });
            return list;
        }

        static void LoadConfig(FeatureConfig c)
        {
            if (c.Loaded) return;
            c.Loaded = true;
            try { c.Lower = File.ReadAllText(c.File.FullName).ToLowerInvariant(); c.Flat = Flatten(c.Lower); } catch (Exception) { return; }
            if (ConfigFormat(c.Rel) == "toml") try { c.Entries = ReadToml(c.File.FullName).Entries; } catch (Exception) { }
        }

        // A mod's config files: by owner, as on the Configs page, or with its id in the path. Kept per set of mod ids.
        static List<FeatureConfig> ConfigsOf(List<FeatureConfig> files, Dictionary<string, List<FeatureConfig>> memo, ICollection<string> owners)
        {
            string ownerKey = string.Join("|", owners.OrderBy(o => o, StringComparer.Ordinal));
            List<FeatureConfig> mine;
            if (!memo.TryGetValue(ownerKey, out mine))
            {
                List<string> squashed = owners.Where(o => o.Length >= 5).Select(Flatten).ToList();
                memo[ownerKey] = mine = files.Where(c => (c.Owner != null && owners.Contains(c.Owner)) || squashed.Any(o => c.Norm.Contains(o))).ToList();
            }
            return mine;
        }

        static Dictionary<string, object> ConfigFor(List<FeatureConfig> files, Dictionary<string, List<FeatureConfig>> memo, ICollection<string> owners, string name, Regex words, string general)
        {
            List<FeatureConfig> mine = ConfigsOf(files, memo, owners);
            if (mine.Count == 0) return null;
            string token = Flatten(name), plain = (name ?? "").ToLowerInvariant();
            Dictionary<string, object> best = null;
            int bestScore = 0;
            if (token.Length >= 3)
                foreach (FeatureConfig c in mine)
                {
                    LoadConfig(c);
                    if (c.Lower == null || (!c.Lower.Contains(plain) && !c.Flat.Contains(token))) continue;
                    if (c.Entries != null)
                        foreach (TomlEntry e in c.Entries)
                        {
                            string hay = Flatten(e.Section + " " + e.Key);
                            if (!hay.Contains(token)) continue;
                            int score = 3 + (words.IsMatch(hay) ? 2 : 0) + (Flatten(e.Key).Contains(token) ? 1 : 0);
                            if (score > bestScore) { bestScore = score; best = Obj("side", c.Side, "path", c.Rel, "section", e.Section, "key", e.Key); }
                        }
                    if (bestScore < 1 && c.Lower.Contains(plain)) { bestScore = 1; best = Obj("side", c.Side, "path", c.Rel); }
                }
            if (best != null) return best;
            FeatureConfig any = mine.FirstOrDefault(c => { LoadConfig(c); return c.Flat != null && c.Flat.Contains(general); });
            return any == null ? null : Obj("side", any.Side, "path", any.Rel, "general", true);
        }

        // A biome's on/off switch in its mod's config: a true/false setting named after it (Nature's Spirit's
        // has_alpine_clearings), or "id": true in a JSON list of biomes (Biomes O' Plenty's biome toggles).
        // A "disable..." setting counts the other way round. Null when the mod has none.
        static Dictionary<string, object> BiomeSwitch(List<FeatureConfig> files, Dictionary<string, List<FeatureConfig>> memo, ICollection<string> owners, string biome)
        {
            List<FeatureConfig> mine = ConfigsOf(files, memo, owners);
            string path = PathTail(biome), token = Flatten(path);
            if (token.Length < 3) return null;
            Dictionary<string, object> best = null;
            int bestScore = 0;
            foreach (FeatureConfig c in mine)
            {
                LoadConfig(c);
                if (c.Lower == null || !c.Flat.Contains(token)) continue;
                if (c.Entries != null)
                    foreach (TomlEntry e in c.Entries.Where(x => x.Type == "bool"))
                    {
                        string key = Flatten(e.Key), hay = Flatten(e.Section + " " + e.Key);
                        if (!hay.Contains(token)) continue;
                        // The setting is the biome's own name (with has/enable/generate around it), not just mentions it.
                        string rest = key.Replace(token, "");
                        if (rest.Length > 0 && !Regex.IsMatch(rest, "^(has|enable|enabled|generate|spawn|allow|disable|disabled|biome|is|use|should|toggle|s)+$")) continue;
                        int score = key.Contains(token) ? 2 : 1;
                        bool invert = rest.Contains("disable");
                        if (score > bestScore) { bestScore = score; best = Obj("side", c.Side, "path", c.Rel, "format", "toml", "section", e.Section, "key", e.Key, "invert", invert, "on", (e.Raw == "true") != invert); }
                    }
                if (bestScore == 0 && c.Entries == null)
                {
                    Match m = Regex.Match(c.Lower, "\"((?:[a-z0-9_.-]+:)?" + Regex.Escape(path) + ")\"\\s*:\\s*(true|false)");
                    if (m.Success) { bestScore = 1; best = Obj("side", c.Side, "path", c.Rel, "format", "text", "key", m.Groups[1].Value, "invert", false, "on", m.Groups[2].Value == "true"); }
                }
            }
            return best;
        }

        // Flips biomes' switches in their mods' configs, in the game's copy and the server's (when it has the file).
        static Dictionary<string, object> SetBiomes(List<string> ids, bool on)
        {
            if (ids == null || ids.Count == 0) throw new UserError("Nothing was selected.");
            lock (FeatureLock)
            {
                FeatureWorld w = BuildFeatureWorld();
                List<FeatureConfig> configs = FeatureConfigs(new HashSet<string>(w.Jars.SelectMany(j => j.Meta.Mods.Concat(j.Meta.Nested)).Select(m => m.Id).Where(id => id != null), StringComparer.OrdinalIgnoreCase));
                var memo = new Dictionary<string, List<FeatureConfig>>();
                var notes = new List<string>();
                int changed = 0;
                foreach (string id in ids)
                {
                    FeatureJar owner;
                    if (!w.BiomeOwner.TryGetValue(id, out owner)) { notes.Add(id + " isn't a mod's biome."); continue; }
                    Dictionary<string, object> sw = BiomeSwitch(configs, memo, OwnersOf(owner, id), id);
                    if (sw == null) { notes.Add(NameOf(w, "biome", id) + " has no switch in its mod's config."); continue; }
                    if ((bool)sw["on"] == on) continue;
                    string rel = (string)sw["path"];
                    foreach (string side in new[] { "game", "server" })
                    {
                        if (side == "server" && (string.IsNullOrEmpty(serverDir) || !Directory.Exists(serverDir))) continue;
                        string file;
                        try { file = ConfigPath(side, rel); } catch (UserError) { continue; }
                        if (!File.Exists(file)) continue;
                        if (side == "server" && ServerBusy()) { notes.Add("The server is running, so its copy of " + rel + " wasn't changed."); continue; }
                        SetBiomeSwitch(side, rel, file, sw, on);
                    }
                    changed++;
                }
                if (changed > 0) Log("Features  " + (on ? "turned on " : "turned off ") + changed + " biome" + (changed == 1 ? "" : "s") + " in their configs: " + string.Join(", ", ids.Take(12)));
                return Obj("ok", true, "changed", changed, "notes", notes);
            }
        }

        static void SetBiomeSwitch(string side, string rel, string file, Dictionary<string, object> sw, bool on)
        {
            bool value = on != (bool)sw["invert"];
            string stamp = new FileInfo(file).LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
            if ((string)sw["format"] == "toml")
            {
                SaveConfigEdits(Obj("side", side, "path", rel, "stamp", stamp,
                    "edits", new object[] { Obj("section", sw["section"], "key", sw["key"], "type", "bool", "value", value) }));
                return;
            }
            string text = File.ReadAllText(file, Encoding.UTF8);
            var re = new Regex("(\"" + Regex.Escape((string)sw["key"]) + "\"\\s*:\\s*)(true|false)", RegexOptions.IgnoreCase);
            if (!re.IsMatch(text)) throw new UserError(rel + " no longer has a setting for " + sw["key"] + ".");
            WriteConfigWithBackup(side, rel, file, stamp, re.Replace(text, "${1}" + (value ? "true" : "false"), 1));
            Log("Config    " + rel + (side == "server" ? " (server)" : "") + ": " + sw["key"] + " = " + (value ? "true" : "false"));
        }

        // ---------- Ores ----------

        static readonly Regex OreConfigWords = new Regex("ore|vein|generat|size|count|height|enable|disable");

        // The blocks an ore places: every block state named in its config (targets, or a mod's own layout).
        static void CollectStates(object node, List<string> into, int depth)
        {
            if (depth > 8 || into.Count >= 8) return;
            Dictionary<string, object> d = D(node);
            if (d != null)
            {
                string name = S(d, "Name");
                if (name != null && name.IndexOf(':') > 0 && !into.Contains(name)) into.Add(name);
                foreach (KeyValuePair<string, object> kv in d) if (kv.Key != "target" && kv.Key != "Name") CollectStates(kv.Value, into, depth + 1);
                return;
            }
            foreach (object o in A(node)) CollectStates(o, into, depth + 1);
        }

        // An int provider as text, and its average: 8, uniform 2–6, and so on.
        static string ProviderText(object p, out double average)
        {
            average = 1;
            if (p == null) return null;
            if (!(p is Dictionary<string, object>)) { average = IntOf(p); return IntOf(p).ToString(CultureInfo.InvariantCulture); }
            Dictionary<string, object> d = (Dictionary<string, object>)p;
            Dictionary<string, object> v = D(Get(d, "value")) ?? d;
            if (Get(v, "min_inclusive") != null)
            {
                int lo = IntOf(Get(v, "min_inclusive")), hi = IntOf(Get(v, "max_inclusive"));
                average = (lo + hi) / 2.0;
                return lo == hi ? lo.ToString(CultureInfo.InvariantCulture) : lo + "–" + hi;
            }
            if (Get(d, "value") != null && !(Get(d, "value") is Dictionary<string, object>)) { average = IntOf(Get(d, "value")); return IntOf(Get(d, "value")).ToString(CultureInfo.InvariantCulture); }
            return "varies";
        }

        // A vertical anchor as text and an overworld Y for sorting.
        static string AnchorText(Dictionary<string, object> a, out int y)
        {
            y = 0;
            if (a == null) return "?";
            if (Get(a, "absolute") != null) { y = IntOf(Get(a, "absolute")); return y.ToString(CultureInfo.InvariantCulture); }
            if (Get(a, "above_bottom") != null) { int n = IntOf(Get(a, "above_bottom")); y = -64 + n; return "bottom" + (n == 0 ? "" : n > 0 ? "+" + n : "−" + -n); }
            if (Get(a, "below_top") != null) { int n = IntOf(Get(a, "below_top")); y = 320 - n; return "top" + (n == 0 ? "" : n > 0 ? "−" + n : "+" + -n); }
            return "?";
        }

        static object[] OreFields(FeatureWorld w, Dictionary<string, object> placed, Dictionary<string, object> configured)
        {
            var blocks = new List<string>();
            CollectStates(Get(configured, "config"), blocks, 0);
            int named = blocks.Count;
            blocks.RemoveAll(b => NotOreBlock.IsMatch(b));
            int size = IntOf(Get(D(Get(configured, "config")), "size"));
            string veins = null, height = null;
            double perChunk = 1;
            int chance = 0;
            object minY = null, maxY = null;
            foreach (Dictionary<string, object> m in A(Get(placed, "placement")).Select(D).Where(x => x != null))
            {
                string t = S(m, "type") ?? "";
                t = t.Substring(t.IndexOf(':') + 1);
                if (t == "count") veins = ProviderText(Get(m, "count"), out perChunk);
                else if (t == "rarity_filter") chance = IntOf(Get(m, "chance"));
                else if (t == "height_range")
                {
                    Dictionary<string, object> hp = D(Get(m, "height"));
                    int lo, hi;
                    if (hp != null && Get(hp, "min_inclusive") != null)
                    {
                        string a = AnchorText(D(Get(hp, "min_inclusive")), out lo), b = AnchorText(D(Get(hp, "max_inclusive")), out hi);
                        string shape = (S(hp, "type") ?? "").Replace("minecraft:", "");
                        height = "Y " + a + " to " + b + (shape == "trapezoid" ? ", most near " + ((lo + hi) / 2) : shape.Contains("biased_to_bottom") ? ", mostly low" : "");
                        minY = lo; maxY = hi;
                    }
                    else if (hp != null) { height = "Y " + AnchorText(hp, out lo); minY = lo; maxY = lo; }
                }
            }
            if (chance > 1) perChunk /= chance;
            if (named > 0 && blocks.Count == 0) return null; // only stone, dirt and the like: not an ore
            return new object[] {
                "blocks", blocks.Select(b => (object)Obj("id", b, "name", NameOf(w, "block", b))).ToList(),
                "size", size > 0 ? (object)size : null, "veins", veins ?? "1", "chance", chance > 1 ? (object)chance : null,
                "perChunk", Math.Round(perChunk, 3), "height", height, "minY", minY, "maxY", maxY };
        }

        // The placed features an add_features rule names (single ids or lists; tags and inline features are skipped).
        static IEnumerable<string> FeatureIdsOf(object features)
        {
            if (features is string) { if (!((string)features).StartsWith("#")) yield return NormId((string)features); yield break; }
            foreach (object o in A(features)) if (o is string && !((string)o).StartsWith("#")) yield return NormId((string)o);
        }

        static HashSet<string> OwnersOf(FeatureJar j, string id)
        {
            var owners = new HashSet<string>(ModIdsOf(j), StringComparer.OrdinalIgnoreCase);
            string ns = id.Substring(0, id.IndexOf(':'));
            if (ns != "minecraft") owners.Add(ns);
            return owners;
        }

        static string PathTail(string id) { string p = id.Substring(id.IndexOf(':') + 1); return p.Substring(p.LastIndexOf('/') + 1); }

        // ---------- What's off ----------

        sealed class FeaturesOff
        {
            public readonly SortedSet<string> Structures = new SortedSet<string>(StringComparer.Ordinal);
            public readonly SortedSet<string> Mobs = new SortedSet<string>(StringComparer.Ordinal);
            public readonly SortedSet<string> Ores = new SortedSet<string>(StringComparer.Ordinal);
        }

        static FeaturesOff ReadFeaturesOff()
        {
            var off = new FeaturesOff();
            string path = Path.Combine(modsDir, FeatureJarName);
            if (!File.Exists(path)) return off;
            try
            {
                using (ZipArchive zip = ZipFile.OpenRead(path))
                {
                    ZipArchiveEntry e = zip.GetEntry(FeatureListEntry);
                    Dictionary<string, object> d = e == null ? null : new JavaScriptSerializer().DeserializeObject(ReadEntryText(e)) as Dictionary<string, object>;
                    foreach (object s in A(Get(d, "structures"))) if (s is string) off.Structures.Add((string)s);
                    foreach (object s in A(Get(d, "mobs"))) if (s is string) off.Mobs.Add((string)s);
                    foreach (object s in A(Get(d, "ores"))) if (s is string) off.Ores.Add((string)s);
                }
            }
            catch (Exception) { }
            return off;
        }

        // ---------- The list ----------

        static Dictionary<string, object> FeaturesList()
        {
            lock (FeatureLock)
            {
                FeatureWorld w = BuildFeatureWorld();
                FeaturesOff off = ReadFeaturesOff();
                var structures = new List<object>();
                var mobs = new List<object>();
                var biomes = new List<object>();
                List<FeatureConfig> configs = FeatureConfigs(new HashSet<string>(w.Jars.SelectMany(j => j.Meta.Mods.Concat(j.Meta.Nested)).Select(m => m.Id).Where(id => id != null), StringComparer.OrdinalIgnoreCase));
                var memo = new Dictionary<string, List<FeatureConfig>>();

                // Where each structure is placed: its structure set's spacing, and its share when the set picks among several.
                var placed = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
                foreach (JarFeatures data in w.Jars.Select(j => j.Data).Concat(w.Base))
                    foreach (KeyValuePair<string, Dictionary<string, object>> s in data.Sets)
                    {
                        List<Dictionary<string, object>> entries = A(Get(s.Value, "structures")).Select(D).Where(x => x != null && S(x, "structure") != null).ToList();
                        int total = entries.Sum(x => Math.Max(1, IntOf(Get(x, "weight"))));
                        Dictionary<string, object> placement = D(Get(s.Value, "placement"));
                        string ptype = S(placement, "type") ?? "";
                        foreach (Dictionary<string, object> x in entries)
                        {
                            string id = NormId(S(x, "structure"));
                            if (placed.ContainsKey(id)) continue;
                            int spacing = IntOf(Get(placement, "spacing"));
                            placed[id] = Obj("set", s.Key, "placement", ptype.Substring(ptype.IndexOf(':') + 1),
                                "spacing", spacing > 0 ? (object)spacing : null, "separation", spacing > 0 ? (object)IntOf(Get(placement, "separation")) : null,
                                "share", entries.Count > 1 ? (object)Math.Round(Math.Max(1, IntOf(Get(x, "weight"))) / (double)total, 3) : null);
                        }
                    }

                // Every placed structure once: the mods' first (a mod's copy of a vanilla one is what the game uses), then
                // the game's own. Unplaced ones are only parts of others. Vanilla ones are Minecraft's, whoever changed them.
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var structureDefs = w.Jars.SelectMany(j => j.Data.Structures.Select(s => new { Jar = j, Id = s.Key, Def = s.Value }))
                    .Concat(w.Base.SelectMany(b => b.Structures.Select(s => new { Jar = (FeatureJar)null, Id = s.Key, Def = s.Value })));
                foreach (var s in structureDefs)
                {
                    Dictionary<string, object> place;
                    if (!placed.TryGetValue(s.Id, out place) || !seen.Add(s.Id)) continue;
                    bool vanilla = s.Id.StartsWith("minecraft:");
                    string type = S(s.Def, "type") ?? "";
                    structures.Add(Fields(
                        new object[] { "id", s.Id, "name", NameOf(w, "structure", s.Id), "type", type.Substring(type.IndexOf(':') + 1), "off", off.Structures.Contains(s.Id),
                            "config", vanilla || s.Jar == null ? null : ConfigFor(configs, memo, OwnersOf(s.Jar, s.Id), PathTail(s.Id), StructureConfigWords, "structure") },
                        SourceFields(vanilla ? null : s.Jar),
                        PlaceFields(w, DescribeBiomes(w, Get(s.Def, "biomes"))),
                        place.SelectMany(kv => new[] { kv.Key, kv.Value }).ToArray()));
                }

                // Each biome's own lists, the mods' versions first.
                var biomeSpawns = new Dictionary<string, List<SpawnRule>>(StringComparer.Ordinal);
                var biomeFeatures = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                foreach (JarFeatures data in w.Jars.Select(j => j.Data).Concat(w.Base))
                {
                    foreach (var kv in data.BiomeSpawns) if (!biomeSpawns.ContainsKey(kv.Key)) biomeSpawns[kv.Key] = kv.Value;
                    foreach (var kv in data.BiomeFeatures) if (!biomeFeatures.ContainsKey(kv.Key)) biomeFeatures[kv.Key] = kv.Value;
                }
                var listed = new Dictionary<string, List<SpawnRule>>(StringComparer.Ordinal); // entity -> its entries in biomes' spawn lists
                foreach (SpawnRule s in biomeSpawns.Values.SelectMany(x => x))
                {
                    List<SpawnRule> list;
                    if (!listed.TryGetValue(s.Entity, out list)) listed[s.Entity] = list = new List<SpawnRule>();
                    list.Add(s);
                }

                // Mobs: each modded entity once, with every rule (from any mod) that spawns it.
                var rules = new Dictionary<string, List<KeyValuePair<FeatureJar, SpawnRule>>>(StringComparer.Ordinal);
                foreach (FeatureJar j in w.Jars)
                    foreach (SpawnRule r in j.Data.Spawns)
                    {
                        List<KeyValuePair<FeatureJar, SpawnRule>> list;
                        if (!rules.TryGetValue(r.Entity, out list)) rules[r.Entity] = list = new List<KeyValuePair<FeatureJar, SpawnRule>>();
                        list.Add(new KeyValuePair<FeatureJar, SpawnRule>(j, r));
                    }
                // A mob belongs to the mod whose id is its namespace; add-ons carrying its loot tables (or spawning it) don't count.
                var owner = new Dictionary<string, FeatureJar>(StringComparer.Ordinal);
                Func<string, FeatureJar> modOf = e =>
                {
                    string ns = e.Substring(0, e.IndexOf(':'));
                    return w.Jars.FirstOrDefault(j => j.Meta.Mods.Concat(j.Meta.Nested).Any(m => m.Id == ns));
                };
                foreach (FeatureJar j in w.Jars)
                    foreach (string e in j.Data.Entities) if (!e.StartsWith("minecraft:") && !owner.ContainsKey(e)) owner[e] = modOf(e) ?? j;
                foreach (KeyValuePair<string, List<KeyValuePair<FeatureJar, SpawnRule>>> r in rules)
                    if (!r.Key.StartsWith("minecraft:") && !owner.ContainsKey(r.Key)) owner[r.Key] = modOf(r.Key) ?? r.Value[0].Key;
                foreach (string e in listed.Keys)
                {
                    if (e.StartsWith("minecraft:") || owner.ContainsKey(e)) continue;
                    FeatureJar by = modOf(e); // else the mod whose biome lists it
                    if (by == null) w.BiomeOwner.TryGetValue(listed[e][0].Path, out by);
                    if (by != null) owner[e] = by;
                }
                // Modded mobs (spawned by rules or listed in biomes), then the game's own; a mob is everywhere it's listed or added.
                var mobIds = owner.Keys.Concat(listed.Keys.Where(e => e.StartsWith("minecraft:")).OrderBy(e => e, StringComparer.Ordinal)).ToList();
                foreach (string id in mobIds)
                {
                    FeatureJar jar = id.StartsWith("minecraft:") ? null : owner[id];
                    List<KeyValuePair<FeatureJar, SpawnRule>> mine;
                    rules.TryGetValue(id, out mine);
                    // Vanilla mobs' add_spawns from mods have rows of their own (below), so they aren't counted here.
                    if (jar == null || mine == null) mine = new List<KeyValuePair<FeatureJar, SpawnRule>>();
                    List<SpawnRule> inBiomes;
                    if (!listed.TryGetValue(id, out inBiomes)) inBiomes = new List<SpawnRule>();
                    var set = new BiomeSet();
                    foreach (var r in mine) AddBiomes(w, r.Value.Biomes, set);
                    foreach (SpawnRule r in inBiomes) set.Ids.Add(r.Path);
                    string ns = id.Substring(0, id.IndexOf(':'));
                    bool any = mine.Count > 0 || inBiomes.Count > 0;
                    mobs.Add(Fields(
                        new object[] { "id", id, "name", NameOf(w, "entity", id), "off", off.Mobs.Contains(id),
                            "spawns", SpawnFields(mine).Concat(BiomeSpawnFields(inBiomes)).ToList(),
                            "category", inBiomes.Select(r => r.Category).Concat(mine.Select(r => r.Value.Category)).FirstOrDefault(c => c != null),
                            "ownRules", !any && w.Jars.Any(j => j.Data.OwnSpawnTypes.Contains(ns)),
                            "config", jar == null ? null : ConfigFor(configs, memo, OwnersOf(jar, id), PathTail(id), MobConfigWords, "spawn") },
                        SourceFields(jar),
                        any ? PlaceFields(w, set) : new object[] { "dims", new List<string>(), "biomes", new List<object>(), "allBiomes", false, "except", null }));
                }
                // A mod's extra spawns of a vanilla mob: one row per mob and mod, turned off rule by rule.
                foreach (KeyValuePair<string, List<KeyValuePair<FeatureJar, SpawnRule>>> r in rules.Where(x => x.Key.StartsWith("minecraft:")))
                    foreach (var byMod in r.Value.GroupBy(x => x.Value.Ns))
                    {
                        var list = byMod.ToList();
                        var set = new BiomeSet();
                        foreach (var x in list) AddBiomes(w, x.Value.Biomes, set);
                        string id = r.Key + "@" + byMod.Key;
                        mobs.Add(Fields(
                            new object[] { "id", id, "entity", r.Key, "name", NameOf(w, "entity", r.Key), "file", list[0].Key.File.Name, "key", NormalizeKey(list[0].Key.File.Name),
                                "mod", ModNameOf(list[0].Key), "extra", true, "off", off.Mobs.Contains(id), "spawns", SpawnFields(list),
                                "config", ConfigFor(configs, memo, new HashSet<string>(ModIdsOf(list[0].Key).Concat(new[] { byMod.Key }), StringComparer.OrdinalIgnoreCase), PathTail(r.Key), MobConfigWords, "spawn") },
                            PlaceFields(w, set)));
                    }

                // Ores: placed features the mods add to biomes (add_features) whose configured feature is an ore.
                var placedAll = new Dictionary<string, KeyValuePair<FeatureJar, Dictionary<string, object>>>(StringComparer.Ordinal);
                var configuredAll = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
                foreach (FeatureJar j in w.Jars)
                {
                    foreach (var p in j.Data.Placed) if (!placedAll.ContainsKey(p.Key)) placedAll[p.Key] = new KeyValuePair<FeatureJar, Dictionary<string, object>>(j, p.Value);
                    foreach (var c in j.Data.Configured) if (!configuredAll.ContainsKey(c.Key)) configuredAll[c.Key] = c.Value;
                }
                foreach (JarFeatures b in w.Base)
                {
                    foreach (var p in b.Placed) if (!placedAll.ContainsKey(p.Key)) placedAll[p.Key] = new KeyValuePair<FeatureJar, Dictionary<string, object>>(null, p.Value);
                    foreach (var c in b.Configured) if (!configuredAll.ContainsKey(c.Key)) configuredAll[c.Key] = c.Value;
                }
                var oreRules = new Dictionary<string, List<KeyValuePair<FeatureJar, FeatureRule>>>(StringComparer.Ordinal);
                foreach (FeatureJar j in w.Jars)
                    foreach (FeatureRule rule in j.Data.FeatureRules)
                        foreach (string fid in FeatureIdsOf(rule.Features))
                        {
                            List<KeyValuePair<FeatureJar, FeatureRule>> list;
                            if (!oreRules.TryGetValue(fid, out list)) oreRules[fid] = list = new List<KeyValuePair<FeatureJar, FeatureRule>>();
                            list.Add(new KeyValuePair<FeatureJar, FeatureRule>(j, rule));
                        }
                // Ores biomes list themselves (all of vanilla's, and modded biomes' own).
                var oreBiomes = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                foreach (var kv in biomeFeatures)
                    foreach (string fid in kv.Value)
                    {
                        HashSet<string> set;
                        if (!oreBiomes.TryGetValue(fid, out set)) oreBiomes[fid] = set = new HashSet<string>(StringComparer.Ordinal);
                        set.Add(kv.Key);
                    }
                var ores = new List<object>();
                foreach (string fid in oreRules.Keys.Union(oreBiomes.Keys).OrderBy(x => x, StringComparer.Ordinal))
                {
                    KeyValuePair<FeatureJar, Dictionary<string, object>> pf;
                    if (!placedAll.TryGetValue(fid, out pf)) continue;
                    object feature = Get(pf.Value, "feature");
                    Dictionary<string, object> configured = feature is string ? (configuredAll.ContainsKey(NormId((string)feature)) ? configuredAll[NormId((string)feature)] : null) : D(feature);
                    if (configured == null || !OreType.IsMatch("\"type\":\"" + (S(configured, "type") ?? "") + "\"")) continue;
                    List<KeyValuePair<FeatureJar, FeatureRule>> byRule;
                    if (!oreRules.TryGetValue(fid, out byRule)) byRule = new List<KeyValuePair<FeatureJar, FeatureRule>>();
                    // The game's own ores are Minecraft's even where a mod adds them to more biomes.
                    FeatureJar jar = pf.Key ?? (fid.StartsWith("minecraft:") || byRule.Count == 0 ? null : byRule[0].Key);
                    var set = new BiomeSet();
                    foreach (var r in byRule) AddBiomes(w, r.Value.Biomes, set);
                    HashSet<string> listedIn;
                    if (oreBiomes.TryGetValue(fid, out listedIn)) set.Ids.UnionWith(listedIn);
                    object[] details = OreFields(w, pf.Value, configured);
                    if (details == null) continue;
                    var blocks = (List<object>)details[1];
                    string name = blocks.Count > 0 ? (string)((Dictionary<string, object>)blocks[0])["name"] : FeatureTitle(PathTail(fid));
                    if (blocks.Count > 0)
                    {
                        // Several placements of one ore read apart by what their id adds: ore_diamond_large is "Diamond Ore (Large)".
                        string[] blockWords = PathTail((string)((Dictionary<string, object>)blocks[0])["id"]).Split('_');
                        List<string> extra = PathTail(fid).Split('_').Where(x => x.Length > 0 && x != "ore" && x != "ores" && !blockWords.Contains(x)).ToList();
                        if (extra.Count > 0) name += " (" + FeatureTitle(string.Join(" ", extra)) + ")";
                    }
                    ores.Add(Fields(
                        new object[] { "id", fid, "name", name, "off", off.Ores.Contains(fid),
                            "config", jar == null ? null : ConfigFor(configs, memo, OwnersOf(jar, fid), PathTail(fid), OreConfigWords, "ore") },
                        SourceFields(jar), PlaceFields(w, set), details));
                }

                // Biomes: the mods' own and the game's. They can't be switched off from data (mods place them with
                // code, TerraBlender and the like), so they carry their config instead, where a mod has one.
                foreach (KeyValuePair<string, FeatureJar> b in w.BiomeOwner.OrderBy(x => x.Key, StringComparer.Ordinal))
                {
                    HashSet<string> owners = OwnersOf(b.Value, b.Key);
                    Dictionary<string, object> sw = BiomeSwitch(configs, memo, owners, b.Key);
                    biomes.Add(Obj("id", b.Key, "name", NameOf(w, "biome", b.Key), "file", b.Value.File.Name, "key", NormalizeKey(b.Value.File.Name), "mod", ModNameOf(b.Value),
                        "dims", new List<string> { DimName(w, DimOf(w, b.Key)) }, "setting", sw, "off", sw != null && !(bool)sw["on"],
                        "config", sw != null ? Obj("side", sw["side"], "path", sw["path"], "section", Get(sw, "section"), "key", (string)sw["format"] == "toml" ? sw["key"] : null)
                            : ConfigFor(configs, memo, owners, PathTail(b.Key), BiomeConfigWords, "biome")));
                }
                foreach (string b in w.VanillaBiomes.OrderBy(x => x, StringComparer.Ordinal))
                    biomes.Add(Obj("id", b, "name", NameOf(w, "biome", b), "mod", "Minecraft", "vanilla", true, "dims", new List<string> { DimName(w, DimOf(w, b)) }, "off", false));

                // Which biomes each shared group holds, for filtering by biome.
                var groups = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (string g in w.UsedGroups) groups["#" + g] = GroupMembers(w, g);

                var known = new HashSet<string>(structures.Concat(mobs).Concat(ores).Select(o => (string)((Dictionary<string, object>)o)["id"]), StringComparer.Ordinal);
                string style = FeatureStyle();
                return Obj("ok", true, "supported", style != null, "loader", LoaderName(), "gameRunning", GameRunning(GameDir()),
                    "jarFile", FeatureJarName, "jarExists", File.Exists(Path.Combine(modsDir, FeatureJarName)),
                    "structures", structures, "mobs", mobs, "ores", ores, "biomes", biomes, "groups", groups, "vanillaData", w.VanillaBiomes.Count > 0,
                    "missingOff", off.Structures.Concat(off.Mobs).Concat(off.Ores).Where(id => !known.Contains(id)).ToList());
            }
        }

        static List<object> SpawnFields(List<KeyValuePair<FeatureJar, SpawnRule>> rules)
        {
            return rules.Select(r => (object)Obj("weight", r.Value.Weight, "min", r.Value.Min, "max", r.Value.Max, "where", r.Key.File.Name)).ToList();
        }

        // Biomes' own spawn lists, one entry per weight and group size ("in 12 biomes").
        static IEnumerable<object> BiomeSpawnFields(List<SpawnRule> entries)
        {
            return entries.GroupBy(r => r.Weight + "|" + r.Min + "|" + r.Max).OrderByDescending(g => g.Count()).Select(g =>
                (object)Obj("weight", g.First().Weight, "min", g.First().Min, "max", g.First().Max, "where", "in " + g.Count() + (g.Count() == 1 ? " biome" : " biomes")));
        }

        // Where a row comes from: a mod's jar, or Minecraft itself.
        static object[] SourceFields(FeatureJar jar)
        {
            return jar == null ? new object[] { "file", null, "key", null, "mod", "Minecraft", "vanilla", true }
                : new object[] { "file", jar.File.Name, "key", NormalizeKey(jar.File.Name), "mod", ModNameOf(jar) };
        }

        // ---------- Turning features on and off ----------

        static Dictionary<string, object> SetFeatures(string kind, List<string> ids, bool on)
        {
            if (kind == "biomes") return SetBiomes(ids, on);
            if (kind != "structures" && kind != "mobs" && kind != "ores") throw new UserError("Unknown kind of feature: " + kind);
            if (ids == null || ids.Count == 0) throw new UserError("Nothing was selected.");
            if (FeatureStyle() == null) throw new UserError("Turning features off needs NeoForge or Forge. " + LoaderName() + " has no data-only way to do it.");
            if (GameRunning(GameDir())) throw new UserError("Close the game first. It keeps the mods' files open, and the change only applies when it starts.");
            lock (FeatureLock)
            {
                FeaturesOff off = ReadFeaturesOff();
                SortedSet<string> list = kind == "structures" ? off.Structures : kind == "ores" ? off.Ores : off.Mobs;
                int changed = ids.Count(id => on ? list.Remove(id) : list.Add(id));
                if (changed > 0) WriteFeatureJar(off, BuildFeatureWorld());
                if (changed > 0)
                {
                    Log("Features  " + (on ? "turned on " : "turned off ") + changed + " " + kind.TrimEnd('s') + (changed == 1 ? "" : "s") + ": " + string.Join(", ", ids.Take(12)) + (ids.Count > 12 ? ", …" : ""));
                    if (window != null && !window.IsDisposed)
                        try { window.BeginInvoke((Action)(() => { if (!window.IsDisposed) window.Notify("refresh"); })); } catch (InvalidOperationException) { }
                }
                return Obj("ok", true, "changed", changed, "jarExists", File.Exists(Path.Combine(modsDir, FeatureJarName)));
            }
        }

        static void WriteFeatureJar(FeaturesOff off, FeatureWorld w)
        {
            string path = Path.Combine(modsDir, FeatureJarName);
            if (off.Structures.Count == 0 && off.Mobs.Count == 0 && off.Ores.Count == 0)
            {
                try { if (File.Exists(path)) File.Delete(path); }
                catch (IOException) { throw new UserError("Couldn't remove " + FeatureJarName + ". Close the game, then try again."); }
                return;
            }
            string style = FeatureStyle();
            string modifierDir = style + "/biome_modifier/";
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var after = new SortedSet<string>(StringComparer.Ordinal);

            foreach (string id in off.Structures)
            {
                // The version the game uses: a mod's (loaded after it, so ours wins), else the game's own.
                FeatureJar j = w.Jars.FirstOrDefault(x => x.Data.Structures.ContainsKey(id));
                JarFeatures data = j != null ? j.Data : w.Base.FirstOrDefault(x => x.Structures.ContainsKey(id));
                if (data == null) continue; // its mod is gone; it stays on the list in case it comes back
                var copy = new Dictionary<string, object>(data.Structures[id]);
                copy["biomes"] = new object[0];
                int c = id.IndexOf(':');
                files["data/" + id.Substring(0, c) + "/worldgen/structure/" + id.Substring(c + 1) + ".json"] = json.Serialize(copy);
                if (j != null) after.UnionWith(ModIdsOf(j));
            }
            foreach (string id in off.Mobs)
            {
                int at = id.IndexOf('@');
                if (at < 0)
                {
                    int c = id.IndexOf(':');
                    files["data/" + FeatureModId + "/" + modifierDir + "off/" + id.Substring(0, c) + "/" + id.Substring(c + 1) + ".json"] = json.Serialize(Obj(
                        "type", style + ":remove_spawns", "biomes", Obj("type", style + ":any"), "entity_types", id));
                    continue;
                }
                string entity = id.Substring(0, at), ns = id.Substring(at + 1);
                foreach (FeatureJar j in w.Jars)
                    foreach (SpawnRule r in j.Data.Spawns.Where(x => x.Entity == entity && x.Ns == ns))
                    {
                        files[r.Path] = json.Serialize(Obj("type", style + ":none"));
                        after.UnionWith(ModIdsOf(j));
                    }
            }
            // An ore is taken back out of every biome after the mods have added it (remove_features runs after add_features).
            foreach (string id in off.Ores)
            {
                int c = id.IndexOf(':');
                files["data/" + FeatureModId + "/" + modifierDir + "off/ores/" + id.Substring(0, c) + "/" + id.Substring(c + 1) + ".json"] = json.Serialize(Obj(
                    "type", style + ":remove_features", "biomes", Obj("type", style + ":any"), "features", id));
            }
            files[FeatureListEntry] = json.Serialize(Obj("structures", off.Structures.ToList(), "mobs", off.Mobs.ToList(), "ores", off.Ores.ToList()));
            files["pack.mcmeta"] = json.Serialize(Obj("pack", Obj("description", "Features turned off in Minecraft Mod Manager", "pack_format", DataPackFormat())));
            files["META-INF/neoforge.mods.toml"] = FeatureModsToml(after, true);
            files["META-INF/mods.toml"] = FeatureModsToml(after, false);

            string temp = path + ".tmp";
            using (var zip = new ZipArchive(new FileStream(temp, FileMode.Create, FileAccess.Write), ZipArchiveMode.Create))
                foreach (KeyValuePair<string, string> f in files)
                    using (var writer = new StreamWriter(zip.CreateEntry(f.Key).Open(), new UTF8Encoding(false))) writer.Write(f.Value);
            try
            {
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
            }
            catch (IOException)
            {
                try { File.Delete(temp); } catch (IOException) { }
                throw new UserError("Couldn't replace " + FeatureJarName + ". Close the game, then try again.");
            }
        }

        // neoforge.mods.toml says optional with type=; Forge's mods.toml with mandatory=. Both load it after the mods it overrides.
        static string FeatureModsToml(IEnumerable<string> after, bool neo)
        {
            var sb = new StringBuilder();
            sb.Append("modLoader=\"lowcodefml\"\nloaderVersion=\"[1,)\"\nlicense=\"CC0-1.0\"\n\n[[mods]]\nmodId=\"" + FeatureModId + "\"\nversion=\"1.0.0\"\n");
            sb.Append("displayName=\"Feature toggles\"\nauthors=\"Minecraft Mod Manager\"\n");
            sb.Append("description='''Structures and mob spawns turned off in Minecraft Mod Manager's Features tab. Written by the app; turn them back on there.'''\n");
            foreach (string id in after)
            {
                sb.Append("\n[[dependencies." + FeatureModId + "]]\nmodId=\"" + id + "\"\n");
                sb.Append(neo ? "type=\"optional\"\n" : "mandatory=false\n");
                sb.Append("versionRange=\"*\"\nordering=\"AFTER\"\nside=\"BOTH\"\n");
            }
            return sb.ToString();
        }

        static int DataPackFormat()
        {
            string v = gameVersion ?? "";
            if (v.StartsWith("1.20.1") || v == "1.20") return 15;
            if (v.StartsWith("1.20.2")) return 18;
            if (v.StartsWith("1.20.3") || v.StartsWith("1.20.4")) return 26;
            if (v.StartsWith("1.20.5") || v.StartsWith("1.20.6")) return 41;
            if (v.StartsWith("1.21.2") || v.StartsWith("1.21.3")) return 57;
            if (v.StartsWith("1.21.4")) return 61;
            if (v.StartsWith("1.21.5")) return 71;
            return 48; // 1.21 and 1.21.1
        }
    }
}
