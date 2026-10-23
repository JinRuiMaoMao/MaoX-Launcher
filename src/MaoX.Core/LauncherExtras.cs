using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static MaoX.Core.I18n;

namespace MaoX.Core;

/// <summary>Cloudflare DNS over HTTPS，减轻域名污染。解析失败时回退到系统 DNS。</summary>
public static class Doh
{
    private static readonly HttpClient Resolver = new(new SocketsHttpHandler
    {
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(8),
    })
    { Timeout = TimeSpan.FromSeconds(8) };

    private static readonly Dictionary<string, (IPAddress Ip, long Until)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Lock = new();

    static Doh() => Resolver.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/dns-json");

    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancel)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;
        var ip = await ResolveAsync(host, cancel);
        var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(ip, port), cancel);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public static async Task<IPAddress> ResolveAsync(string host, CancellationToken cancel = default)
    {
        if (IPAddress.TryParse(host, out var literal))
            return literal;
        lock (Lock)
        {
            if (Cache.TryGetValue(host, out var hit) && hit.Until > Environment.TickCount64)
                return hit.Ip;
        }
        try
        {
            var url = "https://1.1.1.1/dns-query?name=" + Uri.EscapeDataString(host) + "&type=A";
            using var response = await Resolver.GetAsync(url, cancel);
            response.EnsureSuccessStatusCode();
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancel));
            foreach (var answer in json.Items("Answer"))
            {
                if (answer.Int("type") == 1 && IPAddress.TryParse(answer.Str("data"), out var ip))
                {
                    var ttl = Math.Clamp(answer.Int("TTL", 300), 30, 3600);
                    lock (Lock)
                        Cache[host] = (ip, Environment.TickCount64 + ttl * 1000);
                    return ip;
                }
            }
        }
        catch (Exception)
        {
        }
        var fallback = (await Dns.GetHostAddressesAsync(host, cancel)).FirstOrDefault()
                       ?? throw new HttpRequestException(F("无法解析域名 {0}", host));
        lock (Lock)
            Cache[host] = (fallback, Environment.TickCount64 + 60_000);
        return fallback;
    }
}

public class DownloadRecord
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string Path { get; set; } = "";
    public bool Ok { get; set; }
    public string Error { get; set; }
    public DateTime Time { get; set; } = DateTime.Now;
}

/// <summary>最近的下载记录，失败的可以在工具页重试。</summary>
public static class DownloadHistory
{
    private const int Keep = 80;
    private static readonly object Lock = new();
    private static List<DownloadRecord> _items;
    private static string FilePath => System.IO.Path.Combine(AppPaths.CacheDir, "download-history.json");

    public static List<DownloadRecord> All()
    {
        lock (Lock)
            return [.. Load()];
    }

    public static void Record(DownloadTask task, bool ok, string error)
    {
        try
        {
            lock (Lock)
            {
                var list = Load();
                list.Insert(0, new DownloadRecord
                {
                    Name = System.IO.Path.GetFileName(task.Path),
                    Url = task.Url,
                    Path = task.Path,
                    Ok = ok,
                    Error = error,
                    Time = DateTime.Now,
                });
                if (list.Count > Keep)
                    list.RemoveRange(Keep, list.Count - Keep);
                Save(list);
            }
        }
        catch (Exception)
        {
        }
    }

    public static void Clear()
    {
        lock (Lock)
        {
            _items = [];
            try
            {
                if (File.Exists(FilePath))
                    File.Delete(FilePath);
            }
            catch (Exception)
            {
            }
        }
    }

    private static List<DownloadRecord> Load()
    {
        if (_items != null)
            return _items;
        try
        {
            if (File.Exists(FilePath))
                _items = JsonSerializer.Deserialize<List<DownloadRecord>>(File.ReadAllText(FilePath), LauncherConfig.Options);
        }
        catch (Exception)
        {
        }
        return _items ??= [];
    }

