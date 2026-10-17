// Mod configs kept the same everywhere, automatically.
//  - Between instances on this PC: each instance opts in (its Settings, "Mod configs"). The configs live in
//    <data>\shared\config. A config changed in a sharing game is picked up (the newest change wins) and handed
//    to every other sharing instance that has that mod, each time Play is pressed here and when the app
//    starts. Joining works like game settings: the shared configs win, and ones only this instance has are
//    added. A game that's running is left alone until next time.
//  - The server follows the game: a config changed in this game (since the last look) is copied to the
//    server before the server starts from here, and every few minutes while it's stopped. Configs only the
//    server has, edits made only on the server, and client-only configs are left alone; the first look at a
//    config only remembers it. Overwritten files are backed up under sync-backups.
// "Changed" means its settings changed: a mod rewriting the same values (or a new timestamp comment) isn't.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace MinecraftModManager
{
    static partial class Program
    {
        // Leftovers and caches, not settings. Client configs are shared between instances (unlike the server).
        static readonly Regex SkipSharedConfig = new Regex(@"(\.bak|\.bak-[^\\]*|\.old|\.tmp|\.dat|\.log|\.lock|\.cache|\.db|\.zip|\.jar)$|^jei\\world\\|-\d+\.toml\.bak$",
            RegexOptions.IgnoreCase);
        const long MaxSharedConfig = 1 << 20;
        static readonly object ConfigShareLock = new object();

        static string SharedConfigDir() { return Path.Combine(SharedDir(), "config"); }
        static string ConfigShareStatePath() { return Path.Combine(SharedDir(), "configs.json"); }
        static string ConfigDirOf(InstanceInfo info) { return Path.Combine(GameDirOf(info), "config"); }

        // What a config says, ignoring comments and layout for text configs; the bytes otherwise.
        static string ConfigFingerprint(FileInfo f, string rel)
        {
            try
            {
                if (TextConfig(rel))
                {
                    Dictionary<string, string> values = ConfigValues(File.ReadAllLines(f.FullName));
                    if (values.Count > 0)
                    {
                        string text = string.Join("\n", values.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value));
                        using (var sha = SHA1.Create())
                            return "v:" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "");
                    }
                }
                return "b:" + FileSha1(f);
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        static Dictionary<string, FileInfo> ShareableConfigs(string root)
        {
            return ConfigFiles(root).Where(kv => !SkipSharedConfig.IsMatch(kv.Key) && kv.Value.Length <= MaxSharedConfig)
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        }

        static HashSet<string> ModIdsIn(string mods)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(mods)) return ids;
            foreach (FileInfo f in new DirectoryInfo(mods).GetFiles().Where(f => JarName.IsMatch(f.Name)))
                try { foreach (ModInfo m in GetJarMeta(f).Mods) if (m.Id != null) ids.Add(m.Id); } catch (Exception) { }
            return ids;
        }

        static void BackUpConfig(string file, string stamp, string side, string rel)
        {
            if (!File.Exists(file)) return;
            string backup = Path.Combine(dataDir, "sync-backups", stamp, side, "config", rel);
            Directory.CreateDirectory(Path.GetDirectoryName(backup));
            File.Copy(file, backup, true);
        }

        static void CopyConfig(string from, string to)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to));
            File.Copy(from, to + ".copying", true);
            if (File.Exists(to)) File.Delete(to);
            File.Move(to + ".copying", to);
        }

        // ---------- Between instances ----------

        sealed class ConfigShareState
        {
            public readonly List<string> Instances = new List<string>();
            // Per instance: each config's fingerprint when it was last handed out or picked up.
            public readonly Dictionary<string, Dictionary<string, string>> Base = new Dictionary<string, Dictionary<string, string>>();
        }

        static ConfigShareState LoadConfigShareState()
        {
            var state = new ConfigShareState();
            try
            {
                if (!File.Exists(ConfigShareStatePath())) return state;
                var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(File.ReadAllText(ConfigShareStatePath())) as Dictionary<string, object>;
                foreach (object id in A(Get(json, "instances"))) if (id is string) state.Instances.Add((string)id);
                Dictionary<string, object> bases = D(Get(json, "base"));
                if (bases != null)
                    foreach (KeyValuePair<string, object> kv in bases)
                    {
                        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        Dictionary<string, object> files = D(kv.Value);
                        if (files != null) foreach (KeyValuePair<string, object> f in files) if (f.Value is string) map[f.Key] = (string)f.Value;
                        state.Base[kv.Key] = map;
                    }
            }
            catch { }
            return state;
        }

        static void SaveConfigShareState(ConfigShareState state)
        {
            Directory.CreateDirectory(SharedDir());
            File.WriteAllText(ConfigShareStatePath(), new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(Obj("instances", state.Instances,
                "base", state.Base.ToDictionary(kv => kv.Key, kv => (object)kv.Value))), new UTF8Encoding(false));
        }

        static bool SharesConfigs(InstanceInfo info) { return LoadConfigShareState().Instances.Contains(info.Id); }

        // Picks up configs changed in any sharing game (oldest change first, so the newest wins), then hands the
        // shared ones to every sharing instance that has the mod. Returns how many files were written.
        static int SyncSharedConfigs()
        {
            lock (ConfigShareLock)
            {
                ConfigShareState state = LoadConfigShareState();
                if (state.Instances.Count == 0) return 0;
                List<InstanceInfo> idle = PrismInstances().Where(i => state.Instances.Contains(i.Id) && !GameRunning(GameDirOf(i))).ToList();
                string store = SharedConfigDir();
                Dictionary<string, FileInfo> shared = ShareableConfigs(store);

                // 1. Pick up: a config this instance changed since last time, or one the store doesn't have yet.
                var picks = new List<KeyValuePair<string, FileInfo>>();
                var pickedFrom = new HashSet<string>();
                foreach (InstanceInfo i in idle)
                {
                    Dictionary<string, string> seen;
                    state.Base.TryGetValue(i.Id, out seen);
                    foreach (KeyValuePair<string, FileInfo> kv in ShareableConfigs(ConfigDirOf(i)))
                    {
                        string before = null;
                        bool known = seen != null && seen.TryGetValue(kv.Key, out before);
                        if (known && before == null) known = false;
                        if (!known ? !shared.ContainsKey(kv.Key) : ConfigFingerprint(kv.Value, kv.Key) != before)
                        {
                            picks.Add(kv);
                            pickedFrom.Add(i.Name);
                        }
                    }
                }
                foreach (KeyValuePair<string, FileInfo> kv in picks.OrderBy(p => p.Value.LastWriteTimeUtc))
                {
                    try { CopyConfig(kv.Value.FullName, Path.Combine(store, kv.Key)); }
                    catch (Exception ex) { Log("Shared    couldn't pick up config " + kv.Key + ": " + ex.Message); }
                }
                if (picks.Count > 0) shared = ShareableConfigs(store);

                // 2. Hand out: to each sharing instance that has the file already or has the mod it belongs to.
                int written = 0;
                string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
                var fingerprints = shared.ToDictionary(kv => kv.Key, kv => ConfigFingerprint(kv.Value, kv.Key), StringComparer.OrdinalIgnoreCase);
                foreach (InstanceInfo i in idle)
                {
                    HashSet<string> modIds = ModIdsIn(i.Mods);
                    string dir = ConfigDirOf(i);
                    var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var updated = new List<string>();
                    foreach (KeyValuePair<string, FileInfo> kv in shared)
                    {
                        string target = Path.Combine(dir, kv.Key);
                        bool has = File.Exists(target);
                        if (!has && ConfigOwner(kv.Key, modIds) == null) continue;
                        string want = fingerprints[kv.Key];
                        if (want == null) continue;
                        if (has && ConfigFingerprint(new FileInfo(target), kv.Key) == want) { seen[kv.Key] = want; continue; }
                        try
                        {
                            BackUpConfig(target, stamp, Path.Combine("instances", i.Name), kv.Key);
                            CopyConfig(kv.Value.FullName, target);
                            seen[kv.Key] = want;
                            updated.Add(kv.Key);
                        }
                        catch (Exception ex) { Log("Shared    couldn't hand config " + kv.Key + " to " + i.Name + ": " + ex.Message); }
                    }
                    state.Base[i.Id] = seen;
                    written += updated.Count;
                    if (updated.Count > 0)
                        Log("Shared    " + Plural(updated.Count, "config", "configs") + " handed to " + i.Name + ": " + string.Join(", ", updated.Take(6)) + (updated.Count > 6 ? ", …" : ""));
                }
                SaveConfigShareState(state);
                if (pickedFrom.Count > 0) Log("Shared    configs picked up from " + string.Join(", ", pickedFrom));
                return written;
            }
        }

        static void TrySyncSharedConfigs()
        {
            try { SyncSharedConfigs(); }
            catch (Exception ex) { Log("Shared    mod configs couldn't be synced: " + ex.Message); }
        }

        // Joining: the shared configs win, and ones only this instance has are added to them.
        static string ShareConfigs(InstanceInfo info, bool share)
        {
            lock (ConfigShareLock)
            {
                ConfigShareState state = LoadConfigShareState();
                if (share == state.Instances.Contains(info.Id)) return null;
                if (share) state.Instances.Add(info.Id);
                else state.Instances.Remove(info.Id);
                state.Base.Remove(info.Id);
                SaveConfigShareState(state);
            }
            if (share && !GameRunning(GameDirOf(info))) SyncSharedConfigs();
            return share ? "mod configs shared" : "own mod configs";
        }

        // ---------- The server follows the game ----------

        static readonly object ServerConfigLock = new object();
        static Timer serverConfigTimer;
        static Dictionary<string, object> lastServerConfigSync;

        static string ServerConfigStatePath() { return Path.Combine(dataDir, "server-config-sync.json"); }

        static void StartServerConfigWatch()
        {
            serverConfigTimer = new Timer(delegate { TryAutoSyncServerConfigs(); }, null, TimeSpan.FromSeconds(45), TimeSpan.FromMinutes(3));
        }

        static void TryAutoSyncServerConfigs() { AutoSyncServerConfigsSafe(); }

        static List<string> AutoSyncServerConfigsSafe()
        {
            try { return AutoSyncServerConfigs(); }
            catch (Exception ex) { Log("Sync      automatic config sync failed: " + ex.Message); return new List<string>(); }
        }

        // Returns the configs copied to the server (empty when off, stopped short or nothing changed).
        static List<string> AutoSyncServerConfigs()
        {
            var copied = new List<string>();
            string server = serverDir;
            if (!PrefFlag("serverConfigsAuto", true) || string.IsNullOrEmpty(server) || !Directory.Exists(server) || ServerBusy()) return copied;
            if (!Directory.Exists(modsDir)) return copied;
            if (!Monitor.TryEnter(ServerConfigLock, TimeSpan.FromSeconds(30))) return copied;
            try
            {
                Dictionary<string, object> json = null;
                try { if (File.Exists(ServerConfigStatePath())) json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(File.ReadAllText(ServerConfigStatePath())) as Dictionary<string, object>; }
                catch { }
                // [game, server] fingerprints when last looked at; forgotten when the game or the server changes.
                var seen = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                if (json != null && string.Equals(S(json, "server"), server, StringComparison.OrdinalIgnoreCase) && string.Equals(S(json, "game"), GameDir(), StringComparison.OrdinalIgnoreCase))
                {
                    Dictionary<string, object> b = D(Get(json, "base"));
                    if (b != null) foreach (KeyValuePair<string, object> kv in b) { object[] pair = A(kv.Value); if (pair.Length == 2) seen[kv.Key] = new[] { pair[0] as string, pair[1] as string }; }
                }

                string gameConfig = Path.Combine(GameDir(), "config"), serverConfig = Path.Combine(server, "config");
                int ignored, same;
                var sameItems = new List<ConfigItem>();
                List<ConfigItem> items = ComputeConfigSync(gameConfig, serverConfig, out ignored, out same, sameItems);
                HashSet<string> onServer = ModIdsIn(Path.Combine(server, "mods"));
                var next = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                foreach (ConfigItem c in sameItems)
                {
                    string g = ConfigFingerprint(c.Game, c.Rel);
                    next[c.Rel] = new[] { g, g };
                }
                string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
                foreach (ConfigItem c in items)
                {
                    if (ServerBusy()) break; // started meanwhile: the rest waits
                    string g = c.Game == null ? null : ConfigFingerprint(c.Game, c.Rel);
                    string s = c.Server == null ? null : ConfigFingerprint(c.Server, c.Rel);
                    string[] was;
                    bool known = seen.TryGetValue(c.Rel, out was);
                    bool send = c.Kind == "differ" ? known && g != null && g != was[0]
                        : c.Kind == "game-only" && g != null && c.Owner != null && onServer.Contains(c.Owner);
                    if (send)
                    {
                        try
                        {
                            string dest = Path.Combine(serverConfig, c.Rel);
                            BackUpConfig(dest, stamp, "server", c.Rel);
                            CopyConfig(c.Game.FullName, dest);
                            copied.Add(c.Rel);
                            next[c.Rel] = new[] { g, g };
                            continue;
                        }
                        catch (Exception ex) { Log("Sync      couldn't copy config " + c.Rel + " to the server: " + ex.Message); }
                    }
                    if (g != null || s != null) next[c.Rel] = new[] { g, s };
                }
                File.WriteAllText(ServerConfigStatePath(), new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(Obj("server", server, "game", GameDir(),
                    "base", next.ToDictionary(kv => kv.Key, kv => (object)kv.Value))), new UTF8Encoding(false));
                if (copied.Count > 0)
                {
                    Log("Sync      " + Plural(copied.Count, "config", "configs") + " game -> server (automatic): " + string.Join(", ", copied.Take(8)) + (copied.Count > 8 ? ", …" : ""));
                    lastServerConfigSync = Obj("at", DateTime.UtcNow.ToString("o"), "files", copied);
                }
            }
            finally { Monitor.Exit(ServerConfigLock); }
            return copied;
        }
    }
}
