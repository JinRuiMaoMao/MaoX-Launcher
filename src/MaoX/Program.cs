using Avalonia;
using MaoX.Core;

namespace MaoX;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // --home <目录>：指定数据目录（桌面快捷方式会带上），必须在第一次使用 AppPaths 之前设置
        var home = Array.IndexOf(args, "--home");
        if (home >= 0 && home + 1 < args.Length)
            Environment.SetEnvironmentVariable("MAOX_HOME", args[home + 1]);
        I18n.SetLanguage(LauncherConfig.Load().Language);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => App.WriteCrashLog(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => e.SetObserved();
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception e)
        {
            App.WriteCrashLog(e);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new MacOSPlatformOptions { ShowInDock = true })
            .LogToTrace();
}
