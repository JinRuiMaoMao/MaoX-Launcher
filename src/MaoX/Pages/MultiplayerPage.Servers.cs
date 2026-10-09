using System.Net.Sockets;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using MaoX.Controls;
using MaoX.Core;
using MaoX.Dialogs;
using static MaoX.Core.I18n;

namespace MaoX.Pages;

/// <summary>服务器列表：Ping 显示在线人数和延迟，一键启动游戏并进入。</summary>
public partial class MultiplayerPage
{
    private sealed record ServerState(ServerStatus Status, string Error, Bitmap Icon, bool Loading);

    private readonly Dictionary<string, ServerState> _serverStates = new(StringComparer.OrdinalIgnoreCase);
    private bool _pinged;
    private string _tab = "room";

    private static List<ServerEntry> Servers => Main.Cfg.Servers;

    private void OnTabClick(object sender, RoutedEventArgs e) => SetTab((string)((Control)sender).Tag);

    private void SetTab(string tab)
    {
        _tab = tab;
        var room = tab == "room";
        RoomTab.IsChecked = room;
        ServerTab.IsChecked = !room;
        RoomView.IsVisible = Credit.IsVisible = room;
        ServerTools.IsVisible = !room;
        Subtitle.Text = room
            ? T("基于陶瓦联机，无需公网 IP 就能和好友一起玩，邀请码与 HMCL、PCL 社区版互通")
            : T("收藏常玩的服务器，查看在线人数和延迟，一键启动游戏并进入");
        if (room)
        {
            ServerView.IsVisible = ServerEmpty.IsVisible = false;
            if (_phase == "init")
                Bootstrap();
            Render();
            Schedule(0);
            return;
        }
        RenderServers();
        if (!_pinged)
            PingAll();
    }

    private void OnRefreshServers(object sender, RoutedEventArgs e) => PingAll();

    public void ShowServers() => SetTab("servers");

    /// <summary>冒烟测试用：各服务器的 Ping 结果，还有服务器没连完时返回 null。</summary>
    public string ServerSummary()
    {
        var states = Servers.Select(s => (s.Address, State: _serverStates.GetValueOrDefault(s.Address))).ToList();
        if (states.Any(s => s.State == null || s.State.Loading))
            return null;
        return string.Join("; ", states.Select(s => s.State.Status is { } st
                                                ? $"{s.Address} {st.Online}/{st.Max} {st.Latency}ms \"{st.Version}\""
                                                : $"{s.Address} {s.State.Error}"));
    }

    private static void SaveServers()
    {
        try
        {
            Main.Cfg.Save();
        }
        catch (Exception e)
        {
            Main.Toast(F("保存服务器列表失败：{0}", e.Message), "error");
        }
    }

    // ------------------------------------------------------------------ 增删改

    private async void OnAddServer(object sender, RoutedEventArgs e)
    {
        if (await Main.ShowDialogAsync(new ServerDialog()) is not ServerEntry server)
            return;
        Servers.Add(server);
        SaveServers();
        RenderServers();
        Ping(server.Address);
    }

    private async void Edit(ServerEntry server)
    {
        if (await Main.ShowDialogAsync(new ServerDialog(server)) is not ServerEntry edited)
            return;
        var changed = !edited.Address.Equals(server.Address, StringComparison.OrdinalIgnoreCase);
        server.Name = edited.Name;
        server.Address = edited.Address;
        server.Version = edited.Version;
        SaveServers();
        RenderServers();
        if (changed)
            Ping(server.Address);
    }

    private async void Remove(ServerEntry server)
    {
        if (!await Main.Confirm(T("删除服务器"), F("确定要从列表中删除「{0}」吗？", server.Name), T("删除"), "warn", "danger"))
            return;
        Servers.Remove(server);
        SaveServers();
        RenderServers();
    }

