using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.VisualTree;
using Avalonia.Media.Imaging;
using MaoX.Core;

namespace MaoX;

/// <summary>
/// 给 GitHub Actions 用的自检：MaoXLauncher --smoke-test &lt;输出目录&gt; [--launch &lt;版本&gt;]
/// 依次打开每个页面并截图，检查平台识别、Java、版本列表、陶瓦联机；
/// 带 --launch 时还会真实安装并启动该版本，看到游戏开始渲染后结束进程。结果写到 report.txt。
/// </summary>
internal static class SmokeTest
{
    public static bool Active { get; private set; }

    public static bool TryAttach(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window)
    {
        var args = desktop.Args ?? [];
        var index = Array.IndexOf(args, "--smoke-test");
        if (index < 0)
            return false;
        Active = true;
        var output = Path.GetFullPath(index + 1 < args.Length ? args[index + 1] : "smoke-test");
        var launch = Array.IndexOf(args, "--launch");
        var version = launch >= 0 && launch + 1 < args.Length ? args[launch + 1] : null;
        window.Opened += async (_, _) =>
        {
            var failed = await Run(window, output, version);
            desktop.Shutdown(failed ? 1 : 0);
        };
        return true;
    }

    /// <summary>整个自检的时间上限：超时后记下卡在哪一步并退出，避免 CI 一直挂着却拿不到任何报告。</summary>
    private static readonly TimeSpan Watchdog = TimeSpan.FromMinutes(15);

    private static Process _game;

    private static async Task<bool> Run(MainWindow window, string output, string version)
    {
        Directory.CreateDirectory(output);
        var reportPath = Path.Combine(output, "report.txt");
        File.WriteAllText(reportPath, "");
        var failed = false;
        var step = "startup";
        var started = Stopwatch.StartNew();

        void Line(string status, string name, string detail)
        {
            var text = $"[{status}] {name}: {detail} ({started.Elapsed.TotalSeconds:F0}s)";
            lock (reportPath)
                File.AppendAllText(reportPath, text + Environment.NewLine);
            Console.WriteLine(text);
            if (status == "FAIL")
                failed = true;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(Watchdog);
            Line("FAIL", "watchdog", $"smoke test did not finish within {Watchdog.TotalMinutes} minutes, stuck at: {step}");
            lock (reportPath)
                File.AppendAllText(reportPath, "RESULT: FAIL" + Environment.NewLine);
            try
            {
                _game?.Kill(true);
            }
            catch (Exception)
            {
            }
            Environment.Exit(1);
        });

        async Task Check(string name, Func<Task<string>> check, bool required = true, TimeSpan? timeout = null)
        {
            step = name;
            Console.WriteLine($"[....] {name}");
            try
            {
                var task = check();
                if (timeout is { } limit && await Task.WhenAny(task, Task.Delay(limit)) != task)
                    throw new TimeoutException($"timed out after {limit.TotalMinutes} minutes");
                Line("PASS", name, await task);
            }
            catch (Exception e)
            {
                Line(required ? "FAIL" : "WARN", name, MainWindow.ErrorText(e));
            }
        }

        Line("INFO", "launcher", $"v{Mc.LauncherVersion} {Platform.OsName} {Platform.Arch} (OS {Platform.OsVersion})");
        Line("INFO", "paths", $"base={AppPaths.BaseDir} exe={Environment.ProcessPath} bundle={Updater.AppBundle}");
        Line("INFO", "update", $"asset={Updater.AssetName} canSelfUpdate={Updater.CanSelfUpdate}");

        await Check("java detection", () => Task.Run(() =>
        {
            var javas = JavaManager.FindJava();
            return javas.Count == 0
                ? "no java found (will be downloaded on launch)"
                : string.Join(", ", javas.Select(j => $"Java {j.Major} @ {j.Path}"));
        }), required: false);

