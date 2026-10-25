// Features: the structures and mobs the mods add, where they turn up (dimension and biomes), and a switch to
// turn each one off. Everything is read from the jars' own data: structures from worldgen/structure and the
// structure sets that place them, mobs from their loot tables and spawn eggs, spawns from the biome modifiers
// (add_spawns), biome groups from the biome tags, and modded dimensions from their dimension files.
//
// Turning something off writes one small data-only mod into the mods folder (FeatureJarName). It carries:
//   - a copy of each turned-off structure with no biomes, so it never generates in new chunks;
//   - a remove_spawns rule for each turned-off modded mob, covering every biome;
//   - a "none" modifier in place of a mod's extra spawn rule for a vanilla mob (a mod adding End phantoms).
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
        public string Path, Ns, Entity; // Path: the file inside the jar; Ns: the namespace it's in
        public int Weight, Min, Max;
        public object Biomes;
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
        static readonly Regex LangLine = new Regex(@"""((?:entity|biome|dimension|structure)\.[a-z0-9_.-]+\.[a-z0-9_./-]+)""\s*:\s*""((?:[^""\\]|\\.)*)""");
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
                if ((r = Regex.Match(rest, @"^worldgen/biome/(.+)$")).Success) { jf.Biomes.Add(ns + ":" + r.Groups[1].Value); continue; }
                bool structure = rest.StartsWith("worldgen/structure/", StringComparison.Ordinal);
                bool set = rest.StartsWith("worldgen/structure_set/", StringComparison.Ordinal);
                bool tag = rest.StartsWith("tags/worldgen/biome/", StringComparison.Ordinal);
                bool dim = rest.StartsWith("dimension/", StringComparison.Ordinal);
                Match bm = Regex.Match(rest, @"^(?:neoforge|forge)/biome_modifiers?/");
                if (!structure && !set && !tag && !dim && !bm.Success) continue;
                Dictionary<string, object> d;
                try { d = json.DeserializeObject(ReadEntryText(e)) as Dictionary<string, object>; }
                catch (Exception) { continue; }
                if (d == null) continue;
                if (structure) jf.Structures[ns + ":" + rest.Substring("worldgen/structure/".Length)] = d;
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
            public readonly Dictionary<string, List<object>> Tags = new Dictionary<string, List<object>>(StringComparer.Ordinal);
            public readonly HashSet<string> Biomes = new HashSet<string>(StringComparer.Ordinal);
            public readonly Dictionary<string, string> Names = new Dictionary<string, string>(StringComparer.Ordinal);
            public readonly Dictionary<string, string> DimOfBiome = new Dictionary<string, string>(StringComparer.Ordinal);
            public readonly Dictionary<string, BiomeSet> Expanded = new Dictionary<string, BiomeSet>(StringComparer.Ordinal);
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
            foreach (FeatureJar j in w.Jars)
            {
                foreach (KeyValuePair<string, List<object>> t in j.Data.BiomeTags)
                {
                    List<object> list;
                    if (!w.Tags.TryGetValue(t.Key, out list)) w.Tags[t.Key] = list = new List<object>();
                    list.AddRange(t.Value);
                }
                w.Biomes.UnionWith(j.Data.Biomes);
                foreach (KeyValuePair<string, string> n in j.Data.Names) if (!w.Names.ContainsKey(n.Key)) w.Names[n.Key] = n.Value;
                foreach (KeyValuePair<string, string> b in j.Data.DimensionOfBiome) if (!w.DimOfBiome.ContainsKey(b.Key)) w.DimOfBiome[b.Key] = b.Value;
            }
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
                if (!required && !id.StartsWith("minecraft:") && !w.Biomes.Contains(id)) continue; // a biome from a mod that isn't here
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
            foreach (string g in set.Groups) dims.Add(DimOfGroup(g));
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

        // ---------- What's off ----------

        sealed class FeaturesOff
        {
            public readonly SortedSet<string> Structures = new SortedSet<string>(StringComparer.Ordinal);
            public readonly SortedSet<string> Mobs = new SortedSet<string>(StringComparer.Ordinal);
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

                // Where each structure is placed: its structure set's spacing, and its share when the set picks among several.
                var placed = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
                foreach (FeatureJar j in w.Jars)
                    foreach (KeyValuePair<string, Dictionary<string, object>> s in j.Data.Sets)
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

                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (FeatureJar j in w.Jars)
                    foreach (KeyValuePair<string, Dictionary<string, object>> s in j.Data.Structures)
                    {
                        Dictionary<string, object> place;
                        // Vanilla's own (and mods' copies of them) aren't modded features; unplaced ones are only parts of others.
                        if (s.Key.StartsWith("minecraft:") || !placed.TryGetValue(s.Key, out place) || !seen.Add(s.Key)) continue;
                        string type = S(s.Value, "type") ?? "";
                        structures.Add(Fields(
                            new object[] { "id", s.Key, "name", NameOf(w, "structure", s.Key), "file", j.File.Name, "key", NormalizeKey(j.File.Name), "mod", ModNameOf(j),
                                "type", type.Substring(type.IndexOf(':') + 1), "off", off.Structures.Contains(s.Key) },
                            PlaceFields(w, DescribeBiomes(w, Get(s.Value, "biomes"))),
                            place.SelectMany(kv => new[] { kv.Key, kv.Value }).ToArray()));
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
                foreach (KeyValuePair<string, FeatureJar> e in owner)
                {
                    List<KeyValuePair<FeatureJar, SpawnRule>> mine;
                    rules.TryGetValue(e.Key, out mine);
                    mine = mine ?? new List<KeyValuePair<FeatureJar, SpawnRule>>();
                    var set = new BiomeSet();
                    foreach (var r in mine) AddBiomes(w, r.Value.Biomes, set);
                    string ns = e.Key.Substring(0, e.Key.IndexOf(':'));
                    mobs.Add(Fields(
                        new object[] { "id", e.Key, "name", NameOf(w, "entity", e.Key), "file", e.Value.File.Name, "key", NormalizeKey(e.Value.File.Name), "mod", ModNameOf(e.Value),
                            "off", off.Mobs.Contains(e.Key), "spawns", SpawnFields(mine), "ownRules", mine.Count == 0 && w.Jars.Any(j => j.Data.OwnSpawnTypes.Contains(ns)) },
                        mine.Count > 0 ? PlaceFields(w, set) : new object[] { "dims", new List<string>(), "biomes", new List<object>(), "allBiomes", false, "except", null }));
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
                                "mod", ModNameOf(list[0].Key), "vanilla", true, "off", off.Mobs.Contains(id), "spawns", SpawnFields(list) },
                            PlaceFields(w, set)));
                    }

                var known = new HashSet<string>(structures.Concat(mobs).Select(o => (string)((Dictionary<string, object>)o)["id"]), StringComparer.Ordinal);
                string style = FeatureStyle();
                return Obj("ok", true, "supported", style != null, "loader", LoaderName(), "gameRunning", GameRunning(GameDir()),
                    "jarFile", FeatureJarName, "jarExists", File.Exists(Path.Combine(modsDir, FeatureJarName)),
                    "structures", structures, "mobs", mobs,
                    "missingOff", off.Structures.Concat(off.Mobs).Where(id => !known.Contains(id)).ToList());
            }
        }

        static List<object> SpawnFields(List<KeyValuePair<FeatureJar, SpawnRule>> rules)
        {
            return rules.Select(r => (object)Obj("weight", r.Value.Weight, "min", r.Value.Min, "max", r.Value.Max, "file", r.Key.File.Name)).ToList();
        }

        // ---------- Turning features on and off ----------

        static Dictionary<string, object> SetFeatures(string kind, List<string> ids, bool on)
        {
            if (kind != "structures" && kind != "mobs") throw new UserError("Unknown kind of feature: " + kind);
            if (ids == null || ids.Count == 0) throw new UserError("Nothing was selected.");
            if (FeatureStyle() == null) throw new UserError("Turning features off needs NeoForge or Forge. " + LoaderName() + " has no data-only way to do it.");
            if (GameRunning(GameDir())) throw new UserError("Close the game first. It keeps the mods' files open, and the change only applies when it starts.");
            lock (FeatureLock)
            {
                FeaturesOff off = ReadFeaturesOff();
                SortedSet<string> list = kind == "structures" ? off.Structures : off.Mobs;
                int changed = ids.Count(id => on ? list.Remove(id) : list.Add(id));
                if (changed > 0) WriteFeatureJar(off, BuildFeatureWorld());
                if (changed > 0)
                {
                    Log("Features  " + (on ? "turned on " : "turned off ") + changed + " " + (kind == "structures" ? "structure" : "mob") + (changed == 1 ? "" : "s") + ": " + string.Join(", ", ids.Take(12)) + (ids.Count > 12 ? ", …" : ""));
                    if (window != null && !window.IsDisposed)
                        try { window.BeginInvoke((Action)(() => { if (!window.IsDisposed) window.Notify("refresh"); })); } catch (InvalidOperationException) { }
                }
                return Obj("ok", true, "changed", changed, "jarExists", File.Exists(Path.Combine(modsDir, FeatureJarName)));
            }
        }

        static void WriteFeatureJar(FeaturesOff off, FeatureWorld w)
        {
            string path = Path.Combine(modsDir, FeatureJarName);
            if (off.Structures.Count == 0 && off.Mobs.Count == 0)
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
                FeatureJar j = w.Jars.FirstOrDefault(x => x.Data.Structures.ContainsKey(id));
                if (j == null) continue; // its mod is gone; it stays on the list in case it comes back
                var copy = new Dictionary<string, object>(j.Data.Structures[id]);
                copy["biomes"] = new object[0];
                int c = id.IndexOf(':');
                files["data/" + id.Substring(0, c) + "/worldgen/structure/" + id.Substring(c + 1) + ".json"] = json.Serialize(copy);
                after.UnionWith(ModIdsOf(j));
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
            files[FeatureListEntry] = json.Serialize(Obj("structures", off.Structures.ToList(), "mobs", off.Mobs.ToList()));
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
