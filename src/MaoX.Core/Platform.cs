using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MaoX.Core;

/// <summary>当前操作系统与 CPU 架构，名称与 Minecraft 版本 JSON 中的规则一致。</summary>
public static class Platform
{
    public static readonly bool IsWindows = OperatingSystem.IsWindows();
    public static readonly bool IsMac = OperatingSystem.IsMacOS();
    public static readonly bool IsLinux = OperatingSystem.IsLinux();
    public static readonly bool IsAndroid = OperatingSystem.IsAndroid();
    public static readonly bool IsIOS = OperatingSystem.IsIOS();

    /// <summary>手机/平板：游戏在启动器进程里运行，不能像电脑那样启动 java 子进程。</summary>
    public static readonly bool IsMobile = IsAndroid || IsIOS;

    /// <summary>windows / osx / linux。手机上的移植版 Java 和 LWJGL 都按 Linux 处理。</summary>
    public static readonly string OsName = IsWindows ? "windows" : IsMac ? "osx" : "linux";

    /// <summary>x86_64 / arm64 / x86</summary>
    public static readonly string Arch = RuntimeInformation.OSArchitecture switch
    {
        Architecture.Arm64 => "arm64",
        Architecture.X64 => "x86_64",
        _ => "x86",
    };

    public static readonly bool Is64Bit = Arch != "x86";
    public static readonly bool IsArm = Arch == "arm64";

    public static string OsVersion => Environment.OSVersion.Version.ToString();

    public static string JavaExecutable => IsWindows ? "javaw.exe" : "java";

    /// <summary>给下载的可执行文件（Java、联机工具）加上执行权限。Windows 上什么都不做。</summary>
    public static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path))
            return;
        try
        {
            var mode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute
                                       | UnixFileMode.OtherExecute);
        }
        catch (Exception)
        {
            // 文件系统不支持权限位时忽略
        }
    }

    /// <summary>手机上打开网址、打开文件/文件夹由界面层实现（不能启动 explorer / open 之类的进程）。</summary>
    public static Action<string> MobileOpenUrl { get; set; }

    public static Action<string> MobileOpenPath { get; set; }

    /// <summary>手机上从系统相册选一张图，复制到启动器缓存后返回本地路径；取消返回 null。</summary>
    public static Func<string[], Task<string>> MobilePickImage { get; set; }

    public static bool IsImagePattern(string pattern)
    {
        var ext = (pattern ?? "").Trim().TrimStart('*').ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif" or ".heic" or ".heif";
    }

    public static bool IsImageFilter(params string[] patterns) =>
        patterns is { Length: > 0 } && patterns.All(IsImagePattern);

    /// <summary>按文件头判断常见图片格式。HEIC 等识别不了时返回空。</summary>
    public static string ImageExt(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
            return ".png";
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            return ".jpg";
        if (data.Length >= 6 && data[0] == (byte)'G' && data[1] == (byte)'I' && data[2] == (byte)'F')
            return ".gif";
        if (data.Length >= 12 && data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F'
            && data[8] == (byte)'W' && data[9] == (byte)'E' && data[10] == (byte)'B' && data[11] == (byte)'P')
            return ".webp";
        if (data.Length >= 2 && data[0] == (byte)'B' && data[1] == (byte)'M')
            return ".bmp";
        return "";
    }

    public static string SavePicked(byte[] data, string ext)
    {
        if (string.IsNullOrEmpty(ext))
            ext = ".bin";
        if (!ext.StartsWith('.'))
            ext = "." + ext;
        var dir = Path.Combine(AppPaths.CacheDir, "picked");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ext.ToLowerInvariant());
        File.WriteAllBytes(path, data);
        return path;
    }

    /// <summary>用系统文件管理器打开文件夹，或用默认程序打开文件。</summary>
    public static void OpenPath(string path)
    {
        if (IsMobile)
            MobileOpenPath?.Invoke(path);
        else if (IsWindows)
            Process.Start(new ProcessStartInfo("explorer.exe", Quote(path)) { UseShellExecute = false });
        else
            Process.Start(IsMac ? "open" : "xdg-open", path);
    }

    /// <summary>在文件管理器中选中某个文件。</summary>
    public static void RevealFile(string path)
    {
        if (IsMobile)
            MobileOpenPath?.Invoke(path);
        else if (IsWindows)
            Process.Start(new ProcessStartInfo("explorer.exe", "/select," + Quote(path)) { UseShellExecute = false });
        else if (IsMac)
            Process.Start("open", ["-R", path]);
        else
            Process.Start("xdg-open", Path.GetDirectoryName(path) ?? path);
    }

    public static void OpenUrl(string url)
    {
        if (IsMobile)
            MobileOpenUrl?.Invoke(url);
        else if (IsWindows)
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        else
            Process.Start(IsMac ? "open" : "xdg-open", url);
    }

    private static string Quote(string path) => "\"" + path + "\"";

    /// <summary>物理内存总量（MB）。</summary>
    public static long TotalMemoryMb()
    {
        var info = GC.GetGCMemoryInfo();
        return info.TotalAvailableMemoryBytes / 1048576;
    }
}
