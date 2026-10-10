using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MaoX.Controls;
using MaoX.Core;
using MaoX.Dialogs;
using static MaoX.Core.I18n;

namespace MaoX.Pages;

/// <summary>资源页：从 Modrinth / CurseForge 浏览安装模组、资源包、光影、数据包与整合包，并管理已安装的文件。</summary>
public partial class ResourcesPage : UserControl, IPage
{
    private const int PageSize = 20;

    private record Target(string Version, string Loader, string LoaderVersion, string Game, string GameDir);

    private Target _ctx;
    private string _kind = "mod";
    private string _source = "modrinth";
    private string _tab = "modrinth";
    private Dictionary<string, IModClient> _clients = [];
    private Dictionary<string, HashSet<string>> _installedProjects = NewProjectSets();
    private List<LocalFile> _localItems = [];
    private Dictionary<string, ModUpdate> _updates = [];
    private List<WorldInfo> _worlds = [];
    private int _offset;
    private int _searchGen;
    private readonly DispatcherTimer _debounce;
    private readonly Dictionary<string, Button> _cardButtons = [];
    private readonly HashSet<string> _installing = [];
    private readonly Dictionary<string, Bitmap> _icons = [];
    private readonly SemaphoreSlim _iconSlots = new(6);
    private readonly Dictionary<string, ToggleButton> _kindChips = [];
    private bool _updatingBoxes;

    private static MainView Main => MainView.Current;

