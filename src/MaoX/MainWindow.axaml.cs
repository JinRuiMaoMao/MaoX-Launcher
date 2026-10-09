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
using static MaoX.Core.I18n;

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
    /// <summary>任务中心里的任务，最新的在前。</summary>
    public List<TaskItem> Tasks { get; } = [];

    /// <summary>是否有任务正在进行。</summary>
    public bool Busy => Tasks.Any(t => t.Running);

    /// <summary>是否正在准备启动游戏（启动按钮在此期间不可用）。</summary>
    public bool Launching => Tasks.Any(t => t.Running && t.Name.StartsWith(T("启动 "), StringComparison.Ordinal));
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
    private readonly TaskCenterView _taskCenter = new();
    private string _idleStatus = T("就绪");
    private bool _barIndeterminate;

    public MainWindow()
    {
        Current = this;
        InitializeComponent();
        Cfg = LauncherConfig.Load();
        ThemeManager.Apply(Cfg.Theme, Cfg.AccentColor);
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
            if (!SmokeTest.Active)
                LaunchFromCommandLine();
        };
    }

    /// <summary>处理桌面快捷方式传来的 --launch &lt;版本&gt;。</summary>
    private void LaunchFromCommandLine()
    {
        var args = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(args, "--launch");
        if (index < 0 || index + 1 >= args.Length)
            return;
        var version = args[index + 1];
        if (!Installed.Contains(version))
        {
            Toast(F("找不到版本 {0}，可能已被删除或改名", version), "error");
            return;
        }
        SelectVersion(version);
        Launch();
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
                    await Dialog(T("检查更新失败"), ErrorText(e), "error");
                return;
            }
            if (info == null)
            {
                if (!silent)
                    Toast(F("已经是最新版本 v{0}", Mc.LauncherVersion));
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
        var message = F("当前版本 v{0}。", Mc.LauncherVersion);
        if (!Updater.CanSelfUpdate)
            message += T("当前运行的不是发布版，请到发布页手动下载。");
        else if (info.Url == null)
            message += T("这个版本没有提供当前系统的安装包，请到发布页查看。");
        if (!string.IsNullOrWhiteSpace(info.Notes))
            message += "\n\n" + info.Notes;
        var buttons = new List<(string, object, string)>();
        if (canApply)
            buttons.Add((T("立即更新"), "update", "primary"));
        buttons.Add((T("打开发布页"), "page", canApply ? "" : "primary"));
        buttons.Add((T("跳过此版本"), "skip", ""));
        buttons.Add((T("以后再说"), "later", ""));
        switch (await Dialog(F("发现新版本 v{0}", info.Version), message, "info", buttons.ToArray()))
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
        await RunTask(T("下载更新"), () => Updater.DownloadAsync(info, (done, total) =>
            Progress((int)(done / 1024), (int)Math.Max(1, total / 1024), F("下载新版本 v{0}", info.Version))), path => file = path);
        if (file == null)
            return;
        if (MultiplayerPage.InRoom
            && !await Confirm(T("更新启动器"), T("更新需要重启启动器，当前的联机房间会断开。"), T("继续更新")))
            return;
        try
        {
            SaveSettings();
            Cfg.Save();
            Updater.Apply(file);
        }
        catch (Exception e)
        {
            await Dialog(T("更新失败"), F("{0}\n\n可以到发布页手动下载新版本。", ErrorText(e)), "error");
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
                    Toast(T("无法读取背景图片"), "error");
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
            ? (IBrush)this.FindResource("GlassSidebar")
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

    /// <summary>报告进度，记到当前 async 调用链所属的任务上（不在任务里调用时忽略）。</summary>
    public void Progress(int done, int total, string text = "")
    {
        if (total > 0)
            TaskItem.Current.Value?.Report(done, total, text);
    }

    private void FlushProgress()
    {
        var changed = false;
        foreach (var task in Tasks)
            changed |= task.Flush();
        if (changed)
            UpdateStatusBar();
    }

    /// <summary>没有任务时状态栏显示的文字。</summary>
    public void SetStatus(string text)
    {
        _idleStatus = text;
        UpdateStatusBar();
    }

    private void UpdateStatusBar()
    {
        var running = Tasks.Where(t => t.Running).ToList();
        TasksButton.IsVisible = Tasks.Count > 0;
        TasksLabel.Text = running.Count > 0 ? F("任务 {0}", running.Count) : T("任务");
        if (running.Count == 0)
        {
            StatusText.Text = GameRunning ? T("游戏运行中") : _idleStatus;
            ProgressText.Text = "";
            ProgressBar.Set(0);
            _barIndeterminate = false;
        }
        else
        {
            var first = running[^1];
            StatusText.Text = running.Count == 1
                ? first.Name + "…"
                : F("{0} 个任务进行中：{1}", running.Count, string.Join(T("、"), running.Select(t => t.Name)));
            var known = running.Where(t => t.Fraction != null).ToList();
            if (known.Count == 0)
            {
                ProgressText.Text = "";
                if (!_barIndeterminate)
                    ProgressBar.Start();
                _barIndeterminate = true;
            }
            else
            {
                var fraction = known.Average(t => t.Fraction!.Value);
                ProgressBar.Set(fraction);
                _barIndeterminate = false;
                ProgressText.Text = running.Count == 1 ? first.Detail + $"   {fraction * 100:0}%" : $"{fraction * 100:0}%";
            }
        }
        if (_taskCenter.IsAttachedToVisualTree())
            _taskCenter.Refresh();
    }

    public Control TaskCenterView => _taskCenter;

    public void ShowTaskCenter() => OnTasksButtonClick(null, null);

    private void OnTasksButtonClick(object sender, RoutedEventArgs e)
    {
        _taskCenter.Refresh();
        var flyout = new Flyout { Content = _taskCenter, Placement = PlacementMode.TopEdgeAlignedRight };
        flyout.ShowAt(TasksButton);
    }

    public void ClearFinishedTasks()
    {
        Tasks.RemoveAll(t => !t.Running);
        UpdateStatusBar();
    }

    public GameLauncher MakeLauncher()
    {
        return new GameLauncher(Cfg.Clone(), Log, Progress) { Manifest = Manifest };
    }

    // ------------------------------------------------------------------ 任务

    public bool IsTaskRunning(string name) => Tasks.Any(t => t.Running && t.Name == name);

    /// <summary>
    /// 在后台执行一个任务，显示在任务中心里，可以和其他任务同时进行、单独取消（同名任务不能重复开始）。
    /// 失败时弹出错误对话框，取消时只记日志。
    /// </summary>
    public async Task RunTask<T>(string name, Func<Task<T>> job, Action<T> onDone = null)
    {
        if (IsTaskRunning(name))
        {
            Toast(I18n.T("这个任务已经在进行中了"), "warn");
            return;
        }
        if (!SaveSettings())
            return;
        var task = new TaskItem(name);
        Tasks.Insert(0, task);
        if (Tasks.Count > 30)
            Tasks.RemoveAll(t => !t.Running && Tasks.IndexOf(t) >= 30);
        OnTasksChanged();
        T result;
        try
        {
            result = await Task.Run(async () =>
            {
                TaskItem.Current.Value = task;
                TaskContext.Token = task.Cancel.Token;
                return await job();
            });
        }
        catch (Exception e) when (task.Cancel.IsCancellationRequested
                                  && e is OperationCanceledException or DownloadException { InnerException: OperationCanceledException })
        {
            task.Finish("cancelled");
            OnTasksChanged();
            Log(F("{0}已取消", name), "warn");
            return;
        }
        catch (Exception e)
        {
            var message = ErrorText(e);
            task.Finish("failed", message);
            OnTasksChanged();
            Log(F("[错误] {0}失败：{1}", name, message), "error");
            Trace.WriteLine(e);
            await Dialog(F("{0}失败", name), message, "error");
            return;
        }
        task.Finish("done");
        OnTasksChanged();
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
            TaskCanceledException => T("操作超时，请检查网络后重试"),
            HttpRequestException http => F("网络错误：{0}", http.Message),
            _ => e.Message,
        };
    }

    private void OnTasksChanged()
    {
        UpdateStatusBar();
        BusyChanged?.Invoke(Busy);
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
            buttons = [(T("确定"), null, "primary")];
        return ShowDialogAsync(new MessageDialog(title, message, kind, buttons));
    }

    public async Task<bool> Confirm(string title, string message, string ok = "确定", string kind = "warn",
                                    string okStyle = "primary")
    {
        var result = await Dialog(title, message, kind, (T(ok), true, okStyle), (T("取消"), false, ""));
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
                new Avalonia.Platform.Storage.FilePickerFileType(filterName ?? T("文件")) { Patterns = patterns },
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
            _ = Dialog(T("设置有误"), error, "error");
            return false;
        }
        try
        {
            Cfg.Save();
        }
        catch (Exception e)
        {
            Toast(F("保存设置失败：{0}", e.Message), "error");
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

    /// <summary>
    /// 账号的皮肤。离线账号读取本地设置的皮肤；正版 / 外置账号尚未加载时返回 null 并在后台获取（加载完成后触发 AccountChanged）。
    /// </summary>
    public Bitmap SkinFor(Account account)
    {
        if (account == null || string.IsNullOrEmpty(account.Uuid))
            return null;
        if (account.Type == "offline")
            return LocalSkin(account.Skin);
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

    private Bitmap LocalSkin(string hash)
    {
        if (!Skins.Exists(hash))
            return null;
        var key = "local:" + hash;
        if (!_skins.TryGetValue(key, out var bitmap))
        {
            try
            {
                bitmap = new Bitmap(Skins.PathFor(hash));
            }
            catch (Exception)
            {
                bitmap = null;
            }
            _skins[key] = bitmap;
        }
        return bitmap;
    }

    /// <summary>换了皮肤后丢掉缓存，重新获取并刷新头像。</summary>
    public void SkinChanged(Account account)
    {
        _skins.Remove(account.Uuid ?? "");
        UpdateAccountCard();
    }

    private void UpdateAccountCard()
    {
        var account = CurrentAccount;
        SidebarAccountName.Text = account?.Name ?? T("未设置");
        SidebarAccountType.Text = account != null ? Accounts.Describe(account) : T("点击添加账号");
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
            Toast(T("请先安装一个游戏版本"), "warn");
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
            Toast(T("请先安装一个游戏版本"), "warn");
            return;
        }
        var account = CurrentAccount;
        if (account == null)
        {
            ManageAccounts();
            return;
        }
        if (Launching)
        {
            Toast(T("游戏正在启动，请稍候"), "warn");
            return;
        }
        if (GameRunning && !await Confirm(T("游戏正在运行"), T("已有一个游戏实例在运行，确定要再启动一个吗？"), T("再启动一个")))
            return;
        Cfg.LastVersion = version;
        if (!SaveSettings())
            return;
        Log(new string('─', 48));
        if (server != null)
            Log(F("启动后将自动进入联机房间 {0}", server));
        var launcher = MakeLauncher();
        var gameDir = launcher.GameDirFor(version);
        var since = DateTime.Now;
        var cfg = Cfg.Clone();
        await RunTask(F("启动 {0}", version), async () =>
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
        UpdateStatusBar();
        Toast(T("游戏已启动"));
        LaunchPage.OnGameStateChanged();
        var started = DateTime.Now;
        _ = Task.Run(() => WatchGame(process, version, gameDir, since, started));
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

    private async Task WatchGame(Process process, string version, string gameDir, DateTime since, DateTime started)
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
        try
        {
            PlayTime.Record(new GameLauncher(Cfg), version, started, DateTime.Now);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 记录时长失败不影响其他流程
        }
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
            report = new CrashReport { Detail = F("崩溃分析失败：{0}", e.Message) };
        }
        Dispatcher.UIThread.Post(() => OnGameExit(process, code, report));
    }

    private async void OnGameExit(Process process, int code, CrashReport report)
    {
        if (_gameProcess == process)
            _gameProcess = null;
        LaunchPage.OnGameStateChanged();
        LaunchPage.RefreshHero();
        if (_hiddenForGame)
        {
            _hiddenForGame = false;
            Show();
            Activate();
        }
        Log(F("游戏已退出（退出码 {0}）", code), code == 0 ? "launcher" : "error");
        UpdateStatusBar();
        var reasons = report.Reasons.Where(r => code != 0 || r.Kind == CrashAnalyzer.Mod).Select(r => r.Text).ToList();
        if (code == 0 && reasons.Count == 0)
            return;
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        foreach (var text in reasons)
            Log(F("[崩溃分析] {0}", text), "warn");
        await ShowDialogAsync(new CrashDialog(code, reasons, report));
    }

    // ------------------------------------------------------------------ 关闭

    /// <summary>重启启动器（切换语言后）。有任务或联机房间时提示稍后手动重启。</summary>
    public async void RestartLauncher()
    {
        if (Busy || MultiplayerPage.InRoom)
        {
            Toast(I18n.T("有任务或联机房间正在进行，稍后手动重启启动器即可生效"), "info");
            return;
        }
        if (!SaveSettings())
            return;
        try
        {
            Updater.Relaunch();
        }
        catch (Exception e)
        {
            await Dialog(I18n.T("重启失败"), e.Message, "error");
            return;
        }
        _forceClose = true;
        Close();
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_forceClose)
            return;
        if (MultiplayerPage.InRoom)
        {
            e.Cancel = true;
            if (!await Confirm(T("正在联机"), T("关闭启动器会同时关闭联机房间，确定要退出吗？"), T("退出")))
                return;
            _forceClose = true;
            Close();
            return;
        }
        if (Busy && !SmokeTest.Active)
        {
            e.Cancel = true;
            var names = string.Join(T("、"), Tasks.Where(t => t.Running).Select(t => t.Name));
            if (!await Confirm(T("还有任务没完成"), F("正在进行：{0}。\n现在退出会中断这些任务，确定要退出吗？", names), T("退出")))
                return;
            foreach (var task in Tasks.Where(t => t.Running))
                task.Cancel.Cancel();
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