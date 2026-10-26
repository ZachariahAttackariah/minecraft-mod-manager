// Key binds: every control in the game's options.txt, Minecraft's and the mods', named from the jars' language
// files, with the mod each belongs to, its key, and NeoForge's Shift/Ctrl/Alt modifier (written after the key:
// key.keyboard.o:CONTROL). Minecraft's own carry their default, so they can be reset; options.txt doesn't record
// the mods' defaults. Changes go into options.txt (backed up first) while the game is closed, since the game
// writes the file back when it quits; when this instance shares its game settings (Sharing.cs), the change is
// handed on to the others straight away.

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
        // Minecraft's own controls: default key and the group the game's Controls screen shows them in.
        static readonly string[][] VanillaKeys =
        {
            new[] { "key.forward", "key.keyboard.w", "Movement" }, new[] { "key.left", "key.keyboard.a", "Movement" },
            new[] { "key.back", "key.keyboard.s", "Movement" }, new[] { "key.right", "key.keyboard.d", "Movement" },
            new[] { "key.jump", "key.keyboard.space", "Movement" }, new[] { "key.sneak", "key.keyboard.left.shift", "Movement" },
            new[] { "key.sprint", "key.keyboard.left.control", "Movement" },
            new[] { "key.attack", "key.mouse.left", "Gameplay" }, new[] { "key.use", "key.mouse.right", "Gameplay" },
            new[] { "key.pickItem", "key.mouse.middle", "Gameplay" },
            new[] { "key.drop", "key.keyboard.q", "Inventory" }, new[] { "key.inventory", "key.keyboard.e", "Inventory" },
            new[] { "key.swapOffhand", "key.keyboard.f", "Inventory" },
            new[] { "key.hotbar.1", "key.keyboard.1", "Inventory" }, new[] { "key.hotbar.2", "key.keyboard.2", "Inventory" },
            new[] { "key.hotbar.3", "key.keyboard.3", "Inventory" }, new[] { "key.hotbar.4", "key.keyboard.4", "Inventory" },
            new[] { "key.hotbar.5", "key.keyboard.5", "Inventory" }, new[] { "key.hotbar.6", "key.keyboard.6", "Inventory" },
            new[] { "key.hotbar.7", "key.keyboard.7", "Inventory" }, new[] { "key.hotbar.8", "key.keyboard.8", "Inventory" },
            new[] { "key.hotbar.9", "key.keyboard.9", "Inventory" },
            new[] { "key.saveToolbarActivator", "key.keyboard.c", "Creative mode" }, new[] { "key.loadToolbarActivator", "key.keyboard.x", "Creative mode" },
            new[] { "key.chat", "key.keyboard.t", "Multiplayer" }, new[] { "key.playerlist", "key.keyboard.tab", "Multiplayer" },
            new[] { "key.command", "key.keyboard.slash", "Multiplayer" }, new[] { "key.socialInteractions", "key.keyboard.p", "Multiplayer" },
            new[] { "key.screenshot", "key.keyboard.f2", "Miscellaneous" }, new[] { "key.togglePerspective", "key.keyboard.f5", "Miscellaneous" },
            new[] { "key.smoothCamera", "key.keyboard.unknown", "Miscellaneous" }, new[] { "key.fullscreen", "key.keyboard.f11", "Miscellaneous" },
            new[] { "key.spectatorOutlines", "key.keyboard.unknown", "Miscellaneous" }, new[] { "key.advancements", "key.keyboard.l", "Miscellaneous" },
        };
        static readonly string[] KeyModifiers = { "NONE", "SHIFT", "CONTROL", "ALT" };
        static readonly Regex KeyCode = new Regex(@"^key\.(keyboard|mouse)\.[a-z0-9.]+$");
        static readonly Regex AnyLangLine = new Regex(@"""((?:[^""\\]|\\.)+)""\s*:\s*""((?:[^""\\]|\\.)*)""");
        static readonly HashSet<string> NotModSegments = new HashSet<string> { "key", "keys", "keybind", "keybinds", "keyinfo", "keymapping", "minecraft", "category", "categories" };
        // Per jar: the bind names looked for last time, and which of them its language file names.
        static readonly Dictionary<string, KeyValuePair<string, Dictionary<string, string>>> KeyLangCache = new Dictionary<string, KeyValuePair<string, Dictionary<string, string>>>();

        static string OptionsPath() { return Path.Combine(GameDir(), "options.txt"); }

        static Dictionary<string, string> KeyNamesIn(FileInfo file, HashSet<string> wanted, string signature)
        {
            string key = file.FullName + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks;
            lock (KeyLangCache)
            {
                KeyValuePair<string, Dictionary<string, string>> cached;
                if (KeyLangCache.TryGetValue(key, out cached) && cached.Key == signature) return cached.Value;
            }
            var found = new Dictionary<string, string>(StringComparer.Ordinal);
            var json = new JavaScriptSerializer();
            try
            {
                using (ZipArchive zip = ZipFile.OpenRead(file.FullName))
                    foreach (ZipArchiveEntry e in zip.Entries)
                    {
                        if (!LangEntry.IsMatch(e.FullName) || e.Length > 8L << 20) continue;
                        foreach (Match m in AnyLangLine.Matches(ReadEntryText(e)))
                            if (wanted.Contains(m.Groups[1].Value) && !found.ContainsKey(m.Groups[1].Value))
                                try { found[m.Groups[1].Value] = json.Deserialize<string>("\"" + m.Groups[2].Value + "\""); } catch (Exception) { }
                    }
            }
            catch (Exception) { }
            lock (KeyLangCache) KeyLangCache[key] = new KeyValuePair<string, Dictionary<string, string>>(signature, found);
            return found;
        }

        static void SplitBind(string raw, out string key, out string modifier)
        {
            key = raw;
            modifier = "NONE";
            int colon = raw.LastIndexOf(':');
            if (colon > 0 && KeyModifiers.Contains(raw.Substring(colon + 1))) { key = raw.Substring(0, colon); modifier = raw.Substring(colon + 1); }
        }

        static Dictionary<string, object> KeyBinds()
        {
            string path = OptionsPath();
            bool running = GameRunning(GameDir());
            InstanceInfo me = PrismInstances().FirstOrDefault(i => SamePath(i.Mods, modsDir));
            bool shared = me != null && SharesGameSettings(me);
            if (!File.Exists(path))
                return Obj("ok", true, "missing", true, "gameRunning", running, "shared", shared, "binds", new List<object>());
            OptionLines o = OptionLines.Read(path);
            List<string> names = o.Order.Where(k => k.StartsWith("key_", StringComparison.Ordinal)).Select(k => k.Substring(4)).ToList();
            var wanted = new HashSet<string>(names, StringComparer.Ordinal);
            string signature = string.Join("\n", names.OrderBy(x => x, StringComparer.Ordinal));

            // Which jar names each bind (its language file), and which jar each mod id is in.
            var jars = !Directory.Exists(modsDir) ? new List<FileInfo>() : new DirectoryInfo(modsDir).GetFiles("*.jar")
                .Where(f => f.Name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) && !f.Name.Equals(FeatureJarName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var label = new Dictionary<string, string>(StringComparer.Ordinal);
            var definedBy = new Dictionary<string, List<FileInfo>>(StringComparer.Ordinal);
            var jarOfMod = new Dictionary<string, FileInfo>(StringComparer.Ordinal);
            var metaOf = new Dictionary<FileInfo, JarMeta>();
            foreach (FileInfo f in jars)
            {
                JarMeta meta = GetJarMeta(f);
                metaOf[f] = meta;
                foreach (ModInfo m in meta.Mods.Concat(meta.Nested)) if (m.Id != null && !jarOfMod.ContainsKey(m.Id)) jarOfMod[m.Id] = f;
                foreach (KeyValuePair<string, string> n in KeyNamesIn(f, wanted, signature))
                {
                    if (!label.ContainsKey(n.Key)) label[n.Key] = n.Value;
                    List<FileInfo> list;
                    if (!definedBy.TryGetValue(n.Key, out list)) definedBy[n.Key] = list = new List<FileInfo>();
                    list.Add(f);
                }
            }
            string vanillaJar = VanillaClientJar();
            if (vanillaJar != null)
                foreach (KeyValuePair<string, string> n in KeyNamesIn(new FileInfo(vanillaJar), wanted, signature)) if (!label.ContainsKey(n.Key)) label[n.Key] = n.Value;

            var vanilla = VanillaKeys.ToDictionary(v => v[0], v => v, StringComparer.Ordinal);
            var binds = new List<object>();
            foreach (string name in names)
            {
                string key, modifier;
                SplitBind(o.Values["key_" + name], out key, out modifier);
                string[] v;
                bool isVanilla = vanilla.TryGetValue(name, out v);
                FileInfo owner = null;
                if (!isVanilla)
                {
                    // The mod whose id is in the bind's name, among the jars that name it; else the first that does.
                    var ids = name.Split('.').Where(s => s.Length > 1 && !NotModSegments.Contains(s.ToLowerInvariant())).Select(s => s.ToLowerInvariant()).ToList();
                    List<FileInfo> definers;
                    definedBy.TryGetValue(name, out definers);
                    owner = definers == null ? null : definers.FirstOrDefault(f => metaOf[f].Mods.Concat(metaOf[f].Nested).Any(m => m.Id != null && ids.Contains(m.Id)));
                    if (owner == null) owner = ids.Where(jarOfMod.ContainsKey).Select(id => jarOfMod[id]).FirstOrDefault();
                    if (owner == null && definers != null) owner = definers[0];
                }
                JarMeta om = owner != null ? metaOf[owner] : null;
                string text;
                binds.Add(Obj("name", name, "label", label.TryGetValue(name, out text) && text.Trim().Length > 0 ? text : null,
                    "key", key, "modifier", modifier, "vanilla", isVanilla, "group", isVanilla ? v[2] : null, "def", isVanilla ? v[1] : null,
                    "file", owner != null ? owner.Name : null, "fileKey", owner != null ? NormalizeKey(owner.Name) : null,
                    "mod", isVanilla ? "Minecraft" : om != null && om.Mods.Count > 0 && !string.IsNullOrEmpty(om.Mods[0].Name) ? om.Mods[0].Name : null));
            }
            return Obj("ok", true, "missing", false, "gameRunning", running, "shared", shared, "binds", binds);
        }

        // changes: [{ name, key, modifier }]. Every bind is checked before anything is written.
        static Dictionary<string, object> SetKeyBinds(List<Dictionary<string, object>> changes)
        {
            if (changes.Count == 0) throw new UserError("Nothing to change.");
            if (GameRunning(GameDir())) throw new UserError("Close the game first. It writes its key binds back to options.txt when it quits, which would undo this.");
            string path = OptionsPath();
            if (!File.Exists(path)) throw new UserError("This instance has no options.txt yet. Start the game once to make one.");
            OptionLines o = OptionLines.Read(path);
            int changed = 0;
            foreach (Dictionary<string, object> c in changes)
            {
                string name = Str(c, "name"), key = Str(c, "key"), modifier = Str(c, "modifier") ?? "NONE";
                if (name == null || !o.Values.ContainsKey("key_" + name)) throw new UserError("options.txt has no key bind called " + name + ".");
                if (key == null || !KeyCode.IsMatch(key)) throw new UserError("That isn't a key the game knows: " + key);
                if (!KeyModifiers.Contains(modifier)) throw new UserError("Unknown modifier: " + modifier);
                string value = key + (modifier == "NONE" || key == "key.keyboard.unknown" ? "" : ":" + modifier);
                if (o.Values["key_" + name] == value) continue;
                o.Set("key_" + name, value);
                changed++;
            }
            if (changed == 0) return Obj("ok", true, "changed", 0);
            BackUpConfig(path, DateTime.Now.ToString("yyyy-MM-dd_HHmmss"), "keybinds", "options.txt");
            o.Write(path + ".saving");
            File.Copy(path + ".saving", path, true);
            File.Delete(path + ".saving");
            Log("Key binds " + changed + " changed: " + string.Join(", ", changes.Take(8).Select(c => Str(c, "name") + " = " + Str(c, "key"))) + (changes.Count > 8 ? ", …" : ""));
            InstanceInfo me = PrismInstances().FirstOrDefault(i => SamePath(i.Mods, modsDir));
            if (me != null && SharesGameSettings(me)) TrySyncGameSettings();
            return Obj("ok", true, "changed", changed);
        }
    }
}
