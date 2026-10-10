using System.Text;
using System.Text.Json.Nodes;
using static MaoX.Core.I18n;

namespace MaoX.Core;

public class AccountException : Exception
{
    public AccountException(string message, Exception inner = null) : base(message, inner)
    {
    }
}

public record YggdrasilProfile(string Id, string Name);

/// <summary>外置登录服务器：API 根地址（不含末尾斜杠）与元数据。</summary>
public record YggdrasilServer(string Api, JsonNode Meta)
{
    public string ServerName => Meta.Obj("meta")?.Str("serverName") is { Length: > 0 } name ? name : "外置登录";
}

/// <summary>authenticate / refresh 返回的令牌数据。</summary>
public class YggdrasilTokens
{
    public string AccessToken { get; init; }
    public string ClientToken { get; init; }
    public List<YggdrasilProfile> AvailableProfiles { get; init; } = [];
    /// <summary>服务器已自动选中的角色（只有一个角色时通常不为空），否则为 null，需要调用 YggdrasilSelectAsync。</summary>
    public YggdrasilProfile SelectedProfile { get; init; }
    public JsonNode Raw { get; init; }
}

/// <summary>微软设备码登录的代码信息。</summary>
public class MsaDeviceCode
{
    public string UserCode { get; init; }
    public string VerificationUri { get; init; }
    public string DeviceCode { get; init; }
    /// <summary>轮询间隔（秒）。</summary>
    public int Interval { get; init; } = 5;
    /// <summary>代码有效期（秒）。</summary>
    public int ExpiresIn { get; init; } = 900;
    public string Message { get; init; }
}

/// <summary>微软 OAuth 令牌。</summary>
public class MsaTokens
{
    public string AccessToken { get; init; }
    public string RefreshToken { get; init; }
    public int ExpiresIn { get; init; }
    public JsonNode Raw { get; init; }
}

/// <summary>账号：离线、外置登录（authlib-injector / Yggdrasil）、微软正版登录（设备码流程）。</summary>
public static class Accounts
{
    public static readonly string[] AuthlibInjectorMeta =
    [
        "https://authlib-injector.yushi.moe/artifact/latest.json",
        "https://bmclapi2.bangbang93.com/mirrors/authlib-injector/artifact/latest.json",
    ];

    public const string LittleskinApi = "https://littleskin.cn/api/yggdrasil";

    public const string MsDeviceCodeUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";
    public const string MsTokenUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";
    public const string MsScope = "XboxLive.signin offline_access";
    /// <summary>官方启动器的公共应用 ID，走 login.live.com，不需要自己在 Azure 注册应用。</summary>
    public const string BuiltinMsaClientId = "00000000402b5328";
    public const string LiveDeviceCodeUrl = "https://login.live.com/oauth20_connect.srf";
    public const string LiveTokenUrl = "https://login.live.com/oauth20_token.srf";
    public const string LiveScope = "service::user.auth.xboxlive.com::MBI_SSL";
    public const string XblAuthUrl = "https://user.auth.xboxlive.com/user/authenticate";
    public const string XstsAuthUrl = "https://xsts.auth.xboxlive.com/xsts/authorize";
    public const string McLoginUrl = "https://api.minecraftservices.com/authentication/login_with_xbox";
    public const string McProfileUrl = "https://api.minecraftservices.com/minecraft/profile";
    public const string MsaAppGuide = "https://learn.microsoft.com/zh-cn/entra/identity-platform/quickstart-register-app";

    public static readonly IReadOnlyDictionary<long, string> XstsErrors = new Dictionary<long, string>
    {
        [2148916233] = T("这个微软账号还没有创建 Xbox 档案，请先登录 xbox.com 完成创建"),
        [2148916235] = T("Xbox Live 在你所在的国家或地区不可用"),
        [2148916236] = T("该账号需要完成成人验证（韩国）"),
        [2148916237] = T("该账号需要完成成人验证（韩国）"),
        [2148916238] = T("未成年账号需要由家长添加到微软家庭组后才能登录"),
    };

    public static readonly IReadOnlyDictionary<string, string> TypeNames = new Dictionary<string, string>
    {
        ["offline"] = T("离线账号"),
        ["authlib"] = T("外置登录"),
        ["msa"] = T("微软账号"),
    };

    private static double Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    private static string Or(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrEmpty(v));

