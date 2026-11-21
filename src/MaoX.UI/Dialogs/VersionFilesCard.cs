using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MaoX.Controls;
using MaoX.Core;
using static MaoX.Core.I18n;

namespace MaoX.Dialogs;

/// <summary>版本设置里的「模组 / 资源包 / 光影包」分区：直接列出已安装的内容，可以启用、禁用、删除和导入。</summary>
public class VersionFilesCard : UserControl
{
    private record Kind(string Key, string Folder, string Icon, string Name, string Pattern);

    private readonly Func<string> _gameDir;
    private readonly Func<string> _versionId;
    private readonly List<Kind> _kinds = [];
    private readonly Dictionary<string, ToggleButton> _tabs = [];
    private readonly Dictionary<string, int> _counts = [];
    private readonly ListBox _list = new() { Classes = { "plain" }, Padding = new Thickness(0, 0, 8, 0) };
    private readonly TextBlock _empty = new() { Classes = { "muted", "wrap" }, Margin = new Thickness(0, 6) };
    private readonly TextBox _search = new() { Watermark = T("搜索"), MinWidth = 150 };
    private readonly DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(160) };
    private Kind _kind;
    private List<LocalFile> _items = [];
    private readonly Dictionary<string, Bitmap> _icons = [];
    private readonly Dictionary<string, (Image Image, Control Placeholder)> _iconViews = [];
    private readonly Dictionary<string, ModUpdate> _updates = new(StringComparer.OrdinalIgnoreCase);
    private Button _checkButton;
    private Button _updateAllButton;
    private int _loadId;
    private bool _opening;
    private bool _checking;

    private static MainView Main => MainView.Current;

    public VersionFilesCard(Func<string> gameDir, bool modded, Func<string> versionId = null, bool fill = false)
    {
        _gameDir = gameDir;
        _versionId = versionId;
        if (!fill)
            _list.MaxHeight = 300;
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
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            Render();
        };
        _search.TextChanged += (_, _) =>
        {
            _searchDebounce.Stop();
            _searchDebounce.Start();
        };
        _list.ItemTemplate = new FuncDataTemplate<LocalFile>((item, _) => Row(item), true);
        var add = DialogView.MakeButton(T("导入"), icon: "file", onClick: Import);
        var open = DialogView.MakeButton(T("打开文件夹"), "ghost", "folder", () =>
        {
            var folder = Folder();
            Directory.CreateDirectory(folder);
            Platform.OpenPath(folder);
        });
        _checkButton = DialogView.MakeButton(T("检查更新"), "ghost", "refresh", CheckUpdates);
        _updateAllButton = DialogView.MakeButton(T("全部更新"), "primary", "download", UpdateAll);
        _updateAllButton.IsVisible = false;
        add.MinWidth = open.MinWidth = _checkButton.MinWidth = _updateAllButton.MinWidth = 0;
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 8) };
        tools.Children.Add(_search);
        tools.Children.Add(_checkButton);
        tools.Children.Add(_updateAllButton);
        tools.Children.Add(add);
        tools.Children.Add(open);

        var bar = new DockPanel();
        DockPanel.SetDock(tools, Dock.Right);
        bar.Children.Add(tools);
        bar.Children.Add(tabs);

        var body = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        body.Children.Add(bar);
        body.Children.Add(new Panel { Children = { _empty, _list } });

        Content = VersionDialog.SubCard(modded ? T("模组与资源") : T("资源包与光影"), "layers", body);

        UpdateTabs();
        Reload();
    }

    private static IBrush Res(string key) => (IBrush)Application.Current!.FindResource(key)!;

    private string Folder() => Path.Combine(_gameDir(), _kind.Folder);

    private void Select(Kind kind)
    {
        _kind = kind;
        _updates.Clear();
        UpdateUpdateAll();
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
            Parallel.ForEach(paths, new ParallelOptions { MaxDegreeOfParallelism = 4 }, path =>
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
                if (bitmap == null)
                    return;
                Dispatcher.UIThread.Post(() =>
                {
                    if (id != _loadId)
                    {
                        bitmap.Dispose();
                        return;
                    }
                    _icons[path] = bitmap;
                    if (_iconViews.TryGetValue(path, out var view))
                    {
                        view.Image.Source = bitmap;
                        if (bitmap.PixelSize.Width < 64)
                            RenderOptions.SetBitmapInterpolationMode(view.Image, BitmapInterpolationMode.None);
                        view.Image.IsVisible = true;
                        view.Placeholder.IsVisible = false;
                    }
                });
            });
        });
    }

    private void Render()
    {
        var filter = (_search.Text ?? "").Trim();
        var items = filter.Length == 0
            ? _items
            : _items.Where(i => (i.Name ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase)
                                || i.Filename.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        _empty.IsVisible = items.Count == 0;
        _list.IsVisible = items.Count > 0;
        _empty.Text = _items.Count > 0
            ? T("没有找到匹配的内容")
            : F("还没有{0}。可以在「下载」页下载，或者点「导入」添加文件", _kind.Name);
        _iconViews.Clear();
        _list.ItemsSource = items;
    }

    private Control Row(LocalFile item)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto") };
        if (_kind.Key == "mod")
        {
            var toggle = new ToggleSwitch { IsChecked = item.Enabled, Margin = new Thickness(0, 0, 10, 0), OnContent = null, OffContent = null };
            toggle.IsCheckedChanged += (_, _) => Toggle(item, toggle);
            grid.Children.Add(toggle);
        }

        _icons.TryGetValue(item.Path, out var bitmap);
        var image = new Image { Source = bitmap, Stretch = Stretch.UniformToFill, IsVisible = bitmap != null };
        if (bitmap != null && bitmap.PixelSize.Width < 64)
            RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.None);
        var placeholder = new Border
        {
            Width = 36, Height = 36, CornerRadius = new CornerRadius(7), Background = Res("AccentDim"),
            IsVisible = bitmap == null,
            Child = new Icon { Kind = item.IsDir ? "folder" : _kind.Icon, Size = 18, Foreground = Res("Accent") },
        };
        _iconViews[item.Path] = (image, placeholder);
        var icon = new Border
        {
            Width = 36, Height = 36, CornerRadius = new CornerRadius(7), ClipToBounds = true,
            Child = new Panel { Children = { placeholder, image } },
        };
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
        _updates.TryGetValue(item.Path, out var update);
        if (update != null)
            line.Children.Add(new TextBlock
            {
                Text = F("可更新 → {0}", update.Version), Classes = { "small", "bold" },
                Foreground = Res("Success"), VerticalAlignment = VerticalAlignment.Center,
            });
        var text = new StackPanel { Spacing = 2, Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(line);
        text.Children.Add(new TextBlock { Text = item.Filename, Classes = { "small", "dim" }, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(text, 2);
        grid.Children.Add(text);

        if (update != null)
        {
            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center,
            };
            if (!string.IsNullOrWhiteSpace(update.Changelog))
            {
                var notes = new Button
                {
                    Classes = { "ghost" }, Content = T("说明"), Padding = new Thickness(7, 0), FontSize = 12,
                };
                ToolTip.SetTip(notes, T("查看这个版本的更新日志"));
                notes.Click += async (_, _) =>
                    await Main.Dialog(F("{0} 更新说明", item.Name), update.Changelog, "info");
                actions.Children.Add(notes);
            }
            var updateButton = new Button { Content = new IconLabel { Icon = "download", Text = T("更新") } };
            updateButton.Click += (_, _) => RunUpdates([update]);
            actions.Children.Add(updateButton);
            Grid.SetColumn(actions, 3);
            grid.Children.Add(actions);
        }

        var delete = new Button { Classes = { "ghost", "danger-text" }, Content = new Icon { Kind = "delete", Size = 17 }, Padding = new Thickness(9, 0) };
        ToolTip.SetTip(delete, T("删除"));
        delete.Click += (_, _) => Delete(item);
        Grid.SetColumn(delete, 4);
        grid.Children.Add(delete);
        var row = new Border
        {
            Classes = { "row" }, Padding = new Thickness(10, 7), Margin = new Thickness(0, 0, 0, 6),
            Child = grid, Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(row, T("在下载页打开"));
        row.Tapped += (_, e) =>
        {
            if (e.Source is Visual origin
                && (origin.FindAncestorOfType<Button>() != null || origin.FindAncestorOfType<ToggleSwitch>() != null))
                return;
            OpenOnline(item);
        };
        return row;
    }

    private async void OpenOnline(LocalFile item)
    {
        if (_opening || item == null)
            return;
        if (item.IsDir || string.IsNullOrEmpty(item.Path) || !File.Exists(item.Path))
        {
            Main.Toast(T("文件夹没有对应的在线页面"), "info");
            return;
        }
        _opening = true;
        var name = string.IsNullOrEmpty(item.Name) ? item.Filename : item.Name;
        Main.SetStatus(F("正在查找 {0}…", name));
        try
        {
            var dl = Main.MakeLauncher().Dl;
            var kind = _kind.Key;
            var modrinth = new ModrinthClient(dl);
            var curseforge = new CurseForgeClient(dl);
            var found = await Task.WhenAll(
                modrinth.FindByFileAsync(item.Path, kind),
                curseforge.FindByFileAsync(item.Path, kind));
            var hit = found[0] ?? found[1];
            var source = found[0] != null ? "modrinth" : found[1] != null ? "curseforge" : null;
            if (hit == null)
            {
                foreach (var (key, client) in new (string, IModClient)[] { ("modrinth", modrinth), ("curseforge", curseforge) })
                {
                    try
                    {
                        var result = await client.SearchAsync(name, null, null, 0, 8, "relevance", kind);
                        hit = result.Hits.FirstOrDefault(h =>
                            string.Equals(h.Title, name, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(h.Slug, name, StringComparison.OrdinalIgnoreCase));
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    if (hit == null)
                        continue;
                    source = key;
                    break;
                }
            }
            if (hit == null)
            {
                Main.Toast(F("没有在 Modrinth 或 CurseForge 上找到 {0}", name), "warn");
                return;
            }
            var version = _versionId?.Invoke();
            Main.ShowPage("download");
            Main.DownloadPage.ShowProject(version, kind, hit, source);
        }
        catch (Exception e)
        {
            Main.Toast(F("查找失败：{0}", MainView.ErrorText(e)), "error");
        }
        finally
        {
            _opening = false;
            Main.SetStatus(T("就绪"));
        }
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
            _updates.Remove(old);
            UpdateUpdateAll();
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
        _updates.Remove(item.Path);
        UpdateUpdateAll();
        Reload();
    }

    private (string Game, string Loader)? GameContext()
    {
        var version = _versionId?.Invoke();
        if (string.IsNullOrEmpty(version))
            return null;
        try
        {
            var info = Main.MakeLauncher().DetectLoader(version);
            return (info.Game, info.Loader);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async void CheckUpdates()
    {
        if (_checking)
            return;
        var ctx = GameContext();
        if (ctx == null)
        {
            Main.Toast(T("找不到当前版本，请从「启动」页重新打开版本设置"), "warn");
            return;
        }
        var items = _items.Where(i => !i.IsDir && File.Exists(i.Path)).ToList();
        if (items.Count == 0)
        {
            Main.Toast(F("没有可以检查的{0}", _kind.Name), "warn");
            return;
        }
        var kind = _kind.Key;
        var version = _versionId?.Invoke();
        var dl = Main.MakeLauncher().Dl;
        var modrinth = new ModrinthClient(dl);
        var curseforge = new CurseForgeClient(dl);
        _checking = true;
        _checkButton.IsEnabled = false;
        var oldContent = _checkButton.Content;
        _checkButton.Content = new IconLabel { Icon = "refresh", Text = T("检查中…") };
        try
        {
            var updates = await Task.Run(() =>
                Mods.CheckUpdatesAsync(modrinth, curseforge, items, ctx.Value.Game, ctx.Value.Loader, kind));
            if (_kind.Key != kind || _versionId?.Invoke() != version)
                return;
            _updates.Clear();
            foreach (var update in updates)
                _updates[update.Mod.Path] = update;
            Render();
            UpdateUpdateAll();
            Main.Toast(updates.Count > 0
                           ? F("有 {0} 个{1}可以更新", updates.Count, _kind.Name)
                           : F("所有{0}都是最新版本", _kind.Name));
        }
        catch (Exception e)
        {
            await Main.Dialog(T("检查更新失败"), MainView.ErrorText(e), "error");
        }
        finally
        {
            _checking = false;
            _checkButton.IsEnabled = true;
            _checkButton.Content = oldContent;
        }
    }

    private void UpdateUpdateAll()
    {
        // 检查按钮和「全部更新」占同一个位置，避免小窗口里工具栏挤成两行。
        var hasUpdates = _updates.Count > 0;
        _updateAllButton.IsVisible = hasUpdates;
        _checkButton.IsVisible = !hasUpdates;
        _updateAllButton.Content = new IconLabel
        {
            Icon = "download", Text = F("全部更新（{0}）", _updates.Count),
        };
    }

    private void UpdateAll() => RunUpdates(_updates.Values.ToList());

    private void RunUpdates(List<ModUpdate> updates)
    {
        if (updates.Count == 0)
            return;
        var ctx = GameContext();
        if (ctx == null)
        {
            Main.Toast(T("找不到当前版本，请从「启动」页重新打开版本设置"), "warn");
            return;
        }
        var dl = Main.MakeLauncher().Dl;
        var kind = _kind.Key;
        var name = _kind.Name;
        var items = _items.Where(i => !i.IsDir).ToList();
        _ = Main.RunTask(F("更新{0}", name), async () =>
        {
            var modrinth = new ModrinthClient(dl);
            var curseforge = new CurseForgeClient(dl);
            var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var client in new IModClient[] { modrinth, curseforge })
                    installed.UnionWith(await client.InstalledIdsAsync(items));
            }
            catch (Exception)
            {
                // 识别失败最多多下一次前置，不影响更新
            }
            var updated = new List<ModUpdate>();
            var failed = new List<string>();
            for (var i = 0; i < updates.Count; i++)
            {
                Main.Progress(i, updates.Count, F("更新{0}", name));
                var update = updates[i];
                try
                {
                    var client = update.Source == "CurseForge" ? (IModClient)curseforge : modrinth;
                    await Mods.ApplyUpdateAsync(client, dl, update, ctx.Value.Game, ctx.Value.Loader, installed, kind,
                                                Main.Log);
                    updated.Add(update);
                }
                catch (Exception e)
                {
                    failed.Add(F("{0}：{1}", update.Mod.Name, MainView.ErrorText(e)));
                }
            }
            return (Updated: updated, Failed: failed);
        }, result =>
        {
            foreach (var update in result.Updated)
                _updates.Remove(update.Mod.Path);
            UpdateUpdateAll();
            Reload();
            if (result.Failed.Count > 0)
                _ = Main.Dialog(F("部分{0}更新失败", name), string.Join("\n", result.Failed.Take(8)), "error");
            else
                Main.Toast(F("已更新 {0} 个{1}", result.Updated.Count, name));
        });
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
        {
            _updates.Clear();
            UpdateUpdateAll();
            Main.Toast(F("已导入 {0} 个文件", copied));
        }
        Reload();
    }
}
