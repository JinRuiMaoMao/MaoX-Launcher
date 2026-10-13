using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using Foundation;

namespace MaoX.iOS.Game;

/// <summary>
/// 读出安装后（安装工具重签之后）各个可执行文件的代码签名，写进 JIT 助手的日志，用来判断助手为什么被系统拒绝启动。
/// </summary>
internal static partial class CodeSignatureInfo
{
    private const uint LcCodeSignature = 0x1d;

    public static string Report()
    {
        var app = NSBundle.MainBundle.BundlePath;
        var sb = new StringBuilder("Code signatures after installation:\n");
        Describe(sb, app);
        var plugins = Path.Combine(app, "PlugIns");
        if (Directory.Exists(plugins))
            foreach (var appex in Directory.GetDirectories(plugins, "*.appex"))
                Describe(sb, appex);
        var frameworks = Path.Combine(app, "Frameworks");
        if (Directory.Exists(frameworks))
            foreach (var fw in Directory.GetDirectories(frameworks, "StikJIT.framework"))
                Describe(sb, fw);
        return sb.ToString();
    }

    private static void Describe(StringBuilder sb, string bundle)
    {
        sb.Append("- ").Append(Path.GetFileName(bundle)).Append('\n');
        try
        {
            var info = NSBundle.FromPath(bundle)?.InfoDictionary;
            var exe = info?["CFBundleExecutable"]?.ToString();
            sb.Append("  CFBundleIdentifier: ").Append(info?["CFBundleIdentifier"]).Append('\n');
            if (string.IsNullOrEmpty(exe))
            {
                sb.Append("  no CFBundleExecutable\n");
                return;
            }
            Binary(sb, File.ReadAllBytes(Path.Combine(bundle, exe)));
            var profile = Path.Combine(bundle, "embedded.mobileprovision");
            if (File.Exists(profile))
                Profile(sb, File.ReadAllBytes(profile));
            else
                sb.Append("  embedded.mobileprovision: none\n");
        }
        catch (Exception e)
        {
            sb.Append("  error: ").Append(e.Message).Append('\n');
        }
    }

    /// <summary>
    /// 扩展的可执行文件签名里没有 CS_EXECSEG_MAIN_BINARY。Apple 的 codesign 给每个 MH_EXECUTE 都设这个标记，
    /// Sideloadly 只给主程序设；iOS 26 起没有它的扩展一启动就被系统以 CODESIGNING Invalid Page 杀掉。
    /// </summary>
    public static bool LacksMainBinary(string bundle)
    {
        try
        {
            var exe = NSBundle.FromPath(bundle)?.InfoDictionary?["CFBundleExecutable"]?.ToString();
            if (string.IsNullOrEmpty(exe))
                return false;
            var blob = SignatureBlob(Thin(File.ReadAllBytes(Path.Combine(bundle, exe))));
            if (blob.Length < 12 || BinaryPrimitives.ReadUInt32BigEndian(blob) != 0xfade0cc0)
                return false;
            var count = BinaryPrimitives.ReadUInt32BigEndian(blob[8..]);
            for (var i = 0; i < count; i++)
            {
                var off = (int)BinaryPrimitives.ReadUInt32BigEndian(blob[(16 + i * 8)..]);
                var cd = blob[off..];
                if (BinaryPrimitives.ReadUInt32BigEndian(cd) == 0xfade0c02 && BinaryPrimitives.ReadUInt32BigEndian(cd[8..]) >= 0x20400)
                    return (BinaryPrimitives.ReadUInt64BigEndian(cd[80..]) & 1) == 0;
            }
        }
        catch (Exception)
        {
            // 读不出来时不下结论
        }
        return false;
    }

    /// <summary>通用二进制取第一个架构</summary>
    private static ReadOnlySpan<byte> Thin(ReadOnlySpan<byte> data)
    {
        if (BinaryPrimitives.ReadUInt32BigEndian(data) != 0xcafebabe)
            return data;
        var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(data[16..]);
        var size = (int)BinaryPrimitives.ReadUInt32BigEndian(data[20..]);
        return data.Slice(offset, size);
    }

