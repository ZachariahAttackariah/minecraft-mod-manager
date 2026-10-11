// Creating a dedicated server from an instance, and switching between the servers the app knows about.
//
// A new server gets the loader's official server installer (NeoForge and Forge from their Maven
// repositories, checked against the published SHA-1; Fabric's server launcher from Fabric's meta
// service), then the instance's server-side mods and configs, memory, port and name. eula.txt is
// only written when the person ticks "I accept Minecraft's EULA". The installer runs in the
// background and its output appears in the server console.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace MinecraftModManager
{
    static partial class Program
    {
        const int KeepKnownServers = 10;
        static readonly List<string> knownServers = new List<string>();
        static volatile bool serverCreating;
        static string serverInstallerOverride; // --server-installer <jar>, for testing without downloading

        static string Plural(int n, string one, string many) { return n + " " + (n == 1 ? one : many); }

        static string DefaultServersRoot()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Minecraft Servers");
        }

        static void RememberServer(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            lock (knownServers)
            {
                knownServers.RemoveAll(s => SamePath(s, path));
                knownServers.Insert(0, path);
                if (knownServers.Count > KeepKnownServers) knownServers.RemoveRange(KeepKnownServers, knownServers.Count - KeepKnownServers);
            }
        }

        static List<string> KnownServersCopy()
        {
            lock (knownServers) return knownServers.ToList();
        }

        static List<object> ServerList()
        {
            List<string> servers = KnownServersCopy();
            if (serverDir != null && !servers.Any(s => SamePath(s, serverDir))) servers.Insert(0, serverDir);
            return servers.Select(s => (object)Obj("path", s, "name", Path.GetFileName(s.TrimEnd('\\')), "exists", Directory.Exists(s),
                "current", serverDir != null && SamePath(s, serverDir), "neoforge", Directory.Exists(s) ? ServerLoaderVersion(s) : null)).ToList();
        }

        static Dictionary<string, object> SelectServer(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new UserError("No server was chosen.");
            if (ServerBusy() || serverCreating) throw new UserError("Stop the server before switching to another one.");
            path = Path.GetFullPath(path);
            if (!KnownServersCopy().Any(s => SamePath(s, path)) && !(serverDir != null && SamePath(serverDir, path)))
                throw new UserError("That server isn't in the list. Use \"Add existing folder…\" to add it.");
            if (!Directory.Exists(path)) throw new UserError(path + " isn't there any more.");
            serverDir = path;
            RememberServer(path);
            SaveSettings();
            Log("Server    now using " + path);
            return Obj("ok", true, "folder", path);
        }

        static Dictionary<string, object> ServerCreateInfo()
        {
            return Obj("ok", true, "root", DefaultServersRoot(), "ramMb", TotalRamMb(), "java", FindJava(), "creating", serverCreating);
        }

        static Dictionary<string, object> PickServerParent(string current)
        {
            string picked = null;
            string start = !string.IsNullOrEmpty(current) && Directory.Exists(current) ? current : null;
            ui.Send(delegate { picked = PickFolder(start, "Choose where to put the new server's folder"); }, null);
            return Obj("ok", true, "folder", picked);
        }

        // Java for the server: the instance's own Java when it sets one, else the one on PATH / JAVA_HOME.
        static string JavaForInstance(InstanceInfo info)
        {
            try
            {
                string cfg = Path.Combine(InstanceDir(info), "instance.cfg");
                string line = File.ReadAllLines(cfg).FirstOrDefault(l => l.StartsWith("JavaPath="));
                if (line != null)
                {
                    string path = line.Substring("JavaPath=".Length).Trim().Replace('/', '\\');
                    string java = Regex.Replace(path, @"javaw\.exe$", "java.exe", RegexOptions.IgnoreCase);
                    if (File.Exists(java)) return java;
                }
            }
            catch { }
            return FindJava();
        }

        static Dictionary<string, object> CreateServer(Dictionary<string, object> body)
        {
            string name = Regex.Replace(Str(body, "name") ?? "", @"\s+", " ").Trim();
            if (name.Length == 0 || name.Length > 60) throw new UserError("Give the server a name (up to 60 characters).");
            if (ServerBusy()) throw new UserError("Stop the running server first.");
            if (serverCreating) throw new UserError("A server is already being created. Wait for it to finish.");
            InstanceInfo instance = FindInstance(Str(body, "instanceId"));
            if (instance.Loader == null) throw new UserError(instance.Name + " has no mod loader.");
            if (instance.Loader == "quilt") throw new UserError("Creating Quilt servers isn't supported yet.");
            string parent = Str(body, "parent");
            if (string.IsNullOrWhiteSpace(parent)) parent = DefaultServersRoot();
            parent = Path.GetFullPath(parent);
            string folder = Regex.Replace(name, @"[<>:""/\\|?*\x00-\x1f]", "").Trim().TrimEnd('.');
            if (folder.Length == 0) folder = "Server";
            string dir = Path.Combine(parent, folder);
            if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any()) throw new UserError(dir + " already exists and isn't empty. Pick another name or location.");
            int memoryMb = IntValue(body, "memoryMb");
            if (memoryMb == 0) memoryMb = 4096;
            CheckMemory(memoryMb);
            int port = IntValue(body, "port");
            if (port == 0) port = 25565;
            if (port < 1024 || port > 65535) throw new UserError("The port must be a number from 1024 to 65535.");
            string java = JavaForInstance(instance);
            if (java == null) throw new UserError("Java wasn't found. Install Java 21 (for example Eclipse Temurin) so the server can be installed.");
            bool copyMods = Bool(body, "copyMods");
            bool eula = Bool(body, "acceptEula");

            Directory.CreateDirectory(dir);
            serverCreating = true;
            serverDir = dir;
            RememberServer(dir);
            SaveSettings();
            lock (ServerLock)
            {
                consoleLines.Clear();
                serverExitNote = null;
                serverCrashReport = null;
            }
            AddConsole("Creating the server \"" + name + "\" in " + dir, "app");
            AddConsole("Minecraft " + instance.Minecraft + ", " + LoaderName(instance.Loader) + " " + instance.LoaderVersion + ", based on " + instance.Name, "app");
            Log("Server    creating " + name + " in " + dir + " (based on " + instance.Name + ")");
            NotifyPage("server");

            var worker = new Thread(delegate ()
            {
                try
                {
                    InstallServer(dir, instance, java);
                    FinishServer(dir, name, instance, memoryMb, port, copyMods, eula);
                    AddConsole("Done. The server is ready" + (eula ? "" : " (accept Minecraft's EULA on the Server page before starting it)") + ". Press Start server to run it.", "app");
                    Log("Server    created " + name);
                }
                catch (Exception ex)
                {
                    AddConsole("Couldn't create the server: " + ex.Message, "err");
                    Log("Server    creating " + name + " failed: " + ex.Message);
                }
                finally
                {
                    serverCreating = false;
                    NotifyPage("server");
                }
            });
            worker.IsBackground = true;
            worker.Name = "server-create";
            worker.Start();
            return Obj("ok", true, "folder", dir);
        }

        static void InstallServer(string dir, InstanceInfo instance, string java)
        {
            string mc = instance.Minecraft, version = instance.LoaderVersion;
            if (instance.Loader == "fabric")
            {
                Dictionary<string, object> stable = A(HttpJson("GET", "https://meta.fabricmc.net/v2/versions/installer", null)).Select(D)
                    .FirstOrDefault(v => Get(v, "stable") is bool && (bool)Get(v, "stable"));
                if (stable == null) throw new UserError("Fabric's installer list didn't answer.");
                string url = "https://meta.fabricmc.net/v2/versions/loader/" + Uri.EscapeDataString(mc) + "/" + Uri.EscapeDataString(version) + "/" +
                    Uri.EscapeDataString(S(stable, "version")) + "/server/jar";
                AddConsole("Downloading Fabric's server launcher…", "app");
                Download(url, Path.Combine(dir, "fabric-server-launch.jar"), null);
                File.WriteAllText(Path.Combine(dir, "run.bat"), "@echo off\r\njava @user_jvm_args.txt -jar fabric-server-launch.jar %*\r\npause\r\n", Encoding.ASCII);
                AddConsole("Minecraft's own server files are downloaded the first time the server starts.", "app");
                return;
            }
            string jarUrl, file;
            if (instance.Loader == "neoforge")
            {
                file = "neoforge-" + version + "-installer.jar";
                jarUrl = "https://maven.neoforged.net/releases/net/neoforged/neoforge/" + version + "/" + file;
            }
            else
            {
                string full = mc + "-" + version;
                file = "forge-" + full + "-installer.jar";
                jarUrl = "https://maven.minecraftforge.net/net/minecraftforge/forge/" + full + "/" + file;
            }
            string installer = Path.Combine(dir, file);
            if (serverInstallerOverride != null)
            {
                AddConsole("Using the test installer " + serverInstallerOverride, "app");
                File.Copy(serverInstallerOverride, installer, true);
            }
            else
            {
                AddConsole("Downloading " + file + "…", "app");
                Download(jarUrl, installer, jarUrl + ".sha1");
            }
            AddConsole("Running the installer. It downloads Minecraft's server and the loader's libraries, which takes a minute or two.", "app");
            int code = RunAndStream(java, "-jar \"" + installer + "\" --installServer \"" + dir + "\"", dir);
            if (code != 0) throw new UserError("the installer stopped with exit code " + code + ". See the lines above.");
            try { File.Delete(installer); } catch { }
            if (!File.Exists(Path.Combine(dir, "run.bat"))) throw new UserError("the installer finished, but there's no run.bat in the server folder.");
        }

        // Downloads a file, checking it against a published SHA-1 when one is given.
        static void Download(string url, string path, string sha1Url)
        {
            string temp = path + ".download";
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.UserAgent = ModrinthUserAgent;
                request.Timeout = 60000;
                request.ReadWriteTimeout = 60000;
                using (WebResponse response = request.GetResponse())
                using (Stream input = response.GetResponseStream())
                using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                    input.CopyTo(output);
                if (sha1Url != null)
                {
                    string expected;
                    using (var client = new WebClient())
                    {
                        client.Headers[HttpRequestHeader.UserAgent] = ModrinthUserAgent;
                        expected = client.DownloadString(sha1Url).Trim().Split(' ')[0].ToLowerInvariant();
                    }
                    string actual;
                    using (SHA1 sha = SHA1.Create())
                    using (FileStream stream = File.OpenRead(temp))
                        actual = Hex(sha.ComputeHash(stream));
                    if (actual != expected) throw new UserError("the download didn't match its published checksum, so it was thrown away.");
                    AddConsole("Checksum OK (" + actual.Substring(0, 12) + "…)", "app");
                }
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
            }
            catch (WebException ex)
            {
                throw new UserError("couldn't download " + Path.GetFileName(path) + " (" + ex.Message + ").");
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }

        static int RunAndStream(string exe, string args, string workingDir)
        {
            var start = new ProcessStartInfo(exe, args);
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.WorkingDirectory = workingDir;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            start.StandardOutputEncoding = Encoding.UTF8;
            start.StandardErrorEncoding = Encoding.UTF8;
            using (Process proc = Process.Start(start))
            {
                Thread outThread = ReadServerStream(proc.StandardOutput, "out");
                Thread errThread = ReadServerStream(proc.StandardError, "err");
                proc.WaitForExit();
                outThread.Join();
                errThread.Join();
                return proc.ExitCode;
            }
        }

        static void FinishServer(string dir, string name, InstanceInfo instance, int memoryMb, int port, bool copyMods, bool eula)
        {
            // Memory: user_jvm_args.txt (the installer writes one full of comments for NeoForge and Forge).
            string args = Path.Combine(dir, "user_jvm_args.txt");
            List<string> lines = File.Exists(args) ? File.ReadAllLines(args).Where(l => !Regex.IsMatch(l.Trim(), @"^-Xm[sx]")).ToList() : new List<string>();
            lines.Add("-Xms" + Math.Min(memoryMb, 2048) + "M");
            lines.Add("-Xmx" + memoryMb + "M");
            File.WriteAllLines(args, lines);

            // The server fills in the rest of server.properties on its first start.
            File.WriteAllText(Path.Combine(dir, "server.properties"),
                "#Minecraft server properties (created by Minecraft Mod Manager)\nmotd=" + EscapeProperty(name) + "\nserver-port=" + port + "\nonline-mode=true\n",
                new UTF8Encoding(false));
            if (eula)
            {
                File.WriteAllText(Path.Combine(dir, "eula.txt"),
                    "#By changing the setting below to TRUE you are indicating your agreement to our EULA (https://aka.ms/MinecraftEULA).\n#Accepted in Minecraft Mod Manager\neula=true\n",
                    new UTF8Encoding(false));
                Log("Server    accepted the Minecraft EULA for " + name);
            }
            Directory.CreateDirectory(Path.Combine(dir, "mods"));
            if (!copyMods) return;

            // Mods: every enabled jar tagged "server". Without tags nothing tells server mods from client ones, so all are copied.
            string mods = instance.Mods;
            TagsDoc tags = null;
            try { tags = LoadTagsDoc(Path.Combine(mods, "mod-tags.json")); } catch (UserError) { }
            bool haveTags = tags != null && tags.Mods.Count > 0;
            int copied = 0, skippedClient = 0, untagged = 0;
            foreach (FileInfo jar in new DirectoryInfo(mods).GetFiles("*.jar"))
            {
                List<string> jarTags = null;
                if (haveTags)
                {
                    string key = tags.Mods.Keys.FirstOrDefault(k => string.Equals(k, jar.Name, StringComparison.OrdinalIgnoreCase));
                    if (key != null) jarTags = tags.Mods[key];
                }
                bool hasSide = jarTags != null && (jarTags.Contains("server") || jarTags.Contains("client"));
                if (haveTags && !hasSide) { untagged++; continue; }
                if (hasSide && !jarTags.Contains("server")) { skippedClient++; continue; }
                File.Copy(jar.FullName, Path.Combine(dir, "mods", jar.Name), true);
                copied++;
            }
            AddConsole("Copied " + Plural(copied, "server mod", "server mods") + " from " + instance.Name + (skippedClient > 0 ? "; left out " + Plural(skippedClient, "client-only mod", "client-only mods") : "") +
                (untagged > 0 ? "; left out " + Plural(untagged, "mod", "mods") + " without a client/server tag (tag them, then use Sync from the game)" : "") + ".", "app");

            // Configs: everything except client-only ones, backups and caches (the same rules as Sync).
            string gameDir = Path.GetDirectoryName(mods);
            int configs = 0;
            foreach (string sub in new[] { "config", "defaultconfigs" })
            {
                string from = Path.Combine(gameDir, sub);
                if (!Directory.Exists(from)) continue;
                foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
                {
                    string rel = file.Substring(from.Length).TrimStart('\\');
                    if (sub == "config" && SkipConfig.IsMatch(rel)) continue;
                    string to = Path.Combine(dir, sub, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(to));
                    File.Copy(file, to, true);
                    configs++;
                }
            }
            AddConsole("Copied " + Plural(configs, "config file", "config files") + ".", "app");
        }
    }
}
