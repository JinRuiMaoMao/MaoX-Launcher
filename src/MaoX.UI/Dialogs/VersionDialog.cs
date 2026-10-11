using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MaoX.Controls;
using MaoX.Core;
using static MaoX.Core.I18n;

namespace MaoX.Dialogs;

/// <summary>版本管理：单独设置、文件夹、存档、重命名 / 复制 / 删除 / 导出整合包。</summary>
public class VersionDialog : DialogView
{
    private static string FollowGlobal => T("跟随全局设置");

    private static readonly (string Text, string Sub)[] Folders =
    [
        ("游戏目录", ""), ("存档", "saves"), ("模组", "mods"), ("资源包", "resourcepacks"), ("光影包", "shaderpacks"),
        ("截图", "screenshots"),
    ];

    private readonly string _version;
    private readonly GameLauncher _gl;
    private JsonObject _settings;
    private readonly bool _isolationInitial;
    private readonly ToggleSwitch _isolation = new();
    private readonly ToggleSwitch _custom = new();
    private readonly StackPanel _customBox = new() { Spacing = 7, Margin = new Thickness(0, 4, 0, 0) };
    private readonly Slider _memory = new() { Minimum = 512, TickFrequency = 256, IsSnapToTickEnabled = true };
    private readonly ComboBox _java = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _jvm = new();
    private readonly StackPanel _worlds = new() { Spacing = 6 };
    private readonly TextBlock _worldCount = new() { Classes = { "accent", "bold" } };
    private readonly Dictionary<string, string> _javaMap = [];

    private static MainView Main => MainView.Current;

