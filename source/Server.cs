// Hosting the Minecraft server on this PC: which folder it lives in, starting and stopping it,
// a live console (everything the server prints, plus a box to type commands), online players,
// and a few server.properties settings. Nothing here starts the server on its own: only the
// Start button in the page does.
//
// The server is started the way its own run.bat would (java @user_jvm_args.txt @libraries/...
// /win_args.txt), with "nogui" so its output comes here instead of a separate window.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace MinecraftModManager
{
    static partial class Program
    {
        static volatile string serverDir;
        static readonly object ServerLock = new object();
        static Process serverProcess;
        static StreamWriter serverInput;
        static volatile string serverState = "stopped"; // stopped | starting | running | stopping
        static DateTime serverStartedAt, serverStoppedAt;
        static string serverExitNote, serverCrashReport;
        static bool quitWhenStopped;
        static readonly List<ConsoleLine> consoleLines = new List<ConsoleLine>();
        static long consoleSeq;
        static readonly List<string> playersOnline = new List<string>();
        const int ConsoleKeep = 5000;

        static readonly Regex AnsiCodes = new Regex(@"\x1B\[[0-9;?]*[A-Za-z]");
        static readonly Regex DoneLine = new Regex(@"\]: Done \([0-9.,]+s\)! For help, type");
        static readonly Regex JoinLine = new Regex(@"\]: ([A-Za-z0-9_]{1,16}) joined the game");
        static readonly Regex LeftLine = new Regex(@"\]: ([A-Za-z0-9_]{1,16}) left the game");

        sealed class ConsoleLine
        {
            public long Seq;
            public string Time, Text, Kind; // Kind: out | err | input | app
        }

        static string RequireServerDir()
        {
            string dir = serverDir;
            if (dir == null) throw new UserError("no-server", "No server folder is set yet. Choose one on the Server page.");
            if (!Directory.Exists(dir)) throw new UserError("no-server", "The server folder " + dir + " doesn't exist any more. Choose it again on the Server page.");
            return dir;
        }

        static bool ServerBusy()
        {
            lock (ServerLock) return serverProcess != null;
        }

        static int PlayerCount()
        {
            lock (ServerLock) return playersOnline.Count;
        }

        // ---------- Status ----------

        static Dictionary<string, object> ServerStatus()
        {
            string dir = serverDir;
            Dictionary<string, object> status;
            lock (ServerLock)
            {
                long memory = 0;
                if (serverProcess != null)
                {
                    try { serverProcess.Refresh(); memory = serverProcess.WorkingSet64; } catch { }
                }
                status = Obj("folder", dir, "state", serverState, "players", playersOnline.ToList(), "seq", consoleSeq,
                    "startedAt", serverProcess != null ? serverStartedAt.ToUniversalTime().ToString("o") : null,
                    "stoppedAt", serverProcess == null && serverStoppedAt != DateTime.MinValue ? serverStoppedAt.ToUniversalTime().ToString("o") : null,
                    "exitNote", serverExitNote, "crashReport", serverCrashReport, "memory", memory);
            }
            if (dir == null) { status["configured"] = false; return status; }
            status["configured"] = true;
            bool exists = Directory.Exists(dir);
            status["exists"] = exists;
            if (!exists) return status;

            Dictionary<string, string> props = ReadProperties(Path.Combine(dir, "server.properties"));
            // Every line of server.properties, in file order (the page shows the common ones with friendly controls).
            status["properties"] = props.Select(p => (object)new object[] { p.Key, p.Value }).ToList();
            status["eula"] = EulaAccepted(dir);
            status["neoforge"] = ServerLoaderVersion(dir);
            status["externalRunning"] = serverState == "stopped" && ExternalServerRunning(dir, props);
            status["modsCount"] = Directory.Exists(Path.Combine(dir, "mods"))
                ? new DirectoryInfo(Path.Combine(dir, "mods")).GetFiles("*.jar").Length : 0;
            string java = null, launchProblem = null;
            try { java = ServerLaunch(dir)[0]; } catch (UserError ex) { launchProblem = ex.Message; }
            status["java"] = java;
            status["launchProblem"] = launchProblem;
            status["maxMemory"] = JvmMaxMemory(dir);
            status["lanAddresses"] = LanAddresses();
            return status;
        }

        static Dictionary<string, object> ServerConsole(long after, bool wait)
        {
            lock (ServerLock)
            {
                if (wait && consoleSeq <= after) Monitor.Wait(ServerLock, 15000);
                IEnumerable<ConsoleLine> lines = consoleLines.Where(l => l.Seq > after);
                if (after == 0) lines = lines.Skip(Math.Max(0, consoleLines.Count - 1500));
                return Obj("lines", lines.Select(l => (object)new object[] { l.Seq, l.Time, l.Text, l.Kind }).ToList(),
                    "seq", consoleSeq, "state", serverState, "players", playersOnline.ToList());
            }
        }

        static void AddConsole(string text, string kind)
        {
            lock (ServerLock)
            {
                consoleSeq++;
                consoleLines.Add(new ConsoleLine { Seq = consoleSeq, Time = DateTime.Now.ToString("HH:mm:ss"), Text = text, Kind = kind });
                if (consoleLines.Count > ConsoleKeep) consoleLines.RemoveRange(0, consoleLines.Count - ConsoleKeep);
                Monitor.PulseAll(ServerLock);
            }
        }

        // ---------- Folder, EULA, properties ----------

        static Dictionary<string, object> ChooseServerFolder()
        {
            if (ServerBusy()) throw new UserError("Stop the server before choosing another folder.");
            string picked = null;
            string current = serverDir;
            ui.Send(delegate { picked = PickFolder(current, "Choose the server folder (the one with run.bat and server.properties)"); }, null);
            if (picked == null) return Obj("ok", true, "changed", false);
            bool looksRight = File.Exists(Path.Combine(picked, "run.bat")) || File.Exists(Path.Combine(picked, "server.properties"))
                || Directory.Exists(Path.Combine(picked, "libraries"));
            if (!looksRight) throw new UserError(picked + " doesn't look like a server folder (no run.bat, server.properties or libraries folder).");
            serverDir = picked;
            SaveSettings();
            Log("Server    folder is now " + picked);
            return Obj("ok", true, "changed", true, "folder", picked);
        }

        static void OpenServerFolder(string sub)
        {
            string dir = RequireServerDir();
            string target = dir;
            if (sub == "mods" || sub == "config" || sub == "logs" || sub == "crash-reports") target = Path.Combine(dir, sub);
            if (sub == "crash" && serverCrashReport != null && File.Exists(serverCrashReport))
            {
                try { Process.Start("explorer.exe", "/select,\"" + serverCrashReport + "\""); } catch { }
                return;
            }
            if (!Directory.Exists(target)) target = dir;
            try { Process.Start("explorer.exe", "\"" + target + "\""); } catch { }
        }

        static bool EulaAccepted(string dir)
        {
            try
            {
                string path = Path.Combine(dir, "eula.txt");
                return File.Exists(path) && File.ReadAllLines(path).Any(l => Regex.IsMatch(l, @"^\s*eula\s*=\s*true\s*$", RegexOptions.IgnoreCase));
            }
            catch { return false; }
        }

        // Only ever called when the person clicks "I accept" next to the link to Minecraft's EULA.
        static Dictionary<string, object> AcceptEula()
        {
            string dir = RequireServerDir();
            File.WriteAllText(Path.Combine(dir, "eula.txt"),
                "#By changing the setting below to TRUE you are indicating your agreement to our EULA (https://aka.ms/MinecraftEULA).\n" +
                "#" + DateTime.Now.ToString("ddd MMM dd HH:mm:ss yyyy", CultureInfo.InvariantCulture) + " (accepted in Minecraft Mod Manager)\neula=true\n",
                new UTF8Encoding(false));
            Log("Server    accepted the Minecraft EULA in eula.txt");
            return Obj("ok", true);
        }

        static Dictionary<string, string> ReadProperties(string path)
        {
            // Insertion-ordered in practice (Dictionary keeps order when nothing is removed), so the page sees file order.
            var props = new Dictionary<string, string>();
            if (!File.Exists(path)) return props;
            foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                string line = raw.TrimStart();
                if (line.Length == 0 || line[0] == '#' || line[0] == '!') continue;
                int eq = line.IndexOfAny(new[] { '=', ':' });
                if (eq <= 0) continue;
                props[line.Substring(0, eq).Trim()] = UnescapeProperty(line.Substring(eq + 1).Trim());
            }
            return props;
        }

        static string UnescapeProperty(string value)
        {
            if (value.IndexOf('\\') < 0) return value;
            var sb = new StringBuilder();
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c != '\\' || i + 1 >= value.Length) { sb.Append(c); continue; }
                char n = value[++i];
                if (n == 'u' && i + 4 < value.Length)
                {
                    int code;
                    if (int.TryParse(value.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code)) { sb.Append((char)code); i += 4; continue; }
                }
                sb.Append(n == 'n' ? '\n' : n == 't' ? '\t' : n);
            }
            return sb.ToString();
        }

        static string EscapeProperty(string value)
        {
            var sb = new StringBuilder();
            foreach (char c in value)
            {
                if (c == '\\') sb.Append("\\\\");
                else if (c < 0x20 || c > 0x7e) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.ToString();
        }

        // Changes server.properties values in place ({ "changes": { key: value } }), keeping every other line
        // and comment as it was. Common settings are checked; any other existing setting takes a single line of text.
        static Dictionary<string, object> SaveServerProperties(Dictionary<string, object> body)
        {
            string dir = RequireServerDir();
            if (ServerBusy() || ExternalServerRunning(dir, null)) throw new UserError("Stop the server first. It rewrites server.properties while it runs.");
            string path = Path.Combine(dir, "server.properties");
            Dictionary<string, string> existing = ReadProperties(path);
            var changes = new Dictionary<string, string>();
            Dictionary<string, object> sent = D(Get(body, "changes"));
            if (sent != null)
            {
                foreach (KeyValuePair<string, object> pair in sent)
                {
                    if (pair.Value == null) continue;
                    string text = pair.Value is bool ? ((bool)pair.Value ? "true" : "false") : Convert.ToString(pair.Value, CultureInfo.InvariantCulture).Trim();
                    if (!existing.ContainsKey(pair.Key) && !KnownProperties.Contains(pair.Key))
                        throw new UserError("server.properties has no setting called " + pair.Key + ".");
                    string current;
                    if (existing.TryGetValue(pair.Key, out current) && current == text) continue;
                    changes[pair.Key] = ValidateProperty(pair.Key, text);
                }
            }
            if (changes.Count == 0) throw new UserError("Nothing was changed.");
            var lines = File.Exists(path) ? File.ReadAllLines(path, Encoding.UTF8).ToList() : new List<string>();
            var done = new HashSet<string>();
            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i].TrimStart();
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string value;
                if (changes.TryGetValue(key, out value)) { lines[i] = key + "=" + EscapeProperty(value); done.Add(key); }
            }
            foreach (KeyValuePair<string, string> change in changes)
                if (!done.Contains(change.Key)) lines.Add(change.Key + "=" + EscapeProperty(change.Value));
            if (File.Exists(path)) File.Copy(path, path + ".bak", true);
            File.WriteAllText(path, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
            Log("Server    settings changed: " + string.Join(", ", changes.Select(c => c.Key + "=" + (SecretProperty(c.Key) ? "(hidden)" : c.Value))));
            return Obj("ok", true, "changed", changes.Count);
        }

        static readonly string[] KnownProperties = { "motd", "max-players", "difficulty", "gamemode", "pvp", "white-list", "enforce-whitelist",
            "view-distance", "simulation-distance", "server-port", "spawn-protection", "allow-flight", "hardcore", "online-mode" };

        static bool SecretProperty(string key) { return key.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0 || key.IndexOf("secret", StringComparison.OrdinalIgnoreCase) >= 0; }

        static string ValidateProperty(string key, string value)
        {
            int n;
            if (value.IndexOfAny(new[] { '\r', '\n' }) >= 0) throw new UserError(key + " has to fit on one line.");
            if (value.Length > 1000) throw new UserError(key + " is too long.");
            switch (key)
            {
                case "motd":
                    if (value.Length > 120) throw new UserError("Keep the server description under 120 characters.");
                    return value;
                case "max-players":
                    if (!int.TryParse(value, out n) || n < 1 || n > 500) throw new UserError("Max players must be a number from 1 to 500.");
                    return n.ToString(CultureInfo.InvariantCulture);
                case "view-distance":
                case "simulation-distance":
                    if (!int.TryParse(value, out n) || n < 3 || n > 32) throw new UserError("View and simulation distance must be from 3 to 32.");
                    return n.ToString(CultureInfo.InvariantCulture);
                case "server-port":
                    if (!int.TryParse(value, out n) || n < 1024 || n > 65535) throw new UserError("The port must be a number from 1024 to 65535.");
                    return n.ToString(CultureInfo.InvariantCulture);
                case "spawn-protection":
                    if (!int.TryParse(value, out n) || n < 0 || n > 64) throw new UserError("Spawn protection must be from 0 to 64.");
                    return n.ToString(CultureInfo.InvariantCulture);
                case "difficulty":
                    if (!new[] { "peaceful", "easy", "normal", "hard" }.Contains(value)) throw new UserError("Pick a difficulty.");
                    return value;
                case "gamemode":
                    if (!new[] { "survival", "creative", "adventure", "spectator" }.Contains(value)) throw new UserError("Pick a game mode.");
                    return value;
                case "pvp":
                case "white-list":
                case "enforce-whitelist":
                case "allow-flight":
                case "hardcore":
                case "online-mode":
                    if (value != "true" && value != "false") throw new UserError(key + " must be on or off.");
                    return value;
                default:
                    return value;
            }
        }

        static int ServerPort(string dir)
        {
            string value;
            int port;
            return ReadProperties(Path.Combine(dir, "server.properties")).TryGetValue("server-port", out value) && int.TryParse(value, out port) ? port : 25565;
        }

        static string ServerLoaderVersion(string dir)
        {
            try
            {
                string libs = Path.Combine(dir, @"libraries\net\neoforged\neoforge");
                if (!Directory.Exists(libs)) return null;
                return new DirectoryInfo(libs).GetDirectories().Where(d => File.Exists(Path.Combine(d.FullName, "win_args.txt")))
                    .Select(d => d.Name).OrderByDescending(v => v, Comparer<string>.Create(CompareVersions)).FirstOrDefault();
            }
            catch { return null; }
        }

        static string JvmMaxMemory(string dir)
        {
            try
            {
                string path = Path.Combine(dir, "user_jvm_args.txt");
                if (!File.Exists(path)) return null;
                Match m = Regex.Match(File.ReadAllText(path), @"(?m)^\s*-Xmx(\d+[GgMm])");
                return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
            }
            catch { return null; }
        }

        static List<object> LanAddresses()
        {
            var list = new List<object>();
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (nic.GetIPProperties().GatewayAddresses.Count == 0) continue; // skip virtual adapters with no route out
                    foreach (UnicastIPAddressInformation a in nic.GetIPProperties().UnicastAddresses)
                        if (a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address)) list.Add(a.Address.ToString());
                }
            }
            catch { }
            return list.Distinct().ToList();
        }

        // A server started some other way (run.bat, another tool) holds its world's session.lock.
        static bool ExternalServerRunning(string dir, Dictionary<string, string> props)
        {
            try
            {
                props = props ?? ReadProperties(Path.Combine(dir, "server.properties"));
                string level;
                if (!props.TryGetValue("level-name", out level) || string.IsNullOrWhiteSpace(level)) level = "world";
                string lockPath = Path.Combine(dir, level, "session.lock");
                if (!File.Exists(lockPath)) return false;
                using (File.Open(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                return false;
            }
            catch (IOException) { return true; }
            catch { return false; }
        }

        static bool PortInUse(int port)
        {
            try { return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(ep => ep.Port == port); }
            catch { return false; }
        }

        // ---------- Starting ----------

        // { java.exe, arguments } the way run.bat would start the server, with nogui.
        static string[] ServerLaunch(string dir)
        {
            string line = null;
            string bat = Path.Combine(dir, "run.bat");
            if (File.Exists(bat))
            {
                line = File.ReadAllLines(bat).Select(l => l.Trim())
                    .FirstOrDefault(l => l.Length > 0 && !l.StartsWith("REM", StringComparison.OrdinalIgnoreCase) && !l.StartsWith("::") && !l.StartsWith("@")
                        && (l.Contains("@libraries") || l.Contains(" -jar ")));
            }
            if (line == null)
            {
                string version = ServerLoaderVersion(dir);
                if (version == null) throw new UserError("Couldn't work out how to start this server: there's no run.bat and no NeoForge in its libraries folder.");
                line = "java @user_jvm_args.txt @libraries/net/neoforged/neoforge/" + version + "/win_args.txt %*";
            }
            List<string> tokens = SplitCommandLine(line);
            if (tokens.Count < 2) throw new UserError("run.bat's start line isn't something this app understands: " + line);
            string exe = tokens[0];
            if (exe.Equals("java", StringComparison.OrdinalIgnoreCase) || exe.Equals("java.exe", StringComparison.OrdinalIgnoreCase)) exe = FindJava();
            else if (!Path.IsPathRooted(exe)) exe = Path.Combine(dir, exe);
            if (exe == null || !File.Exists(exe)) throw new UserError("Java wasn't found. Install Java 21 (for example Eclipse Temurin) so the server can start.");

            var args = new List<string> { "-Dstdout.encoding=UTF-8", "-Dstderr.encoding=UTF-8", "-Dterminal.jline=false", "-Dterminal.ansi=false" };
            foreach (string token in tokens.Skip(1))
            {
                if (token == "%*") continue;
                if (token.StartsWith("@") && !File.Exists(Path.Combine(dir, token.Substring(1))))
                    throw new UserError("run.bat points at " + token.Substring(1) + ", which isn't in the server folder.");
                args.Add(token);
            }
            if (!args.Any(a => a.Equals("nogui", StringComparison.OrdinalIgnoreCase) || a.Equals("--nogui", StringComparison.OrdinalIgnoreCase))) args.Add("nogui");
            return new[] { exe, string.Join(" ", args.Select(QuoteArg)) };
        }

        static List<string> SplitCommandLine(string line)
        {
            var tokens = new List<string>();
            var current = new StringBuilder();
            bool quoted = false, any = false;
            foreach (char c in line)
            {
                if (c == '"') { quoted = !quoted; any = true; continue; }
                if (char.IsWhiteSpace(c) && !quoted)
                {
                    if (any) tokens.Add(current.ToString());
                    current.Clear();
                    any = false;
                    continue;
                }
                current.Append(c);
                any = true;
            }
            if (any) tokens.Add(current.ToString());
            return tokens;
        }

        static string QuoteArg(string arg)
        {
            return arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0 ? arg : "\"" + arg.Replace("\"", "\\\"") + "\"";
        }

        static string FindJava()
        {
            foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try
                {
                    string candidate = Path.Combine(folder.Trim().Trim('"'), "java.exe");
                    if (folder.Trim().Length > 0 && File.Exists(candidate)) return candidate;
                }
                catch { }
            }
            string home = Environment.GetEnvironmentVariable("JAVA_HOME");
            if (!string.IsNullOrEmpty(home) && File.Exists(Path.Combine(home, @"bin\java.exe"))) return Path.Combine(home, @"bin\java.exe");
            return null;
        }

        static Dictionary<string, object> StartServer()
        {
            string dir = RequireServerDir();
            lock (ServerLock)
            {
                if (serverProcess != null) throw new UserError("The server is already " + (serverState == "stopping" ? "stopping." : "running."));
                if (!EulaAccepted(dir)) throw new UserError("eula", "The server needs you to accept Minecraft's EULA before it can start.");
                if (ExternalServerRunning(dir, null)) throw new UserError("A server is already running from this folder (it wasn't started from here). Stop it in its own window first.");
                int port = ServerPort(dir);
                if (PortInUse(port)) throw new UserError("Port " + port + " is already in use, so the server can't start. Another server or program is using it.");
                string[] launch = ServerLaunch(dir);

                var start = new ProcessStartInfo(launch[0], launch[1]);
                start.UseShellExecute = false;
                start.CreateNoWindow = true;
                start.WorkingDirectory = dir;
                start.RedirectStandardInput = true;
                start.RedirectStandardOutput = true;
                start.RedirectStandardError = true;
                start.StandardOutputEncoding = Encoding.UTF8;
                start.StandardErrorEncoding = Encoding.UTF8;
                var proc = new Process();
                proc.StartInfo = start;
                AddConsole("Starting the server in " + dir, "app");
                AddConsole(Path.GetFileName(launch[0]) + " " + launch[1], "app");
                proc.Start();
                serverProcess = proc;
                serverInput = new StreamWriter(proc.StandardInput.BaseStream, new UTF8Encoding(false));
                serverInput.AutoFlush = true;
                serverInput.NewLine = "\n";
                serverState = "starting";
                serverStartedAt = DateTime.Now;
                serverExitNote = null;
                serverCrashReport = null;
                playersOnline.Clear();

                Thread outThread = ReadServerStream(proc.StandardOutput, "out");
                Thread errThread = ReadServerStream(proc.StandardError, "err");
                var watcher = new Thread(delegate ()
                {
                    outThread.Join();
                    errThread.Join();
                    try { proc.WaitForExit(); } catch { }
                    ServerExited(proc);
                });
                watcher.IsBackground = true;
                watcher.Name = "server-watch";
                watcher.Start();
            }
            Log("Server    started from " + dir);
            NotifyPage("server");
            return ServerStatus();
        }

        static Thread ReadServerStream(StreamReader reader, string kind)
        {
            var thread = new Thread(delegate ()
            {
                try
                {
                    string line;
                    while ((line = reader.ReadLine()) != null) OnServerLine(line, kind);
                }
                catch { }
            });
            thread.IsBackground = true;
            thread.Name = "server-" + kind;
            thread.Start();
            return thread;
        }

        static void OnServerLine(string line, string kind)
        {
            line = AnsiCodes.Replace(line, "").TrimEnd();
            if (line.Length == 0) return;
            bool changed = false;
            lock (ServerLock)
            {
                if (serverState == "starting" && DoneLine.IsMatch(line)) { serverState = "running"; changed = true; }
                Match m = JoinLine.Match(line);
                if (m.Success && !playersOnline.Contains(m.Groups[1].Value)) { playersOnline.Add(m.Groups[1].Value); changed = true; }
                m = LeftLine.Match(line);
                if (m.Success && playersOnline.Remove(m.Groups[1].Value)) changed = true;
            }
            AddConsole(line, kind);
            if (changed) NotifyPage("server");
        }

        static void ServerExited(Process proc)
        {
            int code = -1;
            try { code = proc.ExitCode; } catch { }
            string note;
            bool quit;
            lock (ServerLock)
            {
                if (serverProcess != proc) return;
                bool requested = serverState == "stopping";
                serverProcess = null;
                serverInput = null;
                serverState = "stopped";
                playersOnline.Clear();
                serverStoppedAt = DateTime.Now;
                serverCrashReport = NewestCrashReport(proc.StartInfo.WorkingDirectory, serverStartedAt);
                note = code == 0 || requested && serverCrashReport == null ? "Server stopped" : "Server stopped unexpectedly (exit code " + code + ")";
                if (serverCrashReport != null) note += ". It wrote a crash report: " + Path.GetFileName(serverCrashReport);
                serverExitNote = note;
                quit = quitWhenStopped;
                quitWhenStopped = false;
            }
            AddConsole(note + " at " + DateTime.Now.ToString("HH:mm:ss") + ".", "app");
            Log("Server    " + note.Substring("Server ".Length));
            try { proc.Dispose(); } catch { }
            NotifyPage("server");
            if (quit) ui.Post(delegate { Quit(); }, null);
        }

        static string NewestCrashReport(string dir, DateTime since)
        {
            try
            {
                string folder = Path.Combine(dir, "crash-reports");
                if (!Directory.Exists(folder)) return null;
                FileInfo newest = new DirectoryInfo(folder).GetFiles("crash-*.txt").Where(f => f.LastWriteTime >= since.AddSeconds(-5))
                    .OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
                return newest == null ? null : newest.FullName;
            }
            catch { return null; }
        }

        // ---------- Stopping and commands ----------

        static void SendServerLine(string command)
        {
            // Caller holds ServerLock.
            if (serverProcess == null || serverInput == null) throw new UserError("The server isn't running.");
            try { serverInput.WriteLine(command); }
            catch (IOException) { throw new UserError("The server isn't listening for commands any more."); }
        }

        static Dictionary<string, object> StopServer()
        {
            lock (ServerLock)
            {
                if (serverProcess == null) throw new UserError("The server isn't running.");
                if (serverState != "stopping")
                {
                    SendServerLine("stop");
                    serverState = "stopping";
                }
            }
            AddConsole("> stop", "input");
            Log("Server    stop requested");
            NotifyPage("server");
            return ServerStatus();
        }

        // Last resort when "stop" doesn't finish: ends the Java process (the world may lose the last few minutes).
        static Dictionary<string, object> KillServer()
        {
            lock (ServerLock)
            {
                if (serverProcess == null) throw new UserError("The server isn't running.");
                serverState = "stopping";
                try { serverProcess.Kill(); } catch (Exception ex) { throw new UserError("Windows couldn't end the server: " + ex.Message); }
            }
            AddConsole("Ended the server process.", "app");
            Log("Server    force-stopped");
            return ServerStatus();
        }

        static Dictionary<string, object> ServerCommand(string command)
        {
            command = (command ?? "").Trim();
            if (command.StartsWith("/")) command = command.Substring(1).Trim();
            if (command.Length == 0) throw new UserError("Type a command first.");
            if (command.Length > 1000 || command.IndexOfAny(new[] { '\r', '\n' }) >= 0) throw new UserError("That command is too long.");
            bool stopping = Regex.IsMatch(command, @"^stop\s*$", RegexOptions.IgnoreCase);
            lock (ServerLock)
            {
                SendServerLine(command);
                if (stopping) serverState = "stopping";
            }
            AddConsole("> " + command, "input");
            if (stopping) NotifyPage("server");
            return Obj("ok", true);
        }

        // "Stop the server, then quit" from the close-window question.
        static void BeginStopServerThenQuit()
        {
            lock (ServerLock)
            {
                if (serverProcess == null)
                {
                    ui.Post(delegate { Quit(); }, null);
                    return;
                }
                quitWhenStopped = true;
                if (serverState != "stopping")
                {
                    SendServerLine("stop");
                    serverState = "stopping";
                }
            }
            AddConsole("> stop", "input");
            Log("Server    stop requested before quitting");
            NotifyPage("server");
        }

        // The window closed some other way (Windows shutting down): give the server a fair chance to save.
        static void StopServerOnExit()
        {
            Process proc;
            lock (ServerLock)
            {
                proc = serverProcess;
                if (proc == null) return;
                try { if (serverState != "stopping") SendServerLine("stop"); } catch { }
                serverState = "stopping";
            }
            try { proc.WaitForExit(60000); } catch { }
        }
    }
}
