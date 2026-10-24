using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using static MaoX.Core.I18n;

namespace MaoX.Core;

/// <summary>启动器数据目录。Windows 上与 exe 同目录（便携），macOS / Linux 上在用户数据目录。</summary>
public static partial class AppPaths
{
    public static readonly string BaseDir = ResolveBaseDir();

    [GeneratedRegex("^(/private)?/var/mobile/Containers/Data/Application/[0-9A-Fa-f-]+(?=/|$)")]
    private static partial Regex IosContainer();

    /// <summary>
    /// iOS 重新安装或更新 App 后，数据容器路径里的 UUID 可能会变，旧容器下的绝对路径不能再访问：换成当前容器下的同一位置。
    /// </summary>
    public static string Rebase(string path)
    {
        if (!Platform.IsIOS || string.IsNullOrEmpty(path))
            return path;
        var current = IosContainer().Match(BaseDir);
        var saved = IosContainer().Match(path);
        if (!current.Success || !saved.Success || saved.Value == current.Value)
            return path;
        return current.Value + path[saved.Length..];
    }

    public static string ConfigPath => Path.Combine(BaseDir, "launcher_config.json");
    public static string ToolsDir => Path.Combine(BaseDir, "tools");
    public static string CacheDir => Path.Combine(BaseDir, "cache");
    public static string DefaultMinecraftDir => Path.Combine(BaseDir, ".minecraft");

    private static string ResolveBaseDir()
    {
        var custom = Environment.GetEnvironmentVariable("MAOX_HOME");
        if (!string.IsNullOrWhiteSpace(custom))
            return Path.GetFullPath(custom);
        if (Platform.IsMac)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support", "MaoX Launcher");
        }
        if (Platform.IsLinux)
        {
            var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (string.IsNullOrEmpty(data))
                data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            return Path.Combine(data, "MaoX Launcher");
        }
        return Path.GetFullPath(AppContext.BaseDirectory);
    }
}

/// <summary>
/// 账号。离线 {type=offline, name, uuid}；外置 {type=authlib, api, server_name, username, name, uuid,
/// access_token, client_token}；微软 {type=msa, name, uuid, refresh_token, access_token, expires_at, xuid}。
/// </summary>
public class Account
{
    public string Type { get; set; } = "offline";
    public string Name { get; set; } = "";
    public string Uuid { get; set; } = "";
    public string AccessToken { get; set; }
    public string ClientToken { get; set; }
    public string Api { get; set; }
    public string ServerName { get; set; }
    public string Username { get; set; }
    public string RefreshToken { get; set; }
    public double ExpiresAt { get; set; }
    public string Xuid { get; set; }
    /// <summary>微软账号登录时用的 Client ID（续期要用同一个）。</summary>
    public string MsaClientId { get; set; }
    /// <summary>离线账号自定义皮肤 / 披风：skins 目录下 PNG 的 SHA-256。</summary>
    public string Skin { get; set; }
    public bool SkinSlim { get; set; }
    public string Cape { get; set; }

    public Account Clone() => (Account)MemberwiseClone();
}

