using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MaoX.Controls;
using MaoX.Core;
using MaoX;
using static MaoX.Core.I18n;

namespace MaoX.Pages;

public record LogLine(string Text, IBrush Brush);

public partial class LaunchPage : UserControl, IPage
{
    private const int MaxLogLines = 4000;

    private static IBrush ThemeBrush(string key) => (IBrush)Application.Current!.FindResource(key)!;

    private static readonly IBrush NormalBrush = ThemeBrush("LogText");
    private static readonly IBrush LauncherBrush = ThemeBrush("Accent");
    private static readonly IBrush SuccessBrush = ThemeBrush("Success");
    private static readonly IBrush ErrorBrush = ThemeBrush("Error");
    private static readonly IBrush WarnBrush = ThemeBrush("Warn");

    private readonly AvaloniaList<LogLine> _lines = [];
    private readonly ConcurrentQueue<LogLine> _pending = new();
    private bool _updatingVersions;

    // 其他 Mojang 游戏
    private string _game;
    private Dictionary<string, List<GameInstall>> _installs = [];
    private readonly Dictionary<string, Bitmap> _logos = [];
    private int _detectId;
    private bool _detected;
    private bool _otherLaunching;
    private DateTime _gamePillUntil;

    private static MainView Main => MainView.Current;

    public LaunchPage()
    {
        InitializeComponent();
        OpenDirButton.IsVisible = DashDirButton.IsVisible = !Platform.IsMobile;
        HiddenButton.Classes.Set("accent", Main.Cfg.ShowHiddenVersions);
        LogList.ItemsSource = _lines;
        new DispatcherTimer(TimeSpan.FromMilliseconds(80), DispatcherPriority.Background, (_, _) => FlushLog()).Start();

        _game = Main.Cfg.SelectedGame;
        Main.AccountChanged += UpdateAccount;
        Main.VersionsChanged += UpdateVersions;
        Main.SelectedVersionChanged += UpdateHero;
        Main.BusyChanged += _ => UpdateLaunchButton();
        UpdateAccount();
        RefreshGameDirs();
        ApplyLiveLog();
        SizeChanged += (_, _) => LayoutLaunch();
        LayoutLaunch();
        PageTabs.PreparePill(GameTabPill);
        GameTabs.LayoutUpdated += (_, _) =>
        {
            if (DateTime.UtcNow >= _gamePillUntil)
                SyncGameTabPill(false);
        };
        BuildGameTabs();
    }

    public void OnShow()
    {
        UpdateHero();
        DetectGames();
        ReloadDash();
    }

    // ------------------------------------------------------------------ 其他 Mojang 游戏

    private List<GameInstall> Installs(string id) => _installs.GetValueOrDefault(id) ?? [];

    /// <summary>当前选中的非 Java 游戏，选的是 Java 版时为 null。</summary>
    public MojangGame OtherGame => MojangGames.Find(_game);

    /// <summary>重新检测本机装了哪些游戏（切回启动页、点切换按钮时调用）。</summary>
    public async void DetectGames()
    {
        var id = ++_detectId;
        // 手机上查询其他应用要在主线程
        var result = Platform.IsMobile ? MojangGames.DetectAll() : await Task.Run(MojangGames.DetectAll);
        if (id != _detectId)
            return;
        _installs = result;
        _detected = true;
        BuildGameTabs();
        UpdateHero();
    }

