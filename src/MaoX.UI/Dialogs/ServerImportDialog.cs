using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using MaoX.Controls;
using MaoX.Core;
using static MaoX.Core.I18n;

namespace MaoX.Dialogs;

/// <summary>
/// 从游戏导入服务器时的确认框。隔离实例的 servers.dat 能直接带出版本；
/// 共用目录里的服务器不知道属于哪个版本，发给用户逐个指定，避免多个版本时乱猜。
/// </summary>
public class ServerImportDialog : DialogView
{
    private const string Follow = "跟随启动页选择的版本";

    private readonly List<ServerEntry> _servers;
    private readonly List<ComboBox> _boxes = [];

    public ServerImportDialog(List<ServerEntry> servers)
    {
        _servers = servers;
        DialogWidth = 640;
        MaxHeight = 720;

        var versions = new List<string> { T(Follow) };
        versions.AddRange(MainView.Current?.InstalledList ?? []);
        foreach (var server in servers)
        {
            if (!string.IsNullOrEmpty(server.Version) && !versions.Contains(server.Version))
                versions.Add(server.Version);
        }
        versions = versions.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var root = new StackPanel { Spacing = 8 };
        root.Children.Add(Title(T("导入服务器")));
        root.Children.Add(Paragraph(T("从共用目录读取的服务器不知道属于哪个版本。可以逐个指定进服时使用的版本；留空则跟随启动页当前选择的版本。")));

        var bulkRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 12, 0, 4),
        };
        var bulk = new ComboBox { ItemsSource = versions, SelectedIndex = 0, MinWidth = 240 };
        var apply = MakeButton(T("应用到全部"), onClick: () => ApplyAll(bulk));
        bulkRow.Children.Add(new TextBlock { Text = T("统一指定"), Classes = { "label" }, VerticalAlignment = VerticalAlignment.Center });
        bulkRow.Children.Add(bulk);
        bulkRow.Children.Add(apply);
        root.Children.Add(bulkRow);

        var list = new StackPanel { Spacing = 6 };
        foreach (var server in servers)
        {
            var box = new ComboBox
            {
                ItemsSource = versions, Width = 220,
                SelectedItem = string.IsNullOrEmpty(server.Version) ? T(Follow) : server.Version,
            };
            _boxes.Add(box);
            var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(server.Name) ? server.Address : server.Name,
                FontWeight = Avalonia.Media.FontWeight.SemiBold,
            });
            text.Children.Add(new TextBlock { Text = server.Address, Classes = { "small", "dim" } });
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 4) };
            row.Children.Add(text);
            Grid.SetColumn(box, 1);
            row.Children.Add(box);
            list.Children.Add(new Border { Classes = { "row" }, Padding = new Thickness(14, 10), Child = row });
        }
        root.Children.Add(new ScrollViewer
        {
            Content = list, MaxHeight = 300, Padding = new Thickness(0, 0, 8, 0),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });
        root.Children.Add(ButtonRow(MakeButton(T("取消"), onClick: () => Close()),
                                    MakeButton(T("导入"), "primary", "download", Submit)));
        Content = root;
    }

    private void ApplyAll(ComboBox bulk)
    {
        var version = bulk.SelectedItem as string ?? T(Follow);
        foreach (var box in _boxes)
            box.SelectedItem = version;
    }

    private void Submit()
    {
        for (var i = 0; i < _servers.Count && i < _boxes.Count; i++)
        {
            var version = _boxes[i].SelectedItem as string;
            _servers[i].Version = string.IsNullOrEmpty(version) || version == T(Follow) ? "" : version;
        }
        Close(_servers);
    }
}
