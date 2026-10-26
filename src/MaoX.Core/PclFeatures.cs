using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static MaoX.Core.I18n;

namespace MaoX.Core;

/// <summary>版本在启动器里的收藏、隐藏、分组、图标和备注（存在 launcher_config，不进版本文件夹）。</summary>
public class VersionMeta
{
    public bool Star { get; set; }
    public bool Hide { get; set; }
    public string Group { get; set; } = "";
    public string Note { get; set; } = "";
    public string Icon { get; set; } = "";
}

/// <summary>三档版本隔离：off 共用游戏目录；isolate / full 都以版本文件夹作为 --gameDir。full 再尽量把 assets 指到版本目录。</summary>
public static class Isolation
{
    public const string Off = "off";
    public const string Isolate = "isolate";
    public const string Full = "full";

    public static readonly (string Key, string Text, string Hint)[] Modes =
    [
        (Off, "不隔离", "所有版本共用存档、模组和设置"),
        (Isolate, "隔离", "每个版本独立存档、模组和设置"),
        (Full, "完全隔离", "独立存档、模组，并尽量把资源文件也放到版本目录"),
    ];

    public static string Normalize(string mode) =>
        mode is Off or Isolate or Full ? mode : Isolate;

    public static string FromLegacy(bool isolation) => isolation ? Isolate : Off;

    public static bool Isolates(string mode) => Normalize(mode) is Isolate or Full;

    public static string Resolve(JsonObject settings, LauncherConfig cfg)
    {
        var local = settings?.Str("isolation_mode");
        if (!string.IsNullOrWhiteSpace(local))
            return Normalize(local);
        var flag = settings?.BoolOrNull("isolation");
        if (flag != null)
            return FromLegacy(flag.Value);
        if (!string.IsNullOrWhiteSpace(cfg?.IsolationMode))
            return Normalize(cfg.IsolationMode);
        return FromLegacy(cfg?.VersionIsolation != false);
    }
}

/// <summary>自定义主页：预设、本地 JSON 或 URL，卡片含标题、说明和可选链接。</summary>
public static class Homepage
{
    public record Card(string Title, string Text, string Url);

    /// <summary>Key 为空是空白；preset:* 是内置；custom 只用于设置里的下拉，不会写进配置。</summary>
    public static readonly (string Key, string Name, string Hint)[] Presets =
    [
        ("", "默认动态", "Wiki 最新 / 即将推出，以及 minecraft.net 新闻"),
        ("preset:news", "官方新闻", "Minecraft 版本更新说明"),
        ("preset:help", "常见问题", "启动器内置帮助"),
        ("preset:mcnet", "Minecraft 官网", "官网、漏洞追踪和反馈入口"),
        ("custom", "自定义", "本地 JSON 文件或网址"),
    ];

    public static string PresetKey(string source)
    {
        source = (source ?? "").Trim();
        if (source.Length == 0)
            return "";
        if (source.StartsWith("preset:", StringComparison.OrdinalIgnoreCase))
            return source.ToLowerInvariant();
        return "custom";
    }

