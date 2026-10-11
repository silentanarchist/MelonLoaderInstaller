using Android.Content;
using Android.Database;
using Android.Provider;

namespace WarpforgePatcher;

/// <summary>
/// Exporting the game's downloaded files (Android/data/com.Everguild.WarhammerWarpforge/files) before the game is
/// removed: they cannot be downloaded again now that the game's servers are closed, and removing the game erases them.
///
/// Where this app can see them:
///  - Android 10 and older: read directly (storage permission);
///  - Android 11 and 12: only through a folder the player grants once in Android's own picker (Storage Access
///    Framework); the picker opens on the game's folder;
///  - Android 13 and newer: Android shows no app another app's Android/data folder, not even through the picker.
///    Only a PC (USB file transfer) can copy them there, or the game itself when it runs with the mod: a copy the
///    mod makes in its own folder (UserData/WarpforgeRevival/original-cache) is exported from here too.
/// The export goes to Download/Warpforge game files backup &lt;date&gt;/ on the phone's storage, which outlives the game.
/// </summary>
internal static class CacheBackup
{
    public static string GameDataDir => Path.Combine(Android.OS.Environment.ExternalStorageDirectory!.AbsolutePath, "Android", "data", GameFiles.Package, "files");
    public static string ModCopyDir => Path.Combine(GameFiles.LoaderDir, "UserData", "WarpforgeRevival", "original-cache");
    private const string GameFolderDocId = "primary:Android/data/" + GameFiles.Package;
    private const string Authority = "com.android.externalstorage.documents";

    public enum Source { None, Direct, Granted, ModCopy }

    /// <summary>Android 11 and 12: the game's data folder can be granted to this app through the picker.</summary>
    public static bool CanAskForGrant => OperatingSystem.IsAndroidVersionAtLeast(30) && !OperatingSystem.IsAndroidVersionAtLeast(33);

    public static bool HasFiles(string dir)
    {
        try { return Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any(); }
        catch { return false; }
    }

    /// <summary>The best place this app can read the files from right now (granted tree in <paramref name="grant"/>).</summary>
    public static Source Find(Context context, out Android.Net.Uri? grant)
    {
        grant = GrantedTree(context);
        if (HasFiles(GameDataDir)) return Source.Direct;
        if (grant != null) return Source.Granted;
        if (HasFiles(ModCopyDir)) return Source.ModCopy;
        return Source.None;
    }

    private static Android.Net.Uri? GrantedTree(Context context)
    {
        foreach (var p in context.ContentResolver!.PersistedUriPermissions)
        {
            var uri = p.Uri;
            if (uri == null || !p.IsReadPermission) continue;
            try
            {
                if (DocumentsContract.GetTreeDocumentId(uri) == GameFolderDocId) return uri;
            }
            catch { }
        }
        return null;
    }

