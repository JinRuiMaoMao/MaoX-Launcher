using System.Diagnostics;
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

    public static HttpClient Client { get; private set; } = CreateClient();

    /// <summary>按配置重建 HTTP 客户端（代理、DoH）。启动时和改设置后调用。</summary>
    public static void ApplyNetwork(LauncherConfig cfg)
    {
        var next = CreateClient(cfg?.Proxy, cfg?.Doh == true);
        var old = Client;
        Client = next;
        try
        {
            old.Dispose();
        }
        catch (Exception)
        {
        }
    }

    private static HttpClient CreateClient(string proxy = null, bool doh = false)
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
        if (!string.IsNullOrWhiteSpace(proxy) && Uri.TryCreate(proxy.Trim(), UriKind.Absolute, out var uri))
        {
            handler.UseProxy = true;
            handler.Proxy = new WebProxy(uri);
        }
        if (doh)
            handler.ConnectCallback = Doh.ConnectAsync;
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

/// <summary>一个任务的下载量：下载器累加收到的字节，界面定时取样，算出速度和剩余时间。</summary>
public sealed class DownloadMeter
{
    private const long Window = 3000;
    private readonly Queue<(long Ticks, long Bytes)> _samples = new();
    private long _received;
    private long _batchStart;
    private long _batchExpected;

    public long Received => Interlocked.Read(ref _received);

    public void Add(long bytes) => Interlocked.Add(ref _received, bytes);

    /// <summary>开始一批下载。expected 为这批文件的总大小，有大小未知的文件时为 null（不估算剩余时间）。</summary>
    public void BeginBatch(long? expected)
    {
        Interlocked.Exchange(ref _batchStart, Received);
        Interlocked.Exchange(ref _batchExpected, expected ?? 0);
    }

    /// <summary>最近几秒的速度和剩余时间，最近没有下载时为 null。只在一个线程（界面定时器）上调用。</summary>
    public string Describe()
    {
        var now = Environment.TickCount64;
        var bytes = Received;
        _samples.Enqueue((now, bytes));
        while (_samples.Count > 1 && now - _samples.Peek().Ticks > Window)
            _samples.Dequeue();
        var (start, startBytes) = _samples.Peek();
        if (now - start < 500 || bytes == startBytes)
            return null;
        var rate = (bytes - startBytes) * 1000.0 / (now - start);
        var text = Downloader.FormatRate(rate);
        var remaining = Interlocked.Read(ref _batchExpected) - (bytes - Interlocked.Read(ref _batchStart));
        if (Interlocked.Read(ref _batchExpected) > 0 && remaining > 0)
            text += "，" + F("剩余约 {0}", FormatDuration(remaining / rate));
        return text;
    }

    private static string FormatDuration(double seconds) =>
        seconds < 60 ? F("{0} 秒", Math.Max(1, (int)Math.Ceiling(seconds)))
        : seconds < 3600 ? F("{0} 分 {1} 秒", (int)seconds / 60, (int)seconds % 60)
        : F("{0} 小时 {1} 分", (int)seconds / 3600, (int)seconds % 3600 / 60);
}

/// <summary>带镜像回退、重试和并发的下载器（对应 BMCLAPI / MCIM 镜像规则）。</summary>
public class Downloader
{
    public const string Bmclapi = "https://bmclapi2.bangbang93.com";
    public const string Mcim = "https://mod.mcimirror.top";
    private const string ProbeUrl = "https://libraries.minecraft.net/com/mojang/logging/1.1.1/logging-1.1.1.jar";
    /// <summary>GitHub 下载加速前缀，直接拼在完整的 github.com 地址前面。</summary>
    public static readonly string[] GithubMirrors = ["https://ghfast.top/", "https://gh-proxy.com/"];

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
    private static DateTime _autoRetryAt;

    /// <summary>上一次自动测速的结果，写进日志方便排查下载慢</summary>
    public static string AutoSourceDetail { get; private set; }

    // 下载中比较两个源：0 还没比较，1 正在比较，2 已经选定
    private static int _trialState;
    private static long _officialBytes;
    private static long _mirrorBytes;
    private const double FastEnough = 10 << 20;

    public string Source { get; }
    public int Threads { get; }
    public int Retries { get; }
    public int Timeout { get; }

    /// <summary>下载中换源时写日志，没有时不记</summary>
    public Action<string> Log { get; set; }

    public static string SourceName(string source) => source == "bmclapi" ? T("BMCLAPI 镜像") : T("Mojang 官方");

    public static string FormatRate(double bytesPerSecond) =>
        bytesPerSecond >= 1 << 20 ? $"{bytesPerSecond / (1 << 20):0.0} MB/s" : $"{bytesPerSecond / 1024:0} KB/s";

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

