using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using MaoX.Controls;
using MaoX.Core;

namespace MaoX.Dialogs;

public record InstallChoice(string Loader, LoaderItem LoaderItem, LoaderItem OptiFine);

/// <summary>选择要安装的模组加载器与 OptiFine，返回 InstallChoice，取消返回 null。</summary>
public class InstallDialog : DialogView
{
    private readonly string _mc;
    private readonly Dictionary<string, object> _results = [];   // 加载器 -> List<LoaderItem> 或 Exception
    private readonly Dictionary<string, ToggleButton> _chips = [];
    private string _loader = "vanilla";
    private readonly ComboBox _versionBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _hint = new() { Classes = { "small", "muted", "wrap" } };
    private readonly ToggleSwitch _optifineSwitch = new();
    private readonly ComboBox _optifineBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _optifineHint = new() { Classes = { "small", "muted", "wrap" } };
    private readonly Button _installButton;
    private bool _refreshing;

    public InstallDialog(string mc)
    {
        _mc = mc;
        DialogWidth = 580;
        var root = new StackPanel();
        root.Children.Add(Title("安装 Minecraft " + mc));
        root.Children.Add(Paragraph("选择一个模组加载器；只想玩原版的话直接安装即可。"));

        var chips = new WrapPanel { Margin = new Thickness(0, 20, 0, 18) };
        foreach (var key in new[] { "vanilla" }.Concat(Loaders.All))
        {
            var chip = new ToggleButton
            {
                Classes = { "chip" },
                Content = key == "vanilla" ? "原版" : Mc.LoaderNames[key],
                IsChecked = key == _loader,
                Margin = new Thickness(0, 0, 8, 8),
            };
            chip.Click += (_, _) =>
            {
                _loader = key;
                Refresh();
            };
            _chips[key] = chip;
            chips.Children.Add(chip);
        }
        root.Children.Add(chips);

        root.Children.Add(FieldLabel("加载器版本"));
        _versionBox.Margin = new Thickness(0, 7, 0, 0);
        root.Children.Add(_versionBox);
        _hint.Margin = new Thickness(0, 8, 0, 0);
        root.Children.Add(_hint);

        root.Children.Add(new Border { Height = 1, Background = (IBrush)Application.Current!.FindResource("CardBorder"), Margin = new Thickness(0, 18) });

        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(_optifineSwitch);
        head.Children.Add(new TextBlock { Text = "同时安装 OptiFine", FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(new TextBlock { Text = "高清修复与光影支持", Classes = { "small", "dim" }, VerticalAlignment = VerticalAlignment.Center });
        root.Children.Add(head);
        root.Children.Add(_optifineBox);
        _optifineHint.Margin = new Thickness(0, 8, 0, 0);
        root.Children.Add(_optifineHint);

        _installButton = MakeButton("安装", "primary", "download", Finish);
        root.Children.Add(ButtonRow(MakeButton("取消", onClick: () => Close()), _installButton));
        Content = root;

        _optifineSwitch.IsCheckedChanged += (_, _) => Refresh();
        _optifineBox.SelectionChanged += (_, _) => Refresh();
        Refresh();

        var installer = new LoaderInstaller(MainWindow.Current.MakeLauncher());
        foreach (var key in Loaders.All.Append("optifine"))
            _ = Fetch(installer, key);
    }

    private async Task Fetch(LoaderInstaller installer, string loader)
    {
        object result;
        try
        {
            result = await Task.Run(() => installer.ListVersionsAsync(loader, _mc));
        }
        catch (Exception e)
        {
            result = e;
        }
        _results[loader] = result;
        if (_chips.TryGetValue(loader, out var chip) && result is List<LoaderItem> { Count: 0 })
        {
            chip.Content = Mc.LoaderNames[loader] + " · 不支持";
            chip.IsEnabled = false;
            if (_loader == loader)
                _loader = "vanilla";
        }
        Refresh();
    }

    private static string LabelFor(LoaderItem item)
    {
        var tags = new List<string>();
        if (item.Recommended)
            tags.Add("推荐");
        if (!item.Stable)
            tags.Add("测试版");
        return tags.Count > 0 ? $"{item.Display}    （{string.Join("，", tags)}）" : item.Display;
    }

    private static void Fill(ComboBox box, List<LoaderItem> items)
    {
        var labels = items.Select(LabelFor).ToList();
        var current = box.SelectedItem as string;
        if (box.ItemsSource is not List<string> old || !old.SequenceEqual(labels))
            box.ItemsSource = labels;
        box.SelectedIndex = current != null && labels.Contains(current) ? labels.IndexOf(current) : 0;
        box.IsEnabled = true;
    }

    private void Refresh()
    {
        if (_refreshing)
            return;
        _refreshing = true;
        foreach (var (key, chip) in _chips)
            chip.IsChecked = key == _loader;
        var ok = RefreshLoader();
        RefreshOptiFine();
        _installButton.IsEnabled = ok;
        _refreshing = false;
    }

    private bool RefreshLoader()
    {
        var muted = (IBrush)Application.Current!.FindResource("Muted");
        _hint.Foreground = muted;
        if (_loader == "vanilla")
        {
            _versionBox.ItemsSource = new[] { "无需选择" };
            _versionBox.SelectedIndex = 0;
            _versionBox.IsEnabled = false;
            _hint.Text = "将安装不带模组加载器的原版游戏。";
            return true;
        }
        var name = Mc.LoaderNames[_loader];
        _results.TryGetValue(_loader, out var state);
        switch (state)
        {
            case null:
                _versionBox.ItemsSource = null;
                _versionBox.IsEnabled = false;
                _hint.Text = $"正在获取 {name} 版本列表…";
                return false;
            case Exception e:
                _versionBox.ItemsSource = null;
                _versionBox.IsEnabled = false;
                _hint.Text = "获取版本列表失败：" + MainWindow.ErrorText(e);
                _hint.Foreground = (IBrush)Application.Current!.FindResource("Error");
                return false;
            default:
                var items = (List<LoaderItem>)state;
                Fill(_versionBox, items);
                _hint.Text = $"共 {items.Count} 个可用版本，默认选中推荐版本。";
                return true;
        }
    }

    private void RefreshOptiFine()
    {
        _results.TryGetValue("optifine", out var state);
        var compatible = _loader is "vanilla" or "forge";
        var available = compatible && state is List<LoaderItem> { Count: > 0 };
        _optifineSwitch.IsEnabled = available;
        if (!available)
            _optifineSwitch.IsChecked = false;
        string message = state switch
        {
            null => "正在获取 OptiFine 版本列表…",
            Exception e => "获取 OptiFine 版本列表失败：" + MainWindow.ErrorText(e),
            List<LoaderItem> { Count: 0 } => $"OptiFine 暂不支持 Minecraft {_mc}。",
            _ when !compatible => "OptiFine 只能搭配原版或 Forge 使用。",
            _ when _loader == "forge" => "OptiFine 会作为模组放进 mods 文件夹，与 Forge 一起加载。",
            _ => "将生成独立的 OptiFine 版本（独立版本无法加载其他模组）。",
        };
        if (_optifineSwitch.IsChecked == true && state is List<LoaderItem> list)
        {
            Fill(_optifineBox, list);
            var item = list[Math.Max(_optifineBox.SelectedIndex, 0)];
            if (_loader == "forge" && item.Forge?.StartsWith("Forge ") == true)
                message += $"建议 Forge 版本不低于 {item.Forge[6..]}。";
        }
        else
        {
            _optifineBox.ItemsSource = null;
            _optifineBox.IsEnabled = false;
        }
        _optifineBox.IsVisible = _optifineSwitch.IsChecked == true;
        _optifineHint.Text = message;
        _optifineHint.Foreground = (IBrush)Application.Current!.FindResource(state is Exception ? "Error" : "Muted");
    }

    private void Finish()
    {
        LoaderItem item = null;
        if (_loader != "vanilla")
        {
            if (_results.GetValueOrDefault(_loader) is not List<LoaderItem> { Count: > 0 } items)
                return;
            item = items[Math.Max(_versionBox.SelectedIndex, 0)];
        }
        LoaderItem optifine = null;
        if (_optifineSwitch.IsChecked == true && _results.GetValueOrDefault("optifine") is List<LoaderItem> { Count: > 0 } ofs)
            optifine = ofs[Math.Max(_optifineBox.SelectedIndex, 0)];
        Close(new InstallChoice(_loader == "vanilla" ? null : _loader, item, optifine));
    }
}
