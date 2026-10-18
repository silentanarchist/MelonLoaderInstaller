using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Android.Content;
using Android.Content.PM;

namespace WarpforgePatcher;

/// <summary>
/// Updates this app from its own GitHub releases (silentanarchist/warpforge-revival-mod, tags patcher-v&lt;version&gt;;
/// up to 0.2.8 they were also published in silentanarchist/warpforge-revival-mod-android, which 0.2.7 and older read),
/// so a new build reaches players without them downloading and reinstalling the APK by hand.
///
/// Checked once per start. The newest non-draft, non-pre-release "patcher-v" release newer than this build is offered.
/// The repository also holds the mod's frequent releases, so the patcher's are found by their tags (git/matching-refs)
/// and then read one by one (releases/tags/...), newest first.
/// Its APK is downloaded and accepted only when
///  - its SHA-256 matches the one written in the release notes ("SHA-256 of the APK: `...`", as patcher-release.yml writes them),
///  - it is this app's package, with a higher version code, signed with the same certificate as this installed app.
/// Android enforces the last point again at install time: an update signed with another key is refused, so only builds
/// signed with the owner's app key can ever replace this app. Installing as an update keeps the app's data (the game's
/// signing key among it).
/// A release whose notes contain "Required update" is offered as required: patching is blocked until the app is updated.
/// </summary>
internal static class AppUpdate
{
    private const string Repo = "silentanarchist/warpforge-revival-mod";
    private const string Tags = "https://api.github.com/repos/" + Repo + "/git/matching-refs/tags/patcher-v";
    private const string ReleaseByTag = "https://api.github.com/repos/" + Repo + "/releases/tags/";
    private const string Downloads = "https://github.com/" + Repo + "/releases/download/";

    public sealed class Offer
    {
        public Version Version = new();
        public string Tag = "", ApkUrl = "", ApkName = "", Sha256 = "", PageUrl = "";
        public long Size;
        public bool Required;
    }

    public static Version Current(Context context)
    {
        string name = context.PackageManager!.GetPackageInfo(context.PackageName!, 0)!.VersionName ?? "0";
        return Version.TryParse(name.Split('-')[0], out var v) ? v : new Version(0, 0);
    }

