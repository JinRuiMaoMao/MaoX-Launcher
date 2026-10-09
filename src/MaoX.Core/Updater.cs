using System.Diagnostics;
using System.Net.Http.Headers;

namespace MaoX.Core;

public record UpdateInfo(string Version, string Notes, string Url, long Size, string AssetName, string PageUrl);

/// <summary>
/// 启动器自我更新：从 GitHub Releases 下载与当前平台对应的发布文件并替换自己。
/// Windows 发布的是单个 exe；macOS 发布的是用 ditto 打包的 "MaoX Launcher.app" zip。
/// </summary>
public static class Updater
{
    public const string Repo = "JinRuiMaoMao/MaoX-Launcher";
    public const string RepoUrl = "https://github.com/" + Repo;
    public const string ReleasesUrl = RepoUrl + "/releases";
    private const string LatestApi = "https://api.github.com/repos/" + Repo + "/releases/latest";

    /// <summary>当前平台在 Release 中对应的文件名，不支持的平台为 null。</summary>
    public static string AssetName =>
        Platform.IsWindows ? "MaoX-Launcher-Windows-x64.exe"
        : Platform.IsMac ? $"MaoX-Launcher-macOS-{(Platform.IsArm ? "arm64" : "x64")}.zip"
        : null;

    /// <summary>运行中的 .app 包路径（不是从 .app 启动时为 null）。</summary>
    public static string AppBundle
    {
        get
        {
            var exe = Environment.ProcessPath;
            if (!Platform.IsMac || string.IsNullOrEmpty(exe))
                return null;
            var macOs = Path.GetDirectoryName(exe);
            var contents = Path.GetDirectoryName(macOs);
            var bundle = Path.GetDirectoryName(contents);
            return bundle != null && bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
                   && Path.GetFileName(contents) == "Contents" && Path.GetFileName(macOs) == "MacOS"
                ? bundle
                : null;
        }
    }

    /// <summary>只有发布版（Windows 单文件 exe、macOS .app）才能自我更新，开发时运行的版本不行。</summary>
    // 单文件发布时 Assembly.Location 为空，正好用来判断是不是发布版
#pragma warning disable IL3000
    public static bool CanSelfUpdate =>
        AssetName != null && !string.IsNullOrEmpty(Environment.ProcessPath)
        && (Platform.IsWindows ? string.IsNullOrEmpty(typeof(Updater).Assembly.Location) : AppBundle != null);
#pragma warning restore IL3000

    /// <summary>比较版本号（忽略开头的 v 和 -beta 之类的后缀）。</summary>
    public static bool IsNewer(string candidate, string current) =>
        TryParse(candidate, out var a) && TryParse(current, out var b) && a > b;

    private static bool TryParse(string text, out Version version)
    {
        text = (text ?? "").Trim().TrimStart('v', 'V');
        var dash = text.IndexOfAny(['-', '+']);
        if (dash >= 0)
            text = text[..dash];
        if (!text.Contains('.'))
            text += ".0";
        return Version.TryParse(text, out version);
    }

    /// <summary>检查最新发布。没有更新（或还没有任何发布）时返回 null。</summary>
    public static async Task<UpdateInfo> CheckAsync(CancellationToken cancel = default)
    {
        HttpResult result;
        try
        {
            result = await Http.SendAsync(LatestApi, headers: new Dictionary<string, string>
            {
                ["Accept"] = "application/vnd.github+json",
                ["X-GitHub-Api-Version"] = "2022-11-28",
            }, timeout: 15, cancel: cancel);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new DownloadException("无法连接 GitHub：" + (e is TaskCanceledException ? "连接超时" : e.Message), e);
        }
        if (result.Status == 404)
            return null;
        if (result.Status == 403)
            throw new DownloadException("GitHub 访问次数过多，请稍后再试");
        if (!result.Ok)
            throw new DownloadException($"检查更新失败（HTTP {result.Status}）");
        var json = result.Json();
        var version = (json.Str("tag_name") ?? "").Trim().TrimStart('v', 'V');
        if (!IsNewer(version, Mc.LauncherVersion))
            return null;
        var assetName = AssetName;
        var asset = json.Items("assets").FirstOrDefault(a => a.Str("name") == assetName);
        return new UpdateInfo(version, (json.Str("body") ?? "").Trim(), asset?.Str("browser_download_url"),
                              asset?.Long("size", 0) ?? 0, assetName, json.Str("html_url") ?? ReleasesUrl);
    }

