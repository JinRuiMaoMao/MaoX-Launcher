using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MaoX.Core;

public class LoaderException : Exception
{
    public LoaderException(string message, Exception inner = null) : base(message, inner)
    {
    }
}

/// <summary>加载器版本列表中的一项。Version 为安装用的完整版本号，Display 为显示用版本号。</summary>
public class LoaderItem
{
    public string Version { get; set; }
    public string Display { get; set; }
    public bool Stable { get; set; }
    public bool Recommended { get; set; }
    /// <summary>NeoForge 专用：forge（1.20.1）或 neoforge。</summary>
    public string Artifact { get; set; }
    /// <summary>OptiFine 专用字段。</summary>
    public string Type { get; set; }
    public string Patch { get; set; }
    public string Filename { get; set; }
    public string Forge { get; set; }
}

/// <summary>URL 编码辅助（对应 Python 的 quote / urlencode）。</summary>
internal static class Web
{
    public static string Quote(string text) => Uri.EscapeDataString(text ?? "").Replace("%2F", "/");

    public static string Query(IEnumerable<KeyValuePair<string, string>> pairs) =>
        string.Join("&", pairs.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value ?? "")));
}

public static partial class Loaders
{
    public const string FabricMeta = "https://meta.fabricmc.net/v2";
    public const string QuiltMeta = "https://meta.quiltmc.org/v3";
    public const string ForgeMaven = "https://maven.minecraftforge.net/net/minecraftforge/forge/";
    public const string ForgePromos = "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json";
    public const string NeoForgeApi = "https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/";
    public const string NeoForgeMaven = "https://maven.neoforged.net/releases/net/neoforged/";
    // OptiFine 官网没有直链，与 PCL / HMCL 一样使用 BMCLAPI 提供的 OptiFine 镜像
    public const string OptiFineApi = "https://bmclapi2.bangbang93.com/optifine/";
    public const string OptiFineTweaker = "optifine.OptiFineTweaker";

    public static readonly string[] All = ["forge", "neoforge", "fabric", "quilt"];

    [GeneratedRegex("alpha|beta|pre|rc|snapshot", RegexOptions.IgnoreCase)]
    private static partial Regex UnstablePattern();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();

    [GeneratedRegex(@"_([A-Z])(\d+)(?:_pre(\d+))?$")]
    private static partial Regex OptiFinePattern();

    public static bool IsUnstable(string version) => UnstablePattern().IsMatch(version);

    private static List<long> Numbers(string text) =>
        Digits().Matches(text).Select(m => long.TryParse(m.Value, out var n) ? n : long.MaxValue).ToList();

    private static int CompareLists(List<long> a, List<long> b)
    {
        for (var i = 0; i < Math.Min(a.Count, b.Count); i++)
        {
            var c = a[i].CompareTo(b[i]);
            if (c != 0)
                return c;
        }
        return a.Count.CompareTo(b.Count);
    }

    /// <summary>版本号比较：先比较主体数字，同号时正式版大于测试版，再比较全部数字。</summary>
    public static int CompareVersions(string a, string b)
    {
        var baseA = Regex.Split(a, "[-+]")[0];
        var baseB = Regex.Split(b, "[-+]")[0];
        var c = CompareLists(Numbers(baseA), Numbers(baseB));
        if (c != 0)
            return c;
        c = (!IsUnstable(a)).CompareTo(!IsUnstable(b));
        return c != 0 ? c : CompareLists(Numbers(a), Numbers(b));
    }

    /// <summary>1.21.1 -> "21.1."，1.21 -> "21.0."，26.3 -> "26.3.0."。</summary>
    public static string NeoForgePrefix(string mc)
    {
        var parts = mc.Split('.').ToList();
        if (parts[0] == "1")
        {
            parts = parts.Skip(1).ToList();
            while (parts.Count < 2)
                parts.Add("0");
            return string.Join(".", parts.Take(2)) + ".";
        }
        while (parts.Count < 3)
            parts.Add("0");
        return string.Join(".", parts.Take(3)) + ".";
    }

