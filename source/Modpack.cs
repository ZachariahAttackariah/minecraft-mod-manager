// Modpacks: exporting this instance as a Modrinth .mrpack or a CurseForge .zip, and importing either
// into a new Prism instance.
//
// Export lists mods by where they're published (Modrinth file URLs with their hashes; CurseForge project
// and file ids), so the pack stays small. Jars neither site has byte for byte can go into the pack's
// overrides, and so can the config folder. Only mods that are on are included.
//
// Import makes a new instance with the pack's Minecraft and loader versions, downloads each file
// (Modrinth packs only from the hosts the format allows, every file checked against its hash), and
// unpacks the overrides. CurseForge files whose authors don't allow downloads through other apps are
// listed with their pages instead.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace MinecraftModManager
{
    static partial class Program
    {
        static readonly string[] MrpackHosts = { "cdn.modrinth.com", "github.com", "raw.githubusercontent.com", "gitlab.com" };

        static string[] CurrentPackVersions()
        {
            string instanceDir = Directory.GetParent(GameDir()) == null ? null : Directory.GetParent(GameDir()).FullName;
            string[] pack = instanceDir != null && File.Exists(Path.Combine(instanceDir, "mmc-pack.json")) ? InstancePack(instanceDir) : null;
            if (pack == null || pack[0] == null || pack[1] == null || pack[2] == null)
                throw new UserError("Exporting needs a Prism instance (its Minecraft and loader versions go into the pack). Pick an instance at the top right.");
            return pack;
        }

        static void AddZipEntry(ZipArchive zip, string entryName, string sourcePath)
        {
            using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (Stream output = zip.CreateEntry(entryName, CompressionLevel.Optimal).Open()) input.CopyTo(output);
        }
        static void AddZipText(ZipArchive zip, string entryName, string text)
        {
            using (var writer = new StreamWriter(zip.CreateEntry(entryName, CompressionLevel.Optimal).Open(), new UTF8Encoding(false))) writer.Write(text);
        }
        static void AddZipFolder(ZipArchive zip, string folder, string prefix)
        {
            if (!Directory.Exists(folder)) return;
            string root = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
            foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                try { AddZipEntry(zip, prefix + file.Substring(root.Length).Replace('\\', '/'), file); } catch (IOException) { }
            }
        }

        static Dictionary<string, object> ExportModpack(Dictionary<string, object> body)
        {
            string format = Str(body, "format") == "curseforge" ? "curseforge" : "mrpack";
            string name = Regex.Replace(Str(body, "name") ?? "", @"\s+", " ").Trim();
            string version = Regex.Replace(Str(body, "version") ?? "1.0.0", @"\s+", "").Trim();
            if (name.Length == 0 || name.Length > 60) throw new UserError("Give the pack a name (up to 60 characters).");
            if (version.Length == 0 || version.Length > 20) version = "1.0.0";
            bool includeOther = Bool(body, "includeOther"), includeConfigs = Bool(body, "includeConfigs");
            string[] pack = CurrentPackVersions();
            string mc = pack[0], loaderName = pack[1], loaderVersion = pack[2];
            string dir = modsDir;
            string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            Directory.CreateDirectory(downloads);
            string fileName = Regex.Replace(name, @"[^\w.\- ]+", "_") + " " + version + (format == "mrpack" ? ".mrpack" : ".zip");
            string target = FreeFileName(downloads, fileName);

            // Side tags decide where each mod goes (Modrinth packs say client / server per file).
            Dictionary<string, List<string>> tags = FolderTags(dir);
            var listed = new List<object>();
            var overrides = new List<FileInfo>();
            var leftOut = new List<string>();
            List<FileInfo> jars = new DirectoryInfo(dir).GetFiles("*.jar").ToList(); // on only: turned-off jars end in .disabled
            lock (ModrinthLock)
            {
                Dictionary<string, string> hashes = IdentifyFolder(dir);
                Dictionary<string, Dictionary<string, object>> cf = format == "curseforge" && CfReady ? CfIdentify(jars) : new Dictionary<string, Dictionary<string, object>>();
                foreach (FileInfo jar in jars)
                {
                    string hash;
                    hashes.TryGetValue(jar.Name, out hash);
                    if (format == "mrpack")
                    {
                        Dictionary<string, object> v = hash == null ? null : VersionByHash[hash];
                        Dictionary<string, object> file = v == null || Bool(v, "loose") ? null
                            : A(Get(v, "files")).Select(D).FirstOrDefault(f => f != null && string.Equals(S(D(Get(f, "hashes")), "sha1"), hash, StringComparison.OrdinalIgnoreCase));
                        if (file != null && S(file, "url") != null)
                        {
                            List<string> t;
                            tags.TryGetValue(jar.Name, out t);
                            t = t ?? new List<string>();
                            bool sided = t.Contains("client") || t.Contains("server");
                            listed.Add(Obj("path", "mods/" + S(file, "filename"),
                                "hashes", Obj("sha1", S(D(Get(file, "hashes")), "sha1"), "sha512", S(D(Get(file, "hashes")), "sha512")),
                                "env", Obj("client", !sided || t.Contains("client") ? "required" : "unsupported", "server", !sided || t.Contains("server") ? "required" : "unsupported"),
                                "downloads", new[] { S(file, "url") }, "fileSize", Get(file, "size")));
                            continue;
                        }
                    }
                    else
                    {
                        Dictionary<string, object> m;
                        if (cf.TryGetValue(jar.Name, out m))
                        {
                            listed.Add(Obj("projectID", CfId(Get(m, "id")), "fileID", CfId(Get(D(Get(m, "file")), "id")), "required", true));
                            continue;
                        }
                    }
                    if (includeOther) overrides.Add(jar); else leftOut.Add(jar.Name);
                }
            }

            string temp = target + ".part";
            try
            {
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                    if (format == "mrpack")
                    {
                        string loaderKey = loaderName == "fabric" ? "fabric-loader" : loaderName == "quilt" ? "quilt-loader" : loaderName;
                        var deps = new Dictionary<string, object> { { "minecraft", mc }, { loaderKey, loaderVersion } };
                        AddZipText(zip, "modrinth.index.json", json.Serialize(Obj("formatVersion", 1, "game", "minecraft", "versionId", version, "name", name,
                            "summary", "Exported from Minecraft Mod Manager", "files", listed, "dependencies", deps)));
                    }
                    else
                    {
                        AddZipText(zip, "manifest.json", json.Serialize(Obj("minecraft", Obj("version", mc, "modLoaders", new[] { Obj("id", loaderName + "-" + loaderVersion, "primary", true) }),
                            "manifestType", "minecraftModpack", "manifestVersion", 1, "name", name, "version", version, "author", "", "files", listed, "overrides", "overrides")));
                    }
                    foreach (FileInfo jar in overrides) AddZipEntry(zip, "overrides/mods/" + jar.Name, jar.FullName);
                    if (includeConfigs)
                    {
                        AddZipFolder(zip, Path.Combine(GameDir(), "config"), "overrides/config/");
                        AddZipFolder(zip, Path.Combine(GameDir(), "defaultconfigs"), "overrides/defaultconfigs/");
                    }
                }
                File.Move(temp, target);
            }
            catch (Exception ex)
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                throw new UserError("Couldn't write the pack (" + ex.Message + ").");
            }
            Log("Exported  " + Path.GetFileName(target) + " (" + listed.Count + " listed, " + overrides.Count + " included, " + leftOut.Count + " left out)");
            try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + target + "\""); } catch { }
            return Obj("ok", true, "path", target, "listed", listed.Count, "included", overrides.Count, "leftOut", leftOut);
        }

        // ---------- Import ----------

        static Dictionary<string, object> ImportModpack(HttpListenerRequest req)
        {
            string name = Path.GetFileName(req.QueryString["name"] ?? "");
            if (!Regex.IsMatch(name, @"\.(mrpack|zip)$", RegexOptions.IgnoreCase)) throw new UserError("Pick a .mrpack or a CurseForge .zip.");
            string staging = Path.Combine(dataDir, "downloads");
            Directory.CreateDirectory(staging);
            string temp = Path.Combine(staging, Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write)) req.InputStream.CopyTo(output);
                using (ZipArchive zip = ZipFile.OpenRead(temp))
                {
                    ZipArchiveEntry mrIndex = zip.GetEntry("modrinth.index.json"), cfManifest = zip.GetEntry("manifest.json");
                    if (mrIndex != null) return ImportMrpack(zip, mrIndex);
                    if (cfManifest != null) return ImportCurseForgePack(zip, cfManifest);
                    throw new UserError(name + " isn't a modpack (no modrinth.index.json or manifest.json inside).");
                }
            }
            catch (InvalidDataException) { throw new UserError(name + " isn't a valid zip file."); }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
        }

        static Dictionary<string, object> ReadZipJson(ZipArchiveEntry entry)
        {
            using (var reader = new StreamReader(entry.Open(), Encoding.UTF8))
                return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(reader.ReadToEnd()) as Dictionary<string, object>
                    ?? new Dictionary<string, object>();
        }

        // A path inside the new instance's game folder, or null when it would land outside it.
        static string SafeGamePath(string gameDir, string rel)
        {
            if (string.IsNullOrWhiteSpace(rel) || rel.Contains("..") || Path.IsPathRooted(rel) || rel.Contains(":")) return null;
            string full = Path.GetFullPath(Path.Combine(gameDir, rel.Replace('/', '\\')));
            return full.StartsWith(Path.GetFullPath(gameDir).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) ? full : null;
        }

        static string NewPackInstance(string packName, string mc, string loaderName, string loaderVersion)
        {
            Dictionary<string, object> created = CreateInstance(Obj("name", packName, "minecraft", mc, "loader", loaderName, "loaderVersion", loaderVersion));
            return Path.GetDirectoryName(modsDir.TrimEnd('\\')); // CreateInstance switches to the new instance
        }

        static void Download(Uri url, string path, string sha1, string sha512)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = ModrinthUserAgent;
            request.Timeout = 60000;
            request.ReadWriteTimeout = 60000;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".download";
            using (WebResponse response = request.GetResponse())
            using (Stream input = response.GetResponseStream())
            using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write)) input.CopyTo(output);
            bool ok = true;
            if (sha512 != null) using (var h = SHA512.Create()) using (var s = File.OpenRead(temp)) ok = string.Equals(Hex(h.ComputeHash(s)), sha512, StringComparison.OrdinalIgnoreCase);
            else if (sha1 != null) using (var h = SHA1.Create()) using (var s = File.OpenRead(temp)) ok = string.Equals(Hex(h.ComputeHash(s)), sha1, StringComparison.OrdinalIgnoreCase);
            if (!ok) { File.Delete(temp); throw new UserError(Path.GetFileName(path) + " didn't match the pack's checksum, so it was thrown away."); }
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }

        static int ExtractOverrides(ZipArchive zip, string folder, string gameDir)
        {
            int n = 0;
            string prefix = folder.TrimEnd('/') + "/";
            foreach (ZipArchiveEntry e in zip.Entries.Where(e => e.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && e.Name.Length > 0))
            {
                string path = SafeGamePath(gameDir, e.FullName.Substring(prefix.Length));
                if (path == null) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                e.ExtractToFile(path, true);
                n++;
            }
            return n;
        }

        static Dictionary<string, object> ImportMrpack(ZipArchive zip, ZipArchiveEntry indexEntry)
        {
            Dictionary<string, object> index = ReadZipJson(indexEntry);
            Dictionary<string, object> deps = D(Get(index, "dependencies")) ?? new Dictionary<string, object>();
            string mc = Str(deps, "minecraft");
            string loaderName = deps.ContainsKey("neoforge") ? "neoforge" : deps.ContainsKey("forge") ? "forge" : deps.ContainsKey("fabric-loader") ? "fabric" : deps.ContainsKey("quilt-loader") ? "quilt" : null;
            if (mc == null || loaderName == null) throw new UserError("This pack doesn't say which Minecraft version and loader it needs.");
            string loaderVersion = Str(deps, loaderName == "fabric" ? "fabric-loader" : loaderName == "quilt" ? "quilt-loader" : loaderName);
            string packName = Str(index, "name") ?? "Imported pack";
            string gameDir = NewPackInstance(packName, mc, loaderName, loaderVersion);
            int downloaded = 0;
            var failed = new List<object>();
            foreach (Dictionary<string, object> f in A(Get(index, "files")).Select(D).Where(f => f != null))
            {
                string rel = Str(f, "path");
                if (JobStopped()) { failed.Add(Obj("file", rel, "error", "stopped before this one")); continue; }
                if (Str(D(Get(f, "env")), "client") == "unsupported") continue; // server-only files stay out of a game instance
                string path = SafeGamePath(gameDir, rel);
                Uri url = A(Get(f, "downloads")).OfType<string>().Select(u => { Uri x; return Uri.TryCreate(u, UriKind.Absolute, out x) ? x : null; })
                    .FirstOrDefault(u => u != null && u.Scheme == Uri.UriSchemeHttps && MrpackHosts.Contains(u.Host.ToLowerInvariant()));
                if (path == null || url == null) { failed.Add(Obj("file", rel, "error", "not from a host Modrinth packs allow")); continue; }
                try { Download(url, path, S(D(Get(f, "hashes")), "sha1"), S(D(Get(f, "hashes")), "sha512")); downloaded++; }
                catch (Exception ex) { failed.Add(Obj("file", rel, "error", ex is UserError ? ex.Message : "download failed (" + ex.Message + ")")); }
            }
            int extracted = ExtractOverrides(zip, "overrides", gameDir) + ExtractOverrides(zip, "client-overrides", gameDir);
            Log("Imported  " + packName + " (" + downloaded + " downloaded, " + extracted + " files from overrides, " + failed.Count + " failed)");
            return Obj("ok", true, "name", packName, "downloaded", downloaded, "extracted", extracted, "failed", failed, "manual", new List<object>());
        }

        static Dictionary<string, object> ImportCurseForgePack(ZipArchive zip, ZipArchiveEntry manifestEntry)
        {
            if (!CfReady) throw new UserError("Importing a CurseForge pack needs a CurseForge key (Settings > Sources).");
            Dictionary<string, object> manifest = ReadZipJson(manifestEntry);
            Dictionary<string, object> minecraft = D(Get(manifest, "minecraft"));
            string mc = Str(minecraft, "version");
            Dictionary<string, object> primary = A(Get(minecraft, "modLoaders")).Select(D).FirstOrDefault(l => l != null && Bool(l, "primary"))
                ?? A(Get(minecraft, "modLoaders")).Select(D).FirstOrDefault(l => l != null);
            Match lm = Regex.Match(Str(primary, "id") ?? "", @"^(neoforge|forge|fabric|quilt)-(.+)$");
            if (mc == null || !lm.Success) throw new UserError("This pack doesn't say which Minecraft version and loader it needs.");
            string packName = Str(manifest, "name") ?? "Imported pack";
            string gameDir = NewPackInstance(packName, mc, lm.Groups[1].Value, lm.Groups[2].Value);
            string mods = Path.Combine(gameDir, "mods");
            List<long> fileIds = A(Get(manifest, "files")).Select(D).Where(f => f != null).Select(f => CfId(Get(f, "fileID"))).Where(id => id > 0).ToList();
            var files = new List<Dictionary<string, object>>();
            for (int i = 0; i < fileIds.Count; i += 50)
                files.AddRange(CfList(CfHttp("POST", "/mods/files", Obj("fileIds", fileIds.Skip(i).Take(50).ToList()))).Select(D).Where(f => f != null));
            CfFetchMods(files.Select(f => CfId(Get(f, "modId"))).Distinct());
            int downloaded = 0;
            var failed = new List<object>();
            var manual = new List<object>();
            foreach (Dictionary<string, object> f in files)
            {
                Dictionary<string, object> mod = CfMod(CfId(Get(f, "modId")));
                string fileName = Path.GetFileName(S(f, "fileName") ?? "");
                if (JobStopped()) { failed.Add(Obj("file", fileName, "error", "stopped before this one")); continue; }
                string address = S(f, "downloadUrl");
                Uri url;
                if (mod != null && !CfDownloadsAllowed(mod) || string.IsNullOrEmpty(address))
                {
                    manual.Add(Obj("name", S(mod, "name") ?? fileName, "file", fileName, "url", mod == null ? null : CfUrl(mod)));
                    continue;
                }
                if (!Uri.TryCreate(address, UriKind.Absolute, out url) || url.Scheme != Uri.UriSchemeHttps || !url.Host.EndsWith("forgecdn.net", StringComparison.OrdinalIgnoreCase) || fileName.Length == 0)
                {
                    failed.Add(Obj("file", fileName, "error", "unexpected download address"));
                    continue;
                }
                string sha1 = A(Get(f, "hashes")).Select(D).Where(x => x != null && CfId(Get(x, "algo")) == 1).Select(x => S(x, "value")).FirstOrDefault();
                // Resource packs and shaders in CurseForge packs are listed too; they go by their extension.
                string folder = fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ? mods : Path.Combine(gameDir, "resourcepacks");
                try { Download(url, Path.Combine(folder, fileName), sha1, null); downloaded++; }
                catch (Exception ex) { failed.Add(Obj("file", fileName, "error", ex is UserError ? ex.Message : "download failed (" + ex.Message + ")")); }
            }
            int extracted = ExtractOverrides(zip, Str(manifest, "overrides") ?? "overrides", gameDir);
            Log("Imported  " + packName + " (CurseForge pack: " + downloaded + " downloaded, " + manual.Count + " to download by hand, " + failed.Count + " failed)");
            return Obj("ok", true, "name", packName, "downloaded", downloaded, "extracted", extracted, "failed", failed, "manual", manual);
        }
    }
}
