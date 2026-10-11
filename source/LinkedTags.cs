// Tags kept by another tool in the same mods folder: a ".mod-tags.json" with the same layout as ours,
// but keyed by mod id (or by jar name), with free-form tag names ("To Do", "ars nouveau"). Whenever that
// file changes, its tags are copied into mod-tags.json: matched to jars by mod id, tag names made
// valid ("to-do", "ars-nouveau"). For the mods it lists, its tags win; other mods keep theirs. The
// other file is only ever read. A watcher tells the page as soon as it changes.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace MinecraftModManager
{
    static partial class Program
    {
        const string LinkedTagsFile = ".mod-tags.json";
        static FileSystemWatcher linkedWatcher;
        static string linkedWatchDir;

        // Lowercase letters, numbers, - and _, as tag names must be.
        static string ValidTagName(string name)
        {
            string n = Regex.Replace((name ?? "").Trim().ToLowerInvariant(), "[^a-z0-9_-]+", "-").Trim('-');
            if (n.Length > 32) n = n.Substring(0, 32).TrimEnd('-');
            return TagName.IsMatch(n) ? n : null;
        }

        // Called with every state read (the page asks every 15 seconds, and at once when the watcher fires).
        static void ImportLinkedTags(string dir)
        {
            WatchLinkedTags(dir);
            string source = Path.Combine(dir, LinkedTagsFile);
            if (!File.Exists(source)) return;
            var info = new FileInfo(source);
            string stamp = info.LastWriteTimeUtc.Ticks + "|" + info.Length;

            lock (FileLock)
            {
                string path = Path.Combine(dir, "mod-tags.json");
                TagsDoc doc = LoadTagsDoc(path);
                int mark = doc.Extra.FindIndex(e => e.Key == "importedFrom");
                Dictionary<string, object> last = mark >= 0 ? D(doc.Extra[mark].Value) : null;
                if (last != null && Str(last, "stamp") == stamp) return;

                Dictionary<string, object> theirs;
                try { theirs = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(File.ReadAllText(source)) as Dictionary<string, object>; }
                catch { return; } // mid-write or not JSON: try again next time
                Dictionary<string, object> mods = D(Get(theirs, "mods"));
                if (mods == null) return;

                // Their tag order first, so new tags land in the same order as in their list.
                var order = doc.Tags.Select(t => t.Key).ToList();
                foreach (string t in (D(Get(theirs, "tags")) ?? new Dictionary<string, object>()).Keys.Select(ValidTagName).Where(t => t != null))
                    if (!order.Contains(t)) { order.Add(t); doc.Tags.Add(new KeyValuePair<string, string>(t, "")); }

                var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string k in mods.Keys) keys[k] = k;
                int changed = 0;
                foreach (FileInfo jar in new DirectoryInfo(dir).GetFiles().Where(f => JarName.IsMatch(f.Name)))
                {
                    string jarKey = NormalizeKey(jar.Name);
                    // Keyed by jar name, or by any mod id the jar declares.
                    string key;
                    if (!keys.TryGetValue(jarKey, out key))
                        key = GetJarMeta(jar).Mods.Select(m => m.Id).Where(id => keys.ContainsKey(id)).Select(id => keys[id]).FirstOrDefault();
                    if (key == null) continue;
                    var tags = new List<string>();
                    var items = Get(mods, key) as IEnumerable;
                    if (items != null && !(Get(mods, key) is string))
                        foreach (object item in items)
                        {
                            string t = ValidTagName(item as string);
                            if (t == null || tags.Contains(t)) continue;
                            if (!order.Contains(t)) { order.Add(t); doc.Tags.Add(new KeyValuePair<string, string>(t, "")); }
                            tags.Add(t);
                        }
                    tags = tags.OrderBy(t => order.IndexOf(t)).ToList();
                    string existing = doc.Mods.Keys.FirstOrDefault(k => string.Equals(k, jarKey, StringComparison.OrdinalIgnoreCase));
                    if (existing != null && doc.Mods[existing].SequenceEqual(tags)) continue;
                    if (existing != null) doc.Mods.Remove(existing);
                    doc.Mods[jarKey] = tags;
                    changed++;
                }

                var stampObj = Obj("file", LinkedTagsFile, "stamp", stamp);
                if (mark >= 0) doc.Extra[mark] = new KeyValuePair<string, object>("importedFrom", stampObj);
                else doc.Extra.Add(new KeyValuePair<string, object>("importedFrom", stampObj));
                SaveTagsDoc(path, doc);
                if (changed > 0) Log("Tags      " + changed + (changed == 1 ? " mod" : " mods") + " updated from " + LinkedTagsFile);
            }
        }

        // One watcher, on whichever folder is being managed.
        static void WatchLinkedTags(string dir)
        {
            lock (FileLock) WatchLinkedTagsLocked(dir);
        }

        static void WatchLinkedTagsLocked(string dir)
        {
            if (string.Equals(linkedWatchDir, dir, StringComparison.OrdinalIgnoreCase)) return;
            linkedWatchDir = dir;
            if (linkedWatcher != null) { linkedWatcher.Dispose(); linkedWatcher = null; }
            try
            {
                var w = new FileSystemWatcher(dir, LinkedTagsFile) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
                FileSystemEventHandler changed = delegate { NotifyPage("refresh"); };
                w.Changed += changed;
                w.Created += changed;
                w.Renamed += delegate { NotifyPage("refresh"); };
                w.EnableRaisingEvents = true;
                linkedWatcher = w;
            }
            catch { } // the 15-second refresh still picks changes up
        }
    }
}
