// Fabric mods on a NeoForge (or Forge 1.20.1) instance, through Sinytra Connector, with Forgified Fabric API
// standing in for Fabric API. The Add mods dialog can show Fabric results instead of native ones: while a
// request asks for them, this thread's loader reads "fabric" (loaderOverride), so the usual search, preview,
// version and install code picks Fabric files. Installing one also installs Connector and Forgified Fabric API
// (native builds) when the folder doesn't have them. The Fabric mod's own requirements come as native builds
// where Modrinth has one, Fabric builds otherwise; Fabric API itself is never installed, Forgified Fabric API is.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MinecraftModManager
{
    static partial class Program
    {
        const string ConnectorProject = "u58R1TMW", ForgifiedFabricApiProject = "Aqlf1Shp", FabricApiProject = "P7dR8mSH";

        // Connector runs on NeoForge and Forge; a Fabric or Quilt instance loads Fabric mods itself.
        static bool ConnectorHost() { return loaderSetting == "neoforge" || loaderSetting == "forge"; }

        sealed class LoaderScope : IDisposable
        {
            readonly string previous;
            public LoaderScope(string use) { previous = loaderOverride; loaderOverride = use; }
            public void Dispose() { loaderOverride = previous; }
        }
        static LoaderScope AsFabric() { return new LoaderScope("fabric"); }
        static LoaderScope AsNative() { return new LoaderScope(null); }

        static LoaderScope FabricInstall(bool fabric)
        {
            if (!fabric) return new LoaderScope(loaderOverride);
            if (!ConnectorHost()) throw new UserError("Fabric mods go through Sinytra Connector, which is for NeoForge and Forge instances. This one is " + LoaderName(loaderSetting) + ".");
            return AsFabric();
        }

        // 2: a jar in the folder is (or bundles) this mod id and is on; 1: only a turned-off copy; 0: none.
        static int FolderModState(string dir, string modId)
        {
            int state = 0;
            foreach (FileInfo f in new DirectoryInfo(dir).GetFiles().Where(f => JarName.IsMatch(f.Name)))
            {
                JarMeta meta = GetJarMeta(f);
                if (!meta.Mods.Concat(meta.Nested).Any(m => string.Equals(m.Id, modId, StringComparison.OrdinalIgnoreCase))) continue;
                if (!f.Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)) return 2;
                state = 1;
            }
            return state;
        }

        // Before a Fabric mod is resolved: Connector and Forgified Fabric API go first in the plan (native builds),
        // unless the folder has them already, however they got there. Without Connector there's no point
        // installing the Fabric mod, so that's an error rather than a note.
        static void PlanConnector(string dir, string forTitle, HashSet<string> installed, HashSet<string> visited, List<PlanItem> plan, List<string> notes)
        {
            using (AsNative())
            {
                foreach (var need in new[] { new[] { "connector", ConnectorProject, "Sinytra Connector" }, new[] { "fabric_api", ForgifiedFabricApiProject, "Forgified Fabric API" } })
                {
                    int state = FolderModState(dir, need[0]);
                    if (state == 1) notes.Add(need[2] + " is turned off in this folder. Turn it on, or Fabric mods won't load.");
                    if (state > 0) { installed.Add(need[1]); visited.Add(need[1]); continue; }
                    if (installed.Contains(need[1]) || visited.Contains(need[1])) continue;
                    int before = plan.Count;
                    Resolve(need[1], null, forTitle, installed, visited, plan, notes);
                    if (need[1] == ConnectorProject && plan.Count == before)
                        throw new UserError("Couldn't add Sinytra Connector, which " + forTitle + " needs to run on " + LoaderName(loaderSetting) + ". " + string.Join(" ", notes));
                }
            }
        }

        // A requirement of a Fabric mod being installed through Connector. Returns false when the usual
        // resolving should go ahead (a Fabric build).
        static bool ResolveFabricDependency(string depProject, string title, HashSet<string> installed, HashSet<string> visited, List<PlanItem> plan, List<string> notes)
        {
            if (loaderOverride != "fabric") return false;
            using (AsNative())
            {
                // Fabric API is what Forgified Fabric API replaces (it's already in the plan or the folder).
                if (depProject == FabricApiProject)
                {
                    if (!installed.Contains(ForgifiedFabricApiProject)) Resolve(ForgifiedFabricApiProject, null, title, installed, visited, plan, notes);
                    return true;
                }
                if (installed.Contains(depProject) || visited.Contains(depProject)) return true;
                // A native build beats a Fabric one run through Connector (Cloth Config, Architectury and the like).
                if (BestVersion(depProject) == null) return false;
                Resolve(depProject, null, title, installed, visited, plan, notes);
                return true;
            }
        }
    }
}
