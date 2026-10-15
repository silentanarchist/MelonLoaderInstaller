using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Android.Graphics;
using Android.Media;

namespace WarpforgePatcher;

/// <summary>What "3 - put mod on phone.bat", "6 - fix loading video.bat" (the Still picture) and "7 - loading video setting.bat" do, on the phone.</summary>
internal static class ModFiles
{
    public const string DefaultServer = "wf-revival.silentanarchist.com";   // the mod's RevivalConfig.DefaultServer (mod 0.13.28 on)
    /// <summary>The community server's earlier name (the mod's RevivalConfig.OldServer): the same server. A settings file that
    /// still names it counts as the community server; the new name is asked first and the old one only if it does not answer.</summary>
    public const string OldServer = "warpforge.silentanarchist.com";

    /// <summary>True when the address is blank or names the community server by its new or its old name.</summary>
    public static bool IsCommunityServer(string? value)
    {
        string t = (value ?? "").Trim().TrimEnd('/').ToLowerInvariant();
        foreach (var prefix in new[] { "https://", "http://" }) if (t.StartsWith(prefix)) t = t.Substring(prefix.Length);
        return t.Length == 0 || t == DefaultServer || t == OldServer;
    }
    public const int Port = 8780;

    public static string ModsDir => System.IO.Path.Combine(GameFiles.LoaderDir, "Mods");
    public static string ModFile => System.IO.Path.Combine(ModsDir, "WarpforgeRevival.Android.dll");
    public static string ConfigFile => System.IO.Path.Combine(GameFiles.LoaderDir, "UserData", "WarpforgeRevival.cfg");
    public static string StillFile => System.IO.Path.Combine(GameFiles.LoaderDir, "UserData", "WarpforgeRevival", "content", "intro-last.rgba");
    public static string LatestLog => System.IO.Path.Combine(GameFiles.LoaderDir, "Latest.log");

    /// <summary>The server the mod uses: ServerUrl from its settings file when set, else the community server. Always https.</summary>
    public static string ServerBase() => ServerBases()[0];

    /// <summary>The addresses to try, in order: the configured server, or the community server's new name and then its old one.</summary>
    public static string[] ServerBases()
    {
        string configured = "";
        try
        {
            if (File.Exists(ConfigFile))
            {
                var m = Regex.Match(File.ReadAllText(ConfigFile), "(?m)^\\s*ServerUrl\\s*=\\s*\"([^\"]*)\"");
                if (m.Success) configured = m.Groups[1].Value;
            }
        }
        catch { }
        return IsCommunityServer(configured)
            ? new[] { Https(DefaultServer), Https(OldServer) }
            : new[] { Https(configured) };
    }

