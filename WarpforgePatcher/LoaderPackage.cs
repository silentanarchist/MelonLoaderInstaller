using System.Security.Cryptography;

namespace WarpforgePatcher;

/// <summary>
/// The loader package (melon_data.zip, built from the project's MelonLoader fork; no game content). It is not
/// inside the app (that made the app 58 MB): it is downloaded once from the Android repo on GitHub at a pinned
/// commit and checked against a pinned SHA-256, so it is exactly the package the PC patcher uses. A build can
/// still bundle it as an asset (build.sh --bundle-loader); a bundled copy is used first.
/// A new loader needs a new app build with the new commit and checksum here.
/// </summary>
internal static class LoaderPackage
{
    public const string Commit = "67662ccd45cef784c73f01f00375a1ead13e5b05";   // silentanarchist/warpforge-revival-mod-android
    public const string Sha256 = "05274366462a3364be30c1f5a7adcb4368fdad254102229989ceaba4fdfcda3b";   // the three parts joined (loader59)
    public const int Parts = 3;
    private const string Url = "https://raw.githubusercontent.com/silentanarchist/warpforge-revival-mod-android/" + Commit + "/patcher/tool/melon_data.zip.part";

    /// <summary>Puts a checked copy of the loader package at <paramref name="dest"/>.</summary>
    public static async Task Get(Android.Content.Context context, string dest, Action<string> log)
    {
        // a bundled copy (builds made with --bundle-loader)
        try
        {
            using var asset = context.Assets!.Open("melon_data.zip");
            using (var file = File.Create(dest)) await asset.CopyToAsync(file);
            if (Hash(dest) == Sha256) { log("Loader package: the copy inside the app"); return; }
            log("The loader package inside the app does not match its checksum; downloading instead");
        }
        catch (Java.IO.FileNotFoundException) { }

        // a copy downloaded on an earlier run
        string cached = Path.Combine(context.FilesDir!.AbsolutePath, "loader", "melon_data-" + Sha256[..12] + ".zip");
        if (File.Exists(cached) && Hash(cached) == Sha256)
        {
            File.Copy(cached, dest, true);
            log("Loader package: downloaded earlier, checksum OK");
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
        foreach (var old in Directory.GetFiles(Path.GetDirectoryName(cached)!)) try { File.Delete(old); } catch { }
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("WarpforgePatcher/1");
        string temp = cached + ".part";
        using (var output = File.Create(temp))
        {
            for (int i = 0; i < Parts; i++)
            {
                log($"Downloading the loader package, part {i + 1} of {Parts} (from GitHub)");
                var reply = await http.GetAsync(Url + i, HttpCompletionOption.ResponseHeadersRead);
                for (int attempt = 1; !reply.IsSuccessStatusCode && attempt <= 2 && ((int)reply.StatusCode == 429 || (int)reply.StatusCode >= 500); attempt++)
                {
                    log($"GitHub answered {(int)reply.StatusCode}; trying again in {attempt * 10} s");
                    reply.Dispose();
                    await Task.Delay(TimeSpan.FromSeconds(attempt * 10));
                    reply = await http.GetAsync(Url + i, HttpCompletionOption.ResponseHeadersRead);
                }
                using var _ = reply;
                if (!reply.IsSuccessStatusCode)
                    throw new UserError("err.loaderDownload", "Could not download the loader package from GitHub ({code}). Check the connection and try again.", ("code", (int)reply.StatusCode));
                await using var s = await reply.Content.ReadAsStreamAsync();
                await s.CopyToAsync(output);
            }
        }
        if (Hash(temp) != Sha256)
        {
            File.Delete(temp);
            throw new UserError("err.loaderChecksum", "The downloaded loader package does not match its checksum; nothing was changed. Try again later.");
        }
        File.Move(temp, cached, true);
        File.Copy(cached, dest, true);
        log($"Loader package downloaded ({new FileInfo(cached).Length / 1048576} MB, checksum OK)");
    }

    private static string Hash(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }
}
