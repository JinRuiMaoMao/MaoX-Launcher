using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MaoX.Controls;
using MaoX.Core;
using MaoX.Dialogs;

namespace MaoX.Pages;

/// <summary>多人联机（陶瓦联机）：下载 / 启动服务，创建或加入房间。</summary>
public partial class MultiplayerPage : UserControl, IPage
{
    private static readonly HashSet<string> RoomStates =
        ["host-scanning", "host-starting", "host-ok", "guest-connecting", "guest-starting", "guest-ok"];

    private static readonly Dictionary<string, string> KindNames = new()
    {
        ["HOST"] = "房主", ["LOCAL"] = "你", ["GUEST"] = "玩家",
    };

    private readonly Terracotta _tc;
    private string _phase = "init";
    private string _error = "";
    private JsonNode _state;
    private int _prevProfiles;
    private string _signature;
    private bool _polling;
    private readonly DispatcherTimer _poll;
    private readonly TextBox _code = new() { Watermark = "U/XXXX-XXXX-XXXX-XXXX" };

    private static MainWindow Main => MainWindow.Current;

    public MultiplayerPage()
    {
        InitializeComponent();
        _tc = new Terracotta(null, Main.MakeLauncher().Dl, Main.Log);
        _poll = new DispatcherTimer();
        _poll.Tick += (_, _) =>
        {
            _poll.Stop();
            Poll();
        };
        _code.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
                JoinRoom();
        };
    }

    private string StateName => _phase == "ready" ? _state?.Str("state") : null;

    public bool InRoom => StateName is { } s && RoomStates.Contains(s);

    public void OnShow() => SetTab(_tab);

    public void Shutdown()
    {
        _poll.Stop();
        if (_phase != "init")
        {
            try
            {
                _tc.Shutdown();
            }
            catch (Exception)
            {
                // 退出时尽力而为
            }
        }
    }

    private void OnCreditPressed(object sender, PointerPressedEventArgs e) => Platform.OpenUrl(Terracotta.ProjectUrl);

    // ------------------------------------------------------------------ 生命周期

    private async void Bootstrap()
    {
        if (!_tc.Supported)
        {
            _phase = "unsupported";
            return;
        }
        _phase = "checking";
        bool installed;
        try
        {
            installed = await _tc.InstalledAsync();
        }
        catch (Exception)
        {
            installed = false;
        }
        if (installed)
            Start();
        else
            SetPhase("missing");
    }

    private void SetPhase(string phase, string error = "")
    {
        _phase = phase;
        _error = error;
        Render();
        if (phase == "ready")
            Schedule(0);
    }

    private async void Install()
    {
        if (Main.Busy)
        {
            Main.Toast("当前有任务正在进行，请稍候", "warn");
            return;
        }
        SetPhase("installing");
        await Main.RunTask("下载陶瓦联机", () => _tc.InstallAsync((d, t) => Main.Progress(d, t, "下载陶瓦联机")));
        if (await _tc.InstalledAsync())
            Start();
        else
            SetPhase("fatal", "下载陶瓦联机失败，请检查网络后重试。");
    }

    private async void Start()
    {
        SetPhase("starting");
        try
        {
            await Task.Run(() => _tc.StartAsync());
            SetPhase("ready");
        }
        catch (Exception e)
        {
            SetPhase("fatal", MainWindow.ErrorText(e));
        }
    }

    private async void Retry()
    {
        if (await _tc.InstalledAsync())
            Start();
        else
            Install();
    }

    // ------------------------------------------------------------------ 轮询

    private void Schedule(int? delay = null)
    {
        _poll.Stop();
        if (_phase != "ready")
            return;
        _poll.Interval = TimeSpan.FromMilliseconds(delay ?? (IsEffectivelyVisible ? 1000 : 3000));
        _poll.Start();
    }

    private async void Poll()
    {
        if (_polling || _phase != "ready")
            return;
        _polling = true;
        try
        {
            var state = await Task.Run(() => _tc.StateAsync());
            var previous = _state?.Str("state");
            var current = state.Str("state");
            var profiles = state.Arr("profiles")?.Count ?? 0;
            if (previous == "host-ok" && current == "host-ok" && profiles > _prevProfiles)
                Main.Toast("有新玩家加入了房间");
            if (current == "guest-ok" && previous != "guest-ok")
                Main.Toast("已成功加入房间");
            _prevProfiles = profiles;
            _state = state;
            _polling = false;
            Render();
            Schedule();
        }
        catch (Exception)
        {
            _polling = false;
            if (!await _tc.AliveAsync())
            {
                _state = null;
                SetPhase("fatal", "陶瓦联机已停止运行");
            }
            else
            {
                Schedule();
            }
        }
    }

    private async void Action(Func<Task> action, string errorTitle)
    {
        try
        {
            await Task.Run(action);
        }
        catch (Exception e)
        {
            await Main.Dialog(errorTitle, MainWindow.ErrorText(e), "error");
        }
        Schedule(0);
    }

    private static string PlayerName => Main.CurrentAccount?.Name is { Length: > 0 } name ? name : "Steve";

    private void CreateRoom()
    {
        var player = PlayerName;
        Action(() => _tc.HostAsync(player), "创建房间失败");
    }

    private void JoinRoom()
    {
        var code = (_code.Text ?? "").Trim();
        if (code.Length == 0)
        {
            Main.Toast("请先输入邀请码", "warn");
            return;
        }
        var player = PlayerName;
        Action(() => _tc.JoinAsync(code, player), "加入房间失败");
    }

    private void LeaveRoom() => Action(_tc.LeaveAsync, "操作失败");

    private async void Copy(string text, string what)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null)
            return;
        await clipboard.SetTextAsync(text);
        Main.Toast("已复制" + what);
    }

    // ------------------------------------------------------------------ 渲染

    private void Render()
    {
        var state = _phase == "ready" ? _state : null;
        var profiles = state?.Items("profiles").Select(p => $"{p.Str("machine_id")}|{p.Str("name")}|{p.Str("kind")}");
        var signature = string.Join("\n", _phase, _error, state?.Str("state"), state?.Str("room"), state?.Str("url"),
                                    state?.Str("difficulty"), state?.Get("type")?.ToJsonString(),
                                    string.Join(";", profiles ?? []));
        if (signature == _signature)
            return;
        _signature = signature;
        Body.Content = (state != null ? state.Str("state", "unknown") : _phase) switch
        {
            "unsupported" => CenterCard("error", "当前系统暂不支持联机",
                                        "陶瓦联机需要 Windows 10 及以上、macOS 或 Linux（x64 / ARM64）。", "Warn"),
            "missing" => MissingView(),
            "fatal" => CenterCard("error", "联机服务出现问题", _error, "Error", MakeButton("重试", "primary", "refresh", Retry)),
            "waiting" => WaitingView(),
            "host-scanning" => CenterCard("game", "正在寻找对局域网开放的世界…",
                                          "请在游戏中按 Esc，选择「对局域网开放」并点击「创建局域网世界」。\n检测到之后会自动创建房间。",
                                          "Accent", MakeButton("取消", null, null, LeaveRoom), true),
            "host-starting" => CenterCard("people", "正在创建房间…", "正在连接公共节点，通常只需要几秒钟。", "Accent",
                                          MakeButton("取消", null, null, LeaveRoom), true),
            "guest-connecting" => CenterCard("link", "正在加入房间…", "正在连接公共节点并寻找房主。", "Accent",
                                             MakeButton("取消", null, null, LeaveRoom), true),
            "guest-starting" => CenterCard("link", "已找到房主，正在建立连接…",
                                           Terracotta.Difficulties.GetValueOrDefault(state!.Str("difficulty") ?? "UNKNOWN",
                                                                                     Terracotta.Difficulties["UNKNOWN"]),
                                           "Accent", MakeButton("取消", null, null, LeaveRoom), true),
            "host-ok" => HostOkView(),
            "guest-ok" => GuestOkView(),
            "exception" => ExceptionView(),
            _ => CenterCard("people", _phase switch
            {
                "installing" => "正在下载陶瓦联机…",
                "starting" => "正在启动联机服务…",
                _ => "正在准备…",
            }, Platform.IsWindows ? "首次启动时 Windows 可能会询问是否允许网络访问，请选择允许。" : "", "Accent", null, true),
        };
    }

    private IBrush Res(string key) => (IBrush)this.FindResource(key)!;

    private static Button MakeButton(string text, string style, string icon, Action onClick) =>
        DialogView.MakeButton(text, style, icon, onClick);

    private Control CenterCard(string icon, string title, string text, string color, Control action = null,
                               bool progress = false)
    {
        var inner = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(36, 34) };
        inner.Children.Add(new Icon { Kind = icon, Size = 44, StrokeWidth = 1.5, Foreground = Res(color) });
        inner.Children.Add(new TextBlock { Text = title, Classes = { "h3" }, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) });
        if (!string.IsNullOrEmpty(text))
            inner.Children.Add(new TextBlock
            {
                Text = text, Classes = { "muted" }, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
                LineHeight = 22, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 620,
            });
        if (action != null)
        {
            action.HorizontalAlignment = HorizontalAlignment.Center;
            action.Margin = new Thickness(0, 16, 0, 0);
            inner.Children.Add(action);
        }
        var panel = new DockPanel();
        if (progress)
        {
            var line = new ProgressLine { Height = 2 };
            DockPanel.SetDock(line, Dock.Bottom);
            panel.Children.Add(line);
            line.AttachedToVisualTree += (_, _) => line.Start();
        }
        panel.Children.Add(inner);
        return new Border { Classes = { "card" }, ClipToBounds = true, Child = panel };
    }

    private Control MissingView()
    {
        var text = "首次使用需要下载陶瓦联机组件（约 8 MB），下载后会自动启动。\n" +
                   "它基于 EasyTier 建立点对点连接，不需要公网 IP，也不需要管理员权限。";
        if (Platform.IsMac)
            text = "首次使用需要下载陶瓦联机组件，macOS 上会弹出系统密码框把它安装到「应用程序」。\n" +
                   "它基于 EasyTier 建立点对点连接，不需要公网 IP。";
        return CenterCard("people", "启用多人联机", text, "Accent", MakeButton("下载并启用", "primary", "download", Install));
    }

    private Control Steps(params string[] steps)
    {
        var panel = new StackPanel { Spacing = 10 };
        for (var i = 0; i < steps.Length; i++)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            row.Children.Add(new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = Res("AccentDim"),
                VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock
                {
                    Text = (i + 1).ToString(), FontSize = 12, FontWeight = FontWeight.Bold, Foreground = Res("Accent"),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            });
            var text = new TextBlock { Text = steps[i], TextWrapping = TextWrapping.Wrap, LineHeight = 22, Margin = new Thickness(12, 0, 0, 0) };
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            panel.Children.Add(row);
        }
        return panel;
    }

    private Control Column(string icon, string title, string subtitle, Control content, Control action)
    {
        var dock = new DockPanel { Margin = new Thickness(26, 24) };
        var head = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 0, 20) };
        head.Children.Add(new IconLabel { Icon = icon, Text = title, Classes = { "cardhead" } });
        head.Children.Add(new TextBlock { Text = subtitle, Classes = { "muted" } });
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);
        action.HorizontalAlignment = HorizontalAlignment.Left;
        action.Margin = new Thickness(0, 16, 0, 0);
        DockPanel.SetDock(action, Dock.Bottom);
        dock.Children.Add(action);
        dock.Children.Add(content);
        return new Border { Classes = { "card" }, Child = dock };
    }

    private Control WaitingView()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,20,*") };
        var host = Column("game", "创建房间", "我是房主，邀请好友来我的世界",
                          Steps("启动游戏，进入一个单人世界",
                                "按 Esc 打开菜单，选择「对局域网开放」，再点「创建局域网世界」",
                                "回到这里点击「创建房间」，把邀请码发给好友"),
                          MakeButton("创建房间", "primary", "people", CreateRoom));
        grid.Children.Add(host);
        var guestBody = new StackPanel { Spacing = 7 };
        guestBody.Children.Add(DialogView.FieldLabel("邀请码"));
        if (_code.Parent is Panel old)
            old.Children.Remove(_code);
        guestBody.Children.Add(_code);
        guestBody.Children.Add(new TextBlock
        {
            Text = "形如 U/XXXX-XXXX-XXXX-XXXX，也可以使用 HMCL、PCL 社区版生成的邀请码",
            Classes = { "small", "dim", "wrap" }, Margin = new Thickness(0, 2, 0, 0),
        });
        var guest = Column("link", "加入房间", "好友已经创建了房间，输入邀请码加入", guestBody,
                           MakeButton("加入房间", "primary", "link", JoinRoom));
        Grid.SetColumn(guest, 2);
        grid.Children.Add(guest);
        return grid;
    }

    private StackPanel Hero(StackPanel root, string overline, string value)
    {
        var inner = new StackPanel { Spacing = 4, Margin = new Thickness(30, 26) };
        inner.Children.Add(new TextBlock { Text = overline, Classes = { "overline" }, LetterSpacing = 2.5 });
        inner.Children.Add(new SelectableTextBlock { Text = value, FontSize = 28, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 4, 0, 2) });
        root.Children.Add(new Border { Classes = { "card" }, Child = inner });
        return inner;
    }

    private void Members(StackPanel root)
    {
        var profiles = _state.Items("profiles").ToList();
        var list = new StackPanel { Spacing = 10, Margin = new Thickness(22, 16, 22, 18) };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 4) };
        head.Children.Add(new TextBlock { Text = "房间成员", FontWeight = FontWeight.SemiBold });
        head.Children.Add(new TextBlock { Text = profiles.Count.ToString(), Classes = { "accent", "bold" } });
        list.Children.Add(head);
        foreach (var profile in profiles)
        {
            var name = profile.Str("name") is { Length: > 0 } n ? n : "?";
            var kind = profile.Str("kind", "GUEST");
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            row.Children.Add(new Avatar { Width = 32, Height = 32, NameText = name });
            var text = new StackPanel { Spacing = 1, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            line.Children.Add(new TextBlock { Text = name, FontWeight = FontWeight.SemiBold });
            line.Children.Add(new TextBlock
            {
                Text = KindNames.GetValueOrDefault(kind, kind), Classes = { "small" },
                Foreground = Res(kind == "HOST" ? "Accent" : "Muted"), VerticalAlignment = VerticalAlignment.Center,
            });
            text.Children.Add(line);
            if (profile.Str("vendor") is { Length: > 0 } vendor)
                text.Children.Add(new TextBlock { Text = vendor, Classes = { "small", "dim" } });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            list.Children.Add(row);
        }
        root.Children.Add(new Border { Classes = { "card" }, Margin = new Thickness(0, 16, 0, 0), Child = list });
    }

    private static StackPanel Actions(params Control[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 16, 0, 0) };
        foreach (var b in buttons)
            row.Children.Add(b);
        return row;
    }

    private Control HostOkView()
    {
        var root = new StackPanel();
        var code = _state.Str("room", "");
        var inner = Hero(root, "ROOM CODE", code);
        inner.Children.Add(new TextBlock { Text = "房间已创建，把邀请码发给好友即可加入。请保持游戏和启动器运行。", Classes = { "muted", "wrap" } });
        inner.Children.Add(Actions(MakeButton("复制邀请码", "primary", "copy", () => Copy(code, "邀请码")),
                                   MakeButton("关闭房间", null, "close", LeaveRoom)));
        Members(root);
        return root;
    }

    private Control GuestOkView()
    {
        var root = new StackPanel();
        var url = _state.Str("url", "");
        var inner = Hero(root, "CONNECTED", url);
        inner.Children.Add(new TextBlock
        {
            Text = "已加入房间。在游戏「多人游戏」列表中会出现「陶瓦联机大厅」，也可以直接连接上面的地址。",
            Classes = { "muted", "wrap" },
        });
        inner.Children.Add(Actions(MakeButton("启动游戏并进入", "primary", "play", () => Main.Launch(url)),
                                   MakeButton("复制地址", null, "copy", () => Copy(url, "服务器地址")),
                                   MakeButton("退出房间", null, "close", LeaveRoom)));
        Members(root);
        return root;
    }

    private Control ExceptionView()
    {
        var kind = _state.Int("type", -1);
        var message = kind >= 0 && kind < Terracotta.Exceptions.Count ? Terracotta.Exceptions[kind] : "联机出现未知错误";
        return CenterCard("warn", "联机已中断", message, "Warn", MakeButton("返回", "primary", null, LeaveRoom));
    }
}