    public VersionDialog(string version)
    {
        _version = version;
        AutoFocusText = false;
        _gl = Main.MakeLauncher();
        _settings = _gl.VersionSettings(version);
        DialogWidth = 780;
        var cfg = Main.Cfg;

        var root = new StackPanel();
        root.Children.Add(new TextBlock { Text = version, Classes = { "h2" } });
        var played = PlayTime.Describe(_gl, version);
        root.Children.Add(Text(_gl.DescribeVersion(version) + (played == "" ? "" : "  ·  " + played), "muted"));

        // 版本设置
        var isolation = _settings.BoolOrNull("isolation");
        _isolation.IsChecked = _isolationInitial = isolation ?? cfg.VersionIsolation;
        _custom.IsChecked = _settings.Bool("custom");
        var ram = Platform.TotalMemoryMb();
        _memory.Maximum = Math.Max(2048, ram / 512 * 512);
        _memory.Value = Math.Clamp(_settings.Int("max_memory", cfg.MaxMemory), 512, (int)_memory.Maximum);
        var memText = new TextBlock { Classes = { "small", "accent", "bold" }, Text = $"{(int)_memory.Value} MB" };
        _memory.ValueChanged += (_, _) => memText.Text = $"{(int)_memory.Value} MB";

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
        _jvm.Text = _settings.Str("jvm_args", cfg.JvmArgs);

        var memRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        memRow.Children.Add(Text(T("最大内存"), "small", "muted"));
        Grid.SetColumn(memText, 1);
        memRow.Children.Add(memText);
        _customBox.Children.Add(memRow);
        _customBox.Children.Add(_memory);
        _customBox.Children.Add(Text("Java", "small", "muted"));
        _customBox.Children.Add(_java);
        _customBox.Children.Add(new TextBlock { Text = T("额外 JVM 参数"), Classes = { "small", "muted" }, Margin = new Thickness(0, 4, 0, 0) });
        _customBox.Children.Add(_jvm);
        _customBox.IsVisible = _custom.IsChecked == true;
        _custom.IsCheckedChanged += (_, _) => _customBox.IsVisible = _custom.IsChecked == true;

        var settings = new StackPanel { Spacing = 10 };
        settings.Children.Add(SwitchRow(T("版本隔离"), T("独立的存档、模组与设置"), _isolation));
        settings.Children.Add(SwitchRow(T("单独设置内存与 Java"), null, _custom));
        settings.Children.Add(_customBox);

        // 文件夹
        var folders = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,8,*"),
            RowDefinitions = new RowDefinitions("Auto,8,Auto,8,Auto"),
        };
        for (var i = 0; i < Folders.Length; i++)
        {
            var (text, sub) = Folders[i];
            var button = MakeButton(T(text), icon: "folder", onClick: () => OpenFolder(sub));
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.MinWidth = 0;
            Grid.SetColumn(button, i % 2 * 2);
            Grid.SetRow(button, i / 2 * 2);
            folders.Children.Add(button);
        }

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("3*,14,2*"), Margin = new Thickness(0, 18, 0, 0) };
        top.Children.Add(SubCard(T("版本设置"), "tune", settings));
        var folderCard = SubCard(T("文件夹", "version"), "folder", folders);
        Grid.SetColumn(folderCard, 2);
        top.Children.Add(folderCard);
        root.Children.Add(top);

        // 模组 / 资源包 / 光影包
        var modded = Mods.ModLoaders.Contains(_gl.DetectLoader(version).Loader);
        var files = new VersionFilesCard(GameDir, modded) { Margin = new Thickness(0, 14, 0, 0) };
        _isolation.IsCheckedChanged += (_, _) => files.Reload();
        root.Children.Add(files);

        // 存档
        var worldScroll = new ScrollViewer { Content = _worlds, MaxHeight = 200, Padding = new Thickness(0, 0, 8, 0) };
        var worldCard = SubCard(T("存档"), "game", worldScroll, _worldCount);
        worldCard.Margin = new Thickness(0, 14, 0, 0);
        root.Children.Add(worldCard);

        // 操作
        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 22, 0, 0) };
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var delete = MakeButton(T("删除"), "ghost", "delete", Delete);
        delete.Classes.Add("danger-text");
        left.Children.Add(delete);
        left.Children.Add(MakeButton(T("重命名"), onClick: Rename));
        left.Children.Add(MakeButton(T("复制"), onClick: Duplicate));
        bar.Children.Add(left);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var export = MakeButton(T("导出"), icon: "upload");
        var menu = new MenuFlyout { Placement = PlacementMode.TopEdgeAlignedRight };
        menu.Items.Add(MenuEntry(T("导出整合包（.mrpack）"), Export));
        if (!Platform.IsMobile)
        {
            menu.Items.Add(MenuEntry(T("导出启动脚本"), ExportScript));
            menu.Items.Add(MenuEntry(T("创建桌面快捷方式"), CreateShortcut));
        }
        export.Flyout = menu;
        right.Children.Add(export);
        right.Children.Add(MakeButton(T("完成"), "primary", onClick: () => Close()));
        Grid.SetColumn(right, 2);
        bar.Children.Add(right);
        root.Children.Add(bar);

        Content = new ScrollViewer { Content = root };
        LoadWorlds();
    }

    private static Control SwitchRow(string title, string desc, ToggleSwitch toggle)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title });
        if (desc != null)
            text.Children.Add(Text(desc, "small", "dim"));
        grid.Children.Add(text);
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(toggle);
        return grid;
    }

    public static Border SubCard(string title, string icon, Control content, Control extraHead = null)
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 12) };
        head.Children.Add(new IconLabel { Icon = icon, Text = title, Classes = { "cardhead" } });
        if (extraHead != null)
            head.Children.Add(extraHead);
        var panel = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        panel.Children.Add(head);
        panel.Children.Add(content);
        return new Border
        {
            Background = (IBrush)Application.Current!.FindResource("Bg"),
            BorderBrush = (IBrush)Application.Current.FindResource("CardBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(18, 14, 18, 16),
            Child = panel,
        };
    }

    // ------------------------------------------------------------------ 设置

    private void Save()
    {
        // 重新读取文件，避免覆盖对话框打开期间别处写入的字段（如游戏时长）
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

    public override void OnClosed()
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
            _worlds.Children.Add(new TextBlock { Text = T("还没有存档，进入游戏创建一个世界后会显示在这里"), Classes = { "muted" }, Margin = new Thickness(0, 6) });
            return;
        }
        foreach (var world in worlds)
            _worlds.Children.Add(WorldRow(world));
    }

    private Control WorldRow(WorldInfo world)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
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
        text.Children.Add(Text(string.Join("  ·  ", meta), "small", "dim"));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var open = MakeButton(T("打开"), "ghost", "folder", () => Platform.OpenPath(world.Path));
        open.MinWidth = 0;
        Grid.SetColumn(open, 2);
        grid.Children.Add(open);
        var backup = MakeButton(T("备份"), "ghost", "archive", () => Backup(world));
        backup.MinWidth = 0;
        Grid.SetColumn(backup, 3);
        grid.Children.Add(backup);
        return new Border { Classes = { "row" }, Padding = new Thickness(10, 8), Child = grid };
    }

    private void Backup(WorldInfo world)
    {
        var gameDir = _gl.GameDirFor(_version);
        _ = Main.RunTask(F("备份存档 {0}", world.Name), () => Instance.BackupWorldAsync(gameDir, world, Main.Progress),
                         path => Main.Toast(T("已备份到 backups") + Path.DirectorySeparatorChar + Path.GetFileName(path)));
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
        Close();
        Main.Toast(F("已重命名为 {0}", name));
        Main.RefreshInstalled(name);
    }

    private async void Duplicate()
    {
        if (await Main.ShowDialogAsync(new InputDialog(T("复制版本"), T("新版本的名称（会复制版本文件夹中的全部内容，包括存档和模组）："),
                                                       _version + " 副本", Validator(""), T("复制", "version"))) is not string name)
            return;
        Close();
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
        Close();
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
    }

    private void Export()
    {
        Close();
        _ = Main.ShowDialogAsync(new ModpackExportDialog(_version));
    }

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
                Avalonia.Threading.Dispatcher.UIThread.Post(Main.AccountsChanged);
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
