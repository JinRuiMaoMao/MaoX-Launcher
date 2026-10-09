using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MaoX.Controls;
using MaoX.Core;

namespace MaoX.Dialogs;

/// <summary>账号管理：切换、改名、删除，以及添加离线 / 外置 / 微软账号。</summary>
public class AccountDialog : DialogView
{
    private readonly StackPanel _list = new() { Spacing = 8 };

    private static MainWindow Main => MainWindow.Current;

    public AccountDialog()
    {
        DialogWidth = 560;
        var root = new StackPanel();
        root.Children.Add(Title("账号管理"));
        root.Children.Add(Paragraph("离线账号可以随意改名；点「皮肤」可以更换皮肤和披风（离线账号也可以）"));
        root.Children.Add(new ScrollViewer
        {
            Content = _list,
            MaxHeight = 300,
            Margin = new Thickness(0, 16, 0, 0),
            Padding = new Thickness(0, 0, 8, 0),
        });

        root.Children.Add(new TextBlock { Text = "添加账号", Classes = { "label" }, Margin = new Thickness(0, 20, 0, 8) });
        var add = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        add.Children.Add(MakeButton("离线账号", icon: "user", onClick: AddOffline));
        add.Children.Add(MakeButton("外置登录", icon: "link", onClick: AddAuthlib));
        add.Children.Add(MakeButton("微软正版", icon: "game", onClick: AddMsa));
        root.Children.Add(add);
        root.Children.Add(ButtonRow(MakeButton("完成", "primary", onClick: () => Close())));
        Content = root;

        Main.AccountChanged += Render;
        Render();
    }

    public override void OnClosed() => Main.AccountChanged -= Render;

