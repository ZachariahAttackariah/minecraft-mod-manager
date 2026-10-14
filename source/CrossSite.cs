// Which CurseForge search results are also on Modrinth, so the merged search shows them once and installs
// from Modrinth. Checked per page of CurseForge results, three Modrinth requests at most:
//   1. Same file: CurseForge lists each mod's latest files with their SHA-1, and Modrinth looks up a batch
//      of SHA-1s at once. A file both sites host is the same mod, whatever the names.
//   2. Same slug on Modrinth, and the same name or the same source repository (for authors who upload
//      different builds to each site).
// A match only counts when the Modrinth project has files for this Minecraft version and loader.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MinecraftModManager
{
    static partial class Program
    {
        static string RepoKey(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            Match m = Regex.Match(url.Trim().ToLowerInvariant(), @"^https?://(?:www\.)?(github\.com|gitlab\.com|codeberg\.org)/([^/]+)/([^/#?]+)");
            return m.Success ? m.Groups[1].Value + "/" + m.Groups[2].Value + "/" + Regex.Replace(m.Groups[3].Value, @"\.git$", "") : null;
        }

        static string NameKey(string s) { return Regex.Replace((s ?? "").ToLowerInvariant().Replace("&", "and"), @"\((neo)?forge\)|\((fabric|quilt)\)|[^a-z0-9]", ""); }

        // CurseForge mod id -> the Modrinth project that is the same mod (with how it was matched).
        static Dictionary<long, Dictionary<string, object>> ModrinthTwins(List<Dictionary<string, object>> cfMods)
        {
            var twins = new Dictionary<long, Dictionary<string, object>>();
            var how = new Dictionary<long, string>();
            if (cfMods.Count == 0) return new Dictionary<long, Dictionary<string, object>>();
            try
            {
                // 1. Same file.
                var sha1ToMod = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                foreach (Dictionary<string, object> mod in cfMods)
                    foreach (Dictionary<string, object> file in A(Get(mod, "latestFiles")).Select(D).Where(f => f != null).Take(6))
                        foreach (Dictionary<string, object> hash in A(Get(file, "hashes")).Select(D).Where(x => x != null && CfId(Get(x, "algo")) == 1))
                            if (S(hash, "value") != null) sha1ToMod[S(hash, "value")] = CfId(Get(mod, "id"));
                var projectOf = new Dictionary<long, string>();
                if (sha1ToMod.Count > 0)
                {
                    Dictionary<string, object> found = D(ModrinthPost("/version_files", Obj("hashes", sha1ToMod.Keys.ToList(), "algorithm", "sha1"))) ?? new Dictionary<string, object>();
                    foreach (KeyValuePair<string, object> v in found)
                    {
                        long id;
                        string project = S(D(v.Value), "project_id");
                        if (project != null && sha1ToMod.TryGetValue(v.Key, out id) && !projectOf.ContainsKey(id)) { projectOf[id] = project; how[id] = "file"; }
                    }
                }
                // 2. Same slug, plus the same name or source repository.
                List<Dictionary<string, object>> rest = cfMods.Where(m => !projectOf.ContainsKey(CfId(Get(m, "id"))) && S(m, "slug") != null).ToList();
                var projects = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
                if (rest.Count > 0)
                {
                    foreach (Dictionary<string, object> p in A(ModrinthGet("/projects?ids=" + JsonArrayParam(rest.Select(m => S(m, "slug")).ToArray()))).Select(D).Where(p => p != null))
                        projects[S(p, "slug")] = p;
                    foreach (Dictionary<string, object> m in rest)
                    {
                        Dictionary<string, object> p;
                        if (!projects.TryGetValue(S(m, "slug"), out p)) continue;
                        string repoA = RepoKey(S(D(Get(m, "links")), "sourceUrl")), repoB = RepoKey(S(p, "source_url"));
                        bool sameRepo = repoA != null && repoA == repoB;
                        bool sameName = NameKey(S(m, "name")).Length > 0 && NameKey(S(m, "name")) == NameKey(S(p, "title"));
                        if (sameRepo || sameName) { projectOf[CfId(Get(m, "id"))] = S(p, "id"); how[CfId(Get(m, "id"))] = sameRepo ? "source repository" : "slug and name"; }
                    }
                }
                // The matched projects themselves, to show and to check they fit this game.
                List<string> ids = projectOf.Values.Distinct().Where(id => !projects.Values.Any(p => S(p, "id") == id)).ToList();
                if (ids.Count > 0)
                    foreach (Dictionary<string, object> p in A(ModrinthGet("/projects?ids=" + JsonArrayParam(ids.ToArray()))).Select(D).Where(p => p != null))
                        projects[S(p, "slug") ?? S(p, "id")] = p;
                foreach (KeyValuePair<long, string> pair in projectOf)
                {
                    Dictionary<string, object> p = projects.Values.FirstOrDefault(x => S(x, "id") == pair.Value);
                    if (p == null) continue;
                    bool fits = A(Get(p, "loaders")).Contains(loader) && A(Get(p, "game_versions")).Contains(gameVersion);
                    if (!fits || S(p, "project_type") != "mod") continue;
                    lock (ModrinthLock) ProjectById[S(p, "id")] = p;
                    twins[pair.Key] = Obj("projectId", S(p, "id"), "slug", S(p, "slug"), "title", S(p, "title"), "description", S(p, "description"),
                        "icon", S(p, "icon_url"), "downloads", Get(p, "downloads"), "clientSide", S(p, "client_side"), "serverSide", S(p, "server_side"),
                        "updated", S(p, "updated"), "matchedBy", how[pair.Key]);
                }
            }
            catch (Exception) { } // Modrinth unreachable: the results simply aren't merged
            return twins;
        }
    }
}
