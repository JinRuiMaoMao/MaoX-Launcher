using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MaoX.Controls;
using MaoX.Core;
using static MaoX.Core.I18n;

namespace MaoX.Dialogs;

/// <summary>通用表单对话框：getter 返回结果，抛出 ArgumentException 表示输入有误。</summary>
public class FormDialog : DialogView
{
    private readonly Func<object> _getter;
    private readonly TextBlock _error;

    public FormDialog(string title, string subtitle, Control body, Func<object> getter, string confirm,
                      string confirmIcon = "download", double width = 520)
    {
        DialogWidth = width;
        _getter = getter;
        _error = new TextBlock
        {
            Classes = { "small", "wrap" },
            Foreground = (IBrush)Application.Current!.FindResource("Error"),
            Margin = new Thickness(0, 6, 0, 0),
        };
        var root = new StackPanel();
        root.Children.Add(Title(title));
        if (!string.IsNullOrEmpty(subtitle))
            root.Children.Add(Paragraph(subtitle));
        body.Margin = new Thickness(0, 16, 0, 0);
        root.Children.Add(body);
        root.Children.Add(_error);
        root.Children.Add(ButtonRow(MakeButton(T("取消"), onClick: () => Close()),
                                    MakeButton(confirm, "primary", confirmIcon, Submit)));
        Content = root;
    }

    private void Submit()
    {
        try
        {
            Close(_getter());
        }
        catch (ArgumentException e)
        {
            _error.Text = e.Message;
        }
    }

    public static StackPanel Field(string label, Control input)
    {
        var panel = new StackPanel { Spacing = 7, Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(FieldLabel(label));
        panel.Children.Add(input);
        return panel;
    }
}

public static partial class ModpackActions
{
    [GeneratedRegex(@"[\\/:*?""<>|]")]
    private static partial Regex InvalidChars();

    private static MainView Main => MainView.Current;

    public static string UniqueName(GameLauncher gl, string baseName)
    {
        baseName = InvalidChars().Replace(baseName ?? "", "").Trim().TrimEnd('.');
        if (baseName.Length == 0)
            baseName = "整合包";
        var name = baseName;
        for (var n = 2; Directory.Exists(gl.PathOf("versions", name)); n++)
            name = $"{baseName} ({n})";
        return name;
    }

    private static string Describe(ModpackManifest info)
    {
        var loader = !string.IsNullOrEmpty(info.Loader)
            ? $"{Mc.LoaderNames.GetValueOrDefault(info.Loader, info.Loader)} {info.LoaderVersion}"
            : T("原版");
        return F("Minecraft {0}  ·  {1}  ·  {2} 个文件", info.Mc, loader, info.FileCount);
    }

    private static string CheckName(GameLauncher gl, string name)
    {
        return Instance.CheckName(gl, name);
    }

    private static void OnInstalled(string version)
    {
        Main.Log(F("整合包 {0} 安装完成", version), "success");
        Main.RefreshInstalled(version);
        Main.ShowPage("launch");
        Main.Toast(F("整合包 {0} 安装完成", version));
    }

    /// <summary>从本地 .mrpack / .zip 导入整合包。</summary>
    public static async Task ImportFile(string path = null)
    {
        path ??= await Main.PickFile(T("选择整合包"), T("整合包"), "*.mrpack", "*.zip");
        if (path == null)
            return;
        ModpackManifest info;
        try
        {
            info = Modpack.ReadManifest(path);
        }
        catch (Exception e)
        {
            await Main.Dialog(T("无法导入"), e.Message, "error");
            return;
        }
        var gl = Main.MakeLauncher();
        var nameBox = new TextBox { Text = UniqueName(gl, info.Name) };
        var title = $"{info.Name} {info.Version}".Trim();
        var form = new FormDialog(T("导入整合包"), title + "\n" + Describe(info), FormDialog.Field(T("版本名称"), nameBox),
                                  () => CheckName(gl, nameBox.Text), T("安装"));
        if (await Main.ShowDialogAsync(form) is not string name)
            return;
        Main.Log(new string('─', 48));
        await Main.RunTask(T("安装整合包"), () => Modpack.InstallAsync(gl, path, name, Main.Log, Main.Progress), OnInstalled);
    }

