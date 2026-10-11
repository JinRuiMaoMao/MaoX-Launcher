using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;
using static MaoX.Core.I18n;

namespace MaoX.Core;

/// <summary>Java 版以外的 Mojang 游戏（基岩版、地下城、传奇等）。</summary>
public sealed class MojangGame
{
    private readonly string _name, _shortName;

    public string Id { get; init; }
    public string Name { get => T(_name); init => _name = value; }
    public string ShortName { get => T(_shortName); init => _shortName = value; }
    public string Icon { get; init; }

    /// <summary>Microsoft Store / Xbox 应用的包名（不含发布者 ID）。</summary>
    public string[] Packages { get; init; } = [];

    /// <summary>包名不确定时，按应用清单里的显示名匹配。</summary>
    public string DisplayName { get; init; }

    public int SteamAppId { get; init; }

    /// <summary>Microsoft Store 商品 ID，未安装时打开商店页面。</summary>
    public string StoreProductId { get; init; }

    /// <summary>官网介绍页（minecraft.net 后面的路径，不含语言）。</summary>
    public string WebPage { get; init; }

    /// <summary>存档 / 数据目录候选，支持环境变量。</summary>
    public string[] DataDirs { get; init; } = [];

    public string AndroidPackage { get; init; }
    public string IosScheme { get; init; }
    public string AppStoreUrl { get; init; }

    /// <summary>预览版、教育版等：没装就不显示。</summary>
    public bool HiddenUnlessInstalled { get; init; }

    public string GetUrl
    {
        get
        {
            if (Platform.IsAndroid && AndroidPackage != null)
                return "https://play.google.com/store/apps/details?id=" + AndroidPackage;
            if (Platform.IsIOS && AppStoreUrl != null)
                return AppStoreUrl;
            if (Platform.IsWindows && StoreProductId != null)
                return "ms-windows-store://pdp/?productid=" + StoreProductId;
            return $"https://www.minecraft.net/{(English ? "en-us" : "zh-hans")}/{WebPage}";
        }
    }

    public string DataDir => Platform.IsMobile
        ? null
        : DataDirs.Select(Environment.ExpandEnvironmentVariables).FirstOrDefault(Directory.Exists);
}

/// <summary>检测到的一份安装。同一个游戏可能既有 Microsoft Store 版又有 Steam 版。</summary>
public sealed class GameInstall
{
    public MojangGame Game { get; init; }
    public string Source { get; init; }
    public string Version { get; init; }

    /// <summary>shell:AppsFolder\…、steam://rungameid/…、安卓包名或 iOS URL。</summary>
    public string Target { get; init; }

    public string IconPath { get; init; }

    public override string ToString() => string.IsNullOrEmpty(Version) ? Source : $"{Source} {Version}";
}

public static class MojangGames
{
    private const string MicrosoftPublisher = "8wekyb3d8bbwe";

    public static readonly MojangGame[] All =
    [
        new()
        {
            Id = "bedrock", Name = "Minecraft 基岩版", ShortName = "基岩版", Icon = "pack",
            Packages = ["Microsoft.MinecraftUWP"], StoreProductId = "9NBLGGH2JHXJ", WebPage = "about-minecraft",
            DataDirs = [@"%APPDATA%\Minecraft Bedrock", @"%LOCALAPPDATA%\Packages\Microsoft.MinecraftUWP_8wekyb3d8bbwe\LocalState\games\com.mojang"],
            AndroidPackage = "com.mojang.minecraftpe", IosScheme = "minecraft://",
            AppStoreUrl = "https://apps.apple.com/app/minecraft/id479516143",
        },
        new()
        {
            Id = "preview", Name = "Minecraft 预览版", ShortName = "预览版", Icon = "sparkle",
            Packages = ["Microsoft.MinecraftWindowsBeta"], HiddenUnlessInstalled = true, WebPage = "",
            DataDirs = [@"%APPDATA%\Minecraft Bedrock Preview", @"%LOCALAPPDATA%\Packages\Microsoft.MinecraftWindowsBeta_8wekyb3d8bbwe\LocalState\games\com.mojang"],
        },
        new()
        {
            Id = "dungeons", Name = "Minecraft Dungeons", ShortName = "地下城", Icon = "sword",
            Packages = ["Microsoft.Lovika"], SteamAppId = 1672970, WebPage = "about-dungeons",
            DataDirs = [@"%USERPROFILE%\Saved Games\Mojang Studios\Dungeons", @"%LOCALAPPDATA%\Dungeons"],
        },
        new()
        {
            Id = "dungeons2", Name = "Minecraft Dungeons II", ShortName = "地下城 II", Icon = "swords",
            Packages = ["Microsoft.MinecraftDungeons2"], SteamAppId = 1912410, WebPage = "about-dungeons-ii",
            DataDirs = [@"%LOCALAPPDATA%\Dungeons2\Saved"],
        },
        new()
        {
            Id = "legends", Name = "Minecraft Legends", ShortName = "传奇", Icon = "flag",
            DisplayName = "Minecraft Legends", SteamAppId = 1928870, StoreProductId = "9N98Z825TNFW", WebPage = "about-legends",
        },
        new()
        {
            Id = "education", Name = "Minecraft 教育版", ShortName = "教育版", Icon = "book",
            Packages = ["Microsoft.MinecraftEducationEdition"], HiddenUnlessInstalled = true, WebPage = "",
            DataDirs = [@"%APPDATA%\Minecraft Education Edition", @"%LOCALAPPDATA%\Packages\Microsoft.MinecraftEducationEdition_8wekyb3d8bbwe\LocalState\games\com.mojang"],
        },
    ];

