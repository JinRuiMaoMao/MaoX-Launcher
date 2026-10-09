using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
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

    private static async Task<bool> Run(MainWindow window, string output, string version)
    {
        Directory.CreateDirectory(output);
        var report = new StringBuilder();
        var failed = false;

        void Line(string status, string name, string detail)
        {
            report.AppendLine($"[{status}] {name}: {detail}");
            Console.WriteLine($"[{status}] {name}: {detail}");
            if (status == "FAIL")
                failed = true;
        }

        async Task Check(string name, Func<Task<string>> check, bool required = true)
        {
            try
            {
                Line("PASS", name, await check());
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
                return Path.GetFileName(path);
            });
        }

        await Check("version manifest", async () =>
        {
            for (var i = 0; i < 60 && window.Manifest == null; i++)
                await Task.Delay(500);
            var count = window.Manifest?["versions"]?.AsArray().Count ?? 0;
            return count > 0 ? $"{count} versions" : throw new InvalidOperationException("版本列表没有加载");
        });

        await Check("terracotta", async () =>
        {
            var tc = new Terracotta(null, window.MakeLauncher().Dl, _ => { });
            if (!tc.Supported)
                return "not supported on this platform";
            if (!await tc.InstalledAsync())
                await tc.InstallAsync((_, _) => { });
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
        }, required: false);

        if (version != null)
            await Check("launch " + version, () => Task.Run(() => LaunchGame(window, version, output)));

        report.AppendLine(failed ? "RESULT: FAIL" : "RESULT: PASS");
        await File.WriteAllTextAsync(Path.Combine(output, "report.txt"), report.ToString());
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
        var lines = new List<string>();
        var launcher = new GameLauncher(window.Cfg.Clone(), line => { lock (lines) lines.Add("[launcher] " + line); },
                                        (_, _, _) => { });
        using var process = await launcher.LaunchAsync(version);
        var reached = "";
        var done = new TaskCompletionSource();

        void OnLine(string line)
        {
            lock (lines)
                lines.Add(line);
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
        lock (lines)
            File.WriteAllLines(Path.Combine(output, "game.log"), lines);
        if (done.Task.IsCompleted)
            return "game started rendering: " + reached;
        if (exited)
            throw new InvalidOperationException($"游戏提前退出（退出码 {process.ExitCode}），见 game.log");
        if (reached != "")
            return "game loaded but did not reach rendering in time: " + reached;
        throw new InvalidOperationException("游戏 3 分钟内没有输出任何启动信息，见 game.log");
    }
}
