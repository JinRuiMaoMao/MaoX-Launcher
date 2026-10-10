using System.Formats.Tar;
using System.Text.Json.Nodes;
using Android.App;
using Android.Content;
using Android.Net;
using MaoX.Core;
using SharpCompress.Compressors.Xz;
using static MaoX.Core.I18n;
using Activity = Android.App.Activity;
using Application = Android.App.Application;

namespace MaoX.Android;

/// <summary>
/// 在手机上启动游戏：准备移动版 Java、LWJGL 和渲染器，把启动参数写成 JSON，交给单独进程里的 GameActivity。
/// 移动版 Java 来自 AngelAuraMC/angelauramc-openjdk-build（GPL-2.0 with Classpath Exception），
/// 启动层、LWJGL 和渲染器来自 Amethyst-Android（LGPL-3.0）。
/// </summary>
internal static class GameHost
{
    public const string GameProcess = "io.github.jinruimaomao.maox:game";
    private const string JreUrl =
        "https://github.com/AngelAuraMC/angelauramc-openjdk-build/releases/download/download_jre{0}/jre{0}-android-{1}.tar.xz";
    private static readonly string[] GithubMirrors = ["https://ghfast.top/", "https://gh-proxy.com/"];

    private static Activity _activity;

    private static Context Ctx => Application.Context;
    private static string DataDir => Ctx.FilesDir!.AbsolutePath;
    private static string NativeLibDir => Ctx.ApplicationInfo!.NativeLibraryDir;
    private static string Abi => Platform.IsArm ? "arm64-v8a" : "x86_64";

    public static string LogFile => Path.Combine(DataDir, "game.log");
    private static string ExitFile => Path.Combine(DataDir, "game.exit");

    public static void Install(Activity activity)
    {
        _activity = activity;
        GameLauncher.MobileStarter = StartAsync;
    }

    private static async Task<RunningGame> StartAsync(GameLauncher launcher, PreparedGame info, string server,
                                                      LaunchAuth auth)
    {
        if (IsGameRunning())
            throw new InvalidOperationException(T("游戏已经在运行了"));
        await Task.Run(EnsureComponents);
        var required = info.VJson.Obj("javaVersion")?.Int("majorVersion", 8) ?? 8;
        var jre = await EnsureJreAsync(launcher, required);
        var jna = await EnsureJnaAsync(launcher);
        var config = await Task.Run(() => BuildConfig(launcher, info, server, auth, jre, jna));

        var configPath = Path.Combine(DataDir, "launch.json");
        File.WriteAllText(configPath, config.ToJsonString());
        File.Delete(ExitFile);
        File.WriteAllBytes(LogFile, []);

        var game = new AndroidGame();
        var intent = new Intent().SetClassName(Ctx, "io.github.jinruimaomao.maox.GameActivity")
                                 .PutExtra("config", configPath);
        _activity.RunOnUiThread(() => _activity.StartActivity(intent));
        game.Start();
        return game;
    }

    // ------------------------------------------------------------------ 运行库

    /// <summary>把 APK 里的 LWJGL 等组件解压到内部存储（外部存储不能加载 .so），应用更新后重新解压。</summary>
    private static void EnsureComponents()
    {
        var root = Path.Combine(DataDir, "components");
        var stamp = Path.Combine(root, ".stamp");
        var current = Ctx.PackageManager!.GetPackageInfo(Ctx.PackageName!, 0)!.LastUpdateTime.ToString();
        if (File.Exists(stamp) && File.ReadAllText(stamp) == current)
            return;
        if (Directory.Exists(root))
            Directory.Delete(root, true);
        CopyAssets(Ctx.Assets!, "runtime/components", root);
        File.WriteAllText(stamp, current);
    }

    private static void CopyAssets(global::Android.Content.Res.AssetManager assets, string from, string to)
    {
        var children = assets.List(from) ?? [];
        if (children.Length == 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            using var input = assets.Open(from);
            using var output = File.Create(to);
            input.CopyTo(output);
            return;
        }
        foreach (var child in children)
            CopyAssets(assets, from + "/" + child, Path.Combine(to, child));
    }

