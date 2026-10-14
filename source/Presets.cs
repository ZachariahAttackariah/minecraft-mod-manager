// On/off presets: named sets of which mods are on ("full", "performance", "testing"), switched in one
// click. Kept per mods folder in the app's data folder (mod-presets.json). Each jar is remembered by its
// file name and its mod id, so a preset still finds a mod after it's been updated. Jars added after a
// preset was saved are left as they are when it's applied.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace MinecraftModManager
{
    static partial class Program
    {
        static readonly object PresetLock = new object();
        static string PresetsPath { get { return Path.Combine(dataDir, "mod-presets.json"); } }

        static Dictionary<string, object> PresetsAll()
        {
            try
            {
                if (File.Exists(PresetsPath))
                    return new JavaScriptSerializer().DeserializeObject(File.ReadAllText(PresetsPath)) as Dictionary<string, object> ?? new Dictionary<string, object>();
            }
            catch { }
            return new Dictionary<string, object>();
        }

        static void SavePresetsAll(Dictionary<string, object> all)
        {
            Directory.CreateDirectory(dataDir);
            string temp = PresetsPath + ".tmp";
            File.WriteAllText(temp, new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(all), new UTF8Encoding(false));
            if (File.Exists(PresetsPath)) File.Replace(temp, PresetsPath, null); else File.Move(temp, PresetsPath);
        }

        static Dictionary<string, object> ListPresets()
        {
            lock (PresetLock)
            {
                Dictionary<string, object> folder = D(Get(PresetsAll(), FolderKey(modsDir))) ?? new Dictionary<string, object>();
                var list = folder.Select(p =>
                {
                    Dictionary<string, object> preset = D(p.Value);
                    List<Dictionary<string, object>> jars = A(Get(preset, "jars")).Select(D).Where(j => j != null).ToList();
                    return (object)Obj("name", p.Key, "saved", S(preset, "saved"), "on", jars.Count(j => Bool(j, "on")), "off", jars.Count(j => !Bool(j, "on")));
                }).OrderBy(p => S(D(p), "name"), StringComparer.OrdinalIgnoreCase).ToList();
                return Obj("ok", true, "presets", list);
            }
        }

        static string MainId(FileInfo jar)
        {
            ModInfo main = MainMod(GetJarMeta(jar));
            return main == null ? null : main.Id;
        }

        static Dictionary<string, object> SavePreset(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0 || name.Length > 40) throw new UserError("Give the preset a name (up to 40 characters).");
            var jars = new DirectoryInfo(modsDir).GetFiles().Where(f => JarName.IsMatch(f.Name)).Select(f => (object)Obj(
                "key", Regex.Replace(f.Name, @"\.disabled$", "", RegexOptions.IgnoreCase), "id", MainId(f),
                "on", !f.Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))).ToList();
            lock (PresetLock)
            {
                Dictionary<string, object> all = PresetsAll();
                string fk = FolderKey(modsDir);
                Dictionary<string, object> folder = D(Get(all, fk));
                if (folder == null) all[fk] = folder = new Dictionary<string, object>();
                folder[name] = Obj("saved", DateTime.UtcNow.ToString("o"), "jars", jars);
                SavePresetsAll(all);
            }
            Log("Saved preset \"" + name + "\" (" + jars.Count + " jars)");
            return Obj("ok", true, "name", name, "count", jars.Count);
        }

        static Dictionary<string, object> DeletePreset(string name)
        {
            lock (PresetLock)
            {
                Dictionary<string, object> all = PresetsAll();
                Dictionary<string, object> folder = D(Get(all, FolderKey(modsDir)));
                if (folder == null || !folder.Remove(name ?? "")) throw new UserError("There's no preset called " + name + ".");
                SavePresetsAll(all);
            }
            return Obj("ok", true);
        }

        // Turns mods on and off to match the preset. A snapshot is taken first, so it can be undone.
        static Dictionary<string, object> ApplyPreset(string name, bool preview)
        {
            Dictionary<string, object> preset;
            lock (PresetLock) preset = D(Get(D(Get(PresetsAll(), FolderKey(modsDir))), name ?? ""));
            if (preset == null) throw new UserError("There's no preset called " + name + ".");
            List<Dictionary<string, object>> saved = A(Get(preset, "jars")).Select(D).Where(j => j != null).ToList();
            var byKey = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var byId = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (Dictionary<string, object> j in saved)
            {
                if (S(j, "key") != null) byKey[S(j, "key")] = Bool(j, "on");
                if (S(j, "id") != null && !byId.ContainsKey(S(j, "id"))) byId[S(j, "id")] = Bool(j, "on");
            }
            var turnOn = new List<string>();
            var turnOff = new List<string>();
            var untouched = new List<string>();
            foreach (FileInfo f in new DirectoryInfo(modsDir).GetFiles().Where(f => JarName.IsMatch(f.Name)))
            {
                bool off = f.Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                string key = off ? f.Name.Substring(0, f.Name.Length - ".disabled".Length) : f.Name;
                bool want;
                string id;
                if (!byKey.TryGetValue(key, out want))
                {
                    id = MainId(f);
                    if (id == null || !byId.TryGetValue(id, out want)) { untouched.Add(key); continue; }
                }
                if (want && off) turnOn.Add(f.Name);
                else if (!want && !off) turnOff.Add(f.Name);
            }
            if (preview) return Obj("ok", true, "turnOn", turnOn, "turnOff", turnOff, "untouched", untouched);
            if (turnOn.Count + turnOff.Count >= 2) AutoSnapshot(modsDir, "Before the preset " + name);
            var failed = new List<object>();
            int done = 0;
            foreach (string f in turnOn.Concat(turnOff))
            {
                try { SetEnabled(f, turnOn.Contains(f)); done++; }
                catch (UserError ex) { failed.Add(Obj("file", f, "error", ex.Message)); }
            }
            Log("Applied preset \"" + name + "\": " + turnOn.Count + " on, " + turnOff.Count + " off");
            return Obj("ok", true, "turnedOn", turnOn, "turnedOff", turnOff, "changed", done, "untouched", untouched.Count, "failed", failed);
        }
    }
}
