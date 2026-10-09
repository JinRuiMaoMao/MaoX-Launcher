using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using static MaoX.Core.I18n;

namespace MaoX;

/// <summary>
/// 主题色与亮色 / 深色模式。Styles/Theme.axaml 里的画刷都按深色主题、科技青写好，
/// 这里记下每个画刷的原始颜色，切换时按原色值换算出新颜色直接改画刷（样式里都是 StaticResource 引用同一个画刷对象）。
/// </summary>
public static class ThemeManager
{
    public const string DefaultAccent = "#00D9FF";

    /// <summary>预设主题色（名称, 颜色）。</summary>
    public static readonly (string Name, string Color)[] Presets =
    [
        (T("科技青"), "#00D9FF"), (T("天空蓝"), "#4C9AFF"), (T("草方块绿"), "#4ADE80"), (T("薄荷绿"), "#2DD4BF"),
        (T("紫水晶"), "#A78BFA"), (T("樱花粉"), "#F472B6"), (T("落日橙"), "#FB923C"), (T("金块黄"), "#FACC15"),
    ];

    private static readonly Color DarkBg = Color.Parse("#151820");
    private static readonly Color LightBg = Color.Parse("#F2F4F8");

    /// <summary>深色 → 亮色的对应关系（按 ARGB 原值）。</summary>
    private static readonly Dictionary<uint, Color> LightColors = new[]
    {
        ("#151820", "#F2F4F8"), ("#10131A", "#E8ECF2"), ("#1A1E28", "#FFFFFF"), ("#252A36", "#DDE2EA"),
        ("#1F2430", "#F6F8FB"), ("#2C3242", "#CBD2DD"), ("#3A4256", "#A9B3C4"), ("#232836", "#EDF0F5"),
        ("#2B3142", "#E2E7EE"), ("#E8ECF3", "#1B2130"), ("#8A93A6", "#586174"), ("#596174", "#8A93A5"),
        ("#FF5C7A", "#D92D50"), ("#3A1A24", "#FCE6EB"), ("#FFB547", "#B86E00"), ("#3A2E1A", "#FFF0D9"),
        ("#3DDC97", "#0F9960"), ("#16352A", "#DCF5EA"), ("#B00A0C12", "#800A0C12"), ("#2C3342", "#D5DBE4"),
        ("#20252F", "#FFFFFF"), ("#323A4A", "#D5DBE4"), ("#1A0A0F", "#FFFFFF"), ("#FF7F96", "#E8496A"),
        ("#181C25", "#DFE4EC"), ("#151922", "#FFFFFF"), ("#AEB6C6", "#3A4252"), ("#1D222D", "#E3E7EE"),
        ("#0C0F15", "#F2F4F8"), ("#9910131A", "#B8E8ECF2"), ("#B81A1E28", "#C8FFFFFF"),
        ("#30FFFFFF", "#24000000"), ("#26FFFFFF", "#1C000000"), ("#8010131A", "#B0F4F6FA"),
    }.ToDictionary(p => Color.Parse(p.Item1).ToUInt32(), p => Color.Parse(p.Item2));

    private static readonly Dictionary<SolidColorBrush, Color> Originals = new(ReferenceEqualityComparer.Instance);

    public static bool IsLight { get; private set; }

    public static Color Accent { get; private set; } = Color.Parse(DefaultAccent);

    /// <summary>主题变化后通知（例如需要重绘自己算颜色的控件）。</summary>
    public static event Action Changed;

    private static string _mode = "dark";
    private static bool _watchingSystem;

    /// <summary>mode: dark / light / system；accent: #RRGGBB。</summary>
    public static void Apply(string mode, string accent)
    {
        var app = Application.Current!;
        if (Originals.Count == 0)
            Capture(app);
        _mode = mode is "light" or "system" ? mode : "dark";
        if (_mode == "system" && !_watchingSystem && app.PlatformSettings is { } settings)
        {
            _watchingSystem = true;
            settings.ColorValuesChanged += (_, _) =>
            {
                if (_mode == "system")
                    Apply(_mode, Accent.ToString());
            };
        }
        IsLight = _mode switch
        {
            "light" => true,
            "system" => app.PlatformSettings?.GetColorValues().ThemeVariant == PlatformThemeVariant.Light,
            _ => false,
        };
        Accent = TryParse(accent, out var color) ? color : Color.Parse(DefaultAccent);

        var a = AdjustAccent(Accent, IsLight);
        foreach (var (brush, original) in Originals)
            brush.Color = Map(original, a);

        app.RequestedThemeVariant = IsLight ? ThemeVariant.Light : ThemeVariant.Dark;
        foreach (var style in app.Styles)
        {
            if (style is not FluentTheme fluent)
                continue;
            foreach (var (variant, palette) in fluent.Palettes)
                FillPalette(palette, variant == ThemeVariant.Light, a);
        }
        Changed?.Invoke();
    }

