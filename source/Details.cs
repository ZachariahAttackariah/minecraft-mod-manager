// The mod details panel: what's in the jar, what it needs and what needs it, its config files
// (and whether the server's copy matches), Modrinth's description, and every Modrinth version for
// this Minecraft version + loader with its changelog. Also the changelogs shown before updating.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace MinecraftModManager
{
    static partial class Program
    {
        static readonly Dictionary<string, KeyValuePair<DateTime, List<Dictionary<string, object>>>> VersionListCache =
            new Dictionary<string, KeyValuePair<DateTime, List<Dictionary<string, object>>>>();

        static Dictionary<string, object> ModDetails(string name)
        {
            string dir = modsDir;
            var file = new FileInfo(ResolveModFile(dir, name));
            JarMeta meta = GetJarMeta(file);
            var ids = new HashSet<string>(meta.Mods.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);

            var all = new DirectoryInfo(dir).GetFiles().Where(f => JarName.IsMatch(f.Name)).ToList();
            var metas = all.ToDictionary(f => f.Name, f => GetJarMeta(f));
            Dictionary<string, List<Provider>> on = ProviderMap(all.Where(f => !f.Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)).ToList(), metas);
            Dictionary<string, List<Provider>> off = ProviderMap(all.Where(f => f.Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)).ToList(), metas);

            var deps = new List<object>();
            foreach (DepInfo dep in meta.Deps)
            {
                if (ids.Contains(dep.Id)) continue;
                string status, by = null, version = null;
                List<Provider> found;
                if (dep.Id == "minecraft" || dep.Id == "neoforge") status = "built-in";
                else if (on.TryGetValue(dep.Id, out found))
                {
                    Provider p = found[0];
                    status = InRange(p.Version, dep.Range) ? "ok" : "wrong-version";
                    by = p.Nested ? p.File + " (bundled)" : p.File;
                    version = p.Version;
                }
                else if (off.TryGetValue(dep.Id, out found)) { status = "off"; by = found[0].File; }
                else status = "missing";
                deps.Add(Obj("id", dep.Id, "type", dep.Type, "range", DescribeRange(dep.Range), "side", dep.Side, "status", status, "file", by, "version", version));
            }

            var dependents = new List<object>();
            foreach (FileInfo other in all)
            {
                if (other.Name == file.Name) continue;
                JarMeta m = metas[other.Name];
                foreach (DepInfo dep in m.Deps.Where(d => ids.Contains(d.Id) && (d.Type == "required" || d.Type == "optional")))
                {
                    ModInfo owner = m.Mods.FirstOrDefault();
                    dependents.Add(Obj("file", other.Name, "name", owner == null ? other.Name : owner.Name, "type", dep.Type,
                        "enabled", !other.Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)));
                    break;
                }
            }

            ModInfo main = meta.Mods.FirstOrDefault();
            return Obj("ok", true, "file", file.Name, "size", file.Length, "modified", file.LastWriteTimeUtc.ToString("o"),
                "enabled", !file.Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase),
                "kind", meta.Kind, "broken", meta.Broken,
                "mods", meta.Mods.Select(m => (object)Obj("id", m.Id, "version", m.Version, "name", m.Name, "description", m.Description)).ToList(),
                "nested", meta.Nested.Select(m => (object)Obj("id", m.Id, "version", m.Version, "name", m.Name)).ToList(),
                "deps", deps, "dependents", dependents,
                "configs", ConfigFilesFor(ids),
                "modrinth", ModrinthInfo(file));
        }

        // Config files named after one of the jar's mod ids, with how the server's copy compares.
        static List<object> ConfigFilesFor(HashSet<string> ids)
        {
            var list = new List<object>();
            string config = Path.Combine(GameDir(), "config");
            if (!Directory.Exists(config) || ids.Count == 0) return list;
            string serverConfig = serverDir == null ? null : Path.Combine(serverDir, "config");
            var keys = ids.Where(i => i.Length >= 3).Select(i => i.ToLowerInvariant()).ToList();
            foreach (FileSystemInfo entry in new DirectoryInfo(config).GetFileSystemInfos())
            {
                string lower = entry.Name.ToLowerInvariant();
                if (!keys.Any(k => lower == k || lower.StartsWith(k + "-") || lower.StartsWith(k + "_") || lower.StartsWith(k + "."))) continue;
                IEnumerable<FileInfo> files = entry is DirectoryInfo
                    ? ((DirectoryInfo)entry).EnumerateFiles("*", SearchOption.AllDirectories).Take(40)
                    : new[] { (FileInfo)entry };
                foreach (FileInfo f in files)
                {
                    string rel = f.FullName.Substring(config.Length).TrimStart('\\');
                    if (Regex.IsMatch(rel, @"\.(bak|old|tmp)$|\.bak-", RegexOptions.IgnoreCase)) continue;
                    string onServer = null;
                    if (serverConfig != null)
                    {
                        var s = new FileInfo(Path.Combine(serverConfig, rel));
                        onServer = !s.Exists ? "missing" : s.Length == f.Length && FilesEqual(f, s) ? "same" : "different";
                    }
                    list.Add(Obj("path", rel, "size", f.Length, "modified", f.LastWriteTimeUtc.ToString("o"), "server", onServer));
                    if (list.Count >= 60) return list;
                }
            }
            return list;
        }

        static Dictionary<string, object> ModrinthInfo(FileInfo file)
        {
            try
            {
                lock (ModrinthLock)
                {
                    string hash = FileSha1(file);
                    if (!VersionByHash.ContainsKey(hash))
                    {
                        Dictionary<string, object> found = D(ModrinthPost("/version_files", Obj("hashes", new[] { hash }, "algorithm", "sha1"))) ?? new Dictionary<string, object>();
                        VersionByHash[hash] = D(Get(found, hash));
                    }
                    Dictionary<string, object> version = VersionByHash[hash];
                    if (version == null) return Obj("found", false);
                    Dictionary<string, object> project = GetProject(S(version, "project_id"));
                    if (project == null) return Obj("found", false);
                    Dictionary<string, object> license = D(Get(project, "license"));
                    return Obj("found", true, "projectId", S(project, "id"), "slug", S(project, "slug"), "title", S(project, "title"),
                        "description", S(project, "description"), "body", S(project, "body"), "icon", S(project, "icon_url"),
                        "downloads", Get(project, "downloads"), "followers", Get(project, "followers"),
                        "clientSide", S(project, "client_side"), "serverSide", S(project, "server_side"),
                        "categories", A(Get(project, "categories")).ToList(),
                        "license", license == null ? null : S(license, "name") ?? S(license, "id"),
                        "source", S(project, "source_url"), "issues", S(project, "issues_url"), "wiki", S(project, "wiki_url"), "discord", S(project, "discord_url"),
                        "updated", S(project, "updated"),
                        "gallery", Gallery(project),
                        "versionNumber", S(version, "version_number"), "versionId", S(version, "id"));
                }
            }
            catch (UserError ex)
            {
                return Obj("found", false, "error", ex.Message);
            }
        }

        // The project's gallery images in the author's order, featured first: a smaller "url" for the grid
        // and the full-size "raw" for viewing one.
        static List<object> Gallery(Dictionary<string, object> project)
        {
            return A(Get(project, "gallery")).Select(x => D(x)).Where(g => g != null && S(g, "url") != null)
                .OrderByDescending(g => Get(g, "featured") is bool && (bool)Get(g, "featured"))
                .ThenBy(g => { double o; return double.TryParse(S(g, "ordering"), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out o) ? o : 0; })
                .Select(g => (object)Obj("url", S(g, "url"), "raw", S(g, "raw_url") ?? S(g, "url"), "title", S(g, "title"), "description", S(g, "description"),
                    "featured", Get(g, "featured") is bool && (bool)Get(g, "featured")))
                .ToList();
        }

        // Every Modrinth version of a project for the chosen Minecraft version + loader, newest first (cached 10 minutes).
        static List<Dictionary<string, object>> ProjectVersions(string projectId)
        {
            string key = projectId + "|" + gameVersion + "|" + loader;
            KeyValuePair<DateTime, List<Dictionary<string, object>>> cached;
            if (VersionListCache.TryGetValue(key, out cached) && DateTime.UtcNow - cached.Key < TimeSpan.FromMinutes(10)) return cached.Value;
            List<Dictionary<string, object>> versions = A(ModrinthGet("/project/" + Uri.EscapeDataString(projectId) + "/version?loaders=" +
                JsonArrayParam(loader) + "&game_versions=" + JsonArrayParam(gameVersion)))
                .Select(D).Where(v => v != null).OrderByDescending(v => S(v, "date_published")).ToList();
            VersionListCache[key] = new KeyValuePair<DateTime, List<Dictionary<string, object>>>(DateTime.UtcNow, versions);
            return versions;
        }

        static Dictionary<string, object> VersionObj(Dictionary<string, object> v, string currentId, string currentDate)
        {
            string date = S(v, "date_published") ?? "";
            return Obj("id", S(v, "id"), "number", S(v, "version_number"), "name", S(v, "name"), "type", S(v, "version_type"),
                "date", date, "changelog", S(v, "changelog"), "downloads", Get(v, "downloads"),
                "installed", S(v, "id") == currentId, "newer", currentDate != null && string.CompareOrdinal(date, currentDate) > 0);
        }

        static Dictionary<string, object> ModrinthVersions(string name)
        {
            lock (ModrinthLock)
            {
                var file = new FileInfo(ResolveModFile(modsDir, name));
                string hash = FileSha1(file);
                if (!VersionByHash.ContainsKey(hash))
                {
                    Dictionary<string, object> found = D(ModrinthPost("/version_files", Obj("hashes", new[] { hash }, "algorithm", "sha1"))) ?? new Dictionary<string, object>();
                    VersionByHash[hash] = D(Get(found, hash));
                }
                Dictionary<string, object> current = VersionByHash[hash];
                if (current == null) throw new UserError(name + " isn't on Modrinth, so there are no versions to show.");
                string currentDate = S(current, "date_published");
                List<object> versions = ProjectVersions(S(current, "project_id")).Select(v => (object)VersionObj(v, S(current, "id"), currentDate)).ToList();
                return Obj("ok", true, "target", TargetObj(), "current", Obj("id", S(current, "id"), "number", S(current, "version_number")), "versions", versions);
            }
        }

        // For each jar with an update: the versions between the installed one and the update, with changelogs.
        static Dictionary<string, object> ModrinthChangelogs(List<string> files)
        {
            var results = new List<object>();
            lock (ModrinthLock)
            {
                Dictionary<string, string> hashes = IdentifyFolder(modsDir);
                foreach (string name in files)
                {
                    try
                    {
                        string hash;
                        if (!hashes.TryGetValue(name, out hash) || VersionByHash[hash] == null) throw new UserError(name + " isn't on Modrinth.");
                        Dictionary<string, object> current = VersionByHash[hash];
                        Dictionary<string, object> latest = UpdateFor(hash);
                        if (latest == null) throw new UserError("Already up to date.");
                        string from = S(current, "date_published") ?? "", to = S(latest, "date_published") ?? "";
                        List<Dictionary<string, object>> between = ProjectVersions(S(current, "project_id"))
                            .Where(v => string.CompareOrdinal(S(v, "date_published") ?? "", from) > 0 && string.CompareOrdinal(S(v, "date_published") ?? "", to) <= 0).ToList();
                        if (!between.Any(v => S(v, "id") == S(latest, "id"))) between.Insert(0, latest);
                        Dictionary<string, object> project;
                        ProjectById.TryGetValue(S(current, "project_id") ?? "", out project);
                        results.Add(Obj("file", name, "title", S(project, "title") ?? name, "icon", S(project, "icon_url"), "slug", S(project, "slug"),
                            "from", S(current, "version_number"), "to", S(latest, "version_number"), "count", between.Count,
                            "versions", between.Take(15).Select(v => (object)VersionObj(v, null, null)).ToList()));
                    }
                    catch (UserError ex)
                    {
                        results.Add(Obj("file", name, "error", ex.Message));
                    }
                }
            }
            return Obj("ok", true, "mods", results);
        }

        // Swaps a jar for a chosen Modrinth version (newer or older).
        static Dictionary<string, object> ModrinthSwitch(string name, string versionId)
        {
            if (string.IsNullOrEmpty(versionId)) throw new UserError("No version was chosen.");
            lock (ModrinthLock)
            {
                string dir = modsDir;
                ResolveModFile(dir, name);
                Dictionary<string, string> hashes = IdentifyFolder(dir);
                string hash;
                if (!hashes.TryGetValue(name, out hash) || VersionByHash[hash] == null) throw new UserError(name + " isn't on Modrinth.");
                Dictionary<string, object> current = VersionByHash[hash];
                Dictionary<string, object> target = D(ModrinthGet("/version/" + Uri.EscapeDataString(versionId)));
                if (target == null || S(target, "project_id") != S(current, "project_id")) throw new UserError("That version isn't for this mod.");
                if (!Fits(target)) throw new UserError("That version isn't for Minecraft " + gameVersion + " with " + LoaderName() + ".");
                if (S(target, "id") == S(current, "id")) throw new UserError("That version is already installed.");
                var notes = new List<string>();
                string newFile = ReplaceWithVersion(dir, name, current, target, "Switched  ", notes);
                HashSet<string> installed = InstalledProjects(dir);
                var plan = new List<PlanItem>();
                Dictionary<string, object> project;
                ProjectById.TryGetValue(S(target, "project_id") ?? "", out project);
                AddDependencies(target, S(project, "title") ?? name, installed, new HashSet<string>(installed), plan, notes);
                List<object> added = InstallPlan(plan, dir, notes);
                return Obj("ok", true, "newFile", newFile, "version", S(target, "version_number"), "added", added, "notes", notes);
            }
        }

        // Opens a config file (in the game's or the server's config folder) in its usual program, or shows it in Explorer.
        static void OpenConfigFile(string side, string rel, bool reveal)
        {
            if (string.IsNullOrWhiteSpace(rel) || rel.Contains("..") || Path.IsPathRooted(rel)) throw new UserError("That isn't a config file path.");
            string root = side == "server" ? Path.Combine(RequireServerDir(), "config") : Path.Combine(GameDir(), "config");
            string path = Path.GetFullPath(Path.Combine(root, rel));
            if (!path.StartsWith(Path.GetFullPath(root) + "\\", StringComparison.OrdinalIgnoreCase)) throw new UserError("That isn't a config file path.");
            if (!File.Exists(path)) throw new UserError(rel + " isn't there any more.");
            if (reveal)
            {
                Process.Start("explorer.exe", "/select,\"" + path + "\"");
                return;
            }
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch { Process.Start("notepad.exe", "\"" + path + "\""); }
        }
    }
}
