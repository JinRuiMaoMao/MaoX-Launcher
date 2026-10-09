// 在 Windows 上把 osx-* 的发布目录打成带可执行权限的 "MaoX Launcher.app"（.tar.gz）。
// Windows 生成的 zip 无法记录 Unix 权限，解压后程序会无法运行，所以这里用 tar。
// 用法：dotnet run packaging/macos/PackMac.cs -- <发布目录> <版本号> <输出 .tar.gz>
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

if (args.Length != 3)
{
    Console.Error.WriteLine("用法：dotnet run PackMac.cs -- <发布目录> <版本号> <输出 .tar.gz>");
    return 1;
}

var (source, version, output) = (Path.GetFullPath(args[0]), args[1], Path.GetFullPath(args[2]));
var here = Path.GetDirectoryName(Path.GetFullPath(GetSourcePath()))!;
const string Contents = "MaoX Launcher.app/Contents/";
const UnixFileMode Executable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
const UnixFileMode Normal = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

Directory.CreateDirectory(Path.GetDirectoryName(output)!);
using (var file = File.Create(output))
using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize))
using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
{
    var now = DateTimeOffset.Now;
    var written = new HashSet<string>();

    void AddDirectory(string name)
    {
        if (written.Add(name))
            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, name) { Mode = Executable, ModificationTime = now });
    }

    void Add(string name, byte[] data, bool executable)
    {
        var parts = name.Split('/');
        for (var i = 1; i < parts.Length; i++)
            AddDirectory(string.Join('/', parts[..i]) + "/");
        tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
        {
            Mode = executable ? Executable : Normal,
            ModificationTime = now,
            DataStream = new MemoryStream(data),
        });
    }

    var plist = File.ReadAllText(Path.Combine(here, "Info.plist")).Replace("__VERSION__", version);
    Add(Contents + "Info.plist", Encoding.UTF8.GetBytes(plist), false);
    Add(Contents + "Resources/MaoX.icns", File.ReadAllBytes(Path.Combine(here, "MaoX.icns")), false);
    foreach (var path in Directory.GetFiles(source, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
    {
        var relative = Path.GetRelativePath(source, path).Replace('\\', '/');
        var executable = relative == "MaoXLauncher" || relative.EndsWith(".dylib", StringComparison.Ordinal);
        Add(Contents + "MacOS/" + relative, File.ReadAllBytes(path), executable);
    }
}

Console.WriteLine($"已生成 {output}（{new FileInfo(output).Length / 1048576.0:F1} MB）");
return 0;

static string GetSourcePath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
