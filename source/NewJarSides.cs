// New jars get their client / server tags by themselves. Whenever the mods folder is read, jars the app
// hasn't seen in that folder before are noted; ones without a client or server tag are run through the
// same detection as the Detect button (Sides.cs: the jar's own metadata and code, Modrinth's flags, what's
// inside it, CurseForge) and tagged with the side it finds. A library the new mod requires gets the new
// mod's sides added too, so it isn't left off the server. Tags already there are never changed or removed.
// It covers every way a jar arrives: installs from either site, drops, site-tab downloads, and jars copied
// in by hand. The first look at a folder only remembers what's there. Settings > Mod list can turn it off.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace MinecraftModManager
{
    static partial class Program
    {
        static readonly object SeenJarsLock = new object();
        static int sideJobRunning;

        static string SeenJarsPath() { return Path.Combine(dataDir, "seen-jars.json"); }

        // Called with the jars just listed; starts tagging in the background when there are new ones.
        static void NoticeJars(string dir, IEnumerable<string> names)
        {
            try
            {
                List<string> now = names.Select(NormalizeKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var fresh = new List<string>();
                lock (SeenJarsLock)
                {
                    Dictionary<string, object> all = null;
                    try { if (File.Exists(SeenJarsPath())) all = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(File.ReadAllText(SeenJarsPath())) as Dictionary<string, object>; }
                    catch { }
                    all = all ?? new Dictionary<string, object>();
                    string folder = Path.GetFullPath(dir).TrimEnd('\\').ToLowerInvariant();
                    object[] before = A(Get(all, folder));
                    bool firstLook = !all.ContainsKey(folder);
                    var seen = new HashSet<string>(before.OfType<string>(), StringComparer.OrdinalIgnoreCase);
                    fresh = firstLook ? new List<string>() : now.Where(n => !seen.Contains(n)).ToList();
                    if (!firstLook && fresh.Count == 0 && seen.SetEquals(now)) return;
                    all[folder] = now;
                    Directory.CreateDirectory(dataDir);
                    File.WriteAllText(SeenJarsPath(), new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(all), new UTF8Encoding(false));
                }
                if (fresh.Count == 0 || !PrefFlag("tagSides", true) || HasLinkedTags(dir)) return;
                ThreadPool.QueueUserWorkItem(delegate { TagSidesOfNewJars(dir, fresh); });
            }
            catch (Exception ex) { Log("Sides     couldn't note new jars: " + ex.Message); }
        }

        static void TagSidesOfNewJars(string dir, List<string> fresh)
        {
            // One detection at a time; a second batch waits for the first.
            while (Interlocked.CompareExchange(ref sideJobRunning, 1, 0) != 0) Thread.Sleep(500);
            try
            {
                if (!SamePath(dir, modsDir)) return; // the folder was switched meanwhile
                TagsDoc tags = LoadTagsDoc(Path.Combine(dir, "mod-tags.json"));
                Func<string, List<string>> tagsOf = key =>
                {
                    string k = tags.Mods.Keys.FirstOrDefault(x => string.Equals(x, key, StringComparison.OrdinalIgnoreCase));
                    return k == null ? new List<string>() : tags.Mods[k];
                };
                Func<List<string>, int> bitsOf = list => (list.Contains("client") ? 1 : 0) | (list.Contains("server") ? 2 : 0);
                List<string> untagged = fresh.Where(k => bitsOf(tagsOf(k)) == 0).ToList();
                if (untagged.Count == 0) return;

                Dictionary<string, object> result = DetectSides(PrefFlag("serverExtras", true));
                // A jar and a .disabled copy of it share a key; the first guess for a key is kept.
                var guesses = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
                foreach (Dictionary<string, object> m in BodyList(Get(result, "mods")).Select(D))
                    if (m != null && S(m, "key") != null && !guesses.ContainsKey(S(m, "key"))) guesses[S(m, "key")] = m;
                var done = new List<string>();
                foreach (string key in untagged)
                {
                    Dictionary<string, object> g;
                    if (!guesses.TryGetValue(key, out g)) continue;
                    int bits = SideBits(S(g, "side"));
                    if (bits == 0) continue;
                    var add = new List<string>();
                    if ((bits & 1) != 0) add.Add("client");
                    if ((bits & 2) != 0) add.Add("server");
                    try
                    {
                        UpdateTags(new List<string> { key }, add, new List<string>());
                        done.Add(key + " -> " + string.Join("+", add));
                    }
                    catch (UserError ex) { Log("Sides     couldn't tag " + key + ": " + ex.Message); }
                }

                // The libraries the new mods require: add the sides the new mods need them on.
                var providers = new Dictionary<string, List<FileInfo>>(StringComparer.OrdinalIgnoreCase);
                List<FileInfo> jars = new DirectoryInfo(dir).GetFiles().Where(f => JarName.IsMatch(f.Name)).ToList();
                foreach (FileInfo f in jars)
                    foreach (ModInfo m in GetJarMeta(f).Mods)
                    {
                        List<FileInfo> list;
                        if (!providers.TryGetValue(m.Id, out list)) providers[m.Id] = list = new List<FileInfo>();
                        list.Add(f);
                    }
                tags = LoadTagsDoc(Path.Combine(dir, "mod-tags.json"));
                foreach (string key in fresh)
                {
                    FileInfo jar = jars.FirstOrDefault(f => string.Equals(NormalizeKey(f.Name), key, StringComparison.OrdinalIgnoreCase));
                    if (jar == null) continue;
                    int mine = bitsOf(tagsOf(key));
                    if (mine == 0) continue;
                    JarMeta meta = GetJarMeta(jar);
                    foreach (DepInfo dep in meta.Deps)
                    {
                        if (dep.Type != "required" || dep.Id == null || BuiltInIds.Contains(dep.Id)) continue;
                        int need = mine & (dep.Side == "CLIENT" ? 1 : 3); // "server only" needs still go to the client: single player
                        List<FileInfo> libs;
                        if (need == 0 || !providers.TryGetValue(dep.Id, out libs)) continue;
                        foreach (FileInfo lib in libs.Where(l => l != jar))
                        {
                            string libKey = NormalizeKey(lib.Name);
                            int has = bitsOf(tagsOf(libKey));
                            int added = need & ~has;
                            if (has == 0 || added == 0) continue; // untagged ones are the user's (or Detect's) call
                            var add = new List<string>();
                            if ((added & 1) != 0) add.Add("client");
                            if ((added & 2) != 0) add.Add("server");
                            try
                            {
                                UpdateTags(new List<string> { libKey }, add, new List<string>());
                                done.Add(libKey + " +" + string.Join("+", add) + " (needed by " + key + ")");
                                tags = LoadTagsDoc(Path.Combine(dir, "mod-tags.json"));
                            }
                            catch (UserError ex) { Log("Sides     couldn't tag " + libKey + ": " + ex.Message); }
                        }
                    }
                }
                if (done.Count == 0) return;
                Log("Sides     new jars tagged: " + string.Join(", ", done));
                if (window != null && !window.IsDisposed)
                    try { window.BeginInvoke((Action)(() => { if (!window.IsDisposed) window.Notify("refresh"); })); } catch (InvalidOperationException) { }
            }
            catch (Exception ex) { Log("Sides     couldn't tag new jars: " + ex.Message); }
            finally { Interlocked.Exchange(ref sideJobRunning, 0); }
        }
    }
}