    /// <summary>HD_U_I6 / HD_U_I6_pre3 的比较（正式版排在同号测试版之后）。</summary>
    public static int CompareOptiFine(string a, string b)
    {
        static (string, long, bool, long) Key(string name)
        {
            var m = OptiFinePattern().Match(name);
            if (!m.Success)
                return ("", 0, false, 0);
            return (m.Groups[1].Value, long.Parse(m.Groups[2].Value), !m.Groups[3].Success,
                    m.Groups[3].Success ? long.Parse(m.Groups[3].Value) : 0);
        }

        var (la, na, ra, pa) = Key(a);
        var (lb, nb, rb, pb) = Key(b);
        var c = string.CompareOrdinal(la, lb);
        if (c == 0)
            c = na.CompareTo(nb);
        if (c == 0)
            c = ra.CompareTo(rb);
        return c != 0 ? c : pa.CompareTo(pb);
    }

    [GeneratedRegex(@"^Main-Class:\s*(\S+)", RegexOptions.Multiline)]
    private static partial Regex MainClassPattern();

    internal static string MainClass(string jar)
    {
        string text;
        using (var zip = ZipFile.OpenRead(jar))
        {
            var entry = zip.GetEntry("META-INF/MANIFEST.MF")
                        ?? throw new LoaderException($"{Path.GetFileName(jar)} 中没有 Main-Class");
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            text = reader.ReadToEnd();
        }
        text = text.Replace("\r\n ", "").Replace("\n ", "");
        var m = MainClassPattern().Match(text);
        if (!m.Success)
            throw new LoaderException($"{Path.GetFileName(jar)} 中没有 Main-Class");
        return m.Groups[1].Value;
    }

    /// <summary>解压 zip 中的单个文件；目标已存在且大小一致时跳过。</summary>
    internal static void Extract(ZipArchive zip, string member, string dest)
    {
        var entry = zip.GetEntry(member) ?? throw new LoaderException($"安装器中缺少文件 {member}");
        var info = new FileInfo(dest);
        if (info.Exists && info.Length == entry.Length)
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        entry.ExtractToFile(dest, true);
    }

    internal static bool IsZip(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            return false;
        }
    }

    /// <summary>Windows 上 javaw.exe 没有控制台，运行安装工具时优先使用同目录的 java.exe。</summary>
    internal static string ConsoleJava(string java)
    {
        if (Platform.IsWindows && Path.GetFileName(java).Equals("javaw.exe", StringComparison.OrdinalIgnoreCase))
        {
            var console = Path.Combine(Path.GetDirectoryName(java)!, "java.exe");
            if (File.Exists(console))
                return console;
        }
        return java;
    }

    /// <summary>运行 Java，合并读取标准输出与错误输出，返回 (退出码, 输出行)。</summary>
    internal static async Task<(int ExitCode, List<string> Output)> RunJavaAsync(string java, IEnumerable<string> args,
                                                                                 string workDir = null)
    {
        var psi = new ProcessStartInfo(ConsoleJava(java))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (workDir != null)
            psi.WorkingDirectory = workDir;
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new LoaderException("无法启动 Java 进程");
        process.StandardInput.Close();
        var lines = new List<string>();

        async Task Pump(StreamReader reader)
        {
            string line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                lock (lines)
                    lines.Add(line);
            }
        }

        await Task.WhenAll(Pump(process.StandardOutput), Pump(process.StandardError));
        await process.WaitForExitAsync();
        return (process.ExitCode, lines);
    }

    /// <summary>输出的最后 12 行（去掉首尾空行）。</summary>
    internal static string Tail(List<string> lines)
    {
        var text = string.Join("\n", lines).Trim();
        var all = text.Split('\n');
        return string.Join("\n", all.Skip(Math.Max(0, all.Length - 12)));
    }
}

/// <summary>模组加载器安装：Forge、NeoForge、Fabric、Quilt，以及 OptiFine。</summary>
public partial class LoaderInstaller
{
    private readonly GameLauncher _gl;
    private Downloader Dl => _gl.Dl;
    private Action<string> Log => _gl.Log;

    public LoaderInstaller(GameLauncher launcher)
    {
        _gl = launcher;
    }

    // ------------------------------------------------------------------ 版本列表

