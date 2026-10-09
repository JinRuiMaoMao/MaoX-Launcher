using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using static MaoX.Core.I18n;

namespace MaoX.Core;

/// <summary>正版账号的一件皮肤或披风。State 为 ACTIVE 表示正在使用。</summary>
public record MsaTexture(string Id, string State, string Url, string Variant, string Alias)
{
    public bool Active => State == "ACTIVE";
}

public record MsaProfile(List<MsaTexture> Skins, List<MsaTexture> Capes);

/// <summary>从正版玩家处获取到的皮肤与披风（PNG）。</summary>
public record PlayerTextures(byte[] Skin, bool Slim, byte[] Cape);

/// <summary>皮肤文件存放、正版玩家皮肤查询、微软账号换皮肤 / 披风。</summary>
public static class Skins
{
    public const string McSkinsUrl = "https://api.minecraftservices.com/minecraft/profile/skins";
    public const string McCapeUrl = "https://api.minecraftservices.com/minecraft/profile/capes/active";
    private const string MojangProfileByName = "https://api.mojang.com/users/profiles/minecraft/";
    private const string MojangSessionProfile = "https://sessionserver.mojang.com/session/minecraft/profile/";

    private static readonly byte[] PngSignature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    public static string Dir => Path.Combine(AppPaths.BaseDir, "skins");

    public static string PathFor(string hash) => Path.Combine(Dir, hash + ".png");

    public static bool Exists(string hash) => !string.IsNullOrEmpty(hash) && IsHash(hash) && File.Exists(PathFor(hash));

    public static bool IsHash(string text) => text.Length == 64 && text.All(Uri.IsHexDigit);

    public static bool IsPng(byte[] data) => data != null && data.AsSpan().StartsWith(PngSignature);