public class LauncherConfig
{
    public static readonly JsonSerializerOptions Options = new(Json.Pretty)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public string Username { get; set; } = "Steve";
    public string MinecraftDir { get; set; } = AppPaths.DefaultMinecraftDir;
    /// <summary>额外的游戏目录（分组 / 多实例），当前使用的是 <see cref="MinecraftDir"/>。</summary>
    public List<GameDirEntry> ExtraDirs { get; set; } = [];
    /// <summary>HTTP 代理，例如 http://127.0.0.1:7890；空 = 系统默认。</summary>
    public string Proxy { get; set; } = "";
    /// <summary>用 DNS over HTTPS 解析域名（Cloudflare），减轻污染。</summary>
    public bool Doh { get; set; }
    /// <summary>收藏的资源，格式 source:id（如 modrinth:AANobbMI）。</summary>
    public List<string> FavoriteMods { get; set; } = [];
    /// <summary>手机触屏用陀螺仪辅助转视角。</summary>
    public bool GyroLook { get; set; }
    /// <summary>启动游戏前先检查 Java、磁盘和文件。</summary>
    public bool? PreLaunchCheck { get; set; }
    public bool PreLaunchCheckOn => PreLaunchCheck != false;
    /// <summary>已经看过的公告 id（GitHub Release tag）。</summary>
    public string LastAnnouncement { get; set; } = "";
    public string JavaPath { get; set; } = "";
    public int MaxMemory { get; set; } = 4096;
    /// <summary>空或 true = 按可用内存和模组数量自动分配（和 PCL 一样）；false = 使用 MaxMemory。</summary>
    public bool? AutoMemory { get; set; }
    /// <summary>空或 true = 使用调优的 G1GC，减少卡顿和多余占用。</summary>
    public bool? OptimizeMemory { get; set; }
    public bool AutoMemoryOn => AutoMemory != false;
    public bool OptimizeMemoryOn => OptimizeMemory != false;
    public string DownloadSource { get; set; } = "auto";
    public int DownloadThreads { get; set; } = 16;
    public int WindowWidth { get; set; } = 854;
    public int WindowHeight { get; set; } = 480;
    public bool VersionIsolation { get; set; } = true;
    /// <summary>off / isolate / full。空则按 <see cref="VersionIsolation"/> 迁移。</summary>
    public string IsolationMode { get; set; } = "";
    /// <summary>版本收藏、隐藏、分组、图标和备注。</summary>
    public Dictionary<string, VersionMeta> VersionMetas { get; set; } = [];
    /// <summary>启动页是否显示已隐藏的版本。</summary>
    public bool ShowHiddenVersions { get; set; }
    /// <summary>自定义主页 JSON 文件路径或 URL。</summary>
    public string Homepage { get; set; } = "";
    /// <summary>背景音乐 .wav 路径。</summary>
    public string Bgm { get; set; } = "";
    /// <summary>启动游戏时的音效 .wav 路径。</summary>
    public string LaunchSfx { get; set; } = "";
    /// <summary>节日自动换主题色。</summary>
    public bool? HolidayTheme { get; set; }
    public bool HolidayThemeOn => HolidayTheme != false;
    /// <summary>下载限速 KB/s，0 = 不限制。</summary>
    public int DownloadSpeedLimit { get; set; }
    /// <summary>解析域名时优先使用 IPv6。</summary>
    public bool PreferIpv6 { get; set; }
    /// <summary>stable / preview。</summary>
    public string UpdateChannel { get; set; } = "stable";
    /// <summary>启动游戏前执行的命令。</summary>
    public string PreLaunchCommand { get; set; } = "";
    /// <summary>游戏进程起来后执行的命令。</summary>
    public string PostLaunchCommand { get; set; } = "";
    /// <summary>normal / below / above / high。</summary>
    public string ProcessPriority { get; set; } = "normal";
    /// <summary>游戏窗口标题；空 = 默认。</summary>
    public string WindowTitle { get; set; } = "";
    /// <summary>启动游戏前自动备份上次备份之后玩过的存档</summary>
    public bool AutoBackupWorlds { get; set; } = true;
    /// <summary>每个存档保留的自动备份份数</summary>
    public int BackupKeep { get; set; } = 3;
    public string JvmArgs { get; set; } = "";
    public string LastVersion { get; set; } = "";
    /// <summary>启动页选中的游戏：java 或 MojangGames 里的 Id</summary>
    public string SelectedGame { get; set; } = "java";
    public List<Account> Accounts { get; set; } = [];
    public int AccountIndex { get; set; }
    public string MsaClientId { get; set; } = "";
    /// <summary>keep / minimize / hide</summary>
    public string AfterLaunch { get; set; } = "keep";
    public string Background { get; set; } = "";
    /// <summary>背景图上遮罩的不透明度（0–90）</summary>
    public int BackgroundMask { get; set; } = 40;
    public bool AutoCheckUpdate { get; set; } = true;
    /// <summary>用户选择"跳过"的版本，不再自动提示</summary>
    public string SkippedUpdate { get; set; } = "";
    public List<ServerEntry> Servers { get; set; } = [];
    /// <summary>dark / light / system</summary>
    public string Theme { get; set; } = "dark";
    public string AccentColor { get; set; } = "#00D9FF";
    /// <summary>界面语言：空 = 跟随系统、zh、en</summary>
    public string Language { get; set; } = "";
    /// <summary>iOS 开启 JIT 的方式：auto = 原有方式（StikDebug / SideStore / TrollStore），builtin = 内置 StikJIT（实验）</summary>
    public string IosJitMethod { get; set; } = "auto";
    /// <summary>内置 JIT 连接的设备地址（LocalDevVPN 里的 Device IP），空 = StikJIT 默认的 10.7.0.1</summary>
    public string IosJitAddress { get; set; } = "";
    /// <summary>手机触屏按键的自定义布局</summary>
    public List<ControlLayout> ControlLayouts { get; set; } = [];
    /// <summary>正在使用的布局名称，空 = 默认布局</summary>
    public string ActiveControlLayout { get; set; } = "";

