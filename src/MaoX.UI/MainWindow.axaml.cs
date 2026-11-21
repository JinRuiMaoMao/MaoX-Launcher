using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;

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
        // 整合包可以直接拖到窗口任意位置；具体导入流程交给 MainView。
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var ok = e.DataTransfer.Contains(DataFormat.File);
        e.DragEffects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        View.SetDropHint(ok);
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e) => View.SetDropHint(false);

    private async void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        View.SetDropHint(false);
        var items = e.DataTransfer.TryGetFiles();
        if (items == null || items.Length == 0)
            return;
        var paths = new List<string>();
        try
        {
            foreach (var item in items)
            {
                var path = item.TryGetLocalPath();
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    paths.Add(path);
            }
        }
        finally
        {
            foreach (var item in items)
                item.Dispose();
        }
        if (paths.Count > 0)
            await View.ImportDroppedAsync(paths);
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
