using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MaoX.Core;
using static MaoX.Core.I18n;

namespace MaoX.Pages;

public partial class SettingsPage : UserControl, IPage
{
    private static string AutoJava => T("自动选择（推荐）");

    private static readonly (string Key, string Text)[] Sources =
    [
        ("auto", "自动选择最快的源（推荐）"), ("bmclapi", "BMCLAPI 国内镜像"), ("official", "Mojang 官方源"),
    ];

    private static readonly (string Key, string Text)[] AfterLaunchModes =
    [
        ("keep", "保持不变"), ("minimize", "最小化启动器"), ("hide", "隐藏启动器，游戏退出后恢复"),
    ];

    private static readonly (string Key, string Text)[] UpdateChannels =
    [
        ("stable", "正式版"), ("preview", "预览版"),
    ];

    private readonly Dictionary<string, string> _javaMap = [];
    private List<JavaInfo> _detected = [];

    public IReadOnlyList<JavaInfo> DetectedJava => _detected;
    private bool _loading;

    private static MainView Main => MainView.Current;
    private static LauncherConfig Cfg => Main.Cfg;

    public SettingsPage()
    {
        InitializeComponent();
        if (Platform.IsMobile)
        {
            // 手机上游戏目录固定在应用数据目录，Java 由启动器自带，游戏总是全屏
            McDirBox.IsReadOnly = true;
            foreach (var c in new Control[] { BrowseMcDirButton, OpenMcDirButton, JavaPathPanel, WindowSizePanel,
                                              AfterLaunchPanel, AutoUpdatePanel, CheckUpdateButton, ExtraDirsPanel })
                c.IsVisible = false;
            TouchLayoutPanel.IsVisible = GyroPanel.IsVisible = true;
        }
        _loading = true;
        var cfg = Cfg;
        McDirBox.Text = cfg.MinecraftDir;
        IsolationBox.ItemsSource = Isolation.Modes.Select(m => T(m.Text)).ToList();
        IsolationBox.SelectedIndex = Math.Max(0, Array.FindIndex(Isolation.Modes, m => m.Key == Isolation.Normalize(cfg.IsolationMode)));
        AutoBackupSwitch.IsChecked = cfg.AutoBackupWorlds;
        PreLaunchSwitch.IsChecked = cfg.PreLaunchCheckOn;
        ProxyBox.Text = cfg.Proxy;
        DohSwitch.IsChecked = cfg.Doh;
        Ipv6Switch.IsChecked = cfg.PreferIpv6;
        SpeedLimitSlider.Value = Math.Clamp(cfg.DownloadSpeedLimit, 0, 20480);
        RefreshSpeedLimit();
        WindowTitleBox.Text = cfg.WindowTitle;
        PriorityBox.ItemsSource = LaunchExtras.Priorities.Select(p => T(p.Text)).ToList();
        PriorityBox.SelectedIndex = Math.Max(0, Array.FindIndex(LaunchExtras.Priorities, p => p.Key == cfg.ProcessPriority));
        PreCmdBox.Text = cfg.PreLaunchCommand;
        PostCmdBox.Text = cfg.PostLaunchCommand;
        HolidaySwitch.IsChecked = cfg.HolidayThemeOn;
        HomepageBox.Text = cfg.Homepage;
        BgmBox.Text = cfg.Bgm;
        SfxBox.Text = cfg.LaunchSfx;
        ChannelBox.ItemsSource = UpdateChannels.Select(c => T(c.Text)).ToList();
        ChannelBox.SelectedIndex = Math.Max(0, Array.FindIndex(UpdateChannels, c => c.Key == cfg.UpdateChannel));
        GyroSwitch.IsChecked = cfg.GyroLook;
        UpdateTouchLayoutText();
        RefreshGameDirs();

        AutoMemorySwitch.IsChecked = cfg.AutoMemoryOn;
        OptimizeMemorySwitch.IsChecked = cfg.OptimizeMemoryOn;
        var ram = GameMemory.TotalMb();
        RamText.Text = F("系统内存 {0:0} GB", ram / 1024.0);
        MemorySlider.Maximum = Math.Max(2048, ram / 512 * 512);
        MemorySlider.Value = Math.Clamp(cfg.MaxMemory, 512, MemorySlider.Maximum);
        RefreshMemoryLabel();

        SourceBox.ItemsSource = Sources.Select(s => T(s.Text)).ToList();
        SourceBox.SelectedIndex = Math.Max(0, Array.FindIndex(Sources, s => s.Key == cfg.DownloadSource));
        ThreadsSlider.Value = Math.Clamp(cfg.DownloadThreads, 1, 64);
        ThreadsText.Text = ((int)ThreadsSlider.Value).ToString();

        WidthBox.Text = cfg.WindowWidth.ToString();
        HeightBox.Text = cfg.WindowHeight.ToString();
        JvmBox.Text = cfg.JvmArgs;
        ClientIdBox.Text = cfg.MsaClientId;

        AfterLaunchBox.ItemsSource = AfterLaunchModes.Select(s => T(s.Text)).ToList();
        AfterLaunchBox.SelectedIndex = Math.Max(0, Array.FindIndex(AfterLaunchModes, s => s.Key == cfg.AfterLaunch));
        BackgroundBox.Text = cfg.Background;
        MaskSlider.Value = Math.Clamp(cfg.BackgroundMask, 0, 90);
        MaskText.Text = $"{(int)MaskSlider.Value}%";
        AboutVersion.Text = $"MaoX Launcher v{Mc.LauncherVersion}";
        AboutPlatform.Text = $"{(Platform.IsWindows ? "Windows" : Platform.IsMac ? "macOS" : "Linux")} {Platform.Arch}"
                             + (Updater.CanSelfUpdate ? "" : T("  ·  开发版本，不会自动更新"));
        AutoUpdateSwitch.IsChecked = cfg.AutoCheckUpdate;
        LanguageBox.ItemsSource = I18n.Languages.Select(l => l.Name).ToList();
        LanguageBox.SelectedIndex = Math.Max(0, Array.FindIndex(I18n.Languages, l => l.Key == (cfg.Language ?? "")));
        BuildSwatches();
        UpdateThemeControls();
        FillJava([]);
        LoadBuiltInJit();
        _loading = false;

        AutoBackupSwitch.IsCheckedChanged += (_, _) => Cfg.AutoBackupWorlds = AutoBackupSwitch.IsChecked == true;
        PreLaunchSwitch.IsCheckedChanged += (_, _) => Cfg.PreLaunchCheck = PreLaunchSwitch.IsChecked == true;
        DohSwitch.IsCheckedChanged += (_, _) =>
        {
            Cfg.Doh = DohSwitch.IsChecked == true;
            Http.ApplyNetwork(Cfg);
        };
        Ipv6Switch.IsCheckedChanged += (_, _) =>
        {
            Cfg.PreferIpv6 = Ipv6Switch.IsChecked == true;
            Http.ApplyNetwork(Cfg);
        };
        SpeedLimitSlider.ValueChanged += (_, _) =>
        {
            Cfg.DownloadSpeedLimit = (int)SpeedLimitSlider.Value;
            Http.ApplyNetwork(Cfg);
            RefreshSpeedLimit();
        };
        WindowTitleBox.TextChanged += (_, _) => Cfg.WindowTitle = (WindowTitleBox.Text ?? "").Trim();
        PreCmdBox.TextChanged += (_, _) => Cfg.PreLaunchCommand = (PreCmdBox.Text ?? "").Trim();
        PostCmdBox.TextChanged += (_, _) => Cfg.PostLaunchCommand = (PostCmdBox.Text ?? "").Trim();
        HolidaySwitch.IsCheckedChanged += (_, _) =>
        {
            Cfg.HolidayTheme = HolidaySwitch.IsChecked == true;
            ThemeManager.Apply(Cfg.Theme, Holiday.ResolveAccent(Cfg));
        };
        HomepageBox.LostFocus += (_, _) => Cfg.Homepage = (HomepageBox.Text ?? "").Trim();
        BgmBox.LostFocus += (_, _) => ApplyBgm((BgmBox.Text ?? "").Trim());
        SfxBox.LostFocus += (_, _) => Cfg.LaunchSfx = (SfxBox.Text ?? "").Trim();
        GyroSwitch.IsCheckedChanged += (_, _) =>
        {
            Cfg.GyroLook = GyroSwitch.IsChecked == true;
            var layout = Cfg.CurrentControlLayout();
            if (layout != null)
                layout.Gyro = Cfg.GyroLook;
        };
        ProxyBox.LostFocus += (_, _) =>
        {
            Cfg.Proxy = (ProxyBox.Text ?? "").Trim();
            Http.ApplyNetwork(Cfg);
        };
        AutoMemorySwitch.IsCheckedChanged += (_, _) =>
        {
            Cfg.AutoMemory = AutoMemorySwitch.IsChecked == true;
            RefreshMemoryLabel();
        };
        OptimizeMemorySwitch.IsCheckedChanged += (_, _) => Cfg.OptimizeMemory = OptimizeMemorySwitch.IsChecked == true;
        MemorySlider.ValueChanged += (_, _) =>
        {
            Cfg.MaxMemory = (int)MemorySlider.Value;
            RefreshMemoryLabel();
        };
        Main.SelectedVersionChanged += RefreshMemoryLabel;
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

    public void OnShow() => RefreshMemoryLabel();

    private void RefreshMemoryLabel()
    {
        var auto = Cfg.AutoMemoryOn;
        MemoryManual.Opacity = auto ? 0.55 : 1;
        MemorySlider.IsEnabled = !auto;
        if (auto && !string.IsNullOrEmpty(Main.SelectedVersion))
        {
            var mb = GameMemory.Allocate(Main.MakeLauncher(), Main.SelectedVersion, Cfg);
            var avail = GameMemory.AvailableMb() / 1024.0;
            MemoryText.Text = F("约 {0} MB", mb);
            RamText.Text = F("系统内存 {0:0} GB，可用 {1:0.0} GB", GameMemory.TotalMb() / 1024.0, avail);
        }
        else
        {
            MemoryText.Text = $"{Cfg.MaxMemory} MB";
            RamText.Text = F("系统内存 {0:0} GB", GameMemory.TotalMb() / 1024.0);
        }
    }

    // ------------------------------------------------------------------ iOS 内置 JIT（实验）

    private void LoadBuiltInJit()
    {
        if (!MobileJit.BuiltInIncluded)
            return;
        var reason = MobileJit.BuiltInUnavailableReason();
        BuiltInJitPanel.IsVisible = true;
        BuiltInJitSwitch.IsEnabled = ResetDdiButton.IsEnabled = reason == null;
        BuiltInJitSwitch.IsChecked = reason == null && Cfg.IosJitMethod == "builtin";
        BuiltInJitHint.Text = reason ?? T("由安装包自带的 StikJIT 开启 JIT，不用切到 StikDebug。仍需要 LocalDevVPN、开发者模式和配对文件");
        BuiltInJitSwitch.IsCheckedChanged += (_, _) =>
        {
            if (!_loading)
                Cfg.IosJitMethod = BuiltInJitSwitch.IsChecked == true ? "builtin" : "auto";
        };
        ImportPairingButton.IsEnabled = reason == null;
        GeneratePairingButton.IsVisible = MobileJit.GeneratePairingFile != null;
        GeneratePairingButton.IsEnabled = reason == null;
        UpdatePairingFileText();
    }

    private void UpdatePairingFileText()
    {
        var status = MobileJit.PairingFileStatus?.Invoke();
        PairingFileText.Text = status;
        PairingFileText.IsVisible = !string.IsNullOrEmpty(status);
    }

    private async void OnImportPairing(object sender, RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage == null || MobileJit.ImportPairingFile == null)
            return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = T("选择配对文件") });
        if (files.Count == 0)
            return;
        string error;
        try
        {
            // 手机上选到的文件只能用流读取
            await using var source = await files[0].OpenReadAsync();
            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer);
            error = MobileJit.ImportPairingFile(buffer.ToArray());
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        UpdatePairingFileText();
        if (error == null)
            Main.Toast(T("配对文件已导入"));
        else
            await Main.Dialog(T("导入配对文件"), error, "error");
    }

    private CancellationTokenSource _pairing;

    private async void OnGeneratePairing(object sender, RoutedEventArgs e)
    {
        if (_pairing != null)
        {
            _pairing.Cancel();
            return;
        }
        var unavailable = MobileJit.GeneratePairingUnavailableReason?.Invoke();
        if (unavailable != null)
        {
            await Main.Dialog(T("在本机生成配对文件（iOS 27+）"), unavailable, "error");
            return;
        }

        _pairing = new CancellationTokenSource();
        GeneratePairingButton.Content = T("取消配对");
        ImportPairingButton.IsEnabled = false;
        PairingPanel.IsVisible = true;
        PairingPinText.IsVisible = false;
        void Progress(string status, string pin) => Dispatcher.UIThread.Post(() =>
        {
            if (_pairing == null)
                return;
            PairingStatusText.Text = status;
            PairingPinText.Text = pin;
            PairingPinText.IsVisible = !string.IsNullOrEmpty(pin);
        });

        string message = null, error = null;
        try
        {
            message = await MobileJit.GeneratePairingFile(Progress, _pairing.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        finally
        {
            _pairing.Dispose();
            _pairing = null;
            GeneratePairingButton.Content = T("在本机生成配对文件（iOS 27+）");
            ImportPairingButton.IsEnabled = true;
            PairingPanel.IsVisible = false;
            UpdatePairingFileText();
        }
        if (message != null)
            await Main.Dialog(T("在本机生成配对文件（iOS 27+）"), message);
        else if (error != null)
            await Main.Dialog(T("在本机生成配对文件（iOS 27+）"), error, "error");
    }

    private async void OnResetDdi(object sender, RoutedEventArgs e)
    {
        ResetDdiButton.IsEnabled = false;
        try
        {
            await Main.Dialog(T("内置 JIT（实验）"), await MobileJit.ResetBuiltInDdi());
        }
        finally
        {
            ResetDdiButton.IsEnabled = true;
        }
    }

    // ------------------------------------------------------------------ 外观

    private readonly List<(Button Button, string Color)> _swatches = [];

    private void BuildSwatches()
    {
        var index = 0;
        foreach (var (name, color) in ThemeManager.Presets)
        {
            var swatch = new Button
            {
                Classes = { "swatch" }, Background = new SolidColorBrush(Color.Parse(color)),
            };
            ToolTip.SetTip(swatch, T(name));
            swatch.Click += (_, _) => SetAccent(color);
            _swatches.Add((swatch, color));
            AccentSwatches.Children.Insert(index++, swatch);
        }
    }

    private void UpdateThemeControls()
    {
        ThemeDark.IsChecked = Cfg.Theme is not ("light" or "system");
        ThemeLight.IsChecked = Cfg.Theme == "light";
        ThemeSystem.IsChecked = Cfg.Theme == "system";
        var accent = ThemeManager.ToHex(ThemeManager.Accent);
        foreach (var (button, color) in _swatches)
            button.Classes.Set("selected", color.Equals(accent, StringComparison.OrdinalIgnoreCase));
        if (!AccentBox.IsFocused)
            AccentBox.Text = accent;
    }

    private void OnThemeClick(object sender, RoutedEventArgs e)
    {
        Cfg.Theme = (string)((Control)sender).Tag!;
        ApplyTheme();
    }

    private void SetAccent(string color)
    {
        Cfg.AccentColor = color;
        ApplyTheme();
    }

    private void ApplyTheme()
    {
        ThemeManager.Apply(Cfg.Theme, Holiday.ResolveAccent(Cfg));
        UpdateThemeControls();
        Main.SaveSettings();
    }

    private void OnAccentBoxLostFocus(object sender, RoutedEventArgs e) => CommitAccentBox();

    private void OnAccentBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            CommitAccentBox();
    }

    private void CommitAccentBox()
    {
        if (ThemeManager.TryParse(AccentBox.Text, out var color))
            SetAccent(ThemeManager.ToHex(color));
        else
            AccentBox.Text = ThemeManager.ToHex(ThemeManager.Accent);
    }

    /// <summary>检查无法即时写入配置的输入项。</summary>
    public bool Validate(out string error)
    {
        error = null;
        if (!int.TryParse(WidthBox.Text, out var w) || w <= 0 || !int.TryParse(HeightBox.Text, out var h) || h <= 0)
        {
            error = T("游戏窗口的宽度和高度必须是正整数。");
            return false;
        }
        Cfg.WindowWidth = w;
        Cfg.WindowHeight = h;
        ApplyMcDir();
        return true;
    }

    public void RefreshGameDirs()
    {
        if (ExtraDirsList == null)
            return;
        ExtraDirsList.Children.Clear();
        foreach (var dir in Cfg.ExtraDirs ?? [])
        {
            var path = dir.Path;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
            row.Children.Add(new TextBlock
            {
                Text = (string.IsNullOrWhiteSpace(dir.Name) ? Path.GetFileName(path) : dir.Name) + "  ·  " + path,
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            });
            var use = new Button { Content = T("切换"), Margin = new Thickness(8, 0, 0, 0) };
            use.Click += (_, _) => Main.SetMinecraftDir(path);
            Grid.SetColumn(use, 1);
            row.Children.Add(use);
            var remove = new Button { Classes = { "ghost", "danger-text" }, Content = T("移除"), Margin = new Thickness(6, 0, 0, 0) };
            remove.Click += (_, _) =>
            {
                Cfg.ExtraDirs.RemoveAll(d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase));
                Main.SaveSettings();
                RefreshGameDirs();
            };
            Grid.SetColumn(remove, 2);
            row.Children.Add(remove);
            ExtraDirsList.Children.Add(row);
        }
        if (ExtraDirsList.Children.Count == 0)
            ExtraDirsList.Children.Add(new TextBlock { Text = T("还没有额外目录。可以添加第二个 .minecraft，或从 PCL / HMCL 导入。"), Classes = { "small", "dim", "wrap" } });
    }

    private async void OnAddExtraDir(object sender, RoutedEventArgs e)
    {
        var path = await Main.PickFolder(T("选择游戏目录（.minecraft）"), Cfg.MinecraftDir);
        if (path == null)
            return;
        path = Path.GetFullPath(path);
        Cfg.ExtraDirs ??= [];
        if (string.Equals(path, Path.GetFullPath(Cfg.MinecraftDir), StringComparison.OrdinalIgnoreCase)
            || Cfg.ExtraDirs.Any(d => string.Equals(Path.GetFullPath(d.Path ?? ""), path, StringComparison.OrdinalIgnoreCase)))
        {
            Main.Toast(T("这个目录已经在列表里了"), "info");
            return;
        }
        Cfg.ExtraDirs.Add(new GameDirEntry { Name = Path.GetFileName(path), Path = path });
        Main.SaveSettings();
        RefreshGameDirs();
        Main.LaunchPage.RefreshGameDirs();
    }

    private void OnImportLaunchers(object sender, RoutedEventArgs e) => Main.ShowPage("tools");

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
        var path = await Main.PickFolder(T("选择游戏目录（.minecraft）"), McDirBox.Text);
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
        _ = Main.RunTask(T("检测 Java"), () => Task.Run(() => JavaManager.FindJava([runtime])), javas =>
        {
            FillJava(javas);
            Main.Toast(F("检测到 {0} 个 Java", javas.Count));
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
                selected = F("自定义  —  {0}", Cfg.JavaPath);
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
            ? await Main.PickFile(T("选择 Java 可执行文件"), "Java", "javaw.exe", "java.exe")
            : await Main.PickFile(T("选择 Java 可执行文件"));
        if (path == null)
            return;
        Cfg.JavaPath = path;
        FillJava(_detected);
    }

    private void OnDownloadJdk8(object sender, RoutedEventArgs e) => DownloadJdk(8);
    private void OnDownloadJdk17(object sender, RoutedEventArgs e) => DownloadJdk(17);
    private void OnDownloadJdk21(object sender, RoutedEventArgs e) => DownloadJdk(21);

    private void DownloadJdk(int feature)
    {
        _ = Main.RunTask(F("下载 JDK {0}", feature), async () =>
        {
            var java = await JavaAdoptium.InstallAsync(feature, Main.MakeLauncher().Dl);
            return java;
        }, java =>
        {
            Cfg.JavaPath = java;
            DetectJavaInBackground();
            Main.Toast(F("已安装 JDK {0}", feature));
        });
    }

    private void OnIsolationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || IsolationBox.SelectedIndex < 0)
            return;
        Cfg.IsolationMode = Isolation.Modes[IsolationBox.SelectedIndex].Key;
        Cfg.VersionIsolation = Isolation.Isolates(Cfg.IsolationMode);
    }

    private void OnPriorityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && PriorityBox.SelectedIndex >= 0)
            Cfg.ProcessPriority = LaunchExtras.Priorities[PriorityBox.SelectedIndex].Key;
    }

    private void OnChannelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && ChannelBox.SelectedIndex >= 0)
            Cfg.UpdateChannel = UpdateChannels[ChannelBox.SelectedIndex].Key;
    }

    private void RefreshSpeedLimit() =>
        SpeedLimitText.Text = Cfg.DownloadSpeedLimit <= 0 ? T("不限制") : $"{Cfg.DownloadSpeedLimit} KB/s";

    private void ApplyBgm(string path)
    {
        Cfg.Bgm = path;
        BgmBox.Text = path;
        if (string.IsNullOrEmpty(path))
            LauncherAudio.Stop();
        else
            LauncherAudio.Play(path, true);
    }

    private async void OnBrowseBgm(object sender, RoutedEventArgs e)
    {
        var path = await Main.PickFile(T("选择背景音乐"), T("音频"), LauncherAudio.PickerPatterns);
        if (path != null)
            ApplyBgm(path);
    }

    private void OnClearBgm(object sender, RoutedEventArgs e) => ApplyBgm("");

    private async void OnBrowseSfx(object sender, RoutedEventArgs e)
    {
        var path = await Main.PickFile(T("选择启动音效"), T("音频"), LauncherAudio.PickerPatterns);
        if (path != null)
        {
            Cfg.LaunchSfx = path;
            SfxBox.Text = path;
        }
    }

    private void OnClearSfx(object sender, RoutedEventArgs e)
    {
        Cfg.LaunchSfx = "";
        SfxBox.Text = "";
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

    private async void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LanguageBox.SelectedIndex < 0)
            return;
        Cfg.Language = I18n.Languages[LanguageBox.SelectedIndex].Key;
        Main.SaveSettings();
        if (I18n.Resolve(Cfg.Language) == I18n.Language)
            return;
        // 提示用新选的语言显示，用户可能看不懂当前语言
        var english = I18n.Resolve(Cfg.Language) == "en";
        var choice = await Main.Dialog(english ? "Restart required" : "需要重启",
                                       english ? "The new language takes effect after restarting the launcher."
                                               : "重启启动器后才会切换到新的语言。",
                                       "info", (english ? "Restart now" : "立即重启", "restart", "primary"),
                                       (english ? "Later" : "稍后", null, ""));
        if (choice is "restart")
            Main.RestartLauncher();
    }

    private void OnManageAccounts(object sender, RoutedEventArgs e) => Main.ManageAccounts();

    private async void OnEditTouchLayout(object sender, RoutedEventArgs e)
    {
        if (await Main.ShowDialogAsync(new Dialogs.TouchLayoutDialog(Cfg)) is true)
            UpdateTouchLayoutText();
    }

    private void UpdateTouchLayoutText() =>
        TouchLayoutText.Text = F("正在使用：{0}。可以拖动按键位置，调整大小、透明度，保存多套布局",
                                 Cfg.CurrentControlLayout()?.Name ?? T("默认布局"));

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
        var path = await Main.PickFile(T("选择背景图片"), T("图片"), "*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp", "*.gif");
        if (path != null)
            SetBackground(path);
    }

    private void OnClearBackground(object sender, RoutedEventArgs e) => SetBackground("");

    // ------------------------------------------------------------------ 关于

    private void OnOpenRepo(object sender, RoutedEventArgs e) => Platform.OpenUrl(Updater.RepoUrl);

    private async void OnLicenses(object sender, RoutedEventArgs e)
    {
        var lines = new List<string>
        {
            "Avalonia — MIT",
            "SharpCompress — MIT",
            F("陶瓦联机 Terracotta（{0}）— AGPL-3.0", T("使用时下载的独立程序")),
        };
        if (Platform.IsAndroid)
        {
            lines.AddRange([
                F("Amethyst-Android（{0}）— LGPL-3.0", T("游戏启动层")),
                F("OpenJDK（{0}）— GPL-2.0 with Classpath Exception", T("手机版 Java，来自 AngelAuraMC")),
                F("MobileGlues（{0}）— LGPL-2.1", T("渲染器")),
                "LWJGL — BSD-3-Clause",
                "OpenAL Soft — LGPL-2.0",
                "ByteHook — MIT",
            ]);
        }
        if (Platform.IsIOS)
        {
            lines.AddRange([
                F("Amethyst-iOS（{0}）— GPL-3.0", T("游戏启动层")),
                F("OpenJDK（{0}）— GPL-2.0 with Classpath Exception", T("iOS 版 Java，来自 AngelAuraMC")),
                F("ANGLE（{0}）— BSD-3-Clause", T("渲染器")),
                "gl4es — MIT",
                "MoltenVK — Apache-2.0",
                "LWJGL — BSD-3-Clause",
                "OpenAL Soft — LGPL-2.0",
                "Caciocavallo — GPL-2.0 with Classpath Exception",
                "fishhook — BSD-3-Clause",
            ]);
            if (MobileJit.BuiltInIncluded)
            {
                lines.AddRange([
                    F("StikJIT 1.9.0（{0}）— MPL-2.0", T("内置 JIT，未修改，源码：https://github.com/StikDebug/StikJIT")),
                    F("idevice（{0}）— MIT", MobileJit.GeneratePairingFile != null
                        ? T("随 StikJIT 附带；在本机生成配对文件也用它")
                        : T("随 StikJIT 附带")),
                    F("StikDebug JIT 脚本（{0}）— AGPL-3.0", T("随 StikJIT 附带")),
                ]);
            }
        }
        await Main.Dialog(T("开源许可"),
                          T("MaoX Launcher 以 GPL-3.0 协议开源。") + "\n"
                          + T("MaoX Launcher 使用了以下开源项目，感谢它们的作者：") + "\n\n"
                          + string.Join("\n", lines.Select(l => "• " + l)));
    }

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