    private void Render()
    {
        _list.Children.Clear();
        var accounts = Main.AccountList;
        if (accounts.Count == 0)
        {
            _list.Children.Add(new TextBlock
            {
                Text = "还没有账号，先添加一个吧",
                Classes = { "muted" },
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 30),
            });
            return;
        }
        var accent = (IBrush)Application.Current!.FindResource("Accent");
        for (var i = 0; i < accounts.Count; i++)
        {
            var index = i;
            var account = accounts[i];
            var selected = i == Main.AccountIndex;

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto,Auto") };
            grid.Children.Add(new Avatar { Width = 36, Height = 36, NameText = account.Name, Skin = Main.SkinFor(account) });
            var names = new StackPanel { Spacing = 2, Margin = new Thickness(14, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            names.Children.Add(new TextBlock { Text = account.Name, FontWeight = FontWeight.SemiBold });
            names.Children.Add(Text(Accounts.Describe(account), "small", "muted"));
            Grid.SetColumn(names, 1);
            grid.Children.Add(names);
            if (selected)
            {
                var badge = new Border { Classes = { "badge" }, Child = new TextBlock { Text = "使用中" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
                Grid.SetColumn(badge, 2);
                grid.Children.Add(badge);
            }
            var skin = MakeButton("皮肤", "ghost", onClick: () => _ = Main.ShowDialogAsync(new SkinDialog(account)));
            skin.MinWidth = 0;
            Grid.SetColumn(skin, 3);
            grid.Children.Add(skin);
            if (account.Type == "offline")
            {
                var rename = MakeButton("改名", "ghost", onClick: () => Rename(index));
                rename.MinWidth = 0;
                Grid.SetColumn(rename, 4);
                grid.Children.Add(rename);
            }
            var delete = new Button { Classes = { "ghost", "danger-text" }, Content = new Icon { Kind = "delete", Size = 17 }, Padding = new Thickness(8, 0) };
            ToolTip.SetTip(delete, "删除账号");
            delete.Click += (_, _) => Remove(index);
            Grid.SetColumn(delete, 5);
            grid.Children.Add(delete);

            var row = new Border
            {
                Classes = { "row" },
                Padding = new Thickness(14, 10, 8, 10),
                Child = grid,
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            if (selected)
                row.BorderBrush = accent;
            row.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(row).Properties.IsLeftButtonPressed && index != Main.AccountIndex)
                    Main.SetAccount(index);
            };
            _list.Children.Add(row);
        }
    }

    public static string ValidateOffline(string name)
    {
        if (string.IsNullOrEmpty(name))
            return "名称不能为空";
        if (name.Any(char.IsWhiteSpace))
            return "名称不能包含空格";
        if (name.Length > 16)
            return "名称不能超过 16 个字符";
        return null;
    }

    private async void Remove(int index)
    {
        var account = Main.AccountList[index];
        if (await Main.Confirm("删除账号", $"确定要删除账号「{account.Name}」吗？", "删除", "warn", "danger"))
            Main.RemoveAccount(index);
    }

    private async void Rename(int index)
    {
        var account = Main.AccountList[index];
        var name = await Main.ShowDialogAsync(new InputDialog("修改名称", "离线账号的玩家名称（不超过 16 个字符，不能有空格）",
                                                              account.Name, ValidateOffline)) as string;
        if (string.IsNullOrEmpty(name) || name == account.Name)
            return;
        var renamed = Accounts.OfflineAccount(name);
        (renamed.Skin, renamed.SkinSlim, renamed.Cape) = (account.Skin, account.SkinSlim, account.Cape);
        Main.ReplaceAccount(index, renamed);
    }

    private async void AddOffline()
    {
        var name = await Main.ShowDialogAsync(new InputDialog("添加离线账号", "输入玩家名称（不超过 16 个字符，不能有空格）",
                                                              "", ValidateOffline, "添加")) as string;
        if (!string.IsNullOrEmpty(name))
            Main.AddAccount(Accounts.OfflineAccount(name));
    }

    private async void AddAuthlib()
    {
        if (await Main.ShowDialogAsync(new AuthlibLoginDialog()) is Account account)
            Main.AddAccount(account);
    }

    private async void AddMsa()
    {
        var clientId = (Main.Cfg.MsaClientId ?? "").Trim();
        if (clientId.Length == 0)
        {
            var choice = await Main.Dialog(
                "需要先设置 Client ID",
                "微软登录需要一个在 Azure 注册的应用 Client ID。\n\n" +
                "1. 在 Azure 门户注册应用，账户类型选「个人 Microsoft 帐户」，并开启「允许公共客户端流」\n" +
                "2. 向微软提交 Minecraft 接口权限申请（aka.ms/mce-reviewappid），审核通过后即可使用\n" +
                "3. 把 Client ID 填到「设置 → 账号」中",
                "info", ("查看注册教程", "guide", ""), ("前往设置", "settings", "primary"));
            if (choice is "guide")
            {
                Platform.OpenUrl(Accounts.MsaAppGuide);
            }
            else if (choice is "settings")
            {
                Close();
                Main.ShowPage("settings");
            }
            return;
        }
        if (await Main.ShowDialogAsync(new MsaLoginDialog(clientId)) is Account account)
            Main.AddAccount(account);
    }
}

/// <summary>外置登录（authlib-injector），成功时返回 Account。</summary>
public class AuthlibLoginDialog : DialogView
{
    private readonly TextBox _server = new() { Text = Accounts.LittleskinApi };
    private readonly TextBox _user = new();
    private readonly TextBox _password = new() { PasswordChar = '•', RevealPassword = false };
    private readonly TextBlock _status = new() { Classes = { "small", "wrap" } };
    private readonly Button _login;
    private bool _closed;

    private static MainWindow Main => MainWindow.Current;

    public AuthlibLoginDialog()
    {
        DialogWidth = 480;
        var root = new StackPanel();
        root.Children.Add(Title("外置登录"));
        root.Children.Add(Paragraph("使用 LittleSkin 等皮肤站账号登录（authlib-injector），联机时能显示皮肤"));
        var fields = new StackPanel { Spacing = 7, Margin = new Thickness(0, 16, 0, 0) };
        foreach (var (label, box) in new[] { ("认证服务器", _server), ("邮箱或用户名", _user), ("密码", _password) })
        {
            fields.Children.Add(FieldLabel(label));
            box.Margin = new Thickness(0, 0, 0, 8);
            fields.Children.Add(box);
        }
        _password.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
                Login();
        };
        root.Children.Add(fields);
        root.Children.Add(_status);
        _login = MakeButton("登录", "primary", onClick: Login);
        root.Children.Add(ButtonRow(MakeButton("取消", onClick: () => Close()), _login));
        Content = root;
    }

    public override void OnOpened() => _user.Focus();

    public override void OnClosed() => _closed = true;

    private void SetStatus(string text, bool error)
    {
        _status.Text = text;
        _status.Foreground = (IBrush)Application.Current!.FindResource(error ? "Error" : "Muted");
    }

    private async void Login()
    {
        var server = _server.Text ?? "";
        var user = (_user.Text ?? "").Trim();
        var password = _password.Text ?? "";
        if (user.Length == 0 || password.Length == 0)
        {
            SetStatus("请填写账号和密码", true);
            return;
        }
        _login.IsEnabled = false;
        SetStatus("正在登录…", false);
        try
        {
            var (resolved, tokens) = await Task.Run(async () =>
            {
                var s = await Accounts.ResolveYggdrasilAsync(server);
                return (s, await Accounts.YggdrasilLoginAsync(s.Api, user, password));
            });
            if (_closed)
                return;
            var profile = tokens.SelectedProfile;
            if (profile == null)
            {
                var buttons = tokens.AvailableProfiles.Take(4).Select(p => (p.Name, (object)p, "")).ToArray();
                profile = await Main.Dialog("选择角色", "这个账号下有多个角色，请选择要使用的一个。", "info", buttons) as YggdrasilProfile;
                if (profile == null || _closed)
                {
                    _login.IsEnabled = true;
                    SetStatus("", false);
                    return;
                }
                var chosen = profile;
                tokens = await Task.Run(() => Accounts.YggdrasilSelectAsync(resolved.Api, tokens, chosen));
            }
            if (!_closed)
                Close(Accounts.AuthlibAccount(resolved, user, tokens, profile));
        }
        catch (Exception e)
        {
            if (_closed)
                return;
            _login.IsEnabled = true;
            SetStatus(MainWindow.ErrorText(e), true);
        }
    }
}

/// <summary>微软正版登录（设备代码流程），成功时返回 Account。</summary>
public class MsaLoginDialog : DialogView
{
    private readonly string _clientId;
    private readonly CancellationTokenSource _cancel = new();
    private readonly TextBlock _hint = new() { Text = "正在获取登录代码…", Classes = { "muted", "wrap" }, LineHeight = 22 };
    private readonly SelectableTextBlock _code = new() { FontSize = 30, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 14, 0, 0) };
    private readonly TextBlock _status = new() { Classes = { "small", "wrap" }, Margin = new Thickness(0, 10, 0, 0) };
    private readonly Button _open;
    private MsaDeviceCode _device;

