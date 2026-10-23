using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MaoX.Controls;
using MaoX.Core;
using MaoX.Dialogs;
using static MaoX.Core.I18n;

namespace MaoX.Pages;

/// <summary>版本设置整页（像 PCL）：左上返回，分区查看设置、文件、存档、日志、截图、推荐和检查。</summary>
public class VersionSettingsPage : UserControl, IPage
{
    private static string FollowGlobal => T("跟随全局设置");

    private static readonly (string Text, string Sub)[] Folders =
    [
        ("游戏目录", ""), ("存档", "saves"), ("模组", "mods"), ("资源包", "resourcepacks"), ("光影包", "shaderpacks"),
        ("截图", "screenshots"),
    ];

    private static readonly (string Key, string Text)[] Tabs =
    [
        ("settings", "设置"), ("files", "文件"), ("worlds", "存档"), ("logs", "日志"),
        ("shots", "截图"), ("recommend", "推荐"), ("check", "检查"),
    ];

    private string _version;
    private GameLauncher _gl;
    private JsonObject _settings;
    private bool _isolationInitial;
    private ToggleSwitch _isolation;
    private ToggleSwitch _custom;
    private StackPanel _customBox;
    private Slider _memory;
    private ComboBox _java;
    private TextBox _jvm;
    private readonly Dictionary<string, string> _javaMap = [];
    private readonly Dictionary<string, ToggleButton> _chips = [];
    private readonly Dictionary<string, Control> _panels = [];
    private string _tab = "settings";
    private bool _tabReady;
    private DateTime _pillUntil;
    private Border _pill;
    private Panel _tabHost;
    private TextBlock _title, _subtitle;
    private VersionFilesCard _files;
    private StackPanel _worlds, _logs, _shots, _recommend, _check;
    private TextBlock _worldCount;

    private static MainView Main => MainView.Current;

