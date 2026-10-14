using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Foundation;
using MaoX.Core;
using UIKit;
using static MaoX.Core.I18n;

namespace MaoX.iOS.Game;

/// <summary>
/// 在 iPhone / iPad 上启动游戏：Java 和游戏直接运行在启动器进程里（iOS 不允许启动子进程），
/// 移动版 Java、LWJGL、渲染器和启动层来自 Amethyst-iOS（GPL-3.0），由 CI 打包进应用。
/// Java 需要 JIT：用 StikDebug（或 SideStore）给启动器开启。
/// </summary>
internal static class GameHost
{
    private const string BootClass = "io.github.jinruimaomao.maox.IOSBoot";
    private const string RendererAngle = "libtinygl4angle.dylib";
    private static readonly NSObject MainInvoker = new();
    private static IosGame _game;

    private static string Bundle => NSBundle.MainBundle.BundlePath;
    public static string LogFile => Path.Combine(AppPaths.BaseDir, "game.log");
    internal static string JitScript => Path.Combine(Bundle, "UniversalJIT26.js");

    public static void Install()
    {
        GameLauncher.MobileStarter = StartAsync;
        GameLauncher.MobilePreparing = RequestJitEarly;
        IosBuiltInJit.Install();
        Terracotta.Mobile = IosTerracotta.Instance;
        GameLauncher.MobileJavaRunner = (_, _, _, _) => throw new PlatformNotSupportedException(
            T("iPhone / iPad 上不能运行 Forge / NeoForge / OptiFine 的安装程序：请在电脑上安装好，再把版本文件夹复制到“文件”App 里 MaoX Launcher 的 .minecraft/versions 中"));
        // StikDebug 里给启动器手动指定 JIT 脚本时，可以在“文件”App 的 MaoX Launcher 文件夹里选到它
        try
        {
            var copy = Path.Combine(AppPaths.BaseDir, "UniversalJIT26.js");
            if (File.Exists(JitScript) && !File.Exists(copy))
                File.Copy(JitScript, copy);
        }
        catch (Exception)
        {
            // 文稿目录不可写时忽略
        }
    }