    public ResourcesPage()
    {
        InitializeComponent();
        foreach (var kind in Mods.Kinds)
        {
            var chip = new ToggleButton { Classes = { "chip" }, Content = T(kind.Name), IsChecked = kind.Key == _kind };
            chip.Click += (_, _) => SetKind(kind.Key);
            _kindChips[kind.Key] = chip;
            KindChips.Children.Add(chip);
        }
        SortBox.ItemsSource = Mods.Sorts.Select(s => T(s.Name)).ToList();
        SortBox.SelectedIndex = 0;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Search(true);
        };
        SearchBox.TextChanged += (_, _) =>
        {
            _debounce.Stop();
            _debounce.Start();
        };
        SearchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                _debounce.Stop();
                Search(true);
            }
        };
        Main.VersionsChanged += () => _ctx = null;
        UpdateTabs();
    }

    private static Dictionary<string, HashSet<string>> NewProjectSets() =>
        new() { ["modrinth"] = [], ["curseforge"] = [] };

    /// <summary>版本设置改变后（如版本隔离），下次显示时重新读取目标。</summary>
    public void Invalidate() => _ctx = null;

    // ------------------------------------------------------------------ 目标与类型

    public void OnShow()
    {
        var launcher = Main.MakeLauncher();
        var versions = launcher.InstalledVersions();
        var modded = versions.Where(v => Mods.ModLoaders.Contains(launcher.DetectLoader(v).Loader)).ToList();
        var ordered = modded.Concat(versions.Except(modded)).ToList();
        var current = VersionBox.SelectedItem as string;
        if (current == null || !versions.Contains(current))
        {
            var preferred = Main.SelectedVersion;
            current = preferred != null && modded.Contains(preferred) ? preferred
                : modded.FirstOrDefault() ?? ordered.FirstOrDefault();
        }
        _updatingBoxes = true;
        VersionBox.ItemsSource = ordered;
        VersionBox.SelectedItem = current;
        _updatingBoxes = false;
        if (_ctx == null || _ctx.Version != current || _ctx.GameDir != (current != null ? launcher.GameDirFor(current) : ""))
            SetTarget();
    }

    private void OnVersionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingBoxes)
            SetTarget();
    }

    private void SetTarget()
    {
        var launcher = Main.MakeLauncher();
        var version = VersionBox.SelectedItem as string;
        if (version != null)
        {
            var info = launcher.DetectLoader(version);
            _ctx = new Target(version, info.Loader, info.LoaderVersion, info.Game, launcher.GameDirFor(version));
        }
        else
        {
            _ctx = new Target(null, null, null, null, "");
        }
        _clients = new Dictionary<string, IModClient>
        {
            ["modrinth"] = new ModrinthClient(launcher.Dl), ["curseforge"] = new CurseForgeClient(launcher.Dl),
        };
        try
        {
            _worlds = version != null ? Instance.ListWorlds(_ctx.GameDir) : [];
        }
        catch (Exception)
        {
            _worlds = [];
        }
        var names = _worlds.Select(w => w.Name).ToList();
        var selected = WorldBox.SelectedItem as string;
        _updatingBoxes = true;
        WorldBox.ItemsSource = names;
        WorldBox.SelectedItem = selected != null && names.Contains(selected) ? selected : names.FirstOrDefault();
        _updatingBoxes = false;
        Reset();
    }

    private void SetKind(string kind)
    {
        _kind = kind;
        foreach (var (key, chip) in _kindChips)
            chip.IsChecked = key == kind;
        Reset();
    }

    private void OnWorldChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingBoxes)
            return;
        _installedProjects = NewProjectSets();
        ReloadLocal();
        Search(true);
    }

    private void Reset()
    {
        _installedProjects = NewProjectSets();
        _localItems = [];
        _updates = [];
        UpdateAllButton.IsVisible = false;
        LocalTab.Content = T("已安装");
        if (_kind == "modpack" && _tab == "local")
            _tab = _source;
        UpdateInfo();
        ShowTab();
        if (Available())
        {
            if (_kind != "modpack")
                ReloadLocal();
            Search(true);
        }
    }

    private string TargetDir()
    {
        if (_kind == "datapack")
        {
            var world = CurrentWorld();
            return world != null ? Path.Combine(world.Path, "datapacks") : "";
        }
        var folder = Mods.KindFolders.GetValueOrDefault(_kind);
        return _ctx?.Version != null && folder != null ? Path.Combine(_ctx.GameDir, folder) : "";
    }

    private WorldInfo CurrentWorld() => _worlds.FirstOrDefault(w => w.Name == WorldBox.SelectedItem as string);

    private string SearchVersion => _kind == "modpack" ? null : _ctx?.Game;

    private bool Available()
    {
        if (_kind == "modpack")
            return true;
        if (_ctx?.Version == null)
            return false;
        return _kind switch
        {
            "mod" => Mods.ModLoaders.Contains(_ctx.Loader),
            "datapack" => _worlds.Count > 0,
            _ => true,
        };
    }

    private void UpdateInfo()
    {
        WorldColumn.IsVisible = false;
        LocalTab.IsVisible = _kind != "modpack";
        InfoLoader.Text = "";
        InfoRest.Text = "";
        if (_kind == "modpack")
        {
            InfoRest.Text = T("安装整合包会创建一个新的游戏版本");
        }
        else if (_ctx?.Version != null && Available())
        {
            if (_kind == "mod")
            {
                InfoLoader.Text = $"{Mc.LoaderNames.GetValueOrDefault(_ctx.Loader, _ctx.Loader)} {_ctx.LoaderVersion}".Trim();
                InfoRest.Text = $"·  Minecraft {_ctx.Game}";
            }
            else
            {
                InfoLoader.Text = $"Minecraft {_ctx.Game}";
                WorldColumn.IsVisible = _kind == "datapack";
            }
        }
        else
        {
            EmptyMessage();
        }
    }

    private void EmptyMessage()
    {
        var version = _ctx?.Version;
        EmptyButton.IsVisible = true;
        if (version == null)
        {
            EmptyTitle.Text = T("还没有安装任何版本");
            EmptyText.Text = T("先在「下载」页安装一个游戏版本。");
        }
        else if (_kind == "datapack")
        {
            EmptyTitle.Text = F("{0} 还没有存档", version);
            EmptyText.Text = T("数据包需要安装到存档中，先进入游戏创建一个世界。");
            EmptyButton.IsVisible = false;
        }
        else
        {
            var hint = T("在「下载」页安装版本时选择 Forge、NeoForge、Fabric 或 Quilt，\n再回到这里为它下载模组。");
            if (_ctx.Loader == "optifine")
            {
                EmptyTitle.Text = F("{0} 是 OptiFine 独立版本，无法加载模组", version);
                EmptyText.Text = hint + T("\n想同时使用 OptiFine 和模组，可以安装 Forge 并勾选 OptiFine。");
            }
            else
            {
                EmptyTitle.Text = F("{0} 是原版，无法加载模组", version);
                EmptyText.Text = hint;
            }
        }
    }

    private void OnGoDownload(object sender, RoutedEventArgs e) => Main.ShowPage("download");

    // ------------------------------------------------------------------ 标签页

    private void OnTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string tab })
        {
            _tab = tab;
            ShowTab();
        }
    }

    private void UpdateTabs()
    {
        ModrinthTab.IsChecked = _tab == "modrinth";
        CurseForgeTab.IsChecked = _tab == "curseforge";
        LocalTab.IsChecked = _tab == "local";
    }

    private void ShowTab()
    {
        UpdateTabs();
        if (!Available())
        {
            EmptyView.IsVisible = true;
            BrowseView.IsVisible = LocalView.IsVisible = false;
            BrowseTools.IsVisible = LocalTools.IsVisible = false;
            return;
        }
        EmptyView.IsVisible = false;
        var browse = _tab != "local";
        BrowseView.IsVisible = BrowseTools.IsVisible = browse;
        LocalView.IsVisible = LocalTools.IsVisible = !browse;
        CheckUpdateButton.IsVisible = _kind == "mod";
        if (_kind != "mod")
            UpdateAllButton.IsVisible = false;
        if (browse && _tab != _source)
        {
            _source = _tab;
            Search(true);
        }
    }

    // ------------------------------------------------------------------ 搜索

    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ctx != null)
            Search(true);
    }

    private Control Status(string text, bool error = false, bool loading = false)
    {
        var block = new TextBlock
        {
            Text = text,
            Classes = { error ? "wrap" : "muted" },
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 30),
        };
        if (error)
            block.Foreground = (IBrush)Application.Current!.FindResource("Error")!;
        Control status = block;
        if (loading)
        {
            block.Margin = default;
            status = new StackPanel { Spacing = 8, Margin = new Thickness(0, 30), Children = { new PawLoader(), block } };
        }
        BrowseList.Children.Add(status);
        return status;
    }

    private async void Search(bool reset = false)
    {
        if (!Available() || !_clients.ContainsKey(_source))
            return;
        var gen = ++_searchGen;
        if (reset)
        {
            _offset = 0;
            _cardButtons.Clear();
            BrowseList.Children.Clear();
            BrowseView.Offset = default;
        }
        var source = _source;
        var kind = _kind;
        var client = _clients[source];
        var loading = Status(F("正在搜索 {0}…", client.Name), loading: true);
        var query = (SearchBox.Text ?? "").Trim();
        var index = Mods.Sorts[Math.Max(SortBox.SelectedIndex, 0)].Key;
        var offset = _offset;
        var game = SearchVersion;
        var loader = _ctx?.Loader;
        SearchResult result;
        try
        {
            result = await Task.Run(() => client.SearchAsync(query, game, loader, offset, PageSize, index, kind));
        }
        catch (Exception e)
        {
            if (gen != _searchGen)
                return;
            BrowseList.Children.Remove(loading);
            Status(F("搜索失败：{0}", MainView.ErrorText(e)), true);
            return;
        }
        if (gen != _searchGen)
            return;
        BrowseList.Children.Remove(loading);
        if (result.Hits.Count == 0 && offset == 0)
        {
            var name = T(Mods.KindNames[kind]);
            Status(game != null ? F("没有找到适用于 {0} 的{1}", game, name) : F("没有找到{0}", name));
            return;
        }
        foreach (var hit in result.Hits)
            BrowseList.Children.Add(Card(hit, source));
        _offset = offset + result.Hits.Count;
        if (_offset < result.Total)
        {
            var more = new Button
            {
                Content = F("加载更多（{0} / {1}）", _offset, result.Total),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 12),
            };
            more.Click += (_, _) =>
            {
                BrowseList.Children.Remove(more);
                Search();
            };
            BrowseList.Children.Add(more);
        }
    }

    public static string FormatCount(long n) => n switch
    {
        >= 1_000_000 when English => $"{n / 1_000_000.0:0.0}M",
        >= 10_000 when English => $"{n / 1_000.0:0.0}K",
        >= 100_000_000 => $"{n / 100_000_000.0:0.0} 亿",
        >= 10_000 => $"{n / 10_000.0:0.0} 万",
        _ => n.ToString(),
    };

    private Control Card(SearchHit hit, string source)
    {
        var accent = (IBrush)Application.Current!.FindResource("Accent")!;
        var iconBox = new Border
        {
            Width = 52, Height = 52, CornerRadius = new CornerRadius(11), ClipToBounds = true,
            Background = (IBrush)Application.Current!.FindResource("AccentDim")!,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = string.IsNullOrEmpty(hit.Title) ? "?" : hit.Title[..1].ToUpperInvariant(),
                Classes = { "h3" }, Foreground = accent,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        if (!string.IsNullOrEmpty(hit.IconUrl))
            LoadIcon(hit.IconUrl, iconBox);

        var title = new TextBlock { Text = hit.Title, FontWeight = FontWeight.SemiBold, FontSize = 15 };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        titleRow.Children.Add(title);
        if (!string.IsNullOrEmpty(hit.Author))
            titleRow.Children.Add(new TextBlock { Text = "by " + hit.Author, Classes = { "small", "dim" }, VerticalAlignment = VerticalAlignment.Center });

        var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var dim = (IBrush)Application.Current!.FindResource("Dim")!;
        meta.Children.Add(new Icon { Kind = "download", Size = 14, Foreground = dim });
        meta.Children.Add(new TextBlock { Text = FormatCount(hit.Downloads), Classes = { "small", "dim" }, Margin = new Thickness(0, 0, 10, 0) });
        var categories = hit.Categories.Take(4).ToList();
        if (categories.Count > 0)
            meta.Children.Add(new TextBlock { Text = string.Join("  ·  ", categories), Classes = { "small", "dim" } });

        var text = new StackPanel { Spacing = 3, Margin = new Thickness(16, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(titleRow);
        text.Children.Add(new TextBlock
        {
            Text = hit.Description, Classes = { "muted" }, TextWrapping = TextWrapping.Wrap, MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis, LineHeight = 21,
        });
        text.Children.Add(meta);

        var installed = _installedProjects[source].Contains(hit.Id);
        var button = new Button
        {
            Classes = { "primary" }, Width = 104, VerticalAlignment = VerticalAlignment.Center,
            Content = new IconLabel { Icon = installed ? "check" : "download", Text = installed ? T("已安装") : T("安装") },
            IsEnabled = !installed && !_installing.Contains(hit.Id),
        };
        if (_installing.Contains(hit.Id))
            button.Content = new IconLabel { Text = T("安装中…") };
        button.Click += (_, _) => Install(hit, source, button);
        _cardButtons[hit.Id] = button;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        grid.Children.Add(iconBox);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        Grid.SetColumn(button, 2);
        grid.Children.Add(button);
        var card = new Border { Classes = { "row" }, Padding = new Thickness(16, 14), Child = grid, Cursor = new Cursor(StandardCursorType.Hand) };
        card.Tapped += (_, e) =>
        {
            if (e.Source is Visual origin && origin.FindAncestorOfType<Button>(true) != null)
                return;
            ShowVersions(hit, source);
        };
        return card;
    }

    /// <summary>打开资源详情，查看并安装指定版本；整合包直接进入安装流程（里面可以选版本）。</summary>
    private async void ShowVersions(SearchHit hit, string source)
    {
        if (_kind == "modpack")
        {
            await ModpackActions.InstallFromHit(_clients[source], hit);
            return;
        }
        var ctx = _ctx;
        var kind = _kind;
        var target = new ResourceTarget(ctx.Version, ctx.Game, ctx.Loader, kind, TargetDir());
        var icon = string.IsNullOrEmpty(hit.IconUrl) ? null : _icons.GetValueOrDefault(hit.IconUrl);
        await Main.ShowDialogAsync(new ModVersionsDialog(hit, _clients[source], target, _installedProjects[source], icon, () =>
        {
            if (ctx != _ctx || kind != _kind)
                return;
            _updates.Clear();
            UpdateUpdateAll();
            MarkInstalled();
            ReloadLocal(false);
        }));
    }

    private async void LoadIcon(string url, Border box)
    {
        if (!_icons.TryGetValue(url, out var bitmap))
        {
            var dl = Main.MakeLauncher().Dl;
            await _iconSlots.WaitAsync();
            try
            {
                if (!_icons.TryGetValue(url, out bitmap))
                {
                    bitmap = await Task.Run(async () =>
                    {
                        try
                        {
                            var data = await dl.FetchAsync(url, false);
                            using var stream = new MemoryStream(data);
                            return Bitmap.DecodeToWidth(stream, 104, BitmapInterpolationMode.HighQuality);
                        }
                        catch (Exception)
                        {
                            return null;
                        }
                    });
                    _icons[url] = bitmap;
                }
            }
            finally
            {
                _iconSlots.Release();
            }
        }
        if (bitmap != null)
            box.Child = new Image { Source = bitmap, Stretch = Stretch.UniformToFill };
    }

    private async void Install(SearchHit hit, string source, Button button)
    {
        if (_kind == "modpack")
        {
            await ModpackActions.InstallFromHit(_clients[source], hit);
            return;
        }
        var ctx = _ctx;
        var client = _clients[source];
        var projects = _installedProjects[source];
        var kind = _kind;
        var target = TargetDir();
        _installing.Add(hit.Id);
        button.IsEnabled = false;
        button.Content = new IconLabel { Text = T("安装中…") };
        Main.SetStatus(F("正在安装 {0}…", hit.Title));
        List<string> files;
        try
        {
            files = await Task.Run(() => client.InstallAsync(hit.Id, ctx.Game, ctx.Loader, target, projects, Main.Log, kind));
        }
        catch (Exception e)
        {
            _installing.Remove(hit.Id);
            Main.SetStatus(T("就绪"));
            button.Content = new IconLabel { Icon = "download", Text = T("安装") };
            button.IsEnabled = true;
            await Main.Dialog(F("安装 {0} 失败", hit.Title), MainView.ErrorText(e), "error");
            return;
        }
        _installing.Remove(hit.Id);
        Main.SetStatus(T("就绪"));
        var extra = files.Count - 1;
        var message = F("已安装 {0}", hit.Title) + (extra > 0 ? F("（含 {0} 个前置）", extra) : "");
        if (kind == "shader" && ctx.Loader != "optifine")
            message += T("，需要 OptiFine 或 Iris 才能使用");
        Main.Toast(message);
        projects.Add(hit.Id);
        MarkInstalled();
        ReloadLocal(false);
    }

    private void MarkInstalled()
    {
        var projects = _installedProjects.GetValueOrDefault(_source) ?? [];
        foreach (var (pid, button) in _cardButtons)
        {
            if (projects.Contains(pid) && !_installing.Contains(pid))
            {
                button.Content = new IconLabel { Icon = "check", Text = T("已安装") };
                button.IsEnabled = false;
            }
        }
    }

    // ------------------------------------------------------------------ 已安装

    private void OnRefreshLocal(object sender, RoutedEventArgs e) => ReloadLocal();

    private async void ReloadLocal(bool identify = true)
    {
        if (!Available() || _kind == "modpack")
            return;
        var ctx = _ctx;
        var kind = _kind;
        var folder = TargetDir();
        var clients = _clients;
        List<LocalFile> items;
        try
        {
            items = await Task.Run(() => kind == "mod" ? Mods.ListLocalMods(folder) : Mods.ListLocalFiles(folder));
        }
        catch (Exception)
        {
            items = [];
        }
        if (ctx != _ctx || kind != _kind)
            return;
        _localItems = items;
        LocalTab.Content = F("已安装 · {0}", items.Count);
        RenderLocal();
        if (!identify)
            return;
        var files = items.Where(i => !i.IsDir).ToList();
        foreach (var (key, client) in clients)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var ids = await client.InstalledIdsAsync(files);
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (ctx != _ctx || kind != _kind)
                            return;
                        _installedProjects[key].UnionWith(ids);
                        MarkInstalled();
                    });
                }
                catch (Exception)
                {
                    // 识别失败只影响「已安装」标记
                }
            });
        }
    }

    private void RenderLocal()
    {
        LocalList.Children.Clear();
        if (_localItems.Count == 0)
        {
            LocalList.Children.Add(new TextBlock
            {
                Text = F("还没有安装{0}，去 Modrinth 或 CurseForge 装一些吧", T(Mods.KindNames[_kind])),
                Classes = { "muted" }, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 30),
            });
            return;
        }
        var success = (IBrush)Application.Current!.FindResource("Success")!;
        var accent = (IBrush)Application.Current!.FindResource("Accent")!;
        foreach (var item in _localItems)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
            if (_kind == "mod")
            {
                var toggle = new ToggleSwitch { IsChecked = item.Enabled, Margin = new Thickness(0, 0, 14, 0) };
                toggle.IsCheckedChanged += (_, _) => Toggle(item, toggle);
                grid.Children.Add(toggle);
            }
            else
            {
                grid.Children.Add(new Icon { Kind = item.IsDir ? "folder" : "pack", Size = 22, Foreground = accent, Margin = new Thickness(4, 0, 16, 0) });
            }
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            var name = new TextBlock { Text = item.Name, FontWeight = FontWeight.SemiBold };
            if (!item.Enabled)
                name.Classes.Add("dim");
            line.Children.Add(name);
            if (!string.IsNullOrEmpty(item.Version))
                line.Children.Add(new TextBlock { Text = item.Version, Classes = { "small", "muted" }, VerticalAlignment = VerticalAlignment.Center });
            _updates.TryGetValue(item.Path, out var update);
            if (update != null)
                line.Children.Add(new TextBlock { Text = F("可更新 → {0}", update.Version), Classes = { "small", "bold" }, Foreground = success, VerticalAlignment = VerticalAlignment.Center });
            var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(line);
            text.Children.Add(new TextBlock { Text = item.Filename, Classes = { "small", "dim" } });
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            if (update != null)
            {
                var updateButton = new Button { Content = new IconLabel { Icon = "download", Text = T("更新") }, Margin = new Thickness(0, 0, 6, 0) };
                updateButton.Click += (_, _) => RunUpdates([update]);
                Grid.SetColumn(updateButton, 2);
                grid.Children.Add(updateButton);
            }
            var delete = new Button { Classes = { "ghost", "danger-text" }, Content = new Icon { Kind = "delete", Size = 17 }, Padding = new Thickness(9, 0) };
            ToolTip.SetTip(delete, T("删除"));
            delete.Click += (_, _) => Delete(item);
            Grid.SetColumn(delete, 3);
            grid.Children.Add(delete);
            LocalList.Children.Add(new Border { Classes = { "row" }, Padding = new Thickness(16, 10, 10, 10), Child = grid });
        }
    }

    private async void Toggle(LocalFile mod, ToggleSwitch toggle)
    {
        var enabled = toggle.IsChecked == true;
        if (enabled == mod.Enabled)
            return;
        try
        {
            mod.Path = Mods.SetModEnabled(mod, enabled);
        }
        catch (Exception e)
        {
            toggle.IsChecked = mod.Enabled;
            await Main.Dialog(T("操作失败"), F("无法重命名模组文件（游戏是否正在运行？）：{0}", e.Message), "error");
            return;
        }
        _updates.Clear();
        UpdateAllButton.IsVisible = false;
        ReloadLocal(false);
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
        _updates.Remove(item.Path);
        _installedProjects = NewProjectSets();
        ReloadLocal();
        Search(true);
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        var folder = TargetDir();
        if (folder.Length == 0)
            return;
        Directory.CreateDirectory(folder);
        Platform.OpenPath(folder);
    }

    // ------------------------------------------------------------------ 模组更新

    private async void OnCheckUpdates(object sender, RoutedEventArgs e)
    {
        var mods = _kind == "mod" ? _localItems.ToList() : [];
        if (mods.Count == 0)
        {
            Main.Toast(T("没有可以检查的模组"), "warn");
            return;
        }
        var ctx = _ctx;
        var modrinth = (ModrinthClient)_clients["modrinth"];
        var curseforge = (CurseForgeClient)_clients["curseforge"];
        CheckUpdateButton.IsEnabled = false;
        CheckUpdateLabel.Text = T("检查中…");
        List<ModUpdate> updates;
        try
        {
            updates = await Task.Run(() => Mods.CheckUpdatesAsync(modrinth, curseforge, mods, ctx.Game, ctx.Loader));
        }
        catch (Exception ex)
        {
            await Main.Dialog(T("检查更新失败"), MainView.ErrorText(ex), "error");
            return;
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
            CheckUpdateLabel.Text = T("检查更新");
        }
        if (ctx != _ctx)
            return;
        _updates = updates.ToDictionary(u => u.Mod.Path);
        RenderLocal();
        UpdateUpdateAll();
        Main.Toast(updates.Count > 0 ? F("有 {0} 个模组可以更新", updates.Count) : T("所有模组都是最新版本"));
    }

    private void UpdateUpdateAll()
    {
        UpdateAllButton.IsVisible = _updates.Count > 0;
        UpdateAllLabel.Text = F("全部更新（{0}）", _updates.Count);
    }

    private void OnUpdateAll(object sender, RoutedEventArgs e) => RunUpdates(_updates.Values.ToList());

    private void RunUpdates(List<ModUpdate> updates)
    {
        var dl = Main.MakeLauncher().Dl;
        _ = Main.RunTask(T("更新模组"), async () =>
        {
            var failed = new List<string>();
            for (var i = 0; i < updates.Count; i++)
            {
                Main.Progress(i, updates.Count, T("更新模组"));
                try
                {
                    await Mods.ApplyUpdateAsync(dl, updates[i]);
                }
                catch (Exception ex)
                {
                    failed.Add(F("{0}：{1}", updates[i].Mod.Name, MainView.ErrorText(ex)));
                }
            }
            return failed;
        }, failed =>
        {
            foreach (var u in updates)
                _updates.Remove(u.Mod.Path);
            UpdateUpdateAll();
            ReloadLocal(false);
            if (failed.Count > 0)
                _ = Main.Dialog(T("部分模组更新失败"), string.Join("\n", failed.Take(8)), "error");
            else
                Main.Toast(F("已更新 {0} 个模组", updates.Count));
        });
    }

    // ------------------------------------------------------------------ 整合包

    private void OnImportModpack(object sender, RoutedEventArgs e) => _ = ModpackActions.ImportFile();
}