    private void BuildGameTabs()
    {
        var games = new List<(string Id, string Name, string Icon)> { ("java", T("Java 版"), "java") };
        foreach (var game in MojangGames.All.Where(MojangGames.Supported))
        {
            // 电脑上 Steam 版只在装了的时候显示（macOS / Linux 没有这些游戏的原生版本）
            var listed = Platform.IsWindows || Platform.IsMobile;
            if (Installs(game.Id).Count > 0 || (listed && !game.HiddenUnlessInstalled))
                games.Add((game.Id, game.ShortName, game.Icon));
        }
        if (games.All(g => g.Id != _game))
        {
            // 检测完成前先保留上次选的游戏（比如预览版），检测完还没有才退回 Java 版
            if (_detected || MojangGames.Find(_game) is not { } kept || !MojangGames.Supported(kept))
                _game = "java";
            else
                games.Add((kept.Id, kept.ShortName, kept.Icon));
        }

        var ids = games.Select(g => g.Id).ToList();
        var current = GameTabs.Children.OfType<ToggleButton>().Select(b => (string)b.Tag).ToList();
        if (ids.Count > 0 && ids.SequenceEqual(current))
        {
            foreach (var existing in GameTabs.Children.OfType<ToggleButton>())
                existing.IsChecked = (string)existing.Tag == _game;
            GameTabsScroll.IsVisible = games.Count > 1;
            return;
        }

        GameTabs.Children.Clear();
        foreach (var (id, name, icon) in games)
        {
            var tab = new ToggleButton
            {
                Classes = { "chip", "tab" },
                Tag = id,
                IsChecked = id == _game,
                Content = new IconLabel { Icon = icon, Text = name, IconSize = 15, Spacing = 7 },
            };
            tab.Click += (_, _) => SelectGame(id);
            GameTabs.Children.Add(tab);
        }
        GameTabsScroll.IsVisible = games.Count > 1;
        SyncGameTabPill(false);
    }

    public void SelectGame(string id, bool remember = true)
    {
        var changed = id != _game;
        _game = id;
        foreach (var tab in GameTabs.Children.OfType<ToggleButton>())
            tab.IsChecked = (string)tab.Tag == id;
        if (changed)
        {
            _gamePillUntil = DateTime.UtcNow + PageTabs.Duration;
            SyncGameTabPill(true);
        }
        if (remember)
        {
            Main.Cfg.SelectedGame = id;
            Main.Cfg.Save();
        }
        UpdateHero();
        DetectGames();
    }

    private void SyncGameTabPill(bool animate)
    {
        var tab = GameTabs.Children.OfType<ToggleButton>().FirstOrDefault(b => (string)b.Tag == _game);
        PageTabs.MovePill(GameTabPill, tab, animate);
    }

    private void UpdateOtherHero(MojangGame game)
    {
        var installs = Installs(game.Id);
        HeroOverline.Text = installs.Count > 0 ? "READY  TO  PLAY" : "NOT  INSTALLED";
        HeroTitle.Text = game.Name;
        HeroMeta.Text = installs.Count > 0
            ? string.Join("  ·  ", installs.Select(i => i.ToString()))
            : Platform.IsMobile ? T("这台设备上还没有安装，点「获取游戏」前往应用商店")
            : T("这台电脑上还没有安装，点「获取游戏」前往商店");

        var previous = SourceBox.SelectedItem as GameInstall;
        SourceBox.ItemsSource = installs;
        SourceBox.SelectedItem = installs.FirstOrDefault(i => i.Game == previous?.Game && i.Source == previous?.Source)
                                 ?? installs.FirstOrDefault();
        SourcePanel.IsVisible = installs.Count > 1;
        DataButton.IsVisible = game.DataDir != null;

        var logo = installs.Select(i => i.IconPath).FirstOrDefault(p => p != null);
        GameLogo.Source = LoadLogo(logo);
        GameLogo.IsVisible = GameLogo.Source != null;
    }

    private Bitmap LoadLogo(string path)
    {
        if (path == null)
            return null;
        if (_logos.TryGetValue(path, out var cached))
            return cached;
        try
        {
            return _logos[path] = new Bitmap(path);
        }
        catch (Exception)
        {
            return _logos[path] = null;
        }
    }

