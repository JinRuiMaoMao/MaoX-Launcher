using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MaoX.Controls;
using MaoX.Core;

namespace MaoX.Dialogs;

/// <summary>
/// 皮肤设置。离线账号：皮肤 / 披风保存在本地，启动时由本地验证服务器提供；
/// 微软账号：通过官方接口上传皮肤、切换披风；外置登录：只能预览，到皮肤站修改。
/// </summary>
public class SkinDialog : DialogView
{
    private readonly Account _account;
    private readonly SkinPreview _preview = new() { Width = 128, Height = 256, Cursor = new Cursor(StandardCursorType.Hand) };
    private readonly TextBlock _status = new() { Classes = { "small", "wrap" }, Margin = new Thickness(0, 14, 0, 0) };
    private readonly ToggleButton _classic = new() { Classes = { "chip" }, Content = "经典（Steve）" };
    private readonly ToggleButton _slim = new() { Classes = { "chip" }, Content = "纤细（Alex）" };
    private readonly WrapPanel _capes = new();
    private readonly List<Control> _inputs = [];
    private readonly Button _save;
    private bool _closed;

    private byte[] _skinPng;
    private byte[] _capePng;
    private bool _isSlim;
    private bool _skinDirty;
    private bool _modelDirty;

    // 微软账号
    private MsaProfile _profile;
    private string _capeId;
    private bool _capeDirty;

    private static MainWindow Main => MainWindow.Current;

    private bool Offline => _account.Type == "offline";

    private bool Msa => _account.Type == "msa";