    public static MojangGame Find(string id) => All.FirstOrDefault(g => g.Id == id);

    /// <summary>手机上由平台项目实现：参数是安卓包名或 iOS URL scheme，返回已安装应用的版本号（不知道就返回空串），没装返回 null。</summary>
    public static Func<string, string> MobileProbe { get; set; }

    /// <summary>手机上打开另一个应用，成功返回 true。</summary>
    public static Func<string, bool> MobileLaunch { get; set; }

    /// <summary>当前平台上能玩的游戏（不管装没装）。</summary>
    public static bool Supported(MojangGame game) =>
        Platform.IsWindows ? true
        : Platform.IsAndroid ? game.AndroidPackage != null
        : Platform.IsIOS ? game.IosScheme != null
        : game.SteamAppId > 0;

    /// <summary>检测所有游戏的安装情况（会读注册表和文件，放到后台线程调用）。</summary>
    public static Dictionary<string, List<GameInstall>> DetectAll()
    {
        var result = All.ToDictionary(g => g.Id, _ => new List<GameInstall>());
        try
        {
            if (Platform.IsMobile)
            {
                foreach (var game in All.Where(Supported))
                {
                    var target = Platform.IsAndroid ? game.AndroidPackage : game.IosScheme;
                    if (MobileProbe?.Invoke(target) is { } version)
                        result[game.Id].Add(new GameInstall { Game = game, Source = Platform.IsAndroid ? "Android" : "iOS", Version = version, Target = target });
                }
                return result;
            }
            if (OperatingSystem.IsWindows())
                DetectStore(result);
            DetectSteam(result);
        }
        catch (Exception)
        {
            // 检测失败时当作没装，不影响启动器其他功能
        }
        return result;
    }

