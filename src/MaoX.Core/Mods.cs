using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MaoX.Core;

/// <summary>资源类型：键、名称、存放的文件夹（整合包为 null）。</summary>
public record ResourceKind(string Key, string Name, string Folder);

public record SortOption(string Key, string Name);

/// <summary>统一的搜索结果项。Id 对 CurseForge 为数字字符串。</summary>
public class SearchHit
{
    public string Id { get; set; }
    public string Slug { get; set; } = "";
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    public string Description { get; set; } = "";
    public long Downloads { get; set; }
    public string IconUrl { get; set; } = "";
    public List<string> Categories { get; set; } = [];
    public string Url { get; set; } = "";
    /// <summary>API 返回的原始数据。</summary>
    public JsonNode Raw { get; set; }
}

public record SearchResult(List<SearchHit> Hits, long Total);

/// <summary>整合包的一个可下载版本。</summary>
public class ModpackVersion
{
    public string Name { get; set; }
    public string Detail { get; set; }
    public string Url { get; set; }
    public string Filename { get; set; }
    public string Sha1 { get; set; }
    public long? Size { get; set; }
}

/// <summary>本地模组 / 资源包 / 光影 / 数据包文件。Version 可能为 null；文件夹的 Version 为「文件夹」。</summary>
public class LocalFile
{
    public string Filename { get; set; }
    public string Path { get; set; }
    public bool Enabled { get; set; } = true;
    public bool IsDir { get; set; }
    public string Name { get; set; }
    public string Version { get; set; }
}

/// <summary>一个可用的模组更新。Source 为 "Modrinth" 或 "CurseForge"。</summary>
public class ModUpdate
{
    public LocalFile Mod { get; set; }
    public string Source { get; set; }
    public string Filename { get; set; }
    public string Url { get; set; }
    public string Sha1 { get; set; }
    public long? Size { get; set; }
    public string Version { get; set; }
}

/// <summary>Modrinth 与 CurseForge 客户端的公共接口。</summary>
public interface IModClient
{
    string Name { get; }

    Task<SearchResult> SearchAsync(string query, string gameVersion, string loader, int offset = 0, int limit = 20,
                                   string index = "relevance", string kind = "mod");

    Task<List<ModpackVersion>> ModpackVersionsAsync(string projectId);

    Task<HashSet<string>> InstalledIdsAsync(IEnumerable<LocalFile> mods);

    Task<List<string>> InstallAsync(string projectId, string gameVersion, string loader, string modsDir,
                                    ISet<string> installedProjects, Action<string> log = null, string kind = "mod");
}

/// <summary>模组、资源包、光影、数据包的常量与本地文件管理，以及更新检查。</summary>
public static partial class Mods
{
    public const string ModrinthApi = "https://api.modrinth.com/v2";
    // CurseForge 官方 API 必须使用开发者密钥，这里与 PCL / HMCL 一样经由 MCIM 镜像访问
    public const string CurseForgeApi = "https://mod.mcimirror.top/curseforge/v1";
    public const string CurseForgeCdn = "https://edge.forgecdn.net/files/";

    public static readonly string[] ModLoaders = ["forge", "neoforge", "fabric", "quilt"];

    public static readonly IReadOnlyList<SortOption> Sorts =
    [
        new("relevance", "相关性"), new("downloads", "下载量"), new("follows", "热门"),
        new("updated", "最近更新"), new("newest", "最新发布"),
    ];

    public static readonly IReadOnlySet<string> HiddenCategories = new HashSet<string>
    {
        "fabric", "forge", "neoforge", "quilt", "liteloader", "modloader", "rift", "minecraft", "datapack",
    };

    public static readonly IReadOnlyList<ResourceKind> Kinds =
    [
        new("mod", "模组", "mods"), new("resourcepack", "资源包", "resourcepacks"),
        new("shader", "光影", "shaderpacks"), new("datapack", "数据包", "datapacks"), new("modpack", "整合包", null),
    ];

    public static readonly IReadOnlyDictionary<string, string> KindNames = Kinds.ToDictionary(k => k.Key, k => k.Name);
    public static readonly IReadOnlyDictionary<string, string> KindFolders = Kinds.ToDictionary(k => k.Key, k => k.Folder);

    public static List<string> ModrinthVersionLoaders(string kind, string loader) => kind switch
    {
        "mod" => ModrinthLoaders(loader),
        "datapack" => ["datapack"],
        _ => [],
    };