    public ControlLayout CurrentControlLayout() => ControlLayouts.FirstOrDefault(l => l.Name == ActiveControlLayout);

    public static LauncherConfig Load()
    {
        try
        {
            if (File.Exists(AppPaths.ConfigPath))
            {
                var cfg = JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllText(AppPaths.ConfigPath), Options);
                if (cfg != null)
                {
                    cfg.Accounts ??= [];
                    cfg.Servers ??= [];
                    cfg.ControlLayouts ??= [];
                    cfg.ExtraDirs ??= [];
                    cfg.FavoriteMods ??= [];
                    cfg.VersionMetas ??= [];
                    if (string.IsNullOrWhiteSpace(cfg.IsolationMode))
                        cfg.IsolationMode = Isolation.FromLegacy(cfg.VersionIsolation);
                    else
                    {
                        cfg.IsolationMode = Isolation.Normalize(cfg.IsolationMode);
                        cfg.VersionIsolation = Isolation.Isolates(cfg.IsolationMode);
                    }
                    if (string.IsNullOrWhiteSpace(cfg.UpdateChannel))
                        cfg.UpdateChannel = "stable";
                    if (string.IsNullOrWhiteSpace(cfg.ProcessPriority))
                        cfg.ProcessPriority = "normal";
                    cfg.HolidayTheme ??= true;
                    cfg.Homepage = AppPaths.Rebase(cfg.Homepage);
                    cfg.Bgm = AppPaths.Rebase(cfg.Bgm);
                    cfg.LaunchSfx = AppPaths.Rebase(cfg.LaunchSfx);
                    foreach (var dir in cfg.ExtraDirs)
                        dir.Path = AppPaths.Rebase(dir.Path ?? "");
                    foreach (var layout in cfg.ControlLayouts)
                    {
                        layout.Items ??= [];
                        layout.Buttons ??= [];
                        layout.Mode ??= ControlLayout.Classic;
                        layout.Movement ??= ControlLayout.Joystick;
                        if (layout.Sensitivity <= 0)
                            layout.Sensitivity = 1;
                        foreach (var button in layout.Buttons)
                            button.Keys ??= [];
                    }
                    if (string.IsNullOrWhiteSpace(cfg.MinecraftDir))
                        cfg.MinecraftDir = AppPaths.DefaultMinecraftDir;
                    cfg.MinecraftDir = AppPaths.Rebase(cfg.MinecraftDir);
                    cfg.JavaPath = AppPaths.Rebase(cfg.JavaPath);
                    cfg.Background = AppPaths.Rebase(cfg.Background);
                    cfg.AutoMemory ??= true;
                    cfg.OptimizeMemory ??= true;
                    cfg.PreLaunchCheck ??= true;
                    return cfg;
                }
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return new LauncherConfig();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.BaseDir);
        var tmp = AppPaths.ConfigPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
        File.Move(tmp, AppPaths.ConfigPath, true);
    }

    public LauncherConfig Clone() =>
        JsonSerializer.Deserialize<LauncherConfig>(JsonSerializer.Serialize(this, Options), Options);

    /// <summary>当前游戏目录 + 额外目录，去重后按名称排序。</summary>
    public List<GameDirEntry> AllGameDirs()
    {
        var list = new List<GameDirEntry>();
        var seen = new HashSet<string>(Platform.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        void Add(string name, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            path = Path.GetFullPath(path);
            if (!seen.Add(path))
                return;
            list.Add(new GameDirEntry { Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(path) : name, Path = path });
        }
        Add(T("默认"), MinecraftDir);
        foreach (var dir in ExtraDirs ?? [])
            Add(dir.Name, dir.Path);
        return list;
    }
}

/// <summary>一个额外的 Minecraft 游戏目录。</summary>
public class GameDirEntry
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
}
