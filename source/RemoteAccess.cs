// Remote access: the app's page on a phone or another computer, over the home network or a private VPN such
// as Tailscale (which reaches this PC from anywhere). Never the open internet: connections from public
// addresses are dropped. Off until switched on in Settings.
//
// A small gate listens on the app's port + 1. A device gets through only once it's paired: Settings shows an
// 8-digit code (10 minutes, 5 tries), the device types it in, and gets a long random key as a cookie. Only a
// hash of each key is kept, in the settings file, and any device can be removed there. Past the gate a request
// goes to the app's own local server unchanged, apart from what makes it local (Host, Origin) and a header
// naming the device, so the page knows it's remote and the app refuses things that would open a window on
// the PC (folder pickers, Explorer). The pairing and the device list can only be managed on the PC.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace MinecraftModManager
{
    static partial class Program
    {
        static readonly object remoteLock = new object();
        static bool remoteEnabled;
        static readonly List<Dictionary<string, object>> remoteDevices = new List<Dictionary<string, object>>(); // id, name, hash, added, seen
        static string pairCode;
        static DateTime pairExpires;
        static int pairTries;
        static TcpListener remoteListener;
        static DateTime remoteSeenSaved = DateTime.MinValue;
        [ThreadStatic] static string remoteViewer; // the paired device's name, for requests that came through the gate

        const string RemoteCookie = "mmm_remote";
        static int RemotePort { get { return port + 1; } }

        // Requests that open a window or Explorer on the PC: no use from a phone, and a dialog popping up on an
        // unattended PC would block the app.
        static readonly HashSet<string> PcOnlyRoutes = new HashSet<string> {
            "/api/choose-folder", "/api/open-folder", "/api/open-path", "/api/open-game-folder", "/api/open-file", "/api/open-url",
            "/api/reveal", "/api/packs/reveal", "/api/packs/open", "/api/packs/add", "/api/always/reveal", "/api/always/open",
            "/api/always/add-files", "/api/user-css/open", "/api/zoom", "/api/crash/open", "/api/game/show-prism", "/api/prism/open",
            "/api/instances/open", "/api/sharing/open", "/api/server/folder", "/api/server/open-folder", "/api/server/pick-parent",
            "/api/worlds/open-folder"
        };

        // ---- settings -----------------------------------------------------------------------------------

        static void LoadRemoteSettings(Dictionary<string, object> saved)
        {
            if (saved == null) return;
            lock (remoteLock)
            {
                remoteEnabled = Bool(saved, "enabled");
                remoteDevices.Clear();
                foreach (object o in A(Get(saved, "devices")))
                {
                    var d = o as Dictionary<string, object>;
                    if (d != null && Str(d, "id") != null && Str(d, "hash") != null) remoteDevices.Add(d);
                }
            }
        }

        static Dictionary<string, object> RemoteSettingsCopy()
        {
            lock (remoteLock) return Obj("enabled", remoteEnabled, "devices", remoteDevices.Select(d => new Dictionary<string, object>(d)).ToList());
        }

        // ---- the page's Settings section (PC only) ------------------------------------------------------

        static Dictionary<string, object> RemoteStatus()
        {
            lock (remoteLock)
            {
                bool pairing = pairCode != null && DateTime.UtcNow < pairExpires;
                return Obj("enabled", remoteEnabled, "listening", remoteListener != null, "port", RemotePort,
                    "addresses", RemoteAddresses(),
                    "pairing", pairing ? Obj("code", pairCode, "seconds", (int)(pairExpires - DateTime.UtcNow).TotalSeconds) : null,
                    "devices", remoteDevices.Select(d => Obj("id", Str(d, "id"), "name", Str(d, "name"), "added", Str(d, "added"), "seen", Str(d, "seen"))).ToList());
            }
        }

        static Dictionary<string, object> HandleRemoteApi(string path, Dictionary<string, object> body)
        {
            if (remoteViewer != null) throw new UserError("Remote access is managed on the PC.");
            switch (path)
            {
                case "/api/remote":
                    return RemoteStatus();
                case "/api/remote/enable":
                {
                    bool on = Bool(body, "on");
                    lock (remoteLock)
                    {
                        remoteEnabled = on;
                        if (!on) { pairCode = null; }
                    }
                    if (on) StartRemote(); else StopRemote();
                    SaveSettings();
                    Log("Remote access " + (on ? "on (port " + RemotePort + ")" : "off"));
                    return RemoteStatus();
                }
                case "/api/remote/pair":
                    lock (remoteLock)
                    {
                        if (!remoteEnabled) throw new UserError("Turn on remote access first.");
                        byte[] b = new byte[4];
                        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
                        pairCode = ((BitConverter.ToUInt32(b, 0) % 100000000u)).ToString("D8");
                        pairExpires = DateTime.UtcNow.AddMinutes(10);
                        pairTries = 0;
                    }
                    return RemoteStatus();
                case "/api/remote/pair/cancel":
                    lock (remoteLock) pairCode = null;
                    return RemoteStatus();
                case "/api/remote/remove":
                {
                    string id = Str(body, "id");
                    string name = null;
                    lock (remoteLock)
                    {
                        var d = remoteDevices.FirstOrDefault(x => Str(x, "id") == id);
                        if (d == null) throw new UserError("That device isn't paired any more.");
                        name = Str(d, "name");
                        remoteDevices.Remove(d);
                    }
                    SaveSettings();
                    Log("Remote access: removed " + name);
                    return RemoteStatus();
                }
            }
            throw new UserError("Unknown request: " + path);
        }

        // The addresses a device can open, with what each one is: the home network, or a VPN like Tailscale.
        static List<object> RemoteAddresses()
        {
            var list = new List<object>();
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    bool tailscale = (ni.Name + " " + ni.Description).IndexOf("tailscale", StringComparison.OrdinalIgnoreCase) >= 0;
                    foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        IPAddress a = ua.Address;
                        if (a.AddressFamily != AddressFamily.InterNetwork || !PrivateAddress(a)) continue;
                        byte[] b = a.GetAddressBytes();
                        bool cgnat = b[0] == 100 && (b[1] & 0xC0) == 64;
                        string label = tailscale ? "Tailscale: works from anywhere" : cgnat ? "VPN" : "Home network (" + ni.Name + ")";
                        list.Add(Obj("url", "http://" + a + ":" + RemotePort + "/", "label", label, "vpn", tailscale || cgnat));
                    }
                }
            }
            catch { }
            return list.OrderByDescending(o => (bool)((Dictionary<string, object>)o)["vpn"]).ToList();
        }

        // ---- the gate -----------------------------------------------------------------------------------

        static void StartRemote()
        {
            lock (remoteLock)
            {
                if (remoteListener != null) return;
                try
                {
                    var l = new TcpListener(IPAddress.IPv6Any, RemotePort);
                    l.Server.DualMode = true; // IPv4 too
                    l.Start();
                    remoteListener = l;
                }
                catch (Exception ex)
                {
                    remoteListener = null;
                    Log("Remote access couldn't listen on port " + RemotePort + ": " + ex.Message);
                    return;
                }
            }
            var t = new Thread(RemoteAccept) { IsBackground = true, Name = "remote" };
            t.Start(remoteListener);
        }

        static void StopRemote()
        {
            lock (remoteLock)
            {
                if (remoteListener == null) return;
                try { remoteListener.Stop(); } catch { }
                remoteListener = null;
            }
        }

        static void RemoteAccept(object state)
        {
            var l = (TcpListener)state;
            while (true)
            {
                TcpClient client;
                try { client = l.AcceptTcpClient(); }
                catch { return; } // stopped
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try { RemoteConnection(client); }
                    catch { }
                    finally { try { client.Close(); } catch { } }
                });
            }
        }

        // Home networks, VPNs (Tailscale's 100.64.0.0/10) and this PC; never a public address.
        static bool PrivateAddress(IPAddress a)
        {
            if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
            if (IPAddress.IsLoopback(a)) return true;
            byte[] b = a.GetAddressBytes();
            if (a.AddressFamily == AddressFamily.InterNetwork)
                return b[0] == 10 || (b[0] == 172 && (b[1] & 0xF0) == 16) || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && (b[1] & 0xC0) == 64);
            if (a.AddressFamily == AddressFamily.InterNetworkV6)
                return a.IsIPv6LinkLocal || (b[0] & 0xFE) == 0xFC; // fe80::/10, fc00::/7 (Tailscale's fd7a:115c:a1e0::/48 too)
            return false;
        }

        // One request per connection: read it, answer it (the gate's own pages, or the app's server through
        // the gate), close. Browsers open a new connection for the next one.
        static void RemoteConnection(TcpClient client)
        {
            var ep = client.Client.RemoteEndPoint as IPEndPoint;
            if (ep == null || !PrivateAddress(ep.Address) || !remoteEnabled) return;
            client.NoDelay = true;
            client.ReceiveTimeout = 60000;
            client.SendTimeout = 60000;
            NetworkStream cs = client.GetStream();

            byte[] extra; int extraLen;
            string head = ReadHttpHead(cs, out extra, out extraLen);
            if (head == null) return;
            string[] lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] start = lines[0].Split(' ');
            if (start.Length != 3 || !start[1].StartsWith("/")) { RemoteReply(cs, 400, "text/plain", "Bad request"); return; }
            string method = start[0], target = start[1];
            var headers = new List<KeyValuePair<string, string>>();
            foreach (string line in lines.Skip(1))
            {
                int c = line.IndexOf(':');
                if (c > 0) headers.Add(new KeyValuePair<string, string>(line.Substring(0, c).Trim(), line.Substring(c + 1).Trim()));
            }
            Func<string, string> header = n => headers.Where(h => h.Key.Equals(n, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value).FirstOrDefault();
            string pathOnly = target.Split('?')[0];

            long contentLength = 0;
            if (header("Transfer-Encoding") != null) { RemoteReply(cs, 411, "text/plain", "Length required"); return; }
            if (header("Content-Length") != null && (!long.TryParse(header("Content-Length"), out contentLength) || contentLength < 0)) { RemoteReply(cs, 400, "text/plain", "Bad request"); return; }

            Dictionary<string, object> device = RemoteDeviceFor(header("Cookie"));
            if (pathOnly == "/remote/pair" && method == "POST")
            {
                if (contentLength > 4096) { RemoteReply(cs, 413, "text/plain", "Too large"); return; }
                string form = Encoding.UTF8.GetString(ReadBodyBytes(cs, extra, extraLen, (int)contentLength));
                RemotePair(cs, form, header("User-Agent"));
                return;
            }
            if (device == null)
            {
                if (pathOnly.StartsWith("/api/")) RemoteReply(cs, 401, "application/json", "{\"error\":\"This device isn't paired with Minecraft Mod Manager. Reload the page to pair it.\"}");
                else RemoteReply(cs, 200, "text/html; charset=utf-8", PairPage(null));
                return;
            }
            if (pathOnly == "/remote/forget")
            {
                RemoteReply(cs, 303, "text/plain", "", "Location: /\r\nSet-Cookie: " + RemoteCookie + "=; Max-Age=0; Path=/; HttpOnly; SameSite=Strict\r\n");
                return;
            }
            NoteRemoteSeen(device);

            // Through to the app's own server, as a local request.
            using (var upstream = new TcpClient())
            {
                upstream.NoDelay = true;
                upstream.Connect(IPAddress.Loopback, port);
                NetworkStream us = upstream.GetStream();
                var sb = new StringBuilder();
                sb.Append(method).Append(' ').Append(target).Append(" HTTP/1.1\r\n");
                sb.Append("Host: localhost:").Append(port).Append("\r\n");
                foreach (var h in headers)
                {
                    string k = h.Key.ToLowerInvariant();
                    if (k == "host" || k == "origin" || k == "referer" || k == "cookie" || k == "connection" || k == "keep-alive" || k == "upgrade"
                        || k == "x-mod-manager-remote" || k.StartsWith("proxy-")) continue;
                    sb.Append(h.Key).Append(": ").Append(h.Value).Append("\r\n");
                }
                if (header("Origin") != null) sb.Append("Origin: ").Append(origin).Append("\r\n");
                sb.Append("X-Mod-Manager-Remote: ").Append(Uri.EscapeDataString(Str(device, "name") ?? "device")).Append("\r\n");
                sb.Append("Connection: close\r\n\r\n");
                byte[] hb = Encoding.UTF8.GetBytes(sb.ToString());
                us.Write(hb, 0, hb.Length);
                // The body: what came with the head, then the rest straight from the connection.
                long left = contentLength;
                int first = (int)Math.Min(left, extraLen);
                if (first > 0) { us.Write(extra, 0, first); left -= first; }
                var buf = new byte[65536];
                while (left > 0)
                {
                    int n = cs.Read(buf, 0, (int)Math.Min(buf.Length, left));
                    if (n <= 0) return;
                    us.Write(buf, 0, n);
                    left -= n;
                }
                // The answer goes back as it comes, with "Connection: close" so the browser doesn't wait for more.
                byte[] rextra; int rextraLen;
                string rhead = ReadHttpHead(us, out rextra, out rextraLen);
                if (rhead == null) return;
                var rsb = new StringBuilder();
                foreach (string line in rhead.Split(new[] { "\r\n" }, StringSplitOptions.None))
                {
                    if (line.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Keep-Alive:", StringComparison.OrdinalIgnoreCase)) continue;
                    rsb.Append(line).Append("\r\n");
                }
                rsb.Append("Connection: close\r\n\r\n");
                byte[] rhb = Encoding.UTF8.GetBytes(rsb.ToString());
                cs.Write(rhb, 0, rhb.Length);
                if (rextraLen > 0) cs.Write(rextra, 0, rextraLen);
                int r;
                while ((r = us.Read(buf, 0, buf.Length)) > 0) cs.Write(buf, 0, r);
            }
        }

        // Reads up to the blank line that ends an HTTP head (at most 64 KB); what came after it is returned too.
        static string ReadHttpHead(Stream s, out byte[] extra, out int extraLen)
        {
            var buf = new byte[65536];
            int len = 0;
            extra = new byte[0]; extraLen = 0;
            while (len < buf.Length)
            {
                int n;
                try { n = s.Read(buf, len, buf.Length - len); } catch { return null; }
                if (n <= 0) return null;
                int from = Math.Max(0, len - 3);
                len += n;
                for (int i = from; i + 3 < len; i++)
                {
                    if (buf[i] == '\r' && buf[i + 1] == '\n' && buf[i + 2] == '\r' && buf[i + 3] == '\n')
                    {
                        extraLen = len - (i + 4);
                        extra = new byte[extraLen];
                        Array.Copy(buf, i + 4, extra, 0, extraLen);
                        return Encoding.UTF8.GetString(buf, 0, i);
                    }
                }
            }
            return null;
        }

        static byte[] ReadBodyBytes(Stream s, byte[] extra, int extraLen, int length)
        {
            var body = new byte[length];
            int have = Math.Min(length, extraLen);
            Array.Copy(extra, body, have);
            while (have < length)
            {
                int n = s.Read(body, have, length - have);
                if (n <= 0) break;
                have += n;
            }
            return body;
        }

        static void RemoteReply(Stream s, int status, string type, string text, string extraHeaders = "")
        {
            byte[] body = Encoding.UTF8.GetBytes(text);
            string reason = status == 200 ? "OK" : status == 303 ? "See Other" : status == 401 ? "Unauthorized" : status == 403 ? "Forbidden" : "Error";
            string head = "HTTP/1.1 " + status + " " + reason + "\r\nContent-Type: " + type + "\r\nContent-Length: " + body.Length +
                "\r\nCache-Control: no-store\r\nX-Frame-Options: DENY\r\n" + extraHeaders + "Connection: close\r\n\r\n";
            byte[] hb = Encoding.UTF8.GetBytes(head);
            s.Write(hb, 0, hb.Length);
            s.Write(body, 0, body.Length);
        }

        static string HashKey(string key)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(key))).Replace("-", "").ToLowerInvariant();
        }

        static Dictionary<string, object> RemoteDeviceFor(string cookieHeader)
        {
            if (string.IsNullOrEmpty(cookieHeader)) return null;
            string key = null;
            foreach (string part in cookieHeader.Split(';'))
            {
                string p = part.Trim();
                if (p.StartsWith(RemoteCookie + "=")) key = p.Substring(RemoteCookie.Length + 1);
            }
            if (string.IsNullOrEmpty(key) || key.Length < 32) return null;
            byte[] hash = Encoding.ASCII.GetBytes(HashKey(key));
            lock (remoteLock)
            {
                foreach (var d in remoteDevices)
                {
                    byte[] other = Encoding.ASCII.GetBytes(Str(d, "hash") ?? "");
                    if (other.Length != hash.Length) continue;
                    int diff = 0; // same time whatever matches, so the hash can't be guessed byte by byte
                    for (int i = 0; i < hash.Length; i++) diff |= hash[i] ^ other[i];
                    if (diff == 0) return d;
                }
            }
            return null;
        }

        static void NoteRemoteSeen(Dictionary<string, object> device)
        {
            lock (remoteLock) device["seen"] = DateTime.UtcNow.ToString("o");
            if (DateTime.UtcNow - remoteSeenSaved > TimeSpan.FromHours(1)) { remoteSeenSaved = DateTime.UtcNow; SaveSettings(); }
        }

        static void RemotePair(Stream s, string form, string userAgent)
        {
            string code = null, name = null;
            foreach (string kv in form.Split('&'))
            {
                int eq = kv.IndexOf('=');
                if (eq < 0) continue;
                string k = Uri.UnescapeDataString(kv.Substring(0, eq).Replace('+', ' '));
                string v = Uri.UnescapeDataString(kv.Substring(eq + 1).Replace('+', ' ')).Trim();
                if (k == "code") code = new string(v.Where(char.IsDigit).ToArray());
                else if (k == "name") name = v;
            }
            string key = null;
            lock (remoteLock)
            {
                bool live = pairCode != null && DateTime.UtcNow < pairExpires;
                if (!live) { RemoteReply(s, 200, "text/html; charset=utf-8", PairPage("No code is waiting. On the PC, open Settings > Remote access and choose Pair a device.")); return; }
                if (code != pairCode)
                {
                    pairTries++;
                    if (pairTries >= 5) pairCode = null; // too many guesses: the code is spent
                    RemoteReply(s, 200, "text/html; charset=utf-8", PairPage(pairCode == null ? "Too many wrong tries. Make a new code on the PC." : "That code isn't right. Check it on the PC and try again."));
                    return;
                }
                pairCode = null;
                byte[] raw = new byte[32];
                using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(raw);
                key = Convert.ToBase64String(raw).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                if (string.IsNullOrEmpty(name)) name = GuessDeviceName(userAgent);
                if (name.Length > 40) name = name.Substring(0, 40);
                remoteDevices.Add(Obj("id", Guid.NewGuid().ToString("N").Substring(0, 12), "name", name, "hash", HashKey(key),
                    "added", DateTime.UtcNow.ToString("o"), "seen", DateTime.UtcNow.ToString("o")));
            }
            SaveSettings();
            Log("Remote access: paired " + name);
            if (window != null && !window.IsDisposed) try { window.BeginInvoke((Action)(() => { if (!window.IsDisposed) window.Notify("refresh"); })); } catch { }
            RemoteReply(s, 303, "text/plain", "", "Location: /\r\nSet-Cookie: " + RemoteCookie + "=" + key + "; Max-Age=31536000; Path=/; HttpOnly; SameSite=Strict\r\n");
        }

        static string GuessDeviceName(string ua)
        {
            ua = ua ?? "";
            if (ua.Contains("iPhone")) return "iPhone";
            if (ua.Contains("iPad")) return "iPad";
            if (ua.Contains("Android")) return ua.Contains("Mobile") ? "Android phone" : "Android tablet";
            if (ua.Contains("Mac OS")) return "Mac";
            if (ua.Contains("Windows")) return "Windows PC";
            return "Device";
        }

        static string PairPage(string message)
        {
            string msg = message == null ? "" : "<p class=\"msg\">" + WebUtility.HtmlEncode(message) + "</p>";
            return "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">" +
                "<title>Pair with Minecraft Mod Manager</title><style>" +
                ":root{color-scheme:dark light}body{margin:0;min-height:100vh;display:grid;place-items:center;background:#15181c;color:#e8eaed;font:16px/1.45 system-ui,-apple-system,Segoe UI,sans-serif}" +
                "main{width:min(380px,100% - 32px);padding:24px;border:1px solid #2c3138;border-radius:10px;background:#1c2026}" +
                "h1{font-size:20px;margin:0 0 8px}p{margin:0 0 16px;color:#a8b0ba}label{display:block;font-weight:600;margin:12px 0 6px}" +
                "input{box-sizing:border-box;width:100%;padding:12px;border-radius:6px;border:1px solid #39404a;background:#111418;color:inherit;font:inherit}" +
                "input.code{font:600 26px/1 ui-monospace,Consolas,monospace;letter-spacing:.2em;text-align:center}" +
                "button{margin-top:18px;width:100%;padding:12px;border:0;border-radius:6px;background:#1ec85a;color:#06210f;font:700 16px system-ui,sans-serif}" +
                ".msg{color:#ff8a7a}@media (prefers-color-scheme:light){body{background:#eef1f4;color:#15181c}main{background:#fff;border-color:#d5dbe1}input{background:#f7f9fa;border-color:#c5ccd3}p{color:#4c5560}}" +
                "</style></head><body><main><h1>Pair this device</h1>" +
                "<p>On the PC, open Minecraft Mod Manager, go to Settings &gt; Remote access and choose <b>Pair a device</b>. Type the code it shows.</p>" + msg +
                "<form method=\"post\" action=\"/remote/pair\"><label for=\"code\">Code</label>" +
                "<input id=\"code\" class=\"code\" name=\"code\" inputmode=\"numeric\" autocomplete=\"one-time-code\" maxlength=\"9\" required autofocus>" +
                "<label for=\"name\">Name for this device (optional)</label><input id=\"name\" name=\"name\" maxlength=\"40\" placeholder=\"e.g. My phone\">" +
                "<button type=\"submit\">Pair</button></form></main></body></html>";
        }
    }
}