    /// <summary>启动器的加载器 -> 在 Modrinth 上可用的加载器标签。</summary>
    public static List<string> ModrinthLoaders(string loader) => loader switch
    {
        "fabric" => ["fabric"],
        "quilt" => ["quilt", "fabric"],
        "forge" => ["forge"],
        "neoforge" => ["neoforge"],
        _ => [],
    };

    // ------------------------------------------------------------------ CurseForge 常量

    public const int CfGameId = 432;
    public const int CfModClass = 6;

    public static readonly IReadOnlyDictionary<string, int> CfClasses = new Dictionary<string, int>
    {
        ["mod"] = 6, ["resourcepack"] = 12, ["shader"] = 6552, ["datapack"] = 6945, ["modpack"] = 4471,
    };

    public static readonly IReadOnlyDictionary<string, int[]> CfLoaderTypes = new Dictionary<string, int[]>
    {
        ["forge"] = [1], ["neoforge"] = [6], ["fabric"] = [4], ["quilt"] = [5, 4],
    };

    public static readonly IReadOnlySet<string> CfLoaderTags = new HashSet<string> { "forge", "neoforge", "fabric", "quilt" };

    public static readonly IReadOnlyDictionary<string, int> CfSortFields = new Dictionary<string, int>
    {
        ["relevance"] = 2, ["downloads"] = 6, ["follows"] = 2, ["updated"] = 3, ["newest"] = 11,
    };

    public const int CfRequiredDependency = 3;

    private static readonly ConcurrentDictionary<(string, long, DateTime), uint> Fingerprints = new();