    /// <summary>loader 为 forge / neoforge / fabric / quilt / optifine。推荐版本排在最前，其余按版本号降序。</summary>
    public async Task<List<LoaderItem>> ListVersionsAsync(string loader, string mc)
    {
        List<LoaderItem> items;
        try
        {
            items = loader switch
            {
                "fabric" => await ListFabricAsync(mc),
                "quilt" => await ListQuiltAsync(mc),
                "forge" => await ListForgeAsync(mc),
                "neoforge" => await ListNeoForgeAsync(mc),
                "optifine" => await ListOptiFineAsync(mc),
                _ => throw new LoaderException($"不支持的模组加载器：{loader}"),
            };
        }
        catch (DownloadException e) when (e.Status == 404)
        {
            return [];
        }
        Comparison<string> compare = loader == "optifine" ? Loaders.CompareOptiFine : Loaders.CompareVersions;
        items = items.OrderByDescending(i => i.Display, Comparer<string>.Create(compare)).ToList();
        if (!items.Any(i => i.Recommended))
        {
            var firstStable = items.FirstOrDefault(i => i.Stable);
            if (firstStable != null)
                firstStable.Recommended = true;
        }
        return items.OrderBy(i => !i.Recommended).ToList();
    }

    private async Task<List<LoaderItem>> ListFabricAsync(string mc)
    {
        var data = await Dl.FetchJsonAsync($"{Loaders.FabricMeta}/versions/loader/{Web.Quote(mc)}");
        return (data as JsonArray).Items().Select(d =>
        {
            var v = d.Get("loader").Str("version");
            return new LoaderItem { Version = v, Display = v, Stable = d.Get("loader").Bool("stable") };
        }).ToList();
    }

    private async Task<List<LoaderItem>> ListQuiltAsync(string mc)
    {
        var data = await Dl.FetchJsonAsync($"{Loaders.QuiltMeta}/versions/loader/{Web.Quote(mc)}");
        return (data as JsonArray).Items().Select(d =>
        {
            var v = d.Get("loader").Str("version");
            return new LoaderItem { Version = v, Display = v, Stable = !Loaders.IsUnstable(v) };
        }).ToList();
    }

    [GeneratedRegex("<version>([^<]+)</version>")]
    private static partial Regex MavenVersion();

    private async Task<List<LoaderItem>> ListForgeAsync(string mc)
    {
        var xml = await Dl.FetchTextAsync(Loaders.ForgeMaven + "maven-metadata.xml", mirror: false);
        JsonNode promos;
        try
        {
            promos = (await Dl.FetchJsonAsync(Loaders.ForgePromos, mirror: false)).Get("promos");
        }
        catch (Exception)
        {
            promos = null;
        }
        var recommended = promos.Str(mc + "-recommended");
        var items = new List<LoaderItem>();
        foreach (Match m in MavenVersion().Matches(xml))
        {
            var full = m.Groups[1].Value;
            if (!full.StartsWith(mc + "-", StringComparison.Ordinal))
                continue;
            var display = full[(mc.Length + 1)..];
            if (display.EndsWith("-" + mc, StringComparison.Ordinal))
                display = display[..^(mc.Length + 1)];
            items.Add(new LoaderItem
            {
                Version = full, Display = display, Stable = true, Recommended = display == recommended,
            });
        }
        return items;
    }

    private async Task<List<LoaderItem>> ListNeoForgeAsync(string mc)
    {
        var (artifact, prefix) = mc == "1.20.1" ? ("forge", "1.20.1-") : ("neoforge", Loaders.NeoForgePrefix(mc));
        var data = await Dl.FetchJsonAsync(Loaders.NeoForgeApi + artifact, mirror: false);
        return data.Items("versions").Select(v => v.AsStr())
            .Where(v => v.StartsWith(prefix, StringComparison.Ordinal))
            .Select(v => new LoaderItem { Version = v, Display = v, Stable = !Loaders.IsUnstable(v), Artifact = artifact })
            .ToList();
    }

    private async Task<List<LoaderItem>> ListOptiFineAsync(string mc)
    {
        var data = await Dl.FetchJsonAsync(Loaders.OptiFineApi + Web.Quote(mc), mirror: false);
        return (data as JsonArray).Items().Select(d =>
        {
            var patch = d.Str("patch") ?? "";
            var name = $"{d.Str("type")}_{patch}";
            return new LoaderItem
            {
                Version = name, Display = name, Stable = !patch.StartsWith("pre", StringComparison.Ordinal),
                Type = d.Str("type"), Patch = patch, Filename = d.Str("filename"), Forge = d.Str("forge") ?? "",
            };
        }).ToList();
    }

    // ------------------------------------------------------------------ 安装

