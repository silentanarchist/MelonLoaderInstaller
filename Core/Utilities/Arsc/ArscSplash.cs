using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MelonLoader.Installer.Core.Utilities.Arsc;

/// <summary>
/// WF Revival change (2026-10-28): the smallest edit of a compiled resource table (resources.arsc) that gives a
/// Unity game a start-up picture. Unity's Android player shows the drawable named "unity_static_splash" (centred,
/// only scaled down, on the colour "staticSplashScreenBackgroundColor") from the moment its activity is created
/// until the engine draws its first frame, when the manifest's unity.splash-enable is true. A game built without a
/// static splash has no such drawable, so the screen stays black while the loader starts. This adds it:
/// <list type="bullet">
/// <item>one string to the table's value pool (the PNG's path) and one to the package's key pool (the name),
/// both appended at the end, so no existing index changes;</item>
/// <item>one entry at the end of the drawable type: its type spec and every config's entry table grow by one
/// (an empty slot, except in the default config, which gets the entry), so every existing resource id stays
/// the same;</item>
/// <item>and, optionally, the background colour's value changed in place.</item>
/// </list>
/// Only plain tables are handled (UTF-8 key/value pools without styles, dense type chunks with 32-bit offsets,
/// one package); anything else is refused, and the caller leaves the APK as it was.
/// </summary>
public static class ArscSplash
{
    public const string SplashName = "unity_static_splash";
    public const string SplashPath = "res/drawable/unity_static_splash.png";
    public const string ColorName = "staticSplashScreenBackgroundColor";

    private const ushort ResTableType = 0x0002, ResStringPoolType = 0x0001, ResTablePackageType = 0x0200;
    private const ushort ResTableTypeType = 0x0201, ResTableTypeSpecType = 0x0202;
    private const uint NoEntry = 0xFFFFFFFF;
    private const uint Utf8Flag = 1 << 8;

