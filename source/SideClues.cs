// What a jar's contents say about its side, for Detect when neither the jar's metadata nor Modrinth does:
//   - NeoForge / Forge entry points: @Mod(dist = Dist.CLIENT) means the mod only loads on the client
//     (dist = DEDICATED_SERVER: only on a server). Read straight from the class files' annotations.
//   - Mixin configs: patches listed only under "client" (none under "mixins" or "server") hint at a
//     client-only mod.
//   - Content: block states, item models or data-driven registries (recipes, loot tables, worldgen) mean
//     the mod adds things the server and the client must both know.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace MinecraftModManager
{
    static partial class Program
    {
        sealed class SideClues
        {
            public string Dist;        // "client", "server", or null when an entry point loads everywhere (or none was found)
            public int ModClasses;     // @Mod classes found
            public string MixinSide;   // "client" / "server" when every mixin is for that side only
            public bool AddsBlocksOrItems, HasServerData;
            public string ContentKind; // what its language file names: "blocks", "items", "entities"... (registered content)
            public bool Registers, Networking; // code that registers game content (DeferredRegister), or defines network packets
        }

        static readonly Dictionary<string, SideClues> SideCluesCache = new Dictionary<string, SideClues>();
        static readonly byte[] ModAnnotationMark = Encoding.ASCII.GetBytes("fml/common/Mod;");
        static readonly byte[][] RegisterMarks = { Encoding.ASCII.GetBytes("neoforge/registries/DeferredRegister"), Encoding.ASCII.GetBytes("minecraftforge/registries/DeferredRegister") };
        static readonly byte[][] NetworkMarks = { Encoding.ASCII.GetBytes("network/registration/PayloadRegistrar"), Encoding.ASCII.GetBytes("fabric/api/networking/v1/PayloadTypeRegistry"),
            Encoding.ASCII.GetBytes("minecraftforge/network/simple/SimpleChannel"), Encoding.ASCII.GetBytes("minecraftforge/network/ChannelBuilder") };

        static SideClues GetSideClues(FileInfo jar)
        {
            string key = jar.FullName.ToLowerInvariant().Replace(".disabled", "") + "|" + jar.Length + "|" + jar.LastWriteTimeUtc.Ticks;
            lock (SideCluesCache)
            {
                SideClues cached;
                if (SideCluesCache.TryGetValue(key, out cached)) return cached;
            }
            var clues = new SideClues();
            var dists = new List<HashSet<string>>();
            try
            {
                using (var stream = new FileStream(jar.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    var mixinConfigs = new List<string>();
                    foreach (ZipArchiveEntry e in zip.Entries)
                    {
                        string n = e.FullName;
                        if (Regex.IsMatch(n, @"^assets/(?!minecraft/)[^/]+/(blockstates/[^/]+\.json|models/item/[^/]+\.json)$")) clues.AddsBlocksOrItems = true;
                        else if (Regex.IsMatch(n, @"^data/[^/]+/(recipes?|loot_tables?|worldgen|advancements?|structures?)/")) clues.HasServerData = true;
                        else if (n.IndexOf('/') < 0 && n.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && n.IndexOf("mixin", StringComparison.OrdinalIgnoreCase) >= 0) mixinConfigs.Add(n);
                        else if (clues.ContentKind == null && Regex.IsMatch(n, @"^assets/(?!minecraft/)[^/]+/lang/en_us\.json$") && e.Length < 4000000) clues.ContentKind = LangContent(e, n.Split('/')[1]);
                        else if (n.EndsWith(".class", StringComparison.Ordinal) && e.Length > 0 && e.Length < 400000)
                        {
                            byte[] bytes;
                            using (Stream s = e.Open()) using (var ms = new MemoryStream()) { s.CopyTo(ms); bytes = ms.ToArray(); }
                            if (!clues.Registers && RegisterMarks.Any(m => IndexOf(bytes, m) >= 0)) clues.Registers = true;
                            if (!clues.Networking && NetworkMarks.Any(m => IndexOf(bytes, m) >= 0)) clues.Networking = true;
                            if (IndexOf(bytes, ModAnnotationMark) < 0) continue;
                            HashSet<string> d = ModDistOf(bytes);
                            if (d != null) dists.Add(d);
                        }
                    }
                    clues.MixinSide = MixinSideOf(zip, mixinConfigs);
                }
            }
            catch { }
            clues.ModClasses = dists.Count;
            // Client only when every entry point is limited to the client (likewise for the server).
            if (dists.Count > 0)
            {
                if (dists.All(d => d.Count > 0 && d.All(x => x == "CLIENT"))) clues.Dist = "client";
                else if (dists.All(d => d.Count > 0 && d.All(x => x == "DEDICATED_SERVER"))) clues.Dist = "server";
            }
            lock (SideCluesCache) SideCluesCache[key] = clues;
            return clues;
        }

        // Keys such as "block.<ns>.x", "item.<ns>.x" or "entity.<ns>.x" in its own namespace mean the mod
        // registers that content, which the server and every client must share.
        static string LangContent(ZipArchiveEntry e, string ns)
        {
            try
            {
                string text;
                using (var reader = new StreamReader(e.Open(), Encoding.UTF8)) text = reader.ReadToEnd();
                Match m = Regex.Match(text, "\"(block|item|entity|effect|enchantment|biome|fluid)\\." + Regex.Escape(ns) + "\\.");
                return m.Success ? m.Groups[1].Value : null;
            }
            catch { return null; }
        }

        static int IndexOf(byte[] hay, byte[] needle)
        {
            for (int i = 0; i <= hay.Length - needle.Length; i++)
            {
                int j = 0;
                while (j < needle.Length && hay[i + j] == needle[j]) j++;
                if (j == needle.Length) return i;
            }
            return -1;
        }

        static string MixinSideOf(ZipArchive zip, List<string> configs)
        {
            int common = 0, client = 0, server = 0;
            foreach (string name in configs)
            {
                try
                {
                    string text;
                    using (var reader = new StreamReader(zip.GetEntry(name).Open(), Encoding.UTF8)) text = reader.ReadToEnd();
                    var json = new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string, object>;
                    if (json == null || !json.ContainsKey("package")) continue; // not a mixin config (e.g. a refmap)
                    common += A(Get(json, "mixins")).Length;
                    client += A(Get(json, "client")).Length;
                    server += A(Get(json, "server")).Length;
                }
                catch { }
            }
            if (common == 0 && client > 0 && server == 0) return "client";
            if (common == 0 && server > 0 && client == 0) return "server";
            return null;
        }

        // The "dist" of a class's @Mod annotation: an empty set when it has @Mod without dist (loads
        // everywhere), the Dist names when it has one, or null when the class has no @Mod at all.
        static HashSet<string> ModDistOf(byte[] b)
        {
            try
            {
                int p = 8;
                Func<int> u2 = () => { int v = (b[p] << 8) | b[p + 1]; p += 2; return v; };
                Func<int> u4 = () => { int v = (b[p] << 24) | (b[p + 1] << 16) | (b[p + 2] << 8) | b[p + 3]; p += 4; return v; };
                int count = u2();
                var utf8 = new string[count];
                for (int i = 1; i < count; i++)
                {
                    int tag = b[p++];
                    switch (tag)
                    {
                        case 1: { int len = u2(); utf8[i] = Encoding.UTF8.GetString(b, p, len); p += len; break; }
                        case 3: case 4: p += 4; break;
                        case 5: case 6: p += 8; i++; break;
                        case 7: case 8: case 16: case 19: case 20: p += 2; break;
                        case 9: case 10: case 11: case 12: case 17: case 18: p += 4; break;
                        case 15: p += 3; break;
                        default: return null;
                    }
                }
                p += 6; // access, this, super
                int interfaces = u2(); // (read first: "p += u2()" would add to p as it was before the read)
                p += 2 * interfaces;
                for (int k = 0; k < 2; k++) // fields, then methods
                {
                    int n = u2();
                    for (int i = 0; i < n; i++) { p += 6; int ac = u2(); for (int a = 0; a < ac; a++) { p += 2; int alen = u4(); p += alen; } }
                }
                int attrs = u2();
                for (int a = 0; a < attrs; a++)
                {
                    string name = utf8[u2()];
                    int len = u4(), end = p + len;
                    if (name == "RuntimeVisibleAnnotations")
                    {
                        int num = u2();
                        for (int i = 0; i < num; i++)
                        {
                            string type = utf8[u2()];
                            bool isMod = type == "Lnet/neoforged/fml/common/Mod;" || type == "Lnet/minecraftforge/fml/common/Mod;";
                            var dist = new HashSet<string>();
                            int pairs = u2();
                            for (int q = 0; q < pairs; q++)
                            {
                                string element = utf8[u2()];
                                var values = new List<string>();
                                SkipElement(b, ref p, utf8, element == "dist" ? values : null);
                                if (isMod) foreach (string v in values) dist.Add(v);
                            }
                            if (isMod) return dist;
                        }
                    }
                    p = end;
                }
            }
            catch { }
            return null;
        }

        // Steps over one annotation element value, collecting enum constant names when asked.
        static void SkipElement(byte[] b, ref int p, string[] utf8, List<string> enums)
        {
            char tag = (char)b[p++];
            switch (tag)
            {
                case 'e':
                    p += 2;
                    string constant = utf8[(b[p] << 8) | b[p + 1]];
                    p += 2;
                    if (enums != null) enums.Add(constant);
                    break;
                case '@':
                    p += 2;
                    int pairs = (b[p] << 8) | b[p + 1]; p += 2;
                    for (int i = 0; i < pairs; i++) { p += 2; SkipElement(b, ref p, utf8, null); }
                    break;
                case '[':
                    int n = (b[p] << 8) | b[p + 1]; p += 2;
                    for (int i = 0; i < n; i++) SkipElement(b, ref p, utf8, enums);
                    break;
                default: p += 2; break; // B C D F I J S Z s c
            }
        }
        // CurseForge's environment tags on the jar's own file: "Client" without "Server" means client only
        // (and the other way round). Many files carry neither, or both, which says nothing.
        static string CfEnvironment(FileInfo jar)
        {
            if (!CfReady) return null;
            Dictionary<string, object> match;
            try { match = CfMatchFor(jar.DirectoryName, jar.Name); }
            catch (UserError) { return null; }
            List<string> versions = A(Get(D(Get(match, "file")), "gameVersions")).OfType<string>().ToList();
            bool c = versions.Contains("Client"), s = versions.Contains("Server");
            return c && !s ? "client" : s && !c ? "server" : null;
        }
    }
}
