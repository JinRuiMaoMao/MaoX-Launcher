using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static MaoX.Core.I18n;

namespace MaoX.Core;

/// <summary>启动时使用的身份信息。</summary>
public class LaunchAuth
{
    public string Name { get; init; }
    public string Uuid { get; init; }
    public string Token { get; init; }
    public string UserType { get; init; } = "msa";
    public string Xuid { get; init; }
    public List<string> JvmArgs { get; init; } = [];
}

public class PreparedGame
{
    public JsonObject VJson { get; init; }
    public List<string> Classpath { get; init; }
    public string NativesDir { get; init; }
    public string GameDir { get; init; }
    public string GameAssets { get; init; }
    public string LoggingArg { get; init; }
}

public record LoaderInfo(string Loader, string LoaderVersion, string Game);

public static partial class Mc
{
    public const string LauncherName = "MaoXLauncher";
    public static readonly string LauncherVersion =
        typeof(Mc).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion
        ?? "0.0.0";
    public const string VersionManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    public const string LibrariesUrl = "https://libraries.minecraft.net/";
    public const string ResourcesUrl = "https://resources.download.minecraft.net/";
    public const string VersionSettingsFile = "maox.json";

    public static readonly (string Old, string New)[] LegacyMaven =
    [
        ("http://files.minecraftforge.net/maven/", "https://maven.minecraftforge.net/"),
        ("https://files.minecraftforge.net/maven/", "https://maven.minecraftforge.net/"),
        ("http://", "https://"),
    ];

    public static readonly string[] FallbackMavens =
    [
        "https://maven.minecraftforge.net/",
        "https://libraries.minecraft.net/",
        "https://repo1.maven.org/maven2/",
    ];

    private static readonly Dictionary<string, string> LoaderLibraries = new()
    {
        ["org.quiltmc:quilt-loader"] = "quilt",
        ["net.fabricmc:fabric-loader"] = "fabric",
        ["net.neoforged:neoforge"] = "neoforge",
        ["net.neoforged:forge"] = "neoforge",
        ["net.minecraftforge:forge"] = "forge",
        ["optifine:OptiFine"] = "optifine",
    };

    public static readonly Dictionary<string, string> LoaderNames = new()
    {
        ["forge"] = "Forge",
        ["neoforge"] = "NeoForge",
        ["fabric"] = "Fabric",
        ["quilt"] = "Quilt",
        ["optifine"] = "OptiFine",
    };

    [GeneratedRegex(@"\$\{(\w+)\}")]
    public static partial Regex Placeholder();

    public static bool RuleMatches(JsonNode rule, IReadOnlyDictionary<string, bool> features)
    {
        var os = rule.Obj("os");
        if (os != null)
        {
            var name = os.Str("name");
            if (name != null && name != Platform.OsName && !(name == "macos" && Platform.IsMac))
                return false;
            var arch = os.Str("arch");
            if (arch != null && arch != Platform.Arch)
                return false;
            var version = os.Str("version");
            if (version != null && !Regex.IsMatch(Platform.OsVersion, version))
                return false;
        }
        var required = rule.Obj("features");
        if (required != null)
        {
            foreach (var (key, expected) in required)
            {
                var actual = features != null && features.TryGetValue(key, out var v) && v;
                if (actual != (expected?.GetValue<bool>() ?? false))
                    return false;
            }
        }
        return true;
    }

    public static bool RulesAllow(JsonArray rules, IReadOnlyDictionary<string, bool> features = null)
    {
        if (rules == null || rules.Count == 0)
            return true;
        var allowed = false;
        foreach (var rule in rules.Items())
        {
            if (RuleMatches(rule, features))
                allowed = rule.Str("action") == "allow";
        }
        return allowed;
    }

    /// <summary>group:artifact:version[:classifier][@ext] -> group/path/artifact/version/artifact-version[-classifier].ext</summary>
    public static string MavenPath(string name)
    {
        var ext = "jar";
        var at = name.IndexOf('@');
        if (at >= 0)
        {
            ext = name[(at + 1)..];
            name = name[..at];
        }
        var parts = name.Split(':');
        var file = $"{parts[1]}-{parts[2]}";
        if (parts.Length > 3)
            file += "-" + parts[3];
        return string.Join("/", parts[0].Split('.').Concat([parts[1], parts[2], file + "." + ext]));
    }

    /// <summary>与 Java 的 UUID.nameUUIDFromBytes("OfflinePlayer:" + name) 一致，返回 32 位十六进制。</summary>
    public static string OfflineUuid(string name)
    {
        var digest = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
        digest[6] = (byte)((digest[6] & 0x0F) | 0x30);
        digest[8] = (byte)((digest[8] & 0x3F) | 0x80);
        return Convert.ToHexStringLower(digest);
    }