        foreach (var page in new[] { "launch", "download", "resources", "multiplayer", "settings" })
        {
            await Check("page " + page, async () =>
            {
                window.ShowPage(page);
                await Task.Delay(page == "resources" ? 5000 : 1500);
                var path = Path.Combine(output, $"page-{page}.png");
                Screenshot(window, path);
                if (page == "settings" && window.SettingsPage.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } scroll)
                {
                    scroll.ScrollToEnd();
                    await Task.Delay(500);
                    Screenshot(window, Path.Combine(output, "page-settings-bottom.png"));
                }
                return Path.GetFileName(path);
            });
        }

        await Check("server list", async () =>
        {
            // 只加在内存里，不写入配置
            if (window.Cfg.Servers.Count == 0)
                window.Cfg.Servers.AddRange([
                    new ServerEntry { Name = "Hypixel", Address = "mc.hypixel.net" },
                    new ServerEntry { Name = "2b2t（SRV 记录）", Address = "2b2t.org" },
                    new ServerEntry { Name = "不存在的服务器", Address = "nonexistent.invalid" },
                ]);
            window.ShowPage("multiplayer");
            window.MultiplayerPage.ShowServers();
            string summary = null;
            for (var i = 0; i < 40 && (summary = window.MultiplayerPage.ServerSummary()) == null; i++)
                await Task.Delay(500);
            await Task.Delay(500);
            Screenshot(window, Path.Combine(output, "page-servers.png"));
            if (summary == null || !summary.Contains("ms"))
                throw new Exception("no server answered: " + summary);
            return summary;
        }, required: false);

        await Check("version manifest", async () =>
        {
            for (var i = 0; i < 60 && window.Manifest == null; i++)
                await Task.Delay(500);
            var count = window.Manifest?["versions"]?.AsArray().Count ?? 0;
            return count > 0 ? $"{count} versions" : throw new InvalidOperationException("版本列表没有加载");
        });

        await Check("terracotta", async () =>
        {
            var tc = new Terracotta(null, window.MakeLauncher().Dl, _ => { })
            {
                NonInteractiveInstall = Environment.GetEnvironmentVariable("CI") == "true",
            };
            if (!tc.Supported)
                return "not supported on this platform";
            if (!await tc.InstalledAsync())
            {
                if (Platform.IsMac && !tc.NonInteractiveInstall)
                    return "not installed (installing needs the admin password, skipped)";
                await tc.InstallAsync((_, _) => { });
            }
            try
            {
                await tc.StartAsync();
                var state = await tc.StateAsync();
                return $"started, state={state.Str("state")}";
            }
            finally
            {
                await tc.ShutdownAsync();
            }
        }, required: false, timeout: TimeSpan.FromMinutes(3));

        if (version != null)
            await Check("launch " + version, () => Task.Run(() => LaunchGame(window, version, output)));

        lock (reportPath)
            File.AppendAllText(reportPath, (failed ? "RESULT: FAIL" : "RESULT: PASS") + Environment.NewLine);
        return failed;
    }

    private static void Screenshot(MainWindow window, string path)
    {
        var scale = window.RenderScaling;
        var size = new PixelSize((int)(window.Bounds.Width * scale), (int)(window.Bounds.Height * scale));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
        bitmap.Render(window);
        bitmap.Save(path);
    }

    /// <summary>安装并启动游戏，等到开始渲染（或至少完成登录、加载主类）后结束进程。</summary>
    private static async Task<string> LaunchGame(MainWindow window, string version, string output)
    {
        // 边运行边写 game.log，卡住或超时也能看到进行到哪里
        using var log = new StreamWriter(Path.Combine(output, "game.log")) { AutoFlush = true };
        var progressStep = -1;
        var launcher = new GameLauncher(window.Cfg.Clone(), line => { lock (log) log.WriteLine("[launcher] " + line); },
                                        (current, total, what) =>
                                        {
                                            var tenth = total > 0 ? (int)(current * 10L / total) : 0;
                                            if (Interlocked.Exchange(ref progressStep, tenth) != tenth)
                                                Console.WriteLine($"       {what} {current}/{total}");
                                        });
        using var process = await launcher.LaunchAsync(version);
        _game = process;
        var reached = "";
        var done = new TaskCompletionSource();

        void OnLine(string line)
        {
            lock (log)
            {
                if (_game != null)
                    log.WriteLine(line);
            }
            if (line.Contains("Backend library", StringComparison.Ordinal)
                || line.Contains("OpenAL initialized", StringComparison.Ordinal)
                || line.Contains("Sound engine started", StringComparison.Ordinal))
            {
                reached = line.Trim();
                done.TrySetResult();
            }
            else if (reached == "" && line.Contains("Setting user", StringComparison.Ordinal))
            {
                reached = line.Trim();
            }
        }

        var stdout = Task.Run(() => GameOutput.ReadLines(process.StandardOutput.BaseStream, OnLine));
        var stderr = Task.Run(() => GameOutput.ReadLines(process.StandardError.BaseStream, OnLine));
        await Task.WhenAny(done.Task, process.WaitForExitAsync(), Task.Delay(TimeSpan.FromMinutes(3)));
        var exited = process.HasExited;
        if (!exited)
        {
            process.Kill(true);
            await process.WaitForExitAsync();
        }
        await Task.WhenAny(Task.WhenAll(stdout, stderr), Task.Delay(5000));
        lock (log)
            _game = null;
        if (done.Task.IsCompleted)
            return "game started rendering: " + reached;
        if (exited)
            throw new InvalidOperationException($"游戏提前退出（退出码 {process.ExitCode}），见 game.log");
        if (reached != "")
            return "game loaded but did not reach rendering in time: " + reached;
        throw new InvalidOperationException("游戏 3 分钟内没有输出任何启动信息，见 game.log");
    }
}