    // ------------------------------------------------------------------ HTTP

    /// <summary>4xx / 5xx 不抛异常，网络错误转换为 AccountException。</summary>
    private static async Task<HttpResult> RequestAsync(string url, HttpMethod method = null, HttpContent body = null,
                                                       IDictionary<string, string> headers = null)
    {
        try
        {
            return await Http.SendAsync(url, method, body, headers);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException
                                      or UriFormatException)
        {
            throw new AccountException(F("网络连接失败：{0}", e.InnerException?.Message ?? e.Message), e);
        }
        catch (TaskCanceledException e)
        {
            throw new AccountException(T("网络连接失败：连接超时"), e);
        }
    }

    private static async Task<(int Status, JsonNode Data)> JsonRequestAsync(
        string url, JsonNode payload = null, IDictionary<string, string> form = null,
        IDictionary<string, string> headers = null)
    {
        HttpContent body = payload != null ? Http.JsonContent(payload) : form != null ? Http.FormContent(form) : null;
        var result = await RequestAsync(url, body != null ? HttpMethod.Post : HttpMethod.Get, body, headers);
        return (result.Status, result.Json());
    }

    // ------------------------------------------------------------------ 离线

    public static Account OfflineAccount(string name) =>
        new() { Type = "offline", Name = name, Uuid = Mc.OfflineUuid(name) };

    /// <summary>账号类型说明，外置登录显示服务器名称。</summary>
    public static string Describe(Account account)
    {
        if (account.Type == "authlib")
            return T(Or(account.ServerName) ?? "外置登录");
        return TypeNames.GetValueOrDefault(account.Type ?? "", "");
    }

    // ------------------------------------------------------------------ 外置登录

    /// <summary>解析外置登录服务器地址（支持 API 地址指示 ALI），返回 API 根地址与元数据。</summary>
    public static async Task<YggdrasilServer> ResolveYggdrasilAsync(string url)
    {
        url = (url ?? "").Trim();
        if (url.Length == 0)
            throw new AccountException(T("请填写认证服务器地址"));
        if (!url.Contains("://"))
            url = "https://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var baseUri) || (baseUri.Scheme != "https" && baseUri.Scheme != "http"))
            throw new AccountException(T("这不是有效的外置登录（Yggdrasil）服务器地址"));
        var result = await RequestAsync(url);
        var location = result.Header("X-Authlib-Injector-API-Location");
        if (!string.IsNullOrEmpty(location) && Uri.TryCreate(baseUri, location, out var apiUri))
        {
            var api = apiUri.AbsoluteUri;
            if (api.TrimEnd('/') != url.TrimEnd('/'))
            {
                url = api;
                result = await RequestAsync(url);
            }
        }
        var meta = result.Json();
        if (result.Status != 200 || meta is not JsonObject obj || !obj.ContainsKey("signaturePublickey"))
            throw new AccountException(T("这不是有效的外置登录（Yggdrasil）服务器地址"));
        return new YggdrasilServer(url.TrimEnd('/'), meta);
    }

    private static AccountException YggdrasilError(int status, JsonNode data)
    {
        var message = Or(data.Str("errorMessage"), data.Str("error")) ?? $"HTTP {status}";
        if (data.Str("error") == "ForbiddenOperationException")
        {
            var lower = message.ToLowerInvariant();
            if (lower.Contains("credentials") || lower.Contains("password"))
                return new AccountException(T("邮箱或密码错误"));
            if (lower.Contains("token"))
                return new AccountException(T("登录已失效，请重新登录"));
        }
        return new AccountException(message);
    }

    private static YggdrasilProfile ParseProfile(JsonNode node) =>
        node is JsonObject && node.Str("id") != null ? new YggdrasilProfile(node.Str("id"), node.Str("name")) : null;

    private static YggdrasilTokens ParseTokens(JsonNode data, string fallbackClientToken = null) => new()
    {
        AccessToken = data.Str("accessToken"),
        ClientToken = data.Str("clientToken") ?? fallbackClientToken,
        AvailableProfiles = data.Items("availableProfiles").Select(ParseProfile).Where(p => p != null).ToList(),
        SelectedProfile = ParseProfile(data.Get("selectedProfile")),
        Raw = data,
    };

