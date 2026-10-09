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

        await Check("task center", async () =>
        {
            var dl = window.MakeLauncher().Dl;
            var entry = window.Manifest.Items("versions").First(v => v.Str("id") == "1.21.1");
            var clientUrl = (await dl.FetchJsonAsync(entry.Str("url"))).Get("downloads").Get("client").Str("url");
            var file = Path.Combine(Path.GetTempPath(), $"maox-smoke-{Guid.NewGuid():N}.jar");
            var a = window.RunTask("测试任务（进度）", async () =>
            {
                for (var i = 1; i <= 12; i++)
                {
                    window.Progress(i, 12, "测试进度");
                    await Task.Delay(150, TaskContext.Token);
                }
            });
            var b = window.RunTask("测试任务（取消下载）", async () =>
            {
                await dl.DownloadManyAsync([new DownloadTask(clientUrl, file)], (d, t) => window.Progress(d, t, "下载"));
                // CI 网速很快时下载可能已经完成，继续等待取消信号
                await Task.Delay(Timeout.Infinite, TaskContext.Token);
            });
            await Task.Delay(400);
            var running = window.Tasks.Count(t => t.Running);
            window.Tasks.First(t => t.Name == "测试任务（取消下载）").Cancel.Cancel();
            window.ShowTaskCenter();
            await Task.Delay(400);
            ScreenshotControl(window.TaskCenterView, Path.Combine(output, "task-center.png"));
            await Task.WhenAll(a, b);
            await Task.Delay(300);
            ScreenshotControl(window.TaskCenterView, Path.Combine(output, "task-center-done.png"));
            var states = string.Join(", ", window.Tasks.Select(t => $"{t.Name}={t.State}"));
            File.Delete(file);
            if (running != 2 || window.Tasks.Any(t => t.State == "failed")
                || window.Tasks.First(t => t.Name == "测试任务（进度）").State != "done"
                || window.Tasks.First(t => t.Name == "测试任务（取消下载）").State != "cancelled")
                throw new Exception($"unexpected task states (parallel={running}): {states}");
            window.ClearFinishedTasks();
            return $"parallel={running}; {states}";
        });

        Account skinAccount = null;
        var skinRequests = new List<string>();
        await Check("offline skin", async () =>
        {
            var png = TestSkin();
            var account = Accounts.OfflineAccount("MaoXSmoke");
            account.Skin = Skins.Store(png);
            account.SkinSlim = true;
            var server = OfflineSkinServer.Shared;
            server.Register(account);
            server.Requested += path =>
            {
                lock (skinRequests)
                    skinRequests.Add(path);
            };
            var meta = (await Http.SendAsync(server.Root + "/")).Json();
            var profile = (await Http.SendAsync($"{server.Root}/sessionserver/session/minecraft/profile/{account.Uuid}?unsigned=false")).Json();
            var property = profile.Items("properties").First();
            using var rsa = System.Security.Cryptography.RSA.Create();
            rsa.ImportFromPem(meta.Str("signaturePublickey"));
            if (!rsa.VerifyData(Encoding.UTF8.GetBytes(property.Str("value")), Convert.FromBase64String(property.Str("signature")),
                                System.Security.Cryptography.HashAlgorithmName.SHA1,
                                System.Security.Cryptography.RSASignaturePadding.Pkcs1))
                throw new Exception("textures signature does not verify");
            var (skinUrl, slim, _) = Skins.ParseTextures(profile);
            var texture = await Http.SendAsync(skinUrl);
            if (!slim || !texture.Body.AsSpan().SequenceEqual(png))
                throw new Exception($"texture mismatch: slim={slim} status={texture.Status}");
            var joined = (await Http.SendAsync($"{server.Root}/sessionserver/session/minecraft/hasJoined?username=Friend&serverId=x")).Json();
            if (joined.Str("id") != Mc.OfflineUuid("Friend"))
                throw new Exception("hasJoined returned " + joined.ToJsonString());

            var dialog = new Dialogs.SkinDialog(account);
            _ = window.ShowDialogAsync(dialog);
            await Task.Delay(2000);
            // 整窗截图在高 DPI 下会把带缩放变换的对话框卡片画大，只截对话框内容
            ScreenshotControl(dialog, Path.Combine(output, "skin-dialog.png"));
            dialog.Close();
            await Task.Delay(300);
            skinAccount = account;
            return $"{server.Root} signed profile ok, texture {texture.Body.Length} bytes";
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
            await Check("launch " + version, () => Task.Run(async () =>
            {
                LaunchAuth auth = null;
                if (skinAccount != null)
                    (auth, _) = await Accounts.PrepareLaunchAsync(skinAccount, window.Cfg, window.MakeLauncher().Dl, AppPaths.ToolsDir);
                lock (skinRequests)
                    skinRequests.Clear();
                var result = await LaunchGame(window, version, output, auth);
                if (auth == null)
                    return result;
                lock (skinRequests)
                    return $"{result}; skin server saw: {string.Join(", ", skinRequests.Distinct())}";
            }));

        lock (reportPath)
            File.AppendAllText(reportPath, (failed ? "RESULT: FAIL" : "RESULT: PASS") + Environment.NewLine);
        return failed;
    }

    /// <summary>画一张简单的 64×64 测试皮肤（纤细模型，手臂第 54–55 列留空）。</summary>
    private static byte[] TestSkin()
    {
        using var bitmap = new WriteableBitmap(new PixelSize(64, 64), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888,
                                               Avalonia.Platform.AlphaFormat.Unpremul);
        using (var buffer = bitmap.Lock())
        {
            var pixels = new byte[64 * 64 * 4];
            void Fill(int x0, int y0, int w, int h, uint argb)
            {
                for (var y = y0; y < y0 + h; y++)
                for (var x = x0; x < x0 + w; x++)
                    BitConverter.GetBytes(argb).CopyTo(pixels, (y * 64 + x) * 4);
            }
            Fill(0, 0, 32, 16, 0xFFE0B48C);
            Fill(8, 8, 8, 3, 0xFF5A3A22);
            Fill(9, 12, 2, 1, 0xFF2050E0);
            Fill(13, 12, 2, 1, 0xFF2050E0);
            Fill(0, 16, 16, 16, 0xFF3050A0);
            Fill(16, 16, 24, 16, 0xFF00B5D6);
            Fill(40, 16, 14, 16, 0xFFE0B48C);
            Fill(16, 48, 16, 16, 0xFF3050A0);
            Fill(32, 48, 14, 16, 0xFFE0B48C);
            for (var y = 0; y < 64; y++)
                System.Runtime.InteropServices.Marshal.Copy(pixels, y * 256, buffer.Address + y * buffer.RowBytes, 256);
        }
        using var stream = new MemoryStream();
        bitmap.Save(stream);
        return stream.ToArray();
    }

    private static void ScreenshotControl(Control control, string path)
    {
        var scale = TopLevel.GetTopLevel(control)?.RenderScaling ?? 1;
        var size = new PixelSize(Math.Max(1, (int)(control.Bounds.Width * scale)), Math.Max(1, (int)(control.Bounds.Height * scale)));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
        bitmap.Render(control);
        bitmap.Save(path);
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
    private static async Task<string> LaunchGame(MainWindow window, string version, string output, LaunchAuth auth)
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
        using var process = await launcher.LaunchAsync(version, null, auth);
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
