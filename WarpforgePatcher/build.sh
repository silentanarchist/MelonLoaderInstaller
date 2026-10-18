#!/bin/sh
# Builds the Warpforge Patcher APK.
#   ./build.sh <path to the Android repo (warpforge-revival-mod-android)> [keystore] [keystore password file]
# The app downloads the loader package from the Android repo at the commit pinned in LoaderPackage.cs.
# BUNDLE_LOADER=1 ./build.sh ... puts it inside the app instead (joined here from patcher/tool/melon_data.zip.part0-2;
# makes the APK about 47 MB bigger).
# Without a keystore the build is signed with a debug key (fine for testing; updates of the app then need
# the same debug key). Needs the .NET 8 SDK with the android workload and an Android SDK (ANDROID_HOME).
set -e
here="$(cd "$(dirname "$0")" && pwd)"
repo="${1:?path to warpforge-revival-mod-android}"
mkdir -p "$here/loader"
cat "$repo"/patcher/tool/melon_data.zip.part* > "$here/loader/melon_data.zip"
# translations: the mod repo's localization/patcher/ is the source (LOCALIZATION=<that folder>, or the mod repo next to
# this one); the copies in Localization/ are what gets built in, and are committed so the source builds on its own
loc="${LOCALIZATION:-$here/../../warpforge-revival-mod/localization/patcher}"
if [ -d "$loc" ]; then
  mkdir -p "$here/Localization"
  cp "$loc"/*.json "$here/Localization/"
  echo "translations from $loc: $(ls "$loc" | tr '\n' ' ')"
else
  echo "translations: $loc not found; building with the copies in Localization/"
fi
sign=""
if [ -n "$2" ]; then
  pass="$(cat "${3:?password file}")"
  sign="-p:AndroidKeyStore=true -p:AndroidSigningKeyStore=$2 -p:AndroidSigningKeyAlias=warpforgepatcher -p:AndroidSigningStorePass=$pass -p:AndroidSigningKeyPass=$pass"
fi
bundle=""
[ -n "$BUNDLE_LOADER" ] && bundle="-p:BundleLoader=true"
dotnet publish "$here/WarpforgePatcher.csproj" $bundle -c Release -f net8.0-android -p:AndroidSdkDirectory="${ANDROID_HOME:-/opt/android-sdk}" $sign
ls -la "$here"/bin/Release/net8.0-android/publish/*-Signed.apk