    public static List<Card> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        var node = JsonNode.Parse(json);
        var list = node is JsonArray arr ? arr : node?.Arr("cards");
        if (list == null)
            return [];
        return list.Items().Select(item => new Card(
            item.Str("title") ?? item.Str("name") ?? "",
            item.Str("text") ?? item.Str("description") ?? item.Str("body") ?? "",
            item.Str("url") ?? item.Str("link") ?? "")).Where(c =>
            !string.IsNullOrWhiteSpace(c.Title) || !string.IsNullOrWhiteSpace(c.Text)).ToList();
    }

    public static async Task<List<Card>> LoadAsync(string pathOrUrl, CancellationToken cancel = default)
    {
        pathOrUrl = (pathOrUrl ?? "").Trim();
        if (pathOrUrl.Length == 0 || pathOrUrl == "custom")
            return [];
        if (pathOrUrl.Equals("preset:news", StringComparison.OrdinalIgnoreCase))
        {
            var news = await VanillaNews.FetchAsync(cancel);
            return news.Take(8).Select(n => new Card(n.Title, Plain(n.Body), "")).ToList();
        }
        if (pathOrUrl.Equals("preset:help", StringComparison.OrdinalIgnoreCase))
            return HelpFaq.Items.Select(i => new Card(i.Q, i.A, "")).ToList();
        if (pathOrUrl.Equals("preset:mcnet", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                new Card("Minecraft.net", T("官方网站、下载和新闻"), "https://www.minecraft.net"),
                new Card(T("漏洞追踪"), T("向 Mojang 报告游戏问题"), "https://bugs.mojang.com"),
                new Card(T("反馈中心"), T("功能建议和玩家反馈"), "https://aka.ms/MinecraftFeedback"),
                new Card(T("Wiki"), T("Minecraft Wiki（中文）"), "https://zh.minecraft.wiki"),
            ];
        }
        if (pathOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || pathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var result = await Http.SendAsync(pathOrUrl, timeout: 12, cancel: cancel);
            if (!result.Ok)
                throw new DownloadException(F("主页下载失败（HTTP {0}）", result.Status));
            return Parse(result.Text);
        }
        var path = AppPaths.Rebase(pathOrUrl);
        if (!File.Exists(path))
            throw new FileNotFoundException(F("找不到主页文件：{0}", path));
        return Parse(await File.ReadAllTextAsync(path, cancel));
    }

    public static string Plain(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return "";
        var text = Regex.Replace(html, "<[^>]+>", " ");
        text = WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length > 160 ? text[..160] + "…" : text;
    }
}

/// <summary>Minecraft 官方更新说明（launchercontent.mojang.com）。</summary>
public static class VanillaNews
{
    public record Item(string Title, string Version, string Body);

    public static async Task<List<Item>> FetchAsync(CancellationToken cancel = default)
    {
        var result = await Http.SendAsync("https://launchercontent.mojang.com/javaPatchNotes.json",
                                          timeout: 15, cancel: cancel);
        if (!result.Ok)
            throw new DownloadException(F("无法获取官方新闻（HTTP {0}）", result.Status));
        return result.Json().Items("entries").Select(e => new Item(
            e.Str("title") ?? e.Str("version") ?? "",
            e.Str("version") ?? "",
            e.Str("body") ?? "")).Where(i => i.Title.Length > 0).ToList();
    }
}

/// <summary>启动器内置帮助。</summary>
public static class HelpFaq
{
    public static readonly (string Q, string A)[] Items =
    [
        ("游戏闪退或黑屏怎么办？", "先看启动页日志和版本设置里的崩溃分析。常见原因是 Java 版本不对、内存不够、模组冲突。可以打开自动分配内存，或到版本设置单独指定 Java。"),
        ("模组搜不到或装不上？", "可以用中文名搜，例如「钠」「小地图」。搜索用的是真实 Minecraft 版本号，自定义名称也能识别。下载时会尽量把 Fabric API、Cloth Config 等前置一并装上。换 BMCLAPI 源通常更快。"),
        ("版本隔离是什么？", "不隔离时所有版本共用存档和模组。隔离后每个版本用自己的文件夹。完全隔离还会尽量把资源文件也放到版本目录。"),
        ("外置登录皮肤不显示？", "用 LittleSkin、Blessing Skin 或 Ely.by 等皮肤站的认证服务器地址登录。默认已经填好 LittleSkin。"),
        ("iOS 上打不开游戏？", "需要 JIT。用 StikDebug / SideStore / TrollStore，或设置里打开内置 JIT（实验）。26.3 及以上目前还不能启动。"),
        ("启动器如何更新？", "设置里可以检查更新。更新频道选「预览」时会包含测试版。正式版会通过 GitHub Release 自动提示。"),
        ("怎么支持和反馈？", "爱发电可以支持开发，Discord 方便聊天和反馈，也可以在 GitHub 提 Issue。入口在设置「关于」和工具页「赞助」。"),
        ("背景音乐怎么放多首？", "和 PCL 一样：设置里打开音乐文件夹，把 MP3 / WAV / FLAC / OGG 等放进去，启动器会按文件名依次循环。也可以选单个文件，或自己指定另一个文件夹。"),
        ("手机上怎么选照片？", "换背景、皮肤或披风时会打开系统相册。选中的照片会复制到启动器里，相册权限不用全开。"),
    ];
}

