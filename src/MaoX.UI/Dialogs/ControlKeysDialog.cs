using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using MaoX.Controls;
using MaoX.Core;
using static MaoX.Core.I18n;

namespace MaoX.Dialogs;

/// <summary>给自定义触屏按键选要按下的键（最多 ControlButton.MaxKeys 个）。确定后返回 List&lt;int&gt;，取消返回 null。</summary>
public class ControlKeysDialog : DialogView
{
    private readonly List<int> _keys;
    private readonly Dictionary<int, ToggleButton> _toggles = [];
    private readonly TextBlock _summary = Text("", "small", "wrap");
    private bool _updating;

    public ControlKeysDialog(IEnumerable<int> keys)
    {
        DialogWidth = 600;
        AutoFocusText = false;
        _keys = keys?.Take(ControlButton.MaxKeys).ToList() ?? [];

        var root = new StackPanel { Spacing = 6 };
        root.Children.Add(Title(T("选择按键")));
        root.Children.Add(Paragraph(F("最多选 {0} 个，按下这个触屏按键时会同时按下它们。", ControlButton.MaxKeys)));
        root.Children.Add(_summary);
        foreach (var (group, groupKeys) in ControlKeys.Groups)
        {
            var label = FieldLabel(T(group));
            label.Margin = new Thickness(0, 6, 0, 0);
            root.Children.Add(label);
            var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var key in groupKeys)
            {
                var toggle = new ToggleButton
                {
                    Content = ControlKeys.Name(key), MinWidth = 44, Margin = new Thickness(0, 0, 6, 6),
                    Padding = new Thickness(10, 5), HorizontalContentAlignment = HorizontalAlignment.Center,
                };
                var code = key;
                toggle.IsCheckedChanged += (_, _) => Toggle(code, toggle.IsChecked == true);
                _toggles[key] = toggle;
                wrap.Children.Add(toggle);
            }
            root.Children.Add(wrap);
        }

        var clear = MakeButton(T("清除"), "ghost", onClick: () =>
        {
            _keys.Clear();
            Refresh();
        });
        root.Children.Add(ButtonRow(clear, MakeButton(T("取消"), onClick: () => Close()), MakeButton(T("确定"), "primary", onClick: () => Close(_keys.ToList()))));
        Content = root;
        Refresh();
    }

    internal void Toggle(int key, bool on)
    {
        if (_updating)
            return;
        if (on && !_keys.Contains(key) && _keys.Count < ControlButton.MaxKeys)
            _keys.Add(key);
        else if (!on)
            _keys.Remove(key);
        Refresh();
    }

    private void Refresh()
    {
        _updating = true;
        foreach (var (key, toggle) in _toggles)
            toggle.IsChecked = _keys.Contains(key);
        _summary.Text = F("已选：{0}", ControlKeys.Describe(_keys));
        _updating = false;
    }
}