    private void LaunchOther(MojangGame game)
    {
        var installs = Installs(game.Id);
        if (installs.Count == 0)
        {
            Platform.OpenUrl(game.GetUrl);
            return;
        }
        var install = SourceBox.SelectedItem as GameInstall ?? installs[0];
        try
        {
            MojangGames.Launch(install);
        }
        catch (Exception e)
        {
            Main.Log(F("启动 {0} 失败：{1}", game.Name, e.Message), "error");
            Main.Toast(F("启动 {0} 失败：{1}", game.Name, e.Message), "error");
            return;
        }
        Main.Log(F("已启动 {0}（{1}）", game.Name, install.ToString()), "success");
        Main.Toast(F("正在启动 {0}", game.Name));
        // 游戏由系统启动，启动器拿不到进程，按钮先禁用几秒防止连点开出多个
        _otherLaunching = true;
        UpdateLaunchButton();
        DispatcherTimer.RunOnce(() =>
        {
            _otherLaunching = false;
            UpdateLaunchButton();
        }, TimeSpan.FromSeconds(4));
        if (Main.Cfg.AfterLaunch != "keep" && TopLevel.GetTopLevel(this) is Window window)
            window.WindowState = WindowState.Minimized;
    }

    private void OnOpenGameData(object sender, RoutedEventArgs e)
    {
        if (OtherGame?.DataDir is { } dir)
            Platform.OpenPath(dir);
    }

    // ------------------------------------------------------------------ 日志

    public void ApplyLiveLog()
    {
        var on = Main.Cfg.LiveLog;
        LogCard.IsVisible = on;
        DashCard.IsVisible = !on;
        if (on)
            return;
        _lines.Clear();
        while (_pending.TryDequeue(out _))
        {
        }
        ReloadDash();
    }

    /// <summary>追加一行日志（任意线程）。kind 为 null 时按内容自动判断颜色。</summary>
    public void AppendLog(string text, string kind)
    {
        if (!Main.Cfg.LiveLog)
            return;
        if (kind == null)
        {
            var upper = text.ToUpperInvariant();
            if (upper.Contains("/ERROR]") || upper.Contains("/FATAL]") || upper.Contains("EXCEPTION"))
                kind = "error";
            else if (upper.Contains("/WARN]"))
                kind = "warn";
        }
        var brush = kind switch
        {
            "launcher" => LauncherBrush,
            "success" => SuccessBrush,
            "error" => ErrorBrush,
            "warn" => WarnBrush,
            _ => NormalBrush,
        };
        _pending.Enqueue(new LogLine(text, brush));
    }

    private void FlushLog()
    {
        if (_pending.IsEmpty)
            return;
        var batch = new List<LogLine>();
        while (_pending.TryDequeue(out var line))
            batch.Add(line);
        if (batch.Count > MaxLogLines)
            batch = batch[^MaxLogLines..];

        var scroll = LogList.FindDescendantOfType<ScrollViewer>();
        var atBottom = scroll == null || scroll.Offset.Y >= scroll.Extent.Height - scroll.Viewport.Height - 40;
        _lines.AddRange(batch);
        var extra = _lines.Count - MaxLogLines;
        if (extra > 0)
            _lines.RemoveRange(0, extra);
        if (atBottom)
            Dispatcher.UIThread.Post(() => LogList.ScrollIntoView(_lines.Count - 1), DispatcherPriority.Background);
    }

    private void OnClearLog(object sender, RoutedEventArgs e) => _lines.Clear();

