// Duplicate mods, found by the pre-launch check: jars in one mods folder that are the same mod (the
// same main mod id, or for a jar with no mod info of its own, a byte-for-byte copy). Turned-off copies
// count too, so the list doesn't fill up with spares. The check picks the copy to keep, so the fix is
// one button: a copy made for this Minecraft version first, then one built for the instance's own
// loader (not a Fabric build run through Connector), then the version the other mods ask for, then
// one that's on, then the newest version, then the newest file. The others go to the Recycle Bin,
// their tags move to the kept copy, and if the only copy that was on is removed, the kept one is
// turned on in its place.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MinecraftModManager
{
    sealed class DupCopy
    {
        public FileInfo File;
        public string Version;
        public bool Enabled, Fits, Native;
        public int NeedsMet; // how many other mods' version requirements on this mod it satisfies
    }

    sealed class DupGroup
    {
        public string Name;
        public bool Identical;
        public List<DupCopy> Copies; // best first
        public string Reason;        // why the first one is kept
    }

    static partial class Program
    {
        static List<DupGroup> FindDuplicates(List<FileInfo> jars, Dictionary<string, JarMeta> metas, string mc)
        {
            var byKey = new Dictionary<string, List<FileInfo>>(StringComparer.OrdinalIgnoreCase);
            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (FileInfo jar in jars)
            {
                JarMeta meta = metas[jar.Name];
                string key;
                if (meta.Mods.Count > 0) key = "id:" + meta.Mods[0].Id;
                else
                {
                    // A wrapper jar or plain library: only an identical file counts, since two different
                    // ones can bundle the same thing.
                    try { key = "sha1:" + FileSha1(jar); } catch (IOException) { continue; }
                }
                List<FileInfo> list;
                if (!byKey.TryGetValue(key, out list)) byKey[key] = list = new List<FileInfo>();
                list.Add(jar);
            }

            // The version ranges that turned-on mods require of each mod id (file -> range), so the
            // copy that keeps them happy can win.
            var needs = new Dictionary<string, List<KeyValuePair<string, string>>>(StringComparer.OrdinalIgnoreCase);
            foreach (FileInfo jar in jars)
            {
                if (jar.Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)) continue;
                JarMeta meta = metas[jar.Name];
                foreach (DepInfo dep in meta.Deps)
                {
                    if (dep.Type != "required" || string.IsNullOrWhiteSpace(dep.Range) || dep.Range.Trim() == "*") continue;
                    if (meta.Mods.Any(m => m.Id.Equals(dep.Id, StringComparison.OrdinalIgnoreCase))) continue;
                    List<KeyValuePair<string, string>> list;
                    if (!needs.TryGetValue(dep.Id, out list)) needs[dep.Id] = list = new List<KeyValuePair<string, string>>();
                    list.Add(new KeyValuePair<string, string>(jar.Name, dep.Range));
                }
            }

            var groups = new List<DupGroup>();
            foreach (List<FileInfo> files in byKey.Values.Where(l => l.Count > 1))
            {
                string id = metas[files[0].Name].Mods.Count > 0 ? metas[files[0].Name].Mods[0].Id : null;
                List<KeyValuePair<string, string>> wanted;
                if (id == null || !needs.TryGetValue(id, out wanted)) wanted = new List<KeyValuePair<string, string>>();
                wanted = wanted.Where(w => !files.Any(f => f.Name == w.Key)).ToList();
                var copies = files.Select(f =>
                {
                    JarMeta meta = metas[f.Name];
                    string version = meta.Mods.Count > 0 && !Unknown(meta.Mods[0].Version) ? meta.Mods[0].Version : null;
                    return new DupCopy
                    {
                        File = f,
                        Version = version,
                        Enabled = !f.Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase),
                        Fits = FitsGame(meta, mc),
                        Native = meta.Kind == loader,
                        NeedsMet = wanted.Count(w => InRange(version, w.Value))
                    };
                }).ToList();
                copies.Sort(CompareCopies);
                string first = null;
                bool identical = true;
                foreach (DupCopy c in copies)
                {
                    string h;
                    try { h = FileSha1(c.File); } catch (IOException) { h = c.File.Name; }
                    if (first == null) first = h; else if (h != first) identical = false;
                }
                ModInfo main = MainMod(metas[copies[0].File.Name]);
                groups.Add(new DupGroup
                {
                    Name = main != null ? main.Name : copies[0].File.Name,
                    Identical = identical,
                    Copies = copies,
                    Reason = KeepReason(copies[0], copies[1], identical, mc)
                });
            }
            return groups.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        // Best copy first: made for this game, built for its loader, what other mods need, on, newest
        // version, newest file, plainest name.
        static int CompareCopies(DupCopy a, DupCopy b)
        {
            if (a.Fits != b.Fits) return a.Fits ? -1 : 1;
            if (a.Native != b.Native) return a.Native ? -1 : 1;
            if (a.NeedsMet != b.NeedsMet) return b.NeedsMet - a.NeedsMet;
            if (a.Enabled != b.Enabled) return a.Enabled ? -1 : 1;
            if (a.Version != null && b.Version != null)
            {
                int v = CompareVersions(b.Version, a.Version);
                if (v != 0) return v;
            }
            else if ((a.Version == null) != (b.Version == null)) return a.Version != null ? -1 : 1;
            int t = b.File.LastWriteTimeUtc.CompareTo(a.File.LastWriteTimeUtc);
            if (t != 0) return t;
            if (a.File.Name.Length != b.File.Name.Length) return a.File.Name.Length - b.File.Name.Length;
            return string.Compare(a.File.Name, b.File.Name, StringComparison.OrdinalIgnoreCase);
        }

        static string KeepReason(DupCopy best, DupCopy next, bool identical, string mc)
        {
            if (identical) return best.Enabled && !next.Enabled ? "the copies are identical, and it's the one that's on" : "the copies are identical";
            if (best.Fits != next.Fits) return "it's the one made for Minecraft " + mc;
            if (best.Native != next.Native) return "it's the " + LoaderName(loader) + " build";
            if (best.NeedsMet != next.NeedsMet) return "it's the version your other mods need";
            if (best.Enabled != next.Enabled) return "it's the one that's on";
            if (best.Version != null && next.Version != null && CompareVersions(best.Version, next.Version) != 0) return "it's the newest version";
            return "it's the newest file";
        }

        // Whether NeoForge would load the jar in this game at all (loader and Minecraft version).
        static bool FitsGame(JarMeta meta, string mc)
        {
            if (meta.Broken) return false;
            if (loader == "neoforge" && meta.Kind == "forge" && CompareVersions(mc, "1.20.5") >= 0) return false;
            foreach (DepInfo dep in meta.Deps)
            {
                if (!dep.Id.Equals("minecraft", StringComparison.OrdinalIgnoreCase) || dep.Type != "required") continue;
                if (!InRange(mc, dep.Range) && !MinecraftHotfixFits(mc, dep.Range)) return false;
            }
            return true;
        }

        static Dictionary<string, object> DuplicateIssue(DupGroup g, bool canFix)
        {
            DupCopy keep = g.Copies[0];
            List<DupCopy> others = g.Copies.Skip(1).ToList();
            int on = g.Copies.Count(c => c.Enabled);
            bool turnOn = !keep.Enabled && others.Any(c => c.Enabled);
            var versions = g.Copies.Select(c => c.Version).Where(v => v != null).Distinct().ToList();
            string what = on > 1
                ? g.Name + " is in the mods folder " + g.Copies.Count + " times" + (versions.Count > 1 ? " (" + string.Join(", ", versions) + ")" : "") + ", and the game refuses to start with more than one on."
                : g.Name + " has " + (others.Count == 1 ? "a spare copy" : others.Count + " spare copies") + " in the mods folder" + (versions.Count > 1 ? " (" + string.Join(", ", versions) + ")" : "") + ".";
            string plan = " Keeps " + keep.File.Name + " because " + g.Reason + (turnOn ? ", and turns it on" : "") + ".";
            return Issue(on > 1 ? "error" : "warning", "duplicate", what + (canFix ? plan : ""),
                g.Copies.Select(c => c.File.Name),
                canFix ? Obj("action", "remove-duplicates", "keep", keep.File.Name, "remove", others.Select(c => c.File.Name).ToList(), "turnOn", turnOn) : null);
        }

        // Body: { groups: [{ keep, remove: [...] }] }, from one fix or "Fix all". Each pair is checked
        // against the folder as it is now, so nothing that stopped being a duplicate is removed.
        static Dictionary<string, object> RemoveDuplicates(Dictionary<string, object> body)
        {
            string dir = modsDir;
            if (!Directory.Exists(dir)) throw new UserError("The mods folder " + dir + " doesn't exist.");
            List<FileInfo> jars = new DirectoryInfo(dir).GetFiles().Where(f => JarName.IsMatch(f.Name)).ToList();
            var metas = jars.ToDictionary(f => f.Name, f => GetJarMeta(f));
            List<DupGroup> groups = FindDuplicates(jars, metas, PackVersions(dir)[0] ?? gameVersion);

            var plan = new List<KeyValuePair<string, List<string>>>();
            foreach (object item in (Get(body, "groups") as IEnumerable) ?? new object[0])
            {
                Dictionary<string, object> req = D(item);
                string keep = Str(req, "keep");
                List<string> remove = StrList(req, "remove");
                DupGroup g = groups.FirstOrDefault(x => x.Copies.Any(c => c.File.Name == keep));
                if (g == null || remove.Count == 0 || remove.Contains(keep) || remove.Any(r => !g.Copies.Any(c => c.File.Name == r)))
                    throw new UserError("The mods folder changed since the check. Run the check again.");
                plan.Add(new KeyValuePair<string, List<string>>(keep, remove));
            }
            if (plan.Count == 0) throw new UserError("No duplicates were chosen.");

            int total = plan.Sum(p => p.Value.Count);
            if (total >= 2) AutoSnapshot(dir, "Before removing " + total + " duplicate mods");
            var removed = new List<string>();
            var turnedOn = new List<string>();
            var failed = new List<object>();
            foreach (KeyValuePair<string, List<string>> p in plan)
            {
                string keep = p.Key;
                var gone = new List<string>();
                foreach (string file in p.Value)
                {
                    try { MoveToRecycleBin(file); gone.Add(file); }
                    catch (UserError ex) { failed.Add(Obj("file", file, "error", ex.Message)); }
                }
                removed.AddRange(gone);
                MergeTagEntries(NormalizeKey(keep), gone.Select(NormalizeKey).ToList());
                bool keepOff = keep.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                if (keepOff && gone.Count == p.Value.Count && gone.Any(f => !f.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)))
                {
                    try { turnedOn.Add(SetEnabled(keep, true)); }
                    catch (UserError ex) { failed.Add(Obj("file", keep, "error", ex.Message)); }
                }
                if (gone.Count > 0) Log("Duplicate kept " + keep + ", removed " + string.Join(", ", gone));
            }
            return Obj("ok", true, "removed", removed, "turnedOn", turnedOn, "failed", failed);
        }

        // Moves the tags of removed copies onto the kept one, and drops their entries so they don't
        // linger in the list as "not in folder".
        static void MergeTagEntries(string keepKey, List<string> removedKeys)
        {
            lock (FileLock)
            {
                string path = TagsPath();
                if (!File.Exists(path)) return;
                TagsDoc doc = LoadTagsDoc(path);
                Func<string, string> find = k => doc.Mods.Keys.FirstOrDefault(x => string.Equals(x, k, StringComparison.OrdinalIgnoreCase));
                string keep = find(keepKey);
                var merged = keep != null ? new List<string>(doc.Mods[keep]) : new List<string>();
                bool changed = false;
                foreach (string r in removedKeys.Where(k => !string.Equals(k, keepKey, StringComparison.OrdinalIgnoreCase)))
                {
                    string existing = find(r);
                    if (existing == null) continue;
                    foreach (string t in doc.Mods[existing]) if (!merged.Contains(t)) merged.Add(t);
                    doc.Mods.Remove(existing);
                    changed = true;
                }
                if (!changed) return;
                var order = doc.Tags.Select(t => t.Key).ToList();
                doc.Mods[keep ?? keepKey] = merged.OrderBy(t => order.IndexOf(t) < 0 ? int.MaxValue : order.IndexOf(t)).ToList();
                SaveTagsDoc(path, doc);
            }
        }
    }
}
