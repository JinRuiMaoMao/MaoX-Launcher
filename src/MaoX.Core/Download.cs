using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using static MaoX.Core.I18n;

namespace MaoX.Core;

public class DownloadException : Exception
{
    public int? Status { get; init; }

    public DownloadException(string message, Exception inner = null) : base(message, inner)
    {
    }
}

public class HttpStatusException : DownloadException
{
    public HttpStatusException(int status, string url) : base($"HTTP {status}：{url}")
    {
        Status = status;
    }
}

/// <summary>一个待下载的文件。sha1 / size 用于判断本地文件是否完整。</summary>
public class DownloadTask
{
    public string Url { get; }
    public string Path { get; }
    public string Sha1 { get; }
    public long? Size { get; }
    public IReadOnlyList<string> Alternates { get; }

    public DownloadTask(string url, string path, string sha1 = null, long? size = null,
                        IEnumerable<string> alternates = null)
    {
        Url = url;
        Path = path;
        Sha1 = string.IsNullOrEmpty(sha1) ? null : sha1.ToLowerInvariant();
        Size = size is > 0 ? size : null;
        Alternates = alternates?.ToList() ?? [];
    }

    public bool IsValid()
    {
        var info = new FileInfo(Path);
        if (!info.Exists)
            return false;
        if (Size != null)
            return info.Length == Size;
        if (Sha1 != null)
            return Http.FileSha1(Path) == Sha1;
        return true;
    }
}

/// <summary>共享的 HTTP 客户端与工具方法。</summary>
public static class Http
{
    public static readonly string UserAgent = "MaoXLauncher/" + Mc.LauncherVersion;

    public static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            MaxConnectionsPerServer = 32,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 8,
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    public static string FileSha1(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA1.HashData(stream));
    }

    public static string FileSha512(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA512.HashData(stream));
    }

    public static string FileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>发送请求并返回 (状态码, 响应体, 响应头)。不会因 4xx/5xx 抛异常，网络错误会抛出。</summary>
    public static async Task<HttpResult> SendAsync(string url, HttpMethod method = null, HttpContent body = null,
                                                   IDictionary<string, string> headers = null, int timeout = 20,
                                                   CancellationToken cancel = default)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Get, url) { Content = body };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (headers != null)
        {
            foreach (var (key, value) in headers)
                request.Headers.TryAddWithoutValidation(key, value);
        }
        using var cts = TaskContext.Link(cancel);
        cts.CancelAfter(TimeSpan.FromSeconds(timeout));
        using var response = await Client.SendAsync(request, cts.Token);
        var data = await response.Content.ReadAsByteArrayAsync(cts.Token);
        var all = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers.Concat(response.Content.Headers))
            all[header.Key] = string.Join(",", header.Value);
        return new HttpResult((int)response.StatusCode, data, all);
    }

    public static StringContent JsonContent(JsonNode payload) =>
        new(Json.Serialize(payload), Encoding.UTF8, "application/json");

    public static FormUrlEncodedContent FormContent(IDictionary<string, string> form) => new(form);
}

public record HttpResult(int Status, byte[] Body, IReadOnlyDictionary<string, string> Headers)
{
    public bool Ok => Status is >= 200 and < 300;

    public string Text => Encoding.UTF8.GetString(Body);

    /// <summary>把响应体解析为 JSON，失败时返回空对象。</summary>
    public JsonNode Json()
    {
        try
        {
            return Body.Length > 0 ? JsonNode.Parse(Body) ?? new JsonObject() : new JsonObject();
        }
        catch (System.Text.Json.JsonException)
        {
            return new JsonObject();
        }
    }

    public string Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
}

/// <summary>带镜像回退、重试和并发的下载器（对应 BMCLAPI / MCIM 镜像规则）。</summary>
public class Downloader
{
    public const string Bmclapi = "https://bmclapi2.bangbang93.com";
    public const string Mcim = "https://mod.mcimirror.top";
    private const string ProbeUrl = "https://libraries.minecraft.net/com/mojang/logging/1.1.1/logging-1.1.1.jar";