    /// <returns>the edited table, and what was done (for the log)</returns>
    public static byte[] Apply(byte[] table, uint? backgroundArgb, out string report)
    {
        if (U16(table, 0) != ResTableType) throw new InvalidDataException("not a resource table");
        if (U32(table, 8) != 1) throw new InvalidDataException("more than one package");
        int tableHeader = U16(table, 2);

        var pkgStart = tableHeader + (int)U32(table, tableHeader + 4);           // the package follows the value pool
        if (U16(table, pkgStart) != ResTablePackageType) throw new InvalidDataException("package not found after the value pool");
        int pkgHeader = U16(table, pkgStart + 2);
        int typeStringsOff = (int)U32(table, pkgStart + 8 + 4 + 256);
        int keyStringsOff = (int)U32(table, pkgStart + 8 + 4 + 256 + 8);

        var typeNames = ReadPool(table, pkgStart + typeStringsOff);
        var keyNames = ReadPool(table, pkgStart + keyStringsOff);
        int drawableId = typeNames.IndexOf("drawable") + 1;
        int colorId = typeNames.IndexOf("color") + 1;
        if (drawableId == 0) throw new InvalidDataException("no drawable type");

        var sb = new StringBuilder();
        int existingKey = keyNames.IndexOf(SplashName);
        byte[] result = table;

        if (existingKey >= 0 && HasEntry(table, pkgStart, pkgHeader, drawableId, existingKey))
            sb.Append("start-up picture already in the resource table; ");
        else
        {
            int valueIndex = (int)U32(table, tableHeader + 8);                   // the new string's index = old count
            byte[] valuePool = AppendString(Slice(table, tableHeader, (int)U32(table, tableHeader + 4)), SplashPath);

            int keyIndex = existingKey >= 0 ? existingKey : keyNames.Count;
            int keyPoolStart = pkgStart + keyStringsOff;
            int keyPoolSize = (int)U32(table, keyPoolStart + 4);
            byte[] keyPool = existingKey >= 0 ? Slice(table, keyPoolStart, keyPoolSize) : AppendString(Slice(table, keyPoolStart, keyPoolSize), SplashName);

            // the package: its header, the type pool, the new key pool, then every chunk after the key pool
            var pkg = new MemoryStream();
            pkg.Write(table, pkgStart, keyStringsOff);                          // header + type pool (offsets unchanged)
            pkg.Write(keyPool);
            int p = keyPoolStart + keyPoolSize, pkgEnd = pkgStart + (int)U32(table, pkgStart + 4);
            int newEntry = -1, defaults = 0, types = 0;
            while (p < pkgEnd)
            {
                ushort ct = U16(table, p); int ch = U16(table, p + 2); int cs = (int)U32(table, p + 4);
                if (ct == ResTableTypeSpecType && table[p + 8] == drawableId)
                {
                    int count = (int)U32(table, p + 12);
                    newEntry = count;
                    var spec = new MemoryStream();
                    spec.Write(table, p, cs);
                    spec.Write(new byte[4]);                                    // no config-change flags
                    var b = spec.ToArray();
                    W32(b, 4, (uint)b.Length); W32(b, 12, (uint)(count + 1));
                    pkg.Write(b);
                }
                else if (ct == ResTableTypeType && table[p + 8] == drawableId)
                {
                    if (table[p + 9] != 0) throw new InvalidDataException("drawable type uses sparse or 16-bit entries");
                    int count = (int)U32(table, p + 12), entriesStart = (int)U32(table, p + 16);
                    if (entriesStart != ch + count * 4) throw new InvalidDataException("unexpected drawable type layout");
                    bool isDefault = IsDefaultConfig(table, p + 20);
                    var t = new MemoryStream();
                    t.Write(table, p, ch + count * 4);                         // header + old offsets
                    int dataLen = cs - entriesStart;
                    W(t, isDefault ? (uint)dataLen : NoEntry);                  // the new slot
                    t.Write(table, p + entriesStart, dataLen);                  // old entries, unchanged
                    if (isDefault)
                    {
                        // ResTable_entry (size 8, flags 0, key) + Res_value (size 8, res0 0, TYPE_STRING, value index)
                        W16(t, 8); W16(t, 0); W(t, (uint)keyIndex);
                        W16(t, 8); t.WriteByte(0); t.WriteByte(0x03); W(t, (uint)valueIndex);
                        defaults++;
                    }
                    var b = t.ToArray();
                    W32(b, 4, (uint)b.Length); W32(b, 12, (uint)(count + 1)); W32(b, 16, (uint)(entriesStart + 4));
                    pkg.Write(b);
                    types++;
                }
                else pkg.Write(table, p, cs);
                p += cs;
            }
            if (newEntry < 0 || defaults != 1) throw new InvalidDataException($"drawable type spec or default config not found ({defaults} default configs)");
            var pkgBytes = pkg.ToArray();
            W32(pkgBytes, 4, (uint)pkgBytes.Length);

            var outMs = new MemoryStream();
            outMs.Write(table, 0, tableHeader);
            outMs.Write(valuePool);
            outMs.Write(pkgBytes);
            result = outMs.ToArray();
            W32(result, 4, (uint)result.Length);
            sb.Append($"start-up picture added (resource 0x7f{drawableId:x2}{newEntry:x4}, {types} drawable configs); ");
        }

        if (backgroundArgb.HasValue && colorId > 0)
        {
            int key = keyNames.IndexOf(ColorName);
            int n = key < 0 ? 0 : SetColor(result, colorId, key, backgroundArgb.Value);
            sb.Append(n > 0 ? $"background colour set in {n} config(s)" : "background colour not found (the default black is used)");
        }
        report = sb.ToString().TrimEnd(' ', ';');
        return result;
    }

    // ---- reading ----

    private static bool IsDefaultConfig(byte[] d, int cfg)
    {
        int size = (int)U32(d, cfg);
        for (int i = 4; i < size; i++) if (d[cfg + i] != 0) return false;
        return true;
    }

    private static IEnumerable<(int start, int header, int size)> PackageChunks(byte[] d, int pkgStart, int pkgHeader)
    {
        int end = pkgStart + (int)U32(d, pkgStart + 4);
        for (int p = pkgStart + pkgHeader; p < end; p += (int)U32(d, p + 4))
            yield return (p, U16(d, p + 2), (int)U32(d, p + 4));
    }

    private static bool HasEntry(byte[] d, int pkgStart, int pkgHeader, int typeId, int key)
    {
        foreach (var (p, ch, cs) in PackageChunks(d, pkgStart, pkgHeader))
        {
            if (U16(d, p) != ResTableTypeType || d[p + 8] != typeId || d[p + 9] != 0) continue;
            int count = (int)U32(d, p + 12), es = (int)U32(d, p + 16);
            for (int i = 0; i < count; i++)
            {
                uint off = U32(d, p + ch + i * 4);
                if (off != NoEntry && U32(d, p + es + (int)off + 4) == key) return true;
            }
        }
        return false;
    }

