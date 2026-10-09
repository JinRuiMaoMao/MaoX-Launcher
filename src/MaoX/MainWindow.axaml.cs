using System.Diagnostics;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MaoX.Controls;
using MaoX.Core;
using MaoX.Dialogs;
using MaoX.Pages;

namespace MaoX;

public interface IPage
{
    void OnShow();
}

public partial class MainWindow : Window
{
    public static MainWindow Current { get; private set; }

    public LauncherConfig Cfg { get; }
    public JsonNode Manifest { get; set; }
    public bool Busy { get; private set; }
    public HashSet<string> Installed { get; private set; } = [];
    public List<string> InstalledList { get; private set; } = [];

    /// <summary>当前选中的游戏版本（启动页的版本下拉框）。</summary>
    public string SelectedVersion { get; private set; }

    public event Action<bool> BusyChanged;
    public event Action AccountChanged;
    public event Action VersionsChanged;
    public event Action SelectedVersionChanged;

    public LaunchPage LaunchPage { get; }
    public DownloadPage DownloadPage { get; }
    public ResourcesPage ResourcesPage { get; }
    public MultiplayerPage MultiplayerPage { get; }
    public SettingsPage SettingsPage { get; }

    private readonly Dictionary<string, Control> _pages;
    private string _currentPage;
    private readonly Dictionary<string, Bitmap> _skins = [];
    private readonly HashSet<string> _skinLoading = [];
    private readonly List<(DialogView View, Panel Layer)> _dialogs = [];

    private Process _gameProcess;
    private string _runningVersion;
    private bool _hiddenForGame;
    private bool _forceClose;

    // 后台线程报告的进度先存下来，由定时器统一刷新界面，避免下载时刷屏
    private readonly object _progressLock = new();
    private (int Done, int Total, string Text)? _pendingProgress;

    public MainWindow()
    {
        Current = this;
        InitializeComponent();
        Cfg = LauncherConfig.Load();
        InitAccounts();
        VersionText.Text = "v" + Mc.LauncherVersion + "  ·  " + PlatformLabel();
        Brand.Margin = Platform.IsMac ? new Thickness(12, 58, 0, 34) : new Thickness(12, 52, 0, 34);
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);

        LaunchPage = new LaunchPage();
        DownloadPage = new DownloadPage();
        ResourcesPage = new ResourcesPage();
        MultiplayerPage = new MultiplayerPage();
        SettingsPage = new SettingsPage();
        _pages = new Dictionary<string, Control>
        {
            ["launch"] = LaunchPage,
            ["download"] = DownloadPage,
            ["resources"] = ResourcesPage,
            ["multiplayer"] = MultiplayerPage,
            ["settings"] = SettingsPage,
        };

        new DispatcherTimer(TimeSpan.FromMilliseconds(60), DispatcherPriority.Background, (_, _) => FlushProgress())
            .Start();