    private static readonly (string Prefix, string Replacement)[] BmclapiRules =
    [
        ("https://piston-meta.mojang.com", Bmclapi),
        ("https://launchermeta.mojang.com", Bmclapi),
        ("https://launcher.mojang.com", Bmclapi),
        ("https://piston-data.mojang.com", Bmclapi),
        ("https://resources.download.minecraft.net", Bmclapi + "/assets"),
        ("https://libraries.minecraft.net", Bmclapi + "/maven"),
        ("https://maven.fabricmc.net", Bmclapi + "/maven"),
        ("https://meta.fabricmc.net", Bmclapi + "/fabric-meta"),
        ("https://maven.minecraftforge.net", Bmclapi + "/maven"),
        ("https://maven.neoforged.net/releases", Bmclapi + "/maven"),
        ("https://api.modrinth.com", Mcim + "/modrinth"),
        ("https://cdn.modrinth.com", Mcim),
        ("https://edge.forgecdn.net", Mcim),
        ("https://mediafilez.forgecdn.net", Mcim),
    ];

    private static readonly SemaphoreSlim AutoLock = new(1, 1);
    private static string _autoSource;

    public string Source { get; }
    public int Threads { get; }
    public int Retries { get; }
    public int Timeout { get; }

    public Downloader(string source = "auto", int threads = 16, int retries = 3, int timeout = 30)
    {
        Source = source;
        Threads = Math.Max(1, threads);
        Retries = retries;
        Timeout = timeout;
    }

    public static string MirrorUrl(string url, string source)
    {
        if (source != "bmclapi")
            return url;
        foreach (var (prefix, replacement) in BmclapiRules)
        {
            if (url.StartsWith(prefix, StringComparison.Ordinal))
                return replacement + url[prefix.Length..];
        }
        return url;
    }

    /// <summary>同时请求官方源和镜像源的同一个小文件，返回先成功的那个。结果在进程内缓存。</summary>
    public static async Task<string> DetectFastestSourceAsync(int timeout = 8)
    {
        await AutoLock.WaitAsync();
        try
        {
            if (_autoSource != null)
                return _autoSource;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
            var probes = new[] { "official", "bmclapi" }.Select(async source =>
            {
                using var response = await Http.Client.GetAsync(MirrorUrl(ProbeUrl, source), cts.Token);
                response.EnsureSuccessStatusCode();
                await response.Content.ReadAsByteArrayAsync(cts.Token);
                return source;
            }).ToList();
            var result = "official";
            while (probes.Count > 0)
            {
                var done = await Task.WhenAny(probes);
                probes.Remove(done);
                if (done.IsCompletedSuccessfully)
                {
                    result = done.Result;
                    break;
                }
            }
            cts.Cancel();
            _autoSource = result;
            return result;
        }
        finally
        {
            AutoLock.Release();
        }
    }

    public async Task<string> ResolvedSourceAsync() =>
        Source == "auto" ? await DetectFastestSourceAsync() : Source;

    /// <summary>优先使用选定的下载源，失败后依次回退到官方地址、镜像地址和备用地址。</summary>
    public async Task<List<string>> CandidateUrlsAsync(string url, IEnumerable<string> alternates = null)
    {
        var source = await ResolvedSourceAsync();
        var urls = new List<string>();
        foreach (var candidate in new[] { MirrorUrl(url, source), url, MirrorUrl(url, "bmclapi") }
                     .Concat(alternates ?? []))
        {
            if (!urls.Contains(candidate))
                urls.Add(candidate);
        }
        return urls;
    }

