using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MaoX.Controls;

namespace MaoX;

/// <summary>任务中心里的一个后台任务。进度可在任意线程报告，由界面定时器统一刷新。</summary>
public class TaskItem
{
    /// <summary>当前 async 调用链所属的任务（MainWindow.Progress 据此把进度记到对应任务上）。</summary>
    public static readonly AsyncLocal<TaskItem> Current = new();

    private readonly object _lock = new();
    private (int Done, int Total, string Text)? _progress;

    public TaskItem(string name) => Name = name;

    public string Name { get; }
    public CancellationTokenSource Cancel { get; } = new();

    /// <summary>running / done / failed / cancelled</summary>
    public string State { get; private set; } = "running";

    public string Error { get; private set; }
    public bool Running => State == "running";

    /// <summary>进度比例，没有报告过进度时为 null（显示为不确定进度）。</summary>
    public double? Fraction { get; private set; }

    public string Detail { get; private set; } = "";

    public void Report(int done, int total, string text)
    {
        lock (_lock)
            _progress = (done, total, text);
    }

    /// <summary>把后台线程报告的进度同步到界面属性，返回是否有变化。只在界面线程调用。</summary>
    public bool Flush()
    {
        (int Done, int Total, string Text)? p;
        lock (_lock)
        {
            p = _progress;
            _progress = null;
        }
        if (p is not { } v || !Running)
            return false;
        Fraction = (double)v.Done / v.Total;
        Detail = $"{(string.IsNullOrEmpty(v.Text) ? "" : v.Text + "  ")}{v.Done} / {v.Total}";
        return true;
    }

    public void Finish(string state, string error = null)
    {
        State = state;
        Error = error;
        Fraction = state == "done" ? 1 : Fraction;
    }
}

/// <summary>状态栏上方弹出的任务列表：每个任务的进度，可以单独取消。</summary>
public class TaskCenterView : UserControl
{
    private readonly StackPanel _list = new() { Spacing = 6 };
    private readonly TextBlock _empty = new() { Text = "没有正在进行的任务", Classes = { "muted" }, Margin = new Thickness(4, 6) };
    private readonly Dictionary<TaskItem, (ProgressLine Line, TextBlock Detail, Button Cancel, string State)> _rows = new();

    public TaskCenterView()
    {
        Width = 400;
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(4, 0, 0, 10) };
        head.Children.Add(new TextBlock { Text = "任务", FontWeight = FontWeight.SemiBold, FontSize = 15, VerticalAlignment = VerticalAlignment.Center });
        var clear = new Button { Classes = { "link" }, Content = "清除已结束" };
        clear.Click += (_, _) =>
        {
            MainWindow.Current.ClearFinishedTasks();
            Refresh();
        };
        Grid.SetColumn(clear, 1);
        head.Children.Add(clear);
        var root = new StackPanel();
        root.Children.Add(head);
        root.Children.Add(_empty);
        root.Children.Add(new ScrollViewer { Content = _list, MaxHeight = 420 });
        Content = root;
    }

    /// <summary>重新同步列表（任务增删或状态变化时调用）。</summary>
    public void Refresh()
    {
        var tasks = MainWindow.Current.Tasks;
        _empty.IsVisible = tasks.Count == 0;
        var structure = tasks.Count != _rows.Count || tasks.Any(t => !_rows.TryGetValue(t, out var r) || r.State != t.State);
        if (structure)
        {
            _list.Children.Clear();
            _rows.Clear();
            foreach (var task in tasks)
                _list.Children.Add(Row(task));
        }
        foreach (var task in tasks)
            Update(task);
    }

    private Control Row(TaskItem task)
    {
        var name = new TextBlock { Text = task.Name, FontWeight = FontWeight.SemiBold };
        var detail = new TextBlock { Classes = { "small", "dim" }, TextWrapping = TextWrapping.Wrap, MaxLines = 3 };
        var line = new ProgressLine { Height = 3, Margin = new Thickness(0, 6, 0, 0), IsVisible = task.Running };
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(name);
        text.Children.Add(detail);
        text.Children.Add(line);

        var cancel = new Button { Classes = { "icon", "small" }, Width = 30, Height = 30, IsVisible = task.Running, VerticalAlignment = VerticalAlignment.Top };
        cancel.Content = new Icon { Kind = "close", Size = 15 };
        ToolTip.SetTip(cancel, "取消");
        cancel.Click += (_, _) =>
        {
            task.Cancel.Cancel();
            cancel.IsEnabled = false;
        };

        var icon = new Icon
        {
            Size = 18, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 12, 0),
            Kind = task.State switch { "done" => "success", "failed" => "error", "cancelled" => "close", _ => "download" },
            Foreground = (IBrush)Application.Current!.FindResource(task.State switch
            {
                "done" => "Success", "failed" => "Error", "cancelled" => "Dim", _ => "Accent",
            })!,
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        grid.Children.Add(icon);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        Grid.SetColumn(cancel, 2);
        grid.Children.Add(cancel);
        _rows[task] = (line, detail, cancel, task.State);
        return new Border { Classes = { "row" }, Padding = new Thickness(14, 10, 10, 10), Child = grid };
    }

    private void Update(TaskItem task)
    {
        if (!_rows.TryGetValue(task, out var row))
            return;
        row.Detail.Text = task.State switch
        {
            "done" => "已完成",
            "cancelled" => "已取消",
            "failed" => "失败：" + task.Error,
            _ => task.Cancel.IsCancellationRequested ? "正在取消…" : string.IsNullOrEmpty(task.Detail) ? "进行中…" : task.Detail,
        };
        if (!task.Running)
            return;
        if (task.Fraction is { } f)
            row.Line.Set(f);
        else if (row.Line.Tag == null)
        {
            row.Line.Tag = true;
            row.Line.Start();
        }
    }
}