    private static void Save(List<DownloadRecord> list)
    {
        _items = list;
        Directory.CreateDirectory(AppPaths.CacheDir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(list, LauncherConfig.Options));
    }
}

public record CleanItem(string Key, string Title, string Detail, long Bytes, List<string> Files);

/// <summary>扫描游戏目录里可以安全删掉的缓存、日志和未被引用的库。</summary>
public static class DiskClean
{
    public static List<CleanItem> Scan(string mcDir)
    {
        var items = new List<CleanItem>();
        if (string.IsNullOrEmpty(mcDir) || !Directory.Exists(mcDir))
            return items;

        Add(items, "parts", T("未完成的下载"), Collect(mcDir, "*.part"));
        Add(items, "logs", T("游戏日志"), Collect(System.IO.Path.Combine(mcDir, "logs"), "*.log*")
            .Concat(Collect(System.IO.Path.Combine(mcDir, "versions"), "**/logs/*.log*")).ToList());
        Add(items, "crash", T("崩溃报告"), Collect(System.IO.Path.Combine(mcDir, "crash-reports"), "*.txt"));

        var used = UsedLibraries(mcDir);
        var unused = new List<string>();
        var libRoot = System.IO.Path.Combine(mcDir, "libraries");
        if (Directory.Exists(libRoot))
        {
            foreach (var file in Directory.EnumerateFiles(libRoot, "*", SearchOption.AllDirectories))
            {
                if (file.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                    continue;
                var rel = System.IO.Path.GetRelativePath(libRoot, file).Replace('\\', '/');
                if (!used.Contains(rel))
                    unused.Add(file);
            }
        }
        Add(items, "libs", T("未被任何版本引用的库"), unused);
        return items;
    }

    public static long Delete(IEnumerable<CleanItem> items)
    {
        long bytes = 0;
        foreach (var item in items)
        {
            foreach (var file in item.Files)
            {
                try
                {
                    var info = new FileInfo(file);
                    if (!info.Exists)
                        continue;
                    bytes += info.Length;
                    info.Delete();
                }
                catch (Exception)
                {
                }
            }
        }
        return bytes;
    }

    public static string FormatSize(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB"
        : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.0} MB"
        : bytes >= 1L << 10 ? $"{bytes / (double)(1L << 10):0} KB"
        : $"{bytes} B";

    private static void Add(List<CleanItem> items, string key, string title, List<string> files)
    {
        files = files.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count == 0)
            return;
        long bytes = 0;
        foreach (var file in files)
        {
            try
            {
                bytes += new FileInfo(file).Length;
            }
            catch (Exception)
            {
            }
        }
        items.Add(new CleanItem(key, title, F("{0} 个文件 · {1}", files.Count, FormatSize(bytes)), bytes, files));
    }

