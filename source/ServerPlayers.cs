// The Players tab: who's online, the whitelist, operators and bans. While the server runs (started from
// here) changes go in as server commands, because the server keeps these lists in memory and would write
// over any edit to its files. While it's stopped, whitelist.json, ops.json and banned-players.json are
// edited directly. A file entry needs the player's UUID: it comes from the server's usercache.json when
// they've joined before, otherwise from Mojang's profile lookup (or, with online mode off, the offline
// UUID the server itself would give that name).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace MinecraftModManager
{
    static partial class Program
    {
        static readonly Regex PlayerName = new Regex("^[A-Za-z0-9_]{1,16}$");

        static List<Dictionary<string, object>> ReadPlayerFile(string dir, string file)
        {
            try
            {
                string path = Path.Combine(dir, file);
                if (!File.Exists(path)) return new List<Dictionary<string, object>>();
                return A(new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path, Encoding.UTF8))).Select(D).Where(x => x != null).ToList();
            }
            catch { return new List<Dictionary<string, object>>(); }
        }

        static void WritePlayerFile(string dir, string file, List<Dictionary<string, object>> entries)
        {
            string path = Path.Combine(dir, file);
            string temp = path + ".writing";
            File.WriteAllText(temp, new JavaScriptSerializer().Serialize(entries), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }

        static Dictionary<string, object> ServerPlayers()
        {
            string dir = RequireServerDir();
            Dictionary<string, string> props = ReadProperties(Path.Combine(dir, "server.properties"));
            string v;
            List<string> online;
            lock (ServerLock) online = playersOnline.ToList();
            Func<string, List<object>> names = file => ReadPlayerFile(dir, file)
                .Select(e => (object)Obj("name", S(e, "name"), "uuid", S(e, "uuid"), "level", Get(e, "level"), "reason", S(e, "reason"), "created", S(e, "created")))
                .ToList();
            var recent = ReadPlayerFile(dir, "usercache.json").Select(e => S(e, "name")).Where(n => n != null).Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            return Obj("ok", true, "online", online, "running", ServerBusy(), "external", !ServerBusy() && ExternalServerRunning(dir, props),
                "whitelistOn", props.TryGetValue("white-list", out v) && v == "true",
                "onlineMode", !props.TryGetValue("online-mode", out v) || v != "false",
                "whitelist", names("whitelist.json"), "ops", names("ops.json"), "banned", names("banned-players.json"), "recent", recent);
        }

        // { action: whitelist-add | whitelist-remove | op | deop | ban | pardon | kick | whitelist-on | whitelist-off, name, reason }
        static Dictionary<string, object> ServerPlayerAction(Dictionary<string, object> body)
        {
            string dir = RequireServerDir();
            string action = Str(body, "action") ?? "";
            string name = (Str(body, "name") ?? "").Trim();
            string reason = Regex.Replace(Str(body, "reason") ?? "", @"[\r\n]+", " ").Trim();
            if (reason.Length > 100) reason = reason.Substring(0, 100);
            bool needsName = action != "whitelist-on" && action != "whitelist-off";
            if (needsName && !PlayerName.IsMatch(name)) throw new UserError("A player name is 1 to 16 letters, digits or underscores.");

            if (ServerBusy())
            {
                string command;
                switch (action)
                {
                    case "whitelist-add": command = "whitelist add " + name; break;
                    case "whitelist-remove": command = "whitelist remove " + name; break;
                    case "whitelist-on": command = "whitelist on"; break;
                    case "whitelist-off": command = "whitelist off"; break;
                    case "op": command = "op " + name; break;
                    case "deop": command = "deop " + name; break;
                    case "ban": command = "ban " + name + (reason.Length > 0 ? " " + reason : ""); break;
                    case "pardon": command = "pardon " + name; break;
                    case "kick": command = "kick " + name + (reason.Length > 0 ? " " + reason : ""); break;
                    default: throw new UserError("Unknown player action.");
                }
                ServerCommand(command);
                return Obj("ok", true, "via", "command");
            }

            Dictionary<string, string> props = ReadProperties(Path.Combine(dir, "server.properties"));
            if (ExternalServerRunning(dir, props)) throw new UserError("The server was started outside this app. Use its own console, or stop it first.");
            if (action == "kick") throw new UserError("The server isn't running, so nobody is online to kick.");
            if (action == "whitelist-on" || action == "whitelist-off")
            {
                SaveServerProperties(Obj("changes", Obj("white-list", action == "whitelist-on" ? "true" : "false")));
                return Obj("ok", true, "via", "file");
            }

            lock (FileLock)
            {
                string file = action.StartsWith("whitelist") ? "whitelist.json" : action == "op" || action == "deop" ? "ops.json" : "banned-players.json";
                List<Dictionary<string, object>> entries = ReadPlayerFile(dir, file);
                bool adding = action == "whitelist-add" || action == "op" || action == "ban";
                int removed = entries.RemoveAll(e => string.Equals(S(e, "name"), name, StringComparison.OrdinalIgnoreCase));
                if (!adding)
                {
                    if (removed == 0) throw new UserError(name + " isn't on that list.");
                }
                else
                {
                    string uuid = PlayerUuid(dir, name, !props.ContainsKey("online-mode") || props["online-mode"] != "false");
                    var entry = new Dictionary<string, object> { { "uuid", uuid }, { "name", name } };
                    if (action == "op") { entry["level"] = 4; entry["bypassesPlayerLimit"] = false; }
                    if (action == "ban")
                    {
                        entry["created"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + DateTime.Now.ToString("zzz").Replace(":", "");
                        entry["source"] = "Server";
                        entry["expires"] = "forever";
                        entry["reason"] = reason.Length > 0 ? reason : "Banned by an operator.";
                    }
                    entries.Add(entry);
                }
                WritePlayerFile(dir, file, entries);
                Log("Players   " + action + " " + name + " (" + file + ")");
            }
            return Obj("ok", true, "via", "file");
        }

        // The UUID the server knows a player by: from its usercache, else Mojang (online mode) or the offline UUID.
        static string PlayerUuid(string dir, string name, bool onlineMode)
        {
            Dictionary<string, object> cached = ReadPlayerFile(dir, "usercache.json").FirstOrDefault(e => string.Equals(S(e, "name"), name, StringComparison.OrdinalIgnoreCase));
            if (cached != null && S(cached, "uuid") != null) return S(cached, "uuid");
            if (!onlineMode) return OfflineUuid(name);
            try
            {
                var request = (HttpWebRequest)WebRequest.Create("https://api.mojang.com/users/profiles/minecraft/" + name);
                request.UserAgent = ModrinthUserAgent;
                request.Timeout = 10000;
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    string id = S(new JavaScriptSerializer().DeserializeObject(reader.ReadToEnd()) as Dictionary<string, object>, "id");
                    if (id != null && Regex.IsMatch(id, "^[0-9a-fA-F]{32}$"))
                        return (id.Substring(0, 8) + "-" + id.Substring(8, 4) + "-" + id.Substring(12, 4) + "-" + id.Substring(16, 4) + "-" + id.Substring(20)).ToLowerInvariant();
                }
            }
            catch (WebException ex)
            {
                var r = ex.Response as HttpWebResponse;
                if (r != null && (r.StatusCode == HttpStatusCode.NotFound || r.StatusCode == HttpStatusCode.NoContent))
                    throw new UserError("There's no Minecraft account called " + name + ".");
                throw new UserError("Couldn't look up " + name + " with Mojang (" + ex.Message + "). Try again, or start the server and add them from there.");
            }
            throw new UserError("There's no Minecraft account called " + name + ".");
        }

        static string OfflineUuid(string name)
        {
            byte[] b;
            using (var md5 = MD5.Create()) b = md5.ComputeHash(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
            b[6] = (byte)((b[6] & 0x0f) | 0x30);
            b[8] = (byte)((b[8] & 0x3f) | 0x80);
            string hex = BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
            return hex.Substring(0, 8) + "-" + hex.Substring(8, 4) + "-" + hex.Substring(12, 4) + "-" + hex.Substring(16, 4) + "-" + hex.Substring(20);
        }

        // ---------- Memory (user_jvm_args.txt) ----------

        static Dictionary<string, object> SetServerMemory(Dictionary<string, object> body)
        {
            string dir = RequireServerDir();
            int gb = (int)CfId(Get(body, "gb"));
            long ram = TotalRamMb();
            if (gb < 1 || gb > 64 || ram > 0 && gb * 1024L > ram) throw new UserError("Pick between 1 GB and what this PC has.");
            string path = Path.Combine(dir, "user_jvm_args.txt");
            var lines = File.Exists(path) ? File.ReadAllLines(path, Encoding.UTF8).ToList() : new List<string>();
            Func<string, long> mb = s => { Match m = Regex.Match(s ?? "", @"-Xm[sx](\d+)([GgMm])"); return !m.Success ? 0 : long.Parse(m.Groups[1].Value) * (char.ToUpperInvariant(m.Groups[2].Value[0]) == 'G' ? 1024 : 1); };
            long oldMax = mb(lines.FirstOrDefault(l => Regex.IsMatch(l, @"^\s*-Xmx\d+[GgMm]\s*$")));
            bool done = false;
            for (int i = 0; i < lines.Count; i++)
            {
                // The starting heap moves too when it was fixed to the old maximum (Xms = Xmx) or would end up above
                // the new one: Java refuses to start with -Xms larger than -Xmx.
                if (Regex.IsMatch(lines[i], @"^\s*-Xms\d+[GgMm]\s*$"))
                {
                    long ms = mb(lines[i]);
                    if (ms == oldMax || ms > gb * 1024L) lines[i] = "-Xms" + gb + "G";
                    continue;
                }
                if (!Regex.IsMatch(lines[i], @"^\s*-Xmx\d+[GgMm]\s*$")) continue;
                lines[i] = done ? null : "-Xmx" + gb + "G";
                done = true;
            }
            lines = lines.Where(l => l != null).ToList();
            if (!done) lines.Add("-Xmx" + gb + "G");
            if (File.Exists(path)) File.Copy(path, path + ".bak", true);
            File.WriteAllLines(path, lines, new UTF8Encoding(false));
            Log("Server    memory set to " + gb + "G (user_jvm_args.txt)");
            return Obj("ok", true, "maxMemory", JvmMaxMemory(dir));
        }
    }
}