/// <summary>常用外置登录皮肤站。</summary>
public static class SkinSites
{
    public static readonly (string Name, string Api)[] All =
    [
        ("LittleSkin", Accounts.LittleskinApi),
        ("Blessing Skin", "https://skin.prinzeugen.net/api/yggdrasil"),
        ("Ely.by", "https://authserver.ely.by/auth"),
    ];
}

/// <summary>节日主题色（覆盖用户主题色，可在设置里关闭）。</summary>
public static class Holiday
{
    public static readonly (string Key, string Name, DateTime Date, string Accent)[] All =
    [
        ("christmas", "圣诞节", new DateTime(2026, 12, 25), "#FACC15"),
        ("newyear", "元旦", new DateTime(2026, 1, 1), "#FACC15"),
        ("spring", "春节", new DateTime(2026, 2, 17), "#FF5C7A"),
        ("halloween", "万圣节", new DateTime(2026, 10, 31), "#FB923C"),
        ("aprilfools", "愚人节", new DateTime(2026, 4, 1), "#F472B6"),
    ];

    public static string AccentOrNull(DateTime? now = null)
    {
        var d = now ?? DateTime.Now;
        if (d.Month == 12 && d.Day is >= 20 and <= 26)
            return "#FACC15";
        if (d.Month == 1 && d.Day <= 3)
            return "#FACC15";
        if (d.Month == 2 && d.Day <= 20)
            return "#FF5C7A";
        if (d.Month == 10 && d.Day == 31)
            return "#FB923C";
        if (d.Month == 4 && d.Day == 1)
            return "#F472B6";
        return null;
    }

    public static string ResolveAccent(LauncherConfig cfg, DateTime? now = null)
    {
        if (cfg?.HolidayTheme == false)
            return cfg.AccentColor;
        return AccentOrNull(now) ?? cfg?.AccentColor;
    }
}

