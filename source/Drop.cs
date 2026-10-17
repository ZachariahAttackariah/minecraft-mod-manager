// Files dropped on the window: a .jar goes into the mods folder, a pack .zip is sorted by what's
// inside it (like a .zip downloaded in a tab, see Packs.cs). The page sends each file's bytes.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;

namespace MinecraftModManager
{
    static partial class Program
    {
        const long MaxDropBytes = 1L << 30;

        static Dictionary<string, object> ReceiveDroppedFile(HttpListenerRequest req)
        {
            string name = Path.GetFileName(req.QueryString["name"] ?? "");
            bool jar = name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase), zip = name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
            if (name.Length == 0 || name.StartsWith(".") || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new UserError("That file name can't be used.");
            if (!jar && !zip) throw new UserError(name + " isn't a .jar or a pack .zip, so it wasn't added.");
            if (req.ContentLength64 > MaxDropBytes) throw new UserError(name + " is too big to add this way.");
            string staging = Path.Combine(dataDir, "downloads");
            Directory.CreateDirectory(staging);
            string temp = Path.Combine(staging, Guid.NewGuid().ToString("N") + (jar ? ".jar" : ".zip"));
            try
            {
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
                {
                    var buffer = new byte[81920];
                    long total = 0;
                    int n;
                    while ((n = req.InputStream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        total += n;
                        if (total > MaxDropBytes) throw new UserError(name + " is too big to add this way.");
                        output.Write(buffer, 0, n);
                    }
                }
                // Both kinds are zip archives; anything that isn't one is refused before it lands anywhere.
                try { using (ZipFile.OpenRead(temp)) { } }
                catch (InvalidDataException) { throw new UserError(name + " isn't a valid " + (jar ? "jar" : "zip") + " file."); }

                if (zip)
                {
                    string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                    string message = PlaceDownloadedPack(temp, name, downloads);
                    return Obj("ok", true, "kind", "pack", "message", message);
                }
                string dir = modsDir;
                string target = Path.Combine(dir, name);
                if (File.Exists(target) || File.Exists(target + ".disabled"))
                    return Obj("ok", true, "kind", "mod", "skipped", true, "message", name + " is already in the mods folder.");
                File.Move(temp, target);
                Log("Added     " + name + " (dropped on the window)");
                TagLibraryByLookup(name, null);
                return Obj("ok", true, "kind", "mod", "file", name, "message", "Added " + name + " to the mods folder.");
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }
    }
}