    public MsaLoginDialog(string clientId)
    {
        _clientId = clientId;
        DialogWidth = 500;
        _code.Foreground = (IBrush)Application.Current!.FindResource("Accent");
        var root = new StackPanel();
        root.Children.Add(Title("微软正版登录"));
        root.Children.Add(_hint);
        root.Children.Add(_code);
        root.Children.Add(_status);
        _open = MakeButton("复制代码并打开登录页", "primary", "link", OpenPage);
        _open.IsEnabled = false;
        root.Children.Add(ButtonRow(MakeButton("取消", onClick: () => Close()), _open));
        Content = root;
    }

    public override void OnOpened() => Start();

    public override void OnClosed() => _cancel.Cancel();

    private void SetStatus(string text, bool error)
    {
        _status.Text = text;
        _status.Foreground = (IBrush)Application.Current!.FindResource(error ? "Error" : "Muted");
    }

    private async void Start()
    {
        try
        {
            _device = await Task.Run(() => Accounts.MsaDeviceCodeAsync(_clientId));
            if (_cancel.IsCancellationRequested)
                return;
            _hint.Text = $"点击下方按钮打开微软登录页面（{_device.VerificationUri}），输入下面的代码并登录你的微软账号：";
            _code.Text = _device.UserCode;
            SetStatus("等待你在浏览器中完成登录…", false);
            _open.IsEnabled = true;
            var tokens = await Task.Run(() => Accounts.MsaWaitAsync(_clientId, _device, _cancel.Token));
            SetStatus("已授权，正在登录 Minecraft…", false);
            var account = await Task.Run(() => Accounts.MsaAccountAsync(tokens));
            if (!_cancel.IsCancellationRequested)
                Close(account);
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            if (!_cancel.IsCancellationRequested)
                Dispatcher.UIThread.Post(() => SetStatus(MainWindow.ErrorText(e), true));
        }
    }

    private async void OpenPage()
    {
        if (_device == null)
            return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null)
            await clipboard.SetTextAsync(_device.UserCode);
        Platform.OpenUrl(_device.VerificationUri);
    }
}
