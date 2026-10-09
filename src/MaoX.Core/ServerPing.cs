using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using static MaoX.Core.I18n;

namespace MaoX.Core;

/// <summary>保存在启动器里的服务器。Version 为空表示用当前选中的版本进服。</summary>
public class ServerEntry
{
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public string Version { get; set; } = "";
}

/// <summary>MOTD 的一段文字及样式。Color 为 #RRGGBB，null 表示默认颜色。</summary>
public record MotdSpan(string Text, string Color, bool Bold, bool Italic, bool Underline, bool Strike);

public record ServerStatus(string Version, int Protocol, int Online, int Max, List<string> Players,
                           List<MotdSpan> Motd, byte[] Favicon, int Latency);

/// <summary>Minecraft 服务器列表 Ping（1.7+ 协议，旧服务器回退到 1.4–1.6 的旧协议），支持 SRV 记录。</summary>
public static class ServerPing
{
    public const int DefaultPort = 25565;

    private static readonly Dictionary<char, string> CodeColors = new()
    {
        ['0'] = "#000000", ['1'] = "#0000AA", ['2'] = "#00AA00", ['3'] = "#00AAAA",
        ['4'] = "#AA0000", ['5'] = "#AA00AA", ['6'] = "#FFAA00", ['7'] = "#AAAAAA",
        ['8'] = "#555555", ['9'] = "#5555FF", ['a'] = "#55FF55", ['b'] = "#55FFFF",
        ['c'] = "#FF5555", ['d'] = "#FF55FF", ['e'] = "#FFFF55", ['f'] = "#FFFFFF",
    };

    private static readonly Dictionary<string, string> NamedColors = new()
    {
        ["black"] = "#000000", ["dark_blue"] = "#0000AA", ["dark_green"] = "#00AA00", ["dark_aqua"] = "#00AAAA",
        ["dark_red"] = "#AA0000", ["dark_purple"] = "#AA00AA", ["gold"] = "#FFAA00", ["gray"] = "#AAAAAA",
        ["dark_gray"] = "#555555", ["blue"] = "#5555FF", ["green"] = "#55FF55", ["aqua"] = "#55FFFF",
        ["red"] = "#FF5555", ["light_purple"] = "#FF55FF", ["yellow"] = "#FFFF55", ["white"] = "#FFFFFF",
    };

    /// <summary>拆分 "host"、"host:port"、"[IPv6]:port"。没写端口时 ExplicitPort 为 false（这时才查 SRV）。</summary>
    public static (string Host, int Port, bool ExplicitPort) ParseAddress(string address)
    {
        address = (address ?? "").Trim();
        if (address.StartsWith('['))
        {
            var end = address.IndexOf(']');
            if (end > 0)
            {
                var host = address[1..end];
                var rest = address[(end + 1)..];
                return rest.StartsWith(':') && int.TryParse(rest[1..], out var p6) && p6 is > 0 and < 65536
                    ? (host, p6, true)
                    : (host, DefaultPort, false);
            }
        }
        var colon = address.LastIndexOf(':');
        if (colon > 0 && address.IndexOf(':') == colon && int.TryParse(address[(colon + 1)..], out var port)
            && port is > 0 and < 65536)
            return (address[..colon], port, true);
        return (address, DefaultPort, false);
    }

