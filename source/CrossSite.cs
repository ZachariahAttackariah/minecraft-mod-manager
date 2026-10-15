// Which CurseForge search results are also on Modrinth, so the merged search shows them once and installs
// from Modrinth. Checked per page of CurseForge results (each mod once per half hour; the answer is kept in
// memory), three Modrinth requests at most, the first two at the same time:
//   1. Same file: CurseForge lists each mod's latest files with their SHA-1, and Modrinth looks up a batch
//      of SHA-1s at once. A file both sites host is the same mod, whatever the names.
//   2. Same slug on Modrinth, and the same name or the same source repository (for authors who upload
//      different builds to each site).
// A match only counts when the Modrinth project has files for this Minecraft version and loader.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

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

        // The search's "installed" marks. Worked out when the Modrinth lock is free; while an update scan holds
        // it (for its whole run), the last marks for this folder are used, so a search never waits for a scan.
        static readonly object InstalledSnapLock = new object();
        static string installedSnapDir;
        static HashSet<string> installedSnapModrinth;
        static HashSet<long> installedSnapCurseForge;
        static void SearchInstalled(bool curseForge, out HashSet<string> onModrinth, out HashSet<long> onCurseForge)
        {
            string dir = modsDir;
            if (Monitor.TryEnter(ModrinthLock, 150))
            {
                try
                {
                    onModrinth = InstalledProjects(dir);
                    onCurseForge = curseForge ? new HashSet<long>(CfIdentify(new System.IO.DirectoryInfo(dir).GetFiles().Where(f => JarName.IsMatch(f.Name))).Values.Select(m => CfId(Get(m, "id")))) : null;
                }
                finally { Monitor.Exit(ModrinthLock); }
                lock (InstalledSnapLock)
                {
                    if (installedSnapDir != dir) installedSnapCurseForge = null;
                    installedSnapDir = dir;
                    installedSnapModrinth = onModrinth;
                    if (onCurseForge != null) installedSnapCurseForge = onCurseForge;
                }
                onCurseForge = onCurseForge ?? new HashSet<long>();
                return;
            }
            lock (InstalledSnapLock)
            {
                bool same = installedSnapDir == dir;
                onModrinth = same && installedSnapModrinth != null ? installedSnapModrinth : new HashSet<string>();
                onCurseForge = same && installedSnapCurseForge != null ? installedSnapCurseForge : new HashSet<long>();
            }
        }

        static string NameKey(string s) { return Regex.Replace((s ?? "").ToLowerInvariant().Replace("&", "and"), @"\((neo)?forge\)|\((fabric|quilt)\)|[^a-z0-9]", ""); }

        // CurseForge mod id (for this Minecraft version and loader) -> its twin, or null for none, and when.
        static readonly Dictionary<string, Tuple<DateTime, Dictionary<string, object>>> TwinCache = new Dictionary<string, Tuple<DateTime, Dictionary<string, object>>>();

        // CurseForge mod id -> the Modrinth project that is the same mod (with how it was matched).
        static Dictionary<long, Dictionary<string, object>> ModrinthTwins(List<Dictionary<string, object>> allMods)
        {
            var twins = new Dictionary<long, Dictionary<string, object>>();
            string target = gameVersion + "|" + loader + "|";
            var cfMods = new List<Dictionary<string, object>>();
            lock (TwinCache)
                foreach (Dictionary<string, object> mod in allMods)
                {
                    Tuple<DateTime, Dictionary<string, object>> known;
                    if (TwinCache.TryGetValue(target + CfId(Get(mod, "id")), out known) && DateTime.UtcNow - known.Item1 < TimeSpan.FromMinutes(30))
                    { if (known.Item2 != null) twins[CfId(Get(mod, "id"))] = known.Item2; }
                    else cfMods.Add(mod);
                }
            if (cfMods.Count == 0) return twins;
            var how = new Dictionary<long, string>();
            bool answered = false;
            try
            {
                // 1. Same file.
                var sha1ToMod = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                foreach (Dictionary<string, object> mod in cfMods)
                    foreach (Dictionary<string, object> file in A(Get(mod, "latestFiles")).Select(D).Where(f => f != null).Take(6))
                        foreach (Dictionary<string, object> hash in A(Get(file, "hashes")).Select(D).Where(x => x != null && CfId(Get(x, "algo")) == 1))
                            if (S(hash, "value") != null) sha1ToMod[S(hash, "value")] = CfId(Get(mod, "id"));
                var projectOf = new Dictionary<long, string>();
                // The same-slug lookup doesn't wait for the same-file one: both go at once.
                string[] slugs = cfMods.Select(m => S(m, "slug")).Where(s => s != null).ToArray();
                Task<object> bySlug = slugs.Length > 0 ? Task.Run(() => ModrinthGet("/projects?ids=" + JsonArrayParam(slugs))) : null;
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
                if (bySlug != null)
                    foreach (Dictionary<string, object> p in A(bySlug.Result).Select(D).Where(p => p != null))
                        projects[S(p, "slug")] = p;
                if (rest.Count > 0)
                {
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
                answered = true;
            }
            catch (Exception) { } // Modrinth unreachable: the results simply aren't merged (and nothing is remembered)
            if (answered)
                lock (TwinCache)
                {
                    if (TwinCache.Count > 5000) TwinCache.Clear();
                    foreach (Dictionary<string, object> mod in cfMods)
                    {
                        Dictionary<string, object> twin;
                        twins.TryGetValue(CfId(Get(mod, "id")), out twin);
                        TwinCache[target + CfId(Get(mod, "id"))] = Tuple.Create(DateTime.UtcNow, twin);
                    }
                }
            return twins;
        }
    }
}
