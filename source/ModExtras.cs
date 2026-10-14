// What the user adds to a mod beyond tags: a note, a pinned version, a skipped update and which
// release channel its updates come from. Kept per mods folder in the app's data folder
// (mod-extras.json), keyed by the mod's project (a Modrinth id, "cf:<id>" for CurseForge) so it
// survives updates, or "file:<jar>" for jars neither site knows.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace MinecraftModManager
{
    static partial class Program
    {
        static readonly object ExtrasLock = new object();
        static Dictionary<string, object> extrasCache;
        static int extrasVersion;   // bumps when a release channel changes, so cached update checks are redone

        static string ExtrasPath { get { return Path.Combine(dataDir, "mod-extras.json"); } }
        static string FolderKey(string dir) { return Path.GetFullPath(dir).TrimEnd('\\').ToLowerInvariant(); }

        static Dictionary<string, object> ExtrasAll()
        {
            if (extrasCache != null) return extrasCache;
            extrasCache = new Dictionary<string, object>();
            try
            {
                if (File.Exists(ExtrasPath))
                    extrasCache = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(ExtrasPath)) as Dictionary<string, object> ?? extrasCache;
            }
            catch { }
            return extrasCache;
        }

        // Every mod's extras in one folder, as sent to the page.
        static Dictionary<string, object> ExtrasFor(string dir)
        {
            lock (ExtrasLock)
            {
                Dictionary<string, object> folder = D(Get(ExtrasAll(), FolderKey(dir)));
                return folder == null ? new Dictionary<string, object>() : new Dictionary<string, object>(folder);
            }
        }

        static Dictionary<string, object> ExtraOf(string dir, string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            lock (ExtrasLock) return D(Get(D(Get(ExtrasAll(), FolderKey(dir))), key));
        }

        static readonly string[] Channels = { "release", "beta", "alpha" };

        static Dictionary<string, object> SetModExtras(Dictionary<string, object> body)
        {
            string key = Str(body, "key");
            if (string.IsNullOrWhiteSpace(key) || key.Length > 200) throw new UserError("No mod was chosen.");
            lock (ExtrasLock)
            {
                Dictionary<string, object> all = ExtrasAll();
                string fk = FolderKey(modsDir);
                Dictionary<string, object> folder = D(Get(all, fk));
                if (folder == null) all[fk] = folder = new Dictionary<string, object>();
                Dictionary<string, object> entry = D(Get(folder, key)) ?? new Dictionary<string, object>();
                if (body.ContainsKey("note"))
                {
                    string note = (Str(body, "note") ?? "").Trim();
                    if (note.Length > 1000) throw new UserError("Keep notes under 1,000 characters.");
                    if (note.Length == 0) entry.Remove("note"); else entry["note"] = note;
                }
                if (body.ContainsKey("pin"))
                {
                    if (Bool(body, "pin")) entry["pin"] = true; else entry.Remove("pin");
                }
                if (body.ContainsKey("skip"))
                {
                    string skip = Str(body, "skip");
                    if (string.IsNullOrEmpty(skip)) entry.Remove("skip"); else entry["skip"] = skip;
                }
                if (body.ContainsKey("channel"))
                {
                    string channel = Str(body, "channel");
                    if (string.IsNullOrEmpty(channel)) entry.Remove("channel");
                    else if (!Channels.Contains(channel)) throw new UserError("\"" + channel + "\" isn't a release channel.");
                    else entry["channel"] = channel;
                }
                if (entry.Count == 0) folder.Remove(key); else folder[key] = entry;
                if (folder.Count == 0) all.Remove(fk);
                if (body.ContainsKey("channel")) extrasVersion++;
                try
                {
                    Directory.CreateDirectory(dataDir);
                    string temp = ExtrasPath + ".tmp";
                    File.WriteAllText(temp, new JavaScriptSerializer().Serialize(all), new UTF8Encoding(false));
                    if (File.Exists(ExtrasPath)) File.Replace(temp, ExtrasPath, null); else File.Move(temp, ExtrasPath);
                }
                catch (Exception ex) { throw new UserError("Couldn't save that (" + ex.Message + ")."); }
                return Obj("ok", true, "key", key, "extras", entry.Count == 0 ? null : entry);
            }
        }

        // The release types an update may come from. "auto" (the default) follows what's installed: a
        // release only moves to releases, a beta also to betas, an alpha to anything.
        static string[] AllowedTypes(string dir, string key, string installedType)
        {
            string channel = Str(ExtraOf(dir, key), "channel");
            if (channel == null) channel = PrefChoice("updateChannel", "auto");
            if (channel == "auto") channel = installedType == "alpha" ? "alpha" : installedType == "beta" ? "beta" : "release";
            return channel == "alpha" ? Channels : channel == "beta" ? new[] { "release", "beta" } : new[] { "release" };
        }

        // Why an available update isn't offered: pinned, or this exact version skipped. Null when it's offered.
        static string UpdateHeld(string dir, string key, string versionId)
        {
            Dictionary<string, object> e = ExtraOf(dir, key);
            if (e == null) return null;
            if (Bool(e, "pin")) return "pinned";
            if (versionId != null && Str(e, "skip") == versionId) return "skipped";
            return null;
        }
    }
}