    private static string Https(string written)
    {
        string value = written.Trim().TrimEnd('/');
        if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return value;
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) value = value.Substring(7);
        // like the mod: the game port unless one is written (this app never falls back to plain http)
        string host = value.Split('/')[0];
        bool hasPort = host.LastIndexOf(':') > host.LastIndexOf(']');
        return "https://" + (hasPort ? value : host + ":" + Port + value.Substring(host.Length));
    }

    /// <summary>Downloads the current Android mod and checks it against the server's checksum.</summary>
    public static async Task<(string path, string version)> DownloadMod(string workDir, Action<string> log)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("WarpforgePatcher/1");
        string server = "", manifestText = "";
        var servers = ServerBases();
        for (int i = 0; i < servers.Length; i++)
        {
            server = servers[i];
            log($"Asking {server} for the current mod");
            try { manifestText = await http.GetStringAsync(server + "/api/v1/mod/manifest?platform=android"); break; }
            catch (Exception e) when (i + 1 < servers.Length && e is HttpRequestException or TaskCanceledException)
            {
                log($"{server} did not answer ({e.Message}); trying the server's earlier name");
            }
        }
        using var manifest = JsonDocument.Parse(manifestText);
        var root = manifest.RootElement;
        if (!root.TryGetProperty("available", out var available) || !available.GetBoolean())
            throw new UserError("The server has no Android mod to hand out right now.");
        string version = root.TryGetProperty("label", out var v) ? v.GetString() ?? "" : (root.TryGetProperty("version", out var v2) ? v2.GetString() ?? "" : "");
        string sha = (root.GetProperty("sha256").GetString() ?? "").ToLowerInvariant();
        string url = root.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
        if (url.Length == 0) url = "/api/v1/mod/download?platform=android";
        if (!url.StartsWith("/", StringComparison.Ordinal)) throw new UserError("The server's mod address is not on the server itself; refused.");
        byte[] data = await http.GetByteArrayAsync(server + url);
        string got = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        if (sha.Length != 64 || got != sha)
            throw new UserError("The downloaded mod does not match the server's checksum; nothing was changed.");
        string path = System.IO.Path.Combine(workDir, "WarpforgeRevival.Android.dll");
        await File.WriteAllBytesAsync(path, data);
        log($"Mod {version} downloaded ({data.Length / 1024} KB, checksum OK)");
        return (path, version);
    }

    /// <summary>Puts the mod in the loader's Mods folder; older test files are moved aside as the PC script does.</summary>
    public static void PutMod(string downloaded, Action<string> log)
    {
        string loader = GameFiles.LoaderDir;
        Directory.CreateDirectory(ModsDir);
        MoveAside(System.IO.Path.Combine(loader, "Mods", "WarpforgeRevival.AndroidTest.dll"), System.IO.Path.Combine(loader, "Mods-disabled"), log);
        MoveAside(System.IO.Path.Combine(loader, "Plugins", "WarpforgeRevival.AndroidGraphics.dll"), System.IO.Path.Combine(loader, "Plugins-disabled"), log);
        string temp = ModFile + ".new";
        File.Copy(downloaded, temp, true);
        File.Move(temp, ModFile, true);
        log("Mod put in " + ModsDir);
    }

    private static void MoveAside(string file, string dir, Action<string> log)
    {
        if (!File.Exists(file)) return;
        Directory.CreateDirectory(dir);
        File.Move(file, System.IO.Path.Combine(dir, System.IO.Path.GetFileName(file)), true);
        log($"Moved {System.IO.Path.GetFileName(file)} aside to {dir}");
    }

    // ---- the Still picture ("Fix black menu after loading")

    /// <summary>
    /// The loading clip's last frame as the mod's Still picture: 1440x810, raw RGBA, bottom row first (4,665,600 bytes).
    /// The clip is a plain MP4 inside assets/bin/Data/sharedassets0.resource of the game's main APK (tool/extract-intro.ps1).
    /// </summary>
    public static byte[] MakeStill(string baseApk, string workDir, Action<string> log)
    {
        byte[] data;
        using (var zip = ZipFile.OpenRead(baseApk))
        {
            var entry = zip.GetEntry("assets/bin/Data/sharedassets0.resource") ?? throw new UserError("The game file has no loading video (sharedassets0.resource).");
            using var s = entry.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            data = ms.ToArray();
        }
        var (start, length) = FindMp4(data);
        string clip = System.IO.Path.Combine(workDir, "intro.mp4");
        using (var f = File.Create(clip)) f.Write(data, (int)start, (int)length);
        log($"Loading video found in the game ({length / 1048576.0:F1} MB)");

        using var retriever = new MediaMetadataRetriever();
        retriever.SetDataSource(clip);
        long durationMs = long.TryParse(retriever.ExtractMetadata(MetadataKey.Duration), out long d) ? d : 0;
        if (durationMs <= 0) throw new UserError("Could not read the loading video's length.");
        using var frame = retriever.GetFrameAtTime(Math.Max(0, durationMs - 200) * 1000, Option.Closest)
                          ?? throw new UserError("Could not take the loading video's last frame.");
        using var scaled = Bitmap.CreateScaledBitmap(frame, 1440, 810, true)!;
        using var argb = scaled.GetConfig() == Bitmap.Config.Argb8888 ? null : scaled.Copy(Bitmap.Config.Argb8888!, false);
        var bmp = argb ?? scaled;
        int w = bmp.Width, h = bmp.Height, row = w * 4;
        var buffer = Java.Nio.ByteBuffer.Allocate(row * h);
        bmp.CopyPixelsToBuffer(buffer);      // ARGB_8888 is stored as R,G,B,A bytes
        buffer.Rewind();
        byte[] top = new byte[row * h];
        buffer.Get(top);
        byte[] rgba = new byte[row * h];
        for (int y = 0; y < h; y++)           // bottom row first, as Unity's textures are laid out
            Buffer.BlockCopy(top, y * row, rgba, (h - 1 - y) * row, row);
        for (int i = 3; i < rgba.Length; i += 4) rgba[i] = 255;
        try { File.Delete(clip); } catch { }
        log("Still picture made (1440x810)");
        return rgba;
    }

    private static (long start, long length) FindMp4(byte[] data)
    {
        int at = IndexOf(data, System.Text.Encoding.ASCII.GetBytes("ftyp"));
        if (at < 4) throw new UserError("No loading video found in the game file.");
        long start = at - 4, pos = start;
        bool sawMoov = false;
        while (pos + 8 <= data.Length)
        {
            long size = U32(data, pos);
            string type = System.Text.Encoding.ASCII.GetString(data, (int)pos + 4, 4);
            if (size == 1 && pos + 16 <= data.Length) size = (U32(data, pos + 8) << 32) + U32(data, pos + 12);
            if (size < 8 || !Regex.IsMatch(type, "^[a-z]{4}$") || pos + size > data.Length) break;
            if (type == "moov") sawMoov = true;
            pos += size;
        }
        long length = pos - start;
        if (!sawMoov || length < 100000) throw new UserError("The loading video in the game file looks incomplete.");
        return (start, length);
    }

    private static long U32(byte[] a, long o) => ((long)a[o] << 24) | ((long)a[o + 1] << 16) | ((long)a[o + 2] << 8) | a[o + 3];

    private static int IndexOf(byte[] data, byte[] needle)
    {
        for (int i = 0; i <= data.Length - needle.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && data[i + j] == needle[j]) j++;
            if (j == needle.Length) return i;
        }
        return -1;
    }

    public static void PutStill(byte[] rgba, Action<string> log)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(StillFile)!);
        File.WriteAllBytes(StillFile, rgba);
        log("Still picture put in " + System.IO.Path.GetDirectoryName(StillFile));
    }

    /// <summary>Sets one key of the mod's settings file (MelonPreferences TOML, category [WarpforgeRevival]); everything else is kept.</summary>
    public static void SetSetting(string key, string value, Action<string> log)
    {
        string line = $"{key} = \"{value}\"";
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ConfigFile)!);
        string text = File.Exists(ConfigFile) ? File.ReadAllText(ConfigFile) : "";
        string nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var keyLine = new Regex("(?m)^([ \\t]*" + Regex.Escape(key) + "[ \\t]*=[ \\t]*).*?(\\r?)$");
        if (keyLine.IsMatch(text))
            text = keyLine.Replace(text, m => m.Groups[1].Value + "\"" + value + "\"" + m.Groups[2].Value, 1);
        else
        {
            var header = new Regex("(?m)^\\[WarpforgeRevival\\][ \\t]*\\r?$");
            var h = header.Match(text);
            if (h.Success)
                text = text.Insert(h.Index + h.Length, nl + line);
            else
                text = (text.Length > 0 ? text.TrimEnd() + nl + nl : "") + "[WarpforgeRevival]" + nl + line + nl;
        }
        File.WriteAllText(ConfigFile, text);
        log($"Mod setting {key} = {value}");
    }

    public static string? CurrentSetting(string key)
    {
        try
        {
            if (!File.Exists(ConfigFile)) return null;
            var m = Regex.Match(File.ReadAllText(ConfigFile), "(?m)^\\s*" + Regex.Escape(key) + "\\s*=\\s*\"?([^\"\\r\\n]*)\"?");
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }
        catch { return null; }
    }

    /// <summary>The card art the mod exports in the game (Settings > Mod > Export card art, CardArtExport.cs).</summary>
    public static string ArtExport => System.IO.Path.Combine(GameFiles.LoaderDir, "UserData", "WarpforgeRevival", "card-art-export.zip");

    /// <summary>
    /// Copies the mod's card art export to the phone's Download folder so it is easy to find. Deliberately no share
    /// option (owner, 2026-10-13): the pictures are the game's art; what the player does with the file is up to them.
    /// </summary>
    public static string SaveArtExport(Action<string> log)
    {
        var info = new FileInfo(ArtExport);
        if (!info.Exists) throw new UserError("There is no card art export yet. In the game: Settings > Mod > Export card art, then come back.");
        string dir = Android.OS.Environment.GetExternalStoragePublicDirectory(Android.OS.Environment.DirectoryDownloads)!.AbsolutePath;
        string target = System.IO.Path.Combine(dir, $"Warpforge card art {info.LastWriteTime:yyyy-MM-dd HHmm}.zip");
        long free = new Android.OS.StatFs(dir).AvailableBytes;
        if (free < info.Length + 20L * 1048576)
            throw new UserError($"Not enough free space: {info.Length / 1048576} MB needed, {free / 1048576} MB free.");
        string temp = target + ".part";
        File.Copy(ArtExport, temp, true);
        if (new FileInfo(temp).Length != info.Length) { File.Delete(temp); throw new UserError("The copy came out incomplete; try again."); }
        File.Move(temp, target, true);
        log($"Card art export saved to {target} ({info.Length / 1048576} MB)");
        return target;
    }

    /// <summary>One zip with the loader's log, the mod's settings and this app's own log, for sharing. Never the game login.</summary>
    public static string BundleLogs(string cacheDir, string patcherLog)
    {
        string dir = System.IO.Path.Combine(cacheDir, "share");
        Directory.CreateDirectory(dir);
        string zipPath = System.IO.Path.Combine(dir, $"warpforge-logs-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        foreach (var old in Directory.GetFiles(dir, "*.zip")) try { File.Delete(old); } catch { }
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        void Add(string path, string name)
        {
            if (!File.Exists(path)) return;
            var e = zip.CreateEntry(name);
            using var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var dst = e.Open();
            src.CopyTo(dst);
        }
        Add(LatestLog, "Latest.log");
        Add(ConfigFile, "WarpforgeRevival.cfg");
        Add(patcherLog, "patcher.log");
        return zipPath;
    }
}
