// Installing the mods the pre-launch check finds missing. A dependency is only a mod id (say
// "cloth_config"), so the project behind it is looked for in this order:
//   1. The requiring mod's own listing: its Modrinth version's required projects, or its CurseForge
//      file's required mods. The one whose slug or name is that id (or the only one not installed yet).
//   2. A Modrinth project with that id as its slug ("_" read as "-" too).
//   3. A CurseForge mod with that slug.
// Every download is checked to really contain that mod id; one that doesn't goes to the Recycle Bin.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace MinecraftModManager
{
    static partial class Program
    {
        // A mod id, slug or name reduced to letters and digits, without a trailing loader word ("Map Atlases (Forge)",
        // "map-atlases-forge" and "map_atlases" all read "mapatlases").
        static string IdKey(string s)
        {
            string k = Regex.Replace((s ?? "").ToLowerInvariant(), "[^a-z0-9]", "");
            string trimmed = Regex.Replace(k, "(neoforge|forge|fabric|quilt)$", "");
            return trimmed.Length >= 3 ? trimmed : k;
        }

        static bool ProjectFits(Dictionary<string, object> p)
        {
            return p != null && S(p, "project_type") == "mod" && A(Get(p, "loaders")).Contains(loader) && A(Get(p, "game_versions")).Contains(gameVersion);
        }

        static Dictionary<string, object> InstallMissing(Dictionary<string, object> body)
        {
            string dir = modsDir;
            // (A request body's lists come in as ArrayList, which A() doesn't read.)
            var raw = Get(body, "items") as System.Collections.IEnumerable;
            List<Dictionary<string, object>> items = raw == null || raw is string ? new List<Dictionary<string, object>>()
                : raw.Cast<object>().Select(D).Where(x => x != null && Str(x, "id") != null).ToList();
            if (items.Count == 0) throw new UserError("Nothing is missing.");
            if (items.Count >= 2) AutoSnapshot(dir, "Before adding " + items.Count + " missing mods");
            var results = new List<object>();
            var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Dictionary<string, object> item in items)
            {
                string id = Str(item, "id"), forFile = Str(item, "for");
                if (!done.Add(id)) continue;
                if (JobStopped()) { results.Add(Obj("id", id, "error", "Stopped before this one.")); continue; }
                try { results.Add(InstallOneMissing(dir, id, forFile)); }
                catch (UserError ex) { results.Add(Obj("id", id, "error", ex.Message)); }
            }
            return Obj("ok", true, "results", results);
        }

        static Dictionary<string, object> InstallOneMissing(string dir, string id, string forFile)
        {
            // Already provided by a jar here (turned on meanwhile, or installed with another one)?
            if (new DirectoryInfo(dir).GetFiles("*.jar").Any(f => GetJarMeta(f).Mods.Concat(GetJarMeta(f).Nested).Any(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase))))
                return Obj("id", id, "already", true);

            string modrinthProject = null;
            long curseforgeMod = 0;
            string how = null;
            lock (ModrinthLock)
            {
                // 1. What the requiring mod's own listing says it needs.
                if (forFile != null)
                {
                    try
                    {
                        Dictionary<string, string> hashes = IdentifyFolder(dir);
                        string hash;
                        Dictionary<string, object> version = hashes.TryGetValue(forFile, out hash) ? VersionByHash[hash] : null;
                        if (version != null && !Bool(version, "loose"))
                        {
                            HashSet<string> installed = InstalledProjects(dir);
                            List<Dictionary<string, object>> needed = A(Get(version, "dependencies")).Select(D)
                                .Where(x => x != null && S(x, "dependency_type") == "required" && S(x, "project_id") != null && !installed.Contains(S(x, "project_id")))
                                .Select(x => GetProject(S(x, "project_id"))).Where(ProjectFits).ToList();
                            Dictionary<string, object> named = needed.FirstOrDefault(p => IdKey(S(p, "slug")) == IdKey(id) || IdKey(S(p, "title")) == IdKey(id));
                            if (named == null && needed.Count == 1) named = needed[0];
                            if (named != null) { modrinthProject = S(named, "id"); how = "listed as required by " + forFile + " on Modrinth"; }
                        }
                    }
                    catch (UserError) { }
                }
                // 2. A Modrinth project whose slug is the mod id.
                if (modrinthProject == null)
                {
                    foreach (string slug in new[] { id, id.Replace('_', '-') }.Distinct())
                    {
                        Dictionary<string, object> p;
                        try { p = GetProject(slug); } catch (UserError) { p = null; }
                        if (ProjectFits(p)) { modrinthProject = S(p, "id"); how = "Modrinth project \"" + S(p, "slug") + "\""; break; }
                    }
                }
                // 2b. Modrinth's search for the id as words, taking a result whose name or slug is that id.
                if (modrinthProject == null)
                {
                    try
                    {
                        Dictionary<string, object> found = ModrinthSearch(id.Replace('_', ' ').Replace('-', ' '), 0, "relevance");
                        Dictionary<string, object> hit = A(Get(found, "hits")).Concat((Get(found, "hits") as System.Collections.IEnumerable ?? new object[0]).Cast<object>())
                            .Select(D).FirstOrDefault(x => x != null && (IdKey(S(x, "title")) == IdKey(id) || IdKey(S(x, "slug")) == IdKey(id)));
                        if (hit != null) { modrinthProject = S(hit, "projectId"); how = "Modrinth search (\"" + S(hit, "title") + "\")"; }
                    }
                    catch (UserError) { }
                }
            }
            // 3. CurseForge: the requiring file's required mods, then the slug.
            if (modrinthProject == null && CfReady)
            {
                try
                {
                    Dictionary<string, object> match = forFile == null ? null : CfMatchFor(dir, forFile);
                    List<long> required = match == null ? new List<long>() : A(Get(D(Get(match, "file")), "dependencies")).Select(D)
                        .Where(x => x != null && CfId(Get(x, "relationType")) == 3).Select(x => CfId(Get(x, "modId"))).ToList();
                    if (required.Count > 0) CfFetchMods(required);
                    Dictionary<string, object> mod = required.Select(CfMod).FirstOrDefault(m => m != null && (IdKey(S(m, "slug")) == IdKey(id) || IdKey(S(m, "name")) == IdKey(id)));
                    if (mod != null) how = "listed as required by " + forFile + " on CurseForge";
                    if (mod == null)
                    {
                        mod = CfModBySlugFind(id) ?? CfModBySlugFind(id.Replace('_', '-'));
                        if (mod != null) how = "CurseForge project \"" + S(mod, "slug") + "\"";
                    }
                    if (mod == null)
                    {
                        // CurseForge's search for the id as words, taking a mod whose name or slug is that id.
                        mod = CfList(CfHttp("GET", "/mods/search?gameId=" + CfMinecraft + "&classId=" + CfModsClass + "&gameVersion=" + Uri.EscapeDataString(gameVersion) +
                                "&modLoaderType=" + CfLoader() + "&pageSize=20&searchFilter=" + Uri.EscapeDataString(id.Replace('_', ' ')), null))
                            .Select(D).FirstOrDefault(m => m != null && (IdKey(S(m, "name")) == IdKey(id) || IdKey(S(m, "slug")) == IdKey(id)));
                        if (mod != null) { lock (CfModById) CfModById[CfId(Get(mod, "id"))] = mod; how = "CurseForge search (\"" + S(mod, "name") + "\")"; }
                    }
                    if (mod != null && CfLatestIndex(mod) != null) curseforgeMod = CfId(Get(mod, "id"));
                }
                catch (UserError) { }
            }
            if (modrinthProject == null && curseforgeMod == 0)
                throw new UserError("Couldn't find " + id + " on Modrinth" + (CfReady ? " or CurseForge" : "") + " for Minecraft " + gameVersion + " with " + LoaderName() + ". Search for it by name.");

            Dictionary<string, object> res;
            try { res = modrinthProject != null ? ModrinthInstall(modrinthProject) : CurseForgeInstall("cf:" + curseforgeMod); }
            catch (UserError ex)
            {
                if (ex.Message.Contains("already in this mods folder")) return Obj("id", id, "already", true);
                throw;
            }
            if (Bool(res, "manual")) return Obj("id", id, "manual", true, "url", Str(res, "url"), "title", Str(res, "title"));
            var addedRaw = Get(res, "added") as System.Collections.IEnumerable; // a List here, which A() doesn't read
            List<Dictionary<string, object>> added = addedRaw == null ? new List<Dictionary<string, object>>() : addedRaw.Cast<object>().Select(D).Where(x => x != null).ToList();
            if (added.Count == 0) throw new UserError("Nothing was installed for " + id + ".");
            // The main download has to be the mod that was missing; otherwise it was the wrong project.
            Dictionary<string, object> main = added.FirstOrDefault(a => !Bool(a, "dependency")) ?? added[0];
            var jar = new FileInfo(Path.Combine(dir, Str(main, "file")));
            bool provides = jar.Exists && GetJarMeta(jar).Mods.Concat(GetJarMeta(jar).Nested).Any(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
            if (!provides)
            {
                RecyclePaths(added.Select(a => Path.Combine(dir, Str(a, "file"))).Where(File.Exists).ToList());
                throw new UserError("Found " + (Str(main, "title") ?? Str(main, "file")) + " (" + how + "), but it isn't " + id + ", so it was put in the Recycle Bin. Search for it by name.");
            }
            Log("Added missing " + id + " -> " + Str(main, "file") + " (" + how + ")");
            return Obj("id", id, "file", Str(main, "file"), "title", Str(main, "title"), "how", how,
                "also", added.Where(a => a != main).Select(a => (object)Str(a, "title")).ToList(), "notes", Get(res, "notes"));
        }
    }
}
