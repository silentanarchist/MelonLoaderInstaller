using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace WFRevivalPcPatcher;

/// <summary>
/// Sends the patched files to Husk on an iPhone over the local network, the way Husk's own tools/serve-folder.py does:
/// GET / is the manifest (JSON: {"name", "files": [{"path", "size"}]}), GET /f/&lt;path&gt; one file, with Range requests
/// honoured so a dropped download resumes. Husk (Downloads tab) puts every file in its Shared Storage (/sdcard for the
/// game) at the same path, so the mod is served as MelonLoader/&lt;package&gt;/Mods/&lt;name&gt; and lands where the loader
/// reads it. The game goes as the one .apks bundle (each single .apk would become an app of its own in Husk). A browser
/// (Accept: text/html) gets a plain page with download links instead, as a fallback.
/// Only the listed files are served; nothing else on the PC can be reached. Runs until Enter is pressed.
/// </summary>
internal static class LanShare
{
    private const string Package = "com.Everguild.WarhammerWarpforge";
    private const int FirstPort = 8642;   // Husk's serve-folder.py default

    public static void Run(string folder, Action<string> say)
    {
        // what goes where on the iPhone (path in Husk's Shared Storage -> file on this PC)
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string f in Directory.GetFiles(folder))
        {
            string name = Path.GetFileName(f);
            if (name.EndsWith(".apks", StringComparison.OrdinalIgnoreCase)) files[name] = f;
            else if (name.Equals("WFRevival.Android.dll", StringComparison.OrdinalIgnoreCase) || name.Equals("WarpforgeRevival.Android.dll", StringComparison.OrdinalIgnoreCase))
                files[$"MelonLoader/{Package}/Mods/{name}"] = f;
            else if (name.Equals(Program.ModConfigName, StringComparison.OrdinalIgnoreCase))
                files[$"MelonLoader/{Package}/UserData/{name}"] = f;
        }
        if (files.Count == 0) { say("Nothing to send in that folder (no .apks bundle or mod file)."); return; }

        TcpListener? listener = null;
        int port = FirstPort;
        for (; port < FirstPort + 20; port++)
        {
            try { listener = new TcpListener(IPAddress.Any, port); listener.Start(); break; }
            catch (SocketException) { listener = null; }
        }
        if (listener == null) { say("Could not open a network port to send from (8642-8661 are all in use)."); return; }

        var addresses = LocalAddresses();
        say("");
        say("Sending over Wi-Fi. On the iPhone (same Wi-Fi as this PC):");
        say("  1. Open Husk > Downloads and enter this address:");
        foreach (var a in addresses) say($"       http://{a}:{port}");
        if (addresses.Count == 0) say($"       http://<this PC's address>:{port}   (no network address found: is the PC on Wi-Fi or a network cable?)");
        if (addresses.Count > 1) say("     (more than one address: try the first; the one starting 192.168. is usually right)");
        say("  2. If Windows asks whether to allow network access, choose Allow for private networks.");
        say("     If iOS asks whether Husk may find devices on your local network, tap Allow.");
        say("  Fallback: open the same address in Safari for plain download links.");
        say("");
        say("Files:");
        foreach (var (path, file) in files) say($"  {path}  ({new FileInfo(file).Length / 1048576.0:F1} MB)");
        say("");
        say("Leave this window open until Husk has everything, then press Enter to stop sending.");

