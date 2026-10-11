// Detecting where each jar installs (client only, server only or both) for the Detect button by the
// side chart. A jar that says it belongs on one side only (a client-only displayTest, clientSideOnly, or
// a Fabric environment) is taken at its word. Otherwise Modrinth's client / server flags decide. A mod
// with neither is a guess: its file doesn't limit it to one side, so it's probably needed on both.
// Nothing is changed here; the page shows the results and applies the ones the user keeps as tags.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MinecraftModManager
{
    static partial class Program
    {
        static Dictionary<string, object> DetectSides()
        {
            string dir = modsDir;
            if (!Directory.Exists(dir)) throw new UserError(dir + " isn't there any more.");
            List<FileInfo> jars = new DirectoryInfo(dir).GetFiles().Where(f => JarName.IsMatch(f.Name)).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();

            // Modrinth's flags, by file name. Without Modrinth the jars' own metadata still counts.
            var projects = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            string modrinthProblem = null;
            lock (ModrinthLock)
            {
                try
                {
                    foreach (KeyValuePair<string, string> pair in IdentifyFolder(dir))
                    {
                        Dictionary<string, object> version, project;
                        if (!VersionByHash.TryGetValue(pair.Value, out version) || version == null) continue;
                        if (ProjectById.TryGetValue(S(version, "project_id") ?? "", out project) && project != null) projects[pair.Key] = project;
                    }
                }
                catch (Exception ex)
                {
                    modrinthProblem = ex is UserError ? ex.Message : "Modrinth didn't answer (" + ex.Message + "), so only the mod files were checked.";
                }
            }

            var mods = new List<object>();
            foreach (FileInfo jar in jars)
            {
                JarMeta meta = GetJarMeta(jar);
                Dictionary<string, object> project;
                projects.TryGetValue(jar.Name, out project);
                string side = null, source = null, reason = null;
                bool sure = true;
                if (meta.Side != null)
                {
                    side = meta.Side; source = "mod file"; reason = "The jar says it's " + side + " only: " + meta.SideReason + ".";
                }
                else if (project != null && (side = ModrinthSide(S(project, "client_side"), S(project, "server_side"))) != null)
                {
                    source = "Modrinth";
                    reason = "Modrinth lists it as " + SideWord(S(project, "client_side")) + " on the client and " + SideWord(S(project, "server_side")) + " on the server.";
                }
                else if (!meta.Broken && (meta.Kind != "none" || meta.Nested.Count > 0))
                {
                    side = "both"; source = "mod file"; sure = false;
                    reason = "Not on Modrinth" + (project != null ? " with side info" : "") + ", and the jar doesn't limit itself to one side, so it's probably needed on both.";
                }
                else
                {
                    reason = meta.Broken ? "The jar couldn't be read." : "Not on Modrinth, and the jar has no mod info to go on.";
                }
                ModInfo main = MainMod(meta);
                mods.Add(Obj("file", jar.Name, "key", NormalizeKey(jar.Name), "name", project != null ? S(project, "title") : main != null ? main.Name : null,
                    "side", side, "source", source, "sure", sure, "reason", reason));
            }
            return Obj("ok", true, "mods", mods, "modrinthProblem", modrinthProblem);
        }

        // Modrinth's flags are required / optional / unsupported / unknown. A side is in unless it's
        // unsupported (the same rule new installs are tagged by); both unknown means no answer.
        static string ModrinthSide(string client, string server)
        {
            bool known = new[] { client, server }.Any(v => v == "required" || v == "optional" || v == "unsupported");
            if (!known) return null;
            bool c = client != "unsupported", s = server != "unsupported";
            return c && s ? "both" : c ? "client" : s ? "server" : null;
        }

        static string SideWord(string flag)
        {
            return flag == "required" || flag == "optional" || flag == "unsupported" ? flag : "unknown";
        }
    }
}