    /// <summary>安装加载器，返回新版本的 id。原版需要已经安装好（或会被自动补全）。</summary>
    public async Task<string> InstallAsync(string loader, string mc, LoaderItem item)
    {
        if (loader == "optifine")
            return await InstallOptiFineAsync(mc, item);
        if (!Mc.LoaderNames.TryGetValue(loader ?? "", out var name))
            throw new LoaderException($"不支持的模组加载器：{loader}");
        Log($"正在安装 {name} {item.Display}（Minecraft {mc}）");
        if (loader is "fabric" or "quilt")
        {
            var meta = loader == "fabric" ? Loaders.FabricMeta : Loaders.QuiltMeta;
            var profile = await Dl.FetchJsonAsync(
                $"{meta}/versions/loader/{Web.Quote(mc)}/{Web.Quote(item.Version)}/profile/json");
            return WriteVersion((JsonObject)profile);
        }

        string url;
        if (loader == "forge")
        {
            var full = item.Version;
            url = $"{Loaders.ForgeMaven}{full}/forge-{full}-installer.jar";
        }
        else
        {
            var artifact = item.Artifact ?? "neoforge";
            var v = item.Version;
            url = $"{Loaders.NeoForgeMaven}{artifact}/{v}/{artifact}-{v}-installer.jar";
        }
        return await InstallFromInstallerAsync(mc, url, name);
    }