    /// <summary>下载更新文件到缓存目录，返回文件路径。</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, Action<long, long> progress,
                                                   CancellationToken cancel = default)
    {
        if (string.IsNullOrEmpty(info.Url))
            throw new DownloadException($"这个版本没有提供 {info.AssetName}，请到发布页手动下载");
        var dir = Path.Combine(AppPaths.CacheDir, "update");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, info.AssetName);
        var part = target + ".part";
        using (var request = new HttpRequestMessage(HttpMethod.Get, info.Url))
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            using var response = await Http.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
            if (!response.IsSuccessStatusCode)
                throw new HttpStatusException((int)response.StatusCode, info.Url);
            var total = response.Content.Headers.ContentLength ?? info.Size;
            await using var input = await response.Content.ReadAsStreamAsync(cancel);
            await using var output = File.Create(part);
            var buffer = new byte[81920];
            long done = 0;
            var last = DateTime.MinValue;
            while (true)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                var read = await input.ReadAsync(buffer, timeout.Token);
                if (read == 0)
                    break;
                await output.WriteAsync(buffer.AsMemory(0, read), cancel);
                done += read;
                if ((DateTime.UtcNow - last).TotalMilliseconds > 100)
                {
                    last = DateTime.UtcNow;
                    progress?.Invoke(done, total);
                }
            }
            progress?.Invoke(done, total);
            if (info.Size > 0 && done != info.Size)
                throw new DownloadException("更新文件下载不完整，请重试");
        }
        File.Move(part, target, true);
        return target;
    }

    /// <summary>用下载好的文件替换当前程序并启动新版本。调用成功后应立即退出当前进程。</summary>
    public static void Apply(string downloaded)
    {
        if (!CanSelfUpdate)
            throw new InvalidOperationException("当前运行的不是发布版，无法自动更新");
        if (Platform.IsWindows)
            ApplyWindows(downloaded);
        else
            ApplyMac(downloaded);
    }

    private static void ApplyWindows(string downloaded)
    {
        // 正在运行的 exe 不能覆盖，但可以改名
        var exe = Environment.ProcessPath!;
        var old = exe + ".old";
        TryDelete(old);
        File.Move(exe, old);
        try
        {
            File.Copy(downloaded, exe);
        }
        catch (Exception)
        {
            File.Move(old, exe);
            throw;
        }
        TryDelete(downloaded);
        Process.Start(new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        });
    }

    private static void ApplyMac(string downloaded)
    {
        var bundle = AppBundle!;
        var parent = Path.GetDirectoryName(bundle)!;
        // 解压到同一个文件夹下，保证之后的改名不跨磁盘
        var staging = Path.Combine(parent, ".maox-update-" + Guid.NewGuid().ToString("N")[..8]);
        Run("ditto", "-x", "-k", downloaded, staging);
        var fresh = Directory.GetDirectories(staging, "*.app").FirstOrDefault()
                    ?? throw new DownloadException("更新包里没有找到 .app");
        var old = bundle + ".old";
        TryDeleteDirectory(old);
        Directory.Move(bundle, old);
        try
        {
            Directory.Move(fresh, bundle);
        }
        catch (Exception)
        {
            Directory.Move(old, bundle);
            throw;
        }
        TryDeleteDirectory(staging);
        TryDelete(downloaded);
        Run("xattr", "-dr", "com.apple.quarantine", bundle);
        Process.Start("open", ["-n", bundle]);
    }

    /// <summary>删除上次更新留下的旧版本。旧进程可能还没完全退出，所以在后台重试几次。</summary>
    public static void CleanupOldVersion()
    {
        var path = Platform.IsWindows ? Environment.ProcessPath + ".old" : AppBundle is { } b ? b + ".old" : null;
        if (path == null || (!File.Exists(path) && !Directory.Exists(path)))
            return;
        Task.Run(async () =>
        {
            for (var i = 0; i < 20 && (File.Exists(path) || Directory.Exists(path)); i++)
            {
                TryDelete(path);
                TryDeleteDirectory(path);
                await Task.Delay(500);
            }
        });
    }

    private static void Run(string file, params string[] args)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false });
            process?.WaitForExit(60_000);
            if (process is { HasExited: true, ExitCode: not 0 } && file == "ditto")
                throw new DownloadException($"解压更新包失败（ditto 退出码 {process.ExitCode}）");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            if (file == "ditto")
                throw new DownloadException("解压更新包失败：" + e.Message, e);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