    public static (string Loader, string Version) LoaderFromJson(JsonNode data)
    {
        var args = data.Get("arguments").Items("game").Where(a => a.IsString()).Select(a => a.AsStr()).ToList();
        foreach (var (flag, loader) in new[] { ("--fml.neoForgeVersion", "neoforge"), ("--fml.forgeVersion", "forge") })
        {
            var i = args.IndexOf(flag);
            if (i >= 0)
                return (loader, i + 1 < args.Count ? args[i + 1] : null);
        }
        foreach (var lib in data.Items("libraries"))
        {
            var parts = (lib.Str("name") ?? "").Split(':');
            if (parts.Length >= 3 && LoaderLibraries.TryGetValue(parts[0] + ":" + parts[1], out var loader))
            {
                string version;
                if (loader == "optifine")
                    version = parts[2].Contains('_') ? parts[2][(parts[2].IndexOf('_') + 1)..] : parts[2];
                else if (parts[1] == "forge")
                    version = parts[2].Contains('-') ? parts[2][(parts[2].IndexOf('-') + 1)..] : parts[2];
                else
                    version = parts[2];
                return (loader, version);
            }
        }
        if ((data.Str("minecraftArguments") ?? "").Contains("FMLTweaker"))
            return ("forge", null);
        return (null, null);
    }

    public static JsonObject MergeVersion(JsonObject parent, JsonObject child)
    {
        var merged = (JsonObject)parent.DeepClone();
        foreach (var (key, value) in child)
        {
            if (key == "libraries")
            {
                var libs = new JsonArray();
                foreach (var lib in ((JsonArray)value).Items())
                    libs.Add(lib.DeepClone());
                foreach (var lib in parent.Items("libraries"))
                    libs.Add(lib.DeepClone());
                merged["libraries"] = libs;
            }
            else if (key == "arguments")
            {
                var args = new JsonObject();
                foreach (var kind in new[] { "game", "jvm" })
                {
                    var list = new JsonArray();
                    foreach (var a in parent.Get("arguments").Items(kind))
                        list.Add(a.DeepClone());
                    foreach (var a in value.Items(kind))
                        list.Add(a.DeepClone());
                    args[kind] = list;
                }
                merged["arguments"] = args;
            }
            else if (key != "inheritsFrom")
            {
                merged[key] = value?.DeepClone();
            }
        }
        return merged;
    }
}

/// <summary>把 log4j 的 XML 控制台输出转换成普通文本行。</summary>
public partial class LogParser
{
    private string _prefix = "";
    private List<string> _buffer;

    [GeneratedRegex("(\\w+)=\"([^\"]*)\"")]
    private static partial Regex Attribute();

    public List<string> Feed(string line)
    {
        if (_buffer != null)
        {
            var end = line.IndexOf("]]>", StringComparison.Ordinal);
            if (end < 0)
            {
                _buffer.Add(line);
                return [];
            }
            _buffer.Add(line[..end]);
            var text = string.Join("\n", _buffer);
            _buffer = null;
            return [_prefix + text];
        }

        var stripped = line.Trim();
        if (stripped.StartsWith("<log4j:Event", StringComparison.Ordinal))
        {
            var attrs = Attribute().Matches(stripped).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
            var clock = attrs.TryGetValue("timestamp", out var ts) && long.TryParse(ts, out var ms)
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("HH:mm:ss")
                : "";
            _prefix = $"[{clock}] [{attrs.GetValueOrDefault("thread", "")}/{attrs.GetValueOrDefault("level", "")}]: ";
            return [];
        }
        var start = line.IndexOf("<![CDATA[", StringComparison.Ordinal);
        if (start >= 0)
        {
            var rest = line[(start + 9)..];
            var end = rest.IndexOf("]]>", StringComparison.Ordinal);
            if (end >= 0)
                return [_prefix + rest[..end]];
            _buffer = [rest];
            return [];
        }
        if (stripped.StartsWith("<log4j:", StringComparison.Ordinal) || stripped.StartsWith("</log4j:", StringComparison.Ordinal))
            return [];
        return [line];
    }
}

