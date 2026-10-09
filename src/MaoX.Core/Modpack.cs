using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static MaoX.Core.I18n;

namespace MaoX.Core;

public class ModpackException : Exception
{
    public ModpackException(string message, Exception inner = null) : base(message, inner)
    {
    }
}

/// <summary>整合包信息。Format 为 modrinth / curseforge；Loader 为 forge / neoforge / fabric / quilt 或 null。</summary>
public class ModpackManifest
{
    public string Format { get; set; }
    public string Name { get; set; }
    public string Version { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Mc { get; set; }
    public string Loader { get; set; }
    public string LoaderVersion { get; set; }
    public int FileCount { get; set; }
    /// <summary>modrinth.index.json 或 manifest.json 的原始内容。</summary>
    public JsonNode Raw { get; set; }
}

/// <summary>整合包：安装 Modrinth（.mrpack）与 CurseForge 整合包，导出为 .mrpack。</summary>
public static class Modpack
{
    public static readonly IReadOnlyDictionary<int, string> CfClassFolders = new Dictionary<int, string>
    {
        [6] = "mods", [12] = "resourcepacks", [6552] = "shaderpacks",
    };

    public static readonly IReadOnlyList<KeyValuePair<string, string>> MrLoaderKeys =
    [
        new("fabric-loader", "fabric"), new("quilt-loader", "quilt"), new("forge", "forge"), new("neoforge", "neoforge"),
    ];

    /// <summary>导出时可选的内容分组：config / options / resourcepacks / shaderpacks / saves。</summary>
    public static readonly IReadOnlyDictionary<string, string[]> ExportGroups = new Dictionary<string, string[]>
    {
        ["config"] = ["config", "defaultconfigs", "kubejs", "scripts", "global_packs"],
        ["options"] = ["options.txt", "optionsof.txt", "optionsshaders.txt"],
        ["resourcepacks"] = ["resourcepacks"],
        ["shaderpacks"] = ["shaderpacks"],
        ["saves"] = ["saves"],
    };

    private static string MrLoader(string key) => MrLoaderKeys.FirstOrDefault(p => p.Key == key).Value;

    /// <summary>把整合包内的相对路径安全地拼接到 root 下，拒绝 .. 与绝对路径。</summary>
    public static string SafeJoin(string root, string rel)
    {
        rel = (rel ?? "").Replace('\\', '/');
        var parts = rel.Split('/').Where(p => p is not ("" or ".")).ToArray();
        if (parts.Length == 0 || parts.Contains("..") || parts[0].Contains(':'))
            throw new ModpackException(F("整合包中包含不安全的路径：{0}", rel));
        return Path.Combine([root, .. parts]);
    }

    private static JsonNode ReadEntryJson(ZipArchive zip, string name)
    {
        using var reader = new StreamReader(zip.GetEntry(name)!.Open(), new UTF8Encoding(false), true);
        return JsonNode.Parse(reader.ReadToEnd());
    }

    /// <summary>读取整合包信息。不支持或已损坏时抛出 ModpackException。</summary>
    public static ModpackManifest ReadManifest(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var names = zip.Entries.Select(e => e.FullName).ToHashSet();
            if (names.Contains("modrinth.index.json"))
            {
                var index = ReadEntryJson(zip, "modrinth.index.json");
                var deps = index.Obj("dependencies") ?? new JsonObject();
                string loader = null, loaderVersion = null;
                foreach (var (key, value) in deps)
                {
                    if (MrLoader(key) is { } l)
                    {
                        loader = l;
                        loaderVersion = value.AsStr();
                        break;
                    }
                }
                var name = index.Str("name");
                return new ModpackManifest
                {
                    Format = "modrinth",
                    Name = string.IsNullOrEmpty(name) ? "整合包" : name,
                    Version = index.Str("versionId", ""),
                    Summary = index.Str("summary", ""),
                    Mc = deps.Str("minecraft"),
                    Loader = loader,
                    LoaderVersion = loaderVersion,
                    FileCount = index.Arr("files")?.Count ?? 0,
                    Raw = index,
                };
            }
            if (names.Contains("manifest.json"))
            {
                var manifest = ReadEntryJson(zip, "manifest.json");
                if (manifest.Str("manifestType") != "minecraftModpack")
                    throw new ModpackException(T("不支持的 CurseForge 整合包类型"));
                var minecraft = manifest.Get("minecraft");
                var loaders = minecraft.Items("modLoaders").ToList();
                var primary = loaders.FirstOrDefault(l => l.Bool("primary")) ?? loaders.FirstOrDefault();
                string loader = null, loaderVersion = null;
                if (primary != null)
                {
                    var id = primary.Str("id") ?? throw new KeyNotFoundException("'id'");
                    var dash = id.IndexOf('-');
                    loader = dash >= 0 ? id[..dash] : id;
                    loaderVersion = dash >= 0 ? id[(dash + 1)..] : "";
                }
                var name = manifest.Str("name");
                return new ModpackManifest
                {
                    Format = "curseforge",
                    Name = string.IsNullOrEmpty(name) ? "整合包" : name,
                    Version = manifest.Str("version", ""),
                    Summary = manifest.Str("author", ""),
                    Mc = minecraft.Str("version"),
                    Loader = loader,
                    LoaderVersion = loaderVersion,
                    FileCount = manifest.Arr("files")?.Count ?? 0,
                    Raw = manifest,
                };
            }
        }
        catch (Exception e) when (e is InvalidDataException or JsonException or KeyNotFoundException
                                      or InvalidOperationException)
        {
            throw new ModpackException(F("整合包文件已损坏：{0}", e.Message), e);
        }
        throw new ModpackException(T("不是支持的整合包（支持 Modrinth .mrpack 与 CurseForge 整合包 .zip）"));
    }

