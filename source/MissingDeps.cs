// Installing the mods the pre-launch check finds missing. A dependency is only a mod id (say "fusion" or
// "create_sa"), and the project behind it is often named something else ("Fusion (Connected Textures)",
// "Create Stuff & Additions"). So candidates are gathered widely, then each one's file is downloaded to a
// temporary folder and opened, and the first that really contains that mod id is installed:
//   1. Everything the requiring mod lists as required: its Modrinth version's dependencies, or its
//      CurseForge file's.
//   2. A Modrinth project with that id as its slug, then Modrinth's search for it.
//   3. A CurseForge mod with that slug, then CurseForge's search for it.
// Candidates whose names look like the id are tried first. A CurseForge file whose author only allows
// downloads from the website can't be opened, so it's only offered when its name is the id itself.

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

        sealed class DepCandidate
        {
            public string Site, ModrinthId, Title, How;
            public long CurseForgeId;
            public int Order, Likeness;
            public string Key { get { return Site == "modrinth" ? "mr:" + ModrinthId : "cf:" + CurseForgeId; } }
        }

        // How much a name looks like the id: 3 the same, 2 one contains the other, 1 they share a word, 0 not.
        static int Likeness(string name, string id)
        {
            string a = IdKey(name), b = IdKey(id);
            if (a.Length == 0 || b.Length == 0) return 0;
            if (a == b) return 3;
            if (a.Contains(b) || b.Contains(a)) return 2;
            var words = new HashSet<string>(Regex.Split((name ?? "").ToLowerInvariant(), "[^a-z0-9]+").Where(w => w.Length >= 4));
            return Regex.Split(id.ToLowerInvariant(), "[^a-z0-9]+").Any(w => w.Length >= 4 && words.Contains(w)) ? 1 : 0;
        }

        // Mod ids found inside each candidate's file, kept for this run (several missing ids can share one).
        static readonly Dictionary<string, HashSet<string>> CandidateIds = new Dictionary<string, HashSet<string>>();

        static Dictionary<string, object> InstallMissing(Dictionary<string, object> body)
        {
            string dir = modsDir;
            bool dryRun = Bool(body, "dryRun");
            // (A request body's lists come in as ArrayList, which A() doesn't read.)
            var raw = Get(body, "items") as System.Collections.IEnumerable;
            List<Dictionary<string, object>> items = raw == null || raw is string ? new List<Dictionary<string, object>>()
                : raw.Cast<object>().Select(D).Where(x => x != null && Str(x, "id") != null).ToList();
            if (items.Count == 0) throw new UserError("Nothing is missing.");
            if (items.Count >= 2 && !dryRun) AutoSnapshot(dir, "Before adding " + items.Count + " missing mods");
            var results = new List<object>();
            var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Dictionary<string, object> item in items)
            {
                string id = Str(item, "id"), forFile = Str(item, "for");
                if (!done.Add(id)) continue;
                if (JobStopped()) { results.Add(Obj("id", id, "error", "Stopped before this one.")); continue; }
                try { results.Add(InstallOneMissing(dir, id, forFile, dryRun)); }
                catch (UserError ex) { results.Add(Obj("id", id, "error", ex.Message)); }
            }
            return Obj("ok", true, "results", results);
        }

        static List<DepCandidate> DepCandidates(string dir, string id, string forFile)
        {
            var list = new List<DepCandidate>();
            Action<DepCandidate> add = c => { if (!list.Any(x => x.Key == c.Key)) { c.Order = list.Count; c.Likeness = Likeness(c.Title, id); list.Add(c); } };
            string words = Regex.Replace(id, "[_\\-.]+", " ").Trim();
            lock (ModrinthLock)
            {
                HashSet<string> installed = InstalledProjects(dir);
                // 1. The requiring mod's Modrinth listing.
                if (forFile != null)
                {
                    try
                    {
                        string hash;
                        Dictionary<string, object> version = IdentifyFolder(dir).TryGetValue(forFile, out hash) ? VersionByHash[hash] : null;
                        if (version != null && !Bool(version, "loose"))
                            foreach (Dictionary<string, object> dep in A(Get(version, "dependencies")).Select(D)
                                .Where(x => x != null && S(x, "dependency_type") == "required" && S(x, "project_id") != null && !installed.Contains(S(x, "project_id"))))
                            {
                                Dictionary<string, object> p = GetProject(S(dep, "project_id"));
                                if (ProjectFits(p)) add(new DepCandidate { Site = "modrinth", ModrinthId = S(p, "id"), Title = S(p, "title"), How = "listed as required by " + forFile + " on Modrinth" });
                            }
                    }
                    catch (UserError) { }
                }
                // 2. Modrinth: the id as a slug, also split into two words where it has none ("farmersdelight"
                //    -> "farmers-delight"), all looked up in one request; then a search.
                try
                {
                    string lower = id.ToLowerInvariant();
                    var slugs = new List<string> { lower, lower.Replace('_', '-'), lower.Replace("_", "") };
                    string bare = Regex.Replace(lower, "[^a-z0-9]", "");
                    if (bare == lower && bare.Length >= 7)
                        for (int i = 3; i <= bare.Length - 3; i++) slugs.Add(bare.Substring(0, i) + "-" + bare.Substring(i));
                    foreach (Dictionary<string, object> p in A(ModrinthGet("/projects?ids=" + JsonArrayParam(slugs.Distinct().ToArray()))).Select(D).Where(ProjectFits))
                    {
                        ProjectById[S(p, "id")] = p;
                        add(new DepCandidate { Site = "modrinth", ModrinthId = S(p, "id"), Title = S(p, "title"), How = "Modrinth project \"" + S(p, "slug") + "\"" });
                    }
                }
                catch (UserError) { }
            }
            try
            {
                var hits = Get(ModrinthSearch(words, 0, "relevance"), "hits") as System.Collections.IEnumerable;
                if (hits != null)
                    foreach (Dictionary<string, object> h in hits.Cast<object>().Select(D).Where(x => x != null).Take(6))
                        add(new DepCandidate { Site = "modrinth", ModrinthId = S(h, "projectId"), Title = S(h, "title"), How = "Modrinth search (\"" + S(h, "title") + "\")" });
            }
            catch (UserError) { }
            // 3. CurseForge: the requiring file's listing, the slug, then a search.
            if (CfReady)
            {
                try
                {
                    Dictionary<string, object> match = forFile == null ? null : CfMatchFor(dir, forFile);
                    List<long> required = match == null ? new List<long>() : A(Get(D(Get(match, "file")), "dependencies")).Select(D)
                        .Where(x => x != null && CfId(Get(x, "relationType")) == 3).Select(x => CfId(Get(x, "modId"))).ToList();
                    if (required.Count > 0) CfFetchMods(required);
                    foreach (Dictionary<string, object> m in required.Select(CfMod).Where(m => m != null && CfLatestIndex(m) != null))
                        add(new DepCandidate { Site = "curseforge", CurseForgeId = CfId(Get(m, "id")), Title = S(m, "name"), How = "listed as required by " + forFile + " on CurseForge" });
                    foreach (string slug in new[] { id, id.Replace('_', '-') }.Distinct())
                    {
                        Dictionary<string, object> m = CfModBySlugFind(slug);
                        if (m != null && CfLatestIndex(m) != null) add(new DepCandidate { Site = "curseforge", CurseForgeId = CfId(Get(m, "id")), Title = S(m, "name"), How = "CurseForge project \"" + S(m, "slug") + "\"" });
                    }
                    // The id as words, and for one-word ids also its start ("gaboulibs" -> "gabou", which finds
                    // "Gabou's Libs"): CurseForge's search matches word beginnings, not whole run-together ids.
                    var queries = new List<string> { words };
                    if (!words.Contains(" ") && words.Length >= 8) queries.Add(words.Substring(0, Math.Max(5, words.Length / 2)));
                    foreach (string q in queries)
                        foreach (Dictionary<string, object> m in CfList(CfHttp("GET", "/mods/search?gameId=" + CfMinecraft + "&classId=" + CfModsClass + "&gameVersion=" + Uri.EscapeDataString(gameVersion) +
                            "&modLoaderType=" + CfLoader() + "&pageSize=6&sortField=2&sortOrder=desc&searchFilter=" + Uri.EscapeDataString(q), null)).Select(D).Where(m => m != null))
                        {
                            lock (CfModById) CfModById[CfId(Get(m, "id"))] = m;
                            if (CfLatestIndex(m) != null) add(new DepCandidate { Site = "curseforge", CurseForgeId = CfId(Get(m, "id")), Title = S(m, "name"), How = "CurseForge search (\"" + S(m, "name") + "\")" });
                        }
                }
                catch (UserError) { }
            }
            // Names that look like the id first; otherwise the order above (the requiring mod's listing first).
            return list.OrderByDescending(c => c.Likeness).ThenBy(c => c.Order).ToList();
        }

        // Downloads a candidate's file to a temporary folder and reads which mod ids it contains. Null when
        // it can't be opened (no file for this game, too big, or a CurseForge author who only allows website downloads).
        static HashSet<string> CandidateModIds(DepCandidate c)
        {
            lock (CandidateIds)
            {
                HashSet<string> known;
                if (CandidateIds.TryGetValue(c.Key, out known)) return known;
            }
            HashSet<string> ids = null;
            string temp = Path.Combine(dataDir, "downloads", "check-" + Guid.NewGuid().ToString("N") + ".jar");
            try
            {
                Uri url = null;
                string sha1 = null;
                long size = 0;
                if (c.Site == "modrinth")
                {
                    Dictionary<string, object> version;
                    lock (ModrinthLock) version = BestVersion(c.ModrinthId);
                    Dictionary<string, object> file = A(Get(version, "files")).Select(D).Where(f => f != null).OrderByDescending(f => Bool(f, "primary")).FirstOrDefault();
                    if (file != null && Uri.TryCreate(S(file, "url"), UriKind.Absolute, out url) && url.Host == "cdn.modrinth.com")
                    { sha1 = S(D(Get(file, "hashes")), "sha1"); size = CfId(Get(file, "size")); }
                    else url = null;
                }
                else
                {
                    Dictionary<string, object> mod = CfMod(c.CurseForgeId);
                    Dictionary<string, object> latest = mod == null ? null : CfLatestIndex(mod);
                    Dictionary<string, object> file = latest == null ? null : CfData(CfHttp("GET", "/mods/" + c.CurseForgeId + "/files/" + CfId(Get(latest, "fileId")), null));
                    string address = S(file, "downloadUrl");
                    if (address != null && CfDownloadsAllowed(mod) && Uri.TryCreate(address, UriKind.Absolute, out url) && url.Host.EndsWith("forgecdn.net", StringComparison.OrdinalIgnoreCase))
                    {
                        sha1 = A(Get(file, "hashes")).Select(D).Where(x => x != null && CfId(Get(x, "algo")) == 1).Select(x => S(x, "value")).FirstOrDefault();
                        size = CfId(Get(file, "fileLength"));
                    }
                    else url = null;
                }
                if (url != null && size < 80L * 1024 * 1024)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(temp));
                    Download(url, temp, sha1, null);
                    JarMeta meta = GetJarMeta(new FileInfo(temp));
                    ids = new HashSet<string>(meta.Mods.Concat(meta.Nested).Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (Exception) { ids = null; }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
            lock (CandidateIds) CandidateIds[c.Key] = ids;
            return ids;
        }

        static Dictionary<string, object> InstallOneMissing(string dir, string id, string forFile, bool dryRun)
        {
            // Already provided by a jar here (turned on meanwhile, or installed with another one)?
            if (new DirectoryInfo(dir).GetFiles("*.jar").Any(f => GetJarMeta(f).Mods.Concat(GetJarMeta(f).Nested).Any(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase))))
                return Obj("id", id, "already", true);

            List<DepCandidate> candidates = DepCandidates(dir, id, forFile);
            DepCandidate chosen = null, pageOnly = null;
            int opened = 0;
            foreach (DepCandidate c in candidates)
            {
                if (opened >= 10 || JobStopped()) break;
                HashSet<string> ids = CandidateModIds(c);
                if (ids == null)
                {
                    // Can't be opened; a CurseForge mod named exactly like the id is still worth pointing to.
                    if (c.Site == "curseforge" && c.Likeness == 3 && pageOnly == null) pageOnly = c;
                    continue;
                }
                opened++;
                if (ids.Contains(id)) { chosen = c; break; }
            }
            if (chosen == null && pageOnly != null)
            {
                Dictionary<string, object> mod = CfMod(pageOnly.CurseForgeId);
                return Obj("id", id, "manual", true, "url", mod == null ? null : CfUrl(mod), "title", pageOnly.Title, "how", pageOnly.How);
            }
            if (chosen == null)
                throw new UserError("Couldn't find a mod containing " + id + " on Modrinth" + (CfReady ? " or CurseForge" : "") + " for Minecraft " + gameVersion + " with " + LoaderName() +
                    (candidates.Count > 0 ? " (opened " + opened + " likely ones: " + string.Join(", ", candidates.Take(4).Select(x => x.Title)) + (candidates.Count > 4 ? "…" : "") + ")" : "") +
                    ". It may only be on the author's own page, or under another loader.");
            if (dryRun) return Obj("id", id, "would", chosen.Title, "site", chosen.Site, "how", chosen.How);

            Dictionary<string, object> res;
            try { res = chosen.Site == "modrinth" ? ModrinthInstall(chosen.ModrinthId) : CurseForgeInstall("cf:" + chosen.CurseForgeId); }
            catch (UserError ex)
            {
                if (ex.Message.Contains("already in this mods folder")) return Obj("id", id, "already", true);
                throw;
            }
            if (Bool(res, "manual")) return Obj("id", id, "manual", true, "url", Str(res, "url"), "title", Str(res, "title"));
            var addedRaw = Get(res, "added") as System.Collections.IEnumerable; // a List here, which A() doesn't read
            List<Dictionary<string, object>> added = addedRaw == null ? new List<Dictionary<string, object>>() : addedRaw.Cast<object>().Select(D).Where(x => x != null).ToList();
            if (added.Count == 0) throw new UserError("Nothing was installed for " + id + ".");
            Dictionary<string, object> main = added.FirstOrDefault(a => !Bool(a, "dependency")) ?? added[0];
            // Checked once more on the installed file (the install may have picked another version).
            var jar = new FileInfo(Path.Combine(dir, Str(main, "file")));
            bool provides = jar.Exists && GetJarMeta(jar).Mods.Concat(GetJarMeta(jar).Nested).Any(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
            if (!provides)
            {
                RecyclePaths(added.Select(a => Path.Combine(dir, Str(a, "file"))).Where(File.Exists).ToList());
                throw new UserError("Installed " + (Str(main, "title") ?? Str(main, "file")) + " (" + chosen.How + "), but that file isn't " + id + ", so it was put in the Recycle Bin.");
            }
            Log("Added missing " + id + " -> " + Str(main, "file") + " (" + chosen.How + ")");
            return Obj("id", id, "file", Str(main, "file"), "title", Str(main, "title"), "how", chosen.How,
                "also", added.Where(a => a != main).Select(a => (object)Str(a, "title")).ToList(), "notes", Get(res, "notes"));
        }
    }
}
