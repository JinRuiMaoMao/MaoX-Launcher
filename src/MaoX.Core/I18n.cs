using System.Globalization;
using System.Text.Json;

namespace MaoX.Core;

/// <summary>
/// 界面语言。代码里直接写中文原文，T() / F() 按当前语言查翻译表（Lang/en_*.json，键为中文原文、值为英文），
/// 查不到时原样返回中文。语言在启动时确定，切换后需要重启启动器。
/// </summary>
public static class I18n
{
    /// <summary>设置里的选项：空字符串表示跟随系统。</summary>
    public static readonly (string Key, string Name)[] Languages = [("", "跟随系统 / System"), ("zh", "简体中文"), ("en", "English")];

    private static Dictionary<string, string> _table = [];

    public static string Language { get; private set; } = "zh";

    public static bool English => Language == "en";

    public static string Resolve(string setting) =>
        setting is "zh" or "en"
            ? setting
            : CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh" : "en";

    public static void SetLanguage(string setting)
    {
        Language = Resolve(setting);
        _table = Language == "zh" ? [] : Load(Language);
    }

    private static Dictionary<string, string> Load(string language)
    {
        var table = new Dictionary<string, string>();
        var assembly = typeof(I18n).Assembly;
        var marker = $".Lang.{language}_";
        foreach (var name in assembly.GetManifestResourceNames()
                                     .Where(n => n.Contains(marker, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
                                     .Order(StringComparer.Ordinal))
        {
            try
            {
                using var stream = assembly.GetManifestResourceStream(name)!;
                foreach (var (key, value) in JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [])
                {
                    if (!string.IsNullOrEmpty(value))
                        table[key] = value;
                }
            }
            catch (JsonException)
            {
                // 某个翻译文件损坏时跳过它，对应的文字显示中文
            }
        }
        return table;
    }

    /// <summary>翻译一段固定文字。</summary>
    public static string T(string text) => text != null && _table.TryGetValue(text, out var translated) ? translated : text;

    /// <summary>同一句中文在不同地方需要不同译法时，先查「context|原文」。</summary>
    public static string T(string text, string context) =>
        _table.TryGetValue(context + "|" + text, out var translated) ? translated : T(text);

    /// <summary>翻译带 {0} {1} 占位符的文字后再格式化。</summary>
    public static string F(string format, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(format), args);
}