    private static async Task<LoaderItem> LoaderItemAsync(LoaderInstaller installer, string loader, string mc,
                                                          string version)
    {
        if (loader is "fabric" or "quilt")
            return new LoaderItem { Version = version, Display = version };
        foreach (var item in await installer.ListVersionsAsync(loader, mc))
        {
            if (version == item.Version || version == item.Display || item.Version.EndsWith("-" + version))
                return item;
        }
        if (loader == "forge")
            return new LoaderItem { Version = $"{mc}-{version}", Display = version };
        return new LoaderItem { Version = version, Display = version, Artifact = mc == "1.20.1" ? "forge" : "neoforge" };
    }

    /// <summary>安装原版与加载器，并生成以整合包命名的版本。</summary>
    private static async Task CreateVersionAsync(GameLauncher gl, ModpackManifest info, string name)
    {
        var mc = info.Mc;
        var loader = info.Loader;
        if (string.IsNullOrEmpty(mc))
            throw new ModpackException(T("整合包没有指定 Minecraft 版本"));
        if (!string.IsNullOrEmpty(loader) && !Loaders.All.Contains(loader))
            throw new ModpackException(F("不支持的模组加载器：{0}", loader));
        await gl.EnsureVersionJsonAsync(mc);
        var @base = mc;
        var created = false;
        if (!string.IsNullOrEmpty(loader))
        {
            var installer = new LoaderInstaller(gl);
            var before = gl.InstalledVersions().ToHashSet();
            @base = await installer.InstallAsync(loader, mc, await LoaderItemAsync(installer, loader, mc, info.LoaderVersion));
            created = !before.Contains(@base);
        }
        var data = (JsonObject)Json.ReadFile(gl.VersionJsonPath(@base));
        data["id"] = name;
        Json.WriteFile(gl.VersionJsonPath(name), data);
        if (created)
            Instance.DeleteTree(gl.VersionDir(@base));
        gl.SaveVersionSettings(name, new JsonObject
        {
            ["isolation"] = true,
            ["modpack"] = new JsonObject
            {
                ["name"] = info.Name, ["version"] = info.Version, ["format"] = info.Format,
            },
        });
    }

    private static List<DownloadTask> ModrinthTasks(JsonNode index, string gameDir)
    {
        var tasks = new List<DownloadTask>();
        foreach (var f in index.Items("files"))
        {
            var urls = f.Items("downloads").Select(u => u.AsStr()).ToList();
            if (f.Get("env").Str("client") == "unsupported" || urls.Count == 0)
                continue;
            tasks.Add(new DownloadTask(urls[0], SafeJoin(gameDir, f.Str("path")), f.Get("hashes").Str("sha1"),
                                       f.Long("fileSize"), urls.Skip(1)));
        }
        return tasks;
    }

