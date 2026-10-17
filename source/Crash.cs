// Reading the game's last crash: the newest crash report (or latest.log, when the game died without
// one) is searched for the mods it names, and each gets a score. NeoForge says a lot itself: "Mod
// loading issue for" blocks, "Suspected Mods", stack frames tagged with mod ids or jar names, and
// "Mixin ... from mod" lines. Only reads files; turning a mod off is the page's choice.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MinecraftModManager
{
    static partial class Program
    {
        static readonly HashSet<string> NotSuspects = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "minecraft", "neoforge", "forge", "fml", "javafml", "mcp", "java", "jdk", "lowcodefml", "loader", "fabricloader", "fabric-loader", "quilt_loader" };

        // The newest crash report's name and time (cheap: only a folder listing), for the page's banner.
        static Dictionary<string, object> LastCrashInfo()
        {
            try
            {
                var dir = new DirectoryInfo(Path.Combine(GameDir(), "crash-reports"));
                if (!dir.Exists) return null;
                FileInfo newest = dir.GetFiles("crash-*.txt").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
                return newest == null ? null : Obj("file", newest.Name, "time", newest.LastWriteTimeUtc.ToString("o"));
            }
            catch { return null; }
        }

        static string ReadShared(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8)) return reader.ReadToEnd();
        }

        sealed class Suspect
        {
            public string Id, File, Name;
            public int Score;
            public bool TurnOn; // it's turned off, and turning it on is the fix
            public readonly List<string> Reasons = new List<string>();
            public readonly List<string> Missing = new List<string>();
        }

        static Dictionary<string, object> ReadLastCrash()
        {
            string mods = modsDir;
            string game = GameDir();
            var crashDir = new DirectoryInfo(Path.Combine(game, "crash-reports"));
            FileInfo report = crashDir.Exists ? crashDir.GetFiles("crash-*.txt").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault() : null;
            var log = new FileInfo(Path.Combine(game, "logs", "latest.log"));
            string text = null, source = null;
            DateTime when = DateTime.MinValue;
            if (report != null) { text = ReadShared(report.FullName); source = report.FullName; when = report.LastWriteTimeUtc; }
            // A newer log that ends in an error (no crash report written) is the better evidence.
            if (log.Exists && (report == null || log.LastWriteTimeUtc > report.LastWriteTimeUtc.AddMinutes(1)))
            {
                string logText = ReadShared(log.FullName);
                // Only signs of a real crash: a healthy game logs plenty of ERROR lines too.
                if (Regex.IsMatch(logText, @"/FATAL\]|Exception in thread ""(main|Render thread|Server thread)""|Mod loading has failed|Missing or unsupported mandatory dependencies|Encountered an unexpected exception|Crash report saved"))
                {
                    if (text == null || !logText.Contains("Crash report saved")) { text = logText; source = log.FullName; when = log.LastWriteTimeUtc; }
                }
            }
            if (text == null) return Obj("ok", true, "found", false);

            // Mod ids and jar names in this folder, to point at files.
            var jars = new DirectoryInfo(mods).GetFiles().Where(f => JarName.IsMatch(f.Name)).ToList();
            var byId = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (FileInfo jar in jars)
            {
                JarMeta meta = GetJarMeta(jar);
                foreach (ModInfo m in meta.Mods) { if (!byId.ContainsKey(m.Id)) byId[m.Id] = jar; names[m.Id] = m.Name ?? m.Id; }
            }
            var byFile = jars.ToDictionary(f => Regex.Replace(f.Name, @"\.disabled$", "", RegexOptions.IgnoreCase), f => f, StringComparer.OrdinalIgnoreCase);
            var suspects = new Dictionary<string, Suspect>(StringComparer.OrdinalIgnoreCase);
            Func<string, string, Suspect> suspect = (id, file) =>
            {
                FileInfo jar = null;
                if (id != null && NotSuspects.Contains(id)) return null;
                if (file != null) byFile.TryGetValue(Regex.Replace(Path.GetFileName(file), @"\.disabled$", "", RegexOptions.IgnoreCase), out jar);
                if (jar == null && id != null) byId.TryGetValue(id, out jar);
                string key = jar != null ? jar.Name : id ?? file;
                if (key == null) return null;
                Suspect s;
                if (!suspects.TryGetValue(key, out s))
                {
                    JarMeta meta = jar == null ? null : GetJarMeta(jar);
                    ModInfo main = meta == null ? null : MainMod(meta);
                    suspects[key] = s = new Suspect { Id = id ?? (main != null ? main.Id : null), File = jar == null ? null : jar.Name,
                        Name = (main != null ? main.Name : null) ?? (id != null && names.ContainsKey(id) ? names[id] : null) ?? id ?? Path.GetFileName(file) };
                }
                return s;
            };
            Action<Suspect, int, string> note = (s, score, reason) =>
            {
                if (s == null) return;
                s.Score += score;
                if (reason != null && !s.Reasons.Contains(reason) && s.Reasons.Count < 4) s.Reasons.Add(reason);
            };

            // 1. NeoForge's own list of mods that failed to load, with why.
            foreach (Match m in Regex.Matches(text, @"-- Mod loading issue for: (\S+) --\s*\r?\nDetails:\s*\r?\n(?:\s*Mod file: (.+?)\r?\n)?\s*Failure message: (.+?)\r?\n((?:\t\t.*\r?\n)*)"))
            {
                string id = m.Groups[1].Value, file = m.Groups[2].Success ? m.Groups[2].Value.Trim() : null;
                string why = (m.Groups[3].Value.Trim() + " " + Regex.Replace(m.Groups[4].Value, @"\s+", " ").Trim()).Trim();
                Suspect s = suspect(id, file);
                note(s, 12, why);
                if (s != null)
                    foreach (Match need in Regex.Matches(why, @"requires (\S+) .*?(?:Currently, \S+ is not installed|not installed)"))
                        if (!s.Missing.Contains(need.Groups[1].Value)) s.Missing.Add(need.Groups[1].Value);
            }
            // 2. "Suspected Mods:" with one "Name (id), Version: x" per line.
            foreach (Match block in Regex.Matches(text, @"Suspected Mods?:\s*\r?\n((?:\t.*\r?\n)+)"))
                foreach (Match m in Regex.Matches(block.Groups[1].Value, @"^\t(.+?) \(([A-Za-z0-9_\-.]+)\), Version: ", RegexOptions.Multiline))
                    note(suspect(m.Groups[2].Value, null), 8, "Named as a suspect in the crash report");
            foreach (Match m in Regex.Matches(text, @"Suspected Mods?: ([^\r\n]+)"))
                foreach (Match one in Regex.Matches(m.Groups[1].Value, @"([^,()]+?) \(([A-Za-z0-9_\-.]+)\)"))
                    note(suspect(one.Groups[2].Value, null), 8, "Named as a suspect in the crash report");
            // 3. Stack frames: the first exception's frames, the top ones counting most.
            string head = text;
            int cut = text.IndexOf("A detailed walkthrough of the error", StringComparison.Ordinal);
            if (cut > 0) head = text.Substring(0, cut);
            string exception = Regex.Match(head, @"^(?:[\w.$]+(?:Exception|Error)[^\r\n]*)", RegexOptions.Multiline).Value;
            // The exception and each "Caused by": the last one is the root cause, so it counts most.
            var sections = Regex.Split(head, @"(?=^Caused by: )", RegexOptions.Multiline).Where(x => x.Trim().Length > 0).ToList();
            ClassIndex index = ClassIndexFor(jars);
            for (int si = 0; si < sections.Count; si++)
            {
                string section = sections[si];
                bool root = si == sections.Count - 1;
                int frame = 0, annotations = 0;
                foreach (Match m in Regex.Matches(section, @"^\s*at (\S+)(.*)$", RegexOptions.Multiline))
                {
                    frame++;
                    int weight = (frame <= 3 ? 4 : frame <= 10 ? 2 : 1) * (root ? 2 : 1);
                    // A frame counts once per mod, and only for mods in this folder (the game's own jar isn't a suspect).
                    var seen = new HashSet<Suspect>();
                    Action<Suspect, int, string> hit = (s, w, why) => { if (s != null && s.File != null && seen.Add(s)) note(s, w, why); };
                    // The code that was running: a mod's own module, jar, or (untagged) package.
                    Match tagged = Regex.Match(m.Value, @"(?:TRANSFORMER|MC-BOOTSTRAP|SECURE-BOOTSTRAP|PLUGIN|GAME)/([A-Za-z0-9_\-.]+)@");
                    if (tagged.Success && !NotSuspects.Contains(tagged.Groups[1].Value) && byId.ContainsKey(tagged.Groups[1].Value))
                        hit(suspect(tagged.Groups[1].Value, null), weight, "Its code was running when it crashed");
                    Match jar = Regex.Match(m.Value, @"\[([^\]\s%/]+\.jar)");
                    if (jar.Success && byFile.ContainsKey(jar.Groups[1].Value)) hit(suspect(null, jar.Groups[1].Value), weight, "Its code was running when it crashed");
                    string cls = Regex.Replace(m.Groups[1].Value, @"^(?:[^/]+/)*", "");
                    FileInfo owner = index.Find(Regex.Replace(cls, @"\.[^.]+\(.*$|\.[^.(]+$", ""));
                    if (owner != null) hit(suspect(null, owner.Name), weight, "Its code was running when it crashed");
                    // Mixins listed on a frame were applied to code that was running: a hint only, not a failure.
                    if (annotations < 12)
                        foreach (Match applied in Regex.Matches(m.Value, @"pl:mixin:APP:[^:\s,]+:[^\s,]+ from mod ([A-Za-z0-9_\-.]+)"))
                        {
                            if (frame > 4) break;
                            annotations++;
                            hit(suspect(applied.Groups[1].Value, null), 1, "It changes code that was running (a mixin)");
                        }
                }
                // Mod ids in the error message itself ("Registry object not present: somemod:thing").
                string message = Regex.Match(section, @"^(?:Caused by: )?[\w.$]+(?:Exception|Error|Throwable)[^\r\n]*", RegexOptions.Multiline).Value;
                foreach (Match rl in Regex.Matches(message, @"\b([a-z0-9_.\-]+):[a-z0-9_/.\-]+"))
                    if (byId.ContainsKey(rl.Groups[1].Value) && !NotSuspects.Contains(rl.Groups[1].Value))
                        note(suspect(rl.Groups[1].Value, null), root ? 8 : 4, "Named in the error message");
            }

            // 4. A mixin that really failed: "Attach error for x.mixins.json:Y from mod z", "InvalidInjectionException ...".
            var failedMods = new List<Suspect>();
            foreach (Match m in Regex.Matches(head, @"^(?:Caused by: )?[\w.$]*(?:Mixin\w*Exception|Mixin\w*Error|InvalidInjection\w*|Injection\w*Exception|InvalidMixin\w*)[^\r\n]*?([A-Za-z0-9_\-.]+\.json):([\w.$]+) from mod ([A-Za-z0-9_\-.]+)([^\r\n]*)", RegexOptions.Multiline))
            {
                Suspect s = suspect(m.Groups[3].Value, null);
                if (s == null) continue;
                string detail = Regex.Replace(m.Groups[4].Value, @"\s+", " ").Trim();
                note(s, 16, "Its mixin " + m.Groups[2].Value + " couldn't be applied" + (detail.Length > 0 ? " (" + (detail.Length > 140 ? detail.Substring(0, 140) + "…" : detail).TrimStart(' ', ':', '-') + ")" : ""));
                if (!failedMods.Contains(s)) failedMods.Add(s);
            }
            foreach (Match m in Regex.Matches(head, @"Mixin \[([A-Za-z0-9_\-.]+\.json):([\w.$]+)\] from mod ([A-Za-z0-9_\-.]+) failed"))
            {
                Suspect s = suspect(m.Groups[3].Value, null);
                note(s, 16, "Its mixin " + m.Groups[2].Value + " couldn't be applied");
                if (s != null && !failedMods.Contains(s)) failedMods.Add(s);
            }

            // 5. A class that couldn't be found: whose is it? A turned-off jar, or a mod nobody installed.
            foreach (Match m in Regex.Matches(head, @"(?:ClassNotFoundException|NoClassDefFoundError):?\s+([\w.$/]+)"))
            {
                string missingClass = m.Groups[1].Value.Replace('/', '.');
                if (Regex.IsMatch(missingClass, @"^(java|javax|jdk|sun|net\.minecraft|com\.mojang)\.")) continue;
                FileInfo owner = index.Find(missingClass.Contains(".") ? missingClass.Substring(0, missingClass.LastIndexOf('.')) : missingClass);
                if (owner != null && owner.Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                {
                    Suspect off = suspect(null, owner.Name);
                    note(off, 18, "It's turned off, but the game needed its code (" + missingClass + ")" + (failedMods.Count > 0 ? " for " + string.Join(", ", failedMods.Select(x => x.Name).Distinct()) : "") + ".");
                    if (off != null) off.TurnOn = true;
                    continue;
                }
                if (owner != null) continue; // it's there and on: some other problem (a load order or version clash)
                // Nobody has it: the mod that wanted it is missing a dependency. Which one, from what it requires.
                foreach (Suspect s in failedMods)
                {
                    note(s, 6, "It needs " + missingClass + ", which no jar here has");
                    foreach (string dep in MissingRequirements(s.File, byId))
                        if (!s.Missing.Contains(dep)) { s.Missing.Add(dep); note(s, 6, "It requires " + dep + ", which isn't installed"); }
                }
            }
            // A mod that failed while one of its required mods is missing or off: that's very likely why.
            foreach (Suspect s in suspects.Values.Where(x => x.Score >= 8 && x.File != null).ToList())
                foreach (string dep in MissingRequirements(s.File, byId))
                    if (!s.Missing.Contains(dep)) { s.Missing.Add(dep); note(s, 4, "It requires " + dep + ", which isn't installed"); }

            string description = Regex.Match(text, @"^Description: ([^\r\n]+)", RegexOptions.Multiline).Groups[1].Value;
            List<Suspect> ranked = suspects.Values.Where(s => s.Score > 0).OrderByDescending(s => s.Score).Take(8).ToList();
            List<object> list = ranked.Select(s => (object)Obj("id", s.Id, "file", s.File, "name", s.Name, "score", s.Score, "reasons", s.Reasons,
                "missing", s.Missing, "turnOn", s.TurnOn)).ToList();
            // How sure: a clear leader with direct evidence, or only hints.
            int top = ranked.Count > 0 ? ranked[0].Score : 0, second = ranked.Count > 1 ? ranked[1].Score : 0;
            string confidence = top >= 16 && top >= second * 2 ? "high" : top >= 8 && top > second ? "medium" : "low";
            // The root cause: the last "Caused by" (the first exception is often only a wrapper).
            string cause = Regex.Matches(head, @"^Caused by: ([^\r\n]+)", RegexOptions.Multiline).Cast<Match>().Select(m => m.Groups[1].Value).LastOrDefault();
            return Obj("ok", true, "found", true, "path", source, "name", Path.GetFileName(source), "time", when.ToString("o"),
                "fromLog", source != null && source.EndsWith("latest.log", StringComparison.OrdinalIgnoreCase),
                "description", description.Length > 0 ? description : null, "exception", exception.Length > 0 ? exception : null,
                "cause", cause != null && cause != exception ? cause : null, "confidence", confidence, "suspects", list);
        }
    
        // Which jar a Java package lives in, for stack frames without a mod tag and for classes that couldn't be
        // found. Turned-off jars are included (that's how "it's turned off" is spotted); a package shipped by
        // two jars that are both on (a shaded library) is left out, since it can't point at one mod.
        sealed class ClassIndex
        {
            public readonly Dictionary<string, FileInfo> Owner = new Dictionary<string, FileInfo>(StringComparer.Ordinal);
            public readonly HashSet<string> Shared = new HashSet<string>(StringComparer.Ordinal);

            public FileInfo Find(string package)
            {
                if (string.IsNullOrEmpty(package)) return null;
                string p = package.Replace('$', '.');
                while (p.Count(c => c == '.') >= 1)
                {
                    FileInfo f;
                    if (Shared.Contains(p)) return null;
                    if (Owner.TryGetValue(p, out f)) return f;
                    p = p.Substring(0, p.LastIndexOf('.'));
                }
                return null;
            }
        }

        static readonly Dictionary<string, List<string>> PackagesByJar = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        static List<string> JarPackages(FileInfo jar)
        {
            string key = jar.FullName + "|" + jar.Length + "|" + jar.LastWriteTimeUtc.Ticks;
            lock (PackagesByJar)
            {
                List<string> cached;
                if (PackagesByJar.TryGetValue(key, out cached)) return cached;
            }
            var packages = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                using (var zip = System.IO.Compression.ZipFile.OpenRead(jar.FullName))
                    foreach (var entry in zip.Entries)
                    {
                        string n = entry.FullName;
                        if (!n.EndsWith(".class", StringComparison.Ordinal) || n.StartsWith("META-INF/", StringComparison.Ordinal)) continue;
                        int slash = n.LastIndexOf('/');
                        if (slash > 0) packages.Add(n.Substring(0, slash).Replace('/', '.'));
                    }
            }
            catch (Exception) { }
            var list = packages.ToList();
            lock (PackagesByJar) PackagesByJar[key] = list;
            return list;
        }

        static ClassIndex ClassIndexFor(List<FileInfo> jars)
        {
            var index = new ClassIndex();
            var onOwner = new Dictionary<string, FileInfo>(StringComparer.Ordinal);
            // Jars that are on first, so a package in an old turned-off copy still points at the one in use.
            foreach (FileInfo jar in jars.OrderBy(j => j.Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? 1 : 0))
            {
                bool on = !jar.Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                foreach (string p in JarPackages(jar))
                {
                    if (Regex.IsMatch(p, @"^(java|javax|jdk|sun|net\.minecraft|com\.mojang|org\.spongepowered|org\.objectweb|kotlin|com\.google|org\.apache)\b")) continue;
                    FileInfo had;
                    if (!index.Owner.TryGetValue(p, out had)) { index.Owner[p] = jar; if (on) onOwner[p] = jar; }
                    else if (on && onOwner.ContainsKey(p) && onOwner[p] != jar) index.Shared.Add(p);
                }
            }
            return index;
        }

        // The required mods of a jar that aren't here at all (ids, for "Find ...").
        static List<string> MissingRequirements(string file, Dictionary<string, FileInfo> byId)
        {
            var missing = new List<string>();
            if (file == null) return missing;
            var jar = new FileInfo(Path.Combine(modsDir, file));
            if (!jar.Exists) return missing;
            foreach (DepInfo dep in GetJarMeta(jar).Deps)
                if (dep.Type == "required" && dep.Id != null && !BuiltInIds.Contains(dep.Id) && !NotSuspects.Contains(dep.Id) && !byId.ContainsKey(dep.Id) && !missing.Contains(dep.Id))
                    missing.Add(dep.Id);
            return missing;
        }

        // Opens the crash report (or latest.log) the reader used, in the usual text editor.
        static void OpenCrashFile(string name)
        {
            string game = GameDir();
            string path = name == "latest.log" ? Path.Combine(game, "logs", "latest.log") : Path.Combine(game, "crash-reports", Path.GetFileName(name ?? ""));
            if (!Regex.IsMatch(Path.GetFileName(path), @"^(crash-.*\.txt|latest\.log)$") || !File.Exists(path)) throw new UserError("That crash report isn't there any more.");
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
            catch { System.Diagnostics.Process.Start("notepad.exe", "\"" + path + "\""); }
        }
    }
}