/// <summary>逐行读取游戏输出：优先按 UTF-8 解码，失败时按系统编码（如中文 Windows 的 GBK）解码。</summary>
public static class GameOutput
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static Encoding _fallback;

    private static Encoding Fallback
    {
        get
        {
            if (_fallback != null)
                return _fallback;
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                _fallback = Platform.IsWindows
                    ? Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage)
                    : Encoding.UTF8;
            }
            catch (Exception)
            {
                _fallback = Encoding.UTF8;
            }
            return _fallback;
        }
    }

    public static string Decode(byte[] raw, int count)
    {
        try
        {
            return StrictUtf8.GetString(raw, 0, count);
        }
        catch (DecoderFallbackException)
        {
            return Fallback.GetString(raw, 0, count);
        }
    }

    /// <summary>读取流直到结束，每读到一行调用一次 onLine（不含换行符）。</summary>
    public static void ReadLines(Stream stream, Action<string> onLine)
    {
        var buffer = new byte[8192];
        var line = new MemoryStream();
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] == (byte)'\n')
                {
                    Emit(line, onLine);
                    line.SetLength(0);
                }
                else
                {
                    line.WriteByte(buffer[i]);
                }
            }
        }
        if (line.Length > 0)
            Emit(line, onLine);
    }

    private static void Emit(MemoryStream line, Action<string> onLine)
    {
        var bytes = line.GetBuffer();
        var count = (int)line.Length;
        if (count > 0 && bytes[count - 1] == (byte)'\r')
            count--;
        onLine(Decode(bytes, count));
    }
}

public class GameLauncher
{
    public LauncherConfig Cfg { get; private set; }
    public Action<string> Log { get; }
    public Action<int, int, string> Progress { get; }
    public Downloader Dl { get; }
    public JsonNode Manifest { get; set; }

    public GameLauncher(LauncherConfig cfg, Action<string> log = null, Action<int, int, string> progress = null)
    {
        Cfg = cfg;
        Log = log ?? (_ => { });
        Progress = progress ?? ((_, _, _) => { });
        Dl = new Downloader(cfg.DownloadSource, cfg.DownloadThreads);
    }

    public string McDir => Path.GetFullPath(Cfg.MinecraftDir);

    public string PathOf(params string[] parts) => Path.Combine([McDir, .. parts]);

    public string VersionDir(string versionId) => PathOf("versions", versionId);

    public string VersionJsonPath(string versionId) => PathOf("versions", versionId, versionId + ".json");

    // ------------------------------------------------------------------ 版本管理

    public async Task<JsonNode> GetManifestAsync()
    {
        if (Manifest == null)
        {
            Log(T("正在获取版本列表..."));
            Manifest = await Dl.FetchJsonAsync(Mc.VersionManifestUrl);
        }
        return Manifest;
    }

    /// <summary>已安装的版本，按最近修改时间排序。</summary>
    public List<string> InstalledVersions()
    {
        var root = PathOf("versions");
        if (!Directory.Exists(root))
            return [];
        return Directory.GetDirectories(root)
            .Select(Path.GetFileName)
            .Where(name => File.Exists(VersionJsonPath(name)))
            .OrderByDescending(name => File.GetLastWriteTimeUtc(VersionJsonPath(name)))
            .ToList();
    }

    public async Task EnsureVersionJsonAsync(string versionId)
    {
        var jsonPath = VersionJsonPath(versionId);
        if (!File.Exists(jsonPath))
        {
            var manifest = await GetManifestAsync();
            var entry = manifest.Items("versions").FirstOrDefault(v => v.Str("id") == versionId)
                        ?? throw new InvalidOperationException(F("找不到版本 {0}", versionId));
            Log(F("正在下载版本信息 {0}...", versionId));
            await Dl.DownloadManyAsync([new DownloadTask(entry.Str("url"), jsonPath, entry.Str("sha1"))]);
        }
        var parent = Json.ReadFile(jsonPath).Str("inheritsFrom");
        if (!string.IsNullOrEmpty(parent))
            await EnsureVersionJsonAsync(parent);
    }

    /// <summary>读取版本 JSON 并合并 inheritsFrom，额外字段 _jar 表示游戏本体 jar 所在的版本。</summary>
    public JsonObject LoadVersion(string versionId)
    {
        var data = (JsonObject)Json.ReadFile(VersionJsonPath(versionId));
        var parentId = data.Str("inheritsFrom");
        if (!string.IsNullOrEmpty(parentId))
        {
            var parent = LoadVersion(parentId);
            var merged = Mc.MergeVersion(parent, data);
            merged["_jar"] = data.Str("jar") ?? parent.Str("_jar");
            return merged;
        }
        data["_jar"] = data.Str("jar") ?? versionId;
        return data;
    }

    /// <summary>返回 (加载器, 加载器版本, Minecraft 版本)，原版的加载器为 null。</summary>
    public LoaderInfo DetectLoader(string versionId)
    {
        string loader = null, loaderVersion = null;
        var game = versionId;
        var current = versionId;
        var guard = 0;
        while (!string.IsNullOrEmpty(current) && guard++ < 10)
        {
            var data = Json.TryReadFile(VersionJsonPath(current));
            if (data == null)
                break;
            if (loader == null)
                (loader, loaderVersion) = Mc.LoaderFromJson(data);
            game = current;
            current = data.Str("inheritsFrom");
        }
        if (loader == "forge" && versionId.Contains("neoforge", StringComparison.OrdinalIgnoreCase))
            loader = "neoforge";
        if (loaderVersion != null && loaderVersion.EndsWith("-" + game))
            loaderVersion = loaderVersion[..^(game.Length + 1)];
        return new LoaderInfo(loader, loaderVersion, game);
    }