/// <summary>模组中文名（Modrinth / CurseForge slug → 译名），失败时保持英文。</summary>
public static class ModCn
{
    private static readonly Dictionary<string, string> BuiltIn = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sodium"] = "钠", ["iris"] = "虹膜", ["lithium"] = "锂", ["starlight"] = "星光",
        ["fabric-api"] = "Fabric API", ["quilted-fabric-api"] = "Quilted Fabric API",
        ["cloth-config"] = "Cloth Config", ["modmenu"] = "模组菜单",
        ["architectury-api"] = "Architectury API", ["jei"] = "JEI", ["rei"] = "REI",
        ["emi"] = "EMI", ["jade"] = "玉", ["xaeros-minimap"] = "Xaero 小地图",
        ["xaeros-world-map"] = "Xaero 世界地图", ["voicechat"] = "简单语音聊天",
        ["simple-voice-chat"] = "简单语音聊天", ["sodium-extra"] = "钠额外",
        ["indium"] = "铟", ["ferrite-core"] = "铁氧体核心", ["lazydfu"] = "延迟 DFU",
        ["entityculling"] = "实体剔除", ["immediatelyfast"] = "立刻快",
        ["modernfix"] = "现代修复", ["optifine"] = "高清修复",
    };

    private static Dictionary<string, string> _extra = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loading;

    public static string Title(string slug, string fallback)
    {
        slug = (slug ?? "").Trim();
        if (slug.Length > 0 && (BuiltIn.TryGetValue(slug, out var cn) || _extra.TryGetValue(slug, out cn)))
            return string.Equals(cn, fallback, StringComparison.OrdinalIgnoreCase) ? fallback : $"{cn} · {fallback}";
        return fallback;
    }

    public static bool HasCjk(string text)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        foreach (var c in text)
        {
            if (c is >= '\u4E00' and <= '\u9FFF')
                return true;
        }
        return false;
    }

    /// <summary>中文关键词对应的英文 slug，按匹配程度排序。没有中文或找不到时为空。</summary>
    public static List<string> ExpandQuery(string query)
    {
        query = (query ?? "").Trim();
        if (query.Length == 0 || !HasCjk(query))
            return [];
        var hits = new List<(int Score, string Slug)>();
        foreach (var (slug, cn) in Names())
        {
            var score = 0;
            if (string.Equals(cn, query, StringComparison.OrdinalIgnoreCase))
                score = 100;
            else if (cn.Contains(query, StringComparison.OrdinalIgnoreCase))
                score = 80;
            else if (query.Contains(cn, StringComparison.OrdinalIgnoreCase) && cn.Length >= 2)
                score = 50;
            if (score > 0)
                hits.Add((score, slug));
        }
        return hits.OrderByDescending(h => h.Score).Select(h => h.Slug)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
    }

    /// <summary>发给 Modrinth / CurseForge 的检索词：中文会先换成最匹配的英文 slug。</summary>
    public static string ApiQuery(string query)
    {
        var extra = ExpandQuery(query);
        return extra.Count > 0 ? extra[0] : query ?? "";
    }

    public static int RankScore(string query, string slug, string title)
    {
        query = (query ?? "").Trim();
        if (query.Length == 0)
            return 0;
        var display = Title(slug, title ?? "");
        if (display.Contains(query, StringComparison.OrdinalIgnoreCase)
            || (title ?? "").Contains(query, StringComparison.OrdinalIgnoreCase))
            return 2;
        return ExpandQuery(query).Contains(slug, StringComparer.OrdinalIgnoreCase) ? 1 : 0;
    }

    private static IEnumerable<(string Slug, string Cn)> Names()
    {
        foreach (var (slug, cn) in BuiltIn)
            yield return (slug, cn);
        foreach (var (slug, cn) in _extra)
        {
            if (!BuiltIn.ContainsKey(slug))
                yield return (slug, cn);
        }
    }

    public static async Task EnsureAsync(CancellationToken cancel = default)
    {
        if (_loading || _extra.Count > 0)
            return;
        _loading = true;
        try
        {
            var result = await Http.SendAsync(
                "https://cdn.jsdelivr.net/gh/MeowBot233/mod-translations@master/zh_cn.json",
                timeout: 12, cancel: cancel);
            if (!result.Ok)
                return;
            var json = result.Json();
            if (json is not JsonObject obj)
                return;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in obj)
            {
                var text = value?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(text))
                    map[key] = text;
            }
            if (map.Count > 0)
                _extra = map;
        }
        catch (Exception)
        {
        }
        finally
        {
            _loading = false;
        }
    }
}

