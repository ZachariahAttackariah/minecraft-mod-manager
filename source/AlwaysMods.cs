// Mods in every instance: one collection of jars (<data>\shared\always-mods) that the app copies into
// every mods folder it manages: each Prism instance (unless it's unticked) and each other mods folder
// that's ticked. It runs when the app starts, before Play, when an instance is opened, made or changes
// version, and whenever the collection or a tick changes.
//  - A folder that already has the mod (any version, on or off) is left alone, so turning a copy off
//    keeps it off. A copy deleted by hand comes back, since the point is that every instance has it.
//  - A jar only goes where it can load: the folder's loader and Minecraft version (from the instance,
//    or the app's own for a folder outside one).
//  - The copies the app put somewhere are remembered by hash. When the collection gets a newer jar,
//    those copies are swapped for it (staying off if they were off); a copy someone replaced or
//    updated themselves isn't touched.
//  - Tags go along: the collection keeps its own mod-tags.json, and a copy gets the same tags in the
//    folder it lands in (never in a folder whose tags belong to another tool, see LinkedTags.cs).

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
        static readonly object AlwaysLock = new object();

        static string AlwaysDir() { return Path.Combine(SharedDir(), "always-mods"); }
        static string AlwaysStatePath() { return Path.Combine(SharedDir(), "always-mods.json"); }

        sealed class AlwaysState
        {
            // Folder (full path, lower case) -> ticked. Folders not listed use the default: Prism instances on, other folders off.
            public readonly Dictionary<string, bool> Targets = new Dictionary<string, bool>();
            // Folder -> jar name (without .disabled) -> SHA-1 of the copy the app put there.
            public readonly Dictionary<string, Dictionary<string, string>> Placed = new Dictionary<string, Dictionary<string, string>>();
        }

        sealed class AlwaysTarget
        {
            public string Dir, Name, Minecraft, Loader, GameDir;
            public bool Prism, On, Guessed; // Guessed: a folder outside any instance, so the app's own version and loader are assumed
        }

        static string AlwaysKey(string dir)
        {
            try { return Path.GetFullPath(dir).TrimEnd('\\').ToLowerInvariant(); }
            catch { return dir.ToLowerInvariant(); }
        }

        static AlwaysState LoadAlwaysState()
        {
            var state = new AlwaysState();
            try
            {
                string path = AlwaysStatePath();
                if (!File.Exists(path)) return state;
                var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>;
                Dictionary<string, object> targets = D(Get(json, "targets"));
                if (targets != null) foreach (KeyValuePair<string, object> kv in targets) if (kv.Value is bool) state.Targets[kv.Key] = (bool)kv.Value;
                Dictionary<string, object> placed = D(Get(json, "placed"));
                if (placed != null)
                    foreach (KeyValuePair<string, object> kv in placed)
                    {
                        Dictionary<string, object> files = D(kv.Value);
                        if (files == null) continue;
                        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (KeyValuePair<string, object> f in files) if (f.Value is string) map[f.Key] = (string)f.Value;
                        state.Placed[kv.Key] = map;
                    }
            }
            catch { }
            return state;
        }

        static void SaveAlwaysState(AlwaysState state)
        {
            Directory.CreateDirectory(SharedDir());
            var placed = state.Placed.Where(kv => kv.Value.Count > 0)
                .ToDictionary(kv => kv.Key, kv => (object)kv.Value.ToDictionary(f => f.Key, f => (object)f.Value));
            File.WriteAllText(AlwaysStatePath(), new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(Obj("targets", state.Targets, "placed", placed)),
                new UTF8Encoding(false));
        }

        static Dictionary<string, string> PlacedIn(AlwaysState state, string dir)
        {
            string key = AlwaysKey(dir);
            Dictionary<string, string> map;
            if (!state.Placed.TryGetValue(key, out map)) state.Placed[key] = map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return map;
        }

        static List<FileInfo> AlwaysJars()
        {
            string dir = AlwaysDir();
            if (!Directory.Exists(dir)) return new List<FileInfo>();
            return new DirectoryInfo(dir).GetFiles("*.jar").OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // Every mods folder the app manages: Prism instances with a loader, then the other folders used here.
        static List<AlwaysTarget> AlwaysTargets(AlwaysState state)
        {
            var list = new List<AlwaysTarget>();
            List<InstanceInfo> prism = PrismInstances();
            foreach (InstanceInfo i in prism.Where(i => i.Loader != null))
                list.Add(new AlwaysTarget { Dir = i.Mods, Name = i.Name, Minecraft = i.Minecraft, Loader = i.Loader, GameDir = GameDirOf(i), Prism = true });
            List<string> folders;
            lock (recentFolders) folders = recentFolders.ToList();
            if (modsDir != null && !folders.Any(f => SamePath(f, modsDir))) folders.Insert(0, modsDir);
            foreach (string f in folders)
            {
                if (!Directory.Exists(f) || list.Any(t => SamePath(t.Dir, f))) continue;
                // A folder inside an instance (a second mods folder kept next to "mods") loads with that instance's game.
                string full = Path.GetFullPath(f).TrimEnd('\\') + "\\";
                InstanceInfo owner = prism.FirstOrDefault(i => full.StartsWith(Path.GetFullPath(InstanceDir(i)).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
                list.Add(owner != null && owner.Loader != null
                    ? new AlwaysTarget { Dir = f, Name = FolderLabel(f) + " (in " + owner.Name + ")", Minecraft = owner.Minecraft, Loader = owner.Loader, GameDir = GameDirOf(owner) }
                    : new AlwaysTarget { Dir = f, Name = FolderLabel(f), Minecraft = gameVersion, Loader = loaderSetting, GameDir = Path.GetDirectoryName(Path.GetFullPath(f).TrimEnd('\\')), Guessed = true });
            }
            foreach (AlwaysTarget t in list)
            {
                bool on;
                t.On = state.Targets.TryGetValue(AlwaysKey(t.Dir), out on) ? on : t.Prism;
            }
            return list;
        }

        // Why the jar can't load in that game, or null when it can.
        static string AlwaysMisfit(JarMeta meta, string mc, string ld, bool hasConnector)
        {
            if (meta.Broken) return "the jar can't be read";
            if (ld != null && meta.Kind != "none" && meta.Kind != ld)
            {
                bool ok = (ld == "quilt" && meta.Kind == "fabric")
                    || (ld == "neoforge" && meta.Kind == "forge" && mc != null && CompareVersions(mc, "1.20.5") < 0)
                    || (ld == "neoforge" && meta.Kind == "fabric" && hasConnector);
                if (!ok) return "it's a " + LoaderName(meta.Kind) + " mod";
            }
            if (!string.IsNullOrEmpty(mc))
                foreach (DepInfo dep in meta.Deps)
                {
                    if (dep.Id == null || !dep.Id.Equals("minecraft", StringComparison.OrdinalIgnoreCase) || dep.Type != "required") continue;
                    if (!InRange(mc, dep.Range) && !MinecraftHotfixFits(mc, dep.Range)) return "it's made for Minecraft " + DescribeRange(dep.Range);
                }
            return null;
        }

        static string BaseJarName(string name)
        {
            return name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - ".disabled".Length) : name;
        }

        // The jars in a folder that are the same mod as the collection's jar: the same main mod id, or the same file name.
        static List<FileInfo> SameModIn(List<FileInfo> jars, FileInfo source)
        {
            ModInfo main = MainMod(GetJarMeta(source));
            string sourceName = BaseJarName(source.Name);
            return jars.Where(f => main != null && main.Id != null
                ? GetJarMeta(f).Mods.Concat(GetJarMeta(f).Kind == "none" ? GetJarMeta(f).Nested : new List<ModInfo>()).Any(m => string.Equals(m.Id, main.Id, StringComparison.OrdinalIgnoreCase))
                : string.Equals(BaseJarName(f.Name), sourceName, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        sealed class AlwaysStatus
        {
            public readonly List<string> Missing = new List<string>();   // fits, not there: the next sync adds it
            public readonly List<string> Stale = new List<string>();     // the app's own copy, older than the collection's
            public readonly List<object> Misfits = new List<object>();   // { name, why }
            public int Present;
        }

        static AlwaysStatus StatusOf(AlwaysTarget t, List<FileInfo> collection, Dictionary<string, string> placed)
        {
            var s = new AlwaysStatus();
            List<FileInfo> jars = Directory.Exists(t.Dir) ? new DirectoryInfo(t.Dir).GetFiles().Where(f => JarName.IsMatch(f.Name)).ToList() : new List<FileInfo>();
            bool connector = jars.Any(f => GetJarMeta(f).Mods.Concat(GetJarMeta(f).Nested).Any(m => m.Id == "connector"));
            foreach (FileInfo source in collection)
            {
                ModInfo main = MainMod(GetJarMeta(source));
                string name = main != null && !string.IsNullOrEmpty(main.Name) ? main.Name : source.Name;
                List<FileInfo> same = SameModIn(jars, source);
                if (same.Count > 0)
                {
                    s.Present++;
                    if (OwnStaleCopy(same, source, placed) != null) s.Stale.Add(name);
                    continue;
                }
                string why = AlwaysMisfit(GetJarMeta(source), t.Minecraft, t.Loader, connector);
                if (why != null) s.Misfits.Add(Obj("name", name, "why", why));
                else s.Missing.Add(name);
            }
            return s;
        }

        // The app's own, unchanged copy of an older collection jar, when that's the folder's only copy of the mod.
        static FileInfo OwnStaleCopy(List<FileInfo> same, FileInfo source, Dictionary<string, string> placed)
        {
            if (same.Count != 1) return null;
            FileInfo copy = same[0];
            string hash;
            if (!placed.TryGetValue(BaseJarName(copy.Name), out hash)) return null;
            string now = FileSha1(copy);
            return now == hash && now != FileSha1(source) ? copy : null;
        }

        // ---------- Syncing ----------

        // Brings every ticked folder (or just the one given) up to date with the collection. Returns how many
        // jars were added or swapped in the folder being managed right now, so the page knows to refresh.
        static int SyncAlwaysMods(string onlyDir, List<string> report)
        {
            lock (AlwaysLock)
            {
                List<FileInfo> collection = AlwaysJars();
                if (collection.Count == 0) return 0;
                AlwaysState state = LoadAlwaysState();
                TagsDoc colTags = null;
                try { colTags = LoadTagsDoc(Path.Combine(AlwaysDir(), "mod-tags.json")); } catch { }
                int currentChanged = 0;
                foreach (AlwaysTarget t in AlwaysTargets(state))
                {
                    if (!t.On || (onlyDir != null && !SamePath(onlyDir, t.Dir))) continue;
                    if (t.GameDir != null && GameRunning(t.GameDir)) { if (report != null) report.Add(t.Name + " is running, so it gets them next time."); continue; }
                    try
                    {
                        int n = SyncAlwaysInto(t, collection, PlacedIn(state, t.Dir), colTags, report);
                        if (n > 0 && SamePath(t.Dir, modsDir)) currentChanged += n;
                    }
                    catch (Exception ex)
                    {
                        Log("Always    couldn't update " + t.Name + ": " + ex.Message);
                        if (report != null) report.Add(t.Name + ": " + ex.Message);
                    }
                }
                SaveAlwaysState(state);
                return currentChanged;
            }
        }

        static int SyncAlwaysInto(AlwaysTarget t, List<FileInfo> collection, Dictionary<string, string> placed, TagsDoc colTags, List<string> report)
        {
            Directory.CreateDirectory(t.Dir); // a new instance has no mods folder until its first launch
            List<FileInfo> jars = new DirectoryInfo(t.Dir).GetFiles().Where(f => JarName.IsMatch(f.Name)).ToList();
            bool connector = jars.Any(f => GetJarMeta(f).Mods.Concat(GetJarMeta(f).Nested).Any(m => m.Id == "connector"));
            var added = new List<string>();
            var swapped = new List<string>();
            foreach (FileInfo source in collection)
            {
                List<FileInfo> same = SameModIn(jars, source);
                if (same.Count == 0)
                {
                    if (AlwaysMisfit(GetJarMeta(source), t.Minecraft, t.Loader, connector) != null) continue;
                    string dest = Path.Combine(t.Dir, source.Name);
                    if (File.Exists(dest) || File.Exists(dest + ".disabled")) continue; // a different mod with that file name
                    CopyIn(source.FullName, dest);
                    placed[source.Name] = FileSha1(source);
                    TagAlwaysCopy(t.Dir, source.Name, null, colTags);
                    added.Add(source.Name);
                    continue;
                }
                FileInfo old = OwnStaleCopy(same, source, placed);
                if (old == null) continue;
                bool off = old.Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                string target = Path.Combine(t.Dir, source.Name + (off ? ".disabled" : ""));
                if (!string.Equals(old.FullName, target, StringComparison.OrdinalIgnoreCase) && File.Exists(target)) continue;
                AssertNotInUse(old.FullName);
                string oldBase = BaseJarName(old.Name);
                if (string.Equals(old.FullName, target, StringComparison.OrdinalIgnoreCase)) CopyIn(source.FullName, target);
                else
                {
                    CopyIn(source.FullName, target);
                    if (!RecyclePaths(new[] { old.FullName })) File.Delete(old.FullName);
                }
                placed.Remove(oldBase);
                placed[source.Name] = FileSha1(source);
                if (!string.Equals(oldBase, source.Name, StringComparison.OrdinalIgnoreCase)) TagAlwaysCopy(t.Dir, source.Name, oldBase, colTags);
                swapped.Add(oldBase + " -> " + source.Name + (off ? " (still off)" : ""));
            }
            if (added.Count > 0) Log("Always    added to " + t.Name + ": " + string.Join(", ", added));
            if (swapped.Count > 0) Log("Always    updated in " + t.Name + ": " + string.Join(", ", swapped));
            if (report != null && added.Count + swapped.Count > 0)
                report.Add(t.Name + ": " + string.Join(", ", new[] { added.Count > 0 ? Plural(added.Count, "added", "added") : null, swapped.Count > 0 ? swapped.Count + " updated" : null }.Where(x => x != null)));
            return added.Count + swapped.Count;
        }

        // Copied under a temporary name first, so a half-copied jar never sits in a mods folder.
        static void CopyIn(string from, string to)
        {
            string temp = to + ".copying";
            File.Copy(from, temp, true);
            if (File.Exists(to)) File.Delete(to);
            File.Move(temp, to);
        }

        // The copy gets the collection's tags for that jar (the tag's description too, when the folder doesn't
        // have the tag yet); a swapped-in version keeps the old copy's tags.
        static void TagAlwaysCopy(string dir, string name, string oldName, TagsDoc colTags)
        {
            if (HasLinkedTags(dir)) return;
            lock (FileLock)
            {
                string path = Path.Combine(dir, "mod-tags.json");
                TagsDoc doc;
                try { doc = LoadTagsDoc(path); } catch (UserError) { return; }
                var tags = new List<string>();
                bool changed = false;
                if (oldName != null)
                {
                    string oldKey = doc.Mods.Keys.FirstOrDefault(k => string.Equals(k, oldName, StringComparison.OrdinalIgnoreCase));
                    if (oldKey != null) { tags.AddRange(doc.Mods[oldKey]); doc.Mods.Remove(oldKey); changed = true; }
                }
                string colKey = colTags == null ? null : colTags.Mods.Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
                if (colKey != null)
                    foreach (string tag in colTags.Mods[colKey])
                    {
                        if (!tags.Contains(tag)) tags.Add(tag);
                        if (!doc.Tags.Any(d => d.Key == tag))
                            doc.Tags.Add(new KeyValuePair<string, string>(tag, colTags.Tags.Where(d => d.Key == tag).Select(d => d.Value).FirstOrDefault() ?? ""));
                    }
                string key = doc.Mods.Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) ?? name;
                List<string> have;
                if (doc.Mods.TryGetValue(key, out have)) foreach (string tag in have) if (!tags.Contains(tag)) tags.Add(tag);
                if (tags.Count == 0 && !changed) return;
                var order = doc.Tags.Select(d => d.Key).ToList();
                doc.Mods[key] = tags.OrderBy(x => order.IndexOf(x) < 0 ? int.MaxValue : order.IndexOf(x)).ToList();
                SaveTagsDoc(path, doc);
            }
        }

        static void TrySyncAlwaysMods(string onlyDir)
        {
            try { SyncAlwaysMods(onlyDir, null); }
            catch (Exception ex) { Log("Always    couldn't add the mods every instance gets: " + ex.Message); }
        }

        // ---------- Changing the collection ----------

        // Mods from the list being managed join the collection (their tags too). One the collection already
        // has (the same mod) is replaced, which updates the copies the app put elsewhere.
        static Dictionary<string, object> AddAlwaysFromList(List<string> files)
        {
            if (files.Count == 0) throw new UserError("No mods were selected.");
            var paths = files.Select(f => ResolveModFile(modsDir, f)).ToList();
            TagsDoc tags = null;
            if (!HasLinkedTags(modsDir)) try { tags = LoadTagsDoc(TagsPath()); } catch (UserError) { }
            else
            {
                try
                {
                    string text = LinkedTagsText(modsDir);
                    if (text != null)
                    {
                        string temp = Path.Combine(Path.GetTempPath(), "mmm-linked-tags-" + Guid.NewGuid().ToString("N") + ".json");
                        File.WriteAllText(temp, text, new UTF8Encoding(false));
                        try { tags = LoadTagsDoc(temp); } finally { File.Delete(temp); }
                    }
                }
                catch { }
            }
            return AddToAlways(paths, tags);
        }

        static Dictionary<string, object> AddAlwaysFiles()
        {
            string[] picked = null;
            ui.Send(delegate { picked = PickFiles("Add mods every instance gets", DownloadsFolder(), "Mod files (*.jar)|*.jar"); }, null);
            if (picked == null) return Obj("ok", true, "added", new List<object>(), "notes", new List<string>(), "report", new List<string>(), "currentChanged", 0);
            return AddToAlways(picked.ToList(), null);
        }

        static Dictionary<string, object> AddToAlways(List<string> paths, TagsDoc sourceTags)
        {
            var added = new List<object>();
            var notes = new List<string>();
            lock (AlwaysLock)
            {
                string dir = AlwaysDir();
                Directory.CreateDirectory(dir);
                AlwaysState state = LoadAlwaysState();
                string tagsPath = Path.Combine(dir, "mod-tags.json");
                TagsDoc colTags = LoadTagsDoc(tagsPath);
                bool tagsChanged = false;
                foreach (string path in paths)
                {
                    var file = new FileInfo(path);
                    string name = BaseJarName(file.Name);
                    if (!name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) { notes.Add(file.Name + " isn't a .jar, so it wasn't added."); continue; }
                    JarMeta meta = GetJarMeta(file);
                    if (meta.Broken) { notes.Add(file.Name + " can't be read as a jar, so it wasn't added."); continue; }
                    ModInfo main = MainMod(meta);
                    List<FileInfo> same = SameModIn(AlwaysJars(), new FileInfo(path));
                    string dest = Path.Combine(dir, name);
                    if (same.Any(f => FileSha1(f) == FileSha1(file))) { notes.Add((main != null ? main.Name : name) + " is already in the list."); continue; }
                    CopyIn(file.FullName, dest);
                    foreach (FileInfo old in same.Where(f => !string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (!RecyclePaths(new[] { old.FullName })) File.Delete(old.FullName);
                        string oldKey = colTags.Mods.Keys.FirstOrDefault(k => string.Equals(k, old.Name, StringComparison.OrdinalIgnoreCase));
                        if (oldKey != null) { List<string> carried = colTags.Mods[oldKey]; colTags.Mods.Remove(oldKey); MergeTagList(colTags, name, carried, null); tagsChanged = true; }
                    }
                    if (sourceTags != null)
                    {
                        string key = sourceTags.Mods.Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
                        if (key != null && sourceTags.Mods[key].Count > 0) { MergeTagList(colTags, name, sourceTags.Mods[key], sourceTags); tagsChanged = true; }
                    }
                    Log("Always    " + (same.Count > 0 ? "updated " : "added ") + name + " (every instance gets it)");
                    added.Add(Obj("file", name, "name", main != null ? main.Name : name, "replaced", same.Count > 0));
                }
                if (tagsChanged) SaveTagsDoc(tagsPath, colTags);
                SaveAlwaysState(state);
            }
            var report = new List<string>();
            int current = added.Count > 0 ? SyncAlwaysMods(null, report) : 0;
            return Obj("ok", true, "added", added, "notes", notes, "report", report, "currentChanged", current);
        }

        static void MergeTagList(TagsDoc doc, string name, List<string> tags, TagsDoc defsFrom)
        {
            string key = doc.Mods.Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) ?? name;
            List<string> have;
            if (!doc.Mods.TryGetValue(key, out have)) doc.Mods[key] = have = new List<string>();
            foreach (string tag in tags)
            {
                if (!have.Contains(tag)) have.Add(tag);
                if (!doc.Tags.Any(d => d.Key == tag))
                    doc.Tags.Add(new KeyValuePair<string, string>(tag, defsFrom == null ? "" : defsFrom.Tags.Where(d => d.Key == tag).Select(d => d.Value).FirstOrDefault() ?? ""));
            }
        }

        // Takes jars out of the collection (to the Recycle Bin). With everywhere, the copies the app put in
        // folders go too, as long as nobody changed them (a turned-off copy counts as unchanged).
        static Dictionary<string, object> RemoveAlways(List<string> names, bool everywhere)
        {
            if (names.Count == 0) throw new UserError("No mods were chosen.");
            var report = new List<string>();
            int current = 0;
            lock (AlwaysLock)
            {
                List<FileInfo> collection = AlwaysJars();
                var gone = new List<FileInfo>();
                foreach (string n in names)
                {
                    FileInfo f = collection.FirstOrDefault(c => string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase));
                    if (f == null) throw new UserError(n + " isn't in the list any more. Reopen it.");
                    gone.Add(f);
                }
                AlwaysState state = LoadAlwaysState();
                if (everywhere)
                {
                    foreach (AlwaysTarget t in AlwaysTargets(state))
                    {
                        Dictionary<string, string> placed;
                        if (!state.Placed.TryGetValue(AlwaysKey(t.Dir), out placed) || !Directory.Exists(t.Dir)) continue;
                        bool running = t.GameDir != null && GameRunning(t.GameDir);
                        var removed = new List<string>();
                        foreach (FileInfo source in gone)
                        {
                            List<FileInfo> same = SameModIn(new DirectoryInfo(t.Dir).GetFiles().Where(x => JarName.IsMatch(x.Name)).ToList(), source);
                            foreach (FileInfo copy in same)
                            {
                                string hash;
                                if (!placed.TryGetValue(BaseJarName(copy.Name), out hash) || FileSha1(copy) != hash) continue;
                                if (running) { report.Add(t.Name + " is running, so its copy of " + source.Name + " stays."); continue; }
                                if (!RecyclePaths(new[] { copy.FullName })) { report.Add(copy.Name + " in " + t.Name + " couldn't be removed (it may be in use)."); continue; }
                                placed.Remove(BaseJarName(copy.Name));
                                removed.Add(copy.Name);
                            }
                        }
                        if (removed.Count == 0) continue;
                        Log("Always    removed from " + t.Name + ": " + string.Join(", ", removed) + " (moved to the Recycle Bin)");
                        report.Add(t.Name + ": " + Plural(removed.Count, "copy", "copies") + " removed");
                        if (SamePath(t.Dir, modsDir)) current += removed.Count;
                    }
                }
                foreach (FileInfo f in gone)
                    foreach (Dictionary<string, string> placed in state.Placed.Values)
                        if (!everywhere) placed.Remove(f.Name); // the copies stay, now as the folder's own
                if (!RecyclePaths(gone.Select(f => f.FullName).ToList())) throw new UserError("Windows couldn't move everything to the Recycle Bin (a file may be in use). Reopen the list to see what's left.");
                try
                {
                    string tagsPath = Path.Combine(AlwaysDir(), "mod-tags.json");
                    TagsDoc colTags = LoadTagsDoc(tagsPath);
                    int before = colTags.Mods.Count;
                    foreach (FileInfo f in gone) foreach (string k in colTags.Mods.Keys.Where(k => string.Equals(k, f.Name, StringComparison.OrdinalIgnoreCase)).ToList()) colTags.Mods.Remove(k);
                    if (colTags.Mods.Count != before) SaveTagsDoc(tagsPath, colTags);
                }
                catch (UserError) { }
                SaveAlwaysState(state);
                Log("Always    " + string.Join(", ", gone.Select(f => f.Name)) + " taken off the list" + (everywhere ? ", with the copies it added" : "; the copies stay where they are"));
            }
            return Obj("ok", true, "report", report, "currentChanged", current);
        }

        static Dictionary<string, object> SetAlwaysTarget(string path, bool on)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new UserError("No folder was chosen.");
            var report = new List<string>();
            lock (AlwaysLock)
            {
                AlwaysState state = LoadAlwaysState();
                AlwaysTarget t = AlwaysTargets(state).FirstOrDefault(x => SamePath(x.Dir, path));
                if (t == null) throw new UserError("That folder isn't one the app manages.");
                state.Targets[AlwaysKey(t.Dir)] = on;
                SaveAlwaysState(state);
                Log("Always    " + t.Name + (on ? " gets the mods every instance gets" : " no longer gets the mods every instance gets"));
            }
            int current = on ? SyncAlwaysMods(path, report) : 0;
            return Obj("ok", true, "report", report, "currentChanged", current);
        }

        // ---------- What the page shows ----------

        static Dictionary<string, object> AlwaysInfo()
        {
            List<FileInfo> collection = AlwaysJars();
            TagsDoc colTags = null;
            try { colTags = LoadTagsDoc(Path.Combine(AlwaysDir(), "mod-tags.json")); } catch { }
            var mods = collection.Select(f =>
            {
                JarMeta meta = GetJarMeta(f);
                ModInfo main = MainMod(meta);
                DepInfo mc = meta.Deps.FirstOrDefault(d => d.Id != null && d.Id.Equals("minecraft", StringComparison.OrdinalIgnoreCase) && d.Type == "required");
                string key = colTags == null ? null : colTags.Mods.Keys.FirstOrDefault(k => string.Equals(k, f.Name, StringComparison.OrdinalIgnoreCase));
                return (object)Obj("file", f.Name, "name", main != null && !string.IsNullOrEmpty(main.Name) ? main.Name : f.Name, "modId", main == null ? null : main.Id,
                    "version", main != null && main.Version != null && !main.Version.Contains("${") ? main.Version : null,
                    "loader", meta.Kind == "none" ? null : LoaderName(meta.Kind), "minecraft", mc == null ? null : DescribeRange(mc.Range), "size", f.Length,
                    "tags", key == null ? new List<string>() : colTags.Mods[key]);
            }).ToList();
            AlwaysState state = LoadAlwaysState();
            var targets = AlwaysTargets(state).Select(t =>
            {
                Dictionary<string, string> placed;
                if (!state.Placed.TryGetValue(AlwaysKey(t.Dir), out placed)) placed = new Dictionary<string, string>();
                AlwaysStatus s = StatusOf(t, collection, placed);
                return (object)Obj("path", t.Dir, "name", t.Name, "prism", t.Prism, "on", t.On, "minecraft", t.Minecraft, "loader", t.Loader == null ? null : LoaderName(t.Loader),
                    "guessed", t.Guessed, "current", SamePath(t.Dir, modsDir), "running", t.GameDir != null && GameRunning(t.GameDir), "linkedTags", HasLinkedTags(t.Dir),
                    "present", s.Present, "missing", s.Missing, "stale", s.Stale, "misfits", s.Misfits);
            }).ToList();
            return Obj("ok", true, "path", AlwaysDir(), "mods", mods, "targets", targets);
        }

        // Mod ids and file names in the collection, so the mod list can mark the mods every instance gets.
        static Dictionary<string, object> AlwaysMarks()
        {
            var ids = new List<string>();
            var files = new List<string>();
            try
            {
                foreach (FileInfo f in AlwaysJars())
                {
                    ModInfo main = MainMod(GetJarMeta(f));
                    if (main != null && main.Id != null) ids.Add(main.Id.ToLowerInvariant()); else files.Add(f.Name.ToLowerInvariant());
                }
            }
            catch { }
            return Obj("ids", ids, "files", files);
        }

        static void OpenAlwaysFolder()
        {
            Directory.CreateDirectory(AlwaysDir());
            try { Process.Start("explorer.exe", "\"" + AlwaysDir() + "\""); } catch { }
        }
    }
}