    private void Move(ServerEntry server, int delta)
    {
        var index = Servers.IndexOf(server);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= Servers.Count)
            return;
        Servers.RemoveAt(index);
        Servers.Insert(target, server);
        SaveServers();
        RenderServers();
    }

    private void OnImportServers(object sender, RoutedEventArgs e)
    {
        var root = Main.Cfg.MinecraftDir;
        var dirs = new List<string> { root };
        var versions = Path.Combine(root, "versions");
        if (Directory.Exists(versions))
            dirs.AddRange(Directory.GetDirectories(versions));
        var known = new HashSet<string>(Servers.Select(s => s.Address), StringComparer.OrdinalIgnoreCase);
        var added = new List<ServerEntry>();
        foreach (var dir in dirs)
        {
            try
            {
                foreach (var server in ServerPing.ReadServersDat(dir))
                {
                    if (known.Add(server.Address))
                        added.Add(server);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                Main.Log(F("读取 {0} 失败：{1}", Path.Combine(dir, "servers.dat"), ex.Message));
            }
        }
        if (added.Count == 0)
        {
            Main.Toast(T("游戏的多人游戏列表里没有新的服务器"), "info");
            return;
        }
        Servers.AddRange(added);
        SaveServers();
        RenderServers();
        foreach (var server in added)
            Ping(server.Address);
        Main.Toast(F("已导入 {0} 个服务器", added.Count));
    }

    private void Join(ServerEntry server)
    {
        if (!string.IsNullOrEmpty(server.Version))
        {
            if (!Main.Installed.Contains(server.Version))
            {
                Main.Toast(F("这个服务器设置的版本 {0} 不存在，请编辑服务器重新选择", server.Version), "warn");
                return;
            }
            Main.SelectVersion(server.Version);
        }
        Main.Launch(server.Address);
    }

    private async void CopyAddress(ServerEntry server)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null)
            return;
        await clipboard.SetTextAsync(server.Address);
        Main.Toast(T("已复制服务器地址"));
    }

    // ------------------------------------------------------------------ Ping

    private void PingAll()
    {
        _pinged = true;
        foreach (var address in Servers.Select(s => s.Address).Distinct(StringComparer.OrdinalIgnoreCase))
            Ping(address);
    }

    private async void Ping(string address)
    {
        _serverStates.TryGetValue(address, out var previous);
        _serverStates[address] = new ServerState(previous?.Status, null, previous?.Icon, true);
        RenderServers();
        ServerState state;
        try
        {
            var status = await Task.Run(() => ServerPing.PingAsync(address));
            state = new ServerState(status, null, DecodeIcon(status.Favicon) ?? previous?.Icon, false);
        }
        catch (Exception e)
        {
            state = new ServerState(null, PingError(e), previous?.Icon, false);
        }
        _serverStates[address] = state;
        RenderServers();
    }

    private static Bitmap DecodeIcon(byte[] png)
    {
        if (png == null)
            return null;
        try
        {
            using var stream = new MemoryStream(png);
            return new Bitmap(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string PingError(Exception e) => e switch
    {
        SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData } => T("找不到这个地址，请检查是否拼写正确"),
        SocketException { SocketErrorCode: SocketError.ConnectionRefused } => T("服务器拒绝连接，可能没有开服或端口不对"),
        SocketException { SocketErrorCode: SocketError.TimedOut } or TimeoutException => T("连接超时，服务器可能已关闭"),
        SocketException s => F("无法连接：{0}", s.Message),
        InvalidDataException or EndOfStreamException or IOException => T("服务器没有返回 Minecraft 信息"),
        _ => F("无法连接：{0}", e.Message),
    };

    // ------------------------------------------------------------------ 渲染

    private void RenderServers()
    {
        if (_tab != "servers")
            return;
        ServerEmpty.IsVisible = Servers.Count == 0;
        ServerView.IsVisible = Servers.Count > 0;
        ServerList.Children.Clear();
        for (var i = 0; i < Servers.Count; i++)
            ServerList.Children.Add(ServerRow(Servers[i], i));
    }

    private Control ServerRow(ServerEntry server, int index)
    {
        var state = _serverStates.GetValueOrDefault(server.Address);
        var status = state?.Status;

        var iconBox = new Border
        {
            Width = 52, Height = 52, CornerRadius = new CornerRadius(6), ClipToBounds = true,
            Background = Res("AccentDim"), VerticalAlignment = VerticalAlignment.Center,
        };
        if (state?.Icon is { } bitmap)
        {
            var image = new Image { Source = bitmap, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
            iconBox.Child = image;
        }
        else
        {
            iconBox.Child = new Icon { Kind = "globe", Size = 24, Foreground = Res("Accent") };
        }

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        titleRow.Children.Add(new TextBlock { Text = server.Name, FontWeight = FontWeight.SemiBold, FontSize = 15 });
        var address = server.Address + (string.IsNullOrEmpty(server.Version) ? "" : "  ·  " + server.Version);
        titleRow.Children.Add(new TextBlock { Text = address, Classes = { "small", "dim" }, VerticalAlignment = VerticalAlignment.Center });

        var motd = new TextBlock
        {
            TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, LineHeight = 20,
            Foreground = Res("Muted"),
        };
        if (status != null)
            FillMotd(motd, status.Motd);
        else if (state?.Error != null)
        {
            motd.Text = state.Error;
            motd.Foreground = Res("Error");
        }
        else
        {
            motd.Text = T("正在连接…");
        }

        var text = new StackPanel { Spacing = 4, Margin = new Thickness(16, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(titleRow);
        text.Children.Add(motd);

        var info = new StackPanel { Spacing = 4, MinWidth = 120, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        if (status != null)
        {
            var ping = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
            var latencyBrush = Res(status.Latency switch { < 0 => "Muted", < 150 => "Success", < 300 => "Warn", _ => "Error" });
            ping.Children.Add(new Icon { Kind = "signal", Size = 15, Foreground = latencyBrush });
            ping.Children.Add(new TextBlock { Text = status.Latency >= 0 ? $"{status.Latency} ms" : "—", Foreground = latencyBrush, FontWeight = FontWeight.SemiBold });
            info.Children.Add(ping);
            var players = new TextBlock
            {
                Text = F("{0:N0} / {1:N0} 人在线", status.Online, status.Max), Classes = { "small" }, HorizontalAlignment = HorizontalAlignment.Right,
            };
            if (status.Players.Count > 0)
                ToolTip.SetTip(players, string.Join("\n", status.Players.Take(20)));
            info.Children.Add(players);
            if (status.Version.Length > 0)
                info.Children.Add(new TextBlock
                {
                    Text = status.Version, Classes = { "small", "dim" }, MaxWidth = 170, TextTrimming = TextTrimming.CharacterEllipsis,
                    HorizontalAlignment = HorizontalAlignment.Right,
                });
        }
        else if (state?.Loading != false)
        {
            info.Children.Add(new TextBlock { Text = T("连接中…"), Classes = { "small", "dim" }, HorizontalAlignment = HorizontalAlignment.Right });
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var join = new Button { Classes = { "primary" }, Content = new IconLabel { Icon = "play", Text = T("进入") }, MinWidth = 88 };
        join.Click += (_, _) => Join(server);
        buttons.Children.Add(join);
        var edit = new Button { Classes = { "icon" }, Content = new Icon { Kind = "edit", Size = 17 } };
        ToolTip.SetTip(edit, T("编辑"));
        edit.Click += (_, _) => Edit(server);
        buttons.Children.Add(edit);
        var delete = new Button { Classes = { "icon" }, Content = new Icon { Kind = "delete", Size = 17 } };
        ToolTip.SetTip(delete, T("删除"));
        delete.Click += (_, _) => Remove(server);
        buttons.Children.Add(delete);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        grid.Children.Add(iconBox);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        Grid.SetColumn(info, 2);
        grid.Children.Add(info);
        Grid.SetColumn(buttons, 3);
        grid.Children.Add(buttons);

        var menu = new ContextMenu();
        menu.Items.Add(MenuEntry(T("进入服务器"), () => Join(server)));
        menu.Items.Add(MenuEntry(T("复制地址"), () => CopyAddress(server)));
        menu.Items.Add(MenuEntry(T("编辑"), () => Edit(server)));
        if (index > 0)
            menu.Items.Add(MenuEntry(T("上移"), () => Move(server, -1)));
        if (index < Servers.Count - 1)
            menu.Items.Add(MenuEntry(T("下移"), () => Move(server, 1)));
        menu.Items.Add(MenuEntry(T("删除"), () => Remove(server)));

        var row = new Border { Classes = { "row" }, Padding = new Thickness(14, 12), Child = grid, ContextMenu = menu };
        row.DoubleTapped += (_, e) =>
        {
            if (e.Source is not Visual v || v.FindAncestorOfType<Button>(true) == null)
                Join(server);
        };
        return row;
    }

    private static MenuItem MenuEntry(string text, Action onClick)
    {
        var item = new MenuItem { Header = text };
        item.Click += (_, _) => onClick();
        return item;
    }

    /// <summary>按 MOTD 的颜色和样式生成文字；在当前背景上看不清的颜色改用默认颜色。</summary>
    private void FillMotd(TextBlock block, List<MotdSpan> spans)
    {
        var lines = string.Concat(spans.Select(s => s.Text)).Split('\n');
        block.MaxLines = Math.Min(lines.Length, 2);
        block.TextWrapping = lines.Length > 1 ? TextWrapping.Wrap : TextWrapping.NoWrap;
        var inlines = new InlineCollection();
        var trimStart = true;
        foreach (var span in spans)
        {
            var text = span.Text;
            if (trimStart)
            {
                text = text.TrimStart(' ');
                if (text.Length == 0)
                    continue;
                trimStart = false;
            }
            var parts = text.Split('\n');
            for (var i = 0; i < parts.Length; i++)
            {
                if (i > 0)
                {
                    inlines.Add(new LineBreak());
                    parts[i] = parts[i].TrimStart(' ');
                }
                if (parts[i].Length == 0)
                    continue;
                var run = new Run(parts[i]);
                if (span.Color != null && Color.TryParse(span.Color, out var color) && ThemeManager.Readable(color))
                    run.Foreground = new SolidColorBrush(color);
                if (span.Bold)
                    run.FontWeight = FontWeight.SemiBold;
                if (span.Italic)
                    run.FontStyle = FontStyle.Italic;
                if (span.Underline || span.Strike)
                    run.TextDecorations = span.Underline ? TextDecorations.Underline : TextDecorations.Strikethrough;
                inlines.Add(run);
            }
        }
        if (inlines.Count == 0)
            block.Text = T("（没有简介）");
        else
            block.Inlines = inlines;
    }
}