    public static async Task<ServerStatus> PingAsync(string address, int timeoutMs = 6000, CancellationToken cancel = default)
    {
        var (host, port, explicitPort) = ParseAddress(address);
        if (string.IsNullOrEmpty(host))
            throw new ArgumentException(T("服务器地址为空"));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        cts.CancelAfter(timeoutMs);
        var target = (Host: host, Port: port);
        if (!explicitPort && !IPAddress.TryParse(host, out _) && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            target = await ResolveSrvAsync(host, cts.Token) ?? target;
        try
        {
            try
            {
                return await ModernAsync(host, target.Host, target.Port, cts.Token);
            }
            catch (Exception e) when (e is InvalidDataException or EndOfStreamException or IOException)
            {
                return await LegacyAsync(target.Host, target.Port, cts.Token);
            }
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new TimeoutException(T("连接超时"));
        }
    }

    // ------------------------------------------------------------------ 1.7+ 协议

    private static async Task<ServerStatus> ModernAsync(string handshakeHost, string host, int port, CancellationToken cancel)
    {
        using var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(host, port, cancel);
        var stream = tcp.GetStream();

        var handshake = new List<byte>();
        WriteVarInt(handshake, 0x00);
        WriteVarInt(handshake, -1);
        WriteString(handshake, handshakeHost);
        handshake.Add((byte)(port >> 8));
        handshake.Add((byte)port);
        WriteVarInt(handshake, 1);
        await SendPacketAsync(stream, handshake, cancel);
        await SendPacketAsync(stream, [0x00], cancel);

        var response = await ReadPacketAsync(stream, cancel);
        var offset = 0;
        if (ReadVarInt(response, ref offset) != 0x00)
            throw new InvalidDataException(T("服务器返回了意外的数据"));
        var length = ReadVarInt(response, ref offset);
        var json = JsonNode.Parse(Encoding.UTF8.GetString(response, offset, length));

        var latency = -1;
        try
        {
            var ping = new List<byte> { 0x01 };
            ping.AddRange(new byte[8]);
            var watch = Stopwatch.StartNew();
            await SendPacketAsync(stream, ping, cancel);
            await ReadPacketAsync(stream, cancel);
            latency = (int)watch.ElapsedMilliseconds;
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or InvalidDataException)
        {
            // 有些服务器（代理）不回 Pong，不影响显示
        }

        var motd = new List<MotdSpan>();
        Flatten(json.Get("description"), new Style(), motd);
        byte[] favicon = null;
        var icon = json.Str("favicon");
        if (icon != null && icon.IndexOf("base64,", StringComparison.Ordinal) is var comma and >= 0)
        {
            try
            {
                favicon = Convert.FromBase64String(icon[(comma + 7)..].Replace("\n", ""));
            }
            catch (FormatException)
            {
            }
        }
        var players = json.Get("players");
        return new ServerStatus(
            StripCodes(json.Get("version").Str("name") ?? ""), json.Get("version").Int("protocol", -1),
            players.Int("online"), players.Int("max"),
            players.Items("sample").Select(p => StripCodes(p.Str("name") ?? "")).Where(n => n.Length > 0).ToList(),
            motd, favicon, latency);
    }

    private static async Task SendPacketAsync(Stream stream, IReadOnlyCollection<byte> payload, CancellationToken cancel)
    {
        var packet = new List<byte>(payload.Count + 5);
        WriteVarInt(packet, payload.Count);
        packet.AddRange(payload);
        await stream.WriteAsync(packet.ToArray(), cancel);
    }

    private static async Task<byte[]> ReadPacketAsync(Stream stream, CancellationToken cancel)
    {
        var length = 0;
        for (var shift = 0; ; shift += 7)
        {
            if (shift >= 35)
                throw new InvalidDataException(T("数据包长度无效"));
            var one = new byte[1];
            await stream.ReadExactlyAsync(one, cancel);
            length |= (one[0] & 0x7F) << shift;
            if ((one[0] & 0x80) == 0)
                break;
        }
        if (length is <= 0 or > 4 * 1024 * 1024)
            throw new InvalidDataException(T("数据包长度无效"));
        var data = new byte[length];
        await stream.ReadExactlyAsync(data, cancel);
        return data;
    }

    private static void WriteVarInt(List<byte> buffer, int value)
    {
        var v = (uint)value;
        do
        {
            var b = (byte)(v & 0x7F);
            v >>= 7;
            if (v != 0)
                b |= 0x80;
            buffer.Add(b);
        } while (v != 0);
    }

    private static int ReadVarInt(byte[] data, ref int offset)
    {
        var value = 0;
        for (var shift = 0; shift < 35; shift += 7)
        {
            if (offset >= data.Length)
                throw new InvalidDataException(T("数据不完整"));
            var b = data[offset++];
            value |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return value;
        }
        throw new InvalidDataException(T("VarInt 过长"));
    }

    private static void WriteString(List<byte> buffer, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        WriteVarInt(buffer, bytes.Length);
        buffer.AddRange(bytes);
    }

    // ------------------------------------------------------------------ 1.4–1.6 旧协议

    private static async Task<ServerStatus> LegacyAsync(string host, int port, CancellationToken cancel)
    {
        using var tcp = new TcpClient { NoDelay = true };
        var watch = Stopwatch.StartNew();
        await tcp.ConnectAsync(host, port, cancel);
        var stream = tcp.GetStream();
        await stream.WriteAsync(new byte[] { 0xFE, 0x01 }, cancel);
        var head = new byte[3];
        await stream.ReadExactlyAsync(head, cancel);
        var latency = (int)watch.ElapsedMilliseconds;
        if (head[0] != 0xFF)
            throw new InvalidDataException(T("服务器没有响应 Minecraft 协议"));
        var chars = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(1));
        var body = new byte[chars * 2];
        await stream.ReadExactlyAsync(body, cancel);
        var text = Encoding.BigEndianUnicode.GetString(body);
        string version = "", motd;
        int online, max;
        if (text.StartsWith("§1\0", StringComparison.Ordinal))
        {
            var parts = text.Split('\0');
            version = parts.ElementAtOrDefault(2) ?? "";
            motd = parts.ElementAtOrDefault(3) ?? "";
            int.TryParse(parts.ElementAtOrDefault(4), out online);
            int.TryParse(parts.ElementAtOrDefault(5), out max);
        }
        else
        {
            var parts = text.Split('§');
            motd = string.Join("§", parts.SkipLast(2));
            int.TryParse(parts.ElementAtOrDefault(parts.Length - 2), out online);
            int.TryParse(parts.LastOrDefault(), out max);
        }
        var spans = new List<MotdSpan>();
        AppendLegacy(motd, new Style(), spans);
        return new ServerStatus(version, -1, online, max, [], spans, null, latency);
    }

