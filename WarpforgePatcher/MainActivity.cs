using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Widget;
using MelonLoader.Installer.Core;
using Org.BouncyCastle.OpenSsl;

namespace WarpforgePatcher;

[Activity(Label = "Warpforge Patcher", MainLauncher = true, Exported = true,
          ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.KeyboardHidden,
          ScreenOrientation = ScreenOrientation.Portrait)]
public class MainActivity : Activity, IPatchLogger
{
    private const int PickBackup = 1, SaveKey = 2, LoadKey = 3;

    private TextView status = null!, logView = null!;
    private ScrollView logScroll = null!;
    private Button allowFiles = null!, allowInstall = null!, patch = null!, fromBackup = null!, modOnly = null!, shareLogs = null!, saveKey = null!, loadKey = null!;
    private CheckBox still = null!;
    private bool busy;
    private StreamWriter? logFile;

    private string WorkDir => System.IO.Path.Combine(FilesDir!.AbsolutePath, "work");
    private string KeyFile => System.IO.Path.Combine(FilesDir!.AbsolutePath, "signing-key.pem");
    private string LogPath => System.IO.Path.Combine(FilesDir!.AbsolutePath, "patcher.log");

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window!.AddFlags(WindowManagerFlags.KeepScreenOn);
        BuildUi();
        Log($"Warpforge Patcher {PackageManager!.GetPackageInfo(PackageName!, 0)!.VersionName} on {Build.Manufacturer} {Build.Model}, Android {Build.VERSION.Release}, chip {(OperatingSystem.IsAndroidVersionAtLeast(31) ? Build.SocManufacturer + " " + Build.SocModel : Build.Hardware)}, GPU {GpuName()}; still picture {(still.Checked ? "on" : "off")} by default");
    }

    protected override void OnResume()
    {
        base.OnResume();
        if (!busy) Refresh();
    }

    // ---------------------------------------------------------------- screen

    private void BuildUi()
    {
        int pad = Dp(16);
        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetPadding(pad, pad, pad, pad);
        root.SetBackgroundColor(Color.ParseColor("#16181B"));
        var top = new LinearLayout(this) { Orientation = Orientation.Vertical };

        var title = Text("Warpforge Revival Patcher", 22, "#E8D9A8");
        title.SetTypeface(null, TypefaceStyle.Bold);
        top.AddView(title);
        top.AddView(Text("Puts the mod loader and the Warpforge Revival mod into your own copy of the game, on this phone. Nothing from the game is downloaded or shared.", 13, "#9AA0A6"));

        status = Text("", 14, "#E6E6E6");
        status.SetPadding(0, Dp(12), 0, Dp(8));
        top.AddView(status);

        allowFiles = Btn("Allow access to the loader's folder", () => AskFilesAccess());
        allowInstall = Btn("Allow this app to install the game", () => AskInstallAccess());
        still = new CheckBox(this) { Text = "Fix black menu after loading (shows a still picture instead of the loading video)" };
        still.SetTextColor(Color.ParseColor("#E6E6E6"));
        still.Checked = StillRecommended();
        patch = Btn("Patch and install", () => _ = PatchInstalled());
        fromBackup = Btn("Patch from a backup file (.apks / .xapk)", () => PickFile(PickBackup, "*/*", null));
        modOnly = Btn("Update the mod only", () => _ = UpdateModOnly());
        shareLogs = Btn("Share logs", ShareLogs);
        saveKey = Btn("Save a copy of the signing key", () => PickFile(SaveKey, "application/x-pem-file", "warpforge-patcher-key.pem"));
        loadKey = Btn("Load a saved signing key", () => PickFile(LoadKey, "*/*", null));

        foreach (var v in new View[] { allowFiles, allowInstall, still, patch, fromBackup, modOnly, shareLogs, saveKey, loadKey }) top.AddView(v);
        var topScroll = new ScrollView(this);
        topScroll.AddView(top);
        root.AddView(topScroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 3f));

        logView = Text("", 11, "#B8C4B0");
        logView.Typeface = Typeface.Monospace;
        logView.SetTextIsSelectable(true);
        logScroll = new ScrollView(this);
        logScroll.AddView(logView);
        logScroll.SetBackgroundColor(Color.ParseColor("#0E0F11"));
        logScroll.SetPadding(Dp(8), Dp(8), Dp(8), Dp(8));
        var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 2f) { TopMargin = Dp(12) };
        root.AddView(logScroll, lp);
        SetContentView(root);
    }

    private TextView Text(string text, float sp, string color)
    {
        var t = new TextView(this) { Text = text };
        t.SetTextSize(Android.Util.ComplexUnitType.Sp, sp);
        t.SetTextColor(Color.ParseColor(color));
        return t;
    }

    private Button Btn(string text, Action onClick)
    {
        var b = new Button(this) { Text = text };
        b.SetAllCaps(false);
        b.Click += (_, _) => { if (!busy) onClick(); };
        return b;
    }

    private int Dp(int dp) => (int)(dp * Resources!.DisplayMetrics!.Density + 0.5f);

    /// <summary>
    /// Phones whose video and graphics hardware is the Pixel 6 Pro's kind, where the loading video leaves the menu black
    /// (black-menu-devices.md). Owner, 2026-10-11: on by default for
    ///  - Google's Tensor Pixels (Pixel 6 and later; Unity reproduced the black screen on the Pixel 6, 7, 8a and 9 Pro);
    ///  - Samsungs with the same hardware: an Exynos chip (Samsung's video decoder, as in Tensor) with an Arm Mali GPU
    ///    (Note 20, S20/S21 outside the US, Exynos A-series). Exynos phones with an AMD Xclipse GPU stay off.
    /// Older Pixels and Snapdragon Samsungs stay off.
    /// </summary>
    private static bool StillRecommended()
    {
        string socMaker = OperatingSystem.IsAndroidVersionAtLeast(31) ? (Build.SocManufacturer ?? "") : "";
        if (socMaker.Equals("Google", StringComparison.OrdinalIgnoreCase))
            return true;      // every Tensor phone ships with Android 12 or newer, which reports the chip's maker
        var m = System.Text.RegularExpressions.Regex.Match(Build.Model ?? "", @"^Pixel (\d+)");
        if ((Build.Manufacturer ?? "").Equals("Google", StringComparison.OrdinalIgnoreCase) && m.Success && int.Parse(m.Groups[1].Value) >= 6)
            return true;

        if (!(Build.Manufacturer ?? "").Equals("samsung", StringComparison.OrdinalIgnoreCase)) return false;
        string hw = (Build.Hardware ?? "") + " " + (Build.Board ?? "");
        bool exynos = socMaker.Contains("Samsung", StringComparison.OrdinalIgnoreCase)
                   || hw.Contains("exynos", StringComparison.OrdinalIgnoreCase)
                   || System.Text.RegularExpressions.Regex.IsMatch(hw, @"\b(s5e|universal)\d", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!exynos) return false;
        string gpu = GpuName();
        return gpu.Contains("Mali", StringComparison.OrdinalIgnoreCase) || gpu.Contains("Immortalis", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The GPU's name (GL_RENDERER), from a throwaway 1x1 OpenGL ES context; "" if that fails.</summary>
    private static string GpuName()
    {
        var display = Android.Opengl.EGL14.EglNoDisplay;
        var surface = Android.Opengl.EGL14.EglNoSurface;
        var context = Android.Opengl.EGL14.EglNoContext;
        try
        {
            display = Android.Opengl.EGL14.EglGetDisplay(Android.Opengl.EGL14.EglDefaultDisplay);
            var version = new int[2];
            if (!Android.Opengl.EGL14.EglInitialize(display, version, 0, version, 1)) return "";
            int[] attribs = { Android.Opengl.EGL14.EglRenderableType, Android.Opengl.EGL14.EglOpenglEs2Bit,
                              Android.Opengl.EGL14.EglSurfaceType, Android.Opengl.EGL14.EglPbufferBit, Android.Opengl.EGL14.EglNone };
            var configs = new Android.Opengl.EGLConfig[1];
            var count = new int[1];
            if (!Android.Opengl.EGL14.EglChooseConfig(display, attribs, 0, configs, 0, 1, count, 0) || count[0] == 0) return "";
            context = Android.Opengl.EGL14.EglCreateContext(display, configs[0], Android.Opengl.EGL14.EglNoContext,
                new[] { Android.Opengl.EGL14.EglContextClientVersion, 2, Android.Opengl.EGL14.EglNone }, 0);
            surface = Android.Opengl.EGL14.EglCreatePbufferSurface(display, configs[0],
                new[] { Android.Opengl.EGL14.EglWidth, 1, Android.Opengl.EGL14.EglHeight, 1, Android.Opengl.EGL14.EglNone }, 0);
            if (!Android.Opengl.EGL14.EglMakeCurrent(display, surface, surface, context)) return "";
            return Android.Opengl.GLES20.GlGetString(Android.Opengl.GLES20.GlRenderer) ?? "";
        }
        catch { return ""; }
        finally
        {
            try
            {
                if (display != Android.Opengl.EGL14.EglNoDisplay)
                {
                    Android.Opengl.EGL14.EglMakeCurrent(display, Android.Opengl.EGL14.EglNoSurface, Android.Opengl.EGL14.EglNoSurface, Android.Opengl.EGL14.EglNoContext);
                    if (surface != Android.Opengl.EGL14.EglNoSurface) Android.Opengl.EGL14.EglDestroySurface(display, surface);
                    if (context != Android.Opengl.EGL14.EglNoContext) Android.Opengl.EGL14.EglDestroyContext(display, context);
                    Android.Opengl.EGL14.EglTerminate(display);
                }
            }
            catch { }
        }
    }

    private void Refresh()
    {
        bool files = HasFilesAccess(), install = PackageManager!.CanRequestPackageInstalls();
        var game = GameFiles.FindInstalled(this, OurCertificate());
        var lines = new List<string>();
        if (game == null) lines.Add("Game: not installed. Install it from the Play Store first, or patch from a backup file.");
        else
        {
            lines.Add($"Game: version {game.Version}" + (game.Patched ? ", patched" + (game.PatchDate.Length > 0 ? $" ({game.PatchDate[..Math.Min(10, game.PatchDate.Length)]})" : "") : ", original (not patched yet)"));
            if (game.Patched) lines.Add(game.SignedWithOurKey ? "Patched by this app: a new patch installs over it." : "Patched elsewhere (PC): patching here removes it first (the app asks).");
            if (game.Version != GameFiles.GameVersion) lines.Add($"Warning: the mod is made for game version {GameFiles.GameVersion}.");
        }
        if (files)
        {
            lines.Add(File.Exists(ModFiles.ModFile) ? $"Mod: on the phone ({File.GetLastWriteTime(ModFiles.ModFile):yyyy-MM-dd}); the game keeps it up to date." : "Mod: not on the phone yet.");
            string? video = ModFiles.CurrentSetting("LoadingVideo");
            if (video != null) lines.Add($"Loading video setting: {video}" + (File.Exists(ModFiles.StillFile) ? " (still picture present)" : ""));
        }
        status.Text = string.Join("\n", lines);

        allowFiles.Visibility = files ? ViewStates.Gone : ViewStates.Visible;
        allowInstall.Visibility = install ? ViewStates.Gone : ViewStates.Visible;
        patch.Text = game == null ? "Patch and install (needs the installed game)" : game.Patched ? "Patch again and install" : "Patch and install";
        patch.Enabled = game != null && files && install;
        fromBackup.Enabled = files && install;
        modOnly.Visibility = game?.Patched == true ? ViewStates.Visible : ViewStates.Gone;
        modOnly.Enabled = files;
        saveKey.Enabled = File.Exists(KeyFile);
    }

    // ---------------------------------------------------------------- permissions

    private bool HasFilesAccess() => OperatingSystem.IsAndroidVersionAtLeast(30)
        ? Android.OS.Environment.IsExternalStorageManager
        : CheckSelfPermission(Android.Manifest.Permission.WriteExternalStorage) == Permission.Granted;

    private void AskFilesAccess()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            try { StartActivity(new Intent(Settings.ActionManageAppAllFilesAccessPermission, Android.Net.Uri.Parse("package:" + PackageName))); }
            catch { StartActivity(new Intent(Settings.ActionManageAllFilesAccessPermission)); }
        }
        else RequestPermissions(new[] { Android.Manifest.Permission.WriteExternalStorage, Android.Manifest.Permission.ReadExternalStorage }, 7);
    }

    private void AskInstallAccess() =>
        StartActivity(new Intent(Settings.ActionManageUnknownAppSources, Android.Net.Uri.Parse("package:" + PackageName)));

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        Refresh();
    }

    // ---------------------------------------------------------------- the jobs

    private Task PatchInstalled()
    {
        var game = GameFiles.FindInstalled(this, OurCertificate());
        if (game == null) { Say("The game is not installed."); return Task.CompletedTask; }
        return RunJob("Patching the installed game", async () =>
        {
            ResetWork();
            await PatchAndInstall(game.Parts, game, fromBackup: false);
        });
    }

    private Task PatchBackup(Android.Net.Uri uri)
    {
        return RunJob("Patching from a backup file", async () =>
        {
            ResetWork();
            string name = DisplayName(uri);
            using var pfd = ContentResolver!.OpenFileDescriptor(uri, "r") ?? throw new UserError("Could not open that file.");
            long size = pfd.StatSize;
            CheckSpace(size * 2 + 400L * 1048576);
            GameFiles.Parts parts;
            using (var stream = new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(pfd.Fd, ownsHandle: false), FileAccess.Read))
                parts = await Task.Run(() => GameFiles.UnpackBackup(stream, name, System.IO.Path.Combine(WorkDir, "out"), Log));
            var game = GameFiles.FindInstalled(this, OurCertificate());
            await PatchAndInstall(parts, game, fromBackup: true);
        });
    }

    private async Task PatchAndInstall(GameFiles.Parts parts, GameFiles.Installed? installed, bool fromBackup)
    {
        if (parts.Base.Length == 0 || parts.Arm64.Length == 0)
            throw new UserError("The game's arm64 code file is missing. This phone's copy of the game may not be the arm64 version.");
        if (!parts.HasDataPack)
            throw new UserError("The game's data pack (UnityDataAssetPack, about 700 MB) is missing; the patched game would not start. " +
                                (fromBackup ? "Make the backup again with all its parts." : "Start the original game once so Android downloads it, then try again."));
        string version = GameFiles.VersionOf(this, parts.Base) ?? "?";
        Log($"Game files from the {parts.Origin}: version {version}, {parts.TotalBytes / 1048576} MB in {2 + parts.Extra.Count} parts");
        if (version != GameFiles.GameVersion)
            throw new UserError($"This is game version {version}; the loader and the mod are made for {GameFiles.GameVersion}.");
        if (!fromBackup) CheckSpace(parts.TotalBytes + 400L * 1048576);

        // asked before anything else happens, so nothing is spent on patching if the player would rather not remove the game
        if (installed != null && !installed.SignedWithOurKey && !await ConfirmRemoval(installed))
            throw new UserError("Stopped. Nothing was changed.");

        // everything that needs the original files or the network first, before anything on the phone changes
        byte[]? stillPicture = null;
        if (still.Checked)
        {
            try { stillPicture = await Task.Run(() => ModFiles.MakeStill(parts.Base, WorkDir, Log)); }
            catch (Exception e) { Log("Could not make the still picture: " + e.Message + " (the game is patched anyway)"); }
        }
        var (modPath, modVersion) = await ModFiles.DownloadMod(WorkDir, Log);

        string loaderZip = System.IO.Path.Combine(WorkDir, "melon_data.zip");
        await LoaderPackage.Get(this, loaderZip, Log);
        string[] apks = await Task.Run(() => PatchJob.Run(this, parts, WorkDir, KeyFile, loaderZip, this));
        Log("Patched: " + string.Join(", ", apks.Select(System.IO.Path.GetFileName)));

        // the original (or a copy patched with another key) has to go before Android accepts this one
        installed = GameFiles.FindInstalled(this, OurCertificate());
        if (installed != null && !installed.SignedWithOurKey)
        {
            if (!removalConfirmed && !await ConfirmRemoval(installed))
                throw new UserError("Stopped. Nothing was changed; the patched files are kept until the next patch.");
            var (removed, why) = await PackageOps.Uninstall(this, Log);
            if (!removed || GameFiles.FindInstalled(this, null) != null)
                throw new UserError("The game was not removed. " + why);
            Log("Original game removed");
        }

        Log("Installing the patched game; Android asks you to confirm");
        var (ok, message) = await PackageOps.Install(this, apks, Log);
        if (!ok) throw new UserError("The patched game was not installed. " + message + " The patched files are kept; press the button again to retry.");
        Log("Patched game installed");

        ModFiles.PutMod(modPath, Log);
        if (stillPicture != null)
        {
            ModFiles.PutStill(stillPicture, Log);
            ModFiles.SetSetting("LoadingVideo", "Still", Log);
        }
        ResetWork();
        Log($"Done. Mod {modVersion} is in place.");
        await Ask("All done",
            "Start Warpforge from your home screen.\n\n" +
            "- If it asks for \"All files access\", allow it: the loader keeps its files there.\n" +
            "- The first start after patching sits on a black screen for up to a minute while the loader sets itself up. Later starts are quicker.\n" +
            "- The mod updates itself from then on.", "OK", cancel: null);
    }

    private Task UpdateModOnly() => RunJob("Updating the mod", async () =>
    {
        Directory.CreateDirectory(WorkDir);
        var (path, version) = await ModFiles.DownloadMod(WorkDir, Log);
        ModFiles.PutMod(path, Log);
        if (still.Checked && !File.Exists(ModFiles.StillFile))
        {
            var game = GameFiles.FindInstalled(this, null);
            if (game != null)
            {
                try
                {
                    var rgba = await Task.Run(() => ModFiles.MakeStill(game.Parts.Base, WorkDir, Log));
                    ModFiles.PutStill(rgba, Log);
                }
                catch (Exception e) { Log("Could not make the still picture: " + e.Message); }
            }
        }
        if (still.Checked && File.Exists(ModFiles.StillFile)) ModFiles.SetSetting("LoadingVideo", "Still", Log);
        ResetWork();
        Log($"Done: mod {version} is in place. Close the game fully and start it again.");
    });

    private async Task RunJob(string what, Func<Task> job)
    {
        busy = true;
        removalConfirmed = false;
        SetButtons(false);
        Log("---- " + what);
        try { await job(); }
        catch (UserError e) { Log("Stopped: " + e.Message); Say(e.Message); }
        catch (HttpRequestException e) { Log("Network: " + e); Say("Could not reach the server (" + e.Message + "). Check the connection and try again. Nothing on the phone was changed by this step."); }
        catch (Exception e) { Log("ERROR " + e); Say("Something went wrong: " + e.Message + "\n\nThe log at the bottom has the details; \"Share logs\" sends it."); }
        finally
        {
            busy = false;
            SetButtons(true);
            Refresh();
        }
    }

    private void SetButtons(bool on)
    {
        foreach (var b in new View[] { allowFiles, allowInstall, still, patch, fromBackup, modOnly, saveKey, loadKey }) b.Enabled = on;
    }

    private void ResetWork()
    {
        try { if (Directory.Exists(WorkDir)) Directory.Delete(WorkDir, true); } catch (Exception e) { Log("Could not clear the work folder: " + e.Message); }
        Directory.CreateDirectory(WorkDir);
    }

    private void CheckSpace(long needed)
    {
        long free = new StatFs(FilesDir!.AbsolutePath).AvailableBytes;
        Log($"Free space: {free / 1048576} MB, needed about {needed / 1048576} MB");
        if (free < needed)
            throw new UserError($"Not enough free space: about {needed / 1073741824.0:F1} GB is needed while patching, {free / 1073741824.0:F1} GB is free. It is given back afterwards.");
    }

    private bool removalConfirmed;

    /// <summary>
    /// The installed game has to be removed before Android accepts a copy signed with this app's key. Its downloaded
    /// files go with it; the revival server hands them out again (owner, 2026-10-11). Asked once per patch, up front.
    /// </summary>
    private async Task<bool> ConfirmRemoval(GameFiles.Installed installed)
    {
        bool ok = await Ask("Remove the game first?",
            (installed.Patched ? "The game on this phone was patched with a different key (on a PC)." : "The game on this phone is the original from the store.") +
            " Android only accepts the copy patched here after that one is removed, so the app removes it after patching. Android asks you to confirm.\n\n" +
            "Your account, decks and progress are on the server and are not affected. The game's downloaded files come back from the server. The MelonLoader folder stays.",
            "Continue", "Stop");
        if (ok) Log("Player agreed to remove the installed game after patching");
        removalConfirmed = ok;
        return ok;
    }

    // ---------------------------------------------------------------- signing key

    /// <summary>The certificate of this app's saved signing key (null before the first patch made one).</summary>
    private byte[]? OurCertificate()
    {
        try
        {
            if (!File.Exists(KeyFile)) return null;
            using var reader = new StringReader(File.ReadAllText(KeyFile));
            var pem = new PemReader(reader);
            object o;
            while ((o = pem.ReadObject()) != null)
                if (o is Org.BouncyCastle.X509.X509Certificate cert) return cert.GetEncoded();
        }
        catch (Exception e) { Log("Could not read the signing key: " + e.Message); }
        return null;
    }

    private void SaveKeyTo(Android.Net.Uri uri)
    {
        using var output = ContentResolver!.OpenOutputStream(uri, "wt") ?? throw new UserError("Could not write there.");
        var bytes = File.ReadAllBytes(KeyFile);
        output.Write(bytes, 0, bytes.Length);
        Log("Signing key saved. Keep that file private: anyone with it can make an update of your patched game.");
        Say("Key saved. Keep that file private. If this app is ever reinstalled, \"Load a saved signing key\" puts it back, so the game can be patched again without removing it.");
    }

    private void LoadKeyFrom(Android.Net.Uri uri)
    {
        using var input = ContentResolver!.OpenInputStream(uri) ?? throw new UserError("Could not read that file.");
        using var r = new StreamReader(input);
        string text = r.ReadToEnd();
        if (text.Length > 20000 || !text.Contains("BEGIN CERTIFICATE") || !text.Contains("PRIVATE KEY"))
            throw new UserError("That is not a signing key saved by this app.");
        if (File.Exists(KeyFile)) File.Copy(KeyFile, KeyFile + ".previous", true);
        File.WriteAllText(KeyFile, text);
        Log("Signing key loaded" + (File.Exists(KeyFile + ".previous") ? " (the one before is kept as signing-key.pem.previous)" : ""));
        Refresh();
    }

    // ---------------------------------------------------------------- files picked by the player

    private void PickFile(int request, string mime, string? createName)
    {
        var intent = new Intent(createName != null ? Intent.ActionCreateDocument : Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType(mime);
        if (createName != null) intent.PutExtra(Intent.ExtraTitle, createName);
        StartActivityForResult(intent, request);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        var uri = data?.Data;
        if (resultCode != Result.Ok || uri == null) return;
        try
        {
            switch (requestCode)
            {
                case PickBackup: _ = PatchBackup(uri); break;
                case SaveKey: SaveKeyTo(uri); break;
                case LoadKey: LoadKeyFrom(uri); break;
            }
        }
        catch (UserError e) { Say(e.Message); }
        catch (Exception e) { Log("ERROR " + e); Say(e.Message); }
    }

    private string DisplayName(Android.Net.Uri uri)
    {
        try
        {
            using var c = ContentResolver!.Query(uri, new[] { Android.Provider.OpenableColumns.DisplayName }, null, null, null);
            if (c != null && c.MoveToFirst()) return c.GetString(0) ?? "backup";
        }
        catch { }
        return "backup";
    }

    private void ShareLogs()
    {
        try
        {
            logFile?.Flush();
            string zip = ModFiles.BundleLogs(CacheDir!.AbsolutePath, LogPath);
            var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(this, PackageName + ".files", new Java.IO.File(zip));
            var send = new Intent(Intent.ActionSend);
            send.SetType("application/zip");
            send.PutExtra(Intent.ExtraStream, uri);
            send.AddFlags(ActivityFlags.GrantReadUriPermission);
            StartActivity(Intent.CreateChooser(send, "Share Warpforge logs"));
        }
        catch (Exception e) { Log("Could not share the logs: " + e.Message); Say("Could not share the logs: " + e.Message); }
    }

    // ---------------------------------------------------------------- messages and the log

    private void Say(string message) => RunOnUiThread(() =>
        new AlertDialog.Builder(this).SetMessage(message)!.SetPositiveButton("OK", (EventHandler<DialogClickEventArgs>?)null)!.Show());

    private Task<bool> Ask(string title, string message, string ok, string? cancel = "Cancel")
    {
        var done = new TaskCompletionSource<bool>();
        RunOnUiThread(() =>
        {
            var b = new AlertDialog.Builder(this).SetTitle(title)!.SetMessage(message)!.SetCancelable(false)!
                .SetPositiveButton(ok, (_, _) => done.TrySetResult(true))!;
            if (cancel != null) b.SetNegativeButton(cancel, (_, _) => done.TrySetResult(false));
            b.Show();
        });
        return done.Task;
    }

    public void Log(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        try
        {
            if (logFile == null)
            {
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 2_000_000) File.Delete(LogPath);
                logFile = new StreamWriter(new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
            }
            logFile.WriteLine(line);
        }
        catch { }
        RunOnUiThread(() =>
        {
            logView.Append(line + "\n");
            if (logView.Text!.Length > 60000) logView.Text = logView.Text[^40000..];
            logScroll.Post(() => logScroll.FullScroll(FocusSearchDirection.Down));
        });
    }
}
