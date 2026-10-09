using Avalonia.Controls;
using Avalonia.Interactivity;
using MaoX.Core;

namespace MaoX.Pages;

public partial class SettingsPage : UserControl, IPage
{
    private const string AutoJava = "自动选择（推荐）";

    private static readonly (string Key, string Text)[] Sources =
    [
        ("auto", "自动选择最快的源（推荐）"), ("bmclapi", "BMCLAPI 国内镜像"), ("official", "Mojang 官方源"),
    ];

    private static readonly (string Key, string Text)[] AfterLaunchModes =
    [
        ("keep", "保持不变"), ("minimize", "最小化启动器"), ("hide", "隐藏启动器，游戏退出后恢复"),
    ];

    private readonly Dictionary<string, string> _javaMap = [];
    private List<JavaInfo> _detected = [];

    public IReadOnlyList<JavaInfo> DetectedJava => _detected;
    private bool _loading;

    private static MainWindow Main => MainWindow.Current;
    private static LauncherConfig Cfg => Main.Cfg;

    public SettingsPage()
    {
        InitializeComponent();
        _loading = true;
        var cfg = Cfg;
        McDirBox.Text = cfg.MinecraftDir;
        IsolationSwitch.IsChecked = cfg.VersionIsolation;

        var ram = Platform.TotalMemoryMb();
        RamText.Text = $"系统内存 {ram / 1024.0:0} GB";
        MemorySlider.Maximum = Math.Max(2048, ram / 512 * 512);
        MemorySlider.Value = Math.Clamp(cfg.MaxMemory, 512, MemorySlider.Maximum);
        MemoryText.Text = $"{(int)MemorySlider.Value} MB";

        SourceBox.ItemsSource = Sources.Select(s => s.Text).ToList();
        SourceBox.SelectedIndex = Math.Max(0, Array.FindIndex(Sources, s => s.Key == cfg.DownloadSource));
        ThreadsSlider.Value = Math.Clamp(cfg.DownloadThreads, 1, 64);
        ThreadsText.Text = ((int)ThreadsSlider.Value).ToString();

        WidthBox.Text = cfg.WindowWidth.ToString();
        HeightBox.Text = cfg.WindowHeight.ToString();
        JvmBox.Text = cfg.JvmArgs;
        ClientIdBox.Text = cfg.MsaClientId;

        AfterLaunchBox.ItemsSource = AfterLaunchModes.Select(s => s.Text).ToList();
        AfterLaunchBox.SelectedIndex = Math.Max(0, Array.FindIndex(AfterLaunchModes, s => s.Key == cfg.AfterLaunch));
        BackgroundBox.Text = cfg.Background;
        MaskSlider.Value = Math.Clamp(cfg.BackgroundMask, 0, 90);
        MaskText.Text = $"{(int)MaskSlider.Value}%";
        AboutVersion.Text = $"MaoX Launcher v{Mc.LauncherVersion}";
        AboutPlatform.Text = $"{(Platform.IsWindows ? "Windows" : Platform.IsMac ? "macOS" : "Linux")} {Platform.Arch}"
                             + (Updater.CanSelfUpdate ? "" : "  ·  开发版本，不会自动更新");
        AutoUpdateSwitch.IsChecked = cfg.AutoCheckUpdate;
        FillJava([]);
        _loading = false;

        IsolationSwitch.IsCheckedChanged += (_, _) => Cfg.VersionIsolation = IsolationSwitch.IsChecked == true;
        MemorySlider.ValueChanged += (_, _) =>
        {
            Cfg.MaxMemory = (int)MemorySlider.Value;
            MemoryText.Text = $"{Cfg.MaxMemory} MB";
        };
        ThreadsSlider.ValueChanged += (_, _) =>
        {
            Cfg.DownloadThreads = (int)ThreadsSlider.Value;
            ThreadsText.Text = Cfg.DownloadThreads.ToString();
        };
        WidthBox.TextChanged += (_, _) =>
        {
            if (int.TryParse(WidthBox.Text, out var w) && w > 0)
                Cfg.WindowWidth = w;
        };
        HeightBox.TextChanged += (_, _) =>
        {
            if (int.TryParse(HeightBox.Text, out var h) && h > 0)
                Cfg.WindowHeight = h;
        };
        MaskSlider.ValueChanged += (_, _) =>
        {
            Cfg.BackgroundMask = (int)MaskSlider.Value;
            MaskText.Text = $"{Cfg.BackgroundMask}%";
            Main.ApplyBackground();
        };
        AutoUpdateSwitch.IsCheckedChanged += (_, _) => Cfg.AutoCheckUpdate = AutoUpdateSwitch.IsChecked == true;
        JvmBox.TextChanged += (_, _) => Cfg.JvmArgs = (JvmBox.Text ?? "").Trim();
        ClientIdBox.TextChanged += (_, _) => Cfg.MsaClientId = (ClientIdBox.Text ?? "").Trim();
    }

    public void OnShow()
    {
    }

    /// <summary>检查无法即时写入配置的输入项。</summary>
    public bool Validate(out string error)
    {
        error = null;
        if (!int.TryParse(WidthBox.Text, out var w) || w <= 0 || !int.TryParse(HeightBox.Text, out var h) || h <= 0)
        {
            error = "游戏窗口的宽度和高度必须是正整数。";
            return false;
        }
        Cfg.WindowWidth = w;
        Cfg.WindowHeight = h;
        ApplyMcDir();
        return true;
    }

