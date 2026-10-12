namespace MaoX.Core;

/// <summary>
/// 当前后台任务的取消令牌，随 async 调用链自动传递（AsyncLocal）。
/// 启动器的任务中心在任务开始时设置；下载器、HTTP 请求和耗时循环会检查它，
/// 这样不必把 CancellationToken 一层层传进所有方法也能单独取消某个任务。
/// 下载计量（Meter）同样随调用链传递，用来在任务上显示下载速度。
/// </summary>
public static class TaskContext
{
    private static readonly AsyncLocal<CancellationToken> Ambient = new();
    private static readonly AsyncLocal<DownloadMeter> AmbientMeter = new();

    public static CancellationToken Token
    {
        get => Ambient.Value;
        set => Ambient.Value = value;
    }

    /// <summary>当前任务的下载计量，没有时为 null。</summary>
    public static DownloadMeter Meter
    {
        get => AmbientMeter.Value;
        set => AmbientMeter.Value = value;
    }

    public static void ThrowIfCancelled() => Ambient.Value.ThrowIfCancellationRequested();

    /// <summary>把调用方传入的令牌与当前任务的令牌合并。</summary>
    public static CancellationTokenSource Link(CancellationToken cancel) =>
        CancellationTokenSource.CreateLinkedTokenSource(cancel, Ambient.Value);
}
