using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace MaoX.Core;

public class TerracottaException : Exception
{
    public TerracottaException(string message, Exception inner = null) : base(message, inner)
    {
    }
}

/// <summary>某个平台的安装包：整个 tar.gz 的 SHA-512 与包内各文件的 SHA-512。</summary>
public record TerracottaPackage(string Hash, IReadOnlyDictionary<string, string> Files);

/// <summary>
/// 多人联机：集成 Terracotta | 陶瓦联机。
/// 陶瓦联机基于 EasyTier 的 P2P 组网，无需公网 IP，邀请码与 HMCL、PCL 社区版互通。
/// 启动器只下载并运行未经修改的官方程序，通过它提供的本地 HTTP 接口交互。
/// Terracotta 版权归 Burning_TNT 所有，以 AGPL-3.0 许可发布：https://github.com/burningtnt/Terracotta
/// macOS 上与 HMCL 相同：除了运行包内的命令行程序，还需要用管理员权限安装包内的 .pkg（/Applications/terracotta.app）。
/// </summary>
public class Terracotta : IDisposable
{
    public const string Version = "0.4.2";
    public const string ProjectUrl = "https://github.com/burningtnt/Terracotta";
    public const string NodeListUrl = "https://terracotta.glavo.site/nodes";
    public const string MacAppPath = "/Applications/terracotta.app";

    /// <summary>下载地址模板，{version} / {name} 会被替换。</summary>
    public static readonly string[] Downloads =
    [
        "https://github.com/burningtnt/Terracotta/releases/download/v{version}/{name}",
        "https://alist.8mi.tech/d/mirror/HMCL-Terracotta/Auto/v{version}/{name}",
        "https://cnb.cool/HMCL-Terracotta/Terracotta/-/releases/download/v{version}/{name}",
    ];