    private static int SetColor(byte[] d, int typeId, int key, uint argb)
    {
        int pkgStart = U16(d, 2) + (int)U32(d, U16(d, 2) + 4), n = 0;
        foreach (var (p, ch, cs) in PackageChunks(d, pkgStart, U16(d, pkgStart + 2)))
        {
            if (U16(d, p) != ResTableTypeType || d[p + 8] != typeId || d[p + 9] != 0) continue;
            int count = (int)U32(d, p + 12), es = (int)U32(d, p + 16);
            for (int i = 0; i < count; i++)
            {
                uint off = U32(d, p + ch + i * 4);
                if (off == NoEntry) continue;
                int e = p + es + (int)off;
                if (U32(d, e + 4) != key || (U16(d, e + 2) & 1) != 0) continue;  // not ours, or a complex entry
                int v = e + U16(d, e);
                byte type = d[v + 3];
                if (type >= 0x1c && type <= 0x1f) { W32(d, v + 4, argb); d[v + 3] = 0x1c; n++; }   // a colour literal
            }
        }
        return n;
    }

    /// <summary>The strings of a UTF-8 string pool (with no styles).</summary>
    private static List<string> ReadPool(byte[] d, int at)
    {
        if (U16(d, at) != ResStringPoolType) throw new InvalidDataException("string pool expected");
        int count = (int)U32(d, at + 8), styles = (int)U32(d, at + 12);
        uint flags = U32(d, at + 16); int stringsStart = (int)U32(d, at + 20);
        var list = new List<string>(count);
        if ((flags & Utf8Flag) == 0)
        {
            for (int i = 0; i < count; i++)
            {
                int s = at + stringsStart + (int)U32(d, at + 28 + i * 4);
                int len = U16(d, s); s += 2;
                if ((len & 0x8000) != 0) { len = ((len & 0x7FFF) << 16) | U16(d, s); s += 2; }
                list.Add(Encoding.Unicode.GetString(d, s, len * 2));
            }
            return list;
        }
        for (int i = 0; i < count; i++)
        {
            int s = at + stringsStart + (int)U32(d, at + 28 + i * 4);
            SkipLen8(d, ref s); int len = ReadLen8(d, ref s);
            list.Add(Encoding.UTF8.GetString(d, s, len));
        }
        _ = styles;
        return list;
    }

    // ---- writing ----

    /// <summary>A copy of a UTF-8 string pool (no styles) with one string added at the end.</summary>
    private static byte[] AppendString(byte[] pool, string value)
    {
        int count = (int)U32(pool, 8), styles = (int)U32(pool, 12);
        uint flags = U32(pool, 16); int stringsStart = (int)U32(pool, 20);
        if (styles != 0 || U32(pool, 24) != 0) throw new InvalidDataException("string pool with styles");
        if ((flags & Utf8Flag) == 0) throw new InvalidDataException("UTF-16 string pool");
        if ((flags & 1) != 0) throw new InvalidDataException("sorted string pool");
        int header = U16(pool, 2);
        if (stringsStart != header + count * 4) throw new InvalidDataException("unexpected string pool layout");

        byte[] data = Slice(pool, stringsStart, pool.Length - stringsStart);   // old strings (and their padding)
        byte[] utf8 = Encoding.UTF8.GetBytes(value);
        if (value.Length > 0x7F || utf8.Length > 0x7F) throw new InvalidDataException("string too long");
        var ms = new MemoryStream();
        ms.Write(pool, 0, header + count * 4);                                  // header + old offsets
        W(ms, (uint)data.Length);                                               // new offset: after the old data
        ms.Write(data);
        ms.WriteByte((byte)value.Length); ms.WriteByte((byte)utf8.Length); ms.Write(utf8); ms.WriteByte(0);
        while (ms.Length % 4 != 0) ms.WriteByte(0);
        var b = ms.ToArray();
        W32(b, 4, (uint)b.Length); W32(b, 8, (uint)(count + 1)); W32(b, 20, (uint)(stringsStart + 4));
        return b;
    }

    private static void SkipLen8(byte[] d, ref int s) { if ((d[s] & 0x80) != 0) s += 2; else s += 1; }
    private static int ReadLen8(byte[] d, ref int s)
    {
        int len = d[s];
        if ((len & 0x80) != 0) { len = ((len & 0x7F) << 8) | d[s + 1]; s += 2; } else s += 1;
        return len;
    }

    private static byte[] Slice(byte[] d, int at, int len) { var b = new byte[len]; Buffer.BlockCopy(d, at, b, 0, len); return b; }
    private static ushort U16(byte[] d, int at) => BitConverter.ToUInt16(d, at);
    private static uint U32(byte[] d, int at) => BitConverter.ToUInt32(d, at);
    private static void W32(byte[] d, int at, uint v) => BitConverter.GetBytes(v).CopyTo(d, at);
    private static void W(Stream s, uint v) => s.Write(BitConverter.GetBytes(v));
    private static void W16(Stream s, ushort v) => s.Write(BitConverter.GetBytes(v));
}
