using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using MaoX.Controls;
using MaoX.Core;

namespace MaoX.Dialogs;

/// <summary>添加 / 编辑服务器。确定时返回新的 ServerEntry，取消返回 null。</summary>
public class ServerDialog : DialogView
{
    private const string FollowSelected = "跟随启动页选择的版本";

    private readonly TextBox _name;
    private readonly TextBox _address;
    private readonly ComboBox _version;
    private readonly TextBlock _error;

    public ServerDialog(ServerEntry server = null)
    {
        DialogWidth = 480;
        _name = new TextBox { Text = server?.Name ?? "", Watermark = "Minecraft 服务器" };
        _address = new TextBox { Text = server?.Address ?? "", Watermark = "例如 mc.example.com 或 1.2.3.4:25565" };
        var versions = new List<string> { FollowSelected };
        versions.AddRange(MainWindow.Current.InstalledList);
        if (!string.IsNullOrEmpty(server?.Version) && !versions.Contains(server.Version))
            versions.Add(server.Version);
        _version = new ComboBox
        {
            ItemsSource = versions, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            SelectedItem = string.IsNullOrEmpty(server?.Version) ? FollowSelected : server.Version,
        };
        _error = new TextBlock
        {
            Classes = { "small", "wrap" },
            Foreground = (IBrush)Application.Current!.FindResource("Error"),
            Margin = new Thickness(0, 8, 0, 0),
        };
        foreach (var box in new[] { _name, _address })
        {
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                    Submit();
            };
            box.TextChanged += (_, _) => _error.Text = "";
        }

        var root = new StackPanel { Spacing = 7 };
        root.Children.Add(Title(server == null ? "添加服务器" : "编辑服务器"));
        root.Children.Add(Spaced(FieldLabel("服务器地址")));
        root.Children.Add(_address);
        root.Children.Add(Spaced(FieldLabel("名称（可选）")));
        root.Children.Add(_name);
        root.Children.Add(Spaced(FieldLabel("进服使用的版本")));
        root.Children.Add(_version);
        root.Children.Add(_error);
        root.Children.Add(ButtonRow(MakeButton("取消", onClick: () => Close()),
                                    MakeButton(server == null ? "添加" : "保存", "primary", onClick: Submit)));
        Content = root;
    }

    private static Control Spaced(Control control)
    {
        control.Margin = new Thickness(0, 10, 0, 0);
        return control;
    }

    public override void OnOpened() => _address.Focus();

    private void Submit()
    {
        var address = (_address.Text ?? "").Trim();
        if (address.Length == 0)
        {
            _error.Text = "请填写服务器地址";
            return;
        }
        if (address.Contains(' ') || address.Contains('/'))
        {
            _error.Text = "地址格式不对，只需要填域名或 IP，可以带端口，例如 mc.example.com:25565";
            return;
        }
        var name = (_name.Text ?? "").Trim();
        var version = _version.SelectedItem as string;
        Close(new ServerEntry
        {
            Name = name.Length > 0 ? name : address,
            Address = address,
            Version = version == FollowSelected ? "" : version ?? "",
        });
    }
}
