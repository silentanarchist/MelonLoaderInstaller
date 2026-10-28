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

[Activity(Label = "WF Revival Patcher", MainLauncher = true, Exported = true,
          ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.KeyboardHidden,
          ScreenOrientation = ScreenOrientation.Portrait)]
public class MainActivity : Activity, IPatchLogger
{
    private const int PickBackup = 1, SaveKey = 2, LoadKey = 3;

    private TextView status = null!, logView = null!;
    private ScrollView logScroll = null!;
    // The button of the job now running (patch, from-backup or app update). While Android's confirmation is up it reads
    // "Continue: ..." and is the one button left enabled: pressing it shows the confirmation again (after a tap outside
    // it closed it). As soon as Android answers it goes back to its own label and is disabled until the job ends.
    private Button? jobButton;
    private string jobButtonLabel = "";
    private bool continueShown;
    private Button saveArt = null!, checkUpdates = null!, appUpdate = null!, allowFiles = null!, allowInstall = null!, patch = null!, fromBackup = null!, modOnly = null!, stillFix = null!, setServer = null!, shareLogs = null!, saveKey = null!, loadKey = null!;
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
        Loc.Start(this);
        BuildUi();
        ShowEarlierLog();
        PackageOps.ConfirmationState = ShowContinue;
        _ = CheckForAppUpdate(manual: false);
        _ = RefreshTranslations();
        Log($"WF Revival Patcher {PackageManager!.GetPackageInfo(PackageName!, 0)!.VersionName} on {Build.Manufacturer} {Build.Model}, Android {Build.VERSION.Release}, chip {(OperatingSystem.IsAndroidVersionAtLeast(31) ? Build.SocManufacturer + " " + Build.SocModel : Build.Hardware)}, GPU {GpuName()}; still picture {(still.Checked ? "on" : "off")} by default; language {Loc.Language}{(Loc.Chosen(this).Length > 0 ? " (picked)" : " (device)")}");
    }

    protected override void OnDestroy()
    {
        try { logFile?.Dispose(); } catch { }
        logFile = null;
        base.OnDestroy();
    }

    protected override void OnResume()
    {
        base.OnResume();
        if (!busy) Refresh();
    }

    // ---------------------------------------------------------------- screen

    private LinearLayout patcherPage = null!, toolsPage = null!;
    private static int shownPage;           // kept over a language change (the screen is built again)
    private Button language = null!;
    private readonly List<(Button tab, LinearLayout page)> tabButtons = new();

    private void ShowPage(LinearLayout page)
    {
        foreach (var (tab, p) in tabButtons)
        {
            bool on = p == page;
            p.Visibility = on ? ViewStates.Visible : ViewStates.Gone;
            tab.SetBackgroundColor(Color.ParseColor(on ? "#8A6E2F" : "#2B2E33"));
            tab.SetTextColor(Color.ParseColor(on ? "#FFFAF0" : "#B0B5BA"));
        }
    }

    private TextView Section(string text)
    {
        var t = Text(text, 16, "#E8D9A8");
        t.SetTypeface(null, TypefaceStyle.Bold);
        t.SetPadding(0, Dp(14), 0, Dp(2));
        return t;
    }

    private void BuildUi()
    {
        int pad = Dp(16);
        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetPadding(pad, pad, pad, pad);
        root.SetBackgroundColor(Color.ParseColor("#16181B"));
        var top = new LinearLayout(this) { Orientation = Orientation.Vertical };

        // title on the left, "Check for updates" in the top right corner
        var titleRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        titleRow.SetGravity(GravityFlags.CenterVertical);
        var title = Text("WF Revival Patcher", 22, "#E8D9A8");
        title.SetTypeface(null, TypefaceStyle.Bold);
        titleRow.AddView(title, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        checkUpdates = new Button(this) { Text = Loc.T("ui.checkUpdates", "Check for updates") };
        checkUpdates.SetAllCaps(false);
        checkUpdates.SetTextSize(Android.Util.ComplexUnitType.Sp, 15.6f);
        checkUpdates.SetMinHeight(0);
        checkUpdates.SetMinimumHeight(0);
        checkUpdates.SetPadding(Dp(14), Dp(9), Dp(14), Dp(9));
        checkUpdates.Click += (_, _) => { if (!busy) _ = CheckForAppUpdate(manual: true); };
        // the language, under "Check for updates": the language's own name, so anyone can find theirs
        language = new Button(this) { Text = "🌐 " + Loc.NameOf(Loc.Language) };
        language.SetAllCaps(false);
        language.SetTextSize(Android.Util.ComplexUnitType.Sp, 15.6f);
        language.SetMinHeight(0);
        language.SetMinimumHeight(0);
        language.SetPadding(Dp(14), Dp(9), Dp(14), Dp(9));
        language.Click += (_, _) => { if (!busy) ChooseLanguage(); };
        var corner = new LinearLayout(this) { Orientation = Orientation.Vertical };
        corner.SetGravity(GravityFlags.End);
        corner.AddView(checkUpdates, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        corner.AddView(language, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        titleRow.AddView(corner, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        top.AddView(titleRow);
        top.AddView(Text(Loc.T("ui.intro", "Puts the mod loader and the WF Revival mod into your own copy of the game, on this device. Nothing from the game is downloaded or shared."), 13, "#9AA0A6"));

        status = Text("", 14, "#E6E6E6");
        status.SetPadding(0, Dp(12), 0, Dp(8));
        top.AddView(status);

        // shown only when a newer build of this app is on GitHub (AppUpdate)
        appUpdate = Btn(Loc.T("ui.updateApp", "Update this app"), () => _ = UpdateApp());
        appUpdate.Visibility = ViewStates.Gone;
        appUpdate.SetBackgroundColor(Color.ParseColor("#8A6E2F"));
        appUpdate.SetTextColor(Color.ParseColor("#FFFAF0"));
        top.AddView(appUpdate);

        allowFiles = Btn(Loc.T("ui.allowFiles", "Allow access to the loader's folder"), () => AskFilesAccess());
        allowInstall = Btn(Loc.T("ui.allowInstall", "Allow this app to install the game"), () => AskInstallAccess());
        still = new CheckBox(this) { Text = Loc.T("ui.stillBox", "Fix black menu after loading (shows a still picture instead of the loading video)") };
        still.SetTextColor(Color.ParseColor("#E6E6E6"));
        still.Checked = StillRecommended();
        patch = Btn(Loc.T("ui.patch", "Patch and install"), () => _ = PatchInstalled());
        fromBackup = Btn(Loc.T("ui.fromBackup", "Patch from a backup file (.apks / .xapk)"), () => PickFile(PickBackup, "*/*", null));
        modOnly = Btn(Loc.T("ui.modOnly", "Update the mod only"), () => _ = UpdateModOnly());
        stillFix = Btn(Loc.T("tools.fixApply", "Apply the black screen fix"), () => _ = ToggleStillFix());
        setServer = Btn(Loc.T("tools.serverChange", "Change the server"), ChangeServer);
        serverInfo = Text("", 13, "#E6E6E6");
        saveArt = Btn(Loc.T("tools.artSave", "Save card art export to Downloads"), () => _ = SaveArt());
        shareLogs = Btn(Loc.T("ui.shareLogs", "Share logs"), ShareLogs);
        saveKey = Btn(Loc.T("tools.keySave", "Save a copy of the signing key"), () => PickFile(SaveKey, "application/x-pem-file", "warpforge-patcher-key.pem"));
        loadKey = Btn(Loc.T("tools.keyLoad", "Load a saved signing key"), () => PickFile(LoadKey, "*/*", null));

        // two pages under one menu: Patcher (patching and the mod) and Tools (everything else); more pages can be added here
        var tabs = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        tabs.SetPadding(0, Dp(4), 0, Dp(4));
        patcherPage = new LinearLayout(this) { Orientation = Orientation.Vertical };
        toolsPage = new LinearLayout(this) { Orientation = Orientation.Vertical, Visibility = ViewStates.Gone };
        foreach (var (name, page) in new[] { (Loc.T("ui.tabPatcher", "Patcher"), patcherPage), (Loc.T("ui.tabTools", "Tools"), toolsPage) })
        {
            var tab = new Button(this) { Text = name };
            tab.SetAllCaps(false);
            tab.Click += (_, _) => { ShowPage(page); shownPage = page == toolsPage ? 1 : 0; };
            tabButtons.Add((tab, page));
            tabs.AddView(tab, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        }
        top.AddView(tabs);
        top.AddView(patcherPage);
        top.AddView(toolsPage);

        foreach (var v in new View[] { allowFiles, allowInstall, still, patch, fromBackup, modOnly, shareLogs }) patcherPage.AddView(v);

        toolsPage.AddView(Section(Loc.T("tools.fixTitle", "Black screen fix")));
        toolsPage.AddView(Text(Loc.T("tools.fixAbout", "Shows a still picture instead of the loading video, for devices that end up on a black menu after loading. Works on the already patched game; nothing is patched again."), 13, "#9AA0A6"));
        toolsPage.AddView(stillFix);
        toolsPage.AddView(Section(Loc.T("tools.serverTitle", "Server")));
        toolsPage.AddView(Text(Loc.T("tools.serverAbout", "The WF Revival server the mod connects to (ServerUrl in the mod's settings). Close the game fully before changing it."), 13, "#9AA0A6"));
        toolsPage.AddView(serverInfo);
        toolsPage.AddView(setServer);
        toolsPage.AddView(Section(Loc.T("tools.artTitle", "Card art")));
        toolsPage.AddView(Text(Loc.T("tools.artAbout", "Make the export in the game first: Settings > Mod > Export card art."), 13, "#9AA0A6"));
        toolsPage.AddView(saveArt);
        toolsPage.AddView(Section(Loc.T("tools.keyTitle", "Signing key")));
        toolsPage.AddView(Text(Loc.T("tools.keyAbout", "The key this app signs the patched game with. Keep a copy: patching again with another key means removing the game first."), 13, "#9AA0A6"));
        toolsPage.AddView(saveKey);
        toolsPage.AddView(loadKey);
        ShowPage(shownPage == 1 ? toolsPage : patcherPage);
        var topScroll = new ScrollView(this);
        topScroll.AddView(top);
        root.AddView(topScroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 3.8f));

        logView = Text("", 11, "#B8C4B0");
        logView.Typeface = Typeface.Monospace;
        logView.SetTextIsSelectable(true);
        logScroll = new ScrollView(this);
        logScroll.AddView(logView);
        logScroll.SetBackgroundColor(Color.ParseColor("#0E0F11"));
        logScroll.SetPadding(Dp(8), Dp(8), Dp(8), Dp(8));
        var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1.2f) { TopMargin = Dp(12) };
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
        b.Click += (_, _) =>
        {
            if (!busy) onClick();
            else if (continueShown && b == jobButton) PackageOps.ShowConfirmationAgain(this, Log);
        };
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
        if (game == null) lines.Add(Loc.T("status.noGame", "Game: not installed. Install it from the Play Store first, or patch from a backup file."));
        else
        {
            string date = game.PatchDate[..Math.Min(10, game.PatchDate.Length)];
            lines.Add(!game.Patched ? Loc.T("status.gameOriginal", "Game: version {version}, original (not patched yet)", ("version", game.Version))
                    : date.Length > 0 ? Loc.T("status.gamePatchedOn", "Game: version {version}, patched ({date})", ("version", game.Version), ("date", date))
                    : Loc.T("status.gamePatched", "Game: version {version}, patched", ("version", game.Version)));
            if (game.Patched) lines.Add(game.SignedWithOurKey ? Loc.T("status.ourPatch", "Patched by this app: a new patch installs over it.") : Loc.T("status.otherPatch", "Patched elsewhere (PC): patching here removes it first (the app asks)."));
            if (game.Version != GameFiles.GameVersion) lines.Add(Loc.T("status.wrongVersion", "Warning: the mod is made for game version {version}.", ("version", GameFiles.GameVersion)));
        }
        if (files)
        {
            lines.Add(File.Exists(ModFiles.ModFile) ? Loc.T("status.modThere", "Mod: on the device ({date}); the game keeps it up to date.", ("date", File.GetLastWriteTime(ModFiles.ModFile).ToString("yyyy-MM-dd"))) : Loc.T("status.modMissing", "Mod: not on the device yet."));
            string? video = ModFiles.CurrentSetting("LoadingVideo");
            if (video != null) lines.Add(File.Exists(ModFiles.StillFile) ? Loc.T("status.videoStill", "Loading video setting: {value} (still picture present)", ("value", video)) : Loc.T("status.video", "Loading video setting: {value}", ("value", video)));
            var art = new FileInfo(ModFiles.ArtExport);
            if (art.Exists) lines.Add(Loc.T("status.art", "Card art export: made in the game on {date} ({size} MB).", ("date", art.LastWriteTime.ToString("yyyy-MM-dd HH:mm")), ("size", art.Length / 1048576)));
            else if (game?.Patched == true) lines.Add(Loc.T("status.artNone", "Card art export: none yet. In the game: Settings > Mod > Export card art."));
        }

        allowFiles.Visibility = files ? ViewStates.Gone : ViewStates.Visible;
        allowInstall.Visibility = install ? ViewStates.Gone : ViewStates.Visible;
        patch.Text = game == null ? Loc.T("ui.patchNeedsGame", "Patch and install (needs the installed game)") : game.Patched ? Loc.T("ui.patchAgain", "Patch again and install") : Loc.T("ui.patch", "Patch and install");
        bool blocked = updateOffer?.Required == true;     // a required app update comes first
        if (blocked) lines.Add(Loc.T("status.requiredUpdate", "A required update of this app ({version}) is available. Update the app first (button above); patching is paused until then.", ("version", updateOffer!.Version)));
        status.Text = string.Join("\n", lines);
        patch.Enabled = game != null && files && install && !blocked;
        fromBackup.Enabled = files && install && !blocked;
        modOnly.Visibility = game?.Patched == true ? ViewStates.Visible : ViewStates.Gone;
        modOnly.Enabled = files && !blocked;
        // the black screen fix on its own, for an already patched game: on when the mod's LoadingVideo setting is Still
        bool fixOn = files && StillFixOn();
        stillFix.Enabled = game?.Patched == true && files;
        stillFix.Text = fixOn ? Loc.T("tools.fixRemove", "Remove the black screen fix")
                      : game?.Patched != true ? Loc.T("tools.fixNeedsGame", "Apply the black screen fix (needs the patched game)")
                      : !files ? Loc.T("tools.fixNeedsFiles", "Apply the black screen fix (needs access to the loader's folder)")
                      : Loc.T("tools.fixApply", "Apply the black screen fix");
        string? server = files ? ModFiles.CurrentSetting("ServerUrl") : null;
        serverInfo.Text = !files ? Loc.T("tools.serverNeedsFiles", "Needs access to the loader's folder (Patcher page).")
                        : ModFiles.IsCommunityServer(server)
                            ? Loc.T("tools.serverCommunity", "Now: {server} (the community server)", ("server", ModFiles.DefaultServer))
                            : Loc.T("tools.serverNow", "Now: {server}", ("server", server!.Trim()));
        setServer.Enabled = files;
        bool artReady = files && File.Exists(ModFiles.ArtExport);
        saveArt.Enabled = artReady;
        saveArt.Text = artReady ? Loc.T("tools.artSave", "Save card art export to Downloads") : Loc.T("tools.artSaveNone", "Save card art export to Downloads (no export yet)");
        appUpdate.Visibility = updateOffer != null ? ViewStates.Visible : ViewStates.Gone;
        if (updateOffer != null)
            appUpdate.Text = updateOffer.Required ? Loc.T("ui.updateAppToRequired", "Update this app to {version} (required)", ("version", updateOffer.Version))
                                                  : Loc.T("ui.updateAppTo", "Update this app to {version}", ("version", updateOffer.Version));
        saveKey.Enabled = File.Exists(KeyFile);
    }

    // ---------------------------------------------------------------- language

    private static bool rebuilding;     // the screen is being built again for a new language: show the log so far

    /// <summary>The language list: "Automatic" (the device's language) and every language the game ships.</summary>
    private void ChooseLanguage()
    {
        string chosen = Loc.Chosen(this);
        var codes = new List<string> { "" };
        var names = new List<string> { Loc.T("lang.auto", "Automatic (the device's language: {name})", ("name", Loc.NameOf(Loc.DeviceLanguage()))) };
        foreach (var (code, name) in Loc.Languages) { codes.Add(code); names.Add(name); }
        new AlertDialog.Builder(this)
            .SetTitle(Loc.T("lang.title", "Language"))!
            .SetSingleChoiceItems(names.ToArray(), Math.Max(0, codes.IndexOf(chosen)), (sender, e) =>
            {
                (sender as Dialog)?.Dismiss();
                string pick = codes[e.Which];
                if (pick == chosen || busy) return;
                Loc.Choose(this, pick);
                Log("Language: " + (pick.Length > 0 ? pick : "the device's (" + Loc.DeviceLanguage() + ")"));
                rebuilding = true;
                Recreate();
            })!
            .SetNegativeButton(Loc.T("dlg.cancel", "Cancel"), (EventHandler<DialogClickEventArgs>?)null)!
            .Show();
    }

    /// <summary>After a language change the screen is new: put the end of the log file back into it.</summary>
    private void ShowEarlierLog()
    {
        if (!rebuilding) return;
        rebuilding = false;
        try
        {
            logFile?.Flush();
            if (!File.Exists(LogPath)) return;
            string text = File.ReadAllText(LogPath);
            logView.Text = text.Length > 20000 ? text[^20000..] : text;
            logScroll.Post(() => logScroll.FullScroll(FocusSearchDirection.Down));
        }
        catch { }
    }

    /// <summary>A newer copy of this language's texts from the server is used from the next start on.</summary>
    private async Task RefreshTranslations()
    {
        await Task.Delay(3000);
        await Loc.Refresh(Log);
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
        if (game == null) { Say(Loc.T("err.noGame", "The game is not installed.")); return Task.CompletedTask; }
        jobButton = patch;
        return RunJob("Patching the installed game", async () =>
        {
            ResetWork();
            await PatchAndInstall(game.Parts, game, fromBackup: false);
        });
    }

    private Task PatchBackup(Android.Net.Uri uri)
    {
        jobButton = fromBackup;
        return RunJob("Patching from a backup file", async () =>
        {
            ResetWork();
            string name = DisplayName(uri);
            using var pfd = ContentResolver!.OpenFileDescriptor(uri, "r") ?? throw new UserError("err.cannotOpen", "Could not open that file.");
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
            throw new UserError("err.noArm64", "The game's arm64 code file is missing. This device's copy of the game may not be the arm64 version.");
        if (!parts.HasDataPack)
            throw fromBackup
                ? new UserError("err.noDataPackBackup", "The game's data pack (UnityDataAssetPack, about 700 MB) is missing; the patched game would not start. Make the backup again with all its parts.")
                : new UserError("err.noDataPack", "The game's data pack (UnityDataAssetPack, about 700 MB) is missing; the patched game would not start. Start the original game once so Android downloads it, then try again.");
        string version = GameFiles.VersionOf(this, parts.Base) ?? "?";
        Log($"Game files from the {parts.Origin}: version {version}, {parts.TotalBytes / 1048576} MB in {2 + parts.Extra.Count} parts");
        if (version != GameFiles.GameVersion)
            throw new UserError("err.gameVersion", "This is game version {version}; the loader and the mod are made for {wanted}.", ("version", version), ("wanted", GameFiles.GameVersion));
        if (!fromBackup) CheckSpace(parts.TotalBytes + 400L * 1048576);

        // asked before anything else happens, so nothing is spent on patching if the player would rather not remove the game
        if (installed != null && !installed.SignedWithOurKey && !await ConfirmRemoval(installed))
            throw new UserError("err.stopped", "Stopped. Nothing was changed.");

        // everything that needs the original files or the network first, before anything on the phone changes
        byte[]? stillPicture = null;
        if (still.Checked)
        {
            try { stillPicture = await Task.Run(() => ModFiles.MakeStill(parts.Base, WorkDir, Log)); }
            catch (Exception e) { Log("Could not make the still picture: " + e.Message + " (the game is patched anyway)"); }
        }
        // a newer mod already on the device (a test build) is kept; patching goes on without replacing it
        string? modPath;
        string modVersion;
        try { (modPath, modVersion) = await ModFiles.DownloadMod(WorkDir, Log); }
        catch (ModFiles.NewerModOnDevice newer) { modPath = null; modVersion = newer.Device; }

        string loaderZip = System.IO.Path.Combine(WorkDir, "melon_data.zip");
        await LoaderPackage.Get(this, loaderZip, Log);
        string[] apks = await Task.Run(() => PatchJob.Run(this, parts, WorkDir, KeyFile, loaderZip, this));
        Log("Patched: " + string.Join(", ", apks.Select(System.IO.Path.GetFileName)));

        // the original (or a copy patched with another key) has to go before Android accepts this one
        installed = GameFiles.FindInstalled(this, OurCertificate());
        if (installed != null && !installed.SignedWithOurKey)
        {
            if (!removalConfirmed && !await ConfirmRemoval(installed))
                throw new UserError("err.stoppedKept", "Stopped. Nothing was changed; the patched files are kept until the next patch.");
            var (removed, why) = await PackageOps.Uninstall(this, Log);
            if (!removed || GameFiles.FindInstalled(this, null) != null)
                throw new UserError("err.notRemoved", "The game was not removed. {why}", ("why", why));
            Log("Original game removed");
        }

        Log("Installing the patched game; Android asks you to confirm");
        var (ok, message) = await PackageOps.Install(this, apks, Log);
        if (!ok) throw new UserError("err.notInstalled", "The patched game was not installed. {why} The patched files are kept; press the button again to retry.", ("why", message));
        Log("Patched game installed");

        if (modPath != null) ModFiles.PutMod(modPath, Log);
        else Log($"Mod {modVersion} on the device kept (newer than the server's)");
        if (stillPicture != null)
        {
            ModFiles.PutStill(stillPicture, Log);
            ModFiles.SetSetting("LoadingVideo", "Still", Log);
        }
        ResetWork();
        Log($"Done. Mod {modVersion} is in place.");
        await Ask(Loc.T("done.title", "All done"),
            Loc.T("done.text", "Start Warpforge from your home screen.\n\n- If it asks for \"All files access\", allow it: the loader keeps its files there.\n- The first start after patching sits on a black screen for up to a minute while the loader sets itself up. Later starts are quicker.\n- The mod updates itself from then on."),
            Loc.T("dlg.ok", "OK"), cancel: null);
    }

    private AppUpdate.Offer? updateOffer;

    private bool checking;

    /// <summary>At start-up (quietly when there is nothing new) and from "Check for updates" (always answers).</summary>
    private async Task CheckForAppUpdate(bool manual)
    {
        if (checking) return;
        checking = true;
        RunOnUiThread(() => { checkUpdates.Enabled = false; checkUpdates.Text = Loc.T("ui.checking", "Checking..."); });
        try
        {
            var (offer, error) = await AppUpdate.Check(this, Log);
            updateOffer = offer;
            RunOnUiThread(() => { if (!busy) Refresh(); });
            if (offer != null)
            {
                var have = ("have", (object?)AppUpdate.Current(this));
                var v = ("version", (object?)offer.Version);
                bool now = await Ask(offer.Required ? Loc.T("upd.requiredTitle", "Required update") : Loc.T("upd.title", "Update available"),
                    offer.Required
                        ? Loc.T("upd.requiredText", "WF Revival Patcher {version} is available (you have {have}).\n\nThis update is required: patching is paused until the app is updated.\n\nUpdating keeps your saved signing key. Android asks you to confirm, then the app restarts as the new version.", v, have)
                        : Loc.T("upd.text", "WF Revival Patcher {version} is available (you have {have}).\n\nUpdating keeps your saved signing key. Android asks you to confirm, then the app restarts as the new version.", v, have),
                    Loc.T("upd.now", "Update now"), offer.Required ? Loc.T("upd.notNow", "Not now") : Loc.T("upd.later", "Later"));
                if (now && !busy) await UpdateApp();
            }
            else if (manual)
                Say(error != null ? Loc.T("upd.checkFailed", "Could not check for updates ({error}). Check the connection and try again.", ("error", error))
                                  : Loc.T("upd.latest", "You have the latest version ({version}).", ("version", AppUpdate.Current(this))));
        }
        finally
        {
            checking = false;
            RunOnUiThread(() => { checkUpdates.Enabled = !busy; checkUpdates.Text = Loc.T("ui.checkUpdates", "Check for updates"); });
        }
    }

    private Task UpdateApp()
    {
        jobButton = appUpdate;
        return UpdateAppJob();
    }

    private Task UpdateAppJob() => RunJob("Updating this app", async () =>
    {
        var offer = updateOffer ?? throw new UserError("upd.none", "No app update is waiting.");
        if (!PackageManager!.CanRequestPackageInstalls())
            throw new UserError("upd.needsInstall", "This app needs \"Allow this app to install the game\" to update itself too. Press that button first.");
        string apk = await AppUpdate.Download(this, offer, Log);
        Log($"Installing app {offer.Version}; Android asks you to confirm. The app closes and restarts as the new version.");
        var (ok, message) = await PackageOps.Install(this, new[] { apk }, Log, PackageName);
        // on success Android usually closes this app before this line runs
        if (!ok) throw new UserError("upd.notInstalled", "The app update was not installed. {why}", ("why", message));
    });

    private Task SaveArt() => RunJob("Saving the card art export", async () =>
    {
        string path = await Task.Run(() => ModFiles.SaveArtExport(Log));
        Say(Loc.T("art.saved", "Saved to your Download folder:\n{file}", ("file", System.IO.Path.GetFileName(path))));
    });

    private Task UpdateModOnly() => RunJob("Updating the mod", async () =>
    {
        Directory.CreateDirectory(WorkDir);
        string path, version;
        try { (path, version) = await ModFiles.DownloadMod(WorkDir, Log); }
        catch (ModFiles.NewerModOnDevice newer)
        {
            throw new UserError("mod.newerOnDevice", "This device already has a newer mod ({device}) than the server's ({server}); it was kept.", ("device", newer.Device), ("server", newer.Server));
        }
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
        Say(Loc.T("mod.updated", "Mod {version} is in place. Close the game fully and start it again.", ("version", version)));
    });

    private TextView serverInfo = null!;

    /// <summary>Sets ServerUrl in the mod's settings file, so players need not edit it with a file manager.</summary>
    private void ChangeServer()
    {
        string current = ModFiles.CurrentSetting("ServerUrl")?.Trim() ?? "";
        var input = new EditText(this) { Text = ModFiles.IsCommunityServer(current) ? ModFiles.DefaultServer : current };
        input.SetSingleLine(true);
        input.InputType = Android.Text.InputTypes.ClassText | Android.Text.InputTypes.TextVariationUri;
        var box = new FrameLayout(this);
        box.SetPadding(Dp(20), Dp(4), Dp(20), 0);
        box.AddView(input);
        var dialog = new AlertDialog.Builder(this)
            .SetTitle(Loc.T("srv.title", "Server address"))!
            .SetMessage(Loc.T("srv.text", "For example example.com (port 8780 is assumed). Write https://... or http://... to choose the connection. Close the game fully first; the change applies the next time it starts."))!
            .SetView(box)!
            .SetPositiveButton(Loc.T("srv.save", "Save"), (EventHandler<DialogClickEventArgs>?)null)!
            .SetNeutralButton(Loc.T("srv.community", "Community server"), (EventHandler<DialogClickEventArgs>?)null)!
            .SetNegativeButton(Loc.T("dlg.cancel", "Cancel"), (EventHandler<DialogClickEventArgs>?)null)!
            .Create()!;
        dialog.ShowEvent += (_, _) =>
        {
            dialog.GetButton((int)DialogButtonType.Positive)!.Click += (_, _) =>
            {
                string value = (input.Text ?? "").Trim().TrimEnd('/');
                if (value.Length == 0 || value.Any(c => char.IsWhiteSpace(c) || c == '"' || c == '\\'))
                {
                    input.Error = Loc.T("srv.bad", "Write a server address, without spaces or quotes.");
                    return;
                }
                SaveServer(value);
                dialog.Dismiss();
            };
            dialog.GetButton((int)DialogButtonType.Neutral)!.Click += (_, _) => { SaveServer(ModFiles.DefaultServer); dialog.Dismiss(); };
        };
        dialog.Show();
    }

    private void SaveServer(string value)
    {
        try
        {
            ModFiles.SetSetting("ServerUrl", value, Log);
            Log("Server set. Start the game again for it to connect there.");
        }
        catch (Exception e) { Log("Could not change the server: " + e.Message); Say(Loc.T("srv.failed", "Could not change the server: {error}", ("error", e.Message))); }
        Refresh();
    }

    private static bool StillFixOn() =>
        string.Equals(ModFiles.CurrentSetting("LoadingVideo"), "Still", StringComparison.OrdinalIgnoreCase) && File.Exists(ModFiles.StillFile);

    /// <summary>Turns the black screen fix (the Still picture instead of the loading video) on or off without patching again.</summary>
    private Task ToggleStillFix() => RunJob(StillFixOn() ? "Removing the black screen fix" : "Applying the black screen fix", async () =>
    {
        if (StillFixOn())
        {
            ModFiles.SetSetting("LoadingVideo", "Normal", Log);
            try { File.Delete(ModFiles.StillFile); } catch { }
            Log("Done: the black screen fix is off; the loading video plays again. Close the game fully and start it again.");
            Say(Loc.T("fix.off", "The black screen fix is off; the loading video plays again. Close the game fully and start it again."));
            return;
        }
        var game = GameFiles.FindInstalled(this, null) ?? throw new UserError("err.noGame", "The game is not installed.");
        if (!File.Exists(ModFiles.StillFile))
        {
            // its own folder: the work folder may hold patched files kept for a retry
            string dir = System.IO.Path.Combine(CacheDir!.AbsolutePath, "still");
            Directory.CreateDirectory(dir);
            try
            {
                var rgba = await Task.Run(() => ModFiles.MakeStill(game.Parts.Base, dir, Log));
                ModFiles.PutStill(rgba, Log);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
        ModFiles.SetSetting("LoadingVideo", "Still", Log);
        Log("Done: the black screen fix is on. Close the game fully and start it again.");
        Say(Loc.T("fix.on", "The black screen fix is on. Close the game fully and start it again."));
    });

    private async Task RunJob(string what, Func<Task> job)
    {
        busy = true;
        removalConfirmed = false;
        SetButtons(false);
        Log("---- " + what);
        try { await job(); }
        catch (UserError e) { Log("Stopped: " + e.Message); Say(e.Shown); }
        catch (HttpRequestException e) { Log("Network: " + e); Say(Loc.T("err.network", "Could not reach the server ({error}). Check the connection and try again. Nothing on the device was changed by this step.", ("error", e.Message))); }
        catch (Exception e) { Log("ERROR " + e); Say(Loc.T("err.unexpected", "Something went wrong: {error}\n\nThe log at the bottom has the details; \"Share logs\" sends it.", ("error", e.Message))); }
        finally
        {
            busy = false;
            ShowContinue(false);
            jobButton = null;
            SetButtons(true);
            Refresh();
        }
    }

    /// <summary>Called by PackageOps (any thread) when Android's confirmation is put up (true) and when Android answers (false).</summary>
    private void ShowContinue(bool waiting) => RunOnUiThread(() =>
    {
        if (jobButton == null) return;
        if (waiting && !continueShown)
        {
            jobButtonLabel = jobButton.Text ?? "";
            jobButton.Text = Loc.T("ui.continue", "Continue: {step}", ("step", PackageOps.WaitingFor));
            jobButton.Enabled = true;
            continueShown = true;
        }
        else if (!waiting && continueShown)
        {
            jobButton.Text = jobButtonLabel;
            jobButton.Enabled = !busy;
            continueShown = false;
        }
    });

    private void SetButtons(bool on)
    {
        foreach (var b in new View[] { checkUpdates, appUpdate, allowFiles, allowInstall, still, patch, fromBackup, modOnly, stillFix, setServer, saveArt, saveKey, loadKey, language }) b.Enabled = on;
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
            throw new UserError("err.noSpace", "Not enough free space: about {needed} GB is needed while patching, {free} GB is free. It is given back afterwards.", ("needed", (needed / 1073741824.0).ToString("F1")), ("free", (free / 1073741824.0).ToString("F1")));
    }

    private bool removalConfirmed;

    /// <summary>
    /// The installed game has to be removed before Android accepts a copy signed with this app's key. Its downloaded
    /// files go with it; the revival server hands them out again (owner, 2026-10-11). Asked once per patch, up front.
    /// </summary>
    private async Task<bool> ConfirmRemoval(GameFiles.Installed installed)
    {
        bool ok = await Ask(Loc.T("remove.title", "Remove the game first?"),
            installed.Patched
                ? Loc.T("remove.textPatched", "The game on this device was patched with a different key (on a PC). Android only accepts the copy patched here after that one is removed, so the app removes it after patching. Android asks you to confirm.\n\nYour account, decks and progress are on the server and are not affected. The game's downloaded files come back from the server. The MelonLoader folder stays.")
                : Loc.T("remove.textOriginal", "The game on this device is the original from the store. Android only accepts the copy patched here after that one is removed, so the app removes it after patching. Android asks you to confirm.\n\nYour account, decks and progress are on the server and are not affected. The game's downloaded files come back from the server. The MelonLoader folder stays."),
            Loc.T("remove.continue", "Continue"), Loc.T("remove.stop", "Stop"));
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
        using var output = ContentResolver!.OpenOutputStream(uri, "wt") ?? throw new UserError("key.cannotWrite", "Could not write there.");
        var bytes = File.ReadAllBytes(KeyFile);
        output.Write(bytes, 0, bytes.Length);
        Log("Signing key saved. Keep that file private: anyone with it can make an update of your patched game.");
        Say(Loc.T("key.saved", "Key saved. Keep that file private. If this app is ever reinstalled, \"Load a saved signing key\" puts it back, so the game can be patched again without removing it."));
    }

    private void LoadKeyFrom(Android.Net.Uri uri)
    {
        using var input = ContentResolver!.OpenInputStream(uri) ?? throw new UserError("key.cannotRead", "Could not read that file.");
        using var r = new StreamReader(input);
        string text = r.ReadToEnd();
        if (text.Length > 20000 || !text.Contains("BEGIN CERTIFICATE") || !text.Contains("PRIVATE KEY"))
            throw new UserError("key.notKey", "That is not a signing key saved by this app.");
        if (File.Exists(KeyFile)) File.Copy(KeyFile, KeyFile + ".previous", true);
        File.WriteAllText(KeyFile, text);
        Log("Signing key loaded" + (File.Exists(KeyFile + ".previous") ? " (the one before is kept as signing-key.pem.previous)" : ""));
        Say(Loc.T("key.loaded", "Signing key loaded."));
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
        catch (UserError e) { Log("Stopped: " + e.Message); Say(e.Shown); }
        catch (Exception e) { Log("ERROR " + e); Say(Loc.T("err.unexpected", "Something went wrong: {error}\n\nThe log at the bottom has the details; \"Share logs\" sends it.", ("error", e.Message))); }
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
            StartActivity(Intent.CreateChooser(send, Loc.T("logs.share", "Share WF Revival logs")));
        }
        catch (Exception e) { Log("Could not share the logs: " + e.Message); Say(Loc.T("logs.failed", "Could not share the logs: {error}", ("error", e.Message))); }
    }

    // ---------------------------------------------------------------- messages and the log

    private void Say(string message) => RunOnUiThread(() =>
        new AlertDialog.Builder(this).SetMessage(message)!.SetPositiveButton(Loc.T("dlg.ok", "OK"), (EventHandler<DialogClickEventArgs>?)null)!.Show());

    private Task<bool> Ask(string title, string message, string ok, string? cancel)
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
