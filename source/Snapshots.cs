// Snapshots of the mods folder and "find a problem mod".
//
// A snapshot records every jar in the folder (name, SHA-1, on or off). The jars themselves are
// copied once into a shared store (snapshots\jars\<sha1>.jar, in the app's data folder), so
// restoring can bring back jars that were deleted or updated since, and ten snapshots of the same
// pack cost little more than one. Restoring first takes an automatic snapshot of how things are now.
//
// Finding a problem mod is a guided halving: each round turns off half of the suspects (but keeps
// on any library a still-enabled mod needs), the person plays and says whether the problem is still
// there, and the suspects shrink to the half that must contain it. About log2(n) rounds; at the end
// the folder goes back to the snapshot taken when the search started.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace MinecraftModManager
{
    public sealed class SnapJar
    {
        public string file;   // jar name without .disabled
        public string sha1;
        public bool enabled;
        public long size;
    }

    public sealed class Snapshot
    {
        public string id, name, created;
        public bool auto;
        public List<SnapJar> jars = new List<SnapJar>();
    }

    public sealed class BisectRound
    {
        public int round, suspects, off;
        public string answer;
    }

    public sealed class BisectState
    {
        public string started, snapshotId, status, note; // status: testing | done
        public int round, startCount;
        public List<string> suspects = new List<string>();
        public List<string> testingOff = new List<string>();
        public List<string> result = new List<string>();
        public List<BisectRound> history = new List<BisectRound>();
    }

    public sealed class SnapshotStore
    {
        public string folder;
        public List<Snapshot> snapshots = new List<Snapshot>();
        public BisectState bisect;
    }

    static partial class Program
    {
        const int KeepAutoSnapshots = 10;

        static string SnapshotDir { get { return Path.Combine(dataDir, "snapshots"); } }
        static string JarStoreDir { get { return Path.Combine(dataDir, "snapshots", "jars"); } }

        static string SnapshotFile(string dir)
        {
            string full = Path.GetFullPath(dir).TrimEnd('\\').ToLowerInvariant();
            string hash;
            using (SHA1 sha = SHA1.Create()) hash = Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(full))).Substring(0, 12);
            string leaf = Regex.Replace(Path.GetFileName(Path.GetDirectoryName(full)) ?? "mods", @"[^a-z0-9]+", "-").Trim('-');
            return Path.Combine(SnapshotDir, (leaf.Length > 0 ? leaf + "-" : "") + hash + ".json");
        }

        static SnapshotStore LoadStore(string dir)
        {
            string path = SnapshotFile(dir);
            if (!File.Exists(path)) return new SnapshotStore { folder = dir };
            var serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            try { return serializer.Deserialize<SnapshotStore>(File.ReadAllText(path, Encoding.UTF8)) ?? new SnapshotStore { folder = dir }; }
            catch (Exception) { throw new UserError("The snapshot list at " + path + " couldn't be read."); }
        }

        static void SaveStore(string dir, SnapshotStore store)
        {
            string path = SnapshotFile(dir);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            store.folder = dir;
            var serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            string temp = path + ".tmp";
            File.WriteAllText(temp, serializer.Serialize(store), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }

        static List<FileInfo> FolderJars(string dir)
        {
            return new DirectoryInfo(dir).GetFiles().Where(f => JarName.IsMatch(f.Name)).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        static string KeyOf(string name) { return Regex.Replace(name, @"\.disabled$", "", RegexOptions.IgnoreCase); }
        static bool IsOff(string name) { return name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase); }

        static Snapshot TakeSnapshot(string dir, SnapshotStore store, string name, bool auto)
        {
            Directory.CreateDirectory(JarStoreDir);
            var snap = new Snapshot { id = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"), name = name, created = DateTime.UtcNow.ToString("o"), auto = auto };
            foreach (FileInfo f in FolderJars(dir))
            {
                string sha = FileSha1(f);
                string stored = Path.Combine(JarStoreDir, sha + ".jar");
                if (!File.Exists(stored))
                {
                    File.Copy(f.FullName, stored + ".tmp", true);
                    File.Move(stored + ".tmp", stored);
                }
                snap.jars.Add(new SnapJar { file = KeyOf(f.Name), sha1 = sha, enabled = !IsOff(f.Name), size = f.Length });
            }
            store.snapshots.Add(snap);
            List<Snapshot> autos = store.snapshots.Where(s => s.auto && (store.bisect == null || s.id != store.bisect.snapshotId)).ToList();
            foreach (Snapshot old in autos.Take(Math.Max(0, autos.Count - KeepAutoSnapshots))) store.snapshots.Remove(old);
            SaveStore(dir, store);
            PruneJarStore();
            Log("Snapshot  " + (auto ? "auto: " : "") + name + " (" + snap.jars.Count + " jars)");
            return snap;
        }

        // Called before bulk changes (updating several mods, restoring). Never fails the change itself.
        static void AutoSnapshot(string dir, string name)
        {
            try
            {
                lock (FileLock)
                {
                    SnapshotStore store = LoadStore(dir);
                    TakeSnapshot(dir, store, name, true);
                }
            }
            catch (Exception ex)
            {
                Log("Snapshot  couldn't take the automatic snapshot \"" + name + "\": " + ex.Message);
            }
        }

        // Deletes stored jar copies that no snapshot (of any folder) refers to any more. These are the
        // app's own copies; the jars in mods folders are never touched here.
        static void PruneJarStore()
        {
            try
            {
                if (!Directory.Exists(JarStoreDir)) return;
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = int.MaxValue;
                foreach (string file in Directory.GetFiles(SnapshotDir, "*.json"))
                {
                    SnapshotStore s = serializer.Deserialize<SnapshotStore>(File.ReadAllText(file, Encoding.UTF8));
                    if (s == null) continue;
                    foreach (Snapshot snap in s.snapshots) foreach (SnapJar j in snap.jars) used.Add(j.sha1);
                }
                foreach (string jar in Directory.GetFiles(JarStoreDir, "*.jar"))
                    if (!used.Contains(Path.GetFileNameWithoutExtension(jar))) File.Delete(jar);
            }
            catch { }
        }

        static long StoreSize()
        {
            try { return Directory.Exists(JarStoreDir) ? new DirectoryInfo(JarStoreDir).GetFiles("*.jar").Sum(f => f.Length) : 0; }
            catch { return 0; }
        }

        static Dictionary<string, object> SnapshotsState()
        {
            string dir = modsDir;
            SnapshotStore store;
            lock (FileLock) store = LoadStore(dir);
            var list = store.snapshots.OrderByDescending(s => s.created).Select(s => (object)Obj("id", s.id, "name", s.name, "created", s.created, "auto", s.auto,
                "jars", s.jars.Count, "enabled", s.jars.Count(j => j.enabled), "size", s.jars.Sum(j => j.size))).ToList();
            return Obj("ok", true, "folder", dir, "snapshots", list, "storeFolder", JarStoreDir, "storeSize", StoreSize(), "bisect", BisectObj(store.bisect));
        }

        static Dictionary<string, object> CreateSnapshotApi(string name)
        {
            name = Regex.Replace(name ?? "", @"\s+", " ").Trim();
            if (name.Length == 0) name = "Snapshot " + DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            if (name.Length > 80) name = name.Substring(0, 80);
            lock (FileLock)
            {
                SnapshotStore store = LoadStore(modsDir);
                Snapshot snap = TakeSnapshot(modsDir, store, name, false);
                return Obj("ok", true, "id", snap.id, "jars", snap.jars.Count);
            }
        }

        static Snapshot FindSnapshot(SnapshotStore store, string id)
        {
            Snapshot snap = store.snapshots.FirstOrDefault(s => s.id == id);
            if (snap == null) throw new UserError("That snapshot isn't there any more. Refresh the page.");
            return snap;
        }

        static Dictionary<string, object> RenameSnapshot(string id, string name)
        {
            name = Regex.Replace(name ?? "", @"\s+", " ").Trim();
            if (name.Length == 0 || name.Length > 80) throw new UserError("Give the snapshot a name (up to 80 characters).");
            lock (FileLock)
            {
                SnapshotStore store = LoadStore(modsDir);
                Snapshot snap = FindSnapshot(store, id);
                snap.name = name;
                snap.auto = false; // a named snapshot is kept
                SaveStore(modsDir, store);
                return Obj("ok", true);
            }
        }

        static Dictionary<string, object> DeleteSnapshot(string id)
        {
            lock (FileLock)
            {
                SnapshotStore store = LoadStore(modsDir);
                Snapshot snap = FindSnapshot(store, id);
                if (store.bisect != null && store.bisect.snapshotId == id) throw new UserError("This snapshot is where the problem-mod search goes back to. Finish or stop the search first.");
                store.snapshots.Remove(snap);
                SaveStore(modsDir, store);
                PruneJarStore();
                Log("Snapshot  deleted " + snap.name);
                return Obj("ok", true);
            }
        }

        // ---------- Restoring ----------

        sealed class RestoreStep
        {
            public string Kind; // enable | disable | restore | replace | remove | missing
            public string Key, Sha, Current;
            public bool Enabled;
        }

        static List<RestoreStep> PlanRestore(string dir, Snapshot snap)
        {
            var current = FolderJars(dir).GroupBy(f => KeyOf(f.Name).ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());
            var steps = new List<RestoreStep>();
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SnapJar j in snap.jars)
            {
                keys.Add(j.file);
                FileInfo cur;
                current.TryGetValue(j.file.ToLowerInvariant(), out cur);
                bool stored = File.Exists(Path.Combine(JarStoreDir, j.sha1 + ".jar"));
                if (cur != null && FileSha1(cur) == j.sha1)
                {
                    if (IsOff(cur.Name) == j.enabled) steps.Add(new RestoreStep { Kind = j.enabled ? "enable" : "disable", Key = j.file, Current = cur.Name, Enabled = j.enabled });
                }
                else if (!stored) steps.Add(new RestoreStep { Kind = "missing", Key = j.file, Current = cur == null ? null : cur.Name });
                else steps.Add(new RestoreStep { Kind = cur != null ? "replace" : "restore", Key = j.file, Sha = j.sha1, Current = cur == null ? null : cur.Name, Enabled = j.enabled });
            }
            foreach (FileInfo cur in current.Values)
                if (!keys.Contains(KeyOf(cur.Name))) steps.Add(new RestoreStep { Kind = "remove", Key = KeyOf(cur.Name), Current = cur.Name });
            return steps;
        }

        static Dictionary<string, object> StepsObj(List<RestoreStep> steps)
        {
            Func<string, List<object>> of = kind => steps.Where(s => s.Kind == kind).Select(s => (object)s.Key).ToList();
            return Obj("enable", of("enable"), "disable", of("disable"), "restore", of("restore"), "replace", of("replace"), "remove", of("remove"), "missing", of("missing"),
                "changes", steps.Count(s => s.Kind != "missing"));
        }

        static Dictionary<string, object> RestorePreview(string id)
        {
            lock (FileLock)
            {
                SnapshotStore store = LoadStore(modsDir);
                Snapshot snap = FindSnapshot(store, id);
                return Obj("ok", true, "name", snap.name, "steps", StepsObj(PlanRestore(modsDir, snap)));
            }
        }

        static void ApplyRestore(string dir, List<RestoreStep> steps)
        {
            foreach (RestoreStep s in steps.Where(s => s.Current != null && s.Kind != "missing"))
                AssertNotInUse(Path.Combine(dir, s.Current));
            List<string> removing = steps.Where(s => s.Kind == "remove" || s.Kind == "replace").Select(s => Path.Combine(dir, s.Current)).ToList();
            if (!RecyclePaths(removing)) throw new UserError("Windows couldn't move some jars to the Recycle Bin, so the restore stopped part-way. Nothing else was changed.");
            foreach (RestoreStep s in steps)
            {
                string target = Path.Combine(dir, s.Key + (s.Enabled ? "" : ".disabled"));
                switch (s.Kind)
                {
                    case "enable":
                    case "disable":
                        File.Move(Path.Combine(dir, s.Current), target);
                        break;
                    case "restore":
                    case "replace":
                        File.Copy(Path.Combine(JarStoreDir, s.Sha + ".jar"), target + ".restoring", true);
                        File.Move(target + ".restoring", target);
                        break;
                }
            }
        }

        static Dictionary<string, object> RestoreSnapshotApi(string id)
        {
            lock (FileLock)
            {
                string dir = modsDir;
                SnapshotStore store = LoadStore(dir);
                if (store.bisect != null) throw new UserError("Finish or stop the problem-mod search first.");
                Snapshot snap = FindSnapshot(store, id);
                List<RestoreStep> steps = PlanRestore(dir, snap);
                if (!steps.Any(s => s.Kind != "missing")) throw new UserError("The mods folder already matches " + snap.name + ".");
                foreach (RestoreStep s in steps.Where(s => s.Current != null && s.Kind != "missing")) AssertNotInUse(Path.Combine(dir, s.Current));
                TakeSnapshot(dir, store, "Before restoring \"" + snap.name + "\"", true);
                ApplyRestore(dir, steps);
                Log("Snapshot  restored " + snap.name + " (" + steps.Count(s => s.Kind != "missing") + " changes)");
                return Obj("ok", true, "steps", StepsObj(steps));
            }
        }

        // ---------- Finding a problem mod ----------

        static Dictionary<string, object> BisectSummary()
        {
            try
            {
                string path = SnapshotFile(modsDir);
                if (!File.Exists(path)) return null;
                BisectState b;
                lock (FileLock) b = LoadStore(modsDir).bisect;
                return b == null ? null : Obj("status", b.status, "round", b.round, "suspects", b.suspects.Count, "off", b.testingOff.Count,
                    "roundsLeft", RoundsLeft(b.suspects.Count), "result", b.result);
            }
            catch { return null; }
        }

        static int RoundsLeft(int suspects)
        {
            int rounds = 0;
            while (suspects > 1) { suspects = (suspects + 1) / 2; rounds++; }
            return rounds;
        }

        static Dictionary<string, object> BisectObj(BisectState b)
        {
            if (b == null) return null;
            return Obj("status", b.status, "started", b.started, "round", b.round, "startCount", b.startCount,
                "suspects", b.suspects, "testingOff", b.testingOff, "result", b.result, "note", b.note,
                "roundsLeft", RoundsLeft(b.suspects.Count),
                "history", b.history.Select(h => (object)Obj("round", h.round, "suspects", h.suspects, "off", h.off, "answer", h.answer)).ToList());
        }

        static void RenameJar(string dir, string key, bool enable)
        {
            string from = Path.Combine(dir, enable ? key + ".disabled" : key);
            string to = Path.Combine(dir, enable ? key : key + ".disabled");
            if (!File.Exists(from) || File.Exists(to)) return;
            File.Move(from, to);
        }

        // Which of these suspects can really be turned off: anything a mod that stays on needs is kept on.
        static HashSet<string> OffSet(string dir, IEnumerable<string> wanted)
        {
            List<FileInfo> enabled = FolderJars(dir).Where(f => !IsOff(f.Name)).ToList();
            Dictionary<string, HashSet<string>> needs = RequiredJars(dir, enabled, "client");
            var off = new HashSet<string>(wanted, StringComparer.OrdinalIgnoreCase);
            bool changed = true;
            while (changed)
            {
                changed = false;
                var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (FileInfo f in enabled)
                {
                    if (off.Contains(f.Name)) continue;
                    HashSet<string> n;
                    if (needs.TryGetValue(f.Name, out n)) required.UnionWith(n);
                }
                int before = off.Count;
                off.ExceptWith(required);
                changed = off.Count != before;
            }
            return off;
        }

        static void NextRound(string dir, BisectState b)
        {
            var enabled = new HashSet<string>(FolderJars(dir).Where(f => !IsOff(f.Name)).Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
            List<string> suspects = b.suspects.Where(enabled.Contains).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
            b.suspects = suspects;
            b.testingOff = new List<string>();
            if (suspects.Count > 1)
            {
                int half = suspects.Count / 2;
                foreach (IEnumerable<string> part in new[] { suspects.Skip(half), suspects.Take(half) })
                {
                    HashSet<string> off = OffSet(dir, part);
                    if (off.Count == 0 || off.Count >= suspects.Count) continue;
                    foreach (string key in off) AssertNotInUse(Path.Combine(dir, key));
                    foreach (string key in off) RenameJar(dir, key, false);
                    b.testingOff = off.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
                    b.round++;
                    Log("Bisect    round " + b.round + ": turned off " + off.Count + " of " + suspects.Count + " suspects");
                    return;
                }
            }
            b.status = "done";
            b.result = suspects;
            b.note = suspects.Count > 1 ? "These mods can't be tested apart because they need each other." : null;
            Log("Bisect    finished: " + (suspects.Count == 1 ? suspects[0] : suspects.Count + " suspects left"));
        }

        static Dictionary<string, object> BisectStart(List<string> files)
        {
            lock (FileLock)
            {
                string dir = modsDir;
                SnapshotStore store = LoadStore(dir);
                if (store.bisect != null) throw new UserError("A problem-mod search is already going.");
                List<string> enabled = FolderJars(dir).Where(f => !IsOff(f.Name)).Select(f => f.Name).ToList();
                var chosen = new HashSet<string>(files.Select(KeyOf), StringComparer.OrdinalIgnoreCase);
                List<string> suspects = files.Count > 0 ? enabled.Where(chosen.Contains).ToList() : enabled;
                if (suspects.Count < 2) throw new UserError("Choose at least two mods that are switched on.");
                foreach (FileInfo f in FolderJars(dir)) AssertNotInUse(f.FullName);
                Snapshot snap = TakeSnapshot(dir, store, "Before finding a problem mod", true);
                store.bisect = new BisectState { started = DateTime.UtcNow.ToString("o"), snapshotId = snap.id, status = "testing", startCount = suspects.Count, suspects = suspects };
                NextRound(dir, store.bisect);
                SaveStore(dir, store);
                return Obj("ok", true, "bisect", BisectObj(store.bisect));
            }
        }

        static Dictionary<string, object> BisectAnswer(string answer)
        {
            if (answer != "broken" && answer != "works") throw new UserError("Answer \"broken\" or \"works\".");
            lock (FileLock)
            {
                string dir = modsDir;
                SnapshotStore store = LoadStore(dir);
                BisectState b = store.bisect;
                if (b == null || b.status != "testing") throw new UserError("There's no problem-mod search waiting for an answer.");
                foreach (string key in b.testingOff) AssertNotInUse(Path.Combine(dir, key + ".disabled"));
                b.history.Add(new BisectRound { round = b.round, suspects = b.suspects.Count, off = b.testingOff.Count, answer = answer });
                var off = new HashSet<string>(b.testingOff, StringComparer.OrdinalIgnoreCase);
                if (answer == "broken")
                {
                    // Still broken with them off: the problem is among the suspects that stayed on. The cleared ones stay off.
                    b.suspects = b.suspects.Where(s => !off.Contains(s)).ToList();
                }
                else
                {
                    // Fixed by turning them off: the problem is among them. Switch them back on for the next round.
                    b.suspects = b.testingOff.ToList();
                    foreach (string key in b.testingOff) RenameJar(dir, key, true);
                }
                Log("Bisect    round " + b.round + ": " + (answer == "broken" ? "still broken" : "problem gone"));
                NextRound(dir, b);
                SaveStore(dir, store);
                return Obj("ok", true, "bisect", BisectObj(b));
            }
        }

        // Ends the search. restore: put the folder back as it was when the search started.
        // disable: mods to switch off afterwards (normally the one that was found).
        static Dictionary<string, object> BisectStop(bool restore, List<string> disable)
        {
            lock (FileLock)
            {
                string dir = modsDir;
                SnapshotStore store = LoadStore(dir);
                BisectState b = store.bisect;
                if (b == null) throw new UserError("There's no problem-mod search going.");
                int changes = 0;
                if (restore)
                {
                    Snapshot snap = store.snapshots.FirstOrDefault(s => s.id == b.snapshotId);
                    if (snap == null) throw new UserError("The snapshot from the start of the search is gone, so the folder can't be put back automatically.");
                    List<RestoreStep> steps = PlanRestore(dir, snap);
                    ApplyRestore(dir, steps);
                    changes = steps.Count(s => s.Kind != "missing");
                }
                foreach (string key in disable.Select(KeyOf))
                {
                    string path = Path.Combine(dir, key);
                    if (!File.Exists(path)) continue;
                    AssertNotInUse(path);
                    RenameJar(dir, key, false);
                    Log("Disabled  " + key + " (found by the problem-mod search)");
                }
                store.bisect = null;
                SaveStore(dir, store);
                Log("Bisect    stopped" + (restore ? ", folder put back (" + changes + " changes)" : ""));
                return Obj("ok", true, "changes", changes);
            }
        }
    }
}
