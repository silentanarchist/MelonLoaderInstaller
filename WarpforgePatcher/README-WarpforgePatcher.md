# Warpforge Revival Patcher (Android app)

Patches the player's own copy of *Warhammer 40,000: Warpforge* on the phone itself, without a PC,
cable or adb, for the [Warpforge Revival](https://github.com/silentanarchist/warpforge-revival-mod-android)
project. It does what the PC scripts in that repo's `patcher/` folder do (`1 - patch.bat`,
`2 - install on phone.bat`, `3 - put mod on phone.bat`, and the Still picture of
`6 - fix loading video.bat` with `7 - loading video setting.bat`).

**No game content is shipped or downloaded.** The game's files are read from the copy installed on
the phone (or from the player's own `.apks` / `.xapk` backup). The app carries only the loader
package (`melon_data.zip`, built from the project's MelonLoader fork) and downloads the mod from the
revival server.

## How it patches

It runs **the same patching core as `LemonPatch.exe`** (`../Core`, this fork's LemonLoader installer
code), set up exactly as `LemonPatch-source.cs` does: the loader package, a "local" Unity package with
no libraries so the game keeps its own `libunity.so` (6000.2.6f2), the manifest changes (including
`allowNativeHeapPointerTagging=false`), and one signing key kept between runs (`LEMON_SIGNING_KEY`).
Checked on the game's own files (2026-10-11): the base and arm64 APKs it produces hold the same
entries with the same contents and the same `AndroidManifest.xml` as the PC tool's output.

Change made to the core for this (it applies to the PC tool too): aligning and signing now stream
file-to-file (`Core/Utilities/Signing/APKAligner.cs`, `APKSigner.SignV2`). The original held each
APK in memory several times over, about 2 GB for the 740 MB data pack, too much for a phone. The
output is byte-for-byte the same as before (checked on the game's base and arm64 APKs and an 810 MB
test APK); peak memory for the large file went from out-of-memory to about 50 MB.

## What the app does, in order

1. Finds the installed game (`PackageManager`: `sourceDir` + `splitSourceDirs`), or unpacks a picked
   backup straight into its work folder. Refuses a game version other than 1.35.0, or a copy without
   the data pack.
2. Checks free space (the game's size plus about 400 MB while patching; given back afterwards).
3. If "Fix black menu after loading" is ticked (ticked by default on Pixel 6 and later: Google Tensor phones): takes the
   loading video out of `assets/bin/Data/sharedassets0.resource` (same box walk as
   `tool/extract-intro.ps1`), its frame 200 ms before the end with `MediaMetadataRetriever`, scaled to
   1440x810, bottom row first, raw RGBA (4,665,600 bytes).
4. Downloads the current Android mod from the server (`/api/v1/mod/manifest?platform=android`, the
   checksum must match). The server is the `ServerUrl` in the mod's settings file when there is one,
   else `warpforge.silentanarchist.com`, always over https on port 8780.
5. Patches with the core.
6. If the installed game is not signed with this app's key (the store original, or a PC-patched copy),
   explains and asks, then removes it through Android's own uninstall prompt. Progress is on the
   server; the game's downloaded files download again; the `MelonLoader` folder stays.
7. Installs the patched APKs together through a `PackageInstaller` session (Android asks to confirm).
8. Puts the mod in `MelonLoader/com.Everguild.WarhammerWarpforge/Mods/` (older test files moved aside
   like the PC script), and, with the box ticked, the Still picture in
   `UserData/WarpforgeRevival/content/intro-last.rgba` and `LoadingVideo = "Still"` in
   `UserData/WarpforgeRevival.cfg` (only that key is changed).

Other buttons: **Update the mod only**; **Share logs** (one zip: `Latest.log`, the mod's settings
file, the app's own log; never the game login); **Save a copy of / Load a saved signing key** (so a
reinstalled patcher can still patch over the game without removing it).

## Signing keys (keep both private)

- **The game's signing key** is made on the first patch and kept in the app's private storage
  (`signing-key.pem`). It is a different key from the PC's `tool/signing-key.pem`, so a game patched
  on the PC has to be removed once before the phone-patched one installs, and the other way round.
  The app says so before it removes anything.
- **The app's own signing key** (`warpforge-patcher-app.keystore`, alias `warpforgepatcher`, with its
  password file) signs the patcher APK. Every update of the app must be signed with it, or Android
  refuses the update (and removing the app loses the game key unless it was saved).
  Never commit either key.

## Building

Needs the .NET 8 SDK with the `android` workload, and an Android SDK (platform 34, build-tools).

```
./build.sh <path to warpforge-revival-mod-android> [keystore] [password file]
```

It joins `patcher/tool/melon_data.zip.part0-2` from the Android repo into `loader/melon_data.zip`
(git-ignored) and writes `bin/Release/net8.0-android/publish/com.silentanarchist.warpforgepatcher-Signed.apk`.
Without a keystore the APK is signed with a debug key.

Licence: GPL-3.0, like the rest of this installer (see `../LICENSE`).
