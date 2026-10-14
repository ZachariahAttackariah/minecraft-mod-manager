// Comparing this mods folder with another one the app knows (an instance or a recent folder): which mods
// each has, version differences, on/off differences and tag differences. Mods are matched by mod id, so
// the same mod under two file names lines up. Only reads both folders.

using System;
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
        sealed class FolderMod
        {
            public string Key, File, Id, Name, Version;
            public bool On;
            public List<string> Tags = new List<string>();
        }

        // Each folder's tags in our format: its mod-tags.json, or another tool's .mod-tags.json translated (LinkedTags.cs).
        static Dictionary<string, List<string>> FolderTags(string dir)
        {
            var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string text = HasLinkedTags(dir) ? LinkedTagsText(dir)
                    : File.Exists(Path.Combine(dir, "mod-tags.json")) ? File.ReadAllText(Path.Combine(dir, "mod-tags.json"), Encoding.UTF8) : null;
                if (text == null) return tags;
                Dictionary<string, object> mods = D(Get(new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(text) as Dictionary<string, object>, "mods"));
                if (mods != null) foreach (KeyValuePair<string, object> m in mods) tags[m.Key] = A(m.Value).OfType<string>().ToList();
            }
            catch { }
            return tags;
        }

        static List<FolderMod> FolderMods(string dir)
        {
            Dictionary<string, List<string>> tags = FolderTags(dir);
            var list = new List<FolderMod>();
            foreach (FileInfo f in new DirectoryInfo(dir).GetFiles().Where(f => JarName.IsMatch(f.Name)))
            {
                bool off = f.Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                string key = off ? f.Name.Substring(0, f.Name.Length - ".disabled".Length) : f.Name;
                ModInfo main = MainMod(GetJarMeta(f));
                List<string> t;
                list.Add(new FolderMod { Key = key, File = f.Name, Id = main != null ? main.Id : null, Name = main != null ? main.Name ?? main.Id : null,
                    Version = main != null ? main.Version : null, On = !off, Tags = tags.TryGetValue(key, out t) ? t : new List<string>() });
            }
            return list;
        }

        static Dictionary<string, object> CompareFolders(string other)
        {
            if (string.IsNullOrWhiteSpace(other)) throw new UserError("Pick a folder to compare with.");
            // Only folders the app already knows: its instances and the folders used before.
            List<string> known;
            lock (recentFolders) known = recentFolders.ToList();
            known.AddRange(PrismInstances().Select(i => i.Mods));
            if (!known.Any(k => SamePath(k, other))) throw new UserError("Pick one of the instances or folders in the list.");
            if (!Directory.Exists(other)) throw new UserError(other + " doesn't exist any more.");
            if (SamePath(other, modsDir)) throw new UserError("That's the folder you're managing. Pick another one.");

            List<FolderMod> mine = FolderMods(modsDir), theirs = FolderMods(other);
            Func<FolderMod, string> key = m => m.Id != null ? "id:" + m.Id.ToLowerInvariant() : "file:" + Regex.Replace(m.Key.ToLowerInvariant(), @"[-_+ ]?\d[\w.+-]*\.jar$", "");
            var a = mine.GroupBy(key).ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.On).First());
            var b = theirs.GroupBy(key).ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.On).First());
            var rows = new List<object>();
            int same = 0, versions = 0, onlyHere = 0, onlyThere = 0, onOff = 0, tagDiff = 0;
            foreach (string k in a.Keys.Union(b.Keys))
            {
                FolderMod x, y;
                a.TryGetValue(k, out x);
                b.TryGetValue(k, out y);
                string status;
                if (x == null) { status = "there"; onlyThere++; }
                else if (y == null) { status = "here"; onlyHere++; }
                else if (!string.Equals(x.Version, y.Version, StringComparison.OrdinalIgnoreCase) && !string.Equals(x.Key, y.Key, StringComparison.OrdinalIgnoreCase)) { status = "version"; versions++; }
                else if (x.On != y.On) { status = "onoff"; onOff++; }
                else { status = "same"; same++; }
                bool tagsDiffer = x != null && y != null && !new HashSet<string>(x.Tags).SetEquals(y.Tags);
                if (tagsDiffer) tagDiff++;
                FolderMod any = x ?? y;
                rows.Add(Obj("id", any.Id, "name", (x != null ? x.Name : null) ?? (y != null ? y.Name : null) ?? any.Key, "status", status, "tagsDiffer", tagsDiffer,
                    "here", x == null ? null : Obj("file", x.File, "key", x.Key, "version", x.Version, "on", x.On, "tags", x.Tags),
                    "there", y == null ? null : Obj("file", y.File, "key", y.Key, "version", y.Version, "on", y.On, "tags", y.Tags)));
            }
            return Obj("ok", true, "here", modsDir, "there", other, "rows", rows,
                "counts", Obj("same", same, "version", versions, "here", onlyHere, "there", onlyThere, "onoff", onOff, "tags", tagDiff));
        }
    }
}
