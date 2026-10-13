using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using MaoX.Controls;
using MaoX.Core;
using static MaoX.Core.I18n;

namespace MaoX.Dialogs;

/// <summary>一个存档的备份列表。选中“还原”时返回那份 WorldBackup，取消返回 null。</summary>
public class WorldBackupsDialog : DialogView
{
    public WorldBackupsDialog(WorldInfo world, IReadOnlyList<WorldBackup> backups)
    {
        DialogWidth = 520;
        var root = new StackPanel { Spacing = 8 };
        root.Children.Add(Title(F("还原存档 {0}", world.Name)));
        root.Children.Add(Paragraph(T("用备份替换现在的存档。还原前会先把现在的存档再备份一份，选错了也能找回来。")));
        if (backups.Count == 0)
        {
            root.Children.Add(Text(T("这个存档还没有备份"), "muted"));
        }
        else
        {
            var list = new StackPanel { Spacing = 6, Margin = new Thickness(0, 6, 0, 0) };
            foreach (var backup in backups)
                list.Children.Add(Row(backup));
            root.Children.Add(new ScrollViewer { Content = list, MaxHeight = 360 });
        }
        root.Children.Add(ButtonRow(MakeButton(T("关闭"), onClick: () => Close())));
        Content = root;
    }

    private Control Row(WorldBackup backup)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = backup.Time.ToString("yyyy-MM-dd HH:mm:ss"), FontWeight = Avalonia.Media.FontWeight.SemiBold });
        var kind = backup.Auto ? T("自动备份") : T("手动备份");
        text.Children.Add(Text($"{kind}  ·  {backup.Size / 1048576.0:0.0} MB", "small", "dim"));
        grid.Children.Add(text);
        var restore = MakeButton(T("还原"), "ghost", "download", () => Close(backup));
        restore.MinWidth = 0;
        Grid.SetColumn(restore, 1);
        grid.Children.Add(restore);
        return new Border { Classes = { "row" }, Padding = new Thickness(12, 8), Child = grid };
    }
}
