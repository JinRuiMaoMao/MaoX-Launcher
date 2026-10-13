using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using MaoX.Controls;
using MaoX.Core;
using static MaoX.Core.I18n;

namespace MaoX.Dialogs;

/// <summary>
/// 手机触屏按键编辑器：铺满屏幕，按键摆在游戏里的位置上。拖动按键调整位置，选中后可以调大小、隐藏；
/// 可以保存多套布局，游戏里用当前选中的那套。保存后返回 true。
/// </summary>
public class TouchLayoutDialog : DialogView
{
    private static readonly IBrush Fill = new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0));
    private static readonly IBrush Stroke = new SolidColorBrush(Color.FromArgb(0x88, 255, 255, 255));
    private static readonly IBrush Selected = new SolidColorBrush(Color.FromRgb(0, 217, 255));

    private readonly List<ControlLayout> _layouts;
    private ControlLayout _current;
    private readonly Canvas _canvas = new() { Background = Brushes.Transparent };
    private readonly Border _hotbar;
    private readonly Dictionary<string, Border> _views = [];
    private readonly Border _panel;
    private readonly ScrollViewer _panelBody;
    private readonly Grid _builtinRow;
    private readonly StackPanel _customSection;
    private readonly TextBox _nameBox = new() { Watermark = T("不填就显示按键名"), MaxLength = 24 };
    private readonly TextBlock _keysText = Text("", "small", "wrap");
    private readonly Slider _widthSlider = MakeSlider(ControlButton.MinSide, ControlButton.MaxSide, 4);
    private readonly Slider _heightSlider = MakeSlider(ControlButton.MinSide, ControlButton.MaxSide, 4);
    private readonly TextBlock _widthText = Text("", "small"), _heightText = Text("", "small");
    private readonly CheckBox _toggleBox = new() { Content = T("切换模式") };
    private readonly CheckBox _roundBox = new() { Content = T("圆形") };
    private readonly CheckBox _inGameBox = new() { Content = T("游戏中显示") };
    private readonly CheckBox _inMenuBox = new() { Content = T("菜单里显示") };
    private readonly ComboBox _layoutBox = new() { Width = 150 };
    private readonly Button _renameButton, _deleteButton;
    private readonly TextBlock _hint = Text("", "small", "dim", "wrap");
    private readonly Slider _sizeSlider = MakeSlider(ControlLayout.MinSize * 100, ControlLayout.MaxSize * 100);
    private readonly Slider _opacitySlider = MakeSlider(ControlLayout.MinOpacity * 100, 100);
    private readonly Button _collapseButton;
    private readonly TextBlock _sizeText = Text("", "small"), _opacityText = Text("", "small");
    private readonly CheckBox _hiddenBox = new() { Content = T("隐藏") };
    private string _selected;
    private string _dragId;
    private Point _dragStart, _dragOffset;
    private bool _dragMoved, _panelPlaced, _updating;

    public TouchLayoutDialog(LauncherConfig cfg)
    {
        FullScreen = true;
        Dismissible = false;
        AutoFocusText = false;
        _layouts = cfg.ControlLayouts.Select(l => l.Clone(l.Name)).ToList();
        _current = _layouts.FirstOrDefault(l => l.Name == cfg.ActiveControlLayout);

        _hotbar = new Border
        {
            BorderBrush = Stroke, BorderThickness = new Thickness(2), Background = Fill, IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = T("快捷栏"), Foreground = Brushes.White, Opacity = 0.6, FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        _canvas.Children.Add(_hotbar);
        foreach (var id in ControlLayout.Ids)
        {
            var view = MakeView(id);
            _views[id] = view;
            _canvas.Children.Add(view);
        }

        _renameButton = MakeButton(T("重命名"), "ghost", onClick: Rename);
        _deleteButton = MakeButton(T("删除"), "ghost", onClick: Delete);
        foreach (var b in new[] { _renameButton, _deleteButton })
            b.MinWidth = 0;
        var add = MakeButton(T("新建"), "ghost", onClick: AddLayout);
        add.MinWidth = 0;
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _layoutBox, add, _renameButton, _deleteButton } };

        var addButton = MakeButton(T("添加按键"), "ghost", onClick: async () => await PickKeys(AddButton()));
        var reset = MakeButton(T("恢复默认"), "ghost", onClick: Reset);
        var cancel = MakeButton(T("取消"), onClick: () => Close());
        var save = MakeButton(T("保存"), "primary", onClick: () => Save(cfg));
        foreach (var b in new[] { addButton, reset, cancel, save })
            b.MinWidth = 0;
        var buttons = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 4, 0, 0) };
        buttons.Children.Add(addButton);
        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
            Children = { reset, cancel, save },
        };
        Grid.SetColumn(right, 1);
        buttons.Children.Add(right);

        _builtinRow = Row(T("大小"), _sizeSlider, _sizeText, _hiddenBox);
        _customSection = BuildCustomSection();
        _panelBody = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    top, _hint,
                    _builtinRow,
                    _customSection,
                    Row(T("不透明度"), _opacitySlider, _opacityText, null),
                    buttons,
                },
            },
        };
        var grip = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Cursor = new Cursor(StandardCursorType.SizeAll), Background = Brushes.Transparent };
        grip.Children.Add(new IconLabel { Icon = "tune", Text = T("触屏按键布局"), Classes = { "cardhead" } });
        _collapseButton = MakeButton(T("收起"), "ghost", onClick: TogglePanel);
        _collapseButton.MinWidth = 0;
        Grid.SetColumn(_collapseButton, 1);
        grip.Children.Add(_collapseButton);
        grip.PointerPressed += PanelPressed;
        grip.PointerMoved += PanelMoved;
        grip.PointerReleased += (_, e) => e.Pointer.Capture(null);
        _panel = new Border
        {
            Classes = { "card" }, Padding = new Thickness(14, 10, 14, 12), Width = 400,
            Child = new StackPanel { Spacing = 8, Children = { grip, _panelBody } },
        };
        _canvas.Children.Add(_panel);
        _panel.SizeChanged += (_, _) =>
        {
            if (_panelPlaced)
                MovePanel(Canvas.GetLeft(_panel), Canvas.GetTop(_panel));
        };

        var background = new Panel
        {
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#3A5A7A"), 0), new GradientStop(Color.Parse("#2E4A2E"), 0.62), new GradientStop(Color.Parse("#3B2E22"), 1) },
            },
            Children =
            {
                new TextBlock
                {
                    Text = "+", FontSize = 26, Foreground = Brushes.White, Opacity = 0.8,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
                _canvas,
            },
        };
        _canvas.SizeChanged += (_, _) => Render();
        _canvas.PointerPressed += (_, e) =>
        {
            if (e.Source == _canvas)
                Select(null);
        };
        Content = background;

        _layoutBox.SelectionChanged += (_, _) =>
        {
            if (_updating)
                return;
            _current = _layoutBox.SelectedIndex <= 0 ? null : _layouts[_layoutBox.SelectedIndex - 1];
            Refresh();
        };
        _sizeSlider.ValueChanged += (_, _) =>
        {
            if (_updating || _selected == null)
                return;
            Item(_selected).Size = Math.Round(_sizeSlider.Value) / 100;
            Refresh();
        };
        _hiddenBox.IsCheckedChanged += (_, _) =>
        {
            if (_updating || _selected == null)
                return;
            Item(_selected).Hidden = _hiddenBox.IsChecked == true;
            Refresh();
        };
        _opacitySlider.ValueChanged += (_, _) =>
        {
            if (_updating)
                return;
            EnsureCustom();
            _current.Opacity = Math.Round(_opacitySlider.Value) / 100;
            Refresh();
        };
        RefreshLayoutBox();
        Refresh();
    }

    private static Slider MakeSlider(double min, double max, double tick = 5) => new()
    {
        Minimum = min, Maximum = max, Width = 170, VerticalAlignment = VerticalAlignment.Center,
        TickFrequency = tick, IsSnapToTickEnabled = true,
    };

    /// <summary>选中自定义按键时显示的设置：名称、按键、宽高、形状、切换模式、什么时候显示。</summary>
    private StackPanel BuildCustomSection()
    {
        var name = new Grid { ColumnDefinitions = new ColumnDefinitions("70,*") };
        name.Children.Add(new TextBlock { Text = T("名称"), VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(_nameBox, 1);
        name.Children.Add(_nameBox);

        var pick = MakeButton(T("选择按键"), "ghost", onClick: async () => await PickKeys(_selected));
        pick.MinWidth = 0;
        var keys = new Grid { ColumnDefinitions = new ColumnDefinitions("70,*,Auto") };
        keys.Children.Add(new TextBlock { Text = T("按键"), VerticalAlignment = VerticalAlignment.Center });
        _keysText.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_keysText, 1);
        keys.Children.Add(_keysText);
        pick.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(pick, 2);
        keys.Children.Add(pick);

        var remove = MakeButton(T("删除这个按键"), "ghost", onClick: RemoveButton);
        remove.MinWidth = 0;
        remove.HorizontalAlignment = HorizontalAlignment.Right;
        var options = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var box in new[] { _toggleBox, _roundBox, _inGameBox, _inMenuBox })
        {
            box.Margin = new Thickness(0, 0, 14, 0);
            options.Children.Add(box);
        }

        _nameBox.PropertyChanged += (_, e) =>
        {
            if (e.Property != TextBox.TextProperty || _updating || Custom(_selected) is not { } button)
                return;
            button.Name = _nameBox.Text?.Trim() ?? "";
            _hint.Text = F("已选中：{0}", button.Label);
            Render();
        };
        _widthSlider.ValueChanged += (_, _) => EditCustom(b => b.Width = Math.Round(_widthSlider.Value));
        _heightSlider.ValueChanged += (_, _) => EditCustom(b => b.Height = Math.Round(_heightSlider.Value));
        _toggleBox.IsCheckedChanged += (_, _) => EditCustom(b => b.Toggle = _toggleBox.IsChecked == true);
        _roundBox.IsCheckedChanged += (_, _) => EditCustom(b => b.Round = _roundBox.IsChecked == true);
        _inGameBox.IsCheckedChanged += (_, _) => EditCustom(b => b.InGame = _inGameBox.IsChecked == true);
        _inMenuBox.IsCheckedChanged += (_, _) => EditCustom(b => b.InMenu = _inMenuBox.IsChecked == true);

        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                name, keys,
                Row(T("宽度"), _widthSlider, _widthText, null),
                Row(T("高度"), _heightSlider, _heightText, null),
                options, remove,
            },
        };
    }

    private void EditCustom(Action<ControlButton> edit)
    {
        if (_updating || Custom(_selected) is not { } button)
            return;
        edit(button);
        Refresh();
    }

    private static Grid Row(string label, Control slider, Control value, Control extra)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("70,Auto,48,*") };
        grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(slider, 1);
        grid.Children.Add(slider);
        value.VerticalAlignment = VerticalAlignment.Center;
        value.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(value, 2);
        grid.Children.Add(value);
        if (extra != null)
        {
            extra.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(extra, 3);
            grid.Children.Add(extra);
        }
        return grid;
    }

    private string Label(string id) => Custom(id)?.Label ?? id switch
    {
        "stick" => T("移动"),
        "jump" => T("跳跃"),
        "sneak" => T("潜行"),
        "inventory" => T("背包"),
        "drop" => T("丢弃"),
        "chat" => T("聊天"),
        "view" => T("视角"),
        "pause" => T("暂停"),
        _ => T("键盘"),
    };

    private Border MakeView(string id)
    {
        var text = new TextBlock
        {
            Text = Label(id), Foreground = Brushes.White, FontWeight = FontWeight.Bold,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2),
        };
        Control child = text;
        if (id == "stick")
        {
            child = new Panel
            {
                Children =
                {
                    new Ellipse { Fill = new SolidColorBrush(Color.FromArgb(0x77, 255, 255, 255)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                    text,
                },
            };
        }
        var view = new Border
        {
            Background = id == "stick" ? new SolidColorBrush(Color.FromArgb(0x33, 0, 0, 0)) : Fill,
            BorderBrush = Stroke,
            Child = child,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        view.PointerPressed += (_, e) => ItemPressed(id, view, e);
        view.PointerMoved += (_, e) => ItemMoved(id, e);
        view.PointerReleased += (_, e) =>
        {
            _dragId = null;
            e.Pointer.Capture(null);
        };
        view.PointerCaptureLost += (_, _) => _dragId = null;
        return view;
    }

    // ------------------------------------------------------------------ 位置

    /// <summary>屏幕边缘留出的距离，和游戏里一样避开刘海和圆角。</summary>
    private Thickness Edges()
    {
        var safe = TopLevel.GetTopLevel(this)?.InsetsManager?.SafeAreaPadding ?? default;
        return new Thickness(Math.Max(16, safe.Left), Math.Max(16, safe.Top), Math.Max(16, safe.Right), Math.Max(16, safe.Bottom));
    }

    /// <summary>按键现在的中心、宽高（界面坐标）和是否隐藏。</summary>
    private (double X, double Y, double W, double H, bool Hidden) Place(string id)
    {
        double width = _canvas.Bounds.Width, height = _canvas.Bounds.Height;
        if (Custom(id) is { } button)
        {
            return (button.X * width, button.Y * height, Math.Clamp(button.Width, ControlButton.MinSide, ControlButton.MaxSide),
                Math.Clamp(button.Height, ControlButton.MinSide, ControlButton.MaxSide), false);
        }
        var (bw, bh, _) = ControlLayout.BaseSize(id);
        if (_current?.Get(id) is { } item)
            return (item.X * width, item.Y * height, bw * item.Size, bh * item.Size, item.Hidden);
        var e = Edges();
        var (x, y) = ControlLayout.DefaultCenter(id, width, height, e.Left, e.Top, e.Right, e.Bottom);
        return (x, y, bw, bh, false);
    }

    /// <summary>默认布局不能改：第一次调整时另存成一套新布局。</summary>
    private void EnsureCustom()
    {
        if (_current != null)
            return;
        _current = new ControlLayout { Name = NewName() };
        _layouts.Add(_current);
        RefreshLayoutBox();
    }

    private ControlItem Item(string id)
    {
        EnsureCustom();
        if (_current.Items.TryGetValue(id, out var item))
            return item;
        var (x, y, _, _, _) = Place(id);
        item = new ControlItem { X = x / _canvas.Bounds.Width, Y = y / _canvas.Bounds.Height };
        _current.Items[id] = item;
        return item;
    }

    private string NewName()
    {
        for (var i = 1; ; i++)
        {
            var name = F("我的布局 {0}", i);
            if (_layouts.All(l => l.Name != name))
                return name;
        }
    }

    private void ItemPressed(string id, Border view, PointerPressedEventArgs e)
    {
        Select(id);
        var p = e.GetPosition(_canvas);
        var (x, y, _, _, _) = Place(id);
        _dragId = id;
        _dragStart = p;
        _dragOffset = new Point(p.X - x, p.Y - y);
        _dragMoved = false;
        e.Pointer.Capture(view);
        e.Handled = true;
    }

    private void ItemMoved(string id, PointerEventArgs e)
    {
        if (_dragId != id)
            return;
        var p = e.GetPosition(_canvas);
        if (!_dragMoved && Math.Abs(p.X - _dragStart.X) < 4 && Math.Abs(p.Y - _dragStart.Y) < 4)
            return;
        _dragMoved = true;
        MoveTo(id, p.X - _dragOffset.X, p.Y - _dragOffset.Y);
    }

    /// <summary>把按键中心移到 (x, y)，不让按键超出屏幕。</summary>
    internal void MoveTo(string id, double x, double y)
    {
        double width = _canvas.Bounds.Width, height = _canvas.Bounds.Height;
        var (_, _, w, h, _) = Place(id);
        var fx = Math.Clamp(x, w / 2, Math.Max(w / 2, width - w / 2)) / width;
        var fy = Math.Clamp(y, h / 2, Math.Max(h / 2, height - h / 2)) / height;
        if (Custom(id) is { } button)
        {
            button.X = fx;
            button.Y = fy;
        }
        else
        {
            var item = Item(id);
            item.X = fx;
            item.Y = fy;
        }
        Render();
    }

    // ------------------------------------------------------------------ 自定义按键

    private ControlButton Custom(string id) => id == null ? null : _current?.Buttons.FirstOrDefault(b => b.Id == id);

    /// <summary>在屏幕中间添加一个还没绑定键的自定义按键并选中它，返回它的 id。</summary>
    internal string AddButton()
    {
        EnsureCustom();
        var id = "custom-" + Guid.NewGuid().ToString("N")[..8];
        _current.Buttons.Add(new ControlButton { Id = id });
        _selected = id;
        Refresh();
        return id;
    }

    internal void SetKeys(string id, IEnumerable<int> keys)
    {
        if (Custom(id) is not { } button)
            return;
        button.Keys = keys.Take(ControlButton.MaxKeys).ToList();
        Refresh();
    }

    private async Task PickKeys(string id)
    {
        if (Custom(id) is not { } button)
            return;
        if (await MainView.Current.ShowDialogAsync(new ControlKeysDialog(button.Keys)) is List<int> keys)
            SetKeys(id, keys);
    }

    private void RemoveButton()
    {
        if (Custom(_selected) is not { } button)
            return;
        _current.Buttons.Remove(button);
        _selected = null;
        Refresh();
    }

    /// <summary>让画布上的自定义按键和当前布局里的一致（切换、新建、删除布局或按键后调用）。</summary>
    private void SyncViews()
    {
        var ids = _current?.Buttons.Select(b => b.Id).ToHashSet() ?? [];
        foreach (var id in _views.Keys.Where(id => !ControlLayout.Ids.Contains(id) && !ids.Contains(id)).ToList())
        {
            _canvas.Children.Remove(_views[id]);
            _views.Remove(id);
        }
        foreach (var id in ids.Where(id => !_views.ContainsKey(id)))
        {
            var view = MakeView(id);
            _views[id] = view;
            _canvas.Children.Insert(_canvas.Children.IndexOf(_panel), view);
        }
        if (_selected != null && !_views.ContainsKey(_selected))
            _selected = null;
    }

    internal void Select(string id)
    {
        _selected = id;
        Refresh();
    }

    // ------------------------------------------------------------------ 面板

    private Point _panelGrab;

    private void PanelPressed(object sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(true) != null)
            return;
        var p = e.GetPosition(_canvas);
        _panelGrab = new Point(p.X - Canvas.GetLeft(_panel), p.Y - Canvas.GetTop(_panel));
        e.Pointer.Capture((IInputElement)sender);
        e.Handled = true;
    }

    private void PanelMoved(object sender, PointerEventArgs e)
    {
        if (e.Pointer.Captured != sender)
            return;
        var p = e.GetPosition(_canvas);
        MovePanel(p.X - _panelGrab.X, p.Y - _panelGrab.Y);
    }

    private void MovePanel(double left, double top)
    {
        var size = _panel.Bounds.Size;
        Canvas.SetLeft(_panel, Math.Clamp(left, 0, Math.Max(0, _canvas.Bounds.Width - size.Width)));
        Canvas.SetTop(_panel, Math.Clamp(top, 0, Math.Max(0, _canvas.Bounds.Height - size.Height)));
    }

    private void TogglePanel()
    {
        _panelBody.IsVisible = !_panelBody.IsVisible;
        _panel.Width = _panelBody.IsVisible ? 400 : 220;
        _collapseButton.Content = _panelBody.IsVisible ? T("收起") : T("展开");
    }

    // ------------------------------------------------------------------ 布局列表

    private void RefreshLayoutBox()
    {
        _updating = true;
        _layoutBox.ItemsSource = new[] { T("默认布局") }.Concat(_layouts.Select(l => l.Name)).ToList();
        _layoutBox.SelectedIndex = _current == null ? 0 : _layouts.IndexOf(_current) + 1;
        _updating = false;
    }

    private void AddLayout()
    {
        _current = (_current ?? new ControlLayout()).Clone(NewName());
        _layouts.Add(_current);
        RefreshLayoutBox();
        Refresh();
    }

    private async void Rename()
    {
        if (_current == null)
            return;
        var current = _current;
        var name = await MainView.Current.ShowDialogAsync(new InputDialog(T("重命名布局"), T("布局名称："), current.Name, text =>
        {
            text = text?.Trim() ?? "";
            if (text.Length == 0)
                return T("名称不能为空");
            return _layouts.Any(l => l != current && l.Name == text) ? T("已经有同名的布局了") : null;
        }, T("重命名"))) as string;
        if (string.IsNullOrWhiteSpace(name))
            return;
        current.Name = name.Trim();
        RefreshLayoutBox();
    }

    private void Delete()
    {
        if (_current == null)
            return;
        _layouts.Remove(_current);
        _current = null;
        RefreshLayoutBox();
        Refresh();
    }

    private void Reset()
    {
        if (_current == null)
            return;
        _current.Items.Clear();
        _current.Opacity = 1;
        Refresh();
    }

    private void Save(LauncherConfig cfg)
    {
        cfg.ControlLayouts = _layouts;
        cfg.ActiveControlLayout = _current?.Name ?? "";
        cfg.Save();
        Close(true);
    }

    // ------------------------------------------------------------------ 显示

    private void Refresh()
    {
        SyncViews();
        _updating = true;
        _renameButton.IsEnabled = _deleteButton.IsEnabled = _current != null;
        var custom = Custom(_selected);
        _builtinRow.IsVisible = custom == null;
        _customSection.IsVisible = custom != null;
        if (custom != null)
        {
            if ((_nameBox.Text ?? "").Trim() != custom.Name)
                _nameBox.Text = custom.Name;
            _keysText.Text = ControlKeys.Describe(custom.Keys);
            _widthSlider.Value = custom.Width;
            _heightSlider.Value = custom.Height;
            _widthText.Text = $"{Math.Round(custom.Width)}";
            _heightText.Text = $"{Math.Round(custom.Height)}";
            _toggleBox.IsChecked = custom.Toggle;
            _roundBox.IsChecked = custom.Round;
            _inGameBox.IsChecked = custom.InGame;
            _inMenuBox.IsChecked = custom.InMenu;
        }
        _hint.Text = _selected == null
            ? T("点一下按键选中，拖动调整位置")
            : _current == null
                ? T("默认布局不能修改，调整后会自动另存为新布局")
                : F("已选中：{0}", Label(_selected));
        var item = _selected == null ? null : _current?.Get(_selected);
        _sizeSlider.IsEnabled = _hiddenBox.IsEnabled = _selected != null;
        _sizeSlider.Value = (item?.Size ?? 1) * 100;
        _sizeText.Text = $"{Math.Round(_sizeSlider.Value)}%";
        _hiddenBox.IsChecked = item?.Hidden == true;
        _opacitySlider.Value = (_current?.Opacity ?? 1) * 100;
        _opacityText.Text = $"{Math.Round(_opacitySlider.Value)}%";
        _updating = false;
        Render();
    }

    private void Render()
    {
        double width = _canvas.Bounds.Width, height = _canvas.Bounds.Height;
        if (width < 1 || height < 1)
            return;
        // 游戏分辨率长边最多 1920，界面缩放按自动 guiScale 估算，只是给个参考
        var px = width / Math.Min(1920, width * (TopLevel.GetTopLevel(this)?.RenderScaling ?? 1));
        int gameW = (int)(width / px), gameH = (int)(height / px), gui = 1;
        while (gameW / (gui + 1) >= 320 && gameH / (gui + 1) >= 240)
            gui++;
        _hotbar.Width = 182 * gui * px;
        _hotbar.Height = 22 * gui * px;
        Canvas.SetLeft(_hotbar, (width - _hotbar.Width) / 2);
        Canvas.SetTop(_hotbar, height - _hotbar.Height);

        _panelBody.MaxHeight = Math.Max(160, height - 70);

        var opacity = _current?.Opacity ?? 1;
        foreach (var (id, view) in _views)
        {
            var (x, y, w, h, hidden) = Place(id);
            view.Width = w;
            view.Height = h;
            Canvas.SetLeft(view, x - w / 2);
            Canvas.SetTop(view, y - h / 2);
            var custom = Custom(id);
            var round = custom?.Round ?? ControlLayout.BaseSize(id).Round;
            view.CornerRadius = new CornerRadius(round ? Math.Min(w, h) / 2 : 8);
            var selected = id == _selected;
            view.BorderBrush = selected ? Selected : Stroke;
            view.BorderThickness = new Thickness(selected ? 2.5 : 1.5);
            view.Opacity = hidden ? 0.3 : Math.Max(opacity, selected ? 0.6 : 0);
            var scale = custom != null
                ? Math.Clamp(Math.Min(w, h) / 44, 0.7, 1.4)
                : Math.Clamp(w / ControlLayout.BaseSize(id).Width, 0.75, 1.4);
            if (view.Child is Panel stick)
            {
                var knob = (Ellipse)stick.Children[0];
                knob.Width = knob.Height = w * 52 / 124;
                ((TextBlock)stick.Children[1]).FontSize = 12 * scale;
            }
            else
            {
                var text = (TextBlock)view.Child;
                text.FontSize = 13 * scale;
                if (custom != null)
                    text.Text = custom.Label;
            }
        }
        if (!_panelPlaced)
        {
            _panel.Measure(Size.Infinity);
            var size = _panel.DesiredSize;
            if (size.Width > 0)
            {
                _panelPlaced = true;
                MovePanel((width - size.Width) / 2, (height - size.Height) / 2);
            }
        }
    }
}
