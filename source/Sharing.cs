// Sharing between instances. Each instance opts in or out of each part separately:
//  - Java arguments: the launcher's own global ones (JvmArgs in prismlauncher.cfg). An instance uses
//    them unless its instance.cfg overrides them (OverrideJavaArgs plus its own JvmArgs).
//  - Shader packs and resource packs: the instance's folder becomes a directory junction to
//    <data>\shared\shaderpacks or \resourcepacks, so every sharing instance sees the same packs.
//  - Game settings (options.txt): merged into <data>\shared\options.txt, and handed back out to every
//    sharing instance each time Play is pressed. Keys one game doesn't know (another version's, or the
//    key binds of a mod it doesn't have) are kept, so instances with different mods don't wipe each other's.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;

namespace MinecraftModManager
{
    static partial class Program
    {
        static readonly string[] SharedFolders = { "shaderpacks", "resourcepacks" };
        static readonly object SharingLock = new object();

        static string SharedDir() { return Path.Combine(dataDir, "shared"); }
        static string SharedOptionsPath() { return Path.Combine(SharedDir(), "options.txt"); }
        static string SharingStatePath() { return Path.Combine(SharedDir(), "sharing.json"); }
        static string GameDirOf(InstanceInfo info) { return Path.GetDirectoryName(info.Mods); }
        static string OptionsPathOf(InstanceInfo info) { return Path.Combine(GameDirOf(info), "options.txt"); }
        static string PackLabel(string kind) { return kind == "shaderpacks" ? "shader packs" : "resource packs"; }

        static List<string> ReadInstanceCfg(InstanceInfo info)
        {
            return File.ReadAllLines(Path.Combine(InstanceDir(info), "instance.cfg")).ToList();
        }

        // ---------- The launcher's .cfg files ----------
        // They're Qt INI files: a value with , ; = or a space at either end is written in quotes, and
        // " and \ are escaped. Unquoted commas would make Qt read the value as a list.

        static string IniGet(List<string> cfg, string key)
        {
            string line = cfg.FirstOrDefault(l => l.StartsWith(key + "="));
            return line == null ? null : IniUnescape(line.Substring(key.Length + 1));
        }

