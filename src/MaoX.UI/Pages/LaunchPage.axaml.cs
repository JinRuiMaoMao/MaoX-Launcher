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

    private static MainView Main => MainView.Current;

    public LaunchPage()
    {
        InitializeComponent();
        OpenDirButton.IsVisible = !Platform.IsMobile;
        LogList.ItemsSource = _lines;
        new DispatcherTimer(TimeSpan.FromMilliseconds(80), DispatcherPriority.Background, (_, _) => FlushLog()).Start();

        _game = Main.Cfg.SelectedGame;
        Main.AccountChanged += UpdateAccount;
        Main.VersionsChanged += UpdateVersions;
        Main.SelectedVersionChanged += UpdateHero;
        Main.BusyChanged += _ => UpdateLaunchButton();
        UpdateAccount();
        BuildGameTabs();
    }

    public void OnShow()
    {
        UpdateHero();
        DetectGames();
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

        GameTabs.Children.Clear();
        foreach (var (id, name, icon) in games)
        {
            var tab = new ToggleButton
            {
                Classes = { "chip" },
                Tag = id,
                IsChecked = id == _game,
                Content = new IconLabel { Icon = icon, Text = name, IconSize = 15, Spacing = 7 },
            };
            tab.Click += (_, _) => SelectGame(id);
            GameTabs.Children.Add(tab);
        }
        GameTabsScroll.IsVisible = games.Count > 1;
    }

    public void SelectGame(string id, bool remember = true)
    {
        _game = id;
        foreach (var tab in GameTabs.Children.OfType<ToggleButton>())
            tab.IsChecked = (string)tab.Tag == id;
        if (remember)
        {
            Main.Cfg.SelectedGame = id;
            Main.Cfg.Save();
        }
        UpdateHero();
        DetectGames();
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

    /// <summary>追加一行日志（任意线程）。kind 为 null 时按内容自动判断颜色。</summary>
    public void AppendLog(string text, string kind)
    {
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
        HeroTitle.Text = version;
        try
        {
            var gl = new GameLauncher(Main.Cfg);
            var played = PlayTime.Describe(gl, version);
            HeroMeta.Text = gl.DescribeVersion(version) + (played == "" ? "" : "  ·  " + played);
        }
        catch (Exception)
        {
            HeroMeta.Text = "Minecraft";
        }
    }

    private void OnVersionSettings(object sender, RoutedEventArgs e) => Main.ManageVersion();

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
