using System.Diagnostics;
using System.Text.RegularExpressions;
using static MaoX.Core.I18n;

namespace MaoX.Core;

public record JavaInfo(string Path, int Major);

/// <summary>查找本机 Java、识别版本，以及从 Mojang 下载官方 Java 运行时。</summary>
public static partial class JavaManager
{
    private const string RuntimeManifestUrl =
        "https://launchermeta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json";

    [GeneratedRegex("JAVA_VERSION=\"([^\"]+)\"")]
    private static partial Regex ReleaseVersion();

    [GeneratedRegex("OS_ARCH=\"([^\"]+)\"")]
    private static partial Regex ReleaseArch();

    [GeneratedRegex("version \"([^\"]+)\"")]
    private static partial Regex OutputVersion();

    public static int? ParseMajor(string version)
    {
        var parts = version.Split('.');
        if (parts[0] == "1" && parts.Length > 1)
            parts = parts[1..];
        var m = Regex.Match(parts[0], @"\d+");
        return m.Success ? int.Parse(m.Value) : null;
    }

    /// <summary>返回 Java 主版本号（如 8、17、21），无法识别时返回 null。</summary>
    public static int? JavaVersion(string javaPath)
    {
        var home = Path.GetDirectoryName(Path.GetDirectoryName(javaPath));
        var release = Path.Combine(home ?? "", "release");
        if (File.Exists(release))
        {
            try
            {
                var m = ReleaseVersion().Match(File.ReadAllText(release));
                if (m.Success)
                    return ParseMajor(m.Groups[1].Value);
            }
            catch (IOException)
            {
            }
        }

        var exe = javaPath;
        if (Platform.IsWindows && Path.GetFileName(javaPath).Equals("javaw.exe", StringComparison.OrdinalIgnoreCase))
        {
            var console = Path.Combine(Path.GetDirectoryName(javaPath)!, "java.exe");
            if (File.Exists(console))
                exe = console;
        }
        try
        {
            var psi = new ProcessStartInfo(exe, "-version")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi)!;
            var stderr = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(15000))
            {
                process.Kill();
                return null;
            }
            var m = OutputVersion().Match(stderr.Result + stdout.Result);
            return m.Success ? ParseMajor(m.Groups[1].Value) : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException
                                      or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool Is32BitJava(string home)
    {
        var release = Path.Combine(home, "release");
        if (File.Exists(release))
        {
            try
            {
                var m = ReleaseArch().Match(File.ReadAllText(release));
                if (m.Success)
                    return m.Groups[1].Value.ToLowerInvariant() is "x86" or "i386" or "i586" or "i686";
            }
            catch (IOException)
            {
            }
        }
        return home.Contains("(x86)");
    }

    private static IEnumerable<string> Glob(string pattern)
    {
        // 只支持路径中以单独的 * 表示一级任意目录
        var parts = pattern.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        IEnumerable<string> current = [Platform.IsWindows ? parts[0] + Path.DirectorySeparatorChar : "/"];
        foreach (var part in parts.Skip(1))
        {
            if (part.Length == 0)
                continue;
            current = part == "*"
                ? current.SelectMany(dir =>
                {
                    try
                    {
                        return Directory.Exists(dir) ? Directory.GetDirectories(dir) : [];
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        return [];
                    }
                })
                : current.Select(dir => Path.Combine(dir, part)).Where(Directory.Exists);
        }
        return current.Where(Directory.Exists).ToList();
    }

