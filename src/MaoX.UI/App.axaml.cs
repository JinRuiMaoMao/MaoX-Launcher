using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using MaoX.Core;
using static MaoX.Core.I18n;

namespace MaoX;

public class App : Application
{
    private bool _reporting;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Dispatcher.UIThread.UnhandledException += OnUnhandledException;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            SmokeTest.TryAttach(desktop, window);
            desktop.MainWindow = window;
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime single)
        {
            single.MainView = new MainView();
        }
        base.OnFrameworkInitializationCompleted();
    }

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

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception);
        var main = MainView.Current;
        if (main == null || MainWindow.Current is { IsVisible: false } || _reporting)
            return;
        e.Handled = true;
        _reporting = true;
        try
        {
            main.Log(F("启动器内部错误：{0}", e.Exception), "error");
            _ = main.Dialog(T("出错了"), F("启动器遇到了一个意外错误，已记录到 launcher_crash.log。\n\n{0}", e.Exception.Message), "error");
        }
        catch (Exception)
        {
            // 报告失败时交给默认处理
        }
        finally
        {
            _reporting = false;
        }
    }
}
