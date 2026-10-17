// The game, followed from the app, with the launcher kept out of sight. Prism still signs in and starts the
// game, but it's started minimised (its "preparing" window never pops up) and the instance's console is
// set not to open, even when the game crashes. The app then watches for the game's Java process: Play
// shows Starting / Running, the game's log streams into the app, Stop ends it, and when it exits with a
// crash the crash reader opens. If Prism stops without starting the game, the reason from its own log is
// shown; if it's still busy after a while (a sign-in prompt, a big download), its window is brought back.
// A game started from Prism itself is picked up too. Settings > Play can show Prism as before.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace MinecraftModManager
{
    static partial class Program
    {
        sealed class GameSession
        {
            public string State = "idle";       // idle | starting | running | stopped | crashed | failed
            public string Instance, GameDir, InstanceDir;
            public DateTime Started, Ended;
            public int Pid, ExitCode;
            public bool External, StopAsked, PrismShown;
            public Process Prism, Game;
            public List<string> PrismErrors = new List<string>();
        }

        static readonly object GameLock = new object();
        static GameSession game = new GameSession();
        static Thread gameWatcher;

        static bool HidePrism() { return PrefFlag("hidePrism", true); }

        // The instance's own console settings: closed while the game runs and when it fails (the app shows both).
        static void QuietInstanceConsole(InstanceInfo info)
        {
            if (!HidePrism() || PrismRunning()) return; // an open launcher keeps its own copy and would undo it
            try
            {
                string cfgPath = Path.Combine(InstanceDir(info), "instance.cfg");
                if (!File.Exists(cfgPath)) return;
                List<string> cfg = File.ReadAllLines(cfgPath).ToList();
                string before = string.Join("\n", cfg);
                IniSet(cfg, "OverrideConsole", "true");
                IniSet(cfg, "ShowConsole", "false");
                IniSet(cfg, "ShowConsoleOnError", "false");
                IniSet(cfg, "AutoCloseConsole", "true");
                if (string.Join("\n", cfg) == before) return;
                File.Copy(cfgPath, cfgPath + ".bak", true);
                File.WriteAllLines(cfgPath, cfg, new UTF8Encoding(false));
                Log("Launcher  " + info.Name + ": console set to stay closed (the app shows the game's log)");
            }
            catch (Exception ex) { Log("Launcher  couldn't change " + info.Name + "'s console settings: " + ex.Message); }
        }

        // Starts Prism on the instance: minimised when it's to stay out of sight.
        static Process StartPrismLaunch(string prism, string instanceId)
        {
            var start = new ProcessStartInfo(prism, "--launch \"" + instanceId + "\"") { WorkingDirectory = Path.GetDirectoryName(prism) };
            if (HidePrism() && !PrismRunning())
            {
                // Through the shell, the window style reaches Prism's first window (its "preparing" dialog).
                start.UseShellExecute = true;
                start.WindowStyle = ProcessWindowStyle.Minimized;
            }
            else start.UseShellExecute = false;
            return Process.Start(start);
        }

        static void BeginGameSession(string instanceName, string gameDir, string instanceDir, Process prismProcess)
        {
            lock (GameLock)
            {
                game = new GameSession { State = "starting", Instance = instanceName, GameDir = gameDir, InstanceDir = instanceDir,
                    Started = DateTime.Now, Prism = prismProcess };
            }
            EnsureGameWatcher();
        }

        static void EnsureGameWatcher()
        {
            lock (GameLock)
            {
                if (gameWatcher != null && gameWatcher.IsAlive) return;
                gameWatcher = new Thread(WatchGame) { IsBackground = true, Name = "game watcher" };
                gameWatcher.Start();
            }
        }

        // The game's Java process: java(w).exe whose command line mentions the instance's folder.
        static Process FindGameProcess(string instanceDir)
        {
            string want = Path.GetFullPath(instanceDir).TrimEnd('\\').ToLowerInvariant();
            try
            {
                using (var search = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'javaw.exe' OR Name = 'java.exe'"))
                    foreach (ManagementObject p in search.Get())
                        using (p)
                        {
                            string cmd = ((p["CommandLine"] as string) ?? "").Replace('/', '\\').ToLowerInvariant();
                            if (!cmd.Contains(want + "\\")) continue;
                            try
                            {
                                Process found = Process.GetProcessById(Convert.ToInt32(p["ProcessId"], CultureInfo.InvariantCulture));
                                IntPtr keep = found.Handle; // held from now on, so the exit code can still be read after it ends
                                return found;
                            }
                            catch (Exception) { }
                        }
            }
            catch (Exception) { }
            return null;
        }

        static void WatchGame()
        {
            while (true)
            {
                Thread.Sleep(1000);
                GameSession s;
                lock (GameLock) s = game;
                try
                {
                    if (s.State == "starting")
                    {
                        Process g = FindGameProcess(s.InstanceDir);
                        if (g != null)
                        {
                            lock (GameLock) { s.Game = g; s.Pid = g.Id; s.State = "running"; }
                            Log("Game      " + s.Instance + " is running");
                            NotifyGame();
                            continue;
                        }
                        // An already open launcher takes the launch over and the new one quits: "gone" means no Prism at all.
                        bool prismGone = (s.Prism == null || HasExitedSafe(s.Prism)) && !PrismRunning();
                        if (prismGone && (DateTime.Now - s.Started).TotalSeconds > 5)
                        {
                            List<string> errors = PrismLaunchErrors(s.Started);
                            lock (GameLock) { s.State = "failed"; s.Ended = DateTime.Now; s.PrismErrors = errors; }
                            Log("Game      " + s.Instance + " didn't start" + (errors.Count > 0 ? ": " + errors.Last() : ""));
                            NotifyGame();
                            return;
                        }
                        // Still busy after a while: it may be asking something (signing in) or downloading a lot.
                        if (!s.PrismShown && (DateTime.Now - s.Started).TotalSeconds > 25 && !prismGone)
                        {
                            s.PrismShown = true;
                            foreach (Process p in Process.GetProcessesByName("prismlauncher")) ShowPrismWindows(p.Id);
                        }
                        continue;
                    }
                    if (s.State == "running")
                    {
                        if (s.Game != null && !HasExitedSafe(s.Game)) continue;
                        int code = 0;
                        try { code = s.Game == null ? 0 : s.Game.ExitCode; } catch (Exception) { }
                        bool crashReport = false;
                        try
                        {
                            string reports = Path.Combine(s.GameDir, "crash-reports");
                            crashReport = Directory.Exists(reports) && new DirectoryInfo(reports).GetFiles("crash-*.txt").Any(f => f.LastWriteTime >= s.Started);
                        }
                        catch (Exception) { }
                        bool crashed = !s.StopAsked && (code != 0 || crashReport);
                        lock (GameLock) { s.State = crashed ? "crashed" : "stopped"; s.ExitCode = code; s.Ended = DateTime.Now; }
                        Log("Game      " + s.Instance + (crashed ? " crashed" + (code != 0 ? " (exit code " + code + ")" : "") : s.StopAsked ? " was stopped from the app" : " closed"));
                        NotifyGame();
                        return;
                    }
                    return; // idle or finished: nothing to follow
                }
                catch (Exception ex) { Log("Game      watcher: " + ex.Message); }
            }
        }

        static bool HasExitedSafe(Process p)
        {
            try { return p.HasExited; } catch (Exception) { return true; }
        }

        static void NotifyGame()
        {
            if (window == null || window.IsDisposed) return;
            try { window.BeginInvoke((Action)(() => { if (!window.IsDisposed) window.Notify("game"); })); } catch (InvalidOperationException) { }
        }

        // What went wrong, from Prism's log of this run: its warnings and errors, newest last.
        static List<string> PrismLaunchErrors(DateTime since)
        {
            var found = new List<string>();
            try
            {
                var log = new FileInfo(Path.Combine(PrismDataDir(), "logs", "PrismLauncher-0.log"));
                if (!log.Exists || log.LastWriteTime < since.AddSeconds(-5)) return found;
                string text;
                using (var stream = new FileStream(log.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream)) text = reader.ReadToEnd();
                var interesting = new Regex(@"\b(Critical|Fatal)\b|(fail|error|could ?n.t|cannot|unable|invalid account|no account|not own|expired)", RegexOptions.IgnoreCase);
                var noise = new Regex(@"Invalid type SINGLEFILE|setGeometry|metacache|Dir changed|qtlogging|launcher\.task\.net", RegexOptions.IgnoreCase);
                foreach (string raw in text.Split('\n'))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || !interesting.IsMatch(line) || noise.IsMatch(line)) continue;
                    line = Regex.Replace(line, @"^\s*[\d.]+\s+", "");
                    line = Regex.Replace(line, @"\s*\(unknown:0\)$", "");
                    if (!found.Contains(line)) found.Add(line);
                }
            }
            catch (Exception) { }
            return found.Skip(Math.Max(0, found.Count - 8)).ToList();
        }

        // ---------- Prism's windows ----------

        delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);

        // Restores Prism's minimised windows and brings them forward. Returns how many there were.
        static int ShowPrismWindows(int pid)
        {
            var found = new List<IntPtr>();
            EnumWindows((hwnd, l) =>
            {
                uint owner;
                GetWindowThreadProcessId(hwnd, out owner);
                if (owner == pid && IsWindowVisible(hwnd)) found.Add(hwnd);
                return true;
            }, IntPtr.Zero);
            foreach (IntPtr hwnd in found)
            {
                if (IsIconic(hwnd)) ShowWindow(hwnd, 9); // SW_RESTORE
                SetForegroundWindow(hwnd);
            }
            return found.Count;
        }

        // ---------- What the page asks for ----------

        static Dictionary<string, object> GameStatus()
        {
            GameSession s;
            lock (GameLock) s = game;
            // A game started from Prism itself (or before the app opened) is followed too.
            if (s.State != "starting" && s.State != "running" && Directory.Exists(modsDir) && GameRunning(GameDir()))
            {
                string[] inst = PrismInstanceFor(modsDir);
                if (inst != null)
                {
                    Process g = FindGameProcess(Path.GetDirectoryName(GameDir()));
                    if (g != null)
                    {
                        lock (GameLock)
                        {
                            game = s = new GameSession { State = "running", External = true, Instance = inst[1], GameDir = GameDir(),
                                InstanceDir = Path.GetDirectoryName(GameDir()), Started = DateTime.Now, Game = g, Pid = g.Id };
                        }
                        EnsureGameWatcher();
                    }
                }
            }
            return Obj("ok", true, "state", s.State, "instance", s.Instance, "pid", s.Pid, "external", s.External,
                "started", s.State == "idle" ? null : s.Started.ToUniversalTime().ToString("o"),
                "ended", s.Ended == default(DateTime) ? null : s.Ended.ToUniversalTime().ToString("o"),
                "exitCode", s.ExitCode, "errors", s.PrismErrors, "prismShown", s.PrismShown, "hidePrism", HidePrism());
        }

        // New text from the game's latest.log, from a byte offset (the end of what the page already has).
        static Dictionary<string, object> GameLogFrom(long from)
        {
            string path = Path.Combine(GameDir(), "logs", "latest.log");
            if (!File.Exists(path)) return Obj("ok", true, "text", "", "next", 0, "reset", from > 0);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long length = stream.Length;
                bool reset = from > length || from < 0; // a new log (the game restarted)
                if (reset) from = 0;
                const long cap = 512 * 1024;
                bool cut = length - from > cap;
                if (cut) from = length - cap;
                stream.Seek(from, SeekOrigin.Begin);
                var buffer = new byte[length - from];
                int read = 0, n;
                while (read < buffer.Length && (n = stream.Read(buffer, read, buffer.Length - read)) > 0) read += n;
                string text = Encoding.UTF8.GetString(buffer, 0, read);
                if (cut) { int nl = text.IndexOf('\n'); if (nl >= 0) text = text.Substring(nl + 1); }
                return Obj("ok", true, "text", text, "next", from + read, "reset", reset, "cut", cut);
            }
        }

        static Dictionary<string, object> StopGame()
        {
            GameSession s;
            lock (GameLock) s = game;
            if (s.State != "running" || s.Game == null) throw new UserError("The game isn't running.");
            s.StopAsked = true;
            try { s.Game.Kill(); } catch (Exception ex) { throw new UserError("Windows wouldn't stop the game: " + ex.Message); }
            Log("Game      " + s.Instance + ": stopped from the app");
            return Obj("ok", true);
        }

        static Dictionary<string, object> ShowPrism()
        {
            GameSession s;
            lock (GameLock) s = game;
            if (s.Prism != null && !HasExitedSafe(s.Prism) && ShowPrismWindows(s.Prism.Id) > 0) return Obj("ok", true);
            foreach (Process p in Process.GetProcessesByName("prismlauncher"))
                if (ShowPrismWindows(p.Id) > 0) return Obj("ok", true);
            OpenPrism();
            return Obj("ok", true);
        }
    }
}