/// <summary>从 Adoptium 下载指定主版本的 JDK，装到启动器 tools 目录。</summary>
public static class JavaAdoptium
{
    public static async Task<string> InstallAsync(int feature, Downloader dl, Action<int, int> progress = null)
    {
        var os = Platform.IsWindows ? "windows" : Platform.IsMac ? "mac" : "linux";
        var arch = Platform.IsArm ? "aarch64" : Platform.Is64Bit ? "x64" : "x86";
        var url = $"https://api.adoptium.net/v3/binary/latest/{feature}/ga/{os}/{arch}/jdk/hotspot/normal/eclipse?project=jdk";
        var dest = Path.Combine(AppPaths.ToolsDir, "jdk-" + feature);
        Directory.CreateDirectory(AppPaths.ToolsDir);
        var archive = Path.Combine(AppPaths.CacheDir, $"jdk-{feature}-adoptium{(Platform.IsWindows ? ".zip" : ".tar.gz")}");
        Directory.CreateDirectory(AppPaths.CacheDir);
        await dl.DownloadAsync(new DownloadTask(url, archive));
        progress?.Invoke(1, 2);
        if (Directory.Exists(dest))
        {
            try
            {
                Directory.Delete(dest, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        Directory.CreateDirectory(dest);
        await Task.Run(() => Extract(archive, dest));
        progress?.Invoke(2, 2);
        var java = FindJava(dest) ?? throw new InvalidOperationException(F("JDK {0} 解压后找不到 java", feature));
        if (!Platform.IsWindows)
            Platform.MakeExecutable(java);
        return java;
    }

    private static void Extract(string archive, string dest)
    {
        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archive, dest, true);
            return;
        }
        using var input = File.OpenRead(archive);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        System.Formats.Tar.TarFile.ExtractToDirectory(gzip, dest, overwriteFiles: true);
    }

    private static string FindJava(string root)
    {
        try
        {
            return Directory.GetFiles(root, Platform.JavaExecutable, SearchOption.AllDirectories)
                .FirstOrDefault(p => string.Equals(Path.GetFileName(Path.GetDirectoryName(p)), "bin",
                                                   StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>把日志上传到 mclo.gs，返回分享链接。</summary>
public static class Feedback
{
    public static async Task<string> UploadLogAsync(string text, CancellationToken cancel = default)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0)
            throw new InvalidOperationException(T("没有可以上传的日志"));
        if (text.Length > 10_000_000)
            text = text[^10_000_000..];
        using var body = new FormUrlEncodedContent(new Dictionary<string, string> { ["content"] = text });
        var result = await Http.SendAsync("https://api.mclo.gs/1/log", HttpMethod.Post, body, timeout: 20,
                                          cancel: cancel);
        if (!result.Ok)
            throw new DownloadException(F("上传日志失败（HTTP {0}）", result.Status));
        var url = result.Json().Str("url");
        if (string.IsNullOrEmpty(url))
            throw new DownloadException(T("上传成功但没有返回链接"));
        return url;
    }

    public static string LatestGameLog(string gameDir)
    {
        var path = Path.Combine(gameDir ?? "", "logs", "latest.log");
        return File.Exists(path) ? File.ReadAllText(path) : "";
    }
}

/// <summary>启动器背景音乐和启动音效。桌面端由宿主注册解码器（MP3 / FLAC / OGG / M4A 等），没有时回退到 Windows 的 wav。</summary>
public static class LauncherAudio
{
    public static readonly string[] Extensions =
        [".mp3", ".wav", ".flac", ".ogg", ".oga", ".m4a", ".aac", ".wma", ".aiff", ".aif", ".mp4", ".opus"];

    public static readonly string[] PickerPatterns = Extensions.Select(e => "*" + e).ToArray();

    /// <summary>桌面程序注册：path、是否背景音乐、是否单曲循环。成功返回 true。</summary>
    public static Func<string, bool, bool, bool> PlayImpl { get; set; }

    /// <summary>只停背景音乐（清除 BGM 时）。</summary>
    public static Action StopImpl { get; set; }

    /// <summary>真正暂停当前曲目；没有时 Pause 会停掉解码器。</summary>
    public static Action PauseImpl { get; set; }

    /// <summary>从暂停处继续。成功返回 true。</summary>
    public static Func<bool> ResumeImpl { get; set; }

    /// <summary>退出启动器时停掉所有声音。</summary>
    public static Action StopAllImpl { get; set; }

    /// <summary>0–1。正在播的时候立刻改音量。</summary>
    public static Action<float> SetVolumeImpl { get; set; }

    public static event Action Changed;

    private static readonly object State = new();
    private static List<string> _tracks = [];
    private static readonly HashSet<string> Failed = new(StringComparer.OrdinalIgnoreCase);
    private static int _index;
    private static string _source = "";
    private static float _volume = 0.7f;
    private static FileSystemWatcher _watch;
    private static CancellationTokenSource _reload;
    private static bool _paused;

    public static string Folder => AppPaths.MusicDir;
    public static string LastError { get; private set; }
    public static bool IsPlaying { get; private set; }

    public static float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 1);
            try
            {
                SetVolumeImpl?.Invoke(_volume);
            }
            catch (Exception)
            {
            }
            RaiseChanged();
        }
    }

    public static string CurrentTitle
    {
        get
        {
            lock (State)
            {
                if ((uint)_index >= (uint)_tracks.Count)
                    return "";
                return Path.GetFileNameWithoutExtension(_tracks[_index]);
            }
        }
    }

    public static int TrackCount
    {
        get
        {
            lock (State)
                return _tracks.Count;
        }
    }

    public static bool IsAudio(string path) =>
        Extensions.Contains(Path.GetExtension(path ?? "").ToLowerInvariant());

    /// <summary>文件就这一首；文件夹则按文件名列出里面的音频。空路径用默认 Music 文件夹。</summary>
    public static IReadOnlyList<string> ListTracks(string path)
    {
        path = AppPaths.Rebase((path ?? "").Trim());
        if (string.IsNullOrEmpty(path))
            path = Folder;
        try
        {
            if (Directory.Exists(path))
                return Directory.GetFiles(path).Where(IsAudio).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            if (File.Exists(path) && IsAudio(path))
                return [path];
        }
        catch (Exception)
        {
        }
        return [];
    }

    public static string EnsureFolder()
    {
        Directory.CreateDirectory(Folder);
        return Folder;
    }

    public static void Play(string path, bool loop)
    {
        try
        {
            if (loop)
            {
                StartBgm(path);
                return;
            }
            path = AppPaths.Rebase((path ?? "").Trim());
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return;
            if (PlayImpl != null)
            {
                if (!PlayImpl(path, false, false))
                    LastError = path;
                return;
            }
            PlayWavFallback(path, false);
        }
        catch (Exception e)
        {
            LastError = e.Message;
        }
    }

    public static void StartBgm(string path)
    {
        lock (State)
        {
            _source = path ?? "";
            ClearPlayback();
            _tracks = [.. ListTracks(path)];
            _index = 0;
            Watch(ResolveWatchDir(path));
            if (_tracks.Count == 0)
            {
                RaiseChanged();
                return;
            }
            PlayCurrent(_tracks.Count == 1);
        }
        RaiseChanged();
    }

    /// <summary>左键：正在播就停，停着就继续。</summary>
    public static void Toggle()
    {
        if (IsPlaying)
        {
            Pause();
            return;
        }
        Resume();
    }

    public static void Pause()
    {
        lock (State)
        {
            if (!IsPlaying)
                return;
            IsPlaying = false;
            _paused = true;
            try
            {
                if (PauseImpl != null)
                    PauseImpl();
                else
                    StopImpl?.Invoke();
            }
            catch (Exception)
            {
            }
        }
        RaiseChanged();
    }

    public static void Resume()
    {
        lock (State)
        {
            if (IsPlaying)
                return;
            if (_paused && ResumeImpl != null)
            {
                try
                {
                    if (ResumeImpl())
                    {
                        IsPlaying = true;
                        _paused = false;
                        RaiseChanged();
                        return;
                    }
                }
                catch (Exception)
                {
                }
            }
            _paused = false;
            if (_tracks.Count == 0)
            {
                _tracks = [.. ListTracks(_source)];
                _index = 0;
            }
            if (_tracks.Count == 0)
                return;
            PlayCurrent(_tracks.Count(t => !Failed.Contains(t)) <= 1);
        }
        RaiseChanged();
    }

    /// <summary>右键：下一首。停着的时候也会开始播。</summary>
    public static void Next()
    {
        lock (State)
        {
            if (_tracks.Count == 0)
                _tracks = [.. ListTracks(_source)];
            if (_tracks.Count == 0)
                return;
            _paused = false;
            try
            {
                StopImpl?.Invoke();
            }
            catch (Exception)
            {
            }
            IsPlaying = false;
            if (_tracks.Count > 1 && !SkipToNext())
                _index = 0;
            else if (_tracks.Count == 1)
                _index = 0;
            PlayCurrent(_tracks.Count(t => !Failed.Contains(t)) <= 1);
        }
        RaiseChanged();
    }

    /// <summary>一首播完后由桌面播放器调用，文件夹里的下一首接着放。</summary>
    public static void NotifyBgmEnded()
    {
        lock (State)
        {
            if (_paused || _tracks.Count <= 1)
                return;
            if (!SkipToNext())
                return;
            PlayCurrent(_tracks.Count(t => !Failed.Contains(t)) <= 1);
        }
        RaiseChanged();
    }

    private static void PlayCurrent(bool loop)
    {
        _paused = false;
        for (var i = 0; i < _tracks.Count; i++)
        {
            var file = _tracks[_index];
            try
            {
                var ok = PlayImpl != null ? PlayImpl(file, true, loop) : PlayWavOk(file, loop);
                if (ok)
                {
                    LastError = null;
                    IsPlaying = true;
                    return;
                }
                LastError = file;
            }
            catch (Exception e)
            {
                LastError = e.Message;
            }
            Failed.Add(file);
            IsPlaying = false;
            if (!SkipToNext())
                return;
            loop = _tracks.Count(t => !Failed.Contains(t)) <= 1;
        }
    }

    private static bool SkipToNext()
    {
        if (_tracks.Count == 0)
            return false;
        var start = _index;
        do
        {
            _index = (_index + 1) % _tracks.Count;
            if (!Failed.Contains(_tracks[_index]))
                return true;
        } while (_index != start);
        return false;
    }

    public static void Stop()
    {
        lock (State)
            ClearPlayback();
        RaiseChanged();
    }

    public static void StopAll()
    {
        lock (State)
        {
            _watch?.Dispose();
            _watch = null;
            _reload?.Cancel();
            ClearPlayback();
            if (StopAllImpl != null)
                StopAllImpl();
            else
                PlayWavFallback(null, false);
        }
        RaiseChanged();
    }

    private static void ClearPlayback()
    {
        _tracks = [];
        _index = 0;
        Failed.Clear();
        IsPlaying = false;
        _paused = false;
        if (StopImpl != null)
        {
            StopImpl();
            return;
        }
        PlayWavFallback(null, false);
    }

    private static void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception)
        {
        }
    }

    private static string ResolveWatchDir(string path)
    {
        path = AppPaths.Rebase((path ?? "").Trim());
        if (string.IsNullOrEmpty(path))
            return Folder;
        if (Directory.Exists(path))
            return path;
        if (File.Exists(path))
            return Path.GetDirectoryName(path);
        return Folder;
    }

    private static void Watch(string dir)
    {
        _watch?.Dispose();
        _watch = null;
        if (string.IsNullOrEmpty(dir))
            return;
        try
        {
            Directory.CreateDirectory(dir);
            var watcher = new FileSystemWatcher(dir)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
            };
            void Changed(object _, FileSystemEventArgs e)
            {
                if (IsAudio(e.Name))
                    ScheduleReload();
            }
            watcher.Created += Changed;
            watcher.Deleted += Changed;
            watcher.Changed += Changed;
            watcher.Renamed += (_, e) =>
            {
                if (IsAudio(e.Name) || IsAudio(e.OldName))
                    ScheduleReload();
            };
            _watch = watcher;
        }
        catch (Exception)
        {
        }
    }

    private static void ScheduleReload()
    {
        _reload?.Cancel();
        var cts = _reload = new CancellationTokenSource();
        var source = _source;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            var next = ListTracks(source);
            lock (State)
            {
                if (cts.IsCancellationRequested)
                    return;
                if (_tracks.Count == next.Count && _tracks.SequenceEqual(next, StringComparer.OrdinalIgnoreCase))
                    return;
            }
            try
            {
                StartBgm(source);
            }
            catch (Exception)
            {
            }
        });
    }

    private static bool PlayWavOk(string path, bool loop)
    {
        PlayWavFallback(path, loop);
        return true;
    }

    private static void PlayWavFallback(string path, bool loop)
    {
        if (!Platform.IsWindows)
            return;
        try
        {
            Native.PlaySound(null, IntPtr.Zero, 0);
            if (string.IsNullOrEmpty(path))
                return;
            var flags = Native.Filename | Native.Async | (loop ? Native.Loop : 0);
            Native.PlaySound(path, IntPtr.Zero, flags);
        }
        catch (Exception)
        {
        }
    }

    private static class Native
    {
        public const uint Async = 0x0001;
        public const uint Loop = 0x0008;
        public const uint Filename = 0x00020000;

        [DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode)]
        public static extern bool PlaySound(string sound, IntPtr hmod, uint flags);
    }
}