    /// <summary>登录外置账号。账号下没有角色时抛出异常；SelectedProfile 为空时需要让用户从 AvailableProfiles 中选择。</summary>
    public static async Task<YggdrasilTokens> YggdrasilLoginAsync(string api, string username, string password)
    {
        var clientToken = Guid.NewGuid().ToString("N");
        var (status, data) = await JsonRequestAsync(api + "/authserver/authenticate", new JsonObject
        {
            ["agent"] = new JsonObject { ["name"] = "Minecraft", ["version"] = 1 },
            ["username"] = username,
            ["password"] = password,
            ["clientToken"] = clientToken,
            ["requestUser"] = true,
        });
        if (status != 200)
            throw YggdrasilError(status, data);
        var tokens = ParseTokens(data, clientToken);
        if (tokens.AvailableProfiles.Count == 0)
            throw new AccountException(T("这个账号下还没有角色，请先在皮肤站创建一个角色"));
        return tokens;
    }

    /// <summary>多角色时选择其中一个，返回新的令牌数据。</summary>
    public static async Task<YggdrasilTokens> YggdrasilSelectAsync(string api, YggdrasilTokens tokens,
                                                                   YggdrasilProfile profile)
    {
        var (status, data) = await JsonRequestAsync(api + "/authserver/refresh", new JsonObject
        {
            ["accessToken"] = tokens.AccessToken,
            ["clientToken"] = tokens.ClientToken,
            ["selectedProfile"] = new JsonObject { ["id"] = profile.Id, ["name"] = profile.Name },
            ["requestUser"] = true,
        });
        if (status != 200)
            throw YggdrasilError(status, data);
        return ParseTokens(data, tokens.ClientToken);
    }

    public static Account AuthlibAccount(YggdrasilServer server, string username, YggdrasilTokens tokens,
                                         YggdrasilProfile profile) => new()
    {
        Type = "authlib",
        Api = server.Api,
        ServerName = server.ServerName,
        Username = username,
        Name = profile.Name,
        Uuid = profile.Id,
        AccessToken = tokens.AccessToken,
        ClientToken = tokens.ClientToken,
    };

    /// <summary>令牌失效时刷新，返回账号信息是否有更新。</summary>
    private static async Task<bool> AuthlibRefreshAsync(Account account)
    {
        var (status, _) = await JsonRequestAsync(account.Api + "/authserver/validate", new JsonObject
        {
            ["accessToken"] = account.AccessToken,
            ["clientToken"] = account.ClientToken,
        });
        if (status is 200 or 204)
            return false;
        (status, var data) = await JsonRequestAsync(account.Api + "/authserver/refresh", new JsonObject
        {
            ["accessToken"] = account.AccessToken,
            ["clientToken"] = account.ClientToken,
            ["requestUser"] = true,
        });
        if (status != 200)
            throw new AccountException(F("{0} 的登录已过期，请删除账号后重新登录", account.Name));
        account.AccessToken = data.Str("accessToken");
        var name = data.Obj("selectedProfile")?.Str("name");
        if (!string.IsNullOrEmpty(name))
            account.Name = name;
        return true;
    }

    /// <summary>下载 authlib-injector（官方或 BMCLAPI 镜像），返回 jar 路径。</summary>
    public static async Task<string> EnsureAuthlibInjectorAsync(Downloader dl, string toolsDir = null,
                                                               Action<string> log = null)
    {
        log ??= _ => { };
        var dest = Path.Combine(toolsDir ?? AppPaths.ToolsDir, "authlib-injector.jar");
        if (File.Exists(dest))
            return dest;
        var urls = AuthlibInjectorMeta.ToList();
        if (await dl.ResolvedSourceAsync() == "bmclapi")
            urls.Reverse();
        Exception last = null;
        foreach (var url in urls)
        {
            try
            {
                var meta = await dl.FetchJsonAsync(url, mirror: false);
                var download = meta.Str("download_url") ?? throw new DownloadException(T("缺少 download_url"));
                if (url.Contains("bmclapi") && !download.Contains("bmclapi"))
                {
                    var root = url[..url.LastIndexOf("/artifact/", StringComparison.Ordinal)];
                    download = $"{root}/artifact/{meta.Str("build_number")}/authlib-injector-{meta.Str("version")}.jar";
                }
                log(F("正在下载 authlib-injector {0}...", meta.Str("version") ?? ""));
                var tmp = dest + ".download";
                if (File.Exists(tmp))
                    File.Delete(tmp);
                await dl.DownloadManyAsync([new DownloadTask(download, tmp)]);
                var digest = Http.FileSha256(tmp);
                var expected = meta.Obj("checksums")?.Str("sha256");
                if (!string.IsNullOrEmpty(expected) && digest != expected.ToLowerInvariant())
                {
                    File.Delete(tmp);
                    throw new AccountException(T("authlib-injector 校验失败"));
                }
                File.Move(tmp, dest, true);
                return dest;
            }
            catch (Exception e)
            {
                last = e;
            }
        }
        throw new AccountException(F("下载 authlib-injector 失败：{0}", last?.Message), last);
    }