        var stop = new CancellationTokenSource();
        var shareName = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar));
        var loop = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(stop.Token); }
                catch { break; }
                _ = Task.Run(() => Serve(client, shareName, files, say));
            }
        });
        Console.ReadLine();
        stop.Cancel();
        listener.Stop();
        try { loop.Wait(2000); } catch { }
        say("Stopped sending.");
    }

    private static void Serve(TcpClient client, string name, SortedDictionary<string, string> files, Action<string> say)
    {
        string who = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 30000;
                client.SendTimeout = 120000;
                var stream = client.GetStream();
                // one request per connection (Connection: close), which is all Husk and a browser need
                var head = ReadHead(stream);
                if (head == null) return;
                string[] first = head[0].Split(' ');
                if (first.Length < 2) { Send(stream, 400, "Bad Request", "text/plain", "bad request"); return; }
                string method = first[0].ToUpperInvariant();
                bool isHead = method == "HEAD";
                if (method != "GET" && !isHead) { Send(stream, 405, "Method Not Allowed", "text/plain", "only GET"); return; }
                string target = first[1];
                int q = target.IndexOf('?');
                string path = Uri.UnescapeDataString(q >= 0 ? target[..q] : target);
                var headers = head.Skip(1).Select(l => l.Split(':', 2)).Where(p => p.Length == 2)
                    .GroupBy(p => p[0].Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First()[1].Trim(), StringComparer.OrdinalIgnoreCase);

                if (path is "/" or "/manifest.json" or "/index.html")
                {
                    bool browser = path == "/index.html" || (path == "/" && headers.TryGetValue("Accept", out var acc) && acc.Contains("text/html"));
                    if (browser) Send(stream, 200, "OK", "text/html; charset=utf-8", Page(name, files), isHead);
                    else
                    {
                        var manifest = new { name, files = files.Select(f => new { path = f.Key, size = new FileInfo(f.Value).Length }).ToArray() };
                        Send(stream, 200, "OK", "application/json", JsonSerializer.Serialize(manifest), isHead);
                        say($"  {who} asked for the file list");
                    }
                    return;
                }
                if (!path.StartsWith("/f/") || !files.TryGetValue(path[3..], out var file))
                {
                    Send(stream, 404, "Not Found", "text/plain", "not found", isHead);
                    return;
                }
                long size = new FileInfo(file).Length, start = 0, end = size - 1;
                bool partial = false;
                if (headers.TryGetValue("Range", out var range) && range.StartsWith("bytes="))
                {
                    string[] ab = range[6..].Split(',')[0].Split('-', 2);
                    if (ab[0].Length > 0) { start = long.Parse(ab[0]); if (ab.Length > 1 && ab[1].Length > 0) end = long.Parse(ab[1]); }
                    else if (ab.Length > 1 && ab[1].Length > 0) start = Math.Max(0, size - long.Parse(ab[1]));
                    if (start >= size || start > end)
                    {
                        Write(stream, $"HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */{size}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                        return;
                    }
                    end = Math.Min(end, size - 1);
                    partial = true;
                }
                long length = end - start + 1;
                var sb = new StringBuilder();
                sb.Append(partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
                sb.Append("Content-Type: application/octet-stream\r\n");
                sb.Append($"Content-Length: {length}\r\n");
                sb.Append("Accept-Ranges: bytes\r\n");
                if (partial) sb.Append($"Content-Range: bytes {start}-{end}/{size}\r\n");
                sb.Append($"Content-Disposition: attachment; filename=\"{Path.GetFileName(file)}\"\r\n");
                sb.Append("Connection: close\r\n\r\n");
                Write(stream, sb.ToString());
                if (isHead) return;
                if (start == 0) say($"  {who} is downloading {Path.GetFileName(file)}" + (partial ? "" : "") + "...");
                else say($"  {who} is resuming {Path.GetFileName(file)} from {start / 1048576} MB...");
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
                fs.Position = start;
                var buffer = new byte[1 << 20];
                long left = length;
                while (left > 0)
                {
                    int n = fs.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                    if (n <= 0) break;
                    stream.Write(buffer, 0, n);
                    left -= n;
                }
                if (end == size - 1) say($"  {who} has {Path.GetFileName(file)}");
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
            {
                // the iPhone closed the connection (it resumes by itself)
            }
        }
    }

    /// <summary>The request line and headers, or null.</summary>
    private static List<string>? ReadHead(NetworkStream stream)
    {
        var bytes = new List<byte>();
        int b;
        while (bytes.Count < 16384 && (b = stream.ReadByte()) >= 0)
        {
            bytes.Add((byte)b);
            int c = bytes.Count;
            if (c >= 4 && bytes[c - 4] == '\r' && bytes[c - 3] == '\n' && bytes[c - 2] == '\r' && bytes[c - 1] == '\n') break;
        }
        if (bytes.Count == 0) return null;
        var lines = Encoding.ASCII.GetString(bytes.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries).ToList();
        return lines.Count == 0 ? null : lines;
    }

    private static void Send(NetworkStream stream, int code, string reason, string type, string body, bool headOnly = false)
    {
        byte[] data = Encoding.UTF8.GetBytes(body);
        Write(stream, $"HTTP/1.1 {code} {reason}\r\nContent-Type: {type}\r\nContent-Length: {data.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        if (!headOnly) stream.Write(data);
    }

    private static void Write(NetworkStream stream, string text) => stream.Write(Encoding.ASCII.GetBytes(text));

    private static string Page(string name, SortedDictionary<string, string> files)
    {
        var sb = new StringBuilder();
        sb.Append("<!doctype html><meta charset=utf-8><meta name=viewport content=\"width=device-width,initial-scale=1\"><title>WF Revival</title>");
        sb.Append("<style>body{font:17px -apple-system,sans-serif;margin:20px;background:#16181b;color:#e6e6e6}a{color:#c9a44c;display:block;margin:14px 0}small{color:#9aa0a6}</style>");
        sb.Append($"<h2>WF Revival: {WebUtility.HtmlEncode(name)}</h2>");
        sb.Append("<p><small>Easiest: enter this page's address in Husk &gt; Downloads instead; it puts the mod in the right folder by itself. These links save to the Files app.</small></p>");
        foreach (var (path, file) in files)
            sb.Append($"<a href=\"/f/{string.Join('/', path.Split('/').Select(Uri.EscapeDataString))}\">{WebUtility.HtmlEncode(Path.GetFileName(path))}</a><small>{new FileInfo(file).Length / 1048576.0:F1} MB &middot; goes in /sdcard/{WebUtility.HtmlEncode(Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "")}</small>");
        return sb.ToString();
    }

    /// <summary>This PC's IPv4 addresses on the local network, the likeliest first (Wi-Fi/Ethernet with a gateway, 192.168.x.x).</summary>
    private static List<string> LocalAddresses()
    {
        var found = new List<(string ip, int score)>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var props = ni.GetIPProperties();
                bool gateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                string desc = (ni.Description + " " + ni.Name).ToLowerInvariant();
                bool virtualish = desc.Contains("virtual") || desc.Contains("vmware") || desc.Contains("hyper-v") || desc.Contains("vethernet") || desc.Contains("wsl") || desc.Contains("docker") || desc.Contains("vpn") || desc.Contains("tailscale") || desc.Contains("zerotier");
                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    byte[] b = ua.Address.GetAddressBytes();
                    if (b[0] == 127 || (b[0] == 169 && b[1] == 254)) continue;
                    bool privateNet = b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
                    int score = (gateway ? 4 : 0) + (privateNet ? 2 : 0) + (b[0] == 192 && b[1] == 168 ? 1 : 0) - (virtualish ? 8 : 0);
                    found.Add((ua.Address.ToString(), score));
                }
            }
        }
        catch { }
        return found.OrderByDescending(f => f.score).Select(f => f.ip).Distinct().ToList();
    }
}
