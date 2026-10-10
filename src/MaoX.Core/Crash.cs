using System.Text;
using System.Text.RegularExpressions;
using static MaoX.Core.I18n;

namespace MaoX.Core;

/// <summary>一条崩溃原因。Kind 为 CrashAnalyzer.Mod 或 CrashAnalyzer.Other。</summary>
public record CrashReason(string Kind, string Text);

public class CrashReport
{
    public List<CrashReason> Reasons { get; init; } = [];
    /// <summary>关键错误行（最多 300 字符），没有时为空字符串。</summary>
    public string Detail { get; init; } = "";
    /// <summary>相关的崩溃报告 / JVM 错误日志路径。</summary>
    public List<string> Files { get; init; } = [];
}

/// <summary>游戏崩溃分析：从游戏输出、崩溃报告和 JVM 错误日志中找出常见原因，给出中文说明。</summary>
public static class CrashAnalyzer
{
    /// <summary>模组加载失败一类的问题，游戏可能正常退出（退出码 0）也需要提示。</summary>
    public const string Mod = "mod";
    public const string Other = "other";

    private const RegexOptions None = RegexOptions.CultureInvariant;
    private const RegexOptions IgnoreCase = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

    private static Regex R(string pattern, RegexOptions options = None) => new(pattern, options);

    private static readonly Regex ForgeDependency =
        R(@"Mod ID: '([^']+)', Requested by: '([^']+)', Expected range: '([^']+)', Actual version: '([^']+)'");
    private static readonly Regex LegacyRequires = R(@"Mod \S+ \(([^)]+)\) requires \[([^\]]+)\]");
    private static readonly Regex LegacyRequiresMods = R(@"The mod \S+ \(([^)]+)\) requires mods \[([^\]]+)\]");
    private static readonly Regex FabricMod = R(@"Mod '([^']+)'");
    private static readonly Regex FabricMissing = R(@"of (?:mod )?'?([^,']+?)'?(?: \(([^)]+)\))?, which is missing");
    private static readonly Regex FabricWrongVersion =
        R(@"requires (.+?) of (?:mod )?'?([^,']+?)'?(?: \(([^)]+)\))?, but only");
    private static readonly Regex VersionPrefix = R(@"^(?:any )?version ");
    private static readonly Regex Quoted = R(@"'([^']+)'");
    private static readonly Regex SuspectedMods = R(@"Suspected Mods?:(.*?)(?:\n\s*\n|\nStacktrace:)", RegexOptions.Singleline | None);
    private static readonly Regex SuspectedName = R(@"^\s*(.+?) \(([^)]+)\)");

    private static readonly Regex ClassVersion =
        R(@"class file version (\d+(?:\.\d+)?)\), this version of the Java Runtime only recognizes " +
          @"class file versions up to (\d+(?:\.\d+)?)");
    private static readonly Regex MajorMinor = R(@"Unsupported major\.minor version (\d+)");
    private static readonly Regex UrlClassLoaderCast = R(@"AppClassLoader cannot be cast to (?:class )?java\.net\.URLClassLoader");
    private static readonly Regex Inaccessible = R(@"InaccessibleObjectException|module java\.base does not ""opens");
    private static readonly Regex ForgeLike = R("forge|fml|launchwrapper", IgnoreCase);
    private static readonly Regex HeapReserve = R(@"Could not reserve enough space for .*object heap|Invalid maximum heap size");
    private static readonly Regex NativeMemory =
        R(@"There is insufficient memory for the Java Runtime Environment|Native memory allocation");
    private static readonly Regex OpenGl =
        R(@"Pixel format not accelerated|GLFW error 6554[23]|WGL: The driver does not appear to support " +
          @"OpenGL|Couldn't set pixel format|No OpenGL context found|OpenGL \S+ is not supported");
    private static readonly Regex DriverDll = R(@"(atio6axx|atioglxx|nvoglv(?:64|32)|ig\w*icd(?:64|32)|igxelpicd64)\.dll", IgnoreCase);
    private static readonly Regex FabricSolution = R(@"A potential solution has been determined.*?:\n((?:\s*- .+\n?)+)");
    private static readonly Regex Duplicate =
        R(@"[Dd]uplicate mods?|DuplicateModsFoundException|Found a duplicate mod|" +
          @"Mod ID .+ (?:is )?duplicated|duplicate mod ids");
    private static readonly Regex DuplicateModId = R(@"Mod ID: '([^']+)' from mod files?:");
    private static readonly Regex DuplicateNamed = R(@"Duplicate mods? .*?'([^']+)'");
    private static readonly Regex MixinApply = R(@"Mixin apply for mod (\S+) failed");
    private static readonly Regex MixinInjection = R(@"from mod (\S+?)\] failed injection check");
    private static readonly Regex OptiFine = R(@"net\.optifine|optifine\.", IgnoreCase);
    private static readonly Regex NoSuchMember = R("NoSuchMethodError|NoSuchFieldError");
    private static readonly Regex Corrupt =
        R(@"zip END header not found|Invalid or corrupt jarfile|error in opening zip file|" +
          @"ZipException: (?:invalid|zip file is empty)");
    // 讲述人的 flite 库在 Linux / 手机上经常没有，游戏会自己忽略，不算加载失败
    private static readonly Regex NativeLink = R(@"UnsatisfiedLinkError(?!: (?:Unable to load library 'flite'|dlopen failed: library ""libflite))|Failed to locate library: \S+\.dll");
    private static readonly Regex MissingClass = R(@"(?:NoClassDefFoundError|ClassNotFoundException): ([\w.$/]+)");
    private static readonly Regex Description = R(@"Description: (.+)\n+(.+)");
    private static readonly Regex ExceptionLine =
        R(@"^(?:Exception in thread ""[^""]+"" )?((?:[\w$]+\.)+[\w$]*(?:Exception|Error)\b.*)$", RegexOptions.Multiline | None);
    private static readonly Regex LineBreak = R("\r\n|[\n\r\v\f\x1c\x1d\x1e\x85\u2028\u2029]");