    /// <summary>The newest published patcher release newer than this build (null when up to date); error is set when GitHub could not be asked.</summary>
    public static async Task<(Offer? offer, string? error)> Check(Context context, Action<string> log)
    {
        try
        {
            using var http = Client(TimeSpan.FromSeconds(20));
            var current = Current(context);
            var versions = new List<(Version v, string tag)>();
            using (var refs = JsonDocument.Parse(await http.GetStringAsync(Tags)))
                foreach (var r in refs.RootElement.EnumerateArray())
                {
                    string tag = (r.GetProperty("ref").GetString() ?? "").Replace("refs/tags/", "");
                    if (tag.StartsWith("patcher-v", StringComparison.Ordinal) && Version.TryParse(tag.Substring("patcher-v".Length), out var v) && v > current)
                        versions.Add((v, tag));
                }
            Offer? best = null;
            foreach (var (v, tag) in versions.OrderByDescending(x => x.v).Take(5))
            {
                using var reply = await http.GetAsync(ReleaseByTag + tag);
                if (reply.StatusCode == System.Net.HttpStatusCode.NotFound) continue;     // a tag without a published release
                reply.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await reply.Content.ReadAsStringAsync());
                var r = doc.RootElement;
                if (r.GetProperty("draft").GetBoolean() || r.GetProperty("prerelease").GetBoolean()) continue;
                string body = r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
                var sha = Regex.Match(body, @"SHA-256 of the APK:\s*`([0-9a-fA-F]{64})`");
                foreach (var a in r.GetProperty("assets").EnumerateArray())
                {
                    string name = a.GetProperty("name").GetString() ?? "";
                    if (!name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)) continue;
                    best = new Offer
                    {
                        Version = v, Tag = tag, ApkName = name,
                        ApkUrl = a.GetProperty("browser_download_url").GetString() ?? "",
                        Size = a.GetProperty("size").GetInt64(),
                        Sha256 = sha.Success ? sha.Groups[1].Value.ToLowerInvariant() : "",
                        PageUrl = r.GetProperty("html_url").GetString() ?? "",
                        Required = body.Contains("Required update", StringComparison.OrdinalIgnoreCase),
                    };
                    break;
                }
                if (best != null) break;
            }
            if (best == null || best.Version <= current) { log($"App is up to date ({current})"); return (null, null); }
            if (!best.ApkUrl.StartsWith(Downloads, StringComparison.Ordinal))
            {
                log("Update check: the release's APK is not on this project's GitHub page; ignored");
                return (null, null);
            }
            log($"App update available: {best.Version}{(best.Required ? " (required)" : "")}");
            return (best, null);
        }
        catch (Exception e)
        {
            log("Could not check for an app update: " + e.Message);
            return (null, e.Message);
        }
    }

    /// <summary>Downloads and checks the offered APK; returns its path, ready to hand to Android's installer.</summary>
    public static async Task<string> Download(Context context, Offer offer, Action<string> log)
    {
        if (offer.Sha256.Length != 64)
            throw new UserError("update.noChecksum", "That release has no checksum for its APK, so the app will not install it. Download it from the releases page instead.");
        string dir = Path.Combine(context.CacheDir!.AbsolutePath, "app-update");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "update.apk");

        using var http = Client(TimeSpan.FromMinutes(10));
        log($"Downloading {offer.ApkName} ({offer.Size / 1048576.0:F1} MB)");
        using (var reply = await http.GetAsync(offer.ApkUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            reply.EnsureSuccessStatusCode();
            await using var s = await reply.Content.ReadAsStreamAsync();
            await using var f = File.Create(path);
            await s.CopyToAsync(f);
        }
        string got;
        using (var f = File.OpenRead(path)) got = Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();
        if (got != offer.Sha256)
        {
            File.Delete(path);
            throw new UserError("update.badChecksum", "The downloaded update does not match the checksum in its release notes; nothing was installed.");
        }

        // the same app, a newer build, signed with the same key as this one
        var pm = context.PackageManager!;
        var flags = OperatingSystem.IsAndroidVersionAtLeast(28) ? PackageInfoFlags.SigningCertificates : PackageInfoFlags.Signatures;
        var info = pm.GetPackageArchiveInfo(path, flags) ?? throw new UserError("update.unreadable", "The downloaded update is not a readable app package.");
        var me = pm.GetPackageInfo(context.PackageName!, flags)!;
        if (info.PackageName != context.PackageName)
            throw new UserError("update.otherApp", "The downloaded update is a different app; nothing was installed.");
        if (VersionCode(info) <= VersionCode(me))
            throw new UserError("update.notNewer", "The downloaded update is not newer than this app; nothing was installed.");
        if (!SameSigner(Certs(info), Certs(me)))
            throw new UserError("update.otherKey", "The downloaded update is not signed with this app's key; nothing was installed.");
        log($"Update {info.VersionName} checked (checksum, package, version, signature)");
        return path;
    }

    private static HttpClient Client(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("WarpforgePatcher/1");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    private static long VersionCode(PackageInfo p) =>
#pragma warning disable CA1422
        OperatingSystem.IsAndroidVersionAtLeast(28) ? p.LongVersionCode : p.VersionCode;
#pragma warning restore CA1422

    private static List<byte[]> Certs(PackageInfo p)
    {
        var list = new List<byte[]>();
        if (OperatingSystem.IsAndroidVersionAtLeast(28))
        {
            var signers = p.SigningInfo?.GetApkContentsSigners();
            if (signers != null) foreach (var s in signers) list.Add(s.ToByteArray()!);
        }
        else
        {
#pragma warning disable CA1422
            if (p.Signatures != null) foreach (var s in p.Signatures) list.Add(s.ToByteArray()!);
#pragma warning restore CA1422
        }
        return list;
    }

    private static bool SameSigner(List<byte[]> a, List<byte[]> b) =>
        a.Count > 0 && a.Count == b.Count && a.All(x => b.Any(y => x.AsSpan().SequenceEqual(y)));
}
