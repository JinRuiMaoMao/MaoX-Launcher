using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static MaoX.Core.I18n;

namespace MaoX.Core;

/// <summary>存档信息。LastPlayed 为本地时间。</summary>
public class WorldInfo
{
    public string Folder { get; set; }
    public string Path { get; set; }
    public string Name { get; set; }
    /// <summary>生存 / 创造 / 冒险 / 旁观，未知时为空字符串。</summary>
    public string Mode { get; set; } = "";
    public string Version { get; set; } = "";
    public DateTime LastPlayed { get; set; }
    public bool Hardcore { get; set; }
    /// <summary>icon.png 路径，没有图标时为 null。</summary>
    public string Icon { get; set; }
}

/// <summary>版本管理：重命名、复制、删除，以及存档列表与备份。</summary>
public static partial class Instance
{
    [GeneratedRegex("[\\\\/:*?\"<>|]")]
    private static partial Regex InvalidName();

    public static readonly IReadOnlyDictionary<int, string> GameModes = new Dictionary<int, string>
    {
        [0] = T("生存"), [1] = T("创造"), [2] = T("冒险"), [3] = T("旁观"),
    };

    /// <summary>检查新版本名是否可用，返回去掉首尾空白的名称；不可用时抛出 ArgumentException。</summary>
    public static string CheckName(GameLauncher gl, string name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0)
            throw new ArgumentException(T("名称不能为空"));
        if (InvalidName().IsMatch(name) || name.EndsWith('.') || name is "." or "..")
            throw new ArgumentException(T("名称不能包含 \\ / : * ? \" < > | 等字符"));
        var path = gl.PathOf("versions", name);
        if (Directory.Exists(path) || File.Exists(path))
            throw new ArgumentException(F("已经存在名为「{0}」的版本", name));
        return name;
    }

    /// <summary>继承自该版本的其他版本。</summary>
    public static List<string> Dependents(GameLauncher gl, string versionId)
    {
        var output = new List<string>();
        foreach (var vid in gl.InstalledVersions())
        {
            var data = Json.TryReadFile(gl.VersionJsonPath(vid));
            if (data != null && data.Str("inheritsFrom") == versionId)
                output.Add(vid);
        }
        return output;
    }

    /// <summary>把版本文件夹中以旧名称命名的文件改成新名称，并更新 JSON 中的 id。</summary>
    private static void Retarget(string folder, string old, string @new)
    {
        foreach (var ext in new[] { ".json", ".jar" })
        {
            var src = Path.Combine(folder, old + ext);
            if (File.Exists(src))
                File.Move(src, Path.Combine(folder, @new + ext), true);
        }
        var jsonPath = Path.Combine(folder, @new + ".json");
        var data = (JsonObject)Json.ReadFile(jsonPath);
        data["id"] = @new;
        if (data.Str("jar") == old)
            data["jar"] = @new;
        Json.WriteFile(jsonPath, data);
    }

    public static string Rename(GameLauncher gl, string old, string @new)
    {
        @new = CheckName(gl, @new);
        var children = Dependents(gl, old);
        Directory.Move(gl.PathOf("versions", old), gl.PathOf("versions", @new));
        Retarget(gl.PathOf("versions", @new), old, @new);
        foreach (var child in children)
        {
            var path = gl.VersionJsonPath(child);
            var data = (JsonObject)Json.ReadFile(path);
            data["inheritsFrom"] = @new;
            if (data.Str("jar") == old)
                data["jar"] = @new;
            Json.WriteFile(path, data);
        }
        return @new;
    }

    /// <summary>复制版本（不含 natives），返回新版本 id。progress(已完成, 总数, "复制版本")。</summary>
    public static Task<string> DuplicateAsync(GameLauncher gl, string src, string @new,
                                              Action<int, int, string> progress = null) => Task.Run(() =>
    {
        @new = CheckName(gl, @new);
        var srcDir = gl.PathOf("versions", src);
        var dstDir = gl.PathOf("versions", @new);
        var files = Directory.GetFiles(srcDir, "*", SearchOption.AllDirectories);
        var total = files.Length;
        try
        {
            for (var i = 1; i <= total; i++)
            {
                var path = files[i - 1];
                var rel = Path.GetRelativePath(srcDir, path);
                if (rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0] == "natives")
                    continue;
                var dest = Path.Combine(dstDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(path, dest, true);
                File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(path));
                if (progress != null && (i % 20 == 0 || i == total))
                    progress(i, total, T("复制版本"));
            }
            Retarget(dstDir, src, @new);
        }
        catch
        {
            DeleteTree(dstDir);
            throw;
        }
        return @new;
    });

    public static Task DeleteAsync(GameLauncher gl, string versionId) =>
        Task.Run(() => DeleteTree(gl.PathOf("versions", versionId), true));

    /// <summary>递归删除文件夹；遇到只读文件时去掉只读属性后重试。</summary>
    internal static void DeleteTree(string path, bool throwOnError = false)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                if (throwOnError)
                    throw new DirectoryNotFoundException(path);
                return;
            }
            try
            {
                Directory.Delete(path, true);
            }
            catch (UnauthorizedAccessException)
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(path, true);
            }
        }
        catch (Exception e) when (!throwOnError && e is IOException or UnauthorizedAccessException)
        {
        }
    }

    // ------------------------------------------------------------------ 存档

    /// <summary>
    /// 极简 NBT 解析（只用于读取 level.dat），返回根复合标签的内容。
    /// 值类型：sbyte / short / int / long / float / double / string / List&lt;object&gt; /
    /// Dictionary&lt;string, object&gt;，数组类型（byte[] / int[] / long[]）为 null。
    /// </summary>
    public static Dictionary<string, object> ReadNbt(byte[] data)
    {
        var pos = 0;

        ReadOnlySpan<byte> Take(int size)
        {
            if (size < 0 || pos + size > data.Length)
                throw new InvalidDataException(T("NBT 数据不完整"));
            var span = data.AsSpan(pos, size);
            pos += size;
            return span;
        }

        string ReadString()
        {
            var length = BinaryPrimitives.ReadUInt16BigEndian(Take(2));
            return Encoding.UTF8.GetString(Take(length));
        }

        object Payload(int tag)
        {
            switch (tag)
            {
                case 1:
                    return (sbyte)Take(1)[0];
                case 2:
                    return BinaryPrimitives.ReadInt16BigEndian(Take(2));
                case 3:
                    return BinaryPrimitives.ReadInt32BigEndian(Take(4));
                case 4:
                    return BinaryPrimitives.ReadInt64BigEndian(Take(8));
                case 5:
                    return BinaryPrimitives.ReadSingleBigEndian(Take(4));
                case 6:
                    return BinaryPrimitives.ReadDoubleBigEndian(Take(8));
                case 7 or 11 or 12:
                {
                    var length = BinaryPrimitives.ReadInt32BigEndian(Take(4));
                    Take(checked(length * (tag == 7 ? 1 : tag == 11 ? 4 : 8)));
                    return null;
                }
                case 8:
                    return ReadString();
                case 9:
                {
                    var item = (sbyte)Take(1)[0];
                    var length = BinaryPrimitives.ReadInt32BigEndian(Take(4));
                    var list = new List<object>();
                    for (var i = 0; i < length; i++)
                        list.Add(Payload(item));
                    return list;
                }
                case 10:
                {
                    var output = new Dictionary<string, object>();
                    while (true)
                    {
                        var child = (sbyte)Take(1)[0];
                        if (child == 0)
                            return output;
                        var name = ReadString();
                        output[name] = Payload(child);
                    }
                }
                default:
                    throw new InvalidDataException(F("未知的 NBT 类型 {0}", tag));
            }
        }

        if ((sbyte)Take(1)[0] != 10)
            throw new InvalidDataException(T("不是 NBT 复合标签"));
        ReadString();
        return (Dictionary<string, object>)Payload(10);
    }

    private static long? NbtNumber(object value) => value switch
    {
        sbyte b => b,
        short s => s,
        int i => i,
        long l => l,
        _ => null,
    };

    /// <summary>游戏目录下 saves 中的存档，按最后游玩时间降序。</summary>
    public static List<WorldInfo> ListWorlds(string gameDir)
    {
        var saves = Path.Combine(gameDir, "saves");
        if (!Directory.Exists(saves))
            return [];
        var worlds = new List<WorldInfo>();
        foreach (var path in Directory.EnumerateDirectories(saves))
        {
            var folder = Path.GetFileName(path);
            var level = Path.Combine(path, "level.dat");
            if (!File.Exists(level))
                continue;
            var icon = Path.Combine(path, "icon.png");
            var info = new WorldInfo
            {
                Folder = folder,
                Path = path,
                Name = folder,
                LastPlayed = File.GetLastWriteTime(level),
                Icon = File.Exists(icon) ? icon : null,
            };
            try
            {
                byte[] raw;
                using (var file = File.OpenRead(level))
                using (var gzip = new GZipStream(file, CompressionMode.Decompress))
                using (var buffer = new MemoryStream())
                {
                    gzip.CopyTo(buffer);
                    raw = buffer.ToArray();
                }
                var data = ReadNbt(raw).GetValueOrDefault("Data") as Dictionary<string, object> ?? [];
                var levelName = data.GetValueOrDefault("LevelName") as string;
                info.Name = string.IsNullOrEmpty(levelName) ? folder : levelName;
                var mode = NbtNumber(data.GetValueOrDefault("GameType"));
                info.Mode = mode != null && GameModes.TryGetValue((int)mode, out var m) ? m : "";
                info.Hardcore = (NbtNumber(data.GetValueOrDefault("hardcore")) ?? 0) != 0;
                info.Version = (data.GetValueOrDefault("Version") as Dictionary<string, object>)
                               ?.GetValueOrDefault("Name") as string ?? "";
                var lastPlayed = NbtNumber(data.GetValueOrDefault("LastPlayed"));
                if (lastPlayed is { } ms and not 0)
                    info.LastPlayed = DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException
                                          or ArgumentException or OverflowException)
            {
            }
            worlds.Add(info);
        }
        return worlds.OrderByDescending(w => w.LastPlayed).ToList();
    }

    /// <summary>存档 datapacks 文件夹中的 zip 与带 pack.mcmeta 的文件夹名，按名称排序。</summary>
    public static List<string> ListDatapacks(string worldPath)
    {
        var folder = Path.Combine(worldPath, "datapacks");
        if (!Directory.Exists(folder))
            return [];
        return Directory.EnumerateFileSystemEntries(folder)
            .Select(Path.GetFileName)
            .Where(f => f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                        || File.Exists(Path.Combine(folder, f, "pack.mcmeta")))
            .OrderBy(f => f.ToLowerInvariant(), StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>把存档打包到 &lt;游戏目录&gt;/backups，返回备份文件路径。progress(已完成, 总数, "备份存档")。</summary>
    public static Task<string> BackupWorldAsync(string gameDir, WorldInfo world,
                                                Action<int, int, string> progress = null) => Task.Run(() =>
    {
        var backups = Path.Combine(gameDir, "backups");
        Directory.CreateDirectory(backups);
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var dest = Path.Combine(backups, $"{world.Folder}_{stamp}.zip");
        var files = Directory.GetFiles(world.Path, "*", SearchOption.AllDirectories);
        var tmp = dest + ".part";
        try
        {
            using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
            {
                for (var i = 1; i <= files.Length; i++)
                {
                    var path = files[i - 1];
                    if (Path.GetFileName(path) == "session.lock")
                        continue;
                    var rel = Path.GetRelativePath(world.Path, path).Replace(Path.DirectorySeparatorChar, '/');
                    zip.CreateEntryFromFile(path, world.Folder + "/" + rel, CompressionLevel.Optimal);
                    if (progress != null && (i % 20 == 0 || i == files.Length))
                        progress(i, files.Length, T("备份存档"));
                }
            }
            File.Move(tmp, dest, true);
        }
        finally
        {
            if (File.Exists(tmp))
                File.Delete(tmp);
        }
        return dest;
    });
}