    private static async Task<List<DownloadTask>> CurseForgeTasksAsync(GameLauncher gl, JsonNode manifest,
                                                                       string gameDir, Action<string> log)
    {
        var ids = manifest.Items("files").Where(f => f.Bool("required", true)).Select(f => f.Long("fileID")).ToList();
        var files = new List<JsonNode>();
        foreach (var chunk in ids.Chunk(500))
        {
            var data = await gl.Dl.PostJsonAsync(Mods.CurseForgeApi + "/mods/files", new JsonObject
            {
                ["fileIds"] = new JsonArray(chunk.Select(i => (JsonNode)i).ToArray()),
            });
            files.AddRange(data.Items("data"));
        }
        var modIds = files.Select(f => f.Long("modId")).Distinct().Order().ToList();
        var classes = new Dictionary<long, int>();
        foreach (var chunk in modIds.Chunk(500))
        {
            var data = await gl.Dl.PostJsonAsync(Mods.CurseForgeApi + "/mods", new JsonObject
            {
                ["modIds"] = new JsonArray(chunk.Select(i => (JsonNode)i).ToArray()),
            });
            foreach (var mod in data.Items("data"))
                classes[mod.Long("id")] = mod.Int("classId");
        }
        var missing = ids.ToHashSet().Except(files.Select(f => f.Long("id"))).Count();
        if (missing > 0)
            log(F("[警告] 有 {0} 个文件在 CurseForge 上已不存在，已跳过", missing));
        var tasks = new List<DownloadTask>();
        foreach (var f in files)
        {
            var folder = CfClassFolders.GetValueOrDefault(classes.GetValueOrDefault(f.Long("modId")), "mods");
            tasks.Add(new DownloadTask(CurseForgeClient.DownloadUrl(f), SafeJoin(gameDir, folder + "/" + f.Str("fileName")),
                                       Mods.CfSha1(f), f.Long("fileLength")));
        }
        return tasks;
    }

