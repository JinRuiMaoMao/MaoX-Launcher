using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MaoX.Controls;
using MaoX.Core;
using MaoX.Pages;
using static MaoX.Core.I18n;

namespace MaoX.Dialogs;

/// <summary>资源要装到哪里：游戏版本、对应的 Minecraft 版本、加载器、资源类型和目标文件夹。</summary>
public record ResourceTarget(string Version, string Game, string Loader, string Kind, string Dir);

/// <summary>资源详情：查看全部版本（可按 Minecraft 版本和加载器筛选），安装指定版本或替换已安装的版本。</summary>
public class ModVersionsDialog : DialogView
{
    private const int ShowStep = 40;

    private readonly SearchHit _hit;
    private readonly IModClient _client;
    private readonly ResourceTarget _target;
    private readonly ISet<string> _projects;
    private readonly Action _changed;
    private readonly ComboBox _gameBox = new() { MinWidth = 150 };
    private readonly ComboBox _loaderBox = new() { MinWidth = 130 };
    private readonly TextBlock _summary = new() { Classes = { "small", "dim" }, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _installedText = new()
    {
        Classes = { "small" }, TextTrimming = TextTrimming.CharacterEllipsis, IsVisible = false, Margin = new Thickness(0, 10, 0, 0),
    };
    private readonly StackPanel _list = new() { Spacing = 8 };
    private readonly ScrollViewer _scroll;
    private readonly List<string> _games = [];
    private readonly List<string> _loaders = [];
    private readonly List<ModFileVersion> _versions = [];
    private readonly Dictionary<ModFileVersion, Button> _buttons = [];
    private List<(string VersionId, LocalFile File)> _installed = [];
    private (string Game, string Loader) _query;
    private object _next;
    private Button _more;
    private int _shown;
    private int _gen;
    private ModFileVersion _installing;
    private bool _ready;

    private static MainWindow Main => MainWindow.Current;

    private static IBrush Res(string key) => (IBrush)Application.Current!.FindResource(key)!;

    public ModVersionsDialog(SearchHit hit, IModClient client, ResourceTarget target, ISet<string> installedProjects,
                             Bitmap icon, Action changed)
    {
        _hit = hit;
        _client = client;
        _target = target;
        _projects = installedProjects;
        _changed = changed;
        DialogWidth = 780;
        MaxHeight = 760;

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        root.Children.Add(Header(icon));

        var filters = Filters();
        Grid.SetRow(filters, 1);
        root.Children.Add(filters);

        _scroll = new ScrollViewer
        {
            Content = _list, MinHeight = 300, Margin = new Thickness(0, 12, -14, 0), Padding = new Thickness(0, 0, 14, 0),
        };
        Grid.SetRow(_scroll, 2);
        root.Children.Add(_scroll);

        var buttons = ButtonRow(MakeButton(T("关闭"), onClick: () => Close()));
        buttons.Margin = new Thickness(0, 16, 0, 0);
        Grid.SetRow(buttons, 3);
        root.Children.Add(buttons);
        Content = root;
    }

    private Control Header(Bitmap icon)
    {
        var iconBox = new Border
        {
            Width = 56, Height = 56, CornerRadius = new CornerRadius(12), ClipToBounds = true,
            Background = Res("AccentDim"), VerticalAlignment = VerticalAlignment.Top,
            Child = icon != null
                ? new Image { Source = icon, Stretch = Stretch.UniformToFill }
                : new TextBlock
                {
                    Text = string.IsNullOrEmpty(_hit.Title) ? "?" : _hit.Title[..1].ToUpperInvariant(), Classes = { "h3" },
                    Foreground = Res("Accent"), HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
        };

        var text = new StackPanel { Spacing = 3, Margin = new Thickness(16, 0, 16, 0) };
        text.Children.Add(new TextBlock { Text = _hit.Title, Classes = { "h2" }, TextTrimming = TextTrimming.CharacterEllipsis });
        var meta = new List<string>();
        if (!string.IsNullOrEmpty(_hit.Author))
            meta.Add("by " + _hit.Author);
        meta.Add(F("{0} 次下载", ResourcesPage.FormatCount(_hit.Downloads)));
        meta.Add(_client.Name);
        text.Children.Add(new TextBlock { Text = string.Join("  ·  ", meta), Classes = { "small", "dim" } });
        if (!string.IsNullOrWhiteSpace(_hit.Description))
            text.Children.Add(new TextBlock
            {
                Text = _hit.Description, Classes = { "muted" }, TextWrapping = TextWrapping.Wrap, MaxLines = 2,
                TextTrimming = TextTrimming.CharacterEllipsis, LineHeight = 21, Margin = new Thickness(0, 4, 0, 0),
            });

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        grid.Children.Add(iconBox);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if (!string.IsNullOrEmpty(_hit.Url))
        {
            var web = MakeButton(T("打开网页"), icon: "external", onClick: () => Platform.OpenUrl(_hit.Url));
            web.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(web, 2);
            grid.Children.Add(web);
        }
        return grid;
    }

    private Control Filters()
    {
        _games.Add(null);
        if (Main.Manifest != null)
            _games.AddRange(Main.Manifest.Items("versions").Where(v => v.Str("type") == "release").Select(v => v.Str("id")));
        if (_target.Game != null && !_games.Contains(_target.Game))
            _games.Insert(1, _target.Game);
        _gameBox.ItemsSource = _games.Select(g => g ?? T("全部版本")).ToList();
        _gameBox.SelectedIndex = Math.Max(0, _games.IndexOf(_target.Game));
        _gameBox.SelectionChanged += (_, _) => Reload();

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(new TextBlock { Text = "Minecraft", Classes = { "label" }, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(_gameBox);
        if (_target.Kind == "mod")
        {
            _loaders.Add(null);
            _loaders.AddRange(Mods.ModLoaders);
            _loaderBox.ItemsSource = _loaders.Select(l => l == null ? T("全部加载器") : LoaderName(l)).ToList();
            _loaderBox.SelectedIndex = Math.Max(0, _loaders.IndexOf(_target.Loader));
            _loaderBox.SelectionChanged += (_, _) => Reload();
            row.Children.Add(new TextBlock { Text = T("加载器"), Classes = { "label" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
            row.Children.Add(_loaderBox);
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 18, 0, 0) };
        grid.Children.Add(row);
        _summary.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(_summary, 1);
        grid.Children.Add(_summary);

        var panel = new StackPanel();
        panel.Children.Add(grid);
        _installedText.Foreground = Res("Accent");
        panel.Children.Add(_installedText);
        return panel;
    }

    private static string LoaderName(string loader) => Mc.LoaderNames.GetValueOrDefault(loader, loader);

    public override void OnOpened()
    {
        _ready = true;
        Reload();
        _ = RefreshInstalled();
    }

    public override void OnClosed() => _gen++;

    // ------------------------------------------------------------------ 版本列表

    private TextBlock Status(string text, bool error = false)
    {
        var block = new TextBlock
        {
            Text = text, Classes = { error ? "wrap" : "muted" }, TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 30),
        };
        if (error)
            block.Foreground = Res("Error");
        return block;
    }

    private async void Reload()
    {
        if (!_ready)
            return;
        var gen = ++_gen;
        _versions.Clear();
        _buttons.Clear();
        _next = null;
        _more = null;
        _shown = 0;
        _summary.Text = "";
        _list.Children.Clear();
        _list.Children.Add(Status(T("正在获取版本列表…")));
        _scroll.Offset = default;
        _query = (_games[Math.Max(0, _gameBox.SelectedIndex)], _target.Kind == "mod" ? _loaders[Math.Max(0, _loaderBox.SelectedIndex)] : null);
        var (game, loader) = _query;
        VersionPage page;
        try
        {
            page = await Task.Run(() => _client.VersionsAsync(_hit.Id, game, loader, _target.Kind));
        }
        catch (Exception e)
        {
            if (gen != _gen)
                return;
            _list.Children.Clear();
            _list.Children.Add(Status(F("获取版本列表失败：{0}", MainWindow.ErrorText(e)), true));
            var retry = MakeButton(T("重试"), icon: "refresh", onClick: Reload);
            retry.HorizontalAlignment = HorizontalAlignment.Center;
            _list.Children.Add(retry);
            return;
        }
        if (gen != _gen)
            return;
        _list.Children.Clear();
        Append(page);
    }

    private void Append(VersionPage page)
    {
        _versions.AddRange(page.Versions);
        _next = page.Next;
        if (_versions.Count == 0 && _next == null)
        {
            _list.Children.Add(Status(T("没有找到符合条件的版本，可以把筛选条件改成「全部」看看")));
            UpdateSummary();
            return;
        }
        RenderMore();
    }

    private void RenderMore()
    {
        if (_more != null)
            _list.Children.Remove(_more);
        _more = null;
        var end = Math.Min(_versions.Count, _shown + ShowStep);
        for (var i = _shown; i < end; i++)
            _list.Children.Add(Row(_versions[i]));
        _shown = end;
        if (_shown < _versions.Count || _next != null)
        {
            _more = new Button { Content = T("加载更多"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 4) };
            _more.Click += (_, _) =>
            {
                if (_shown < _versions.Count)
                    RenderMore();
                else
                    LoadNext();
            };
            _list.Children.Add(_more);
        }
        UpdateSummary();
    }

    private async void LoadNext()
    {
        var gen = _gen;
        var more = _more;
        more.IsEnabled = false;
        more.Content = T("加载中…");
        var (game, loader) = _query;
        var next = _next;
        VersionPage page;
        try
        {
            page = await Task.Run(() => _client.VersionsAsync(_hit.Id, game, loader, _target.Kind, next));
        }
        catch (Exception e)
        {
            if (gen != _gen)
                return;
            more.IsEnabled = true;
            more.Content = T("加载更多");
            Main.Toast(F("获取版本列表失败：{0}", MainWindow.ErrorText(e)), "error");
            return;
        }
        if (gen != _gen)
            return;
        Append(page);
    }

    private void UpdateSummary() =>
        _summary.Text = _next == null ? F("共 {0} 个版本", _versions.Count) : F("已加载 {0} 个版本", _versions.Count);

    private static string GameRange(List<string> games)
    {
        var sorted = games.Distinct().OrderBy(g => g, Comparer<string>.Create(Loaders.CompareVersions)).ToList();
        return sorted.Count <= 3 ? string.Join(", ", sorted) : $"{sorted[0]} – {sorted[^1]}";
    }

    private bool Compatible(ModFileVersion v)
    {
        if (_target.Game != null && v.GameVersions.Count > 0 && !v.GameVersions.Contains(_target.Game))
            return false;
        if (_target.Kind == "mod" && _target.Loader != null && v.Loaders.Count > 0)
        {
            var accepted = Mods.ModrinthLoaders(_target.Loader);
            if (!v.Loaders.Any(accepted.Contains))
                return false;
        }
        return true;
    }

    private string TargetText() =>
        _target.Kind == "mod"
            ? $"{LoaderName(_target.Loader)} · Minecraft {_target.Game}"
            : $"Minecraft {_target.Game}";

    private string Describe(ModFileVersion v)
    {
        var parts = new List<string>();
        if (v.GameVersions.Count > 0)
            parts.Add("Minecraft " + GameRange(v.GameVersions));
        if (_target.Kind == "mod" && v.Loaders.Count > 0)
            parts.Add(string.Join(" / ", v.Loaders.Select(LoaderName)));
        return string.Join("  ·  ", parts);
    }

    private Control Row(ModFileVersion v)
    {
        var (typeText, brushKey) = v.Type switch
        {
            "beta" => (T("测试版"), "Warn"),
            "alpha" => (T("早期测试版"), "Error"),
            _ => (T("正式版"), "Success"),
        };
        var brush = Res(brushKey);
        var badge = new Border
        {
            Classes = { "badge" }, MinWidth = 58, VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(brush is ISolidColorBrush solid ? solid.Color : Colors.Gray, 0.16),
            Child = new TextBlock { Text = typeText, Foreground = brush, HorizontalAlignment = HorizontalAlignment.Center },
        };

        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var name = new TextBlock { Text = v.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 420 };
        if (!string.IsNullOrEmpty(v.Filename))
            ToolTip.SetTip(name, v.Filename);
        nameRow.Children.Add(name);
        if (!Compatible(v))
            nameRow.Children.Add(new TextBlock { Text = T("不适用于当前版本"), Classes = { "small" }, Foreground = Res("Warn"), VerticalAlignment = VerticalAlignment.Center });

        var parts = new List<string>();
        var describe = Describe(v);
        if (describe.Length > 0)
            parts.Add(describe);
        if (v.Date > DateTime.MinValue)
            parts.Add(v.Date.ToString("yyyy-MM-dd"));
        if (v.Downloads > 0)
            parts.Add(F("{0} 次下载", ResourcesPage.FormatCount(v.Downloads)));
        var text = new StackPanel { Spacing = 2, Margin = new Thickness(14, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(nameRow);
        text.Children.Add(new TextBlock { Text = string.Join("  ·  ", parts), Classes = { "small", "dim" }, TextTrimming = TextTrimming.CharacterEllipsis });

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        grid.Children.Add(badge);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var panel = new StackPanel();
        panel.Children.Add(grid);
        if (!string.IsNullOrWhiteSpace(v.Changelog))
        {
            var changelog = new SelectableTextBlock
            {
                Text = v.Changelog.Trim(), TextWrapping = TextWrapping.Wrap, LineHeight = 21, FontSize = 12.5,
                Foreground = Res("Muted"), IsVisible = false, Margin = new Thickness(0, 10, 0, 0),
            };
            var toggle = new Button { Classes = { "link" }, Content = T("更新日志"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            toggle.Click += (_, _) => changelog.IsVisible = !changelog.IsVisible;
            Grid.SetColumn(toggle, 2);
            grid.Children.Add(toggle);
            panel.Children.Add(changelog);
        }

        var button = new Button { MinWidth = 96, VerticalAlignment = VerticalAlignment.Center };
        button.Click += (_, _) => Install(v);
        _buttons[v] = button;
        UpdateButton(v, button);
        Grid.SetColumn(button, 3);
        grid.Children.Add(button);
        return new Border { Classes = { "row" }, Padding = new Thickness(14, 10), Child = panel };
    }

    private void UpdateButton(ModFileVersion v, Button button)
    {
        button.Classes.Remove("primary");
        ToolTip.SetTip(button, null);
        if (v == _installing)
        {
            button.Content = new IconLabel { Text = T("安装中…") };
            button.IsEnabled = false;
        }
        else if (_installed.Any(i => i.VersionId == v.Id))
        {
            button.Content = new IconLabel { Icon = "check", Text = T("已安装") };
            button.IsEnabled = false;
        }
        else if (_installed.Count > 0)
        {
            button.Content = new IconLabel { Icon = "refresh", Text = T("替换") };
            ToolTip.SetTip(button, F("替换已安装的 {0}", string.Join("、", _installed.Select(i => i.File.Filename))));
            button.IsEnabled = _installing == null;
        }
        else
        {
            button.Classes.Add("primary");
            button.Content = new IconLabel { Icon = "download", Text = T("安装") };
            button.IsEnabled = _installing == null;
        }
    }

    private void UpdateButtons()
    {
        foreach (var (v, button) in _buttons)
            UpdateButton(v, button);
    }

    // ------------------------------------------------------------------ 安装

    private async Task RefreshInstalled()
    {
        var kind = _target.Kind;
        var dir = _target.Dir;
        List<(string, LocalFile)> installed;
        try
        {
            var files = await Task.Run(() => kind == "mod" ? Mods.ListLocalMods(dir) : Mods.ListLocalFiles(dir));
            installed = await Task.Run(() => _client.InstalledVersionsAsync(_hit.Id, files));
        }
        catch (Exception)
        {
            installed = [];
        }
        _installed = installed;
        _installedText.Text = F("已安装：{0}", string.Join("、", installed.Select(i => i.Item2.Filename)));
        _installedText.IsVisible = installed.Count > 0;
        UpdateButtons();
    }

    private async void Install(ModFileVersion v)
    {
        if (_installing != null)
            return;
        if (!Compatible(v)
            && !await Main.Confirm(T("版本可能不兼容"),
                                   F("{0} 标注适用于 {1}，当前是 {2}，装上后可能无法加载。仍要安装吗？", v.Name, Describe(v), TargetText()),
                                   T("仍要安装")))
            return;
        _installing = v;
        UpdateButtons();
        var replace = _installed.Select(i => i.File).ToList();
        List<string> files;
        try
        {
            files = await Task.Run(() => _client.InstallVersionAsync(v, _target.Game, _target.Loader, _target.Dir, _projects,
                                                                     replace, Main.Log, _target.Kind));
        }
        catch (Exception e)
        {
            _installing = null;
            UpdateButtons();
            await Main.Dialog(F("安装 {0} 失败", _hit.Title), MainWindow.ErrorText(e), "error");
            return;
        }
        _installing = null;
        var extra = files.Count - 1;
        Main.Toast((replace.Count > 0 ? F("已替换为 {0}", v.Name) : F("已安装 {0}", v.Name))
                   + (extra > 0 ? F("（含 {0} 个前置）", extra) : ""));
        _changed?.Invoke();
        await RefreshInstalled();
    }
}
