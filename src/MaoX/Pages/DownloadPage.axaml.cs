using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MaoX.Core;
using MaoX.Dialogs;
using static MaoX.Core.I18n;

namespace MaoX.Pages;

public record VersionRow(string Id, string TypeText, string Date, bool Installed);

public partial class DownloadPage : UserControl, IPage
{
    public static readonly Dictionary<string, string> VersionTypes = new()
    {
        ["release"] = T("正式版"), ["snapshot"] = T("快照版"), ["old_beta"] = T("远古 Beta"), ["old_alpha"] = T("远古 Alpha"),
    };

    private static MainWindow Main => MainWindow.Current;

    public DownloadPage()
    {
        InitializeComponent();
        SearchBox.TextChanged += (_, _) => FillList();
        Main.VersionsChanged += FillList;
    }

    public void OnShow()
    {
        if (Main.Manifest == null && !Main.IsTaskRunning(T("获取版本列表")))
            LoadManifest();
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => LoadManifest();

    private async void LoadManifest()
    {
        var launcher = Main.MakeLauncher();
        launcher.Manifest = null;
        HintText.Text = T("正在获取版本列表…");
        HintText.IsVisible = HintPaws.IsVisible = Main.Manifest == null;
        await Main.RunTask(T("获取版本列表"), launcher.GetManifestAsync, manifest =>
        {
            Main.Manifest = manifest;
            FillList();
            Main.SetStatus(F("共 {0} 个版本", manifest.Arr("versions")?.Count ?? 0));
        });
        HintPaws.IsVisible = false;
        if (Main.Manifest == null)
            HintText.Text = T("获取版本列表失败，请点击「刷新列表」重试");
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e) => FillList();

    private void FillList()
    {
        var manifest = Main.Manifest;
        if (manifest == null)
        {
            VersionList.ItemsSource = null;
            return;
        }
        var shown = new HashSet<string>();
        if (ReleaseChip.IsChecked == true)
            shown.Add("release");
        if (SnapshotChip.IsChecked == true)
            shown.Add("snapshot");
        if (OldChip.IsChecked == true)
            shown.UnionWith(["old_beta", "old_alpha"]);
        var keyword = (SearchBox.Text ?? "").Trim().ToLowerInvariant();
        var selected = (VersionList.SelectedItem as VersionRow)?.Id;
        var rows = new List<VersionRow>();
        foreach (var v in manifest.Items("versions"))
        {
            var id = v.Str("id");
            var type = v.Str("type");
            if (!shown.Contains(type) || (keyword.Length > 0 && !id.ToLowerInvariant().Contains(keyword)))
                continue;
            var time = v.Str("releaseTime");
            rows.Add(new VersionRow(id, VersionTypes.GetValueOrDefault(type, type), time.Length >= 10 ? time[..10] : time,
                                    Main.Installed.Contains(id)));
        }
        VersionList.ItemsSource = rows;
        VersionList.SelectedItem = rows.FirstOrDefault(r => r.Id == selected);
        HintText.Text = T("没有符合条件的版本");
        HintText.IsVisible = rows.Count == 0;
        UpdateSelected();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelected();

    private void UpdateSelected()
    {
        if (VersionList.SelectedItem is VersionRow row)
        {
            SelectedText.Text = F("已选择  {0}", row.Id);
            SelectedText.Classes.Remove("muted");
        }
        else
        {
            SelectedText.Text = T("未选择版本");
            SelectedText.Classes.Add("muted");
        }
    }

    private void OnListDoubleTapped(object sender, TappedEventArgs e)
    {
        if (VersionList.SelectedItem is VersionRow)
            Install();
    }

    private void OnInstall(object sender, RoutedEventArgs e) => Install();

    private async void Install()
    {
        if (VersionList.SelectedItem is not VersionRow row)
        {
            Main.Toast(T("请先在列表中选择一个版本"), "warn");
            return;
        }
        var version = row.Id;
        if (await Main.ShowDialogAsync(new InstallDialog(version)) is not InstallChoice choice)
            return;
        var launcher = Main.MakeLauncher();
        var parts = new List<string>();
        if (choice.Loader != null)
            parts.Add(Mc.LoaderNames[choice.Loader]);
        if (choice.OptiFine != null)
            parts.Add("OptiFine");
        parts.Add(version);
        Main.Log(new string('─', 48));
        await Main.RunTask(F("安装 {0}", string.Join(" + ", parts)), async () =>
        {
            var installer = new LoaderInstaller(launcher);
            await launcher.PrepareAsync(version);
            var installed = version;
            if (choice.Loader != null)
            {
                installed = await installer.InstallAsync(choice.Loader, version, choice.LoaderItem);
                await launcher.PrepareAsync(installed);
            }
            if (choice.OptiFine != null)
            {
                installed = await installer.InstallOptiFineAsync(version, choice.OptiFine,
                                                                 choice.Loader != null ? installed : null);
                await launcher.PrepareAsync(installed);
            }
            return installed;
        }, installed =>
        {
            Main.Log(F("{0} 安装完成", installed), "success");
            Main.RefreshInstalled(installed);
            Main.ShowPage("launch");
            Main.Toast(F("{0} 安装完成", installed));
        });
    }
}