    /// <summary>SHA-512，与 HMCL 内置的校验值一致。键为分类名，如 windows-x86_64、macos-arm64、linux-x86_64。</summary>
    public static readonly IReadOnlyDictionary<string, TerracottaPackage> Packages =
        new Dictionary<string, TerracottaPackage>
        {
            ["windows-x86_64"] = new(
                "6a98f524d4f00373696517306af8aa50d01d55ce4eadb27e9e4bc2f882707a0b5f20d5d4c33371d1459dcf5bf144ffed9beb414202d9ccf32b11dbbfcf19d650",
                new Dictionary<string, string>
                {
                    ["VCRUNTIME140.DLL"] = "3d4b24061f72c0e957c7b04a0c4098c94c8f1afb4a7e159850b9939c7210d73398be6f27b5ab85073b4e8c999816e7804fef0f6115c39cd061f4aaeb4dcda8cf",
                    ["terracotta-0.4.2-windows-x86_64.exe"] = "6e98d1f2380ed22fb5a2dd4aafce6c773e9cf69100c8bb8e49e7d6983756bdb9a31f80e06bcfbe5a2742144fe806d3d687dec54d8f09d87c659341f99dd9fd80",
                }),
            ["windows-arm64"] = new(
                "fc1077247014ac0c712469498bde2ef7f6d881d5fcb7bdd5e11ebe20218fed365be19afdb8d453a79d77b729f866058522b910741767f4df947faa891434b463",
                new Dictionary<string, string>
                {
                    ["VCRUNTIME140.DLL"] = "5cb5ce114614101d260f4754c09e8a0dd57e4da885ebb96b91e274326f3e1dd95ed0ade9f542f1922fad0ed025e88a1f368e791e1d01fae69718f0ec3c7b98c8",
                    ["terracotta-0.4.2-windows-arm64.exe"] = "30a15c5c53e5817c5a3634532172559327474741d3b2c7ef4e8a30acc6f59cdcf3570bf5f583e3cbe9e2abc8253e977c1abda1e9f36c88c4e99240da257347d0",
                }),
            ["macos-x86_64"] = new(
                "a762e4b2d6f84e899292b9e3856d009411a516d3c47f54575f843ce082f63dff2baa68ba0faa844b8b64fb12e91017386f15f5e7f975f8ee605bf8d4217cb091",
                new Dictionary<string, string>
                {
                    ["terracotta-0.4.2-macos-x86_64"] = "24efb85390eff88a538ed7e503fb1488e5e622730ca30c741a0e8b4c8f4e8d4868a2f9f38da8de540aeb535af2fd1e41c7081dae9c700e8a1a03b6c540218164",
                    ["terracotta-0.4.2-macos-x86_64.pkg"] = "0f80437061231018ec0f860f875aba02cae9e0b36d21f2db8e99d25948806e1119f7844a4d4acca01d6124d7079718762cdebe3756b005a55632de7029fe0d24",
                }),
            ["macos-arm64"] = new(
                "09e444fea2d9fd19f3e5cb62e29055228345be163924cbd408d947646fafed1012cf48508ee6a155ede3d571e2ffaa72d09ceeb1493c8a60feb05e0699f19ba3",
                new Dictionary<string, string>
                {
                    ["terracotta-0.4.2-macos-arm64"] = "8e59a9d78acd57702dc044d6f2799c6af586b075a262f7d4dbbf0876e1af8d8271e04783c24ff820b801e3b14cd0190ab8403d097f3f2d98b6d911f95ed1e972",
                    ["terracotta-0.4.2-macos-arm64.pkg"] = "b0b72f8883767c359f0700e958bd859dd5932c4674fbdf2a32f7de189fb9f54822d4825a754dc856416c8760338eb8ffca80b40c0a57c608fbe6ae9928888993",
                }),
            ["linux-x86_64"] = new(
                "d326ad95815d04568d485b5038e40ffc47ca54292fa0925eee6f5cea014024f901d661708aac2a743037b990882ad82b4d0b7bb03dc3b2fe720dbf0f3efe1c98",
                new Dictionary<string, string>
                {
                    ["terracotta-0.4.2-linux-x86_64"] = "fac328ba8957a711b03557bb913940f22d61b76608cd203fdf51024b6f94b19f5bc91c9b8a9fa80baf6968e1e6873c1880fd4cf54a2f8e3c6cf1e6ac161f8d0c",
                }),
            ["linux-arm64"] = new(
                "57c08f48d9535e93ad547d2dfc852d267992cc164a7208b42a2da0a6cbc2f21862f610e02a746b4b67150f4dec26b86a4f96eb9bd2f58d124d5b40ba50c6d55e",
                new Dictionary<string, string>
                {
                    ["terracotta-0.4.2-linux-arm64"] = "d807744c2041c98686e4b505324713badea7a0f31e8810be49ae053a63fb6dfc474ac58d678fb93eea0dd5cccff7372d9ec6135a1046f4b306cad35cd90ecacd",
                }),
        };

    /// <summary>state == "exception" 时，按 type 字段取对应的说明。</summary>
    public static readonly IReadOnlyList<string> Exceptions =
    [
        "无法连接到房主，房间可能已经关闭",
        "与房主的连接已断开",
        "联机组件意外退出，请重新加入",
        "联机组件意外退出，请重新创建房间",
        "房主的游戏世界已关闭",
        "房主使用的联机协议不兼容，请双方更新启动器后重试",
    ];

    /// <summary>guest-starting 状态下 difficulty 字段的说明。</summary>
    public static readonly IReadOnlyDictionary<string, string> Difficulties = new Dictionary<string, string>
    {
        ["EASIEST"] = "网络条件极佳，即将连接",
        ["SIMPLE"] = "网络条件良好，即将连接",
        ["MEDIUM"] = "网络条件一般，正在尝试打洞或中继",
        ["TOUGH"] = "网络条件较差，连接可能不稳定",
        ["UNKNOWN"] = "正在检测网络状况",
    };