    private static Task<T> OnMain<T>(Func<T> action)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        MainInvoker.BeginInvokeOnMainThread(() =>
        {
            try
            {
                tcs.SetResult(action());
            }
            catch (Exception e)
            {
                tcs.SetException(e);
            }
        });
        return tcs.Task;
    }

    private static async Task<RunningGame> StartAsync(GameLauncher launcher, PreparedGame info, string server,
                                                      LaunchAuth auth)
    {
        if (_game != null)
            throw new InvalidOperationException(T("游戏已经在运行了"));
        if (!GameNative.Available)
            throw new PlatformNotSupportedException(
                T("这个安装包没有带游戏运行环境，请到 GitHub Releases 下载完整的 MaoX-Launcher-iOS.ipa"));

        var required = info.VJson.Obj("javaVersion")?.Int("majorVersion", 8) ?? 8;
        var javaVersion = required <= 8 ? 8 : required <= 17 ? 17 : required <= 21 ? 21 : 25;
        var javaHome = Path.Combine(Bundle, "java_runtimes", $"java-{javaVersion}-openjdk");
        if (!Directory.Exists(javaHome))
            throw new InvalidOperationException(F("安装包里缺少 Java {0}", javaVersion));

        if (_jitRequest is { } early)
        {
            try
            {
                await early;
            }
            catch (InvalidOperationException)
            {
                // 下载时的请求超时或没打开 StikDebug，下面再请求一次
            }
        }
        await EnsureJitAsync(launcher);

        var screen = await OnMain(GameViewController.InitialSize);
        var config = await Task.Run(() => BuildConfig(launcher, info, server, auth, javaHome, javaVersion, screen));
        File.WriteAllBytes(LogFile, []);

        var game = new IosGame();
        var json = config.ToJsonString();
        var code = await OnMain(() => new GameViewController(info.GameDir, screen, launcher.Cfg.CurrentControlLayout()).LaunchAsync(json)).Unwrap();
        if (code != 0)
            throw new InvalidOperationException(code switch
            {
                GameNative.ErrorLegacyScript => T("StikDebug 用的是旧版 JIT 脚本：请在 StikDebug 里长按 MaoX Launcher →“指定脚本”，选“文件”App 中 MaoX Launcher 文件夹里的 UniversalJIT26.js，然后重新打开启动器"),
                GameNative.ErrorMemory => GameNative.HasEntitlement("com.apple.developer.kernel.increased-memory-limit")
                    ? T("连续内存不够：请在版本设置里调低最大内存后再试")
                    : T("连续内存不够：请调低最大内存，或用 SideStore 安装时开启“增加内存限制”（increased-memory-limit）权限"),
                _ => F("游戏启动失败：{0}", GameNative.LastError()),
            });
        _game = game;
        return game;
    }

    // ------------------------------------------------------------------ JIT

    private static readonly object JitLock = new();
    private static Task _jitRequest;

    /// <summary>下载游戏文件的同时请求 JIT，下载完就能直接启动。</summary>
    private static void RequestJitEarly(GameLauncher launcher)
    {
        if (!GameNative.Available || GameNative.JitEnabled())
            return;
        Task request;
        lock (JitLock)
        {
            if (_jitRequest is { IsCompleted: false })
                return;
            _jitRequest = request = EnsureJitAsync(launcher);
        }
        request.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>
    /// 没有 JIT 时请 StikDebug（iOS 17.4+）或 SideStore 给当前进程开启，最多等两分钟。
    /// 用户在设置里打开了内置 JIT（实验）且可用时，改由安装包里的 JIT 助手开启，失败时不会再改用 StikDebug。
    /// </summary>
    private static async Task EnsureJitAsync(GameLauncher launcher)
    {
        if (GameNative.JitEnabled())
            return;
        if (IosBuiltInJit.Selected(launcher.Cfg, out var unavailable))
        {
            await IosBuiltInJit.EnableAsync(launcher);
            launcher.Log(T("JIT 已开启"));
            return;
        }
        if (unavailable != null)
            launcher.Log(F("内置 JIT 不可用（{0}），使用原来的方式开启 JIT", unavailable));
        launcher.Log(T("正在请求开启 JIT，请在弹出的 StikDebug 中确认..."));
        // 切到 StikDebug 后 iOS 很快会挂起启动器，申请一段后台运行时间，让同时进行的下载不中断
        var background = new BackgroundTime();
        await OnMain(background.Begin);
        try
        {
            var opened = await OnMain(() =>
            {
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                UIApplication.SharedApplication.OpenUrl(new NSUrl(JitRequestUrl()), new UIApplicationOpenUrlOptions(),
                                                        ok => tcs.TrySetResult(ok));
                return tcs.Task;
            }).Unwrap();
            if (!opened)
                throw new InvalidOperationException(
                    T("Java 需要 JIT 才能运行：请先安装 StikDebug（或用 SideStore 开启 JIT），再回来点启动"));

            var deadline = DateTime.UtcNow.AddMinutes(2);
            while (!GameNative.JitEnabled())
            {
                if (DateTime.UtcNow > deadline)
                    throw new InvalidOperationException(
                        T("等待 JIT 超时：请在 StikDebug 里给 MaoX Launcher 开启 JIT 后再点启动"));
                await Task.Delay(300);
            }
        }
        finally
        {
            await OnMain(background.End);
        }
        launcher.Log(T("JIT 已开启"));
    }

    private sealed class BackgroundTime
    {
        private nint _id = UIApplication.BackgroundTaskInvalid;

        public bool Begin()
        {
            _id = UIApplication.SharedApplication.BeginBackgroundTask("MaoX JIT", () => End());
            return true;
        }

        public bool End()
        {
            if (_id != UIApplication.BackgroundTaskInvalid)
            {
                UIApplication.SharedApplication.EndBackgroundTask(_id);
                _id = UIApplication.BackgroundTaskInvalid;
            }
            return true;
        }
    }

    private static string JitRequestUrl()
    {
        var bundleId = NSBundle.MainBundle.BundleIdentifier;
        var pid = Environment.ProcessId;
        if (GameNative.HasEntitlement("jb.pmap_cs.custom_trust"))
            return $"apple-magnifier://enable-jit?bundle-id={bundleId}";
        if (!OperatingSystem.IsIOSVersionAtLeast(17, 4))
            return $"sidestore://sidejit-enable?pid={pid}";
        var script = "";
        if ((GameNative.Flags() & (GameNative.JitFlags.ForceMirrored | GameNative.JitFlags.HasTxm)) != 0
            && File.Exists(JitScript))
            script = "&script-data=" + Convert.ToBase64String(File.ReadAllBytes(JitScript));
        return $"stikjit://enable-jit?bundle-id={bundleId}&pid={pid}{script}";
    }

    // ------------------------------------------------------------------ 启动参数

    private static JsonObject BuildConfig(GameLauncher launcher, PreparedGame info, string server, LaunchAuth auth,
                                          string javaHome, int javaVersion, GameViewController.ScreenInfo screen)
    {
        var bundle = Bundle;
        var frameworks = Path.Combine(bundle, "Frameworks");
        var libs = Path.Combine(bundle, "libs");
        var tmp = Path.GetTempPath();
        var natives = Path.Combine(tmp, "natives");
        Directory.CreateDirectory(natives);

        // iOS 版 LWJGL 和启动层放最前；版本自带的 org.lwjgl 库全部去掉
        var classpath = new List<string> { Path.Combine(libs, "maox-ios.jar"), Path.Combine(libs, "lwjgl.jar") };
        var sep = Path.DirectorySeparatorChar;
        foreach (var path in info.Classpath.Where(p => !p.Contains($"{sep}libraries{sep}org{sep}lwjgl{sep}")))
        {
            if (!classpath.Contains(path))
                classpath.Add(path);
        }
        var mobileInfo = new PreparedGame
        {
            VJson = info.VJson,
            Classpath = classpath,
            NativesDir = natives,
            GameDir = info.GameDir,
            GameAssets = info.GameAssets,
            LoggingArg = info.LoggingArg,
        };

        // 和 Amethyst 一样默认用物理内存的 1/4，iOS 上 Java 占太多会被系统杀掉
        var physical = (long)(NSProcessInfo.ProcessInfo.PhysicalMemory >> 20);
        var limit = (int)(physical / 4);
        var memory = Math.Max(512, Math.Min(launcher.Cfg.MaxMemory, limit));
        if (memory != launcher.Cfg.MaxMemory)
            launcher.Log(F("手机内存有限，游戏最大内存调整为 {0} MB", memory));

        var java8 = javaVersion == 8;
        var args = new List<string>
        {
            Path.Combine(javaHome, "bin", "java"),
            "-XstartOnFirstThread",
            "-Xms128M",
            $"-Xmx{memory}M",
            "-Djava.library.path=" + frameworks,
            "-Djava.io.tmpdir=" + tmp,
            "-Duser.dir=" + info.GameDir,
            "-Duser.home=" + AppPaths.BaseDir,
            "-Duser.timezone=" + NSTimeZone.LocalTimeZone.Name,
            "-DUIScreen.maximumFramesPerSecond=" + screen.MaxFps,
            "-Dorg.lwjgl.glfw.checkThread0=false",
            "-Dorg.lwjgl.system.allocator=system",
            "-Dlog4j2.formatMsgNoLookups=true",
            "-Dorg.lwjgl.opengl.libname=" + RendererAngle,
            $"-javaagent:{Path.Combine(libs, "patchjna_agent.jar")}=",
            // 避免随机的栈保护页分配崩溃
            "-XX:+UnlockExperimentalVMOptions",
            "-XX:+DisablePrimordialThreadGuardPages",
            "-Dfml.earlyprogresswindow=false",
            "-Dloader.disable_forked_guis=true",
            "-Dsodium.checks.issue2561=false",
            "-Dnet.minecraft.clientmodname=MaoX",
            // 免费开发者账号没有扩展虚拟地址权限，压缩类指针空间分配会失败
            "-XX:-UseCompressedClassPointers",
            // Caciocavallo：在没有窗口系统的 iOS 上提供 AWT
            "-Djava.awt.headless=false",
            "-Dcacio.font.fontmanager=sun.awt.X11FontManager",
            "-Dcacio.font.fontscaler=sun.font.FreetypeFontScaler",
            $"-Dcacio.managed.screensize={screen.Width}x{screen.Height}",
            "-Dswing.defaultlaf=javax.swing.plaf.metal.MetalLookAndFeel",
        };
        if (OperatingSystem.IsIOSVersionAtLeast(26))
            args.Add("-XX:+MirrorMappedCodeCache");
        if (java8)
        {
            args.Add("-Dawt.toolkit=net.java.openjdk.cacio.ctc.CTCToolkit");
            args.Add("-Djava.awt.graphicsenv=net.java.openjdk.cacio.ctc.CTCGraphicsEnvironment");
        }
        else
        {
            args.Add("-Dawt.toolkit=com.github.caciocavallosilano.cacio.ctc.CTCToolkit");
            args.Add("-Djava.awt.graphicsenv=com.github.caciocavallosilano.cacio.ctc.CTCGraphicsEnvironment");
            foreach (var pkg in new[] { "java.awt", "java.awt.peer", "sun.awt.image", "sun.java2d", "java.awt.dnd.peer",
                                        "sun.awt", "sun.awt.event", "sun.awt.datatransfer", "sun.font" })
                args.Add($"--add-exports=java.desktop/{pkg}=ALL-UNNAMED");
            args.Add("--add-exports=java.base/sun.security.action=ALL-UNNAMED");
            foreach (var pkg in new[] { "java.base/java.util", "java.desktop/java.awt", "java.desktop/sun.font",
                                        "java.desktop/sun.java2d", "java.base/java.lang.reflect", "java.base/java.net" })
                args.Add($"--add-opens={pkg}=ALL-UNNAMED");
            // 启动层通过反射调用 Forge 的 BootstrapLauncher
            args.Add("--add-exports=cpw.mods.bootstraplauncher/cpw.mods.bootstraplauncher=ALL-UNNAMED");
        }
        var cacioDir = Path.Combine(bundle, java8 ? "libs_caciocavallo" : "libs_caciocavallo17");
        if (Directory.Exists(cacioDir))
        {
            var jars = Directory.GetFiles(cacioDir, "*.jar").Order();
            args.Add($"-Xbootclasspath/{(java8 ? "p" : "a")}:{string.Join(":", jars)}");
        }

        string[] dropped =
        [
            "-Xmx", "-Xms", "-XX:+UseG1GC", "-XX:HeapDumpPath", "-Xdock", "-XstartOnFirstThread",
            "-Djava.library.path=", "-Dorg.lwjgl.opengl.libname",
        ];
        // 游戏按 --width / --height 换算触摸坐标，必须和实际画面一样大（不能用电脑版的 854×480）
        var command = launcher.BuildCommand(info.VJson, "java", mobileInfo, server, auth, screen.Width, screen.Height)
                              .Skip(1).ToList();
        var mainClass = info.VJson.Str("mainClass");
        var mainIndex = command.IndexOf(mainClass);
        if (mainIndex < 0)
            throw new InvalidOperationException(F("版本文件里缺少主类：{0}", mainClass));
        args.AddRange(command.Take(mainIndex).Where(a => !dropped.Any(a.StartsWith)));
        args.Add(BootClass);
        args.AddRange(command.Skip(mainIndex));

        var resolv = Path.Combine(tmp, "resolv.conf");
        File.WriteAllText(resolv, "nameserver 223.5.5.5\nnameserver 8.8.8.8\n");

        return new JsonObject
        {
            ["env"] = new JsonObject
            {
                ["JAVA_HOME"] = javaHome,
                ["LD_LIBRARY_PATH"] = "",
                ["HACK_IGNORE_START_ON_FIRST_THREAD"] = "1",
                ["LIBGL_NOINTOVLHACK"] = "1",
                ["LIBGL_NORMALIZE"] = "1",
                ["MESA_GL_VERSION_OVERRIDE"] = "4.1",
                // 按游戏请求的 OpenGL 版本自动选择：旧版本用 gl4es，1.17+ 用 ANGLE（Metal）
                ["RENDERER"] = "auto",
                ["MAOX_NATIVE_LIB"] = GameNative.LibraryPath,
            },
            ["args"] = new JsonArray(args.Select(a => (JsonNode)a).ToArray()),
            ["javaHome"] = javaHome,
            ["gameDir"] = info.GameDir,
            ["logFile"] = LogFile,
            ["resolvConf"] = resolv,
            ["maxMemory"] = memory,
            ["jitAlwaysAttached"] = false,
        };
    }

    // ------------------------------------------------------------------ 游戏

    /// <summary>游戏运行在启动器进程里，输出由原生层写进日志文件；游戏退出时整个应用跟着退出。</summary>
    private sealed class IosGame : RunningGame
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool HasExited => _exit.Task.IsCompleted;

        public override void Kill() => MainInvoker.BeginInvokeOnMainThread(() => GameNative.Exit(1));

        public override Task ReadOutputAsync(Action<string> onLine) => Task.Run(() =>
        {
            using var file = new FileStream(LogFile, FileMode.OpenOrCreate, FileAccess.Read,
                                            FileShare.ReadWrite | FileShare.Delete);
            GameOutput.ReadLines(new TailStream(file, _exit.Task), line =>
            {
                WatchLanWorld(line);
                onLine(line);
            });
        });

        /// <summary>陶瓦联机在 iOS 上收不到游戏的局域网广播，改为从日志里读出“对局域网开放”的端口。</summary>
        private static void WatchLanWorld(string line)
        {
            if (LanStarted.Match(line) is { Success: true } match && int.TryParse(match.Groups[1].Value, out var port))
                IosTerracotta.Instance.SetLanPort(port);
            else if (line.Contains("Stopping server", StringComparison.Ordinal))
                IosTerracotta.Instance.SetLanPort(0);
        }

        private static readonly Regex LanStarted = new(@"Started serving on (\d+)", RegexOptions.Compiled);

        public override Task<int> WaitForExitAsync() => _exit.Task;
    }
}