/// <summary>启动前后命令、进程优先级、游戏窗口标题。</summary>
public static class LaunchExtras
{
    public static readonly (string Key, string Text)[] Priorities =
    [
        ("normal", "正常"), ("below", "低于正常"), ("above", "高于正常"), ("high", "高"),
    ];

    public static void RunCommand(string command, string workDir, Action<string> log)
    {
        command = (command ?? "").Trim();
        if (command.Length == 0)
            return;
        try
        {
            var psi = Platform.IsWindows
                ? new ProcessStartInfo("cmd.exe", "/c " + command)
                : new ProcessStartInfo("/bin/sh", "-c " + command);
            psi.WorkingDirectory = Directory.Exists(workDir) ? workDir : AppPaths.BaseDir;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using var process = Process.Start(psi);
            if (process == null)
                return;
            if (!process.WaitForExit(60_000))
            {
                try
                {
                    process.Kill(true);
                }
                catch (Exception)
                {
                }
                log?.Invoke(T("[警告] 启动命令超时，已结束"));
                return;
            }
            var output = (process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd()).Trim();
            if (output.Length > 0)
                log?.Invoke(output.Length > 800 ? output[..800] + "…" : output);
        }
        catch (Exception e)
        {
            log?.Invoke(F("[警告] 启动命令失败：{0}", e.Message));
        }
    }

