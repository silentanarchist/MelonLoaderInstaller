#!/bin/sh
# Builds the Warpforge Patcher APK.
#   ./build.sh <path to the Android repo (warpforge-revival-mod-android)> [keystore] [keystore password file]
# The loader package is taken from the Android repo's patcher/tool/melon_data.zip.part0-2 (joined here).
# Without a keystore the build is signed with a debug key (fine for testing; updates of the app then need
# the same debug key). Needs the .NET 8 SDK with the android workload and an Android SDK (ANDROID_HOME).
set -e
here="$(cd "$(dirname "$0")" && pwd)"
repo="${1:?path to warpforge-revival-mod-android}"
mkdir -p "$here/loader"
cat "$repo"/patcher/tool/melon_data.zip.part* > "$here/loader/melon_data.zip"
sign=""
if [ -n "$2" ]; then
  pass="$(cat "${3:?password file}")"
  sign="-p:AndroidKeyStore=true -p:AndroidSigningKeyStore=$2 -p:AndroidSigningKeyAlias=warpforgepatcher -p:AndroidSigningStorePass=$pass -p:AndroidSigningKeyPass=$pass"
fi
dotnet publish "$here/WarpforgePatcher.csproj" -c Release -f net8.0-android -p:AndroidSdkDirectory="${ANDROID_HOME:-/opt/android-sdk}" $sign
ls -la "$here"/bin/Release/net8.0-android/publish/*-Signed.apk
