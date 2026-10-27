using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
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
/// 带 --launch 时还会真实安装并启动该版本，看到游戏开始渲染后结束进程。
/// 带 --apply-update 时如果有新版本，会下载并替换自己，然后启动新版本。结果写到 report.txt。
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
        Controls.HolidayDecor.Frozen = true;
        var output = Path.GetFullPath(index + 1 < args.Length ? args[index + 1] : "smoke-test");
        var launch = Array.IndexOf(args, "--launch");
        var version = launch >= 0 && launch + 1 < args.Length ? args[launch + 1] : null;
        var applyUpdate = args.Contains("--apply-update");
        window.Opened += async (_, _) =>
        {
            var failed = await Run(window, output, version, applyUpdate);
            desktop.Shutdown(failed ? 1 : 0);
        };
        return true;
    }

    /// <summary>整个自检的时间上限：超时后记下卡在哪一步并退出，避免 CI 一直挂着却拿不到任何报告。</summary>
    private static readonly TimeSpan Watchdog = TimeSpan.FromMinutes(15);

    private static Process _game;

    private static async Task<bool> Run(MainWindow host, string output, string version, bool applyUpdate)
    {
        var window = host.View;
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
                Line(required ? "FAIL" : "WARN", name, MainView.ErrorText(e));
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

        await Check("auto memory", () => Task.FromResult(CheckAutoMemory()));
        await Check("launcher extras", () => Task.FromResult(CheckExtras()));
        await Check("pcl extras", () => Task.FromResult(CheckPclExtras()));
        await Check("large mod list", () => CheckLargeModList());
        await Check("modpack repair", () => Task.FromResult(CheckModpackRepair()));
        await Check("languages", () => Task.FromResult(CheckLanguages()));
        await Check("site feed", () => Task.FromResult(CheckMcSiteFeed()));
        await Check("game dir switch", () => Task.FromResult(CheckGameDirSwitch(window)));

        await Check("custom version name", () => Task.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "maox-detect-loader");
            if (Directory.Exists(root))
                Directory.Delete(root, true);
            var gl = new GameLauncher(new LauncherConfig { MinecraftDir = root });
            WriteVersion(gl, "1.20.1", new JsonObject { ["id"] = "1.20.1" });
            WriteVersion(gl, "yweifyu", new JsonObject
            {
                ["id"] = "yweifyu",
                ["inheritsFrom"] = "1.20.1",
                ["libraries"] = new JsonArray(new JsonObject { ["name"] = "net.fabricmc:fabric-loader:0.16.9" }),
            });
            WriteVersion(gl, "merged", new JsonObject
            {
                ["id"] = "merged",
                ["libraries"] = new JsonArray(
                    new JsonObject { ["name"] = "net.fabricmc:fabric-loader:0.16.10" },
                    new JsonObject { ["name"] = "net.fabricmc:intermediary:1.21.1" }),
            });
            var renamed = gl.DetectLoader("yweifyu");
            var merged = gl.DetectLoader("merged");
            Directory.Delete(root, true);
            if (renamed.Loader != "fabric" || renamed.Game != "1.20.1")
                throw new Exception($"renamed version: loader={renamed.Loader} game={renamed.Game}");
            if (merged.Loader != "fabric" || merged.Game != "1.21.1")
                throw new Exception($"merged version: loader={merged.Loader} game={merged.Game}");
            return "yweifyu → Fabric 1.20.1; merged → Fabric 1.21.1";
        }));

        await Check("loader skips vanilla folder", () => Task.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "maox-loader-vanilla");
            if (Directory.Exists(root))
                Directory.Delete(root, true);
            var gl = new GameLauncher(new LauncherConfig { MinecraftDir = root });
            static JsonObject Vanilla(string id) => new()
            {
                ["id"] = id,
                ["downloads"] = new JsonObject
                {
                    ["client"] = new JsonObject { ["url"] = "http://example.invalid/" + id, ["sha1"] = "0", ["size"] = 3 },
                },
                ["libraries"] = new JsonArray(),
            };
            static JsonObject Fabric(string id, string parent) => new()
            {
                ["id"] = id,
                ["inheritsFrom"] = parent,
                ["libraries"] = new JsonArray(
                    new JsonObject { ["name"] = "net.fabricmc:fabric-loader:0.16.9" },
                    new JsonObject { ["name"] = "net.fabricmc:intermediary:" + parent }),
            };
            WriteVersion(gl, "1.20.1", Vanilla("1.20.1"));
            File.WriteAllBytes(Path.Combine(gl.VersionDir("1.20.1"), "1.20.1.jar"), [1, 2, 3]);
            WriteVersion(gl, "1.20.1-fabric", Fabric("1.20.1-fabric", "1.20.1"));
            gl.AbsorbInheritedVanillaAsync("1.20.1-fabric", "1.20.1", keepVanilla: true).GetAwaiter().GetResult();
            if (!File.Exists(gl.VersionJsonPath("1.20.1")))
                throw new Exception("existing vanilla was deleted");
            WriteVersion(gl, "1.21", Vanilla("1.21"));
            File.WriteAllBytes(Path.Combine(gl.VersionDir("1.21"), "1.21.jar"), [4, 5, 6]);
            WriteVersion(gl, "1.21-fabric", Fabric("1.21-fabric", "1.21"));
            gl.AbsorbInheritedVanillaAsync("1.21-fabric", "1.21", keepVanilla: false).GetAwaiter().GetResult();
            if (Directory.Exists(gl.VersionDir("1.21")))
                throw new Exception("install-only vanilla folder remains");
            if (!File.Exists(Path.Combine(gl.VersionDir("1.21-fabric"), "1.21-fabric.jar")))
                throw new Exception("client jar was not copied into the loader version");
            var flat = Json.ReadFile(gl.VersionJsonPath("1.21-fabric"));
            if (!string.IsNullOrEmpty(flat.Str("inheritsFrom")))
                throw new Exception("inheritsFrom was not flattened");
            var info = gl.DetectLoader("1.21-fabric");
            Directory.Delete(root, true);
            if (info.Loader != "fabric" || info.Game != "1.21")
                throw new Exception($"flattened detect: loader={info.Loader} game={info.Game}");
            return "kept existing vanilla; discarded install-only vanilla";
        }));

        foreach (var page in new[] { "launch", "download", "resources", "multiplayer", "tools", "settings" })
        {
            await Check("page " + page, async () =>
            {
                window.ShowPage(page);
                await Task.Delay(page == "resources" ? 5000 : page == "launch" ? 3500 : 1500);
                var path = Path.Combine(output, $"page-{page}.png");
                Screenshot(window, path);
                if (page == "launch" && !window.Cfg.LiveLog && !window.LaunchPage.DashCard.IsVisible)
                    throw new Exception("launch dash should be visible when live log is off");
                if (page == "launch")
                    await CheckBgmDock(window);
                if (window.Nav.Children.OfType<RadioButton>().Any(b => (string)b.Tag == "resources"))
                    throw new Exception("resources sidebar should be removed");
                if (page == "download")
                {
                    if (window.DownloadPage.VanillaRoot.Bounds.Width + 8 < window.DownloadPage.Bounds.Width)
                        throw new Exception($"download list not stretched: {window.DownloadPage.VanillaRoot.Bounds.Width} vs {window.DownloadPage.Bounds.Width}");
                    window.DownloadPage.ShowTab("community");
                    await Task.Delay(400);
                    Screenshot(window, Path.Combine(output, "page-download-community.png"));
                    window.DownloadPage.ShowTab("modpack");
                    await Task.Delay(400);
                    if (!window.DownloadPage.TargetVersionBar.IsVisible)
                        throw new Exception("target version should sit on the download title row");
                    if (window.DownloadPage.RefreshButton.IsVisible)
                        throw new Exception("refresh should hide on resource tabs");
                    Screenshot(window, Path.Combine(output, "page-download-modpack.png"));
                    window.DownloadPage.ShowTab("vanilla");
                }
                if (page == "resources")
                {
                    if (window.DownloadPage.CurrentTab != "community")
                        throw new Exception("resources should open the download community tab");
                    if (!window.DownloadPage.ResourcesHost.IsVisible)
                        throw new Exception("community host hidden");
                }
                if (page == "settings")
                {
                    window.SettingsPage.ShowTab("personal");
                    await Task.Delay(300);
                    Screenshot(window, Path.Combine(output, "page-settings-personal.png"));
                    window.SettingsPage.LanguageBox.IsDropDownOpen = true;
                    await Task.Delay(400);
                    Screenshot(window, Path.Combine(output, "page-settings-languages.png"));
                    window.SettingsPage.LanguageBox.IsDropDownOpen = false;
                    window.SettingsPage.ShowTab("other");
                    await Task.Delay(300);
                    if (window.SettingsPage.OtherScroll is { } scroll)
                        scroll.Offset = new Vector(0, scroll.Extent.Height);
                    await Task.Delay(400);
                    Screenshot(window, Path.Combine(output, "page-settings-bottom.png"));
                    window.SettingsPage.ShowTab("launch");
                }
                return Path.GetFileName(path);
            });
        }

        await Check("holiday themes", async () =>
        {
            window.Cfg.HolidayTheme = true;
            window.ShowPage("launch");
            window.SettingsPage.ShowTab("personal");
            var names = new List<string>();
            foreach (var day in Holiday.All)
            {
                if (Holiday.Match(day.Sample)?.Key != day.Key)
                    throw new Exception($"{day.Name} date {day.Sample:d} matched {Holiday.Match(day.Sample)?.Key}");
                ThemeManager.ApplyFor(window.Cfg, day);
                if (!string.Equals(ThemeManager.ToHex(ThemeManager.Accent), day.Accent, StringComparison.OrdinalIgnoreCase))
                    throw new Exception($"{day.Name} applied {ThemeManager.ToHex(ThemeManager.Accent)} != {day.Accent}");
                if (!window.HolidayArt.Showing || window.HolidayArt.Motif != day.Key)
                    throw new Exception($"{day.Name} decor {window.HolidayArt.Motif}");
                await Task.Delay(250);
                Screenshot(window, Path.Combine(output, $"holiday-{day.Key}.png"));
                window.ShowPage("settings");
                await Task.Delay(200);
                Screenshot(window, Path.Combine(output, $"holiday-{day.Key}-settings.png"));
                window.ShowPage("launch");
                names.Add($"{day.Name} {day.Accent}");
            }
            window.Cfg.HolidayDecor = false;
            ThemeManager.ApplyFor(window.Cfg, Holiday.All[0]);
            if (window.HolidayArt.Showing)
                throw new Exception("holiday decor switch should hide art");
            window.Cfg.HolidayDecor = true;
            ThemeManager.ApplyFor(window.Cfg);
            return string.Join("; ", names);
        });

        await Check("page launch mobile", async () =>
        {
            window.ShowPage("launch");
            var minW = host.MinWidth;
            var minH = host.MinHeight;
            var w = host.Width;
            var h = host.Height;
            host.MinWidth = 400;
            host.MinHeight = 280;
            host.Width = 844;
            host.Height = 390;
            await Task.Delay(800);
            Screenshot(window, Path.Combine(output, "page-launch-mobile.png"));
            window.PageScroll.Offset = new Vector(0, 4000);
            await Task.Delay(400);
            Screenshot(window, Path.Combine(output, "page-launch-mobile-bottom.png"));
            if (window.LaunchPage.NewsList.Bounds.Height <= 0)
                throw new Exception("news should layout on the compact launch page");
            host.Width = w;
            host.Height = h;
            host.MinWidth = minW;
            host.MinHeight = minH;
            await Task.Delay(300);
            return "page-launch-mobile.png";
        });

        await Check("mojang games", async () =>
        {
            var timer = Stopwatch.StartNew();
            var found = await Task.Run(MojangGames.DetectAll);
            var elapsed = timer.ElapsedMilliseconds;
            window.ShowPage("launch");
            var games = MojangGames.All.Where(MojangGames.Supported).ToList();
            foreach (var game in games.Where(g => found[g.Id].Count > 0 || !g.HiddenUnlessInstalled))
            {
                window.LaunchPage.SelectGame(game.Id, remember: false);
                await Task.Delay(800);
                Screenshot(window, Path.Combine(output, $"game-{game.Id}.png"));
            }
            window.LaunchPage.SelectGame("java", remember: false);
            var installed = games.Where(g => found[g.Id].Count > 0)
                                 .Select(g => $"{g.Id}=[{string.Join(", ", found[g.Id])}]").ToList();
            return $"{games.Count} supported, detected in {elapsed}ms: " + (installed.Count == 0 ? "none installed" : string.Join("; ", installed));
        });

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
            if (!window.TaskLayer.IsVisible)
                throw new Exception("task panel should open inside the window");
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
            window.HideTaskCenter();
            if (window.TaskLayer.IsVisible)
                throw new Exception("task panel should hide");
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

        await Check("mod versions", async () =>
        {
            var dl = window.MakeLauncher().Dl;
            var dir = Path.Combine(Path.GetTempPath(), $"maox-smoke-mods-{Guid.NewGuid():N}");
            try
            {
                const string sodium = "AANobbMI";
                var modrinth = new ModrinthClient(dl);
                var versions = (await modrinth.VersionsAsync(sodium, "1.21.1", "fabric")).Versions;
                if (versions.Count < 2 || versions.Any(v => !v.GameVersions.Contains("1.21.1") || !v.Loaders.Contains("fabric")))
                    throw new Exception($"modrinth filter returned {versions.Count} versions");
                var projects = new HashSet<string>();
                await modrinth.InstallVersionAsync(versions[1], "1.21.1", "fabric", dir, projects);
                var installed = await modrinth.InstalledVersionsAsync(sodium, Mods.ListLocalMods(dir));
                if (installed.Count != 1 || installed[0].VersionId != versions[1].Id)
                    throw new Exception("old version not identified: " + string.Join(", ", installed.Select(i => i.VersionId)));
                await modrinth.InstallVersionAsync(versions[0], "1.21.1", "fabric", dir, projects, installed.Select(i => i.File).ToList());
                var files = Mods.ListLocalMods(dir).Select(f => f.Filename).ToList();
                if (!files.Contains(versions[0].Filename) || files.Contains(versions[1].Filename))
                    throw new Exception("switch failed, files: " + string.Join(", ", files));

                var depsDir = Path.Combine(Path.GetTempPath(), $"maox-smoke-deps-{Guid.NewGuid():N}");
                List<string> deps;
                try
                {
                    deps = await modrinth.InstallAsync("PtjYWJkn", "1.21.1", "fabric", depsDir, new HashSet<string>());
                    if (deps.Count < 2)
                        throw new Exception("mod dependencies not installed: " + string.Join(", ", deps));
                }
                finally
                {
                    if (Directory.Exists(depsDir))
                        Directory.Delete(depsDir, true);
                }

                var curseforge = new CurseForgeClient(dl);
                const string jei = "238222";
                var filtered = (await curseforge.VersionsAsync(jei, "1.20.1", "forge")).Versions;
                if (filtered.Count == 0 || filtered.Any(v => !v.GameVersions.Contains("1.20.1")))
                    throw new Exception($"curseforge filter returned {filtered.Count} files");
                var first = await curseforge.VersionsAsync(jei, null, null);
                var second = first.Next != null ? await curseforge.VersionsAsync(jei, null, null, next: first.Next) : null;
                if (second == null || second.Versions.Count == 0 || second.Versions.Any(v => first.Versions.Any(f => f.Id == v.Id)))
                    throw new Exception("curseforge paging failed");

                var hit = new SearchHit { Id = sodium, Title = "Sodium", Author = "jellysquid3", Downloads = 1, Url = "https://modrinth.com/mod/sodium" };
                var dialog = new Dialogs.ModVersionsDialog(hit, modrinth, new Dialogs.ResourceTarget("1.21.1-Fabric", "1.21.1", "fabric", "mod", dir),
                                                           projects, null, null);
                _ = window.ShowDialogAsync(dialog);
                await Task.Delay(4000);
                ScreenshotControl(dialog, Path.Combine(output, "mod-versions.png"));
                dialog.Close();
                await Task.Delay(300);
                return $"modrinth {versions.Count} versions, switched {versions[1].Name} -> {versions[0].Name}; "
                       + $"deps {deps.Count}; curseforge {filtered.Count} files for forge 1.20.1, paging {first.Versions.Count}+{second.Versions.Count}";
            }
            finally
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, true);
            }
        }, required: false, timeout: TimeSpan.FromMinutes(3));

        await Check("paw art", async () =>
        {
            var view = new Controls.DialogView { DialogWidth = 520 };
            view.Content = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 36,
                Children =
                {
                    new Controls.Logo { Width = 120, Height = 120 },
                    new Controls.Logo { Width = 34, Height = 34 },
                    new Controls.Icon { Kind = "paw", Filled = true, StrokeWidth = 0, Size = 50 },
                    new Controls.PawLoader(),
                },
            };
            _ = window.ShowDialogAsync(view);
            await Task.Delay(1200);
            ScreenshotControl(view, Path.Combine(output, "paw-art.png"));
            view.Close();
            await Task.Delay(300);
            return "paw-art.png";
        }, required: false);

        await Check("touch layout editor", async () =>
        {
            var cfg = window.Cfg.Clone();
            cfg.ControlLayouts = [];
            cfg.ActiveControlLayout = "";
            var dialog = new Dialogs.TouchLayoutDialog(cfg);
            var result = window.ShowDialogAsync(dialog);
            await Task.Delay(1200);
            ScreenshotControl(dialog, Path.Combine(output, "touch-layout-default.png"));
            dialog.Select("jump");
            dialog.MoveTo("jump", dialog.Bounds.Width / 2, dialog.Bounds.Height * 0.8);
            dialog.GetVisualDescendants().OfType<Slider>().First().Value = 150;
            dialog.Select("chat");
            dialog.GetVisualDescendants().OfType<CheckBox>().First().IsChecked = true;

            var picker = new Dialogs.ControlKeysDialog([]);
            var picked = window.ShowDialogAsync(picker);
            await Task.Delay(400);
            ScreenshotControl(picker, Path.Combine(output, "touch-layout-keys.png"));
            foreach (var key in new[] { ControlKeys.MouseRight, 340, 65, 66, 67 })
                picker.Toggle(key, true);
            picker.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == I18n.T("确定"))
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            if (await picked is not List<int> keys || keys.Count != ControlButton.MaxKeys)
                throw new Exception("key picker did not return " + ControlButton.MaxKeys + " keys");
            var custom = dialog.AddButton();
            dialog.SetKeys(custom, keys.Take(2));
            dialog.MoveTo(custom, dialog.Bounds.Width * 0.25, dialog.Bounds.Height * 0.5);
            await Task.Delay(200);
            dialog.GetVisualDescendants().OfType<TextBox>().First(t => t.IsEffectivelyVisible).Text = "Use";
            dialog.GetVisualDescendants().OfType<CheckBox>().First(c => c.Content as string == I18n.T("切换模式")).IsChecked = true;
            ComboBox Combo(string item) => dialog.GetVisualDescendants().OfType<ComboBox>().First(c => c.Items.Cast<object>().Contains(item));
            Combo(I18n.T("拆分控制：准星 + 攻击 / 使用按钮")).SelectedIndex = 1;
            Combo(I18n.T("方向键：双击前进疾跑")).SelectedIndex = 1;
            await Task.Delay(400);
            var arrows = dialog.GetVisualDescendants().OfType<TextBlock>()
                .Count(t => t.IsEffectivelyVisible && t.Text is "↑" or "↓" or "←" or "→");
            if (arrows < 4)
                throw new Exception("dpad should show four separate keys, got " + arrows);
            ScreenshotControl(dialog, Path.Combine(output, "touch-layout-edited.png"));
            var save = dialog.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == I18n.T("保存"));
            save.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            if (await result is not true)
                throw new Exception("dialog did not return true after save");
            var layout = cfg.CurrentControlLayout() ?? throw new Exception("no active layout after save");
            var jump = layout.Get("jump");
            if (jump == null || Math.Abs(jump.X - 0.5) > 0.01 || Math.Abs(jump.Size - 1.5) > 0.001 || layout.Get("chat")?.Hidden != true)
                throw new Exception("unexpected layout: " + layout.ToJson().ToJsonString());
            if (layout.Buttons is not [{ Name: "Use", Toggle: true } button]
                || !button.Keys.SequenceEqual([ControlKeys.MouseRight, 340]) || Math.Abs(button.X - 0.25) > 0.01)
                throw new Exception("unexpected custom buttons: " + layout.ToJson().ToJsonString());
            if (layout.Mode != ControlLayout.Split || layout.Movement != ControlLayout.Dpad)
                throw new Exception("unexpected control mode: " + layout.ToJson().ToJsonString());
            await Task.Delay(300);
            return $"{layout.Name}: {layout.ToJson().ToJsonString()}";
        });

        await Check("resource card click", async () =>
        {
            window.ShowPage("resources");
            Border card = null;
            for (var i = 0; i < 30 && card == null; i++)
            {
                await Task.Delay(500);
                card = window.ResourcesPage.GetVisualDescendants().OfType<Border>()
                    .FirstOrDefault(b => b.Classes.Contains("row") && b.Cursor != null && b.IsEffectivelyVisible);
            }
            if (card == null)
                return "no search results (no modded version installed)";
            var title = card.GetVisualDescendants().OfType<TextBlock>().First(t => !string.IsNullOrEmpty(t.Text));
            title.RaiseEvent(new Avalonia.Input.TappedEventArgs(Avalonia.Input.Gestures.TappedEvent, null));
            Dialogs.ModVersionsDialog dialog = null;
            for (var i = 0; i < 10 && dialog == null; i++)
            {
                await Task.Delay(300);
                dialog = window.GetVisualDescendants().OfType<Dialogs.ModVersionsDialog>().FirstOrDefault();
            }
            if (dialog == null)
                throw new Exception("clicking a card did not open the versions dialog");
            await Task.Delay(3000);
            ScreenshotControl(dialog, Path.Combine(output, "card-versions.png"));
            dialog.Close();
            await Task.Delay(300);
            return $"opened versions of {title.Text}";
        }, required: false);

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

        await Check("update", async () =>
        {
            var info = await Updater.CheckAsync();
            if (info == null)
                return $"up to date (v{Mc.LauncherVersion})";
            var summary = $"v{Mc.LauncherVersion} -> v{info.Version}, "
                          + (info.Url != null ? $"{info.AssetName} {info.Size / 1048576.0:F1} MB" : $"{info.AssetName} missing");
            if (!applyUpdate)
                return summary;
            var file = await Updater.DownloadAsync(info, null);
            Updater.Apply(file);
            return summary + ", installed and restarted";
        }, required: false, timeout: TimeSpan.FromMinutes(5));

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

    private static string CheckAutoMemory()
    {
        var vanilla = GameMemory.Auto(8 * 1024, 0, null);
        var pack = GameMemory.Auto(16 * 1024, 200, "fabric");
        var tight = GameMemory.Auto(800, 0, null);
        if (vanilla is < 3500 or > 5000)
            throw new Exception($"vanilla 8 GB free → {vanilla} MB");
        if (pack <= vanilla || pack < 8000)
            throw new Exception($"200-mod pack 16 GB free → {pack} MB (vanilla {vanilla})");
        if (tight < 512 || tight > 1024)
            throw new Exception($"800 MB free → {tight} MB");
        var flags = GameMemory.JvmFlags(new LauncherConfig { OptimizeMemory = true }).ToList();
        if (!flags.Contains("-XX:+UseG1GC") || !flags.Contains("-XX:MaxGCPauseMillis=50"))
            throw new Exception("missing G1GC flags: " + string.Join(' ', flags));
        var (total, available) = GameMemory.SystemRam();
        if (total < 512 || available < 0 || available > total + 1024)
            throw new Exception($"system ram {available}/{total} MB");
        return $"vanilla={vanilla}MB pack={pack}MB free={available}MB/{total}MB";
    }

    private static string CheckLanguages()
    {
        if (I18n.Locales.Length < 140)
            throw new Exception("locales: " + I18n.Locales.Length);
        if (I18n.Languages.Length != I18n.Locales.Length + 1)
            throw new Exception("language options: " + I18n.Languages.Length);
        if (I18n.Normalize("zh") != "zh_cn" || I18n.Normalize("en") != "en_us" || I18n.Normalize("ja") != "ja_jp"
            || I18n.Normalize("zh-Hant") != "zh_tw" || I18n.Normalize("pt-BR") != "pt_br")
            throw new Exception($"aliases zh={I18n.Normalize("zh")} ja={I18n.Normalize("ja")} hant={I18n.Normalize("zh-Hant")}");
        var follow = I18n.Resolve("");
        if (string.IsNullOrEmpty(follow) || I18n.Locales.All(l => l.Key != follow))
            throw new Exception("follow system: " + follow);
        if (I18n.Lookup("en_us", "设置") != "Settings")
            throw new Exception("en settings: " + I18n.Lookup("en_us", "设置"));
        if (I18n.Lookup("zh_tw", "设置") != "設定")
            throw new Exception("zh_tw settings: " + I18n.Lookup("zh_tw", "设置"));
        if (I18n.Lookup("en_us", "实时日志") != "Live log")
            throw new Exception("en live log: " + I18n.Lookup("en_us", "实时日志"));
        if (I18n.Lookup("en_us", "爱发电") != "Afdian")
            throw new Exception("en afdian: " + I18n.Lookup("en_us", "爱发电"));
        if (!I18n.Lookup("en_us", "请从后台划掉启动器后再打开，语言才会切换。")
                .Contains("Swipe", StringComparison.Ordinal))
            throw new Exception("en relaunch: " + I18n.Lookup("en_us", "请从后台划掉启动器后再打开，语言才会切换。"));
        if (I18n.Lookup("zh_tw", "实时日志") != "即時日誌")
            throw new Exception("zh_tw live log: " + I18n.Lookup("zh_tw", "实时日志"));
        var ja = I18n.Lookup("ja_jp", "启动游戏");
        if (string.IsNullOrEmpty(ja) || ja == "启动游戏")
            throw new Exception("ja launch: " + ja);
        if (I18n.Lookup("ar_sa", "设置") != "الإعدادات")
            throw new Exception("ar settings: " + I18n.Lookup("ar_sa", "设置"));
        if (I18n.Lookup("th_th", "设置") != "ตั้งค่า")
            throw new Exception("th settings: " + I18n.Lookup("th_th", "设置"));
        if (I18n.Lookup("ko_kr", "设置") != "설정")
            throw new Exception("ko settings: " + I18n.Lookup("ko_kr", "设置"));
        var broken = I18n.Locales.FirstOrDefault(l => l.Name.Contains('\uFFFD') || l.Name.Contains("???")
                                                     || l.Region.Contains('\uFFFD') || l.Region.Contains("???"));
        if (broken != null)
            throw new Exception("garbled locale name: " + broken.Key);
        if (UiFonts.Fallbacks.Length < 8)
            throw new Exception("font fallbacks: " + UiFonts.Fallbacks.Length);
        I18n.SetLanguage(LauncherConfig.Load().Language);
        return $"locales={I18n.Locales.Length} follow={follow} ja={ja}";
    }

    private static string CheckMcSiteFeed()
    {
        var map = McSiteFeed.ParseVersionMap("| java = 26.3\n| java-snap = 26.4 Snapshot 5\n| bedrock = 26.52\n| bedrock-preview = Preview 26.70.21\n");
        if (map.GetValueOrDefault("java") != "26.3" || map.GetValueOrDefault("java-snap") != "26.4 Snapshot 5")
            throw new Exception("version map: " + string.Join(',', map.Select(p => p.Key + "=" + p.Value)));
        var drops = McSiteFeed.ParseDrops(
            """<style>.mp-highlight-title a{color:red}</style><div class="mp-highlight-image mp-dark-only"><img src="/images/thumb/Wilderness_Bound_main_page_dark.png/640px.png"></div><div class="mp-highlight-title"><a href="/w/Wilderness_Bound">Wilderness Bound</a></div><div class="mp-highlight-tag">Latest</div><div class="mp-highlight-image mp-dark-only"><img src="/images/thumb/winterdrop.jpeg/640px.jpeg"></div><div class="mp-highlight-title"><a href="/w/Fourth_Drop_2026">Fourth Drop 2026</a></div><div class="mp-highlight-tag">Upcoming</div>""");
        if (drops.All(d => !d.Tag.Equals("Latest", StringComparison.OrdinalIgnoreCase))
            || drops.All(d => d.Item.Title != "Wilderness Bound")
            || drops.Any(d => d.Item.Title.Contains("position") || d.Item.Title.Contains('{'))
            || drops.First(d => d.Tag.Equals("Latest", StringComparison.OrdinalIgnoreCase)).Item.Image is not { } art
            || !art.Contains("Wilderness_Bound", StringComparison.Ordinal))
            throw new Exception("drops: " + string.Join(',', drops.Select(d => d.Tag + "/" + d.Item.Title + "/" + d.Item.Image)));
        var news = McSiteFeed.ParseNews(JsonNode.Parse(
            """{"entries":[{"title":"Hello","text":"World","readMoreLink":"https://www.minecraft.net/article/hello"},{"title":"Old","text":"x","readMoreLink":"https://www.minecraft.net/article/old"}]}"""));
        if (news.Count != 1 || news[0].Title != "Hello" || !news[0].Url.Contains("minecraft.net"))
            throw new Exception("news");
        var articles = McSiteFeed.ParseArticles(
            """<div class="MC_tiledHeroA_card"><img class="MC_tiledHeroA_poster_img" src="/content/dam/pizza.jpg"/><h2 class="MC_Heading_3">Domino’s x Minecraft Dungeons II</h2><div class="MC_tiledHeroA_blurb"><p>Fuel up!</p></div><a href="/en-us/article/dominos-minecraft-dungeons-ii">Check it out!</a></div><div class="MC_tiledHeroA_card"><img src="/content/dam/ice.jpg"/><h2>Everything you can test</h2><a href="/en-us/article/drop-4-2026-testing">Test</a></div>""");
        if (articles.Count != 2 || articles[0].Title != "Domino’s x Minecraft Dungeons II"
            || !articles[0].Url.Contains("/article/dominos-minecraft-dungeons-ii", StringComparison.Ordinal)
            || articles[0].Image is not { } cover || !cover.Contains("pizza.jpg", StringComparison.Ordinal)
            || articles[1].Title != "Everything you can test")
            throw new Exception("articles: " + string.Join(',', articles.Select(a => a.Title + "/" + a.Image)));
        if (!McSiteFeed.ArticlesUrl.Contains("/articles", StringComparison.Ordinal))
            throw new Exception("articles url: " + McSiteFeed.ArticlesUrl);
        if (!McSiteFeed.WikiPage("Java Edition 26.3").EndsWith("/w/Java_Edition_26.3", StringComparison.Ordinal))
            throw new Exception("wiki url: " + McSiteFeed.WikiPage("Java Edition 26.3"));
        return $"versions={map.Count} drops={drops.Count}";
    }

    private static string CheckGameDirSwitch(MainView window)
    {
        var official = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "maox-smoke-official-" + Guid.NewGuid().ToString("N")[..8]));
        Directory.CreateDirectory(official);
        var before = window.Cfg.MinecraftDir;
        var added = false;
        try
        {
            window.Cfg.ExtraDirs ??= [];
            if (window.Cfg.ExtraDirs.All(d => !string.Equals(Path.GetFullPath(d.Path ?? ""), official, StringComparison.OrdinalIgnoreCase)))
            {
                window.Cfg.ExtraDirs.Add(new GameDirEntry { Name = "官方启动器", Path = official });
                added = true;
            }
            window.SettingsPage.McDirBox.Text = before;
            window.SetMinecraftDir(official);
            if (!string.Equals(Path.GetFullPath(window.Cfg.MinecraftDir), official, StringComparison.OrdinalIgnoreCase))
                throw new Exception("switch reverted to " + window.Cfg.MinecraftDir);
            if (!string.Equals(Path.GetFullPath(window.SettingsPage.McDirBox.Text ?? ""), official, StringComparison.OrdinalIgnoreCase))
                throw new Exception("settings box stale: " + window.SettingsPage.McDirBox.Text);
            window.SaveSettings();
            if (!string.Equals(Path.GetFullPath(window.Cfg.MinecraftDir), official, StringComparison.OrdinalIgnoreCase))
                throw new Exception("save reverted to " + window.Cfg.MinecraftDir);
            return "official";
        }
        finally
        {
            window.SetMinecraftDir(before);
            if (added)
                window.Cfg.ExtraDirs.RemoveAll(d => string.Equals(Path.GetFullPath(d.Path ?? ""), official, StringComparison.OrdinalIgnoreCase));
            try
            {
                Directory.Delete(official, true);
            }
            catch (Exception)
            {
            }
        }
    }

    private static async Task<string> CheckLargeModList()
    {
        var root = Path.Combine(Path.GetTempPath(), "maox-smoke-modlist-" + Guid.NewGuid().ToString("N")[..8]);
        var mods = Path.Combine(root, "mods");
        Directory.CreateDirectory(mods);
        try
        {
            const int count = 120;
            for (var i = 0; i < count; i++)
            {
                using var zip = ZipFile.Open(Path.Combine(mods, $"dummy-{i:D3}.jar"), ZipArchiveMode.Create);
                var entry = zip.CreateEntry("fabric.mod.json");
                await using var stream = entry.Open();
                var json = $"{{\"id\":\"dummy{i}\",\"name\":\"Dummy {i}\",\"version\":\"1.0.{i}\"}}";
                await stream.WriteAsync(Encoding.UTF8.GetBytes(json));
            }
            var watch = Stopwatch.StartNew();
            var listed = Mods.ListLocalMods(mods);
            var readMs = watch.ElapsedMilliseconds;
            if (listed.Count != count)
                throw new Exception($"listed {listed.Count}, expected {count}");
            if (listed.Any(m => string.IsNullOrEmpty(m.Name) || string.IsNullOrEmpty(m.Version)))
                throw new Exception("mod name/version missing");
            watch.Restart();
            var card = new Dialogs.VersionFilesCard(() => root, true);
            await Task.Delay(400);
            var uiMs = watch.ElapsedMilliseconds;
            if (readMs > 4000)
                throw new Exception($"ListLocalMods took {readMs}ms for {count} jars");
            return $"{count} mods listed in {readMs}ms, card {uiMs}ms ({card.GetType().Name})";
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch (Exception)
            {
            }
        }
    }

    private static string CheckModpackRepair()
    {
        var root = Path.Combine(Path.GetTempPath(), "maox-smoke-packrepair-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var gl = new GameLauncher(new LauncherConfig { MinecraftDir = root, VersionIsolation = true });
            const string version = "TestPack";
            Directory.CreateDirectory(gl.VersionDir(version));
            var dest = Path.Combine(gl.GameDirFor(version), "mods", "example.jar");
            var info = new ModpackManifest { Name = "TestPack", Format = "modrinth" };
            Modpack.SaveTasks(gl, version, [new DownloadTask("https://example.invalid/example.jar", dest, "abc", 12)], info);
            if (!Modpack.HasIndex(gl, version))
                throw new Exception("index not saved");
            var tasks = Modpack.LoadTasks(gl, version);
            if (tasks.Count != 1 || !tasks[0].Path.EndsWith("example.jar", StringComparison.OrdinalIgnoreCase))
                throw new Exception("load path " + tasks.FirstOrDefault()?.Path);
            if (Modpack.CountMissing(tasks) != 1)
                throw new Exception("missing should be 1");
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllText(dest, "");
            if (Modpack.CountMissing(tasks) != 1)
                throw new Exception("empty file should count as missing");
            File.WriteAllText(dest, "hello pack");
            if (Modpack.CountMissing(tasks) != 0)
                throw new Exception("present file should not be missing");
            var checks = LaunchCheck.Run(gl, version, null);
            if (checks.All(c => c.Title != I18n.T("整合包文件")))
                throw new Exception("launch check missing pack row: " + string.Join(", ", checks.Select(c => c.Title)));
            return $"index={tasks.Count} missing=0 check=ok";
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch (Exception)
            {
            }
        }
    }

    private static string CheckExtras()
    {
        var rec = Recommended.For("fabric").ToList();
        if (rec.Count < 4)
            throw new Exception("recommended fabric mods: " + rec.Count);
        var cfg = new LauncherConfig { MinecraftDir = Path.Combine(Path.GetTempPath(), "maox-extras-empty") };
        var items = LaunchCheck.Run(new GameLauncher(cfg), "", null);
        if (!LaunchCheck.HasError(items))
            throw new Exception("empty version should fail launch check");
        var clean = DiskClean.Scan(cfg.MinecraftDir);
        if (clean.Count != 0)
            throw new Exception("empty dir should have nothing to clean");
        var json = new ControlLayout { Name = "test", Gyro = true, Sensitivity = 1.2 }.ToJson();
        var back = ControlLayout.FromJson(json, "test");
        if (!back.Gyro || Math.Abs(back.Sensitivity - 1.2) > 0.01)
            throw new Exception("layout roundtrip gyro/sensitivity");
        var custom = ControlLayout.FromJson((JsonObject)JsonNode.Parse("""{"buttons":[{"label":"X","keys":[69]}]}"""), "t");
        if (custom.Buttons is not [{ InGame: true, InMenu: true }])
            throw new Exception("custom buttons should stay visible in menus by default");
        var home = Path.GetFullPath(AppPaths.DefaultMinecraftDir);
        var official = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "maox-official-mc"));
        var dirs = new LauncherConfig
        {
            MinecraftDir = home,
            ExtraDirs = [new GameDirEntry { Name = "官方启动器", Path = official }],
        };
        dirs.MinecraftDir = official;
        var after = dirs.AllGameDirs();
        if (after.Count < 2
            || after.All(d => d.Name != "官方启动器")
            || after.All(d => !string.Equals(Path.GetFullPath(d.Path), home, StringComparison.OrdinalIgnoreCase)))
            throw new Exception("game dirs collapsed after switch: " + string.Join(',', after.Select(d => d.Name)));
        return $"recommend={rec.Count} checks={items.Count}";
    }

    private static string CheckPclExtras()
    {
        var cards = Homepage.Parse("""{"cards":[{"title":"Hello","text":"World","url":"https://example.com"}]}""");
        if (cards.Count != 1 || cards[0].Title != "Hello")
            throw new Exception("homepage parse: " + cards.Count);
        if (Isolation.Normalize("nope") != Isolation.Isolate || Isolation.FromLegacy(false) != Isolation.Off)
            throw new Exception("isolation normalize");
        var cfg = new LauncherConfig { IsolationMode = "full", VersionIsolation = false };
        var mode = Isolation.Resolve(new JsonObject(), cfg);
        if (mode != Isolation.Full)
            throw new Exception("isolation resolve: " + mode);
        var local = Isolation.Resolve(new JsonObject { ["isolation"] = false }, cfg);
        if (local != Isolation.Off)
            throw new Exception("isolation version override: " + local);
        foreach (var day in Holiday.All)
        {
            if (Holiday.Match(day.Sample)?.Key != day.Key)
                throw new Exception($"holiday {day.Name}: {Holiday.Match(day.Sample)?.Key}");
        }
        if (Holiday.Match(new DateTime(2026, 12, 19)) != null
            || Holiday.Match(new DateTime(2026, 12, 27)) != null
            || Holiday.Match(new DateTime(2026, 1, 4)) != null
            || Holiday.Match(new DateTime(2026, 2, 24)) != null
            || Holiday.Match(new DateTime(2026, 7, 1)) != null)
            throw new Exception("holiday should be off outside the windows");
        var holidayOff = new LauncherConfig { HolidayTheme = false, AccentColor = "#00D9FF" };
        if (Holiday.ResolveAccent(holidayOff, new DateTime(2026, 12, 25)) != "#00D9FF")
            throw new Exception("holiday switch off");
        if (new LauncherConfig { HolidayDecor = false }.HolidayDecorOn)
            throw new Exception("holiday decor switch off");
        if (Holiday.All.Select(t => t.Key).Distinct().Count() != Holiday.All.Length
            || Holiday.All.Select(t => t.Accent).Distinct().Count() != Holiday.All.Length)
            throw new Exception("holiday keys/accents must be unique");
        if (ModCn.Title("sodium", "Sodium") is not { Length: > 6 })
            throw new Exception("mod cn sodium");
        if (HelpFaq.Items.Length < 4 || SkinSites.All.Length < 3)
            throw new Exception("help/skins");
        if (!Updater.AfdianUrl.Contains("afdian.com/a/MaoX_Laun", StringComparison.Ordinal)
            || !Updater.DiscordUrl.Contains("discord.gg/rZ93MTVDx5", StringComparison.Ordinal))
            throw new Exception("community links");
        if (!Updater.CanRelaunch)
            throw new Exception("desktop should be able to relaunch");
        if (HelpFaq.Items.All(i => i.Q != "怎么支持和反馈？"))
            throw new Exception("support faq");
        if (!LauncherAudio.Extensions.Contains(".mp3") || !LauncherAudio.Extensions.Contains(".flac")
            || !LauncherAudio.PickerPatterns.Contains("*.ogg"))
            throw new Exception("audio extensions");
        var music = Path.Combine(Path.GetTempPath(), "maox-music-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(music);
            File.WriteAllBytes(Path.Combine(music, "b.wav"), [1]);
            File.WriteAllBytes(Path.Combine(music, "a.mp3"), [1]);
            File.WriteAllText(Path.Combine(music, "readme.txt"), "x");
            var tracks = LauncherAudio.ListTracks(music);
            if (tracks.Count != 2 || !tracks[0].EndsWith("a.mp3", StringComparison.OrdinalIgnoreCase)
                || !tracks[1].EndsWith("b.wav", StringComparison.OrdinalIgnoreCase))
                throw new Exception("music playlist: " + string.Join(",", tracks));
            if (LauncherAudio.Folder != AppPaths.MusicDir)
                throw new Exception("music folder: " + LauncherAudio.Folder);
        }
        finally
        {
            try
            {
                Directory.Delete(music, true);
            }
            catch (Exception)
            {
            }
        }
        if (!Platform.IsImageFilter("*.png", "*.jpg", "*.jpeg", "*.webp") || Platform.IsImageFilter("*.png", "*.zip")
            || Platform.ImageExt([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]) != ".png"
            || Platform.ImageExt([0xFF, 0xD8, 0xFF, 0xE0]) != ".jpg")
            throw new Exception("image picker filter");
        if (OperatingSystem.IsWindows()
            && AppContext.TryGetSwitch("System.Runtime.InteropServices.BuiltInComInterop.IsSupported", out var com)
            && !com)
            throw new Exception("COM is disabled; NAudio cannot decode MP3");
        var wav = Path.Combine(Path.GetTempPath(), "maox-silent-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            File.WriteAllBytes(wav, SilentWav());
            LauncherAudio.Play(wav, true);
            var hasAudio = string.IsNullOrEmpty(LauncherAudio.LastError) && LauncherAudio.IsPlaying;
            if (!hasAudio && Environment.GetEnvironmentVariable("CI") != "true")
                throw new Exception("bgm play: " + (LauncherAudio.LastError ?? "not playing"));
            if (hasAudio)
            {
                if (!LauncherAudio.ShowControls || LauncherAudio.IsPaused)
                    throw new Exception("bgm should show controls while playing");
                LauncherAudio.Pause();
                if (LauncherAudio.IsPlaying || !LauncherAudio.IsPaused || !LauncherAudio.ShowControls)
                    throw new Exception("bgm pause");
                LauncherAudio.Resume();
                if (!LauncherAudio.IsPlaying || LauncherAudio.IsPaused)
                    throw new Exception("bgm resume");
                var mp3 = Directory.Exists(Path.Combine(AppContext.BaseDirectory, "Music"))
                    ? Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Music"), "*.mp3").FirstOrDefault()
                    : null;
                if (mp3 != null)
                {
                    LauncherAudio.Play(mp3, true);
                    if (!string.IsNullOrEmpty(LauncherAudio.LastError))
                        throw new Exception("mp3 bgm: " + LauncherAudio.LastError);
                }
                LauncherAudio.Stop();
                LauncherAudio.Play(wav, false);
                if (!string.IsNullOrEmpty(LauncherAudio.LastError))
                    throw new Exception("sfx play: " + LauncherAudio.LastError);
                if (LauncherAudio.IsPlaying || LauncherAudio.ShowControls)
                    throw new Exception("sfx should not take over the music dock");
            }
            LauncherAudio.Play(Path.Combine(Path.GetTempPath(), "maox-missing-sfx-" + Guid.NewGuid().ToString("N") + ".wav"), false);
            if (string.IsNullOrEmpty(LauncherAudio.LastError))
                throw new Exception("missing sfx should report");
        }
        finally
        {
            LauncherAudio.Stop();
            try
            {
                File.Delete(wav);
            }
            catch (Exception)
            {
            }
        }
        if (new LauncherConfig().BgmVolume != 70)
            throw new Exception("bgm volume default");
        if (new LauncherConfig().SettingsTab != "launch")
            throw new Exception("settings tab default");
        if (new LauncherConfig().DownloadTab != "vanilla")
            throw new Exception("download tab default");
        LauncherAudio.Volume = 0.4f;
        if (Math.Abs(LauncherAudio.Volume - 0.4f) > 0.01)
            throw new Exception("bgm volume");
        LauncherAudio.Next();
        LauncherAudio.Toggle();
        LauncherAudio.Pause();
        LauncherAudio.Stop();
        if (LauncherAudio.ShowControls || LauncherAudio.IsPaused || LauncherAudio.IsPlaying)
            throw new Exception("bgm should hide without a track");
        if (!Loaders.All.Contains("liteloader") || Mc.LoaderNames["liteloader"] != "LiteLoader")
            throw new Exception("liteloader missing");
        if (new LauncherConfig().AutoBackupWorlds)
            throw new Exception("auto backup should be off by default");
        if (new LauncherConfig().LiveLog)
            throw new Exception("live log should be off by default");
        if (ModCn.ApiQuery("钠") != "sodium" || ModCn.ExpandQuery("小地图").Count == 0)
            throw new Exception("mod cn expand: " + ModCn.ApiQuery("钠"));
        if (Homepage.PresetKey("preset:news") != "preset:news" || Homepage.Presets.Length < 4)
            throw new Exception("homepage presets");
        if (Modpack.ExportFormats.Length < 4 || Modpack.ExportFormats.All(f => f.Key != "multimc"))
            throw new Exception("export formats");
        var snap = Path.Combine(Path.GetTempPath(), "maox-snap-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(Path.Combine(snap, "mods"));
            File.WriteAllText(Path.Combine(snap, "mods", "a.jar"), "one");
            File.WriteAllText(Path.Combine(snap, "options.txt"), "fov:70");
            var zip = InstanceSnapshot.BackupAsync(snap).GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(snap, "mods", "b.jar"), "two");
            File.WriteAllText(Path.Combine(snap, "mods", "a.jar"), "changed");
            File.Delete(Path.Combine(snap, "options.txt"));
            var diff = InstanceSnapshot.Diff(zip, snap);
            if (diff.All(c => c.Rel != "mods/b.jar" || c.Kind != "added")
                || diff.All(c => c.Rel != "mods/a.jar" || c.Kind != "changed")
                || diff.All(c => c.Rel != "options.txt" || c.Kind != "removed"))
                throw new Exception("snapshot diff: " + string.Join(',', diff.Select(c => c.Kind + ":" + c.Rel)));
        }
        finally
        {
            if (Directory.Exists(snap))
                Directory.Delete(snap, true);
        }
        return $"cards={cards.Count} isolation={mode} faq={HelpFaq.Items.Length} cn=sodium formats={Modpack.ExportFormats.Length}";
    }

    private static void WriteVersion(GameLauncher gl, string id, JsonObject data)
    {
        Directory.CreateDirectory(gl.VersionDir(id));
        Json.WriteFile(gl.VersionJsonPath(id), data);
    }

    private static async Task CheckBgmDock(MainView window)
    {
        if (LauncherAudio.ShowControls != window.BgmDock.IsVisible)
            throw new Exception("bgm dock should match the current playlist");
        var wav = Path.Combine(Path.GetTempPath(), "maox-bgm-dock-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            File.WriteAllBytes(wav, SilentWav());
            LauncherAudio.Play(wav, true);
            await Task.Delay(80);
            if (LauncherAudio.ShowControls && !window.BgmDock.IsVisible)
                throw new Exception("bgm dock should show while music is playing");
            if (LauncherAudio.IsPlaying)
            {
                LauncherAudio.Pause();
                await Task.Delay(80);
                if (!window.BgmDock.IsVisible)
                    throw new Exception("bgm dock should stay visible while paused");
            }
            LauncherAudio.Stop();
            await Task.Delay(80);
            if (window.BgmDock.IsVisible)
                throw new Exception("bgm dock should hide without music");
        }
        finally
        {
            LauncherAudio.Stop();
            try
            {
                File.Delete(wav);
            }
            catch (Exception)
            {
            }
        }
    }

    private static void ScreenshotControl(Control control, string path)
    {
        var scale = TopLevel.GetTopLevel(control)?.RenderScaling ?? 1;
        var size = new PixelSize(Math.Max(1, (int)(control.Bounds.Width * scale)), Math.Max(1, (int)(control.Bounds.Height * scale)));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
        bitmap.Render(control);
        bitmap.Save(path);
    }

    private static void Screenshot(MainView view, string path)
    {
        var window = TopLevel.GetTopLevel(view)!;
        var scale = window.RenderScaling;
        var size = new PixelSize((int)(window.Bounds.Width * scale), (int)(window.Bounds.Height * scale));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
        bitmap.Render(window);
        bitmap.Save(path);
    }

    /// <summary>安装并启动游戏，等到开始渲染（或至少完成登录、加载主类）后结束进程。</summary>
    private static async Task<string> LaunchGame(MainView window, string version, string output, LaunchAuth auth)
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

    /// <summary>44 字节头 + 一帧静音，用来确认背景音乐解码不会把启动器崩掉。</summary>
    private static byte[] SilentWav() =>
    [
        (byte)'R', (byte)'I', (byte)'F', (byte)'F', 38, 0, 0, 0,
        (byte)'W', (byte)'A', (byte)'V', (byte)'E',
        (byte)'f', (byte)'m', (byte)'t', (byte)' ', 16, 0, 0, 0,
        1, 0, 1, 0, 0x40, 0x1F, 0, 0, 0x80, 0x3E, 0, 0, 2, 0, 16, 0,
        (byte)'d', (byte)'a', (byte)'t', (byte)'a', 2, 0, 0, 0, 0, 0,
    ];
}
