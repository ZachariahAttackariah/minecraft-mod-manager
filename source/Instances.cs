// Instances: the Prism Launcher instances on this PC, plus other mods folders used before, for the
// picker next to the Play button. Choosing one switches the managed mods folder, and for a Prism instance
// the Minecraft version and loader that Modrinth lookups use follow the instance's own versions.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace MinecraftModManager
{
    static partial class Program
    {
        const int KeepRecentFolders = 8;
        static readonly List<string> recentFolders = new List<string>();

        sealed class InstanceInfo
        {
            public string Id, Name, Mods, Minecraft, Loader, LoaderVersion;
            public long LastPlayed;
            public int ModCount, MemoryMb;
        }

        static string LoaderName(string loaderName)
        {
            switch (loaderName) { case "neoforge": return "NeoForge"; case "forge": return "Forge"; case "fabric": return "Fabric"; case "quilt": return "Quilt"; default: return loaderName; }
        }

        static string prismDataOverride; // --prism <folder>, for testing against a copy of Prism's data folder

        static string PrismDataDir()
        {
            return prismDataOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PrismLauncher");
        }

        static string PrismInstancesDir()
        {
            string data = PrismDataDir();
            if (!Directory.Exists(data)) return null;
            string configured = null;
            try
            {
                string cfg = Path.Combine(data, "prismlauncher.cfg");
                if (File.Exists(cfg))
                    configured = File.ReadAllLines(cfg).Where(l => l.StartsWith("InstanceDir=")).Select(l => l.Substring("InstanceDir=".Length).Trim()).FirstOrDefault();
            }
            catch { }
            if (string.IsNullOrEmpty(configured)) configured = "instances";
            return Path.IsPathRooted(configured) ? configured : Path.Combine(data, configured);
        }

        // { minecraft version, loader (neoforge / forge / fabric / quilt), loader version } from mmc-pack.json.
        static string[] InstancePack(string instanceDir)
        {
            var result = new string[3];
            try
            {
                string pack = Path.Combine(instanceDir, "mmc-pack.json");
                if (!File.Exists(pack)) return result;
                var json = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(pack)) as Dictionary<string, object>;
                foreach (object c in A(Get(json, "components")))
                {
                    string uid = S(D(c), "uid"), version = S(D(c), "version");
                    switch (uid)
                    {
                        case "net.minecraft": result[0] = version; break;
                        case "net.neoforged": result[1] = "neoforge"; result[2] = version; break;
                        case "net.minecraftforge": result[1] = "forge"; result[2] = version; break;
                        case "net.fabricmc.fabric-loader": result[1] = "fabric"; result[2] = version; break;
                        case "org.quiltmc.quilt-loader": result[1] = "quilt"; result[2] = version; break;
                    }
                }
            }
            catch { }
            return result;
        }

        // Prism's instances, most recently played first.
        static List<InstanceInfo> PrismInstances()
        {
            var list = new List<InstanceInfo>();
            string root = PrismInstancesDir();
            if (root == null || !Directory.Exists(root)) return list;
            foreach (DirectoryInfo dir in new DirectoryInfo(root).GetDirectories())
            {
                string cfg = Path.Combine(dir.FullName, "instance.cfg");
                if (!File.Exists(cfg)) continue;
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (string line in File.ReadAllLines(cfg))
                    {
                        int eq = line.IndexOf('=');
                        if (eq > 0) values[line.Substring(0, eq).Trim()] = IniUnescape(line.Substring(eq + 1));
                    }
                }
                catch { continue; }
                string game = !Directory.Exists(Path.Combine(dir.FullName, "minecraft")) && Directory.Exists(Path.Combine(dir.FullName, ".minecraft")) ? ".minecraft" : "minecraft";
                string mods = Path.Combine(dir.FullName, game, "mods");
                string[] pack = InstancePack(dir.FullName);
                string name, launched;
                values.TryGetValue("name", out name);
                values.TryGetValue("lastLaunchTime", out launched);
                long lastPlayed;
                long.TryParse(launched, out lastPlayed);
                int count = 0;
                try { if (Directory.Exists(mods)) count = new DirectoryInfo(mods).GetFiles().Count(f => JarName.IsMatch(f.Name)); } catch { }
                list.Add(new InstanceInfo
                {
                    Id = dir.Name, Name = string.IsNullOrEmpty(name) ? dir.Name : name, Mods = mods,
                    Minecraft = pack[0], Loader = pack[1], LoaderVersion = pack[2], LastPlayed = lastPlayed, ModCount = count,
                    MemoryMb = InstanceMemoryMb(values.Select(v => v.Key + "=" + v.Value).ToList())
                });
            }
            return list.OrderByDescending(i => i.LastPlayed).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        static bool SamePath(string a, string b)
        {
            try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        // "...\SomePack\minecraft\mods" reads better as "minecraft\mods" than as "mods".
        static string FolderLabel(string path)
        {
            string trimmed = path.TrimEnd('\\', '/');
            string leaf = Path.GetFileName(trimmed);
            string parent = Path.GetFileName(Path.GetDirectoryName(trimmed) ?? "");
            return leaf.Equals("mods", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(parent) ? parent + "\\mods" : leaf;
        }

        static Dictionary<string, object> ListInstances()
        {
            string current = modsDir;
            List<InstanceInfo> prism = PrismInstances();
            SharingState sharing = LoadSharingState();
            var instances = prism.Select(i => (object)Obj("id", i.Id, "name", i.Name, "path", i.Mods, "minecraft", i.Minecraft, "loader", i.Loader,
                "loaderVersion", i.LoaderVersion, "mods", i.ModCount, "lastPlayed", i.LastPlayed, "memoryMb", i.MemoryMb, "current", SamePath(i.Mods, current),
                "sharing", InstanceSharingObj(i, sharing))).ToList();
            List<string> folders;
            lock (recentFolders) folders = recentFolders.ToList();
            if (!folders.Any(f => SamePath(f, current))) folders.Insert(0, current);
            var recent = folders.Where(f => !prism.Any(i => SamePath(i.Mods, f)))
                .Select(f => (object)Obj("name", FolderLabel(f), "path", f, "exists", Directory.Exists(f), "current", SamePath(f, current))).ToList();
            return Obj("ok", true, "current", current, "prismInstalled", FindPrism() != null, "prismSetUp", PrismInstancesDir() != null,
                "shareByDefault", sharing.ShareByDefault,
                "prismRunning", PrismRunning(), "instances", instances, "recent", recent);
        }

        // Prism instances are always listed, so only other folders go in the recent list.
        static void RememberFolder(string path)
        {
            if (PrismInstanceFor(path) != null) return;
            lock (recentFolders)
            {
                recentFolders.RemoveAll(f => SamePath(f, path));
                recentFolders.Insert(0, path);
                if (recentFolders.Count > KeepRecentFolders) recentFolders.RemoveRange(KeepRecentFolders, recentFolders.Count - KeepRecentFolders);
            }
        }

        // Where to start when the saved mods folder is gone (say its instance was deleted outside the app):
        // the most recently played instance, else a mods folder used before. Null when there's neither.
        static string FallbackFolder()
        {
            InstanceInfo played = PrismInstances().FirstOrDefault(i => Directory.Exists(i.Mods));
            if (played != null) return played.Mods;
            lock (recentFolders) return recentFolders.FirstOrDefault(Directory.Exists);
        }

        // For a Prism instance, Modrinth lookups follow the instance's own Minecraft version and loader.
        static void FollowInstanceTarget(string mods)
        {
            if (PrismInstanceFor(mods) == null) return;
            string[] pack = InstancePack(new DirectoryInfo(mods).Parent.Parent.FullName);
            if (!string.IsNullOrEmpty(pack[0]) && Regex.IsMatch(pack[0], @"^\d+\.\d+(\.\d+)?$")) gameVersion = pack[0];
            if (pack[1] != null && Array.IndexOf(Loaders, pack[1]) >= 0) loader = pack[1];
        }

        static Dictionary<string, object> SelectInstance(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new UserError("No instance was chosen.");
            path = Path.GetFullPath(path);
            InstanceInfo prism = PrismInstances().FirstOrDefault(i => SamePath(i.Mods, path));
            bool known;
            lock (recentFolders) known = prism != null || recentFolders.Any(f => SamePath(f, path)) || SamePath(path, modsDir);
            if (!known) throw new UserError("That folder isn't in the list. Use \"Other mods folder…\" to pick it.");
            if (prism != null)
            {
                if (prism.Loader == null) throw new UserError(prism.Name + " has no mod loader, so there are no mods to manage.");
                Directory.CreateDirectory(path); // a new instance may not have a mods folder until its first launch
            }
            if (!Directory.Exists(path)) throw new UserError(path + " isn't there any more.");
            bool changed = !SamePath(path, modsDir);
            RememberFolder(modsDir); // the folder being left stays one click away
            modsDir = path;
            FollowInstanceTarget(path);
            RememberFolder(path);
            SaveSettings();
            if (changed) Log("Now managing " + path);
            return Obj("ok", true, "changed", changed, "modsDir", path, "target", TargetObj());
        }

        // ---------- Creating, changing and deleting instances ----------
        // Instances are written the way Prism writes them (instance.cfg + mmc-pack.json + a minecraft
        // folder). Prism downloads Minecraft and the loader the first time an instance is played.
        // Prism keeps its own copy of each instance's settings while it's open and would overwrite
        // changes made underneath it, so changing or deleting an instance needs Prism closed.

        const string PrismMeta = "https://meta.prismlauncher.org/v1/";
        static readonly Dictionary<string, KeyValuePair<DateTime, List<Dictionary<string, object>>>> MetaCache =
            new Dictionary<string, KeyValuePair<DateTime, List<Dictionary<string, object>>>>();

        static readonly Dictionary<string, string> LoaderUid = new Dictionary<string, string>
        {
            { "neoforge", "net.neoforged" }, { "forge", "net.minecraftforge" },
            { "fabric", "net.fabricmc.fabric-loader" }, { "quilt", "org.quiltmc.quilt-loader" }
        };
        static readonly Dictionary<string, string> LoaderComponentName = new Dictionary<string, string>
        {
            { "neoforge", "NeoForge" }, { "forge", "Forge" }, { "fabric", "Fabric Loader" }, { "quilt", "Quilt Loader" }
        };

        // A Prism meta index (every version of one component), newest first. Cached for 30 minutes.
        static List<Dictionary<string, object>> MetaIndex(string uid)
        {
            lock (MetaCache)
            {
                KeyValuePair<DateTime, List<Dictionary<string, object>>> cached;
                if (MetaCache.TryGetValue(uid, out cached) && DateTime.UtcNow - cached.Key < TimeSpan.FromMinutes(30)) return cached.Value;
            }
            Dictionary<string, object> index;
            try { index = D(HttpJson("GET", PrismMeta + uid + "/index.json", null)); }
            catch (UserError) { throw new UserError("Couldn't reach the Minecraft version list. Check your internet connection and try again."); }
            List<Dictionary<string, object>> versions = A(Get(index, "versions")).Select(D).Where(v => v != null)
                .OrderByDescending(v => S(v, "releaseTime") ?? "").ToList();
            lock (MetaCache) MetaCache[uid] = new KeyValuePair<DateTime, List<Dictionary<string, object>>>(DateTime.UtcNow, versions);
            return versions;
        }

        static string RequiredMinecraft(Dictionary<string, object> version)
        {
            foreach (object r in A(Get(version, "requires")))
                if (S(D(r), "uid") == "net.minecraft") return S(D(r), "equals");
            return null;
        }

        // Loader versions available for a Minecraft version, newest first. Fabric and Quilt work on any
        // Minecraft version that has intermediary mappings.
        static List<Dictionary<string, object>> LoaderVersions(string loaderName, string minecraft)
        {
            string uid;
            if (!LoaderUid.TryGetValue(loaderName ?? "", out uid)) throw new UserError("Pick NeoForge, Fabric, Forge or Quilt.");
            List<Dictionary<string, object>> all = MetaIndex(uid);
            if (loaderName == "fabric" || loaderName == "quilt")
                return MetaIndex("net.fabricmc.intermediary").Any(v => S(v, "version") == minecraft) ? all : new List<Dictionary<string, object>>();
            return all.Where(v => RequiredMinecraft(v) == minecraft).ToList();
        }

        // The version a new instance gets by default: Prism's recommended one, else the newest non-beta.
        static string DefaultLoaderVersion(List<Dictionary<string, object>> versions)
        {
            Dictionary<string, object> pick = versions.FirstOrDefault(v => Get(v, "recommended") is bool && (bool)Get(v, "recommended"))
                ?? versions.FirstOrDefault(v => (S(v, "type") ?? "release") == "release" && !(S(v, "version") ?? "").Contains("beta"))
                ?? versions.FirstOrDefault();
            return pick == null ? null : S(pick, "version");
        }

        static Dictionary<string, object> MetaVersions(string minecraft, string loaderName)
        {
            List<object> releases = MetaIndex("net.minecraft").Where(v => S(v, "type") == "release").Select(v => (object)S(v, "version")).ToList();
            if (string.IsNullOrEmpty(minecraft)) minecraft = gameVersion;
            List<object> loaderVersions = new List<object>();
            string suggested = null;
            if (!string.IsNullOrEmpty(loaderName))
            {
                List<Dictionary<string, object>> versions = LoaderVersions(loaderName, minecraft);
                suggested = DefaultLoaderVersion(versions);
                loaderVersions = versions.Take(150).Select(v => (object)Obj("version", S(v, "version"), "type", S(v, "type") ?? "release",
                    "recommended", Get(v, "recommended") is bool && (bool)Get(v, "recommended"))).ToList();
            }
            return Obj("ok", true, "minecraft", minecraft, "releases", releases, "loader", loaderName, "loaderVersions", loaderVersions,
                "suggested", suggested, "ramMb", TotalRamMb(), "prismRunning", PrismRunning());
        }

        static bool PrismRunning()
        {
            try { return Process.GetProcessesByName("prismlauncher").Length > 0; } catch { return false; }
        }

        static void RequirePrismClosed()
        {
            if (PrismRunning()) throw new UserError("Close the launcher first. While it's open it keeps its own copy of each instance's settings and would undo this change.");
        }

        static InstanceInfo FindInstance(string id)
        {
            InstanceInfo found = PrismInstances().FirstOrDefault(i => i.Id == id);
            if (found == null) throw new UserError("That instance isn't there any more. Close the list and open it again.");
            return found;
        }

        static string InstanceDir(InstanceInfo info) { return Path.Combine(PrismInstancesDir(), info.Id); }

        static bool InstanceGameRunning(InstanceInfo info)
        {
            return GameRunning(Path.GetDirectoryName(info.Mods));
        }

        // Folder name for a new instance: the name without characters Windows refuses, made unique.
        static string NewInstanceFolder(string root, string name)
        {
            string clean = Regex.Replace(name, @"[<>:""/\\|?*\x00-\x1f]", "").Trim().TrimEnd('.');
            if (clean.Length == 0) clean = "Instance";
            if (clean.Length > 60) clean = clean.Substring(0, 60).Trim();
            string folder = clean;
            for (int n = 2; Directory.Exists(Path.Combine(root, folder)); n++) folder = clean + " (" + n + ")";
            return folder;
        }

        static string ComponentsJson(string minecraft, string loaderName, string loaderVersion)
        {
            Dictionary<string, object> mc = MetaIndex("net.minecraft").FirstOrDefault(v => S(v, "version") == minecraft);
            if (mc == null) throw new UserError("Minecraft " + minecraft + " isn't in the version list.");
            string lwjglUid = "org.lwjgl3", lwjglVersion = null;
            foreach (object r in A(Get(mc, "requires")))
            {
                string uid = S(D(r), "uid");
                if (uid != null && uid.StartsWith("org.lwjgl")) { lwjglUid = uid; lwjglVersion = S(D(r), "suggests") ?? S(D(r), "equals"); }
            }
            var components = new List<object>();
            if (lwjglVersion != null)
                components.Add(Obj("cachedName", lwjglUid == "org.lwjgl3" ? "LWJGL 3" : "LWJGL 2", "cachedVersion", lwjglVersion, "cachedVolatile", true,
                    "dependencyOnly", true, "uid", lwjglUid, "version", lwjglVersion));
            components.Add(Obj("cachedName", "Minecraft", "cachedRequires", lwjglVersion == null ? new object[0] : new object[] { Obj("suggests", lwjglVersion, "uid", lwjglUid) },
                "cachedVersion", minecraft, "important", true, "uid", "net.minecraft", "version", minecraft));
            if (loaderName == "fabric" || loaderName == "quilt")
                components.Add(Obj("cachedName", "Intermediary Mappings", "cachedRequires", new object[] { Obj("equals", minecraft, "uid", "net.minecraft") },
                    "cachedVersion", minecraft, "cachedVolatile", true, "dependencyOnly", true, "uid", "net.fabricmc.intermediary", "version", minecraft));
            string requiresUid = loaderName == "fabric" || loaderName == "quilt" ? "net.fabricmc.intermediary" : "net.minecraft";
            object requires = requiresUid == "net.minecraft" ? (object)Obj("equals", minecraft, "uid", requiresUid) : Obj("uid", requiresUid);
            components.Add(Obj("cachedName", LoaderComponentName[loaderName], "cachedRequires", new object[] { requires },
                "cachedVersion", loaderVersion, "uid", LoaderUid[loaderName], "version", loaderVersion));
            return new JavaScriptSerializer().Serialize(Obj("components", components, "formatVersion", 1));
        }

        // Settings that belong to the instance a copy is made from (Java, memory), not its identity or play time.
        static readonly string[] NotCopiedSettings = { "name", "uuid", "lastLaunchTime", "lastTimePlayed", "totalTimePlayed", "notes" };

        static Dictionary<string, object> CreateInstance(Dictionary<string, object> body)
        {
            string name = Regex.Replace(Str(body, "name") ?? "", @"\s+", " ").Trim();
            if (name.Length == 0 || name.Length > 80) throw new UserError("Give the instance a name (up to 80 characters).");
            string root = PrismInstancesDir();
            if (root == null) throw new UserError("There's no instances folder on this PC yet, so there's nowhere to put a new instance.");
            Directory.CreateDirectory(root);
            string copyFrom = Str(body, "copyFrom");
            InstanceInfo source = string.IsNullOrEmpty(copyFrom) ? null : FindInstance(copyFrom);
            string minecraft = source != null ? source.Minecraft : Str(body, "minecraft");
            string loaderName = source != null ? source.Loader : Str(body, "loader");
            string loaderVersion = Str(body, "loaderVersion");
            if (string.IsNullOrEmpty(minecraft) || string.IsNullOrEmpty(loaderName)) throw new UserError("Pick a Minecraft version and a loader.");
            List<Dictionary<string, object>> available = LoaderVersions(loaderName, minecraft);
            if (available.Count == 0) throw new UserError(LoaderName(loaderName) + " isn't available for Minecraft " + minecraft + ".");
            if (string.IsNullOrEmpty(loaderVersion)) loaderVersion = source != null ? source.LoaderVersion : DefaultLoaderVersion(available);
            if (!available.Any(v => S(v, "version") == loaderVersion)) throw new UserError(LoaderName(loaderName) + " " + loaderVersion + " isn't a version for Minecraft " + minecraft + ".");
            int memoryMb = IntValue(body, "memoryMb");
            if (memoryMb != 0) CheckMemory(memoryMb);
            if (source != null && InstanceGameRunning(source)) throw new UserError("Close " + source.Name + " (the game) before copying it.");
            Dictionary<string, object> sharing = D(Get(body, "sharing"));
            if (sharing != null) CleanJavaArgs(Str(sharing, "javaArgs"));

            string folder = NewInstanceFolder(root, name);
            string dir = Path.Combine(root, folder);
            // Built next to the instances folder (same drive), then moved in whole, so Prism never sees half an instance.
            string temp = Path.Combine(Directory.GetParent(root).FullName, ".mmm-creating-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(temp, "minecraft", "mods"));
                var cfg = new List<string> { "[General]", "ConfigVersion=1.3", "InstanceType=OneSix", "name=" + IniEscape(name), "iconKey=default" };
                if (source != null)
                {
                    foreach (string line in File.ReadAllLines(Path.Combine(InstanceDir(source), "instance.cfg")))
                    {
                        int eq = line.IndexOf('=');
                        if (eq <= 0 || line.StartsWith("[")) continue;
                        string key = line.Substring(0, eq).Trim();
                        if (NotCopiedSettings.Contains(key, StringComparer.OrdinalIgnoreCase) || cfg.Any(c => c.StartsWith(key + "="))) continue;
                        cfg.Add(line);
                    }
                    CopyGameFolder(Path.GetDirectoryName(source.Mods), Path.Combine(temp, "minecraft"), Bool(body, "copyWorlds"));
                }
                cfg.Add("notes=" + IniEscape("Created in Minecraft Mod Manager on " + DateTime.Now.ToString("yyyy-MM-dd") + (source != null ? " as a copy of " + source.Name : "") + "."));
                SetMemoryLines(cfg, memoryMb);
                if (sharing != null) SetInstanceJava(cfg, Bool(sharing, "java"), Str(sharing, "javaArgs") ?? IniGet(cfg, "JvmArgs"));
                File.WriteAllLines(Path.Combine(temp, "instance.cfg"), cfg, new System.Text.UTF8Encoding(false));
                File.WriteAllText(Path.Combine(temp, "mmc-pack.json"), ComponentsJson(minecraft, loaderName, loaderVersion), new System.Text.UTF8Encoding(false));
                Directory.Move(temp, dir);
            }
            finally
            {
                if (Directory.Exists(temp)) try { Directory.Delete(temp, true); } catch { }
            }
            Log("Instance  created " + name + " (Minecraft " + minecraft + ", " + LoaderName(loaderName) + " " + loaderVersion + ")" + (source != null ? " as a copy of " + source.Name : ""));
            var problems = new List<object>();
            MarkInstanceKnown(folder); // its sharing was chosen in the dialog, so the defaults don't apply again
            InstanceInfo created = PrismInstances().FirstOrDefault(i => i.Id == folder);
            if (created != null)
            {
                // Linked pack folders aren't copied (see CopyGameFolder): the copy shares them too, or gets
                // its own copy of the shared packs.
                foreach (string kind in SharedFolders)
                {
                    try
                    {
                        if (sharing != null && Bool(sharing, kind)) ShareFolder(created, kind);
                        else if (source != null && SharesFolder(source, kind)) CopyTree(Path.Combine(SharedDir(), kind), Path.Combine(GameDirOf(created), kind));
                    }
                    catch (Exception ex) { problems.Add("The " + PackLabel(kind) + " couldn't be shared: " + ex.Message); }
                }
                try { if (sharing != null && Bool(sharing, "options")) ShareGameSettings(created, true); }
                catch (Exception ex) { problems.Add("The game settings couldn't be shared: " + ex.Message); }
            }
            string mods = Path.Combine(dir, "minecraft", "mods");
            Dictionary<string, object> selected = SelectInstance(mods);
            selected["name"] = name;
            selected["prismRunning"] = PrismRunning();
            selected["problems"] = problems;
            return selected;
        }

        // Everything a copy needs to play the same: mods (with their tags), configs, resource and shader
        // packs, options and other mod data. Not logs, crash reports, screenshots or caches, and worlds only when asked.
        static readonly string[] SkipWhenCopying = { "logs", "crash-reports", "screenshots", "Distant_Horizons_server_data", ".mixin.out", "debug" };

        static void CopyGameFolder(string from, string to, bool worlds)
        {
            if (!Directory.Exists(from)) return;
            Directory.CreateDirectory(to);
            foreach (string file in Directory.GetFiles(from))
            {
                string name = Path.GetFileName(file);
                if (Regex.IsMatch(name, @"^(hs_err_pid|replay_pid).*\.log$|\.(download|copying|restoring|tmp)$", RegexOptions.IgnoreCase)) continue;
                File.Copy(file, Path.Combine(to, name), true);
            }
            foreach (string sub in Directory.GetDirectories(from))
            {
                string name = Path.GetFileName(sub);
                if (SkipWhenCopying.Contains(name, StringComparer.OrdinalIgnoreCase) || IsLink(sub)) continue;
                if (!worlds && name.Equals("saves", StringComparison.OrdinalIgnoreCase)) continue;
                CopyGameFolder(sub, Path.Combine(to, name), true);
            }
        }

        static int IntValue(Dictionary<string, object> body, string key)
        {
            object value;
            if (body == null || !body.TryGetValue(key, out value) || value == null) return 0;
            int n;
            return int.TryParse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), out n) ? n : 0;
        }

        static void CheckMemory(int mb)
        {
            long total = TotalRamMb();
            if (mb < 1024) throw new UserError("Give the game at least 1 GB of memory.");
            if (total > 0 && mb > total - 2048) throw new UserError("That's more memory than this PC can spare (it has " + (total / 1024) + " GB).");
        }

        // memoryMb 0 means "Prism's default": the override is switched off.
        static void SetMemoryLines(List<string> cfg, int memoryMb)
        {
            cfg.RemoveAll(l => l.StartsWith("OverrideMemory=") || l.StartsWith("MaxMemAlloc=") || l.StartsWith("MinMemAlloc="));
            if (memoryMb == 0) return;
            cfg.Add("OverrideMemory=true");
            cfg.Add("MinMemAlloc=" + Math.Min(1024, memoryMb));
            cfg.Add("MaxMemAlloc=" + memoryMb);
        }

        static Dictionary<string, object> InstanceSettings(Dictionary<string, object> body)
        {
            InstanceInfo info = FindInstance(Str(body, "id"));
            if (InstanceGameRunning(info)) throw new UserError("Close the game first.");
            string dir = InstanceDir(info);
            string cfgPath = Path.Combine(dir, "instance.cfg");
            List<string> cfg = File.ReadAllLines(cfgPath).ToList();
            var changes = new List<string>();

            string name = Regex.Replace(Str(body, "name") ?? "", @"\s+", " ").Trim();
            if (name.Length == 0 || name.Length > 80) throw new UserError("Give the instance a name (up to 80 characters).");
            if (name != info.Name)
            {
                cfg.RemoveAll(l => l.StartsWith("name="));
                int at = cfg.FindIndex(l => l.StartsWith("InstanceType="));
                cfg.Insert(at < 0 ? Math.Min(1, cfg.Count) : at + 1, "name=" + IniEscape(name));
                changes.Add("renamed to " + name);
            }
            if (body.ContainsKey("memoryMb"))
            {
                int memoryMb = IntValue(body, "memoryMb");
                if (memoryMb != 0) CheckMemory(memoryMb);
                int current = InstanceMemoryMb(cfg);
                if (memoryMb != current)
                {
                    SetMemoryLines(cfg, memoryMb);
                    changes.Add(memoryMb == 0 ? "memory: default" : "memory: " + (memoryMb / 1024.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " GB");
                }
            }
            Dictionary<string, object> sharing = D(Get(body, "sharing"));
            if (sharing != null && sharing.ContainsKey("java"))
            {
                string java = SetInstanceJava(cfg, Bool(sharing, "java"), Str(sharing, "javaArgs"));
                if (java != null) changes.Add(java);
            }

            string loaderVersion = Str(body, "loaderVersion");
            string minecraft = Str(body, "minecraft");
            string loaderName = Str(body, "loader");
            if (string.IsNullOrEmpty(minecraft)) minecraft = info.Minecraft;
            if (string.IsNullOrEmpty(loaderName)) loaderName = info.Loader;
            bool versionChanged = false;
            string packJson = null;
            string packPath = Path.Combine(dir, "mmc-pack.json");
            if (minecraft != info.Minecraft || loaderName != info.Loader)
            {
                // Another Minecraft version or loader: the instance's components are rewritten from scratch.
                if (!LoaderUid.ContainsKey(loaderName ?? "")) throw new UserError("Pick NeoForge, Fabric, Forge or Quilt.");
                if (!MetaIndex("net.minecraft").Any(v => S(v, "version") == minecraft && S(v, "type") == "release"))
                    throw new UserError("Minecraft " + minecraft + " isn't a release version.");
                List<Dictionary<string, object>> available = LoaderVersions(loaderName, minecraft);
                if (available.Count == 0) throw new UserError(LoaderName(loaderName) + " isn't available for Minecraft " + minecraft + ".");
                if (string.IsNullOrEmpty(loaderVersion) || !available.Any(v => S(v, "version") == loaderVersion)) loaderVersion = DefaultLoaderVersion(available);
                packJson = ComponentsJson(minecraft, loaderName, loaderVersion);
                if (minecraft != info.Minecraft) changes.Add("Minecraft " + info.Minecraft + " -> " + minecraft);
                changes.Add(loaderName != info.Loader
                    ? LoaderName(info.Loader) + " " + info.LoaderVersion + " -> " + LoaderName(loaderName) + " " + loaderVersion
                    : LoaderName(loaderName) + " " + loaderVersion);
                versionChanged = true;
            }
            else if (!string.IsNullOrEmpty(loaderVersion) && loaderVersion != info.LoaderVersion)
            {
                if (!LoaderVersions(info.Loader, info.Minecraft).Any(v => S(v, "version") == loaderVersion))
                    throw new UserError(LoaderName(info.Loader) + " " + loaderVersion + " isn't a version for Minecraft " + info.Minecraft + ".");
                var serializer = new JavaScriptSerializer();
                var pack = serializer.DeserializeObject(File.ReadAllText(packPath)) as Dictionary<string, object>;
                foreach (object c in A(Get(pack, "components")))
                {
                    Dictionary<string, object> component = D(c);
                    if (S(component, "uid") != LoaderUid[info.Loader]) continue;
                    component["version"] = loaderVersion;
                    component["cachedVersion"] = loaderVersion;
                }
                packJson = serializer.Serialize(pack);
                changes.Add(LoaderName(info.Loader) + " " + info.LoaderVersion + " -> " + loaderVersion);
            }

            // Everything above lives in the instance's own files, which the launcher would overwrite if it's open.
            // Sharing packs and game settings doesn't touch those, so it works with the launcher open.
            bool instanceFiles = changes.Count > 0;
            var folderChanges = SharedFolders.Where(kind => sharing != null && sharing.ContainsKey(kind) && Bool(sharing, kind) != SharesFolder(info, kind)).ToList();
            bool optionsChange = sharing != null && sharing.ContainsKey("options") && Bool(sharing, "options") != SharesGameSettings(info);
            if (!instanceFiles && folderChanges.Count == 0 && !optionsChange) throw new UserError("Nothing was changed.");
            if (instanceFiles) RequirePrismClosed();

            foreach (string kind in folderChanges) changes.Add(ApplyFolderSharing(info, kind, Bool(sharing, kind)));
            if (optionsChange) changes.Add(ShareGameSettings(info, Bool(sharing, "options")));
            if (packJson != null)
            {
                File.Copy(packPath, packPath + ".bak", true);
                File.WriteAllText(packPath, packJson, new System.Text.UTF8Encoding(false));
            }
            if (instanceFiles)
            {
                File.Copy(cfgPath, cfgPath + ".bak", true);
                File.WriteAllLines(cfgPath, cfg, new System.Text.UTF8Encoding(false));
            }
            Log("Instance  " + info.Name + ": " + string.Join(", ", changes));
            // When it's the instance being managed, Modrinth lookups follow its new version and loader.
            if (versionChanged && SamePath(info.Mods, modsDir))
            {
                FollowInstanceTarget(info.Mods);
                SaveSettings();
            }
            return Obj("ok", true, "changes", changes, "versionChanged", versionChanged, "target", TargetObj());
        }

        static int InstanceMemoryMb(List<string> cfg)
        {
            bool overridden = cfg.Any(l => l.Trim() == "OverrideMemory=true");
            string max = cfg.Where(l => l.StartsWith("MaxMemAlloc=")).Select(l => l.Substring("MaxMemAlloc=".Length)).FirstOrDefault();
            int mb;
            return overridden && int.TryParse(max, out mb) ? mb : 0;
        }

        static Dictionary<string, object> InstanceDeleteInfo(string id)
        {
            InstanceInfo info = FindInstance(id);
            string dir = InstanceDir(info);
            string saves = Path.Combine(Path.GetDirectoryName(info.Mods), "saves");
            var worlds = Directory.Exists(saves) ? new DirectoryInfo(saves).GetDirectories().Where(d => File.Exists(Path.Combine(d.FullName, "level.dat")))
                .Select(d => (object)d.Name).ToList() : new List<object>();
            long size = FolderSize(new DirectoryInfo(dir)); // shared packs aren't part of it
            return Obj("ok", true, "name", info.Name, "size", size, "worlds", worlds, "current", SamePath(info.Mods, modsDir),
                "prismRunning", PrismRunning(), "gameRunning", InstanceGameRunning(info));
        }

        static Dictionary<string, object> DeleteInstance(string id)
        {
            InstanceInfo info = FindInstance(id);
            if (SamePath(info.Mods, modsDir)) throw new UserError("This is the instance being managed right now. Switch to another one first.");
            RequirePrismClosed();
            if (InstanceGameRunning(info)) throw new UserError("Close the game first.");
            string dir = InstanceDir(info);
            List<string> links = RemoveLinks(info); // the shared packs stay where they are
            if (!RecyclePaths(new[] { dir }))
            {
                RestoreLinks(links);
                throw new UserError("Windows couldn't move the instance to the Recycle Bin (a file may be in use). Nothing was deleted.");
            }
            if (SharesGameSettings(info)) ShareGameSettings(info, false);
            Log("Instance  deleted " + info.Name + " (moved to the Recycle Bin)");
            return Obj("ok", true, "name", info.Name);
        }

        static void OpenInstanceFolder(string id)
        {
            InstanceInfo info = FindInstance(id);
            try { Process.Start("explorer.exe", "\"" + InstanceDir(info) + "\""); } catch { }
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        sealed class MEMORYSTATUSEX
        {
            public uint dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            public uint dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern bool GlobalMemoryStatusEx([System.Runtime.InteropServices.In, System.Runtime.InteropServices.Out] MEMORYSTATUSEX buffer);

        static long TotalRamMb()
        {
            try
            {
                var status = new MEMORYSTATUSEX();
                return GlobalMemoryStatusEx(status) ? (long)(status.ullTotalPhys / (1024 * 1024)) : 0;
            }
            catch { return 0; }
        }

        // Opens Prism's own window.
        static void OpenPrism()
        {
            string prism = FindPrism();
            if (prism == null) throw new UserError("The launcher isn't installed.");
            var start = new ProcessStartInfo(prism);
            start.UseShellExecute = false;
            start.WorkingDirectory = Path.GetDirectoryName(prism);
            Process.Start(start);
        }
    }
}
