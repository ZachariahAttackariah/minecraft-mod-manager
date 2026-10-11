// Tags kept by another tool in the same mods folder: a ".mod-tags.json" with the same layout as ours,
// but keyed by mod id (or by jar name), with free-form tag names ("To Do", "ars nouveau"). The page is
// shown a translation of it, made in memory every time the file changes: matched to jars by mod id,
// tag names made valid ("to-do", "ars-nouveau"). Nothing is ever written in such a folder: the other
// tool merges any mod-tags.json it finds into its own file (and deletes it), so writing one there
// fed our renamed tags back into its list. Tags there are read-only here, and a watcher refreshes the
// page as soon as the other tool saves.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace MinecraftModManager
{
    static partial class Program
    {
        const string LinkedTagsFile = ".mod-tags.json";
        static FileSystemWatcher linkedWatcher;
        static string linkedWatchDir;
        static string linkedStamp, linkedText; // last translation, by the other file's time and size

        static bool HasLinkedTags(string dir)
        {
            return dir != null && File.Exists(Path.Combine(dir, LinkedTagsFile));
        }

        // Tag changes would create a mod-tags.json for the other tool to swallow, so they're refused.
        static void RequireOwnTags(string dir)
        {
            if (HasLinkedTags(dir))
                throw new UserError("This folder's tags come from " + LinkedTagsFile + " (another launcher's tag file), so they can only be changed there.");
        }

        // Lowercase letters, numbers, - and _, as tag names must be.
        static string ValidTagName(string name)
        {
            string n = Regex.Replace((name ?? "").Trim().ToLowerInvariant(), "[^a-z0-9_-]+", "-").Trim('-');
            if (n.Length > 32) n = n.Substring(0, 32).TrimEnd('-');
            return TagName.IsMatch(n) ? n : null;
        }

        // The other file as mod-tags.json text (keys = jar names), or null when there's none or it can't be read.
        static string LinkedTagsText(string dir)
        {
            WatchLinkedTags(dir);
            string source = Path.Combine(dir, LinkedTagsFile);
            if (!File.Exists(source)) return null;
            var info = new FileInfo(source);
            string stamp = dir + "|" + info.LastWriteTimeUtc.Ticks + "|" + info.Length;
            lock (FileLock) if (stamp == linkedStamp) return linkedText;

            Dictionary<string, object> theirs;
            try { theirs = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(File.ReadAllText(source)) as Dictionary<string, object>; }
            catch { lock (FileLock) return linkedText; } // mid-write: keep showing the last good copy
            Dictionary<string, object> mods = D(Get(theirs, "mods"));
            if (mods == null) return null;

            var doc = new TagsDoc();
            var order = new List<string>();
            Action<string> addTag = t => { if (!order.Contains(t)) { order.Add(t); doc.Tags.Add(new KeyValuePair<string, string>(t, "")); } };
            foreach (string t in (D(Get(theirs, "tags")) ?? new Dictionary<string, object>()).Keys.Select(ValidTagName).Where(t => t != null)) addTag(t);

            var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string k in mods.Keys) keys[k] = k;
            foreach (FileInfo jar in new DirectoryInfo(dir).GetFiles().Where(f => JarName.IsMatch(f.Name)))
            {
                string jarKey = NormalizeKey(jar.Name);
                string key; // keyed by jar name, or by any mod id the jar declares
                if (!keys.TryGetValue(jarKey, out key))
                    key = GetJarMeta(jar).Mods.Select(m => m.Id).Where(id => keys.ContainsKey(id)).Select(id => keys[id]).FirstOrDefault();
                if (key == null) continue;
                var tags = new List<string>();
                object value = Get(mods, key);
                if (value is IEnumerable && !(value is string))
                    foreach (object item in (IEnumerable)value)
                    {
                        string t = ValidTagName(item as string);
                        if (t == null || tags.Contains(t)) continue;
                        addTag(t);
                        tags.Add(t);
                    }
                doc.Mods[jarKey] = tags.OrderBy(t => order.IndexOf(t)).ToList();
            }
            doc.Extra.Add(new KeyValuePair<string, object>("linkedFrom", LinkedTagsFile));
            string text = TagsDocText(doc);
            lock (FileLock) { linkedStamp = stamp; linkedText = text; }
            return text;
        }

        // One watcher, on whichever folder is being managed.
        static void WatchLinkedTags(string dir)
        {
            lock (FileLock)
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
}