    private static List<string> Collect(string folder, string pattern)
    {
        if (!Directory.Exists(folder))
            return [];
        try
        {
            var option = pattern.Contains("**") ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var name = pattern[(pattern.LastIndexOf('/') + 1)..];
            return Directory.EnumerateFiles(folder, name, option).ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static HashSet<string> UsedLibraries(string mcDir)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var versions = System.IO.Path.Combine(mcDir, "versions");
        if (!Directory.Exists(versions))
            return used;
        foreach (var file in Directory.EnumerateFiles(versions, "*.json", SearchOption.AllDirectories))
        {
            var data = Json.TryReadFile(file);
            if (data == null)
                continue;
            foreach (var lib in data.Items("libraries"))
            {
                var path = lib.Obj("downloads")?.Obj("artifact")?.Str("path")
                           ?? lib.Obj("downloads")?.Obj("classifiers")
                               ?.Select(p => (p.Value as JsonObject)?.Str("path"))
                               .FirstOrDefault(p => !string.IsNullOrEmpty(p));
                if (!string.IsNullOrEmpty(path))
                    used.Add(path.Replace('\\', '/'));
                var name = lib.Str("name");
                if (string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(name))
                    used.Add(MavenPath(name));
            }
        }
        return used;
    }

    private static string MavenPath(string name)
    {
        var parts = name.Split('@')[0].Split(':');
        if (parts.Length < 3)
            return name;
        var file = parts.Length > 3
            ? $"{parts[1]}-{parts[2]}-{parts[3]}.jar"
            : $"{parts[1]}-{parts[2]}.jar";
        return string.Join('/', parts[0].Split('.').Concat([parts[1], parts[2], file]));
    }
}

public record FoundLauncher(string Kind, string Name, string GameDir, List<Account> Accounts);

/// <summary>扫描本机 PCL / HMCL / 官方启动器的游戏目录和离线账号。</summary>
public static class LauncherImport
{
    public static List<FoundLauncher> Detect()
    {
        var found = new List<FoundLauncher>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string kind, string name, string dir, List<Account> accounts = null)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir) || !seen.Add(Path.GetFullPath(dir)))
                return;
            found.Add(new FoundLauncher(kind, name, Path.GetFullPath(dir), accounts ?? []));
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Add("official", T("官方启动器"), Path.Combine(appData, ".minecraft"));
        Add("official", T("官方启动器"), Path.Combine(home, ".minecraft"));

        foreach (var hmcl in new[]
                 {
                     Path.Combine(appData, "hmcl.json"),
                     Path.Combine(home, ".hmcl", "hmcl.json"),
                     Path.Combine(home, "hmcl.json"),
                 })
        {
            var data = Json.TryReadFile(hmcl);
            if (data == null)
                continue;
            Add("hmcl", "HMCL", data.Str("commonDir") ?? data.Str("gameDir") ?? Path.Combine(appData, ".minecraft"),
                ReadHmclAccounts(Path.GetDirectoryName(hmcl)));
        }

        foreach (var root in new[]
                 {
                     Path.Combine(home, "Documents"), Path.Combine(home, "Desktop"),
                     Path.Combine(home, "Downloads"), appData, home,
                 }.Where(Directory.Exists))
        {
            foreach (var setup in SafeEnumerate(root, "Setup.ini", 2))
            {
                var folder = ReadIni(setup, "LaunchFolder") ?? ReadIni(setup, "FolderForMinecraft");
                if (string.IsNullOrEmpty(folder))
                    folder = Path.Combine(Path.GetDirectoryName(setup)!, ".minecraft");
                Add("pcl", "PCL", folder);
            }
        }
        return found;
    }

    public static int Apply(LauncherConfig cfg, FoundLauncher found)
    {
        var added = 0;
        var path = Path.GetFullPath(found.GameDir);
        if (!string.Equals(path, Path.GetFullPath(cfg.MinecraftDir), StringComparison.OrdinalIgnoreCase)
            && !(cfg.ExtraDirs ?? []).Any(d => string.Equals(Path.GetFullPath(d.Path ?? ""), path, StringComparison.OrdinalIgnoreCase)))
        {
            cfg.ExtraDirs ??= [];
            cfg.ExtraDirs.Add(new GameDirEntry { Name = found.Name, Path = path });
            added++;
        }
        foreach (var account in found.Accounts)
        {
            if (cfg.Accounts.Any(a => a.Type == account.Type && a.Name == account.Name && a.Uuid == account.Uuid))
                continue;
            cfg.Accounts.Add(account);
            added++;
        }
        return added;
    }

    private static List<Account> ReadHmclAccounts(string dir)
    {
        var accounts = new List<Account>();
        foreach (var file in new[] { "accounts.json", Path.Combine(dir ?? "", "accounts.json") })
        {
            var data = Json.TryReadFile(file);
            if (data is not JsonArray array)
                continue;
            foreach (var item in array.OfType<JsonObject>())
            {
                var type = item.Str("type") ?? item.Str("loginType") ?? "";
                var name = item.Str("username") ?? item.Str("name") ?? item.Obj("selectedProfile")?.Str("name");
                if (string.IsNullOrEmpty(name))
                    continue;
                if (type.Contains("offline", StringComparison.OrdinalIgnoreCase) || type is "0" or "offline")
                    accounts.Add(Accounts.OfflineAccount(name));
            }
        }
        return accounts;
    }

    private static string ReadIni(string path, string key)
    {
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var i = line.IndexOf('=');
                if (i < 0)
                    continue;
                if (line[..i].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                    return line[(i + 1)..].Trim().Trim('"');
            }
        }
        catch (Exception)
        {
        }
        return null;
    }

    private static IEnumerable<string> SafeEnumerate(string root, string name, int depth)
    {
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var (dir, d) = queue.Dequeue();
            string[] files = [], dirs = [];
            try
            {
                files = Directory.GetFiles(dir, name);
                if (d < depth)
                    dirs = Directory.GetDirectories(dir);
            }
            catch (Exception)
            {
            }
            foreach (var file in files)
                yield return file;
            foreach (var child in dirs)
                queue.Enqueue((child, d + 1));
        }
    }
}

