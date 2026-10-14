// Mod details for jars CurseForge knows and Modrinth doesn't: the description, screenshots, links,
// every file for this Minecraft version and loader with changelogs, and switching to one of them.
// Like the rest of the CurseForge data, it's kept in memory for the current run only.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MinecraftModManager
{
    static partial class Program
    {
        static readonly Dictionary<long, string> CfDescriptionById = new Dictionary<long, string>();
        static readonly Dictionary<string, KeyValuePair<DateTime, List<Dictionary<string, object>>>> CfFilesCache =
            new Dictionary<string, KeyValuePair<DateTime, List<Dictionary<string, object>>>>();

        // The overview for a CurseForge jar, in the same shape as ModrinthInfo. Its description is HTML (CurseForge
        // writes them that way); the page turns it into plain formatted text before showing it.
        static Dictionary<string, object> CfInfo(FileInfo file)
        {
            Dictionary<string, object> match = CfMatchFor(file.DirectoryName, file.Name);
            if (match == null) return null;
            long id = CfId(Get(match, "id"));
            Dictionary<string, object> mod = CfMod(id);
            if (mod == null) return null;
            Dictionary<string, object> current = D(Get(match, "file"));
            Dictionary<string, object> links = D(Get(mod, "links"));
            Dictionary<string, object> author = D(A(Get(mod, "authors")).FirstOrDefault());
            return Obj("found", true, "site", "curseforge", "projectId", "cf:" + id, "slug", S(mod, "slug"), "title", S(mod, "name"),
                "description", S(mod, "summary"), "body", CfDescription(id), "icon", CfIcon(mod), "author", S(author, "name"),
                "downloads", Get(mod, "downloadCount"), "followers", null, "clientSide", null, "serverSide", null,
                "categories", A(Get(mod, "categories")).Select(c => (object)S(D(c), "name")).Where(c => c != null).ToList(),
                "license", null, "url", S(links, "websiteUrl"), "source", S(links, "sourceUrl"), "issues", S(links, "issuesUrl"), "wiki", S(links, "wikiUrl"),
                "discord", null, "updated", S(mod, "dateModified"), "downloadsAllowed", CfDownloadsAllowed(mod),
                "gallery", A(Get(mod, "screenshots")).Select(x => D(x)).Where(x => x != null && S(x, "url") != null)
                    .Select(x => (object)Obj("url", S(x, "thumbnailUrl") ?? S(x, "url"), "raw", S(x, "url"), "title", S(x, "title"), "description", S(x, "description"), "featured", false)).ToList(),
                "versionNumber", S(current, "displayName"), "versionId", "cf:" + CfId(Get(current, "id")), "loose", Bool(match, "loose"));
        }

        static string CfDescription(long id)
        {
            lock (CfDescriptionById)
            {
                string cached;
                if (CfDescriptionById.TryGetValue(id, out cached)) return cached;
            }
            string html = null;
            try { html = Get(D(CfHttp("GET", "/mods/" + id + "/description", null)), "data") as string; }
            catch (UserError) { return null; }
            lock (CfDescriptionById) CfDescriptionById[id] = html;
            return html;
        }

        // Every file of a mod for the chosen Minecraft version and loader, newest first (kept 10 minutes).
        static List<Dictionary<string, object>> CfFiles(long id)
        {
            string key = id + "|" + gameVersion + "|" + loader;
            lock (CfFilesCache)
            {
                KeyValuePair<DateTime, List<Dictionary<string, object>>> cached;
                if (CfFilesCache.TryGetValue(key, out cached) && DateTime.UtcNow - cached.Key < TimeSpan.FromMinutes(10)) return cached.Value;
            }
            var files = new List<Dictionary<string, object>>();
            for (int index = 0; index < 200; index += 50)
            {
                object[] page = CfList(CfHttp("GET", "/mods/" + id + "/files?gameVersion=" + Uri.EscapeDataString(gameVersion) +
                    "&modLoaderType=" + CfLoader() + "&index=" + index + "&pageSize=50", null));
                files.AddRange(page.Select(D).Where(f => f != null));
                if (page.Length < 50) break;
            }
            files = files.Where(f => !(Get(f, "isAvailable") is bool) || (bool)Get(f, "isAvailable"))
                .OrderByDescending(f => S(f, "fileDate") ?? "").ThenByDescending(f => CfId(Get(f, "id"))).ToList();
            lock (CfFilesCache) CfFilesCache[key] = new KeyValuePair<DateTime, List<Dictionary<string, object>>>(DateTime.UtcNow, files);
            return files;
        }

        static Dictionary<string, object> CfVersionObj(Dictionary<string, object> f, long currentId, string currentDate)
        {
            long fid = CfId(Get(f, "id"));
            string date = S(f, "fileDate") ?? "";
            return Obj("id", "cf:" + fid, "number", S(f, "displayName") ?? S(f, "fileName"), "name", S(f, "fileName"), "type", CfReleaseName(Get(f, "releaseType")),
                "date", date, "changelog", null, "cf", true, "downloads", Get(f, "downloadCount"),
                "installed", fid == currentId, "newer", currentDate != null && (string.CompareOrdinal(date, currentDate) > 0 || (date == currentDate && fid > currentId)));
        }

        static Dictionary<string, object> CfVersions(string name)
        {
            Dictionary<string, object> match = CfMatchFor(modsDir, name);
            if (match == null) throw new UserError(name + " isn't on Modrinth or CurseForge, so there are no versions to show.");
            long id = CfId(Get(match, "id"));
            Dictionary<string, object> mod = CfMod(id);
            Dictionary<string, object> current = D(Get(match, "file"));
            long currentId = CfId(Get(current, "id"));
            string currentDate = S(current, "fileDate");
            List<object> versions = CfFiles(id).Select(f => (object)CfVersionObj(f, currentId, currentDate)).ToList();
            return Obj("ok", true, "source", "curseforge", "loose", Bool(match, "loose"), "target", TargetObj(), "url", CfUrl(mod), "downloadsAllowed", CfDownloadsAllowed(mod),
                "current", Obj("id", "cf:" + currentId, "number", S(current, "displayName")), "versions", versions);
        }

        // One file's changelog (HTML), fetched when its row is opened.
        static Dictionary<string, object> CfFileChangelog(string name, string fileId)
        {
            Dictionary<string, object> match = CfMatchFor(modsDir, name);
            if (match == null) throw new UserError(name + " isn't on CurseForge.");
            long fid = CfId((fileId ?? "").Replace("cf:", ""));
            if (fid <= 0) throw new UserError("No version was chosen.");
            string html = Get(D(CfHttp("GET", "/mods/" + CfId(Get(match, "id")) + "/files/" + fid + "/changelog", null)), "data") as string;
            return Obj("ok", true, "html", html);
        }

        // Swaps a CurseForge jar for another of its files (newer or older), keeping on/off and tags.
        static Dictionary<string, object> CfSwitch(string name, string fileId)
        {
            long fid = CfId((fileId ?? "").Replace("cf:", ""));
            if (fid <= 0) throw new UserError("No version was chosen.");
            lock (ModrinthLock)
            {
                string dir = modsDir;
                ResolveModFile(dir, name);
                Dictionary<string, object> match = CfMatchFor(dir, name);
                if (match == null) throw new UserError(name + " isn't on CurseForge.");
                long id = CfId(Get(match, "id"));
                Dictionary<string, object> mod = CfMod(id);
                if (mod == null) throw new UserError("CurseForge doesn't know this mod any more.");
                if (fid == CfId(Get(D(Get(match, "file")), "id"))) throw new UserError("That version is already installed.");
                Dictionary<string, object> target = CfFiles(id).FirstOrDefault(f => CfId(Get(f, "id")) == fid);
                if (target == null) throw new UserError("That file isn't for Minecraft " + gameVersion + " with " + LoaderName() + ".");
                AssertNotInUse(ResolveModFile(dir, name));
                bool disabled = name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                var notes = new List<string>();
                string newFile = CfDownloadFile(mod, fid, dir, disabled, name, notes);
                if (newFile == null) throw new UserError(name + " couldn't be replaced.");
                string oldKey = disabled ? name.Substring(0, name.Length - ".disabled".Length) : name;
                string newKey = disabled ? newFile.Substring(0, newFile.Length - ".disabled".Length) : newFile;
                try { RenameTagKey(oldKey, newKey); }
                catch (UserError ex) { notes.Add("Couldn't move the tags of " + oldKey + ": " + ex.Message); }
                Log("Switched  " + oldKey + " -> " + newKey + " (CurseForge)");
                return Obj("ok", true, "newFile", newFile, "version", S(target, "displayName"), "added", new List<object>(), "notes", notes);
            }
        }
    }
}
