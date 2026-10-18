using Android.App;
using Android.Content;
using Android.Content.PM;

namespace WarpforgePatcher;

/// <summary>Removing the original game and installing the patched one, through Android's own installer (the player confirms each).</summary>
internal static class PackageOps
{
    private const string StatusAction = "com.silentanarchist.warpforgepatcher.INSTALL_STATUS";

    // The install / uninstall currently waiting for an answer from Android. Android's confirmation screen can be closed
    // by tapping outside it, and then Android sometimes sends no answer at all: the app would wait for ever. So the
    // confirmation is kept, and the job's button turns into "Continue: ..." to show it again (MainActivity.ShowContinue).
    /// <summary>Told true when Android's confirmation is put up and false when Android answers (from any thread).</summary>
    public static Action<bool>? ConfirmationState;
    private static TaskCompletionSource<(bool, LocText)>? pending;
    private static Intent? lastConfirm;
    private static int pendingSession = -1;
    private static LocText pendingWhat = new("", "");

    /// <summary>True while an install or uninstall waits for Android and its confirmation has been shown at least once.</summary>
    public static bool WaitingForConfirmation => pending != null && !pending.Task.IsCompleted && lastConfirm != null;
    public static LocText WaitingFor => pendingWhat;

    /// <summary>Shows Android's confirmation again (after it was closed by a tap outside it).</summary>
    public static bool ShowConfirmationAgain(Activity activity, Action<string> log)
    {
        if (!WaitingForConfirmation) return false;
        try { activity.StartActivity(lastConfirm); log("Showing Android's confirmation again"); return true; }
        catch (Exception e)
        {
            log("Android's confirmation could not be shown again: " + e.Message);
            Cancel(activity, log, new LocText("confirm.gone", "Android's confirmation is no longer available. Press the button again to start over."));
            return false;
        }
    }

    /// <summary>Gives up waiting: the install session is abandoned and the waiting job ends as cancelled.</summary>
    private static void Cancel(Activity activity, Action<string> log, LocText? why = null)
    {
        if (pendingSession >= 0)
        {
            try { activity.PackageManager!.PackageInstaller.AbandonSession(pendingSession); } catch { }
            pendingSession = -1;
        }
        log("Stopped waiting for Android's confirmation");
        pending?.TrySetResult((false, why ?? new LocText("install.cancelled", "It was cancelled.")));
    }

    /// <summary>Installs all patched APKs together as one app (they are splits of one package).</summary>
    public static Task<(bool ok, LocText message)> Install(Activity activity, IEnumerable<string> apks, Action<string> log, string? package = null)
    {
        var installer = activity.PackageManager!.PackageInstaller;
        var p = new PackageInstaller.SessionParams(PackageInstallMode.FullInstall);
        p.SetAppPackageName(package ?? GameFiles.Package);
        if (OperatingSystem.IsAndroidVersionAtLeast(26)) p.SetInstallReason(PackageInstallReason.User);
        int id = installer.CreateSession(p);
        using (var session = installer.OpenSession(id))
        {
            int i = 0;
            byte[] buffer = new byte[1 << 20];
            foreach (string apk in apks)
            {
                log($"Handing {Path.GetFileName(apk)} to Android's installer");
                using var input = File.OpenRead(apk);
                using var output = session.OpenWrite($"{i++}-{Path.GetFileName(apk)}", 0, input.Length)!;
                int n;
                while ((n = input.Read(buffer, 0, buffer.Length)) > 0) output.Write(buffer, 0, n);
                output.Flush();
                session.Fsync(output);
            }
            var wait = Listen(activity, log, package == null ? new LocText("confirm.installGame", "install the patched game") : new LocText("confirm.installUpdate", "install the app update"));
            pendingSession = id;
            session.Commit(StatusSender(activity, id));
            return wait;
        }
    }

