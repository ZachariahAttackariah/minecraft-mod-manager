// Detecting where each jar installs (client only, server only or both) for the Detect button by the
// side chart. A jar that says it belongs on one side only (clientSideOnly, or a Fabric environment) is
// taken at its word. Otherwise Modrinth's client / server flags decide. Without those it's a guess:
// a jar whose displayTest says the server needn't have it is probably client only (only probably:
// Axes Are Weapons, a server-side mod, says the same), anything else is probably needed on both.
// Last, every library is added to the sides of the mods that require it (FollowDependencies).
// Nothing is changed here; the page shows the results and applies the ones the user keeps as tags.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MinecraftModManager
{
    static partial class Program
    {
        static Dictionary<string, object> DetectSides(bool serverExtras)
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

            var found = new List<SideGuess>();
            foreach (FileInfo jar in jars)
            {
                JarMeta meta = GetJarMeta(jar);
                Dictionary<string, object> project;
                projects.TryGetValue(jar.Name, out project);
                string side = null, source = null, reason = null;
                bool sure = true, serverOptional = false;
                if (meta.Side != null)
                {
                    side = meta.Side; source = "mod file"; reason = "The jar says it's " + side + " only: " + meta.SideReason + ".";
                }
                else if (project != null && (side = ModrinthSide(S(project, "client_side"), S(project, "server_side"), serverExtras)) != null)
                {
                    source = "Modrinth";
                    string cs = SideWord(S(project, "client_side")), ss = SideWord(S(project, "server_side"));
                    reason = "Modrinth lists it as " + cs + " on the client and " + ss + " on the server." +
                        (side == "server" && cs == "optional" ? " It works without the client having it, so it's only needed on the server." :
                         side == "client" && ss == "optional" ? " It works without the server having it, so it's only needed on the client." :
                         side == "both" && cs == "required" && ss == "optional" ? " It works on the client alone, but the server copy adds features, so it goes on both." : "");
                    serverOptional = cs == "required" && ss == "optional";
                }
                else if (meta.NoServerNeeded)
                {
                    side = "client"; source = "mod file"; sure = false;
                    reason = "Not on Modrinth" + (project != null ? " with side info" : "") + ". The jar says the server doesn't need it, which usually means client only, but some server-side mods say the same.";
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
                found.Add(new SideGuess { File = jar.Name, Meta = meta, Name = project != null ? S(project, "title") : main != null ? main.Name : null,
                    Sides = SideBits(side), Source = source, Sure = sure, Reason = reason, ServerOptional = serverOptional });
            }
            FollowDependencies(found);
            var mods = found.Select(g => (object)Obj("file", g.File, "key", NormalizeKey(g.File), "name", g.Name,
                "side", SideName(g.Sides), "source", g.Source, "sure", g.Sure, "reason", g.Reason, "serverOptional", g.ServerOptional)).ToList();
            return Obj("ok", true, "mods", mods, "serverExtras", serverExtras, "serverOptionalCount", found.Count(g => g.ServerOptional), "modrinthProblem", modrinthProblem);
        }

        sealed class SideGuess
        {
            public string File, Name, Source, Reason;
            public JarMeta Meta;
            public int Sides; // 1 client, 2 server, 3 both, 0 unknown
            public bool Sure, ServerOptional;
        }

        static int SideBits(string side) { return side == "client" ? 1 : side == "server" ? 2 : side == "both" ? 3 : 0; }
        static string SideName(int bits) { return bits == 1 ? "client" : bits == 2 ? "server" : bits == 3 ? "both" : null; }

        static readonly HashSet<string> BuiltInIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "minecraft", "neoforge", "forge", "fabricloader", "java", "fml", "javafml" };

        // A library goes wherever a mod that requires it goes. Modrinth often lists libraries and core
        // mods (Create, Geckolib, CreativeCore) as optional on a side their dependents need them on, so
        // each jar's sides grow to cover the sides of the mods that require it, until nothing changes.
        static void FollowDependencies(List<SideGuess> all)
        {
            var providers = new Dictionary<string, List<SideGuess>>(StringComparer.OrdinalIgnoreCase);
            foreach (SideGuess g in all)
                foreach (ModInfo m in g.Meta.Mods)
                {
                    List<SideGuess> list;
                    if (!providers.TryGetValue(m.Id, out list)) providers[m.Id] = list = new List<SideGuess>();
                    if (!list.Contains(g)) list.Add(g);
                }
            for (int round = 0; round < 20; round++)
            {
                bool changed = false;
                foreach (SideGuess user in all)
                {
                    if (user.Sides == 0 || !user.Sure) continue; // a guess doesn't move other mods
                    foreach (DepInfo dep in user.Meta.Deps)
                    {
                        if (dep.Type != "required" || BuiltInIds.Contains(dep.Id) || user.Meta.Mods.Any(m => m.Id.Equals(dep.Id, StringComparison.OrdinalIgnoreCase))) continue;
                        int need = user.Sides & (dep.Side == "CLIENT" ? 1 : dep.Side == "SERVER" ? 2 : 3);
                        List<SideGuess> libs;
                        if (need == 0 || !providers.TryGetValue(dep.Id, out libs)) continue;
                        foreach (SideGuess lib in libs)
                        {
                            int added = need & ~lib.Sides;
                            if (lib == user || added == 0) continue;
                            if (lib.Sides == 0) { lib.Source = "dependencies"; lib.Sure = user.Sure; lib.Reason = ""; }
                            lib.Sides |= added;
                            lib.Reason = (lib.Reason + " Needed on the " + (added == 3 ? "client and server" : added == 1 ? "client" : "server") + " because " + (user.Name ?? user.File) + " requires it.").Trim();
                            changed = true;
                        }
                    }
                }
                if (!changed) break;
            }
        }

        // Modrinth's flags are required / optional / unsupported / unknown. Tags mean "must be installed
        // there", so a required side is in, and an optional one only when neither side is required
        // (works on either, so both). Checked against ~770 of Roma's hand-tagged mods: 98% the same,
        // where counting every optional side agreed 92%. New installs are tagged by the same rule.
        // serverExtras: a client mod whose server copy is optional (it adds features there: multiplayer
        // sync, server-side data) goes on both. The user's choice in the Detect dialog, on by default.
        static string ModrinthSide(string client, string server, bool serverExtras = false)
        {
            bool known = new[] { client, server }.Any(v => v == "required" || v == "optional" || v == "unsupported");
            if (!known) return null;
            bool cReq = client == "required", sReq = server == "required";
            if (cReq && !sReq && serverExtras && server == "optional") return "both";
            if (cReq || sReq) return cReq && sReq ? "both" : cReq ? "client" : "server";
            bool c = client != "unsupported", s = server != "unsupported";
            return c && s ? "both" : c ? "client" : s ? "server" : null;
        }

        static string SideWord(string flag)
        {
            return flag == "required" || flag == "optional" || flag == "unsupported" ? flag : "unknown";
        }
    }
}
