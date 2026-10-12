namespace MaoX.Core;

/// <summary>iOS 内置 JIT（实验）。只有安装包里带了 JIT 助手扩展时，iOS 工程才会设置这些钩子。</summary>
public static class MobileJit
{
    /// <summary>返回 null 表示内置 JIT 可用，否则是不可用的原因。</summary>
    public static Func<string> BuiltInUnavailableReason;

    /// <summary>清除助手缓存的开发者磁盘映像，返回给用户看的结果。</summary>
    public static Func<Task<string>> ResetBuiltInDdi;

    public static bool BuiltInIncluded => BuiltInUnavailableReason != null;
}