    // ------------------------------------------------------------------ 微软登录

    /// <summary>没填 Client ID 时使用内置的。</summary>
    public static string EffectiveClientId(string clientId) => Or(clientId?.Trim()) ?? BuiltinMsaClientId;

    private static bool IsLive(string clientId) => clientId == BuiltinMsaClientId;

    public static async Task<MsaDeviceCode> MsaDeviceCodeAsync(string clientId)
    {
        var live = IsLive(clientId);
        var form = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["scope"] = live ? LiveScope : MsScope,
        };
        if (live)
            form["response_type"] = "device_code";
        var (status, data) = await JsonRequestAsync(live ? LiveDeviceCodeUrl : MsDeviceCodeUrl, form: form);
        if (status != 200)
            throw new AccountException(F("无法发起微软登录：{0}",
                                         Or(data.Str("error_description"), data.Str("error")) ?? $"HTTP {status}"));
        return new MsaDeviceCode
        {
            UserCode = data.Str("user_code"),
            VerificationUri = data.Str("verification_uri"),
            DeviceCode = data.Str("device_code"),
            Interval = data.Int("interval", 5),
            ExpiresIn = data.Int("expires_in", 900),
            Message = data.Str("message"),
        };
    }

    private static MsaTokens ParseMsaTokens(JsonNode data) => new()
    {
        AccessToken = data.Str("access_token"),
        RefreshToken = data.Str("refresh_token"),
        ExpiresIn = data.Int("expires_in"),
        Raw = data,
    };

    /// <summary>轮询一次设备码登录结果：成功返回令牌，用户还没完成时返回 null。</summary>
    public static async Task<MsaTokens> MsaPollAsync(string clientId, string deviceCode)
    {
        var (status, data) = await JsonRequestAsync(IsLive(clientId) ? LiveTokenUrl : MsTokenUrl, form: new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            ["device_code"] = deviceCode,
        });
        if (status == 200)
            return ParseMsaTokens(data);
        var error = data.Str("error");
        if (error is "authorization_pending" or "slow_down")
            return null;
        if (error == "authorization_declined")
            throw new AccountException(T("你拒绝了登录授权"));
        if (error == "expired_token")
            throw new AccountException(T("登录代码已过期，请重新开始"));
        throw new AccountException(F("微软登录失败：{0}", Or(data.Str("error_description"), error) ?? status.ToString()));
    }

    /// <summary>按 Interval 轮询直到用户完成登录；超过有效期时抛出「登录代码已过期」。可通过 cancel 取消。</summary>
    public static async Task<MsaTokens> MsaWaitAsync(string clientId, MsaDeviceCode code,
                                                     CancellationToken cancel = default)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, code.Interval));
        var deadline = DateTime.UtcNow.AddSeconds(code.ExpiresIn);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(interval, cancel);
            var tokens = await MsaPollAsync(clientId, code.DeviceCode);
            if (tokens != null)
                return tokens;
        }
        throw new AccountException(T("登录代码已过期，请重新开始"));
    }

    private static async Task<MsaTokens> MsaRefreshTokenAsync(string clientId, string refreshToken)
    {
        var live = IsLive(clientId);
        var (status, data) = await JsonRequestAsync(live ? LiveTokenUrl : MsTokenUrl, form: new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken ?? "",
            ["scope"] = live ? LiveScope : MsScope,
        });
        if (status != 200)
            throw new AccountException(T("微软账号登录已过期，请删除账号后重新登录"));
        return ParseMsaTokens(data);
    }

    /// <summary>微软令牌 -> Xbox Live -> XSTS -> Minecraft，返回账号（不含 RefreshToken）。</summary>
    public static async Task<Account> MinecraftLoginAsync(string msToken, bool live = false)
    {
        var (status, xbl) = await JsonRequestAsync(XblAuthUrl, new JsonObject
        {
            ["Properties"] = new JsonObject
            {
                ["AuthMethod"] = "RPS",
                ["SiteName"] = "user.auth.xboxlive.com",
                ["RpsTicket"] = (live ? "t=" : "d=") + msToken,
            },
            ["RelyingParty"] = "http://auth.xboxlive.com",
            ["TokenType"] = "JWT",
        });
        if (status != 200)
            throw new AccountException(F("Xbox Live 登录失败（HTTP {0}）", status));
        (status, var xsts) = await JsonRequestAsync(XstsAuthUrl, new JsonObject
        {
            ["Properties"] = new JsonObject
            {
                ["SandboxId"] = "RETAIL",
                ["UserTokens"] = new JsonArray(xbl.Str("Token")),
            },
            ["RelyingParty"] = "rp://api.minecraftservices.com/",
            ["TokenType"] = "JWT",
        });
        if (status != 200)
        {
            var xerr = xsts.Long("XErr");
            throw new AccountException(XstsErrors.TryGetValue(xerr, out var known)
                                           ? known
                                           : F("Xbox 授权失败（{0}）", xerr != 0 ? xerr : status));
        }
        var claim = xsts.Obj("DisplayClaims")?.Items("xui").FirstOrDefault();
        var uhs = Or(claim?.Str("uhs"), xbl.Obj("DisplayClaims")?.Items("xui").FirstOrDefault()?.Str("uhs"));
        (status, var mc) = await JsonRequestAsync(McLoginUrl, new JsonObject
        {
            ["identityToken"] = $"XBL3.0 x={uhs};{xsts.Str("Token")}",
        });
        if (status == 403)
            throw new AccountException(T("这个 Client ID 还没有获得 Minecraft 接口权限。新注册的应用需要先向微软提交申请" +
                                         "（https://aka.ms/mce-reviewappid），审核通过后才能登录"));
        if (status != 200)
            throw new AccountException(F("Minecraft 登录失败（HTTP {0}）", status));
        var accessToken = mc.Str("access_token");
        (status, var profile) = await JsonRequestAsync(McProfileUrl, headers: new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer " + accessToken,
        });
        if (status == 404)
            throw new AccountException(T("这个微软账号还没有购买 Minecraft: Java 版"));
        if (status != 200)
            throw new AccountException(F("获取 Minecraft 档案失败（HTTP {0}）", status));
        return new Account
        {
            Type = "msa",
            Name = profile.Str("name"),
            Uuid = profile.Str("id"),
            AccessToken = accessToken,
            ExpiresAt = Now + mc.Long("expires_in", 86400),
            Xuid = claim?.Str("xid") ?? "",
        };
    }

    /// <summary>设备码登录成功后，用令牌完成 Minecraft 登录并得到完整的微软账号。</summary>
    public static async Task<Account> MsaAccountAsync(MsaTokens tokens, string clientId)
    {
        var account = await MinecraftLoginAsync(tokens.AccessToken, IsLive(clientId));
        account.RefreshToken = tokens.RefreshToken ?? "";
        account.MsaClientId = clientId;
        return account;
    }

    private static async Task<bool> MsaRefreshAsync(Account account, string configClientId)
    {
        if (account.ExpiresAt - 600 > Now)
            return false;
        // 续期必须用登录时的 Client ID；旧账号没记录，用设置里的
        var clientId = Or(account.MsaClientId) ?? Or(configClientId);
        if (string.IsNullOrEmpty(clientId))
            throw new AccountException(T("微软账号登录已过期，请删除账号后重新登录"));
        var tokens = await MsaRefreshTokenAsync(clientId, account.RefreshToken);
        var fresh = await MinecraftLoginAsync(tokens.AccessToken, IsLive(clientId));
        account.Type = fresh.Type;
        account.Name = fresh.Name;
        account.Uuid = fresh.Uuid;
        account.AccessToken = fresh.AccessToken;
        account.ExpiresAt = fresh.ExpiresAt;
        account.Xuid = fresh.Xuid;
        account.RefreshToken = Or(tokens.RefreshToken) ?? account.RefreshToken;
        return true;
    }

    // ------------------------------------------------------------------ 启动

    /// <summary>微软账号令牌快过期时续期（直接修改 account），返回账号信息是否有更新。</summary>
    public static Task<bool> RefreshAsync(Account account, LauncherConfig cfg) =>
        account.Type == "msa" ? MsaRefreshAsync(account, (cfg?.MsaClientId ?? "").Trim()) : Task.FromResult(false);

    /// <summary>
    /// 必要时刷新令牌（直接修改 account），返回启动用的身份信息与账号信息是否有更新（有更新时应保存配置）。
    /// localSkins 为 false 时离线账号不使用本地皮肤服务器（导出的启动脚本脱离启动器运行）。
    /// </summary>
    public static async Task<(LaunchAuth Auth, bool Changed)> PrepareLaunchAsync(
        Account account, LauncherConfig cfg, Downloader dl, string toolsDir = null, Action<string> log = null,
        bool localSkins = true)
    {
        switch (account.Type)
        {
            case "offline":
            {
                var jvmArgs = new List<string>();
                if (localSkins && (Skins.Exists(account.Skin) || Skins.Exists(account.Cape)))
                {
                    var jar = await EnsureAuthlibInjectorAsync(dl, toolsDir, log);
                    var server = OfflineSkinServer.Shared;
                    server.Register(account);
                    jvmArgs = server.JvmArgs(jar);
                }
                return (new LaunchAuth
                {
                    Name = account.Name, Uuid = account.Uuid, Token = account.Uuid, UserType = "msa", JvmArgs = jvmArgs,
                }, false);
            }
            case "msa":
            {
                var changed = await MsaRefreshAsync(account, (cfg?.MsaClientId ?? "").Trim());
                return (new LaunchAuth
                {
                    Name = account.Name, Uuid = account.Uuid, Token = account.AccessToken, UserType = "msa",
                    Xuid = account.Xuid,
                }, changed);
            }
            case "authlib":
            {
                var changed = await AuthlibRefreshAsync(account);
                var jar = await EnsureAuthlibInjectorAsync(dl, toolsDir, log);
                var result = await RequestAsync(account.Api);
                var jvmArgs = new List<string> { $"-javaagent:{jar}={account.Api}", "-Dauthlibinjector.side=client" };
                if (result.Status == 200 && result.Body.Length > 0)
                    jvmArgs.Add("-Dauthlibinjector.yggdrasil.prefetched=" + Convert.ToBase64String(result.Body));
                return (new LaunchAuth
                {
                    Name = account.Name, Uuid = account.Uuid, Token = account.AccessToken, UserType = "mojang",
                    JvmArgs = jvmArgs,
                }, changed);
            }
            default:
                throw new AccountException(T("未知的账号类型"));
        }
    }

    /// <summary>返回账号当前皮肤的地址，离线账号或没有皮肤时返回 null。</summary>
    public static async Task<string> SkinUrlAsync(Account account)
    {
        if (account.Type == "msa")
        {
            var (status, data) = await JsonRequestAsync(McProfileUrl, headers: new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer " + account.AccessToken,
            });
            var skins = status == 200 ? data.Items("skins").ToList() : [];
            var active = skins.FirstOrDefault(s => s.Str("state") == "ACTIVE") ?? skins.FirstOrDefault();
            return active?.Str("url");
        }
        if (account.Type == "authlib")
        {
            var (status, data) = await JsonRequestAsync(
                $"{account.Api}/sessionserver/session/minecraft/profile/{account.Uuid}");
            if (status != 200)
                return null;
            foreach (var prop in data.Items("properties"))
            {
                if (prop.Str("name") != "textures")
                    continue;
                try
                {
                    var textures = Json.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(prop.Str("value") ?? "")));
                    return textures.Obj("textures")?.Obj("SKIN")?.Str("url");
                }
                catch (Exception e) when (e is FormatException or System.Text.Json.JsonException)
                {
                    return null;
                }
            }
        }
        return null;
    }

    private static readonly byte[] PngSignature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>下载皮肤 PNG，没有皮肤或不是 PNG 时返回 null。</summary>
    public static async Task<byte[]> FetchSkinAsync(Account account)
    {
        var url = await SkinUrlAsync(account);
        if (string.IsNullOrEmpty(url))
            return null;
        var result = await RequestAsync(url);
        return result.Status == 200 && result.Body.AsSpan().StartsWith(PngSignature) ? result.Body : null;
    }
}
