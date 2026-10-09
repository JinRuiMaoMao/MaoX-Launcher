using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using MaoX.Controls;

namespace MaoX.Dialogs;

/// <summary>输入一行文字。validate 返回错误信息（null 表示通过）。确定时返回文字，取消返回 null。</summary>
public class InputDialog : DialogView
{
    private readonly TextBox _box;
    private readonly TextBlock _error;
    private readonly Func<string, string> _validate;

    public InputDialog(string title, string message, string initial = "", Func<string, string> validate = null,
                       string okText = "确定")
    {
        DialogWidth = 460;
        _validate = validate;
        _box = new TextBox { Text = initial ?? "", Margin = new Thickness(0, 16, 0, 0) };
        _box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
                Submit();
        };
        _box.TextChanged += (_, _) => _error!.Text = "";
        _error = new TextBlock
        {
            Classes = { "small", "wrap" },
            Foreground = (IBrush)Application.Current!.FindResource("Error"),
            Margin = new Thickness(0, 8, 0, 0),
        };
        var root = new StackPanel();
        root.Children.Add(Title(title));
        if (!string.IsNullOrEmpty(message))
            root.Children.Add(Paragraph(message));
        root.Children.Add(_box);
        root.Children.Add(_error);
        root.Children.Add(ButtonRow(MakeButton("取消", onClick: () => Close()), MakeButton(okText, "primary", onClick: Submit)));
        Content = root;
    }

    public override void OnOpened()
    {
        _box.Focus();
        _box.SelectAll();
    }

    private void Submit()
    {
        var text = (_box.Text ?? "").Trim();
        var error = _validate?.Invoke(text);
        if (error != null)
        {
            _error.Text = error;
            return;
        }
        Close(text);
    }
}