    private static int ExtractOverrides(string path, IReadOnlyList<string> prefixes, string gameDir)
    {
        var count = 0;
        using var zip = ZipFile.OpenRead(path);
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName;
            var prefix = prefixes.FirstOrDefault(p => name.StartsWith(p + "/", StringComparison.Ordinal));
            if (prefix == null || name.EndsWith('/'))
                continue;
            var dest = SafeJoin(gameDir, name[(prefix.Length + 1)..]);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, true);
            count++;
        }
        return count;
    }

    /// <summary>安装整合包，返回新版本的 id。失败时删除已创建的版本文件夹。</summary>
    public static async Task<string> InstallAsync(GameLauncher gl, string path, string name, Action<string> log = null,
                                                  Action<int, int, string> progress = null)
    {
        log ??= _ => { };
        var info = await Task.Run(() => ReadManifest(path));
        name = Instance.CheckName(gl, name);
        var loaderText = !string.IsNullOrEmpty(info.Loader) ? F("，{0} {1}", info.Loader, info.LoaderVersion) : "";
        log(F("正在安装整合包 {0}（Minecraft {1}{2}）", info.Name, info.Mc, loaderText));
        try
        {
            await CreateVersionAsync(gl, info, name);
            var gameDir = gl.GameDirFor(name);
            List<DownloadTask> tasks;
            List<string> prefixes;
            if (info.Format == "modrinth")
            {
                tasks = ModrinthTasks(info.Raw, gameDir);
                prefixes = ["overrides", "client-overrides"];
            }
            else
            {
                tasks = await CurseForgeTasksAsync(gl, info.Raw, gameDir, log);
                var overrides = info.Raw.Str("overrides");
                prefixes = [string.IsNullOrEmpty(overrides) ? "overrides" : overrides];
            }
            log(F("正在下载整合包中的 {0} 个文件...", tasks.Count));
            await gl.Dl.DownloadManyAsync(tasks, (d, t) => progress?.Invoke(d, t, T("下载整合包文件")));
            var extracted = await Task.Run(() => ExtractOverrides(path, prefixes, gameDir));
            log(F("已解压 {0} 个配置文件", extracted));
            await gl.PrepareAsync(name);
        }
        catch
        {
            Instance.DeleteTree(gl.VersionDir(name));
            throw;
        }
        return name;
    }

    // ------------------------------------------------------------------ 导出

    private static IEnumerable<string> WalkFiles(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories);

    private static string RelTo(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>
    /// 导出为 Modrinth 整合包。能在 Modrinth 上找到的文件只记录下载地址，其余文件放进 overrides。
    /// include 为 ExportGroups 中的键。返回 (在线文件数, 打包文件数)。
    /// </summary>
    public static async Task<(int Online, int Packed)> ExportMrpackAsync(
        GameLauncher gl, string versionId, string dest, string name, string packVersion, string summary,
        IReadOnlyCollection<string> include, Action<string> log = null, Action<int, int, string> progress = null)
    {
        log ??= _ => { };
        var (loader, loaderVersion, mc) = gl.DetectLoader(versionId);
        if (loader == "optifine")
            throw new ModpackException(T("OptiFine 独立版本无法导出为整合包，请改用 Forge + OptiFine"));
        var deps = new JsonObject { ["minecraft"] = mc };
        if (!string.IsNullOrEmpty(loader))
        {
            var key = MrLoaderKeys.First(p => p.Value == loader).Key;
            deps[key] = loaderVersion;
        }
        var gameDir = gl.GameDirFor(versionId);

        var candidates = new List<(string Rel, string Path)>();
        foreach (var (folder, ext) in new[] { ("mods", ".jar"), ("resourcepacks", ".zip"), ("shaderpacks", ".zip") })
        {
            if (folder != "mods" && !include.Contains(folder))
                continue;
            var root = Path.Combine(gameDir, folder);
            if (!Directory.Exists(root))
                continue;
            foreach (var f in Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName)
                         .Order(StringComparer.Ordinal))
            {
                if (f.EndsWith(ext, StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(root, f)))
                    candidates.Add((folder + "/" + f, Path.Combine(root, f)));
            }
        }
        log(F("正在识别 {0} 个文件...", candidates.Count));
        var hashes = await Task.Run(() => candidates.ToDictionary(c => c.Rel, c => Http.FileSha1(c.Path)));
        JsonObject found;
        try
        {
            found = hashes.Count > 0 ? await new ModrinthClient(gl.Dl).IdentifyAsync(hashes.Values) : new JsonObject();
        }
        catch (Exception e)
        {
            log(F("[警告] 无法连接 Modrinth，所有文件将直接打包：{0}", e.Message));
            found = new JsonObject();
        }

        var files = new JsonArray();
        var overrides = new List<(string Rel, string Path)>();
        foreach (var (rel, path) in candidates)
        {
            var sha1 = hashes[rel];
            var version = found.Get(sha1);
            var file = version.Items("files").FirstOrDefault(f => f.Get("hashes").Str("sha1") == sha1);
            var url = file.Str("url");
            if (!string.IsNullOrEmpty(url))
            {
                files.Add(new JsonObject
                {
                    ["path"] = rel,
                    ["hashes"] = new JsonObject { ["sha1"] = sha1, ["sha512"] = Http.FileSha512(path) },
                    ["env"] = new JsonObject { ["client"] = "required", ["server"] = "required" },
                    ["downloads"] = new JsonArray(url),
                    ["fileSize"] = new FileInfo(path).Length,
                });
            }
            else
            {
                overrides.Add((rel, path));
            }
        }
        foreach (var group in include)
        {
            if (group is "resourcepacks" or "shaderpacks" || !ExportGroups.TryGetValue(group, out var entries))
                continue;
            foreach (var entry in entries)
            {
                var path = Path.Combine(gameDir, entry);
                if (File.Exists(path))
                {
                    overrides.Add((entry, path));
                }
                else if (Directory.Exists(path))
                {
                    foreach (var full in WalkFiles(path))
                    {
                        if (Path.GetFileName(full) != "session.lock")
                            overrides.Add((RelTo(gameDir, full), full));
                    }
                }
            }
        }
        // 资源包、光影文件夹中的非 zip 内容（解压的资源包）也要带上
        foreach (var folder in new[] { "resourcepacks", "shaderpacks" })
        {
            var root = Path.Combine(gameDir, folder);
            if (!include.Contains(folder) || !Directory.Exists(root))
                continue;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                foreach (var p in WalkFiles(dir))
                    overrides.Add((RelTo(gameDir, p), p));
            }
        }

        var index = new JsonObject
        {
            ["formatVersion"] = 1,
            ["game"] = "minecraft",
            ["versionId"] = string.IsNullOrEmpty(packVersion) ? "1.0.0" : packVersion,
            ["name"] = name,
            ["summary"] = summary,
            ["files"] = files,
            ["dependencies"] = deps,
        };
        var tmp = dest + ".part";
        await Task.Run(() =>
        {
            try
            {
                using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
                {
                    var indexEntry = zip.CreateEntry("modrinth.index.json", CompressionLevel.Optimal);
                    using (var writer = new StreamWriter(indexEntry.Open(), new UTF8Encoding(false)))
                        writer.Write(Json.Serialize(index, true));
                    for (var i = 1; i <= overrides.Count; i++)
                    {
                        var (rel, path) = overrides[i - 1];
                        zip.CreateEntryFromFile(path, "overrides/" + rel, CompressionLevel.Optimal);
                        if (progress != null && (i % 20 == 0 || i == overrides.Count))
                            progress(i, overrides.Count, T("打包文件"));
                    }
                }
                File.Move(tmp, dest, true);
            }
            finally
            {
                if (File.Exists(tmp))
                    File.Delete(tmp);
            }
        });
        log(F("整合包已导出：{0}（{1} 个在线文件，{2} 个打包文件）", dest, files.Count, overrides.Count));
        return (files.Count, overrides.Count);
    }
}