    // ------------------------------------------------------------------ MOTD

    private record struct Style(string Color = null, bool Bold = false, bool Italic = false, bool Underline = false,
                                bool Strike = false);

    private static void Flatten(JsonNode node, Style style, List<MotdSpan> spans)
    {
        switch (node)
        {
            case null:
                return;
            case JsonArray array:
                foreach (var child in array)
                    Flatten(child, style, spans);
                return;
            case JsonObject obj:
                if (obj.Str("color") is { } color)
                    style = style with { Color = color.StartsWith('#') ? color : NamedColors.GetValueOrDefault(color, style.Color) };
                style = style with
                {
                    Bold = Flag(obj, "bold", style.Bold), Italic = Flag(obj, "italic", style.Italic),
                    Underline = Flag(obj, "underlined", style.Underline), Strike = Flag(obj, "strikethrough", style.Strike),
                };
                AppendLegacy(obj.Str("text") ?? obj.Str("translate") ?? "", style, spans);
                Flatten(obj["extra"], style, spans);
                return;
            default:
                AppendLegacy(node.AsStr() ?? "", style, spans);
                return;
        }
    }

    private static bool Flag(JsonObject obj, string key, bool fallback) =>
        obj[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;

    /// <summary>解析文字里的 § 格式代码。</summary>
    private static void AppendLegacy(string text, Style style, List<MotdSpan> spans)
    {
        var sb = new StringBuilder();
        void Flush()
        {
            if (sb.Length == 0)
                return;
            var span = new MotdSpan(sb.ToString(), style.Color, style.Bold, style.Italic, style.Underline, style.Strike);
            if (spans.Count > 0 && spans[^1] with { Text = span.Text } == span)
                spans[^1] = span with { Text = spans[^1].Text + span.Text };
            else
                spans.Add(span);
            sb.Clear();
        }
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '§' && i + 1 < text.Length)
            {
                var code = char.ToLowerInvariant(text[++i]);
                Flush();
                if (CodeColors.TryGetValue(code, out var color))
                    style = new Style(color);
                else
                    style = code switch
                    {
                        'l' => style with { Bold = true },
                        'o' => style with { Italic = true },
                        'n' => style with { Underline = true },
                        'm' => style with { Strike = true },
                        'r' => new Style(),
                        _ => style,
                    };
                continue;
            }
            sb.Append(text[i]);
        }
        Flush();
    }

    /// <summary>读取游戏自己的服务器列表（游戏目录下的 servers.dat）。</summary>
    public static List<ServerEntry> ReadServersDat(string gameDir)
    {
        var path = Path.Combine(gameDir, "servers.dat");
        if (!File.Exists(path))
            return [];
        var root = Instance.ReadNbt(File.ReadAllBytes(path));
        if (root.GetValueOrDefault("servers") is not List<object> list)
            return [];
        return list.OfType<Dictionary<string, object>>()
                   .Select(s => new ServerEntry
                   {
                       Name = s.GetValueOrDefault("name") as string ?? "",
                       Address = (s.GetValueOrDefault("ip") as string ?? "").Trim(),
                   })
                   .Where(s => s.Address.Length > 0)
                   .ToList();
    }

    public static string StripCodes(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '§')
                i++;
            else
                sb.Append(text[i]);
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ SRV

    private static readonly IPAddress[] FallbackDns = [IPAddress.Parse("223.5.5.5"), IPAddress.Parse("8.8.8.8")];

    private static List<IPAddress> DnsServers()
    {
        var list = new List<IPAddress>();
        try
        {
            list.AddRange(NetworkInterface.GetAllNetworkInterfaces()
                              .Where(n => n.OperationalStatus == OperationalStatus.Up
                                          && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                              .SelectMany(n => n.GetIPProperties().DnsAddresses)
                              .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !a.ToString().StartsWith("fec0")));
        }
        catch (Exception e) when (e is NetworkInformationException or PlatformNotSupportedException)
        {
        }
        list.AddRange(FallbackDns);
        return list.Distinct().Take(4).ToList();
    }

    /// <summary>查询 _minecraft._tcp.host 的 SRV 记录，查不到返回 null。</summary>
    public static async Task<(string Host, int Port)?> ResolveSrvAsync(string host, CancellationToken cancel)
    {
        var query = BuildSrvQuery("_minecraft._tcp." + host.TrimEnd('.'), out var id);
        foreach (var server in DnsServers())
        {
            try
            {
                using var udp = new UdpClient(AddressFamily.InterNetwork);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                cts.CancelAfter(1500);
                await udp.SendAsync(query, new IPEndPoint(server, 53), cts.Token);
                var result = await udp.ReceiveAsync(cts.Token);
                var answer = ParseSrvAnswer(result.Buffer, id, out var authoritative);
                if (answer != null || authoritative)
                    return answer;
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException or IndexOutOfRangeException
                                          or ArgumentOutOfRangeException)
            {
                if (cancel.IsCancellationRequested)
                    return null;
            }
        }
        return null;
    }

    private static byte[] BuildSrvQuery(string name, out ushort id)
    {
        id = (ushort)Random.Shared.Next(1, 65535);
        var q = new List<byte> { (byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in name.Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            q.Add((byte)bytes.Length);
            q.AddRange(bytes);
        }
        q.AddRange([0, 0, 33, 0, 1]);
        return q.ToArray();
    }

    /// <summary>解析 DNS 应答；authoritative 表示服务器明确答复了（包括"没有这条记录"），不必再问别的 DNS。</summary>
    private static (string Host, int Port)? ParseSrvAnswer(byte[] data, ushort id, out bool authoritative)
    {
        authoritative = false;
        if (data.Length < 12 || BinaryPrimitives.ReadUInt16BigEndian(data) != id)
            return null;
        var rcode = data[3] & 0x0F;
        authoritative = rcode is 0 or 3;
        var questions = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4));
        var answers = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(6));
        var offset = 12;
        for (var i = 0; i < questions; i++)
        {
            ReadName(data, ref offset);
            offset += 4;
        }
        (string Host, int Port, int Priority)? best = null;
        for (var i = 0; i < answers; i++)
        {
            ReadName(data, ref offset);
            var type = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));
            var length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 8));
            var rdata = offset + 10;
            offset = rdata + length;
            if (type != 33)
                continue;
            var priority = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(rdata));
            var port = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(rdata + 4));
            var target = rdata + 6;
            var host = ReadName(data, ref target);
            if (host.Length > 0 && (best == null || priority < best.Value.Priority))
                best = (host, port, priority);
        }
        return best is { } b ? (b.Host, b.Port) : null;
    }

    private static string ReadName(byte[] data, ref int offset)
    {
        var labels = new List<string>();
        var position = offset;
        var jumped = false;
        for (var guard = 0; guard < 128; guard++)
        {
            int length = data[position];
            if (length == 0)
            {
                position++;
                break;
            }
            if ((length & 0xC0) == 0xC0)
            {
                if (!jumped)
                    offset = position + 2;
                jumped = true;
                position = ((length & 0x3F) << 8) | data[position + 1];
                continue;
            }
            labels.Add(Encoding.ASCII.GetString(data, position + 1, length));
            position += length + 1;
        }
        if (!jumped)
            offset = position;
        return string.Join(".", labels);
    }
}