    // ------------------------------------------------------------------ 游戏目录

    private bool ApplyMcDir()
    {
        var dir = (McDirBox.Text ?? "").Trim();
        if (dir.Length == 0)
        {
            dir = AppPaths.DefaultMinecraftDir;
            McDirBox.Text = dir;
        }
        if (dir == Cfg.MinecraftDir)
            return false;
        Cfg.MinecraftDir = dir;
        return true;
    }

    private void OnMcDirLostFocus(object sender, RoutedEventArgs e)
    {
        if (ApplyMcDir())
        {
            Main.SaveSettings();
            Main.RefreshInstalled();
        }
    }

    private async void OnBrowseMcDir(object sender, RoutedEventArgs e)
    {
        var path = await Main.PickFolder("选择游戏目录（.minecraft）", McDirBox.Text);
        if (path == null)
            return;
        McDirBox.Text = path;
        if (ApplyMcDir())
        {
            Main.SaveSettings();
            Main.RefreshInstalled();
        }
    }

    private void OnOpenMcDir(object sender, RoutedEventArgs e)
    {
        ApplyMcDir();
        var dir = Path.GetFullPath(Cfg.MinecraftDir);
        Directory.CreateDirectory(dir);
        Platform.OpenPath(dir);
    }

    // ------------------------------------------------------------------ Java

    private string RuntimeDir => Path.Combine(Path.GetFullPath(Cfg.MinecraftDir), "runtime");

    public async void DetectJavaInBackground()
    {
        var runtime = RuntimeDir;
        try
        {
            FillJava(await Task.Run(() => JavaManager.FindJava([runtime])));
        }
        catch (Exception)
        {
            // 后台检测失败不打扰用户
        }
    }

    private void OnDetectJava(object sender, RoutedEventArgs e)
    {
        var runtime = RuntimeDir;
        _ = Main.RunTask("检测 Java", () => Task.Run(() => JavaManager.FindJava([runtime])), javas =>
        {
            FillJava(javas);
            Main.Toast($"检测到 {javas.Count} 个 Java");
        });
    }

    private void FillJava(List<JavaInfo> javas)
    {
        var loading = _loading;
        _loading = true;
        _detected = javas;
        _javaMap.Clear();
        foreach (var java in javas)
            _javaMap[$"Java {java.Major}  —  {java.Path}"] = java.Path;
        var items = new List<string> { AutoJava };
        items.AddRange(_javaMap.Keys);
        string selected = AutoJava;
        if (!string.IsNullOrEmpty(Cfg.JavaPath))
        {
            selected = _javaMap.FirstOrDefault(p => string.Equals(p.Value, Cfg.JavaPath, StringComparison.OrdinalIgnoreCase)).Key;
            if (selected == null)
            {
                selected = "自定义  —  " + Cfg.JavaPath;
                _javaMap[selected] = Cfg.JavaPath;
                items.Add(selected);
            }
        }
        JavaBox.ItemsSource = items;
        JavaBox.SelectedItem = selected;
        _loading = loading;
    }

    private void OnJavaChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || JavaBox.SelectedItem is not string text)
            return;
        Cfg.JavaPath = text == AutoJava ? "" : _javaMap.GetValueOrDefault(text, "");
    }

    private async void OnBrowseJava(object sender, RoutedEventArgs e)
    {
        var path = Platform.IsWindows
            ? await Main.PickFile("选择 Java 可执行文件", "Java", "javaw.exe", "java.exe")
            : await Main.PickFile("选择 Java 可执行文件");
        if (path == null)
            return;
        Cfg.JavaPath = path;
        FillJava(_detected);
    }

    // ------------------------------------------------------------------ 下载、账号、个性化

    private void OnSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && SourceBox.SelectedIndex >= 0)
            Cfg.DownloadSource = Sources[SourceBox.SelectedIndex].Key;
    }

    private void OnAfterLaunchChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && AfterLaunchBox.SelectedIndex >= 0)
            Cfg.AfterLaunch = AfterLaunchModes[AfterLaunchBox.SelectedIndex].Key;
    }

    private void OnManageAccounts(object sender, RoutedEventArgs e) => Main.ManageAccounts();

    private void OnClientIdGuide(object sender, RoutedEventArgs e) => Platform.OpenUrl(Accounts.MsaAppGuide);

    private void SetBackground(string path)
    {
        path = (path ?? "").Trim();
        BackgroundBox.Text = path;
        if (path == Cfg.Background)
            return;
        Cfg.Background = path;
        Main.SaveSettings();
        Main.ApplyBackground();
    }

    private void OnBackgroundLostFocus(object sender, RoutedEventArgs e) => SetBackground(BackgroundBox.Text);

    private async void OnBrowseBackground(object sender, RoutedEventArgs e)
    {
        var path = await Main.PickFile("选择背景图片", "图片", "*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp", "*.gif");
        if (path != null)
            SetBackground(path);
    }

    private void OnClearBackground(object sender, RoutedEventArgs e) => SetBackground("");

    // ------------------------------------------------------------------ 关于

    private void OnOpenRepo(object sender, RoutedEventArgs e) => Platform.OpenUrl(Updater.RepoUrl);

    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        try
        {
            await Main.CheckForUpdate(false);
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }
}