    /// <summary>版本说明，如「Fabric 0.15.0 · Minecraft 1.20.1」或「原版 · 正式版」。</summary>
    public string DescribeVersion(string versionId)
    {
        var info = DetectLoader(versionId);
        if (info.Loader != null)
        {
            var name = Mc.LoaderNames.GetValueOrDefault(info.Loader, info.Loader);
            var text = info.LoaderVersion != null ? $"{name} {info.LoaderVersion}" : name;
            return $"{text}  ·  Minecraft {info.Game}";
        }
        var type = Json.TryReadFile(VersionJsonPath(versionId))?.Str("type");
        var kind = type switch
        {
            "release" => T("正式版"),
            "snapshot" => T("快照版"),
            "old_beta" => "Beta",
            "old_alpha" => "Alpha",
            _ => type ?? "",
        };
        return info.Game != versionId ? F("原版 {0}", info.Game) : F("原版  ·  {0}", kind);
    }

    // ------------------------------------------------------------------ 版本单独设置

    /// <summary>版本单独的设置（isolation、custom、max_memory、java_path、jvm_args、modpack），保存在版本文件夹中。</summary>
    public JsonObject VersionSettings(string versionId) =>
        Json.TryReadFile(PathOf("versions", versionId, Mc.VersionSettingsFile)) as JsonObject ?? new JsonObject();

    public void SaveVersionSettings(string versionId, JsonObject data) =>
        Json.WriteFile(PathOf("versions", versionId, Mc.VersionSettingsFile), data);

    public LauncherConfig EffectiveConfig(string versionId)
    {
        var cfg = Cfg.Clone();
        var settings = VersionSettings(versionId);
        if (settings.Bool("custom"))
        {
            if (settings.Get("max_memory") != null)
                cfg.MaxMemory = settings.Int("max_memory", cfg.MaxMemory);
            if (settings.Get("java_path") != null)
                cfg.JavaPath = settings.Str("java_path") ?? "";
            if (settings.Get("jvm_args") != null)
                cfg.JvmArgs = settings.Str("jvm_args") ?? "";
        }
        return cfg;
    }

    public string GameDirFor(string versionId)
    {
        var isolation = VersionSettings(versionId).BoolOrNull("isolation") ?? Cfg.VersionIsolation;
        return isolation ? VersionDir(versionId) : McDir;
    }

    // ------------------------------------------------------------------ 文件准备

    private (List<string> Classpath, List<(string Jar, List<string> Excludes)> Natives, List<DownloadTask> Tasks)
        CollectLibraries(JsonObject vjson)
    {
        var classpath = new List<string>();
        var natives = new List<(string, List<string>)>();
        var tasks = new List<DownloadTask>();
        var seen = new HashSet<string>();
        foreach (var lib in vjson.Items("libraries"))
        {
            if (!Mc.RulesAllow(lib.Arr("rules")) || lib.BoolOrNull("clientreq") == false)
                continue;
            var name = lib.Str("name");
            if (string.IsNullOrEmpty(name))
                continue;
            var parts = name.Split('@')[0].Split(':');
            var key = string.Join(":", parts.Take(2).Concat(parts.Skip(3)));
            if (!seen.Add(key))
                continue;

            var downloads = lib.Obj("downloads");
            var artifact = downloads.Obj("artifact");
            var baseUrl = (lib.Str("url") ?? Mc.LibrariesUrl).TrimEnd('/') + "/";
            foreach (var (old, @new) in Mc.LegacyMaven)
            {
                if (baseUrl.StartsWith(old, StringComparison.Ordinal))
                    baseUrl = @new + baseUrl[old.Length..];
            }
            if (artifact != null)
            {
                var rel = artifact.Str("path") ?? Mc.MavenPath(name);
                var full = PathOf(["libraries", .. rel.Split('/')]);
                var url = artifact.Str("url");
                if (!string.IsNullOrEmpty(url))
                    tasks.Add(new DownloadTask(url, full, artifact.Str("sha1"), artifact.Long("size")));
                classpath.Add(full);
            }
            else if (lib.Get("natives") == null)
            {
                var rel = Mc.MavenPath(name);
                var full = PathOf(["libraries", .. rel.Split('/')]);
                var alternates = Mc.FallbackMavens.Where(b => b != baseUrl).Select(b => b + rel);
                tasks.Add(new DownloadTask(baseUrl + rel, full, lib.Str("sha1"), lib.Long("size"), alternates));
                classpath.Add(full);
            }

            var nativesMap = lib.Obj("natives");
            var classifier = nativesMap?.Str(Platform.OsName);
            if (classifier == null && Platform.IsMac)
                classifier = nativesMap?.Str("macos");
            if (classifier != null)
            {
                classifier = classifier.Replace("${arch}", Platform.Is64Bit ? "64" : "32");
                var info = downloads.Obj("classifiers")?.Obj(classifier);
                string full;
                if (info != null)
                {
                    full = PathOf(["libraries", .. info.Str("path").Split('/')]);
                    tasks.Add(new DownloadTask(info.Str("url"), full, info.Str("sha1"), info.Long("size")));
                }
                else
                {
                    var rel = Mc.MavenPath(name + ":" + classifier);
                    full = PathOf(["libraries", .. rel.Split('/')]);
                    tasks.Add(new DownloadTask(baseUrl + rel, full));
                }
                var excludes = lib.Get("extract").Items("exclude").Select(e => e.AsStr()).ToList();
                natives.Add((full, excludes));
            }
        }
        return (classpath, natives, tasks);
    }

