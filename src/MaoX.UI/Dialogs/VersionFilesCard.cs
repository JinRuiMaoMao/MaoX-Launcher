using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MaoX.Controls;
using MaoX.Core;
using static MaoX.Core.I18n;

namespace MaoX.Dialogs;

/// <summary>版本设置里的「模组 / 资源包 / 光影包」分区：直接列出已安装的内容，可以启用、禁用、删除和导入。</summary>
public class VersionFilesCard : UserControl
{
    private record Kind(string Key, string Folder, string Icon, string Name, string Pattern);

    private readonly Func<string> _gameDir;
    private readonly List<Kind> _kinds = [];
    private readonly Dictionary<string, ToggleButton> _tabs = [];
    private readonly Dictionary<string, int> _counts = [];
    private readonly StackPanel _list = new() { Spacing = 6 };
    private readonly TextBox _search = new() { Watermark = T("搜索"), MinWidth = 150 };
    private Kind _kind;
    private List<LocalFile> _items = [];
    private readonly Dictionary<string, Bitmap> _icons = [];
    private int _loadId;

    private static MainView Main => MainView.Current;

    public VersionFilesCard(Func<string> gameDir, bool modded)
    {
        _gameDir = gameDir;
        if (modded)
            _kinds.Add(new Kind("mod", "mods", "box", T("模组"), "*.jar"));
        _kinds.Add(new Kind("resourcepack", "resourcepacks", "image", T("资源包"), "*.zip"));
        _kinds.Add(new Kind("shader", "shaderpacks", "sparkle", T("光影包"), "*.zip"));
        _kind = _kinds[0];

        var tabs = new WrapPanel();
        foreach (var kind in _kinds)
        {
            var tab = new ToggleButton { Classes = { "chip" }, Content = kind.Name, Margin = new Thickness(0, 0, 8, 8) };
            tab.Click += (_, _) => Select(kind);
            _tabs[kind.Key] = tab;
            tabs.Children.Add(tab);
        }

        _search.InnerLeftContent = new Icon { Kind = "search", Size = 15, Margin = new Thickness(10, 0, 0, 0), Foreground = Res("Dim") };
        _search.TextChanged += (_, _) => Render();
        var add = DialogView.MakeButton(T("导入"), icon: "file", onClick: Import);
        var open = DialogView.MakeButton(T("打开文件夹"), "ghost", "folder", () =>
        {
            var folder = Folder();
            Directory.CreateDirectory(folder);
            Platform.OpenPath(folder);
        });
        add.MinWidth = open.MinWidth = 0;
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 8) };
        tools.Children.Add(_search);
        tools.Children.Add(add);
        tools.Children.Add(open);

        var bar = new DockPanel();
        DockPanel.SetDock(tools, Dock.Right);
        bar.Children.Add(tools);
        bar.Children.Add(tabs);

        var scroll = new ScrollViewer { Content = _list, MaxHeight = 300, Padding = new Thickness(0, 0, 8, 0) };
        var body = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        body.Children.Add(bar);
        body.Children.Add(scroll);

        Content = VersionDialog.SubCard(modded ? T("模组与资源") : T("资源包与光影"), "layers", body);

        UpdateTabs();
        Reload();
    }

    private static IBrush Res(string key) => (IBrush)Application.Current!.FindResource(key)!;

    private string Folder() => Path.Combine(_gameDir(), _kind.Folder);

    private void Select(Kind kind)
    {
        _kind = kind;
        _search.Text = "";
        UpdateTabs();
        Reload();
    }

    private void UpdateTabs()
    {
        foreach (var kind in _kinds)
        {
            var tab = _tabs[kind.Key];
            tab.IsChecked = kind == _kind;
            tab.Content = _counts.TryGetValue(kind.Key, out var n) && n > 0 ? $"{kind.Name} · {n}" : kind.Name;
        }
    }

    /// <summary>重新读取当前分类的文件（版本隔离切换后也要调用）。</summary>
    public async void Reload()
    {
        var id = ++_loadId;
        var kind = _kind;
        var gameDir = _gameDir();
        List<(Kind Kind, List<LocalFile> Items)> all;
        try
        {
            all = await Task.Run(() => _kinds.Select(k =>
            {
                var folder = Path.Combine(gameDir, k.Folder);
                // 其他分类只数个数，不读 jar 信息
                var items = k != kind ? CountOnly(folder, k.Key)
                    : k.Key == "mod" ? Mods.ListLocalMods(folder) : Mods.ListLocalFiles(folder);
                return (k, items);
            }).ToList());
        }
        catch (Exception)
        {
            all = [];
        }
        if (id != _loadId)
            return;
        foreach (var (k, items) in all)
            _counts[k.Key] = items.Count;
        _items = all.FirstOrDefault(a => a.Kind == kind).Items ?? [];
        UpdateTabs();
        Render();
        LoadIcons(id, _items.Where(i => !_icons.ContainsKey(i.Path)).Select(i => i.Path).ToList());
    }

    private static List<LocalFile> CountOnly(string folder, string kind)
    {
        if (!Directory.Exists(folder))
            return [];
        return Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName)
            .Where(f => kind == "mod"
                ? f.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)
                  || f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                : f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || Directory.Exists(Path.Combine(folder, f)))
            .Select(f => new LocalFile { Filename = f }).ToList();
    }

    private void LoadIcons(int id, List<string> paths)
    {
        if (paths.Count == 0)
            return;
        _ = Task.Run(() =>
        {
            foreach (var path in paths)
            {
                if (id != _loadId)
                    return;
                Bitmap bitmap = null;
                try
                {
                    if (Mods.ReadIcon(path) is { } png)
                    {
                        using var stream = new MemoryStream(png);
                        bitmap = new Bitmap(stream);
                        if (bitmap.PixelSize.Width > 128)
                        {
                            stream.Position = 0;
                            var small = Bitmap.DecodeToWidth(stream, 128);
                            bitmap.Dispose();
                            bitmap = small;
                        }
                    }
                }
                catch (Exception)
                {
                    bitmap = null;
                }
                Dispatcher.UIThread.Post(() =>
                {
                    _icons[path] = bitmap;
                    if (bitmap != null && id == _loadId)
                        Render();
                });
            }
        });
    }

    private void Render()
    {
        _list.Children.Clear();
        var filter = (_search.Text ?? "").Trim();
        var items = filter.Length == 0
            ? _items
            : _items.Where(i => (i.Name ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase)
                                || i.Filename.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        if (items.Count == 0)
        {
            var text = _items.Count > 0
                ? T("没有找到匹配的内容")
                : F("还没有{0}。可以在「资源」页下载，或者点「导入」添加文件", _kind.Name);
            _list.Children.Add(new TextBlock { Text = text, Classes = { "muted", "wrap" }, Margin = new Thickness(0, 6) });
            return;
        }
        foreach (var item in items)
            _list.Children.Add(Row(item));
    }

    private Control Row(LocalFile item)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto") };
        if (_kind.Key == "mod")
        {
            var toggle = new ToggleSwitch { IsChecked = item.Enabled, Margin = new Thickness(0, 0, 10, 0), OnContent = null, OffContent = null };
            toggle.IsCheckedChanged += (_, _) => Toggle(item, toggle);
            grid.Children.Add(toggle);
        }

        _icons.TryGetValue(item.Path, out var bitmap);
        Control icon;
        if (bitmap != null)
        {
            var image = new Image { Source = bitmap, Stretch = Stretch.UniformToFill };
            if (bitmap.PixelSize.Width < 64)
                RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.None);
            icon = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(7), ClipToBounds = true, Child = image };
        }
        else
        {
            icon = new Border
            {
                Width = 36, Height = 36, CornerRadius = new CornerRadius(7), Background = Res("AccentDim"),
                Child = new Icon { Kind = item.IsDir ? "folder" : _kind.Icon, Size = 18, Foreground = Res("Accent") },
            };
        }
        if (!item.Enabled)
            icon.Opacity = 0.4;
        Grid.SetColumn(icon, 1);
        grid.Children.Add(icon);

        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var name = new TextBlock { Text = item.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        if (!item.Enabled)
            name.Classes.Add("dim");
        line.Children.Add(name);
        if (!string.IsNullOrEmpty(item.Version))
            line.Children.Add(new TextBlock { Text = item.Version, Classes = { "small", "muted" }, VerticalAlignment = VerticalAlignment.Center });
        if (!item.Enabled)
            line.Children.Add(new TextBlock { Text = T("已禁用"), Classes = { "small", "dim" }, VerticalAlignment = VerticalAlignment.Center });
        var text = new StackPanel { Spacing = 2, Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(line);
        text.Children.Add(new TextBlock { Text = item.Filename, Classes = { "small", "dim" }, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(text, 2);
        grid.Children.Add(text);

        var delete = new Button { Classes = { "ghost", "danger-text" }, Content = new Icon { Kind = "delete", Size = 17 }, Padding = new Thickness(9, 0) };
        ToolTip.SetTip(delete, T("删除"));
        delete.Click += (_, _) => Delete(item);
        Grid.SetColumn(delete, 3);
        grid.Children.Add(delete);
        return new Border { Classes = { "row" }, Padding = new Thickness(10, 7), Child = grid };
    }

    private async void Toggle(LocalFile mod, ToggleSwitch toggle)
    {
        var enabled = toggle.IsChecked == true;
        if (enabled == mod.Enabled)
            return;
        try
        {
            var old = mod.Path;
            mod.Path = Mods.SetModEnabled(mod, enabled);
            if (_icons.Remove(old, out var bitmap))
                _icons[mod.Path] = bitmap;
        }
        catch (Exception e)
        {
            toggle.IsChecked = mod.Enabled;
            await Main.Dialog(T("操作失败"), F("无法重命名模组文件（游戏是否正在运行？）：{0}", e.Message), "error");
            return;
        }
        Reload();
    }

    private async void Delete(LocalFile item)
    {
        if (!await Main.Confirm(T("删除"), F("确定要删除 {0} 吗？此操作无法撤销。", item.Filename), T("删除"), "warn", "danger"))
            return;
        try
        {
            if (item.IsDir)
                Directory.Delete(item.Path, true);
            else
                File.Delete(item.Path);
        }
        catch (Exception e)
        {
            await Main.Dialog(T("删除失败"), e.Message, "error");
            return;
        }
        _icons.Remove(item.Path);
        Reload();
    }

    private async void Import()
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage == null)
            return;
        var kind = _kind;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = F("导入{0}", kind.Name),
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType(kind.Name) { Patterns = [kind.Pattern] },
                FilePickerFileTypes.All,
            ],
        });
        if (files.Count == 0)
            return;
        var folder = Path.Combine(_gameDir(), kind.Folder);
        Directory.CreateDirectory(folder);
        var copied = 0;
        foreach (var file in files)
        {
            try
            {
                var target = Path.Combine(folder, Path.GetFileName(file.Name));
                // 手机上选到的是 content:// 文件，只能用流复制
                await using var source = await file.OpenReadAsync();
                await using var dest = File.Create(target);
                await source.CopyToAsync(dest);
                copied++;
            }
            catch (Exception e)
            {
                Main.Toast(F("导入 {0} 失败：{1}", file.Name, e.Message), "error");
            }
        }
        if (copied > 0)
            Main.Toast(F("已导入 {0} 个文件", copied));
        Reload();
    }
}
