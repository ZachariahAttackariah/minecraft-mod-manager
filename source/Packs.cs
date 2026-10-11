// Packs: resource packs, shader packs and data packs, managed like the mods.
//  - Resource packs live in <game>\resourcepacks (the shared folder, when the instance shares them).
//    Turning one on or off edits the resourcePacks list in the game's options.txt.
//  - Shader packs live in <game>\shaderpacks. Iris (Oculus on Forge) keeps the chosen pack in
//    config\iris.properties and each pack's own settings in "<pack>.txt" beside it, which travels with it.
//  - Data packs go into one world (saves\<world>\datapacks, or the server's world), or into every world
//    through Paxi (the datapacks folder next to mods, or config\paxi\datapacks), on the game or the server.
// Modrinth identifies zips by hash like jars, offers updates, and searches and installs each kind.
// Adding a shader pack makes sure Iris is there; adding a data pack to a Paxi folder makes sure Paxi is.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace MinecraftModManager
{
    static partial class Program
    {
        static readonly string[] PackKinds = { "resourcepacks", "shaderpacks", "datapacks" };

        static string KindNoun(string kind)
        {
            return kind == "resourcepacks" ? "resource pack" : kind == "shaderpacks" ? "shader pack" : "data pack";
        }

        // ---------- Where packs go ----------

        sealed class PackPlace
        {
            public string Id, Label, Dir, Side; // Side: "game" or "server"
            public string ModsDir;              // for a Paxi folder: the mods folder Paxi has to be in
        }

        static string ServerLevel()
        {
            string level;
            Dictionary<string, string> props = ReadProperties(Path.Combine(serverDir, "server.properties"));
            return props.TryGetValue("level-name", out level) && !string.IsNullOrWhiteSpace(level) ? level.Trim() : "world";
        }

        // Paxi loads every-world data packs from a datapacks folder in the game (or server) folder, where
        // CurseForge puts them, unless its config turns that off; then from its own config\paxi\datapacks.
        static string PaxiDataDir(string root)
        {
            bool baseFolder = true;
            try
            {
                string config = Path.Combine(root, "config");
                if (Directory.Exists(config))
                    foreach (string file in Directory.GetFiles(config, "paxi*.*"))
                    {
                        Match m = Regex.Match(File.ReadAllText(file), @"Load from base 'datapacks' directory""?\s*[=:]\s*(true|false)", RegexOptions.IgnoreCase);
                        if (m.Success) baseFolder = m.Groups[1].Value.ToLowerInvariant() == "true";
                    }
            }
            catch { }
            return baseFolder ? Path.Combine(root, "datapacks") : Path.Combine(root, "config", "paxi", "datapacks");
        }

        // Paxi's own folder is listed too when packs are sitting in it, so none are out of sight.
        static void AddPaxiPlaces(List<PackPlace> places, string idPrefix, string labelPrefix, string root, string side, string mods)
        {
            string main = PaxiDataDir(root);
            places.Add(new PackPlace { Id = idPrefix + "paxi", Label = labelPrefix + "every world (Paxi)", Dir = main, Side = side, ModsDir = mods });
            string own = Path.Combine(root, "config", "paxi", "datapacks");
            if (!SamePath(own, main) && PackEntries(own).Any())
                places.Add(new PackPlace { Id = idPrefix + "paxi-config", Label = labelPrefix + "every world (Paxi's config folder)", Dir = own, Side = side, ModsDir = mods });
        }

        // Data packs default to the server's every-world folder when there's a server, since that's
        // where they count in multiplayer; otherwise this game's.
        static List<PackPlace> PackPlaces(string kind)
        {
            string game = GameDir();
            var places = new List<PackPlace>();
            if (kind != "datapacks")
            {
                places.Add(new PackPlace { Id = "game", Label = "This game", Dir = Path.Combine(game, kind), Side = "game" });
                return places;
            }
            string server = serverDir;
            if (server != null && Directory.Exists(server))
            {
                AddPaxiPlaces(places, "server-", "Server: ", server, "server", Path.Combine(server, "mods"));
                string level = ServerLevel();
                places.Add(new PackPlace { Id = "server-world", Label = "Server: the world " + level, Dir = Path.Combine(server, level, "datapacks"), Side = "server" });
            }
            AddPaxiPlaces(places, "", "This game: ", game, "game", modsDir);
            string saves = Path.Combine(game, "saves");
            if (Directory.Exists(saves))
                foreach (DirectoryInfo world in new DirectoryInfo(saves).GetDirectories()
                    .Where(d => File.Exists(Path.Combine(d.FullName, "level.dat"))).OrderByDescending(d => d.LastWriteTimeUtc))
                    places.Add(new PackPlace { Id = "world:" + world.Name, Label = "This game: the world " + world.Name, Dir = Path.Combine(world.FullName, "datapacks"), Side = "game" });
            return places;
        }

        static PackPlace FindPlace(string kind, string id)
        {
            if (Array.IndexOf(PackKinds, kind) < 0) throw new UserError("Unknown kind of pack.");
            List<PackPlace> places = PackPlaces(kind);
            PackPlace place = string.IsNullOrEmpty(id) ? places.FirstOrDefault() : places.FirstOrDefault(p => p.Id == id);
            if (place == null) throw new UserError("That folder isn't there any more. Refresh the page.");
            return place;
        }

        // Removing or replacing a pack the game or server has open would fail halfway.
        static void GuardPlace(PackPlace place)
        {
            if (place.Side == "server" && ServerBusy()) throw new UserError("Stop the server first. It keeps its data packs open while it runs.");
            if (place.Side == "game" && GameRunning(GameDir())) throw new UserError("Close the game first. It keeps its packs open while it runs.");
        }

        static IEnumerable<FileSystemInfo> PackEntries(string dir)
        {
            if (!Directory.Exists(dir)) return new FileSystemInfo[0];
            return new DirectoryInfo(dir).GetFileSystemInfos()
                .Where(e => e is DirectoryInfo ? !e.Name.StartsWith(".") : e.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase);
        }

        static string ResolvePack(PackPlace place, string name)
        {
            if (string.IsNullOrEmpty(name) || name != Path.GetFileName(name) || name.StartsWith(".")) throw new UserError("That isn't a pack name.");
            string path = Path.Combine(place.Dir, name);
            if (!File.Exists(path) && !Directory.Exists(path)) throw new UserError(name + " isn't there any more. Refresh the page.");
            return path;
        }

        // ---------- What's inside a pack ----------

        sealed class PackMeta
        {
            public string Description, Icon;
            public int Format;
            public bool HasMeta, HasAssets, HasData, HasShaders;
        }

        static readonly Dictionary<string, PackMeta> PackMetaCache = new Dictionary<string, PackMeta>(StringComparer.OrdinalIgnoreCase);

        static PackMeta CachedPackMeta(FileSystemInfo entry)
        {
            string key = entry.FullName + "|" + entry.LastWriteTimeUtc.Ticks + "|" + (entry is FileInfo ? ((FileInfo)entry).Length : 0);
            PackMeta meta;
            lock (PackMetaCache) if (PackMetaCache.TryGetValue(key, out meta)) return meta;
            meta = ReadPackMeta(entry);
            lock (PackMetaCache) PackMetaCache[key] = meta;
            return meta;
        }

        static PackMeta ReadPackMeta(FileSystemInfo entry)
        {
            var meta = new PackMeta();
            try
            {
                if (entry is DirectoryInfo)
                {
                    string dir = entry.FullName;
                    meta.HasAssets = Directory.Exists(Path.Combine(dir, "assets"));
                    meta.HasData = Directory.Exists(Path.Combine(dir, "data"));
                    meta.HasShaders = Directory.Exists(Path.Combine(dir, "shaders"));
                    string mcmeta = Path.Combine(dir, "pack.mcmeta");
                    if (File.Exists(mcmeta)) { meta.HasMeta = true; ParseMcmeta(File.ReadAllText(mcmeta, Encoding.UTF8), meta); }
                    string png = Path.Combine(dir, "pack.png");
                    if (File.Exists(png) && new FileInfo(png).Length < 262144) meta.Icon = "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(png));
                    return meta;
                }
                using (ZipArchive zip = ZipFile.OpenRead(entry.FullName))
                {
                    foreach (ZipArchiveEntry e in zip.Entries)
                    {
                        string n = e.FullName.Replace('\\', '/');
                        if (n.StartsWith("assets/")) meta.HasAssets = true;
                        else if (n.StartsWith("data/")) meta.HasData = true;
                        else if (n.StartsWith("shaders/")) meta.HasShaders = true;
                    }
                    ZipArchiveEntry mcmeta = zip.GetEntry("pack.mcmeta");
                    if (mcmeta != null)
                    {
                        meta.HasMeta = true;
                        using (var reader = new StreamReader(mcmeta.Open(), Encoding.UTF8)) ParseMcmeta(reader.ReadToEnd(), meta);
                    }
                    ZipArchiveEntry png = zip.GetEntry("pack.png");
                    if (png != null && png.Length < 262144)
                        using (Stream s = png.Open())
                        using (var ms = new MemoryStream())
                        {
                            s.CopyTo(ms);
                            meta.Icon = "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
                        }
                }
            }
            catch { }
            return meta;
        }

        static void ParseMcmeta(string text, PackMeta meta)
        {
            try
            {
                var json = new JavaScriptSerializer().DeserializeObject(text.TrimStart('﻿')) as Dictionary<string, object>;
                Dictionary<string, object> pack = D(Get(json, "pack"));
                int format;
                if (int.TryParse(S(pack, "pack_format"), out format)) meta.Format = format;
                meta.Description = Regex.Replace(TextOf(Get(pack, "description")), "§.", "").Trim();
            }
            catch { }
        }

        // A pack description can be plain text or a chat component (object or list).
        static string TextOf(object component)
        {
            if (component == null) return "";
            if (component is string) return (string)component;
            if (component is object[]) return string.Concat(((object[])component).Select(TextOf));
            Dictionary<string, object> d = D(component);
            if (d == null) return Convert.ToString(component, CultureInfo.InvariantCulture);
            return (S(d, "text") ?? S(d, "translate") ?? "") + TextOf(Get(d, "extra"));
        }

        // Which folders a zip belongs in, by what's inside it: "resourcepacks", "datapacks", both, or
        // "shaderpacks". Null when it doesn't look like any pack.
        static string[] PackKindsOf(string path)
        {
            PackMeta m = ReadPackMeta(new FileInfo(path));
            if (m.HasShaders && !m.HasMeta) return new[] { "shaderpacks" };
            if (m.HasMeta && m.HasData && m.HasAssets) return new[] { "resourcepacks", "datapacks" };
            if (m.HasMeta && m.HasData) return new[] { "datapacks" };
            if (m.HasMeta) return new[] { "resourcepacks" };
            return null;
        }

        // ---------- Resource packs on and off (options.txt) ----------

        static string GameOptionsPath() { return Path.Combine(GameDir(), "options.txt"); }

        static List<string> ReadPackList(string optionsPath, string key)
        {
            try
            {
                if (!File.Exists(optionsPath)) return new List<string>();
                string line = File.ReadAllLines(optionsPath, Encoding.UTF8).FirstOrDefault(l => l.StartsWith(key + ":"));
                if (line == null) return new List<string>();
                return A(new JavaScriptSerializer().DeserializeObject(line.Substring(key.Length + 1))).Select(o => Convert.ToString(o, CultureInfo.InvariantCulture)).ToList();
            }
            catch { return new List<string>(); }
        }

        static void WritePackList(string optionsPath, string key, List<string> list)
        {
            List<string> lines = File.Exists(optionsPath) ? File.ReadAllLines(optionsPath, Encoding.UTF8).ToList() : new List<string>();
            string value = key + ":" + new JavaScriptSerializer().Serialize(list);
            int at = lines.FindIndex(l => l.StartsWith(key + ":"));
            if (at >= 0) lines[at] = value; else lines.Add(value);
            File.WriteAllLines(optionsPath, lines, new UTF8Encoding(false));
        }

        static Dictionary<string, object> SetResourcePackOn(string name, bool on)
        {
            PackPlace place = FindPlace("resourcepacks", "game");
            ResolvePack(place, name);
            if (GameRunning(GameDir())) throw new UserError("Close the game first. It writes its own list of resource packs when it closes.");
            string options = GameOptionsPath();
            string id = "file/" + name;
            List<string> list = ReadPackList(options, "resourcePacks");
            list.RemoveAll(x => x == id);
            if (on)
            {
                if (list.Count == 0) list.Add("vanilla");
                list.Add(id); // last in the list is applied last, so it wins over the others
            }
            WritePackList(options, "resourcePacks", list);
            if (!on)
            {
                List<string> incompatible = ReadPackList(options, "incompatibleResourcePacks");
                if (incompatible.Remove(id)) WritePackList(options, "incompatibleResourcePacks", incompatible);
            }
            Log((on ? "Turned on  " : "Turned off ") + "resource pack " + name);
            return Obj("ok", true);
        }

        // ---------- Shaders (Iris / Oculus) ----------

        // The enabled (else disabled) jar in a mods folder whose mod id is one of these, or null.
        static FileInfo FindModJar(string dir, params string[] ids)
        {
            if (!Directory.Exists(dir)) return null;
            List<FileInfo> matches = new DirectoryInfo(dir).GetFiles().Where(f => JarName.IsMatch(f.Name))
                .Where(f => { string id = GetJarInfo(f)[0]; return id != null && ids.Contains(id); }).ToList();
            return matches.FirstOrDefault(f => !f.Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)) ?? matches.FirstOrDefault();
        }

        static string ShaderModId()
        {
            FileInfo jar = FindModJar(modsDir, "iris", "oculus");
            return jar == null ? null : GetJarInfo(jar)[0];
        }

        static string ShaderConfigPath(string modId) { return Path.Combine(GameDir(), "config", modId + ".properties"); }

        static Dictionary<string, string> ReadJavaProps(string path)
        {
            var props = new Dictionary<string, string>();
            if (!File.Exists(path)) return props;
            foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                string line = raw.TrimStart();
                if (line.Length == 0 || line[0] == '#' || line[0] == '!') continue;
                int at = line.IndexOfAny(new[] { '=', ':' });
                if (at <= 0) continue;
                props[line.Substring(0, at).Trim()] = Regex.Replace(line.Substring(at + 1).TrimStart(), @"\\(.)", "$1");
            }
            return props;
        }

        static void SetJavaProp(string path, string key, string value)
        {
            List<string> lines = File.Exists(path) ? File.ReadAllLines(path, Encoding.UTF8).ToList() : new List<string>();
            string escaped = Regex.Replace(value, @"([\\:=#!])", @"\$1");
            if (escaped.StartsWith(" ")) escaped = "\\" + escaped;
            int at = lines.FindIndex(l => Regex.IsMatch(l, "^\\s*" + Regex.Escape(key) + "\\s*[=:]"));
            if (at >= 0) lines[at] = key + "=" + escaped; else lines.Add(key + "=" + escaped);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllLines(path, lines, new UTF8Encoding(false));
        }

        // name null: shaders off.
        static Dictionary<string, object> SetShader(string name)
        {
            string mod = ShaderModId();
            if (mod == null) throw new UserError("Iris isn't installed in this instance. Installing a shader pack from here adds it.");
            if (GameRunning(GameDir())) throw new UserError("Close the game first, or pick the shader in the game's own Shader Packs screen.");
            string path = ShaderConfigPath(mod);
            if (!string.IsNullOrEmpty(name))
            {
                ResolvePack(FindPlace("shaderpacks", "game"), name);
                SetJavaProp(path, "shaderPack", name);
            }
            SetJavaProp(path, "enableShaders", string.IsNullOrEmpty(name) ? "false" : "true");
            Log("Shaders   " + (string.IsNullOrEmpty(name) ? "turned off" : "now " + name));
            return Obj("ok", true);
        }

        // ---------- The mods some packs need ----------

        // The loader the server runs, from what its installer left behind, or null if it can't tell.
        static string ServerLoader(string server)
        {
            if (Directory.Exists(Path.Combine(server, "libraries", "net", "neoforged"))) return "neoforge";
            if (Directory.Exists(Path.Combine(server, "libraries", "net", "minecraftforge"))) return "forge";
            if (File.Exists(Path.Combine(server, "fabric-server-launch.jar")) || Directory.Exists(Path.Combine(server, ".fabric"))) return "fabric";
            if (File.Exists(Path.Combine(server, "quilt-server-launch.jar"))) return "quilt";
            return null;
        }

        // Shader packs need Iris (Oculus on Forge); data packs in a Paxi folder need Paxi. Turns the mod
        // back on if it's there but off, or installs it from Modrinth with the mods it needs. Returns the
        // names of what was added or turned on.
        static List<string> EnsurePackMod(string kind, PackPlace place, List<string> notes)
        {
            var added = new List<string>();
            string dir, slug, title;
            string[] ids;
            if (kind == "shaderpacks") { dir = modsDir; ids = new[] { "iris", "oculus" }; slug = loader == "forge" ? "oculus" : "iris"; title = loader == "forge" ? "Oculus" : "Iris"; }
            else if (kind == "datapacks" && place.ModsDir != null) { dir = place.ModsDir; ids = new[] { "paxi" }; slug = "paxi"; title = "Paxi"; }
            else return added;
            bool onServer = !SamePath(dir, modsDir);
            try
            {
                FileInfo jar = FindModJar(dir, ids);
                if (jar != null)
                {
                    if (!jar.Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)) return added;
                    if (onServer)
                    {
                        if (ServerBusy()) throw new UserError("it's turned off on the server, and the server is running. Stop it, then turn " + title + " on");
                        string enabledName = jar.Name.Substring(0, jar.Name.Length - ".disabled".Length);
                        File.Move(jar.FullName, Path.Combine(dir, enabledName));
                        Log("Enabled   " + jar.Name + " -> " + enabledName + " (server)");
                    }
                    else SetEnabled(jar.Name, true);
                    added.Add(title + " (turned back on)");
                    return added;
                }
                if (onServer)
                {
                    string serverLoader = ServerLoader(Path.GetDirectoryName(dir));
                    if (serverLoader != null && serverLoader != loader)
                        throw new UserError("the server runs " + LoaderName(serverLoader) + " and this instance " + LoaderName(loader) + ", so add it to the server by hand");
                }
                lock (ModrinthLock)
                {
                    Directory.CreateDirectory(dir);
                    HashSet<string> installed = InstalledProjects(dir);
                    Dictionary<string, object> project = GetProject(slug);
                    if (project == null) throw new UserError("it wasn't found on Modrinth");
                    if (installed.Contains(S(project, "id"))) return added;
                    var plan = new List<PlanItem>();
                    Resolve(S(project, "id"), null, null, installed, new HashSet<string>(installed), plan, notes);
                    foreach (object item in InstallPlan(plan, dir, notes)) added.Add(S(D(item), "title"));
                }
                if (added.Count > 0 && onServer) notes.Add("Restart the server so it loads " + string.Join(" and ", added) + ".");
            }
            catch (UserError ex)
            {
                notes.Add("Couldn't add " + title + ": " + ex.Message.TrimEnd('.') + ".");
            }
            return added;
        }

        // ---------- Listing ----------

        static Dictionary<string, object> ListPacks(string kind, string placeId, bool check)
        {
            PackPlace place = FindPlace(kind, placeId);
            List<FileSystemInfo> entries = PackEntries(place.Dir).ToList();
            Dictionary<string, object> modrinth = new Dictionary<string, object>();
            string modrinthError = null;
            try { modrinth = PackModrinthInfo(kind, entries.OfType<FileInfo>().ToList(), check); }
            catch (UserError ex) { modrinthError = ex.Message; }

            List<string> enabled = kind == "resourcepacks" ? ReadPackList(GameOptionsPath(), "resourcePacks") : new List<string>();
            FileInfo shaderJar = kind == "shaderpacks" ? FindModJar(modsDir, "iris", "oculus") : null;
            string shaderMod = shaderJar == null ? null : GetJarInfo(shaderJar)[0];
            bool shaderModOff = shaderJar != null && shaderJar.Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
            Dictionary<string, string> shaderProps = shaderMod != null ? ReadJavaProps(ShaderConfigPath(shaderMod)) : new Dictionary<string, string>();
            string active = null, enableShaders;
            if (shaderMod != null && shaderProps.TryGetValue("enableShaders", out enableShaders) && enableShaders == "true") shaderProps.TryGetValue("shaderPack", out active);

            var packs = new List<object>();
            foreach (FileSystemInfo e in entries)
            {
                PackMeta meta = CachedPackMeta(e);
                object on = null;
                if (kind == "resourcepacks") on = enabled.IndexOf("file/" + e.Name); // its place in the list, -1 when off
                else if (kind == "shaderpacks") on = active == e.Name;
                packs.Add(Obj("name", e.Name, "folder", e is DirectoryInfo, "size", e is FileInfo ? ((FileInfo)e).Length : FolderSize((DirectoryInfo)e),
                    "modified", e.LastWriteTimeUtc.ToString("o"), "description", meta.Description, "format", meta.Format, "icon", meta.Icon,
                    "on", on, "modrinth", Get(modrinth, e.Name)));
            }

            var places = PackPlaces(kind).Select(p =>
            {
                FileInfo paxi = p.ModsDir == null ? null : FindModJar(p.ModsDir, "paxi");
                return (object)Obj("id", p.Id, "label", p.Label, "path", p.Dir, "side", p.Side, "paxi", p.ModsDir != null,
                    "paxiInstalled", paxi != null, "paxiOff", paxi != null && paxi.Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase),
                    "count", PackEntries(p.Dir).Count());
            }).ToList();

            // Who else sees these packs, when the folder is shared.
            List<object> sharedWith = null;
            if (kind != "datapacks")
            {
                string[] me = PrismInstanceFor(modsDir);
                InstanceInfo current = me == null ? null : PrismInstances().FirstOrDefault(i => i.Id == me[0]);
                if (current != null && SharesFolder(current, kind))
                    sharedWith = PrismInstances().Where(i => i.Id != current.Id && SharesFolder(i, kind)).Select(i => (object)i.Name).ToList();
            }

            return Obj("ok", true, "kind", kind, "place", place.Id, "path", place.Dir, "side", place.Side, "places", places, "packs", packs,
                "sharedWith", sharedWith, "modrinthError", modrinthError,
                "shaderMod", shaderMod, "shaderModOff", shaderModOff, "shadersOn", active != null,
                "gameRunning", place.Side == "game" && GameRunning(GameDir()), "serverRunning", place.Side == "server" && ServerBusy(),
                "target", TargetObj());
        }

        static void OpenPackFolder(string kind, string placeId)
        {
            PackPlace place = FindPlace(kind, placeId);
            Directory.CreateDirectory(place.Dir);
            try { System.Diagnostics.Process.Start("explorer.exe", "\"" + place.Dir + "\""); } catch { }
        }

        // ---------- Modrinth ----------

        static string[] PackLoaders(string kind)
        {
            return kind == "resourcepacks" ? new[] { "minecraft" } : kind == "datapacks" ? new[] { "datapack" } : new[] { "iris", "optifine" };
        }

        static string ModrinthPackType(string kind)
        {
            return kind == "resourcepacks" ? "resourcepack" : kind == "datapacks" ? "datapack" : "shader";
        }

        static readonly Dictionary<string, Dictionary<string, object>> PackLatestByKey = new Dictionary<string, Dictionary<string, object>>();
        static readonly Dictionary<string, DateTime> PackCheckedAt = new Dictionary<string, DateTime>();

        // The .zip of a Modrinth version: its primary file if that's a zip, else its first zip.
        static Dictionary<string, object> PackFile(Dictionary<string, object> version)
        {
            Dictionary<string, object> first = null;
            foreach (object item in A(Get(version, "files")))
            {
                Dictionary<string, object> f = D(item);
                if (f == null || !(S(f, "filename") ?? "").EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                if (Get(f, "primary") is bool && (bool)Get(f, "primary")) return f;
                if (first == null) first = f;
            }
            return first;
        }

        // Newest release (else newest of any kind) with a zip. Shader packs work across Minecraft
        // versions and many only list the newest ones, so they aren't filtered by version.
        static Dictionary<string, object> BestPackVersion(string projectId, string kind)
        {
            string query = "/project/" + Uri.EscapeDataString(projectId) + "/version?loaders=" + JsonArrayParam(PackLoaders(kind));
            if (kind != "shaderpacks") query += "&game_versions=" + JsonArrayParam(gameVersion);
            List<Dictionary<string, object>> versions = A(ModrinthGet(query)).Select(D).Where(v => v != null && PackFile(v) != null)
                .OrderByDescending(v => S(v, "date_published")).ToList();
            return versions.FirstOrDefault(v => S(v, "version_type") == "release") ?? versions.FirstOrDefault();
        }

        static Dictionary<string, object> NewerPack(Dictionary<string, object> current, Dictionary<string, object> latest)
        {
            if (current == null || latest == null || S(latest, "id") == S(current, "id")) return null;
            return string.CompareOrdinal(S(latest, "date_published") ?? "", S(current, "date_published") ?? "") > 0 ? latest : null;
        }

        // file name -> what Modrinth knows about it (and a newer version, if there is one).
        static Dictionary<string, object> PackModrinthInfo(string kind, List<FileInfo> files, bool force)
        {
            lock (ModrinthLock)
            {
                Dictionary<string, string> hashes = IdentifyFiles(files);
                string prefix = kind + "|" + gameVersion + "|";
                DateTime checkedAt;
                bool stale = force || !PackCheckedAt.TryGetValue(prefix, out checkedAt) || DateTime.UtcNow - checkedAt > TimeSpan.FromMinutes(30);
                List<string> identified = hashes.Values.Distinct().Where(h => VersionByHash[h] != null).ToList();
                List<string> toCheck = stale ? identified : identified.Where(h => !PackLatestByKey.ContainsKey(prefix + h)).ToList();
                if (kind == "shaderpacks")
                {
                    var byProject = new Dictionary<string, Dictionary<string, object>>();
                    foreach (string h in toCheck)
                    {
                        string project = S(VersionByHash[h], "project_id");
                        if (!byProject.ContainsKey(project)) byProject[project] = BestPackVersion(project, kind);
                        PackLatestByKey[prefix + h] = byProject[project];
                    }
                }
                else
                {
                    for (int i = 0; i < toCheck.Count; i += 500)
                    {
                        List<string> chunk = toCheck.Skip(i).Take(500).ToList();
                        Dictionary<string, object> found = D(ModrinthPost("/version_files/update", Obj("hashes", chunk, "algorithm", "sha1",
                            "loaders", PackLoaders(kind), "game_versions", new[] { gameVersion }))) ?? new Dictionary<string, object>();
                        foreach (string h in chunk) PackLatestByKey[prefix + h] = D(Get(found, h));
                    }
                }
                if (stale) PackCheckedAt[prefix] = DateTime.UtcNow;

                var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, string> pair in hashes)
                {
                    Dictionary<string, object> version = VersionByHash[pair.Value];
                    if (version == null) continue;
                    Dictionary<string, object> project, latest;
                    ProjectById.TryGetValue(S(version, "project_id") ?? "", out project);
                    PackLatestByKey.TryGetValue(prefix + pair.Value, out latest);
                    latest = NewerPack(version, latest);
                    result[pair.Key] = Obj("projectId", S(version, "project_id"), "slug", S(project, "slug"), "title", S(project, "title"),
                        "versionNumber", S(version, "version_number"),
                        "update", latest == null ? null : Obj("versionId", S(latest, "id"), "versionNumber", S(latest, "version_number")));
                }
                return result;
            }
        }

        static Dictionary<string, object> SearchPacks(string kind, string placeId, string query, int offset, string sort)
        {
            PackPlace place = FindPlace(kind, placeId);
            string index = sort == "downloads" || sort == "updated" || sort == "newest" || sort == "follows" ? sort : "relevance";
            var facets = new List<object> { new[] { "project_type:" + ModrinthPackType(kind) } };
            if (kind == "shaderpacks") facets.Add(new[] { "categories:iris" });
            else facets.Add(new[] { "versions:" + gameVersion });
            Dictionary<string, object> result = D(ModrinthGet("/search?limit=20&offset=" + Math.Max(0, offset) + "&index=" + index +
                "&query=" + Uri.EscapeDataString(query ?? "") + "&facets=" + Uri.EscapeDataString(new JavaScriptSerializer().Serialize(facets))));
            if (result == null) throw new UserError("Modrinth's search didn't answer. Try again.");
            HashSet<string> installed;
            lock (ModrinthLock) installed = InstalledPackProjects(place.Dir);
            var hits = new List<object>();
            foreach (object item in A(Get(result, "hits")))
            {
                Dictionary<string, object> hit = D(item);
                hits.Add(Obj("projectId", S(hit, "project_id"), "slug", S(hit, "slug"), "title", S(hit, "title"), "author", S(hit, "author"),
                    "description", S(hit, "description"), "icon", S(hit, "icon_url"), "downloads", Get(hit, "downloads"),
                    "updated", S(hit, "date_modified"), "installed", installed.Contains(S(hit, "project_id") ?? "")));
            }
            return Obj("ok", true, "target", TargetObj(), "total", Get(result, "total_hits"), "offset", offset, "hits", hits);
        }

        static HashSet<string> InstalledPackProjects(string dir)
        {
            var installed = new HashSet<string>();
            foreach (string h in IdentifyFiles(PackEntries(dir).OfType<FileInfo>()).Values)
                if (VersionByHash[h] != null) installed.Add(S(VersionByHash[h], "project_id"));
            return installed;
        }

        static Dictionary<string, object> InstallPack(string kind, string placeId, string projectId)
        {
            if (string.IsNullOrWhiteSpace(projectId)) throw new UserError("No pack was chosen.");
            PackPlace place = FindPlace(kind, placeId);
            var notes = new List<string>();
            string file, title;
            lock (ModrinthLock)
            {
                Dictionary<string, object> project = GetProject(projectId);
                if (project == null) throw new UserError("That pack wasn't found on Modrinth.");
                title = S(project, "title");
                if (InstalledPackProjects(place.Dir).Contains(S(project, "id"))) throw new UserError(title + " is already there.");
                Dictionary<string, object> version = BestPackVersion(S(project, "id"), kind);
                if (version == null)
                    throw new UserError("Modrinth has no " + KindNoun(kind) + " download of " + title + (kind == "shaderpacks" ? " for Iris." : " for Minecraft " + gameVersion + "."));
                Directory.CreateDirectory(place.Dir);
                file = DownloadPackVersion(version, place.Dir, null);
                Log("Installed " + file + " from Modrinth into " + place.Label + " (" + place.Dir + ")");
            }
            List<string> added = EnsurePackMod(kind, place, notes);
            if (place.Side == "server" && ServerBusy()) notes.Add("The server is running, so it picks the pack up after /reload or a restart.");
            return Obj("ok", true, "file", file, "title", title, "added", added, "notes", notes);
        }

        static string DownloadPackVersion(Dictionary<string, object> version, string dir, string replacing)
        {
            Dictionary<string, object> file = PackFile(version);
            if (file == null) throw new UserError("Modrinth listed no .zip for " + S(version, "name") + ".");
            string name = Path.GetFileName(S(file, "filename") ?? "");
            if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new UserError("Modrinth gave a file name that can't be used, so nothing was installed.");
            Uri url;
            if (!Uri.TryCreate(S(file, "url"), UriKind.Absolute, out url) || url.Scheme != Uri.UriSchemeHttps
                || !(url.Host == "cdn.modrinth.com" || url.Host.EndsWith(".modrinth.com", StringComparison.OrdinalIgnoreCase)))
                throw new UserError("Modrinth gave an unexpected download address for " + name + ", so it wasn't installed.");
            string target = Path.Combine(dir, name);
            bool sameName = replacing != null && string.Equals(Path.GetFileName(replacing), name, StringComparison.OrdinalIgnoreCase);
            if (!sameName && (File.Exists(target) || Directory.Exists(target))) throw new UserError(name + " is already there.");
            string temp = target + ".download";
            try
            {
                try
                {
                    var request = (HttpWebRequest)WebRequest.Create(url);
                    request.UserAgent = ModrinthUserAgent;
                    request.Timeout = 60000;
                    request.ReadWriteTimeout = 120000;
                    using (WebResponse response = request.GetResponse())
                    using (Stream input = response.GetResponseStream())
                    using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                        input.CopyTo(output);
                }
                catch (WebException ex) { throw new UserError("Couldn't download " + name + " (" + ex.Message + ")."); }
                string expected = S(D(Get(file, "hashes")), "sha1"), actual;
                using (SHA1 sha = SHA1.Create())
                using (FileStream stream = File.OpenRead(temp))
                    actual = Hex(sha.ComputeHash(stream));
                if (expected != null && !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                    throw new UserError("The download of " + name + " didn't match Modrinth's checksum, so it was thrown away.");
                if (replacing != null && !RecyclePaths(new[] { replacing }))
                    throw new UserError("Couldn't move the old " + Path.GetFileName(replacing) + " to the Recycle Bin, so nothing was changed.");
                File.Move(temp, target);
                return name;
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }

        static Dictionary<string, object> UpdatePacks(string kind, string placeId, List<string> names)
        {
            if (names.Count == 0) throw new UserError("No packs were chosen.");
            PackPlace place = FindPlace(kind, placeId);
            GuardPlace(place);
            var results = new List<object>();
            lock (ModrinthLock)
            {
                List<FileInfo> files = PackEntries(place.Dir).OfType<FileInfo>().ToList();
                Dictionary<string, string> hashes = IdentifyFiles(files);
                string prefix = kind + "|" + gameVersion + "|";
                foreach (string name in names)
                {
                    try
                    {
                        string path = ResolvePack(place, name);
                        string hash;
                        if (!hashes.TryGetValue(name, out hash) || VersionByHash[hash] == null) throw new UserError(name + " isn't on Modrinth, so it can't be updated here.");
                        Dictionary<string, object> current = VersionByHash[hash], latest;
                        if (!PackLatestByKey.TryGetValue(prefix + hash, out latest)) PackLatestByKey[prefix + hash] = latest = BestPackVersion(S(current, "project_id"), kind);
                        latest = NewerPack(current, latest);
                        if (latest == null) throw new UserError(name + " is already up to date.");
                        string newName = DownloadPackVersion(latest, place.Dir, path);
                        CarryPackState(kind, place, name, newName);
                        Log("Updated   " + name + " -> " + newName + " (Modrinth " + S(current, "version_number") + " -> " + S(latest, "version_number") + ", " + place.Label + ")");
                        results.Add(Obj("file", name, "newFile", newName));
                    }
                    catch (UserError ex) { results.Add(Obj("file", name, "error", ex.Message)); }
                }
            }
            return Obj("ok", true, "results", results);
        }

        // An updated pack keeps its place: still on (resource packs), still the chosen shader with its settings.
        static void CarryPackState(string kind, PackPlace place, string oldName, string newName)
        {
            if (oldName == newName) return;
            try
            {
                if (kind == "resourcepacks")
                {
                    string options = GameOptionsPath();
                    foreach (string key in new[] { "resourcePacks", "incompatibleResourcePacks" })
                    {
                        List<string> list = ReadPackList(options, key);
                        int at = list.IndexOf("file/" + oldName);
                        if (at < 0) continue;
                        list[at] = "file/" + newName;
                        WritePackList(options, key, list);
                    }
                }
                else if (kind == "shaderpacks")
                {
                    string settings = Path.Combine(place.Dir, oldName + ".txt"), newSettings = Path.Combine(place.Dir, newName + ".txt");
                    if (File.Exists(settings) && !File.Exists(newSettings)) File.Move(settings, newSettings);
                    string mod = ShaderModId();
                    string active;
                    if (mod != null && ReadJavaProps(ShaderConfigPath(mod)).TryGetValue("shaderPack", out active) && active == oldName)
                        SetJavaProp(ShaderConfigPath(mod), "shaderPack", newName);
                }
            }
            catch (Exception ex) { Log("Updated   " + newName + ", but couldn't carry over its settings: " + ex.Message); }
        }

        static Dictionary<string, object> DeletePacks(string kind, string placeId, List<string> names)
        {
            if (names.Count == 0) throw new UserError("No packs were chosen.");
            PackPlace place = FindPlace(kind, placeId);
            GuardPlace(place);
            var paths = new List<string>();
            foreach (string name in names)
            {
                string path = ResolvePack(place, name);
                paths.Add(path);
                // Iris keeps a shader's own settings next to it.
                if (kind == "shaderpacks" && File.Exists(path + ".txt")) paths.Add(path + ".txt");
            }
            if (!RecyclePaths(paths)) throw new UserError("Windows couldn't move everything to the Recycle Bin (a file may be in use). Refresh to see what's left.");
            Log("Deleted   " + string.Join(", ", names) + " from " + place.Label + " (moved to the Recycle Bin)");
            return Obj("ok", true);
        }

        // ---------- Adding pack files ----------

        static Dictionary<string, object> AddPackFiles(string kind, string placeId)
        {
            PackPlace place = FindPlace(kind, placeId);
            string[] picked = null;
            ui.Send(delegate { picked = PickFiles("Add " + KindNoun(kind) + "s", DownloadsFolder()); }, null);
            var added = new List<object>();
            var notes = new List<string>();
            if (picked == null) return Obj("ok", true, "added", added, "mods", new List<string>(), "notes", notes);
            foreach (string path in picked)
            {
                string name = Path.GetFileName(path);
                string[] kinds = PackKindsOf(path);
                if (kinds == null) { notes.Add(name + " doesn't look like a pack, so it wasn't added."); continue; }
                if (!kinds.Contains(kind)) { notes.Add(name + " is a " + KindNoun(kinds[0]) + ", so it wasn't added here."); continue; }
                string target = Path.Combine(place.Dir, name);
                if (File.Exists(target) || Directory.Exists(target)) { notes.Add(name + " is already there."); continue; }
                Directory.CreateDirectory(place.Dir);
                File.Copy(path, target);
                Log("Added     " + name + " to " + place.Label + " (" + place.Dir + ")");
                added.Add(name);
            }
            List<string> mods = added.Count > 0 ? EnsurePackMod(kind, place, notes) : new List<string>();
            return Obj("ok", true, "added", added, "mods", mods, "notes", notes);
        }

        static string DownloadsFolder()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }

        // Runs on the UI thread, like PickFolder.
        static string[] PickFiles(string title, string startIn)
        {
            using (var owner = new Form())
            using (var dialog = new OpenFileDialog())
            {
                owner.ShowInTaskbar = false;
                owner.TopMost = true;
                owner.FormBorderStyle = FormBorderStyle.None;
                owner.StartPosition = FormStartPosition.CenterScreen;
                owner.Size = new System.Drawing.Size(1, 1);
                owner.Opacity = 0;
                dialog.Title = title;
                dialog.Filter = "Pack files (*.zip)|*.zip";
                dialog.Multiselect = true;
                if (Directory.Exists(startIn)) dialog.InitialDirectory = startIn;
                owner.Show();
                owner.Activate();
                return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.FileNames : null;
            }
        }

        // A .zip downloaded in a tab: resource and shader packs go into this game, data packs into the
        // default data pack folder (the server's every-world folder when there is a server), and anything
        // else into Downloads. It downloads to the app's own folder first, so it can look inside.
        static void HandlePackDownload(CoreWebView2DownloadStartingEventArgs e, Action<string> report)
        {
            string name = Path.GetFileName(e.ResultFilePath);
            string downloads = Path.GetDirectoryName(e.ResultFilePath);
            string staging = Path.Combine(dataDir, "downloads");
            Directory.CreateDirectory(staging);
            string temp = Path.Combine(staging, Guid.NewGuid().ToString("N") + ".zip");
            e.ResultFilePath = temp;
            e.Handled = true;
            report("Downloading " + name + "…");
            CoreWebView2DownloadOperation op = e.DownloadOperation;
            op.StateChanged += delegate
            {
                if (op.State == CoreWebView2DownloadState.Interrupted) { report("Download of " + name + " failed (" + op.InterruptReason + ")."); return; }
                if (op.State != CoreWebView2DownloadState.Completed) return;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    string message;
                    try { message = PlaceDownloadedPack(temp, name, downloads); }
                    catch (Exception ex) { message = "Couldn't place " + name + ": " + ex.Message; }
                    finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
                    ui.Post(delegate
                    {
                        report(message);
                        if (window != null && !window.IsDisposed) window.Notify("packs");
                    }, null);
                });
            };
        }

        static string PlaceDownloadedPack(string temp, string name, string downloads)
        {
            string[] kinds = PackKindsOf(temp);
            if (kinds == null)
            {
                string saved = FreeFileName(downloads, name);
                Directory.CreateDirectory(downloads);
                File.Move(temp, saved);
                return "Saved " + Path.GetFileName(saved) + " to your Downloads folder.";
            }
            string kind = kinds[0];
            PackPlace place = FindPlace(kind, null);
            string target = Path.Combine(place.Dir, name);
            if (File.Exists(target) || Directory.Exists(target)) return name + " is already there (" + place.Label + "), so it wasn't added again.";
            Directory.CreateDirectory(place.Dir);
            File.Move(temp, target);
            Log("Downloaded " + name + " into " + place.Label + " (" + place.Dir + ")");
            var notes = new List<string>();
            List<string> mods = EnsurePackMod(kind, place, notes);
            string where = kind == "datapacks" ? place.Label : "this game's " + KindNoun(kind) + "s";
            List<string> turnedOn = mods.Where(m => m.EndsWith(" (turned back on)")).Select(m => m.Substring(0, m.Length - " (turned back on)".Length)).ToList();
            List<string> installed = mods.Where(m => !m.EndsWith(" (turned back on)")).ToList();
            return "Added " + name + " to " + where + "." +
                (installed.Count > 0 ? " Also installed " + string.Join(" and ", installed) + ", which " + (installed.Count == 1 ? "is" : "are") + " needed for it." : "") +
                (turnedOn.Count > 0 ? " Turned " + string.Join(" and ", turnedOn) + " back on, since it's needed." : "") +
                (notes.Count > 0 ? " " + string.Join(" ", notes) : "");
        }

        static string FreeFileName(string dir, string name)
        {
            string path = Path.Combine(dir, name);
            for (int n = 2; File.Exists(path); n++) path = Path.Combine(dir, Path.GetFileNameWithoutExtension(name) + " (" + n + ")" + Path.GetExtension(name));
            return path;
        }
    }
}
