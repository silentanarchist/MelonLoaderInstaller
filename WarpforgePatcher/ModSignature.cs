using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace WarpforgePatcher;

/// <summary>
/// The mod's own signature check (silentanarchist/warpforge-revival-mod, WarpforgeRevival/ModSignature.cs at fd81ba6),
/// so the patcher installs only mod builds the project owner signed. From mod 0.14.0 every build on the server carries
/// the owner's Ed25519 signature at its end:
///   [the build] [signature, 64 bytes] [key id, 8 bytes] ["WFRSIG01", 8 bytes]
/// The signed message is "Warpforge Revival mod v1\n" + file name + "\n" + version + "\n" followed by the SHA-256 of the
/// build (everything before the 80-byte trailer); the version is read from the build itself, as the server reads it.
/// The key id is the first 8 bytes of the SHA-256 of the public key. The server's checksum alone does not help if the
/// server itself is broken into; the signature does, as the signing key never leaves the owner's computer.
/// The file is installed exactly as downloaded, trailer included (the server checks the whole file's hash at sign-in).
/// </summary>
internal static class ModSignature
{
    /// <summary>The Android mod's file name: "WF Revival" builds (mod 0.15.0 on, after the 2026-10-21 rename) are signed
    /// as WFRevival.Android.dll, older ones as WarpforgeRevival.Android.dll. Both are accepted.</summary>
    public const string AndroidFileName = "WFRevival.Android.dll";
    public const string OldAndroidFileName = "WarpforgeRevival.Android.dll";
    public static readonly string[] AndroidFileNames = { AndroidFileName, OldAndroidFileName };
    /// <summary>The signed message's first line: kept as "Warpforge Revival mod v1" or moved to a WF Revival label; both accepted.</summary>
    private static readonly string[] Headers = { "Warpforge Revival mod v1", "WF Revival mod v1", "WF Revival mod v2" };
    private const string Magic = "WFRSIG01";
    private const int Trailer = 64 + 8 + 8;

    /// <summary>The owner's public keys (base64, not secret): the everyday key and a spare kept offline. Same as the mod's.</summary>
    private static readonly string[] OwnerKeys =
    {
        "v0GNRYRtzVn43h1UPJiNR1pLwvHlo4tpZUDI0q5NMg0=",
        "+STVSQrWCM6CEz1ouUaizh/+HK/PnlAMd4d+3593u6Q=",
    };

    private static readonly Regex VersionInBuild = new(@"(?:Warpforge Revival|WF Revival)[\s\S](\d+(?:\.\d+){1,3})(-[a-z][a-z0-9]*)?", RegexOptions.CultureInvariant);

    /// <summary>The version a build names for itself ("0.14.0-a"), read the way the server reads it; null if none.</summary>
    public static string? VersionOf(byte[] build, int length)
    {
        var m = VersionInBuild.Match(Encoding.Latin1.GetString(build, 0, length));
        return m.Success ? m.Groups[1].Value + m.Groups[2].Value : null;
    }

    /// <summary>The version of a file on the device (a signed or an older unsigned build); null if none can be read.</summary>
    public static string? VersionOfFile(string path)
    {
        try
        {
            byte[] data = File.ReadAllBytes(path);
            int length = data.Length > Trailer && Encoding.ASCII.GetString(data, data.Length - 8, 8) == Magic ? data.Length - Trailer : data.Length;
            return VersionOf(data, length);
        }
        catch { return null; }
    }

    /// <summary>Null when the build is signed by one of the owner's keys (version then says which build it is), else why it was refused.</summary>
    public static string? Check(byte[] data, string fileName, out string? version) => Check(data, new[] { fileName }, OwnerKeys, out version, out _);

    /// <summary>Like Check, for a build signed under any of the given file names; signedAs says which one.</summary>
    public static string? Check(byte[] data, string[] fileNames, out string? version, out string? signedAs) => Check(data, fileNames, OwnerKeys, out version, out signedAs);

    internal static string? Check(byte[] data, string fileName, string[] keys, out string? version) => Check(data, new[] { fileName }, keys, out version, out _);

    internal static string? Check(byte[] data, string[] fileNames, string[] keys, out string? version, out string? signedAs)
    {
        version = null;
        signedAs = null;
        if (data == null || data.Length <= Trailer) return "the download is too small";
        int body = data.Length - Trailer;
        if (Encoding.ASCII.GetString(data, data.Length - 8, 8) != Magic) return "the download is not signed";
        var sig = new byte[64];
        Array.Copy(data, body, sig, 0, 64);
        var keyId = new byte[8];
        Array.Copy(data, body + 64, keyId, 0, 8);
        version = VersionOf(data, body);
        if (version == null) return "the download names no version";
        byte[] hash = SHA256.HashData(new ReadOnlySpan<byte>(data, 0, body));
        foreach (var k in keys)
        {
            byte[] key;
            try { key = Convert.FromBase64String(k); } catch { continue; }
            if (key.Length != 32) continue;
            if (!SHA256.HashData(key).AsSpan(0, 8).SequenceEqual(keyId)) continue;
            foreach (string fileName in fileNames)
                foreach (string header in Headers)
                    if (Ed25519.Verify(key, Message(header, fileName, version, hash), sig)) { signedAs = fileName; return null; }
            return "the signature does not match";
        }
        return "the download is signed with a key this app does not know";
    }

    internal static byte[] Message(string fileName, string version, byte[] sha256) => Message(Headers[0], fileName, version, sha256);

    internal static byte[] Message(string header, string fileName, string version, byte[] sha256)
    {
        var head = Encoding.UTF8.GetBytes(header + "\n" + fileName + "\n" + version + "\n");
        var all = new byte[head.Length + sha256.Length];
        head.CopyTo(all, 0);
        sha256.CopyTo(all, head.Length);
        return all;
    }

    /// <summary>Compares "0.14.0-a" style versions by their numbers (the letter suffix is the platform, not ordered).</summary>
    public static int CompareVersions(string a, string b)
    {
        static Version Num(string s) => Version.TryParse(s.Split('-')[0], out var v) ? v : new Version(0, 0);
        return Num(a).CompareTo(Num(b));
    }
}