    /// <summary>读取 PNG 头里的宽高，不是 PNG 时返回 (0, 0)。</summary>
    public static (int Width, int Height) PngSize(byte[] data)
    {
        if (!IsPng(data) || data.Length < 24)
            return (0, 0);
        int Read(int offset) => (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
        return (Read(16), Read(20));
    }

    /// <summary>皮肤只接受 64×64 或旧版 64×32。</summary>
    public static string ValidateSkin(byte[] data)
    {
        if (!IsPng(data))
            return T("皮肤必须是 PNG 图片");
        return PngSize(data) is (64, 64) or (64, 32) ? null : T("皮肤图片的尺寸必须是 64×64 或 64×32");
    }

    /// <summary>披风宽高比 2:1，宽度为 64 的倍数（64×32、128×64…）。</summary>
    public static string ValidateCape(byte[] data)
    {
        if (!IsPng(data))
            return T("披风必须是 PNG 图片");
        var (w, h) = PngSize(data);
        return w >= 64 && w % 64 == 0 && w == h * 2 ? null : T("披风图片的尺寸必须是 64×32（或 128×64 等同比例）");
    }

    /// <summary>按内容哈希保存到 skins 目录，返回哈希。</summary>
    public static string Store(byte[] png)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(png));
        var path = PathFor(hash);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllBytes(path, png);
        }
        return hash;
    }

    public static byte[] Load(string hash) => Exists(hash) ? File.ReadAllBytes(PathFor(hash)) : null;

    private static async Task<HttpResult> SendAsync(string url, HttpMethod method = null, HttpContent body = null,
                                                    IDictionary<string, string> headers = null)
    {
        try
        {
            return await Http.SendAsync(url, method, body, headers, timeout: 30);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException)
        {
            if (TaskContext.Token.IsCancellationRequested)
                throw;
            throw new AccountException(F("网络连接失败：{0}", e is TaskCanceledException ? T("连接超时") : e.InnerException?.Message ?? e.Message), e);
        }
    }

    private static async Task<byte[]> FetchPngAsync(string url)
    {
        if (string.IsNullOrEmpty(url))
            return null;
        var result = await SendAsync(url);
        return result.Status == 200 && IsPng(result.Body) ? result.Body : null;
    }

    /// <summary>解析 textures 属性（Base64 JSON），返回皮肤地址、是否纤细模型、披风地址。</summary>
    public static (string Skin, bool Slim, string Cape) ParseTextures(JsonNode profile)
    {
        foreach (var prop in profile.Items("properties"))
        {
            if (prop.Str("name") != "textures")
                continue;
            try
            {
                var textures = Json.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(prop.Str("value") ?? ""))).Obj("textures");
                var skin = textures?.Obj("SKIN");
                return (skin?.Str("url"), skin?.Obj("metadata")?.Str("model") == "slim", textures?.Obj("CAPE")?.Str("url"));
            }
            catch (Exception e) when (e is FormatException or System.Text.Json.JsonException)
            {
                break;
            }
        }
        return (null, false, null);
    }

    /// <summary>按正版玩家名获取皮肤和披风。</summary>
    public static async Task<PlayerTextures> FetchPlayerAsync(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0)
            throw new AccountException(T("请填写正版玩家名"));
        var lookup = await SendAsync(MojangProfileByName + Uri.EscapeDataString(name));
        var id = lookup.Status == 200 ? lookup.Json().Str("id") : null;
        if (string.IsNullOrEmpty(id))
            throw new AccountException(lookup.Status is 200 or 204 or 404
                                           ? F("找不到正版玩家「{0}」", name)
                                           : F("查询正版玩家失败（HTTP {0}）", lookup.Status));
        var profile = await SendAsync(MojangSessionProfile + id);
        if (profile.Status != 200)
            throw new AccountException(F("获取玩家皮肤失败（HTTP {0}）", profile.Status));
        var (skinUrl, slim, capeUrl) = ParseTextures(profile.Json());
        var skin = await FetchPngAsync(skinUrl) ?? throw new AccountException(F("「{0}」使用的是默认皮肤", name));
        return new PlayerTextures(skin, slim, await FetchPngAsync(capeUrl));
    }

    // ------------------------------------------------------------------ 微软账号

    private static Dictionary<string, string> Bearer(Account account) => new() { ["Authorization"] = "Bearer " + account.AccessToken };

    private static MsaTexture ParseTexture(JsonNode node) =>
        new(node.Str("id"), node.Str("state"), node.Str("url"), node.Str("variant"), node.Str("alias"));

    private static AccountException MsaError(HttpResult result, string action)
    {
        if (result.Status == 401)
            return new AccountException(T("微软账号登录已过期，请删除账号后重新登录"));
        var data = result.Json();
        var detail = data.Str("errorMessage") ?? data.Str("error") ?? $"HTTP {result.Status}";
        return new AccountException(F("{0}失败：{1}", action, detail));
    }

    private static MsaProfile ParseProfile(JsonNode data) =>
        new(data.Items("skins").Select(ParseTexture).ToList(), data.Items("capes").Select(ParseTexture).ToList());

    public static async Task<MsaProfile> MsaProfileAsync(Account account)
    {
        var result = await SendAsync(Accounts.McProfileUrl, headers: Bearer(account));
        return result.Status == 200 ? ParseProfile(result.Json()) : throw MsaError(result, T("获取皮肤信息"));
    }

    public static async Task<MsaProfile> MsaUploadSkinAsync(Account account, byte[] png, bool slim)
    {
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        using var form = new MultipartFormDataContent
        {
            { new StringContent(slim ? "slim" : "classic"), "variant" },
            { file, "file", "skin.png" },
        };
        var result = await SendAsync(McSkinsUrl, HttpMethod.Post, form, Bearer(account));
        return result.Ok ? ParseProfile(result.Json()) : throw MsaError(result, T("上传皮肤"));
    }

    public static async Task MsaResetSkinAsync(Account account)
    {
        var result = await SendAsync(McSkinsUrl + "/active", HttpMethod.Delete, headers: Bearer(account));
        if (!result.Ok)
            throw MsaError(result, T("恢复默认皮肤"));
    }

    /// <summary>切换披风，capeId 为 null 时不显示披风。</summary>
    public static async Task MsaSetCapeAsync(Account account, string capeId)
    {
        var result = capeId == null
            ? await SendAsync(McCapeUrl, HttpMethod.Delete, headers: Bearer(account))
            : await SendAsync(McCapeUrl, HttpMethod.Put, Http.JsonContent(new JsonObject { ["capeId"] = capeId }), Bearer(account));
        if (!result.Ok)
            throw MsaError(result, T("切换披风"));
    }

    public static Task<byte[]> DownloadAsync(string url) => FetchPngAsync(url);
}

/// <summary>
/// 给离线账号用的本地验证服务器（Yggdrasil API 的最小实现），配合 authlib-injector 让游戏显示自定义皮肤和披风。
/// 只监听 127.0.0.1，随启动器进程存活。局域网联机时任何玩家名都能通过 hasJoined 校验。
/// </summary>
public sealed class OfflineSkinServer
{
    private static readonly Lazy<OfflineSkinServer> SharedInstance = new(() => new OfflineSkinServer());

    public static OfflineSkinServer Shared => SharedInstance.Value;

    private record Profile(string Id, string Name, string Skin, bool Slim, string Cape);

    private record Response(int Status, byte[] Body = null, string ContentType = "application/json; charset=utf-8");

    private readonly TcpListener _listener;
    private readonly RSA _key = RSA.Create(2048);
    private readonly ConcurrentDictionary<string, Profile> _profiles = new();

