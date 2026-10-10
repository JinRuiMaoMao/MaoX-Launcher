using System.Diagnostics.CodeAnalysis;
using System.Xml.Linq;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;
using SkiaSharp;

namespace MaoX.Android;

/// <summary>
/// 部分安卓系统（安卓 9、雷电/MuMu 等模拟器）上 Avalonia 找不到系统的中文回退字体，中文会显示成方块。
/// 这里按 /system/etc/fonts.xml 找到简体中文用的字体，作为全局回退字体加载。
/// </summary>
internal sealed class ChineseFont : FontCollectionBase
{
    public static readonly Uri CollectionKey = new("fonts:MaoXChinese");

    /// <summary>回退字体，用在 FontManagerOptions.FontFallbacks 里。</summary>
    public static readonly FontFamily Family = new(CollectionKey + "#MaoXChinese");

    private readonly SKTypeface _skia;
    private IGlyphTypeface _typeface;
    private readonly Dictionary<FontCollectionKey, IGlyphTypeface> _styles = [];

    private ChineseFont(SKTypeface skia) => _skia = skia;

    /// <summary>找到系统的简体中文字体；没有时返回 null。</summary>
    public static ChineseFont Load()
    {
        try
        {
            var (file, index) = FindSystemFont();
            var skia = file == null ? null : SKTypeface.FromFile(file, index);
            return skia == null ? null : new ChineseFont(skia);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static (string File, int Index) FindSystemFont()
    {
        foreach (var config in new[] { "/system/etc/font_fallback.xml", "/system/etc/fonts.xml" })
        {
            if (!File.Exists(config))
                continue;
            var family = XDocument.Load(config).Root?.Descendants("family")
                                  .FirstOrDefault(f => ((string)f.Attribute("lang") ?? "").Split(' ', ',')
                                                           .Any(l => l is "zh-Hans" or "zh-CN" or "zh"));
            var font = family?.Elements("font").FirstOrDefault(f => f.Attribute("fallbackFor") == null)
                       ?? family?.Elements("font").FirstOrDefault();
            if (font == null)
                continue;
            var path = Path.Combine("/system/fonts", font.Value.Trim());
            if (File.Exists(path))
                return (path, (int?)font.Attribute("index") ?? 0);
        }
        foreach (var (name, index) in new[] { ("NotoSansCJK-Regular.ttc", 2), ("NotoSansSC-Regular.otf", 0),
                                              ("DroidSansFallbackFull.ttf", 0), ("DroidSansFallback.ttf", 0) })
        {
            var path = Path.Combine("/system/fonts", name);
            if (File.Exists(path))
                return (path, index);
        }
        return (null, 0);
    }

    public override Uri Key => CollectionKey;

    public override int Count => 1;

    public override FontFamily this[int index] => Family;

    public override IEnumerator<FontFamily> GetEnumerator()
    {
        yield return Family;
    }

    // Avalonia 11.3 没有公开从文件创建字体的接口，直接用 Skia 后端的实现类
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, "Avalonia.Skia.GlyphTypefaceImpl", "Avalonia.Skia")]
    public override void Initialize(IFontManagerImpl fontManager)
    {
        var type = Type.GetType("Avalonia.Skia.GlyphTypefaceImpl, Avalonia.Skia", true)!;
        _typeface = (IGlyphTypeface)Activator.CreateInstance(type, _skia, FontSimulations.None)!;
    }

    /// <summary>只有一个字体，粗体等样式由 Avalonia 模拟。</summary>
    public override bool TryGetGlyphTypeface(string familyName, FontStyle style, FontWeight weight, FontStretch stretch,
                                             out IGlyphTypeface glyphTypeface)
    {
        glyphTypeface = _typeface;
        if (_typeface == null)
            return false;
        var key = new FontCollectionKey(style, weight, stretch);
        lock (_styles)
        {
            if (!_styles.TryGetValue(key, out glyphTypeface))
            {
                if (!TryCreateSyntheticGlyphTypeface(_typeface, style, weight, stretch, out glyphTypeface))
                    glyphTypeface = _typeface;
                _styles[key] = glyphTypeface;
            }
        }
        return true;
    }
}
