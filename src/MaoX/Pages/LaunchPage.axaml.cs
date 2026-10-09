using System.Collections.Concurrent;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MaoX.Core;

namespace MaoX.Pages;

public record LogLine(string Text, IBrush Brush);

public partial class LaunchPage : UserControl, IPage
{
    private const int MaxLogLines = 4000;

    private static readonly IBrush NormalBrush = new SolidColorBrush(Color.Parse("#AEB6C6"));
    private static readonly IBrush LauncherBrush = new SolidColorBrush(Color.Parse("#00D9FF"));
    private static readonly IBrush SuccessBrush = new SolidColorBrush(Color.Parse("#3DDC97"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#FF5C6C"));
    private static readonly IBrush WarnBrush = new SolidColorBrush(Color.Parse("#FFB547"));

    private readonly AvaloniaList<LogLine> _lines = [];
    private readonly ConcurrentQueue<LogLine> _pending = new();
    private bool _updatingVersions;

    private static MainWindow Main => MainWindow.Current;

    public LaunchPage()
    {
        InitializeComponent();
        LogList.ItemsSource = _lines;
        new DispatcherTimer(TimeSpan.FromMilliseconds(80), DispatcherPriority.Background, (_, _) => FlushLog()).Start();

        Main.AccountChanged += UpdateAccount;
        Main.VersionsChanged += UpdateVersions;
        Main.SelectedVersionChanged += UpdateHero;
        Main.BusyChanged += _ => UpdateLaunchButton();
        UpdateAccount();
    }

    public void OnShow()
    {
        UpdateHero();
    }

    // ------------------------------------------------------------------ 日志

    /// <summary>追加一行日志（任意线程）。kind 为 null 时按内容自动判断颜色。</summary>
    public void AppendLog(string text, string kind)
    {
        if (kind == null)
        {
            var upper = text.ToUpperInvariant();
            if (upper.Contains("/ERROR]") || upper.Contains("/FATAL]") || upper.Contains("EXCEPTION"))
                kind = "error";
            else if (upper.Contains("/WARN]"))
                kind = "warn";
        }
        var brush = kind switch
        {
            "launcher" => LauncherBrush,
            "success" => SuccessBrush,
            "error" => ErrorBrush,
            "warn" => WarnBrush,
            _ => NormalBrush,
        };
        _pending.Enqueue(new LogLine(text, brush));
    }

    private void FlushLog()
    {
        if (_pending.IsEmpty)
            return;
        var batch = new List<LogLine>();
        while (_pending.TryDequeue(out var line))
            batch.Add(line);
        if (batch.Count > MaxLogLines)
            batch = batch[^MaxLogLines..];

        var scroll = LogList.FindDescendantOfType<ScrollViewer>();
        var atBottom = scroll == null || scroll.Offset.Y >= scroll.Extent.Height - scroll.Viewport.Height - 40;
        _lines.AddRange(batch);
        var extra = _lines.Count - MaxLogLines;
        if (extra > 0)
            _lines.RemoveRange(0, extra);
        if (atBottom)
            Dispatcher.UIThread.Post(() => LogList.ScrollIntoView(_lines.Count - 1), DispatcherPriority.Background);
    }

    private void OnClearLog(object sender, RoutedEventArgs e) => _lines.Clear();

    private async void OnCopyLog(object sender, RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null)
            return;
        await clipboard.SetTextAsync(string.Join(Environment.NewLine, _lines.Select(l => l.Text)));
        Main.Toast("日志已复制到剪贴板");
    }

    private void OnOpenGameDir(object sender, RoutedEventArgs e) => Main.OpenGameDir();

    // ------------------------------------------------------------------ 账号与版本

    private void UpdateAccount()
    {
        var account = Main.CurrentAccount;
        HeroAccountName.Text = account?.Name ?? "未设置";
        HeroAccountType.Text = account != null ? Accounts.TypeNames.GetValueOrDefault(account.Type, "") : "";
        HeroAvatar.NameText = account?.Name;
        HeroAvatar.Skin = Main.SkinFor(account);
    }

    private void OnAccountClick(object sender, RoutedEventArgs e) => Main.ManageAccounts();

    private void UpdateVersions()
    {
        _updatingVersions = true;
        VersionBox.ItemsSource = Main.InstalledList.ToList();
        VersionBox.SelectedItem = Main.SelectedVersion;
        _updatingVersions = false;
        UpdateHero();
    }

    private void OnVersionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingVersions && VersionBox.SelectedItem is string version)
            Main.SelectVersion(version);
    }

    public void RefreshHero() => UpdateHero();

    private void UpdateHero()
    {
        var version = Main.SelectedVersion;
        if (!Equals(VersionBox.SelectedItem, version))
        {
            _updatingVersions = true;
            VersionBox.SelectedItem = version;
            _updatingVersions = false;
        }
        if (string.IsNullOrEmpty(version))
        {
            HeroTitle.Text = "还没有游戏版本";
            HeroMeta.Text = "前往「下载」安装一个 Minecraft 版本";
            return;
        }
        HeroTitle.Text = version;
        try
        {
            var gl = new GameLauncher(Main.Cfg);
            var played = PlayTime.Describe(gl, version);
            HeroMeta.Text = gl.DescribeVersion(version) + (played == "" ? "" : "  ·  " + played);
        }
        catch (Exception)
        {
            HeroMeta.Text = "Minecraft";
        }
    }

    private void OnVersionSettings(object sender, RoutedEventArgs e) => Main.ManageVersion();

    // ------------------------------------------------------------------ 启动

    private void OnLaunchClick(object sender, RoutedEventArgs e) => Main.Launch();

    private async void OnStopClick(object sender, RoutedEventArgs e)
    {
        if (await Main.Confirm("结束游戏", "确定要强制结束正在运行的游戏吗？未保存的进度可能会丢失。", "结束游戏", "warn", "danger"))
            Main.KillGame();
    }

    public void OnGameStateChanged() => UpdateLaunchButton();

    private void UpdateLaunchButton()
    {
        LaunchButton.IsEnabled = !Main.Busy;
        LaunchLabel.Text = Main.Busy ? "请稍候…" : "启动游戏";
        StopButton.IsVisible = Main.GameRunning;
    }

    // ------------------------------------------------------------------ 背景图

    public void SetHasBackground(bool on) => Watermark.IsVisible = !on;
}
