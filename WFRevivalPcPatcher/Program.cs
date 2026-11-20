using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using MelonLoader.Installer.Core;
using QuestPatcher.Axml;
using WarpforgePatcher;

namespace WFRevivalPcPatcher;

/// <summary>
/// WF Revival PC Patcher (a console program for the owner's Husk test; a one-window app may follow).
/// 0.2.0: a small menu (patch / mod only / send over Wi-Fi / server), the server setting, and sending the files to Husk
/// on an iPhone over the local network (LanShare, Husk's own "shared folder" download).
/// Takes the player's own backup of the game (.xapk / .apks, or a folder with the three APKs), patches it with the
/// phone app's core and pinned loader package, downloads the signed Android mod, and writes everything to one folder:
/// the three patched APKs, the same three packed as one .apks (Husk on iPhone takes that), the mod file and a note on
/// where each goes. Nothing is installed anywhere; no game content is downloaded or shipped.
/// </summary>
internal static class Program
{
    private const string Version = "0.2.0";
    private const string Package = "com.Everguild.WarhammerWarpforge";
    private const string GameVersion = "1.35.0";          // what the loader package and the mod are made for
    private const string UnityVersion = "6000.2.6f2";     // the game's own engine version

    // the phone app's pinned loader package (WarpforgePatcher/LoaderPackage.cs): keep these two in step with it
    private const string LoaderCommit = "67662ccd45cef784c73f01f00375a1ead13e5b05";
    private const string LoaderSha256 = "05274366462a3364be30c1f5a7adcb4368fdad254102229989ceaba4fdfcda3b";
    private const int LoaderParts = 3;
    private const string LoaderUrl = "https://raw.githubusercontent.com/silentanarchist/wf-revival-mod-android/" + LoaderCommit + "/patcher/tool/melon_data.zip.part";

    private const string OutputFolderName = "WF Revival patched";
    private const string ModFolderName = "WF Revival mod";
    /// <summary>The mod's settings file name; sent along (with ServerUrl) only when another server than the community one is set.</summary>
    internal const string ModConfigName = "WarpforgeRevival.cfg";