    /// <summary>从搜索结果安装整合包：先选择版本与名称，再下载并安装。</summary>
    public static async Task InstallFromHit(IModClient client, SearchHit hit)
    {
        Main.SetStatus(F("正在获取 {0} 的版本列表…", hit.Title));
        List<ModpackVersion> versions;
        try
        {
            versions = await Task.Run(() => client.ModpackVersionsAsync(hit.Id));
        }
        catch (Exception e)
        {
            Main.SetStatus(T("就绪"));
            await Main.Dialog(T("获取版本列表失败"), MainView.ErrorText(e), "error");
            return;
        }
        Main.SetStatus(T("就绪"));
        if (versions.Count == 0)
        {
            await Main.Dialog(T("无法安装"), F("{0} 没有可下载的整合包文件。", hit.Title), "error");
            return;
        }
        var gl = Main.MakeLauncher();
        var versionBox = new ComboBox
        {
            ItemsSource = versions.Select(v => v.Name).ToList(),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var detail = new TextBlock { Text = versions[0].Detail, Classes = { "small", "dim" }, Margin = new Thickness(0, -4, 0, 8) };
        versionBox.SelectionChanged += (_, _) => detail.Text = versions[Math.Max(versionBox.SelectedIndex, 0)].Detail;
        var nameBox = new TextBox { Text = UniqueName(gl, hit.Title) };
        var body = new StackPanel();
        body.Children.Add(FormDialog.Field(T("整合包版本"), versionBox));
        body.Children.Add(detail);
        body.Children.Add(FormDialog.Field(T("版本名称"), nameBox));
        var form = new FormDialog(T("安装整合包"), hit.Title, body,
                                  () => (versions[Math.Max(versionBox.SelectedIndex, 0)], CheckName(gl, nameBox.Text)), T("安装"));
        if (await Main.ShowDialogAsync(form) is not ValueTuple<ModpackVersion, string> result)
            return;
        var (item, name) = result;
        var path = gl.PathOf("cache", "modpacks", InvalidChars().Replace(item.Filename ?? "modpack.zip", "_"));
        Main.Log(new string('─', 48));
        await Main.RunTask(F("安装整合包 {0}", hit.Title), async () =>
        {
            await gl.Dl.DownloadManyAsync([new DownloadTask(item.Url, path, item.Sha1, item.Size)],
                                          (d, t) => Main.Progress(d, t, T("下载整合包")));
            try
            {
                return await Modpack.InstallAsync(gl, path, name, Main.Log, Main.Progress);
            }
            finally
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }, OnInstalled);
    }
}

/// <summary>导出为 Modrinth 整合包。</summary>
public class ModpackExportDialog : DialogView
{
    private static readonly (string Key, string Text, string Desc, bool Default)[] Options =
    [
        ("config", T("配置文件"), T("config、defaultconfigs、kubejs 等"), true),
        ("options", T("游戏设置"), T("options.txt（按键、视频设置）"), false),
        ("resourcepacks", T("资源包"), T("resourcepacks 文件夹"), true),
        ("shaderpacks", T("光影包"), T("shaderpacks 文件夹"), true),
        ("saves", T("存档"), T("saves 文件夹，体积可能很大"), false),
    ];

    private readonly string _version;
    private readonly GameLauncher _gl;
    private readonly TextBox _name = new();
    private readonly TextBox _packVersion = new();
    private readonly TextBox _summary = new();
    private readonly Dictionary<string, ToggleSwitch> _switches = [];
    private readonly TextBlock _error = new() { Classes = { "small" }, Margin = new Thickness(0, 6, 0, 0) };

    private static MainView Main => MainView.Current;

    public ModpackExportDialog(string version)
    {
        _version = version;
        _gl = Main.MakeLauncher();
        DialogWidth = 560;
        var pack = _gl.VersionSettings(version).Obj("modpack");
        _name.Text = pack?.Str("name") is { Length: > 0 } n ? n : version;
        _packVersion.Text = pack?.Str("version") is { Length: > 0 } v ? v : "1.0.0";
        _error.Foreground = (IBrush)Application.Current!.FindResource("Error");

        var root = new StackPanel();
        root.Children.Add(Title(T("导出整合包")));
        root.Children.Add(Paragraph(T("导出为 Modrinth 整合包（.mrpack），可以在 MaoX、HMCL、PCL、Prism 等启动器中导入。" +
                                      "能在 Modrinth 上找到的模组只记录下载地址，体积更小。")));
        var fields = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,12,*") };
        row.Children.Add(FormDialog.Field(T("整合包名称"), _name));
        var versionField = FormDialog.Field(T("整合包版本"), _packVersion);
        Grid.SetColumn(versionField, 2);
        row.Children.Add(versionField);
        fields.Children.Add(row);
        fields.Children.Add(FormDialog.Field(T("简介（可选）"), _summary));
        fields.Children.Add(new TextBlock { Text = T("包含内容（模组总是包含）"), Classes = { "label" }, Margin = new Thickness(0, 4, 0, 10) });
        foreach (var (key, text, desc, def) in Options)
        {
            var toggle = new ToggleSwitch { IsChecked = def };
            _switches[key] = toggle;
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 0, 0, 6) };
            line.Children.Add(toggle);
            line.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            line.Children.Add(new TextBlock { Text = desc, Classes = { "small", "dim" }, VerticalAlignment = VerticalAlignment.Center });
            fields.Children.Add(line);
        }
        root.Children.Add(fields);
        root.Children.Add(_error);
        root.Children.Add(ButtonRow(MakeButton(T("取消"), onClick: () => Close()), MakeButton(T("导出"), "primary", "upload", Export)));
        Content = root;
    }

    public override void OnOpened()
    {
        if (_gl.DetectLoader(_version).Loader == "optifine")
        {
            Close();
            _ = Main.Dialog(T("无法导出"), T("OptiFine 独立版本无法导出为整合包，请改用 Forge + OptiFine。"), "warn");
        }
    }

    private async void Export()
    {
        var name = (_name.Text ?? "").Trim();
        if (name.Length == 0)
        {
            _error.Text = T("请填写整合包名称");
            return;
        }
        var packVersion = (_packVersion.Text ?? "").Trim();
        var summary = (_summary.Text ?? "").Trim();
        var include = _switches.Where(p => p.Value.IsChecked == true).Select(p => p.Key).ToList();
        var safe = Regex.Replace($"{name}-{packVersion}", @"[\\/:*?""<>|]", "_");
        var dest = await Main.SaveFile(T("保存整合包"), safe + ".mrpack", T("Modrinth 整合包"), "*.mrpack");
        if (dest == null)
            return;
        Close();
        await Main.RunTask(T("导出整合包"),
                           () => Modpack.ExportMrpackAsync(_gl, _version, dest, name, packVersion, summary, include, Main.Log, Main.Progress),
                           r => Main.Toast(F("整合包已导出（{0} 个在线文件，{1} 个打包文件）", r.Online, r.Packed)));
    }
}
