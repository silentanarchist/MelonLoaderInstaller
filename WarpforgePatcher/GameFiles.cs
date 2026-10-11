using System.IO.Compression;
using Android.Content;
using Android.Content.PM;

namespace WarpforgePatcher;

/// <summary>Where the game's files are, and what state the installed game is in.</summary>
internal static class GameFiles
{
    public const string Package = "com.Everguild.WarhammerWarpforge";
    public const string GameVersion = "1.35.0";           // the version the loader package and the mod are made for
    public const string UnityVersion = "6000.2.6f2";      // the game's own engine version (LemonPatch's default)

    /// <summary>The loader's folder on the phone's storage (Mods, UserData, Latest.log).</summary>
    public static string LoaderDir => Path.Combine(Android.OS.Environment.ExternalStorageDirectory!.AbsolutePath, "MelonLoader", Package);

    /// <summary>The game's parts: the main APK, the arm64 code APK, and every other split (the 700 MB data pack among them).</summary>
    public sealed class Parts
    {
        public string Base = "";
        public string Arm64 = "";
        public List<string> Extra = new();
        public string Origin = "";       // "installed game" or the backup file's name

        public bool HasDataPack => Extra.Any(p => Path.GetFileName(p).Contains("UnityDataAssetPack", StringComparison.OrdinalIgnoreCase));
        public long TotalBytes => new[] { Base, Arm64 }.Concat(Extra).Where(File.Exists).Sum(p => new FileInfo(p).Length);
    }

    public sealed class Installed
    {
        public string Version = "";
        public bool Patched;             // the loader is inside (assets/MelonLoader)
        public string PatchDate = "";
        public bool SignedWithOurKey;    // a new patch installs over it without removing it first
        public Parts Parts = new();
    }

    public static Installed? FindInstalled(Context context, byte[]? ourCertificate)
    {
        PackageInfo info;
        try
        {
            info = context.PackageManager!.GetPackageInfo(Package, OperatingSystem.IsAndroidVersionAtLeast(28) ? PackageInfoFlags.SigningCertificates : PackageInfoFlags.Signatures)!;
        }
        catch (PackageManager.NameNotFoundException) { return null; }

        var app = info.ApplicationInfo!;
        var result = new Installed { Version = info.VersionName ?? "" };
        result.Parts.Origin = "installed game";
        result.Parts.Base = app.SourceDir ?? "";
        foreach (string split in app.SplitSourceDirs ?? Array.Empty<string>())
        {
            if (Path.GetFileName(split).Contains("arm64", StringComparison.OrdinalIgnoreCase) && result.Parts.Arm64.Length == 0)
                result.Parts.Arm64 = split;
            else
                result.Parts.Extra.Add(split);
        }

        try
        {
            using var zip = ZipFile.OpenRead(result.Parts.Base);
            var date = zip.GetEntry("assets/lemon_patch_date.txt");
            result.Patched = date != null || zip.Entries.Any(e => e.FullName.StartsWith("assets/MelonLoader/", StringComparison.Ordinal));
            if (date != null)
            {
                using var r = new StreamReader(date.Open());
                result.PatchDate = r.ReadToEnd().Trim();
            }
        }
        catch { }

        if (ourCertificate != null)
        {
            foreach (byte[] cert in SigningCertificates(info))
                if (cert.AsSpan().SequenceEqual(ourCertificate)) result.SignedWithOurKey = true;
        }
        return result;
    }

    private static IEnumerable<byte[]> SigningCertificates(PackageInfo info)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(28))
        {
            var signers = info.SigningInfo?.GetApkContentsSigners();
            if (signers != null) foreach (var s in signers) yield return s.ToByteArray()!;
        }
        else
        {
#pragma warning disable CA1422
            if (info.Signatures != null) foreach (var s in info.Signatures) yield return s.ToByteArray()!;
#pragma warning restore CA1422
        }
    }

    /// <summary>
    /// Unpacks a backup of the game (.apks from SAI and similar, or .xapk) straight into the patch's output folder,
    /// under the names the patching core expects there, so the core does not copy the 700 MB again.
    /// </summary>
    public static Parts UnpackBackup(Stream backup, string name, string outDir, Action<string> log)
    {
        Directory.CreateDirectory(outDir);
        var parts = new Parts { Origin = name };
        using var zip = new ZipArchive(backup, ZipArchiveMode.Read);
        var apks = zip.Entries.Where(e => e.FullName.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) && !e.FullName.Contains('/')).ToList();
        if (apks.Count == 0)
            throw new UserError("That file holds no .apk files. Pick the game's .apks or .xapk backup.");

        ZipArchiveEntry? main = apks.FirstOrDefault(e => e.Name.Equals(Package + ".apk", StringComparison.OrdinalIgnoreCase))
                             ?? apks.FirstOrDefault(e => e.Name.Equals("base.apk", StringComparison.OrdinalIgnoreCase));
        ZipArchiveEntry? arm64 = apks.FirstOrDefault(e => e.Name.Contains("arm64", StringComparison.OrdinalIgnoreCase));
        if (main == null || arm64 == null)
            throw new UserError("The backup is missing the game's main file or its arm64 code file. Found: " + string.Join(", ", apks.Select(e => e.Name)));

        foreach (var e in apks)
        {
            // names inside the output folder: base.apk for the main one, the others with a leading "split_" removed
            string target = e == main ? "base.apk" : (e.Name.StartsWith("split_", StringComparison.OrdinalIgnoreCase) ? e.Name.Substring(6) : e.Name);
            string path = Path.Combine(outDir, target);
            log($"Unpacking {e.Name} ({e.Length / 1048576} MB)");
            e.ExtractToFile(path, true);
            if (e == main) parts.Base = path;
            else if (e == arm64) parts.Arm64 = path;
            else parts.Extra.Add(path);
        }
        return parts;
    }

    public static string? VersionOf(Context context, string apk)
    {
        try { return context.PackageManager!.GetPackageArchiveInfo(apk, 0)?.VersionName; }
        catch { return null; }
    }
}

/// <summary>A problem to show the player as it is, without a stack trace.</summary>
internal sealed class UserError(string message) : Exception(message);