    /// <summary>启动游戏。失败时抛出异常。</summary>
    public static void Launch(GameInstall install)
    {
        if (Platform.IsMobile)
        {
            if (MobileLaunch?.Invoke(install.Target) != true)
                throw new InvalidOperationException(T("无法打开这个应用"));
        }
        else if (install.Target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            Process.Start(new ProcessStartInfo("explorer.exe", install.Target) { UseShellExecute = false });
        else
            Platform.OpenUrl(install.Target);
    }

    // ------------------------------------------------------------------ Microsoft Store / Xbox

    private record StorePackage(string Name, Version Version, string Root);

    [SupportedOSPlatform("windows")]
    private static void DetectStore(Dictionary<string, List<GameInstall>> result)
    {
        const string repository = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
        using var root = Registry.CurrentUser.OpenSubKey(repository);
        if (root == null)
            return;

        // 包全名：名称_版本_架构_资源ID_发布者ID；同一个包可能残留多个版本，取最新的
        var packages = new Dictionary<string, StorePackage>(StringComparer.OrdinalIgnoreCase);
        foreach (var fullName in root.GetSubKeyNames())
        {
            var parts = fullName.Split('_');
            if (parts.Length != 5 || parts[4] != MicrosoftPublisher || !Version.TryParse(parts[1], out var version))
                continue;
            using var key = root.OpenSubKey(fullName);
            if (key?.GetValue("PackageRootFolder") is not string folder || !File.Exists(Path.Combine(folder, "AppxManifest.xml")))
                continue;
            if (!packages.TryGetValue(parts[0], out var old) || old.Version < version)
                packages[parts[0]] = new StorePackage(parts[0], version, folder);
        }

        var byDisplayName = All.Where(g => g.DisplayName != null).ToList();
        foreach (var package in packages.Values)
        {
            var game = All.FirstOrDefault(g => g.Packages.Contains(package.Name, StringComparer.OrdinalIgnoreCase));
            if (game == null && (byDisplayName.Count == 0 || IsFramework(package.Name)))
                continue;
            var manifest = ReadManifest(package.Root);
            if (manifest == null)
                continue;
            game ??= byDisplayName.FirstOrDefault(g => manifest.DisplayName?.Contains(g.DisplayName, StringComparison.OrdinalIgnoreCase) == true);
            if (game == null || manifest.AppId == null)
                continue;
            result[game.Id].Add(new GameInstall
            {
                Game = game,
                Source = "Microsoft Store",
                Version = package.Version.ToString(),
                Target = $@"shell:AppsFolder\{package.Name}_{MicrosoftPublisher}!{manifest.AppId}",
                IconPath = manifest.Logo,
            });
        }
    }

    private static bool IsFramework(string name) =>
        name.StartsWith("Microsoft.VCLibs", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Microsoft.NET", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Microsoft.UI.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Microsoft.Windows", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Microsoft.Services", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Microsoft.DirectX", StringComparison.OrdinalIgnoreCase);

    private record Manifest(string DisplayName, string AppId, string Logo);

    private static Manifest ReadManifest(string root)
    {
        try
        {
            var doc = XDocument.Load(Path.Combine(root, "AppxManifest.xml"));
            XElement Child(XElement e, string name) => e?.Elements().FirstOrDefault(c => c.Name.LocalName == name);
            var properties = Child(doc.Root, "Properties");
            var app = Child(Child(doc.Root, "Applications"), "Application");
            var visual = app?.Elements().FirstOrDefault(c => c.Name.LocalName == "VisualElements");
            var logo = FindLogo(root, (string)visual?.Attribute("Square150x150Logo"))
                       ?? FindLogo(root, (string)Child(properties, "Logo"));
            return new Manifest((string)Child(properties, "DisplayName"), (string)app?.Attribute("Id"), logo);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>清单里写的是 Logo.png，实际文件可能带缩放后缀（Logo.scale-200.png），取最大的那张。</summary>
    private static string FindLogo(string root, string relative)
    {
        if (string.IsNullOrEmpty(relative))
            return null;
        var path = Path.Combine(root, relative.Replace('\\', Path.DirectorySeparatorChar));
        if (File.Exists(path))
            return path;
        var dir = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(dir))
            return null;
        return Directory.EnumerateFiles(dir, Path.GetFileNameWithoutExtension(path) + ".*" + Path.GetExtension(path))
                        .Where(f => !f.Contains("contrast", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(f => new FileInfo(f).Length)
                        .FirstOrDefault();
    }

    // ------------------------------------------------------------------ Steam

    private static void DetectSteam(Dictionary<string, List<GameInstall>> result)
    {
        var steam = SteamDir();
        if (steam == null)
            return;
        var libraries = new List<string> { steam };
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                libraries.Add(m.Groups[1].Value.Replace(@"\\", @"\"));

        foreach (var game in All.Where(g => g.SteamAppId > 0))
        {
            foreach (var library in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var acf = Path.Combine(library, "steamapps", $"appmanifest_{game.SteamAppId}.acf");
                if (!File.Exists(acf))
                    continue;
                // StateFlags 4 = 已完整安装（下载中、待更新时是别的值）
                var state = Regex.Match(File.ReadAllText(acf), "\"StateFlags\"\\s+\"(\\d+)\"");
                if (state.Success && (int.Parse(state.Groups[1].Value) & 4) == 0)
                    continue;
                var icon = Path.Combine(steam, "appcache", "librarycache", $"{game.SteamAppId}_icon.jpg");
                result[game.Id].Add(new GameInstall
                {
                    Game = game,
                    Source = "Steam",
                    Version = "",
                    Target = "steam://rungameid/" + game.SteamAppId,
                    IconPath = File.Exists(icon) ? icon : null,
                });
                break;
            }
        }
    }

    private static string SteamDir()
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return key?.GetValue("SteamPath") is string path && Directory.Exists(path) ? Path.GetFullPath(path) : null;
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] candidates = Platform.IsMac
            ? [Path.Combine(home, "Library", "Application Support", "Steam")]
            : [Path.Combine(home, ".steam", "steam"), Path.Combine(home, ".local", "share", "Steam")];
        return candidates.FirstOrDefault(Directory.Exists);
    }
}