    private static List<string> CandidateHomes(IEnumerable<string> runtimeDirs)
    {
        var homes = new List<string>();
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrEmpty(javaHome))
            homes.Add(javaHome);
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var exe = Path.Combine(dir.Trim('"'), Platform.IsWindows ? "java.exe" : "java");
                if (!File.Exists(exe))
                    continue;
                var real = new FileInfo(exe).ResolveLinkTarget(true)?.FullName ?? exe;
                homes.Add(Path.GetDirectoryName(Path.GetDirectoryName(real))!);
            }
            catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
            {
            }
        }

        var patterns = new List<string>();
        if (Platform.IsWindows)
        {
            var roots = new[] { "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432" }
                .Select(Environment.GetEnvironmentVariable).Where(r => !string.IsNullOrEmpty(r)).Distinct();
            string[] vendors = ["Java", "Eclipse Adoptium", "Eclipse Foundation", "AdoptOpenJDK", "Microsoft",
                                "Zulu", "BellSoft", "Amazon Corretto", "Semeru", "Oracle"];
            foreach (var root in roots)
            {
                patterns.AddRange(vendors.Select(v => Path.Combine(root, v, "*")));
                patterns.Add(Path.Combine(root, "Minecraft Launcher", "runtime", "*", "*", "*"));
            }
            var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (!string.IsNullOrEmpty(local))
                patterns.Add(Path.Combine(local, "Packages", "Microsoft.4297127D64EC6_8wekyb3d8bbwe", "LocalCache",
                                          "Local", "runtime", "*", "*", "*"));
        }
        else if (Platform.IsMac)
        {
            patterns.Add("/Library/Java/JavaVirtualMachines/*/Contents/Home");
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            patterns.Add(Path.Combine(home, "Library", "Java", "JavaVirtualMachines", "*", "Contents", "Home"));
            patterns.Add("/opt/homebrew/opt/*/libexec/openjdk.jdk/Contents/Home");
            patterns.Add("/usr/local/opt/*/libexec/openjdk.jdk/Contents/Home");
        }
        else
        {
            patterns.Add("/usr/lib/jvm/*");
        }
        foreach (var dir in runtimeDirs)
        {
            patterns.Add(Path.Combine(dir, "*"));
            patterns.Add(Path.Combine(dir, "*", "jre.bundle", "Contents", "Home"));
        }
        foreach (var pattern in patterns)
            homes.AddRange(Glob(pattern));
        return homes;
    }

    /// <summary>扫描本机已安装的 Java，按版本号降序返回。</summary>
    public static List<JavaInfo> FindJava(IEnumerable<string> runtimeDirs = null)
    {
        var seen = new HashSet<string>(Platform.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var result = new List<JavaInfo>();
        foreach (var home in CandidateHomes(runtimeDirs ?? []))
        {
            var exe = Path.Combine(home, "bin", Platform.JavaExecutable);
            if (!File.Exists(exe) || (Platform.Is64Bit && Is32BitJava(home)))
                continue;
            string key;
            try
            {
                key = new FileInfo(exe).ResolveLinkTarget(true)?.FullName ?? Path.GetFullPath(exe);
            }
            catch (IOException)
            {
                key = Path.GetFullPath(exe);
            }
            if (!seen.Add(key))
                continue;
            var version = JavaVersion(exe);
            if (version != null)
                result.Add(new JavaInfo(exe, version.Value));
        }
        return result.OrderByDescending(j => j.Major).ToList();
    }

    public static string PickExact(IEnumerable<JavaInfo> javas, int required) =>
        javas.FirstOrDefault(j => j.Major == required)?.Path;

    public static string PickCompatible(IEnumerable<JavaInfo> javas, int required) =>
        javas.Where(j => j.Major >= required).OrderBy(j => j.Major).FirstOrDefault()?.Path;

    public static string RuntimePlatform()
    {
        if (Platform.IsWindows)
            return Platform.IsArm ? "windows-arm64" : Platform.Is64Bit ? "windows-x64" : "windows-x86";
        if (Platform.IsMac)
            return Platform.IsArm ? "mac-os-arm64" : "mac-os";
        return Platform.Is64Bit ? "linux" : "linux-i386";
    }

    public static string RuntimeJavaPath(string home) =>
        Platform.IsMac
            ? Path.Combine(home, "jre.bundle", "Contents", "Home", "bin", "java")
            : Path.Combine(home, "bin", Platform.JavaExecutable);

    /// <summary>从 Mojang 下载官方 Java 运行时（如 java-runtime-delta），返回 java 可执行文件路径。</summary>
    public static async Task<string> InstallRuntimeAsync(string component, string runtimeRoot, Downloader dl,
                                                         Action<int, int> progress = null, string platform = null)
    {
        var all = await dl.FetchJsonAsync(RuntimeManifestUrl);
        var entries = all.Get(platform ?? RuntimePlatform()).Arr(component);
        if ((entries == null || entries.Count == 0) && platform == null && Platform.IsMac && Platform.IsArm)
            entries = all.Get("mac-os").Arr(component);  // 没有 Apple 芯片版本时用 Rosetta 运行 Intel 版本
        if (entries == null || entries.Count == 0)
            throw new InvalidOperationException(F("官方没有为当前平台提供 Java 运行时 {0}", component));
        var manifest = await dl.FetchJsonAsync(entries[0].Get("manifest").Str("url"));

        var home = Path.Combine(runtimeRoot, component);
        var tasks = new List<DownloadTask>();
        var executables = new List<string>();
        var links = new List<(string Dest, string Target)>();
        foreach (var (rel, info) in manifest.Obj("files"))
        {
            var dest = Path.Combine(home, Path.Combine(rel.Split('/')));
            switch (info.Str("type"))
            {
                case "directory":
                    Directory.CreateDirectory(dest);
                    break;
                case "file":
                    var raw = info.Get("downloads").Get("raw");
                    tasks.Add(new DownloadTask(raw.Str("url"), dest, raw.Str("sha1"), raw.Long("size")));
                    if (info.Bool("executable"))
                        executables.Add(dest);
                    break;
                case "link":
                    links.Add((dest, info.Str("target")));
                    break;
            }
        }

        await dl.DownloadManyAsync(tasks, progress);

        if (!Platform.IsWindows)
        {
            foreach (var path in executables)
                Platform.MakeExecutable(path);
            foreach (var (dest, target) in links)
            {
                if (File.Exists(dest) || Directory.Exists(dest))
                    continue;
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.CreateSymbolicLink(dest, target);
            }
        }

        var java = RuntimeJavaPath(home);
        if (!File.Exists(java))
            throw new InvalidOperationException(F("Java 运行时安装不完整：找不到 {0}", java));
        return java;
    }
}
