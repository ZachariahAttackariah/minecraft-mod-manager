// The dependency tree for a mod's details: what it requires, what those require, and so on, from the
// jars' own metadata in this folder. A mod that already appeared higher up isn't expanded again.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MinecraftModManager
{
    static partial class Program
    {
        static Dictionary<string, object> DependencyTree(string name)
        {
            string dir = modsDir;
            var root = new FileInfo(ResolveModFile(dir, name));
            var jars = new DirectoryInfo(dir).GetFiles().Where(f => JarName.IsMatch(f.Name)).ToList();
            var metas = jars.ToDictionary(f => f.Name, f => GetJarMeta(f), StringComparer.OrdinalIgnoreCase);
            // Who provides each mod id: an enabled jar wins over a turned-off one; bundled copies count too.
            var provider = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
            var bundled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (FileInfo f in jars.OrderBy(f => f.Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (ModInfo m in metas[f.Name].Mods) if (!provider.ContainsKey(m.Id)) provider[m.Id] = f;
                foreach (ModInfo m in metas[f.Name].Nested) bundled.Add(m.Id);
            }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int nodes = 0;
            Func<FileInfo, int, List<object>> children = null;
            children = (jar, depth) =>
            {
                var list = new List<object>();
                foreach (DepInfo dep in metas[jar.Name].Deps.Where(d => d.Type == "required").GroupBy(d => d.Id).Select(g => g.First()))
                {
                    if (nodes++ > 400) break;
                    string id = dep.Id ?? "";
                    if (BuiltInIds.Contains(id)) continue;
                    // Provided by the jar itself (its own mods or ones bundled inside it).
                    if (metas[jar.Name].Mods.Concat(metas[jar.Name].Nested).Any(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase))) continue;
                    FileInfo p;
                    if (!provider.TryGetValue(id, out p))
                    {
                        list.Add(Obj("id", id, "name", id, "status", bundled.Contains(id) ? "bundled" : "missing", "range", dep.Range, "children", new List<object>()));
                        continue;
                    }
                    ModInfo main = MainMod(metas[p.Name]);
                    bool off = p.Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                    bool again = !seen.Add(p.Name);
                    list.Add(Obj("id", id, "name", main != null ? main.Name ?? main.Id : p.Name, "file", p.Name, "status", off ? "off" : "ok", "range", dep.Range,
                        "repeat", again, "children", again || depth >= 8 ? new List<object>() : children(p, depth + 1)));
                }
                return list.OrderBy(o => S(D(o), "name"), StringComparer.OrdinalIgnoreCase).ToList();
            };
            seen.Add(root.Name);
            ModInfo top = MainMod(metas[root.Name]);
            return Obj("ok", true, "name", top != null ? top.Name ?? top.Id : root.Name, "file", root.Name, "children", children(root, 0), "total", seen.Count - 1);
        }
    }
}