    private async void OnCopyLog(object sender, RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null)
            return;
        await clipboard.SetTextAsync(string.Join(Environment.NewLine, _lines.Select(l => l.Text)));
        Main.Toast(T("日志已复制到剪贴板"));
    }

    private void OnOpenGameDir(object sender, RoutedEventArgs e) => Main.OpenGameDir();

    // ------------------------------------------------------------------ 账号与版本

    private void UpdateAccount()
    {
        var account = Main.CurrentAccount;
        HeroAccountName.Text = account?.Name ?? T("未设置");
        HeroAccountType.Text = account != null ? Accounts.TypeNames.GetValueOrDefault(account.Type, "") : "";
        HeroAvatar.NameText = account?.Name;
        HeroAvatar.Skin = Main.SkinFor(account);
    }

    private void OnAccountClick(object sender, RoutedEventArgs e) => Main.ManageAccounts();

    private void UpdateVersions()
    {
        _updatingVersions = true;
        VersionBox.ItemsSource = Main.InstalledList.ToList();
        VersionBox.SelectedItem = Main.SelectedVersion;
        _updatingVersions = false;
        UpdateHero();
    }

    private bool _updatingDirs;

    public void RefreshGameDirs()
    {
        var dirs = Main.Cfg.AllGameDirs();
        GameDirPanel.IsVisible = dirs.Count > 1;
        if (dirs.Count <= 1)
        {
            LayoutHero();
            return;
        }
        var labels = dirs.Select(d =>
        {
            var name = string.IsNullOrWhiteSpace(d.Name) ? Path.GetFileName(d.Path) : d.Name;
            return dirs.Count(x => x.Name == d.Name) > 1 ? name + "  ·  " + Path.GetFileName(d.Path) : name;
        }).ToList();
        var current = Path.GetFullPath(Main.Cfg.MinecraftDir);
        var index = dirs.FindIndex(d => string.Equals(Path.GetFullPath(d.Path), current, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            index = 0;
        _updatingDirs = true;
        var currentItems = (GameDirBox.ItemsSource as IEnumerable<string>)?.ToList();
        if (currentItems == null || !currentItems.SequenceEqual(labels))
            GameDirBox.ItemsSource = labels;
        if (GameDirBox.SelectedIndex != index)
            GameDirBox.SelectedIndex = index;
        _updatingDirs = false;
        LayoutHero();
        ToolTip.SetTip(GameDirBox, dirs[index].Path);
    }

    private void OnGameDirChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingDirs)
            return;
        var dirs = Main.Cfg.AllGameDirs();
        if (GameDirBox.SelectedIndex is >= 0 and var i && i < dirs.Count)
            Main.SetMinecraftDir(dirs[i].Path);
    }

    private void OnVersionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingVersions && VersionBox.SelectedItem is string version)
            Main.SelectVersion(version);
    }

    public void RefreshHero() => UpdateHero();

    private void UpdateHero()
    {
        var version = Main.SelectedVersion;
        if (!Equals(VersionBox.SelectedItem, version))
        {
            _updatingVersions = true;
            VersionBox.SelectedItem = version;
            _updatingVersions = false;
        }

        var other = OtherGame;
        AccountPanel.IsVisible = VersionPanel.IsVisible = other == null;
        UpdateLaunchButton();
        if (other != null)
        {
            UpdateOtherHero(other);
            return;
        }
        HeroOverline.Text = "READY  TO  PLAY";
        SourcePanel.IsVisible = DataButton.IsVisible = GameLogo.IsVisible = false;
        if (string.IsNullOrEmpty(version))
        {
            HeroTitle.Text = T("还没有游戏版本");
            HeroMeta.Text = T("前往「下载」安装一个 Minecraft 版本");
            return;
        }
        var meta = Main.Cfg.VersionMetas.GetValueOrDefault(version);
        HeroTitle.Text = (meta?.Star == true ? "★ " : "") + version;
        try
        {
            var gl = new GameLauncher(Main.Cfg);
            var played = PlayTime.Describe(gl, version);
            var note = string.IsNullOrWhiteSpace(meta?.Note) ? "" : "  ·  " + meta.Note;
            HeroMeta.Text = gl.DescribeVersion(version) + (played == "" ? "" : "  ·  " + played) + note;
        }
        catch (Exception)
        {
            HeroMeta.Text = string.IsNullOrWhiteSpace(meta?.Note) ? "Minecraft" : meta.Note;
        }
        if (!string.IsNullOrWhiteSpace(meta?.Icon) && File.Exists(AppPaths.Rebase(meta.Icon)))
        {
            try
            {
                GameLogo.Source = new Bitmap(AppPaths.Rebase(meta.Icon));
                GameLogo.IsVisible = true;
            }
            catch (Exception)
            {
                GameLogo.IsVisible = false;
            }
        }
    }

    private void OnVersionSettings(object sender, RoutedEventArgs e) => Main.ManageVersion();

    private void OnToggleHidden(object sender, RoutedEventArgs e)
    {
        Main.Cfg.ShowHiddenVersions = !Main.Cfg.ShowHiddenVersions;
        HiddenButton.Classes.Set("accent", Main.Cfg.ShowHiddenVersions);
        Main.RefreshInstalled(Main.SelectedVersion);
    }

    private static McSiteFeed.Snapshot _feed;
    private int _infoId;

    public void ReloadDash() => LoadDashInfo();

    /// <summary>窄屏才上下叠 Wiki 卡片；手机横屏宽度够，左右并排，新闻才不会被顶出屏幕。</summary>
    private bool Narrow => Bounds.Width > 0 && Bounds.Width < 520;

    private bool CompactUi => Platform.IsMobile || Main.Classes.Contains("compact") || Narrow;

    private double ArtHeight => CompactUi ? 88 : 132;

    private void LayoutLaunch()
    {
        LayoutHero();
        LayoutDash();
        ApplyArtHeights();
        Main.RefreshPageScroll();
    }

    /// <summary>启动卡和动态都按内容高度排，整页一起滚动，下面的新闻才能滑出来。</summary>
    private void LayoutHero()
    {
        LaunchRoot.RowDefinitions = new RowDefinitions("Auto,Auto");
        DashBody.RowDefinitions = new RowDefinitions("Auto,Auto");
        if (Narrow)
        {
            var rows = GameDirPanel.IsVisible ? "Auto,Auto,Auto,Auto" : "Auto,Auto,Auto";
            HeroControls.ColumnDefinitions = new ColumnDefinitions("*");
            HeroControls.RowDefinitions = new RowDefinitions(rows);
            PlaceHero(AccountPanel, 0, 0, default);
            var row = 1;
            if (GameDirPanel.IsVisible)
                PlaceHero(GameDirPanel, 0, row++, new Thickness(0, 8, 0, 0));
            PlaceHero(VersionHost, 0, row++, new Thickness(0, 8, 0, 0));
            PlaceHero(LaunchPanel, 0, row, new Thickness(0, 8, 0, 0));
            AccountPicker.ClearValue(WidthProperty);
            AccountPicker.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            LaunchButton.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            LaunchButton.MinWidth = 0;
            return;
        }
        HeroControls.ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto");
        HeroControls.RowDefinitions = new RowDefinitions("Auto");
        PlaceHero(AccountPanel, 0, 0, default);
        PlaceHero(GameDirPanel, 1, 0, new Thickness(14, 0, 0, 0));
        PlaceHero(VersionHost, 2, 0, new Thickness(14, 0, 0, 0));
        PlaceHero(LaunchPanel, 3, 0, new Thickness(14, 0, 0, 0));
        AccountPicker.ClearValue(WidthProperty);
        AccountPicker.ClearValue(HorizontalAlignmentProperty);
        LaunchButton.ClearValue(HorizontalAlignmentProperty);
        LaunchButton.ClearValue(MinWidthProperty);
    }

    private static void PlaceHero(Control control, int column, int row, Thickness margin)
    {
        Grid.SetColumn(control, column);
        Grid.SetRow(control, row);
        control.Margin = margin;
    }

    private void LayoutDash()
    {
        var stack = Narrow;
        DashColumns.ColumnDefinitions = stack ? new ColumnDefinitions("*") : new ColumnDefinitions("*,*");
        DashColumns.RowDefinitions = stack ? new RowDefinitions("Auto,Auto") : new RowDefinitions("Auto");
        Grid.SetColumn(UpcomingPanel, stack ? 0 : 1);
        Grid.SetRow(UpcomingPanel, stack ? 1 : 0);
        UpcomingPanel.Margin = stack ? new Thickness(0, 12, 0, 0) : new Thickness(14, 0, 0, 0);
    }

    private void ApplyArtHeights()
    {
        var h = ArtHeight;
        foreach (var list in new[] { LatestList, UpcomingList, NewsList })
        {
            foreach (var img in list.GetVisualDescendants().OfType<Image>())
                img.Height = h;
            foreach (var border in list.GetVisualDescendants().OfType<Border>().Where(b => b.Child is Image))
                border.Height = h;
        }
    }

    private async void LoadDashInfo()
    {
        var id = ++_infoId;
        NewsTitle.Text = T("最新新闻");
        if (_feed != null)
            await ApplyDash(_feed, id);
        else
        {
            _ = FillDrop(LatestList, new McSiteFeed.Item(T("正在获取…"), "", ""));
            _ = FillDrop(UpcomingList, new McSiteFeed.Item(T("正在获取…"), "", ""));
            _ = FillNewsItem(new McSiteFeed.Item(T("正在获取…"), "", ""));
        }
        try
        {
            _feed = await McSiteFeed.FetchAsync();
            if (id != _infoId)
                return;
            await ApplyDash(_feed, id);
        }
        catch (Exception e)
        {
            if (id != _infoId)
                return;
            _ = FillDrop(LatestList, null);
            _ = FillDrop(UpcomingList, null);
            await FillNewsItem(new McSiteFeed.Item(F("动态加载失败：{0}", e.Message), "", ""));
        }
    }

    private async Task ApplyDash(McSiteFeed.Snapshot feed, int id)
    {
        await Task.WhenAll(FillDrop(LatestList, feed.Latest), FillDrop(UpcomingList, feed.Upcoming));
        var source = (Main.Cfg.Homepage ?? "").Trim();
        if (source.Length > 0 && !source.Equals("preset:news", StringComparison.OrdinalIgnoreCase))
        {
            NewsTitle.Text = T("主页");
            try
            {
                var cards = await Homepage.LoadAsync(source);
                if (id != _infoId)
                    return;
                var card = cards.FirstOrDefault();
                await FillNewsItem(card == null ? null : new McSiteFeed.Item(card.Title, card.Text, card.Url));
            }
            catch (Exception e)
            {
                if (id != _infoId)
                    return;
                await FillNewsItem(new McSiteFeed.Item(F("主页加载失败：{0}", e.Message), "", ""));
            }
        }
        else
        {
            NewsTitle.Text = T("最新新闻");
            await FillNewsItem(feed.News);
        }
    }

    private async Task FillDrop(StackPanel list, McSiteFeed.Item item)
    {
        list.Children.Clear();
        if (item == null)
        {
            list.Children.Add(new TextBlock { Text = T("暂时没有内容"), Classes = { "muted" } });
            return;
        }
        var picture = new Image { Stretch = Stretch.UniformToFill, Height = ArtHeight };
        var art = new Border
        {
            Height = ArtHeight, ClipToBounds = true, CornerRadius = new CornerRadius(11, 11, 0, 0), Child = picture,
        };
        var title = new TextBlock
        {
            Text = item.Title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12, 10, 12, 12),
        };
        var card = new Border
        {
            Classes = { "row" }, Padding = new Thickness(0), ClipToBounds = true,
            Child = new StackPanel { Children = { art, title } },
        };
        if (!string.IsNullOrWhiteSpace(item.Url))
        {
            card.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
            var url = item.Url;
            card.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(card).Properties.IsLeftButtonPressed)
                    Platform.OpenUrl(url);
            };
        }
        list.Children.Add(card);
        var image = ThemeManager.IsLight ? item.ImageLight ?? item.Image : item.Image ?? item.ImageLight;
        if (!string.IsNullOrWhiteSpace(image))
            await LoadArt(picture, image);
    }

    private async Task FillNewsItem(McSiteFeed.Item item)
    {
        NewsList.Children.Clear();
        if (item == null)
        {
            NewsList.Children.Add(new TextBlock { Text = T("暂时没有内容"), Classes = { "muted" } });
            return;
        }
        var picture = new Image { Stretch = Stretch.UniformToFill, Height = ArtHeight };
        var art = new Border
        {
            Height = ArtHeight, ClipToBounds = true, CornerRadius = new CornerRadius(11, 11, 0, 0), Child = picture,
            IsVisible = false,
        };
        var box = new StackPanel { Spacing = 4 };
        box.Children.Add(new TextBlock
        {
            Text = item.Title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12, 10, 12, item.Text.Length > 0 ? 0 : 12),
        });
        if (item.Text.Length > 0)
            box.Children.Add(new TextBlock
            {
                Text = item.Text, Classes = { "small", "muted", "wrap" }, Margin = new Thickness(12, 0, 12, 12),
            });
        var card = new Border
        {
            Classes = { "row" }, Padding = new Thickness(0), ClipToBounds = true,
            Child = new StackPanel { Children = { art, box } },
        };
        if (!string.IsNullOrWhiteSpace(item.Url))
        {
            card.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
            var url = item.Url;
            card.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(card).Properties.IsLeftButtonPressed)
                    Platform.OpenUrl(url);
            };
        }
        NewsList.Children.Add(card);
        var image = ThemeManager.IsLight ? item.ImageLight ?? item.Image : item.Image ?? item.ImageLight;
        if (!string.IsNullOrWhiteSpace(image))
        {
            art.IsVisible = true;
            await LoadArt(picture, image);
        }
    }

    private static readonly Dictionary<string, Bitmap> _art = new(StringComparer.OrdinalIgnoreCase);

    private static async Task LoadArt(Image target, string url)
    {
        try
        {
            if (!_art.TryGetValue(url, out var bitmap))
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var data = await Http.Client.GetByteArrayAsync(url, cts.Token);
                if (data is not { Length: > 0 })
                    return;
                using var stream = new MemoryStream(data);
                bitmap = new Bitmap(stream);
                _art[url] = bitmap;
            }
            target.Source = bitmap;
        }
        catch (Exception)
        {
        }
    }

    private void OnDashDownload(object sender, RoutedEventArgs e) => Main.ShowPage("download");
    private void OnDashResources(object sender, RoutedEventArgs e) => Main.ShowPage("resources");
    private void OnDashMultiplayer(object sender, RoutedEventArgs e) => Main.ShowPage("multiplayer");
    private void OnDashTools(object sender, RoutedEventArgs e) => Main.ShowPage("tools");

    // ------------------------------------------------------------------ 启动

    private void OnLaunchClick(object sender, RoutedEventArgs e)
    {
        if (OtherGame is { } game)
            LaunchOther(game);
        else
            Main.Launch();
    }

    private async void OnStopClick(object sender, RoutedEventArgs e)
    {
        if (await Main.Confirm(T("结束游戏"), T("确定要强制结束正在运行的游戏吗？未保存的进度可能会丢失。"), T("结束游戏"), "warn", "danger"))
            Main.KillGame();
    }

    public void OnGameStateChanged() => UpdateLaunchButton();

    private void UpdateLaunchButton()
    {
        if (OtherGame is { } game)
        {
            var installed = Installs(game.Id).Count > 0;
            LaunchButton.IsEnabled = !_otherLaunching;
            LaunchLabel.Icon = installed ? "play" : "external";
            LaunchLabel.Text = !installed ? T("获取游戏") : _otherLaunching ? T("正在启动…") : T("启动游戏");
            StopButton.IsVisible = false;
            return;
        }
        LaunchLabel.Icon = "play";
        LaunchButton.IsEnabled = !Main.Launching;
        LaunchLabel.Text = Main.Launching ? T("正在启动…") : T("启动游戏");
        StopButton.IsVisible = Main.GameRunning;
    }

    // ------------------------------------------------------------------ 背景图

    public void SetHasBackground(bool on) => Watermark.IsVisible = !on;
}
