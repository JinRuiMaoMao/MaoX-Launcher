using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MaoX.Controls;
using MaoX.Core;
using MaoX.Dialogs;
using static MaoX.Core.I18n;

namespace MaoX.Pages;

/// <summary>工具页：导入其他启动器、清理磁盘、下载历史、本地服务器、实验室和公告。</summary>
public class ToolsPage : UserControl, IPage
{
    private readonly StackPanel _body = new() { Spacing = 16 };
    private readonly StackPanel _import = new() { Spacing = 8 };
    private readonly StackPanel _clean = new() { Spacing = 8 };
    private readonly StackPanel _history = new() { Spacing = 6 };
    private readonly TextBox _seed = new() { Watermark = T("种子，可留空"), Width = 220 };
    private readonly TextBox _from = new() { Width = 110, Watermark = "#RRGGBB" };
    private readonly TextBox _to = new() { Width = 110, Watermark = "#00D9FF" };
    private readonly TextBlock _gradient = new() { Classes = { "muted", "wrap" } };
    private List<CleanItem> _cleanItems = [];
    private readonly HashSet<string> _cleanPick = [];

    private static MainView Main => MainView.Current;
    private static LauncherConfig Cfg => Main.Cfg;

    public ToolsPage()
    {
        var head = new StackPanel { Classes = { "pagehead" }, Spacing = 2 };
        head.Children.Add(new TextBlock { Text = T("工具"), Classes = { "h1" } });
        head.Children.Add(new TextBlock { Text = T("导入、清理、下载历史和一些实用小工具"), Classes = { "muted", "pagesub" } });

        _body.Children.Add(VersionDialog.SubCard(T("从其他启动器导入"), "download", ImportCard()));
        _body.Children.Add(VersionDialog.SubCard(T("磁盘清理"), "delete", CleanCard()));
        _body.Children.Add(VersionDialog.SubCard(T("下载历史"), "layers", HistoryCard()));
        if (!Platform.IsMobile)
            _body.Children.Add(VersionDialog.SubCard(T("本地服务器"), "signal", ServerCard()));
        _body.Children.Add(VersionDialog.SubCard(T("实验室"), "sparkle", LabsCard()));
        _body.Children.Add(VersionDialog.SubCard(T("官方新闻"), "flag", NewsCard()));
        _body.Children.Add(VersionDialog.SubCard(T("帮助"), "info", HelpCard()));
        _body.Children.Add(VersionDialog.SubCard(T("反馈"), "file", FeedbackCard()));
        _body.Children.Add(VersionDialog.SubCard(T("赞助"), "sparkle", SponsorCard()));
        _body.Children.Add(VersionDialog.SubCard(T("公告"), "flag", AnnounceCard()));
        if (!Platform.IsMobile)
            _body.Children.Add(VersionDialog.SubCard(T("手柄"), "game", GamepadCard()));

        var root = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);
        root.Children.Add(new ScrollViewer { Content = _body, Padding = new Thickness(0, 0, 14, 10) });
        Content = root;
    }

    public void OnShow()
    {
        RefreshHistory();
        _ = ScanImport();
    }

    private Control ImportCard()
    {
        var hint = new TextBlock
        {
            Text = T("扫描本机 PCL、HMCL 和官方启动器的游戏目录，加入额外目录，并导入找到的离线账号。"),
            Classes = { "muted", "wrap" },
        };
        var scan = DialogView.MakeButton(T("重新扫描"), "ghost", "refresh", () => _ = ScanImport());
        var pick = DialogView.MakeButton(T("选择文件夹…"), icon: "folder", onClick: PickImport);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { scan, pick } };
        var box = new StackPanel { Spacing = 10, Children = { hint, row, _import } };
        return box;
    }

    private async Task ScanImport()
    {
        _import.Children.Clear();
        _import.Children.Add(new TextBlock { Text = T("正在扫描…"), Classes = { "muted" } });
        List<FoundLauncher> found;
        try
        {
            found = await Task.Run(LauncherImport.Detect);
        }
        catch (Exception e)
        {
            _import.Children.Clear();
            _import.Children.Add(new TextBlock { Text = e.Message, Classes = { "wrap" } });
            return;
        }
        _import.Children.Clear();
        if (found.Count == 0)
        {
            _import.Children.Add(new TextBlock { Text = T("没有发现其他启动器的游戏目录。也可以手动选择一个 .minecraft 文件夹。"), Classes = { "muted" } });
            return;
        }
        foreach (var item in found)
            _import.Children.Add(ImportRow(item));
    }

    private Control ImportRow(FoundLauncher item)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock { Text = item.Name + "  ·  " + item.Kind.ToUpperInvariant(), FontWeight = FontWeight.SemiBold });
        text.Children.Add(DialogView.Text(item.GameDir + (item.Accounts.Count > 0 ? F("  ·  {0} 个账号", item.Accounts.Count) : ""), "small", "dim"));
        grid.Children.Add(text);
        var add = DialogView.MakeButton(T("导入"), "primary", onClick: () =>
        {
            var n = LauncherImport.Apply(Cfg, item);
            Main.SaveSettings();
            Main.RefreshInstalled();
            Main.Toast(n > 0 ? F("已导入 {0} 项", n) : T("已经导入过了"));
            Main.SettingsPage.RefreshGameDirs();
        });
        Grid.SetColumn(add, 1);
        grid.Children.Add(add);
        return new Border { Classes = { "row" }, Padding = new Thickness(12, 8), Child = grid };
    }

    private async void PickImport()
    {
        var path = await Main.PickFolder(T("选择游戏目录（.minecraft）"));
        if (path == null)
            return;
        var found = new FoundLauncher("folder", Path.GetFileName(path), path, []);
        LauncherImport.Apply(Cfg, found);
        Main.SaveSettings();
        Main.RefreshInstalled();
        Main.SettingsPage.RefreshGameDirs();
        Main.Toast(T("已加入额外游戏目录"));
    }

    private Control CleanCard()
    {
        var hint = new TextBlock
        {
            Text = T("清理未完成的下载、旧日志、崩溃报告，以及没有任何版本再引用的 libraries。不会动存档和模组。"),
            Classes = { "muted", "wrap" },
        };
        var scan = DialogView.MakeButton(T("扫描"), icon: "search", onClick: () => _ = ScanClean());
        var wipe = DialogView.MakeButton(T("删除选中项"), "primary", "delete", DoClean);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { scan, wipe } };
        return new StackPanel { Spacing = 10, Children = { hint, row, _clean } };
    }

    private async Task ScanClean()
    {
        _clean.Children.Clear();
        _clean.Children.Add(new TextBlock { Text = T("正在扫描…"), Classes = { "muted" } });
        var dir = Cfg.MinecraftDir;
        try
        {
            _cleanItems = await Task.Run(() => DiskClean.Scan(dir));
        }
        catch (Exception e)
        {
            _clean.Children.Clear();
            _clean.Children.Add(new TextBlock { Text = e.Message, Classes = { "wrap" } });
            return;
        }
        _cleanPick.Clear();
        _clean.Children.Clear();
        if (_cleanItems.Count == 0)
        {
            _clean.Children.Add(new TextBlock { Text = T("没有发现可以清理的文件。"), Classes = { "muted" } });
            return;
        }
        foreach (var item in _cleanItems)
        {
            var box = new CheckBox { Content = item.Title + "  ·  " + item.Detail, IsChecked = true };
            _cleanPick.Add(item.Key);
            box.IsCheckedChanged += (_, _) =>
            {
                if (box.IsChecked == true)
                    _cleanPick.Add(item.Key);
                else
                    _cleanPick.Remove(item.Key);
            };
            _clean.Children.Add(box);
        }
    }

    private async void DoClean()
    {
        var pick = _cleanItems.Where(i => _cleanPick.Contains(i.Key)).ToList();
        if (pick.Count == 0)
        {
            Main.Toast(T("请先扫描并勾选要删除的项"), "warn");
            return;
        }
        var size = DiskClean.FormatSize(pick.Sum(i => i.Bytes));
        if (!await Main.Confirm(T("磁盘清理"), F("将删除 {0}，确定吗？", size), T("删除"), "warn", "danger"))
            return;
        await Main.RunTask(T("磁盘清理"), () => Task.Run(() => DiskClean.Delete(pick)), bytes =>
        {
            Main.Toast(F("已清理 {0}", DiskClean.FormatSize(bytes)));
            _ = ScanClean();
        });
    }

    private Control HistoryCard()
    {
        var clear = DialogView.MakeButton(T("清空记录"), "ghost", onClick: () =>
        {
            DownloadHistory.Clear();
            RefreshHistory();
        });
        return new StackPanel { Spacing = 8, Children = { clear, _history } };
    }

    private void RefreshHistory()
    {
        _history.Children.Clear();
        var items = DownloadHistory.All();
        if (items.Count == 0)
        {
            _history.Children.Add(new TextBlock { Text = T("还没有下载记录。失败的文件可以在这里重试。"), Classes = { "muted" } });
            return;
        }
        foreach (var item in items.Take(30))
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock { Text = item.Name, FontWeight = FontWeight.SemiBold });
            text.Children.Add(DialogView.Text(
                (item.Ok ? T("成功") : T("失败")) + "  ·  " + item.Time.ToString("yyyy-MM-dd HH:mm")
                + (string.IsNullOrEmpty(item.Error) ? "" : "  ·  " + item.Error), "small", "dim"));
            grid.Children.Add(text);
            if (!item.Ok)
            {
                var retry = DialogView.MakeButton(T("重试"), "ghost", "refresh", () => Retry(item));
                retry.MinWidth = 0;
                Grid.SetColumn(retry, 1);
                grid.Children.Add(retry);
            }
            _history.Children.Add(new Border { Classes = { "row" }, Padding = new Thickness(12, 8), Child = grid });
        }
    }

    private void Retry(DownloadRecord item)
    {
        if (string.IsNullOrEmpty(item.Url) || string.IsNullOrEmpty(item.Path))
            return;
        var launcher = Main.MakeLauncher();
        _ = Main.RunTask(F("重试 {0}", item.Name), () => launcher.Dl.DownloadAsync(new DownloadTask(item.Url, item.Path)),
                         () =>
                         {
                             Main.Toast(F("已重新下载 {0}", item.Name));
                             RefreshHistory();
                         });
    }

    private Control ServerCard()
    {
        var hint = new TextBlock
        {
            Text = T("为当前选中的原版下载官方服务端 jar，写好 eula 和启动脚本。模组端请用对应加载器的服务端。"),
            Classes = { "muted", "wrap" },
        };
        var go = DialogView.MakeButton(T("准备服务端"), "primary", "download", PrepareServer);
        return new StackPanel { Spacing = 10, Children = { hint, go } };
    }

    private async void PrepareServer()
    {
        var version = Main.SelectedVersion;
        if (string.IsNullOrEmpty(version))
        {
            Main.Toast(T("请先选择一个游戏版本"), "warn");
            return;
        }
        var folder = await Main.PickFolder(T("选择服务端文件夹"));
        if (folder == null)
            return;
        var launcher = Main.MakeLauncher();
        await Main.RunTask(T("准备服务端"), () => LocalServer.PrepareAsync(launcher, version, folder, Main.Progress), path =>
        {
            Main.Toast(T("服务端已准备好"));
            Platform.OpenPath(path);
        });
    }

    private Control LabsCard()
    {
        var seedRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        seedRow.Children.Add(_seed);
        seedRow.Children.Add(DialogView.MakeButton(T("打开种子地图"), icon: "globe", onClick: () =>
        {
            var seed = Uri.EscapeDataString(_seed.Text ?? "");
            Platform.OpenUrl("https://www.chunkbase.com/apps/seed-map#seed=" + seed);
        }));
        seedRow.Children.Add(DialogView.MakeButton(T("结构图文件夹"), "ghost", "folder", () =>
        {
            var dir = Path.Combine(new GameLauncher(Cfg).McDir, "schematics");
            Directory.CreateDirectory(dir);
            Platform.OpenPath(dir);
        }));

        var colorRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        colorRow.Children.Add(_from);
        colorRow.Children.Add(_to);
        colorRow.Children.Add(DialogView.MakeButton(T("生成渐变"), onClick: MakeGradient));
        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = T("种子地图和结构图"), FontWeight = FontWeight.SemiBold },
                seedRow,
                new TextBlock { Text = T("皮革颜色渐变"), FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) },
                colorRow,
                _gradient,
            },
        };
    }

    private void MakeGradient()
    {
        if (!TryColor(_from.Text, out var a) || !TryColor(_to.Text, out var b))
        {
            _gradient.Text = T("请输入两个 #RRGGBB 颜色");
            return;
        }
        var parts = new List<string>();
        for (var i = 0; i <= 8; i++)
        {
            var t = i / 8.0;
            var r = (int)(a.r + (b.r - a.r) * t);
            var g = (int)(a.g + (b.g - a.g) * t);
            var bl = (int)(a.b + (b.b - a.b) * t);
            parts.Add($"#{r:X2}{g:X2}{bl:X2}");
        }
        _gradient.Text = string.Join("  →  ", parts);
    }

    private static bool TryColor(string text, out (int r, int g, int b) color)
    {
        color = default;
        text = (text ?? "").Trim().TrimStart('#');
        if (text.Length != 6)
            return false;
        try
        {
            color = (Convert.ToInt32(text[..2], 16), Convert.ToInt32(text[2..4], 16), Convert.ToInt32(text[4..], 16));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private Control NewsCard()
    {
        var list = new StackPanel { Spacing = 8 };
        var hint = new TextBlock { Text = T("Minecraft 官方更新说明。"), Classes = { "muted", "wrap" } };
        var load = DialogView.MakeButton(T("刷新新闻"), icon: "refresh", onClick: () => _ = LoadNews(list));
        _ = LoadNews(list);
        return new StackPanel { Spacing = 8, Children = { hint, load, list } };
    }

    private async Task LoadNews(StackPanel list)
    {
        list.Children.Clear();
        list.Children.Add(new TextBlock { Text = T("正在获取…"), Classes = { "muted" } });
        try
        {
            var items = await VanillaNews.FetchAsync();
            list.Children.Clear();
            foreach (var item in items.Take(6))
            {
                var body = item.Body.Replace("\r", "").Replace("\n", " ");
                if (body.Length > 160)
                    body = body[..160] + "…";
                var box = new StackPanel { Spacing = 2 };
                box.Children.Add(new TextBlock { Text = item.Title, FontWeight = FontWeight.SemiBold });
                if (body.Length > 0)
                    box.Children.Add(new TextBlock { Text = body, Classes = { "small", "muted", "wrap" } });
                list.Children.Add(box);
            }
            if (list.Children.Count == 0)
                list.Children.Add(new TextBlock { Text = T("暂时没有新闻"), Classes = { "muted" } });
        }
        catch (Exception e)
        {
            list.Children.Clear();
            list.Children.Add(new TextBlock { Text = F("获取失败：{0}", e.Message), Classes = { "muted", "wrap" } });
        }
    }

    private static Control HelpCard()
    {
        var box = new StackPanel { Spacing = 10 };
        foreach (var (q, a) in HelpFaq.Items)
        {
            box.Children.Add(new TextBlock { Text = T(q), FontWeight = FontWeight.SemiBold });
            box.Children.Add(new TextBlock { Text = T(a), Classes = { "muted", "wrap" } });
        }
        return box;
    }

    private Control FeedbackCard()
    {
        var hint = new TextBlock
        {
            Text = T("把当前版本的 latest.log 上传到 mclo.gs，方便分享给别人排查问题。"),
            Classes = { "muted", "wrap" },
        };
        var go = DialogView.MakeButton(T("上传游戏日志"), icon: "upload", onClick: () => _ = UploadLog());
        return new StackPanel { Spacing = 8, Children = { hint, go } };
    }

    private async Task UploadLog()
    {
        var version = Main.SelectedVersion;
        var gameDir = string.IsNullOrEmpty(version) ? "" : new GameLauncher(Cfg).GameDirFor(version);
        var text = Feedback.LatestGameLog(gameDir);
        if (string.IsNullOrWhiteSpace(text))
        {
            await Main.Dialog(T("没有日志"), T("先启动一次游戏，或确认版本隔离后的 logs 文件夹里有 latest.log。"), "warn");
            return;
        }
        try
        {
            var url = await Feedback.UploadLogAsync(text);
            if (await Main.Dialog(T("日志已上传"), url, "info",
                                 (T("打开链接"), "open", "primary"), (T("关闭"), "close", "")) is "open")
                Platform.OpenUrl(url);
        }
        catch (Exception e)
        {
            await Main.Dialog(T("上传失败"), MainView.ErrorText(e), "error");
        }
    }

    private static Control SponsorCard()
    {
        var hint = new TextBlock
        {
            Text = T("如果喜欢 MaoX，可以在爱发电支持开发，加入 Discord，或在 GitHub 点 Star。"),
            Classes = { "muted", "wrap" },
        };
        var afdian = DialogView.MakeButton(T("爱发电"), icon: "sparkle", onClick: () => Platform.OpenUrl(Updater.AfdianUrl));
        var discord = DialogView.MakeButton("Discord", icon: "people", onClick: () => Platform.OpenUrl(Updater.DiscordUrl));
        var star = DialogView.MakeButton(T("打开 GitHub"), icon: "link", onClick: () => Platform.OpenUrl(Updater.RepoUrl));
        var rel = DialogView.MakeButton(T("打开发布页"), icon: "external", onClick: () => Platform.OpenUrl(Updater.ReleasesUrl));
        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                hint,
                new WrapPanel
                {
                    ItemSpacing = 8,
                    LineSpacing = 8,
                    Children = { afdian, discord, star, rel },
                },
            },
        };
    }

    private Control AnnounceCard()
    {
        var text = new TextBlock { Classes = { "muted", "wrap" }, Text = T("点下面可以查看 GitHub 上的最新发布说明。") };
        var go = DialogView.MakeButton(T("查看最新公告"), icon: "external", onClick: () => _ = ShowAnnounce());
        return new StackPanel { Spacing = 8, Children = { text, go } };
    }

    private async Task ShowAnnounce()
    {
        try
        {
            var info = await Announce.FetchAsync();
            if (info == null)
            {
                Platform.OpenUrl(Updater.ReleasesUrl);
                return;
            }
            Cfg.LastAnnouncement = info.Id;
            Main.SaveSettings();
            if (await Main.Dialog(info.Title, string.IsNullOrWhiteSpace(info.Body) ? T("打开发布页查看详情。") : info.Body,
                                 "info", (T("打开发布页"), "page", "primary"), (T("关闭"), "close", "")) is "page")
                Platform.OpenUrl(info.Url);
        }
        catch (Exception e)
        {
            await Main.Dialog(T("获取公告失败"), MainView.ErrorText(e), "error");
        }
    }

    private static Control GamepadCard()
    {
        var hint = new TextBlock
        {
            Text = T("桌面版由游戏自己的 GLFW 识别手柄。可在游戏控制设置里改按键；需要社区映射表时可以打开 gamecontrollerdb。"),
            Classes = { "muted", "wrap" },
        };
        var open = DialogView.MakeButton(T("打开映射表网页"), icon: "external",
                                        onClick: () => Platform.OpenUrl("https://github.com/mdqinc/SDL_GameControllerDB"));
        return new StackPanel { Spacing = 8, Children = { hint, open } };
    }
}
