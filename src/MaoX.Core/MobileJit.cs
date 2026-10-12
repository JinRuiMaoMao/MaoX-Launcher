namespace MaoX.Core;

/// <summary>iOS 内置 JIT（实验）。只有安装包里带了 JIT 助手扩展时，iOS 工程才会设置这些钩子。</summary>
public static class MobileJit
{
    /// <summary>返回 null 表示内置 JIT 可用，否则是不可用的原因。</summary>
    public static Func<string> BuiltInUnavailableReason;

    /// <summary>清除助手缓存的开发者磁盘映像，返回给用户看的结果。</summary>
    public static Func<Task<string>> ResetBuiltInDdi;

    /// <summary>当前配对文件的状态，给用户看。</summary>
    public static Func<string> PairingFileStatus;

    /// <summary>导入配对文件（文件内容），返回 null 表示成功，否则是不能导入的原因。</summary>
    public static Func<byte[], string> ImportPairingFile;

    /// <summary>在本机生成配对文件（iOS 27+）不可用的原因，null 表示可用。没有设置时界面不显示这个功能。</summary>
    public static Func<string> GeneratePairingUnavailableReason;

    /// <summary>
    /// 在本机生成配对文件（iOS 27+）。进度回调的参数是给用户看的状态和配对码（还没有时为 null），
    /// 返回成功的说明；失败抛出带说明的 InvalidOperationException，取消时抛出 OperationCanceledException。
    /// </summary>
    public static Func<Action<string, string>, CancellationToken, Task<string>> GeneratePairingFile;

    public static bool BuiltInIncluded => BuiltInUnavailableReason != null;
}
