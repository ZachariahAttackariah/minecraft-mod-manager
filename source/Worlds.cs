// World backups: a world folder zipped into the app's data folder (world-backups), listed with restore.
// Taken by hand, or (Settings) before updates and before removing worldgen mods. Restoring moves the
// current world to the Recycle Bin first, and never runs while the world is open.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MinecraftModManager
{
    static partial class Program
    {
        static readonly object WorldLock = new object();
        const int WorldBackupsKept = 5;

        sealed class WorldRef { public string Side, Name, Dir; }

        static List<WorldRef> AllWorlds()
        {
            var list = new List<WorldRef>();
            try
            {
                var saves = new DirectoryInfo(Path.Combine(GameDir(), "saves"));
                if (saves.Exists)
                    foreach (DirectoryInfo w in saves.GetDirectories().Where(w => File.Exists(Path.Combine(w.FullName, "level.dat"))))
                        list.Add(new WorldRef { Side = "game", Name = w.Name, Dir = w.FullName });
            }
            catch { }
            if (serverDir != null && Directory.Exists(serverDir))
            {
                string level;
                if (!ReadProperties(Path.Combine(serverDir, "server.properties")).TryGetValue("level-name", out level) || string.IsNullOrWhiteSpace(level)) level = "world";
                string dir = Path.Combine(serverDir, level);
                if (File.Exists(Path.Combine(dir, "level.dat"))) list.Add(new WorldRef { Side = "server", Name = level, Dir = dir });
            }
            return list;
        }

        static string BackupDirFor(WorldRef w)
        {
            string id;
            using (var sha = SHA1.Create())
                id = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(w.Dir).ToLowerInvariant()))).Replace("-", "").Substring(0, 10);
            return Path.Combine(dataDir, "world-backups", Regex.Replace(w.Side + "-" + w.Name, @"[^\w.-]+", "_") + "-" + id);
        }

        // The game or the server has the world open while its session.lock can't be opened exclusively.
        static bool WorldInUse(string dir)
        {
            string lockPath = Path.Combine(dir, "session.lock");
            if (!File.Exists(lockPath)) return false;
            try { using (File.Open(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } return false; }
            catch (IOException) { return true; }
            catch { return false; }
        }

        static WorldRef FindWorld(string side, string name)
        {
            WorldRef w = AllWorlds().FirstOrDefault(x => x.Side == side && x.Name == name);
            if (w == null) throw new UserError("That world isn't there any more.");
            return w;
        }

        static Dictionary<string, object> ListWorlds()
        {
            var worlds = new List<object>();
            foreach (WorldRef w in AllWorlds())
            {
                var dir = new DirectoryInfo(BackupDirFor(w));
                var backups = dir.Exists ? dir.GetFiles("*.zip").OrderByDescending(f => f.LastWriteTimeUtc)
                    .Select(f => (object)Obj("file", f.Name, "time", f.LastWriteTimeUtc.ToString("o"), "size", f.Length, "reason", BackupReason(f.Name))).ToList() : new List<object>();
                worlds.Add(Obj("side", w.Side, "name", w.Name, "inUse", WorldInUse(w.Dir),
                    "lastPlayed", File.GetLastWriteTimeUtc(Path.Combine(w.Dir, "level.dat")).ToString("o"), "backups", backups));
            }
            return Obj("ok", true, "worlds", worlds, "folder", Path.Combine(dataDir, "world-backups"));
        }

        static string BackupReason(string file)
        {
            Match m = Regex.Match(file, @"__\d{8}-\d{6}(?:__(.+))?\.zip$");
            return m.Success && m.Groups[1].Success ? m.Groups[1].Value.Replace('_', ' ') : null;
        }

        // Zips a world. Files the game is writing are read anyway (shared); its session.lock is left out.
        static Dictionary<string, object> BackupWorld(string side, string name, string reason)
        {
            lock (WorldLock)
            {
                WorldRef w = FindWorld(side, name);
                string folder = BackupDirFor(w);
                Directory.CreateDirectory(folder);
                string tag = string.IsNullOrEmpty(reason) ? "" : "__" + Regex.Replace(reason, @"[^\w-]+", "_").Trim('_');
                if (tag.Length > 60) tag = tag.Substring(0, 60);
                string zipPath = Path.Combine(folder, Regex.Replace(w.Name, @"[^\w.-]+", "_") + "__" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + tag + ".zip");
                string temp = zipPath + ".part";
                int skipped = 0;
                try
                {
                    using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write))
                    using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
                    {
                        string root = Path.GetFullPath(w.Dir).TrimEnd('\\') + "\\";
                        foreach (string file in Directory.EnumerateFiles(w.Dir, "*", SearchOption.AllDirectories))
                        {
                            if (Path.GetFileName(file).Equals("session.lock", StringComparison.OrdinalIgnoreCase)) continue;
                            try
                            {
                                using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                                using (Stream entry = zip.CreateEntry(file.Substring(root.Length).Replace('\\', '/'), CompressionLevel.Fastest).Open())
                                    input.CopyTo(entry);
                            }
                            catch (IOException) { skipped++; }
                        }
                    }
                    File.Move(temp, zipPath);
                }
                catch (Exception ex)
                {
                    try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                    throw new UserError("Couldn't back up " + w.Name + " (" + ex.Message + ").");
                }
                // Keep the newest few; older ones go to the Recycle Bin.
                List<string> old = new DirectoryInfo(folder).GetFiles("*.zip").OrderByDescending(f => f.LastWriteTimeUtc).Skip(WorldBackupsKept).Select(f => f.FullName).ToList();
                if (old.Count > 0) RecyclePaths(old);
                Log("World backup " + w.Side + "/" + w.Name + " -> " + Path.GetFileName(zipPath) + (skipped > 0 ? " (" + skipped + " files in use skipped)" : ""));
                return Obj("ok", true, "file", Path.GetFileName(zipPath), "size", new FileInfo(zipPath).Length, "skipped", skipped, "world", w.Name);
            }
        }

        // Before risky changes (Settings): the most recently played world of this game.
        static string BackupLatestWorld(string reason)
        {
            WorldRef latest = AllWorlds().Where(w => w.Side == "game")
                .OrderByDescending(w => File.GetLastWriteTimeUtc(Path.Combine(w.Dir, "level.dat"))).FirstOrDefault();
            if (latest == null) return null;
            BackupWorld(latest.Side, latest.Name, reason);
            return latest.Name;
        }

        static Dictionary<string, object> RestoreWorld(string side, string name, string file)
        {
            lock (WorldLock)
            {
                WorldRef w = FindWorld(side, name);
                if (WorldInUse(w.Dir)) throw new UserError(w.Name + " is open in the " + (w.Side == "server" ? "server" : "game") + ". Close it first.");
                if (w.Side == "server" && serverProcess != null) throw new UserError("Stop the server first.");
                string zipPath = Path.Combine(BackupDirFor(w), Path.GetFileName(file ?? ""));
                if (!zipPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || !File.Exists(zipPath)) throw new UserError("That backup isn't there any more.");
                // Unpack beside the world first, so a bad zip never leaves the world half gone.
                string staging = w.Dir.TrimEnd('\\') + ".restoring";
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
                try { ZipFile.ExtractToDirectory(zipPath, staging); }
                catch (Exception ex)
                {
                    try { Directory.Delete(staging, true); } catch { }
                    throw new UserError("Couldn't read that backup (" + ex.Message + ").");
                }
                if (!RecyclePaths(new List<string> { w.Dir }))
                {
                    try { Directory.Delete(staging, true); } catch { }
                    throw new UserError("Couldn't move the current " + w.Name + " to the Recycle Bin, so nothing was restored.");
                }
                Directory.Move(staging, w.Dir);
                Log("World restore " + w.Side + "/" + w.Name + " <- " + Path.GetFileName(zipPath) + " (the previous world is in the Recycle Bin)");
                return Obj("ok", true, "world", w.Name);
            }
        }

        static Dictionary<string, object> DeleteWorldBackup(string side, string name, string file)
        {
            WorldRef w = FindWorld(side, name);
            string zipPath = Path.Combine(BackupDirFor(w), Path.GetFileName(file ?? ""));
            if (!zipPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || !File.Exists(zipPath)) throw new UserError("That backup isn't there any more.");
            if (!RecyclePaths(new List<string> { zipPath })) throw new UserError("Couldn't move that backup to the Recycle Bin.");
            return Obj("ok", true);
        }
    }
}
