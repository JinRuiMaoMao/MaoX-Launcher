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

/// <summary>当前实例相对某份备份的文件差异。</summary>
public class InstanceDiffDialog : DialogView
{
    public InstanceDiffDialog(InstanceBackup backup, IReadOnlyList<FileChange> changes)
    {
        DialogWidth = 560;
        var root = new StackPanel { Spacing = 8 };
        root.Children.Add(Title(T("备份差异")));
        root.Children.Add(Paragraph(F("{0}  ·  {1:0.0} MB", backup.Time.ToString("yyyy-MM-dd HH:mm"), backup.Size / 1048576.0)));
        if (changes.Count == 0)
            root.Children.Add(Text(T("和这份备份完全一样"), "muted"));
        else
        {
            var added = changes.Count(c => c.Kind == "added");
            var removed = changes.Count(c => c.Kind == "removed");
            var changed = changes.Count(c => c.Kind == "changed");
            root.Children.Add(Text(F("新增 {0}  ·  删除 {1}  ·  修改 {2}", added, removed, changed), "small", "dim"));
            var list = new StackPanel { Spacing = 4, Margin = new Thickness(0, 6, 0, 0) };
            foreach (var change in changes.Take(80))
            {
                var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
                line.Children.Add(Text(InstanceSnapshot.KindText(change.Kind), "small", "accent"));
                var name = new TextBlock
                {
                    Text = change.Rel, Classes = { "small" }, Margin = new Thickness(10, 0, 0, 0),
                    TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                };
                Grid.SetColumn(name, 1);
                line.Children.Add(name);
                list.Children.Add(line);
            }
            if (changes.Count > 80)
                list.Children.Add(Text(F("还有 {0} 个文件未列出", changes.Count - 80), "small", "dim"));
            root.Children.Add(new ScrollViewer { Content = list, MaxHeight = 360 });
        }
        root.Children.Add(ButtonRow(MakeButton(T("关闭"), onClick: () => Close())));
        Content = root;
    }
}
