using System.Runtime.InteropServices;
using Foundation;
using MaoX.Core;
using static MaoX.Core.I18n;

namespace MaoX.iOS.Game;

/// <summary>
/// 陶瓦联机 iOS 版：libterracotta.dylib 由 CI 用官方源码打补丁编译（src/MaoX.iOS/Terracotta），在启动器进程里运行。
/// 房间里的 EasyTier 不建虚拟网卡（--no-tun），所以不需要 VPN；房主的局域网端口由游戏日志告诉它，因为 iOS 应用收不到组播。
/// </summary>
internal sealed unsafe class IosTerracotta : IMobileTerracotta
{
    public static readonly IosTerracotta Instance = new();

    private readonly Lock _lock = new();
    private nint _lib;
    private string _version;

    private IosTerracotta()
    {
    }

    public string LibraryDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "terracotta");

    public string BundledLibrary => Path.Combine(NSBundle.MainBundle.BundlePath, "Frameworks", "libterracotta.dylib");

    public bool Started => _version != null;

    private nint Fn(string name)
    {
        var fn = ObjCRuntime.Dlfcn.dlsym(_lib, name);
        return fn != 0 ? fn : throw new EntryPointNotFoundException(name);
    }

    public Task<string> StartAsync(string libraryPath) => Task.Run(() =>
    {
        lock (_lock)
        {
            if (_version != null)
                return _version;
            _lib = ObjCRuntime.Dlfcn.dlopen(libraryPath, 0x2);
            if (_lib == 0)
                throw new TerracottaException(F("陶瓦联机启动失败：{0}", ObjCRuntime.Dlfcn.dlerror() ?? libraryPath));

            var data = Path.Combine(LibraryDir, "data");
            Directory.CreateDirectory(data);
            var fd = -1;
            using (var log = new FileStream(Path.Combine(LibraryDir, "application.log"), FileMode.Create,
                                            FileAccess.Write, FileShare.ReadWrite))
            {
                var dup = ObjCRuntime.Dlfcn.dlsym((nint)(-2), "dup");
                if (dup != 0)
                    fd = ((delegate* unmanaged<int, int>)dup)((int)log.SafeFileHandle.DangerousGetHandle());
            }
            var dir = Marshal.StringToCoTaskMemUTF8(data);
            try
            {
                var code = ((delegate* unmanaged<nint, int, int>)Fn("terracotta_start"))(dir, fd);
                if (code != 0)
                    throw new TerracottaException(F("陶瓦联机启动失败（代码 {0}）", code));
            }
            finally
            {
                Marshal.FreeCoTaskMem(dir);
            }
            var meta = TakeString(((delegate* unmanaged<nint>)Fn("terracotta_get_metadata"))());
            return _version = meta?.Split('\n')[0] ?? Terracotta.Version;
        }
    });

    private string TakeString(nint value)
    {
        if (value == 0)
            return null;
        try
        {
            return Marshal.PtrToStringUTF8(value);
        }
        finally
        {
            ((delegate* unmanaged<nint, void>)Fn("terracotta_free"))(value);
        }
    }

    public string State() =>
        TakeString(((delegate* unmanaged<nint>)Fn("terracotta_get_state"))()) ?? """{"state":"waiting"}""";

    public void SetWaiting() => ((delegate* unmanaged<void>)Fn("terracotta_set_waiting"))();

    public void SetScanning(string player)
    {
        var name = Marshal.StringToCoTaskMemUTF8(player);
        try
        {
            ((delegate* unmanaged<nint, nint, void>)Fn("terracotta_set_scanning"))(0, name);
        }
        finally
        {
            Marshal.FreeCoTaskMem(name);
        }
    }

    public Task<bool> SetGuestingAsync(string room, string player) => Task.Run(() =>
    {
        var code = Marshal.StringToCoTaskMemUTF8(room);
        var name = Marshal.StringToCoTaskMemUTF8(player);
        try
        {
            return ((delegate* unmanaged<nint, nint, int>)Fn("terracotta_set_guesting"))(code, name) != 0;
        }
        finally
        {
            Marshal.FreeCoTaskMem(code);
            Marshal.FreeCoTaskMem(name);
        }
    });

    public Task<bool> PrepareVpnAsync() => Task.FromResult(true);

    /// <summary>游戏对局域网开放后告诉陶瓦端口（0 表示世界已关闭）。</summary>
    public void SetLanPort(int port)
    {
        if (Started)
            ((delegate* unmanaged<int, void>)Fn("terracotta_set_lan_port"))(port);
    }
}