    private async Task<((string Id, JsonNode Data)? Index, List<DownloadTask> Tasks)> CollectAssetsAsync(JsonObject vjson)
    {
        var index = vjson.Obj("assetIndex");
        if (index == null)
            return (null, []);
        var indexPath = PathOf("assets", "indexes", index.Str("id") + ".json");
        await Dl.DownloadManyAsync([new DownloadTask(index.Str("url"), indexPath, index.Str("sha1"), index.Long("size"))]);
        var data = Json.ReadFile(indexPath);
        var tasks = new List<DownloadTask>();
        foreach (var (_, obj) in data.Obj("objects") ?? [])
        {
            var hash = obj.Str("hash");
            tasks.Add(new DownloadTask($"{Mc.ResourcesUrl}{hash[..2]}/{hash}", PathOf("assets", "objects", hash[..2], hash),
                                       hash, obj.Long("size")));
        }
        return ((index.Str("id"), data), tasks);
    }

    private static void ExtractNatives(List<(string Jar, List<string> Excludes)> natives, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var (jar, excludes) in natives)
        {
            using var zip = ZipFile.OpenRead(jar);
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName;
                if (name.EndsWith('/') || name.StartsWith("META-INF/") || name.Split('/').Contains("..")
                    || excludes.Any(e => name.StartsWith(e, StringComparison.Ordinal)))
                    continue;
                var target = Path.Combine(dest, Path.Combine(name.Split('/')));
                var info = new FileInfo(target);
                if (info.Exists && info.Length == entry.Length)
                    continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                try
                {
                    entry.ExtractToFile(target, true);
                }
                catch (IOException)
                {
                    // 文件正被运行中的游戏占用
                }
            }
        }
    }

    /// <summary>1.7.2 及更早的版本需要把资源文件按原始文件名展开。</summary>
    private string MapLegacyAssets((string Id, JsonNode Data)? assetIndex, string gameDir)
    {
        var assetsRoot = PathOf("assets");
        if (assetIndex == null)
            return assetsRoot;
        var (indexId, data) = assetIndex.Value;
        string target;
        if (data.Bool("map_to_resources"))
            target = Path.Combine(gameDir, "resources");
        else if (data.Bool("virtual"))
            target = PathOf("assets", "virtual", indexId);
        else
            return assetsRoot;
        Log(T("正在整理旧版资源文件..."));
        foreach (var (name, obj) in data.Obj("objects"))
        {
            var hash = obj.Str("hash");
            var dest = Path.Combine(target, Path.Combine(name.Split('/')));
            var info = new FileInfo(dest);
            if (info.Exists && info.Length == obj.Long("size"))
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(PathOf("assets", "objects", hash[..2], hash), dest, true);
        }
        return target;
    }

    /// <summary>Forge 等安装器要求游戏目录下存在 launcher_profiles.json。</summary>
    public void EnsureLauncherProfiles()
    {
        var path = PathOf("launcher_profiles.json");
        if (!File.Exists(path))
            Json.WriteFile(path, new JsonObject { ["profiles"] = new JsonObject() });
    }

    /// <summary>确保某个版本的所有文件都已下载并校验，返回启动所需信息。</summary>
    public async Task<PreparedGame> PrepareAsync(string versionId)
    {
        Directory.CreateDirectory(McDir);
        await EnsureVersionJsonAsync(versionId);
        var vjson = LoadVersion(versionId);
        Log(F("正在检查 {0} 的游戏文件...", versionId));

        var tasks = new List<DownloadTask>();
        var jarId = vjson.Str("_jar");
        var clientJar = PathOf("versions", jarId, jarId + ".jar");
        var client = vjson.Obj("downloads")?.Obj("client");
        if (client != null)
            tasks.Add(new DownloadTask(client.Str("url"), clientJar, client.Str("sha1"), client.Long("size")));
        else if (!File.Exists(clientJar))
            throw new InvalidOperationException(F("缺少游戏本体文件 {0}", clientJar));

        var (classpath, natives, libTasks) = CollectLibraries(vjson);
        tasks.AddRange(libTasks);
        var versionJar = PathOf("versions", versionId, versionId + ".jar");

        string loggingArg = null;
        var logCfg = vjson.Obj("logging")?.Obj("client");
        var logFile = logCfg?.Obj("file");
        if (logFile != null)
        {
            var logPath = PathOf("assets", "log_configs", logFile.Str("id"));
            tasks.Add(new DownloadTask(logFile.Str("url"), logPath, logFile.Str("sha1"), logFile.Long("size")));
            loggingArg = (logCfg.Str("argument") ?? "").Replace("${path}", logPath);
        }

        var (assetIndex, assetTasks) = await CollectAssetsAsync(vjson);
        tasks.AddRange(assetTasks);

        if (!await Task.Run(() => tasks.AsParallel().All(t => t.IsValid())))
        {
            var source = await Dl.ResolvedSourceAsync();
            Log(F("下载源：{0}", source == "bmclapi" ? T("BMCLAPI 镜像") : T("Mojang 官方")));
        }
        var count = await Dl.DownloadManyAsync(tasks, (d, t) => Progress(d, t, T("下载游戏文件")));
        Log(count > 0 ? F("已下载 {0} 个文件", count) : T("游戏文件完整，无需下载"));

        // 和官方启动器一样把本体复制成 <版本名>.jar，新版 Forge 按这个文件名把本体排除出类路径
        if (!string.Equals(Path.GetFullPath(versionJar), Path.GetFullPath(clientJar),
                           Platform.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            var target = new FileInfo(versionJar);
            if (!target.Exists || target.Length != new FileInfo(clientJar).Length)
                File.Copy(clientJar, versionJar, true);
        }
        classpath.Add(versionJar);

        var gameDir = GameDirFor(versionId);
        Directory.CreateDirectory(gameDir);
        var nativesDir = PathOf("versions", versionId, "natives");
        await Task.Run(() => ExtractNatives(natives, nativesDir));
        var gameAssets = await Task.Run(() => MapLegacyAssets(assetIndex, gameDir));
        EnsureLauncherProfiles();

        return new PreparedGame
        {
            VJson = vjson,
            Classpath = classpath,
            NativesDir = nativesDir,
            GameDir = gameDir,
            GameAssets = gameAssets,
            LoggingArg = loggingArg,
        };
    }

    // ------------------------------------------------------------------ 启动

    public string RuntimeRoot => PathOf("runtime");

    /// <summary>Apple 芯片的 Mac 上，使用 LWJGL 2 的老版本只有 x86_64 本地库，需要 Rosetta 下的 x86_64 Java。</summary>
    private static bool NeedsIntelJava(JsonObject vjson) =>
        Platform.IsMac && Platform.IsArm && vjson.Items("libraries").Any(lib =>
            (lib.Str("name") ?? "").StartsWith("org.lwjgl.lwjgl:lwjgl:2.", StringComparison.Ordinal));

    public async Task<string> SelectJavaAsync(JsonObject vjson)
    {
        var custom = (Cfg.JavaPath ?? "").Trim();
        if (custom.Length > 0)
        {
            if (!File.Exists(custom))
                throw new InvalidOperationException(F("设置中的 Java 路径不存在：{0}", custom));
            return custom;
        }

        var javaInfo = vjson.Obj("javaVersion");
        var required = javaInfo?.Int("majorVersion", 8) ?? 8;
        var component = javaInfo?.Str("component") ?? "jre-legacy";
        var intel = NeedsIntelJava(vjson);
        var runtimeRoot = intel ? Path.Combine(RuntimeRoot, "x86_64") : RuntimeRoot;
        var javas = await Task.Run(() => JavaManager.FindJava([runtimeRoot]));
        if (intel)
            javas = javas.Where(j => j.Path.StartsWith(runtimeRoot, StringComparison.Ordinal)).ToList();

        var exact = JavaManager.PickExact(javas, required);
        if (exact != null)
            return exact;

        Log(F("本机没有 Java {0}，正在下载官方 Java 运行时 ({1})...", required, component));
        try
        {
            return await JavaManager.InstallRuntimeAsync(component, runtimeRoot, Dl,
                                                         (d, t) => Progress(d, t, T("下载 Java")),
                                                         intel ? "mac-os" : null);
        }
        catch (Exception e)
        {
            Log(F("[警告] Java 运行时下载失败：{0}", e.Message));
        }

        var fallback = JavaManager.PickCompatible(javas, required);
        if (fallback != null)
        {
            Log(F("[警告] 将使用更高版本的 Java 代替 Java {0}，可能存在兼容问题", required));
            return fallback;
        }
        throw new InvalidOperationException(F("找不到可用的 Java {0}，请安装后在设置中指定", required));
    }

    /// <summary>server 为「主机:端口」时，游戏启动后直接进入该服务器；auth 为空时使用配置中的离线用户名。</summary>
    public List<string> BuildCommand(JsonObject vjson, string java, PreparedGame info, string server = null,
                                     LaunchAuth auth = null)
    {
        var cfg = Cfg;
        if (auth == null)
        {
            var uid = Mc.OfflineUuid(cfg.Username);
            auth = new LaunchAuth { Name = cfg.Username, Uuid = uid, Token = uid };
        }
        var width = cfg.WindowWidth.ToString();
        var height = cfg.WindowHeight.ToString();
        var sep = Path.PathSeparator.ToString();
        var values = new Dictionary<string, string>
        {
            ["auth_player_name"] = auth.Name,
            ["version_name"] = vjson.Str("id"),
            ["game_directory"] = info.GameDir,
            ["assets_root"] = PathOf("assets"),
            ["game_assets"] = info.GameAssets,
            ["assets_index_name"] = vjson.Obj("assetIndex")?.Str("id") ?? vjson.Str("assets") ?? "legacy",
            ["auth_uuid"] = auth.Uuid,
            ["auth_access_token"] = auth.Token,
            ["auth_session"] = auth.Token,
            ["auth_xuid"] = string.IsNullOrEmpty(auth.Xuid) ? "0" : auth.Xuid,
            ["clientid"] = "0",
            ["user_type"] = auth.UserType ?? "msa",
            ["user_properties"] = "{}",
            ["version_type"] = Mc.LauncherName,
            ["resolution_width"] = width,
            ["resolution_height"] = height,
            ["natives_directory"] = info.NativesDir,
            ["launcher_name"] = Mc.LauncherName,
            ["launcher_version"] = Mc.LauncherVersion,
            ["classpath"] = string.Join(sep, info.Classpath),
            ["classpath_separator"] = sep,
            ["library_directory"] = PathOf("libraries"),
        };
        var features = new Dictionary<string, bool> { ["has_custom_resolution"] = true };

        List<string> Resolve(JsonArray entries)
        {
            var output = new List<string>();
            foreach (var entry in entries.Items())
            {
                if (entry.IsString())
                {
                    output.Add(entry.AsStr());
                }
                else if (Mc.RulesAllow(entry.Arr("rules"), features))
                {
                    var value = entry.Get("value");
                    if (value is JsonArray list)
                        output.AddRange(list.Items().Select(v => v.AsStr()));
                    else if (value != null)
                        output.Add(value.AsStr());
                }
            }
            return output;
        }

        var arguments = vjson.Obj("arguments");
        List<string> jvmArgs, gameArgs;
        if (vjson.Get("minecraftArguments") != null)
        {
            jvmArgs = [];
            if (Platform.IsWindows)
                jvmArgs.Add("-XX:HeapDumpPath=MojangTricksIntelDriversForPerformance_javaw.exe_minecraft.exe.heapdump");
            if (Platform.IsMac)
                jvmArgs.Add("-Xdock:name=Minecraft");
            jvmArgs.AddRange(["-Djava.library.path=${natives_directory}", "-Dminecraft.launcher.brand=${launcher_name}",
                              "-Dminecraft.launcher.version=${launcher_version}", "-cp", "${classpath}"]);
            jvmArgs.AddRange(Resolve(arguments?.Arr("jvm")));
            gameArgs = vjson.Str("minecraftArguments").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            gameArgs.AddRange(Resolve(arguments?.Arr("game")));
            gameArgs.AddRange(["--width", width, "--height", height]);
        }
        else
        {
            jvmArgs = Resolve(arguments?.Arr("jvm"));
            gameArgs = Resolve(arguments?.Arr("game"));
        }

        if (!string.IsNullOrEmpty(server))
        {
            var colon = server.LastIndexOf(':');
            var host = colon > 0 ? server[..colon] : server;
            var port = colon > 0 ? server[(colon + 1)..] : "25565";
            if (Json.Serialize(arguments?.Arr("game")).Contains("${quickPlayMultiplayer}"))
            {
                features["is_quick_play_multiplayer"] = true;
                values["quickPlayMultiplayer"] = server;
                gameArgs = Resolve(arguments?.Arr("game"));
            }
            else
            {
                gameArgs.AddRange(["--server", host, "--port", port]);
            }
        }

        var cmd = new List<string>
        {
            java,
            $"-Xmx{cfg.MaxMemory}m",
            "-XX:+UseG1GC",
            "-XX:-OmitStackTraceInFastThrow",
            "-Dlog4j2.formatMsgNoLookups=true",
            "-Dfml.ignoreInvalidMinecraftCertificates=true",
            "-Dfml.ignorePatchDiscrepancies=true",
            "-Dstdout.encoding=UTF-8",
            "-Dstderr.encoding=UTF-8",
        };
        cmd.AddRange(auth.JvmArgs ?? []);
        cmd.AddRange((cfg.JvmArgs ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries));
        cmd.AddRange(jvmArgs);
        if (!string.IsNullOrEmpty(info.LoggingArg))
            cmd.Add(info.LoggingArg);
        cmd.Add(vjson.Str("mainClass"));
        cmd.AddRange(gameArgs);
        return cmd.Select(arg => Mc.Placeholder().Replace(arg, m =>
            values.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value)).ToList();
    }

    /// <summary>补全游戏文件并选择 Java，返回完整的启动命令（第一项是 java 路径）和工作目录。</summary>
    public async Task<(List<string> Command, string GameDir)> PrepareCommandAsync(string versionId, string server = null,
                                                                                 LaunchAuth auth = null)
    {
        Cfg = EffectiveConfig(versionId);
        var info = await PrepareAsync(versionId);
        var java = await SelectJavaAsync(info.VJson);
        Log(F("使用 Java：{0}", java));
        return (BuildCommand(info.VJson, java, info, server, auth), info.GameDir);
    }

    /// <summary>准备文件、选择 Java 并启动游戏。返回已启动的进程（标准输出与错误输出已重定向并合并读取）。</summary>
    public async Task<Process> LaunchAsync(string versionId, string server = null, LaunchAuth auth = null)
    {
        var (cmd, gameDir) = await PrepareCommandAsync(versionId, server, auth);
        Log(F("正在启动 {0}（玩家 {1}，最大内存 {2} MB）...", versionId, auth?.Name ?? Cfg.Username, Cfg.MaxMemory));
        var psi = new ProcessStartInfo(cmd[0])
        {
            WorkingDirectory = gameDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in cmd.Skip(1))
            psi.ArgumentList.Add(arg);
        var process = Process.Start(psi) ?? throw new InvalidOperationException(T("无法启动 Java 进程"));
        process.StandardInput.Close();
        return process;
    }

    /// <summary>手机上由平台层实现：用准备好的文件在游戏进程里启动游戏。</summary>
    public static Func<GameLauncher, PreparedGame, string, LaunchAuth, Task<RunningGame>> MobileStarter { get; set; }

    /// <summary>手机上不能启动 java 子进程，安装工具（Forge 处理器等）由平台层用手机版 Java 运行。参数：所需 Java 主版本、Java 参数、工作目录。</summary>
    public static Func<GameLauncher, int, IReadOnlyList<string>, string, Task<(int ExitCode, List<string> Output)>> MobileJavaRunner { get; set; }

    /// <summary>用适合 <paramref name="vjson"/> 的 Java 运行安装工具，返回 (退出码, 输出行)。</summary>
    internal async Task<(int ExitCode, List<string> Output)> RunToolJavaAsync(JsonObject vjson, IReadOnlyList<string> args,
                                                                             string workDir = null)
    {
        if (Platform.IsMobile)
        {
            var runner = MobileJavaRunner ?? throw new PlatformNotSupportedException(T("这个平台还不能运行 Java 安装程序"));
            return await runner(this, vjson.Obj("javaVersion")?.Int("majorVersion", 8) ?? 8, args, workDir);
        }
        return await Loaders.RunJavaAsync(await SelectJavaAsync(vjson), args, workDir);
    }

    /// <summary>准备文件并启动游戏：电脑上启动 java 子进程，手机上交给 <see cref="MobileStarter"/>。</summary>
    public async Task<RunningGame> StartAsync(string versionId, string server = null, LaunchAuth auth = null)
    {
        if (!Platform.IsMobile)
            return RunningGame.Of(await LaunchAsync(versionId, server, auth));
        var starter = MobileStarter ?? throw new PlatformNotSupportedException(Platform.IsIOS
            ? T("iPhone / iPad 版暂时还不能启动游戏：iOS 不允许普通应用使用 Java 运行需要的 JIT。现在可以先管理账号、版本和模组。")
            : T("这个平台还不能启动游戏"));
        Cfg = EffectiveConfig(versionId);
        var info = await PrepareAsync(versionId);
        Log(F("正在启动 {0}（玩家 {1}，最大内存 {2} MB）...", versionId, auth?.Name ?? Cfg.Username, Cfg.MaxMemory));
        return await starter(this, info, server, auth);
    }
}
