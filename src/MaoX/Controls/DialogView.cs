using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace MaoX.Controls;

/// <summary>窗口内的模态对话框。用 MainWindow.ShowDialogAsync 显示，调用 Close(结果) 关闭。</summary>
public class DialogView : UserControl
{
    internal readonly TaskCompletionSource<object> Completion = new();

    /// <summary>对话框卡片的宽度。</summary>
    public double DialogWidth { get; set; } = 480;

    /// <summary>按 Esc 或点击遮罩时是否允许关闭（返回 null）。</summary>
    public bool Dismissible { get; set; } = true;

    public void Close(object result = null) => MainWindow.Current.CloseDialog(this, result);

    public virtual void OnOpened()
    {
    }

    public virtual void OnClosed()
    {
    }

    // ------------------------------------------------------------------ 构建界面的小工具

    public static TextBlock Title(string text) => new() { Text = text, Classes = { "h2" }, Margin = new Thickness(0, 0, 0, 6) };

    public static TextBlock Text(string text, params string[] classes)
    {
        var block = new TextBlock { Text = text };
        foreach (var c in classes)
            block.Classes.Add(c);
        return block;
    }

    public static TextBlock Paragraph(string text) =>
        new() { Text = text, Classes = { "muted", "wrap" }, LineHeight = 22 };

    public static TextBlock FieldLabel(string text) => new() { Text = text, Classes = { "label" } };

    public static Button MakeButton(string text, string style = null, string icon = null, Action onClick = null)
    {
        var button = new Button
        {
            Content = icon == null ? text : new IconLabel { Icon = icon, Text = text },
            MinWidth = 88,
        };
        if (!string.IsNullOrEmpty(style))
            button.Classes.Add(style);
        if (onClick != null)
            button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>右对齐的按钮行。</summary>
    public static StackPanel ButtonRow(params Control[] buttons)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Thickness(0, 26, 0, 0),
        };
        foreach (var b in buttons)
            row.Children.Add(b);
        return row;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && Dismissible)
        {
            Close();
            e.Handled = true;
        }
    }
}

/// <summary>通用消息对话框：图标 + 标题 + 正文 + 若干按钮，返回被点击按钮的值。</summary>
public class MessageDialog : DialogView
{
    public MessageDialog(string title, string message, string kind, IReadOnlyList<(string Text, object Value, string Style)> buttons)
    {
        DialogWidth = 500;
        var (icon, brushKey) = kind switch
        {
            "error" => ("error", "Error"),
            "warn" => ("warn", "Warn"),
            "success" => ("success", "Success"),
            _ => ("info", "Accent"),
        };
        var iconControl = new Icon { Kind = icon, Size = 30, StrokeWidth = 1.7, VerticalAlignment = VerticalAlignment.Top };
        iconControl[!Icon.ForegroundProperty] = this.GetResourceObservable(brushKey).ToBinding();

        var body = new StackPanel { Spacing = 4 };
        body.Children.Add(Title(title));
        var text = new SelectableTextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 23,
            Foreground = (IBrush)Application.Current!.FindResource("Muted"),
        };
        body.Children.Add(new ScrollViewer { Content = text, MaxHeight = 420 });

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Children.Add(iconControl);
        Grid.SetColumn(body, 1);
        body.Margin = new Thickness(18, 0, 0, 0);
        grid.Children.Add(body);

        var row = ButtonRow();
        foreach (var (label, value, style) in buttons)
            row.Children.Add(MakeButton(label, style, onClick: () => Close(value)));

        var root = new StackPanel();
        root.Children.Add(grid);
        root.Children.Add(row);
        Content = root;
    }
}
