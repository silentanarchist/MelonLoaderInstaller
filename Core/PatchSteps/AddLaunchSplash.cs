using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using MelonLoader.Installer.Core.Utilities.Arsc;

namespace MelonLoader.Installer.Core.PatchSteps;

/// <summary>
/// WF Revival change (2026-10-28): a start-up picture instead of a black screen while the loader starts. Only runs
/// when LEMON_LAUNCH_SPLASH names a PNG (the WF Revival Patcher app sets it; LEMON_LAUNCH_SPLASH_COLOR may give the
/// background as RRGGBB). Puts the PNG in the base APK as res/drawable/unity_static_splash.png and adds the matching
/// resource (ArscSplash), which Unity's player shows until the game draws its first frame. If anything about the
/// resource table is unexpected, the APK is left exactly as it was and patching carries on without the picture.
/// </summary>
internal class AddLaunchSplash : IPatchStep
{
    public bool Run(Patcher patcher)
    {
        string? png = Environment.GetEnvironmentVariable("LEMON_LAUNCH_SPLASH");
        if (string.IsNullOrEmpty(png)) return true;
        if (!File.Exists(png)) { patcher.Logger.Log("Start-up picture: file not found, skipped"); return true; }

        uint? color = null;
        string? rgb = Environment.GetEnvironmentVariable("LEMON_LAUNCH_SPLASH_COLOR");
        if (!string.IsNullOrEmpty(rgb) && rgb.Length == 6 && uint.TryParse(rgb, System.Globalization.NumberStyles.HexNumber, null, out uint c))
            color = 0xFF000000 | c;

        patcher.Logger.Log("Adding the start-up picture");
        try
        {
            using FileStream zipStream = new(patcher.Info.OutputBaseApkPath, FileMode.Open);
            using ZipArchive archive = new(zipStream, ZipArchiveMode.Update);

            ZipArchiveEntry? arscEntry = archive.GetEntry("resources.arsc");
            if (arscEntry == null) { patcher.Logger.Log("Start-up picture: no resources.arsc, skipped"); return true; }
            byte[] arsc;
            using (var s = arscEntry.Open()) using (var ms = new MemoryStream()) { s.CopyTo(ms); arsc = ms.ToArray(); }

            byte[] edited = ArscSplash.Apply(arsc, color, out string report);    // throws (APK untouched) if unexpected

            // resources.arsc stays uncompressed (Android 11+ requires it; the aligner puts it on a 4-byte boundary)
            arscEntry.Delete();
            var newArsc = archive.CreateEntry("resources.arsc", CompressionLevel.NoCompression);
            using (var s = newArsc.Open()) s.Write(edited);

            archive.GetEntry(ArscSplash.SplashPath)?.Delete();
            var pic = archive.CreateEntry(ArscSplash.SplashPath, CompressionLevel.NoCompression);   // PNGs are stored, like aapt does
            using (var s = pic.Open()) using (var f = File.OpenRead(png)) f.CopyTo(s);

            patcher.Logger.Log("Start-up picture: " + report);
        }
        catch (InvalidDataException e)
        {
            patcher.Logger.Log("Start-up picture skipped (" + e.Message + "); the game starts with a black screen as before");
        }
        return true;
    }
}
