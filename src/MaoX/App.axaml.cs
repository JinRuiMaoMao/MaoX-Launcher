using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace MaoX;

public class App : Application
{
    private bool _reporting;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Dispatcher.UIThread.UnhandledException += OnUnhandledException;
            var window = new MainWindow();
            SmokeTest.TryAttach(desktop, window);
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Program.WriteCrashLog(e.Exception);
        var main = MainWindow.Current;
        if (main == null || !main.IsVisible || _reporting)
            return;
        e.Handled = true;
        _reporting = true;
        try
        {
            main.Log($"启动器内部错误：{e.Exception}", "error");
            _ = main.Dialog("出错了", $"启动器遇到了一个意外错误，已记录到 launcher_crash.log。\n\n{e.Exception.Message}", "error");
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