    private static readonly HttpClient Local = new(new SocketsHttpHandler { UseProxy = false })
    {
        Timeout = System.Threading.Timeout.InfiniteTimeSpan,
    };

    public string Classifier { get; }
    public string Dir { get; }
    public Downloader Dl { get; }
    public Action<string> Log { get; }
    /// <summary>本地 HTTP 接口端口，未启动时为 null。</summary>
    public int? Port { get; private set; }
    public bool Supported => Classifier != null;

    private bool _verified;
    private List<string> _nodes;
    private Process _process;

    /// <summary>root 默认为 工具目录/terracotta，实际文件放在 root/版本号 下。</summary>
    public Terracotta(string root, Downloader downloader, Action<string> log = null)
    {
        Classifier = CurrentClassifier();
        Dir = Path.Combine(root ?? Path.Combine(AppPaths.ToolsDir, "terracotta"), Version);
        Dl = downloader;
        Log = log ?? (_ => { });
    }

    // ------------------------------------------------------------------ 平台与安装包

    /// <summary>把 Platform.OsName / Platform.Arch 映射为安装包分类名，没有对应安装包时返回 null。</summary>
    public static string ClassifierFor(string osName, string arch)
    {
        var system = osName switch
        {
            "windows" => "windows",
            "osx" or "macos" => "macos",
            "linux" => "linux",
            _ => osName,
        };
        var name = $"{system}-{arch}";
        return Packages.ContainsKey(name) ? name : null;
    }

    /// <summary>当前系统的分类名；Windows 10 以下不支持。</summary>
    public static string CurrentClassifier()
    {
        if (Platform.IsWindows && !OperatingSystem.IsWindowsVersionAtLeast(10))
            return null;
        return ClassifierFor(Platform.OsName, Platform.Arch);
    }

    public static string PackageName(string classifier) => $"terracotta-{Version}-{classifier}-pkg.tar.gz";

    /// <summary>安装包的下载地址（官方 GitHub 优先，其后为镜像）。</summary>
    public static List<string> PackageUrls(string classifier) =>
        Downloads.Select(u => u.Replace("{version}", Version).Replace("{name}", PackageName(classifier))).ToList();

    /// <summary>包内主程序的文件名。</summary>
    public static string ExecutableName(string classifier) =>
        $"terracotta-{Version}-{classifier}" + (classifier.StartsWith("windows-", StringComparison.Ordinal) ? ".exe" : "");

    private IReadOnlyDictionary<string, string> Files => Packages[Classifier].Files;

    private bool IsMacPackage => Classifier?.StartsWith("macos-", StringComparison.Ordinal) == true;

    public string Executable() => Path.Combine(Dir, ExecutableName(Classifier));

    private bool FilesVerified()
    {
        if (_verified)
            return true;
        try
        {
            _verified = Files.All(f => Http.FileSha512(Path.Combine(Dir, f.Key)) == f.Value);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _verified = false;
        }
        return _verified;
    }

    /// <summary>文件是否已下载且校验通过（macOS 上还要求 /Applications/terracotta.app 已安装）。</summary>
    public Task<bool> InstalledAsync() => Task.Run(() =>
        Supported && FilesVerified() && (!IsMacPackage || Directory.Exists(MacAppPath)));

    /// <summary>下载并解压陶瓦联机，校验每个文件的 SHA-512；macOS 上随后会弹出系统密码框安装 .pkg。progress(已完成, 总数)。</summary>
    public async Task InstallAsync(Action<int, int> progress = null, CancellationToken cancel = default)
    {
        if (!Supported)
            throw new TerracottaException("当前系统暂不支持陶瓦联机");
        if (!await Task.Run(FilesVerified, cancel))
            await DownloadAndExtractAsync(progress, cancel);
        if (IsMacPackage && !Directory.Exists(MacAppPath))
            await InstallMacPackageAsync(cancel);
        Log("陶瓦联机安装完成");
    }

