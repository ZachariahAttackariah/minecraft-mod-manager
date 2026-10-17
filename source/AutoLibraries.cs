// New libraries go to the Libraries list by themselves: a jar the manager installs (from Modrinth or
// CurseForge, a dependency too), or that's dropped on the window, downloaded in a site tab or copied from
// the server, gets the "library" tag when its site files it as a library (Modrinth's "library" category,
// CurseForge's "API and Library"). Only new jars are looked at: updates and version switches keep the
// tags the jar had. Settings > Mod list can turn it off.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MinecraftModManager
{
    static partial class Program
    {
        static bool TagLibrariesOn()
        {
            return PrefFlag("tagLibraries", true) && Directory.Exists(modsDir) && !HasLinkedTags(modsDir);
        }

        // Plenty of libraries are filed under another category; their summary gives them away ("An open source
        // library for other mods", "code, frameworks, and utilities for minecraft mods").
        static readonly System.Text.RegularExpressions.Regex LibrarySummary = new System.Text.RegularExpressions.Regex(
            @"\b(librar(y|ies)|apis?|frameworks?)\b.*\bmods?\b|\bmods?\b.*\b(librar(y|ies)|apis?|frameworks?)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        static bool ModrinthIsLibrary(Dictionary<string, object> project)
        {
            return project != null && (A(Get(project, "categories")).Concat(A(Get(project, "additional_categories")))
                .OfType<string>().Any(c => string.Equals(c, "library", StringComparison.OrdinalIgnoreCase))
                || LibrarySummary.IsMatch(S(project, "description") ?? ""));
        }

        static bool CfIsLibrary(Dictionary<string, object> mod)
        {
            return mod != null && (A(Get(mod, "categories")).Select(c => S(D(c), "name"))
                .Any(n => string.Equals(n, "API and Library", StringComparison.OrdinalIgnoreCase))
                || LibrarySummary.IsMatch(S(mod, "summary") ?? ""));
        }

        static void TagAsLibrary(string file, string title, List<string> notes)
        {
            try
            {
                if (UpdateTags(new List<string> { file }, new List<string> { "library" }, new List<string>()) > 0 && notes != null)
                    notes.Add((title ?? file) + " is a library, so it's listed under Libraries.");
            }
            catch (UserError ex) { if (notes != null) notes.Add("Couldn't tag " + file + " as a library: " + ex.Message); }
        }

        // A jar just installed from Modrinth (the project is known).
        static void TagLibraryByProject(string file, string projectId, List<string> notes)
        {
            if (file == null || projectId == null || !TagLibrariesOn()) return;
            Dictionary<string, object> project;
            if (!ProjectById.TryGetValue(projectId, out project)) return;
            if (ModrinthIsLibrary(project)) TagAsLibrary(file, S(project, "title"), notes);
        }

        // A jar just installed from CurseForge.
        static void TagLibraryByCf(string file, Dictionary<string, object> mod, List<string> notes)
        {
            if (file == null || !TagLibrariesOn()) return;
            if (CfIsLibrary(mod)) TagAsLibrary(file, S(mod, "name"), notes);
        }

        // A jar that arrived some other way: looked up by its hash on Modrinth, else on CurseForge.
        static void TagLibraryByLookup(string file, List<string> notes)
        {
            if (file == null || !TagLibrariesOn()) return;
            try
            {
                lock (ModrinthLock)
                {
                    var info = new FileInfo(Path.Combine(modsDir, file));
                    if (!info.Exists) return;
                    Dictionary<string, string> hashes = IdentifyFiles(new[] { info });
                    string hash;
                    Dictionary<string, object> version;
                    if (hashes.TryGetValue(file, out hash) && VersionByHash.TryGetValue(hash, out version) && version != null)
                    {
                        string projectId = S(version, "project_id");
                        Dictionary<string, object> project;
                        if (!ProjectById.TryGetValue(projectId ?? "", out project)) project = GetProject(projectId);
                        if (ModrinthIsLibrary(project)) TagAsLibrary(file, S(project, "title"), notes);
                        return;
                    }
                    if (!CfReady) return;
                    Dictionary<string, object> match = CfMatchFor(modsDir, file);
                    if (match != null) TagLibraryByCf(file, CfMod(CfId(Get(match, "id"))), notes);
                }
            }
            catch (Exception ex) { Log("Couldn't look up " + file + " for the library tag: " + ex.Message); }
        }
    }
}
