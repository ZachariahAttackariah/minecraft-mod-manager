// Keeping the server in step with the game. The game's mods folder (with its client/server tags)
// is the source of truth for mods: every enabled jar tagged "server" should be on the server, the
// same version, switched on; a jar turned off in the game should be off on the server too.
// Configs are compared both ways, setting by setting, and only copied when the person ticks them.
// Nothing is applied without the person choosing it in the preview, and never while the server runs.
// Replaced jars go to the Recycle Bin; overwritten configs are backed up under sync-backups.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MinecraftModManager
{
    static partial class Program
    {
        sealed class SideJar
        {
            public FileInfo File;
            public string Key;      // file name without .disabled
            public bool Enabled;
            public string ModId, Version, Name;
            public List<string> Tags = new List<string>();
        }

        sealed class SyncItem
        {
            public string Id, Kind, Title, Note;
            public SideJar Game, Server;
            public bool Default;
        }

        static List<SideJar> ListSideJars(string dir, TagsDoc tags)
        {
            var list = new List<SideJar>();
            if (!Directory.Exists(dir)) return list;
            foreach (FileInfo f in new DirectoryInfo(dir).GetFiles().Where(f => JarName.IsMatch(f.Name)))
            {
                bool off = f.Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                JarMeta meta = GetJarMeta(f);
                ModInfo first = MainMod(meta);
                var jar = new SideJar
                {
                    File = f,
                    Key = off ? f.Name.Substring(0, f.Name.Length - ".disabled".Length) : f.Name,
                    Enabled = !off,
                    ModId = first == null ? null : first.Id.ToLowerInvariant(),
                    Version = first == null ? null : first.Version,
                    Name = first == null ? null : first.Name
                };
                if (tags != null)
                {
                    string k = tags.Mods.Keys.FirstOrDefault(x => string.Equals(x, jar.Key, StringComparison.OrdinalIgnoreCase));
                    if (k != null) jar.Tags = tags.Mods[k];
                }
                list.Add(jar);
            }
            return list;
        }

        static TagsDoc GameTags()
        {
            try { return LoadTagsDoc(Path.Combine(modsDir, "mod-tags.json")); }
            catch (UserError) { return null; }
        }

        static bool SameJar(SideJar a, SideJar b)
        {
            if (a.File.Length != b.File.Length) return false;
            if (string.Equals(a.Key, b.Key, StringComparison.OrdinalIgnoreCase)) return true;
            try { return FileSha1(a.File) == FileSha1(b.File); } catch { return false; }
        }

        static string Label(SideJar j) { return j.Name ?? Regex.Replace(j.Key, @"\.jar$", "", RegexOptions.IgnoreCase); }

        static List<SyncItem> ComputeModSync(string gameMods, string serverMods, out int inSync)
        {
            TagsDoc tags = GameTags();
            List<SideJar> game = ListSideJars(gameMods, tags);
            List<SideJar> server = ListSideJars(serverMods, null);
            var serverByKey = server.GroupBy(s => s.Key.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());
            var serverById = server.Where(s => s.ModId != null).GroupBy(s => s.ModId).ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.Enabled).First());
            var matched = new HashSet<SideJar>();
            var items = new List<SyncItem>();
            inSync = 0;

            foreach (SideJar g in game.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                SideJar s;
                if (!serverByKey.TryGetValue(g.Key.ToLowerInvariant(), out s) && g.ModId != null) serverById.TryGetValue(g.ModId, out s);
                if (s != null) matched.Add(s);
                bool tagServer = g.Tags.Contains("server"), tagClient = g.Tags.Contains("client");
                string id = "mod:" + (g.ModId ?? g.Key.ToLowerInvariant());
                if (!tagServer && !tagClient)
                {
                    if (g.Enabled && (s == null || !s.Enabled))
                        items.Add(new SyncItem { Id = id, Kind = "untagged", Title = Label(g), Game = g, Server = s,
                            Note = "Has no client/server tag, so it isn't synced. Tag it in the mod list to decide." });
                    else inSync++;
                    continue;
                }
                if (g.Enabled && tagServer)
                {
                    if (s == null)
                        items.Add(new SyncItem { Id = id, Kind = "add", Title = Label(g), Game = g, Default = true, Note = "Not on the server yet." });
                    else if (!SameJar(g, s))
                        items.Add(new SyncItem { Id = id, Kind = "replace", Title = Label(g), Game = g, Server = s, Default = true,
                            Note = "The server has " + (s.Version != null && g.Version != null && s.Version != g.Version ? "version " + s.Version + "; the game has " + g.Version : "a different file") + "." });
                    else if (!s.Enabled)
                        items.Add(new SyncItem { Id = id, Kind = "enable", Title = Label(g), Game = g, Server = s, Default = true, Note = "Turned off on the server." });
                    else inSync++;
                }
                else if (s != null && s.Enabled)
                {
                    if (!g.Enabled)
                        items.Add(new SyncItem { Id = id, Kind = "disable", Title = Label(g), Game = g, Server = s, Default = true,
                            Note = "Turned off in the game but still on in the server." + (tagClient && tagServer ? " Players can't join while the two differ." : "") });
                    else
                        items.Add(new SyncItem { Id = id, Kind = "client-only", Title = Label(g), Game = g, Server = s, Default = false,
                            Note = "Tagged client-only, but it's on the server. Turn it off there only if you're sure: a mod that's really needed on both sides would stop players joining." });
                }
                else inSync++;
            }
            // Mod ids the game's enabled mods require (client or both sides), to spot server jars the game is missing.
            var gameNeeds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (SideJar g in game.Where(x => x.Enabled))
                foreach (DepInfo dep in GetJarMeta(g.File).Deps.Where(d => d.Type == "required" && d.Side != "SERVER"))
                    if (!gameNeeds.ContainsKey(dep.Id)) gameNeeds[dep.Id] = Label(g);
            var gameHas = new HashSet<string>(game.SelectMany(x => GetJarMeta(x.File).Mods.Concat(GetJarMeta(x.File).Nested)).Select(m => m.Id), StringComparer.OrdinalIgnoreCase);

            foreach (SideJar s in server.Where(x => x.Enabled && !matched.Contains(x)).OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                string neededBy;
                if (s.ModId != null && gameNeeds.TryGetValue(s.ModId, out neededBy) && !gameHas.Contains(s.ModId))
                    items.Add(new SyncItem { Id = "srv:" + s.ModId, Kind = "to-game", Title = Label(s), Server = s, Default = true,
                        Note = neededBy + " in the game needs this, and only the server has it. Copies it into the game's mods folder (the game won't start without it)." });
                else
                    items.Add(new SyncItem { Id = "srv:" + (s.ModId ?? s.Key.ToLowerInvariant()), Kind = "server-only", Title = Label(s), Server = s, Default = false,
                        Note = "Only on the server. That's normal for server-side mods; tick it only to turn it off there." });
            }
            return items;
        }

        // ---------- Configs ----------

        static readonly Regex SkipConfig = new Regex(@"(\.bak|\.bak-[^\\]*|\.old|\.tmp|\.dat|\.log|\.lock|\.cache)$|[-_.]client[-_.]|(^|\\)client\\|^jei\\world\\|-\d+\.toml\.bak$",
            RegexOptions.IgnoreCase);

        sealed class ConfigItem
        {
            public string Rel, Kind, Owner; // Kind: differ | game-only | server-only
            public FileInfo Game, Server;
        }

        static Dictionary<string, FileInfo> ConfigFiles(string root)
        {
            var map = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(root)) return map;
            foreach (FileInfo f in new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories))
                map[f.FullName.Substring(root.Length).TrimStart('\\')] = f;
            return map;
        }

        // Which mod a config belongs to, guessed from its name or folder (create-server.toml -> create).
        static string ConfigOwner(string rel, ICollection<string> modIds)
        {
            string first = rel.Split('\\')[0];
            string stem = Path.GetFileNameWithoutExtension(first).ToLowerInvariant();
            if (modIds.Contains(stem)) return stem;
            string head = Regex.Split(stem, "[-_.]")[0];
            if (modIds.Contains(head)) return head;
            string joined = Regex.Replace(stem, "-(common|server|client|startup|mixins)$", "");
            return modIds.Contains(joined) ? joined : null;
        }

        static List<ConfigItem> ComputeConfigSync(string gameConfig, string serverConfig, out int ignored, out int same)
        {
            Dictionary<string, FileInfo> game = ConfigFiles(gameConfig), server = ConfigFiles(serverConfig);
            TagsDoc tags = GameTags();
            var sideOf = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (SideJar j in ListSideJars(modsDir, tags)) if (j.ModId != null) sideOf[j.ModId] = j.Tags;
            foreach (SideJar j in ListSideJars(Path.Combine(serverDir ?? "", "mods"), null)) if (j.ModId != null && !sideOf.ContainsKey(j.ModId)) sideOf[j.ModId] = new List<string> { "server" };
            var items = new List<ConfigItem>();
            ignored = 0;
            same = 0;
            foreach (string rel in game.Keys.Union(server.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(r => r, StringComparer.OrdinalIgnoreCase))
            {
                if (SkipConfig.IsMatch(rel)) { ignored++; continue; }
                string owner = ConfigOwner(rel, sideOf.Keys);
                List<string> side;
                if (owner != null && sideOf.TryGetValue(owner, out side) && side.Contains("client") && !side.Contains("server")) { ignored++; continue; }
                FileInfo g, s;
                game.TryGetValue(rel, out g);
                server.TryGetValue(rel, out s);
                if (g != null && s != null)
                {
                    int more;
                    // Equal files, or files whose settings match and only comments differ (Java writes a timestamp comment).
                    if (g.Length == s.Length && FilesEqual(g, s) || ConfigChanges(g, s, out more).Count == 0 && more == 0 && TextConfig(rel)) { same++; continue; }
                    items.Add(new ConfigItem { Rel = rel, Kind = "differ", Owner = owner, Game = g, Server = s });
                }
                else if (owner != null && sideOf.ContainsKey(owner))
                {
                    items.Add(new ConfigItem { Rel = rel, Kind = g != null ? "game-only" : "server-only", Owner = owner, Game = g, Server = s });
                }
                else ignored++;
            }
            return items;
        }

        static bool TextConfig(string rel)
        {
            return Regex.IsMatch(rel, @"\.(toml|properties|cfg|json5?|ini|txt|yml|yaml|conf)$", RegexOptions.IgnoreCase);
        }

        static bool FilesEqual(FileInfo a, FileInfo b)
        {
            try
            {
                if (a.Length > 8 * 1024 * 1024) return false;
                return File.ReadAllBytes(a.FullName).SequenceEqual(File.ReadAllBytes(b.FullName));
            }
            catch { return false; }
        }

        // Setting-by-setting differences for text configs: [{ key, game, server }].
        static List<object> ConfigChanges(FileInfo game, FileInfo server, out int more)
        {
            more = 0;
            var result = new List<object>();
            if (game == null || server == null || game.Length > 512 * 1024 || server.Length > 512 * 1024) return result;
            Dictionary<string, string> a = ConfigValues(File.ReadAllLines(game.FullName)), b = ConfigValues(File.ReadAllLines(server.FullName));
            foreach (string key in a.Keys.Union(b.Keys))
            {
                string x, y;
                a.TryGetValue(key, out x);
                b.TryGetValue(key, out y);
                if (x == y) continue;
                if (result.Count < 12) result.Add(Obj("key", key, "game", x, "server", y));
                else more++;
            }
            return result;
        }

        static Dictionary<string, string> ConfigValues(string[] lines)
        {
            var values = new Dictionary<string, string>();
            string section = "";
            var counts = new Dictionary<string, int>();
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//")) continue;
                Match h = Regex.Match(line, @"^\[\[?\s*([^\]]+?)\s*\]\]?$");
                if (h.Success) { section = h.Groups[1].Value.Trim('"') + "."; continue; }
                Match kv = Regex.Match(line, "^\"?([A-Za-z0-9_.$:\\- ]+?)\"?\\s*[=:]\\s*(.*?),?$");
                string key = kv.Success ? section + kv.Groups[1].Value.Trim() : section + "(line)";
                string value = kv.Success ? kv.Groups[2].Value.Trim() : line;
                int n;
                counts.TryGetValue(key, out n);
                counts[key] = n + 1;
                values[n == 0 ? key : key + " #" + (n + 1)] = value;
            }
            return values;
        }

        // ---------- Preview and apply ----------

        static string GameDir() { return Path.GetDirectoryName(modsDir.TrimEnd('\\')); }

        static Dictionary<string, object> SyncPreview()
        {
            string server = RequireServerDir();
            string serverMods = Path.Combine(server, "mods");
            int inSync, ignored, same;
            List<SyncItem> mods = ComputeModSync(modsDir, serverMods, out inSync);
            List<ConfigItem> configs = ComputeConfigSync(Path.Combine(GameDir(), "config"), Path.Combine(server, "config"), out ignored, out same);
            var modList = mods.Select(m => (object)Obj("id", m.Id, "kind", m.Kind, "title", m.Title, "note", m.Note, "default", m.Default,
                "gameFile", m.Game == null ? null : m.Game.File.Name, "serverFile", m.Server == null ? null : m.Server.File.Name)).ToList();
            var configList = new List<object>();
            foreach (ConfigItem c in configs)
            {
                int more;
                List<object> changes = ConfigChanges(c.Game, c.Server, out more);
                string newer = c.Game == null ? "server" : c.Server == null ? "game" : c.Game.LastWriteTimeUtc >= c.Server.LastWriteTimeUtc ? "game" : "server";
                configList.Add(Obj("id", "cfg:" + c.Rel.ToLowerInvariant(), "path", c.Rel, "kind", c.Kind, "owner", c.Owner, "newer", newer,
                    "gameModified", c.Game == null ? null : c.Game.LastWriteTimeUtc.ToString("o"),
                    "serverModified", c.Server == null ? null : c.Server.LastWriteTimeUtc.ToString("o"),
                    "changes", changes, "moreChanges", more));
            }
            return Obj("ok", true, "serverFolder", server, "gameFolder", GameDir(), "running", ServerBusy() || ExternalServerRunning(server, null),
                "mods", modList, "modsInSync", inSync, "configs", configList, "configsSame", same, "configsIgnored", ignored);
        }

        static Dictionary<string, object> SyncApply(List<string> modIds, List<string> toServer, List<string> toGame)
        {
            string server = RequireServerDir();
            if (ServerBusy() || ExternalServerRunning(server, null)) throw new UserError("Stop the server first. Its mods are in use while it runs.");
            if (modIds.Count + toServer.Count + toGame.Count == 0) throw new UserError("Nothing was ticked.");
            string serverMods = Path.Combine(server, "mods");
            Directory.CreateDirectory(serverMods);
            var done = new List<string>();
            var failed = new List<string>();
            lock (FileLock)
            {
                int inSync;
                Dictionary<string, SyncItem> items = ComputeModSync(modsDir, serverMods, out inSync).GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.First());
                foreach (string id in modIds)
                {
                    SyncItem item;
                    if (!items.TryGetValue(id, out item)) { failed.Add(id + ": already in sync or gone. Refresh the preview."); continue; }
                    try
                    {
                        done.Add(ApplyModSync(item, serverMods));
                    }
                    catch (Exception ex)
                    {
                        failed.Add(item.Title + ": " + ex.Message);
                    }
                }

                int ignored, same;
                Dictionary<string, ConfigItem> configs = ComputeConfigSync(Path.Combine(GameDir(), "config"), Path.Combine(server, "config"), out ignored, out same)
                    .ToDictionary(c => "cfg:" + c.Rel.ToLowerInvariant(), c => c);
                string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
                foreach (string id in toServer.Concat(toGame))
                {
                    ConfigItem c;
                    if (!configs.TryGetValue(id, out c)) { failed.Add(id.Substring(4) + ": already the same or gone. Refresh the preview."); continue; }
                    bool up = toServer.Contains(id);
                    FileInfo from = up ? c.Game : c.Server;
                    if (from == null) { failed.Add(c.Rel + ": there's no copy on that side to send."); continue; }
                    string dest = Path.Combine(up ? Path.Combine(server, "config") : Path.Combine(GameDir(), "config"), c.Rel);
                    try
                    {
                        if (File.Exists(dest))
                        {
                            string backup = Path.Combine(dataDir, "sync-backups", stamp, up ? "server" : "game", "config", c.Rel);
                            Directory.CreateDirectory(Path.GetDirectoryName(backup));
                            File.Copy(dest, backup, true);
                        }
                        Directory.CreateDirectory(Path.GetDirectoryName(dest));
                        File.Copy(from.FullName, dest, true);
                        Log("Sync      config " + c.Rel + (up ? " game -> server" : " server -> game"));
                        done.Add("Copied " + c.Rel + (up ? " to the server" : " to the game"));
                    }
                    catch (Exception ex)
                    {
                        failed.Add(c.Rel + ": " + ex.Message);
                    }
                }
            }
            return Obj("ok", true, "done", done, "failed", failed);
        }

        static string ApplyModSync(SyncItem item, string serverMods)
        {
            switch (item.Kind)
            {
                case "add":
                case "replace":
                {
                    string dest = Path.Combine(serverMods, item.Game.Key);
                    if (item.Server != null && !string.Equals(item.Server.File.FullName, dest, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(item.Server.File.FullName, dest + ".disabled", StringComparison.OrdinalIgnoreCase)
                        && File.Exists(dest)) throw new UserError(item.Game.Key + " is already in the server's mods folder.");
                    string temp = dest + ".copying";
                    File.Copy(item.Game.File.FullName, temp, true);
                    try
                    {
                        if (item.Server != null)
                        {
                            AssertNotInUse(item.Server.File.FullName);
                            if (!RecyclePaths(new[] { item.Server.File.FullName })) throw new UserError("Windows couldn't move the old jar to the Recycle Bin.");
                        }
                        if (File.Exists(dest)) throw new UserError(item.Game.Key + " is already in the server's mods folder.");
                        File.Move(temp, dest);
                    }
                    finally
                    {
                        if (File.Exists(temp)) File.Delete(temp);
                    }
                    Log("Sync      " + (item.Server == null ? "added " + item.Game.Key : "replaced " + item.Server.File.Name + " -> " + item.Game.Key) + " on the server");
                    return (item.Server == null ? "Added " : "Updated ") + item.Title + " on the server";
                }
                case "enable":
                {
                    string from = item.Server.File.FullName;
                    string to = from.Substring(0, from.Length - ".disabled".Length);
                    if (File.Exists(to)) throw new UserError(Path.GetFileName(to) + " already exists on the server.");
                    File.Move(from, to);
                    Log("Sync      enabled " + Path.GetFileName(to) + " on the server");
                    return "Turned on " + item.Title + " on the server";
                }
                case "disable":
                case "client-only":
                case "server-only":
                {
                    string from = item.Server.File.FullName;
                    AssertNotInUse(from);
                    if (File.Exists(from + ".disabled")) throw new UserError(item.Server.File.Name + ".disabled already exists on the server.");
                    File.Move(from, from + ".disabled");
                    Log("Sync      disabled " + item.Server.File.Name + " on the server");
                    return "Turned off " + item.Title + " on the server";
                }
                case "to-game":
                    CopyFromServer(item.Server.File.Name);
                    return "Copied " + item.Title + " into the game";
                default:
                    throw new UserError("This one can't be synced automatically.");
            }
        }
    }
}
