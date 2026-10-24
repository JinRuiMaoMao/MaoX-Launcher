using System.IO.Compression;
using static MaoX.Core.I18n;

namespace MaoX.Core;

/// <summary>一份实例快照（模组 / 配置，不含存档）。</summary>
public record InstanceBackup(string Path, DateTime Time, long Size);

/// <summary>当前实例相对备份的差异。Kind 为 added / removed / changed。</summary>
public record FileChange(string Rel, string Kind, long CurrentSize, long BackupSize);

/// <summary>改模组或配置前的实例备份，以及和当前文件的差异预览。</summary>
public static class InstanceSnapshot
{
    public const string Folder = "maox-snapshots";
    private const string StampFormat = "yyyy-MM-dd_HH-mm-ss";

    public static readonly string[] FileEntries =
        ["options.txt", "optionsof.txt", "optionsshaders.txt", "servers.dat"];

    public static readonly string[] DirEntries =
        ["mods", "config", "defaultconfigs", "kubejs", "scripts", "resourcepacks", "shaderpacks"];

    public static IEnumerable<(string Rel, string Path)> ListFiles(string gameDir)
    {
        foreach (var name in FileEntries)
        {
            var path = Path.Combine(gameDir, name);
            if (File.Exists(path))
                yield return (name.Replace('\\', '/'), path);
        }
        foreach (var dir in DirEntries)
        {
            var root = Path.Combine(gameDir, dir);
            if (!Directory.Exists(root))
                continue;
            foreach (var full in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(full) == "session.lock")
                    continue;
                var rel = Path.GetRelativePath(gameDir, full).Replace(Path.DirectorySeparatorChar, '/');
                yield return (rel, full);
            }
        }
    }

    public static Task<string> BackupAsync(string gameDir, Action<int, int, string> progress = null) => Task.Run(() =>
    {
        var folder = Path.Combine(gameDir, Folder);
        Directory.CreateDirectory(folder);
        var dest = Path.Combine(folder, DateTime.Now.ToString(StampFormat) + ".zip");
        var files = ListFiles(gameDir).ToList();
        var tmp = dest + ".part";
        try
        {
            using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
            {
                for (var i = 1; i <= files.Count; i++)
                {
                    var (rel, path) = files[i - 1];
                    zip.CreateEntryFromFile(path, rel, CompressionLevel.Optimal);
                    if (progress != null && (i % 20 == 0 || i == files.Count))
                        progress(i, files.Count, T("备份实例"));
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

    public static List<InstanceBackup> List(string gameDir)
    {
        var folder = Path.Combine(gameDir, Folder);
        if (!Directory.Exists(folder))
            return [];
        var result = new List<InstanceBackup>();
        foreach (var path in Directory.EnumerateFiles(folder, "*.zip"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (!DateTime.TryParseExact(name, StampFormat, null, System.Globalization.DateTimeStyles.None, out var time))
                continue;
            result.Add(new InstanceBackup(path, time, new FileInfo(path).Length));
        }
        return result.OrderByDescending(b => b.Time).ToList();
    }

    /// <summary>当前文件相对备份：新增、删除、内容变化。</summary>
    public static List<FileChange> Diff(string zipPath, string gameDir)
    {
        var current = ListFiles(gameDir).ToDictionary(f => f.Rel, f => f.Path, StringComparer.OrdinalIgnoreCase);
        var backup = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/') || entry.FullName.Contains(".."))
                    continue;
                backup[entry.FullName.Replace('\\', '/')] = entry.Length;
            }
        }
        var changes = new List<FileChange>();
        foreach (var (rel, path) in current)
        {
            var size = new FileInfo(path).Length;
            if (!backup.TryGetValue(rel, out var old))
                changes.Add(new FileChange(rel, "added", size, 0));
            else if (old != size || !SameBytes(path, zipPath, rel))
                changes.Add(new FileChange(rel, "changed", size, old));
        }
        foreach (var (rel, old) in backup)
        {
            if (!current.ContainsKey(rel))
                changes.Add(new FileChange(rel, "removed", 0, old));
        }
        return changes.OrderBy(c => c.Kind).ThenBy(c => c.Rel, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static async Task RestoreAsync(string gameDir, InstanceBackup backup, Action<int, int, string> progress = null)
    {
        await BackupAsync(gameDir, progress);
        await Task.Run(() =>
        {
            using var zip = ZipFile.OpenRead(backup.Path);
            for (var i = 0; i < zip.Entries.Count; i++)
            {
                var entry = zip.Entries[i];
                var rel = entry.FullName.Replace('\\', '/');
                if (rel.EndsWith('/') || rel.Contains("..") || rel.Contains(':'))
                    continue;
                var target = Path.Combine(gameDir, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, true);
                if (progress != null && (i % 20 == 0 || i == zip.Entries.Count - 1))
                    progress(i + 1, zip.Entries.Count, T("还原实例"));
            }
        });
    }

    private static bool SameBytes(string currentPath, string zipPath, string rel)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var entry = zip.GetEntry(rel) ?? zip.Entries.FirstOrDefault(e =>
                e.FullName.Replace('\\', '/').Equals(rel, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
                return false;
            using var a = File.OpenRead(currentPath);
            using var b = entry.Open();
            var left = new byte[81920];
            var right = new byte[81920];
            while (true)
            {
                var n = a.Read(left);
                var m = b.Read(right);
                if (n != m)
                    return false;
                if (n == 0)
                    return true;
                if (!left.AsSpan(0, n).SequenceEqual(right.AsSpan(0, m)))
                    return false;
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static string KindText(string kind) => kind switch
    {
        "added" => T("新增"),
        "removed" => T("已删除"),
        _ => T("已修改"),
    };
}