    /// <summary>
    /// 同时请求官方源和镜像源的同一个小文件，返回先成功的那个。结果在进程内缓存；两边都失败（比如刚安装时系统还在询问
    /// 是否允许联网）时暂时用官方源，30 秒后再测，不把失败的结果一直留到退出。
    /// </summary>
    public static async Task<string> DetectFastestSourceAsync(int timeout = 8)
    {
        await AutoLock.WaitAsync();
        try
        {
            if (_autoSource != null)
                return _autoSource;
            if (DateTime.UtcNow < _autoRetryAt)
                return "official";
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
            var watch = Stopwatch.StartNew();
            var probes = new[] { "official", "bmclapi" }.Select(async source =>
            {
                using var response = await Http.Client.GetAsync(MirrorUrl(ProbeUrl, source), cts.Token);
                response.EnsureSuccessStatusCode();
                await response.Content.ReadAsByteArrayAsync(cts.Token);
                return source;
            }).ToList();
            string result = null;
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
            if (result == null)
            {
                _autoRetryAt = DateTime.UtcNow.AddSeconds(30);
                AutoSourceDetail = T("自动测速失败，暂时使用官方源");
                return "official";
            }
            AutoSourceDetail = F("自动测速 {0} ms", watch.ElapsedMilliseconds);
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
        var meter = TaskContext.Meter;
        var mirrored = MirrorUrl(task.Url, "bmclapi");
        await WithFallbackAsync(task.Url, async u =>
        {
            var mirror = u == mirrored && u != task.Url;
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
                        meter?.Add(read);
                        if (mirror)
                            Interlocked.Add(ref _mirrorBytes, read);
                        else
                            Interlocked.Add(ref _officialBytes, read);
                    }
                }
                if (task.Sha1 != null && Convert.ToHexStringLower(sha1.GetHashAndReset()) != task.Sha1)
                    throw new DownloadException(T("SHA1 校验失败"));
                File.Move(tmp, task.Path, true);
                DownloadHistory.Record(task, true, null);
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
        var expected = pending.All(t => t.Size != null) ? pending.Sum(t => t.Size!.Value) : (long?)null;
        TaskContext.Meter?.BeginBatch(expected);

        var done = 0;
        var failures = new List<(DownloadTask Task, Exception Error)>();
        using var finished = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        var trial = Source == "auto" && (total >= 100 || expected >= 30 << 20)
            ? CompareSourcesAsync(finished.Token)
            : Task.CompletedTask;
        try
        {
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
                    DownloadHistory.Record(task, false, e.Message);
                    lock (failures)
                        failures.Add((task, e));
                }
                progress?.Invoke(Interlocked.Increment(ref done), total);
            });
        }
        finally
        {
            finished.Cancel();
            await trial;
        }
        if (failures.Count > 0)
        {
            var (task, error) = failures[0];
            throw new DownloadException(
                F("{0} 个文件下载失败，例如 {1}：{2}", failures.Count, System.IO.Path.GetFileName(task.Path), error.Message), error);
        }
        return total;
    }

    /// <summary>
    /// 开头的测速只比较谁先返回一个小文件，不代表下载大量文件时谁更快。第一次下载大量文件时，用当前的源下载几秒、
    /// 再换另一个源下载几秒，之后用较快的那个（进程内只比较一次）。批次提前结束时恢复原来的源，下次再比较。
    /// </summary>
    private async Task CompareSourcesAsync(CancellationToken stop)
    {
        if (Interlocked.CompareExchange(ref _trialState, 1, 0) != 0)
            return;
        var first = Volatile.Read(ref _autoSource);
        var decided = false;
        try
        {
            if (first == null)
                return;
            var second = first == "bmclapi" ? "official" : "bmclapi";
            var firstRate = await MeasureAsync(first, stop);
            if (firstRate >= FastEnough)
            {
                decided = true;
                return;
            }
            Volatile.Write(ref _autoSource, second);
            var secondRate = await MeasureAsync(second, stop);
            var best = secondRate > firstRate ? second : first;
            Volatile.Write(ref _autoSource, best);
            decided = true;
            AutoSourceDetail = F("下载中测速：{0} {1}，{2} {3}", SourceName(first), FormatRate(firstRate),
                                 SourceName(second), FormatRate(secondRate));
            Log?.Invoke(F("下载源：{0}（{1}）", SourceName(best), AutoSourceDetail));
        }
        catch (OperationCanceledException)
        {
            if (first != null)
                Volatile.Write(ref _autoSource, first);
        }
        finally
        {
            Volatile.Write(ref _trialState, decided ? 2 : 0);
        }
    }

    /// <summary>这个源接下来几秒的下载速度（字节/秒）。前两秒是新源建立连接的时间，不计入。</summary>
    private static async Task<double> MeasureAsync(string source, CancellationToken stop)
    {
        await Task.Delay(2000, stop);
        var start = SourceBytes(source);
        var watch = Stopwatch.StartNew();
        await Task.Delay(6000, stop);
        return (SourceBytes(source) - start) / watch.Elapsed.TotalSeconds;
    }

    private static long SourceBytes(string source) =>
        source == "bmclapi" ? Interlocked.Read(ref _mirrorBytes) : Interlocked.Read(ref _officialBytes);
}