    private static int JreVersionFor(int required) => required <= 8 ? 8 : required <= 17 ? 17 : required <= 21 ? 21 : 25;

    private static async Task<string> EnsureJreAsync(GameLauncher launcher, int required)
    {
        var version = JreVersionFor(required);
        var home = Path.Combine(DataDir, "java", "jre" + version);
        var stamp = Path.Combine(home, ".maox-ok");
        if (File.Exists(stamp))
            return home;

        launcher.Log(F("正在下载手机版 Java {0}...", version));
        var url = string.Format(JreUrl, version, Platform.IsArm ? "arm64" : "x86_64");
        var mirrors = GithubMirrors.Select(m => m + url).ToList();
        var china = await launcher.Dl.ResolvedSourceAsync() == "bmclapi";
        var primary = china ? mirrors[0] : url;
        var alternates = china ? mirrors.Skip(1).Append(url) : mirrors;
        var archive = Path.Combine(Ctx.CacheDir!.AbsolutePath, $"jre{version}.tar.xz");
        await launcher.Dl.DownloadManyAsync([new DownloadTask(primary, archive, alternates: alternates)],
                                            (d, t) => launcher.Progress(d, t, T("下载 Java")));

        launcher.Log(F("正在解压手机版 Java {0}...", version));
        await Task.Run(() =>
        {
            if (Directory.Exists(home))
                Directory.Delete(home, true);
            Directory.CreateDirectory(home);
            using (var file = File.OpenRead(archive))
            using (var xz = new XZStream(file))
                TarFile.ExtractToDirectory(xz, home, true);
            File.Delete(archive);

            var lib = JvmLibDir(home);
            var freetype6 = Path.Combine(lib, "libfreetype.so.6");
            if (File.Exists(freetype6))
                File.Move(freetype6, Path.Combine(lib, "libfreetype.so"), true);
            File.Copy(Path.Combine(NativeLibDir, "libawt_xawt.so"), Path.Combine(lib, "libawt_xawt.so"), true);
            File.WriteAllText(stamp, version.ToString());
        });
        return home;
    }

    /// <summary>APK 里的 libjnidispatch.so 是 JNA 7.0.0 原生接口，只和 JNA 5.14.0 配套，游戏自带的 JNA 统一换成它。</summary>
    private static async Task<string> EnsureJnaAsync(GameLauncher launcher)
    {
        const string rel = "net/java/dev/jna/jna/5.14.0/jna-5.14.0.jar";
        var path = launcher.PathOf(["libraries", .. rel.Split('/')]);
        await launcher.Dl.DownloadManyAsync([
            new DownloadTask(Mc.FallbackMavens[^1] + rel, path, "67bf3eaea4f0718cb376a181a629e5f88fa1c9dd",
                             alternates: Mc.FallbackMavens[..^1].Select(b => b + rel)),
        ]);
        return path;
    }

    /// <summary>Java 8 的本地库在 lib/amd64 或 lib/aarch64 下，新版本直接在 lib 下。</summary>
    private static string JvmLibDir(string home)
    {
        foreach (var arch in new[] { "aarch64", "amd64", "x86_64" })
        {
            var dir = Path.Combine(home, "lib", arch);
            if (Directory.Exists(dir))
                return dir;
        }
        return Path.Combine(home, "lib");
    }

    // ------------------------------------------------------------------ 启动参数

    /// <summary>版本 JSON 里 LWJGL 的版本号，如 3.3.3 → 333、2.9.4 → 294。</summary>
    private static int LwjglVersion(JsonObject vjson)
    {
        foreach (var lib in vjson.Items("libraries"))
        {
            var name = lib.Str("name") ?? "";
            string rest = null;
            if (name.StartsWith("org.lwjgl:lwjgl:", StringComparison.Ordinal))
                rest = name["org.lwjgl:lwjgl:".Length..];
            else if (name.StartsWith("org.lwjgl.lwjgl:lwjgl:", StringComparison.Ordinal))
                rest = name["org.lwjgl.lwjgl:lwjgl:".Length..];
            if (rest == null)
                continue;
            var digits = new string(rest.TakeWhile(c => char.IsDigit(c) || c == '.').Where(char.IsDigit).ToArray());
            if (int.TryParse(digits, out var v) && v is >= 200 and <= 999)
                return v;
        }
        return 333;
    }