    public static void ApplyPriority(Process process, string priority)
    {
        if (process == null || process.HasExited)
            return;
        try
        {
            process.PriorityClass = priority switch
            {
                "below" => ProcessPriorityClass.BelowNormal,
                "above" => ProcessPriorityClass.AboveNormal,
                "high" => ProcessPriorityClass.High,
                _ => ProcessPriorityClass.Normal,
            };
        }
        catch (Exception)
        {
        }
    }

    public static void TrySetWindowTitle(Process process, string title)
    {
        if (!Platform.IsWindows || process == null || string.IsNullOrWhiteSpace(title))
            return;
        _ = Task.Run(async () =>
        {
            for (var i = 0; i < 12; i++)
            {
                await Task.Delay(1500);
                if (process.HasExited)
                    return;
                if (ApplyTitle(process.Id, title))
                    return;
            }
        });
    }

    private static bool ApplyTitle(int pid, string title)
    {
        var found = false;
        Native.EnumWindows((hwnd, _) =>
        {
            Native.GetWindowThreadProcessId(hwnd, out var windowPid);
            if (windowPid != (uint)pid || !Native.IsWindowVisible(hwnd))
                return true;
            var length = Native.GetWindowTextLength(hwnd);
            if (length <= 0)
                return true;
            var sb = new StringBuilder(length + 1);
            Native.GetWindowText(hwnd, sb, sb.Capacity);
            var current = sb.ToString();
            if (current.Contains("Minecraft", StringComparison.OrdinalIgnoreCase)
                || current.Contains("LWJGL", StringComparison.OrdinalIgnoreCase)
                || current.Contains("GLFW", StringComparison.OrdinalIgnoreCase))
            {
                Native.SetWindowText(hwnd, title);
                found = true;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static class Native
    {
        public delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool SetWindowText(IntPtr hWnd, string text);
    }
}

/// <summary>IPv6 优先的系统 DNS（未开 DoH 时使用）。</summary>
public static class Ipv6Connect
{
    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancel)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;
        IPAddress[] addrs;
        if (IPAddress.TryParse(host, out var literal))
            addrs = [literal];
        else
        {
            addrs = await Dns.GetHostAddressesAsync(host, cancel);
            addrs = addrs.OrderByDescending(a => a.AddressFamily == AddressFamily.InterNetworkV6).ToArray();
        }
        Exception last = null;
        foreach (var ip in addrs)
        {
            var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(ip, port), cancel);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception e)
            {
                last = e;
                socket.Dispose();
            }
        }
        throw last ?? new HttpRequestException(F("无法解析域名 {0}", host));
    }
}
