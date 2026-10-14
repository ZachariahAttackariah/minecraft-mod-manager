// Minecraft Mod Manager
//
// Double-click the exe: it opens the manager in its own window (WebView2, the Edge engine
// built into Windows). Behind the window a small web server on http://localhost:8737/
// serves the page and does the file work. Links to Modrinth / CurseForge open as tabs in
// the same window, and .jar downloads in any tab go straight into the mods folder.
// The page (embedded in the exe) can enable, disable and
// delete jars in one mods folder, one at a time or as a selection:
//   disable  X.jar          -> X.jar.disabled   (the launcher's naming)
//   enable   X.jar.disabled -> X.jar
//   delete   moves the jar to the Windows Recycle Bin
//   tags     adds / removes tags in the folder's mod-tags.json (previous copy kept as .bak)
// Only this PC can reach the server, and every API call must carry the random
// token embedded in the page it served. Every change is appended to actions.log.
//
// The code is split by topic: Program.cs (core, HTTP, Modrinth, tags), Window.cs (window + tabs),
// Server.cs (hosting the Minecraft server), Sync.cs (game -> server), JarMeta.cs (what's inside a
// jar, and the pre-launch check), Details.cs (mod details, versions, changelogs),
// Snapshots.cs (snapshots + finding a problem mod) and Cleanup.cs (disk cleanup).
//
// Command line (optional): --mods <folder>  --server <folder>  --data <folder>  --port <number>
//   --no-window (server only, for testing). Test runs with --mods never touch saved settings,
//   use no server folder unless --server is given, and keep snapshots in a temp folder unless --data is given.
// Built with the C# 5 compiler that ships with Windows plus Microsoft's WebView2 DLLs; see build.ps1.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MinecraftModManager
{
    sealed class UserError : Exception
    {
        public readonly string Code; // lets the page react to specific problems, e.g. "eula"
        public UserError(string message) : base(message) { }
        public UserError(string code, string message) : base(message) { Code = code; }
    }

    static partial class Program
    {
        const string AppName = "Minecraft Mod Manager";
        const string PageResource = "MinecraftModManager.mod-manager.html";

        static readonly string AppDir = AppDomain.CurrentDomain.BaseDirectory;
        static readonly string SettingsPath = Path.Combine(AppDir, "MinecraftModManager.settings.json");
        static readonly string LogPath = Path.Combine(AppDir, "actions.log");
        static readonly string Token = Guid.NewGuid().ToString("N");
        static readonly object FileLock = new object();
        static readonly object LogLock = new object();
        static readonly Regex JarName = new Regex(@"\.jar(\.disabled)?$", RegexOptions.IgnoreCase);

        static volatile string modsDir;
        static volatile string gameVersion = "1.21.1";
        static volatile string loader = "neoforge";
        static bool rememberFolder = true;
        static string dataDir;                        // snapshots, sync backups (normally %LOCALAPPDATA%\MinecraftModManager)
        static Dictionary<string, object> savedWindow; // window position from the settings file
        static int port = 8737;
        static string origin;
        static string pageHtml;
        static HttpListener listener;
        static AppWindow window;
        static IntPtr windowHandle;
        static Mutex singleInstance;
        static SynchronizationContext ui;

        [STAThread]
        static int Main(string[] args)
        {
            try { SetProcessDPIAware(); } catch { }
            // Built without a target-framework stamp, so .NET would default to old TLS; Modrinth needs 1.2+.
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | (SecurityProtocolType)12288; }
            catch (NotSupportedException) { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; }
            ServicePointManager.DefaultConnectionLimit = 8;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool showWindow = true;
            string folderArg = null, serverArg = null, dataArg = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--mods" && i + 1 < args.Length) folderArg = args[++i];
                else if (args[i] == "--server" && i + 1 < args.Length) serverArg = args[++i];
                else if (args[i] == "--data" && i + 1 < args.Length) dataArg = args[++i];
                else if (args[i] == "--prism" && i + 1 < args.Length) prismDataOverride = Path.GetFullPath(args[++i]);
                else if (args[i] == "--server-installer" && i + 1 < args.Length) serverInstallerOverride = Path.GetFullPath(args[++i]);
                else if (args[i] == "--port" && i + 1 < args.Length) port = int.Parse(args[++i]);
                else if (args[i] == "--no-window" || args[i] == "--no-browser") showWindow = false;
            }
            origin = "http://localhost:" + port;

            // One copy per port. Launching it again brings the running window to the front.
            bool firstCopy;
            singleInstance = new Mutex(true, "MinecraftModManager.Instance." + port, out firstCopy);
            if (!firstCopy)
            {
                try { EventWaitHandle.OpenExisting("MinecraftModManager.Show." + port).Set(); } catch { }
                return 0;
            }
            var showRequests = new EventWaitHandle(false, EventResetMode.AutoReset, "MinecraftModManager.Show." + port);

            // A new build installed while the app ran leaves the old exe renamed beside it; tidy it up.
            try
            {
                foreach (string old in Directory.GetFiles(AppDir, "MinecraftModManager.old*.exe"))
                    try { File.Delete(old); } catch { } // still running: next time
            }
            catch { }

            pageHtml = LoadPage();
            var context = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(context);
            ui = context;

            if (folderArg != null)
            {
                modsDir = Path.GetFullPath(folderArg);
                rememberFolder = false;
                serverDir = serverArg == null ? null : Path.GetFullPath(serverArg);
                dataDir = dataArg != null ? Path.GetFullPath(dataArg) : Path.Combine(Path.GetTempPath(), "MinecraftModManager-test");
            }
            else
            {
                modsDir = LoadSavedFolder();
                if (serverArg != null) serverDir = Path.GetFullPath(serverArg);
                dataDir = dataArg != null ? Path.GetFullPath(dataArg)
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MinecraftModManager");
            }
            if (curseforgeKey == null) curseforgeKey = ReadCurseForgeKey(); // test runs (--mods) don't read the rest of the settings

            // A saved folder that's gone falls back to another instance rather than a folder dialog, which can
            // open behind other windows and look like the app never started. Test runs (--mods) never fall back.
            if (folderArg == null && (modsDir == null || !Directory.Exists(modsDir)))
            {
                string gone = modsDir;
                string fallback = FallbackFolder();
                if (fallback != null)
                {
                    modsDir = fallback;
                    SaveSettings();
                    if (gone != null) Log("Now managing " + modsDir + " (" + gone + " isn't there any more)");
                }
            }
            if (modsDir == null || !Directory.Exists(modsDir))
            {
                string picked = PickFolder(null);
                if (picked == null) return 1;
                modsDir = picked;
                SaveSettings();
            }

            RememberFolder(modsDir);
            FollowInstanceTarget(modsDir); // in case the instance's version was changed in the launcher since
            if (serverDir != null) RememberServer(serverDir);

            listener = new HttpListener();
            listener.Prefixes.Add(origin + "/");
            try
            {
                listener.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Couldn't start on port " + port + ". Another program may be using it.\n\n" + ex.Message,
                    AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }

            var server = new Thread(Serve);
            server.IsBackground = true;
            server.Name = "http";
            server.Start();

            var showWatcher = new Thread(delegate ()
            {
                while (true)
                {
                    showRequests.WaitOne();
                    ui.Post(delegate { BringToFront(); }, null);
                }
            });
            showWatcher.IsBackground = true;
            showWatcher.Start();

            // Settings changed in a game since last time reach the other sharing instances, even ones
            // started from the launcher itself.
            ThreadPool.QueueUserWorkItem(delegate { TryApplySharingDefaults(); TrySyncGameSettings(); });

            if (showWindow)
            {
                window = new AppWindow();
                Application.Run(window);
                WaitForBrowserExit();
            }
            else
            {
                Application.Run();
            }
            StopServerOnExit();
            try { listener.Stop(); listener.Close(); } catch { }
            GC.KeepAlive(singleInstance);
            return 0;
        }

        static void BringToFront()
        {
            if (window == null || window.IsDisposed || !window.Visible)
            {
                OpenBrowser();
                return;
            }
            window.ShowManager();
        }

        // ---------- Downloads (the window and its tabs are in Window.cs) ----------

        // A download in any tab: .jar files go straight into the mods folder,
        // anything else goes to the usual Downloads folder.
        static void HandleDownload(CoreWebView2DownloadStartingEventArgs e, Action<string> report)
        {
            string name = Path.GetFileName(e.ResultFilePath);
            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                HandlePackDownload(e, report); // see Packs.cs
                return;
            }
            if (!name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            {
                report("Saving " + name + " to your Downloads folder. Only .jar and pack .zip files go into the game.");
                return;
            }
            string target = Path.Combine(modsDir, name);
            if (File.Exists(target) || File.Exists(target + ".disabled"))
            {
                e.Cancel = true;
                report(name + " is already in the mods folder, so it wasn't downloaded again.");
                return;
            }
            e.ResultFilePath = target;
            e.Handled = true;
            report("Downloading " + name + "…");
            CoreWebView2DownloadOperation op = e.DownloadOperation;
            op.StateChanged += delegate
            {
                if (op.State == CoreWebView2DownloadState.Completed)
                {
                    string saved = Path.GetFileName(op.ResultFilePath);
                    Log("Downloaded " + saved + " into the mods folder");
                    report("Added " + saved + " to the mods folder.");
                    if (window != null && !window.IsDisposed) window.Notify("refresh");
                }
                else if (op.State == CoreWebView2DownloadState.Interrupted)
                {
                    report("Download of " + name + " failed (" + op.InterruptReason + ").");
                }
            };
        }

        static string ShortPath(string path)
        {
            string[] parts = path.TrimEnd('\\', '/').Split('\\', '/');
            return parts.Length <= 3 ? path : "…\\" + string.Join("\\", parts.Skip(parts.Length - 3));
        }

        // ---------- HTTP ----------

        static void Serve()
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = listener.GetContext(); }
                catch { break; }
                ThreadPool.QueueUserWorkItem(delegate { Handle(ctx); });
            }
        }

        static void Handle(HttpListenerContext ctx)
        {
            HttpListenerRequest req = ctx.Request;
            string path = req.Url.AbsolutePath;
            string method = req.HttpMethod;
            try
            {
                if (method == "GET" && (path == "/" || path == "/index.html"))
                {
                    SendText(ctx, 200, "text/html; charset=utf-8", pageHtml.Replace("__MOD_MANAGER_TOKEN__", Token));
                    return;
                }
                if (method == "GET" && path == "/api/ping")
                {
                    SendJson(ctx, 200, Obj("app", "minecraft-mod-manager"));
                    return;
                }
                if (!path.StartsWith("/api/"))
                {
                    SendText(ctx, 404, "text/plain; charset=utf-8", "Not found");
                    return;
                }

                string requestOrigin = req.Headers["Origin"];
                if (!string.IsNullOrEmpty(requestOrigin) && requestOrigin != origin)
                {
                    SendError(ctx, 403, "Requests from other websites are blocked.");
                    return;
                }
                // A mod's logo from inside its jar. Images can't send the token header, so it comes in the URL.
                if (method == "GET" && path == "/api/mod-icon")
                {
                    if (req.QueryString["t"] != Token) { SendText(ctx, 403, "text/plain; charset=utf-8", "Forbidden"); return; }
                    SendModIcon(ctx, req.QueryString["file"]);
                    return;
                }
                if (req.Headers["X-Mod-Manager-Token"] != Token)
                {
                    SendError(ctx, 403, "This page is from an earlier run of Minecraft Mod Manager. Reload the page.");
                    return;
                }

                if (method == "GET" && path == "/api/state")
                {
                    SendJson(ctx, 200, GetState());
                    return;
                }
                if (method == "GET" && path == "/api/curseforge/search")
                {
                    int cfOffset;
                    int.TryParse(req.QueryString["offset"], out cfOffset);
                    SendJson(ctx, 200, CurseForgeSearch(req.QueryString["q"], cfOffset, req.QueryString["sort"], req.QueryString["category"]));
                    return;
                }
                if (method == "GET" && path == "/api/modrinth/search")
                {
                    int offset;
                    int.TryParse(req.QueryString["offset"], out offset);
                    SendJson(ctx, 200, ModrinthSearch(req.QueryString["q"], offset, req.QueryString["sort"], req.QueryString["category"], req.QueryString["side"]));
                    return;
                }
                if (method == "GET" && path == "/api/packs/search")
                {
                    int offset;
                    int.TryParse(req.QueryString["offset"], out offset);
                    SendJson(ctx, 200, SearchPacks(req.QueryString["kind"], req.QueryString["place"], req.QueryString["q"], offset, req.QueryString["sort"]));
                    return;
                }
                if (method == "GET")
                {
                    object result = null;
                    switch (path)
                    {
                        case "/api/packs":
                            result = ListPacks(req.QueryString["kind"], req.QueryString["place"], req.QueryString["check"] == "1");
                            break;
                        case "/api/server":
                            result = ServerStatus();
                            break;
                        case "/api/server/create-info":
                            result = ServerCreateInfo();
                            break;
                        case "/api/server/console":
                        {
                            long after;
                            long.TryParse(req.QueryString["after"], out after);
                            result = ServerConsole(after, req.QueryString["wait"] == "1");
                            break;
                        }
                        case "/api/sync/preview":
                            result = SyncPreview();
                            break;
                        case "/api/check":
                            result = CheckMods(req.QueryString["side"] == "server" ? "server" : "client");
                            break;
                        case "/api/mod/details":
                            result = ModDetails(req.QueryString["file"]);
                            break;
                        case "/api/modrinth/versions":
                            result = ModrinthVersions(req.QueryString["file"]);
                            break;
                        case "/api/mod/deptree":
                            result = DependencyTree(req.QueryString["file"]);
                            break;
                        case "/api/tags/suggest":
                            result = TagSuggestionSources();
                            break;
                        case "/api/compare":
                            result = CompareFolders(req.QueryString["other"]);
                            break;
                        case "/api/presets":
                            result = ListPresets();
                            break;
                        case "/api/crash":
                            result = ReadLastCrash();
                            break;
                        case "/api/curseforge/changelog":
                            result = CfFileChangelog(req.QueryString["file"], req.QueryString["id"]);
                            break;
                        case "/api/snapshots":
                            result = SnapshotsState();
                            break;
                        case "/api/cleanup":
                            result = CleanupScan();
                            break;
                        case "/api/instances":
                            result = ListInstances();
                            break;
                        case "/api/instances/versions":
                            result = MetaVersions(req.QueryString["minecraft"], req.QueryString["loader"]);
                            break;
                        case "/api/instances/delete-info":
                            result = InstanceDeleteInfo(req.QueryString["id"]);
                            break;
                        case "/api/sharing":
                            result = SharingInfo();
                            break;
                    }
                    if (result != null)
                    {
                        SendJson(ctx, 200, result);
                        return;
                    }
                }
                if (method != "POST")
                {
                    SendError(ctx, 405, "Changes must be sent with POST.");
                    return;
                }

                // A file dropped on the window arrives as raw bytes, not JSON (Drop.cs).
                if (path == "/api/upload")
                {
                    SendJson(ctx, 200, ReceiveDroppedFile(req));
                    return;
                }
                Dictionary<string, object> body = ReadBody(req);
                switch (path)
                {
                    case "/api/set-enabled":
                    {
                        bool enable = Bool(body, "enabled");
                        var results = new List<object>();
                        foreach (string file in StrList(body, "files"))
                        {
                            try { results.Add(Obj("file", file, "newFile", SetEnabled(file, enable))); }
                            catch (UserError ex) { results.Add(Obj("file", file, "error", ex.Message)); }
                        }
                        SendJson(ctx, 200, Obj("ok", true, "results", results));
                        return;
                    }
                    case "/api/delete":
                    {
                        var results = new List<object>();
                        foreach (string file in StrList(body, "files"))
                        {
                            try { MoveToRecycleBin(file); results.Add(Obj("file", file)); }
                            catch (UserError ex) { results.Add(Obj("file", file, "error", ex.Message)); }
                        }
                        SendJson(ctx, 200, Obj("ok", true, "results", results));
                        return;
                    }
                    case "/api/tags":
                    {
                        int changed = UpdateTags(StrList(body, "keys"), StrList(body, "add"), StrList(body, "remove"));
                        SendJson(ctx, 200, Obj("ok", true, "changed", changed));
                        return;
                    }
                    case "/api/choose-folder":
                    {
                        string picked = null;
                        string current = modsDir;
                        ui.Send(delegate { picked = PickFolder(current); }, null);
                        bool changed = picked != null && !string.Equals(picked, current, StringComparison.OrdinalIgnoreCase);
                        if (changed)
                        {
                            RememberFolder(current);
                            modsDir = picked;
                            FollowInstanceTarget(picked);
                            RememberFolder(picked);
                            SaveSettings();
                            Log("Now managing " + picked);
                        }
                        SendJson(ctx, 200, Obj("ok", true, "changed", changed, "modsDir", modsDir, "target", TargetObj()));
                        return;
                    }
                    case "/api/open-folder":
                        OpenModsFolder();
                        SendJson(ctx, 200, Obj("ok", true));
                        return;
                    case "/api/open-game-folder":
                        OpenGameFolder();
                        SendJson(ctx, 200, Obj("ok", true));
                        return;
                    case "/api/settings":
                    {
                        string version = (Str(body, "gameVersion") ?? "").Trim();
                        string newLoader = (Str(body, "loader") ?? "").Trim().ToLowerInvariant();
                        if (!Regex.IsMatch(version, @"^\d+\.\d+(\.\d+)?$")) throw new UserError("Use a Minecraft version like 1.21.1.");
                        if (Array.IndexOf(Loaders, newLoader) < 0) throw new UserError("Pick NeoForge, Forge, Fabric or Quilt.");
                        gameVersion = version;
                        loader = newLoader;
                        SaveSettings();
                        SendJson(ctx, 200, Obj("ok", true, "target", TargetObj()));
                        return;
                    }
                    case "/api/tags/create":
                        SendJson(ctx, 200, CreateTag(Str(body, "name"), Str(body, "description")));
                        return;
                    case "/api/tags/edit":
                        SendJson(ctx, 200, EditTag(Str(body, "name"), Str(body, "newName"), Str(body, "description")));
                        return;
                    case "/api/tags/forget":
                        SendJson(ctx, 200, ForgetTagEntries(StrList(body, "keys")));
                        return;
                    case "/api/tags/delete":
                        SendJson(ctx, 200, DeleteTag(Str(body, "name")));
                        return;
                    case "/api/play":
                        SendJson(ctx, 200, Play());
                        return;
                    case "/api/modrinth/scan":
                        SendJson(ctx, 200, ModrinthScan(Bool(body, "force")));
                        return;
                    case "/api/sides/detect":
                        SendJson(ctx, 200, DetectSides(!body.ContainsKey("serverExtras") || Bool(body, "serverExtras")));
                        return;
                    case "/api/check/remove-duplicates":
                        SendJson(ctx, 200, RemoveDuplicates(body));
                        return;
                    case "/api/prefs":
                        SendJson(ctx, 200, SetPrefs(body));
                        return;
                    case "/api/presets/save":
                        SendJson(ctx, 200, SavePreset(Str(body, "name")));
                        return;
                    case "/api/presets/apply":
                        SendJson(ctx, 200, ApplyPreset(Str(body, "name"), Bool(body, "preview")));
                        return;
                    case "/api/presets/delete":
                        SendJson(ctx, 200, DeletePreset(Str(body, "name")));
                        return;
                    case "/api/crash/open":
                        OpenCrashFile(Str(body, "name"));
                        SendJson(ctx, 200, Obj("ok", true));
                        return;
                    case "/api/check/dependents":
                        SendJson(ctx, 200, DependentsOf(StrList(body, "files")));
                        return;
                    case "/api/mod/extras":
                        SendJson(ctx, 200, SetModExtras(body));
                        return;
                    case "/api/curseforge/key":
                        SendJson(ctx, 200, SetCurseForgeKey(Str(body, "key")));
                        return;
                    case "/api/curseforge/install":
                        SendJson(ctx, 200, CurseForgeInstall(Str(body, "projectId")));
                        return;
                    case "/api/modrinth/install":
                        SendJson(ctx, 200, ModrinthInstall(Str(body, "projectId")));
                        return;
                    case "/api/modrinth/update":
                        SendJson(ctx, 200, ModrinthUpdate(StrList(body, "files")));
                        return;
                    case "/api/modrinth/changelogs":
                        SendJson(ctx, 200, ModrinthChangelogs(StrList(body, "files")));
                        return;
                    case "/api/modrinth/switch":
                        SendJson(ctx, 200, ModrinthSwitch(Str(body, "file"), Str(body, "versionId")));
                        return;
                    case "/api/instances/select":
                        SendJson(ctx, 200, SelectInstance(Str(body, "path")));
                        return;
                    case "/api/instances/create":
                        SendJson(ctx, 200, CreateInstance(body));
                        return;
                    case "/api/instances/settings":
                        SendJson(ctx, 200, InstanceSettings(body));
                        return;
                    case "/api/instances/delete":
                        SendJson(ctx, 200, DeleteInstance(Str(body, "id")));
                        return;
                    case "/api/instances/open":
                        OpenInstanceFolder(Str(body, "id"));
                        SendJson(ctx, 200, Obj("ok", true));
                        return;
                    case "/api/sharing/java":
                        SendJson(ctx, 200, SetSharedJavaArgs(Str(body, "javaArgs")));
                        return;
                    case "/api/packs/install":
                        SendJson(ctx, 200, InstallPack(Str(body, "kind"), Str(body, "place"), Str(body, "projectId")));
                        return;
                    case "/api/packs/update":
                        SendJson(ctx, 200, UpdatePacks(Str(body, "kind"), Str(body, "place"), StrList(body, "names")));
                        return;
                    case "/api/packs/delete":
                        SendJson(ctx, 200, DeletePacks(Str(body, "kind"), Str(body, "place"), StrList(body, "names")));
                        return;
                    case "/api/packs/add":
                        SendJson(ctx, 200, AddPackFiles(Str(body, "kind"), Str(body, "place")));
                        return;
                    case "/api/packs/open":
                        OpenPackFolder(Str(body, "kind"), Str(body, "place"));
                        SendJson(ctx, 200, Obj("ok", true));
                        return;
                    case "/api/packs/resource":
                        SendJson(ctx, 200, SetResourcePackOn(Str(body, "name"), Bool(body, "on")));
                        return;
                    case "/api/packs/shader":
                        SendJson(ctx, 200, SetShader(Str(body, "name")));
                        return;
                    case "/api/sharing/defaults":
                        SendJson(ctx, 200, SetShareByDefault(Bool(body, "on")));
                        return;
                    case "/api/sharing/open":
                        OpenSharedFolder(Str(body, "kind"));
                        SendJson(ctx, 200, Obj("ok", true));
                        return;
                    case "/api/prism/open":
                        OpenPrism();
                        SendJson(ctx, 200, Obj("ok", true));
                        return;
                    case "/api/check/copy-from-server":
                        SendJson(ctx, 200, CopyFromServer(Str(body, "file")));
                        return;
                    case "/api/open-file":
                        OpenConfigFile(Str(body, "side"), Str(body, "path"), Bool(body, "reveal"));
                        SendJson(ctx, 200, Obj("ok", true));
                        return;
                    case "/api/open-url":
                        OpenExternal(Str(body, "url"));
                        SendJson(ctx, 200, Obj("ok", true));
                        return;

                    case "/api/server/create":
                        SendJson(ctx, 200, CreateServer(body));
                        return;
                    case "/api/server/select":
                        SendJson(ctx, 200, SelectServer(Str(body, "path")));
                        return;
                    case "/api/server/pick-parent":
                        SendJson(ctx, 200, PickServerParent(Str(body, "current")));
                        return;
                    case "/api/server/folder":
                        SendJson(ctx, 200, ChooseServerFolder());
                        return;
                    case "/api/server/open-folder":
                        OpenServerFolder(Str(body, "sub"));
                        SendJson(ctx, 200, Obj("ok", true));
                        return;
                    case "/api/server/eula":
                        SendJson(ctx, 200, AcceptEula());
                        return;
                    case "/api/server/properties":
                        SendJson(ctx, 200, SaveServerProperties(body));
                        return;
                    case "/api/server/start":
                        SendJson(ctx, 200, StartServer());
                        return;
                    case "/api/server/stop":
                        SendJson(ctx, 200, StopServer());
                        return;
                    case "/api/server/kill":
                        SendJson(ctx, 200, KillServer());
                        return;
                    case "/api/server/command":
                        SendJson(ctx, 200, ServerCommand(Str(body, "command")));
                        return;
                    case "/api/sync/apply":
                        SendJson(ctx, 200, SyncApply(StrList(body, "mods"), StrList(body, "configsToServer"), StrList(body, "configsToGame")));
                        return;

                    case "/api/snapshots/create":
                        SendJson(ctx, 200, CreateSnapshotApi(Str(body, "name")));
                        return;
                    case "/api/snapshots/preview":
                        SendJson(ctx, 200, RestorePreview(Str(body, "id")));
                        return;
                    case "/api/snapshots/restore":
                        SendJson(ctx, 200, RestoreSnapshotApi(Str(body, "id")));
                        return;
                    case "/api/snapshots/delete":
                        SendJson(ctx, 200, DeleteSnapshot(Str(body, "id")));
                        return;
                    case "/api/snapshots/rename":
                        SendJson(ctx, 200, RenameSnapshot(Str(body, "id"), Str(body, "name")));
                        return;
                    case "/api/bisect/start":
                        SendJson(ctx, 200, BisectStart(StrList(body, "files")));
                        return;
                    case "/api/bisect/answer":
                        SendJson(ctx, 200, BisectAnswer(Str(body, "answer")));
                        return;
                    case "/api/bisect/stop":
                        SendJson(ctx, 200, BisectStop(Bool(body, "restore"), StrList(body, "disable")));
                        return;

                    case "/api/cleanup/run":
                        SendJson(ctx, 200, CleanupRun(StrList(body, "ids")));
                        return;

                    case "/api/quit":
                        if (Bool(body, "stopServer"))
                        {
                            BeginStopServerThenQuit();
                            SendJson(ctx, 200, Obj("ok", true, "waiting", true));
                            return;
                        }
                        SendJson(ctx, 200, Obj("ok", true));
                        ui.Post(delegate { Quit(); }, null);
                        return;
                }
                SendError(ctx, 404, "Unknown request: " + method + " " + path);
            }
            catch (UserError ex)
            {
                try { SendJson(ctx, 400, Obj("error", ex.Message, "code", ex.Code)); } catch { }
            }
            catch (Exception ex)
            {
                TrySendError(ctx, 500, "Something went wrong: " + ex.Message);
            }
        }

        static Dictionary<string, object> GetState()
        {
            string dir = modsDir;
            bool exists = Directory.Exists(dir);
            var files = new List<object>();
            string tagsText = null;
            bool tagsLinked = false;
            if (exists)
            {
                var jars = new DirectoryInfo(dir).GetFiles()
                    .Where(f => JarName.IsMatch(f.Name))
                    .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase);
                foreach (FileInfo file in jars)
                {
                    string[] info = GetJarInfo(file);
                    ModInfo main = GetJarMeta(file).Mods.FirstOrDefault();
                    files.Add(Obj("name", file.Name, "size", file.Length, "modified", file.LastWriteTimeUtc.ToString("o"),
                        "modId", info[0], "displayName", info[1], "logo", main != null && main.Logo != null));
                }
                string tagsPath = Path.Combine(dir, "mod-tags.json");
                if (HasLinkedTags(dir))
                {
                    try { tagsText = LinkedTagsText(dir); } catch { tagsText = null; }
                    tagsLinked = true;
                }
                else if (File.Exists(tagsPath)) tagsText = File.ReadAllText(tagsPath, Encoding.UTF8);
            }
            string[] prism = PrismInstanceFor(dir);
            return Obj("modsDir", dir, "folderExists", exists, "files", files, "tagsText", tagsText, "tagsLinked", tagsLinked, "curseforge", CfReady, "prefs", PrefsCopy(), "extras", ExtrasFor(dir), "lastCrash", exists ? LastCrashInfo() : null, "target", TargetObj(),
                "prism", Obj("installed", FindPrism() != null, "instanceId", prism == null ? null : prism[0], "instanceName", prism == null ? null : prism[1]),
                "server", Obj("configured", serverDir != null, "state", serverState, "players", PlayerCount()),
                "bisect", BisectSummary());
        }

        // ---------- Prism Launcher (Play) ----------
        // Prism is approved for Microsoft sign-in and can start an instance directly:
        // prismlauncher.exe --launch <instance folder name>

        static string FindPrism()
        {
            string[] candidates = {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\PrismLauncher\prismlauncher.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"PrismLauncher\prismlauncher.exe")
            };
            return candidates.FirstOrDefault(File.Exists);
        }

        // When the mods folder is <...>\instances\<id>\minecraft\mods inside Prism, returns { id, name }.
        static string[] PrismInstanceFor(string dir)
        {
            try
            {
                var mods = new DirectoryInfo(dir);
                DirectoryInfo game = mods.Parent;
                if (!mods.Name.Equals("mods", StringComparison.OrdinalIgnoreCase) || game == null) return null;
                if (!game.Name.Equals("minecraft", StringComparison.OrdinalIgnoreCase) && !game.Name.Equals(".minecraft", StringComparison.OrdinalIgnoreCase)) return null;
                DirectoryInfo instance = game.Parent;
                if (instance == null || instance.Parent == null || !instance.Parent.Name.Equals("instances", StringComparison.OrdinalIgnoreCase)) return null;
                string cfg = Path.Combine(instance.FullName, "instance.cfg");
                if (!File.Exists(cfg)) return null;
                string name = File.ReadAllLines(cfg).Where(l => l.StartsWith("name=")).Select(l => IniUnescape(l.Substring(5))).FirstOrDefault();
                return new[] { instance.Name, string.IsNullOrEmpty(name) ? instance.Name : name };
            }
            catch (Exception)
            {
                return null;
            }
        }

        static Dictionary<string, object> Play()
        {
            string prism = FindPrism();
            if (prism == null) throw new UserError("The launcher isn't installed, so the game can't be started from here.");
            string[] instance = PrismInstanceFor(modsDir);
            if (instance == null) throw new UserError("This mods folder isn't part of an instance, so there's nothing to launch. Pick an instance next to the Play button.");
            TryApplySharingDefaults(); // an instance made in the launcher since starts out sharing too
            TrySyncGameSettings(); // shared game settings changed in another instance come along
            var start = new ProcessStartInfo(prism, "--launch \"" + instance[0] + "\"");
            start.UseShellExecute = false;
            start.WorkingDirectory = Path.GetDirectoryName(prism);
            Process.Start(start);
            Log("Launched  " + instance[1]);
            return Obj("ok", true, "instanceName", instance[1]);
        }

        // Mod id and display name of the jar's first mod (see JarMeta.cs; cached by size and timestamp
        // so renaming a jar to .disabled doesn't reopen it).
        static string[] GetJarInfo(FileInfo file)
        {
            ModInfo first = MainMod(GetJarMeta(file));
            return first == null ? new string[2] : new[] { first.Id, first.Name };
        }

        // ---------- File changes ----------

        // Accept only a bare .jar / .jar.disabled name that exists directly in the mods folder.
        static string ResolveModFile(string dir, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new UserError("No file name was sent.");
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.StartsWith(".") || name != Path.GetFileName(name))
                throw new UserError("That isn't a plain file name.");
            if (!JarName.IsMatch(name)) throw new UserError("Only .jar and .jar.disabled files can be changed.");
            string path = Path.Combine(dir, name);
            if (!File.Exists(path)) throw new UserError(name + " isn't in the mods folder any more. Refresh the page.");
            return path;
        }

        static void AssertNotInUse(string path)
        {
            try
            {
                using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            }
            catch (UnauthorizedAccessException)
            {
                throw new UserError("Windows won't let this app change " + Path.GetFileName(path) + ". Check whether the file is read-only.");
            }
            catch (IOException)
            {
                throw new UserError(Path.GetFileName(path) + " is open in another program. Close Minecraft or the launcher and try again.");
            }
        }

        static string SetEnabled(string name, bool enable)
        {
            lock (FileLock)
            {
                string dir = modsDir;
                string path = ResolveModFile(dir, name);
                bool isDisabled = name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                if (enable && !isDisabled) throw new UserError(name + " is already enabled.");
                if (!enable && isDisabled) throw new UserError(name + " is already disabled.");
                string newName = enable ? name.Substring(0, name.Length - ".disabled".Length) : name + ".disabled";
                string newPath = Path.Combine(dir, newName);
                if (File.Exists(newPath)) throw new UserError(newName + " already exists in the mods folder.");
                AssertNotInUse(path);
                File.Move(path, newPath);
                Log((enable ? "Enabled   " : "Disabled  ") + name + " -> " + newName);
                return newName;
            }
        }

        static void MoveToRecycleBin(string name)
        {
            lock (FileLock)
            {
                string dir = modsDir;
                string path = ResolveModFile(dir, name);
                AssertNotInUse(path);
                if (!RecyclePaths(new[] { path }))
                    throw new UserError("Windows couldn't move " + name + " to the Recycle Bin. Nothing was deleted.");
                Log("Deleted   " + name + " (moved to the Recycle Bin)");
            }
        }

        // Moves files or folders to the Recycle Bin in one operation. No confirmation or error popups,
        // but Windows warns rather than ever deleting something for good (for example when it's too big
        // for the Recycle Bin). Returns false when anything is still there afterwards.
        static bool RecyclePaths(IList<string> paths)
        {
            if (paths.Count == 0) return true;
            var op = new SHFILEOPSTRUCT();
            op.wFunc = FO_DELETE;
            op.pFrom = string.Join("\0", paths) + "\0\0";
            op.fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_WANTNUKEWARNING;
            op.hwnd = windowHandle; // owner for Windows' own "too big for the Recycle Bin" warning
            int result = SHFileOperation(ref op);
            return result == 0 && !op.fAnyOperationsAborted && !paths.Any(p => File.Exists(p) || Directory.Exists(p));
        }

        // ---------- Modrinth ----------
        // Public API, no key needed (https://docs.modrinth.com). Jars are identified by SHA-1.
        // All Modrinth work runs under ModrinthLock, which also guards these caches.

        const string ModrinthApi = "https://api.modrinth.com/v2";
        const string ModrinthUserAgent = "MinecraftModManager/0.5 (Windows mod manager)";
        static readonly string[] Loaders = { "neoforge", "forge", "fabric", "quilt" };
        static readonly object ModrinthLock = new object();
        static readonly Dictionary<string, string> Sha1ByFile = new Dictionary<string, string>();
        static readonly Dictionary<string, Dictionary<string, object>> VersionByHash = new Dictionary<string, Dictionary<string, object>>();
        static readonly Dictionary<string, Dictionary<string, object>> ProjectById = new Dictionary<string, Dictionary<string, object>>();
        static readonly Dictionary<string, Dictionary<string, object>> LatestByHash = new Dictionary<string, Dictionary<string, object>>();
        static string latestTarget = "";
        static DateTime latestCheckedAt = DateTime.MinValue;

        static Dictionary<string, object> D(object value) { return value as Dictionary<string, object>; }
        static object[] A(object value) { return value as object[] ?? new object[0]; }
        static object Get(Dictionary<string, object> d, string key) { object v; return d != null && d.TryGetValue(key, out v) ? v : null; }
        static string S(Dictionary<string, object> d, string key)
        {
            object v = Get(d, key);
            return v == null ? null : Convert.ToString(v, CultureInfo.InvariantCulture);
        }
        static string JsonArrayParam(params string[] values) { return Uri.EscapeDataString(new JavaScriptSerializer().Serialize(values)); }
        static string LoaderName()
        {
            switch (loader) { case "neoforge": return "NeoForge"; case "forge": return "Forge"; case "fabric": return "Fabric"; default: return "Quilt"; }
        }

        static object ModrinthGet(string pathAndQuery) { return HttpJson("GET", ModrinthApi + pathAndQuery, null); }
        static object ModrinthPost(string path, object body) { return HttpJson("POST", ModrinthApi + path, body); }

        // Returns parsed JSON, or null for 404.
        static object HttpJson(string method, string url, object body)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = method;
            request.UserAgent = ModrinthUserAgent;
            request.Accept = "application/json";
            request.Timeout = 30000;
            request.ReadWriteTimeout = 30000;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            try
            {
                if (body != null)
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(body));
                    request.ContentType = "application/json";
                    request.ContentLength = bytes.Length;
                    using (Stream output = request.GetRequestStream()) output.Write(bytes, 0, bytes.Length);
                }
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    var serializer = new JavaScriptSerializer();
                    serializer.MaxJsonLength = int.MaxValue;
                    return serializer.DeserializeObject(reader.ReadToEnd());
                }
            }
            catch (WebException ex)
            {
                var response = ex.Response as HttpWebResponse;
                if (response != null && response.StatusCode == HttpStatusCode.NotFound) return null;
                if (response != null && (int)response.StatusCode == 429)
                    throw new UserError("Modrinth is limiting requests right now. Wait a minute and try again.");
                throw new UserError("Couldn't reach Modrinth (" + ex.Message + "). Check your internet connection and try again.");
            }
        }

        static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(); }

        static string FileSha1(FileInfo file)
        {
            string key = Regex.Replace(file.Name, @"\.disabled$", "", RegexOptions.IgnoreCase) + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks;
            string hash;
            lock (Sha1ByFile)
                if (Sha1ByFile.TryGetValue(key, out hash)) return hash;
            using (SHA1 sha = SHA1.Create())
            using (var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                hash = Hex(sha.ComputeHash(stream));
            lock (Sha1ByFile) Sha1ByFile[key] = hash;
            return hash;
        }

        // Hashes every jar in the folder and looks up hashes Modrinth hasn't been asked about yet.
        // Returns jar file name -> SHA-1.
        static Dictionary<string, string> IdentifyFolder(string dir)
        {
            if (!Directory.Exists(dir)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return IdentifyFiles(new DirectoryInfo(dir).GetFiles().Where(f => JarName.IsMatch(f.Name)));
        }

        // The same for any files (pack zips too). Returns file name -> SHA-1.
        static Dictionary<string, string> IdentifyFiles(IEnumerable<FileInfo> fileList)
        {
            List<FileInfo> files = fileList.ToList();
            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (FileInfo file in files)
            {
                try { hashes[file.Name] = FileSha1(file); }
                catch (IOException) { }
            }
            List<string> unknown = hashes.Values.Distinct().Where(h => !VersionByHash.ContainsKey(h)).ToList();
            for (int i = 0; i < unknown.Count; i += 500)
            {
                List<string> chunk = unknown.Skip(i).Take(500).ToList();
                Dictionary<string, object> found = D(ModrinthPost("/version_files", Obj("hashes", chunk, "algorithm", "sha1"))) ?? new Dictionary<string, object>();
                foreach (string h in chunk) VersionByHash[h] = D(Get(found, h));
            }
            FetchProjects(hashes.Values.Select(h => VersionByHash[h]).Where(v => v != null)
                .Select(v => S(v, "project_id")).Where(id => id != null && !ProjectById.ContainsKey(id)).Distinct().ToList());
            // Jars Modrinth doesn't know byte for byte: CurseForge's fingerprint comes first; what neither
            // knows is matched to a Modrinth project by its mod id and name (Loose.cs).
            List<FileInfo> rest = files.Where(f => JarName.IsMatch(f.Name) && hashes.ContainsKey(f.Name) && VersionByHash[hashes[f.Name]] == null).ToList();
            if (rest.Count > 0)
            {
                HashSet<string> onCurseForge = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (CfReady)
                {
                    try { onCurseForge.UnionWith(CfIdentify(rest).Keys); }
                    catch (UserError) { }
                }
                foreach (FileInfo f in rest.Where(f => !onCurseForge.Contains(f.Name))) LooseModrinth(f, hashes[f.Name]);
            }
            return hashes;
        }

        static void FetchProjects(List<string> ids)
        {
            for (int i = 0; i < ids.Count; i += 100)
            {
                foreach (object item in A(ModrinthGet("/projects?ids=" + JsonArrayParam(ids.Skip(i).Take(100).ToArray()))))
                {
                    Dictionary<string, object> project = D(item);
                    if (project != null) ProjectById[S(project, "id")] = project;
                }
            }
        }

        static Dictionary<string, object> GetProject(string idOrSlug)
        {
            foreach (Dictionary<string, object> known in ProjectById.Values)
                if (S(known, "id") == idOrSlug || S(known, "slug") == idOrSlug) return known;
            Dictionary<string, object> project = D(ModrinthGet("/project/" + Uri.EscapeDataString(idOrSlug)));
            if (project != null) ProjectById[S(project, "id")] = project;
            return project;
        }

        static HashSet<string> InstalledProjects(string dir)
        {
            var installed = new HashSet<string>();
            foreach (string h in IdentifyFolder(dir).Values)
            {
                Dictionary<string, object> version = VersionByHash[h];
                if (version != null) installed.Add(S(version, "project_id"));
            }
            return installed;
        }

        // Identifies every jar on Modrinth and checks each for a newer version for the chosen
        // Minecraft version + loader (re-checked every 30 minutes, or now when forced).
        static Dictionary<string, object> ModrinthScan(bool force)
        {
            lock (ModrinthLock)
            {
                Dictionary<string, string> hashes = IdentifyFolder(modsDir);
                List<string> identified = hashes.Values.Distinct().Where(h => VersionByHash[h] != null).ToList();
                // A changed release channel (Settings or per mod) asks again, like a new target does.
                string target = gameVersion + "|" + loader + "|" + PrefChoice("updateChannel", "auto") + "|" + extrasVersion;
                if (target != latestTarget)
                {
                    LatestByHash.Clear();
                    latestTarget = target;
                    latestCheckedAt = DateTime.MinValue;
                }
                bool stale = force || DateTime.UtcNow - latestCheckedAt > TimeSpan.FromMinutes(30);
                List<string> toCheck = stale ? identified : identified.Where(h => !LatestByHash.ContainsKey(h)).ToList();
                CheckLatest(modsDir, toCheck);
                if (stale) latestCheckedAt = DateTime.UtcNow;

                var mods = new Dictionary<string, object>();
                int updates = 0;
                foreach (KeyValuePair<string, string> pair in hashes)
                {
                    Dictionary<string, object> version = VersionByHash[pair.Value];
                    if (version == null) continue;
                    Dictionary<string, object> project;
                    ProjectById.TryGetValue(S(version, "project_id") ?? "", out project);
                    Dictionary<string, object> latest = UpdateFor(pair.Value);
                    string held = latest == null ? null : UpdateHeld(modsDir, S(version, "project_id"), S(latest, "id"));
                    Dictionary<string, object> heldUpdate = null;
                    if (held != null) { heldUpdate = Obj("versionId", S(latest, "id"), "versionNumber", S(latest, "version_number"), "type", S(latest, "version_type"), "why", held); latest = null; }
                    if (latest != null) updates++;
                    mods[pair.Key] = Obj(
                        "loose", Bool(version, "loose"),
                        "held", heldUpdate,
                        "versionType", S(version, "version_type"),
                        "projectId", S(version, "project_id"),
                        "slug", S(project, "slug"),
                        "title", S(project, "title"),
                        "icon", S(project, "icon_url"),
                        "clientSide", S(project, "client_side"),
                        "serverSide", S(project, "server_side"),
                        "versionNumber", S(version, "version_number"),
                        "update", latest == null ? null : Obj("versionId", S(latest, "id"), "versionNumber", S(latest, "version_number"), "type", S(latest, "version_type")));
                }
                int onModrinth = mods.Count, onCurseForge = 0;
                string cfError = null;
                if (CfReady)
                {
                    // Jars Modrinth doesn't know: ask CurseForge (CurseForge.cs).
                    List<FileInfo> rest = hashes.Where(p => VersionByHash[p.Value] == null).Select(p => new FileInfo(Path.Combine(modsDir, p.Key))).ToList();
                    try { if (rest.Count > 0) onCurseForge = CfScanInto(mods, rest, ref updates); }
                    catch (UserError ex) { cfError = ex.Message; }
                }
                return Obj("ok", true, "target", TargetObj(), "checkedAt", latestCheckedAt == DateTime.MinValue ? null : latestCheckedAt.ToString("o"),
                    "total", hashes.Count, "identified", onModrinth, "identifiedCurseForge", onCurseForge, "curseforgeError", cfError, "updates", updates, "mods", mods);
            }
        }

        // Asks Modrinth for the newest version of each hash, grouped by the release types each mod may move to.
        static void CheckLatest(string dir, List<string> hashes)
        {
            // Loosely matched jars have no hash Modrinth knows; their newest version comes from the version list.
            foreach (string h in hashes.Where(h => Bool(VersionByHash[h], "loose")).ToList()) LatestByHash[h] = LooseLatest(h);
            hashes = hashes.Where(h => !Bool(VersionByHash[h], "loose")).ToList();
            foreach (var group in hashes.GroupBy(h => string.Join(",", AllowedTypes(dir, S(VersionByHash[h], "project_id"), S(VersionByHash[h], "version_type")))))
            {
                List<string> list = group.ToList();
                for (int i = 0; i < list.Count; i += 500)
                {
                    List<string> chunk = list.Skip(i).Take(500).ToList();
                    Dictionary<string, object> found = D(ModrinthPost("/version_files/update",
                        Obj("hashes", chunk, "algorithm", "sha1", "loaders", new[] { loader }, "game_versions", new[] { gameVersion },
                            "version_types", group.Key.Split(',')))) ?? new Dictionary<string, object>();
                    foreach (string h in chunk) LatestByHash[h] = D(Get(found, h));
                }
            }
        }

        // The newer compatible version for a jar's hash, or null when it's current. Never offers an older one.
        static Dictionary<string, object> UpdateFor(string hash)
        {
            Dictionary<string, object> current, latest;
            if (!VersionByHash.TryGetValue(hash, out current) || current == null) return null;
            if (!LatestByHash.TryGetValue(hash, out latest) || latest == null) return null;
            if (S(latest, "id") == S(current, "id")) return null;
            return string.CompareOrdinal(S(latest, "date_published") ?? "", S(current, "date_published") ?? "") > 0 ? latest : null;
        }

        // Loaders show up among Modrinth's display categories; they aren't topics.
        static readonly HashSet<string> LoaderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "fabric", "forge", "neoforge", "quilt", "liteloader", "modloader", "rift", "babric", "bta-babric", "java-agent", "legacy-fabric", "ornithe", "nilloader", "risugami", "datapack", "minecraft" };

        // category: a Modrinth category slug (or empty). side: "client" (client-only mods), "server"
        // (mods that do something on a server), or empty.
        static Dictionary<string, object> ModrinthSearch(string query, int offset, string sort, string category = null, string side = null)
        {
            string index = sort == "downloads" || sort == "updated" || sort == "newest" || sort == "follows" ? sort : "relevance";
            var facetList = new List<object> { new[] { "project_type:mod" }, new[] { "categories:" + loader }, new[] { "versions:" + gameVersion } };
            if (!string.IsNullOrEmpty(category) && Regex.IsMatch(category, "^[a-z-]{2,30}$")) facetList.Add(new[] { "categories:" + category });
            if (side == "client") facetList.Add(new[] { "server_side:unsupported" });
            else if (side == "server") facetList.Add(new[] { "server_side:required", "server_side:optional" });
            string facets = new JavaScriptSerializer().Serialize(facetList.ToArray());
            Dictionary<string, object> result = D(ModrinthGet("/search?limit=20&offset=" + Math.Max(0, offset) + "&index=" + index +
                "&query=" + Uri.EscapeDataString(query ?? "") + "&facets=" + Uri.EscapeDataString(facets)));
            if (result == null) throw new UserError("Modrinth's search didn't answer. Try again.");
            HashSet<string> installed;
            lock (ModrinthLock) installed = InstalledProjects(modsDir);
            var hits = new List<object>();
            foreach (object item in A(Get(result, "hits")))
            {
                Dictionary<string, object> hit = D(item);
                hits.Add(Obj("projectId", S(hit, "project_id"), "slug", S(hit, "slug"), "title", S(hit, "title"), "author", S(hit, "author"),
                    "description", S(hit, "description"), "icon", S(hit, "icon_url"), "downloads", Get(hit, "downloads"),
                    "clientSide", S(hit, "client_side"), "serverSide", S(hit, "server_side"), "updated", S(hit, "date_modified"),
                    "categories", A(Get(hit, "display_categories")).OfType<string>().Where(c => !LoaderNames.Contains(c)).ToList(),
                    "installed", installed.Contains(S(hit, "project_id") ?? "")));
            }
            return Obj("ok", true, "target", TargetObj(), "total", Get(result, "total_hits"), "offset", offset, "hits", hits);
        }

        sealed class PlanItem
        {
            public Dictionary<string, object> Version;
            public string Title;
            public string RequiredBy;
        }

        // Installs a mod and the required dependencies that aren't in the folder yet.
        static Dictionary<string, object> ModrinthInstall(string projectIdOrSlug)
        {
            if (string.IsNullOrWhiteSpace(projectIdOrSlug)) throw new UserError("No mod was chosen.");
            lock (ModrinthLock)
            {
                string dir = modsDir;
                HashSet<string> installed = InstalledProjects(dir);
                var plan = new List<PlanItem>();
                var notes = new List<string>();
                Resolve(projectIdOrSlug, null, null, installed, new HashSet<string>(installed), plan, notes);
                var added = InstallPlan(plan, dir, notes);
                return Obj("ok", true, "added", added, "notes", notes);
            }
        }

        static void Resolve(string projectIdOrSlug, string versionId, string requiredBy, HashSet<string> installed,
            HashSet<string> visited, List<PlanItem> plan, List<string> notes)
        {
            Dictionary<string, object> project = GetProject(projectIdOrSlug);
            if (project == null)
            {
                if (requiredBy == null) throw new UserError("That mod wasn't found on Modrinth.");
                notes.Add("A mod that " + requiredBy + " needs wasn't found on Modrinth.");
                return;
            }
            string id = S(project, "id");
            string title = S(project, "title");
            if (installed.Contains(id))
            {
                if (requiredBy == null) throw new UserError(title + " is already in this mods folder.");
                return;
            }
            if (!visited.Add(id)) return;

            Dictionary<string, object> version = null;
            if (versionId != null)
            {
                version = D(ModrinthGet("/version/" + Uri.EscapeDataString(versionId)));
                if (!Fits(version)) version = null;
            }
            if (version == null) version = BestVersion(id);
            if (version == null)
            {
                string message = "Modrinth has no version of " + title + " for Minecraft " + gameVersion + " with " + LoaderName() + ".";
                if (requiredBy == null) throw new UserError(message);
                notes.Add(message + " " + requiredBy + " needs it.");
                return;
            }
            plan.Add(new PlanItem { Version = version, Title = title, RequiredBy = requiredBy });
            AddDependencies(version, title, installed, visited, plan, notes);
        }

        static void AddDependencies(Dictionary<string, object> version, string title, HashSet<string> installed,
            HashSet<string> visited, List<PlanItem> plan, List<string> notes)
        {
            foreach (object item in A(Get(version, "dependencies")))
            {
                Dictionary<string, object> dependency = D(item);
                if (S(dependency, "dependency_type") != "required") continue;
                string depProject = S(dependency, "project_id");
                string depVersion = S(dependency, "version_id");
                if (depProject == null && depVersion != null) depProject = S(D(ModrinthGet("/version/" + Uri.EscapeDataString(depVersion))), "project_id");
                if (depProject != null) Resolve(depProject, depVersion, title, installed, visited, plan, notes);
            }
        }

        static bool Fits(Dictionary<string, object> version)
        {
            return version != null
                && A(Get(version, "loaders")).Any(l => (l as string) == loader)
                && A(Get(version, "game_versions")).Any(g => (g as string) == gameVersion);
        }

        // Newest release for the chosen Minecraft version + loader, or the newest beta/alpha if there's no release.
        static Dictionary<string, object> BestVersion(string projectId)
        {
            List<Dictionary<string, object>> versions = A(ModrinthGet("/project/" + Uri.EscapeDataString(projectId) + "/version?loaders=" +
                JsonArrayParam(loader) + "&game_versions=" + JsonArrayParam(gameVersion)))
                .Select(D).Where(v => v != null).OrderByDescending(v => S(v, "date_published")).ToList();
            return versions.FirstOrDefault(v => S(v, "version_type") == "release") ?? versions.FirstOrDefault();
        }

        static List<object> InstallPlan(List<PlanItem> plan, string dir, List<string> notes)
        {
            var added = new List<object>();
            foreach (PlanItem item in plan)
            {
                try
                {
                    string file = DownloadVersion(item.Version, dir, false, null, notes);
                    if (file == null) continue;
                    bool here = SamePath(dir, modsDir); // or the server's mods folder (a mod a pack needs)
                    Log("Installed " + file + " from Modrinth" + (item.RequiredBy != null ? " (needed by " + item.RequiredBy + ")" : "") + (here ? "" : " into " + dir));
                    if (here) TagFromProject(file, S(item.Version, "project_id"), notes);
                    added.Add(Obj("file", file, "title", item.Title, "projectId", S(item.Version, "project_id"), "dependency", item.RequiredBy != null));
                }
                catch (UserError ex)
                {
                    notes.Add(ex.Message);
                }
            }
            return added;
        }

        // Downloads a version's main .jar into the folder and checks it against Modrinth's SHA-1.
        // replacing: for updates, the old jar, which goes to the Recycle Bin only once the new one checks out.
        // Returns the new file name, or null when it was skipped (already there, or the same mod as another jar).
        static string DownloadVersion(Dictionary<string, object> version, string dir, bool disabled, string replacing, List<string> notes)
        {
            Dictionary<string, object> file = null;
            foreach (object item in A(Get(version, "files")))
            {
                Dictionary<string, object> f = D(item);
                if (f == null) continue;
                if (file == null) file = f;
                if (Get(f, "primary") is bool && (bool)Get(f, "primary")) { file = f; break; }
            }
            if (file == null) throw new UserError("Modrinth listed no file for " + S(version, "name") + ".");
            string name = Path.GetFileName(S(file, "filename") ?? "");
            if (!name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new UserError("Modrinth's file " + name + " isn't a .jar, so it wasn't installed.");
            Uri url;
            if (!Uri.TryCreate(S(file, "url"), UriKind.Absolute, out url) || url.Scheme != Uri.UriSchemeHttps
                || !(url.Host == "cdn.modrinth.com" || url.Host.EndsWith(".modrinth.com", StringComparison.OrdinalIgnoreCase)))
                throw new UserError("Modrinth gave an unexpected download address for " + name + ", so it wasn't installed.");

            return DownloadJar(url, name, S(D(Get(file, "hashes")), "sha1"), "Modrinth", dir, disabled, replacing, notes);
        }

        // Downloads one jar (from Modrinth or CurseForge) next to the others, checks its SHA-1, skips it when
        // the same mod is already there, and only then sends the jar it replaces to the Recycle Bin.
        static string DownloadJar(Uri url, string name, string expected, string site, string dir, bool disabled, string replacing, List<string> notes)
        {
            string finalName = name + (disabled ? ".disabled" : "");
            bool sameName = replacing != null && (string.Equals(replacing, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(replacing, name + ".disabled", StringComparison.OrdinalIgnoreCase));
            if (!sameName && (File.Exists(Path.Combine(dir, name)) || File.Exists(Path.Combine(dir, name + ".disabled"))))
            {
                notes.Add(name + " is already in the mods folder.");
                return null;
            }

            string temp = Path.Combine(dir, name + ".download");
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
                string actual;
                using (SHA1 sha = SHA1.Create())
                using (FileStream stream = File.OpenRead(temp))
                    actual = Hex(sha.ComputeHash(stream));
                if (expected != null && !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                    throw new UserError("The download of " + name + " didn't match " + site + "'s checksum, so it was thrown away.");

                if (replacing == null)
                {
                    // The same mod may already be here under another name (for example a CurseForge copy).
                    string modId = GetJarInfo(new FileInfo(temp))[0];
                    if (modId != null)
                    {
                        FileInfo twin = new DirectoryInfo(dir).GetFiles().Where(f => JarName.IsMatch(f.Name))
                            .FirstOrDefault(f => GetJarInfo(f)[0] == modId);
                        if (twin != null)
                        {
                            notes.Add(name + " was skipped because " + twin.Name + " is the same mod.");
                            return null;
                        }
                    }
                }
                else
                {
                    MoveToRecycleBin(replacing);
                }
                File.Move(temp, Path.Combine(dir, finalName));
                return finalName;
            }
            catch (WebException ex)
            {
                throw new UserError("Couldn't download " + name + " (" + ex.Message + ").");
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }

        // New installs get client/server tags from Modrinth's own side flags.
        static void TagFromProject(string file, string projectId, List<string> notes)
        {
            Dictionary<string, object> project;
            if (projectId == null || !ProjectById.TryGetValue(projectId, out project)) return;
            string side = ModrinthSide(S(project, "client_side"), S(project, "server_side")); // same rule as Detect (Sides.cs)
            if (side == null) return;
            var sides = side == "both" ? new List<string> { "client", "server" } : new List<string> { side };
            try { UpdateTags(new List<string> { file }, sides, new List<string>()); }
            catch (UserError ex) { notes.Add("Couldn't tag " + file + ": " + ex.Message); }
        }

        // Updates jars to the newest compatible Modrinth version, keeping them enabled or disabled
        // as they were, moving their tags to the new file name, and adding any new required dependencies.
        static Dictionary<string, object> ModrinthUpdate(List<string> files)
        {
            if (files.Count == 0) throw new UserError("No mods were chosen.");
            lock (ModrinthLock)
            {
                string dir = modsDir;
                if (files.Count >= 2) AutoSnapshot(dir, "Before updating " + files.Count + " mods");
                Dictionary<string, string> hashes = IdentifyFolder(dir);
                HashSet<string> installed = InstalledProjects(dir);
                var visited = new HashSet<string>(installed);
                var results = new List<object>();
                var notes = new List<string>();
                var plan = new List<PlanItem>();
                foreach (string name in files)
                {
                    try
                    {
                        string path = ResolveModFile(dir, name);
                        string hash;
                        if (!hashes.TryGetValue(name, out hash) || VersionByHash[hash] == null)
                        {
                            // Not on Modrinth: a CurseForge jar is updated from CurseForge (CurseForge.cs).
                            Dictionary<string, object> cf = CfMatchFor(dir, name);
                            if (cf == null) throw new UserError(name + " isn't on Modrinth or CurseForge, so it can't be updated here.");
                            results.Add(Obj("file", name, "newFile", CfUpdateJar(dir, name, cf, notes)));
                            continue;
                        }
                        if (!LatestByHash.ContainsKey(hash)) CheckLatest(dir, new List<string> { hash });
                        Dictionary<string, object> current = VersionByHash[hash];
                        Dictionary<string, object> latest = UpdateFor(hash);
                        if (latest == null) throw new UserError(name + " is already up to date.");
                        if (UpdateHeld(dir, S(current, "project_id"), S(latest, "id")) == "pinned") throw new UserError(name + " is pinned to this version. Unpin it to update.");
                        string newFile = ReplaceWithVersion(dir, name, current, latest, "Updated   ", notes);
                        results.Add(Obj("file", name, "newFile", newFile));
                        Dictionary<string, object> project;
                        ProjectById.TryGetValue(S(latest, "project_id") ?? "", out project);
                        AddDependencies(latest, S(project, "title") ?? name, installed, visited, plan, notes);
                    }
                    catch (UserError ex)
                    {
                        results.Add(Obj("file", name, "error", ex.Message));
                    }
                }
                List<object> added = InstallPlan(plan, dir, notes);
                return Obj("ok", true, "results", results, "added", added, "notes", notes);
            }
        }

        // Swaps a jar for another Modrinth version of the same mod (newer or older), keeping it enabled or
        // disabled as it was and moving its tags to the new file name. The old jar goes to the Recycle Bin.
        static string ReplaceWithVersion(string dir, string name, Dictionary<string, object> current, Dictionary<string, object> target, string verb, List<string> notes)
        {
            AssertNotInUse(ResolveModFile(dir, name));
            bool disabled = name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
            string newFile = DownloadVersion(target, dir, disabled, name, notes);
            if (newFile == null) throw new UserError(name + " couldn't be replaced.");
            string oldKey = disabled ? name.Substring(0, name.Length - ".disabled".Length) : name;
            string newKey = disabled ? newFile.Substring(0, newFile.Length - ".disabled".Length) : newFile;
            try { RenameTagKey(oldKey, newKey); }
            catch (UserError ex) { notes.Add("Couldn't move the tags of " + oldKey + ": " + ex.Message); }
            Log(verb + oldKey + " -> " + newKey + " (Modrinth " + S(current, "version_number") + " -> " + S(target, "version_number") + ")");
            return newFile;
        }

        // Drops mod-tags.json entries for jars that aren't in the folder any more (deleted or replaced by
        // hand). An entry whose jar is there, on or off, is never touched. Returns the entries removed,
        // with their tags, so the page can offer Undo.
        static Dictionary<string, object> ForgetTagEntries(List<string> keys)
        {
            if (keys.Count == 0) throw new UserError("No mods were chosen.");
            RequireOwnTags(modsDir);
            lock (FileLock)
            {
                string dir = modsDir, path = Path.Combine(dir, "mod-tags.json");
                if (!File.Exists(path)) throw new UserError("This folder has no mod-tags.json.");
                TagsDoc doc = LoadTagsDoc(path);
                var removed = new List<object>();
                foreach (string k in keys)
                {
                    string key = doc.Mods.Keys.FirstOrDefault(m => string.Equals(m, k, StringComparison.OrdinalIgnoreCase));
                    if (key == null) continue;
                    string jar = NormalizeKey(key);
                    if (File.Exists(Path.Combine(dir, jar)) || File.Exists(Path.Combine(dir, jar + ".disabled"))) continue;
                    removed.Add(Obj("key", key, "tags", doc.Mods[key]));
                    doc.Mods.Remove(key);
                }
                if (removed.Count > 0)
                {
                    SaveTagsDoc(path, doc);
                    Log("Tags      forgot " + removed.Count + (removed.Count == 1 ? " entry" : " entries") + " for jars not in the folder");
                }
                return Obj("ok", true, "removed", removed);
            }
        }

        static void RenameTagKey(string oldKey, string newKey)
        {
            if (string.Equals(oldKey, newKey, StringComparison.OrdinalIgnoreCase)) return;
            lock (FileLock)
            {
                string path = Path.Combine(modsDir, "mod-tags.json");
                if (!File.Exists(path)) return;
                TagsDoc doc = LoadTagsDoc(path);
                string existing = doc.Mods.Keys.FirstOrDefault(k => string.Equals(k, oldKey, StringComparison.OrdinalIgnoreCase));
                if (existing == null) return;
                List<string> tags = doc.Mods[existing];
                doc.Mods.Remove(existing);
                doc.Mods[newKey] = tags;
                SaveTagsDoc(path, doc);
            }
        }

        // ---------- Tags (mod-tags.json) ----------

        const string DefaultDescription = "Tags for the mods in this folder. Keys under \"mods\" are exact jar filenames. A mod with both tags must be installed on both sides.";
        static readonly Regex TagName = new Regex("^[a-z0-9][a-z0-9_-]{0,31}$");

        sealed class TagsDoc
        {
            public string Description = DefaultDescription;
            public readonly List<KeyValuePair<string, string>> Tags = new List<KeyValuePair<string, string>>();
            public readonly Dictionary<string, List<string>> Mods = new Dictionary<string, List<string>>();
            public readonly List<KeyValuePair<string, object>> Extra = new List<KeyValuePair<string, object>>();
        }

        // Adds and removes tags on the given jars (keys are .jar names; ".disabled" is ignored).
        // Rewrites mod-tags.json in its usual one-mod-per-line layout, keeping the previous file as mod-tags.json.bak.
        static int UpdateTags(List<string> keys, List<string> add, List<string> remove)
        {
            if (keys.Count == 0) throw new UserError("No mods were selected.");
            if (add.Count == 0 && remove.Count == 0) throw new UserError("No tag changes were sent.");
            RequireOwnTags(modsDir);
            foreach (string tag in add.Concat(remove))
            {
                if (!TagName.IsMatch(tag))
                    throw new UserError("\"" + tag + "\" isn't a valid tag name. Use lowercase letters, numbers, - or _ (up to 32 characters).");
            }
            List<string> jarKeys = keys.Select(NormalizeKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            lock (FileLock)
            {
                string path = Path.Combine(modsDir, "mod-tags.json");
                TagsDoc doc = LoadTagsDoc(path);
                var order = doc.Tags.Select(t => t.Key).ToList();
                foreach (string tag in add)
                {
                    if (!order.Contains(tag))
                    {
                        doc.Tags.Add(new KeyValuePair<string, string>(tag, ""));
                        order.Add(tag);
                    }
                }

                int changed = 0;
                foreach (string jar in jarKeys)
                {
                    string key = doc.Mods.Keys.FirstOrDefault(k => string.Equals(k, jar, StringComparison.OrdinalIgnoreCase)) ?? jar;
                    List<string> current;
                    bool isNew = !doc.Mods.TryGetValue(key, out current);
                    var tags = isNew ? new List<string>() : new List<string>(current);
                    tags.RemoveAll(t => remove.Contains(t));
                    foreach (string tag in add) if (!tags.Contains(tag)) tags.Add(tag);
                    tags = tags.Select((t, i) => new { t, i })
                        .OrderBy(x => order.IndexOf(x.t) < 0 ? int.MaxValue : order.IndexOf(x.t))
                        .ThenBy(x => x.i)
                        .Select(x => x.t)
                        .ToList();
                    if (isNew || !tags.SequenceEqual(current))
                    {
                        doc.Mods[key] = tags;
                        changed++;
                    }
                }

                if (changed > 0)
                {
                    SaveTagsDoc(path, doc);
                    string what = string.Join(" ", add.Select(t => "+" + t).Concat(remove.Select(t => "-" + t)));
                    string names = string.Join(", ", jarKeys.Take(4)) + (jarKeys.Count > 4 ? ", and " + (jarKeys.Count - 4) + " more" : "");
                    Log("Tags      " + what + " on " + changed + (changed == 1 ? " mod: " : " mods: ") + names);
                }
                return changed;
            }
        }

        // ---- Managing the tags themselves (the "tags" list in mod-tags.json) ----
        // client and server are the side tags the app's side filter and C/S boxes rely on:
        // their description can change, but they can't be renamed or deleted.

        static readonly string[] SideTags = { "client", "server" };

        static string TagsPath() { return Path.Combine(modsDir, "mod-tags.json"); }

        static string CleanDescription(string description)
        {
            string clean = Regex.Replace(description ?? "", @"\s+", " ").Trim();
            return clean.Length > 300 ? clean.Substring(0, 300) : clean;
        }

        static void CheckTagName(string name)
        {
            if (!TagName.IsMatch(name ?? ""))
                throw new UserError("\"" + name + "\" isn't a valid tag name. Use lowercase letters, numbers, - or _ (up to 32 characters).");
        }

        static Dictionary<string, object> CreateTag(string name, string description)
        {
            RequireOwnTags(modsDir);
            CheckTagName(name);
            lock (FileLock)
            {
                string path = TagsPath();
                TagsDoc doc = LoadTagsDoc(path);
                if (doc.Tags.Any(t => t.Key == name) || doc.Mods.Values.Any(tags => tags.Contains(name)))
                    throw new UserError("There's already a tag called " + name + ".");
                doc.Tags.Add(new KeyValuePair<string, string>(name, CleanDescription(description)));
                SaveTagsDoc(path, doc);
                Log("Tag       created " + name);
                return Obj("ok", true, "name", name);
            }
        }

        // Renames a tag (on every mod that has it) and/or changes its description.
        static Dictionary<string, object> EditTag(string name, string newName, string description)
        {
            if (string.IsNullOrEmpty(name)) throw new UserError("No tag was chosen.");
            RequireOwnTags(modsDir);
            newName = string.IsNullOrWhiteSpace(newName) ? name : newName.Trim();
            lock (FileLock)
            {
                string path = TagsPath();
                TagsDoc doc = LoadTagsDoc(path);
                int index = doc.Tags.FindIndex(t => t.Key == name);
                if (index < 0 && !doc.Mods.Values.Any(tags => tags.Contains(name))) throw new UserError("There's no tag called " + name + " any more. Refresh and try again.");
                bool renaming = newName != name;
                if (renaming)
                {
                    if (SideTags.Contains(name)) throw new UserError(name + " is a side tag the app relies on, so it can't be renamed.");
                    CheckTagName(newName);
                    if (doc.Tags.Any(t => t.Key == newName) || doc.Mods.Values.Any(tags => tags.Contains(newName)))
                        throw new UserError("There's already a tag called " + newName + ".");
                }
                string newDescription = description == null ? (index >= 0 ? doc.Tags[index].Value : "") : CleanDescription(description);
                var entry = new KeyValuePair<string, string>(newName, newDescription);
                if (index >= 0) doc.Tags[index] = entry; else doc.Tags.Add(entry);
                int mods = 0;
                if (renaming)
                {
                    foreach (List<string> tags in doc.Mods.Values)
                    {
                        int at = tags.IndexOf(name);
                        if (at < 0) continue;
                        tags[at] = newName;
                        mods++;
                    }
                }
                SaveTagsDoc(path, doc);
                Log(renaming ? "Tag       renamed " + name + " -> " + newName + " (on " + mods + " mods)" : "Tag       changed the description of " + name);
                return Obj("ok", true, "name", newName, "mods", mods);
            }
        }

        // Deletes a tag from the tag list and from every mod. The mods themselves aren't touched.
        static Dictionary<string, object> DeleteTag(string name)
        {
            if (string.IsNullOrEmpty(name)) throw new UserError("No tag was chosen.");
            RequireOwnTags(modsDir);
            if (SideTags.Contains(name)) throw new UserError(name + " is a side tag the app relies on, so it can't be deleted.");
            lock (FileLock)
            {
                string path = TagsPath();
                TagsDoc doc = LoadTagsDoc(path);
                int removed = doc.Tags.RemoveAll(t => t.Key == name);
                int mods = 0;
                foreach (List<string> tags in doc.Mods.Values)
                    if (tags.RemoveAll(t => t == name) > 0) mods++;
                if (removed == 0 && mods == 0) throw new UserError("There's no tag called " + name + " any more. Refresh and try again.");
                SaveTagsDoc(path, doc);
                Log("Tag       deleted " + name + " (removed from " + mods + " mods)");
                return Obj("ok", true, "mods", mods);
            }
        }

        static string NormalizeKey(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.StartsWith("."))
                throw new UserError("\"" + name + "\" isn't a plain jar file name.");
            if (name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - ".disabled".Length);
            if (!name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) throw new UserError("Tags can only be set on .jar files.");
            return name;
        }

        static TagsDoc LoadTagsDoc(string path)
        {
            var doc = new TagsDoc();
            if (!File.Exists(path)) return doc;
            Dictionary<string, object> root;
            try
            {
                var serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = int.MaxValue;
                root = serializer.DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
            }
            catch (Exception)
            {
                root = null;
            }
            if (root == null) throw new UserError("mod-tags.json isn't valid JSON, so tags can't be changed. Fix the file (or remove it) and try again.");

            foreach (KeyValuePair<string, object> entry in root)
            {
                if (entry.Key == "description" && entry.Value is string)
                {
                    doc.Description = (string)entry.Value;
                }
                else if (entry.Key == "tags" && entry.Value is Dictionary<string, object>)
                {
                    foreach (KeyValuePair<string, object> tag in (Dictionary<string, object>)entry.Value)
                        doc.Tags.Add(new KeyValuePair<string, string>(tag.Key, tag.Value as string ?? ""));
                }
                else if (entry.Key == "mods" && entry.Value is Dictionary<string, object>)
                {
                    foreach (KeyValuePair<string, object> mod in (Dictionary<string, object>)entry.Value)
                    {
                        var tags = new List<string>();
                        var items = mod.Value as System.Collections.IEnumerable;
                        if (items != null && !(mod.Value is string))
                            foreach (object item in items) { var s = item as string; if (s != null) tags.Add(s); }
                        doc.Mods[mod.Key] = tags;
                    }
                }
                else if (entry.Key == "description" || entry.Key == "tags" || entry.Key == "mods")
                {
                    throw new UserError("mod-tags.json has an unexpected \"" + entry.Key + "\" value, so tags can't be changed. Fix the file and try again.");
                }
                else
                {
                    doc.Extra.Add(entry);
                }
            }
            return doc;
        }

        static void SaveTagsDoc(string path, TagsDoc doc)
        {
            // Never in a folder whose tags belong to another tool (LinkedTags.cs): it would merge the file into its own.
            if (HasLinkedTags(Path.GetDirectoryName(path))) return;
            string text = TagsDocText(doc);
            string temp = path + ".tmp";
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            if (!File.Exists(path))
            {
                File.Move(temp, path);
                return;
            }
            try
            {
                File.Replace(temp, path, path + ".bak");
            }
            catch (IOException)
            {
                // Some setups (antivirus, synced folders) refuse File.Replace; fall back to copy-then-overwrite.
                File.Copy(path, path + ".bak", true);
                File.Copy(temp, path, true);
                File.Delete(temp);
            }
        }

        static string TagsDocText(TagsDoc doc)
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"description\": ").Append(JsonString(doc.Description)).Append(",\n");
            if (doc.Tags.Count == 0) sb.Append("  \"tags\": {},\n");
            else
            {
                sb.Append("  \"tags\": {\n");
                sb.Append(string.Join(",\n", doc.Tags.Select(t => "    " + JsonString(t.Key) + ": " + JsonString(t.Value))));
                sb.Append("\n  },\n");
            }
            // Same order PowerShell's Sort-Object gives (culture-aware, case-insensitive), matching the original file.
            List<string> keys = doc.Mods.Keys.OrderBy(k => k, StringComparer.CurrentCultureIgnoreCase).ToList();
            if (keys.Count == 0) sb.Append("  \"mods\": {}");
            else
            {
                sb.Append("  \"mods\": {\n");
                sb.Append(string.Join(",\n", keys.Select(k => "    " + JsonString(k) + ": [" + string.Join(", ", doc.Mods[k].Select(JsonString)) + "]")));
                sb.Append("\n  }");
            }
            foreach (KeyValuePair<string, object> extra in doc.Extra)
                sb.Append(",\n  ").Append(JsonString(extra.Key)).Append(": ").Append(new JavaScriptSerializer().Serialize(extra.Value));
            sb.Append("\n}\n");
            return sb.ToString();
        }

        static string JsonString(string value)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in value ?? "")
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        static void Log(string message)
        {
            string folder = Path.GetFileName((modsDir ?? "").TrimEnd('\\', '/'));
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + "  (" + folder + ")";
            try
            {
                lock (LogLock) File.AppendAllText(LogPath, line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch { }
        }

        // ---------- Settings, dialogs, shell ----------

        // Settings file: the mods folder plus the Minecraft version and loader that Modrinth lookups target.
        static string LoadSavedFolder()
        {
            string saved = null;
            try
            {
                if (File.Exists(SettingsPath))
                {
                    // DeserializeObject, not Deserialize<>: only it gives lists as object[], which A() reads.
                    var settings = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(SettingsPath)) as Dictionary<string, object>;
                    saved = Str(settings, "modsFolder");
                    string version = Str(settings, "gameVersion");
                    string savedLoader = Str(settings, "loader");
                    if (!string.IsNullOrEmpty(version)) gameVersion = version;
                    if (Array.IndexOf(Loaders, savedLoader) >= 0) loader = savedLoader;
                    string server = Str(settings, "serverFolder");
                    if (!string.IsNullOrEmpty(server)) serverDir = server;
                    foreach (object folder in A(Get(settings, "recentFolders")))
                        if (folder is string) recentFolders.Add((string)folder);
                    foreach (object folder in A(Get(settings, "servers")))
                        if (folder is string) knownServers.Add((string)folder);
                    savedWindow = D(Get(settings, "window"));
                    curseforgeKey = Str(settings, "curseforgeKey"); // CurseForge.cs; kept only in this local file
                    Dictionary<string, object> savedPrefs = D(Get(settings, "prefs"));
                    if (savedPrefs != null) lock (prefs) foreach (KeyValuePair<string, object> p in savedPrefs) prefs[p.Key] = p.Value;
                }
            }
            catch { }
            return string.IsNullOrEmpty(saved) ? null : saved;
        }

        // Test runs with --mods never write settings, so they can't change the user's real ones.
        static void SaveSettings()
        {
            if (!rememberFolder) return;
            try
            {
                File.WriteAllText(SettingsPath, new JavaScriptSerializer().Serialize(Obj("modsFolder", modsDir, "gameVersion", gameVersion, "loader", loader,
                    "serverFolder", serverDir, "window", savedWindow, "recentFolders", RecentFoldersCopy(), "servers", KnownServersCopy(),
                    "curseforgeKey", string.IsNullOrEmpty(curseforgeKey) ? null : curseforgeKey, "prefs", PrefsCopy())), new UTF8Encoding(false));
            }
            catch { }
        }

        static List<string> RecentFoldersCopy()
        {
            lock (recentFolders) return recentFolders.ToList();
        }

        static Dictionary<string, object> TargetObj()
        {
            return Obj("gameVersion", gameVersion, "loader", loader);
        }

        // Runs on the UI thread. A hidden topmost owner keeps the dialog above the browser.
        static string PickFolder(string startIn)
        {
            return PickFolder(startIn, "Choose the mods folder for Minecraft Mod Manager");
        }

        static string PickFolder(string startIn, string description)
        {
            using (var owner = new Form())
            using (var dialog = new FolderBrowserDialog())
            {
                owner.ShowInTaskbar = false;
                owner.TopMost = true;
                owner.FormBorderStyle = FormBorderStyle.None;
                owner.StartPosition = FormStartPosition.CenterScreen;
                owner.Size = new Size(1, 1);
                owner.Opacity = 0;
                dialog.Description = description;
                dialog.ShowNewFolderButton = false;
                if (startIn != null && Directory.Exists(startIn)) dialog.SelectedPath = startIn;
                owner.Show();
                owner.Activate();
                return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.SelectedPath : null;
            }
        }

        static void OpenBrowser()
        {
            try { Process.Start(origin + "/"); } catch { }
        }

        static void OpenModsFolder()
        {
            try { Process.Start("explorer.exe", "\"" + modsDir + "\""); } catch { }
        }

        // The folder the mods folder is in: for an instance, its game folder (configs, saves, packs).
        static void OpenGameFolder()
        {
            string dir = GameDir();
            if (!Directory.Exists(dir)) throw new UserError(dir + " isn't there any more.");
            try { Process.Start("explorer.exe", "\"" + dir + "\""); } catch { }
        }

        // Runs on the UI thread. Closing the main window ends Application.Run in Main.
        static void Quit()
        {
            try { listener.Stop(); listener.Close(); } catch { }
            if (window != null && !window.IsDisposed) window.ForceClose();
            else Application.ExitThread();
        }

        // Shell-opens an http(s) address in the person's own browser.
        static void OpenExternal(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url ?? "", UriKind.Absolute, out uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                throw new UserError("That isn't a web address.");
            try { Process.Start(uri.AbsoluteUri); } catch { }
        }

        static string Desktop { get { return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); } }

        static string FormatSize(long bytes)
        {
            if (bytes >= 1L << 30) return (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
            if (bytes >= 1L << 20) return (bytes / (double)(1L << 20)).ToString("0", CultureInfo.InvariantCulture) + " MB";
            return Math.Max(1, bytes / 1024) + " KB";
        }

        // The page is embedded in the exe. A mod-manager.html next to the exe overrides it,
        // so the page can be edited without rebuilding.
        static string LoadPage()
        {
            string overridePath = Path.Combine(AppDir, "mod-manager.html");
            if (File.Exists(overridePath)) return File.ReadAllText(overridePath, Encoding.UTF8);
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(PageResource))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
                return reader.ReadToEnd();
        }

        static Icon LoadAppIcon()
        {
            try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { return SystemIcons.Application; }
        }

        // ---------- JSON helpers ----------

        static Dictionary<string, object> Obj(params object[] pairs)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i + 1 < pairs.Length; i += 2) d[(string)pairs[i]] = pairs[i + 1];
            return d;
        }

        static string Str(Dictionary<string, object> d, string key)
        {
            object value;
            return d != null && d.TryGetValue(key, out value) ? value as string : null;
        }

        // A JSON string or array of strings, as a list.
        static List<string> StrList(Dictionary<string, object> d, string key)
        {
            var list = new List<string>();
            object value;
            if (d == null || !d.TryGetValue(key, out value) || value == null) return list;
            var single = value as string;
            if (single != null) { list.Add(single); return list; }
            var items = value as System.Collections.IEnumerable;
            if (items != null)
                foreach (object item in items) { var s = item as string; if (s != null) list.Add(s); }
            return list;
        }

        static bool Bool(Dictionary<string, object> d, string key)
        {
            object value;
            return d != null && d.TryGetValue(key, out value) && value is bool && (bool)value;
        }

        static Dictionary<string, object> ReadBody(HttpListenerRequest req)
        {
            string text;
            using (var reader = new StreamReader(req.InputStream, Encoding.UTF8)) text = reader.ReadToEnd();
            if (string.IsNullOrWhiteSpace(text)) return new Dictionary<string, object>();
            try
            {
                return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(text) ?? new Dictionary<string, object>();
            }
            catch (Exception)
            {
                throw new UserError("The request wasn't valid JSON.");
            }
        }

        static void SendText(HttpListenerContext ctx, int status, string type, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            HttpListenerResponse res = ctx.Response;
            res.StatusCode = status;
            res.ContentType = type;
            res.AddHeader("Cache-Control", "no-store");
            res.AddHeader("X-Content-Type-Options", "nosniff");
            res.AddHeader("Referrer-Policy", "no-referrer");
            res.ContentLength64 = bytes.Length;
            using (Stream output = res.OutputStream) output.Write(bytes, 0, bytes.Length);
        }

        static void SendModIcon(HttpListenerContext ctx, string name)
        {
            string path;
            try { path = ResolveModFile(modsDir, name ?? ""); }
            catch (UserError) { SendText(ctx, 404, "text/plain; charset=utf-8", "Not found"); return; }
            string type;
            byte[] bytes = JarLogo(new FileInfo(path), out type);
            if (bytes == null) { SendText(ctx, 404, "text/plain; charset=utf-8", "No logo"); return; }
            HttpListenerResponse res = ctx.Response;
            res.StatusCode = 200;
            res.ContentType = type;
            res.AddHeader("Cache-Control", "private, max-age=86400"); // a new version of a jar has a new name, so a new address
            res.AddHeader("X-Content-Type-Options", "nosniff");
            res.ContentLength64 = bytes.Length;
            using (Stream output = res.OutputStream) output.Write(bytes, 0, bytes.Length);
        }

        static void SendJson(HttpListenerContext ctx, int status, object value)
        {
            var serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            SendText(ctx, status, "application/json; charset=utf-8", serializer.Serialize(value));
        }

        static void SendError(HttpListenerContext ctx, int status, string message)
        {
            SendJson(ctx, status, Obj("error", message));
        }

        static void TrySendError(HttpListenerContext ctx, int status, string message)
        {
            try { SendError(ctx, status, message); } catch { }
        }

        // ---------- Win32 ----------

        const uint FO_DELETE = 3;
        const ushort FOF_SILENT = 0x0004;
        const ushort FOF_NOCONFIRMATION = 0x0010;
        const ushort FOF_ALLOWUNDO = 0x0040;
        const ushort FOF_NOERRORUI = 0x0400;
        const ushort FOF_WANTNUKEWARNING = 0x4000;

        // 64-bit layout (build with /platform:x64).
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
            [MarshalAs(UnmanagedType.LPWStr)] public string pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();
    }
}