    public VersionSettingsPage()
    {
        var back = DialogView.MakeButton(T("返回"), "ghost", "arrow-left", Back);
        back.MinWidth = 0;
        back.HorizontalContentAlignment = HorizontalAlignment.Left;
        _title = new TextBlock { Classes = { "h1" }, TextTrimming = TextTrimming.CharacterEllipsis };
        _subtitle = new TextBlock { Classes = { "muted", "pagesub" }, TextTrimming = TextTrimming.CharacterEllipsis };
        var headText = new StackPanel { Spacing = 2, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        headText.Children.Add(_title);
        headText.Children.Add(_subtitle);
        var head = new StackPanel
        {
            Orientation = Orientation.Horizontal, Classes = { "pagehead" },
            Children = { back, headText },
        };

        _pill = new Border { Classes = { "tabpill" }, Opacity = 0 };
        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var (key, text) in Tabs)
        {
            var chip = new ToggleButton { Classes = { "chip", "tab" }, Content = T(text), Tag = key };
            chip.Click += (_, _) => SetTab(key);
            _chips[key] = chip;
            chips.Children.Add(chip);
        }
        var tabBar = new Panel { Children = { _pill, chips }, Margin = new Thickness(0, 0, 0, 14) };
        PageTabs.PreparePill(_pill);
        LayoutUpdated += (_, _) =>
        {
            if (DateTime.UtcNow >= _pillUntil)
                PageTabs.MovePill(_pill, Chip(), false);
        };

        _tabHost = new Panel();
        var scroll = new ScrollViewer { Content = _tabHost, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var root = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(tabBar, Dock.Top);
        root.Children.Add(head);
        root.Children.Add(tabBar);
        root.Children.Add(scroll);
        Content = root;
    }

    public void Open(string version)
    {
        _version = version;
        Rebuild();
    }

    public void OnShow()
    {
        if (string.IsNullOrEmpty(_version))
            return;
        LoadWorlds();
        if (_tab is "logs" or "shots" or "recommend" or "check")
            RefreshTab();
    }

    public void Leave()
    {
        try
        {
            Save();
        }
        catch (Exception e)
        {
            Main.Toast(F("保存版本设置失败：{0}", e.Message), "error");
        }
        Main.ResourcesPage.Invalidate();
    }

    private void Back()
    {
        Leave();
        Main.ShowPage("launch");
    }

    private ToggleButton Chip() => _chips.GetValueOrDefault(_tab) ?? _chips["settings"];

    private void SetTab(string key)
    {
        if (_tab == key && _tabReady)
        {
            UpdateChips();
            return;
        }
        var from = _tab;
        _tab = key;
        UpdateChips();
        if (!_panels.TryGetValue(key, out var panel))
            return;
        if (_tabReady)
            PageTabs.Swap(panel, _panels.Values.ToArray(), PageTabs.Direction(Tabs.Select(t => t.Key).ToList(), from, key));
        else
            PageTabs.ShowNow(panel, _panels.Values.ToArray());
        _tabReady = true;
        _pillUntil = DateTime.UtcNow + PageTabs.Duration;
        PageTabs.MovePill(_pill, Chip(), true);
        RefreshTab();
    }

    private void UpdateChips()
    {
        foreach (var (key, chip) in _chips)
            chip.IsChecked = key == _tab;
    }

    private void RefreshTab()
    {
        if (_tab == "logs")
            LoadLogs();
        else if (_tab == "shots")
            LoadShots();
        else if (_tab == "recommend")
            LoadRecommend();
        else if (_tab == "check")
            LoadCheck();
    }

    // ------------------------------------------------------------------ 构建

    private void Rebuild()
    {
        _javaMap.Clear();
        _gl = Main.MakeLauncher();
        _settings = _gl.VersionSettings(_version);
        var cfg = Main.Cfg;
        _title.Text = _version;
        var played = PlayTime.Describe(_gl, _version);
        _subtitle.Text = _gl.DescribeVersion(_version) + (played == "" ? "" : "  ·  " + played);

        var isolation = _settings.BoolOrNull("isolation");
        _isolation = new ToggleSwitch { IsChecked = _isolationInitial = isolation ?? cfg.VersionIsolation };
        _custom = new ToggleSwitch { IsChecked = _settings.Bool("custom") };
        _memory = new Slider { Minimum = 512, TickFrequency = 256, IsSnapToTickEnabled = true };
        var ram = Platform.TotalMemoryMb();
        _memory.Maximum = Math.Max(2048, ram / 512 * 512);
        _memory.Value = Math.Clamp(_settings.Int("max_memory", cfg.MaxMemory), 512, (int)_memory.Maximum);
        var memText = new TextBlock { Classes = { "small", "accent", "bold" }, Text = $"{(int)_memory.Value} MB" };
        _memory.ValueChanged += (_, _) => memText.Text = $"{(int)_memory.Value} MB";
        _java = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        _jvm = new TextBox { Text = _settings.Str("jvm_args", cfg.JvmArgs) };
        _customBox = new StackPanel { Spacing = 7, Margin = new Thickness(0, 4, 0, 0) };

        var javaItems = new List<string> { FollowGlobal };
        foreach (var java in Main.SettingsPage.DetectedJava)
        {
            var label = $"Java {java.Major}  —  {java.Path}";
            _javaMap[label] = java.Path;
            javaItems.Add(label);
        }
        var current = _settings.Str("java_path");
        var selected = FollowGlobal;
        if (!string.IsNullOrEmpty(current))
        {
            selected = _javaMap.FirstOrDefault(p => string.Equals(p.Value, current, StringComparison.OrdinalIgnoreCase)).Key;
            if (selected == null)
            {
                selected = F("自定义  —  {0}", current);
                _javaMap[selected] = current;
                javaItems.Add(selected);
            }
        }
        _java.ItemsSource = javaItems;
        _java.SelectedItem = selected;

        var memRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        memRow.Children.Add(DialogView.Text(T("最大内存"), "small", "muted"));
        Grid.SetColumn(memText, 1);
        memRow.Children.Add(memText);
        _customBox.Children.Add(memRow);
        _customBox.Children.Add(_memory);
        _customBox.Children.Add(DialogView.Text("Java", "small", "muted"));
        _customBox.Children.Add(_java);
        _customBox.Children.Add(new TextBlock { Text = T("额外 JVM 参数"), Classes = { "small", "muted" }, Margin = new Thickness(0, 4, 0, 0) });
        _customBox.Children.Add(_jvm);
        _customBox.IsVisible = _custom.IsChecked == true;
        _custom.IsCheckedChanged += (_, _) => _customBox.IsVisible = _custom.IsChecked == true;

        var settings = new StackPanel { Spacing = 10 };
        settings.Children.Add(SwitchRow(T("版本隔离"), T("独立的存档、模组与设置"), _isolation));
        settings.Children.Add(SwitchRow(T("单独设置内存与 Java"),
            cfg.AutoMemoryOn ? T("关闭后不再使用全局的自动分配内存") : null, _custom));
        settings.Children.Add(_customBox);

        var folders = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,8,*"),
            RowDefinitions = new RowDefinitions("Auto,8,Auto,8,Auto"),
        };
        for (var i = 0; i < Folders.Length; i++)
        {
            var (text, sub) = Folders[i];
            var button = DialogView.MakeButton(T(text), icon: "folder", onClick: () => OpenFolder(sub));
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.MinWidth = 0;
            Grid.SetColumn(button, i % 2 * 2);
            Grid.SetRow(button, i / 2 * 2);
            folders.Children.Add(button);
        }

        var settingsBody = new StackPanel { Spacing = 14 };
        settingsBody.Children.Add(VersionDialog.SubCard(T("版本设置"), "tune", settings));
        settingsBody.Children.Add(VersionDialog.SubCard(T("文件夹", "version"), "folder", folders));
        settingsBody.Children.Add(ActionsCard());

        var modded = Mods.ModLoaders.Contains(_gl.DetectLoader(_version).Loader);
        _files = new VersionFilesCard(GameDir, modded);
        _isolation.IsCheckedChanged += (_, _) => _files.Reload();

        _worldCount = new TextBlock { Classes = { "accent", "bold" } };
        _worlds = new StackPanel { Spacing = 6 };
        var worldCard = VersionDialog.SubCard(T("存档"), "game", _worlds, _worldCount);

        _logs = new StackPanel { Spacing = 8 };
        _shots = new StackPanel { Spacing = 8 };
        _recommend = new StackPanel { Spacing = 8 };
        _check = new StackPanel { Spacing = 8 };

        _panels.Clear();
        _tabHost.Children.Clear();
        _tabReady = false;
        AddPanel("settings", settingsBody);
        AddPanel("files", _files);
        AddPanel("worlds", worldCard);
        AddPanel("logs", _logs);
        AddPanel("shots", _shots);
        AddPanel("recommend", _recommend);
        AddPanel("check", _check);
        LoadWorlds();
        SetTab(_tab is "settings" or "files" or "worlds" or "logs" or "shots" or "recommend" or "check" ? _tab : "settings");
    }

