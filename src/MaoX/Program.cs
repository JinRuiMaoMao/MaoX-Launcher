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
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrashLog(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => e.SetObserved();
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception e)
        {
            WriteCrashLog(e);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new MacOSPlatformOptions { ShowInDock = true })
            .LogToTrace();

    public static void WriteCrashLog(Exception e)
    {
        if (e == null)
            return;
        try
        {
            Directory.CreateDirectory(AppPaths.BaseDir);
            File.AppendAllText(Path.Combine(AppPaths.BaseDir, "launcher_crash.log"),
                               $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e}\n\n");
        }
        catch (Exception)
        {
            // 无法写入日志时忽略
        }
    }
}