public record CheckItem(string Title, string Detail, string Kind);

/// <summary>启动前检查 Java、磁盘和版本文件。错误需要确认，警告只提示。</summary>
public static class LaunchCheck
{
    public static List<CheckItem> Run(GameLauncher gl, string version, Account account)
    {
        var items = new List<CheckItem>();
        if (string.IsNullOrEmpty(version))
        {
            items.Add(new CheckItem(T("没有选中版本"), T("请先安装或选择一个游戏版本"), "error"));
            return items;
        }
        if (!File.Exists(gl.VersionJsonPath(version)))
            items.Add(new CheckItem(T("版本信息不完整"), F("找不到 {0} 的版本 JSON", version), "error"));
        else
            items.Add(new CheckItem(T("版本文件"), gl.DescribeVersion(version), "ok"));

        if (account == null)
            items.Add(new CheckItem(T("没有账号"), T("启动前需要添加一个账号"), "error"));
        else
            items.Add(new CheckItem(T("账号"), account.Name + "  ·  " + Accounts.Describe(account), "ok"));

        try
        {
            var dir = gl.GameDirFor(version);
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".maox-write");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))!);
            var free = drive.AvailableFreeSpace;
            if (free < 500L << 20)
                items.Add(new CheckItem(T("磁盘空间不足"), F("游戏目录所在磁盘只剩 {0}", DiskClean.FormatSize(free)), "error"));
            else if (free < 2L << 30)
                items.Add(new CheckItem(T("磁盘空间偏少"), F("还剩 {0}，建议先清理", DiskClean.FormatSize(free)), "warn"));
            else
                items.Add(new CheckItem(T("磁盘空间"), DiskClean.FormatSize(free), "ok"));
        }
        catch (Exception e)
        {
            items.Add(new CheckItem(T("游戏目录不可写"), e.Message, "error"));
        }

        var data = Json.TryReadFile(gl.VersionJsonPath(version));
        var required = data?.Obj("javaVersion")?.Int("majorVersion", 8) ?? 8;
        items.Add(new CheckItem(T("需要的 Java"), F("Java {0} 或兼容版本", required), "ok"));
        return items;
    }

    public static bool HasError(IEnumerable<CheckItem> items) => items.Any(i => i.Kind == "error");
}

