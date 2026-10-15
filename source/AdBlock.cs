// Ads in the app's tabs (Modrinth, CurseForge, mod pages) are blocked with uBlock Origin Lite by Raymond Hill
// (GPLv3, https://github.com/uBlockOrigin/uBOL-home). It isn't part of this repository or the exe: the first
// time the window opens, its Edge build is fetched from the project's GitHub releases into the data folder
// (extensions\ublock-origin-lite) and loaded into the tabs' browser profile. A newer release is looked for
// at most once a day. Anything failing (offline, GitHub unreachable) just leaves things as they are until
// the next start.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Web.WebView2.Core;

namespace MinecraftModManager
{
    static partial class Program
    {
        const string AdBlockReleases = "https://api.github.com/repos/uBlockOrigin/uBOL-home/releases/latest";

        static string AdBlockRoot { get { return Path.Combine(dataDir, "extensions", "ublock-origin-lite"); } }
        static string AdBlockStateFile { get { return Path.Combine(AdBlockRoot, "state.json"); } }

        sealed class AdBlockState { public string Version; public string Installed; public DateTime Checked; }

        static AdBlockState ReadAdBlockState()
        {
            try
            {
                var d = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(AdBlockStateFile)) as Dictionary<string, object>;
                DateTime when;
                DateTime.TryParse(Str(d, "checked"), null, System.Globalization.DateTimeStyles.RoundtripKind, out when);
                return new AdBlockState { Version = Str(d, "version"), Installed = Str(d, "installed"), Checked = when };
            }
            catch { return new AdBlockState(); }
        }

        static void WriteAdBlockState(AdBlockState s)
        {
            try
            {
                Directory.CreateDirectory(AdBlockRoot);
                File.WriteAllText(AdBlockStateFile, new JavaScriptSerializer().Serialize(Obj("version", s.Version, "installed", s.Installed, "checked", s.Checked.ToString("o"))), new UTF8Encoding(false));
            }
            catch { }
        }

        static string AdBlockFolder(string version) { return Path.Combine(AdBlockRoot, Regex.Replace(version ?? "", @"[^\w.\-]", "_")); }

        // Off the UI thread: makes sure the newest release (checked at most daily) is unpacked. Returns the state.
        static AdBlockState PrepareAdBlock()
        {
            AdBlockState s = ReadAdBlockState();
            bool have = s.Version != null && File.Exists(Path.Combine(AdBlockFolder(s.Version), "manifest.json"));
            if (have && DateTime.UtcNow - s.Checked < TimeSpan.FromDays(1)) return s;
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(AdBlockReleases);
                request.UserAgent = ModrinthUserAgent;
                request.Accept = "application/vnd.github+json";
                request.Timeout = 20000;
                Dictionary<string, object> release;
                using (var response = request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    release = new JavaScriptSerializer().DeserializeObject(reader.ReadToEnd()) as Dictionary<string, object>;
                string tag = Str(release, "tag_name");
                Dictionary<string, object> asset = A(Get(release, "assets")).Select(D)
                    .FirstOrDefault(a => a != null && Regex.IsMatch(Str(a, "name") ?? "", @"^uBOLite_.*\.edge\.zip$", RegexOptions.IgnoreCase));
                string url = Str(asset, "browser_download_url");
                Uri uri;
                if (tag == null || url == null || !Uri.TryCreate(url, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com") return s;
                if (tag != s.Version || !have)
                {
                    string folder = AdBlockFolder(tag);
                    string zip = Path.Combine(AdBlockRoot, "download.zip"), temp = folder + ".unpacking";
                    Directory.CreateDirectory(AdBlockRoot);
                    var get = (HttpWebRequest)WebRequest.Create(uri);
                    get.UserAgent = ModrinthUserAgent;
                    get.Timeout = 60000;
                    get.ReadWriteTimeout = 60000;
                    using (var response = get.GetResponse())
                    using (Stream input = response.GetResponseStream())
                    using (var output = new FileStream(zip, FileMode.Create, FileAccess.Write)) input.CopyTo(output);
                    if (Directory.Exists(temp)) Directory.Delete(temp, true);
                    ZipFile.ExtractToDirectory(zip, temp);
                    File.Delete(zip);
                    // The extension's files are either at the top of the zip or in one folder inside it.
                    string root = File.Exists(Path.Combine(temp, "manifest.json")) ? temp
                        : Directory.GetDirectories(temp).FirstOrDefault(d => File.Exists(Path.Combine(d, "manifest.json")));
                    if (root == null) { Directory.Delete(temp, true); return s; }
                    if (Directory.Exists(folder)) Directory.Delete(folder, true);
                    Directory.Move(root, folder);
                    if (Directory.Exists(temp)) Directory.Delete(temp, true);
                    s.Version = tag;
                }
                s.Checked = DateTime.UtcNow;
                WriteAdBlockState(s);
            }
            catch (Exception) { } // tried again next start
            return s;
        }

        // On the UI thread, once the browser profile exists: loads the prepared version into it, replacing an
        // older one, and tidies away old copies.
        static async void SetUpAdBlock(CoreWebView2Profile profile)
        {
            try
            {
                AdBlockState s = await Task.Run(() => PrepareAdBlock());
                if (s.Version == null) return;
                string folder = AdBlockFolder(s.Version);
                if (!File.Exists(Path.Combine(folder, "manifest.json"))) return;
                IReadOnlyList<CoreWebView2BrowserExtension> loaded = await profile.GetBrowserExtensionsAsync();
                CoreWebView2BrowserExtension current = loaded.FirstOrDefault(e => (e.Name ?? "").IndexOf("uBlock", StringComparison.OrdinalIgnoreCase) >= 0);
                if (current != null && s.Installed == s.Version)
                {
                    if (!current.IsEnabled) await current.EnableAsync(true);
                    return;
                }
                if (current != null) await current.RemoveAsync();
                await profile.AddBrowserExtensionAsync(folder);
                s.Installed = s.Version;
                WriteAdBlockState(s);
                foreach (string old in Directory.GetDirectories(AdBlockRoot).Where(d => !string.Equals(Path.GetFullPath(d), Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase)))
                    try { Directory.Delete(old, true); } catch { }
            }
            catch (Exception) { } // the tabs simply show ads this time
        }
    }
}
