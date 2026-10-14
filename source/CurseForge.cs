// CurseForge (https://docs.curseforge.com/rest-api/), alongside Modrinth: recognising jars Modrinth
// doesn't know (by CurseForge's fingerprint of the file), update checks and updates for them, and search
// + install from the Add mods dialog. Rules from the API terms:
// - the key lives only in MinecraftModManager.settings.json next to the exe (never in the repository);
// - what the API returns is kept in memory for this run only, never written to disk;
// - an author can switch off downloads through other apps (allowModDistribution = false, no downloadUrl):
//   those mods are never downloaded here; the mod's CurseForge page opens instead, and a jar downloaded
//   there still lands in the mods folder through the built-in browser.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace MinecraftModManager
{
    static partial class Program
    {
        const string CfApi = "https://api.curseforge.com/v1";
        const int CfMinecraft = 432, CfModsClass = 6;
        static string curseforgeKey;

        static bool CfReady { get { return !string.IsNullOrEmpty(curseforgeKey); } }

        static string ReadCurseForgeKey()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return null;
                var settings = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(SettingsPath)) as Dictionary<string, object>;
                string key = Str(settings, "curseforgeKey");
                return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
            }
            catch { return null; }
        }

        // CurseForge's numbers for the loaders: Forge 1, Fabric 4, Quilt 5, NeoForge 6.
        static int CfLoader()
        {
            switch (loader) { case "forge": return 1; case "fabric": return 4; case "quilt": return 5; default: return 6; }
        }

        static object CfHttp(string method, string path, object body)
        {
            if (!CfReady) throw new UserError("CurseForge isn't set up: there's no API key in the settings.");
            var request = (HttpWebRequest)WebRequest.Create(CfApi + path);
            request.Method = method;
            request.UserAgent = ModrinthUserAgent;
            request.Accept = "application/json";
            request.Headers["x-api-key"] = curseforgeKey;
            request.Timeout = 30000;
            request.ReadWriteTimeout = 30000;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            try
            {
                if (body != null)
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(body));
                    request.ContentType = "application/json";
                    request.ContentLength = bytes.Length;
                    using (Stream output = request.GetRequestStream()) output.Write(bytes, 0, bytes.Length);
                }
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(reader.ReadToEnd());
            }
            catch (WebException ex)
            {
                var response = ex.Response as HttpWebResponse;
                if (response != null && response.StatusCode == HttpStatusCode.NotFound) return null;
                if (response != null && (response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == HttpStatusCode.Unauthorized))
                    throw new UserError("CurseForge didn't accept the API key.");
                if (response != null && (int)response.StatusCode == 429)
                    throw new UserError("CurseForge is limiting requests right now. Wait a minute and try again.");
                throw new UserError("Couldn't reach CurseForge (" + ex.Message + "). Check your internet connection and try again.");
            }
        }

        static Dictionary<string, object> CfData(object response) { return D(Get(D(response), "data")); }
        static object[] CfList(object response) { return A(Get(D(response), "data")); }

        // ---- Fingerprints: CurseForge's MurmurHash2 (seed 1) of the file with tabs, newlines and spaces taken out ----

        static readonly Dictionary<string, uint> CfPrintByFile = new Dictionary<string, uint>();

        static uint CfFingerprint(FileInfo file)
        {
            string key = Regex.Replace(file.Name, @"\.disabled$", "", RegexOptions.IgnoreCase) + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks;
            uint print;
            lock (CfPrintByFile) if (CfPrintByFile.TryGetValue(key, out print)) return print;
            byte[] raw;
            using (var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                raw = new byte[stream.Length];
                int read = 0;
                while (read < raw.Length) { int n = stream.Read(raw, read, raw.Length - read); if (n <= 0) break; read += n; }
            }
            int len = 0;
            foreach (byte b in raw) if (b != 9 && b != 10 && b != 13 && b != 32) raw[len++] = b;
            unchecked
            {
                const uint m = 0x5bd1e995;
                uint h = 1u ^ (uint)len;
                int i = 0;
                for (; i + 4 <= len; i += 4)
                {
                    uint k = (uint)(raw[i] | raw[i + 1] << 8 | raw[i + 2] << 16 | raw[i + 3] << 24);
                    k *= m; k ^= k >> 24; k *= m;
                    h *= m; h ^= k;
                }
                switch (len - i)
                {
                    case 3: h ^= (uint)raw[i + 2] << 16; goto case 2;
                    case 2: h ^= (uint)raw[i + 1] << 8; goto case 1;
                    case 1: h ^= raw[i]; h *= m; break;
                }
                h ^= h >> 13; h *= m; h ^= h >> 15;
                print = h;
            }
            lock (CfPrintByFile) CfPrintByFile[key] = print;
            return print;
        }

        // ---- What CurseForge knows about the jars (this run only) ----

        static readonly Dictionary<uint, Dictionary<string, object>> CfMatchByPrint = new Dictionary<uint, Dictionary<string, object>>();
        static readonly Dictionary<long, Dictionary<string, object>> CfModById = new Dictionary<long, Dictionary<string, object>>();

        static long CfId(object value) { long n; return long.TryParse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), out n) ? n : 0; }

        // File name -> { id (mod id), file (the installed file), mod (the project) } for jars CurseForge recognises.
        static Dictionary<string, Dictionary<string, object>> CfIdentify(IEnumerable<FileInfo> files)
        {
            var prints = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            foreach (FileInfo f in files)
            {
                try { prints[f.Name] = CfFingerprint(f); } catch (IOException) { }
            }
            List<uint> unknown;
            lock (CfMatchByPrint) unknown = prints.Values.Distinct().Where(p => !CfMatchByPrint.ContainsKey(p)).ToList();
            for (int i = 0; i < unknown.Count; i += 500)
            {
                List<uint> chunk = unknown.Skip(i).Take(500).ToList();
                Dictionary<string, object> data = CfData(CfHttp("POST", "/fingerprints/" + CfMinecraft, Obj("fingerprints", chunk)));
                var found = new Dictionary<uint, Dictionary<string, object>>();
                foreach (object item in A(Get(data, "exactMatches")))
                {
                    Dictionary<string, object> match = D(item);
                    long print = CfId(Get(D(Get(match, "file")), "fileFingerprint"));
                    if (print > 0) found[(uint)print] = match;
                }
                lock (CfMatchByPrint) foreach (uint p in chunk) { Dictionary<string, object> m; found.TryGetValue(p, out m); CfMatchByPrint[p] = m; }
            }
            var result = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            lock (CfMatchByPrint)
                foreach (KeyValuePair<string, uint> p in prints)
                    if (CfMatchByPrint[p.Value] != null) result[p.Key] = CfMatchByPrint[p.Value];
            CfFetchMods(result.Values.Select(m => CfId(Get(m, "id"))));
            return result;
        }

        static void CfFetchMods(IEnumerable<long> ids)
        {
            List<long> missing;
            lock (CfModById) missing = ids.Where(id => id > 0 && !CfModById.ContainsKey(id)).Distinct().ToList();
            for (int i = 0; i < missing.Count; i += 100)
                foreach (object item in CfList(CfHttp("POST", "/mods", Obj("modIds", missing.Skip(i).Take(100).ToList()))))
                {
                    Dictionary<string, object> mod = D(item);
                    if (mod != null) lock (CfModById) CfModById[CfId(Get(mod, "id"))] = mod;
                }
        }

        static Dictionary<string, object> CfMod(long id)
        {
            Dictionary<string, object> mod;
            lock (CfModById) if (CfModById.TryGetValue(id, out mod)) return mod;
            mod = CfData(CfHttp("GET", "/mods/" + id, null));
            if (mod != null) lock (CfModById) CfModById[id] = mod;
            return mod;
        }

        static string CfUrl(Dictionary<string, object> mod) { return S(D(Get(mod, "links")), "websiteUrl"); }
        static string CfIcon(Dictionary<string, object> mod) { Dictionary<string, object> logo = D(Get(mod, "logo")); return S(logo, "thumbnailUrl") ?? S(logo, "url"); }
        static bool CfDownloadsAllowed(Dictionary<string, object> mod) { object v = Get(mod, "allowModDistribution"); return !(v is bool) || (bool)v; }
        static string CfReleaseName(object type) { long t = CfId(type); return t == 2 ? "beta" : t == 3 ? "alpha" : "release"; }

        // The newest file for the chosen Minecraft version and loader (a release when there is one), from
        // the mod's own index of latest files: { fileId, filename, releaseType }, or null.
        static Dictionary<string, object> CfLatestIndex(Dictionary<string, object> mod)
        {
            int want = CfLoader();
            List<Dictionary<string, object>> fits = A(Get(mod, "latestFilesIndexes")).Select(x => D(x)).Where(x => x != null
                && S(x, "gameVersion") == gameVersion && CfId(Get(x, "modLoader")) == want).ToList();
            return fits.Where(x => CfId(Get(x, "releaseType")) == 1).OrderByDescending(x => CfId(Get(x, "fileId"))).FirstOrDefault()
                ?? fits.OrderByDescending(x => CfId(Get(x, "fileId"))).FirstOrDefault();
        }

        // A newer file than the installed one, or null.
        // The newest file in the release types this mod may move to (see AllowedTypes), when it's newer than the jar.
        static Dictionary<string, object> CfUpdateFor(Dictionary<string, object> match)
        {
            Dictionary<string, object> mod = CfMod(CfId(Get(match, "id")));
            if (mod == null) return null;
            // A loose match whose own file isn't in CurseForge's list: nothing can be called newer.
            if (CfId(Get(D(Get(match, "file")), "id")) == 0) return null;
            string[] types = AllowedTypes(modsDir, "cf:" + CfId(Get(match, "id")), CfReleaseName(Get(D(Get(match, "file")), "releaseType")));
            int want = CfLoader();
            Dictionary<string, object> latest = A(Get(mod, "latestFilesIndexes")).Select(x => D(x)).Where(x => x != null
                    && S(x, "gameVersion") == gameVersion && CfId(Get(x, "modLoader")) == want && types.Contains(CfReleaseName(Get(x, "releaseType"))))
                .OrderByDescending(x => CfId(Get(x, "fileId"))).FirstOrDefault();
            if (latest == null) return null;
            return CfId(Get(latest, "fileId")) > CfId(Get(D(Get(match, "file")), "id")) ? latest : null;
        }

        // ---- Used by the Modrinth scan, update and changelog code for jars Modrinth doesn't know ----

        // Adds CurseForge's answer for these jars to the scan's mods map; returns how many it recognised.
        static int CfScanInto(Dictionary<string, object> mods, IEnumerable<FileInfo> jars, ref int updates)
        {
            int found = 0;
            List<FileInfo> jarList = jars.ToList();
            Dictionary<string, Dictionary<string, object>> matches = CfIdentify(jarList);
            foreach (FileInfo jar in jarList.Where(j => !matches.ContainsKey(j.Name)))
            {
                Dictionary<string, object> loose = CfLooseMatch(jar);
                if (loose != null) matches[jar.Name] = loose;
            }
            foreach (KeyValuePair<string, Dictionary<string, object>> p in matches)
            {
                Dictionary<string, object> match = p.Value, file = D(Get(match, "file"));
                Dictionary<string, object> mod = CfMod(CfId(Get(match, "id")));
                if (mod == null) continue;
                Dictionary<string, object> latest = CfUpdateFor(match);
                string key = "cf:" + CfId(Get(mod, "id"));
                string held = latest == null ? null : UpdateHeld(modsDir, key, "cf:" + CfId(Get(latest, "fileId")));
                Dictionary<string, object> heldUpdate = null;
                if (held != null)
                {
                    heldUpdate = Obj("versionId", "cf:" + CfId(Get(latest, "fileId")), "versionNumber", S(latest, "filename"), "type", CfReleaseName(Get(latest, "releaseType")), "why", held);
                    latest = null;
                }
                if (latest != null) updates++;
                found++;
                mods[p.Key] = Obj("source", "curseforge", "loose", Bool(match, "loose"), "cfModId", CfId(Get(mod, "id")), "projectId", null, "slug", null,
                    "url", CfUrl(mod), "title", S(mod, "name"), "icon", CfIcon(mod), "clientSide", null, "serverSide", null,
                    "versionNumber", S(file, "displayName"), "versionType", CfReleaseName(Get(file, "releaseType")), "held", heldUpdate, "downloadsAllowed", CfDownloadsAllowed(mod),
                    "update", latest == null ? null : Obj("versionId", "cf:" + CfId(Get(latest, "fileId")), "versionNumber", S(latest, "filename"), "type", CfReleaseName(Get(latest, "releaseType"))));
            }
            return found;
        }

        // CurseForge's match for a jar: by fingerprint, else by its mod id and name (Loose.cs).
        static Dictionary<string, object> CfMatchFor(string dir, string name)
        {
            return CfExactFor(dir, name) ?? (CfReady && JarName.IsMatch(name) ? CfLooseMatch(new FileInfo(Path.Combine(dir, name))) : null);
        }
        static Dictionary<string, object> CfExactFor(string dir, string name)
        {
            if (!CfReady) return null;
            Dictionary<string, Dictionary<string, object>> found = CfIdentify(new[] { new FileInfo(Path.Combine(dir, name)) });
            Dictionary<string, object> match;
            return found.TryGetValue(name, out match) ? match : null;
        }

        // Downloads one CurseForge file into the folder (see DownloadJar), unless its author doesn't allow it.
        static string CfDownloadFile(Dictionary<string, object> mod, long fileId, string dir, bool disabled, string replacing, List<string> notes)
        {
            string title = S(mod, "name");
            Dictionary<string, object> file = CfData(CfHttp("GET", "/mods/" + CfId(Get(mod, "id")) + "/files/" + fileId, null));
            if (file == null) throw new UserError("CurseForge has no such file of " + title + ".");
            string address = S(file, "downloadUrl");
            if (!CfDownloadsAllowed(mod) || string.IsNullOrEmpty(address))
                throw new UserError(title + "'s author only allows downloads from the CurseForge website. Open its page to get it.");
            string name = Path.GetFileName(S(file, "fileName") ?? "");
            if (!name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new UserError("CurseForge's file " + name + " isn't a .jar, so it wasn't installed.");
            Uri url;
            if (!Uri.TryCreate(address, UriKind.Absolute, out url) || url.Scheme != Uri.UriSchemeHttps || !url.Host.EndsWith("forgecdn.net", StringComparison.OrdinalIgnoreCase))
                throw new UserError("CurseForge gave an unexpected download address for " + name + ", so it wasn't installed.");
            string sha1 = A(Get(file, "hashes")).Select(x => D(x)).Where(x => x != null && CfId(Get(x, "algo")) == 1).Select(x => S(x, "value")).FirstOrDefault();
            string saved = DownloadJar(url, name, sha1, "CurseForge", dir, disabled, replacing, notes);
            if (saved != null) CfInstallDependencies(file, title, dir, notes, new HashSet<long> { CfId(Get(mod, "id")) });
            return saved;
        }

        // Required dependencies that aren't in the folder yet (the jar-level duplicate check in DownloadJar
        // also skips a dependency that's here from Modrinth under another name).
        static void CfInstallDependencies(Dictionary<string, object> file, string title, string dir, List<string> notes, HashSet<long> visited)
        {
            List<long> needed = A(Get(file, "dependencies")).Select(x => D(x)).Where(x => x != null && CfId(Get(x, "relationType")) == 3)
                .Select(x => CfId(Get(x, "modId"))).Where(id => id > 0 && visited.Add(id)).ToList();
            if (needed.Count == 0) return;
            HashSet<long> here = new HashSet<long>(CfIdentify(new DirectoryInfo(dir).GetFiles().Where(f => JarName.IsMatch(f.Name))).Values.Select(m => CfId(Get(m, "id"))));
            foreach (long id in needed.Where(id => !here.Contains(id)))
            {
                try
                {
                    Dictionary<string, object> dep = CfMod(id);
                    Dictionary<string, object> latest = dep == null ? null : CfLatestIndex(dep);
                    if (latest == null) { notes.Add(title + " needs a mod CurseForge has no " + LoaderName() + " " + gameVersion + " file of."); continue; }
                    string saved = CfDownloadFile(dep, CfId(Get(latest, "fileId")), dir, false, null, notes);
                    if (saved != null) { Log("Installed " + saved + " from CurseForge (needed by " + title + ")"); notes.Add("Also added " + S(dep, "name") + ", which " + title + " needs."); }
                }
                catch (UserError ex) { notes.Add(ex.Message); }
            }
        }

        // Update one jar CurseForge recognised: the new file replaces it, keeping it on or off and its tags.
        static string CfUpdateJar(string dir, string name, Dictionary<string, object> match, List<string> notes)
        {
            Dictionary<string, object> mod = CfMod(CfId(Get(match, "id")));
            Dictionary<string, object> latest = CfUpdateFor(match);
            if (mod == null || latest == null) throw new UserError(name + " is already up to date.");
            if (UpdateHeld(dir, "cf:" + CfId(Get(mod, "id")), null) == "pinned") throw new UserError(name + " is pinned to this version. Unpin it to update.");
            AssertNotInUse(ResolveModFile(dir, name));
            bool disabled = name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
            string newFile = CfDownloadFile(mod, CfId(Get(latest, "fileId")), dir, disabled, name, notes);
            if (newFile == null) throw new UserError(name + " couldn't be replaced.");
            string oldKey = disabled ? name.Substring(0, name.Length - ".disabled".Length) : name;
            string newKey = disabled ? newFile.Substring(0, newFile.Length - ".disabled".Length) : newFile;
            try { RenameTagKey(oldKey, newKey); }
            catch (UserError ex) { notes.Add("Couldn't move the tags of " + oldKey + ": " + ex.Message); }
            Log("Updated   " + oldKey + " -> " + newKey + " (CurseForge)");
            return newFile;
        }

        // The update dialog's entry for a CurseForge jar: the newest file and its changelog.
        static Dictionary<string, object> CfChangelogEntry(string name, Dictionary<string, object> match)
        {
            Dictionary<string, object> mod = CfMod(CfId(Get(match, "id")));
            Dictionary<string, object> latest = CfUpdateFor(match);
            if (mod == null || latest == null) throw new UserError("Already up to date.");
            long modId = CfId(Get(mod, "id")), fileId = CfId(Get(latest, "fileId"));
            Dictionary<string, object> current = D(Get(match, "file"));
            long currentId = CfId(Get(current, "id"));
            // Every file between the installed one and the update (in the same release types), newest first.
            List<Dictionary<string, object>> files = CfFiles(modId);
            Dictionary<string, object> target = files.FirstOrDefault(f => CfId(Get(f, "id")) == fileId)
                ?? CfData(CfHttp("GET", "/mods/" + modId + "/files/" + fileId, null));
            string from = S(current, "fileDate") ?? "", to = S(target, "fileDate") ?? "";
            string[] types = AllowedTypes(modsDir, "cf:" + modId, CfReleaseName(Get(current, "releaseType")));
            List<Dictionary<string, object>> between = files.Where(f => CfId(Get(f, "id")) != currentId && types.Contains(CfReleaseName(Get(f, "releaseType")))
                && string.CompareOrdinal(S(f, "fileDate") ?? "", from) > 0 && string.CompareOrdinal(S(f, "fileDate") ?? "", to) <= 0).ToList();
            if (!between.Any(f => CfId(Get(f, "id")) == fileId) && target != null) between.Insert(0, target);
            var versions = new List<object>();
            foreach (Dictionary<string, object> f in between.Take(8))
            {
                object log = null;
                try { log = Get(D(CfHttp("GET", "/mods/" + modId + "/files/" + CfId(Get(f, "id")) + "/changelog", null)), "data"); }
                catch (UserError) { }
                versions.Add(Obj("number", S(f, "displayName") ?? S(f, "fileName"), "type", CfReleaseName(Get(f, "releaseType")),
                    "date", S(f, "fileDate"), "changelog", log as string));
            }
            return Obj("file", name, "title", S(mod, "name"), "icon", CfIcon(mod), "slug", null,
                "from", S(current, "displayName"), "to", S(target, "displayName") ?? S(latest, "filename"), "count", between.Count,
                "versions", versions, "downloadsAllowed", CfDownloadsAllowed(mod), "url", CfUrl(mod));
        }

        // ---- Search and install, from the Add mods dialog ----

        // Modrinth's category slugs and the CurseForge category names that mean the same.
        static readonly Dictionary<string, string[]> CfCategoryNames = new Dictionary<string, string[]>
        {
            { "adventure", new[] { "Adventure and RPG" } }, { "equipment", new[] { "Armor, Tools, and Weapons" } }, { "library", new[] { "API and Library" } },
            { "food", new[] { "Food" } }, { "magic", new[] { "Magic" } }, { "storage", new[] { "Storage" } }, { "technology", new[] { "Technology" } },
            { "worldgen", new[] { "World Gen" } }, { "utility", new[] { "Utility & QoL" } }, { "mobs", new[] { "Mobs" } }, { "transportation", new[] { "Player Transport" } },
            { "decoration", new[] { "Cosmetic", "Decoration" } }, { "optimization", new[] { "Performance" } }, { "economy", new[] { "Economy" } }, { "management", new[] { "Server Utility" } },
            { "game-mechanics", new[] { "Miscellaneous" } }, { "social", new[] { "Server Utility" } }, { "minigame", new[] { "Minigames" } }
        };
        static List<Dictionary<string, object>> cfCategories;

        // CurseForge's id for a Modrinth category slug, or 0 when it has no such category.
        static long CfCategoryId(string slug)
        {
            string[] names;
            if (!CfCategoryNames.TryGetValue(slug ?? "", out names)) return 0;
            if (cfCategories == null) cfCategories = CfList(CfHttp("GET", "/categories?gameId=" + CfMinecraft + "&classId=" + CfModsClass, null)).Select(D).Where(c => c != null).ToList();
            Dictionary<string, object> hit = cfCategories.FirstOrDefault(c => names.Any(n => string.Equals(S(c, "name"), n, StringComparison.OrdinalIgnoreCase)));
            return hit == null ? 0 : CfId(Get(hit, "id"));
        }

        static Dictionary<string, object> CurseForgeSearch(string query, int offset, string sort, string category = null)
        {
            // CurseForge sort fields: 2 popularity, 3 last updated, 6 total downloads, 11 release date.
            int field = sort == "downloads" ? 6 : sort == "updated" ? 3 : sort == "newest" ? 11 : 2;
            long categoryId = 0;
            if (!string.IsNullOrEmpty(category))
            {
                categoryId = CfCategoryId(category);
                // No matching category there: say so rather than show unfiltered results.
                if (categoryId == 0) return Obj("ok", true, "target", TargetObj(), "total", 0, "offset", offset, "hits", new List<object>(), "noCategory", true);
            }
            Dictionary<string, object> result = D(CfHttp("GET", "/mods/search?gameId=" + CfMinecraft + "&classId=" + CfModsClass +
                "&gameVersion=" + Uri.EscapeDataString(gameVersion) + "&modLoaderType=" + CfLoader() + "&sortField=" + field + "&sortOrder=desc" +
                "&index=" + Math.Max(0, offset) + "&pageSize=20" + (categoryId > 0 ? "&categoryId=" + categoryId : "") + (string.IsNullOrWhiteSpace(query) ? "" : "&searchFilter=" + Uri.EscapeDataString(query.Trim())), null));
            if (result == null) throw new UserError("CurseForge's search didn't answer. Try again.");
            HashSet<long> installed;
            lock (ModrinthLock)
                installed = new HashSet<long>(CfIdentify(new DirectoryInfo(modsDir).GetFiles().Where(f => JarName.IsMatch(f.Name))).Values.Select(m => CfId(Get(m, "id"))));
            var hits = new List<object>();
            foreach (object item in A(Get(result, "data")))
            {
                Dictionary<string, object> mod = D(item);
                if (mod == null) continue;
                lock (CfModById) CfModById[CfId(Get(mod, "id"))] = mod;
                Dictionary<string, object> author = D(A(Get(mod, "authors")).FirstOrDefault());
                hits.Add(Obj("source", "curseforge", "projectId", "cf:" + CfId(Get(mod, "id")), "slug", S(mod, "slug"), "title", S(mod, "name"), "author", S(author, "name"),
                    "description", S(mod, "summary"), "icon", CfIcon(mod), "downloads", Get(mod, "downloadCount"), "updated", S(mod, "dateModified"),
                    "url", CfUrl(mod), "downloadsAllowed", CfDownloadsAllowed(mod), "installed", installed.Contains(CfId(Get(mod, "id")))));
            }
            Dictionary<string, object> page = D(Get(result, "pagination"));
            return Obj("ok", true, "target", TargetObj(), "total", Math.Min(CfId(Get(page, "totalCount")), 10000), "offset", offset, "hits", hits);
        }

        static Dictionary<string, object> CurseForgeInstall(string projectId)
        {
            long id = CfId((projectId ?? "").Replace("cf:", ""));
            if (id <= 0) throw new UserError("No mod was chosen.");
            lock (ModrinthLock)
            {
                string dir = modsDir;
                Dictionary<string, object> mod = CfMod(id);
                if (mod == null) throw new UserError("That mod wasn't found on CurseForge.");
                string title = S(mod, "name");
                // The author's choice: no downloads through other apps. Their page opens instead.
                if (!CfDownloadsAllowed(mod)) return Obj("ok", true, "manual", true, "url", CfUrl(mod), "title", title, "added", new List<object>(), "notes", new List<object>());
                Dictionary<string, object> latest = CfLatestIndex(mod);
                if (latest == null) throw new UserError("CurseForge has no file of " + title + " for Minecraft " + gameVersion + " with " + LoaderName() + ".");
                if (CfIdentify(new DirectoryInfo(dir).GetFiles().Where(f => JarName.IsMatch(f.Name))).Values.Any(m => CfId(Get(m, "id")) == id))
                    throw new UserError(title + " is already in this mods folder.");
                var notes = new List<string>();
                string saved = CfDownloadFile(mod, CfId(Get(latest, "fileId")), dir, false, null, notes);
                var added = new List<object>();
                if (saved != null)
                {
                    Log("Installed " + saved + " from CurseForge");
                    added.Add(Obj("file", saved, "title", title, "projectId", "cf:" + id, "dependency", false));
                }
                return Obj("ok", true, "added", added, "notes", notes);
            }
        }
    }
}
