using System.IO.Compression;
using MelonLoader.Installer.Core;

namespace WarpforgePatcher;

/// <summary>
/// Runs the same patching core as the PC's LemonPatch.exe, set up the same way (LemonPatch-source.cs in the
/// Android repo): the loader package, the game's own libunity.so kept, one saved signing key.
/// </summary>
internal static class PatchJob
{
    /// <param name="workDir">the app's own work folder (emptied first)</param>
    /// <param name="parts">the game's files; with alreadyInOut they were unpacked into workDir/out under the core's names</param>
    /// <returns>the patched APKs, ready to install</returns>
    public static string[] Run(Android.Content.Context context, GameFiles.Parts parts, string workDir, string keyFile, string loaderZip, IPatchLogger log)
    {
        string outDir = Path.Combine(workDir, "out");
        string temp = Path.Combine(workDir, "temp");
        if (Directory.Exists(temp)) Directory.Delete(temp, true);
        Directory.CreateDirectory(temp);
        Directory.CreateDirectory(outDir);

        // the loader package (LoaderPackage: downloaded and checked before patching); the core deletes the copy it is given
        string melon = Path.Combine(temp, "melon_data.zip");
        File.Copy(loaderZip, melon, true);

        // Keep the game's own libunity.so: the core would otherwise put in a generic 6000.2.6f1 build, which the
        // game's 6000.2.6f2 data refuses. A "local" Unity package with no libraries in it prevents the swap.
        string unityZip = Path.Combine(temp, "unity.zip");
        using (var z = ZipFile.Open(unityZip, ZipArchiveMode.Create))
        {
            var e = z.CreateEntry("arm64-v8a/keep-the-games-own-libunity.txt");
            using var w = new StreamWriter(e.Open());
            w.Write("intentionally empty");
        }
        log.Log("Keeping the game's own engine library (libunity.so)");

        // one signing key for every run (generated on the first, kept in this app's private storage)
        Environment.SetEnvironmentVariable("LEMON_SIGNING_KEY", keyFile);

        var args = new PatchArguments(parts.Base, parts.Arm64, parts.Extra.ToArray(), outDir, temp, melon, unityZip,
            AssetRipper.Primitives.UnityVersion.Parse(GameFiles.UnityVersion), GameFiles.Package, true);
        bool ok = new Patcher(args, log).Run();
        try { Directory.Delete(temp, true); } catch { }
        if (!ok) throw new UserError("err.patchFailed", "Patching did not finish. The log below says where it stopped.");

        string[] apks = Directory.GetFiles(outDir, "*.apk");
        if (apks.Length < 3) throw new UserError("err.patchFewFiles", "Patching left fewer files than expected: {files}", ("files", string.Join(", ", apks.Select(Path.GetFileName))));
        return apks;
    }
}
