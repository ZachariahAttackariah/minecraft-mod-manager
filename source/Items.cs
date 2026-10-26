// Items and recipes (two more kinds on the Features page). Items are read from the jars' item models and
// language files; recipes from their recipe data, with the conditions a recipe loads under worked out from
// the installed mods (a compat recipe for a mod that isn't here never loads, so it isn't listed). Where
// several jars carry the same recipe, the one that isn't its namespace's own mod is taken as the override.
//
// An item's switch is whether JEI shows it. That lives in JEI's own config/jei/blacklist.json, the file its
// edit mode writes, so hiding here is the same as hiding in game. A recipe's switch turns it off for real:
// the feature-toggles jar (Features.cs) carries a copy of it with a condition that's never met, so the game
// skips it, and JEI with it.
//
// Icons are the items' own textures: an item model's layer0, else a face of the block model it points to.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace MinecraftModManager
{
    sealed class RecipeDef
    {
        public string Folder, Type, Result;
        public int Count;
        public bool Fluid, Empty;
        public List<string> Inputs;
        public object Conditions;
    }

    // What one jar carries for items and recipes (its bundled jars' items and recipes too).
    sealed class JarItems
    {
        public readonly Dictionary<string, Dictionary<string, object>> Models = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal); // item id -> its item model
        public readonly Dictionary<string, RecipeDef> Recipes = new Dictionary<string, RecipeDef>(StringComparer.Ordinal);
        public readonly Dictionary<string, string> Names = new Dictionary<string, string>(StringComparer.Ordinal); // item.* and block.* lang keys
        public readonly HashSet<string> Assets = new HashSet<string>(StringComparer.Ordinal); // models and textures in the jar itself, for icons
    }

    static partial class Program
    {
        static readonly Dictionary<string, JarItems> JarItemsCache = new Dictionary<string, JarItems>();
        static readonly object ItemLock = new object();
        static readonly Dictionary<string, byte[]> ItemIconCache = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        static string itemIconStamp; // the mods folder's state the icon cache belongs to

        static readonly Regex ItemModelEntry = new Regex(@"^assets/([a-z0-9_.-]+)/models/item/([a-z0-9_.-]+)\.json$");
        static readonly Regex RecipeEntry = new Regex(@"^data/([a-z0-9_.-]+)/(recipes?)/([a-z0-9_./-]+)\.json$");
        static readonly Regex AssetEntry = new Regex(@"^assets/[a-z0-9_.-]+/(models|textures)/");
        static readonly Regex ItemLangLine = new Regex(@"""((?:item|block)\.[a-z0-9_.-]+\.[a-z0-9_./-]+)""\s*:\s*""((?:[^""\\]|\\.)*)""");
        static readonly string[] IngredientFields = { "ingredients", "ingredient", "key", "input", "inputs", "base", "addition", "template" };
        static readonly HashSet<string> NotIngredientKeys = new HashSet<string> { "type", "count", "amount", "chance", "neoforge:conditions", "conditions", "components", "nbt" };

        // ---------- Reading the jars ----------

        static JarItems GetJarItems(FileInfo file)
        {
            string key = file.FullName + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks;
            lock (JarItemsCache)
            {
                JarItems cached;
                if (JarItemsCache.TryGetValue(key, out cached)) return cached;
            }
            var ji = new JarItems();
            try { using (ZipArchive zip = ZipFile.OpenRead(file.FullName)) ReadItemEntries(zip, ji, 0); }
            catch (Exception) { } // a broken jar just adds nothing
            lock (JarItemsCache) JarItemsCache[key] = ji;
            return ji;
        }

        static void ReadItemEntries(ZipArchive zip, JarItems ji, int depth)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            foreach (ZipArchiveEntry e in zip.Entries)
            {
                string n = e.FullName;
                if (depth == 0 && n.StartsWith("META-INF/jarjar/", StringComparison.Ordinal) && n.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) && e.Length < 64L << 20)
                {
                    try
                    {
                        var mem = new MemoryStream();
                        using (Stream s = e.Open()) s.CopyTo(mem);
                        mem.Position = 0;
                        using (var inner = new ZipArchive(mem, ZipArchiveMode.Read)) ReadItemEntries(inner, ji, depth + 1);
                    }
                    catch (Exception) { }
                    continue;
                }
                if (depth == 0 && AssetEntry.IsMatch(n)) ji.Assets.Add(n);
                Match m = ItemModelEntry.Match(n);
                if (m.Success)
                {
                    string id = m.Groups[1].Value + ":" + m.Groups[2].Value;
                    if (ji.Models.ContainsKey(id) || e.Length > 1L << 20) continue;
                    try { ji.Models[id] = json.DeserializeObject(ReadEntryText(e)) as Dictionary<string, object> ?? new Dictionary<string, object>(); }
                    catch (Exception) { ji.Models[id] = new Dictionary<string, object>(); }
                    continue;
                }
                if (LangEntry.IsMatch(n))
                {
                    if (e.Length > 8L << 20) continue;
                    foreach (Match line in ItemLangLine.Matches(ReadEntryText(e)))
                        if (!ji.Names.ContainsKey(line.Groups[1].Value))
                            try { ji.Names[line.Groups[1].Value] = json.Deserialize<string>("\"" + line.Groups[2].Value + "\""); } catch (Exception) { }
                    continue;
                }
                m = RecipeEntry.Match(n);
                if (!m.Success || e.Length > 1L << 20) continue;
                string rid = m.Groups[1].Value + ":" + m.Groups[3].Value;
                if (ji.Recipes.ContainsKey(rid)) continue;
                try
                {
                    var d = json.DeserializeObject(ReadEntryText(e)) as Dictionary<string, object>;
                    if (d != null) ji.Recipes[rid] = ReadRecipe(m.Groups[2].Value, d);
                }
                catch (Exception) { }
            }
        }

        static RecipeDef ReadRecipe(string folder, Dictionary<string, object> d)
        {
            var r = new RecipeDef { Folder = folder, Type = S(d, "type"), Inputs = new List<string>(),
                Conditions = Get(d, "neoforge:conditions") ?? Get(d, "conditions") ?? Get(d, "forge:conditions") };
            if (string.IsNullOrEmpty(r.Type)) { r.Empty = true; return r; } // only a condition: another mod's way of removing a recipe
            r.Type = NormId(r.Type);
            object result = Get(d, "result") ?? FirstOf(Get(d, "results")) ?? Get(d, "output") ?? FirstOf(Get(d, "outputs")) ?? Get(d, "result_item");
            string id;
            int count;
            bool fluid;
            StackOf(result, out id, out count, out fluid);
            r.Result = id;
            r.Count = count;
            r.Fluid = fluid;
            foreach (string field in IngredientFields) CollectIngredients(Get(d, field), r.Inputs, 0);
            return r;
        }

        static object FirstOf(object o) { object[] a = o as object[]; return a != null && a.Length > 0 ? a[0] : null; }

        // A recipe's result in any of the shapes mods write it: "id", {"id"}, {"item"}, {"item": {"id"}}, a fluid.
        static void StackOf(object o, out string id, out int count, out bool fluid)
        {
            id = null; count = 1; fluid = false;
            if (o is string) { if (!((string)o).StartsWith("#")) id = NormId((string)o); return; }
            Dictionary<string, object> d = D(o);
            if (d == null) return;
            object item = Get(d, "item");
            id = S(d, "id") ?? (item as string) ?? S(D(item), "id");
            if (id == null && S(d, "fluid") != null) { id = S(d, "fluid"); fluid = true; }
            if (id != null) id = NormId(id);
            int n = IntOf(Get(d, fluid ? "amount" : "count"));
            if (n > 0) count = n;
        }

        // The items and tags a recipe takes, in the order they first appear (12 at most).
        static void CollectIngredients(object node, List<string> into, int depth)
        {
            if (node == null || depth > 6 || into.Count >= 12) return;
            if (node is string)
            {
                string s = (string)node;
                if (s.IndexOf(':') > 0 || s.StartsWith("#")) Add(into, s.StartsWith("#") ? "#" + NormId(s.Substring(1)) : NormId(s));
                return;
            }
            Dictionary<string, object> d = D(node);
            if (d != null)
            {
                if (S(d, "item") != null) { Add(into, NormId(S(d, "item"))); return; }
                if (S(d, "tag") != null) { Add(into, "#" + NormId(S(d, "tag"))); return; }
                if (S(d, "fluid") != null || S(d, "fluid_tag") != null) return;
                foreach (KeyValuePair<string, object> kv in d)
                    if (!NotIngredientKeys.Contains(kv.Key)) CollectIngredients(kv.Value, into, depth + 1);
                return;
            }
            foreach (object o in A(node)) CollectIngredients(o, into, depth + 1);
        }

        static void Add(List<string> list, string s) { if (!list.Contains(s)) list.Add(s); }

        // Whether a recipe's load conditions hold with these mods installed. Ones that can't be told from here count as met.
        static bool ConditionsMet(object conditions, HashSet<string> mods)
        {
            if (conditions == null) return true;
            IEnumerable<object> list = conditions is object[] ? (object[])conditions : new[] { conditions };
            return list.All(c => ConditionMet(D(c), mods, 0));
        }

        static bool ConditionMet(Dictionary<string, object> c, HashSet<string> mods, int depth)
        {
            if (c == null || depth > 8) return true;
            string t = S(c, "type") ?? "";
            switch (t.Substring(t.IndexOf(':') + 1))
            {
                case "false": return false;
                case "true": return true;
                case "mod_loaded": return mods.Contains(S(c, "modid") ?? "");
                case "not": return !ConditionMet(D(Get(c, "value")), mods, depth + 1);
                case "and": return A(Get(c, "values")).All(x => ConditionMet(D(x), mods, depth + 1));
                case "or": return A(Get(c, "values")).Any(x => ConditionMet(D(x), mods, depth + 1));
                case "item_exists":
                {
                    string item = S(c, "item");
                    return item == null || mods.Contains(NormId(item).Substring(0, NormId(item).IndexOf(':')));
                }
                default: return true;
            }
        }

        // ---------- Putting it together ----------

        sealed class ItemJar { public FileInfo File; public JarMeta Meta; public JarItems Data; }

        sealed class ItemWorld
        {
            public List<ItemJar> Jars;
            public JarItems Base;     // the game's own (its client jar)
            public FileInfo BaseFile;
            public readonly Dictionary<string, string> Names = new Dictionary<string, string>(StringComparer.Ordinal);
            public readonly Dictionary<string, ItemJar> OwnerOfNs = new Dictionary<string, ItemJar>(StringComparer.Ordinal);
            public readonly HashSet<string> ModIds = new HashSet<string>(StringComparer.Ordinal) { "minecraft", "neoforge", "forge", "c" };
        }

        static string VanillaClientJar()
        {
            string v = gameVersion ?? "";
            string path = Path.Combine(PrismDataDir(), "libraries", "com", "mojang", "minecraft", v, "minecraft-" + v + "-client.jar");
            return File.Exists(path) ? path : null;
        }

        static string RecipeFolder()
        {
            string v = gameVersion ?? "";
            return Regex.IsMatch(v, @"^1\.(1[0-9]|20)(\.|$)") ? "recipes" : "recipe";
        }

        static ItemWorld BuildItemWorld()
        {
            var w = new ItemWorld();
            w.Jars = !Directory.Exists(modsDir) ? new List<ItemJar>() : new DirectoryInfo(modsDir).GetFiles("*.jar")
                .Where(f => f.Name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) && !f.Name.Equals(FeatureJarName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(f => new ItemJar { File = f, Meta = GetJarMeta(f), Data = GetJarItems(f) }).ToList();
            string vanilla = VanillaClientJar();
            if (vanilla != null) { w.BaseFile = new FileInfo(vanilla); w.Base = GetJarItems(w.BaseFile); }
            foreach (ItemJar j in w.Jars)
                foreach (ModInfo m in j.Meta.Mods.Concat(j.Meta.Nested))
                    if (m.Id != null)
                    {
                        w.ModIds.Add(m.Id);
                        if (!w.OwnerOfNs.ContainsKey(m.Id)) w.OwnerOfNs[m.Id] = j;
                    }
            foreach (JarItems data in w.Jars.Select(j => j.Data).Concat(w.Base != null ? new[] { w.Base } : new JarItems[0]))
                foreach (KeyValuePair<string, string> n in data.Names) if (!w.Names.ContainsKey(n.Key)) w.Names[n.Key] = n.Value;
            return w;
        }

        static string ItemName(ItemWorld w, string id)
        {
            int c = id.IndexOf(':');
            string tail = id.Substring(0, c) + "." + id.Substring(c + 1).Replace('/', '.'), name;
            if (w.Names.TryGetValue("item." + tail, out name) && name.Trim().Length > 0) return name;
            if (w.Names.TryGetValue("block." + tail, out name) && name.Trim().Length > 0) return name;
            return FeatureTitle(id.Substring(c + 1));
        }

        static bool HasItemName(ItemWorld w, string id)
        {
            int c = id.IndexOf(':');
            string tail = id.Substring(0, c) + "." + id.Substring(c + 1).Replace('/', '.');
            return w.Names.ContainsKey("item." + tail) || w.Names.ContainsKey("block." + tail);
        }

        static string ItemModName(ItemJar j)
        {
            return j.Meta.Mods.Count > 0 && !string.IsNullOrEmpty(j.Meta.Mods[0].Name) ? j.Meta.Mods[0].Name : null;
        }

        static object[] ItemSource(ItemJar jar)
        {
            return jar == null ? new object[] { "file", null, "key", null, "mod", "Minecraft", "vanilla", true }
                : new object[] { "file", jar.File.Name, "key", NormalizeKey(jar.File.Name), "mod", ItemModName(jar) };
        }

        sealed class RecipeSource { public ItemJar Jar; public RecipeDef Def; }

        // Each recipe id's copies, in the order the game stacks them: Minecraft's, its own mod's, then others (the last wins).
        static Dictionary<string, List<RecipeSource>> RecipeCopies(ItemWorld w)
        {
            string folder = RecipeFolder();
            var all = new Dictionary<string, List<RecipeSource>>(StringComparer.Ordinal);
            Action<ItemJar, JarItems> add = (jar, data) =>
            {
                foreach (KeyValuePair<string, RecipeDef> r in data.Recipes)
                {
                    if (r.Value.Folder != folder) continue;
                    List<RecipeSource> list;
                    if (!all.TryGetValue(r.Key, out list)) all[r.Key] = list = new List<RecipeSource>();
                    list.Add(new RecipeSource { Jar = jar, Def = r.Value });
                }
            };
            if (w.Base != null) add(null, w.Base);
            foreach (ItemJar j in w.Jars) add(j, j.Data);
            foreach (KeyValuePair<string, List<RecipeSource>> kv in all)
            {
                string ns = kv.Key.Substring(0, kv.Key.IndexOf(':'));
                ItemJar owner;
                w.OwnerOfNs.TryGetValue(ns, out owner);
                List<RecipeSource> sorted = kv.Value.OrderBy(s => s.Jar == null ? 0 : s.Jar == owner ? 1 : 2).ToList();
                kv.Value.Clear();
                kv.Value.AddRange(sorted);
            }
            return all;
        }

        static string RecipeTypeName(ItemWorld w, string type)
        {
            int c = type.IndexOf(':');
            string ns = type.Substring(0, c), path = type.Substring(c + 1);
            if (ns == "minecraft")
            {
                if (path == "crafting_shaped" || path == "crafting_shapeless" || path == "crafting_transmute") return "Crafting";
                if (path.StartsWith("crafting_special_") || path == "crafting_decorated_pot") return "Crafting (special)";
                if (path == "campfire_cooking") return "Campfire";
                if (path == "stonecutting") return "Stonecutter";
                if (path.StartsWith("smithing")) return "Smithing";
                return FeatureTitle(path);
            }
            ItemJar owner;
            string mod = w.OwnerOfNs.TryGetValue(ns, out owner) && ItemModName(owner) != null ? ItemModName(owner) : FeatureTitle(ns);
            return FeatureTitle(path) + " (" + mod + ")";
        }

        // ---------- The list ----------

        static Dictionary<string, object> ItemsList()
        {
            FeaturesOff off;
            lock (FeatureLock) off = ReadFeaturesOff(); // (taken first: turning recipes off takes these two locks in this order)
            lock (ItemLock)
            {
                ItemWorld w = BuildItemWorld();
                HashSet<string> hidden = JeiHiddenItems();
                Dictionary<string, List<RecipeSource>> copies = RecipeCopies(w);

                // The recipes the game loads: the winning copy of each, when its conditions hold.
                var made = new Dictionary<string, int>(StringComparer.Ordinal);
                var used = new Dictionary<string, int>(StringComparer.Ordinal);
                var recipes = new List<object>();
                foreach (KeyValuePair<string, List<RecipeSource>> kv in copies.OrderBy(x => x.Key, StringComparer.Ordinal))
                {
                    RecipeSource win = kv.Value[kv.Value.Count - 1];
                    if (win.Def.Empty || !ConditionsMet(win.Def.Conditions, w.ModIds)) continue;
                    string ns = kv.Key.Substring(0, kv.Key.IndexOf(':'));
                    ItemJar owner;
                    if (!w.OwnerOfNs.TryGetValue(ns, out owner)) owner = win.Jar;
                    bool vanilla = ns == "minecraft" && (owner == null || win.Jar == null);
                    RecipeDef r = win.Def;
                    if (r.Result != null && !r.Fluid) made[r.Result] = (made.ContainsKey(r.Result) ? made[r.Result] : 0) + 1;
                    foreach (string i in r.Inputs) if (!i.StartsWith("#")) used[i] = (used.ContainsKey(i) ? used[i] : 0) + 1;
                    string name = r.Result == null ? FeatureTitle(PathTail(kv.Key)) : r.Fluid ? FeatureTitle(PathTail(r.Result)) : ItemName(w, r.Result);
                    recipes.Add(Fields(
                        new object[] { "id", kv.Key, "name", name, "result", r.Result, "count", r.Count, "fluid", r.Fluid, "type", r.Type, "typeName", RecipeTypeName(w, r.Type),
                            "inputs", r.Inputs, "off", off.Recipes.Contains(kv.Key),
                            "by", win.Jar != null && win.Jar != owner ? ItemModName(win.Jar) ?? win.Jar.File.Name : null },
                        ItemSource(vanilla ? null : owner)));
                }

                // Items: every item model with a name (or a recipe making it). Minecraft's own are listed as the game's.
                var items = new List<object>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var models = w.Jars.SelectMany(j => j.Data.Models.Select(m => new { Jar = j, Id = m.Key, Model = m.Value }));
                if (w.Base != null) models = models.Concat(w.Base.Models.Select(m => new { Jar = (ItemJar)null, Id = m.Key, Model = m.Value }));
                foreach (var m in models)
                {
                    if (!seen.Add(m.Id)) continue;
                    if (!HasItemName(w, m.Id) && !made.ContainsKey(m.Id)) continue; // a model other models build on, not an item
                    string ns = m.Id.Substring(0, m.Id.IndexOf(':'));
                    ItemJar owner = null;
                    if (ns != "minecraft" && !w.OwnerOfNs.TryGetValue(ns, out owner)) owner = m.Jar;
                    if (ns != "minecraft" && owner == null) continue;
                    string parent = S(m.Model, "parent") ?? "";
                    int c = m.Id.IndexOf(':');
                    bool block = w.Names.ContainsKey("block." + ns + "." + m.Id.Substring(c + 1).Replace('/', '.')) || Regex.IsMatch(parent, @"^([a-z0-9_.-]+:)?block/");
                    int nMade, nUsed;
                    made.TryGetValue(m.Id, out nMade);
                    used.TryGetValue(m.Id, out nUsed);
                    items.Add(Fields(
                        new object[] { "id", m.Id, "name", ItemName(w, m.Id), "block", block, "off", hidden.Contains(m.Id), "made", nMade, "used", nUsed },
                        ItemSource(owner)));
                }

                var known = new HashSet<string>(recipes.Select(o => (string)((Dictionary<string, object>)o)["id"]), StringComparer.Ordinal);
                return Obj("ok", true, "supported", FeatureStyle() != null, "loader", LoaderName(), "gameRunning", GameRunning(GameDir()),
                    "jei", w.ModIds.Contains("jei"), "jeiFile", "config/jei/blacklist.json", "otherViewer", w.ModIds.Contains("emi") ? "EMI" : w.ModIds.Contains("roughlyenoughitems") ? "REI" : null,
                    "jarFile", FeatureJarName, "jarExists", File.Exists(Path.Combine(modsDir, FeatureJarName)), "vanillaData", w.Base != null,
                    "items", items, "recipes", recipes, "missingOff", off.Recipes.Where(id => !known.Contains(id)).ToList());
            }
        }

        // ---------- JEI's hidden items ----------

        static string JeiBlacklistPath() { return Path.Combine(GameDir(), "config", "jei", "blacklist.json"); }

        // The file's entries after its version marker. Null when it's missing; an error when it can't be read.
        static List<object> ReadJeiBlacklist()
        {
            string path = JeiBlacklistPath();
            if (!File.Exists(path)) return null;
            object[] all;
            try { all = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as object[]; }
            catch (Exception) { throw new UserError("JEI's config/jei/blacklist.json isn't valid JSON, so it was left alone."); }
            if (all == null) throw new UserError("JEI's config/jei/blacklist.json isn't a list, so it was left alone.");
            if (all.Length > 0 && IntOf(Get(D(all[0]), "version")) != 2)
                throw new UserError("JEI's config/jei/blacklist.json is from a version of JEI this app doesn't know, so it was left alone.");
            return all.Skip(1).ToList();
        }

        static string JeiEntryItem(object entry)
        {
            Dictionary<string, object> ing = D(Get(D(entry), "ingredient"));
            if (S(ing, "type") != "item_stack") return null;
            string id = S(D(Get(ing, "ingredient")), "id");
            return id == null ? null : NormId(id);
        }

        static HashSet<string> JeiHiddenItems()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                List<object> entries = ReadJeiBlacklist();
                if (entries != null) foreach (object e in entries) { string id = JeiEntryItem(e); if (id != null) set.Add(id); }
            }
            catch (UserError) { }
            return set;
        }

        static Dictionary<string, object> SetJeiHidden(List<string> ids, bool hide)
        {
            if (ids == null || ids.Count == 0) throw new UserError("Nothing was selected.");
            if (GameRunning(GameDir())) throw new UserError("Close the game first. JEI writes its hidden list when you change it in game, which would undo this.");
            lock (ItemLock)
            {
                List<object> entries = ReadJeiBlacklist() ?? new List<object>();
                var want = new HashSet<string>(ids.Select(NormId), StringComparer.Ordinal);
                var has = new HashSet<string>(entries.Select(JeiEntryItem).Where(x => x != null), StringComparer.Ordinal);
                int changed;
                if (hide)
                {
                    List<string> add = want.Where(id => !has.Contains(id)).OrderBy(x => x, StringComparer.Ordinal).ToList();
                    // Wildcard: every version of the item (other enchantments, colours and so on), as JEI's edit mode does with Ctrl.
                    foreach (string id in add) entries.Add(Obj("hide_mode", "WILDCARD", "ingredient", Obj("ingredient", Obj("id", id), "type", "item_stack")));
                    changed = add.Count;
                }
                else
                {
                    int before = entries.Count;
                    changed = want.Count(id => has.Contains(id));
                    entries.RemoveAll(e => { string id = JeiEntryItem(e); return id != null && want.Contains(id); });
                    if (entries.Count == before) changed = 0;
                }
                if (changed == 0) return Obj("ok", true, "changed", 0);
                string path = JeiBlacklistPath();
                BackUpConfig(path, DateTime.Now.ToString("yyyy-MM-dd_HHmmss"), "jei-game", Path.Combine("jei", "blacklist.json"));
                var json = new JavaScriptSerializer();
                var sb = new StringBuilder("[\n  {\"version\":2}");
                foreach (object e in entries) sb.Append(",\n  ").Append(json.Serialize(e));
                sb.Append("\n]\n");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path + ".saving", sb.ToString(), new UTF8Encoding(false));
                File.Copy(path + ".saving", path, true);
                File.Delete(path + ".saving");
                Log("JEI       " + (hide ? "hid " : "showed ") + changed + " item" + (changed == 1 ? "" : "s") + ": " + string.Join(", ", ids.Take(12)) + (ids.Count > 12 ? ", …" : ""));
                return Obj("ok", true, "changed", changed);
            }
        }

        // ---------- Turned-off recipes in the feature-toggles jar ----------

        // A copy of each turned-off recipe that never loads; the jar loads after every mod carrying that recipe.
        static void AddRecipeOverrides(FeaturesOff off, SortedDictionary<string, string> files, SortedSet<string> after, string style)
        {
            if (off.Recipes.Count == 0) return;
            ItemWorld w;
            lock (ItemLock) w = BuildItemWorld();
            Dictionary<string, List<RecipeSource>> copies = RecipeCopies(w);
            string never = style == "forge"
                ? "{\"conditions\":[{\"type\":\"forge:false\"}]}"
                : "{\"neoforge:conditions\":[{\"type\":\"neoforge:false\"}]}";
            foreach (string id in off.Recipes)
            {
                List<RecipeSource> list;
                string folder = copies.TryGetValue(id, out list) ? list[list.Count - 1].Def.Folder : RecipeFolder();
                int c = id.IndexOf(':');
                files["data/" + id.Substring(0, c) + "/" + folder + "/" + id.Substring(c + 1) + ".json"] = never;
                if (list != null) foreach (RecipeSource s in list.Where(x => x.Jar != null)) after.UnionWith(ModIdsOfMeta(s.Jar.Meta));
            }
        }

        // ---------- Icons ----------

        static readonly string[] FaceKeys = { "layer0", "all", "side", "texture", "front", "top", "end", "cross", "plant", "wool", "pattern", "particle" };

        static ItemWorld iconWorld;
        static DateTime iconWorldAt;
        static readonly Dictionary<string, Dictionary<string, object>> IconModelCache = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
        // Jars kept open while icons are being read (a page of them asks for dozens at once), closed again once
        // they've gone unused for a few seconds. They're opened so that moving or deleting the jar still works.
        static readonly Dictionary<string, ZipArchive> IconZips = new Dictionary<string, ZipArchive>(StringComparer.OrdinalIgnoreCase);
        static System.Threading.Timer iconZipTimer;

        // The PNG for an item: its item model's layer0, else a face of the block model it points to.
        static byte[] ItemIcon(string id)
        {
            ItemWorld w;
            lock (ItemIconCache)
            {
                if (iconWorld == null || (DateTime.UtcNow - iconWorldAt).TotalSeconds > 30)
                {
                    lock (ItemLock) iconWorld = BuildItemWorld();
                    iconWorldAt = DateTime.UtcNow;
                    string stamp = string.Join("|", iconWorld.Jars.Select(j => j.File.Name + j.File.Length));
                    if (stamp != itemIconStamp) { ItemIconCache.Clear(); IconModelCache.Clear(); CloseIconZips(); itemIconStamp = stamp; }
                }
                w = iconWorld;
                byte[] cached;
                if (ItemIconCache.TryGetValue(id, out cached)) return cached;
                byte[] bytes = null;
                try { bytes = FindItemIcon(w, id); } catch (Exception) { }
                if (ItemIconCache.Count < 20000) ItemIconCache[id] = bytes;
                if (iconZipTimer == null) iconZipTimer = new System.Threading.Timer(_ => CloseIconZips(), null, 4000, System.Threading.Timeout.Infinite);
                else iconZipTimer.Change(4000, System.Threading.Timeout.Infinite);
                return bytes;
            }
        }

        static void CloseIconZips()
        {
            lock (ItemIconCache)
            {
                foreach (ZipArchive z in IconZips.Values) try { z.Dispose(); } catch (Exception) { }
                IconZips.Clear();
            }
        }

        static Dictionary<string, object> IconModel(ItemWorld w, string modelId)
        {
            Dictionary<string, object> model;
            if (IconModelCache.TryGetValue(modelId, out model)) return model;
            byte[] text = ReadAsset(w, "assets/" + modelId.Substring(0, modelId.IndexOf(':')) + "/models/" + modelId.Substring(modelId.IndexOf(':') + 1) + ".json");
            if (text != null) try { model = new JavaScriptSerializer().DeserializeObject(Encoding.UTF8.GetString(text)) as Dictionary<string, object>; } catch (Exception) { }
            IconModelCache[modelId] = model;
            return model;
        }

        static byte[] FindItemIcon(ItemWorld w, string id)
        {
            Dictionary<string, object> model = null;
            foreach (ItemJar j in w.Jars) if (j.Data.Models.TryGetValue(id, out model)) break;
            if (model == null && w.Base != null) w.Base.Models.TryGetValue(id, out model);
            if (model == null) return null;
            var textures = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int depth = 0; model != null && depth < 8; depth++)
            {
                Dictionary<string, object> t = D(Get(model, "textures"));
                if (t != null) foreach (KeyValuePair<string, object> kv in t) if (kv.Value is string && !textures.ContainsKey(kv.Key)) textures[kv.Key] = (string)kv.Value;
                string parent = S(model, "parent");
                if (parent == null || parent.StartsWith("builtin/")) break;
                model = IconModel(w, NormId(parent));
            }
            string tex = null;
            foreach (string k in FaceKeys) if (textures.TryGetValue(k, out tex)) break;
            if (tex == null) tex = textures.Values.FirstOrDefault();
            for (int i = 0; tex != null && tex.StartsWith("#") && i < 8; i++) textures.TryGetValue(tex.Substring(1), out tex);
            if (tex == null || tex.StartsWith("#")) return null;
            tex = NormId(tex);
            return ReadAsset(w, "assets/" + tex.Substring(0, tex.IndexOf(':')) + "/textures/" + tex.Substring(tex.IndexOf(':') + 1) + ".png");
        }

        // An asset from the first jar that has it: the mods (they override the game's), then the game's own.
        static byte[] ReadAsset(ItemWorld w, string entry)
        {
            FileInfo from = w.Jars.Where(j => j.Data.Assets.Contains(entry)).Select(j => j.File).FirstOrDefault();
            if (from == null && w.Base != null && w.Base.Assets.Contains(entry)) from = w.BaseFile;
            if (from == null) return null;
            ZipArchive zip;
            if (!IconZips.TryGetValue(from.FullName, out zip))
            {
                var stream = new FileStream(from.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                try { zip = new ZipArchive(stream, ZipArchiveMode.Read); }
                catch (Exception) { stream.Dispose(); return null; }
                IconZips[from.FullName] = zip;
            }
            ZipArchiveEntry e = zip.GetEntry(entry);
            if (e == null || e.Length > 4L << 20) return null;
            var mem = new MemoryStream();
            using (Stream s = e.Open()) s.CopyTo(mem);
            return mem.ToArray();
        }

        static void SendItemIcon(HttpListenerContext ctx, string id)
        {
            byte[] bytes = id != null && Regex.IsMatch(id, @"^[a-z0-9_.-]+:[a-z0-9_./-]+$") ? ItemIcon(id) : null;
            HttpListenerResponse res = ctx.Response;
            if (bytes == null) { SendText(ctx, 404, "text/plain; charset=utf-8", "No icon"); return; }
            res.StatusCode = 200;
            res.ContentType = "image/png";
            res.AddHeader("Cache-Control", "private, max-age=3600");
            res.AddHeader("X-Content-Type-Options", "nosniff");
            res.ContentLength64 = bytes.Length;
            using (Stream output = res.OutputStream) output.Write(bytes, 0, bytes.Length);
        }
    }
}
