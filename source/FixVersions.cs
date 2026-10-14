// Fixing version problems the pre-launch check finds: a mod made for another Minecraft or NeoForge
// version, or a mod that needs a library in a version range the installed one isn't in.
//
// The fix switches a jar to another of its versions on Modrinth or CurseForge (for this Minecraft
// version and loader, newest first). Each candidate's file is downloaded to a temporary folder and
// opened, and it's only picked when every declared requirement still holds with it in place: its own
// (Minecraft, NeoForge, the libraries it needs) and those of every other mod that needs it.
//
// For "X needs Y in range R": first a newer X that accepts the Y that's here, then a Y in R. A Y that's
// only bundled inside another jar gets its own jar in R (NeoForge loads the newest copy of a mod).
// Nothing changes until the plan is confirmed (dryRun shows it first).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MinecraftModManager
{
    static partial class Program
    {
        sealed class VersionPick
        {
            public string File, Site, VersionId, Number, Title, Reason;
            public bool NewJar;              // a separate jar for a mod that's only bundled elsewhere
            public string ProjectId;         // for NewJar: the Modrinth project or "cf:<id>"
            public Dictionary<string, object> Raw;
            public JarMeta Meta;             // what the file contains, once opened
            public int Newer;                // +1 newer than what's installed, -1 older, 0 unknown
        }
        static readonly Dictionary<string, JarMeta> InspectedVersions = new Dictionary<string, JarMeta>();

        sealed class FolderState
        {
            public string Dir, Mc, Nf;
            public List<FileInfo> Enabled;
            public Dictionary<string, JarMeta> Metas;
        }

        static FolderState ReadFolderState()
        {
            string dir = modsDir;
            string[] pack = PackVersions(dir);
            var jars = new DirectoryInfo(dir).GetFiles().Where(f => JarName.IsMatch(f.Name)).ToList();
            return new FolderState { Dir = dir, Mc = pack[0] ?? gameVersion, Nf = pack[1],
                Enabled = jars.Where(f => !f.Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)).ToList(),
                Metas = jars.ToDictionary(f => f.Name, f => GetJarMeta(f), StringComparer.OrdinalIgnoreCase) };
        }

        // Would every declared requirement hold with `meta` in place of the jar `replacing` (null: added)?
        static bool Acceptable(FolderState s, JarMeta meta, string replacing)
        {
            var providers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            Action<ModInfo> add = m => { List<string> l; if (!providers.TryGetValue(m.Id, out l)) providers[m.Id] = l = new List<string>(); l.Add(m.Version); };
            foreach (FileInfo f in s.Enabled.Where(f => !string.Equals(f.Name, replacing, StringComparison.OrdinalIgnoreCase)))
                foreach (ModInfo m in s.Metas[f.Name].Mods.Concat(s.Metas[f.Name].Nested)) add(m);
            foreach (ModInfo m in meta.Mods.Concat(meta.Nested)) add(m);
            var builtin = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "minecraft", s.Mc } };
            if (loader == "neoforge" && s.Nf != null) builtin["neoforge"] = s.Nf;
            // NeoForge checks a dependency's range whenever that mod is there, optional or not; an incompatible
            // one may not be there in its range at all.
            Func<DepInfo, IEnumerable<ModInfo>, bool> holds = (dep, own) =>
            {
                if (dep.Side == "SERVER" || dep.Type == "discouraged" || dep.Id == null || own.Any(m => m.Id.Equals(dep.Id, StringComparison.OrdinalIgnoreCase))) return true;
                string have;
                List<string> versions;
                if (dep.Type == "incompatible") return !(providers.TryGetValue(dep.Id, out versions) && versions.Any(v => InRange(v, dep.Range)));
                if (builtin.TryGetValue(dep.Id, out have)) return InRange(have, dep.Range);
                return !providers.TryGetValue(dep.Id, out versions) || versions.Any(v => InRange(v, dep.Range)); // missing: a separate problem
            };
            if (!meta.Deps.All(d => holds(d, meta.Mods))) return false;
            var ids = new HashSet<string>(meta.Mods.Concat(meta.Nested).Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
            foreach (FileInfo f in s.Enabled.Where(f => !string.Equals(f.Name, replacing, StringComparison.OrdinalIgnoreCase)))
                foreach (DepInfo dep in s.Metas[f.Name].Deps.Where(d => ids.Contains(d.Id ?? "")))
                    if (!holds(dep, s.Metas[f.Name].Mods)) return false;
            return true;
        }

        // Downloads one file to a temporary folder and reads it. Null when it can't be fetched.
        static JarMeta InspectRemote(string url, string sha1, string allowedHost)
        {
            Uri uri;
            if (!Uri.TryCreate(url ?? "", UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.Host.EndsWith(allowedHost, StringComparison.OrdinalIgnoreCase)) return null;
            string temp = Path.Combine(dataDir, "downloads", "check-" + Guid.NewGuid().ToString("N") + ".jar");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(temp));
                Download(uri, temp, sha1, null);
                JarMeta meta = GetJarMeta(new FileInfo(temp));
                return meta.Broken ? null : meta;
            }
            catch (Exception) { return null; }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
        }

        // The other versions of an installed jar, newest first: (site, id, number, url, sha1, raw).
        static List<VersionPick> VersionsOf(string dir, string file)
        {
            var list = new List<VersionPick>();
            string projectId = null, currentId = null;
            lock (ModrinthLock)
            {
                string hash;
                Dictionary<string, object> v = IdentifyFolder(dir).TryGetValue(file, out hash) ? VersionByHash[hash] : null;
                string currentDate = null;
                if (v != null) { projectId = S(v, "project_id"); currentId = S(v, "id"); currentDate = S(v, "date_published"); }
                if (projectId != null)
                {
                    foreach (Dictionary<string, object> ver in ProjectVersions(projectId).Where(x => S(x, "id") != currentId))
                    {
                        Dictionary<string, object> f = A(Get(ver, "files")).Select(D).Where(x => x != null).OrderByDescending(x => Bool(x, "primary")).FirstOrDefault();
                        int newer = currentDate == null ? 0 : Math.Sign(string.CompareOrdinal(S(ver, "date_published") ?? "", currentDate));
                        if (f != null) list.Add(new VersionPick { File = file, Site = "modrinth", VersionId = S(ver, "id"), Number = S(ver, "version_number"), Raw = f, Newer = newer });
                    }
                    if (list.Count > 0) return list;
                }
            }
            if (CfReady)
            {
                Dictionary<string, object> match = CfMatchFor(dir, file);
                if (match != null)
                {
                    long current = CfId(Get(D(Get(match, "file")), "id"));
                    string currentDate = S(D(Get(match, "file")), "fileDate");
                    foreach (Dictionary<string, object> f in CfFiles(CfId(Get(match, "id"))).Where(x => CfId(Get(x, "id")) != current))
                        list.Add(new VersionPick { File = file, Site = "curseforge", VersionId = "cf:" + CfId(Get(f, "id")), Number = S(f, "displayName"), Raw = f,
                            Newer = currentDate == null || current == 0 ? 0 : Math.Sign(string.CompareOrdinal(S(f, "fileDate") ?? "", currentDate)) });
                }
            }
            return list;
        }

        static JarMeta InspectPick(VersionPick p)
        {
            lock (InspectedVersions)
            {
                JarMeta known;
                if (InspectedVersions.TryGetValue(p.VersionId, out known)) return p.Meta = known;
            }
            JarMeta meta;
            if (p.Site == "modrinth") meta = InspectRemote(S(p.Raw, "url"), S(D(Get(p.Raw, "hashes")), "sha1"), "cdn.modrinth.com");
            else
            {
                string sha1 = A(Get(p.Raw, "hashes")).Select(D).Where(x => x != null && CfId(Get(x, "algo")) == 1).Select(x => S(x, "value")).FirstOrDefault();
                meta = InspectRemote(S(p.Raw, "downloadUrl"), sha1, "forgecdn.net");
            }
            lock (InspectedVersions) InspectedVersions[p.VersionId] = meta;
            return p.Meta = meta;
        }

        // The newest other version of `file` that leaves every requirement satisfied (and passes `extra`).
        // direction: +1 only newer versions, -1 only older ones, 0 either.
        static VersionPick BestReplacement(FolderState s, string file, Func<JarMeta, bool> extra, int maxLooks, int direction)
        {
            int looked = 0;
            foreach (VersionPick p in VersionsOf(s.Dir, file))
            {
                if (direction != 0 && p.Newer == -direction) continue;
                if (looked++ >= maxLooks || JobStopped()) break;
                JarMeta meta = InspectPick(p);
                if (meta == null) continue;
                if ((extra == null || extra(meta)) && Acceptable(s, meta, file)) return p;
            }
            return null;
        }

        static string ModVersionIn(JarMeta meta, string id)
        {
            ModInfo m = meta.Mods.Concat(meta.Nested).FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            return m == null ? null : m.Version;
        }

        static Dictionary<string, object> FixVersions(Dictionary<string, object> body)
        {
            bool dryRun = Bool(body, "dryRun");
            var raw = Get(body, "items") as System.Collections.IEnumerable; // a request body's lists are ArrayLists
            List<Dictionary<string, object>> items = raw == null || raw is string ? new List<Dictionary<string, object>>()
                : raw.Cast<object>().Select(D).Where(x => x != null && Str(x, "file") != null).ToList();
            if (items.Count == 0) throw new UserError("No version problems were sent.");
            FolderState s = ReadFolderState();
            var plan = new List<object>();
            var picks = new List<VersionPick>();
            var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Dictionary<string, object> item in items)
            {
                string file = Str(item, "file"), depId = Str(item, "dep"), provider = Str(item, "provider"), nestedIn = Str(item, "nestedIn");
                string key = file + "|" + depId;
                if (!handled.Add(key) || picks.Any(p => p.File == file)) continue;
                if (JobStopped()) { plan.Add(Obj("file", file, "error", "Stopped before this one.")); continue; }
                JarMeta current;
                if (!s.Metas.TryGetValue(file, out current)) { plan.Add(Obj("file", file, "error", file + " isn't in the folder any more.")); continue; }
                string name = MainMod(current) != null ? MainMod(current).Name ?? MainMod(current).Id : file;
                // Already sorted out by a switch planned above (the same library for several mods, say)?
                if (picks.Count > 0 && Acceptable(s, current, file)) { plan.Add(Obj("file", file, "title", name, "covered", true)); continue; }
                try
                {
                    VersionPick pick = null;
                    if (depId == null)
                    {
                        // Made for another Minecraft or NeoForge version: another version of this mod, newer first.
                        pick = BestReplacement(s, file, null, 8, +1) ?? BestReplacement(s, file, null, 6, -1);
                        if (pick != null) pick.Reason = "fits Minecraft " + s.Mc + (s.Nf != null ? " and NeoForge " + s.Nf : "");
                    }
                    else
                    {
                        // 1. A newer version of the mod that accepts the library that's here.
                        pick = BestReplacement(s, file, null, 6, +1);
                        if (pick != null) pick.Reason = "works with the " + depId + " that's installed";
                        // 2. The library in the range the mod needs (its own jar when it's only bundled elsewhere).
                        DepInfo need = current.Deps.FirstOrDefault(d => d.Id != null && d.Id.Equals(depId, StringComparison.OrdinalIgnoreCase));
                        if (pick == null && need != null && provider != null)
                        {
                            pick = BestReplacement(s, provider, m => InRange(ModVersionIn(m, depId), need.Range), 8, 0);
                            if (pick != null) pick.Reason = "the version " + name + " needs";
                        }
                        if (pick == null && need != null && nestedIn != null) pick = SeparateLibrary(s, depId, need.Range, file);
                        // 3. Last, an older version of the mod.
                        if (pick == null)
                        {
                            pick = BestReplacement(s, file, null, 6, -1);
                            if (pick != null) pick.Reason = "an older version that works with the " + depId + " that's installed";
                        }
                        if (pick != null && pick.Reason == null) pick.Reason = "the version " + name + " needs";
                    }
                    if (pick == null)
                    {
                        if (!plan.Cast<Dictionary<string, object>>().Any(x => Str(x, "file") == file && x.ContainsKey("error")))
                            plan.Add(Obj("file", file, "error", depId == null
                                ? "No version of it on Modrinth or CurseForge fits Minecraft " + s.Mc + (s.Nf != null ? " with NeoForge " + s.Nf : "") + ". Turn it off, or keep it and change the instance's versions."
                                : "No version of it or of " + depId + " fits together with the rest of your mods. Turn one of them off, or wait for an update."));
                        continue;
                    }
                    JarMeta was = pick.NewJar ? null : s.Metas[pick.File];
                    pick.Title = pick.NewJar ? depId : (MainMod(was) != null ? MainMod(was).Name ?? MainMod(was).Id : pick.File);
                    picks.Add(pick);
                    // Later problems are checked with this switch in place.
                    if (pick.Meta != null)
                    {
                        if (pick.NewJar) { s.Enabled.Add(new FileInfo(Path.Combine(s.Dir, pick.File))); s.Metas[pick.File] = pick.Meta; }
                        else s.Metas[pick.File] = pick.Meta;
                    }
                    plan.Add(Obj("file", pick.File, "title", pick.Title, "from", was == null ? null : (MainMod(was) != null ? MainMod(was).Version : null),
                        "to", pick.Number, "site", pick.Site, "newJar", pick.NewJar, "reason", pick.Reason, "for", name));
                }
                catch (UserError ex) { plan.Add(Obj("file", file, "error", ex.Message)); }
            }
            if (dryRun || picks.Count == 0) return Obj("ok", true, "plan", plan, "applied", false);

            if (picks.Count >= 2) AutoSnapshot(s.Dir, "Before fixing " + picks.Count + " mod versions");
            var results = new List<object>();
            foreach (VersionPick p in picks)
            {
                try
                {
                    if (p.NewJar) results.Add(Obj("title", p.Title, "to", p.Number, "file", InstallExactVersion(p)));
                    else
                    {
                        Dictionary<string, object> res = p.Site == "modrinth" ? ModrinthSwitch(p.File, p.VersionId) : CfSwitch(p.File, p.VersionId);
                        results.Add(Obj("title", p.Title, "to", p.Number, "file", Str(res, "newFile")));
                    }
                }
                catch (UserError ex) { results.Add(Obj("title", p.Title, "error", ex.Message)); }
            }
            return Obj("ok", true, "plan", plan, "applied", true, "results", results);
        }

        // A library that's only bundled inside another jar, in the range a mod needs, as its own jar.
        static VersionPick SeparateLibrary(FolderState s, string depId, string range, string forFile)
        {
            foreach (DepCandidate c in DepCandidates(s.Dir, depId, forFile).Take(6))
            {
                HashSet<string> ids = CandidateModIds(c);
                if (ids == null || !ids.Contains(depId)) continue;
                List<VersionPick> versions = new List<VersionPick>();
                if (c.Site == "modrinth")
                {
                    lock (ModrinthLock)
                        foreach (Dictionary<string, object> ver in ProjectVersions(c.ModrinthId))
                        {
                            Dictionary<string, object> f = A(Get(ver, "files")).Select(D).Where(x => x != null).OrderByDescending(x => Bool(x, "primary")).FirstOrDefault();
                            if (f != null) versions.Add(new VersionPick { Site = "modrinth", VersionId = S(ver, "id"), Number = S(ver, "version_number"), Raw = f, ProjectId = c.ModrinthId });
                        }
                }
                else
                    foreach (Dictionary<string, object> f in CfFiles(c.CurseForgeId))
                        versions.Add(new VersionPick { Site = "curseforge", VersionId = "cf:" + CfId(Get(f, "id")), Number = S(f, "displayName"), Raw = f, ProjectId = "cf:" + c.CurseForgeId });
                int looked = 0;
                foreach (VersionPick v in versions)
                {
                    if (looked++ >= 8) break;
                    JarMeta meta = InspectPick(v);
                    if (meta == null || !InRange(ModVersionIn(meta, depId), range) || !Acceptable(s, meta, null)) continue;
                    v.NewJar = true;
                    v.File = Path.GetFileName(v.Site == "modrinth" ? S(v.Raw, "filename") : S(v.Raw, "fileName"));
                    v.Reason = "its own jar, since the copy bundled in another mod is too old";
                    return v;
                }
            }
            return null;
        }

        static string InstallExactVersion(VersionPick p)
        {
            var notes = new List<string>();
            if (p.Site == "modrinth")
            {
                lock (ModrinthLock)
                {
                    HashSet<string> installed = InstalledProjects(modsDir);
                    var plan = new List<PlanItem>();
                    Resolve(p.ProjectId, p.VersionId, null, installed, new HashSet<string>(installed), plan, notes);
                    List<object> added = InstallPlan(plan, modsDir, notes);
                    Dictionary<string, object> first = added.Select(D).FirstOrDefault();
                    if (first == null) throw new UserError(notes.FirstOrDefault() ?? "Nothing was installed.");
                    return Str(first, "file");
                }
            }
            Dictionary<string, object> mod = CfMod(CfId(p.ProjectId.Replace("cf:", "")));
            if (mod == null) throw new UserError("CurseForge doesn't know that mod any more.");
            string saved = CfDownloadFile(mod, CfId(p.VersionId.Replace("cf:", "")), modsDir, false, null, notes);
            if (saved == null) throw new UserError(notes.FirstOrDefault() ?? "Nothing was installed.");
            return saved;
        }
    }
}
