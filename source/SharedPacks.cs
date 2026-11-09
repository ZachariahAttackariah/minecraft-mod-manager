// Packs shared across instances: resource packs, shader packs and data packs, kept like the shared mods
// (AlwaysMods.cs) in <data>\shared\always-<kind> and copied into every ticked instance (the same ticks
// as the mods). It runs whenever the shared mods do.
//  - Resource packs go into the game's resourcepacks folder and are turned on once in each instance
//    (the first time that instance has the pack and has an options.txt). Turned off later, they stay off.
//  - Shader packs go into shaderpacks, only where Iris or Oculus is on. The chosen shader isn't changed.
//  - Data packs go into the every-world Paxi folder when the instance has Paxi on, otherwise into each
//    of its worlds (a world made later gets them the next time this runs). A server folder gets them in
//    its world; it gets no resource or shader packs.
//  - A pack folder linked to the shared one (Sharing.cs) counts once, however many instances use it.
//  - A place already has a pack when it has a file or folder of that name, or a zip with the same
//    contents. The app's own unchanged copy is swapped when the shared file changes; a copy someone
//    replaced is left alone.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace MinecraftModManager
{
    static partial class Program
    {
        static string SharedPackDir(string kind) { return Path.Combine(SharedDir(), "always-" + kind); }
        static string SharedPackStatePath() { return Path.Combine(SharedDir(), "always-packs.json"); }

        static void CheckPackKind(string kind)
        {
            if (Array.IndexOf(PackKinds, kind) < 0) throw new UserError("Unknown kind of pack.");
        }

        sealed class SharedPackState
        {
            // kind -> folder (final path, lower case) -> pack file name -> SHA-1 of the copy the app put there.
            public readonly Dictionary<string, Dictionary<string, Dictionary<string, string>>> Placed = new Dictionary<string, Dictionary<string, Dictionary<string, string>>>();
            // game folder (lower case) -> resource packs the app has turned on there once.
            public readonly Dictionary<string, List<string>> TurnedOn = new Dictionary<string, List<string>>();
        }

        static SharedPackState LoadSharedPackState()
        {
            var state = new SharedPackState();
            try
            {
                string path = SharedPackStatePath();
                if (!File.Exists(path)) return state;
                var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>;
                Dictionary<string, object> placed = D(Get(json, "placed"));
                if (placed != null)
                    foreach (KeyValuePair<string, object> kind in placed)
                    {
                        Dictionary<string, object> dirs = D(kind.Value);
                        if (dirs == null) continue;
                        var byDir = new Dictionary<string, Dictionary<string, string>>();
                        foreach (KeyValuePair<string, object> dir in dirs)
                        {
                            Dictionary<string, object> files = D(dir.Value);
                            if (files == null) continue;
                            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            foreach (KeyValuePair<string, object> f in files) if (f.Value is string) map[f.Key] = (string)f.Value;
                            byDir[dir.Key] = map;
                        }
                        state.Placed[kind.Key] = byDir;
                    }
                Dictionary<string, object> on = D(Get(json, "turnedOn"));
                if (on != null)
                    foreach (KeyValuePair<string, object> kv in on)
                        state.TurnedOn[kv.Key] = A(kv.Value).OfType<string>().ToList();
            }
            catch { }
            return state;
        }

        static void SaveSharedPackState(SharedPackState state)
        {
            Directory.CreateDirectory(SharedDir());
            var placed = state.Placed.ToDictionary(k => k.Key, k => (object)k.Value.Where(d => d.Value.Count > 0)
                .ToDictionary(d => d.Key, d => (object)d.Value.ToDictionary(f => f.Key, f => (object)f.Value)));
            var on = state.TurnedOn.Where(kv => kv.Value.Count > 0).ToDictionary(kv => kv.Key, kv => (object)kv.Value);
            File.WriteAllText(SharedPackStatePath(), new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(Obj("placed", placed, "turnedOn", on)),
                new UTF8Encoding(false));
        }

        static Dictionary<string, string> PlacedPacks(SharedPackState state, string kind, string key)
        {
            Dictionary<string, Dictionary<string, string>> byDir;
            if (!state.Placed.TryGetValue(kind, out byDir)) state.Placed[kind] = byDir = new Dictionary<string, Dictionary<string, string>>();
            Dictionary<string, string> map;
            if (!byDir.TryGetValue(key, out map)) byDir[key] = map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return map;
        }

        static List<FileInfo> SharedPacks(string kind)
        {
            string dir = SharedPackDir(kind);
            if (!Directory.Exists(dir)) return new List<FileInfo>();
            return new DirectoryInfo(dir).GetFiles("*.zip").OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // ---------- Where each kind goes ----------

        sealed class PackDest
        {
            public string Dir, Label;
        }

        static bool IsServerDir(string gameDir) { return gameDir != null && File.Exists(Path.Combine(gameDir, "server.properties")); }

        static bool HasModOn(string modsFolder, params string[] ids)
        {
            FileInfo jar = FindModJar(modsFolder, ids);
            return jar != null && !jar.Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
        }

        // The folders one kind of pack goes into for that place. why: it can't take that kind at all.
        // waiting: it could, but there's nowhere yet (no worlds).
        static List<PackDest> PackDestsFor(AlwaysTarget t, string kind, out string why, out string waiting)
        {
            why = null;
            waiting = null;
            var list = new List<PackDest>();
            if (t.GameDir == null) { why = "it isn't in a game folder"; return list; }
            bool server = IsServerDir(t.GameDir);
            if (kind != "datapacks")
            {
                if (server) { why = "servers don't use " + KindNoun(kind) + "s"; return list; }
                if (kind == "shaderpacks" && !HasModOn(t.Dir, "iris", "oculus")) { why = "it has no Iris or Oculus"; return list; }
                list.Add(new PackDest { Dir = Path.Combine(t.GameDir, kind), Label = KindNoun(kind) + "s" });
                return list;
            }
            if (HasModOn(t.Dir, "paxi"))
            {
                list.Add(new PackDest { Dir = PaxiDataDir(t.GameDir), Label = "every world (Paxi)" });
                return list;
            }
            if (server)
            {
                string level;
                Dictionary<string, string> props = ReadProperties(Path.Combine(t.GameDir, "server.properties"));
                level = props.TryGetValue("level-name", out level) && !string.IsNullOrWhiteSpace(level) ? level.Trim() : "world";
                if (File.Exists(Path.Combine(t.GameDir, level, "level.dat")))
                    list.Add(new PackDest { Dir = Path.Combine(t.GameDir, level, "datapacks"), Label = "the world " + level });
            }
            else
            {
                string saves = Path.Combine(t.GameDir, "saves");
                if (Directory.Exists(saves))
                    foreach (DirectoryInfo world in new DirectoryInfo(saves).GetDirectories().Where(d => File.Exists(Path.Combine(d.FullName, "level.dat"))).OrderBy(d => d.Name))
                        list.Add(new PackDest { Dir = Path.Combine(world.FullName, "datapacks"), Label = "the world " + world.Name });
            }
            if (list.Count == 0) waiting = "no worlds yet: they go into each world once there is one (or into every world with Paxi)";
            return list;
        }

        // Folders linked to the shared pack folder resolve to the same place, so they're one key.
        static string DestKey(string dir)
        {
            string final = Directory.Exists(dir) ? FinalPath(dir) : null;
            return AlwaysKey(final ?? dir);
        }

        // Whether the folder has the pack: a file or folder of that name, or a zip with the same contents
        // (copy: that file; null for a folder pack).
        static bool PackPresent(string dir, FileInfo source, out FileInfo copy)
        {
            copy = null;
            if (!Directory.Exists(dir)) return false;
            string path = Path.Combine(dir, source.Name);
            if (File.Exists(path)) { copy = new FileInfo(path); return true; }
            if (Directory.Exists(path)) return true;
            string hash = null;
            foreach (FileInfo f in new DirectoryInfo(dir).GetFiles("*.zip"))
            {
                if (f.Length != source.Length) continue;
                if (hash == null) hash = FileSha1(source);
                if (FileSha1(f) == hash) { copy = f; return true; }
            }
            return false;
        }

        // ---------- Syncing ----------

        static void SyncSharedPacks(string onlyDir, List<string> report)
        {
            lock (AlwaysLock)
            {
                var collections = PackKinds.ToDictionary(k => k, SharedPacks);
                if (collections.Values.All(c => c.Count == 0)) return;
                AlwaysState ticks = LoadAlwaysState();
                SharedPackState state = LoadSharedPackState();
                var done = new HashSet<string>();
                foreach (AlwaysTarget t in AlwaysTargets(ticks))
                {
                    if (!t.On || (onlyDir != null && !SamePath(onlyDir, t.Dir))) continue;
                    if (t.GameDir != null && GameRunning(t.GameDir))
                    {
                        string line = t.Name + " is running, so it gets them next time.";
                        if (report != null && !report.Contains(line)) report.Add(line);
                        continue;
                    }
                    try
                    {
                        var added = new List<string>();
                        var swapped = new List<string>();
                        foreach (string kind in PackKinds)
                        {
                            List<FileInfo> collection = collections[kind];
                            if (collection.Count == 0) continue;
                            string why, waiting;
                            foreach (PackDest dest in PackDestsFor(t, kind, out why, out waiting))
                            {
                                Directory.CreateDirectory(dest.Dir);
                                string key = DestKey(dest.Dir);
                                if (!done.Add(kind + "|" + key)) continue; // a linked folder already done for another instance
                                Dictionary<string, string> placed = PlacedPacks(state, kind, key);
                                foreach (FileInfo source in collection)
                                {
                                    FileInfo copy;
                                    if (!PackPresent(dest.Dir, source, out copy))
                                    {
                                        CopyIn(source.FullName, Path.Combine(dest.Dir, source.Name));
                                        placed[source.Name] = FileSha1(source);
                                        added.Add(source.Name);
                                        continue;
                                    }
                                    string hash;
                                    if (copy == null || !string.Equals(copy.Name, source.Name, StringComparison.OrdinalIgnoreCase) || !placed.TryGetValue(source.Name, out hash)) continue;
                                    string now = FileSha1(copy);
                                    if (now != hash || now == FileSha1(source)) continue;
                                    AssertNotInUse(copy.FullName);
                                    CopyIn(source.FullName, copy.FullName);
                                    placed[source.Name] = FileSha1(source);
                                    swapped.Add(source.Name);
                                }
                            }
                        }
                        List<string> turnedOn = TurnOnSharedResourcePacks(t, collections["resourcepacks"], state);
                        if (added.Count > 0) Log("Shared    packs added to " + t.Name + ": " + string.Join(", ", added));
                        if (swapped.Count > 0) Log("Shared    packs updated in " + t.Name + ": " + string.Join(", ", swapped));
                        if (turnedOn.Count > 0) Log("Shared    resource packs turned on in " + t.Name + ": " + string.Join(", ", turnedOn));
                        if (report != null && added.Count + swapped.Count + turnedOn.Count > 0)
                            report.Add(t.Name + ": " + string.Join(", ", new[] {
                                added.Count > 0 ? Plural(added.Count, "pack", "packs") + " added" : null,
                                swapped.Count > 0 ? swapped.Count + " updated" : null,
                                turnedOn.Count > 0 ? Plural(turnedOn.Count, "resource pack", "resource packs") + " turned on" : null }.Where(x => x != null)));
                    }
                    catch (Exception ex)
                    {
                        Log("Shared    couldn't update the packs in " + t.Name + ": " + ex.Message);
                        if (report != null) report.Add(t.Name + ": " + ex.Message);
                    }
                }
                SaveSharedPackState(state);
            }
        }

        // A shared resource pack is turned on the first time an instance has it, once (so turning it off sticks).
        // A game that hasn't run yet has no options.txt; it gets the pack turned on after its first run.
        static List<string> TurnOnSharedResourcePacks(AlwaysTarget t, List<FileInfo> collection, SharedPackState state)
        {
            var turned = new List<string>();
            if (collection.Count == 0 || t.GameDir == null || IsServerDir(t.GameDir)) return turned;
            string options = Path.Combine(t.GameDir, "options.txt");
            if (!File.Exists(options)) return turned;
            string gameKey = AlwaysKey(t.GameDir);
            List<string> once;
            if (!state.TurnedOn.TryGetValue(gameKey, out once)) state.TurnedOn[gameKey] = once = new List<string>();
            string folder = Path.Combine(t.GameDir, "resourcepacks");
            List<string> list = ReadPackList(options, "resourcePacks");
            bool changed = false;
            foreach (FileInfo source in collection)
            {
                if (once.Any(n => string.Equals(n, source.Name, StringComparison.OrdinalIgnoreCase))) continue;
                if (!File.Exists(Path.Combine(folder, source.Name))) continue;
                once.Add(source.Name);
                string id = "file/" + source.Name;
                if (list.Contains(id)) continue;
                if (list.Count == 0) list.Add("vanilla");
                list.Add(id); // last is applied last, so it wins over the others
                changed = true;
                turned.Add(source.Name);
            }
            if (changed) WritePackList(options, "resourcePacks", list);
            return turned;
        }

        // ---------- Changing the collection ----------

        static Dictionary<string, object> AddSharedPackFiles(string kind)
        {
            CheckPackKind(kind);
            string[] picked = null;
            ui.Send(delegate { picked = PickFiles("Add " + KindNoun(kind) + "s shared across instances", DownloadsFolder()); }, null);
            if (picked == null) return Obj("ok", true, "added", new List<object>(), "notes", new List<string>(), "report", new List<string>());
            return AddToSharedPacks(kind, picked.ToList());
        }

        // Packs from the Packs page (this instance's folder) join the shared ones.
        static Dictionary<string, object> AddSharedPacksFromPlace(string kind, string placeId, List<string> names)
        {
            if (names.Count == 0) throw new UserError("No packs were chosen.");
            PackPlace place = FindPlace(kind, placeId);
            var paths = new List<string>();
            var notes = new List<string>();
            foreach (string name in names)
            {
                string path = ResolvePack(place, name);
                if (Directory.Exists(path)) notes.Add(name + " is a folder, so it can't be shared. Zip it first.");
                else paths.Add(path);
            }
            Dictionary<string, object> result = AddToSharedPacks(kind, paths);
            ((List<string>)result["notes"]).InsertRange(0, notes);
            return result;
        }

        static Dictionary<string, object> AddToSharedPacks(string kind, List<string> paths)
        {
            CheckPackKind(kind);
            var added = new List<object>();
            var notes = new List<string>();
            lock (AlwaysLock)
            {
                string dir = SharedPackDir(kind);
                Directory.CreateDirectory(dir);
                foreach (string path in paths)
                {
                    var file = new FileInfo(path);
                    if (!file.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) { notes.Add(file.Name + " isn't a .zip, so it wasn't added."); continue; }
                    string[] kinds = PackKindsOf(path);
                    if (kinds == null) { notes.Add(file.Name + " doesn't look like a pack, so it wasn't added."); continue; }
                    if (!kinds.Contains(kind)) { notes.Add(file.Name + " is a " + KindNoun(kinds[0]) + ", so it wasn't added here."); continue; }
                    string dest = Path.Combine(dir, file.Name);
                    bool replacing = File.Exists(dest);
                    if (replacing && FileSha1(new FileInfo(dest)) == FileSha1(file)) { notes.Add(file.Name + " is already shared."); continue; }
                    CopyIn(file.FullName, dest);
                    Log("Shared    " + (replacing ? "updated " : "added ") + file.Name + " (" + KindNoun(kind) + " for every instance)");
                    added.Add(Obj("file", file.Name, "name", PackDisplayName(file.Name), "replaced", replacing));
                }
            }
            var report = new List<string>();
            if (added.Count > 0) SyncSharedPacks(null, report);
            return Obj("ok", true, "added", added, "notes", notes, "report", report);
        }

        static string PackDisplayName(string file)
        {
            return file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? file.Substring(0, file.Length - 4) : file;
        }

        // Takes packs off the shared list (to the Recycle Bin). With everywhere, the copies the app put in
        // instances go too, as long as nobody changed them.
        static Dictionary<string, object> RemoveSharedPacks(string kind, List<string> names, bool everywhere)
        {
            CheckPackKind(kind);
            if (names.Count == 0) throw new UserError("No packs were chosen.");
            var report = new List<string>();
            lock (AlwaysLock)
            {
                List<FileInfo> collection = SharedPacks(kind);
                var gone = new List<FileInfo>();
                foreach (string n in names)
                {
                    FileInfo f = collection.FirstOrDefault(c => string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase));
                    if (f == null) throw new UserError(n + " isn't shared any more. Reopen the list.");
                    gone.Add(f);
                }
                SharedPackState state = LoadSharedPackState();
                Dictionary<string, Dictionary<string, string>> byDir;
                if (!state.Placed.TryGetValue(kind, out byDir)) byDir = new Dictionary<string, Dictionary<string, string>>();
                if (everywhere)
                {
                    var done = new HashSet<string>();
                    foreach (AlwaysTarget t in AlwaysTargets(LoadAlwaysState()))
                    {
                        string why, waiting;
                        bool running = t.GameDir != null && GameRunning(t.GameDir);
                        var removed = new List<string>();
                        foreach (PackDest dest in PackDestsFor(t, kind, out why, out waiting))
                        {
                            if (!Directory.Exists(dest.Dir)) continue;
                            string key = DestKey(dest.Dir);
                            Dictionary<string, string> placed;
                            if (!done.Add(key) || !byDir.TryGetValue(key, out placed)) continue;
                            foreach (FileInfo source in gone)
                            {
                                string hash, path = Path.Combine(dest.Dir, source.Name);
                                if (!placed.TryGetValue(source.Name, out hash) || !File.Exists(path) || FileSha1(new FileInfo(path)) != hash) continue;
                                if (running) { report.Add(t.Name + " is running, so its copy of " + source.Name + " stays."); continue; }
                                if (!RecyclePaths(new[] { path })) { report.Add(source.Name + " in " + t.Name + " couldn't be removed (it may be in use)."); continue; }
                                placed.Remove(source.Name);
                                removed.Add(source.Name);
                            }
                        }
                        if (removed.Count == 0) continue;
                        Log("Shared    removed from " + t.Name + ": " + string.Join(", ", removed) + " (moved to the Recycle Bin)");
                        report.Add(t.Name + ": " + Plural(removed.Count, "copy", "copies") + " removed");
                    }
                    foreach (List<string> once in state.TurnedOn.Values)
                        once.RemoveAll(n => gone.Any(f => string.Equals(f.Name, n, StringComparison.OrdinalIgnoreCase)));
                }
                else
                    foreach (Dictionary<string, string> placed in byDir.Values)
                        foreach (FileInfo f in gone) placed.Remove(f.Name); // the copies stay, now as the instance's own
                if (!RecyclePaths(gone.Select(f => f.FullName).ToList())) throw new UserError("Windows couldn't move everything to the Recycle Bin (a file may be in use). Reopen the list to see what's left.");
                SaveSharedPackState(state);
                Log("Shared    " + string.Join(", ", gone.Select(f => f.Name)) + " no longer shared" + (everywhere ? ", with the copies it added" : "; the copies stay where they are"));
            }
            return Obj("ok", true, "report", report, "currentChanged", 0);
        }

        // ---------- What the page shows ----------

        static Dictionary<string, object> SharedPackInfo(string kind)
        {
            CheckPackKind(kind);
            List<FileInfo> collection = SharedPacks(kind);
            var items = collection.Select(f =>
            {
                PackMeta meta = CachedPackMeta(f);
                return (object)Obj("file", f.Name, "name", PackDisplayName(f.Name), "description", meta.Description, "format", meta.Format, "icon", meta.Icon,
                    "size", f.Length, "tags", new List<string>());
            }).ToList();
            AlwaysState ticks = LoadAlwaysState();
            SharedPackState state = LoadSharedPackState();
            Dictionary<string, Dictionary<string, string>> byDir;
            if (!state.Placed.TryGetValue(kind, out byDir)) byDir = new Dictionary<string, Dictionary<string, string>>();
            string sharedFolder = Directory.Exists(Path.Combine(SharedDir(), kind)) ? DestKey(Path.Combine(SharedDir(), kind)) : null;
            var targets = AlwaysTargets(ticks).Select(t =>
            {
                string why, waiting;
                List<PackDest> dests = PackDestsFor(t, kind, out why, out waiting);
                var missing = new List<string>();
                var stale = new List<string>();
                var misfits = new List<object>();
                int present = 0;
                foreach (FileInfo source in collection)
                {
                    string name = PackDisplayName(source.Name);
                    if (why != null) { misfits.Add(Obj("name", name, "why", why)); continue; }
                    if (dests.Count == 0) continue;
                    bool everywhere = true, old = false;
                    foreach (PackDest dest in dests)
                    {
                        FileInfo copy;
                        if (!PackPresent(dest.Dir, source, out copy)) { everywhere = false; continue; }
                        Dictionary<string, string> placed;
                        string hash;
                        if (copy != null && string.Equals(copy.Name, source.Name, StringComparison.OrdinalIgnoreCase) && byDir.TryGetValue(DestKey(dest.Dir), out placed)
                            && placed.TryGetValue(source.Name, out hash) && FileSha1(copy) == hash && hash != FileSha1(source)) old = true;
                    }
                    if (!everywhere) missing.Add(name);
                    else { present++; if (old) stale.Add(name); }
                }
                string where = why != null ? null
                    : dests.Count == 0 ? waiting
                    : kind == "datapacks" && dests.Count > 1 ? "into " + dests.Count + " worlds"
                    : kind == "datapacks" ? "into " + dests[0].Label
                    : sharedFolder != null && dests.Any(d => Directory.Exists(d.Dir) && DestKey(d.Dir) == sharedFolder) ? "uses the shared " + KindNoun(kind) + " folder" : null;
                return (object)Obj("path", t.Dir, "name", t.Name, "prism", t.Prism, "on", t.On, "minecraft", t.Minecraft, "loader", t.Loader == null ? null : LoaderName(t.Loader),
                    "guessed", t.Guessed, "current", SamePath(t.Dir, modsDir), "running", t.GameDir != null && GameRunning(t.GameDir),
                    "present", present, "missing", missing, "stale", stale, "misfits", misfits, "where", where, "waiting", dests.Count == 0 && why == null);
            }).ToList();
            return Obj("ok", true, "kind", kind, "path", SharedPackDir(kind), "items", items, "targets", targets);
        }

        static HashSet<string> SharedPackNames(string kind)
        {
            try { return new HashSet<string>(SharedPacks(kind).Select(f => f.Name), StringComparer.OrdinalIgnoreCase); }
            catch { return new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
        }

        static void OpenSharedPackFolder(string kind)
        {
            CheckPackKind(kind);
            Directory.CreateDirectory(SharedPackDir(kind));
            try { Process.Start("explorer.exe", "\"" + SharedPackDir(kind) + "\""); } catch { }
        }
    }
}
