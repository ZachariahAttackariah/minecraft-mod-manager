// Auto-tag: each known jar's categories on Modrinth (categories + additional categories) or CurseForge
// (category names). The page maps them onto the folder's own tags and shows them for review; nothing is
// tagged from here.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace MinecraftModManager
{
    static partial class Program
    {
        static Dictionary<string, object> TagSuggestionSources()
        {
            var mods = new List<object>();
            lock (ModrinthLock)
            {
                string dir = modsDir;
                Dictionary<string, string> hashes = IdentifyFolder(dir);
                var cfRest = new List<FileInfo>();
                foreach (KeyValuePair<string, string> p in hashes)
                {
                    string key = Regex.Replace(p.Key, @"\.disabled$", "", RegexOptions.IgnoreCase);
                    Dictionary<string, object> version = VersionByHash[p.Value];
                    if (version == null) { cfRest.Add(new FileInfo(Path.Combine(dir, p.Key))); continue; }
                    Dictionary<string, object> project;
                    if (!ProjectById.TryGetValue(S(version, "project_id") ?? "", out project)) project = GetProject(S(version, "project_id"));
                    if (project == null) continue;
                    var cats = A(Get(project, "categories")).Concat(A(Get(project, "additional_categories"))).OfType<string>().Distinct().ToList();
                    mods.Add(Obj("file", p.Key, "key", key, "site", "modrinth", "title", S(project, "title"), "categories", cats,
                        "clientSide", S(project, "client_side"), "serverSide", S(project, "server_side")));
                }
                if (CfReady && cfRest.Count > 0)
                {
                    foreach (FileInfo f in cfRest)
                    {
                        Dictionary<string, object> match;
                        try { match = CfMatchFor(dir, f.Name); } catch (UserError) { continue; }
                        Dictionary<string, object> mod = match == null ? null : CfMod(CfId(Get(match, "id")));
                        if (mod == null) continue;
                        var cats = A(Get(mod, "categories")).Select(c => S(D(c), "name")).Where(c => c != null).Distinct().ToList();
                        mods.Add(Obj("file", f.Name, "key", Regex.Replace(f.Name, @"\.disabled$", "", RegexOptions.IgnoreCase), "site", "curseforge",
                            "title", S(mod, "name"), "categories", cats));
                    }
                }
            }
            return Obj("ok", true, "mods", mods);
        }
    }
}
