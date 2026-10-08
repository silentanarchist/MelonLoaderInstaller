> **About this branch (`warpforge-revival`).** This is a fork changed for the
> [Warpforge Revival Android project](https://github.com/silentanarchist/warpforge-revival-mod-android).
> AI (Anthropic's Claude) was used to modify it to fit the needs of that project's mod. The changes
> have been tried on only a few devices and are not part of, or endorsed by, the original project.
> Everything below this note is the original project's own README.

# [Lemon]Loader Installer
Installs MelonLoader onto APKs

# Redesign Licensing/Credits
- [ReVanced Manager](https://github.com/ReVanced/revanced-manager) for design inspiration
- [Inter](https://fonts.google.com/specimen/Inter), under the [Open Font License](https://openfontlicense.org/)
- [ADB/Platform Tools](https://developer.android.com/tools/releases/platform-tools) and [AAPT2](https://developer.android.com/tools/aapt2), under the [Android Software Development Kit License Agreement](https://developer.android.com/tools/releases/platform-tools)

# Licensing/Credits
- [SAI](https://github.com/Aefyr/SAI), under the [GPL-3.0 license](https://github.com/Aefyr/SAI/blob/master/LICENSE)
- [SplitAPKInstall](https://github.com/nkalra0123/splitapkinstall), under the [Apache-2.0 license](https://github.com/nkalra0123/splitapkinstall/blob/master/LICENSE)
- [Apkifier](https://github.com/emulamer/Apkifier) under the [MIT license](https://github.com/emulamer/Apkifier/blob/master/LICENSE)
- [QuestPatcher](https://github.com/Lauriethefish/QuestPatcher) under the [zlib License](https://github.com/Lauriethefish/QuestPatcher/blob/main/LICENSE)
  - Changes (in accordance with the License; all used files are located in `Core/Utilities/Signing` and `Core/Utilities/Axml`)
    - Merged ApkSigner's V2 code into my APKSigner class
    - Changed namespace of helper classes to match other files and folder hierarchy position
    - QuestPatcher.Axml had no changes