    /// <summary>Removes the installed game (Android asks the player to confirm).</summary>
    public static Task<(bool ok, LocText message)> Uninstall(Activity activity, Action<string> log)
    {
        var wait = Listen(activity, log, new LocText("confirm.removeGame", "remove the original game"));
        pendingSession = -1;
        activity.PackageManager!.PackageInstaller.Uninstall(GameFiles.Package, StatusSender(activity, 0x5157));
        return wait;
    }

    private static IntentSender StatusSender(Context context, int requestCode)
    {
        var intent = new Intent(StatusAction).SetPackage(context.PackageName);
        // mutable: the system fills in the result
        var flags = PendingIntentFlags.UpdateCurrent | (OperatingSystem.IsAndroidVersionAtLeast(31) ? PendingIntentFlags.Mutable : 0);
        return PendingIntent.GetBroadcast(context, requestCode, intent, flags)!.IntentSender;
    }

    private static Task<(bool ok, LocText message)> Listen(Activity activity, Action<string> log, LocText what)
    {
        var done = new TaskCompletionSource<(bool, LocText)>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending = done;
        lastConfirm = null;
        pendingWhat = what;
        var receiver = new StatusReceiver(activity, done, log);
        var filter = new IntentFilter(StatusAction);
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            activity.RegisterReceiver(receiver, filter, ReceiverFlags.NotExported);
        else
            activity.RegisterReceiver(receiver, filter);
        done.Task.ContinueWith(_ =>
        {
            try { activity.UnregisterReceiver(receiver); } catch { }
            if (ReferenceEquals(pending, done)) { pending = null; lastConfirm = null; pendingSession = -1; }
            ConfirmationState?.Invoke(false);
        });
        return done.Task;
    }

    private sealed class StatusReceiver(Activity activity, TaskCompletionSource<(bool, LocText)> done, Action<string> log) : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            if (intent == null) return;
            var status = (PackageInstallStatus)intent.GetIntExtra(PackageInstaller.ExtraStatus, -999);
            string message = intent.GetStringExtra(PackageInstaller.ExtraStatusMessage) ?? "";
            if (status == PackageInstallStatus.PendingUserAction)
            {
                // Android's own confirmation screen
                var confirm = OperatingSystem.IsAndroidVersionAtLeast(33)
                    ? (Intent?)intent.GetParcelableExtra(Intent.ExtraIntent, Java.Lang.Class.FromType(typeof(Intent)))
#pragma warning disable CA1422
                    : (Intent?)intent.GetParcelableExtra(Intent.ExtraIntent);
#pragma warning restore CA1422
                if (confirm != null)
                {
                    lastConfirm = confirm;
                    ConfirmationState?.Invoke(true);
                    try { activity.StartActivity(confirm); }
                    catch (Exception e) { done.TrySetResult((false, new LocText("install.noConfirm", "Could not show Android's confirmation: {error}", ("error", e.Message)))); }
                }
                return;
            }
            bool ok = status == PackageInstallStatus.Success;
            if (!ok) log($"Android answered: {status} {message}");
            done.TrySetResult((ok, ok ? new LocText("", "") : Explain(status, message)));
        }
    }

    private static LocText Explain(PackageInstallStatus status, string message) => status switch
    {
        PackageInstallStatus.FailureAborted => new LocText("install.cancelled", "It was cancelled."),
        PackageInstallStatus.FailureConflict => new LocText("install.conflict", "Android refused it because a copy of the game with a different signature is still installed."),
        PackageInstallStatus.FailureStorage => new LocText("install.storage", "There is not enough free space on the device."),
        PackageInstallStatus.FailureIncompatible => new LocText("install.incompatible", "Android says the game does not fit this device ({details}).", ("details", message)),
        PackageInstallStatus.FailureBlocked => new LocText("install.blocked", "Android blocked it ({details}).", ("details", message)),
        _ => new LocText("install.refused", "Android refused it ({status}: {details}).", ("status", status), ("details", message)),
    };
}
