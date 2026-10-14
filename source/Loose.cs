// Jars that neither Modrinth (SHA-1) nor CurseForge (fingerprint) knows byte for byte: often the same mod
// built or repackaged elsewhere. They're matched by the mod id inside the jar, which is usually the
// project's slug, as long as the name agrees too. Modrinth is tried first, then CurseForge.
//
// Such a "loose" match gets a description, screenshots and versions like any other. An update is only
// offered when one of the site's versions carries the jar's own version number, so the app knows where
// the jar stands; otherwise its versions are listed but nothing is called newer.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace MinecraftModManager
{
    static partial class Program
    {
        static readonly HashSet<string> LooseTried = new HashSet<string>();
        static readonly Dictionary<string, Dictionary<string, object>> CfLooseByFile = new Dictionary<string, Dictionary<string, object>>();
        static readonly Dictionary<string, Dictionary<string, object>> CfModBySlug = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);

        static string LooseName(string s) { return Regex.Replace((s ?? "").ToLowerInvariant(), @"[^a-z0-9]", ""); }

        // The site's name and the jar's own name agree (one contains the other), or the jar has no name.
        static bool NamesAgree(string siteName, string jarName, string id)
        {
            string a = LooseName(siteName), b = LooseName(jarName ?? id);
            return a.Length > 0 && b.Length > 0 && (a.Contains(b) || b.Contains(a));
        }

        // The version number written in the jar, as it appears in a site's version or file names.
        static bool HasVersion(string text, string version)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(version) || version.Contains("${")) return false;
            return Regex.IsMatch(text, @"(^|[^0-9.])" + Regex.Escape(version) + @"($|[^0-9])", RegexOptions.IgnoreCase);
        }

        // Called with ModrinthLock held, for a jar whose hash Modrinth doesn't know. Records a stand-in
        // "version" for it so the rest of the Modrinth code treats it like a known jar. True when matched.
        static bool LooseModrinth(FileInfo file, string hash)
        {
            if (VersionByHash.ContainsKey(hash) && VersionByHash[hash] != null) return true;
            if (!file.Name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) && !file.Name.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)) return false;
            if (!LooseTried.Add("mr|" + hash)) return false;
            ModInfo main = MainMod(GetJarMeta(file));
            if (main == null || string.IsNullOrEmpty(main.Id)) return false;
            Dictionary<string, object> project;
            try { project = GetProject(main.Id) ?? GetProject(main.Id.Replace('_', '-')); }
            catch (UserError) { return false; }
            if (project == null || S(project, "project_type") != "mod" || !NamesAgree(S(project, "title"), main.Name, main.Id)) return false;
            List<Dictionary<string, object>> versions;
            try { versions = ProjectVersions(S(project, "id")); }
            catch (UserError) { versions = new List<Dictionary<string, object>>(); }
            // No files for this Minecraft version and loader on Modrinth: CurseForge's page for the mod is the useful one, if it has it.
            if (versions.Count == 0 && CfReady && CfLooseMatch(file) != null) return false;
            string jarFile = Regex.Replace(file.Name, @"\.disabled$", "", RegexOptions.IgnoreCase);
            Dictionary<string, object> current = versions.Where(v => HasVersion(S(v, "version_number"), main.Version) || HasVersion(S(v, "name"), main.Version))
                .OrderByDescending(v => A(Get(v, "files")).Any(x => string.Equals(S(D(x), "filename"), jarFile, StringComparison.OrdinalIgnoreCase)))
                .ThenBy(v => (S(v, "version_number") ?? "").Length).FirstOrDefault();
            VersionByHash[hash] = Obj("id", current != null ? S(current, "id") : "loose:" + hash, "project_id", S(project, "id"),
                "version_number", main.Version ?? "?", "date_published", current != null ? S(current, "date_published") : null,
                "version_type", current != null ? S(current, "version_type") : "release", "loose", true, "dependencies", new object[0]);
            return true;
        }

        // The newest allowed version for a loosely matched jar, worked out from the version list (Modrinth's
        // update lookup goes by hash, which it doesn't know). Null when the jar's place in the list is unknown.
        static Dictionary<string, object> LooseLatest(string hash)
        {
            Dictionary<string, object> current = VersionByHash[hash];
            if (current == null || S(current, "date_published") == null) return null;
            string[] types = AllowedTypes(modsDir, S(current, "project_id"), S(current, "version_type"));
            try { return ProjectVersions(S(current, "project_id")).FirstOrDefault(v => types.Contains(S(v, "version_type"))); }
            catch (UserError) { return null; }
        }

        // The CurseForge side: a stand-in for CfIdentify's match ({ id, file }) found by the mod id as slug.
        static Dictionary<string, object> CfLooseMatch(FileInfo file)
        {
            if (!CfReady) return null;
            string cacheKey = file.FullName.ToLowerInvariant().Replace(".disabled", "") + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks;
            lock (CfLooseByFile)
            {
                Dictionary<string, object> cached;
                if (CfLooseByFile.TryGetValue(cacheKey, out cached)) return cached;
            }
            Dictionary<string, object> match = null;
            try
            {
                ModInfo main = MainMod(GetJarMeta(file));
                if (main != null && !string.IsNullOrEmpty(main.Id))
                {
                    Dictionary<string, object> mod = CfModBySlugFind(main.Id) ?? CfModBySlugFind(main.Id.Replace('_', '-'));
                    if (mod != null && NamesAgree(S(mod, "name"), main.Name, main.Id))
                    {
                        long id = CfId(Get(mod, "id"));
                        lock (CfModById) CfModById[id] = mod;
                        // The file with the jar's own name, else the plainest one carrying its version number.
                        string jarName = Regex.Replace(file.Name, @"\.disabled$", "", RegexOptions.IgnoreCase);
                        Dictionary<string, object> current = CfFiles(id).Where(f => HasVersion(S(f, "fileName"), main.Version) || HasVersion(S(f, "displayName"), main.Version))
                            .OrderByDescending(f => string.Equals(S(f, "fileName"), jarName, StringComparison.OrdinalIgnoreCase))
                            .ThenBy(f => (S(f, "fileName") ?? "").Length).FirstOrDefault();
                        match = Obj("id", id, "loose", true, "file", current ?? Obj("id", 0, "displayName", main.Version ?? file.Name, "fileDate", null, "releaseType", 1));
                    }
                }
            }
            catch (UserError) { return null; } // not cached: CurseForge may just be unreachable right now
            lock (CfLooseByFile) CfLooseByFile[cacheKey] = match;
            return match;
        }

        static Dictionary<string, object> CfModBySlugFind(string slug)
        {
            lock (CfModBySlug)
            {
                Dictionary<string, object> known;
                if (CfModBySlug.TryGetValue(slug, out known)) return known;
            }
            Dictionary<string, object> mod = A(Get(D(CfHttp("GET", "/mods/search?gameId=" + CfMinecraft + "&classId=" + CfModsClass + "&slug=" + Uri.EscapeDataString(slug), null)), "data"))
                .Select(D).FirstOrDefault(m => m != null && string.Equals(S(m, "slug"), slug, StringComparison.OrdinalIgnoreCase));
            lock (CfModBySlug) CfModBySlug[slug] = mod;
            return mod;
        }
    }
}