    public SkinDialog(Account account)
    {
        _account = account;
        DialogWidth = 600;
        var root = new StackPanel();
        root.Children.Add(Title("皮肤 · " + account.Name));
        root.Children.Add(Paragraph(account.Type switch
        {
            "offline" => "离线账号的皮肤由启动器在本机提供，自己和同样使用 MaoX 的局域网玩家能看到。",
            "msa" => "修改会直接保存到你的微软正版账号，所有服务器都能看到。",
            _ => "外置登录账号的皮肤需要到皮肤站修改，这里只能预览。",
        }));

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("170,*"), Margin = new Thickness(0, 16, 0, 0) };
        var previewCard = new Border
        {
            Classes = { "card" },
            Padding = new Thickness(0, 14),
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    _preview,
                    new TextBlock { Text = "点击查看背面", Classes = { "small", "muted" }, HorizontalAlignment = HorizontalAlignment.Center },
                },
            },
        };
        _preview.PointerPressed += (_, _) =>
        {
            _preview.Back = !_preview.Back;
            ((TextBlock)((StackPanel)previewCard.Child).Children[1]).Text = _preview.Back ? "点击查看正面" : "点击查看背面";
        };
        grid.Children.Add(previewCard);

        var options = new StackPanel { Spacing = 8, Margin = new Thickness(20, 0, 0, 0) };
        Grid.SetColumn(options, 1);
        grid.Children.Add(options);
        root.Children.Add(grid);
        root.Children.Add(_status);

        if (account.Type == "authlib")
        {
            options.Children.Add(FieldLabel("皮肤站"));
            options.Children.Add(Text(account.ServerName ?? account.Api, "muted"));
            options.Children.Add(Row(MakeButton("打开皮肤站", icon: "external", onClick: OpenSkinSite)));
            _save = MakeButton("完成", "primary", onClick: () => Close());
            root.Children.Add(ButtonRow(_save));
        }
        else
        {
            options.Children.Add(FieldLabel("皮肤"));
            options.Children.Add(Row(Input(MakeButton("选择图片…", icon: "image", onClick: PickSkin)),
                                     Input(MakeButton("从正版玩家获取…", icon: "user", onClick: FetchPlayer))));
            var reset = Input(new Button { Classes = { "link" }, Content = "恢复默认皮肤" });
            reset.Click += (_, _) => ResetSkin();
            options.Children.Add(reset);

            options.Children.Add(WithTop(FieldLabel("模型"), 10));
            _classic.Click += (_, _) => SetModel(false);
            _slim.Click += (_, _) => SetModel(true);
            options.Children.Add(Row(Input(_classic), Input(_slim)));

            options.Children.Add(WithTop(FieldLabel("披风"), 10));
            if (Offline)
            {
                var pickCape = Input(MakeButton("选择图片…", icon: "image", onClick: PickCape));
                var removeCape = Input(new Button { Classes = { "link" }, Content = "不使用披风", VerticalAlignment = VerticalAlignment.Center });
                removeCape.Click += (_, _) => SetCape(null);
                options.Children.Add(Row(pickCape, removeCape));
            }
            else
            {
                options.Children.Add(_capes);
            }
            _save = MakeButton("保存", "primary", onClick: Save);
            root.Children.Add(ButtonRow(MakeButton("取消", onClick: () => Close()), _save));
        }
        Content = root;
    }

    public override void OnOpened() => Load();

    public override void OnClosed() => _closed = true;

    // ------------------------------------------------------------------ 界面小工具

    private static StackPanel Row(params Control[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var child in children)
            row.Children.Add(child);
        return row;
    }

    private static Control WithTop(Control control, double top)
    {
        control.Margin = new Thickness(0, top, 0, 0);
        return control;
    }

    private T Input<T>(T control) where T : Control
    {
        _inputs.Add(control);
        return control;
    }

    private void SetBusy(bool busy, string text = null)
    {
        foreach (var input in _inputs)
            input.IsEnabled = !busy;
        _save.IsEnabled = !busy;
        if (!busy)
            UpdateModel();
        SetStatus(text ?? "", false);
    }

    private void SetStatus(string text, bool error)
    {
        _status.Text = text;
        _status.IsVisible = !string.IsNullOrEmpty(text);
        _status.Foreground = (IBrush)Application.Current!.FindResource(error ? "Error" : "Muted");
    }

    private static Bitmap Decode(byte[] png)
    {
        try
        {
            return png == null ? null : new Bitmap(new MemoryStream(png));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void ShowSkin(byte[] png, bool slim)
    {
        _skinPng = png;
        _isSlim = slim;
        _preview.Skin = Decode(png);
        _preview.Slim = slim;
        UpdateModel();
    }

    private void UpdateModel()
    {
        _classic.IsChecked = !_isSlim;
        _slim.IsChecked = _isSlim;
        // 默认皮肤由 UUID 决定模型，只有自定义皮肤能选
        _classic.IsEnabled = _slim.IsEnabled = _skinPng != null && _save.IsEnabled;
    }

    private void SetModel(bool slim)
    {
        if (slim != _isSlim)
            _modelDirty = true;
        _isSlim = slim;
        _preview.Slim = slim;
        UpdateModel();
    }

    private void SetCape(byte[] png)
    {
        _capePng = png;
        _preview.Cape = Decode(png);
        if (png != null)
            _preview.Back = true;
    }

    // ------------------------------------------------------------------ 读取当前皮肤

    private async void Load()
    {
        if (Offline)
        {
            ShowSkin(Skins.Load(_account.Skin), _account.SkinSlim);
            SetCape(Skins.Load(_account.Cape));
            _preview.Back = false;
            SetStatus("", false);
            return;
        }
        SetBusy(true, "正在读取皮肤…");
        try
        {
            if (Msa)
            {
                if (await Task.Run(() => Accounts.RefreshAsync(_account, Main.Cfg)))
                    Main.AccountsChanged();
                _profile = await Task.Run(() => Skins.MsaProfileAsync(_account));
                var active = _profile.Skins.FirstOrDefault(s => s.Active);
                var cape = _profile.Capes.FirstOrDefault(c => c.Active);
                var (skin, capePng) = await Task.Run(async () => (await Skins.DownloadAsync(active?.Url),
                                                                  await Skins.DownloadAsync(cape?.Url)));
                if (_closed)
                    return;
                ShowSkin(skin, active?.Variant?.Equals("slim", StringComparison.OrdinalIgnoreCase) == true);
                _capeId = cape?.Id;
                SetCape(capePng);
                _preview.Back = false;
                BuildCapes();
            }
            else
            {
                var (skin, slim, capePng) = await Task.Run(FetchAuthlib);
                if (_closed)
                    return;
                ShowSkin(skin, slim);
                SetCape(capePng);
                _preview.Back = false;
            }
            SetBusy(false);
        }
        catch (Exception e)
        {
            if (_closed)
                return;
            SetBusy(false);
            SetStatus(MainWindow.ErrorText(e), true);
        }
    }

    private async Task<(byte[] Skin, bool Slim, byte[] Cape)> FetchAuthlib()
    {
        var result = await Http.SendAsync($"{_account.Api}/sessionserver/session/minecraft/profile/{_account.Uuid}");
        if (result.Status != 200)
            return (null, false, null);
        var (skinUrl, slim, capeUrl) = Skins.ParseTextures(result.Json());
        return (await Skins.DownloadAsync(skinUrl), slim, await Skins.DownloadAsync(capeUrl));
    }

    private void BuildCapes()
    {
        _capes.Children.Clear();
        var none = new ToggleButton { Classes = { "chip" }, Content = "不显示", IsChecked = _capeId == null };
        none.Click += (_, _) => ChooseCape(null);
        _capes.Children.Add(Input(none));
        foreach (var cape in _profile?.Capes ?? [])
        {
            var chip = new ToggleButton { Classes = { "chip" }, Content = cape.Alias ?? "披风", IsChecked = cape.Id == _capeId, Tag = cape.Id };
            chip.Click += (_, _) => ChooseCape(cape);
            _capes.Children.Add(Input(chip));
        }
        if (_profile?.Capes.Count is null or 0)
            _capes.Children.Add(new TextBlock { Text = "这个账号还没有披风", Classes = { "small", "muted" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0) });
    }

    private async void ChooseCape(MsaTexture cape)
    {
        _capeId = cape?.Id;
        _capeDirty = true;
        foreach (var chip in _capes.Children.OfType<ToggleButton>())
            chip.IsChecked = Equals(chip.Tag, _capeId);
        if (cape == null)
        {
            SetCape(null);
            return;
        }
        var png = await Task.Run(() => Skins.DownloadAsync(cape.Url));
        if (!_closed && _capeId == cape.Id)
            SetCape(png);
    }

    // ------------------------------------------------------------------ 修改

    private async void PickSkin()
    {
        var path = await Main.PickFile("选择皮肤图片", "PNG 图片", "*.png");
        if (path == null)
            return;
        byte[] png;
        try
        {
            png = await File.ReadAllBytesAsync(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SetStatus("读取图片失败：" + e.Message, true);
            return;
        }
        UseSkin(png, null);
    }

    private void UseSkin(byte[] png, bool? slim)
    {
        var error = Skins.ValidateSkin(png);
        var bitmap = error == null ? Decode(png) : null;
        if (bitmap == null)
        {
            SetStatus(error ?? "无法读取这张图片", true);
            return;
        }
        _skinDirty = true;
        _modelDirty = false;
        ShowSkin(png, slim ?? SkinPreview.LooksSlim(bitmap));
        _preview.Back = false;
        SetStatus(_isSlim ? "已自动识别为纤细（Alex）模型，如果不对可以手动切换" : "", false);
    }

    private async void FetchPlayer()
    {
        var name = await Main.ShowDialogAsync(new InputDialog("从正版玩家获取", "输入正版玩家名，使用他当前的皮肤" + (Offline ? "和披风" : ""),
                                                              "", n => n.Length == 0 ? "请填写玩家名" : null, "获取")) as string;
        if (string.IsNullOrEmpty(name) || _closed)
            return;
        SetBusy(true, $"正在获取「{name}」的皮肤…");
        try
        {
            var textures = await Task.Run(() => Skins.FetchPlayerAsync(name));
            if (_closed)
                return;
            SetBusy(false);
            UseSkin(textures.Skin, textures.Slim);
            if (Offline && textures.Cape != null)
                SetCape(textures.Cape);
        }
        catch (Exception e)
        {
            if (_closed)
                return;
            SetBusy(false);
            SetStatus(MainWindow.ErrorText(e), true);
        }
    }

    private void ResetSkin()
    {
        _skinDirty = true;
        _modelDirty = false;
        ShowSkin(null, false);
        SetStatus("", false);
    }

    private async void PickCape()
    {
        var path = await Main.PickFile("选择披风图片", "PNG 图片", "*.png");
        if (path == null)
            return;
        try
        {
            var png = await File.ReadAllBytesAsync(path);
            var error = Skins.ValidateCape(png);
            if (error != null || Decode(png) == null)
            {
                SetStatus(error ?? "无法读取这张图片", true);
                return;
            }
            SetCape(png);
            SetStatus("", false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SetStatus("读取图片失败：" + e.Message, true);
        }
    }

    private void OpenSkinSite()
    {
        if (Uri.TryCreate(_account.Api, UriKind.Absolute, out var api))
            Platform.OpenUrl(api.GetLeftPart(UriPartial.Authority));
    }

    // ------------------------------------------------------------------ 保存

    private async void Save()
    {
        if (Offline)
        {
            try
            {
                _account.Skin = _skinPng != null ? Skins.Store(_skinPng) : null;
                _account.SkinSlim = _skinPng != null && _isSlim;
                _account.Cape = _capePng != null ? Skins.Store(_capePng) : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                SetStatus("保存皮肤失败：" + e.Message, true);
                return;
            }
            Main.AccountsChanged();
            Main.SkinChanged(_account);
            Main.Toast(_skinPng != null || _capePng != null ? "皮肤已保存，下次启动游戏时生效" : "已恢复默认皮肤");
            Close(true);
            return;
        }

        if (!_skinDirty && !_modelDirty && !_capeDirty)
        {
            Close();
            return;
        }
        SetBusy(true, "正在保存到正版账号…");
        try
        {
            var (skin, slim, reset, capeDirty, capeId) = (_skinPng, _isSlim, _skinDirty && _skinPng == null, _capeDirty, _capeId);
            var upload = !reset && (_skinDirty || _modelDirty) && skin != null;
            await Task.Run(async () =>
            {
                if (await Accounts.RefreshAsync(_account, Main.Cfg))
                    Avalonia.Threading.Dispatcher.UIThread.Post(Main.AccountsChanged);
                if (reset)
                    await Skins.MsaResetSkinAsync(_account);
                else if (upload)
                    await Skins.MsaUploadSkinAsync(_account, skin, slim);
                if (capeDirty)
                    await Skins.MsaSetCapeAsync(_account, capeId);
            });
            if (_closed)
                return;
            Main.SkinChanged(_account);
            Main.Toast("皮肤已更新，重新进入游戏后生效");
            Close(true);
        }
        catch (Exception e)
        {
            if (_closed)
                return;
            SetBusy(false);
            SetStatus(MainWindow.ErrorText(e), true);
        }
    }
}
