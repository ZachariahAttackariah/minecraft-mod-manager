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
                if (Regex.IsMatch(logText, @"/(FATAL|ERROR)\]|Exception in thread|Mod loading has failed|Crash report saved"))
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
                if (file != null) byFile.TryGetValue(Path.GetFileName(file), out jar);
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
            int frame = 0;
            foreach (Match m in Regex.Matches(head, @"^\s*at (\S+)(.*)$", RegexOptions.Multiline))
            {
                frame++;
                int weight = frame <= 3 ? 4 : frame <= 10 ? 2 : 1;
                // A frame counts once per mod, and only for mods in this folder (the game's own jar isn't a suspect).
                var seen = new HashSet<Suspect>();
                Action<Suspect, string> hit = (s, why) => { if (s != null && s.File != null && seen.Add(s)) note(s, weight, why); };
                Match tagged = Regex.Match(m.Value, @"(?:TRANSFORMER|MC-BOOTSTRAP|SECURE-BOOTSTRAP|PLUGIN|GAME)/([A-Za-z0-9_\-.]+)@");
                if (tagged.Success && !NotSuspects.Contains(tagged.Groups[1].Value) && byId.ContainsKey(tagged.Groups[1].Value)) hit(suspect(tagged.Groups[1].Value, null), "In the crash's stack trace");
                Match jar = Regex.Match(m.Value, @"\[([^\]\s%/]+\.jar)");
                if (jar.Success && byFile.ContainsKey(jar.Groups[1].Value)) hit(suspect(null, jar.Groups[1].Value), "In the crash's stack trace");
                foreach (Match mixin in Regex.Matches(m.Value, @"pl:mixin:APP:([A-Za-z0-9_\-]+?)(?:\.mixins|-common\.mixins|\.client\.mixins)?\.json"))
                    if (byId.ContainsKey(mixin.Groups[1].Value)) hit(suspect(mixin.Groups[1].Value, null), "Its mixin was in the crash's stack trace");
            }
            // 4. "Mixin ... from mod x" (a mixin that failed to apply).
            foreach (Match m in Regex.Matches(text, @"from mod ([A-Za-z0-9_\-.]+)"))
                note(suspect(m.Groups[1].Value, null), 6, "One of its mixins failed");

            string description = Regex.Match(text, @"^Description: ([^\r\n]+)", RegexOptions.Multiline).Groups[1].Value;
            List<object> list = suspects.Values.Where(s => s.Score > 0).OrderByDescending(s => s.Score).Take(8)
                .Select(s => (object)Obj("id", s.Id, "file", s.File, "name", s.Name, "score", s.Score, "reasons", s.Reasons, "missing", s.Missing)).ToList();
            return Obj("ok", true, "found", true, "path", source, "name", Path.GetFileName(source), "time", when.ToString("o"),
                "fromLog", source != null && source.EndsWith("latest.log", StringComparison.OrdinalIgnoreCase),
                "description", description.Length > 0 ? description : null, "exception", exception.Length > 0 ? exception : null, "suspects", list);
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