    private void AddPanel(string key, Control content)
    {
        content.IsVisible = false;
        _panels[key] = content;
        _tabHost.Children.Add(content);
    }

    private Control ActionsCard()
    {
        var left = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
        var delete = DialogView.MakeButton(T("删除"), "ghost", "delete", Delete);
        delete.Classes.Add("danger-text");
        left.Children.Add(delete);
        left.Children.Add(DialogView.MakeButton(T("重命名"), onClick: Rename));
        left.Children.Add(DialogView.MakeButton(T("复制"), onClick: Duplicate));
        var export = DialogView.MakeButton(T("导出"), icon: "upload");
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        menu.Items.Add(MenuEntry(T("导出整合包（.mrpack）"), Export));
        if (!Platform.IsMobile)
        {
            menu.Items.Add(MenuEntry(T("导出启动脚本"), ExportScript));
            menu.Items.Add(MenuEntry(T("创建桌面快捷方式"), CreateShortcut));
        }
        export.Flyout = menu;
        left.Children.Add(export);
        return VersionDialog.SubCard(T("版本操作"), "edit", left);
    }

    private static Control SwitchRow(string title, string desc, ToggleSwitch toggle)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title });
        if (desc != null)
            text.Children.Add(DialogView.Text(desc, "small", "dim"));
        grid.Children.Add(text);
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(toggle);
        return grid;
    }

    // ------------------------------------------------------------------ 设置

    private void Save()
    {
        if (string.IsNullOrEmpty(_version) || _isolation == null)
            return;
        var latest = _gl.VersionSettings(_version);
        var s = (JsonObject)latest.DeepClone();
        if (_isolation.IsChecked != _isolationInitial || s.ContainsKey("isolation"))
            s["isolation"] = _isolation.IsChecked == true;
        s["custom"] = _custom.IsChecked == true;
        if (_custom.IsChecked == true)
        {
            var java = _java.SelectedItem as string ?? FollowGlobal;
            s["max_memory"] = (int)_memory.Value;
            s["java_path"] = java == FollowGlobal ? "" : _javaMap.GetValueOrDefault(java, "");
            s["jvm_args"] = (_jvm.Text ?? "").Trim();
        }
        if (JsonNode.DeepEquals(s, latest) || !Directory.Exists(_gl.VersionDir(_version)))
            return;
        _gl.SaveVersionSettings(_version, s);
        _settings = s;
    }

    private string GameDir()
    {
        Save();
        return new GameLauncher(Main.Cfg).GameDirFor(_version);
    }

    private void OpenFolder(string sub)
    {
        var dir = GameDir();
        var path = sub.Length > 0 ? Path.Combine(dir, sub) : dir;
        Directory.CreateDirectory(path);
        Platform.OpenPath(path);
    }

    // ------------------------------------------------------------------ 存档

    private async void LoadWorlds()
    {
        if (_worlds == null || string.IsNullOrEmpty(_version))
            return;
        var gameDir = _gl.GameDirFor(_version);
        List<WorldInfo> worlds;
        try
        {
            worlds = await Task.Run(() => Instance.ListWorlds(gameDir));
        }
        catch (Exception)
        {
            worlds = [];
        }
        _worlds.Children.Clear();
        _worldCount.Text = worlds.Count > 0 ? worlds.Count.ToString() : "";
        if (worlds.Count == 0)
        {
            _worlds.Children.Add(new TextBlock
            {
                Text = T("还没有存档，进入游戏创建一个世界后会显示在这里"), Classes = { "muted" },
                Margin = new Thickness(0, 6),
            });
            return;
        }
        foreach (var world in worlds)
            _worlds.Children.Add(WorldRow(world));
    }

    private Control WorldRow(WorldInfo world)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto") };
        Control icon = null;
        if (!string.IsNullOrEmpty(world.Icon) && File.Exists(world.Icon))
        {
            try
            {
                using var stream = File.OpenRead(world.Icon);
                icon = new Border
                {
                    Width = 38, Height = 38, CornerRadius = new CornerRadius(8), ClipToBounds = true,
                    Child = new Image { Source = new Bitmap(stream), Stretch = Stretch.UniformToFill },
                };
            }
            catch (Exception)
            {
                icon = null;
            }
        }
        icon ??= new Border
        {
            Width = 38, Height = 38, CornerRadius = new CornerRadius(8),
            Background = (IBrush)Application.Current!.FindResource("AccentDim"),
            Child = new Icon { Kind = "game", Size = 20, Foreground = (IBrush)Application.Current.FindResource("Accent") },
        };
        grid.Children.Add(icon);
        var text = new StackPanel { Spacing = 2, Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = world.Name, FontWeight = FontWeight.SemiBold });
        var meta = new[] { world.Hardcore ? T("极限") : T(world.Mode), world.Version, world.LastPlayed > DateTime.MinValue ? world.LastPlayed.ToString("yyyy-MM-dd HH:mm") : "" }
                   .Where(m => !string.IsNullOrEmpty(m));
        text.Children.Add(DialogView.Text(string.Join("  ·  ", meta), "small", "dim"));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var open = DialogView.MakeButton(T("打开"), "ghost", "folder", () => Platform.OpenPath(world.Path));
        open.MinWidth = 0;
        Grid.SetColumn(open, 2);
        grid.Children.Add(open);
        var backup = DialogView.MakeButton(T("备份"), "ghost", "archive", () => Backup(world));
        backup.MinWidth = 0;
        Grid.SetColumn(backup, 3);
        grid.Children.Add(backup);
        var restore = DialogView.MakeButton(T("还原"), "ghost", "download", () => Restore(world));
        restore.MinWidth = 0;
        Grid.SetColumn(restore, 4);
        grid.Children.Add(restore);
        return new Border { Classes = { "row" }, Padding = new Thickness(10, 8), Child = grid };
    }

    private void Backup(WorldInfo world)
    {
        var gameDir = _gl.GameDirFor(_version);
        _ = Main.RunTask(F("备份存档 {0}", world.Name), () => Instance.BackupWorldAsync(gameDir, world, Main.Progress),
                         path => Main.Toast(T("已备份到 backups") + Path.DirectorySeparatorChar + Path.GetFileName(path)));
    }

    private async void Restore(WorldInfo world)
    {
        if (!CheckNotRunning())
            return;
        var gameDir = _gl.GameDirFor(_version);
        var backups = await Task.Run(() => Instance.ListBackups(gameDir, world.Folder));
        if (await Main.ShowDialogAsync(new WorldBackupsDialog(world, backups)) is not WorldBackup backup)
            return;
        if (!await Main.Confirm(T("还原存档"),
                                F("用 {0} 的备份替换存档 {1} 吗？现在的存档会先自动备份。", backup.Time.ToString("yyyy-MM-dd HH:mm"), world.Name),
                                T("还原"), "warn", "danger"))
            return;
        await Main.RunTask(F("还原存档 {0}", world.Name), () => Instance.RestoreWorldAsync(gameDir, world, backup, Main.Progress),
                           () =>
                           {
                               Main.Toast(F("已还原存档 {0}", world.Name));
                               LoadWorlds();
                           });
    }

    // ------------------------------------------------------------------ 日志 / 截图 / 推荐 / 检查

    private void LoadLogs()
    {
        _logs.Children.Clear();
        var dir = _gl.GameDirFor(_version);
        var latest = Path.Combine(dir, "logs", "latest.log");
        var crashes = Directory.Exists(Path.Combine(dir, "crash-reports"))
            ? Directory.GetFiles(Path.Combine(dir, "crash-reports"), "*.txt").OrderByDescending(File.GetLastWriteTimeUtc).Take(12).ToList()
            : [];
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 8) };
        bar.Children.Add(DialogView.MakeButton(T("打开日志文件夹"), "ghost", "folder", () =>
        {
            var folder = Path.Combine(dir, "logs");
            Directory.CreateDirectory(folder);
            Platform.OpenPath(folder);
        }));
        _logs.Children.Add(bar);
        if (File.Exists(latest))
            _logs.Children.Add(FileRow(T("最新日志"), latest, File.GetLastWriteTime(latest)));
        foreach (var crash in crashes)
            _logs.Children.Add(FileRow(Path.GetFileName(crash), crash, File.GetLastWriteTime(crash)));
        if (!File.Exists(latest) && crashes.Count == 0)
            _logs.Children.Add(new TextBlock { Text = T("还没有日志。启动一次游戏后，最新日志和崩溃报告会显示在这里。"), Classes = { "muted" } });
    }

    private void LoadShots()
    {
        _shots.Children.Clear();
        var folder = Path.Combine(_gl.GameDirFor(_version), "screenshots");
        var files = Directory.Exists(folder)
            ? Directory.GetFiles(folder, "*.png").OrderByDescending(File.GetLastWriteTimeUtc).Take(40).ToList()
            : [];
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        bar.Children.Add(DialogView.MakeButton(T("打开截图文件夹"), "ghost", "folder", () =>
        {
            Directory.CreateDirectory(folder);
            Platform.OpenPath(folder);
        }));
        _shots.Children.Add(bar);
        if (files.Count == 0)
        {
            _shots.Children.Add(new TextBlock { Text = T("还没有截图。在游戏里按 F2 就会出现在这里。"), Classes = { "muted" }, Margin = new Thickness(0, 8, 0, 0) });
            return;
        }
        var wrap = new WrapPanel { ItemSpacing = 10, LineSpacing = 10, Margin = new Thickness(0, 10, 0, 0) };
        foreach (var file in files)
        {
            try
            {
                using var stream = File.OpenRead(file);
                var image = new Image { Source = Bitmap.DecodeToWidth(stream, 280), Stretch = Stretch.UniformToFill };
                var card = new Border
                {
                    Width = 160, Height = 90, CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = image,
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                };
                var path = file;
                card.Tapped += (_, _) => Platform.OpenPath(path);
                wrap.Children.Add(card);
            }
            catch (Exception)
            {
            }
        }
        _shots.Children.Add(wrap);
    }

    private async void LoadRecommend()
    {
        _recommend.Children.Clear();
        var info = _gl.DetectLoader(_version);
        if (!Mods.ModLoaders.Contains(info.Loader))
        {
            _recommend.Children.Add(new TextBlock { Text = T("原版没有模组。安装 Fabric / Quilt / Forge 后，这里会推荐常用优化和实用模组。"), Classes = { "muted" } });
            return;
        }
        _recommend.Children.Add(new TextBlock { Text = T("按当前加载器推荐的常用模组，点安装会按这个版本筛选。"), Classes = { "muted" }, Margin = new Thickness(0, 0, 0, 8) });
        var modsDir = Path.Combine(_gl.GameDirFor(_version), "mods");
        var installed = await Task.Run(() => Mods.ListLocalMods(modsDir));
        var names = installed.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var client = new ModrinthClient(_gl.Dl);
        foreach (var (slug, name, why) in Recommended.For(info.Loader))
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock { Text = name, FontWeight = FontWeight.SemiBold });
            text.Children.Add(DialogView.Text(why, "small", "dim"));
            row.Children.Add(text);
            var have = names.Contains(name);
            var button = DialogView.MakeButton(have ? T("已安装") : T("安装"), have ? "ghost" : "primary", have ? "check" : "download");
            button.IsEnabled = !have;
            var project = slug;
            var title = name;
            button.Click += (_, _) => InstallRecommended(client, project, title, info, modsDir, button);
            Grid.SetColumn(button, 1);
            row.Children.Add(button);
            _recommend.Children.Add(new Border { Classes = { "row" }, Padding = new Thickness(14, 10), Child = row });
        }
    }

    private async void InstallRecommended(ModrinthClient client, string slug, string title, LoaderInfo info, string modsDir, Button button)
    {
        button.IsEnabled = false;
        button.Content = new IconLabel { Text = T("安装中…") };
        try
        {
            await Task.Run(() => client.InstallAsync(slug, info.Game, info.Loader, modsDir, new HashSet<string>(), Main.Log));
            button.Content = new IconLabel { Icon = "check", Text = T("已安装") };
            Main.Toast(F("已安装 {0}", title));
        }
        catch (Exception e)
        {
            button.IsEnabled = true;
            button.Content = new IconLabel { Icon = "download", Text = T("安装") };
            await Main.Dialog(F("安装 {0} 失败", title), MainView.ErrorText(e), "error");
        }
    }

    private async void LoadCheck()
    {
        _check.Children.Clear();
        _check.Children.Add(new TextBlock { Text = T("正在检查…"), Classes = { "muted" } });
        var version = _version;
        var gl = _gl;
        List<CheckItem> items;
        List<(string Name, List<string> Missing)> deps;
        try
        {
            (items, deps) = await Task.Run(() =>
            {
                var checks = LaunchCheck.Run(gl, version, Main.CurrentAccount);
                var missing = new List<(string, List<string>)>();
                var mods = Mods.ListLocalMods(Path.Combine(gl.GameDirFor(version), "mods"));
                var ids = mods.Select(m => Path.GetFileNameWithoutExtension(m.Filename).ToLowerInvariant()).ToHashSet();
                foreach (var mod in mods.Where(m => m.Enabled))
                {
                    var need = Mods.ReadDependencies(mod.Path);
                    var lost = need.Where(id => !ids.Contains(id.Replace('_', '-')) && !ids.Any(x => x.Contains(id, StringComparison.OrdinalIgnoreCase))).ToList();
                    if (lost.Count > 0)
                        missing.Add((mod.Name, lost));
                }
                return (checks, missing);
            });
        }
        catch (Exception e)
        {
            _check.Children.Clear();
            _check.Children.Add(new TextBlock { Text = F("检查失败：{0}", e.Message), Classes = { "wrap" } });
            return;
        }
        if (version != _version)
            return;
        _check.Children.Clear();
        foreach (var item in items)
            _check.Children.Add(StatusRow(item.Title, item.Detail, item.Kind));
        _check.Children.Add(new TextBlock { Text = T("模组前置"), Classes = { "h3" }, Margin = new Thickness(0, 16, 0, 6) });
        if (deps.Count == 0)
            _check.Children.Add(StatusRow(T("前置完整"), T("已安装模组声明的必需前置都能对上"), "ok"));
        else
        {
            foreach (var (name, lost) in deps)
                _check.Children.Add(StatusRow(name, T("缺少：") + string.Join(T("、"), lost), "warn"));
        }
        var target = new TextBox { Watermark = T("目标 Minecraft 版本，例如 1.21.1"), Width = 220 };
        var check = DialogView.MakeButton(T("检查升级兼容"), onClick: () => CheckUpgrade(target.Text));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 16, 0, 0) };
        row.Children.Add(target);
        row.Children.Add(check);
        _check.Children.Add(new TextBlock { Text = T("升级兼容"), Classes = { "h3" }, Margin = new Thickness(0, 8, 0, 6) });
        _check.Children.Add(new TextBlock { Text = T("看看当前模组有没有适合更新游戏版本的文件。"), Classes = { "muted" }, Margin = new Thickness(0, 0, 0, 8) });
        _check.Children.Add(row);
    }

    private async void CheckUpgrade(string target)
    {
        target = (target ?? "").Trim();
        if (string.IsNullOrEmpty(target))
        {
            Main.Toast(T("请先填写目标 Minecraft 版本"), "warn");
            return;
        }
        var info = _gl.DetectLoader(_version);
        var mods = Mods.ListLocalMods(Path.Combine(_gl.GameDirFor(_version), "mods"));
        var client = new ModrinthClient(_gl.Dl);
        await Main.RunTask(T("检查升级兼容"), async () =>
        {
            var ok = 0;
            var miss = new List<string>();
            foreach (var mod in mods.Where(m => m.Enabled).Take(40))
            {
                try
                {
                    var result = await client.SearchAsync(mod.Name, target, info.Loader, 0, 3, "relevance", "mod");
                    if (result.Hits.Any(h => h.Title.Equals(mod.Name, StringComparison.OrdinalIgnoreCase)))
                        ok++;
                    else
                        miss.Add(mod.Name);
                }
                catch (Exception)
                {
                    miss.Add(mod.Name);
                }
            }
            return (ok, miss);
        }, result =>
        {
            var (ok, miss) = result;
            Main.Dialog(T("升级兼容"),
                        miss.Count == 0
                            ? F("抽查的 {0} 个模组在 {1} 上都能找到对应文件。", ok, target)
                            : F("{0} 个模组可能有 {1} 的版本，这些可能还没有：\n{2}", ok, target, string.Join(T("、"), miss.Take(20))),
                        miss.Count == 0 ? "info" : "warn");
        });
    }

    private static Control StatusRow(string title, string detail, string kind)
    {
        var color = kind switch { "error" => "Error", "warn" => "Warn", _ => "Success" };
        var icon = kind switch { "error" => "error", "warn" => "warn", _ => "success" };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Children.Add(new Icon { Kind = icon, Size = 18, Foreground = (IBrush)Application.Current!.FindResource(color), Margin = new Thickness(0, 0, 12, 0) });
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold });
        text.Children.Add(DialogView.Text(detail, "small", "dim"));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return new Border { Classes = { "row" }, Padding = new Thickness(14, 10), Child = grid };
    }

    private Control FileRow(string title, string path, DateTime time)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold });
        text.Children.Add(DialogView.Text(time.ToString("yyyy-MM-dd HH:mm"), "small", "dim"));
        grid.Children.Add(text);
        var open = DialogView.MakeButton(T("打开"), "ghost", "file", () => Platform.OpenPath(path));
        open.MinWidth = 0;
        Grid.SetColumn(open, 1);
        grid.Children.Add(open);
        var copy = DialogView.MakeButton(T("复制"), "ghost", "copy", async () =>
        {
            try
            {
                await TopLevel.GetTopLevel(this)!.Clipboard!.SetTextAsync(await File.ReadAllTextAsync(path));
                Main.Toast(T("已复制到剪贴板"));
            }
            catch (Exception e)
            {
                Main.Toast(e.Message, "error");
            }
        });
        copy.MinWidth = 0;
        Grid.SetColumn(copy, 2);
        grid.Children.Add(copy);
        return new Border { Classes = { "row" }, Padding = new Thickness(14, 10), Child = grid };
    }

    // ------------------------------------------------------------------ 版本操作

    private bool CheckNotRunning()
    {
        if (Main.RunningVersion != _version)
            return true;
        _ = Main.Dialog(T("游戏正在运行"), F("请先关闭正在运行的 {0}。", _version), "warn");
        return false;
    }

    private Func<string, string> Validator(string current) => text =>
    {
        if (text == current)
            return T("名称没有变化");
        try
        {
            Instance.CheckName(_gl, text);
            return null;
        }
        catch (ArgumentException e)
        {
            return e.Message;
        }
    };

    private async void Rename()
    {
        if (!CheckNotRunning())
            return;
        if (await Main.ShowDialogAsync(new InputDialog(T("重命名版本"), T("新的版本名称："), _version, Validator(_version), T("重命名"))) is not string name)
            return;
        Save();
        try
        {
            name = Instance.Rename(_gl, _version, name);
        }
        catch (Exception e)
        {
            await Main.Dialog(T("重命名失败"), F("无法重命名（文件可能正被占用）：{0}", e.Message), "error");
            return;
        }
        Main.Toast(F("已重命名为 {0}", name));
        Main.RefreshInstalled(name);
        Open(name);
    }

    private async void Duplicate()
    {
        if (await Main.ShowDialogAsync(new InputDialog(T("复制版本"), T("新版本的名称（会复制版本文件夹中的全部内容，包括存档和模组）："),
                                                       _version + " 副本", Validator(""), T("复制", "version"))) is not string name)
            return;
        await Main.RunTask(T("复制版本"), () => Instance.DuplicateAsync(_gl, _version, name, Main.Progress), created =>
        {
            Main.RefreshInstalled(created);
            Main.Toast(F("已复制为 {0}", created));
        });
    }

    private async void Delete()
    {
        if (!CheckNotRunning())
            return;
        var children = Instance.Dependents(_gl, _version);
        var message = F("确定要删除 {0} 吗？版本文件夹会被永久删除", _version);
        if (Path.GetFullPath(_gl.GameDirFor(_version)) == Path.GetFullPath(_gl.VersionDir(_version)))
            message += T("，其中的存档和模组也会一起删除");
        message += T("。");
        if (children.Count > 0)
            message += T("\n\n以下版本依赖它，删除后它们会在启动时自动重新下载所需文件：\n") + string.Join(T("、"), children);
        if (!await Main.Confirm(T("删除版本"), message, T("删除"), "warn", "danger"))
            return;
        try
        {
            await Instance.DeleteAsync(_gl, _version);
        }
        catch (Exception e)
        {
            await Main.Dialog(T("删除失败"), F("部分文件无法删除（可能正被占用）：{0}", e.Message), "error");
        }
        Main.RefreshInstalled();
        Main.Toast(F("已删除 {0}", _version));
        Back();
    }

    private void Export() => _ = Main.ShowDialogAsync(new ModpackExportDialog(_version));

    private static MenuItem MenuEntry(string text, Action onClick)
    {
        var item = new MenuItem { Header = text };
        item.Click += (_, _) => onClick();
        return item;
    }

    private async void ExportScript()
    {
        var account = Main.CurrentAccount;
        if (account == null)
        {
            Main.Toast(T("请先添加一个账号"), "warn");
            return;
        }
        if (account.Type != "offline"
            && !await Main.Confirm(T("导出启动脚本"),
                                   T("脚本里会包含当前账号的登录令牌，请不要发给别人。令牌过期后（正版账号大约 1 天）需要重新导出。"),
                                   T("继续导出")))
            return;
        var ext = Platform.IsWindows ? "bat" : Platform.IsMac ? "command" : "sh";
        var path = await Main.SaveFile(T("导出启动脚本"), $"启动 {_version}.{ext}", T("启动脚本"), "*." + ext);
        if (path == null)
            return;
        var cfg = Main.Cfg.Clone();
        var launcher = Main.MakeLauncher();
        await Main.RunTask(T("导出启动脚本"), async () =>
        {
            var (auth, changed) = await Accounts.PrepareLaunchAsync(account, cfg, launcher.Dl, AppPaths.ToolsDir, Main.Log,
                                                                    localSkins: false);
            if (changed)
                Dispatcher.UIThread.Post(Main.AccountsChanged);
            var (command, gameDir) = await launcher.PrepareCommandAsync(_version, null, auth);
            Shortcuts.WriteLaunchScript(path, command, gameDir, _version);
        }, () =>
        {
            Main.Toast(T("启动脚本已导出"));
            Platform.RevealFile(path);
        });
    }

    private async void CreateShortcut()
    {
        try
        {
            await Task.Run(() => Shortcuts.CreateDesktopShortcut(_version));
            Main.Toast(T("已在桌面创建快捷方式，双击即可直接启动这个版本"));
        }
        catch (Exception e)
        {
            await Main.Dialog(T("创建快捷方式失败"), MainView.ErrorText(e), "error");
        }
    }
}
