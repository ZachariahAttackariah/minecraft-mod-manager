// The config editor: the game's and the server's config folders as settings pages.
// TOML configs (what NeoForge mods write) are read setting by setting, with the description, range,
// allowed values and default NeoForge puts in the comment above each one, so the page can show switches,
// number boxes and dropdowns. Saving rewrites only the edited values' lines: comments, order and layout
// stay as they were. JSON and anything else are edited as text. Every save keeps the old file under
// sync-backups, and a file that changed on disk since it was opened isn't overwritten. Nothing is saved
// while the game (or, for the server's files, the server) is running: they rewrite configs as they close.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace MinecraftModManager
{
    static partial class Program
    {
        sealed class TomlEntry
        {
            public string Section = "", Key, Raw, Type;
            public int Line, EndLine;              // first and last line of the value (arrays can span lines)
            public string Prefix;                  // the line up to and including "= "
            public readonly List<string> Desc = new List<string>();
            public string Default, Min, Max;
            public List<string> Allowed;
        }

        sealed class TomlDoc
        {
            public string[] Lines;
            public string Newline = "\r\n";
            public readonly List<TomlEntry> Entries = new List<TomlEntry>();
            public readonly Dictionary<string, List<string>> SectionDesc = new Dictionary<string, List<string>>();
        }

        static readonly Regex TomlRange = new Regex(@"^Range:\s*(?:(?<min>[-+0-9.Ee]+)\s*~\s*(?<max>[-+0-9.Ee]+)|>\s*(?<min>[-+0-9.Ee]+)|<\s*(?<max>[-+0-9.Ee]+))\s*$", RegexOptions.IgnoreCase);
        static readonly Regex TomlInterval = new Regex(@"^Allowed values:\s*[\(\[]\s*(?<min>[-+0-9.Ee]*)\s*,\s*(?<max>[-+0-9.Ee]*)\s*[\)\]]\s*$", RegexOptions.IgnoreCase);
        static readonly Regex TomlAllowed = new Regex(@"^Allowed Values:\s*(?<list>.+)$", RegexOptions.IgnoreCase);
        static readonly Regex TomlDefault = new Regex(@"^Default:\s*(?<v>.*)$", RegexOptions.IgnoreCase);

        // A list from a request body (the serializer gives ArrayList there, not object[]).
        static List<object> BodyList(object value)
        {
            if (value is string || value == null) return new List<object>();
            var items = value as System.Collections.IEnumerable;
            return items == null ? new List<object>() : items.Cast<object>().ToList();
        }

        static string ConfigRoot(string side)
        {
            if (side == "server")
            {
                string server = serverDir;
                if (string.IsNullOrEmpty(server)) throw new UserError("No server is set up.");
                return Path.Combine(server, "config");
            }
            return Path.Combine(GameDir(), "config");
        }

        // A path inside the config folder, never outside it.
        static string ConfigPath(string side, string rel)
        {
            if (string.IsNullOrWhiteSpace(rel)) throw new UserError("No config file was chosen.");
            string root = Path.GetFullPath(ConfigRoot(side)).TrimEnd('\\');
            string full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', '\\')));
            if (!full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) throw new UserError("That file isn't in the config folder.");
            return full;
        }

        static string ConfigFormat(string rel)
        {
            string ext = Path.GetExtension(rel).ToLowerInvariant();
            return ext == ".toml" ? "toml" : ext == ".json" ? "json" : ext == ".json5" ? "json5" : TextConfig(rel) ? "text" : "other";
        }

        // ---------- Reading TOML ----------

        static int FindOutsideQuotes(string s, char c, int from)
        {
            bool dq = false, sq = false;
            for (int i = from; i < s.Length; i++)
            {
                char ch = s[i];
                if (dq) { if (ch == '\\') i++; else if (ch == '"') dq = false; continue; }
                if (sq) { if (ch == '\'') sq = false; continue; }
                if (ch == '"') dq = true;
                else if (ch == '\'') sq = true;
                else if (ch == c) return i;
            }
            return -1;
        }

        // Bracket depth change of a value's text, ignoring brackets inside strings and comments.
        static int BracketDepth(string s)
        {
            int depth = 0;
            bool dq = false, sq = false;
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                if (dq) { if (ch == '\\') i++; else if (ch == '"') dq = false; continue; }
                if (sq) { if (ch == '\'') sq = false; continue; }
                if (ch == '#') break;
                if (ch == '"') dq = true;
                else if (ch == '\'') sq = true;
                else if (ch == '[' || ch == '{') depth++;
                else if (ch == ']' || ch == '}') depth--;
            }
            return depth;
        }

        static string TomlNoComment(string value)
        {
            int hash = FindOutsideQuotes(value, '#', 0);
            return (hash >= 0 ? value.Substring(0, hash) : value).Trim();
        }

        static string UnquoteKey(string key)
        {
            key = key.Trim();
            if (key.Length >= 2 && (key[0] == '"' && key[key.Length - 1] == '"' || key[0] == '\'' && key[key.Length - 1] == '\''))
                return key.Substring(1, key.Length - 2);
            return key;
        }

        static string TomlType(string raw)
        {
            if (raw == "true" || raw == "false") return "bool";
            if (Regex.IsMatch(raw, @"^[+-]?\d+$")) return "int";
            if (Regex.IsMatch(raw, @"^[+-]?(\d+\.\d*|\.\d+|\d+)([eE][+-]?\d+)?$") || raw == "inf" || raw == "-inf" || raw == "nan") return "float";
            if (raw.StartsWith("\"") && !raw.StartsWith("\"\"\"") || raw.StartsWith("'") && !raw.StartsWith("'''")) return "string";
            if (raw.StartsWith("[")) return "list";
            return "other";
        }

        static TomlDoc ReadToml(string path)
        {
            string text = File.ReadAllText(path, Encoding.UTF8);
            var doc = new TomlDoc();
            doc.Newline = text.Contains("\r\n") ? "\r\n" : "\n";
            doc.Lines = text.Replace("\r\n", "\n").Split('\n');
            string section = "";
            var pending = new List<string>();
            for (int i = 0; i < doc.Lines.Length; i++)
            {
                string t = doc.Lines[i].Trim();
                if (t.Length == 0) { pending.Clear(); continue; }
                if (t.StartsWith("#")) { pending.Add(t.Substring(1).Trim()); continue; }
                if (t.StartsWith("["))
                {
                    int close = FindOutsideQuotes(t, ']', t.StartsWith("[[") ? 2 : 1);
                    string name = close > 0 ? t.Substring(t.StartsWith("[[") ? 2 : 1, close - (t.StartsWith("[[") ? 2 : 1)) : t.Trim('[', ']');
                    section = string.Join(".", Regex.Split(name.Trim(), @"\.(?=(?:[^""]*""[^""]*"")*[^""]*$)").Select(UnquoteKey));
                    if (pending.Count > 0) doc.SectionDesc[section] = new List<string>(pending);
                    pending.Clear();
                    continue;
                }
                string line = doc.Lines[i];
                int eq = FindOutsideQuotes(line, '=', 0);
                if (eq < 0) { pending.Clear(); continue; }
                var e = new TomlEntry { Section = section, Key = UnquoteKey(line.Substring(0, eq)), Line = i, EndLine = i };
                int valueStart = eq + 1;
                while (valueStart < line.Length && line[valueStart] == ' ') valueStart++;
                e.Prefix = line.Substring(0, valueStart);
                var value = new StringBuilder(line.Substring(valueStart));
                int depth = BracketDepth(value.ToString());
                while (depth > 0 && e.EndLine + 1 < doc.Lines.Length)
                {
                    e.EndLine++;
                    value.Append("\n").Append(doc.Lines[e.EndLine]);
                    depth += BracketDepth(doc.Lines[e.EndLine]);
                }
                e.Raw = e.EndLine == e.Line ? TomlNoComment(value.ToString()) : value.ToString().Trim();
                e.Type = TomlType(e.Raw);
                foreach (string c in pending)
                {
                    Match m;
                    if ((m = TomlRange.Match(c)).Success || (m = TomlInterval.Match(c)).Success)
                    {
                        if (m.Groups["min"].Success && m.Groups["min"].Value.Length > 0) e.Min = m.Groups["min"].Value;
                        if (m.Groups["max"].Success && m.Groups["max"].Value.Length > 0) e.Max = m.Groups["max"].Value;
                    }
                    else if ((m = TomlAllowed.Match(c)).Success)
                        e.Allowed = m.Groups["list"].Value.Split(',').Select(x => x.Trim().Trim('"')).Where(x => x.Length > 0).ToList();
                    else if ((m = TomlDefault.Match(c)).Success)
                    {
                        // "Default: true." in hand-written comments: the full stop isn't part of the value.
                        string v = m.Groups["v"].Value.Trim();
                        if (v.EndsWith(".") && TomlType(v.Substring(0, v.Length - 1)) != "other") v = v.Substring(0, v.Length - 1);
                        e.Default = v;
                    }
                    else e.Desc.Add(c);
                }
                pending.Clear();
                doc.Entries.Add(e);
                i = e.EndLine;
            }
            return doc;
        }

        static string TomlUnquote(string raw)
        {
            if (raw.StartsWith("'")) return raw.Length >= 2 ? raw.Substring(1, raw.Length - 2) : "";
            try { return new JavaScriptSerializer().Deserialize<string>(raw); }
            catch { return raw.Trim('"'); }
        }

        static string TomlQuote(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s ?? "")
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("X4")); else sb.Append(c); break;
                }
            }
            return sb.Append('"').ToString();
        }

        // A list of plain strings or numbers as items; null when it holds anything else (tables, nested lists).
        static List<object> TomlListItems(string raw)
        {
            string inner = raw.Trim();
            if (!inner.StartsWith("[") || !inner.EndsWith("]")) return null;
            inner = string.Join("\n", inner.Substring(1, inner.Length - 2).Split('\n').Select(TomlNoComment));
            var items = new List<object>();
            int i = 0;
            while (true)
            {
                while (i < inner.Length && (char.IsWhiteSpace(inner[i]) || inner[i] == ',')) i++;
                if (i >= inner.Length) break;
                char c = inner[i];
                if (c == '[' || c == '{') return null;
                int end;
                if (c == '"' || c == '\'')
                {
                    end = i + 1;
                    while (end < inner.Length && inner[end] != c) { if (c == '"' && inner[end] == '\\') end++; end++; }
                    if (end >= inner.Length) return null;
                    items.Add(TomlUnquote(inner.Substring(i, end - i + 1)));
                    i = end + 1;
                }
                else
                {
                    end = inner.IndexOf(',', i);
                    string token = (end < 0 ? inner.Substring(i) : inner.Substring(i, end - i)).Trim();
                    string type = TomlType(token);
                    if (type == "int" || type == "float" || type == "bool") items.Add(token); else return null;
                    i = end < 0 ? inner.Length : end + 1;
                }
            }
            return items;
        }

        static Dictionary<string, object> EntryObj(TomlEntry e)
        {
            object value = e.Raw;
            List<object> items = null;
            if (e.Type == "string") value = TomlUnquote(e.Raw);
            else if (e.Type == "list") items = TomlListItems(e.Raw);
            return Obj("section", e.Section, "key", e.Key, "type", e.Type == "list" && items == null ? "other" : e.Type, "value", value, "items", items,
                "raw", e.Raw, "desc", string.Join("\n", e.Desc), "default", e.Default, "min", e.Min, "max", e.Max, "allowed", e.Allowed);
        }

        // ---------- What the page asks for ----------

        static Dictionary<string, string> ModNamesIn(string mods)
        {
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(mods)) return names;
            foreach (FileInfo f in new DirectoryInfo(mods).GetFiles().Where(f => JarName.IsMatch(f.Name)))
                try { foreach (ModInfo m in GetJarMeta(f).Mods) if (m.Id != null && !names.ContainsKey(m.Id)) names[m.Id] = m.Name ?? m.Id; } catch (Exception) { }
            return names;
        }

        static Dictionary<string, object> ListConfigFiles()
        {
            Dictionary<string, FileInfo> game = ShareableConfigs(Path.Combine(GameDir(), "config"));
            bool hasServer = !string.IsNullOrEmpty(serverDir) && Directory.Exists(serverDir);
            Dictionary<string, FileInfo> server = hasServer ? ShareableConfigs(Path.Combine(serverDir, "config")) : new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> names = ModNamesIn(modsDir);
            if (hasServer) foreach (KeyValuePair<string, string> kv in ModNamesIn(Path.Combine(serverDir, "mods"))) if (!names.ContainsKey(kv.Key)) names[kv.Key] = kv.Value;
            var files = new List<object>();
            foreach (string rel in game.Keys.Union(server.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(r => r, StringComparer.OrdinalIgnoreCase))
            {
                FileInfo g, s;
                game.TryGetValue(rel, out g);
                server.TryGetValue(rel, out s);
                string owner = ConfigOwner(rel, names.Keys);
                string same = g == null || s == null ? null : ConfigFingerprint(g, rel) == ConfigFingerprint(s, rel) ? "same" : "different";
                files.Add(Obj("path", rel, "format", ConfigFormat(rel), "owner", owner, "ownerName", owner == null ? null : names[owner],
                    "game", g == null ? null : Obj("size", g.Length, "modified", g.LastWriteTimeUtc.ToString("o")),
                    "server", s == null ? null : Obj("size", s.Length, "modified", s.LastWriteTimeUtc.ToString("o")), "match", same));
            }
            return Obj("ok", true, "files", files, "hasServer", hasServer, "gameRunning", GameRunning(GameDir()), "serverRunning", ServerBusy());
        }

        static Dictionary<string, object> ReadConfigFile(string side, string rel)
        {
            string path = ConfigPath(side, rel);
            var info = new FileInfo(path);
            if (!info.Exists) throw new UserError(rel + " isn't there any more.");
            if (info.Length > 2 << 20) throw new UserError(rel + " is too big to edit here (" + (info.Length >> 20) + " MB).");
            string format = ConfigFormat(rel);
            var result = Obj("ok", true, "side", side, "path", rel, "format", format, "stamp", info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture),
                "text", File.ReadAllText(path, Encoding.UTF8));
            if (format == "toml")
            {
                try
                {
                    TomlDoc doc = ReadToml(path);
                    result["entries"] = doc.Entries.Select(e => (object)EntryObj(e)).ToList();
                    result["sections"] = doc.SectionDesc.ToDictionary(kv => kv.Key, kv => (object)string.Join("\n", kv.Value));
                }
                catch (Exception ex) { result["parseError"] = ex.Message; }
            }
            return result;
        }

        static void RequireConfigsWritable(string side)
        {
            if (side == "server") { if (ServerBusy()) throw new UserError("Stop the server first: it rewrites its configs while it runs and when it stops."); }
            else if (GameRunning(GameDir())) throw new UserError("Close the game first: it rewrites its configs when it closes, which would undo this.");
        }

        static string WriteConfigWithBackup(string side, string rel, string path, string stamp, string content)
        {
            RequireConfigsWritable(side);
            var info = new FileInfo(path);
            if (!info.Exists) throw new UserError(rel + " isn't there any more.");
            if (info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) != stamp)
                throw new UserError(rel + " changed on disk since it was opened (the game or another program saved it). Open it again to see the new version.");
            BackUpConfig(path, DateTime.Now.ToString("yyyy-MM-dd_HHmmss"), "editor-" + side, rel);
            File.WriteAllText(path + ".saving", content, new UTF8Encoding(false));
            File.Copy(path + ".saving", path, true);
            File.Delete(path + ".saving");
            return new FileInfo(path).LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
        }

        // A typed value from the page as TOML text, checked against what the setting allows.
        static string TomlValueText(TomlEntry e, Dictionary<string, object> edit)
        {
            string type = Str(edit, "type") ?? e.Type;
            object value = Get(edit, "value");
            string what = (e.Section.Length > 0 ? e.Section + "." : "") + e.Key;
            switch (type)
            {
                case "bool":
                    if (!(value is bool)) throw new UserError(what + " needs true or false.");
                    return (bool)value ? "true" : "false";
                case "int":
                case "float":
                {
                    string s = Convert.ToString(value, CultureInfo.InvariantCulture).Trim();
                    double d;
                    if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) throw new UserError(what + " needs a number.");
                    if (type == "int" && !Regex.IsMatch(s, @"^[+-]?\d+$")) throw new UserError(what + " needs a whole number.");
                    double lim;
                    if (e.Min != null && double.TryParse(e.Min, NumberStyles.Float, CultureInfo.InvariantCulture, out lim) && d < lim) throw new UserError(what + " can't be below " + e.Min + ".");
                    if (e.Max != null && double.TryParse(e.Max, NumberStyles.Float, CultureInfo.InvariantCulture, out lim) && d > lim) throw new UserError(what + " can't be above " + e.Max + ".");
                    if (type == "float" && !s.Contains(".") && !s.ToLowerInvariant().Contains("e")) s += ".0";
                    return s;
                }
                case "string":
                {
                    string s = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
                    if (e.Allowed != null && e.Allowed.Count > 0 && !e.Allowed.Contains(s)) throw new UserError(what + " has to be one of: " + string.Join(", ", e.Allowed) + ".");
                    return TomlQuote(s);
                }
                case "list":
                {
                    // A list of numbers (or true/false) stays unquoted; anything else is a list of strings.
                    bool bare = TomlListItems(e.Raw) != null && e.Raw.IndexOf('"') < 0 && e.Raw.IndexOf('\'') < 0
                        && Regex.IsMatch(e.Raw, @"[0-9a-z]");
                    return "[" + string.Join(", ", BodyList(value).Select(x =>
                    {
                        string s = (Convert.ToString(x, CultureInfo.InvariantCulture) ?? "").Trim();
                        string t = TomlType(s);
                        if (bare && t != "int" && t != "float" && t != "bool") throw new UserError(what + " is a list of numbers; \"" + s + "\" isn't one.");
                        return bare ? s : TomlQuote(s);
                    })) + "]";
                }
                default:
                {
                    string s = (Convert.ToString(value, CultureInfo.InvariantCulture) ?? "").Trim();
                    if (s.Length == 0) throw new UserError(what + " can't be empty.");
                    if (BracketDepth(s) != 0 || FindOutsideQuotes(s, '\n', 0) >= 0 && !s.StartsWith("[")) throw new UserError(what + " isn't a valid value.");
                    return s;
                }
            }
        }

        static Dictionary<string, object> SaveConfigEdits(Dictionary<string, object> body)
        {
            string side = Str(body, "side") == "server" ? "server" : "game";
            string rel = Str(body, "path");
            string path = ConfigPath(side, rel);
            if (ConfigFormat(rel) != "toml") throw new UserError("Only TOML configs are edited setting by setting; use the text editor for this one.");
            TomlDoc doc = ReadToml(path);
            var lines = doc.Lines.ToList();
            var changes = new List<KeyValuePair<TomlEntry, string>>();
            foreach (Dictionary<string, object> edit in BodyList(Get(body, "edits")).Select(D).Where(x => x != null))
            {
                string section = Str(edit, "section") ?? "", key = Str(edit, "key");
                TomlEntry e = doc.Entries.FirstOrDefault(x => x.Section == section && x.Key == key);
                if (e == null) throw new UserError((section.Length > 0 ? section + "." : "") + key + " isn't in " + rel + " any more. Open it again.");
                changes.Add(new KeyValuePair<TomlEntry, string>(e, TomlValueText(e, edit)));
            }
            if (changes.Count == 0) throw new UserError("Nothing was changed.");
            // Bottom to top, so replacing a multi-line list doesn't move the lines still to do.
            foreach (KeyValuePair<TomlEntry, string> c in changes.OrderByDescending(c => c.Key.Line))
            {
                lines.RemoveRange(c.Key.Line, c.Key.EndLine - c.Key.Line + 1);
                lines.Insert(c.Key.Line, c.Key.Prefix + c.Value);
            }
            string stamp = WriteConfigWithBackup(side, rel, path, Str(body, "stamp"), string.Join(doc.Newline, lines));
            Log("Config    " + rel + (side == "server" ? " (server)" : "") + ": " + string.Join(", ", changes.Select(c => c.Key.Key + " = " + (c.Value.Length > 40 ? c.Value.Substring(0, 40) + "…" : c.Value))));
            return Obj("ok", true, "stamp", stamp, "changed", changes.Count);
        }

        static Dictionary<string, object> SaveConfigText(Dictionary<string, object> body)
        {
            string side = Str(body, "side") == "server" ? "server" : "game";
            string rel = Str(body, "path");
            string path = ConfigPath(side, rel);
            string text = Str(body, "text") ?? "";
            if (ConfigFormat(rel) == "json")
            {
                try { new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(text); }
                catch (Exception ex) { throw new UserError("That isn't valid JSON, so it wasn't saved: " + ex.Message); }
            }
            if (ConfigFormat(rel) == "toml")
            {
                // A quick sanity check: brackets balance and every setting line has an "=".
                int depth = 0;
                foreach (string l in text.Replace("\r\n", "\n").Split('\n'))
                {
                    string t = l.Trim();
                    if (depth == 0 && t.Length > 0 && !t.StartsWith("#") && !t.StartsWith("[") && FindOutsideQuotes(t, '=', 0) < 0)
                        throw new UserError("This line isn't a setting, so it wasn't saved: " + (t.Length > 60 ? t.Substring(0, 60) + "…" : t));
                    if (!(depth == 0 && t.StartsWith("["))) depth += BracketDepth(t);
                }
                if (depth != 0) throw new UserError("A bracket isn't closed, so it wasn't saved.");
            }
            string stamp = WriteConfigWithBackup(side, rel, path, Str(body, "stamp"), text);
            Log("Config    " + rel + (side == "server" ? " (server)" : "") + ": edited as text");
            return Obj("ok", true, "stamp", stamp);
        }

        // Every TOML setting whose name, description, section or file matches.
        static Dictionary<string, object> SearchConfigs(string q)
        {
            q = (q ?? "").Trim();
            if (q.Length < 2) return Obj("ok", true, "hits", new List<object>());
            string[] words = q.ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var hits = new List<object>();
            var sides = new List<KeyValuePair<string, Dictionary<string, FileInfo>>> { new KeyValuePair<string, Dictionary<string, FileInfo>>("game", ShareableConfigs(Path.Combine(GameDir(), "config"))) };
            if (!string.IsNullOrEmpty(serverDir) && Directory.Exists(serverDir)) sides.Add(new KeyValuePair<string, Dictionary<string, FileInfo>>("server", ShareableConfigs(Path.Combine(serverDir, "config"))));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, Dictionary<string, FileInfo>> side in sides)
                foreach (KeyValuePair<string, FileInfo> f in side.Value.Where(kv => ConfigFormat(kv.Key) == "toml").OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                {
                    if (!seen.Add(f.Key)) continue; // the game's copy stands for both
                    TomlDoc doc;
                    try { doc = ReadToml(f.Value.FullName); } catch (Exception) { continue; }
                    foreach (TomlEntry e in doc.Entries)
                    {
                        string hay = (f.Key + " " + e.Section + " " + e.Key + " " + string.Join(" ", e.Desc)).ToLowerInvariant().Replace('_', ' ');
                        if (!words.All(w => hay.Contains(w))) continue;
                        Dictionary<string, object> o = EntryObj(e);
                        o["path"] = f.Key;
                        o["side"] = side.Key;
                        hits.Add(o);
                        if (hits.Count >= 150) return Obj("ok", true, "hits", hits, "more", true);
                    }
                }
            return Obj("ok", true, "hits", hits);
        }
    }
}