    private static string Read(string path, int limit = 2 * 1024 * 1024)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                              FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[(int)Math.Min(limit, Math.Max(0, stream.Length))];
            var total = 0;
            int read;
            while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
                total += read;
            return Encoding.UTF8.GetString(buffer, 0, total).Replace("\r\n", "\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    private static string Newest(string dir, string pattern, string extension, DateTime sinceUtc)
    {
        if (!Directory.Exists(dir))
            return null;
        try
        {
            return Directory.EnumerateFiles(dir, pattern)
                .Where(p => p.EndsWith(extension, StringComparison.Ordinal))
                .Select(p => (Path: p, Time: File.GetLastWriteTimeUtc(p)))
                .Where(f => f.Time >= sinceUtc)
                .OrderByDescending(f => f.Time)
                .Select(f => f.Path)
                .FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static List<string> Unique(IEnumerable<string> items) => items.Distinct().ToList();

    private static string[] SplitLines(string text)
    {
        var lines = LineBreak.Split(text);
        return lines.Length > 0 && lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    private static int JavaFromClassVersion(string v) =>
        (int)double.Parse(v, System.Globalization.CultureInfo.InvariantCulture) - 44;

    private static List<string> ModDependencyReasons(string text)
    {
        var reasons = new List<string>();
        // Forge 1.13+ / NeoForge
        foreach (Match m in ForgeDependency.Matches(text))
        {
            var (modId, requester, wanted, actual) = (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value);
            if (modId == "minecraft")
                reasons.Add(F("模组「{0}」不适用于当前游戏版本（需要 Minecraft {1}）", requester, wanted));
            else if (actual.ToUpperInvariant().Contains("MISSING"))
                reasons.Add(F("模组「{0}」缺少前置模组「{1}」（需要版本 {2}）", requester, modId, wanted));
            else
                reasons.Add(F("模组「{0}」需要「{1}」的版本 {2}，当前为 {3}", requester, modId, wanted, actual));
        }
        // 1.12.2 及更早的 Forge
        foreach (var regex in new[] { LegacyRequires, LegacyRequiresMods })
        {
            foreach (Match m in regex.Matches(text))
            {
                foreach (var dep in m.Groups[2].Value.Split(','))
                    reasons.Add(F("模组「{0}」缺少前置模组「{1}」", m.Groups[1].Value, dep.Split('@')[0].Trim()));
            }
        }
        // Fabric / Quilt
        foreach (var line in SplitLines(text))
        {
            if (line.Contains("which is missing"))
            {
                var mod = FabricMod.Match(line);
                var dep = FabricMissing.Match(line);
                if (mod.Success && dep.Success)
                {
                    var target = dep.Groups[2].Success ? dep.Groups[2].Value : dep.Groups[1].Value;
                    reasons.Add(F("模组「{0}」缺少前置模组「{1}」", mod.Groups[1].Value, target));
                }
            }
            else if (line.Contains("but only the wrong version is present"))
            {
                var mod = FabricMod.Match(line);
                var dep = FabricWrongVersion.Match(line);
                if (mod.Success && dep.Success)
                {
                    var target = dep.Groups[3].Success ? dep.Groups[3].Value : dep.Groups[2].Value;
                    var wanted = VersionPrefix.Replace(dep.Groups[1].Value, "");
                    reasons.Add(target == "minecraft"
                                    ? F("模组「{0}」不适用于当前游戏版本（需要 Minecraft {1}）", mod.Groups[1].Value, wanted)
                                    : F("模组「{0}」需要「{1}」的 {2} 版本", mod.Groups[1].Value, target, wanted));
                }
            }
            else if (line.Contains("is incompatible with") && line.Contains("Mod '"))
            {
                var mods = Quoted.Matches(line);
                if (mods.Count >= 2)
                    reasons.Add(F("模组「{0}」与「{1}」不兼容，请删除其中一个", mods[0].Groups[1].Value, mods[1].Groups[1].Value));
            }
        }
        return reasons;
    }

    /// <summary>Forge / NeoForge 崩溃报告中的 Suspected Mods 段落。</summary>
    private static List<string> SuspectedModNames(string report)
    {
        var mods = new List<string>();
        var m = SuspectedMods.Match(report);
        if (m.Success)
        {
            var body = m.Groups[1].Value;
            if (!body[..Math.Min(20, body.Length)].ToUpperInvariant().Contains("NONE"))
            {
                foreach (var line in SplitLines(body))
                {
                    var name = SuspectedName.Match(line);
                    if (name.Success)
                        mods.Add(name.Groups[1].Value);
                }
            }
        }
        return mods;
    }

    /// <summary>
    /// 分析一次游戏退出。lines 为最近的游戏输出行，since 为启动时间（只读取此后生成的崩溃报告与日志），
    /// exitCode 为进程退出码。退出码为 0 时界面通常只展示 Kind == Mod 的原因。
    /// </summary>
    public static CrashReport Analyze(IReadOnlyList<string> lines, string gameDir, DateTime since, long exitCode)
    {
        var sinceUtc = since.ToUniversalTime().AddSeconds(-2);
        var reportPath = Newest(Path.Combine(gameDir, "crash-reports"), "crash-*.txt", ".txt", sinceUtc);
        var hsErrPath = Newest(gameDir, "hs_err_pid*.log", ".log", sinceUtc);
        var report = reportPath != null ? Read(reportPath) : "";
        var hsErr = hsErrPath != null ? Read(hsErrPath, 256 * 1024) : "";
        var output = string.Join("\n", lines);
        if (lines.Count < 50)
        {
            var latest = Path.Combine(gameDir, "logs", "latest.log");
            if (File.Exists(latest) && File.GetLastWriteTimeUtc(latest) >= sinceUtc)
                output += "\n" + Read(latest);
        }
        var text = string.Join("\n", report, hsErr, output);
        var reasons = new List<CrashReason>();

        void Add(string kind, string message) => reasons.Add(new CrashReason(kind, message));

        // ---------------------------------------------------------------- Java 版本
        Match m;
        if ((m = ClassVersion.Match(text)).Success)
            Add(Other, F("游戏或模组需要 Java {0} 及以上，" +
                         "当前使用的是 Java {1}。请在设置或版本设置中更换 Java",
                         JavaFromClassVersion(m.Groups[1].Value), JavaFromClassVersion(m.Groups[2].Value)));
        else if ((m = MajorMinor.Match(text)).Success)
            Add(Other, F("游戏或模组需要 Java {0} 及以上，当前 Java 版本过低", JavaFromClassVersion(m.Groups[1].Value)));
        if (UrlClassLoaderCast.IsMatch(text))
            Add(Other, T("这个版本需要 Java 8，当前 Java 版本过高。请在版本设置中指定 Java 8"));
        else if (Inaccessible.IsMatch(text) && ForgeLike.IsMatch(text))
            Add(Other, T("当前 Java 版本过高，与这个版本的 Forge 或模组不兼容。请在版本设置中指定较低版本的 Java"));

        // ---------------------------------------------------------------- 内存
        if (HeapReserve.IsMatch(text))
            Add(Other, T("无法分配设置的内存：可能使用了 32 位 Java，或最大内存设置过大"));
        else if (NativeMemory.IsMatch(text))
            Add(Other, T("系统可用内存不足：请关闭其他程序，或适当调低最大内存"));
        else if (text.Contains("java.lang.OutOfMemoryError") || text.Contains("Out of Memory Error"))
            Add(Other, T("游戏内存不足：请在设置中调高最大内存（大型整合包建议 6 GB 以上）"));

        // ---------------------------------------------------------------- 显卡
        if (OpenGl.IsMatch(text))
            Add(Other, T("显卡驱动不支持 OpenGL 或驱动有问题：请更新显卡驱动；双显卡电脑请让 Java 使用独立显卡"));
        else if (hsErr.Length > 0 && DriverDll.IsMatch(hsErr))
            Add(Other, T("显卡驱动崩溃：请更新显卡驱动；如果安装了光影，可以先关闭光影再试"));

        // ---------------------------------------------------------------- 模组
        foreach (var reason in Unique(ModDependencyReasons(text)))
            Add(Mod, reason);
        m = FabricSolution.Match(text);
        if (m.Success && !reasons.Any(r => r.Kind == Mod))
        {
            foreach (var line in SplitLines(m.Groups[1].Value.Trim()).Take(6))
                Add(Mod, F("Fabric 建议：{0}", line.Trim(' ', '\t', '-')));
        }
        if (Duplicate.IsMatch(text))
        {
            var names = Unique(DuplicateModId.Matches(text).Select(x => x.Groups[1].Value)
                                   .Concat(DuplicateNamed.Matches(text).Select(x => x.Groups[1].Value)));
            Add(Mod, F("有重复安装的模组{0}，" +
                       "请在 mods 文件夹中只保留一个版本",
                       names.Count > 0 ? F("：{0}", string.Join(T("、"), names.Take(5))) : ""));
        }
        foreach (var mod in Unique(MixinApply.Matches(text).Select(x => x.Groups[1].Value)
                                       .Concat(MixinInjection.Matches(text).Select(x => x.Groups[1].Value))).Take(5))
            Add(Mod, F("模组「{0}」注入失败，可能与当前游戏版本或其他模组不兼容", mod));
        if (OptiFine.IsMatch(text) && NoSuchMember.IsMatch(text))
            Add(Mod, T("OptiFine 与当前 Forge 或其他模组不兼容：请更换 OptiFine / Forge 版本，或移除 OptiFine"));
        foreach (var mod in SuspectedModNames(report).Take(5))
            Add(Mod, F("崩溃报告指出可能与模组「{0}」有关，可以尝试更新或移除它", mod));

        // ---------------------------------------------------------------- 其他
        if (Corrupt.IsMatch(text))
            Add(Other, T("有文件已损坏：请重新下载出问题的模组，或在启动时让启动器补全游戏文件"));
        if (NativeLink.IsMatch(text))
            Add(Other, T("游戏本地库加载失败：可能是游戏路径包含特殊字符，或 Java 与系统架构不匹配（32 / 64 位）"));
        if (text.Contains("Manually triggered debug crash"))
            Add(Other, T("这是按住 F3 + C 手动触发的崩溃，不是错误"));
        if (text.Contains("java.lang.StackOverflowError") && reasons.Count == 0)
            Add(Other, T("游戏发生了栈溢出，通常由模组之间的冲突引起；也可以在额外 JVM 参数中加入 -Xss4m 再试"));
        if (reasons.Count == 0)
        {
            foreach (var cls in Unique(MissingClass.Matches(text).Select(x => x.Groups[1].Value)).Take(3))
                Add(cls.Contains("minecraft") ? Other : Mod,
                    F("找不到类 {0}：可能缺少前置模组，或模组与当前版本不匹配", cls.Replace('/', '.')));
        }
        if ((exitCode is -1073741819 or 3221225477) && reasons.Count == 0)
            Add(Other, T("游戏进程发生内存访问冲突，通常是显卡驱动或光影导致：请更新显卡驱动并关闭光影"));

        var detail = "";
        if (report.Length > 0 && (m = Description.Match(report)).Success)
            detail = F("{0}：{1}", m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim());
        if (detail.Length == 0 && (m = ExceptionLine.Match(output)).Success)
            detail = m.Groups[1].Value.Trim();
        if (detail.Length > 300)
            detail = detail[..(char.IsHighSurrogate(detail[299]) ? 299 : 300)];
        return new CrashReport
        {
            Reasons = reasons,
            Detail = detail,
            Files = new[] { reportPath, hsErrPath }.Where(p => p != null).ToList(),
        };
    }
}
