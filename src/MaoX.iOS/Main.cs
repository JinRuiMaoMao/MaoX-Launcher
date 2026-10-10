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
        AppDomain.CurrentDomain.UnhandledException += (_, e) => App.WriteCrashLog(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => e.SetObserved();
        UIApplication.Main(args, null, typeof(AppDelegate));
    }
}