        UpdateAccountCard();
        RefreshInstalled();
        ApplyBackground();
        ShowPage("launch");
        Opened += (_, _) =>
        {
            SettingsPage.DetectJavaInBackground();
            Updater.CleanupOldVersion();
            if (Cfg.AutoCheckUpdate && Updater.CanSelfUpdate && !SmokeTest.Active)
                DispatcherTimer.RunOnce(() => _ = CheckForUpdate(true), TimeSpan.FromSeconds(3));
        };
    }

    // ------------------------------------------------------------------ 启动器更新

    private bool _checkingUpdate;

    /// <summary>检查新版本。silent 为 true 时（启动时自动检查）失败或没有更新都不打扰用户。</summary>
    public async Task CheckForUpdate(bool silent)
    {
        if (_checkingUpdate)
            return;
        _checkingUpdate = true;
        try
        {
            UpdateInfo info;
            try
            {
                info = await Updater.CheckAsync();
            }
            catch (Exception e)
            {
                if (!silent)
                    await Dialog("检查更新失败", ErrorText(e), "error");
                return;
            }
            if (info == null)
            {
                if (!silent)
                    Toast($"已经是最新版本 v{Mc.LauncherVersion}");
                return;
            }
            if (silent && info.Version == Cfg.SkippedUpdate)
                return;
            await OfferUpdate(info);
        }
        finally
        {
            _checkingUpdate = false;
        }
    }

    private async Task OfferUpdate(UpdateInfo info)
    {
        var canApply = Updater.CanSelfUpdate && info.Url != null;
        var message = $"当前版本 v{Mc.LauncherVersion}。";
        if (!Updater.CanSelfUpdate)
            message += "当前运行的不是发布版，请到发布页手动下载。";
        else if (info.Url == null)
            message += "这个版本没有提供当前系统的安装包，请到发布页查看。";
        if (!string.IsNullOrWhiteSpace(info.Notes))
            message += "\n\n" + info.Notes;
        var buttons = new List<(string, object, string)>();
        if (canApply)
            buttons.Add(("立即更新", "update", "primary"));
        buttons.Add(("打开发布页", "page", canApply ? "" : "primary"));
        buttons.Add(("跳过此版本", "skip", ""));
        buttons.Add(("以后再说", "later", ""));
        switch (await Dialog($"发现新版本 v{info.Version}", message, "info", buttons.ToArray()))
        {
            case "update":
                await InstallUpdate(info);
                break;
            case "page":
                Platform.OpenUrl(info.PageUrl);
                break;
            case "skip":
                Cfg.SkippedUpdate = info.Version;
                Cfg.Save();
                break;
        }
    }

    private async Task InstallUpdate(UpdateInfo info)
    {
        string file = null;
        await RunTask("下载更新", () => Updater.DownloadAsync(info, (done, total) =>
            Progress((int)(done / 1024), (int)Math.Max(1, total / 1024), $"下载新版本 v{info.Version}")), path => file = path);
        if (file == null)
            return;
        if (MultiplayerPage.InRoom
            && !await Confirm("更新启动器", "更新需要重启启动器，当前的联机房间会断开。", "继续更新"))
            return;
        try
        {
            SaveSettings();
            Cfg.Save();
            Updater.Apply(file);
        }
        catch (Exception e)
        {
            await Dialog("更新失败", ErrorText(e) + "\n\n可以到发布页手动下载新版本。", "error");
            return;
        }
        _forceClose = true;
        Close();
    }

    private static string PlatformLabel() =>
        (Platform.IsWindows ? "Windows" : Platform.IsMac ? "macOS" : "Linux") + " " + Platform.Arch;

    // ------------------------------------------------------------------ 背景图

    private string _backgroundPath;
    private Bitmap _background;

    /// <summary>按配置铺满整个窗口的背景图；有背景图时侧边栏和卡片变为半透明。</summary>
    public void ApplyBackground()
    {
        var path = Cfg.Background ?? "";
        if (path != _backgroundPath)
        {
            _backgroundPath = path;
            var old = _background;
            Bitmap bitmap = null;
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                try
                {
                    using var stream = File.OpenRead(path);
                    bitmap = Bitmap.DecodeToWidth(stream, 2560, BitmapInterpolationMode.HighQuality);
                }
                catch (Exception)
                {
                    Toast("无法读取背景图片", "error");
                }
            }
            _background = bitmap;
            BackdropImage.Background = bitmap == null
                ? null
                : new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill };
            old?.Dispose();
        }

        var on = _background != null;
        BackdropImage.IsVisible = BackdropMask.IsVisible = on;
        BackdropMask.Opacity = Math.Clamp(Cfg.BackgroundMask, 0, 90) / 100.0;
        Classes.Set("hasbg", on);
        SidebarPanel.Background = on
            ? new SolidColorBrush(Color.Parse("#9910131A"))
            : (IBrush)this.FindResource("Sidebar");
        LaunchPage.SetHasBackground(on);
    }

    // ------------------------------------------------------------------ 页面

    public void ShowPage(string key)
    {
        if (_currentPage == key)
            return;
        if (_currentPage == "settings")
            SaveSettings();
        _currentPage = key;
        foreach (var child in Nav.Children.OfType<RadioButton>())
            child.IsChecked = (string)child.Tag == key;
        var page = _pages[key];
        PageHost.Content = page;
        (page as IPage)?.OnShow();
    }

    private void OnNavClick(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string key })
            ShowPage(key);
    }

    private void OnTitleBarPressed(object sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount == 1)
            BeginMoveDrag(e);
    }

    private void OnTitleBarDoubleTapped(object sender, TappedEventArgs e)
    {
        if (CanResize)
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    // ------------------------------------------------------------------ 日志与进度（可在任意线程调用）

    public void Log(string text) => LaunchPage.AppendLog(text, "launcher");

    public void Log(string text, string kind) => LaunchPage.AppendLog(text, kind);

    public void Progress(int done, int total, string text = "")
    {
        if (total <= 0)
            return;
        lock (_progressLock)
            _pendingProgress = (done, total, text);
    }

    private void FlushProgress()
    {
        (int Done, int Total, string Text)? p;
        lock (_progressLock)
        {
            p = _pendingProgress;
            _pendingProgress = null;
        }
        if (p is not { } v || !Busy)
            return;
        ProgressBar.Set((double)v.Done / v.Total);
        if (!string.IsNullOrEmpty(v.Text))
            StatusText.Text = v.Text;
        ProgressText.Text = $"{v.Done} / {v.Total}   {v.Done * 100.0 / v.Total:0}%";
    }

    public void SetStatus(string text) => StatusText.Text = text;

    public GameLauncher MakeLauncher()
    {
        return new GameLauncher(Cfg.Clone(), Log, Progress) { Manifest = Manifest };
    }

    // ------------------------------------------------------------------ 任务

    /// <summary>在后台执行一个独占任务（同一时间只允许一个），失败时弹出错误对话框。</summary>
    public async Task RunTask<T>(string name, Func<Task<T>> job, Action<T> onDone = null)
    {
        if (Busy)
        {
            Toast("当前有任务正在进行，请稍候", "warn");
            return;
        }
        if (!SaveSettings())
            return;
        SetBusy(true, name);
        T result;
        try
        {
            result = await Task.Run(job);
        }
        catch (Exception e)
        {
            SetBusy(false);
            var message = ErrorText(e);
            Log($"[错误] {name}失败：{message}", "error");
            Trace.WriteLine(e);
            await Dialog(name + "失败", message, "error");
            return;
        }
        SetBusy(false);
        onDone?.Invoke(result);
    }

    public Task RunTask(string name, Func<Task> job, Action onDone = null) =>
        RunTask<bool>(name, async () =>
        {
            await job();
            return true;
        }, onDone == null ? null : _ => onDone());

    public static string ErrorText(Exception e)
    {
        while (e is AggregateException { InnerExceptions.Count: 1 } agg)
            e = agg.InnerException!;
        return e switch
        {
            TaskCanceledException => "操作超时，请检查网络后重试",
            HttpRequestException http => "网络错误：" + http.Message,
            _ => e.Message,
        };
    }

    private void SetBusy(bool busy, string text = "")
    {
        Busy = busy;
        if (busy)
        {
            StatusText.Text = text + "…";
            ProgressText.Text = "";
            ProgressBar.Start();
        }
        else
        {
            StatusText.Text = GameRunning ? "游戏运行中" : "就绪";
            ProgressText.Text = "";
            ProgressBar.Set(0);
        }
        BusyChanged?.Invoke(busy);
    }

    // ------------------------------------------------------------------ 对话框与提示

    public async Task<object> ShowDialogAsync(DialogView view)
    {
        var card = new Border
        {
            Classes = { "dialog" },
            Width = view.DialogWidth,
            Child = view,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0,
            RenderTransform = TransformOperations.Parse("scale(0.96)"),
            Transitions =
            [
                new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(160) },
                new TransformOperationsTransition
                {
                    Property = RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(200),
                    Easing = new Avalonia.Animation.Easings.CubicEaseOut(),
                },
            ],
        };
        card[!MaxHeightProperty] = new Avalonia.Data.Binding("Bounds.Height")
        {
            Source = this,
            Converter = new Avalonia.Data.Converters.FuncValueConverter<double, double>(h => Math.Max(200, h - 80)),
        };
        var backdrop = new Border
        {
            Background = (IBrush)this.FindResource("Overlay"),
            Opacity = 0,
            Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(160) }],
        };
        var layer = new Panel { Children = { backdrop, card } };
        KeyboardNavigation.SetTabNavigation(layer, KeyboardNavigationMode.Cycle);
        DialogStack.Children.Add(layer);
        DialogLayer.IsVisible = true;
        _dialogs.Add((view, layer));
        Dispatcher.UIThread.Post(() =>
        {
            backdrop.Opacity = 1;
            card.Opacity = 1;
            card.RenderTransform = TransformOperations.Parse("scale(1)");
            var inputs = view.GetVisualDescendants().OfType<InputElement>()
                             .Where(c => c.Focusable && c.IsEffectivelyEnabled && c.IsEffectivelyVisible).ToList();
            var focus = inputs.FirstOrDefault(c => c is TextBox) ?? inputs.FirstOrDefault();
            focus?.Focus();
            view.OnOpened();
        }, DispatcherPriority.Background);
        return await view.Completion.Task;
    }

    internal void CloseDialog(DialogView view, object result)
    {
        var index = _dialogs.FindIndex(d => d.View == view);
        if (index < 0)
            return;
        var layer = _dialogs[index].Layer;
        _dialogs.RemoveAt(index);
        layer.IsHitTestVisible = false;
        foreach (var child in layer.Children)
            child.Opacity = 0;
        DispatcherTimer.RunOnce(() =>
        {
            DialogStack.Children.Remove(layer);
            if (_dialogs.Count == 0)
                DialogLayer.IsVisible = false;
        }, TimeSpan.FromMilliseconds(170));
        view.OnClosed();
        view.Completion.TrySetResult(result);
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _dialogs.Count > 0)
        {
            var top = _dialogs[^1].View;
            if (top.Dismissible)
                top.Close();
            e.Handled = true;
        }
    }

    public Task<object> Dialog(string title, string message, string kind = "info",
                               params (string Text, object Value, string Style)[] buttons)
    {
        if (buttons.Length == 0)
            buttons = [("确定", null, "primary")];
        return ShowDialogAsync(new MessageDialog(title, message, kind, buttons));
    }

    public async Task<bool> Confirm(string title, string message, string ok = "确定", string kind = "warn",
                                    string okStyle = "primary")
    {
        var result = await Dialog(title, message, kind, (ok, true, okStyle), ("取消", false, ""));
        return result is true;
    }

    public void Toast(string text, string kind = "success")
    {
        var (icon, brushKey) = kind switch
        {
            "error" => ("error", "Error"),
            "warn" => ("warn", "Warn"),
            "info" => ("info", "Accent"),
            _ => ("success", "Success"),
        };
        var iconControl = new Icon { Kind = icon, Size = 18, Foreground = (IBrush)this.FindResource(brushKey) };
        var toast = new Border
        {
            Classes = { "toast" },
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Children = { iconControl, new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } },
            },
            Opacity = 0,
            RenderTransform = TransformOperations.Parse("translateY(10px)"),
            Transitions =
            [
                new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(180) },
                new TransformOperationsTransition { Property = RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(220) },
            ],
        };
        ToastLayer.Children.Add(toast);
        while (ToastLayer.Children.Count > 4)
            ToastLayer.Children.RemoveAt(0);
        Dispatcher.UIThread.Post(() =>
        {
            toast.Opacity = 1;
            toast.RenderTransform = TransformOperations.Parse("translateY(0px)");
        }, DispatcherPriority.Background);
        DispatcherTimer.RunOnce(() =>
        {
            toast.Opacity = 0;
            DispatcherTimer.RunOnce(() => ToastLayer.Children.Remove(toast), TimeSpan.FromMilliseconds(200));
        }, TimeSpan.FromMilliseconds(2800));
    }

    public async Task<string> PickFolder(string title, string start = null)
    {
        var options = new Avalonia.Platform.Storage.FolderPickerOpenOptions { Title = title };
        if (!string.IsNullOrEmpty(start) && Directory.Exists(start))
            options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(new Uri(Path.GetFullPath(start)));
        var result = await StorageProvider.OpenFolderPickerAsync(options);
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }

    public async Task<string> PickFile(string title, string filterName = null, params string[] patterns)
    {
        var options = new Avalonia.Platform.Storage.FilePickerOpenOptions { Title = title };
        if (patterns.Length > 0)
            options.FileTypeFilter =
            [
                new Avalonia.Platform.Storage.FilePickerFileType(filterName ?? "文件") { Patterns = patterns },
                Avalonia.Platform.Storage.FilePickerFileTypes.All,
            ];
        var result = await StorageProvider.OpenFilePickerAsync(options);
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }

    public async Task<string> SaveFile(string title, string suggestedName, string filterName, params string[] patterns)
    {
        var options = new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            FileTypeChoices = [new Avalonia.Platform.Storage.FilePickerFileType(filterName) { Patterns = patterns }],
            DefaultExtension = patterns.FirstOrDefault()?.TrimStart('*', '.'),
        };
        var result = await StorageProvider.SaveFilePickerAsync(options);
        return result?.TryGetLocalPath();
    }

    // ------------------------------------------------------------------ 设置

    /// <summary>保存配置。设置页的修改会直接写入 Cfg，这里只负责校验与落盘。</summary>
    public bool SaveSettings()
    {
        if (!SettingsPage.Validate(out var error))
        {
            ShowPage("settings");
            _ = Dialog("设置有误", error, "error");
            return false;
        }
        try
        {
            Cfg.Save();
        }
        catch (Exception e)
        {
            Toast("保存设置失败：" + e.Message, "error");
        }
        return true;
    }

    // ------------------------------------------------------------------ 账号

    private void InitAccounts()
    {
        Cfg.Accounts = Cfg.Accounts?.Where(a => a != null && !string.IsNullOrEmpty(a.Type) && !string.IsNullOrEmpty(a.Name))
                           .ToList() ?? [];
        if (Cfg.Accounts.Count == 0)
            Cfg.Accounts.Add(Accounts.OfflineAccount(string.IsNullOrWhiteSpace(Cfg.Username) ? "Steve" : Cfg.Username));
        Cfg.AccountIndex = Math.Clamp(Cfg.AccountIndex, 0, Cfg.Accounts.Count - 1);
    }

    public List<Account> AccountList => Cfg.Accounts;

    public int AccountIndex => Cfg.AccountIndex;

    public Account CurrentAccount =>
        Cfg.Accounts.Count > 0 ? Cfg.Accounts[Math.Clamp(Cfg.AccountIndex, 0, Cfg.Accounts.Count - 1)] : null;

    public void AccountsChanged()
    {
        if (Cfg.AccountIndex >= Cfg.Accounts.Count)
            Cfg.AccountIndex = Math.Max(0, Cfg.Accounts.Count - 1);
        Cfg.Username = CurrentAccount?.Name ?? "";
        try
        {
            Cfg.Save();
        }
        catch (Exception)
        {
            // 下次保存时再试
        }
        UpdateAccountCard();
    }

    public void SetAccount(int index)
    {
        Cfg.AccountIndex = index;
        AccountsChanged();
    }

    public void AddAccount(Account account)
    {
        var same = Cfg.Accounts.FindIndex(a => a.Type == account.Type && a.Uuid == account.Uuid && a.Api == account.Api);
        if (same < 0)
        {
            Cfg.Accounts.Add(account);
            same = Cfg.Accounts.Count - 1;
        }
        else
        {
            Cfg.Accounts[same] = account;
        }
        _skins.Remove(account.Uuid ?? "");
        Cfg.AccountIndex = same;
        AccountsChanged();
    }

    public void ReplaceAccount(int index, Account account)
    {
        Cfg.Accounts[index] = account;
        AccountsChanged();
    }

    public void RemoveAccount(int index)
    {
        Cfg.Accounts.RemoveAt(index);
        if (Cfg.AccountIndex >= Cfg.Accounts.Count)
            Cfg.AccountIndex = Math.Max(0, Cfg.Accounts.Count - 1);
        else if (index < Cfg.AccountIndex)
            Cfg.AccountIndex--;
        AccountsChanged();
    }

    public void ManageAccounts() => _ = ShowDialogAsync(new AccountDialog());

    private void OnAccountCardClick(object sender, RoutedEventArgs e) => ManageAccounts();

    /// <summary>正版 / 外置账号的皮肤，尚未加载时返回 null 并在后台获取（加载完成后触发 AccountChanged）。</summary>
    public Bitmap SkinFor(Account account)
    {
        if (account == null || account.Type == "offline" || string.IsNullOrEmpty(account.Uuid))
            return null;
        var key = account.Uuid;
        if (_skins.TryGetValue(key, out var skin))
            return skin;
        if (_skinLoading.Add(key))
        {
            _ = Task.Run(async () =>
            {
                Bitmap bitmap = null;
                try
                {
                    var png = await Accounts.FetchSkinAsync(account);
                    if (png != null)
                        bitmap = new Bitmap(new MemoryStream(png));
                }
                catch (Exception)
                {
                    // 拿不到皮肤就显示首字母
                }
                Dispatcher.UIThread.Post(() =>
                {
                    _skins[key] = bitmap;
                    _skinLoading.Remove(key);
                    if (bitmap != null)
                        AccountChanged?.Invoke();
                });
            });
        }
        return null;
    }

    private void UpdateAccountCard()
    {
        var account = CurrentAccount;
        SidebarAccountName.Text = account?.Name ?? "未设置";
        SidebarAccountType.Text = account != null ? Accounts.Describe(account) : "点击添加账号";
        SidebarAvatar.NameText = account?.Name;
        SidebarAvatar.Skin = SkinFor(account);
        AccountChanged?.Invoke();
    }

    // ------------------------------------------------------------------ 版本

    public void RefreshInstalled(string select = null)
    {
        var versions = new GameLauncher(Cfg).InstalledVersions();
        InstalledList = versions;
        Installed = [.. versions];
        var target = select ?? SelectedVersion ?? Cfg.LastVersion;
        SelectedVersion = target != null && Installed.Contains(target) ? target : versions.FirstOrDefault();
        VersionsChanged?.Invoke();
        SelectedVersionChanged?.Invoke();
    }

    public void SelectVersion(string version)
    {
        if (version == SelectedVersion)
            return;
        SelectedVersion = version;
        SelectedVersionChanged?.Invoke();
    }

    public async void ManageVersion()
    {
        var version = SelectedVersion;
        if (string.IsNullOrEmpty(version))
        {
            ShowPage("download");
            Toast("请先安装一个游戏版本", "warn");
            return;
        }
        await ShowDialogAsync(new VersionDialog(version));
        SelectedVersionChanged?.Invoke();
    }

    public void OpenGameDir()
    {
        var launcher = new GameLauncher(Cfg);
        var path = SelectedVersion != null ? launcher.GameDirFor(SelectedVersion) : launcher.McDir;
        Directory.CreateDirectory(path);
        Platform.OpenPath(path);
    }

    // ------------------------------------------------------------------ 启动游戏

    public bool GameRunning => _gameProcess is { HasExited: false };

    public string RunningVersion => GameRunning ? _runningVersion : null;

    public void KillGame()
    {
        try
        {
            _gameProcess?.Kill(true);
        }
        catch (Exception)
        {
            // 进程已经退出
        }
    }

    public async void Launch(string server = null)
    {
        var version = SelectedVersion;
        if (string.IsNullOrEmpty(version))
        {
            ShowPage("download");
            Toast("请先安装一个游戏版本", "warn");
            return;
        }
        var account = CurrentAccount;
        if (account == null)
        {
            ManageAccounts();
            return;
        }
        if (Busy)
        {
            Toast("当前有任务正在进行，请稍候", "warn");
            return;
        }
        if (GameRunning && !await Confirm("游戏正在运行", "已有一个游戏实例在运行，确定要再启动一个吗？", "再启动一个"))
            return;
        Cfg.LastVersion = version;
        if (!SaveSettings())
            return;
        Log(new string('─', 48));
        if (server != null)
            Log($"启动后将自动进入联机房间 {server}");
        var launcher = MakeLauncher();
        var gameDir = launcher.GameDirFor(version);
        var since = DateTime.Now;
        var cfg = Cfg.Clone();
        await RunTask("启动 " + version, async () =>
        {
            var (auth, changed) = await Accounts.PrepareLaunchAsync(account, cfg, launcher.Dl, AppPaths.ToolsDir, Log);
            if (changed)
                Dispatcher.UIThread.Post(AccountsChanged);
            return await launcher.LaunchAsync(version, server, auth);
        }, process => OnGameStarted(process, version, gameDir, since));
    }

    private void OnGameStarted(Process process, string version, string gameDir, DateTime since)
    {
        _gameProcess = process;
        _runningVersion = version;
        StatusText.Text = "游戏运行中";
        Toast("游戏已启动");
        LaunchPage.OnGameStateChanged();
        _ = Task.Run(() => WatchGame(process, gameDir, since));
        switch (Cfg.AfterLaunch)
        {
            case "minimize":
                WindowState = WindowState.Minimized;
                break;
            case "hide":
                if (!MultiplayerPage.InRoom)
                {
                    _hiddenForGame = true;
                    Hide();
                }
                break;
        }
    }

    private async Task WatchGame(Process process, string gameDir, DateTime since)
    {
        var parser = new LogParser();
        var recent = new Queue<string>();
        var sync = new object();

        void OnLine(string raw)
        {
            List<string> lines;
            lock (sync)
            {
                lines = parser.Feed(raw);
                foreach (var line in lines)
                {
                    recent.Enqueue(line);
                    if (recent.Count > 4000)
                        recent.Dequeue();
                }
            }
            foreach (var line in lines)
                LaunchPage.AppendLog(line, null);
        }

        var stdout = Task.Run(() => GameOutput.ReadLines(process.StandardOutput.BaseStream, OnLine));
        var stderr = Task.Run(() => GameOutput.ReadLines(process.StandardError.BaseStream, OnLine));
        try
        {
            await Task.WhenAll(stdout, stderr);
        }
        catch (Exception)
        {
            // 管道异常时只等待进程结束
        }
        await process.WaitForExitAsync();
        var code = process.ExitCode;
        CrashReport report;
        try
        {
            List<string> lines;
            lock (sync)
                lines = [.. recent];
            report = CrashAnalyzer.Analyze(lines, gameDir, since, code);
        }
        catch (Exception e)
        {
            report = new CrashReport { Detail = "崩溃分析失败：" + e.Message };
        }
        Dispatcher.UIThread.Post(() => OnGameExit(process, code, report));
    }

    private async void OnGameExit(Process process, int code, CrashReport report)
    {
        if (_gameProcess == process)
            _gameProcess = null;
        LaunchPage.OnGameStateChanged();
        if (_hiddenForGame)
        {
            _hiddenForGame = false;
            Show();
            Activate();
        }
        Log($"游戏已退出（退出码 {code}）", code == 0 ? "launcher" : "error");
        if (!Busy)
            StatusText.Text = GameRunning ? "游戏运行中" : "就绪";
        var reasons = report.Reasons.Where(r => code != 0 || r.Kind == CrashAnalyzer.Mod).Select(r => r.Text).ToList();
        if (code == 0 && reasons.Count == 0)
            return;
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        foreach (var text in reasons)
            Log("[崩溃分析] " + text, "warn");
        await ShowDialogAsync(new CrashDialog(code, reasons, report));
    }

    // ------------------------------------------------------------------ 关闭

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_forceClose)
            return;
        if (MultiplayerPage.InRoom)
        {
            e.Cancel = true;
            if (!await Confirm("正在联机", "关闭启动器会同时关闭联机房间，确定要退出吗？", "退出"))
                return;
            _forceClose = true;
            Close();
            return;
        }
        SaveSettings();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        try
        {
            Cfg.Save();
        }
        catch (Exception)
        {
            // 忽略
        }
        MultiplayerPage.Shutdown();
    }
}