    /// <summary>CurseForge 的文件指纹：去掉空白字节后的 MurmurHash2（seed = 1）。</summary>
    public static uint CurseForgeFingerprint(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new FileNotFoundException(path);
        var key = (info.FullName, info.Length, info.LastWriteTimeUtc);
        if (Fingerprints.TryGetValue(key, out var cached))
            return cached;
        var raw = File.ReadAllBytes(path);
        var data = new byte[raw.Length];
        var length = 0;
        foreach (var b in raw)
        {
            if (b is not (9 or 10 or 13 or 32))
                data[length++] = b;
        }

        const uint m = 0x5BD1E995;
        var h = 1u ^ (uint)length;
        var n4 = length - length % 4;
        unchecked
        {
            for (var i = 0; i < n4; i += 4)
            {
                var k = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i, 4));
                k *= m;
                k ^= k >> 24;
                k *= m;
                h = (h * m) ^ k;
            }
            var tail = length - n4;
            if (tail == 3)
                h ^= (uint)data[n4 + 2] << 16;
            if (tail >= 2)
                h ^= (uint)data[n4 + 1] << 8;
            if (tail >= 1)
                h = (h ^ data[n4]) * m;
            h ^= h >> 13;
            h *= m;
            h ^= h >> 15;
        }
        Fingerprints[key] = h;
        return h;
    }

    /// <summary>CurseForge 文件中 algo = 1 的哈希（SHA1）。</summary>
    public static string CfSha1(JsonNode file) =>
        file.Items("hashes").FirstOrDefault(h => h.Int("algo") == 1)?.Str("value");

    // ------------------------------------------------------------------ 更新

    /// <summary>先用 Modrinth 检查，Modrinth 不认识的模组再交给 CurseForge。</summary>
    public static async Task<List<ModUpdate>> CheckUpdatesAsync(ModrinthClient modrinth, CurseForgeClient curseforge,
                                                                IReadOnlyList<LocalFile> mods, string gameVersion,
                                                                string loader)
    {
        var (updates, unknown) = await modrinth.UpdatesAsync(mods, gameVersion, loader);
        if (unknown.Count > 0)
        {
            try
            {
                updates.AddRange(await curseforge.UpdatesAsync(unknown, gameVersion, loader));
            }
            catch (Exception)
            {
                // CurseForge 不可用时只返回 Modrinth 的结果
            }
        }
        return updates;
    }

    /// <summary>下载新版本并替换旧文件（保留禁用状态），返回新文件路径。</summary>
    public static async Task<string> ApplyUpdateAsync(Downloader dl, ModUpdate update)
    {
        var mod = update.Mod;
        var folder = Path.GetDirectoryName(mod.Path)!;
        var dest = Path.Combine(folder, update.Filename);
        await dl.DownloadManyAsync([new DownloadTask(update.Url, dest, update.Sha1, update.Size)]);
        if (!mod.Enabled)
        {
            File.Move(dest, dest + ".disabled", true);
            dest += ".disabled";
        }
        // macOS 默认文件系统也不区分大小写，只差大小写时不能删除旧文件
        var comparison = Platform.IsLinux ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (!string.Equals(Path.GetFullPath(dest), Path.GetFullPath(mod.Path), comparison))
            File.Delete(mod.Path);
        return dest;
    }

    // ------------------------------------------------------------------ 本地模组

    private static readonly JsonDocumentOptions LenientJson = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>宽松解析：去掉 BOM，并把字符串中的控制字符替换成空格（对应 Python 的 strict=False）。</summary>
    private static JsonNode ParseLenient(string text)
    {
        var chars = text.TrimStart('\uFEFF').Select(c => c < ' ' ? ' ' : c).ToArray();
        return JsonNode.Parse(new string(chars), documentOptions: LenientJson);
    }

    private static string TomlValue(string text, string key)
    {
        var m = Regex.Match(text, $@"^\s*{Regex.Escape(key)}\s*=\s*[""']([^""']*)[""']", RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value : null;
    }

    [GeneratedRegex(@"^Implementation-Version:\s*(\S+)", RegexOptions.Multiline)]
    private static partial Regex ImplementationVersion();

    private static string NonEmpty(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrEmpty(v));

    /// <summary>从模组 jar 中读取名称与版本，支持 Fabric / Quilt / Forge / NeoForge / 旧版 mcmod.info。</summary>
    public static (string Name, string Version) ReadModInfo(string path)
    {
        string name = null, version = null;
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var names = zip.Entries.Select(e => e.FullName).ToHashSet();

            string Read(string member)
            {
                using var reader = new StreamReader(zip.GetEntry(member)!.Open(), Encoding.UTF8);
                return reader.ReadToEnd();
            }

            if (names.Contains("fabric.mod.json"))
            {
                var data = ParseLenient(Read("fabric.mod.json"));
                name = NonEmpty(data.Str("name"), data.Str("id"));
                version = data.Str("version");
            }
            else if (names.Contains("quilt.mod.json"))
            {
                var data = ParseLenient(Read("quilt.mod.json")).Get("quilt_loader");
                name = NonEmpty(data.Get("metadata").Str("name"), data.Str("id"));
                version = data.Str("version");
            }
            else
            {
                var toml = new[] { "META-INF/neoforge.mods.toml", "META-INF/mods.toml" }.FirstOrDefault(names.Contains);
                if (toml != null)
                {
                    var text = Read(toml);
                    name = TomlValue(text, "displayName");
                    version = TomlValue(text, "version");
                    if (version != null && version.Contains("${") && names.Contains("META-INF/MANIFEST.MF"))
                    {
                        var m = ImplementationVersion().Match(Read("META-INF/MANIFEST.MF"));
                        version = m.Success ? m.Groups[1].Value : null;
                    }
                }
                else if (names.Contains("mcmod.info"))
                {
                    var data = ParseLenient(Read("mcmod.info"));
                    var entries = data is JsonObject ? data.Arr("modList") : data as JsonArray;
                    var first = entries?.Count > 0 ? entries[0] : null;
                    if (first != null)
                    {
                        name = first.Str("name");
                        version = first.Str("version");
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException
                                      or JsonException or InvalidOperationException or ArgumentException)
        {
        }
        if (version != null && version.Contains("${"))
            version = null;
        return (name, version);
    }

    private static List<string> SortedEntries(string folder) =>
        Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName)
            .OrderBy(f => f.ToLowerInvariant(), StringComparer.Ordinal).ToList();

    [GeneratedRegex(@"\.(jar|zip)(\.disabled)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ModSuffix();

    [GeneratedRegex(@"\.zip$", RegexOptions.IgnoreCase)]
    private static partial Regex ZipSuffix();

    /// <summary>mods 文件夹中的 .jar / .jar.disabled / .zip，按文件名排序。</summary>
    public static List<LocalFile> ListLocalMods(string modsDir)
    {
        if (!Directory.Exists(modsDir))
            return [];
        var mods = new List<LocalFile>();
        foreach (var filename in SortedEntries(modsDir))
        {
            var lower = filename.ToLowerInvariant();
            if (!(lower.EndsWith(".jar") || lower.EndsWith(".jar.disabled") || lower.EndsWith(".zip")))
                continue;
            var path = Path.Combine(modsDir, filename);
            var (name, version) = ReadModInfo(path);
            mods.Add(new LocalFile
            {
                Filename = filename,
                Path = path,
                Enabled = !lower.EndsWith(".disabled"),
                Name = name ?? ModSuffix().Replace(filename, ""),
                Version = version,
            });
        }
        return mods;
    }

    /// <summary>资源包、光影包、数据包文件夹中的 zip 与已解压的文件夹。</summary>
    public static List<LocalFile> ListLocalFiles(string folder)
    {
        if (!Directory.Exists(folder))
            return [];
        var items = new List<LocalFile>();
        foreach (var filename in SortedEntries(folder))
        {
            var path = Path.Combine(folder, filename);
            var isDir = Directory.Exists(path);
            if (!(isDir || filename.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
                continue;
            items.Add(new LocalFile
            {
                Filename = filename,
                Path = path,
                Enabled = true,
                IsDir = isDir,
                Name = ZipSuffix().Replace(filename, ""),
                Version = isDir ? "文件夹" : null,
            });
        }
        return items;
    }

    /// <summary>启用 / 禁用模组（增删 .disabled 后缀），返回新路径。</summary>
    public static string SetModEnabled(LocalFile mod, bool enabled)
    {
        var path = mod.Path;
        var target = path.EndsWith(".disabled", StringComparison.Ordinal) ? path[..^".disabled".Length] : path;
        if (!enabled)
            target += ".disabled";
        if (target != path)
            File.Move(path, target, true);
        return target;
    }

    /// <summary>计算一组文件的 SHA1，读取失败的文件跳过。返回 sha1 -> 文件（重复时保留最后一个）。</summary>
    internal static Task<Dictionary<string, LocalFile>> HashFilesAsync(IEnumerable<LocalFile> mods) => Task.Run(() =>
    {
        var hashes = new Dictionary<string, LocalFile>();
        foreach (var mod in mods)
        {
            try
            {
                hashes[Http.FileSha1(mod.Path)] = mod;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        return hashes;
    });

    internal static Task<Dictionary<uint, LocalFile>> FingerprintFilesAsync(IEnumerable<LocalFile> mods) => Task.Run(() =>
    {
        var prints = new Dictionary<uint, LocalFile>();
        foreach (var mod in mods)
        {
            try
            {
                prints[CurseForgeFingerprint(mod.Path)] = mod;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        return prints;
    });
}

/// <summary>Modrinth API 客户端（受下载源设置影响，可走 MCIM 镜像）。</summary>
public class ModrinthClient : IModClient
{
    private readonly Downloader _dl;

    public string Name => "Modrinth";

    public ModrinthClient(Downloader downloader)
    {
        _dl = downloader;
    }

    public async Task<SearchResult> SearchAsync(string query, string gameVersion, string loader, int offset = 0,
                                                int limit = 20, string index = "relevance", string kind = "mod")
    {
        var loaders = kind == "mod" ? Mods.ModrinthLoaders(loader) : [];
        var facets = new JsonArray(new JsonArray("project_type:" + kind));
        if (!string.IsNullOrEmpty(gameVersion))
            facets.Add(new JsonArray("versions:" + gameVersion));
        if (loaders.Count > 0)
            facets.Add(new JsonArray(loaders.Select(l => (JsonNode)("categories:" + l)).ToArray()));
        var qs = Web.Query(new Dictionary<string, string>
        {
            ["query"] = query ?? "",
            ["facets"] = Json.Serialize(facets),
            ["offset"] = offset.ToString(),
            ["limit"] = limit.ToString(),
            ["index"] = index,
        });
        var data = await _dl.FetchJsonAsync(Mods.ModrinthApi + "/search?" + qs);
        var hits = data.Items("hits").Select(h =>
        {
            var display = h.Arr("display_categories");
            var categories = display is { Count: > 0 } ? display : h.Arr("categories");
            var slug = h.Str("slug", "");
            return new SearchHit
            {
                Id = h.Str("project_id"),
                Slug = slug,
                Title = h.Str("title", ""),
                Author = h.Str("author", ""),
                Description = h.Str("description", ""),
                Downloads = h.Long("downloads"),
                IconUrl = h.Str("icon_url") ?? "",
                Categories = categories.Items().Select(c => c.AsStr()).Where(c => !Mods.HiddenCategories.Contains(c)).ToList(),
                Url = $"https://modrinth.com/{kind}/{(string.IsNullOrEmpty(slug) ? h.Str("project_id") : slug)}",
                Raw = h,
            };
        }).ToList();
        return new SearchResult(hits, data.Long("total_hits"));
    }

    /// <summary>项目的版本列表（Modrinth 原始数据，新版本在前）。gameVersion / loaders 为空时不过滤。</summary>
    public async Task<List<JsonNode>> ProjectVersionsAsync(string projectId, string gameVersion,
                                                           IReadOnlyList<string> loaders)
    {
        var parameters = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(gameVersion))
            parameters["game_versions"] = Json.Serialize(new JsonArray(gameVersion));
        if (loaders is { Count: > 0 })
            parameters["loaders"] = Json.Serialize(new JsonArray(loaders.Select(l => (JsonNode)l).ToArray()));
        var url = $"{Mods.ModrinthApi}/project/{Web.Quote(projectId)}/version";
        var data = await _dl.FetchJsonAsync(url + (parameters.Count > 0 ? "?" + Web.Query(parameters) : ""));
        return (data as JsonArray).Items().ToList();
    }

    private static JsonNode PrimaryFile(JsonNode version)
    {
        var files = version.Items("files").ToList();
        return files.FirstOrDefault(f => f.Bool("primary")) ?? files.FirstOrDefault();
    }

    /// <summary>整合包的全部版本，新版本在前。</summary>
    public async Task<List<ModpackVersion>> ModpackVersionsAsync(string projectId)
    {
        var output = new List<ModpackVersion>();
        foreach (var v in await ProjectVersionsAsync(projectId, null, null))
        {
            var file = PrimaryFile(v);
            var filename = file.Str("filename");
            if (file == null || filename == null || !filename.EndsWith(".mrpack", StringComparison.Ordinal))
                continue;
            var games = string.Join(", ", v.Items("game_versions").Take(3).Select(g => g.AsStr()));
            var loaders = string.Join(", ", v.Items("loaders").Select(g => g.AsStr()));
            var number = v.Str("version_number");
            output.Add(new ModpackVersion
            {
                Name = string.IsNullOrEmpty(number) ? v.Str("name", "") : number,
                Detail = $"Minecraft {games}  ·  {loaders}",
                Url = file.Str("url"),
                Filename = filename,
                Sha1 = file.Get("hashes").Str("sha1"),
                Size = file.Get("size") != null ? file.Long("size") : null,
            });
        }
        return output;
    }

    /// <summary>最合适的版本：优先正式版，否则取最新的版本；没有可用版本时返回 null。</summary>
    public async Task<JsonNode> BestVersionAsync(string projectId, string gameVersion, IReadOnlyList<string> loaders)
    {
        var versions = await ProjectVersionsAsync(projectId, gameVersion, loaders);
        if (versions.Count == 0)
            return null;
        return versions.FirstOrDefault(v => v.Str("version_type") == "release") ?? versions[0];
    }

    /// <summary>根据文件 SHA1 查询对应的 Modrinth 版本，返回 {sha1: version}。</summary>
    public async Task<JsonObject> IdentifyAsync(IEnumerable<string> sha1Hashes)
    {
        var list = sha1Hashes.ToList();
        if (list.Count == 0)
            return new JsonObject();
        var data = await _dl.PostJsonAsync(Mods.ModrinthApi + "/version_files", new JsonObject
        {
            ["hashes"] = new JsonArray(list.Select(h => (JsonNode)h).ToArray()),
            ["algorithm"] = "sha1",
        });
        return data as JsonObject ?? new JsonObject();
    }

    /// <summary>通过文件 SHA1 识别已安装模组对应的 Modrinth 项目 id。</summary>
    public async Task<HashSet<string>> InstalledIdsAsync(IEnumerable<LocalFile> mods)
    {
        var hashes = await Mods.HashFilesAsync(mods);
        JsonObject found;
        try
        {
            found = await IdentifyAsync(hashes.Keys);
        }
        catch (Exception)
        {
            return [];
        }
        return found.Select(p => p.Value).OfType<JsonObject>().Select(v => v.Str("project_id"))
            .Where(id => !string.IsNullOrEmpty(id)).ToHashSet();
    }

    /// <summary>安装资源（模组会连同全部必需前置），返回新安装的文件名列表。installedProjects 会被更新。</summary>
    public async Task<List<string>> InstallAsync(string projectId, string gameVersion, string loader, string modsDir,
                                                 ISet<string> installedProjects, Action<string> log = null,
                                                 string kind = "mod")
    {
        log ??= _ => { };
        var loaders = Mods.ModrinthVersionLoaders(kind, loader);
        Directory.CreateDirectory(modsDir);
        var queue = new Queue<string>([projectId]);
        var installed = new List<string>();
        while (queue.Count > 0)
        {
            var pid = queue.Dequeue();
            if (installedProjects.Contains(pid))
                continue;
            var version = await BestVersionAsync(pid, gameVersion, loaders);
            if (version == null)
            {
                if (pid == projectId)
                    throw new InvalidOperationException("这个模组没有适用于当前版本的文件");
                log($"[警告] 前置模组 {pid} 没有适用于当前版本的文件，已跳过");
                continue;
            }
            var file = PrimaryFile(version);
            if (file == null)
                continue;
            var dest = Path.Combine(modsDir, file.Str("filename"));
            await _dl.DownloadManyAsync([new DownloadTask(file.Str("url"), dest, file.Get("hashes").Str("sha1"),
                                                          file.Long("size"))]);
            installedProjects.Add(version.Str("project_id"));
            installed.Add(file.Str("filename"));
            if (pid != projectId)
                log($"已安装前置模组 {file.Str("filename")}");
            if (kind != "mod")
                continue;
            foreach (var dep in version.Items("dependencies"))
            {
                if (dep.Str("dependency_type") == "required" && !string.IsNullOrEmpty(dep.Str("project_id")))
                    queue.Enqueue(dep.Str("project_id"));
            }
        }
        return installed;
    }

    /// <summary>返回 (可更新列表, Modrinth 不认识的模组列表)。</summary>
    public async Task<(List<ModUpdate> Updates, List<LocalFile> Unknown)> UpdatesAsync(
        IEnumerable<LocalFile> mods, string gameVersion, string loader)
    {
        var hashes = await Mods.HashFilesAsync(mods);
        if (hashes.Count == 0)
            return ([], []);
        var data = await _dl.PostJsonAsync(Mods.ModrinthApi + "/version_files/update", new JsonObject
        {
            ["hashes"] = new JsonArray(hashes.Keys.Select(h => (JsonNode)h).ToArray()),
            ["algorithm"] = "sha1",
            ["loaders"] = new JsonArray(Mods.ModrinthLoaders(loader).Select(l => (JsonNode)l).ToArray()),
            ["game_versions"] = new JsonArray(gameVersion),
        }) as JsonObject ?? new JsonObject();
        var known = await IdentifyAsync(hashes.Keys);
        var updates = new List<ModUpdate>();
        foreach (var (sha1, version) in data)
        {
            var file = PrimaryFile(version);
            if (file == null || file.Get("hashes").Str("sha1") == sha1 || !hashes.TryGetValue(sha1, out var mod))
                continue;
            updates.Add(new ModUpdate
            {
                Mod = mod,
                Source = "Modrinth",
                Filename = file.Str("filename"),
                Url = file.Str("url"),
                Sha1 = file.Get("hashes").Str("sha1"),
                Size = file.Get("size") != null ? file.Long("size") : null,
                Version = version.Str("version_number", ""),
            });
        }
        var unknown = hashes.Where(p => !known.ContainsKey(p.Key) && !data.ContainsKey(p.Key))
            .Select(p => p.Value).ToList();
        return (updates, unknown);
    }
}

/// <summary>CurseForge API 客户端（经由 MCIM 镜像）。</summary>
public partial class CurseForgeClient : IModClient
{
    private readonly Downloader _dl;

    public string Name => "CurseForge";

    public CurseForgeClient(Downloader downloader)
    {
        _dl = downloader;
    }

    private Task<JsonNode> GetAsync(string path, IDictionary<string, string> parameters = null)
    {
        var url = Mods.CurseForgeApi + path + (parameters is { Count: > 0 } ? "?" + Web.Query(parameters) : "");
        return _dl.FetchJsonAsync(url, mirror: false);
    }

    public async Task<SearchResult> SearchAsync(string query, string gameVersion, string loader, int offset = 0,
                                                int limit = 20, string index = "relevance", string kind = "mod")
    {
        if (!Mods.CfClasses.TryGetValue(kind, out var classId))
            throw new ArgumentException($"未知的资源类型：{kind}");
        var parameters = new Dictionary<string, string>
        {
            ["gameId"] = Mods.CfGameId.ToString(),
            ["classId"] = classId.ToString(),
            ["index"] = offset.ToString(),
            ["pageSize"] = limit.ToString(),
            ["sortField"] = Mods.CfSortFields.GetValueOrDefault(index ?? "", 2).ToString(),
            ["sortOrder"] = "desc",
        };
        if (!string.IsNullOrEmpty(query))
            parameters["searchFilter"] = query;
        if (!string.IsNullOrEmpty(gameVersion))
            parameters["gameVersion"] = gameVersion;
        var types = kind == "mod" ? Mods.CfLoaderTypes.GetValueOrDefault(loader ?? "") : null;
        if (types is { Length: 1 })
            parameters["modLoaderType"] = types[0].ToString();
        else if (types != null)
            parameters["modLoaderTypes"] = "[" + string.Join(", ", types) + "]";
        var data = await GetAsync("/mods/search", parameters);
        var hits = data.Items("data").Select(mod => new SearchHit
        {
            Id = mod.Get("id")?.AsStr(),
            Slug = mod.Str("slug", ""),
            Title = mod.Str("name", ""),
            Author = string.Join(", ", mod.Items("authors").Take(2).Select(a => a.Str("name"))),
            Description = mod.Str("summary", ""),
            Downloads = mod.Long("downloadCount"),
            IconUrl = mod.Get("logo").Str("thumbnailUrl") ?? "",
            Categories = mod.Items("categories").Select(c => c.Str("name")).ToList(),
            Url = mod.Get("links").Str("websiteUrl") ?? "",
            Raw = mod,
        }).ToList();
        // CurseForge 的分页上限为 10000 条
        var total = Math.Min(data.Get("pagination").Long("totalCount"), 10000 - limit);
        return new SearchResult(hits, total);
    }

    private async Task<List<JsonNode>> FilesAsync(string modId, string gameVersion, string loader, string kind = "mod")
    {
        var types = (kind == "mod" ? Mods.CfLoaderTypes.GetValueOrDefault(loader ?? "") : null)?.Cast<int?>().ToArray()
                    ?? [null];
        foreach (var loaderType in types)
        {
            var parameters = new Dictionary<string, string> { ["pageSize"] = "50" };
            if (!string.IsNullOrEmpty(gameVersion))
                parameters["gameVersion"] = gameVersion;
            if (loaderType != null)
                parameters["modLoaderType"] = loaderType.ToString();
            var files = (await GetAsync($"/mods/{modId}/files", parameters)).Items("data").ToList();
            if (files.Count > 0)
                return files;
        }
        if (loader == "forge" && kind == "mod")
        {
            // 很多老版本 Forge 模组的文件没有标注加载器
            var parameters = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(gameVersion))
                parameters["gameVersion"] = gameVersion;
            parameters["pageSize"] = "50";
            var files = await GetAsync($"/mods/{modId}/files", parameters);
            return files.Items("data").Where(f => !f.Items("gameVersions")
                .Any(v => Mods.CfLoaderTags.Contains((v.AsStr() ?? "").ToLowerInvariant()))).ToList();
        }
        return [];
    }

    /// <summary>最合适的文件（CurseForge 原始数据）：优先最新的正式版；没有可用文件时返回 null。</summary>
    public async Task<JsonNode> BestFileAsync(string modId, string gameVersion, string loader, string kind = "mod")
    {
        var files = (await FilesAsync(modId, gameVersion, loader, kind))
            .Where(f => f.Bool("isAvailable", true))
            .OrderByDescending(f => f.Str("fileDate", ""), StringComparer.Ordinal)
            .ToList();
        if (files.Count == 0)
            return null;
        return files.FirstOrDefault(f => f.Int("releaseType") == 1) ?? files[0];
    }

    [GeneratedRegex(@"^\d")]
    private static partial Regex StartsWithDigit();

    public async Task<List<ModpackVersion>> ModpackVersionsAsync(string projectId)
    {
        var files = (await GetAsync($"/mods/{projectId}/files", new Dictionary<string, string> { ["pageSize"] = "50" }))
            .Items("data").OrderByDescending(f => f.Str("fileDate", ""), StringComparer.Ordinal).ToList();
        var output = new List<ModpackVersion>();
        foreach (var f in files)
        {
            if (!f.Bool("isAvailable", true) || f.Bool("isServerPack"))
                continue;
            var gameVersions = f.Items("gameVersions").Select(v => v.AsStr() ?? "").ToList();
            var mc = gameVersions.Where(v => StartsWithDigit().IsMatch(v)).Take(3);
            var loaders = gameVersions.Where(v => Mods.CfLoaderTags.Contains(v.ToLowerInvariant()));
            var display = f.Str("displayName");
            output.Add(new ModpackVersion
            {
                Name = string.IsNullOrEmpty(display) ? f.Str("fileName") : display,
                Detail = $"Minecraft {string.Join(", ", mc)}  ·  {string.Join(", ", loaders)}",
                Url = DownloadUrl(f),
                Filename = f.Str("fileName"),
                Sha1 = Mods.CfSha1(f),
                Size = f.Get("fileLength") != null ? f.Long("fileLength") : null,
            });
        }
        return output;
    }

    /// <summary>通过 CurseForge 文件指纹检查更新（最多 8 个并发请求）。</summary>
    public async Task<List<ModUpdate>> UpdatesAsync(IEnumerable<LocalFile> mods, string gameVersion, string loader)
    {
        var prints = await Mods.FingerprintFilesAsync(mods);
        if (prints.Count == 0)
            return [];
        var data = await _dl.PostJsonAsync(Mods.CurseForgeApi + "/fingerprints", new JsonObject
        {
            ["fingerprints"] = new JsonArray(prints.Keys.Select(p => (JsonNode)p).ToArray()),
        });
        var matches = data.Get("data").Items("exactMatches")
            .Where(m => m.Get("file") != null && prints.ContainsKey((uint)m.Get("file").Long("fileFingerprint")))
            .ToList();
        var results = new ModUpdate[matches.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, matches.Count),
                                    new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (i, _) =>
        {
            try
            {
                var match = matches[i];
                var current = match.Get("file");
                var best = await BestFileAsync(match.Get("id")?.AsStr(), gameVersion, loader);
                if (best != null && best.Long("id") != current.Long("id")
                    && string.CompareOrdinal(best.Str("fileDate", ""), current.Str("fileDate", "")) > 0)
                {
                    var display = best.Str("displayName");
                    results[i] = new ModUpdate
                    {
                        Mod = prints[(uint)current.Long("fileFingerprint")],
                        Source = "CurseForge",
                        Filename = best.Str("fileName"),
                        Url = DownloadUrl(best),
                        Sha1 = Mods.CfSha1(best),
                        Size = best.Get("fileLength") != null ? best.Long("fileLength") : null,
                        Version = string.IsNullOrEmpty(display) ? best.Str("fileName") : display,
                    };
                }
            }
            catch (Exception)
            {
                // 单个模组检查失败时忽略
            }
        });
        return results.Where(u => u != null).ToList();
    }

    /// <summary>文件下载地址；API 没有给出 downloadUrl 时按 CDN 规则拼接。</summary>
    public static string DownloadUrl(JsonNode file)
    {
        var url = file.Str("downloadUrl");
        if (!string.IsNullOrEmpty(url))
            return url;
        var fileId = file.Long("id");
        return $"{Mods.CurseForgeCdn}{fileId / 1000}/{fileId % 1000}/{Web.Quote(file.Str("fileName"))}";
    }

    public async Task<HashSet<string>> InstalledIdsAsync(IEnumerable<LocalFile> mods)
    {
        var prints = (await Mods.FingerprintFilesAsync(mods)).Keys.ToList();
        if (prints.Count == 0)
            return [];
        JsonNode data;
        try
        {
            data = await _dl.PostJsonAsync(Mods.CurseForgeApi + "/fingerprints", new JsonObject
            {
                ["fingerprints"] = new JsonArray(prints.Select(p => (JsonNode)p).ToArray()),
            });
        }
        catch (Exception)
        {
            return [];
        }
        return data.Get("data").Items("exactMatches").Select(m => m.Get("id")?.AsStr())
            .Where(id => id != null).ToHashSet();
    }

    public async Task<List<string>> InstallAsync(string projectId, string gameVersion, string loader, string modsDir,
                                                 ISet<string> installedProjects, Action<string> log = null,
                                                 string kind = "mod")
    {
        log ??= _ => { };
        Directory.CreateDirectory(modsDir);
        var queue = new Queue<string>([projectId]);
        var installed = new List<string>();
        var seen = new HashSet<string>();
        while (queue.Count > 0)
        {
            var pid = queue.Dequeue();
            if (installedProjects.Contains(pid) || !seen.Add(pid))
                continue;
            var file = await BestFileAsync(pid, gameVersion, loader, kind);
            if (file == null)
            {
                if (pid == projectId)
                    throw new InvalidOperationException("这个模组没有适用于当前版本的文件");
                log($"[警告] 前置模组 {pid} 没有适用于当前版本的文件，已跳过");
                continue;
            }
            var dest = Path.Combine(modsDir, file.Str("fileName"));
            await _dl.DownloadManyAsync([new DownloadTask(DownloadUrl(file), dest, Mods.CfSha1(file),
                                                          file.Long("fileLength"))]);
            installedProjects.Add(pid);
            installed.Add(file.Str("fileName"));
            if (pid != projectId)
                log($"已安装前置模组 {file.Str("fileName")}");
            if (kind != "mod")
                continue;
            foreach (var dep in file.Items("dependencies"))
            {
                if (dep.Int("relationType") == Mods.CfRequiredDependency)
                    queue.Enqueue(dep.Get("modId")?.AsStr());
            }
        }
        return installed;
    }
}
