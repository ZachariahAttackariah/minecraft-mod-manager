// Scheduled server restarts and backups (Settings > Server, both off by default). While the server
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
                    DateTime due = scheduleRunningSince.Value.AddHours(hours);
                    double minutesLeft = (due - DateTime.UtcNow).TotalMinutes;
                    if (minutesLeft <= 5 && minutesLeft > 1 && warnedAt == 0) { Say("The server restarts in 5 minutes."); warnedAt = 5; }
                    else if (minutesLeft <= 1 && minutesLeft > 0 && warnedAt != 1) { Say("The server restarts in 1 minute."); warnedAt = 1; }
                    else if (minutesLeft <= 0) ScheduledRestart(backups);
                    return;
                }
                if (backups && DateTime.UtcNow - lastScheduledBackup > TimeSpan.FromHours(24)) LiveBackup();
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
            AddConsole("Scheduled restart: stopping the server.", "app");
            try { StopServer(); } catch (UserError) { return; }
            // Wait for it to finish saving and exit (up to 5 minutes).
            for (int i = 0; i < 300; i++)
            {
                lock (ServerLock) if (serverProcess == null) break;
                Thread.Sleep(1000);
            }
            lock (ServerLock) if (serverProcess != null) { AddConsole("Scheduled restart: the server didn't stop in time, so it was left as it is.", "app"); return; }
            if (backups) BackupServerWorld("scheduled restart");
            scheduleRunningSince = null;
            try { StartServer(); AddConsole("Scheduled restart: starting again.", "app"); }
            catch (UserError ex) { AddConsole("Scheduled restart: couldn't start again (" + ex.Message + ").", "app"); }
            NotifyPage("server");
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