    /// <summary>LC_CODE_SIGNATURE 指向的签名数据，没有时为空</summary>
    private static ReadOnlySpan<byte> SignatureBlob(ReadOnlySpan<byte> macho)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(macho) != 0xfeedfacf)
            return default;
        var commands = BinaryPrimitives.ReadUInt32LittleEndian(macho[16..]);
        var at = 32;
        for (var i = 0; i < commands; i++)
        {
            var cmd = BinaryPrimitives.ReadUInt32LittleEndian(macho[at..]);
            if (cmd == LcCodeSignature)
            {
                var off = (int)BinaryPrimitives.ReadUInt32LittleEndian(macho[(at + 8)..]);
                var len = (int)BinaryPrimitives.ReadUInt32LittleEndian(macho[(at + 12)..]);
                return macho.Slice(off, len);
            }
            at += (int)BinaryPrimitives.ReadUInt32LittleEndian(macho[(at + 4)..]);
        }
        return default;
    }

    private static void Binary(StringBuilder sb, byte[] file)
    {
        if (BinaryPrimitives.ReadUInt32BigEndian(file) is 0xcafebabe)
            sb.Append("  fat, ").Append(BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(4))).Append(" slices; first slice:\n");
        var data = Thin(file);
        if (BinaryPrimitives.ReadUInt32LittleEndian(data) != 0xfeedfacf)
        {
            sb.Append("  not a 64-bit Mach-O\n");
            return;
        }
        var cpuSubtype = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]) & 0xff;
        var fileType = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        sb.Append($"  Mach-O filetype {fileType}, cpusubtype {cpuSubtype}\n");
        var blob = SignatureBlob(data);
        if (blob.IsEmpty)
            sb.Append("  no code signature\n");
        else
            SuperBlob(sb, blob);
    }

    private static void SuperBlob(StringBuilder sb, ReadOnlySpan<byte> blob)
    {
        if (BinaryPrimitives.ReadUInt32BigEndian(blob) != 0xfade0cc0)
        {
            sb.Append("  bad signature superblob\n");
            return;
        }
        var count = BinaryPrimitives.ReadUInt32BigEndian(blob[8..]);
        var hasCms = false;
        for (var i = 0; i < count; i++)
        {
            var type = BinaryPrimitives.ReadUInt32BigEndian(blob[(12 + i * 8)..]);
            var off = (int)BinaryPrimitives.ReadUInt32BigEndian(blob[(16 + i * 8)..]);
            var magic = BinaryPrimitives.ReadUInt32BigEndian(blob[off..]);
            var len = (int)BinaryPrimitives.ReadUInt32BigEndian(blob[(off + 4)..]);
            var body = blob.Slice(off, len);
            switch (magic)
            {
                case 0xfade0c02:
                    CodeDirectory(sb, type, body);
                    break;
                case 0xfade7171:
                    sb.Append("  entitlements: ").Append(Keys(Encoding.UTF8.GetString(body[8..]))).Append('\n');
                    break;
                case 0xfade7172:
                    sb.Append("  DER entitlements: ").Append(len - 8).Append(" bytes\n");
                    break;
                case 0xfade0b01:
                    hasCms = len > 8;
                    break;
            }
        }
        sb.Append("  CMS signature: ").Append(hasCms ? "yes" : "no (ad-hoc)").Append('\n');
    }

    private static void CodeDirectory(StringBuilder sb, uint slot, ReadOnlySpan<byte> cd)
    {
        var version = BinaryPrimitives.ReadUInt32BigEndian(cd[8..]);
        var flags = BinaryPrimitives.ReadUInt32BigEndian(cd[12..]);
        var identOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(cd[20..]);
        var hashType = cd[37];
        var pageSize = cd[39];
        sb.Append($"  CodeDirectory slot 0x{slot:x}: version 0x{version:x}, flags 0x{flags:x}, hashType {hashType}, page 2^{pageSize}");
        sb.Append(", identifier ").Append(CString(cd[identOffset..]));
        if (version >= 0x20200)
        {
            var teamOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(cd[48..]);
            sb.Append(", team ").Append(teamOffset == 0 ? "none" : CString(cd[teamOffset..]));
        }
        if (version >= 0x20400)
        {
            var execSegFlags = BinaryPrimitives.ReadUInt64BigEndian(cd[80..]);
            sb.Append($", execSegFlags 0x{execSegFlags:x}");
            if ((execSegFlags & 1) == 0)
                sb.Append(" (no MAIN_BINARY)");
        }
        else
        {
            sb.Append(", no execSeg fields");
        }
        sb.Append('\n');
    }

    private static void Profile(StringBuilder sb, byte[] profile)
    {
        var text = Encoding.UTF8.GetString(profile);
        var start = text.IndexOf("<?xml", StringComparison.Ordinal);
        var end = text.IndexOf("</plist>", StringComparison.Ordinal);
        if (start < 0 || end < start)
        {
            sb.Append("  embedded.mobileprovision: unreadable\n");
            return;
        }
        var plist = text[start..(end + 8)];
        var name = Regex.Match(plist, @"<key>Name</key>\s*<string>([^<]*)</string>").Groups[1].Value;
        var ents = Regex.Match(plist, @"<key>Entitlements</key>\s*<dict>([\s\S]*?)</dict>\s*<key>").Groups[1].Value;
        sb.Append("  embedded.mobileprovision: ").Append(name).Append("; entitlements: ").Append(Keys(ents)).Append('\n');
    }

    /// <summary>权限的键和简单的值（布尔、字符串），不展开数组</summary>
    private static string Keys(string xml)
    {
        var items = EntitlementRegex().Matches(xml)
            .Select(m => m.Groups[2].Success ? $"{m.Groups[1].Value}={m.Groups[2].Value}"
                       : m.Groups[3].Success ? $"{m.Groups[1].Value}={m.Groups[3].Value}"
                       : m.Groups[1].Value);
        return string.Join(", ", items);
    }

    [GeneratedRegex(@"<key>([^<]+)</key>\s*(?:<(true|false)\s*/>|<string>([^<]*)</string>)?")]
    private static partial Regex EntitlementRegex();

    private static string CString(ReadOnlySpan<byte> s)
    {
        var end = s.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? s : s[..end]);
    }
}
