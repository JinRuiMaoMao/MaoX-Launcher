using System.Runtime.InteropServices;

namespace MaoX.iOS.Game;

/// <summary>
/// libmaoxgame.dylib（src/MaoX.iOS/Native，由 CI 编译进应用的 Frameworks）的接口。
/// 运行时用 RTLD_GLOBAL 加载：Java 里的 LWJGL 要通过 dlsym 找到其中的 pojav* 函数。
/// </summary>
internal static unsafe class GameNative
{
    public const int ActionDown = 0, ActionUp = 1, ActionMove = 2, ActionMoveMotion = 3;
    public const int ErrorLegacyScript = 2, ErrorMemory = 3;

    [Flags]
    public enum JitFlags { IsIos26 = 1, ForceMirrored = 2, HasTxm = 4 }

    private static nint _lib;

    public static string LibraryPath => Path.Combine(Foundation.NSBundle.MainBundle.BundlePath, "Frameworks", "libmaoxgame.dylib");

    public static bool Available => File.Exists(LibraryPath);

    private const int RtldNow = 0x2, RtldGlobal = 0x8;

    private static nint Fn(string name)
    {
        if (_lib == 0)
        {
            _lib = ObjCRuntime.Dlfcn.dlopen(LibraryPath, RtldNow | RtldGlobal);
            if (_lib == 0)
                throw new DllNotFoundException(ObjCRuntime.Dlfcn.dlerror() ?? LibraryPath);
        }
        var fn = ObjCRuntime.Dlfcn.dlsym(_lib, name);
        return fn != 0 ? fn : throw new EntryPointNotFoundException(name);
    }

    public static bool JitEnabled() => ((delegate* unmanaged<int>)Fn("maox_jit_enabled"))() != 0;

    public static JitFlags Flags() => (JitFlags)((delegate* unmanaged<int>)Fn("maox_jit_flags"))();

    // 内置 JIT 助手（Native/jit_helper.m），状态码见 IosBuiltInJit.Status
    public static int JitHelperAvailability() => ((delegate* unmanaged<int>)Fn("maox_jit_helper_availability"))();

    public static int JitHelperRequest(string json, long token, delegate* unmanaged<long, int, nint, void> callback)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(json);
        try
        {
            return ((delegate* unmanaged<nint, long, delegate* unmanaged<long, int, nint, void>, int>)
                Fn("maox_jit_helper_request"))(utf8, token, callback);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    public static void JitHelperCancel(long token) => ((delegate* unmanaged<long, void>)Fn("maox_jit_helper_cancel"))(token);

    public static int JitHelperPid() => ((delegate* unmanaged<int>)Fn("maox_jit_helper_pid"))();

    public static string JitHelperLastError()
    {
        var text = ((delegate* unmanaged<nint>)Fn("maox_jit_helper_last_error"))();
        try
        {
            return Marshal.PtrToStringUTF8(text) ?? "";
        }
        finally
        {
            NativeMemory.Free((void*)text);
        }
    }

    // 本机生成配对文件（Native/pairing.m），状态码和事件见 IosPairing
    public static int PairAvailability() => ((delegate* unmanaged<int>)Fn("maox_pair_availability"))();

    public static int PairStart(string json, long token, delegate* unmanaged<long, int, nint, void> callback)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(json);
        try
        {
            return ((delegate* unmanaged<nint, long, delegate* unmanaged<long, int, nint, void>, int>)
                Fn("maox_pair_start"))(utf8, token, callback);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    public static void PairCancel(long token) => ((delegate* unmanaged<long, void>)Fn("maox_pair_cancel"))(token);

    public static bool HasEntitlement(string name)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(name);
        try
        {
            return ((delegate* unmanaged<nint, int>)Fn("maox_has_entitlement"))(utf8) != 0;
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    public static int Launch(string json, nint layer)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(json);
        try
        {
            return ((delegate* unmanaged<nint, nint, int>)Fn("maox_launch"))(utf8, layer);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    public static string LastError() => Marshal.PtrToStringUTF8(((delegate* unmanaged<nint>)Fn("maox_last_error"))()) ?? "";

    public static void SetGrabCallback(delegate* unmanaged<int, void> callback) =>
        ((delegate* unmanaged<delegate* unmanaged<int, void>, void>)Fn("maox_set_grab_callback"))(callback);

    public static void SetExitCallback(delegate* unmanaged<int, void> callback) =>
        ((delegate* unmanaged<delegate* unmanaged<int, void>, void>)Fn("maox_set_exit_callback"))(callback);

    public static void Exit(int code) => ((delegate* unmanaged<int, void>)Fn("maox_exit"))(code);

    /// <summary>游戏结束后回到启动器：用同一条可执行文件覆盖当前进程，JVM 才能再启动一次。</summary>
    public static void Relaunch() => ((delegate* unmanaged<void>)Fn("maox_relaunch"))();

    public static void SetWindowSize(int width, int height) =>
        ((delegate* unmanaged<int, int, void>)Fn("maox_set_window_size"))(width, height);

    public static void SendScreenSize(int width, int height) =>
        ((delegate* unmanaged<int, int, void>)Fn("maox_send_screen_size"))(width, height);

    public static void SendCursor(int action, float x, float y) =>
        ((delegate* unmanaged<int, float, float, void>)Fn("maox_send_cursor"))(action, x, y);

    public static void SendKey(int key, bool down, int mods = 0) =>
        ((delegate* unmanaged<int, int, int, void>)Fn("maox_send_key"))(key, down ? 1 : 0, mods);

    public static void SendMouse(int button, bool down) =>
        ((delegate* unmanaged<int, int, void>)Fn("maox_send_mouse"))(button, down ? 1 : 0);

    public static void SendScroll(float dx, float dy) =>
        ((delegate* unmanaged<float, float, void>)Fn("maox_send_scroll"))(dx, dy);

    public static void SendChar(uint codepoint) => ((delegate* unmanaged<uint, void>)Fn("maox_send_char"))(codepoint);

    public static bool IsGrabbing() => ((delegate* unmanaged<int>)Fn("maox_is_grabbing"))() != 0;

    /// <summary>写一行到游戏日志（game.log）。</summary>
    public static void Log(string text)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(text);
        try
        {
            ((delegate* unmanaged<nint, void>)Fn("maox_log"))(utf8);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }
}