    private static JsonObject BuildConfig(GameLauncher launcher, PreparedGame info, string server, LaunchAuth auth,
                                          string jreHome, string jna)
    {
        var cache = Ctx.CacheDir!.AbsolutePath;
        var home = AppPaths.BaseDir;
        var lwjgl = LwjglVersion(info.VJson);
        var lwjglVer = lwjgl >= 341 ? "3.4.1" : "3.3.3";
        var components = Path.Combine(DataDir, "components");
        var lwjglJars = Path.Combine(components, "lwjgl3", lwjglVer);
        var lwjglNatives = Path.Combine(components, $"lwjgl-{lwjglVer}-natives", Abi);
        var jvmLib = JvmLibDir(jreHome);
        var jvmServer = Path.Combine(jvmLib, Directory.Exists(Path.Combine(jvmLib, "server")) ? "server" : "client");

        // LWJGL 换成移动版：核心和合并包放最前，其余模块随后；版本自带的 org.lwjgl 库全部去掉
        var classpath = new List<string>
        {
            Path.Combine(lwjglJars, "lwjgl.jar"),
            Path.Combine(lwjglJars, $"lwjgl-{lwjglVer}-merged-modules.jar"),
        };
        classpath.AddRange(Directory.GetFiles(lwjglJars, "*.jar").Order()
                                    .Where(f => !classpath.Contains(f) && !f.EndsWith("lwjglx.jar")));
        var sep = Path.DirectorySeparatorChar;
        var jnaDir = $"{sep}libraries{sep}net{sep}java{sep}dev{sep}jna{sep}jna{sep}";
        foreach (var path in info.Classpath.Where(p => !p.Contains($"{sep}libraries{sep}org{sep}lwjgl{sep}")))
        {
            var entry = path.Contains(jnaDir) ? jna : path;
            if (!classpath.Contains(entry))
                classpath.Add(entry);
        }
        if (lwjgl <= 299)
            classpath.Add(Path.Combine(lwjglJars, "lwjgl-lwjglx.jar"));
        var mobileInfo = new PreparedGame
        {
            VJson = info.VJson,
            Classpath = classpath,
            NativesDir = lwjglNatives,
            GameDir = info.GameDir,
            GameAssets = info.GameAssets,
            LoggingArg = info.LoggingArg,
        };

        var memory = Math.Max(512, Math.Min(launcher.Cfg.MaxMemory, (int)(DeviceMemoryMb() / 2)));
        if (memory != launcher.Cfg.MaxMemory)
            launcher.Log(F("手机内存有限，游戏最大内存调整为 {0} MB", memory));
        var libraryPath = $"{lwjglNatives}:{NativeLibDir}";
        string[] dropped =
        [
            "-Xmx", "-Xms", "-XX:ActiveProcessorCount", "-Dorg.lwjgl.opengl.libname", "-Dorg.lwjgl.freetype.libname",
            "-Djava.library.path=", "-XX:HeapDumpPath",
        ];
        var resolv = Path.Combine(DataDir, "resolv.conf");
        File.WriteAllText(resolv, ResolvConf());

        var args = new List<string>
        {
            $"-Xms{memory}m",
            $"-Xmx{memory}m",
            "-Djava.home=" + jreHome,
            "-Djava.io.tmpdir=" + cache,
            "-Djna.boot.library.path=" + NativeLibDir,
            "-Duser.home=" + home,
            "-Duser.language=" + Java.Util.Locale.Default.Language,
            "-Duser.timezone=" + Java.Util.TimeZone.Default.ID,
            "-Dos.name=Linux",
            "-Dos.version=Android-" + global::Android.OS.Build.VERSION.Release,
            "-Dorg.lwjgl.vulkan.libname=libvulkan.so",
            "-Dglfwstub.initEgl=false",
            "-Dext.net.resolvPath=" + resolv,
            "-Dnet.minecraft.clientmodname=MaoX",
            "-Dfml.earlyprogresswindow=false",
            "-Dloader.disable_forked_guis=true",
            // 手机上的 LWJGL 是 3.3.3，Sodium 会因为“版本不是 3.3.1”拒绝启动
            "-Dsodium.checks.issue2561=false",
            "-Djdk.lang.Process.launchMechanism=FORK",
            "-Djava.awt.headless=true",
            "-Dorg.lwjgl.opengl.libname=libSimpleFPEWrapper.so",
            $"-Dorg.lwjgl.freetype.libname={lwjglNatives}/libfreetype.so",
            "-Dorg.lwjgl.spvc.libname=spirv-cross-c-shared",
            "-Dorg.lwjgl.system.allocator=system",
            "-XX:ActiveProcessorCount=" + Environment.ProcessorCount,
            "-Djava.library.path=" + libraryPath,
            "-Dorg.lwjgl.librarypath=" + lwjglNatives,
            $"-DZstdNativePath={NativeLibDir}/libzstd-jni-1.5.7-6-dhcompat.so",
            "-Dimgui.library.name=imgui-java",
        };
        var command = launcher.BuildCommand(info.VJson, "java", mobileInfo, server, auth);
        args.AddRange(command.Skip(1).Where(a => !dropped.Any(a.StartsWith)));

        var ldPath = string.Join(":", Path.Combine(jvmLib, "jli"), jvmLib, "/system/lib64", "/vendor/lib64",
                                 "/vendor/lib64/hw", NativeLibDir, lwjglNatives);
        var env = new JsonObject
        {
            ["POJAV_NATIVEDIR"] = NativeLibDir,
            ["JAVA_HOME"] = jreHome,
            ["HOME"] = home,
            ["TMPDIR"] = cache,
            ["LIBGL_MIPMAP"] = "3",
            ["LIBGL_NOERROR"] = "1",
            ["LIBGL_NOINTOVLHACK"] = "1",
            ["LIBGL_NORMALIZE"] = "1",
            ["LIBGL_ES"] = "3",
            ["FORCE_VSYNC"] = "false",
            ["MESA_GLSL_CACHE_DIR"] = cache,
            ["force_glsl_extensions_warn"] = "true",
            ["allow_higher_compat_version"] = "true",
            ["allow_glsl_extension_directive_midshader"] = "true",
            ["LD_LIBRARY_PATH"] = ldPath,
            ["PATH"] = Path.Combine(jreHome, "bin") + ":" + Environment.GetEnvironmentVariable("PATH"),
            ["AMETHYST_RENDERER"] = "opengles_mobileglues",
            ["MG_DIR_PATH"] = Path.Combine(DataDir, "MobileGlues"),
            ["POJAVEXEC_EGL"] = "libmobileglues.so",
            ["SFPEW_EGL"] = "libmobileglues.so",
        };
        Directory.CreateDirectory(Path.Combine(DataDir, "MobileGlues"));

        // 和 Amethyst 一样：先按顺序加载 Java 的核心库，再加载 JRE 里其余的 .so、OpenAL 和渲染器
        string Find(string name) =>
            ldPath.Split(':').Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists) ?? name;
        var preload = new List<string>
        {
            Find("libjli.so"), Path.Combine(jvmServer, "libjvm.so"), Find("libverify.so"), Find("libjava.so"),
            Find("libnet.so"), Find("libnio.so"), Find("libawt.so"), Find("libawt_headless.so"),
            Find("libfreetype.so"), Find("libfontmanager.so"),
        };
        preload.AddRange(Directory.GetFiles(jvmLib, "*.so", SearchOption.AllDirectories));
        preload.Add(Path.Combine(NativeLibDir, "libopenal.so"));
        preload.Add(Path.Combine(NativeLibDir, "libmobileglues.so"));

