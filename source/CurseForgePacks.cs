// Packs from CurseForge: searching and installing resource packs, shader packs and data packs there, next to
// Modrinth (Packs.cs), the same way Add mods searches both sites. CurseForge files packs under their own
// classes (Resource Packs, Shaders, Data Packs); their ids are looked up once and kept in memory. A pack's
// author can switch off downloads through other apps: then its page opens instead, and a zip downloaded
// in the tab goes into the pack folder by itself (HandlePackDownload). Nothing from CurseForge is cached
// on disk.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MinecraftModManager
{
    static partial class Program
    {
        static Dictionary<string, long> cfPackClasses;

        // CurseForge's class id for a kind of pack (0 when it can't be found).
        static long CfPackClass(string kind)
        {
            if (cfPackClasses == null)
            {
                var found = new Dictionary<string, long>();
                try
                {
                    foreach (Dictionary<string, object> c in CfList(CfHttp("GET", "/categories?gameId=" + CfMinecraft + "&classesOnly=true", null)).Select(D).Where(c => c != null))
                    {
                        string name = (S(c, "name") ?? "").ToLowerInvariant(), slug = (S(c, "slug") ?? "").ToLowerInvariant();
                        if (name == "resource packs" || slug == "texture-packs") found["resourcepacks"] = CfId(Get(c, "id"));
                        else if (name == "shaders" || slug == "shaders") found["shaderpacks"] = CfId(Get(c, "id"));
                        else if (name == "data packs" || slug == "data-packs") found["datapacks"] = CfId(Get(c, "id"));
                    }
                }
                catch (UserError) { }
                // CurseForge's long-standing ids, should the list not answer.
                if (!found.ContainsKey("resourcepacks")) found["resourcepacks"] = 12;
                if (!found.ContainsKey("shaderpacks")) found["shaderpacks"] = 6552;
                if (!found.ContainsKey("datapacks")) found["datapacks"] = 6945;
                cfPackClasses = found;
            }
            long id;
            return cfPackClasses.TryGetValue(kind, out id) ? id : 0;
        }

        // The file to install: the newest release for this Minecraft version (any version for shader packs,
        // which work across versions), else the newest file of any kind.
        static Dictionary<string, object> CfPackLatestIndex(Dictionary<string, object> mod, string kind)
        {
            List<Dictionary<string, object>> fits = A(Get(mod, "latestFilesIndexes")).Select(D)
                .Where(x => x != null && (kind == "shaderpacks" || S(x, "gameVersion") == gameVersion)).ToList();
            return fits.Where(x => CfId(Get(x, "releaseType")) == 1).OrderByDescending(x => CfId(Get(x, "fileId"))).FirstOrDefault()
                ?? fits.OrderByDescending(x => CfId(Get(x, "fileId"))).FirstOrDefault();
        }

        static HashSet<long> CfInstalledPacks(string dir)
        {
            var ids = new HashSet<long>();
            try
            {
                foreach (Dictionary<string, object> m in CfIdentify(PackEntries(dir).OfType<FileInfo>().Where(f => f.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))).Values)
                    ids.Add(CfId(Get(m, "id")));
            }
            catch (UserError) { }
            return ids;
        }

        static Dictionary<string, object> CurseForgePackSearch(string kind, string placeId, string query, int offset, string sort)
        {
            if (!CfReady) throw new UserError("CurseForge search needs a CurseForge API key (Settings > Sources).");
            PackPlace place = FindPlace(kind, placeId);
            long classId = CfPackClass(kind);
            // CurseForge sort fields: 2 popularity, 3 last updated, 6 total downloads, 11 release date.
            int field = sort == "downloads" ? 6 : sort == "updated" ? 3 : sort == "newest" ? 11 : 2;
            Dictionary<string, object> result = D(CfHttp("GET", "/mods/search?gameId=" + CfMinecraft + "&classId=" + classId +
                (kind == "shaderpacks" ? "" : "&gameVersion=" + Uri.EscapeDataString(gameVersion)) +
                "&sortField=" + field + "&sortOrder=desc&index=" + Math.Max(0, offset) + "&pageSize=20" +
                (string.IsNullOrWhiteSpace(query) ? "" : "&searchFilter=" + Uri.EscapeDataString(query.Trim())), null));
            if (result == null) throw new UserError("CurseForge's search didn't answer. Try again.");
            HashSet<long> installed = CfInstalledPacks(place.Dir);
            var hits = new List<object>();
            foreach (Dictionary<string, object> mod in A(Get(result, "data")).Select(D).Where(m => m != null))
            {
                long id = CfId(Get(mod, "id"));
                lock (CfModById) CfModById[id] = mod;
                Dictionary<string, object> author = D(A(Get(mod, "authors")).FirstOrDefault());
                hits.Add(Obj("source", "curseforge", "projectId", "cf:" + id, "slug", S(mod, "slug"), "title", S(mod, "name"), "author", S(author, "name"),
                    "description", S(mod, "summary"), "icon", CfIcon(mod), "downloads", Get(mod, "downloadCount"), "updated", S(mod, "dateModified"),
                    "url", CfUrl(mod), "downloadsAllowed", CfDownloadsAllowed(mod), "installed", installed.Contains(id)));
            }
            Dictionary<string, object> page = D(Get(result, "pagination"));
            return Obj("ok", true, "target", TargetObj(), "total", Math.Min(CfId(Get(page, "totalCount")), 10000), "offset", offset, "hits", hits);
        }

        static Dictionary<string, object> CurseForgePackInstall(string kind, string placeId, string projectId)
        {
            long id = CfId((projectId ?? "").Replace("cf:", ""));
            if (id <= 0) throw new UserError("No pack was chosen.");
            PackPlace place = FindPlace(kind, placeId);
            var notes = new List<string>();
            string saved, title;
            lock (ModrinthLock)
            {
                Dictionary<string, object> mod = CfMod(id);
                if (mod == null) throw new UserError("That pack wasn't found on CurseForge.");
                title = S(mod, "name");
                // The author's choice: no downloads through other apps. Their page opens instead.
                if (!CfDownloadsAllowed(mod)) return Obj("ok", true, "manual", true, "url", CfUrl(mod), "title", title, "added", new List<object>(), "notes", notes);
                if (CfInstalledPacks(place.Dir).Contains(id)) throw new UserError(title + " is already there.");
                Dictionary<string, object> latest = CfPackLatestIndex(mod, kind);
                if (latest == null)
                    throw new UserError("CurseForge has no " + KindNoun(kind) + " download of " + title + (kind == "shaderpacks" ? "." : " for Minecraft " + gameVersion + "."));
                Dictionary<string, object> file = CfData(CfHttp("GET", "/mods/" + id + "/files/" + CfId(Get(latest, "fileId")), null));
                if (file == null) throw new UserError("CurseForge has no such file of " + title + ".");
                string address = S(file, "downloadUrl"), name = Path.GetFileName(S(file, "fileName") ?? "");
                if (string.IsNullOrEmpty(address)) return Obj("ok", true, "manual", true, "url", CfUrl(mod), "title", title, "added", new List<object>(), "notes", notes);
                if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    throw new UserError("CurseForge's file " + name + " isn't a .zip, so it wasn't installed.");
                Uri url;
                if (!Uri.TryCreate(address, UriKind.Absolute, out url) || url.Scheme != Uri.UriSchemeHttps || !url.Host.EndsWith("forgecdn.net", StringComparison.OrdinalIgnoreCase))
                    throw new UserError("CurseForge gave an unexpected download address for " + name + ", so it wasn't installed.");
                string sha1 = A(Get(file, "hashes")).Select(D).Where(x => x != null && CfId(Get(x, "algo")) == 1).Select(x => S(x, "value")).FirstOrDefault();
                Directory.CreateDirectory(place.Dir);
                saved = DownloadPackZip(url, name, sha1, "CurseForge", place.Dir, null);
                Log("Installed " + saved + " from CurseForge into " + place.Label + " (" + place.Dir + ")");
            }
            List<string> added = EnsurePackMod(kind, place, notes);
            if (place.Side == "server" && ServerBusy()) notes.Add("The server is running, so it picks the pack up after /reload or a restart.");
            return Obj("ok", true, "file", saved, "title", title, "added", added, "notes", notes);
        }
    }
}