    private async Task<T> WithFallbackAsync<T>(string url, Func<string, Task<T>> action,
                                                IEnumerable<string> alternates = null, bool mirror = true)
    {
        Exception last = null;
        var candidates = mirror ? await CandidateUrlsAsync(url, alternates) : [url];
        foreach (var candidate in candidates)
        {
            for (var attempt = 0; attempt < Retries; attempt++)
            {
                TaskContext.ThrowIfCancelled();
                try
                {
                    return await action(candidate);
                }
                catch (HttpStatusException e)
                {
                    last = e;
                    if (e.Status is 400 or 403 or 404 or 410)
                        break;
                }
                catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException
                                              or DownloadException or System.Text.Json.JsonException
                                          && !TaskContext.Token.IsCancellationRequested)
                {
                    last = e;
                }
                await Task.Delay(500 * (attempt + 1), TaskContext.Token);
            }
        }
        throw new DownloadException($"{url} ({last?.Message})", last)
        {
            Status = (last as DownloadException)?.Status,
        };
    }

    private async Task<byte[]> GetBytesAsync(string url, HttpMethod method = null, JsonNode payload = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Get, url);
        if (payload != null)
            request.Content = Http.JsonContent(payload);
        using var cts = TaskContext.Link(default);
        cts.CancelAfter(TimeSpan.FromSeconds(Timeout));
        using var response = await Http.Client.SendAsync(request, cts.Token);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new HttpStatusException((int)response.StatusCode, url);
        return await response.Content.ReadAsByteArrayAsync(cts.Token);
    }

    /// <summary>mirror=false 用于镜像可能过期的元数据（如 Forge 版本列表）。</summary>
    public Task<byte[]> FetchAsync(string url, bool mirror = true) =>
        WithFallbackAsync(url, u => GetBytesAsync(u), mirror: mirror);

    public async Task<string> FetchTextAsync(string url, bool mirror = true) =>
        Encoding.UTF8.GetString(await FetchAsync(url, mirror));

    public Task<JsonNode> FetchJsonAsync(string url, bool mirror = true) =>
        WithFallbackAsync(url, async u => JsonNode.Parse(await GetBytesAsync(u)), mirror: mirror);

    public Task<JsonNode> PostJsonAsync(string url, JsonNode payload, bool mirror = true) =>
        WithFallbackAsync(url, async u => JsonNode.Parse(await GetBytesAsync(u, HttpMethod.Post, payload)),
                          mirror: mirror);

    public async Task DownloadAsync(DownloadTask task, CancellationToken cancel = default)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(task.Path)!);
        var tmp = task.Path + ".part";
        await WithFallbackAsync(task.Url, async u =>
        {
            try
            {
                using var cts = TaskContext.Link(cancel);
                cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(Timeout, 30) * 10));
                using var response = await Http.Client.GetAsync(u, HttpCompletionOption.ResponseHeadersRead,
                                                                cts.Token);
                if (response.StatusCode != HttpStatusCode.OK)
                    throw new HttpStatusException((int)response.StatusCode, u);
                using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                await using (var input = await response.Content.ReadAsStreamAsync(cts.Token))
                await using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None,
                                                         1 << 16, true))
                {
                    var buffer = new byte[1 << 16];
                    int read;
                    while ((read = await input.ReadAsync(buffer, cts.Token)) > 0)
                    {
                        sha1.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                    }
                }
                if (task.Sha1 != null && Convert.ToHexStringLower(sha1.GetHashAndReset()) != task.Sha1)
                    throw new DownloadException(T("SHA1 校验失败"));
                File.Move(tmp, task.Path, true);
                return true;
            }
            finally
            {
                if (File.Exists(tmp))
                    File.Delete(tmp);
            }
        }, task.Alternates);
    }

    /// <summary>并发下载所有缺失或损坏的文件，返回实际下载的文件数。progress(已完成, 总数)。</summary>
    public async Task<int> DownloadManyAsync(IEnumerable<DownloadTask> tasks, Action<int, int> progress = null,
                                             CancellationToken cancel = default)
    {
        using var linked = TaskContext.Link(cancel);
        cancel = linked.Token;
        var unique = new Dictionary<string, DownloadTask>(Platform.IsWindows
                                                              ? StringComparer.OrdinalIgnoreCase
                                                              : StringComparer.Ordinal);
        foreach (var task in tasks)
            unique.TryAdd(System.IO.Path.GetFullPath(task.Path), task);
        var pending = await Task.Run(() => unique.Values.AsParallel().Where(t => !t.IsValid()).ToList(), cancel);
        var total = pending.Count;
        if (total == 0)
            return 0;

        var done = 0;
        var failures = new List<(DownloadTask Task, Exception Error)>();
        await Parallel.ForEachAsync(pending, new ParallelOptions
        {
            MaxDegreeOfParallelism = Threads,
            CancellationToken = cancel,
        }, async (task, token) =>
        {
            try
            {
                await DownloadAsync(task, token);
            }
            catch (Exception e) when (e is not OperationCanceledException || !cancel.IsCancellationRequested)
            {
                lock (failures)
                    failures.Add((task, e));
            }
            progress?.Invoke(Interlocked.Increment(ref done), total);
        });
        if (failures.Count > 0)
        {
            var (task, error) = failures[0];
            throw new DownloadException(
                F("{0} 个文件下载失败，例如 {1}：{2}", failures.Count, System.IO.Path.GetFileName(task.Path), error.Message), error);
        }
        return total;
    }
}
