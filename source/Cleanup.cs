// Disk cleanup: old logs, crash reports, Java crash dumps, debug output and leftovers in the game
// and server folders, Distant Horizons' LOD data (it regenerates), the built-in browser's cache and
// old sync backups. Everything goes to the Recycle Bin (the browser cache is cleared through
// WebView2 itself). Worlds, saves, screenshots, configs and mods are never on the list.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MinecraftModManager
{
    static partial class Program
    {
        sealed class CleanGroup
        {
            public string Id, Where, Label, Description, Risk = "safe", Blocked;
            public readonly List<string> Paths = new List<string>();
            public long Size;
            public int Files;
        }

        static readonly Regex KeepLogs = new Regex(@"^(latest|debug)\.log$", RegexOptions.IgnoreCase);

        static void AddPath(CleanGroup g, string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    g.Paths.Add(path);
                    g.Size += new FileInfo(path).Length;
                    g.Files++;
                }
                else if (Directory.Exists(path))
                {
                    List<FileInfo> files = new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).ToList();
                    g.Paths.Add(path);
                    g.Size += files.Sum(f => f.Length);
                    g.Files += files.Count;
                }
            }
            catch { }
        }

        static void AddFiles(CleanGroup g, string folder, string pattern, Func<FileInfo, bool> keep)
        {
            try
            {
                if (!Directory.Exists(folder)) return;
                foreach (FileInfo f in new DirectoryInfo(folder).GetFiles(pattern))
                    if (keep == null || !keep(f)) AddPath(g, f.FullName);
            }
            catch { }
        }

        static bool LooksLikeGameDir(string dir)
        {
            return dir != null && (Directory.Exists(Path.Combine(dir, "config")) || File.Exists(Path.Combine(dir, "options.txt")));
        }

        // The game holds logs\latest.log open while it runs.
        static bool GameRunning(string gameDir)
        {
            string log = Path.Combine(gameDir, "logs", "latest.log");
            if (!File.Exists(log)) return false;
            try { using (File.Open(log, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } return false; }
            catch (IOException) { return true; }
            catch { return false; }
        }

        static List<CleanGroup> ScanCleanup()
        {
            var groups = new List<CleanGroup>();
            string game = GameDir();
            if (LooksLikeGameDir(game))
            {
                string blocked = GameRunning(game) ? "Close the game first." : null;
                string where = PrismInstanceFor(modsDir) != null ? PrismInstanceFor(modsDir)[1] : "Game";
                var g = new CleanGroup { Id = "game-logs", Where = where, Label = "Old game logs", Description = "Logs from earlier sessions (the current latest.log is kept).", Blocked = blocked };
                AddFiles(g, Path.Combine(game, "logs"), "*", f => KeepLogs.IsMatch(f.Name));
                groups.Add(g);
                g = new CleanGroup { Id = "game-crash", Where = where, Label = "Crash reports", Description = "Reports the game wrote when it crashed.", Blocked = blocked };
                AddFiles(g, Path.Combine(game, "crash-reports"), "*", null);
                groups.Add(g);
                g = new CleanGroup { Id = "game-dumps", Where = where, Label = "Java crash dumps", Description = "hs_err_pid / replay_pid files Java leaves after a hard crash.", Blocked = blocked };
                AddFiles(g, game, "hs_err_pid*.log", null);
                AddFiles(g, game, "replay_pid*.log", null);
                groups.Add(g);
                g = new CleanGroup { Id = "game-debug", Where = where, Label = "Debug output", Description = ".mixin.out, which only matters when debugging mods.", Blocked = blocked };
                AddPath(g, Path.Combine(game, ".mixin.out"));
                groups.Add(g);
                g = new CleanGroup { Id = "game-dh", Where = where, Label = "Distant Horizons cache (multiplayer)", Risk = "careful", Blocked = blocked,
                    Description = "Far-away terrain the game saved from servers. It's rebuilt as you play, which takes a while." };
                AddPath(g, Path.Combine(game, "Distant_Horizons_server_data"));
                groups.Add(g);
                g = new CleanGroup { Id = "mods-leftovers", Where = where, Label = "Unfinished downloads", Description = "Half-downloaded jars left in the mods folder.", Blocked = blocked };
                AddFiles(g, modsDir, "*.download", null);
                AddFiles(g, modsDir, "*.restoring", null);
                AddFiles(g, modsDir, "*.tmp", null);
                groups.Add(g);
            }

            string server = serverDir;
            if (server != null && Directory.Exists(server))
            {
                Dictionary<string, string> props = ReadProperties(Path.Combine(server, "server.properties"));
                string blocked = ServerBusy() || ExternalServerRunning(server, props) ? "Stop the server first." : null;
                const string where = "Server";
                var g = new CleanGroup { Id = "server-logs", Where = where, Label = "Old server logs", Description = "Logs from earlier runs (the current latest.log is kept).", Blocked = blocked };
                AddFiles(g, Path.Combine(server, "logs"), "*", f => KeepLogs.IsMatch(f.Name));
                groups.Add(g);
                g = new CleanGroup { Id = "server-loose", Where = where, Label = "Loose log files", Description = "Captured console logs sitting in the server folder itself.", Blocked = blocked };
                AddFiles(g, server, "*.log", f => f.Name.StartsWith("hs_err_pid", StringComparison.OrdinalIgnoreCase));
                groups.Add(g);
                g = new CleanGroup { Id = "server-crash", Where = where, Label = "Crash reports", Description = "Reports the server wrote when it crashed.", Blocked = blocked };
                AddFiles(g, Path.Combine(server, "crash-reports"), "*", null);
                groups.Add(g);
                g = new CleanGroup { Id = "server-dumps", Where = where, Label = "Java crash dumps", Description = "hs_err_pid / replay_pid files Java leaves after a hard crash.", Blocked = blocked };
                AddFiles(g, server, "hs_err_pid*.log", null);
                AddFiles(g, server, "replay_pid*.log", null);
                groups.Add(g);
                g = new CleanGroup { Id = "server-debug", Where = where, Label = "Debug output", Description = ".mixin.out, which only matters when debugging mods.", Blocked = blocked };
                AddPath(g, Path.Combine(server, ".mixin.out"));
                groups.Add(g);
                string level;
                if (!props.TryGetValue("level-name", out level) || string.IsNullOrWhiteSpace(level)) level = "world";
                g = new CleanGroup { Id = "server-dh", Where = where, Label = "Distant Horizons world data", Risk = "careful", Blocked = blocked,
                    Description = "The server's far-away terrain (LODs) for " + level + ". The world itself isn't touched, but the server has to generate this again, which takes hours and a lot of CPU." };
                try
                {
                    string world = Path.Combine(server, level);
                    if (Directory.Exists(world))
                        foreach (string file in Directory.EnumerateFiles(world, "DistantHorizons.sqlite*", SearchOption.AllDirectories)) AddPath(g, file);
                }
                catch { }
                groups.Add(g);
            }

            var cache = new CleanGroup { Id = "browser-cache", Where = "This app", Label = "Built-in browser cache", Description = "Cached pages and images from the tabs. Sign-ins and cookies are kept." };
            string profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"MinecraftModManager\WebView2\EBWebView\Default");
            foreach (string sub in new[] { "Cache", "Code Cache", "GPUCache", @"Service Worker\CacheStorage" })
            {
                var part = new CleanGroup();
                AddPath(part, Path.Combine(profile, sub));
                cache.Size += part.Size;
                cache.Files += part.Files;
            }
            if (window == null) cache.Blocked = "Only available in the app window.";
            groups.Add(cache);
            var backups = new CleanGroup { Id = "sync-backups", Where = "This app", Label = "Sync backups", Description = "Copies of configs that syncing overwrote." };
            AddPath(backups, Path.Combine(dataDir, "sync-backups"));
            groups.Add(backups);
            return groups;
        }

        static Dictionary<string, object> CleanupScan()
        {
            List<CleanGroup> groups = ScanCleanup();
            return Obj("ok", true, "total", groups.Where(g => g.Blocked == null).Sum(g => g.Size),
                "groups", groups.Select(g => (object)Obj("id", g.Id, "where", g.Where, "label", g.Label, "description", g.Description, "risk", g.Risk,
                    "blocked", g.Blocked, "size", g.Size, "files", g.Files)).ToList());
        }

        static Dictionary<string, object> CleanupRun(List<string> ids)
        {
            if (ids.Count == 0) throw new UserError("Nothing was ticked.");
            var done = new List<object>();
            var failed = new List<string>();
            long freed = 0;
            foreach (CleanGroup g in ScanCleanup().Where(x => ids.Contains(x.Id)))
            {
                if (g.Blocked != null) { failed.Add(g.Label + ": " + g.Blocked); continue; }
                if (g.Size == 0 && g.Files == 0) continue;
                if (g.Id == "browser-cache")
                {
                    Task task = null;
                    ui.Send(delegate { task = window.ClearBrowserCache(); }, null);
                    try
                    {
                        if (task == null || !task.Wait(20000)) throw new UserError("timed out");
                        freed += g.Size;
                        done.Add(g.Label);
                        Log("Cleanup   cleared the built-in browser cache");
                    }
                    catch (Exception ex)
                    {
                        failed.Add(g.Label + ": " + (ex.InnerException ?? ex).Message);
                    }
                    continue;
                }
                if (RecyclePaths(g.Paths))
                {
                    freed += g.Size;
                    done.Add(g.Label + (g.Where == "This app" ? "" : " (" + g.Where + ")"));
                    Log("Cleanup   " + g.Where + ": " + g.Label + " (" + g.Files + " files, " + FormatSize(g.Size) + ") moved to the Recycle Bin");
                }
                else failed.Add(g.Label + ": Windows couldn't move everything to the Recycle Bin (some files may be in use).");
            }
            return Obj("ok", true, "done", done, "failed", failed, "freed", freed);
        }
    }
}
