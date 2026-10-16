// Scheduled server restarts and backups (the Server page > Settings, both off by default). While the server
// runs (started from here), every 6, 12 or 24 hours it warns players in chat 5 minutes and 1 minute
// ahead, stops, backs up the world if backups are on, and starts again. With backups on and restarts
// off, the world is backed up once a day while saving is paused (save-off / save-all flush / save-on).

using System;
using System.Collections.Generic;
using System.Threading;

namespace MinecraftModManager
{
    static partial class Program
    {
        static Timer scheduleTimer;
        static DateTime? scheduleRunningSince;
        static DateTime lastScheduledBackup = DateTime.MinValue;
        static int warnedAt; // minutes-before already announced for the current cycle (5, 1), 0 = none
        static int scheduleBusy;
        static double restartMinutesOverride; // --restart-minutes, for testing the cycle without waiting hours

        static void StartServerSchedule()
        {
            scheduleTimer = new Timer(delegate { try { ScheduleTick(); } catch { } }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(20));
        }

        static void ScheduleTick()
        {
            if (Interlocked.CompareExchange(ref scheduleBusy, 1, 0) != 0) return;
            try
            {
                bool running;
                lock (ServerLock) running = serverProcess != null && serverState == "running";
                if (!running) { scheduleRunningSince = null; warnedAt = 0; return; }
                if (scheduleRunningSince == null) { scheduleRunningSince = DateTime.UtcNow; lastScheduledBackup = DateTime.UtcNow; warnedAt = 0; }

                string every = PrefChoice("serverRestart", "off");
                bool backups = PrefFlag("serverBackups", false);
                int hours;
                if (int.TryParse(every, out hours) && hours > 0)
                {
                    DateTime due = restartMinutesOverride > 0 ? scheduleRunningSince.Value.AddMinutes(restartMinutesOverride) : scheduleRunningSince.Value.AddHours(hours);
                    double minutesLeft = (due - DateTime.UtcNow).TotalMinutes;
                    if (minutesLeft <= 5 && minutesLeft > 1 && warnedAt == 0) { Say("The server restarts in " + (int)Math.Ceiling(minutesLeft) + " minutes."); warnedAt = 5; }
                    else if (minutesLeft <= 1 && minutesLeft > 0 && warnedAt != 1) { Say("The server restarts in 1 minute."); warnedAt = 1; }
                    else if (minutesLeft <= 0) ScheduledRestart(backups);
                    return;
                }
                if (backups && DateTime.UtcNow - lastScheduledBackup > (restartMinutesOverride > 0 ? TimeSpan.FromMinutes(restartMinutesOverride) : TimeSpan.FromHours(24))) LiveBackup();
            }
            finally { Interlocked.Exchange(ref scheduleBusy, 0); }
        }

        static void Say(string text)
        {
            try { ServerCommand("say " + text); } catch (UserError) { }
        }

        static void ScheduledRestart(bool backups)
        {
            Log("Server    scheduled restart");
            RestartNow("Scheduled restart", backups);
        }

        // Stops the server, waits for it to save and exit, optionally backs up its world, and starts it again.
        static void RestartNow(string what, bool backups)
        {
            AddConsole(what + ": stopping the server.", "app");
            try { StopServer(); } catch (UserError) { return; }
            // Wait for it to finish saving and exit (up to 5 minutes).
            for (int i = 0; i < 300; i++)
            {
                lock (ServerLock) if (serverProcess == null) break;
                Thread.Sleep(1000);
            }
            lock (ServerLock) if (serverProcess != null) { AddConsole(what + ": the server didn't stop in time, so it was left as it is.", "app"); return; }
            if (backups) BackupServerWorld(what.ToLowerInvariant());
            scheduleRunningSince = null;
            try { StartServer(); AddConsole(what + ": starting again.", "app"); }
            catch (UserError ex) { AddConsole(what + ": couldn't start again (" + ex.Message + ").", "app"); }
            NotifyPage("server");
        }

        // The Restart button: the same as a scheduled restart, minus the warnings, in the background.
        static Dictionary<string, object> RestartServer()
        {
            lock (ServerLock)
                if (serverProcess == null || serverState != "running") throw new UserError("The server isn't running.");
            Log("Server    restart requested");
            new Thread(() => { try { RestartNow("Restart", false); } catch { } }) { IsBackground = true }.Start();
            Thread.Sleep(300);
            return ServerStatus();
        }

        // When the next scheduled restart is due (null: none, or the server isn't running).
        static DateTime? NextScheduledRestart()
        {
            int hours;
            if (!int.TryParse(PrefChoice("serverRestart", "off"), out hours) || hours <= 0) return null;
            DateTime since;
            lock (ServerLock)
            {
                if (serverProcess == null || serverState != "running") return null;
                since = scheduleRunningSince ?? serverStartedAt.ToUniversalTime();
            }
            return restartMinutesOverride > 0 ? since.AddMinutes(restartMinutesOverride) : since.AddHours(hours);
        }

        // "Back up now" for the server's world while it runs: saving paused so the files are consistent.
        static Dictionary<string, object> BackupRunningServerWorld(string name, string reason)
        {
            try
            {
                ServerCommand("save-off");
                ServerCommand("save-all flush");
                Thread.Sleep(5000);
                return BackupWorld("server", name, reason);
            }
            finally
            {
                try { ServerCommand("save-on"); } catch (UserError) { }
            }
        }

        // Backs up the running server's world with saving paused, so the files are consistent.
        static void LiveBackup()
        {
            lastScheduledBackup = DateTime.UtcNow;
            try
            {
                ServerCommand("save-off");
                ServerCommand("save-all flush");
                Thread.Sleep(15000);
                BackupServerWorld("daily");
            }
            finally
            {
                try { ServerCommand("save-on"); } catch (UserError) { }
            }
        }

        static void BackupServerWorld(string reason)
        {
            try
            {
                foreach (WorldRef w in AllWorlds())
                    if (w.Side == "server")
                    {
                        Dictionary<string, object> res = BackupWorld("server", w.Name, reason);
                        AddConsole("Backed up the world (" + S(res, "file") + ").", "app");
                    }
            }
            catch (UserError ex) { AddConsole("World backup failed: " + ex.Message, "app"); }
        }
    }
}
