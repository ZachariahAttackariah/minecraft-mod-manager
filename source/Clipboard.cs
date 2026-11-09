// Copy, cut and paste between instances: mods from the mod list, and packs from the Packs tab.
// The clipboard lives in the app (not Windows'), so switching instances in between keeps it.
//  - Paste puts copies into the folder being looked at now: the mods folder, or the chosen pack place
//    (a data pack can go from one world to another, or to Paxi's every-world folder).
//  - A mod keeps its on/off state and its tags. A place that already has the mod (any version, on or
//    off) or a pack of that name is skipped. A mod made for another loader or Minecraft version is
//    pasted with a warning, since pasting was asked for.
//  - Cut moves: the originals go to the Recycle Bin once the copies are in, and the clipboard empties.
//    A game (or the server) that's running keeps its files, so cutting from it waits until it's closed.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MinecraftModManager
{
    static partial class Program
    {
        sealed class ClipState
        {
            public string Kind, Dir, From, SourceGame, SourceSide;
            public List<string> Names = new List<string>();
            public bool Cut;
        }

        static readonly object ClipLock = new object();
        static ClipState clip;

        static string FolderOwnerName(string dir)
        {
            string[] instance = PrismInstanceFor(dir);
            return instance != null ? instance[1] : FolderLabel(dir);
        }

        static Dictionary<string, object> ClipCopy(string kind, string placeId, List<string> files, bool cut)
        {
            if (files.Count == 0) throw new UserError("Nothing was selected.");
            var c = new ClipState { Kind = kind, Cut = cut };
            if (kind == "mods")
            {
                foreach (string f in files) ResolveModFile(modsDir, f);
                c.Dir = modsDir;
                c.From = FolderOwnerName(modsDir);
                c.SourceGame = GameDir();
                c.SourceSide = "game";
            }
            else
            {
                PackPlace place = FindPlace(kind, placeId);
                foreach (string f in files) ResolvePack(place, f);
                c.Dir = place.Dir;
                c.From = (place.Side == "server" ? "the server" : FolderOwnerName(modsDir)) + (kind == "datapacks" ? " · " + place.Label.Replace("This game: ", "").Replace("Server: ", "").Replace("the world ", "") : "");
                c.SourceGame = place.Side == "server" ? serverDir : GameDir();
                c.SourceSide = place.Side;
            }
            c.Names = files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            lock (ClipLock) clip = c;
            Log("Clipboard " + (cut ? "cut " : "copied ") + string.Join(", ", c.Names) + " from " + c.From);
            return Obj("ok", true, "clip", ClipInfo());
        }

        static void ClipClear()
        {
            lock (ClipLock) clip = null;
        }

        // For the page: what's on the clipboard, and whether it came from the mods folder open now.
        static Dictionary<string, object> ClipInfo()
        {
            ClipState c;
            lock (ClipLock) c = clip;
            if (c == null) return null;
            return Obj("kind", c.Kind, "names", c.Names, "count", c.Names.Count, "from", c.From, "cut", c.Cut, "dir", c.Dir,
                "fromHere", c.Kind == "mods" && modsDir != null && SamePath(c.Dir, modsDir));
        }

        static string ClipNoun(string kind, int n)
        {
            return kind == "mods" ? (n == 1 ? "mod" : "mods") : KindNoun(kind) + (n == 1 ? "" : "s");
        }

        static Dictionary<string, object> ClipPaste(string kind, string placeId)
        {
            ClipState c;
            lock (ClipLock) c = clip;
            if (c == null) throw new UserError("Nothing has been copied yet. Select some, then Copy or Cut (Ctrl+C / Ctrl+X).");
            if (c.Kind != kind) throw new UserError("What's copied are " + ClipNoun(c.Kind, 2) + ", so they can't be pasted among " + ClipNoun(kind, 2) + ".");
            string dest;
            string destName;
            if (kind == "mods") { dest = modsDir; destName = FolderOwnerName(modsDir); }
            else
            {
                PackPlace place = FindPlace(kind, placeId);
                dest = place.Dir;
                destName = place.Side == "server" ? "the server" : FolderOwnerName(modsDir);
            }
            if (SamePath(dest, c.Dir)) throw new UserError("They're from here already. Open another instance (or place) and paste there.");
            if (c.Cut)
            {
                if (c.SourceSide == "server" && ServerBusy()) throw new UserError("Stop the server first: it keeps its data packs open, so they can't be moved out yet.");
                if (c.SourceSide == "game" && c.SourceGame != null && GameRunning(c.SourceGame)) throw new UserError(c.From + " is running, so its files can't be moved out yet. Close it, or Copy instead.");
            }
            var pasted = new List<string>();
            var moved = new List<string>();
            var notes = new List<string>();
            Directory.CreateDirectory(dest);
            if (kind == "mods") PasteMods(c, dest, pasted, moved, notes);
            else PastePacks(c, dest, pasted, moved, notes);
            if (moved.Count > 0)
            {
                var paths = moved.Select(n => Path.Combine(c.Dir, n)).ToList();
                if (kind == "shaderpacks") paths.AddRange(moved.Select(n => Path.Combine(c.Dir, n + ".txt")).Where(File.Exists));
                if (!RecyclePaths(paths)) notes.Add("The copies are in, but Windows couldn't move every original in " + c.From + " to the Recycle Bin (a file may be in use).");
                else if (kind == "mods") RemoveTagEntries(c.Dir, moved.Select(BaseJarName).ToList());
            }
            // What was cut and moved is here now, so it leaves the clipboard; what stayed behind can still be pasted elsewhere.
            if (c.Cut && moved.Count > 0)
                lock (ClipLock)
                    if (clip == c) { c.Names = c.Names.Where(n => !moved.Contains(n)).ToList(); if (c.Names.Count == 0) clip = null; }
            if (pasted.Count > 0)
                Log("Pasted    " + string.Join(", ", pasted) + " from " + c.From + " into " + destName + " (" + dest + ")" + (c.Cut ? ", moved" : ""));
            return Obj("ok", true, "pasted", pasted, "moved", moved.Count > 0, "from", c.From, "to", destName, "notes", notes, "clip", ClipInfo());
        }

        static void PasteMods(ClipState c, string dest, List<string> pasted, List<string> moved, List<string> notes)
        {
            TagsDoc sourceTags = TagsDocOf(c.Dir);
            List<FileInfo> jars = new DirectoryInfo(dest).GetFiles().Where(f => JarName.IsMatch(f.Name)).ToList();
            bool connector = jars.Any(f => GetJarMeta(f).Mods.Concat(GetJarMeta(f).Nested).Any(m => m.Id == "connector"));
            foreach (string name in c.Names)
            {
                var source = new FileInfo(Path.Combine(c.Dir, name));
                if (!source.Exists) { notes.Add(name + " isn't in " + c.From + " any more."); continue; }
                JarMeta meta = GetJarMeta(source);
                ModInfo main = MainMod(meta);
                string title = main != null && !string.IsNullOrEmpty(main.Name) ? main.Name : name;
                List<FileInfo> same = SameModIn(jars, source);
                if (same.Count > 0) { notes.Add(title + " is already here (" + same[0].Name + "), so it was left as it is."); continue; }
                string target = Path.Combine(dest, name), other = Path.Combine(dest, name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? BaseJarName(name) : name + ".disabled");
                if (File.Exists(target) || File.Exists(other)) { notes.Add("A different mod here is already called " + BaseJarName(name) + ", so " + title + " wasn't pasted."); continue; }
                if (c.Cut) AssertNotInUse(source.FullName);
                CopyIn(source.FullName, target);
                TagAlwaysCopy(dest, BaseJarName(name), null, sourceTags);
                string why = AlwaysMisfit(meta, gameVersion, loader, connector);
                if (why != null) notes.Add(title + " was pasted, but it won't load here: " + why + ".");
                pasted.Add(name);
                if (c.Cut) moved.Add(name);
                jars.Add(new FileInfo(target));
            }
        }

        static void PastePacks(ClipState c, string dest, List<string> pasted, List<string> moved, List<string> notes)
        {
            foreach (string name in c.Names)
            {
                string source = Path.Combine(c.Dir, name), target = Path.Combine(dest, name);
                bool folder = Directory.Exists(source);
                if (!folder && !File.Exists(source)) { notes.Add(name + " isn't in " + c.From + " any more."); continue; }
                if (File.Exists(target) || Directory.Exists(target)) { notes.Add(name + " is already here, so it was left as it is."); continue; }
                if (folder) CopyTree(source, target);
                else
                {
                    if (c.Cut) AssertNotInUse(source);
                    CopyIn(source, target);
                }
                // Iris keeps a shader's own settings next to it, so they come along.
                if (c.Kind == "shaderpacks" && File.Exists(source + ".txt") && !File.Exists(target + ".txt")) File.Copy(source + ".txt", target + ".txt");
                pasted.Add(name);
                if (c.Cut) moved.Add(name);
            }
        }

        // A folder's tags, its own mod-tags.json or the one another tool keeps for it (LinkedTags.cs).
        static TagsDoc TagsDocOf(string dir)
        {
            try
            {
                if (!HasLinkedTags(dir)) return LoadTagsDoc(Path.Combine(dir, "mod-tags.json"));
                string text = LinkedTagsText(dir);
                if (text == null) return null;
                string temp = Path.Combine(Path.GetTempPath(), "mmm-linked-tags-" + Guid.NewGuid().ToString("N") + ".json");
                File.WriteAllText(temp, text, new UTF8Encoding(false));
                try { return LoadTagsDoc(temp); } finally { File.Delete(temp); }
            }
            catch { return null; }
        }

        // A moved mod's tags leave the folder it left (never in tags another tool keeps).
        static void RemoveTagEntries(string dir, List<string> names)
        {
            if (HasLinkedTags(dir)) return;
            lock (FileLock)
            {
                try
                {
                    string path = Path.Combine(dir, "mod-tags.json");
                    if (!File.Exists(path)) return;
                    TagsDoc doc = LoadTagsDoc(path);
                    int before = doc.Mods.Count;
                    foreach (string n in names) foreach (string k in doc.Mods.Keys.Where(k => string.Equals(k, n, StringComparison.OrdinalIgnoreCase)).ToList()) doc.Mods.Remove(k);
                    if (doc.Mods.Count != before) SaveTagsDoc(path, doc);
                }
                catch (UserError) { }
            }
        }
    }
}
