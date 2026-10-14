// Personal touches for the look: a background image (kept in the app's data folder as background.*,
// shown dimmed behind everything), and user.css in the same folder, loaded after the page's own styles
// for anything the Settings page doesn't cover.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace MinecraftModManager
{
    static partial class Program
    {
        static readonly string[] BackgroundTypes = { ".png", ".jpg", ".jpeg", ".webp", ".gif" };

        static FileInfo BackgroundFile()
        {
            try
            {
                var dir = new DirectoryInfo(dataDir);
                return dir.Exists ? dir.GetFiles("background.*").FirstOrDefault(f => BackgroundTypes.Contains(f.Extension.ToLowerInvariant())) : null;
            }
            catch { return null; }
        }

        static Dictionary<string, object> BackgroundInfo()
        {
            FileInfo f = BackgroundFile();
            return f == null ? null : Obj("version", f.LastWriteTimeUtc.Ticks.ToString());
        }

        static Dictionary<string, object> ReceiveBackground(HttpListenerRequest req)
        {
            string ext = Path.GetExtension(req.QueryString["name"] ?? "").ToLowerInvariant();
            if (!BackgroundTypes.Contains(ext)) throw new UserError("Pick a PNG, JPG, WebP or GIF image.");
            if (req.ContentLength64 > 25L * 1024 * 1024) throw new UserError("That image is too big (25 MB at most).");
            Directory.CreateDirectory(dataDir);
            string temp = Path.Combine(dataDir, "background.new");
            using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write)) req.InputStream.CopyTo(output);
            FileInfo old = BackgroundFile();
            if (old != null) old.Delete(); // the app's own copy; the original picture stays where it was
            File.Move(temp, Path.Combine(dataDir, "background" + (ext == ".jpeg" ? ".jpg" : ext)));
            return Obj("ok", true, "background", BackgroundInfo());
        }

        static Dictionary<string, object> RemoveBackground()
        {
            FileInfo f = BackgroundFile();
            if (f != null) f.Delete();
            return Obj("ok", true);
        }

        static void SendBackground(HttpListenerContext ctx)
        {
            FileInfo f = BackgroundFile();
            if (f == null) { SendText(ctx, 404, "text/plain; charset=utf-8", "No background"); return; }
            string ext = f.Extension.ToLowerInvariant();
            byte[] bytes = File.ReadAllBytes(f.FullName);
            HttpListenerResponse res = ctx.Response;
            res.StatusCode = 200;
            res.ContentType = ext == ".png" ? "image/png" : ext == ".webp" ? "image/webp" : ext == ".gif" ? "image/gif" : "image/jpeg";
            res.AddHeader("Cache-Control", "private, max-age=31536000"); // the page asks with the file's date, so a new image is a new address
            res.AddHeader("X-Content-Type-Options", "nosniff");
            res.ContentLength64 = bytes.Length;
            using (Stream output = res.OutputStream) output.Write(bytes, 0, bytes.Length);
        }

        static string UserCssPath { get { return Path.Combine(dataDir, "user.css"); } }

        static void SendUserCss(HttpListenerContext ctx)
        {
            string css = "";
            try { if (File.Exists(UserCssPath)) css = File.ReadAllText(UserCssPath, Encoding.UTF8); } catch (IOException) { }
            SendText(ctx, 200, "text/css; charset=utf-8", css);
        }

        // Makes user.css (with a few examples, all commented out) the first time, then opens it to edit.
        static void OpenUserCss()
        {
            Directory.CreateDirectory(dataDir);
            if (!File.Exists(UserCssPath))
                File.WriteAllText(UserCssPath,
                    "/* Your own styles for Minecraft Mod Manager. They load after the app's own, so anything here wins.\n" +
                    "   Save, then press \"Reload user.css\" in Settings > Look (or restart the app). Delete this file to undo it all.\n\n" +
                    "   A few things to try (remove the slash-stars around a line to use it):\n\n" +
                    "   :root { --bg: #101418; --surface: #182028; }        darker panels\n" +
                    "   .row .mod-name { font-size: 16px; }                  bigger mod names\n" +
                    "   .chip { border-radius: 99px; }                       pill-shaped tags\n" +
                    "   .wordmark { letter-spacing: 0.1em; }                 a wider title\n" +
                    "*/\n", new UTF8Encoding(false));
            try { Process.Start(new ProcessStartInfo(UserCssPath) { UseShellExecute = true }); }
            catch { Process.Start("notepad.exe", "\"" + UserCssPath + "\""); }
        }
    }
}