    public int Port { get; }

    public string Root => $"http://127.0.0.1:{Port}";

    /// <summary>每个请求的「方法 路径」，用于自检。</summary>
    public event Action<string> Requested;

    private OfflineSkinServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoop);
    }

    public void Register(Account account)
    {
        var id = account.Uuid.Replace("-", "").ToLowerInvariant();
        _profiles[id] = new Profile(id, account.Name, Skins.Exists(account.Skin) ? account.Skin : null, account.SkinSlim,
                                    Skins.Exists(account.Cape) ? account.Cape : null);
    }

    public string PublicKeyPem => _key.ExportSubjectPublicKeyInfoPem();

    public JsonObject Metadata() => new()
    {
        ["meta"] = new JsonObject
        {
            ["serverName"] = "MaoX 离线皮肤",
            ["implementationName"] = Mc.LauncherName,
            ["implementationVersion"] = Mc.LauncherVersion,
            ["feature.non_email_login"] = true,
        },
        ["skinDomains"] = new JsonArray("127.0.0.1", "localhost"),
        ["signaturePublickey"] = PublicKeyPem,
    };

    /// <summary>启动游戏用的 JVM 参数（authlib-injector 指向本服务器）。</summary>
    public List<string> JvmArgs(string injectorJar) =>
    [
        $"-javaagent:{injectorJar}={Root}",
        "-Dauthlibinjector.side=client",
        "-Dauthlibinjector.noShowServerName",
        "-Dauthlibinjector.noLogFile",
        "-Dauthlibinjector.yggdrasil.prefetched=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(Json.Serialize(Metadata()))),
    ];

    private async Task AcceptLoop()
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync();
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }
            _ = Task.Run(() => Serve(client));
        }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var stream = client.GetStream();
                var (method, target, body) = await ReadRequest(stream, timeout.Token);
                var path = target.Split('?', 2)[0];
                Requested?.Invoke($"{method} {target}");
                Response response;
                try
                {
                    response = Handle(method, path, Query(target), body);
                }
                catch (Exception e) when (e is System.Text.Json.JsonException or FormatException)
                {
                    response = Error(400, "IllegalArgumentException", e.Message);
                }
                await Write(stream, response, method == "HEAD", timeout.Token);
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or InvalidDataException)
            {
                // 客户端断开或请求格式不对，丢弃这个连接
            }
        }
    }

    private static async Task<(string Method, string Target, byte[] Body)> ReadRequest(NetworkStream stream, CancellationToken cancel)
    {
        var buffer = new byte[8192];
        var data = new MemoryStream();
        var headerEnd = -1;
        while (headerEnd < 0)
        {
            var read = await stream.ReadAsync(buffer, cancel);
            if (read == 0)
                throw new InvalidDataException("连接提前关闭");
            data.Write(buffer, 0, read);
            if (data.Length > 65536)
                throw new InvalidDataException("请求头过长");
            headerEnd = data.GetBuffer().AsSpan(0, (int)data.Length).IndexOf("\r\n\r\n"u8);
        }
        var all = data.ToArray();
        var lines = Encoding.UTF8.GetString(all, 0, headerEnd).Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length < 2)
            throw new InvalidDataException("请求行格式不对");
        var length = 0;
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0 && line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                length = Math.Clamp(int.TryParse(line[(colon + 1)..].Trim(), out var n) ? n : 0, 0, 1 << 20);
        }
        var body = new MemoryStream();
        body.Write(all, headerEnd + 4, all.Length - headerEnd - 4);
        while (body.Length < length)
        {
            var read = await stream.ReadAsync(buffer, cancel);
            if (read == 0)
                break;
            body.Write(buffer, 0, read);
        }
        return (first[0].ToUpperInvariant(), first[1], body.ToArray());
    }

    private static async Task Write(NetworkStream stream, Response response, bool headOnly, CancellationToken cancel)
    {
        var body = response.Body ?? [];
        var head = new StringBuilder($"HTTP/1.1 {response.Status} {Reason(response.Status)}\r\n");
        if (response.Status != 204)
            head.Append($"Content-Type: {response.ContentType}\r\nContent-Length: {body.Length}\r\n");
        head.Append("Connection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), cancel);
        if (!headOnly && response.Status != 204)
            await stream.WriteAsync(body, cancel);
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        204 => "No Content",
        400 => "Bad Request",
        404 => "Not Found",
        405 => "Method Not Allowed",
        _ => "Error",
    };

    private static Dictionary<string, string> Query(string target)
    {
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var index = target.IndexOf('?');
        if (index < 0)
            return query;
        foreach (var pair in target[(index + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            query[Uri.UnescapeDataString(parts[0].Replace('+', ' '))] =
                parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : "";
        }
        return query;
    }

    private static Response JsonResponse(JsonNode node) => new(200, Encoding.UTF8.GetBytes(Json.Serialize(node)));

    private static Response Error(int status, string error, string message) =>
        new(status, Encoding.UTF8.GetBytes(Json.Serialize(new JsonObject { ["error"] = error, ["errorMessage"] = message })));

    private Profile FindByName(string name) =>
        _profiles.Values.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? new Profile(Mc.OfflineUuid(name), name, null, false, null);

    private Response Handle(string method, string path, Dictionary<string, string> query, byte[] body)
    {
        path = path.TrimEnd('/');
        const string profilePrefix = "/sessionserver/session/minecraft/profile/";
        const string namePrefix = "/api/users/profiles/minecraft/";
        if (path == "")
            return JsonResponse(Metadata());
        if (path.StartsWith(profilePrefix, StringComparison.Ordinal))
        {
            var id = path[profilePrefix.Length..].Replace("-", "").ToLowerInvariant();
            return _profiles.TryGetValue(id, out var profile)
                ? JsonResponse(ProfileJson(profile, query.GetValueOrDefault("unsigned") == "false"))
                : new Response(204);
        }
        if (path == "/sessionserver/session/minecraft/join")
            return method == "POST" ? new Response(204) : Error(405, "Method Not Allowed", "use POST");
        if (path == "/sessionserver/session/minecraft/hasJoined")
        {
            var name = query.GetValueOrDefault("username");
            return string.IsNullOrEmpty(name) ? new Response(204) : JsonResponse(ProfileJson(FindByName(name), true));
        }
        if (path == "/api/profiles/minecraft" && method == "POST")
        {
            var names = (Json.Parse(body) as JsonArray).Items().Select(n => n.AsStr()).Where(n => !string.IsNullOrEmpty(n));
            var result = new JsonArray();
            foreach (var profile in names.Distinct(StringComparer.OrdinalIgnoreCase).Take(100).Select(FindByName))
                result.Add(new JsonObject { ["id"] = profile.Id, ["name"] = profile.Name });
            return JsonResponse(result);
        }
        if (path.StartsWith(namePrefix, StringComparison.Ordinal))
        {
            var profile = FindByName(Uri.UnescapeDataString(path[namePrefix.Length..]));
            return JsonResponse(new JsonObject { ["id"] = profile.Id, ["name"] = profile.Name });
        }
        if (path.StartsWith("/textures/", StringComparison.Ordinal))
        {
            var hash = path["/textures/".Length..];
            return Skins.IsHash(hash) && Skins.Exists(hash)
                ? new Response(200, File.ReadAllBytes(Skins.PathFor(hash)), "image/png")
                : Error(404, "Not Found", "texture not found");
        }
        if (path == "/minecraftservices/player/attributes")
            return JsonResponse(Json.Parse("""
                {"privileges":{"onlineChat":{"enabled":true},"multiplayerServer":{"enabled":true},
                 "multiplayerRealms":{"enabled":false},"telemetry":{"enabled":false}},
                 "profanityFilterPreferences":{"profanityFilterOn":false}}
                """));
        if (path == "/minecraftservices/privacy/blocklist")
            return JsonResponse(new JsonObject { ["blockedProfiles"] = new JsonArray() });
        return Error(404, "Not Found", "not found");
    }

    private JsonObject ProfileJson(Profile profile, bool signed)
    {
        var textures = new JsonObject();
        if (profile.Skin != null)
        {
            var skin = new JsonObject { ["url"] = $"{Root}/textures/{profile.Skin}" };
            if (profile.Slim)
                skin["metadata"] = new JsonObject { ["model"] = "slim" };
            textures["SKIN"] = skin;
        }
        if (profile.Cape != null)
            textures["CAPE"] = new JsonObject { ["url"] = $"{Root}/textures/{profile.Cape}" };
        var payload = new JsonObject
        {
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["profileId"] = profile.Id,
            ["profileName"] = profile.Name,
            ["textures"] = textures,
        };
        var value = Convert.ToBase64String(Encoding.UTF8.GetBytes(Json.Serialize(payload)));
        var property = new JsonObject { ["name"] = "textures", ["value"] = value };
        if (signed)
            property["signature"] = Sign(value);
        return new JsonObject
        {
            ["id"] = profile.Id,
            ["name"] = profile.Name,
            ["properties"] = new JsonArray(property),
        };
    }

    private string Sign(string value)
    {
        lock (_key)
            return Convert.ToBase64String(_key.SignData(Encoding.UTF8.GetBytes(value), HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1));
    }
}