    /// <summary>
    /// 安装 OptiFine。指定 forgeVersion 时作为模组放进该版本的 mods 文件夹，
    /// 否则生成一个独立的 OptiFine 版本（OptiFine 补丁库 + launchwrapper）。返回版本 id。
    /// </summary>
    public async Task<string> InstallOptiFineAsync(string mc, LoaderItem item, string forgeVersion = null)
    {
        var url = $"{Loaders.OptiFineApi}{Web.Quote(mc)}/{Web.Quote(item.Type)}/{Web.Quote(item.Patch)}";
        var installer = _gl.PathOf("cache", "installers", item.Filename);
        Log($"正在下载 OptiFine {item.Display}...");
        await Dl.DownloadManyAsync([new DownloadTask(url, installer)]);
        if (!Loaders.IsZip(installer))
        {
            File.Delete(installer);
            throw new LoaderException("OptiFine 文件已损坏，请重试");
        }

        if (!string.IsNullOrEmpty(forgeVersion))
        {
            var mods = Path.Combine(_gl.GameDirFor(forgeVersion), "mods");
            Directory.CreateDirectory(mods);
            File.Copy(installer, Path.Combine(mods, item.Filename), true);
            Log($"已将 OptiFine 放入 {forgeVersion} 的 mods 文件夹");
            return forgeVersion;
        }

        await _gl.PrepareAsync(mc);
        var coord = $"optifine:OptiFine:{mc}_{item.Version}";
        var library = LibPath(coord);
        string lwCoord;
        HashSet<string> names;
        using (var zip = ZipFile.OpenRead(installer))
        {
            names = zip.Entries.Select(e => e.FullName).ToHashSet();
            if (names.Contains("launchwrapper-of.txt"))
            {
                string lwVersion;
                using (var reader = new StreamReader(zip.GetEntry("launchwrapper-of.txt")!.Open(), Encoding.UTF8))
                    lwVersion = reader.ReadToEnd().Trim();
                lwCoord = "optifine:launchwrapper-of:" + lwVersion;
                Loaders.Extract(zip, $"launchwrapper-of-{lwVersion}.jar", LibPath(lwCoord));
            }
            else if (names.Contains("launchwrapper-2.0.jar"))
            {
                lwCoord = "optifine:launchwrapper:2.0";
                Loaders.Extract(zip, "launchwrapper-2.0.jar", LibPath(lwCoord));
            }
            else
            {
                lwCoord = null;
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(library)!);
        if (names.Contains("optifine/Patcher.class"))
        {
            Log("正在生成 OptiFine 补丁...");
            var java = await _gl.SelectJavaAsync(_gl.LoadVersion(mc));
            var (code, output) = await Loaders.RunJavaAsync(java,
                ["-cp", installer, "optifine.Patcher", _gl.PathOf("versions", mc, mc + ".jar"), installer, library]);
            if (code != 0 || !File.Exists(library))
                throw new LoaderException($"OptiFine 补丁生成失败（退出码 {code}）：\n{Loaders.Tail(output)}");
        }
        else
        {
            File.Copy(installer, library, true);
        }

        static JsonObject LocalLib(string name) => new()
        {
            ["name"] = name,
            ["downloads"] = new JsonObject { ["artifact"] = new JsonObject { ["path"] = Mc.MavenPath(name) } },
        };

        var parent = Json.ReadFile(_gl.VersionJsonPath(mc));
        var versionId = $"{mc}-OptiFine_{item.Version}";
        var data = new JsonObject
        {
            ["id"] = versionId,
            ["inheritsFrom"] = mc,
            ["type"] = parent.Str("type", "release"),
            ["time"] = parent.Str("time", ""),
            ["releaseTime"] = parent.Str("releaseTime", ""),
            ["mainClass"] = "net.minecraft.launchwrapper.Launch",
            ["libraries"] = new JsonArray(
                LocalLib(coord),
                lwCoord != null ? LocalLib(lwCoord) : new JsonObject { ["name"] = "net.minecraft:launchwrapper:1.12" }),
        };
        if (parent.Get("arguments") != null)
            data["arguments"] = new JsonObject
            {
                ["game"] = new JsonArray("--tweakClass", Loaders.OptiFineTweaker),
                ["jvm"] = new JsonArray(),
            };
        else
            data["minecraftArguments"] = parent.Str("minecraftArguments", "") + " --tweakClass " + Loaders.OptiFineTweaker;
        return WriteVersion(data);
    }

    private string WriteVersion(JsonObject data)
    {
        var versionId = data.Str("id");
        Json.WriteFile(_gl.VersionJsonPath(versionId), data);
        return versionId;
    }

    private string LibPath(string coord) => _gl.PathOf(["libraries", .. Mc.MavenPath(coord).Split('/')]);

    private async Task<string> InstallFromInstallerAsync(string mc, string url, string name)
    {
        await _gl.PrepareAsync(mc);
        var installer = _gl.PathOf("cache", "installers", url[(url.LastIndexOf('/') + 1)..]);
        Log($"正在下载 {name} 安装器...");
        await Dl.DownloadManyAsync([new DownloadTask(url, installer)]);
        try
        {
            using var zip = ZipFile.OpenRead(installer);
            var entry = zip.GetEntry("install_profile.json")
                        ?? throw new LoaderException($"{name} 安装器中没有 install_profile.json");
            var profile = ReadEntryJson(entry);
            if (profile.Get("versionInfo") != null)
                return InstallLegacy(zip, profile, mc);
            return await InstallModernAsync(zip, profile, mc, installer, name);
        }
        catch (InvalidDataException)
        {
            File.Delete(installer);
            throw new LoaderException($"{name} 安装器文件已损坏，请重试");
        }
    }

    private static JsonNode ReadEntryJson(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        return JsonNode.Parse(stream);
    }

    /// <summary>1.12.2 及更早的旧版安装器：解出 universal 包并写入版本信息。</summary>
    private string InstallLegacy(ZipArchive zip, JsonNode profile, string mc)
    {
        var install = profile.Get("install");
        var info = (JsonObject)profile.Get("versionInfo");
        Loaders.Extract(zip, install.Str("filePath"), LibPath(install.Str("path")));
        var target = install.Str("target");
        info["id"] = string.IsNullOrEmpty(target) ? info.Str("id") : target;
        if (string.IsNullOrEmpty(info.Str("inheritsFrom")))
        {
            var minecraft = install.Str("minecraft");
            info["inheritsFrom"] = string.IsNullOrEmpty(minecraft) ? mc : minecraft;
        }
        if (!info.ContainsKey("jar"))
            info["jar"] = info.Str("inheritsFrom");
        return WriteVersion(info);
    }

    /// <summary>新版安装器（Forge 1.13+ 与全部 NeoForge）：下载依赖并依次运行安装处理器。</summary>
    private async Task<string> InstallModernAsync(ZipArchive zip, JsonNode profile, string mc, string installer,
                                                  string name)
    {
        var members = zip.Entries.Select(e => e.FullName).ToHashSet();
        var versionEntry = zip.GetEntry((profile.Str("json") ?? "").TrimStart('/'))
                           ?? throw new LoaderException($"{name} 安装器中缺少版本信息");
        var version = (JsonObject)ReadEntryJson(versionEntry);
        var libDir = _gl.PathOf("libraries");

        var tasks = new List<DownloadTask>();
        foreach (var lib in profile.Items("libraries").Concat(version.Items("libraries")))
        {
            var artifact = lib.Get("downloads").Get("artifact");
            var rel = artifact.Str("path");
            if (string.IsNullOrEmpty(rel))
                rel = Mc.MavenPath(lib.Str("name"));
            var dest = Path.Combine([libDir, .. rel.Split('/')]);
            if (members.Contains("maven/" + rel))
                Loaders.Extract(zip, "maven/" + rel, dest);
            else if (!string.IsNullOrEmpty(artifact.Str("url")))
                tasks.Add(new DownloadTask(artifact.Str("url"), dest, artifact.Str("sha1"), artifact.Long("size")));
        }
        Log($"正在下载 {name} 依赖库...");
        await Dl.DownloadManyAsync(tasks, (d, t) => _gl.Progress(d, t, $"下载 {name} 依赖库"));

        var processors = profile.Items("processors").Where(p =>
        {
            var sides = p.Arr("sides");
            return sides == null || sides.Count == 0 || sides.Items().Any(s => s.AsStr() == "client");
        }).ToList();
        if (processors.Count > 0)
            await RunProcessorsAsync(zip, profile, processors, mc, installer, name);
        return WriteVersion(version);
    }

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex DataToken();

    private async Task RunProcessorsAsync(ZipArchive zip, JsonNode profile, List<JsonNode> processors, string mc,
                                          string installer, string name)
    {
        var work = Path.Combine(Path.GetTempPath(), "maox-installer-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        try
        {
            var data = new Dictionary<string, string>();
            foreach (var (key, raw) in profile.Obj("data") ?? [])
            {
                var value = (raw is JsonObject obj ? obj.Str("client", "") : raw.AsStr()) ?? "";
                if (value.StartsWith('[') && value.EndsWith(']'))
                {
                    data[key] = LibPath(value[1..^1]);
                }
                else if (value.Length >= 2 && value.StartsWith('\'') && value.EndsWith('\''))
                {
                    data[key] = value[1..^1];
                }
                else if (value.StartsWith('/'))
                {
                    var member = value.TrimStart('/');
                    var dest = Path.Combine([work, .. member.Split('/')]);
                    Loaders.Extract(zip, member, dest);
                    data[key] = dest;
                }
                else
                {
                    data[key] = value;
                }
            }
            data["SIDE"] = "client";
            data["MINECRAFT_VERSION"] = mc;
            data["ROOT"] = _gl.McDir;
            data["INSTALLER"] = installer;
            data["MINECRAFT_JAR"] = _gl.PathOf("versions", mc, mc + ".jar");
            data["LIBRARY_DIR"] = _gl.PathOf("libraries");

            string Fill(string arg)
            {
                if (arg.StartsWith('[') && arg.EndsWith(']'))
                    return LibPath(arg[1..^1]);
                return DataToken().Replace(arg, m => data.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
            }

            var java = await _gl.SelectJavaAsync(_gl.LoadVersion(mc));
            var total = processors.Count;
            for (var index = 1; index <= total; index++)
            {
                TaskContext.ThrowIfCancelled();
                var proc = processors[index - 1];
                var outputs = new Dictionary<string, string>();
                foreach (var (k, v) in proc.Obj("outputs") ?? [])
                    outputs[Fill(k)] = Fill(v.AsStr() ?? "").Trim('\'');
                if (outputs.Count > 0 && outputs.All(o => File.Exists(o.Key) && Http.FileSha1(o.Key) == o.Value))
                    continue;

                var jarCoord = proc.Str("jar");
                var jar = LibPath(jarCoord);
                var classpath = string.Join(Path.PathSeparator,
                                            new[] { jar }.Concat(proc.Items("classpath").Select(c => LibPath(c.AsStr()))));
                var args = proc.Items("args").Select(a => Fill(a.AsStr())).ToList();
                var taskIndex = args.IndexOf("--task");
                var task = taskIndex >= 0 && taskIndex < args.Count - 1 ? args[taskIndex + 1] : jarCoord.Split(':')[1];
                Log($"运行安装处理器 {index}/{total}：{task}");
                _gl.Progress(index - 1, total, "安装 " + name);
                var (code, output) = await Loaders.RunJavaAsync(
                    java, new[] { "-cp", classpath, Loaders.MainClass(jar) }.Concat(args), work);
                if (code != 0)
                    throw new LoaderException($"安装处理器 {task} 失败（退出码 {code}）：\n{Loaders.Tail(output)}");
                foreach (var (path, sha) in outputs)
                {
                    if (!File.Exists(path) || Http.FileSha1(path) != sha)
                        throw new LoaderException($"安装处理器 {task} 的输出校验失败：{Path.GetFileName(path)}");
                }
            }
            _gl.Progress(total, total, "安装 " + name);
        }
        finally
        {
            try
            {
                Directory.Delete(work, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
