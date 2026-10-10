using Avalonia.Controls;
using Avalonia.Input;

namespace MaoX;

/// <summary>电脑版的窗口：自绘标题栏、关闭前确认，内容是 MainView。</summary>
public partial class MainWindow : Window
{
    public static MainWindow Current { get; private set; }

    private bool _forceClose;

    public MainWindow()
    {
        Current = this;
        InitializeComponent();
    }

    /// <summary>不再询问，直接关闭窗口。</summary>
    public void ForceClose()
    {
        _forceClose = true;
        Close();
    }

    private void OnTitleBarPressed(object sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount == 1)
            BeginMoveDrag(e);
    }

    private void OnTitleBarDoubleTapped(object sender, TappedEventArgs e)
    {
        if (CanResize)
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_forceClose)
            return;
        if (View.NeedsExitConfirm)
        {
            e.Cancel = true;
            if (await View.ConfirmExit())
                ForceClose();
            return;
        }
        View.SaveSettings();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        View.Shutdown();
    }
}