    private async Task DownloadAndExtractAsync(Action<int, int> progress, CancellationToken cancel)
    {
        var name = PackageName(Classifier);
        var urls = PackageUrls(Classifier);
        var archive = Path.Combine(Path.GetTempPath(), "maox-" + name);
        var packageHash = Packages[Classifier].Hash;
        if (!(File.Exists(archive) && await Task.Run(() => Http.FileSha512(archive), cancel) == packageHash))
        {
            if (File.Exists(archive))
                File.Delete(archive);
            Log($"正在下载陶瓦联机 {Version}...");
            await Dl.DownloadManyAsync([new DownloadTask(urls[0], archive, alternates: urls.Skip(1))], progress, cancel);
            if (await Task.Run(() => Http.FileSha512(archive), cancel) != packageHash)
            {
                File.Delete(archive);
                throw new TerracottaException("陶瓦联机安装包校验失败，请重试");
            }
        }
        await Task.Run(() => Extract(archive), cancel);
        File.Delete(archive);
        _verified = true;
    }

    private void Extract(string archive)
    {
        Directory.CreateDirectory(Dir);
        var members = new Dictionary<string, byte[]>();
        using (var file = File.OpenRead(archive))
        using (var gzip = new GZipStream(file, CompressionMode.Decompress))
        using (var tar = new TarReader(gzip))
        {
            while (tar.GetNextEntry() is { } entry)
            {
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile
                                            or TarEntryType.ContiguousFile))
                    continue;
                var baseName = entry.Name[(entry.Name.LastIndexOf('/') + 1)..];
                if (!Files.ContainsKey(baseName) || entry.DataStream == null)
                    continue;
                using var data = new MemoryStream();
                entry.DataStream.CopyTo(data);
                members[baseName] = data.ToArray();
            }
        }
        foreach (var (filename, sha) in Files)
        {
            if (!members.TryGetValue(filename, out var data))
                throw new TerracottaException($"安装包中缺少 {filename}");
            if (Convert.ToHexStringLower(SHA512.HashData(data)) != sha)
                throw new TerracottaException($"{filename} 校验失败");
            var dest = Path.Combine(Dir, filename);
            File.WriteAllBytes(dest, data);
            if (!filename.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && !filename.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                Platform.MakeExecutable(dest);
        }
    }

    /// <summary>与 HMCL 相同：用 osascript 请求管理员权限执行 installer -pkg，安装 /Applications/terracotta.app 及其后台服务。</summary>
    private async Task InstallMacPackageAsync(CancellationToken cancel)
    {
        var pkg = Path.Combine(Dir, ExecutableName(Classifier) + ".pkg");
        var temp = Directory.CreateTempSubdirectory("maox-terracotta-pkg-");
        try
        {
            var moved = Path.Combine(temp.FullName, Path.GetFileName(pkg));
            File.Copy(pkg, moved, true);
            static string Literal(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            var script = $"do shell script \"installer -pkg \" & quoted form of {Literal(moved)} & \" -target /\" " +
                         $"with prompt {Literal("陶瓦联机需要安装系统组件才能在 macOS 上使用，请输入密码以继续。")} " +
                         "with administrator privileges";
            Log("正在安装陶瓦联机的系统组件，请在弹出的窗口中输入密码...");
            var psi = new ProcessStartInfo("osascript")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add(script);
            using var process = Process.Start(psi) ?? throw new TerracottaException("无法启动系统安装程序");
            var stdout = process.StandardOutput.ReadToEndAsync(cancel);
            var stderr = process.StandardError.ReadToEndAsync(cancel);
            await process.WaitForExitAsync(cancel);
            await stdout;
            var error = await stderr;
            if (process.ExitCode != 0)
            {
                if (error.Contains("-128"))
                    throw new TerracottaException("已取消安装陶瓦联机的系统组件");
                throw new TerracottaException($"安装陶瓦联机的系统组件失败（退出码 {process.ExitCode}）：{error.Trim()}");
            }
            if (!Directory.Exists(MacAppPath))
                throw new TerracottaException("安装陶瓦联机的系统组件失败");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new TerracottaException("找不到系统程序 osascript，无法安装陶瓦联机", e);
        }
        finally
        {
            try
            {
                temp.Delete(true);
            }
            catch (IOException)
            {
            }
        }
    }

    // ------------------------------------------------------------------ 进程与接口

    private async Task<(int Status, byte[] Body)> RequestAsync(string path,
                                                               IEnumerable<(string Key, string Value)> query = null,
                                                               int timeout = 5)
    {
        var port = Port ?? throw new TerracottaException("联机服务未启动");
        var url = $"http://127.0.0.1:{port}{path}";
        if (query != null)
        {
            var parts = query.Select(q => Uri.EscapeDataString(q.Key) + "=" + Uri.EscapeDataString(q.Value ?? ""))
                .ToList();
            if (parts.Count > 0)
                url += "?" + string.Join("&", parts);
        }
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
            using var response = await Local.GetAsync(url, cts.Token);
            return ((int)response.StatusCode, await response.Content.ReadAsByteArrayAsync(cts.Token));
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException)
        {
            var reason = e is TaskCanceledException ? "连接超时" : e.InnerException?.Message ?? e.Message;
            throw new TerracottaException("无法连接联机服务：" + reason, e);
        }
    }

    public async Task<bool> AliveAsync()
    {
        try
        {
            return (await RequestAsync("/meta", timeout: 2)).Status == 200;
        }
        catch (TerracottaException)
        {
            return false;
        }
    }

    /// <summary>启动（或接管本对象已启动的）陶瓦联机后台进程，记录它的 HTTP 端口。</summary>
    public async Task StartAsync(int timeout = 20)
    {
        if (Port != null && await AliveAsync())
            return;
        var portDir = Directory.CreateTempSubdirectory("maox-terracotta-");
        var portFile = Path.Combine(portDir.FullName, "http");
        try
        {
            var psi = new ProcessStartInfo(Executable())
            {
                WorkingDirectory = Dir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--hmcl");
            psi.ArgumentList.Add(portFile);
            Process process;
            try
            {
                process = Process.Start(psi) ?? throw new TerracottaException("陶瓦联机启动失败");
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                throw new TerracottaException("陶瓦联机启动失败：" + e.Message, e);
            }
            process.StandardInput.Close();
            process.OutputDataReceived += (_, _) => { };
            process.ErrorDataReceived += (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var deadline = DateTime.UtcNow.AddSeconds(timeout);
            int? port = null;
            while (DateTime.UtcNow < deadline)
            {
                port = ReadPortFile(portFile);
                if (port != null)
                    break;
                if (process.HasExited && process.ExitCode != 0)
                {
                    var code = process.ExitCode;
                    process.Dispose();
                    throw new TerracottaException($"陶瓦联机启动失败（退出码 {code}）");
                }
                await Task.Delay(200);
            }
            if (port == null)
            {
                Kill(process);
                process.Dispose();
                throw new TerracottaException("陶瓦联机启动超时");
            }
            _process?.Dispose();
            _process = process;
            Port = port;
        }
        finally
        {
            try
            {
                portDir.Delete(true);
            }
            catch (IOException)
            {
            }
        }
        Log($"陶瓦联机已启动（本地端口 {Port}）");
    }

    private static int? ReadPortFile(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var port = Json.Parse(File.ReadAllText(path)).Long("port", -1);
            return port is > 0 and <= 65535 ? (int)port : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public async Task<JsonNode> MetaAsync() => Json.Parse((await RequestAsync("/meta")).Body);

    /// <summary>
    /// 当前联机状态。常用字段：state（waiting / host-scanning / host-starting / host-ok / guest-connecting /
    /// guest-starting / guest-ok / exception）、room（邀请码）、url（加入后的服务器地址）、difficulty、type（异常类型，对应 Exceptions）、
    /// profiles（[{machine_id, name, vendor, kind: HOST / LOCAL / GUEST}]）。
    /// </summary>
    public async Task<JsonNode> StateAsync()
    {
        var (status, body) = await RequestAsync("/state");
        if (status != 200)
            throw new TerracottaException($"获取联机状态失败（HTTP {status}）");
        try
        {
            return Json.Parse(body);
        }
        catch (System.Text.Json.JsonException e)
        {
            throw new TerracottaException("获取联机状态失败：" + e.Message, e);
        }
    }

    private static bool InChina()
    {
        var lang = CultureInfo.CurrentCulture.Name;
        return lang.Equals("zh-CN", StringComparison.OrdinalIgnoreCase)
               || lang.Equals("zh-Hans-CN", StringComparison.OrdinalIgnoreCase)
               || TimeZoneInfo.Local.BaseUtcOffset == TimeSpan.FromHours(8);
    }

    /// <summary>额外的公共节点列表（与 HMCL 使用同一来源），获取失败时只用陶瓦内置的节点。</summary>
    public async Task<List<string>> PublicNodesAsync()
    {
        if (_nodes == null)
        {
            try
            {
                var nodes = await Dl.FetchJsonAsync(NodeListUrl, mirror: false);
                var china = InChina();
                _nodes = (nodes as JsonArray).Items()
                    .Where(n => n is JsonObject && !string.IsNullOrEmpty(n.Str("url"))
                                && (string.IsNullOrEmpty(n.Str("region"))
                                    || (n.Str("region").ToUpperInvariant() == "CN") == china))
                    .Select(n => n.Str("url"))
                    .ToList();
            }
            catch (Exception)
            {
                _nodes = [];
            }
        }
        return _nodes;
    }

    private static IEnumerable<(string, string)> Nodes(List<string> nodes) => nodes.Select(n => ("public_nodes", n));

    /// <summary>创建房间：开始扫描对局域网开放的世界。</summary>
    public async Task HostAsync(string player)
    {
        var nodes = await PublicNodesAsync();
        await RequestAsync("/state/scanning", new[] { ("player", player) }.Concat(Nodes(nodes)));
    }

    public async Task JoinAsync(string room, string player)
    {
        var nodes = await PublicNodesAsync();
        var (status, _) = await RequestAsync("/state/guesting",
                                             new[] { ("room", room), ("player", player) }.Concat(Nodes(nodes)));
        if (status == 400)
            throw new TerracottaException("邀请码无效，请检查后重试");
        if (status != 200)
            throw new TerracottaException($"加入房间失败（HTTP {status}）");
    }

    /// <summary>退出房间 / 关闭房间 / 取消，回到等待状态。</summary>
    public Task LeaveAsync() => RequestAsync("/state/ide");

    /// <summary>让陶瓦联机退出；若由本对象启动的进程几秒内没有退出则强制结束。</summary>
    public async Task ShutdownAsync()
    {
        if (Port != null)
        {
            try
            {
                await RequestAsync("/panic", [("peaceful", "true")], timeout: 2);
            }
            catch (TerracottaException)
            {
            }
            Port = null;
        }
        var process = _process;
        _process = null;
        if (process == null)
            return;
        using (process)
        {
            // 不用 WaitForExitAsync：Windows 上真正的服务进程（--hmcl2）会继承输出管道，等待 EOF 会一直等到它退出
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!process.HasExited && DateTime.UtcNow < deadline)
                await Task.Delay(100);
            if (!process.HasExited)
                Kill(process);
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    /// <summary>同步版本的 ShutdownAsync，供程序退出时调用。</summary>
    public void Shutdown() => Task.Run(ShutdownAsync).GetAwaiter().GetResult();

    public void Dispose()
    {
        Shutdown();
        GC.SuppressFinalize(this);
    }
}
