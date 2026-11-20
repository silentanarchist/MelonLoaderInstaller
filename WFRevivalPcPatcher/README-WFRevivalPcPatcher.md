# WF Revival PC Patcher

Patches the player's own backup of Warhammer 40,000: Warpforge 1.35.0 on a PC, for **Husk on iPhone** (or to copy to an
Android device), with the same patching core (`../Core`) and the same pinned loader package as the phone app
(`../WarpforgePatcher`). Nothing is installed anywhere and no game content is downloaded or shipped.

0.1.0 is a console program (owner's Husk test, 2026-11-20); a one-window app may follow (see the project doc
`pc-patcher-handover.md`).

## Use
Drag the game's `.xapk` / `.apks` backup (or the folder with the three APKs) onto `WFRevivalPcPatcher.exe`, or put the
backup next to it and double-click, or start it and drag the file into the window. It writes a folder
`WF Revival patched` next to the backup with:
- `Warpforge-WF-Revival-patched.apks`: the three patched APKs as one bundle (Husk, SAI)
- `base.apk`, `config.arm64_v8a.apk`, `UnityDataAssetPack.apk`: the same, separately
- `WFRevival.Android.dll`: the current Android mod, checked against the server's SHA-256 and the WF Revival project's
  Ed25519 signature (`ModSignature.cs`, shared with the phone app)
- `READ ME - where the files go.txt`, `patch-log.txt`

## Same settings as the phone app (`WarpforgePatcher/PatchJob.cs`)
The game's own libunity.so kept (empty "local" Unity package), the start-up picture (`LEMON_LAUNCH_SPLASH`), one kept
signing key (`LEMON_SIGNING_KEY`, in `%LOCALAPPDATA%\WFRevivalPcPatcher\signing-key.pem`), the loader package at the
commit and SHA-256 of `WarpforgePatcher/LoaderPackage.cs` (**keep the two in step**), cached in
`%LOCALAPPDATA%\WFRevivalPcPatcher\loader`.

## Build
    dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true \
      -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none
(`-r osx-arm64` / `osx-x64` for a Mac build later; `linux-x64` was used to test it.)
