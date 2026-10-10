using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Widget;
using static MaoX.Core.I18n;

namespace MaoX.Android;

/// <summary>把下载好的新版 APK 交给系统安装程序，用户确认后覆盖安装（数据保留）。</summary>
internal static class ApkInstaller
{
    public static void Install(string apk)
    {
        var ctx = Application.Context;
        var pm = ctx.PackageManager!;
        if (!pm.CanRequestPackageInstalls())
        {
            var settings = new Intent(global::Android.Provider.Settings.ActionManageUnknownAppSources,
                                      global::Android.Net.Uri.Parse("package:" + ctx.PackageName));
            settings.AddFlags(ActivityFlags.NewTask);
            ctx.StartActivity(settings);
            throw new InvalidOperationException(T("请在打开的设置里允许 MaoX Launcher 安装应用，然后回来再点一次「立即更新」"));
        }
        var installer = pm.PackageInstaller;
        var parameters = new PackageInstaller.SessionParams(PackageInstallMode.FullInstall);
        parameters.SetAppPackageName(ctx.PackageName);
        var id = installer.CreateSession(parameters);
        using var session = installer.OpenSession(id);
        using (var input = File.OpenRead(apk))
        using (var output = session.OpenWrite("maox.apk", 0, input.Length))
        {
            input.CopyTo(output);
            session.Fsync(output);
        }
        var intent = new Intent(ctx, typeof(InstallReceiver));
        var flags = PendingIntentFlags.UpdateCurrent
                    | (OperatingSystem.IsAndroidVersionAtLeast(31) ? PendingIntentFlags.Mutable : 0);
        session.Commit(PendingIntent.GetBroadcast(ctx, id, intent, flags)!.IntentSender);
    }
}

[BroadcastReceiver(Name = "io.github.jinruimaomao.maox.InstallReceiver", Exported = false)]
internal class InstallReceiver : BroadcastReceiver
{
    public override void OnReceive(Context context, Intent intent)
    {
        var status = intent!.GetIntExtra(PackageInstaller.ExtraStatus, int.MinValue);
        if (status == (int)PackageInstallStatus.PendingUserAction)
        {
#pragma warning disable CA1422, CS0618
            if (intent.GetParcelableExtra(Intent.ExtraIntent) is Intent confirm)
#pragma warning restore CA1422, CS0618
            {
                confirm.AddFlags(ActivityFlags.NewTask);
                context!.StartActivity(confirm);
            }
        }
        else if (status != (int)PackageInstallStatus.Success)
        {
            var message = intent.GetStringExtra(PackageInstaller.ExtraStatusMessage);
            Toast.MakeText(context, F("更新安装失败：{0}", message ?? status.ToString()), ToastLength.Long)!.Show();
        }
    }
}