/// <summary>按加载器推荐的常用模组（Modrinth slug）。</summary>
public static class Recommended
{
    public static readonly (string Slug, string Name, string Why, string[] Loaders)[] All =
    [
        ("fabric-api", "Fabric API", "Fabric 模组几乎都需要", ["fabric"]),
        ("qsl", "Quilted Fabric API", "Quilt 模组的基础库", ["quilt"]),
        ("sodium", "Sodium", "显著提升帧数", ["fabric", "quilt", "neoforge"]),
        ("lithium", "Lithium", "优化游戏逻辑，减少卡顿", ["fabric", "quilt"]),
        ("starlight", "Starlight", "更快的光照计算", ["fabric", "quilt"]),
        ("iris", "Iris", "光影，配合 Sodium 使用", ["fabric", "quilt"]),
        ("modmenu", "Mod Menu", "游戏内查看已安装模组", ["fabric", "quilt"]),
        ("jei", "Just Enough Items", "查看合成表和物品", ["fabric", "forge", "neoforge", "quilt"]),
        ("cloth-config", "Cloth Config", "很多模组的设置界面", ["fabric", "forge", "neoforge", "quilt"]),
        ("ferrite-core", "FerriteCore", "降低内存占用", ["fabric", "forge", "neoforge", "quilt"]),
        ("embeddium", "Embeddium", "Forge / NeoForge 上的渲染优化", ["forge", "neoforge"]),
        ("oculus", "Oculus", "Forge / NeoForge 上的光影", ["forge", "neoforge"]),
    ];

    public static IEnumerable<(string Slug, string Name, string Why)> For(string loader) =>
        All.Where(m => loader != null && m.Loaders.Contains(loader)).Select(m => (m.Slug, m.Name, m.Why));
}

public record Announcement(string Id, string Title, string Body, string Url);

/// <summary>用 GitHub 最新 Release 作为启动器公告。</summary>
public static class Announce
{
    public static async Task<Announcement> FetchAsync()
    {
        var json = await Http.Client.GetStringAsync("https://api.github.com/repos/" + Updater.Repo + "/releases/latest");
        var data = JsonNode.Parse(json);
        var tag = data.Str("tag_name");
        if (string.IsNullOrEmpty(tag))
            return null;
        return new Announcement(tag, data.Str("name") ?? tag, data.Str("body") ?? "", data.Str("html_url") ?? Updater.ReleasesUrl);
    }
}

/// <summary>本地原版服务器：下载 server.jar 并写好启动脚本。</summary>
public static class LocalServer
{
    public static (string Url, string Sha1, long? Size) ServerJar(JsonObject vjson)
    {
        var server = vjson?.Obj("downloads")?.Obj("server");
        if (server == null)
            return (null, null, null);
        return (server.Str("url"), server.Str("sha1"), server.Long("size") is var n and > 0 ? n : null);
    }

    public static async Task<string> PrepareAsync(GameLauncher gl, string version, string folder, Action<int, int, string> progress = null)
    {
        Directory.CreateDirectory(folder);
        var vjson = gl.LoadVersion(version);
        var (url, sha1, size) = ServerJar(vjson);
        if (string.IsNullOrEmpty(url))
            throw new InvalidOperationException(T("这个版本没有提供服务端 jar"));
        var jar = Path.Combine(folder, "server.jar");
        if (!File.Exists(jar) || (sha1 != null && Http.FileSha1(jar) != sha1))
            await gl.Dl.DownloadAsync(new DownloadTask(url, jar, sha1, size));
        File.WriteAllText(Path.Combine(folder, "eula.txt"), "eula=true" + Environment.NewLine);
        var script = Path.Combine(folder, Platform.IsWindows ? "start-server.bat" : "start-server.sh");
        var memory = Math.Max(1024, gl.Cfg.MaxMemory);
        if (Platform.IsWindows)
        {
            File.WriteAllText(script, $"@echo off\r\ncd /d \"%~dp0\"\r\njava -Xmx{memory}M -jar server.jar nogui\r\npause\r\n", Encoding.Default);
        }
        else
        {
            File.WriteAllText(script, $"#!/bin/sh\ncd \"$(dirname \"$0\")\"\njava -Xmx{memory}M -jar server.jar nogui\n");
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                                 | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
                }
                catch (Exception)
                {
                }
            }
        }
        progress?.Invoke(1, 1, T("服务端已准备好"));
        return folder;
    }
}
