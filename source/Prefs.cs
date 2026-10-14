// The Settings page's preferences: kept under "prefs" in MinecraftModManager.settings.json and sent to the
// page with every state, so the look applies before anything draws. The page knows the defaults; only
// what the user changed is stored. Every key is checked against what it may hold.

using System;
using System.Collections.Generic;
using System.Linq;

namespace MinecraftModManager
{
    static partial class Program
    {
        static readonly Dictionary<string, object> prefs = new Dictionary<string, object>();

        static readonly Dictionary<string, string[]> PrefChoices = new Dictionary<string, string[]>
        {
            { "theme", new[] { "dark", "light", "system" } },
            { "accent", new[] { "emerald", "sky", "amethyst", "gold", "redstone" } },
            { "density", new[] { "comfortable", "compact" } },
            { "tagColors", new[] { "vivid", "soft" } },
            { "rowActions", new[] { "hover", "always" } },
            { "tagsPerRow", new[] { "3", "5", "all" } },
            { "tagOrder", new[] { "alpha", "file" } },
            { "tagHide", new[] { "ctrl", "cycle" } },
            { "updateChannel", new[] { "auto", "release", "beta", "alpha" } },
            { "searchSources", new[] { "both", "modrinth", "curseforge" } },
            { "serverRestart", new[] { "off", "6", "12", "24" } },
        };
        static readonly string[] PrefFlags = { "fileNames", "blockyTitle", "checkBeforePlay", "serverExtras", "confirmDelete", "whatChanged", "serverBackups", "worldBackups" };

        static Dictionary<string, object> PrefsCopy()
        {
            lock (prefs) return new Dictionary<string, object>(prefs);
        }

        static Dictionary<string, object> SetPrefs(Dictionary<string, object> body)
        {
            lock (prefs)
            {
                foreach (KeyValuePair<string, object> p in body)
                {
                    string[] choices;
                    if (PrefChoices.TryGetValue(p.Key, out choices))
                    {
                        string v = Convert.ToString(p.Value, System.Globalization.CultureInfo.InvariantCulture);
                        if (!choices.Contains(v)) throw new UserError("\"" + v + "\" isn't a choice for " + p.Key + ".");
                        prefs[p.Key] = v;
                    }
                    else if (PrefFlags.Contains(p.Key))
                    {
                        if (!(p.Value is bool)) throw new UserError(p.Key + " is on or off.");
                        prefs[p.Key] = p.Value;
                    }
                    else throw new UserError("There's no setting called " + p.Key + ".");
                }
            }
            SaveSettings();
            return Obj("ok", true, "prefs", PrefsCopy());
        }

        static bool PrefFlag(string key, bool fallback)
        {
            object v;
            lock (prefs) return prefs.TryGetValue(key, out v) && v is bool ? (bool)v : fallback;
        }

        static string PrefChoice(string key, string fallback)
        {
            object v;
            lock (prefs) return prefs.TryGetValue(key, out v) && v is string ? (string)v : fallback;
        }

        // A new key is tried against CurseForge before it's kept; an empty one switches CurseForge off.
        static Dictionary<string, object> SetCurseForgeKey(string key)
        {
            key = (key ?? "").Trim();
            if (key.Length == 0)
            {
                curseforgeKey = null;
                SaveSettings();
                return Obj("ok", true, "curseforge", false);
            }
            string old = curseforgeKey;
            curseforgeKey = key;
            try
            {
                if (CfHttp("GET", "/games/" + CfMinecraft, null) == null) throw new UserError("CurseForge didn't recognise that key.");
            }
            catch (UserError)
            {
                curseforgeKey = old;
                throw;
            }
            SaveSettings();
            return Obj("ok", true, "curseforge", true);
        }
    }
}
