using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MaoX.Controls;
using MaoX.Core;

namespace MaoX.Dialogs;

/// <summary>游戏异常退出后展示崩溃分析结果。</summary>
public class CrashDialog : DialogView
{
    public CrashDialog(int code, List<string> reasons, CrashReport report)
    {
        DialogWidth = 580;
        var error = (IBrush)Application.Current!.FindResource("Error");
        var root = new StackPanel();

        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        head.Children.Add(new Icon { Kind = "error", Size = 30, Foreground = error });
        var titles = new StackPanel { Spacing = 2 };
        titles.Children.Add(new TextBlock { Text = code != 0 ? "游戏崩溃了" : "模组加载失败", Classes = { "h2" } });
        titles.Children.Add(Text($"退出码 {code}", "small", "dim"));
        head.Children.Add(titles);
        root.Children.Add(head);

        var body = new StackPanel { Spacing = 10, Margin = new Thickness(0, 20, 0, 0) };
        if (reasons.Count > 0)
        {
            body.Children.Add(FieldLabel("可能的原因"));
            foreach (var reason in reasons.Take(8))
            {
                var row = new Border
                {
                    Classes = { "row" },
                    Padding = new Thickness(14, 10),
                    Child = new SelectableTextBlock { Text = reason, TextWrapping = TextWrapping.Wrap, LineHeight = 22 },
                };
                body.Children.Add(row);
            }
        }
        else
        {
            body.Children.Add(Paragraph("没有找到已知的崩溃原因。可以在「游戏日志」中查看完整输出。"));
            if (!string.IsNullOrWhiteSpace(report.Detail))
            {
                body.Children.Add(FieldLabel("错误信息"));
                body.Children.Add(new Border
                {
                    Classes = { "log" },
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(14, 10),
                    Child = new SelectableTextBlock
                    {
                        Text = report.Detail,
                        TextWrapping = TextWrapping.Wrap,
                        FontFamily = (FontFamily)Application.Current.FindResource("MonoFont")!,
                        FontSize = 12.5,
                    },
                });
            }
        }
        root.Children.Add(new ScrollViewer { Content = body, MaxHeight = 380 });

        var buttons = new List<Control>();
        if (report.Files.Count > 0)
        {
            var file = report.Files[0];
            buttons.Add(MakeButton("查看崩溃报告", icon: "file", onClick: () => Platform.OpenPath(file)));
            buttons.Add(MakeButton("在文件夹中显示", icon: "folder", onClick: () => Platform.RevealFile(file)));
        }
        buttons.Add(MakeButton("确定", "primary", onClick: () => Close()));
        root.Children.Add(ButtonRow([.. buttons]));
        Content = root;
    }
}