    public static bool TryParse(string text, out Color color)
    {
        color = default;
        text = (text ?? "").Trim();
        if (!text.StartsWith('#'))
            text = "#" + text;
        if (text.Length != 7 || !Color.TryParse(text, out color))
            return false;
        return true;
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static void Capture(Application app)
    {
        foreach (var style in app.Styles)
        {
            if (style is not Styles styles || style is FluentTheme)
                continue;
            // 编译后的 XAML 资源是延迟创建的，Values 里可能还是占位对象，按键取一次才会真正创建
            foreach (var key in styles.Resources.Keys.ToList())
            {
                if (styles.Resources.TryGetResource(key, null, out var value) && value is SolidColorBrush brush)
                    Originals[brush] = brush.Color;
            }
        }
    }

    // ------------------------------------------------------------------ 颜色换算

    /// <summary>深色背景上主题色要够亮，亮色背景上要够深，否则文字看不清。</summary>
    private static Color AdjustAccent(Color color, bool light)
    {
        var hsl = color.ToHsl();
        var l = light ? Math.Min(hsl.L, 0.5) : Math.Max(hsl.L, 0.55);
        return HslColor.ToRgb(hsl.H, hsl.S, l);
    }

    private static Color Lighten(Color color, double delta)
    {
        var hsl = color.ToHsl();
        return HslColor.ToRgb(hsl.H, hsl.S, Math.Clamp(hsl.L + delta, 0, 1));
    }

    private static Color Mix(Color from, Color to, double amount) => Color.FromArgb(
        255,
        (byte)Math.Round(from.R + (to.R - from.R) * amount),
        (byte)Math.Round(from.G + (to.G - from.G) * amount),
        (byte)Math.Round(from.B + (to.B - from.B) * amount));

    private static double Luminance(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255;

    private static Color OnAccent(Color accent) =>
        Luminance(accent) > 0.6 ? Color.Parse("#06141A") : Color.Parse("#FFFFFF");

    private static Color Map(Color original, Color accent)
    {
        var bg = IsLight ? LightBg : DarkBg;
        var hoverDelta = IsLight ? -0.06 : 0.12;
        var pressDelta = IsLight ? -0.12 : -0.08;
        var rgb = original.ToUInt32() & 0xFFFFFF;
        switch (rgb)
        {
            case 0x00D9FF:
                return original.A == 255 ? accent : Color.FromArgb(original.A, accent.R, accent.G, accent.B);
            case 0x5CE6FF:
                return Lighten(accent, hoverDelta);
            case 0x00B5D6:
                return Lighten(accent, pressDelta);
            case 0x00A7C4:
                return Lighten(accent, IsLight ? 0.1 : -0.1);
            case 0x0C2F3A:
                return Mix(bg, accent, IsLight ? 0.13 : 0.16);
            case 0x103A47:
                return Mix(bg, accent, IsLight ? 0.2 : 0.22);
            case 0x0E2A35:
                return Mix(bg, accent, 0.12);
            case 0x06141A:
                return OnAccent(accent);
        }
        if (IsLight && LightColors.TryGetValue(original.ToUInt32(), out var light))
            return light;
        return original;
    }

    private static void FillPalette(ColorPaletteResources palette, bool light, Color accent)
    {
        Color C(string dark) => light && LightColors.TryGetValue(Color.Parse(dark).ToUInt32(), out var c) ? c : Color.Parse(dark);
        palette.Accent = accent;
        palette.RegionColor = C("#151820");
        palette.AltHigh = C("#151820");
        palette.BaseHigh = C("#E8ECF3");
        palette.ErrorText = C("#FF5C7A");
        palette.ListLow = C("#232836");
        palette.ListMedium = C("#2B3142");
        palette.ChromeMediumLow = C("#1A1E28");
        palette.ChromeLow = C("#10131A");
        palette.ChromeMedium = C("#1F2430");
        palette.ChromeHigh = C("#2C3242");
    }

    /// <summary>MOTD 之类自带颜色的文字：在当前背景上是否看得清。</summary>
    public static bool Readable(Color color)
    {
        var l = Luminance(color);
        return IsLight ? l < 0.75 : l > 0.27;
    }
}