    /// <summary>Android's folder picker, opened on the game's data folder (Android 11 and 12).</summary>
    public static Intent GrantIntent()
    {
        var intent = new Intent(Intent.ActionOpenDocumentTree);
        var initial = DocumentsContract.BuildDocumentUri(Authority, GameFolderDocId);
        intent.PutExtra(DocumentsContract.ExtraInitialUri, initial);
        intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantPersistableUriPermission);
        return intent;
    }

    /// <summary>Keeps the player's grant if it is the game's folder; false for any other folder.</summary>
    public static bool AcceptGrant(Context context, Android.Net.Uri tree)
    {
        string id;
        try { id = DocumentsContract.GetTreeDocumentId(tree) ?? ""; } catch { return false; }
        if (id != GameFolderDocId) return false;
        context.ContentResolver!.TakePersistableUriPermission(tree, ActivityFlags.GrantReadUriPermission);
        return true;
    }

    /// <summary>Copies the files to Download/Warpforge game files backup &lt;date&gt;/ and checks every size. Returns that folder.</summary>
    public static string Export(Context context, Source source, Android.Net.Uri? grant, Action<string> log)
    {
        string target = Path.Combine(Android.OS.Environment.GetExternalStoragePublicDirectory(Android.OS.Environment.DirectoryDownloads)!.AbsolutePath,
                                     $"Warpforge game files backup {DateTime.Now:yyyy-MM-dd HHmm}");
        Directory.CreateDirectory(target);
        long bytes = 0; int files = 0;
        byte[] buffer = new byte[1 << 20];

        void CopyStream(Stream src, string dest, long expected)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using (var dst = File.Create(dest))
            {
                int n;
                while ((n = src.Read(buffer, 0, buffer.Length)) > 0) dst.Write(buffer, 0, n);
            }
            long got = new FileInfo(dest).Length;
            if (expected >= 0 && got != expected)
                throw new UserError($"The copy of {Path.GetFileName(dest)} came out {got} bytes instead of {expected}. The backup is incomplete; try again.");
            bytes += got; files++;
            if (files % 200 == 0) log($"  {files} files, {bytes / 1048576} MB so far");
        }

        if (source is Source.Direct or Source.ModCopy)
        {
            string from = source == Source.Direct ? GameDataDir : ModCopyDir;
            long need = Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
            CheckSpace(target, need);
            log($"Exporting {need / 1048576} MB from {from}");
            foreach (string f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                using var src = File.OpenRead(f);
                CopyStream(src, Path.Combine(target, Path.GetRelativePath(from, f)), new FileInfo(f).Length);
            }
        }
        else if (source == Source.Granted && grant != null)
        {
            // the granted folder is Android/data/<game>; the files are in its "files" subfolder
            string rootId = DocumentsContract.GetTreeDocumentId(grant)!;
            var entries = new List<(string id, string rel, long size)>();
            Walk(context, grant, rootId, "", entries);
            var wanted = entries.Where(e => e.rel.StartsWith("files/", StringComparison.Ordinal)).ToList();
            if (wanted.Count == 0) throw new UserError("Android showed the game's folder, but it holds no downloaded files.");
            long need = wanted.Sum(e => Math.Max(0, e.size));
            CheckSpace(target, need);
            log($"Exporting {need / 1048576} MB through the granted folder");
            foreach (var e in wanted)
            {
                var uri = DocumentsContract.BuildDocumentUriUsingTree(grant, e.id)!;
                using var src = context.ContentResolver!.OpenInputStream(uri) ?? throw new UserError("Android would not open " + e.rel);
                CopyStream(src, Path.Combine(target, e.rel.Substring("files/".Length)), e.size);
            }
        }
        else throw new UserError("The game's files are not visible to this app.");

        File.WriteAllText(Path.Combine(target, "ABOUT THIS BACKUP.txt"),
            $"Warpforge's downloaded files ({GameFiles.Package}), exported by Warpforge Patcher on {DateTime.Now:yyyy-MM-dd HH:mm}.\r\n" +
            $"Source: {source}. {files} files, {bytes} bytes.\r\n" +
            "They belong in Android/data/com.Everguild.WarhammerWarpforge/files on the phone. They come from the game's servers, " +
            "which are closed, so keep this copy (also on a PC). It holds game content: do not share or publish it.\r\n");
        log($"Exported {files} files ({bytes / 1048576} MB) to {target}");
        return target;
    }

    private static void Walk(Context context, Android.Net.Uri tree, string parentId, string rel, List<(string, string, long)> into)
    {
        var children = DocumentsContract.BuildChildDocumentsUriUsingTree(tree, parentId)!;
        string[] cols = { DocumentsContract.Document.ColumnDocumentId, DocumentsContract.Document.ColumnDisplayName,
                          DocumentsContract.Document.ColumnMimeType, DocumentsContract.Document.ColumnSize };
        using ICursor? c = context.ContentResolver!.Query(children, cols, null, null, null);
        if (c == null) return;
        while (c.MoveToNext())
        {
            string id = c.GetString(0) ?? "", name = c.GetString(1) ?? "", mime = c.GetString(2) ?? "";
            long size = c.IsNull(3) ? -1 : c.GetLong(3);
            if (name.Length == 0 || name.Contains('/') || name == "." || name == "..") continue;
            string path = rel.Length == 0 ? name : rel + "/" + name;
            if (mime == DocumentsContract.Document.MimeTypeDir) Walk(context, tree, id, path, into);
            else into.Add((id, path, size));
        }
    }

    private static void CheckSpace(string dir, long need)
    {
        long free = new Android.OS.StatFs(dir).AvailableBytes;
        if (free < need + 50L * 1048576)
            throw new UserError($"Not enough free space for the backup: {need / 1048576} MB needed, {free / 1048576} MB free.");
    }
}