    internal static string AppDir
    {
        get
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
            if (string.IsNullOrEmpty(local)) local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            return Path.Combine(local, "WFRevivalPcPatcher");
        }
    }
    private static StreamWriter? logFile;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.Title = "WF Revival PC Patcher " + Version;
        Say($"WF Revival PC Patcher {Version}");
        Say("Patches your own copy of Warpforge with the WF Revival mod loader, and can send the files to Husk on an iPhone");
        Say("over Wi-Fi. Nothing is installed on this PC; no game files are downloaded or shared.");
        Directory.CreateDirectory(AppDir);
        Settings.Load();

        // "--send <folder>": only send a folder made earlier (also used to test the sending)
        if (args.Length == 2 && args[0] == "--send")
        {
            LanShare.Run(Path.GetFullPath(args[1]), Say);
            return 0;
        }
        // a backup dragged onto the program (or given on the command line): patch it straight away
        if (args.Length > 0)
        {
            string? folder = Try(() => Patch(args[0]));
            if (folder != null) OfferSend(folder);
            Pause();
            return folder != null ? 0 : 1;
        }
        if (Console.IsInputRedirected) { Try(() => Patch("")); return 0; }

        while (true)
        {
            Console.WriteLine();
            Console.WriteLine($"Server: {Settings.Server}{(IsCommunityServer(Settings.Server) ? " (the community server)" : "")}");
            Console.WriteLine("  1  Patch my game backup (.xapk / .apks), with the mod");
            Console.WriteLine("  2  Get the current mod only");
            Console.WriteLine("  3  Send files to an iPhone over Wi-Fi (Husk)");
            Console.WriteLine("  4  Change the server");
            Console.WriteLine("  Q  Quit");
            Console.Write("Choose: ");
            string choice = (Console.ReadLine() ?? "q").Trim().ToLowerInvariant();
            Console.WriteLine();
            switch (choice)
            {
                case "1": { string? f = Try(() => Patch(FindInput())); if (f != null) OfferSend(f); break; }
                case "2": { string? f = Try(ModOnly); if (f != null) OfferSend(f); break; }
                case "3": SendChosen(); break;
                case "4": ChangeServer(); break;
                case "q": case "": return 0;
                default:
                    // a backup dragged into the window at the menu
                    string dropped = choice.Trim('"');
                    if (File.Exists(dropped) || Directory.Exists(dropped)) { string? f = Try(() => Patch(dropped)); if (f != null) OfferSend(f); }
                    break;
            }
        }
    }

    /// <summary>Runs one job; a problem is told plainly and the menu comes back.</summary>
    private static string? Try(Func<Task<string>> job)
    {
        try { return job().GetAwaiter().GetResult(); }
        catch (Stop stop) { Say(""); Say("Stopped: " + stop.Message); }
        catch (Exception e) { Say(""); Say("Something went wrong: " + e.Message); Log(e.ToString()); }
        finally { logFile?.Dispose(); logFile = null; }
        return null;
    }

    private static void Pause()
    {
        if (Console.IsInputRedirected) return;
        Console.WriteLine();
        Console.WriteLine("Press Enter to close this window.");
        Console.ReadLine();
    }

    private static bool Ask(string question)
    {
        if (Console.IsInputRedirected) return false;
        Console.Write(question + " [Y/n] ");
        string a = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
        return a is "" or "y" or "yes";
    }

    // ------------------------------------------------------------------ jobs

    /// <summary>Patches a backup; returns the output folder.</summary>
    private static async Task<string> Patch(string given)
    {
        // ---- 1. the game's files
        string input = given.Trim().Trim('"');
        if (input.Length == 0) throw new Stop("no game backup was given.");
        if (File.Exists(input) && input.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)) input = Path.GetDirectoryName(Path.GetFullPath(input))!;
        input = Path.GetFullPath(input);
        if (!File.Exists(input) && !Directory.Exists(input)) throw new Stop("there is no file or folder at " + input);

        string outputDir = OutputFolder(Path.GetDirectoryName(input.TrimEnd(Path.DirectorySeparatorChar))!, OutputFolderName);
        logFile = new StreamWriter(Path.Combine(outputDir, "patch-log.txt")) { AutoFlush = true };
        Log($"WF Revival PC Patcher {Version}, {DateTime.Now:yyyy-MM-dd HH:mm:ss}, {Environment.OSVersion}, server {Settings.Server}");

        string work = Path.Combine(AppDir, "work");
        if (Directory.Exists(work)) Directory.Delete(work, true);
        string workOut = Path.Combine(work, "out"), temp = Path.Combine(work, "temp");
        Directory.CreateDirectory(workOut);
        Directory.CreateDirectory(temp);

        string baseApk, arm64Apk;
        List<string> extra = new();
        long inputBytes;
        if (File.Exists(input))
        {
            Say("Game backup: " + Path.GetFileName(input));
            inputBytes = new FileInfo(input).Length;
            CheckSpace(work, inputBytes * 2 + 600L * 1048576);
            CheckSpace(outputDir, inputBytes * 2 + 100L * 1048576);
            (baseApk, arm64Apk, extra) = Unpack(input, workOut);
        }
        else
        {
            Say("Game files in: " + input);
            var apks = Directory.GetFiles(input, "*.apk");
            baseApk = apks.FirstOrDefault(p => Path.GetFileName(p).Equals(Package + ".apk", StringComparison.OrdinalIgnoreCase))
                   ?? apks.FirstOrDefault(p => Path.GetFileName(p).Equals("base.apk", StringComparison.OrdinalIgnoreCase)) ?? "";
            arm64Apk = apks.FirstOrDefault(p => Path.GetFileName(p).Contains("arm64", StringComparison.OrdinalIgnoreCase)) ?? "";
            extra = apks.Where(p => p != baseApk && p != arm64Apk).ToList();
            if (baseApk.Length == 0 || arm64Apk.Length == 0)
                throw new Stop($"that folder needs {Package}.apk (or base.apk) and config.arm64_v8a.apk. Found: {string.Join(", ", apks.Select(Path.GetFileName))}");
            inputBytes = apks.Sum(p => new FileInfo(p).Length);
            CheckSpace(work, inputBytes + 600L * 1048576);
            CheckSpace(outputDir, inputBytes * 2 + 100L * 1048576);
        }
        if (!extra.Any(p => Path.GetFileName(p).Contains("UnityDataAssetPack", StringComparison.OrdinalIgnoreCase)))
            throw new Stop("the game's data pack (UnityDataAssetPack.apk, about 700 MB) is missing; the patched game would not start. Make the backup again with all its parts.");
        string version = VersionOf(baseApk) ?? "?";
        Say($"Game version {version}, {inputBytes / 1048576} MB in {2 + extra.Count} parts");
        if (version != GameVersion) throw new Stop($"this is game version {version}; the loader and the mod are made for {GameVersion}.");

        // ---- 2. the mod (signed by the WF Revival project) and the loader package
        Say("");
        var (modFile, modVersion) = await DownloadMod(outputDir);
        WriteServerSetting(outputDir);
        string loaderZip = await GetLoader(temp);

        // ---- 3. patch, with the phone app's settings (WarpforgePatcher/PatchJob.cs)
        Say("");
        Say("Patching (this takes a few minutes)");
        string unityZip = Path.Combine(temp, "unity.zip");
        using (var z = ZipFile.Open(unityZip, ZipArchiveMode.Create))
        {
            // keep the game's own libunity.so: the core would otherwise put in a generic build the game's data refuses
            var e = z.CreateEntry("arm64-v8a/keep-the-games-own-libunity.txt");
            using var w = new StreamWriter(e.Open());
            w.Write("intentionally empty");
        }
        string splash = Path.Combine(temp, "launch-splash.png");
        using (var res = typeof(Program).Assembly.GetManifestResourceStream("launch-splash.png")!) using (var f = File.Create(splash)) res.CopyTo(f);
        Environment.SetEnvironmentVariable("LEMON_LAUNCH_SPLASH", splash);
        Environment.SetEnvironmentVariable("LEMON_LAUNCH_SPLASH_COLOR", "16181B");
        string keyFile = Path.Combine(AppDir, "signing-key.pem");
        bool newKey = !File.Exists(keyFile);
        Environment.SetEnvironmentVariable("LEMON_SIGNING_KEY", keyFile);

        var patchArgs = new PatchArguments(baseApk, arm64Apk, extra.ToArray(), workOut, temp, loaderZip, unityZip,
            AssetRipper.Primitives.UnityVersion.Parse(UnityVersion), Package, true);
        var patcher = new Patcher(patchArgs, new CoreLog())
        {
            StepStarted = (step, steps) => Bar($"Patching: step {step} of {steps}", (step - 1) / (double)steps, ""),
        };
        bool ok = patcher.Run();
        EndBar();
        if (!ok) throw new Stop("patching did not finish. patch-log.txt in the output folder says where it stopped.");

        // ---- 4. the output folder
        string[] patched = Directory.GetFiles(workOut, "*.apk").OrderBy(p => Path.GetFileName(p) == "base.apk" ? 0 : 1).ToArray();
        if (patched.Length < 3) throw new Stop("patching left fewer files than expected: " + string.Join(", ", patched.Select(Path.GetFileName)));
        Say("");
        long total = patched.Sum(p => new FileInfo(p).Length), done = 0;
        foreach (string p in patched)
        {
            string target = Path.Combine(outputDir, Path.GetFileName(p));
            File.Move(p, target, true);
        }
        // the same three as one bundle (Husk on iPhone, SAI and similar take .apks); stored, not compressed, like bundletool
        string bundle = Path.Combine(outputDir, "Warpforge-WF-Revival-patched.apks");
        using (var zip = ZipFile.Open(bundle, ZipArchiveMode.Create))
        {
            foreach (string p in patched)
            {
                string name = Path.GetFileName(p);
                var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
                using var from = File.OpenRead(Path.Combine(outputDir, name));
                using var to = entry.Open();
                var buffer = new byte[1 << 20];
                int n;
                while ((n = from.Read(buffer, 0, buffer.Length)) > 0)
                {
                    to.Write(buffer, 0, n);
                    done += n;
                    Bar("Packing the .apks bundle", (double)done / total, $"{Mb(done)} / {Mb(total)} MB");
                }
            }
        }
        EndBar();
        Log("Bundle written: " + bundle);
        try { Directory.Delete(work, true); } catch { }

        File.WriteAllText(Path.Combine(outputDir, "READ ME - where the files go.txt"), ReadMe(Path.GetFileName(modFile), modVersion, newKey, game: true));
        Say("");
        Say("Done. In the folder \"" + Path.GetFileName(outputDir) + "\" (next to your backup):");
        Say("  Warpforge-WF-Revival-patched.apks  the patched game as one file (for Husk on iPhone)");
        foreach (string p in patched) Say("  " + Path.GetFileName(p).PadRight(34) + " the same, as separate files");
        Say("  " + Path.GetFileName(modFile).PadRight(34) + $" the mod ({modVersion}); see the READ ME for where it goes");
        Say("");
        Say("Signed with this PC's own key" + (newKey ? " (made just now and kept for next time)" : "") + ". A device with a copy");
        Say("patched by the phone app (or the original game) has to remove it once before this one installs.");
        Settings.LastFolder = outputDir;
        Settings.Save();
        OpenFolder(outputDir);
        return outputDir;
    }

    /// <summary>Downloads the current mod only (for a game patched earlier, or a fresh Husk install); returns its folder.</summary>
    private static async Task<string> ModOnly()
    {
        string outputDir;
        try { outputDir = OutputFolder(AppContext.BaseDirectory, ModFolderName); }
        catch { outputDir = OutputFolder(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), ModFolderName); }
        logFile = new StreamWriter(Path.Combine(outputDir, "log.txt")) { AutoFlush = true };
        Log($"WF Revival PC Patcher {Version}, mod only, server {Settings.Server}");
        var (modFile, modVersion) = await DownloadMod(outputDir);
        WriteServerSetting(outputDir);
        File.WriteAllText(Path.Combine(outputDir, "READ ME - where the files go.txt"), ReadMe(Path.GetFileName(modFile), modVersion, false, game: false));
        Say($"The mod is in \"{outputDir}\".");
        Settings.LastFolder = outputDir;
        Settings.Save();
        OpenFolder(outputDir);
        return outputDir;
    }

    /// <summary>Offers to send a just-made folder to an iPhone.</summary>
    private static void OfferSend(string folder)
    {
        Console.WriteLine();
        if (Ask("Send these files to an iPhone over Wi-Fi now (Husk)?")) LanShare.Run(folder, Say);
    }

    /// <summary>Menu 3: the last folder made, or one dragged in.</summary>
    private static void SendChosen()
    {
        string? folder = Settings.LastFolder != null && Directory.Exists(Settings.LastFolder) ? Settings.LastFolder : null;
        if (folder != null)
        {
            Console.WriteLine($"Last files made: {folder}");
            if (!Ask("Send that folder?")) folder = null;
        }
        if (folder == null)
        {
            Console.WriteLine("Drag the folder to send (\"WF Revival patched\" or \"WF Revival mod\") into this window and press Enter:");
            folder = (Console.ReadLine() ?? "").Trim().Trim('"');
            if (!Directory.Exists(folder)) { Console.WriteLine("That is not a folder."); return; }
        }
        try { LanShare.Run(folder, Say); }
        catch (Exception e) { Say("Sending stopped: " + e.Message); }
    }

    /// <summary>Menu 4: the server the mod comes from (and, for another than the community server, the one the mod uses).</summary>
    private static void ChangeServer()
    {
        Console.WriteLine($"Server now: {Settings.Server}");
        Console.WriteLine($"Type the server's address (like myserver.example.com, or with a port: myserver.example.com:8780),");
        Console.WriteLine($"or press Enter alone for the community server ({DefaultServer}):");
        string typed = (Console.ReadLine() ?? "").Trim().TrimEnd('/');
        if (typed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) typed = typed.Substring(7);
        if (typed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) typed = typed.Substring(8);
        if (typed.Length == 0) typed = DefaultServer;
        if (typed.Any(char.IsWhiteSpace) || typed.Contains('"')) { Console.WriteLine("That does not look like an address; nothing changed."); return; }
        Settings.Server = typed;
        Settings.Save();
        Console.WriteLine($"Server set to {typed}." + (IsCommunityServer(typed) ? "" : " Files made from now on include the mod's settings file pointing at it."));
    }

    // ------------------------------------------------------------------ server

    private const string DefaultServer = "wf-revival.silentanarchist.com";   // the mod's RevivalConfig.DefaultServer
    private const string OldServer = "warpforge.silentanarchist.com";        // its earlier name: the same server
    private const int ServerPort = 8780;

    internal static bool IsCommunityServer(string? value)
    {
        string t = (value ?? "").Trim().TrimEnd('/').ToLowerInvariant();
        foreach (var prefix in new[] { "https://", "http://" }) if (t.StartsWith(prefix)) t = t.Substring(prefix.Length);
        return t.Length == 0 || t == DefaultServer || t == OldServer;
    }

    /// <summary>The addresses to ask, in order (as the phone app: https only, the game port unless one is written).</summary>
    private static string[] ServerBases() => IsCommunityServer(Settings.Server)
        ? new[] { Https(DefaultServer), Https(OldServer) }
        : new[] { Https(Settings.Server) };

    private static string Https(string written)
    {
        string value = written.Trim().TrimEnd('/');
        if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return value;
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) value = value.Substring(7);
        string host = value.Split('/')[0];
        bool hasPort = host.LastIndexOf(':') > host.LastIndexOf(']');
        return "https://" + (hasPort ? value : host + ":" + ServerPort + value.Substring(host.Length));
    }

    /// <summary>For another server than the community one: the mod's settings file with ServerUrl, so the mod connects there.</summary>
    private static void WriteServerSetting(string outputDir)
    {
        string path = Path.Combine(outputDir, ModConfigName);
        if (IsCommunityServer(Settings.Server)) { if (File.Exists(path)) File.Delete(path); return; }
        File.WriteAllText(path, $"[WarpforgeRevival]\nServerUrl = \"{Settings.Server}\"\n");
        Say($"Mod settings file: ServerUrl = {Settings.Server}");
    }

    // ------------------------------------------------------------------ input

    /// <summary>A backup next to this program or in the current folder, else asks for one.</summary>
    private static string FindInput()
    {
        foreach (string dir in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory }.Distinct())
        {
            var found = Directory.GetFiles(dir).Where(f => f.EndsWith(".xapk", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".apks", StringComparison.OrdinalIgnoreCase))
                .Where(f => !Path.GetFileName(f).StartsWith("Warpforge-WF-Revival-patched", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (found.Length == 1) { Say("Found " + Path.GetFileName(found[0]) + " next to this program."); return found[0]; }
        }
        if (Console.IsInputRedirected) return "";
        Say("Drag your game backup (.xapk or .apks), or the folder holding the three APKs, into this window and press Enter:");
        return Console.ReadLine() ?? "";
    }

    /// <summary>
    /// "WF Revival patched" next to the backup, emptied (only this program's files are in it). When it cannot be emptied
    /// (a file open in another program, say), the next free "WF Revival patched 2", "3", ... instead.
    /// </summary>
    private static string OutputFolder(string parent, string name)
    {
        for (int i = 1; ; i++)
        {
            string dir = Path.Combine(parent, i == 1 ? name : $"{name} {i}");
            Directory.CreateDirectory(dir);   // a folder that cannot be made at all (no rights there) is the caller's problem
            try
            {
                foreach (string f in Directory.GetFiles(dir)) File.Delete(f);
                return dir;
            }
            catch (Exception e) when (i < 20 && e is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"\"{Path.GetFileName(dir)}\" could not be emptied ({e.Message}); using the next folder name.");
            }
        }
    }

    /// <summary>Unpacks a backup into the core's output folder under the names it expects there (as the phone app does).</summary>
    private static (string baseApk, string arm64, List<string> extra) Unpack(string backup, string outDir)
    {
        using var zip = ZipFile.OpenRead(backup);
        var apks = zip.Entries.Where(e => e.FullName.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) && !e.FullName.Contains('/')).ToList();
        if (apks.Count == 0) throw new Stop("that file holds no .apk files. Pick the game's .xapk or .apks backup.");
        var main = apks.FirstOrDefault(e => e.Name.Equals(Package + ".apk", StringComparison.OrdinalIgnoreCase)) ?? apks.FirstOrDefault(e => e.Name.Equals("base.apk", StringComparison.OrdinalIgnoreCase));
        var arm64 = apks.FirstOrDefault(e => e.Name.Contains("arm64", StringComparison.OrdinalIgnoreCase));
        if (main == null || arm64 == null) throw new Stop("the backup is missing the game's main file or its arm64 code file. Found: " + string.Join(", ", apks.Select(e => e.Name)));
        string b = "", a = "";
        var extra = new List<string>();
        long total = apks.Sum(e => e.Length), done = 0;
        var buffer = new byte[1 << 20];
        foreach (var e in apks)
        {
            string target = e == main ? "base.apk" : (e.Name.StartsWith("split_", StringComparison.OrdinalIgnoreCase) ? e.Name.Substring(6) : e.Name);
            string path = Path.Combine(outDir, target);
            Log($"Unpacking {e.Name} ({e.Length / 1048576} MB)");
            using (var from = e.Open())
            using (var to = File.Create(path))
            {
                int n;
                while ((n = from.Read(buffer, 0, buffer.Length)) > 0)
                {
                    to.Write(buffer, 0, n);
                    done += n;
                    Bar("Unpacking the backup", (double)done / total, $"{Mb(done)} / {Mb(total)} MB");
                }
            }
            if (e == main) b = path; else if (e == arm64) a = path; else extra.Add(path);
        }
        EndBar();
        return (b, a, extra);
    }

    /// <summary>versionName from the APK's binary manifest.</summary>
    private static string? VersionOf(string apk)
    {
        try
        {
            using var zip = ZipFile.OpenRead(apk);
            var entry = zip.GetEntry("AndroidManifest.xml");
            if (entry == null) return null;
            using var memory = new MemoryStream();
            using (var s = entry.Open()) s.CopyTo(memory);
            memory.Position = 0;
            var root = AxmlLoader.LoadDocument(memory);
            return root.Attributes.FirstOrDefault(a => a.Name == "versionName")?.Value?.ToString();
        }
        catch (Exception e) { Log("Could not read the game's version: " + e.Message); return null; }
    }

    private static void CheckSpace(string dir, long needed)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))!);
            if (drive.AvailableFreeSpace < needed)
                throw new Stop($"not enough free space on {drive.Name}: about {needed / 1073741824.0:F1} GB needed, {drive.AvailableFreeSpace / 1073741824.0:F1} GB free.");
        }
        catch (Stop) { throw; }
        catch { }
    }

    // ------------------------------------------------------------------ downloads

    /// <summary>The current Android mod, checked against the server's checksum and the WF Revival project's signature.</summary>
    private static async Task<(string path, string version)> DownloadMod(string outputDir)
    {
        using var http = NewHttp();
        string server = "", manifestText = "";
        var servers = ServerBases();
        for (int i = 0; i < servers.Length; i++)
        {
            server = servers[i];
            Say("Asking " + server + " for the current mod");
            try { manifestText = await http.GetStringAsync(server + "/api/v1/mod/manifest?platform=android"); break; }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                Log($"{server} did not answer ({e.Message})");
                if (i + 1 == servers.Length) throw new Stop($"the server {Settings.Server} did not answer ({e.Message}). Check the address (menu 4) and the connection.");
            }
        }
        using var manifest = JsonDocument.Parse(manifestText);
        var root = manifest.RootElement;
        if (!root.TryGetProperty("available", out var available) || !available.GetBoolean()) throw new Stop("the server has no Android mod to hand out right now.");
        string sha = (root.GetProperty("sha256").GetString() ?? "").ToLowerInvariant();
        string url = root.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
        if (url.Length == 0) url = "/api/v1/mod/download?platform=android";
        if (!url.StartsWith('/')) throw new Stop("the server's mod address is not on the server itself; refused.");
        byte[] data = await Download(http, server + url, "Downloading the mod");
        if (sha.Length != 64 || Hex(SHA256.HashData(data)) != sha) throw new Stop("the downloaded mod does not match the server's checksum.");
        string? refused = ModSignature.Check(data, ModSignature.AndroidFileNames, out string? signedVersion, out string? signedAs);
        if (refused != null) throw new Stop("the mod from the server was refused: " + refused + ". Only mod builds signed by the WF Revival project are used.");
        string path = Path.Combine(outputDir, signedAs ?? ModSignature.AndroidFileName);
        await File.WriteAllBytesAsync(path, data);
        Say($"Mod {signedVersion}: signature OK");
        return (path, signedVersion ?? "?");
    }

    /// <summary>The pinned loader package (same as the phone app's), kept between runs.</summary>
    private static async Task<string> GetLoader(string temp)
    {
        string cached = Path.Combine(AppDir, "loader", "melon_data-" + LoaderSha256[..12] + ".zip");
        if (!(File.Exists(cached) && HashFile(cached) == LoaderSha256))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
            using var http = NewHttp();
            using (var output = File.Create(cached + ".part"))
                for (int i = 0; i < LoaderParts; i++)
                    await output.WriteAsync(await Download(http, LoaderUrl + i, $"Downloading the loader package (part {i + 1} of {LoaderParts})"));
            if (HashFile(cached + ".part") != LoaderSha256) { File.Delete(cached + ".part"); throw new Stop("the downloaded loader package does not match its checksum."); }
            File.Move(cached + ".part", cached, true);
        }
        Say("Loader package: checksum OK");
        string copy = Path.Combine(temp, "melon_data.zip");   // the core deletes the copy it is given
        File.Copy(cached, copy, true);
        return copy;
    }

    private static HttpClient NewHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("WFRevivalPcPatcher/" + Version);
        return http;
    }

    /// <summary>A download with the bar; stops when nothing arrives for 30 seconds.</summary>
    private static async Task<byte[]> Download(HttpClient http, string url, string label)
    {
        using var reply = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        reply.EnsureSuccessStatusCode();
        long total = reply.Content.Headers.ContentLength ?? -1;
        await using var s = await reply.Content.ReadAsStreamAsync();
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        var clock = Stopwatch.StartNew();
        long shown = -1000;
        while (true)
        {
            var read = s.ReadAsync(buffer, 0, buffer.Length);
            if (await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(30))) != read) { EndBar(); throw new Stop(label.ToLowerInvariant() + " stopped: nothing arrived for 30 seconds. Check the connection and try again."); }
            int n = await read;
            if (n == 0) break;
            memory.Write(buffer, 0, n);
            if (clock.ElapsedMilliseconds - shown >= 150)
            {
                shown = clock.ElapsedMilliseconds;
                double speed = memory.Length / Math.Max(clock.Elapsed.TotalSeconds, 0.001);
                Bar(label, total > 0 ? (double)memory.Length / total : -1, $"{Mb(memory.Length)}{(total > 0 ? " / " + Mb(total) : "")} MB  {Mb((long)speed)} MB/s");
            }
        }
        EndBar();
        Log($"{label}: {Mb(memory.Length)} MB in {clock.Elapsed.TotalSeconds:F1} s");
        return memory.ToArray();
    }

    // ------------------------------------------------------------------ output

    private static string ReadMe(string modName, string modVersion, bool newKey, bool game)
    {
        bool custom = !IsCommunityServer(Settings.Server);
        string mods = $"/sdcard/MelonLoader/{Package}/Mods/";
        var t = new System.Text.StringBuilder();
        t.AppendLine($"WF Revival - {(game ? "patched Warpforge" : "the mod")} ({DateTime.Now:yyyy-MM-dd}), made by WF Revival PC Patcher {Version}");
        t.AppendLine($"Mod: {modName} ({modVersion}), from {Settings.Server}");
        if (custom) t.AppendLine($"{ModConfigName}: the mod's settings file, pointing it at {Settings.Server}. It goes in /sdcard/MelonLoader/{Package}/UserData/");
        t.AppendLine();
        t.AppendLine("ON AN iPHONE WITH HUSK, OVER WI-FI (easiest)");
        t.AppendLine("Run WFRevivalPcPatcher.exe, choose 3 (Send files to an iPhone over Wi-Fi), and enter the address it shows in");
        t.AppendLine("Husk > Downloads on the iPhone (same Wi-Fi network). " + (custom ? "The mod and its settings file land" : "The mod lands") + " in the right folder by itself.");
        if (game)
        {
            t.AppendLine("The game arrives as Warpforge-WF-Revival-patched.apks in Husk's Shared Storage: add it from there (Husk > Add,");
            t.AppendLine("then Shared Storage), then delete that copy to free the space.");
        }
        t.AppendLine();
        t.AppendLine("ON AN iPHONE WITH HUSK, BY HAND");
        if (game) t.AppendLine("1. Add Warpforge-WF-Revival-patched.apks to Husk (share it to Husk from the Files app, or pick it in Husk).");
        t.AppendLine((game ? "2. " : "") + $"Put {modName} into this folder in Husk's Files tab (make the folders if they are missing):");
        t.AppendLine("     " + mods);
        if (game) t.AppendLine("3. Start Warpforge. The first start sits on the loading picture or a black screen for a while; later starts are quicker.");
        t.AppendLine();
        t.AppendLine("ON AN ANDROID DEVICE OR EMULATOR");
        if (game)
        {
            t.AppendLine("1. Install the three APKs together, for example with \"adb install-multiple base.apk config.arm64_v8a.apk UnityDataAssetPack.apk\"");
            t.AppendLine("   or with an app that installs .apks files (SAI). Remove the original game first: a copy signed with another key");
            t.AppendLine("   is refused.");
            t.AppendLine($"2. Put {modName} into  {mods}");
            t.AppendLine("   (the phone patcher app does both steps by itself; this folder is for devices without it).");
            t.AppendLine();
            t.AppendLine("SIGNING KEY");
            t.AppendLine($"These files are signed with this PC's own key{(newKey ? " (made on this run)" : "")}, kept in");
            t.AppendLine("  " + Path.Combine(AppDir, "signing-key.pem"));
            t.AppendLine("Patching again on this PC uses the same key, so a new patch installs over the previous one. Keep that file private.");
            t.AppendLine("A device that has the original game, or a copy patched by the phone app, has to remove it once first.");
        }
        else
        {
            t.AppendLine($"Put {modName} into  {mods}  (the phone patcher app's \"Update the mod only\" does this too).");
        }
        t.AppendLine();
        t.AppendLine("Nothing from the game is downloaded or shared: " + (game ? "these files were made from your own backup." : "this is only the mod."));
        return t.ToString();
    }

    private static void OpenFolder(string dir)
    {
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS()) Process.Start("open", dir);
        }
        catch { }
    }

    // ------------------------------------------------------------------ console

    private static bool barShown;

    private static void Bar(string label, double fraction, string detail)
    {
        if (Console.IsOutputRedirected) return;
        int width = 28;
        string bar = fraction < 0 ? new string('·', width) : new string('#', (int)Math.Round(Math.Clamp(fraction, 0, 1) * width)).PadRight(width, '-');
        string percent = fraction < 0 ? "    " : $"{(int)(Math.Clamp(fraction, 0, 1) * 100),3}%";
        string line = $"  [{bar}] {percent}  {label}  {detail}";
        int max = Math.Max(20, SafeWidth() - 1);
        if (line.Length > max) line = line[..max];
        Console.Write("\r" + line.PadRight(max));
        barShown = true;
    }

    private static void EndBar()
    {
        if (!barShown) return;
        Console.Write("\r" + new string(' ', Math.Max(20, SafeWidth() - 1)) + "\r");
        barShown = false;
    }

    private static int SafeWidth()
    {
        try { return Console.WindowWidth; } catch { return 100; }
    }

    private static void Say(string text)
    {
        EndBar();
        Console.WriteLine(text);
        Log(text);
    }

    private static void Log(string text)
    {
        try { logFile?.WriteLine($"[{DateTime.Now:HH:mm:ss}] {text}"); } catch { }
    }

    private static string Mb(long bytes) => (bytes / 1048576.0).ToString("F1", CultureInfo.InvariantCulture);
    private static string Hex(byte[] hash) => Convert.ToHexString(hash).ToLowerInvariant();
    private static string HashFile(string path) { using var s = File.OpenRead(path); return Hex(SHA256.HashData(s)); }

    /// <summary>The core's log lines go to the log file only (the bar shows the step).</summary>
    private sealed class CoreLog : IPatchLogger
    {
        public void Log(string message) => Program.Log(message);
    }

    /// <summary>A problem to tell the player plainly, without a stack trace.</summary>
    private sealed class Stop(string message) : Exception(message) { }
}
