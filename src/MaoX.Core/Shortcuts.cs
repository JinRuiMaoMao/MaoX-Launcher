using System.Diagnostics;
using System.Text;
using static MaoX.Core.I18n;

namespace MaoX.Core;

/// <summary>桌面快捷方式（通过 MaoXLauncher --launch 启动指定版本）与独立的启动脚本。</summary>
public static class Shortcuts
{
    /// <summary>快捷方式启动启动器时额外带上的参数：开发时用 MAOX_HOME 指定了数据目录，需要原样传下去。</summary>
    private static List<string> LauncherArgs(string version)
    {
        var args = new List<string> { "--launch", version };
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MAOX_HOME")))
            args.AddRange(["--home", AppPaths.BaseDir]);
        return args;
    }

    private static string SafeFileName(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '/' or ':' ? '_' : c));

    /// <summary>在桌面创建启动该版本的快捷方式，返回快捷方式路径。</summary>
    public static string CreateDesktopShortcut(string version)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException(T("无法确定启动器的位置"));
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrEmpty(desktop))
            desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop");
        Directory.CreateDirectory(desktop);
        var name = SafeFileName($"{version} - MaoX");
        var args = LauncherArgs(version);

        if (Platform.IsWindows)
        {
            var path = Path.Combine(desktop, name + ".lnk");
            static string Ps(string s) => "'" + s.Replace("'", "''") + "'";
            var arguments = string.Join(" ", args.Select(a => a.Contains(' ') || a.Length == 0 ? $"\"{a}\"" : a));
            var script = new StringBuilder()
                .Append("$s = (New-Object -ComObject WScript.Shell).CreateShortcut(").Append(Ps(path)).Append(");")
                .Append("$s.TargetPath = ").Append(Ps(exe)).Append(';')
                .Append("$s.Arguments = ").Append(Ps(arguments)).Append(';')
                .Append("$s.WorkingDirectory = ").Append(Ps(Path.GetDirectoryName(exe)!)).Append(';')
                .Append("$s.IconLocation = ").Append(Ps(exe + ",0")).Append(';')
                .Append("$s.Description = ").Append(Ps(F("用 MaoX Launcher 启动 {0}", version))).Append(';')
                .Append("$s.Save()")
                .ToString();
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-EncodedCommand");
            psi.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            using var process = Process.Start(psi) ?? throw new InvalidOperationException(T("无法创建快捷方式"));
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit(20_000);
            if (!File.Exists(path))
                throw new InvalidOperationException(F("创建快捷方式失败：{0}", error.Trim()));
            return path;
        }

        // macOS / Linux：可双击运行的脚本
        var file = Path.Combine(desktop, name + (Platform.IsMac ? ".command" : ".sh"));
        var bundle = Updater.AppBundle;
        var body = bundle != null
            ? $"open -n -a {Sh(bundle)} --args {string.Join(" ", args.Select(Sh))}"
            : $"{Sh(exe)} {string.Join(" ", args.Select(Sh))} &";
        File.WriteAllText(file, $"#!/bin/bash\n# {F("用 MaoX Launcher 启动 {0}", version)}\n{body}\n");
        Platform.MakeExecutable(file);
        return file;
    }

    /// <summary>导出独立启动脚本（不需要启动器也能运行）。Windows 为 .bat，其他系统为 .command / .sh。</summary>
    public static void WriteLaunchScript(string path, IReadOnlyList<string> command, string gameDir, string version)
    {
        // 启动器用的日志配置输出 XML，脚本里去掉，命令行窗口里看到的就是普通文本日志
        command = command.Where(a => !a.StartsWith("-Dlog4j.configurationFile=", StringComparison.Ordinal)).ToList();
        if (Platform.IsWindows)
        {
            var sb = new StringBuilder();
            sb.Append("@echo off\r\n");
            sb.Append("chcp 65001 >nul\r\n");
            sb.Append($"title {Bat(version)}\r\n");
            sb.Append($"rem {F("由 MaoX Launcher 导出的 {0} 启动脚本", version)}\r\n");
            sb.Append($"cd /d {BatArg(gameDir)}\r\n");
            var java = BatArg(JavaConsole(command[0]));
            var line = string.Join(" ", command.Skip(1).Select(BatArg));
            // cmd 一行最多 8191 个字符，整合包的 classpath 经常超出，这时改用 Java 的 @参数文件
            if (java.Length + line.Length > 8000)
            {
                if (JavaManager.JavaVersion(command[0]) is < 9)
                    throw new InvalidOperationException(T("这个版本的启动命令太长，Windows 批处理放不下，而 Java 8 又不支持参数文件。请换用 Java 9 及以上再导出。"));
                var argsFile = Path.ChangeExtension(path, ".args.txt");
                File.WriteAllText(argsFile, string.Join("\r\n", command.Skip(1).Select(JavaArgFileArg)), NativeEncoding());
                line = BatArg("@" + argsFile);
            }
            sb.Append(java).Append(' ').Append(line);
            sb.Append("\r\npause\r\n");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }
        else
        {
            var sb = new StringBuilder();
            sb.Append("#!/bin/bash\n");
            sb.Append($"# {F("由 MaoX Launcher 导出的 {0} 启动脚本", version)}\n");
            sb.Append($"cd {Sh(gameDir)} || exit 1\n");
            sb.Append("exec ").Append(string.Join(" \\\n  ", command.Select(Sh))).Append('\n');
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            Platform.MakeExecutable(path);
        }
    }

    /// <summary>脚本里用 java.exe 而不是 javaw.exe，这样能在命令行窗口看到游戏日志。</summary>
    private static string JavaConsole(string java)
    {
        if (!java.EndsWith("javaw.exe", StringComparison.OrdinalIgnoreCase))
            return java;
        var console = java[..^"javaw.exe".Length] + "java.exe";
        return File.Exists(console) ? console : java;
    }

    private static string Sh(string s) => "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>Java @参数文件：引号内反斜杠是转义符。</summary>
    private static string JavaArgFileArg(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>Java 按系统 ANSI 代码页读取参数文件（中文系统是 GBK）。</summary>
    private static Encoding NativeEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException)
        {
            return new UTF8Encoding(false);
        }
    }

    private static string Bat(string s) => s.Replace("%", "%%").Replace("^", "^^").Replace("&", "^&")
                                            .Replace("|", "^|").Replace("<", "^<").Replace(">", "^>");

    /// <summary>bat 参数：整体加引号（内部引号加倍），百分号写成 %%。</summary>
    private static string BatArg(string s) => "\"" + s.Replace("\"", "\"\"").Replace("%", "%%") + "\"";
}
