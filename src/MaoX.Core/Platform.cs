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