        static string IniUnescape(string raw)
        {
            raw = raw.Trim();
            var sb = new StringBuilder();
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (c == '"') continue;
                if (c == '\\' && i + 1 < raw.Length)
                {
                    char n = raw[++i];
                    sb.Append(n == 'n' ? '\n' : n == 't' ? '\t' : n == 'r' ? '\r' : n);
                    continue;
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        static string IniEscape(string value)
        {
            value = Regex.Replace(value ?? "", @"[\r\n\t]", " ");
            var sb = new StringBuilder();
            bool quote = value.Length > 0 && (value[0] == ' ' || value[value.Length - 1] == ' ');
            foreach (char c in value)
            {
                if (c == ';' || c == ',' || c == '=') quote = true;
                if (c == '"' || c == '\\') sb.Append('\\');
                sb.Append(c);
            }
            return quote ? "\"" + sb + "\"" : sb.ToString();
        }

        static void IniSet(List<string> cfg, string key, string value)
        {
            string line = key + "=" + IniEscape(value);
            int at = cfg.FindIndex(l => l.StartsWith(key + "="));
            if (at >= 0) { cfg[at] = line; return; }
            // Everything the launcher reads is in [General].
            int general = cfg.FindIndex(l => l.Trim() == "[General]");
            int next = general < 0 ? -1 : cfg.FindIndex(general + 1, l => l.StartsWith("["));
            if (next >= 0) cfg.Insert(next, line); else cfg.Add(line);
        }

        // ---------- Java arguments ----------

        static string LauncherConfigPath() { return Path.Combine(PrismDataDir(), "prismlauncher.cfg"); }

        static string SharedJavaArgs()
        {
            try
            {
                string path = LauncherConfigPath();
                return File.Exists(path) ? IniGet(File.ReadAllLines(path).ToList(), "JvmArgs") ?? "" : "";
            }
            catch { return ""; }
        }

        // True when the instance uses its own arguments. Older launchers had one OverrideJava switch for
        // both the Java location and the arguments.
        static bool OwnJavaArgs(List<string> cfg)
        {
            return (IniGet(cfg, "OverrideJavaArgs") ?? IniGet(cfg, "OverrideJava")) == "true";
        }

        static string CleanJavaArgs(string args)
        {
            args = Regex.Replace(args ?? "", @"\s+", " ").Trim();
            if (args.Length > 4000) throw new UserError("Those Java arguments are too long.");
            if (Regex.IsMatch(args, @"(^|\s)-Xm[sx]", RegexOptions.IgnoreCase))
                throw new UserError("Leave out -Xmx and -Xms. Memory is set for each instance in its settings.");
            return args;
        }

        // Switches an instance between the shared arguments and its own. Returns what changed, or null.
        static string SetInstanceJava(List<string> cfg, bool useShared, string ownArgs)
        {
            bool wasShared = !OwnJavaArgs(cfg);
            string before = Regex.Replace(IniGet(cfg, "JvmArgs") ?? "", @"\s+", " ").Trim();
            ownArgs = CleanJavaArgs(ownArgs);
            if (useShared == wasShared && (useShared || ownArgs == before)) return null;
            string legacy = IniGet(cfg, "OverrideJava");
            if (legacy != null)
            {
                if (IniGet(cfg, "OverrideJavaLocation") == null) IniSet(cfg, "OverrideJavaLocation", legacy);
                cfg.RemoveAll(l => l.StartsWith("OverrideJava="));
            }
            IniSet(cfg, "OverrideJavaArgs", useShared ? "false" : "true");
            // An instance's own arguments stay in its file while it uses the shared ones, for switching back.
            if (!useShared) IniSet(cfg, "JvmArgs", ownArgs);
            return useShared ? "uses the shared Java arguments" : wasShared ? "uses its own Java arguments" : "own Java arguments changed";
        }

        static Dictionary<string, object> SetSharedJavaArgs(string args)
        {
            args = CleanJavaArgs(args);
            string path = LauncherConfigPath();
            if (!File.Exists(path)) throw new UserError("Open the launcher once first, so it can set itself up.");
            RequirePrismClosed();
            List<string> cfg = File.ReadAllLines(path).ToList();
            if ((IniGet(cfg, "JvmArgs") ?? "") == args) throw new UserError("Nothing was changed.");
            IniSet(cfg, "JvmArgs", args);
            File.Copy(path, path + ".bak", true);
            File.WriteAllLines(path, cfg, new UTF8Encoding(false));
            Log("Shared    Java arguments: " + (args.Length == 0 ? "(none)" : args));
            return Obj("ok", true, "javaArgs", args);
        }

        // ---------- Shared pack folders ----------

        static bool IsLink(string path)
        {
            try { return Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
            catch { return false; }
        }

        // Where a path really leads, following junctions. Null if it can't be opened.
        static string FinalPath(string path)
        {
            using (SafeFileHandle handle = CreateFile(path, 0, 7, IntPtr.Zero, 3, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero))
            {
                if (handle.IsInvalid) return null;
                var sb = new StringBuilder(1024);
                uint length = GetFinalPathNameByHandle(handle, sb, (uint)sb.Capacity, 0);
                if (length == 0 || length >= sb.Capacity) return null;
                string s = sb.ToString();
                if (s.StartsWith(@"\\?\UNC\")) return @"\\" + s.Substring(8);
                return s.StartsWith(@"\\?\") ? s.Substring(4) : s;
            }
        }

        static bool SharesFolder(InstanceInfo info, string kind)
        {
            string local = Path.Combine(GameDirOf(info), kind);
            if (!IsLink(local)) return false;
            string target = FinalPath(local), shared = FinalPath(Path.Combine(SharedDir(), kind));
            return target != null && shared != null && SamePath(target, shared);
        }

        // Moves the instance's packs into the shared folder, then turns its folder into a link to it.
        // A pack that's already shared (same name, same bytes) is dropped from the instance; a different
        // pack with a taken name gets the instance's name added. Returns { moved, renamed }.
        static int[] ShareFolder(InstanceInfo info, string kind)
        {
            string shared = Path.Combine(SharedDir(), kind);
            Directory.CreateDirectory(shared);
            string local = Path.Combine(GameDirOf(info), kind);
            int moved = 0, renamed = 0;
            if (IsLink(local))
            {
                if (SharesFolder(info, kind)) return new[] { 0, 0 };
                Directory.Delete(local); // a link to somewhere else: only the link goes
            }
            if (Directory.Exists(local))
            {
                foreach (string entry in Directory.GetFileSystemEntries(local))
                {
                    string name = Path.GetFileName(entry);
                    string dest = Path.Combine(shared, name);
                    if (SameContent(entry, dest))
                    {
                        if (File.Exists(entry)) File.Delete(entry); else Directory.Delete(entry, true);
                        continue;
                    }
                    if (File.Exists(dest) || Directory.Exists(dest))
                    {
                        dest = FreeName(shared, name, info.Name);
                        renamed++;
                    }
                    MoveEntry(entry, dest);
                    moved++;
                }
                Directory.Delete(local);
            }
            Directory.CreateDirectory(GameDirOf(info)); // a new instance may not have its game folder yet
            MakeJunction(local, shared);
            return new[] { moved, renamed };
        }

        // The instance gets a folder of its own again, starting with a copy of the shared packs.
        static void UnshareFolder(InstanceInfo info, string kind)
        {
            if (!SharesFolder(info, kind)) return;
            string local = Path.Combine(GameDirOf(info), kind);
            Directory.Delete(local); // removes the link, not the shared packs
            Directory.CreateDirectory(local);
            CopyTree(Path.Combine(SharedDir(), kind), local);
        }

        static string ApplyFolderSharing(InstanceInfo info, string kind, bool share)
        {
            try
            {
                if (!share)
                {
                    UnshareFolder(info, kind);
                    return "own " + PackLabel(kind) + " (a copy of the shared ones)";
                }
                int[] result = ShareFolder(info, kind);
                return PackLabel(kind) + " shared" + (result[0] > 0 ? " (" + Plural(result[0], "pack", "packs") + " moved into the shared folder" +
                    (result[1] > 0 ? ", " + result[1] + " renamed because a different pack had the same name" : "") + ")" : "");
            }
            catch (UserError) { throw; }
            catch (Exception ex)
            {
                throw new UserError("Couldn't " + (share ? "share" : "unshare") + " the " + PackLabel(kind) + " of " + info.Name + ": " + ex.Message +
                    " Close anything that's using them and try again.");
            }
        }

        static string FreeName(string dir, string name, string instanceName)
        {
            string tag = Regex.Replace(instanceName, @"[<>:""/\\|?*\x00-\x1f]", "").Trim();
            bool isFile = Path.HasExtension(name) && !Directory.Exists(Path.Combine(dir, name));
            string stem = isFile ? Path.GetFileNameWithoutExtension(name) : name, ext = isFile ? Path.GetExtension(name) : "";
            for (int n = 1; ; n++)
            {
                string candidate = stem + " (" + tag + (n > 1 ? " " + n : "") + ")" + ext;
                string full = Path.Combine(dir, candidate);
                if (!File.Exists(full) && !Directory.Exists(full)) return full;
            }
        }

        static void MoveEntry(string from, string to)
        {
            bool isFile = File.Exists(from);
            try
            {
                if (isFile) File.Move(from, to); else Directory.Move(from, to);
            }
            catch (IOException)
            {
                // Another drive: copy, then remove the original.
                if (isFile) { File.Copy(from, to); File.Delete(from); }
                else { CopyTree(from, to); Directory.Delete(from, true); }
            }
        }

        static void CopyTree(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (string file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true);
            foreach (string sub in Directory.GetDirectories(from)) CopyTree(sub, Path.Combine(to, Path.GetFileName(sub)));
        }

        static bool SameContent(string a, string b)
        {
            if (File.Exists(a)) return File.Exists(b) && SameFile(a, b);
            if (!Directory.Exists(a) || !Directory.Exists(b)) return false;
            Func<string, List<string>> list = root => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .Select(f => f.Substring(root.Length)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            List<string> left = list(a), right = list(b);
            return left.SequenceEqual(right, StringComparer.OrdinalIgnoreCase) && left.All(rel => SameFile(a + rel, b + rel));
        }

        static bool SameFile(string a, string b)
        {
            if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
            using (FileStream sa = File.OpenRead(a))
            using (FileStream sb = File.OpenRead(b))
            {
                var ba = new byte[65536];
                var bb = new byte[65536];
                while (true)
                {
                    int na = ReadFull(sa, ba), nb = ReadFull(sb, bb);
                    if (na != nb) return false;
                    if (na == 0) return true;
                    for (int i = 0; i < na; i++) if (ba[i] != bb[i]) return false;
                }
            }
        }

        static int ReadFull(Stream s, byte[] buffer)
        {
            int total = 0, n;
            while (total < buffer.Length && (n = s.Read(buffer, total, buffer.Length - total)) > 0) total += n;
            return total;
        }

        // A directory junction (works without admin rights, unlike a symbolic link).
        static void MakeJunction(string link, string target)
        {
            target = Path.GetFullPath(target).TrimEnd('\\');
            byte[] substitute = Encoding.Unicode.GetBytes(@"\??\" + target);
            byte[] print = Encoding.Unicode.GetBytes(target);
            int pathBytes = substitute.Length + 2 + print.Length + 2;
            var buffer = new byte[16 + pathBytes];
            BitConverter.GetBytes(IO_REPARSE_TAG_MOUNT_POINT).CopyTo(buffer, 0);
            BitConverter.GetBytes((ushort)(8 + pathBytes)).CopyTo(buffer, 4);
            BitConverter.GetBytes((ushort)0).CopyTo(buffer, 8);                         // substitute name offset
            BitConverter.GetBytes((ushort)substitute.Length).CopyTo(buffer, 10);
            BitConverter.GetBytes((ushort)(substitute.Length + 2)).CopyTo(buffer, 12);   // print name offset
            BitConverter.GetBytes((ushort)print.Length).CopyTo(buffer, 14);
            substitute.CopyTo(buffer, 16);
            print.CopyTo(buffer, 16 + substitute.Length + 2);

            Directory.CreateDirectory(link);
            bool ok = false;
            try
            {
                using (SafeFileHandle handle = CreateFile(link, GENERIC_WRITE, 0, IntPtr.Zero, 3, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero))
                {
                    if (handle.IsInvalid) throw new IOException("Windows couldn't open " + link + " (error " + Marshal.GetLastWin32Error() + ").");
                    int returned;
                    if (!DeviceIoControl(handle, FSCTL_SET_REPARSE_POINT, buffer, buffer.Length, IntPtr.Zero, 0, out returned, IntPtr.Zero))
                        throw new IOException("Windows couldn't link " + link + " to " + target + " (error " + Marshal.GetLastWin32Error() + ").");
                }
                ok = true;
            }
            finally
            {
                if (!ok) try { Directory.Delete(link); } catch { }
            }
        }

        // Before an instance folder goes to the Recycle Bin, its links come out, so nothing can ever
        // reach the shared packs through it. Returns the links removed, to put back if needed.
        static List<string> RemoveLinks(InstanceInfo info)
        {
            var removed = new List<string>();
            foreach (string kind in SharedFolders)
            {
                string local = Path.Combine(GameDirOf(info), kind);
                if (!IsLink(local)) continue;
                string target = FinalPath(local);
                Directory.Delete(local);
                if (target != null) removed.Add(local + "|" + target);
            }
            return removed;
        }

        static void RestoreLinks(List<string> links)
        {
            foreach (string pair in links)
            {
                string[] parts = pair.Split('|');
                try { MakeJunction(parts[0], parts[1]); } catch { }
            }
        }

        // Instance folder size without counting what links point at.
        static long FolderSize(DirectoryInfo dir)
        {
            long size = 0;
            try
            {
                foreach (FileInfo f in dir.GetFiles()) size += f.Length;
                foreach (DirectoryInfo d in dir.GetDirectories())
                    if ((d.Attributes & FileAttributes.ReparsePoint) == 0) size += FolderSize(d);
            }
            catch { }
            return size;
        }

        // ---------- Shared game settings (options.txt) ----------

        sealed class OptionLines
        {
            public readonly List<string> Order = new List<string>();
            public readonly Dictionary<string, string> Values = new Dictionary<string, string>();

            public static OptionLines Read(string path)
            {
                var o = new OptionLines();
                if (!File.Exists(path)) return o;
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    int colon = line.IndexOf(':');
                    if (colon > 0) o.Set(line.Substring(0, colon), line.Substring(colon + 1));
                }
                return o;
            }

            public void Set(string key, string value)
            {
                if (!Values.ContainsKey(key)) { if (key == "version") Order.Insert(0, key); else Order.Add(key); }
                Values[key] = value;
            }

            public void Write(string path)
            {
                File.WriteAllLines(path, Order.Select(k => k + ":" + Values[k]), new UTF8Encoding(false));
            }
        }

        // The enabled resource packs only travel between instances that share the packs themselves.
        static readonly string[] PackKeys = { "resourcePacks", "incompatibleResourcePacks" };

        static bool SharedKey(string key, bool packsShared)
        {
            return key != "version" && (packsShared || Array.IndexOf(PackKeys, key) < 0);
        }

        static bool MergeOptions(OptionLines into, OptionLines from, bool packsShared, bool onlyMissing)
        {
            bool changed = false;
            foreach (string key in from.Order)
            {
                if (!SharedKey(key, packsShared)) continue;
                string old;
                if (into.Values.TryGetValue(key, out old) && (onlyMissing || old == from.Values[key])) continue;
                into.Set(key, from.Values[key]);
                changed = true;
            }
            return changed;
        }

        sealed class SharingState
        {
            public readonly List<string> Options = new List<string>();     // instance folder names
            public readonly Dictionary<string, long> Seen = new Dictionary<string, long>(); // options.txt time at the last sync
        }

        static SharingState LoadSharingState()
        {
            var state = new SharingState();
            try
            {
                string path = SharingStatePath();
                if (!File.Exists(path)) return state;
                var json = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>;
                foreach (object id in A(Get(json, "options"))) if (id is string) state.Options.Add((string)id);
                Dictionary<string, object> seen = D(Get(json, "seen"));
                if (seen != null)
                    foreach (KeyValuePair<string, object> kv in seen)
                    {
                        long ticks;
                        if (long.TryParse(Convert.ToString(kv.Value, CultureInfo.InvariantCulture), out ticks)) state.Seen[kv.Key] = ticks;
                    }
            }
            catch { }
            return state;
        }

        static void SaveSharingState(SharingState state)
        {
            Directory.CreateDirectory(SharedDir());
            var seen = state.Seen.ToDictionary(kv => kv.Key, kv => (object)kv.Value.ToString(CultureInfo.InvariantCulture));
            File.WriteAllText(SharingStatePath(), new JavaScriptSerializer().Serialize(Obj("options", state.Options, "seen", seen)), new UTF8Encoding(false));
        }

        static long OptionsTime(InstanceInfo info)
        {
            string path = OptionsPathOf(info);
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0;
        }

        // Picks up settings changed in any sharing game since the last sync (oldest change first, so the
        // newest wins), then hands the result to every sharing instance. A game that's running is left
        // alone and picked up next time.
        static void SyncGameSettings()
        {
            lock (SharingLock)
            {
                SharingState state = LoadSharingState();
                if (state.Options.Count == 0) return;
                List<InstanceInfo> idle = PrismInstances().Where(i => state.Options.Contains(i.Id) && !GameRunning(GameDirOf(i))).ToList();
                OptionLines shared = OptionLines.Read(SharedOptionsPath());
                var pickedUp = new List<string>();
                foreach (InstanceInfo i in idle.Where(i => OptionsTime(i) > 0).OrderBy(OptionsTime))
                {
                    long seen;
                    state.Seen.TryGetValue(i.Id, out seen);
                    if (OptionsTime(i) <= seen) continue;
                    OptionLines theirs = OptionLines.Read(OptionsPathOf(i));
                    bool changed = MergeOptions(shared, theirs, SharesFolder(i, "resourcepacks"), false);
                    if (theirs.Values.ContainsKey("version")) shared.Set("version", theirs.Values["version"]);
                    if (changed) pickedUp.Add(i.Name);
                }
                if (shared.Order.Count == 0) return;
                Directory.CreateDirectory(SharedDir());
                shared.Write(SharedOptionsPath());
                foreach (InstanceInfo i in idle)
                {
                    string path = OptionsPathOf(i);
                    OptionLines mine = OptionLines.Read(path);
                    bool changed = MergeOptions(mine, shared, SharesFolder(i, "resourcepacks"), false);
                    // A game that hasn't run yet gets a data version, so it doesn't treat the file as ancient.
                    if (!mine.Values.ContainsKey("version") && shared.Values.ContainsKey("version")) { mine.Set("version", shared.Values["version"]); changed = true; }
                    if (changed)
                    {
                        Directory.CreateDirectory(GameDirOf(i));
                        mine.Write(path);
                    }
                    state.Seen[i.Id] = OptionsTime(i);
                }
                SaveSharingState(state);
                if (pickedUp.Count > 0) Log("Shared    game settings picked up from " + string.Join(", ", pickedUp));
            }
        }

        static void TrySyncGameSettings()
        {
            try { SyncGameSettings(); }
            catch (Exception ex) { Log("Shared    game settings couldn't be synced: " + ex.Message); }
        }

        static bool SharesGameSettings(InstanceInfo info)
        {
            return LoadSharingState().Options.Contains(info.Id);
        }

        // Joining: the shared settings win, but anything only this game has is added to them.
        static string ShareGameSettings(InstanceInfo info, bool share)
        {
            lock (SharingLock)
            {
                SharingState state = LoadSharingState();
                if (share == state.Options.Contains(info.Id)) return null;
                if (share)
                {
                    OptionLines shared = OptionLines.Read(SharedOptionsPath());
                    OptionLines mine = OptionLines.Read(OptionsPathOf(info));
                    bool changed = MergeOptions(shared, mine, SharesFolder(info, "resourcepacks"), true);
                    if (!shared.Values.ContainsKey("version") && mine.Values.ContainsKey("version")) { shared.Set("version", mine.Values["version"]); changed = true; }
                    if (changed)
                    {
                        Directory.CreateDirectory(SharedDir());
                        shared.Write(SharedOptionsPath());
                    }
                    state.Options.Add(info.Id);
                    state.Seen[info.Id] = OptionsTime(info);
                }
                else
                {
                    state.Options.Remove(info.Id);
                    state.Seen.Remove(info.Id);
                }
                SaveSharingState(state);
            }
            if (share) SyncGameSettings();
            return share ? "game settings shared" : "own game settings";
        }

        // ---------- What the page shows ----------

        static Dictionary<string, object> InstanceSharingObj(InstanceInfo info, SharingState state)
        {
            List<string> cfg;
            try { cfg = ReadInstanceCfg(info); } catch { cfg = new List<string>(); }
            return Obj("java", !OwnJavaArgs(cfg), "javaArgs", IniGet(cfg, "JvmArgs") ?? "",
                "shaderpacks", SharesFolder(info, "shaderpacks"), "resourcepacks", SharesFolder(info, "resourcepacks"),
                "options", state.Options.Contains(info.Id));
        }

        static Dictionary<string, object> SharingInfo()
        {
            SharingState state = LoadSharingState();
            var instances = PrismInstances().Select(i =>
            {
                Dictionary<string, object> o = InstanceSharingObj(i, state);
                o["id"] = i.Id;
                o["name"] = i.Name;
                o["current"] = SamePath(i.Mods, modsDir);
                return (object)o;
            }).ToList();
            var folders = new Dictionary<string, object>();
            foreach (string kind in SharedFolders)
            {
                string dir = Path.Combine(SharedDir(), kind);
                folders[kind] = Obj("path", dir, "count", Directory.Exists(dir) ? Directory.GetFileSystemEntries(dir).Length : 0);
            }
            return Obj("ok", true, "javaArgs", SharedJavaArgs(), "launcherSetUp", File.Exists(LauncherConfigPath()), "prismRunning", PrismRunning(),
                "folders", folders, "hasOptions", File.Exists(SharedOptionsPath()), "instances", instances);
        }

        static void OpenSharedFolder(string kind)
        {
            if (Array.IndexOf(SharedFolders, kind) < 0) throw new UserError("Unknown shared folder.");
            string dir = Path.Combine(SharedDir(), kind);
            Directory.CreateDirectory(dir);
            try { Process.Start("explorer.exe", "\"" + dir + "\""); } catch { }
        }

        // ---------- Win32 ----------

        const uint GENERIC_WRITE = 0x40000000, FILE_FLAG_BACKUP_SEMANTICS = 0x02000000, FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
        const uint FSCTL_SET_REPARSE_POINT = 0x000900A4, IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint size, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] inBuffer, int inSize, IntPtr outBuffer, int outSize, out int returned, IntPtr overlapped);
    }
}