        return new JsonObject
        {
            ["env"] = env,
            ["ldLibraryPath"] = jvmServer + ":" + ldPath,
            ["preload"] = new JsonArray(preload.Select(p => (JsonNode)p).ToArray()),
            ["args"] = new JsonArray(args.Select(a => (JsonNode)a).ToArray()),
            ["gameDir"] = info.GameDir,
            ["logFile"] = LogFile,
            ["exitFile"] = ExitFile,
            ["inputStackQueue"] = info.VJson.Get("arguments") != null,
            ["scale"] = 1.0,
            ["labels"] = new JsonObject
            {
                ["jump"] = T("跳跃"), ["sneak"] = T("潜行"), ["inventory"] = T("背包"), ["drop"] = T("丢弃"),
                ["chat"] = T("聊天"), ["view"] = T("视角"), ["pause"] = T("暂停"), ["keyboard"] = T("键盘"),
            },
        };
    }

    private static long DeviceMemoryMb()
    {
        var am = (ActivityManager)Ctx.GetSystemService(Context.ActivityService)!;
        var mem = new ActivityManager.MemoryInfo();
        am.GetMemoryInfo(mem);
        return mem.TotalMem / 1048576;
    }

    /// <summary>安卓没有 /etc/resolv.conf，移动版 Java 从这个文件读 DNS 服务器。</summary>
    private static string ResolvConf()
    {
        var servers = new List<string>();
        try
        {
            var cm = (ConnectivityManager)Ctx.GetSystemService(Context.ConnectivityService)!;
            var props = cm.GetLinkProperties(cm.ActiveNetwork);
            if (props != null)
                servers.AddRange(props.DnsServers.Select(a => a.HostAddress).Where(a => !string.IsNullOrEmpty(a)));
        }
        catch (Exception)
        {
            // 拿不到系统 DNS 时用公共 DNS
        }
        servers.AddRange(["223.5.5.5", "8.8.8.8"]);
        return string.Concat(servers.Distinct().Select(s => $"nameserver {s}\n"));
    }

    // ------------------------------------------------------------------ 游戏进程

    private static ActivityManager.RunningAppProcessInfo FindGameProcess()
    {
        var am = (ActivityManager)Ctx.GetSystemService(Context.ActivityService)!;
        return am.RunningAppProcesses?.FirstOrDefault(p => p.ProcessName == GameProcess);
    }

    private static bool IsGameRunning() => FindGameProcess() != null;

    /// <summary>:game 进程里的游戏。输出由原生层写进日志文件，这里边写边读；进程消失即视为退出。</summary>
    private sealed class AndroidGame : RunningGame
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool HasExited => _exit.Task.IsCompleted;

        public void Start() => _ = Task.Run(MonitorAsync);

        private async Task MonitorAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (FindGameProcess() == null && DateTime.UtcNow < deadline)
                await Task.Delay(300);
            while (FindGameProcess() != null)
                await Task.Delay(500);
            var code = 0;
            try
            {
                if (File.Exists(ExitFile))
                    code = JsonNode.Parse(File.ReadAllText(ExitFile))?.Int("code", 1) ?? 1;
            }
            catch (Exception)
            {
                code = 1;
            }
            _exit.TrySetResult(code);
        }

        public override void Kill()
        {
            if (FindGameProcess() is { } p)
                global::Android.OS.Process.KillProcess(p.Pid);
        }

        public override Task ReadOutputAsync(Action<string> onLine) => Task.Run(() =>
        {
            using var file = new FileStream(LogFile, FileMode.OpenOrCreate, FileAccess.Read,
                                            FileShare.ReadWrite | FileShare.Delete);
            GameOutput.ReadLines(new TailStream(file, _exit.Task), onLine);
        });

        public override Task<int> WaitForExitAsync() => _exit.Task;
    }

    /// <summary>读到文件末尾时等待新内容，游戏退出后读完剩余内容再结束。</summary>
    private sealed class TailStream(Stream inner, Task exited) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            while (true)
            {
                var done = exited.IsCompleted;
                var read = inner.Read(buffer, offset, count);
                if (read > 0 || done)
                    return read;
                Thread.Sleep(200);
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
