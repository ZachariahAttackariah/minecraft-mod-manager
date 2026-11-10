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
            { "accent", new[] { "emerald", "sky", "amethyst", "gold", "redstone", "copper", "prismarine", "cherry", "custom" } },
            { "background", new[] { "glow", "plain", "blocks" } },
            { "corners", new[] { "round", "square" } },
            { "textSize", new[] { "90", "100", "110", "120", "150", "175", "200" } },
            { "darkShade", new[] { "charcoal", "deepslate", "midnight", "spruce", "black" } },
            { "lightShade", new[] { "snow", "birch", "stone" } },
            { "contrast", new[] { "normal", "high" } },
            { "font", new[] { "noto", "grotesk", "system", "verdana" } },
            { "panels", new[] { "solid", "glass" } },
            { "tagStyle", new[] { "tint", "solid", "outline" } },
            { "motion", new[] { "system", "on", "reduce" } },
            { "rowStyle", new[] { "lines", "zebra" } },
            { "startPage", new[] { "last", "mods", "libraries", "features", "packs", "shared", "configs", "keys", "server", "snapshots" } },
            { "bgDim", new[] { "40", "60", "75", "90" } },
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
        static readonly string[] PrefFlags = { "fileNames", "autoLibraries", "hidePrism", "tagLibraries", "tagSides", "serverConfigsAuto", "blockyTitle", "checkBeforePlay", "serverExtras", "confirmDelete", "whatChanged", "serverBackups", "worldBackups", "showIcons", "colSide", "colTags", "colVersion" };

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
                    else if (p.Key == "tagHues")
                    {
                        // Your own colour per tag: { tag: "#rrggbb" }; a tag left out goes back to its automatic colour.
                        var map = p.Value as Dictionary<string, object>;
                        if (map == null) throw new UserError("Tag colours have to be a list of tag: colour.");
                        var clean = new Dictionary<string, object>();
                        foreach (KeyValuePair<string, object> kv in map)
                        {
                            string c = kv.Value as string;
                            if (kv.Key.Length > 64 || c == null || !System.Text.RegularExpressions.Regex.IsMatch(c, "^#[0-9a-fA-F]{6}$")) continue;
                            clean[kv.Key] = c.ToLowerInvariant();
                        }
                        prefs[p.Key] = clean;
                    }
                    else if (p.Key == "tagSlots")
                    {
                        // Which automatic colour each tag got, the first time it was seen ({ tag: n }), so a tag
                        // keeps its colour from one start to the next, in every folder.
                        var map = p.Value as Dictionary<string, object>;
                        if (map == null) throw new UserError("Tag colour slots have to be a list of tag: number.");
                        var clean = new Dictionary<string, object>();
                        foreach (KeyValuePair<string, object> kv in map)
                        {
                            int n;
                            if (kv.Key.Length > 64 || kv.Value == null || !int.TryParse(Convert.ToString(kv.Value, System.Globalization.CultureInfo.InvariantCulture), out n) || n < 0 || n > 4096) continue;
                            clean[kv.Key] = n;
                        }
                        prefs[p.Key] = clean;
                    }
                    else if (p.Key == "changelogSeen")
                    {
                        // The newest changelog entry shown so far ("yyyy-mm-dd#lines"), for What's new after an update.
                        string v = p.Value as string;
                        if (v == null || !System.Text.RegularExpressions.Regex.IsMatch(v, @"^\d{4}-\d{2}-\d{2}#\d{1,4}$")) throw new UserError("changelogSeen has to look like 2026-01-31#4.");
                        prefs[p.Key] = v;
                    }
                    else if (p.Key == "accentColor")
                    {
                        string v = p.Value as string;
                        if (v == null || !System.Text.RegularExpressions.Regex.IsMatch(v, "^#[0-9a-fA-F]{6}$")) throw new UserError("The accent colour has to look like #2fb465.");
                        prefs[p.Key] = v.ToLowerInvariant();
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
