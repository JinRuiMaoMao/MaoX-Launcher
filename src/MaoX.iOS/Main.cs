using Avalonia;
using Avalonia.iOS;
using Foundation;
using MaoX.Core;
using UIKit;

namespace MaoX.iOS;

[Register("AppDelegate")]
public class AppDelegate : AvaloniaAppDelegate<App>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) => base.CustomizeAppBuilder(builder).LogToTrace();
}

public static class Program
{
    private static void Main(string[] args)
    {
        // 数据放在应用的“文稿”目录，可以在“文件”App 的“我的 iPhone / MaoX Launcher”里看到
        var home = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        Environment.SetEnvironmentVariable("MAOX_HOME", home);
        I18n.SetLanguage(LauncherConfig.Load().Language);
        Game.GameHost.Install();
        Platform.MobilePickImage = IosPhotoPicker.Pick;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => App.WriteCrashLog(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => e.SetObserved();
        // 基岩版注册了 minecraft:// 链接，能打开就说明装了（Info.plist 里要声明 LSApplicationQueriesSchemes）
        MojangGames.MobileProbe = scheme => UIApplication.SharedApplication.CanOpenUrl(new NSUrl(scheme)) ? "" : null;
        MojangGames.MobileLaunch = scheme =>
        {
            UIApplication.SharedApplication.OpenUrl(new NSUrl(scheme), new UIApplicationOpenUrlOptions(), null);
            return true;
        };
        Updater.UrlInstaller = InstallUpdate;
        UIApplication.Main(args, null, typeof(AppDelegate));
    }

    private static readonly NSObject MainInvoker = new();

    /// <summary>
    /// TrollStore 安装的用 TrollStore 的链接直接安装；侧载的交给 SideStore / AltStore 下载、重新签名并覆盖安装，数据保留。
    /// </summary>
    private static Task<bool> InstallUpdate(string ipaUrl)
    {
        var trollStore = Game.GameNative.Available && Game.GameNative.HasEntitlement("jb.pmap_cs.custom_trust");
        string[] tools = trollStore
            ? ["apple-magnifier://install?url="]
            : ["sidestore://install?url=", "altstore://install?url="];
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        MainInvoker.BeginInvokeOnMainThread(() =>
        {
            var app = UIApplication.SharedApplication;
            var tool = tools.FirstOrDefault(t => app.CanOpenUrl(new NSUrl(t[..(t.IndexOf(':') + 3)])));
            if (tool == null)
            {
                tcs.SetResult(false);
                return;
            }
            app.OpenUrl(new NSUrl(tool + Uri.EscapeDataString(ipaUrl)), new UIApplicationOpenUrlOptions(),
                        ok => tcs.TrySetResult(ok));
        });
        return tcs.Task;
    }
}
