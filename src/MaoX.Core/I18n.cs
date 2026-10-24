using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MaoX.Core;

/// <summary>
/// 界面语言。代码里写中文原文，T() / F() 按当前语言查 Lang/{locale}*.json。
/// 简体中文是原文；其它语言查不到时回退到英文，再没有就显示中文。
/// 启动时按设备语言选择，设置里可指定 Minecraft 26.1 的任意语言。
/// </summary>
public static class I18n
{
    public record Locale(string Key, string Name, string Region, string Parent, bool Rtl);

    public const string FollowSystem = "";
    public const string SourceLocale = "zh_cn";
    public const string EnglishLocale = "en_us";

    public static readonly Locale[] Locales = LoadLocales();

    /// <summary>设置里的选项：第一项是跟随系统，其余按游戏里的名称排序。</summary>
    public static readonly (string Key, string Name)[] Languages = BuildLanguageOptions();

    private static readonly Dictionary<string, Locale> ByKey =
        Locales.ToDictionary(l => l.Key, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zh"] = "zh_cn", ["zh-cn"] = "zh_cn", ["zh-hans"] = "zh_cn", ["zh_hans"] = "zh_cn",
        ["zh-tw"] = "zh_tw", ["zh-hant"] = "zh_tw", ["zh_hant"] = "zh_tw",
        ["zh-hk"] = "zh_hk", ["en"] = "en_us", ["en-us"] = "en_us", ["en-gb"] = "en_gb",
        ["ja"] = "ja_jp", ["ko"] = "ko_kr", ["de"] = "de_de", ["fr"] = "fr_fr",
        ["es"] = "es_es", ["pt"] = "pt_pt", ["pt-br"] = "pt_br", ["ru"] = "ru_ru",
        ["it"] = "it_it", ["nl"] = "nl_nl", ["pl"] = "pl_pl", ["uk"] = "uk_ua",
        ["vi"] = "vi_vn", ["th"] = "th_th", ["id"] = "id_id", ["tr"] = "tr_tr",
        ["ar"] = "ar_sa", ["he"] = "he_il", ["hi"] = "hi_in", ["cs"] = "cs_cz",
        ["hu"] = "hu_hu", ["ro"] = "ro_ro", ["sv"] = "sv_se", ["da"] = "da_dk",
        ["no"] = "no_no", ["nb"] = "no_no", ["nn"] = "nn_no", ["fi"] = "fi_fi",
        ["el"] = "el_gr", ["ms"] = "ms_my", ["fa"] = "fa_ir",
        ["uk"] = "uk_ua", ["be"] = "be_by", ["bg"] = "bg_bg", ["sr"] = "sr_sp",
        ["sk"] = "sk_sk", ["sl"] = "sl_si", ["hr"] = "hr_hr", ["lt"] = "lt_lt",
        ["lv"] = "lv_lv", ["et"] = "et_ee", ["ca"] = "ca_es", ["eu"] = "eu_es",
        ["gl"] = "gl_es", ["af"] = "af_za", ["is"] = "is_is", ["ga"] = "ga_ie",
        ["cy"] = "cy_gb", ["sq"] = "sq_al", ["mk"] = "mk_mk", ["kk"] = "kk_kz",
        ["hy"] = "hy_am", ["ka"] = "ka_ge", ["mn"] = "mn_mn", ["ta"] = "ta_in",
        ["kn"] = "kn_in", ["lo"] = "lo_la", ["fil"] = "fil_ph", ["tl"] = "tl_ph",
        ["az"] = "az_az", ["uz"] = "uz_uz", ["ky"] = "ky_kg", ["tt"] = "tt_ru",
        ["yi"] = "yi_de", ["fo"] = "fo_fo", ["mt"] = "mt_mt", ["bs"] = "bs_ba",
        ["se"] = "se_no", ["yo"] = "yo_ng", ["ig"] = "ig_ng", ["so"] = "so_so",
        ["haw"] = "haw_us",
    };

    private static Dictionary<string, string> _table = [];

    public static string Language { get; private set; } = SourceLocale;

    public static bool English => Language.StartsWith("en", StringComparison.OrdinalIgnoreCase);

    public static bool Cjk => Language is SourceLocale or "zh_tw" or "zh_hk" or "lzh"
        || Language.StartsWith("ja", StringComparison.OrdinalIgnoreCase)
        || Language.StartsWith("ko", StringComparison.OrdinalIgnoreCase);

    public static bool Rtl => ByKey.TryGetValue(Language, out var loc) && loc.Rtl;

    public static string DisplayName(string key)
    {
        if (string.IsNullOrEmpty(key))
            return "跟随系统 / System";
        key = Normalize(key);
        return ByKey.TryGetValue(key, out var loc) ? $"{loc.Name} ({loc.Region})" : key;
    }

    public static string Resolve(string setting)
    {
        if (!string.IsNullOrWhiteSpace(setting))
            return Normalize(setting);
        return FromCulture(CultureInfo.CurrentUICulture);
    }

    public static void SetLanguage(string setting)
    {
        Language = Resolve(setting);
        _table = LoadChain(Language);
    }

    /// <summary>不切换当前语言，只取出某套语言里的一句（设置里预览重启提示）。</summary>
    public static string Lookup(string locale, string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        var table = LoadChain(Normalize(locale));
        return table.TryGetValue(text, out var translated) ? translated : text;
    }

    public static string T(string text) =>
        text != null && _table.TryGetValue(text, out var translated) ? translated : text;

    public static string T(string text, string context) =>
        _table.TryGetValue(context + "|" + text, out var translated) ? translated : T(text);

    public static string F(string format, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(format), args);

    public static string Normalize(string key)
    {
        key = (key ?? "").Trim().Replace('-', '_').ToLowerInvariant();
        if (key.Length == 0)
            return SourceLocale;
        if (Aliases.TryGetValue(key, out var alias))
            return alias;
        if (ByKey.ContainsKey(key))
            return key;
        var head = key.Split('_')[0];
        if (Aliases.TryGetValue(head, out alias))
            return alias;
        var match = Locales.FirstOrDefault(l => l.Key.StartsWith(head + "_", StringComparison.Ordinal));
        return match?.Key ?? EnglishLocale;
    }

    public static string FromCulture(CultureInfo culture)
    {
        culture ??= CultureInfo.CurrentUICulture;
        var name = (culture.Name ?? "").Replace('-', '_');
        if (name.Length > 0)
        {
            var direct = Normalize(name);
            if (ByKey.ContainsKey(direct) && direct != EnglishLocale)
                return direct;
            if (name.StartsWith("zh_Hans", StringComparison.OrdinalIgnoreCase)
                || string.Equals(culture.TwoLetterISOLanguageName, "zh", StringComparison.OrdinalIgnoreCase)
                   && !name.Contains("Hant", StringComparison.OrdinalIgnoreCase)
                   && !name.Contains("TW", StringComparison.OrdinalIgnoreCase)
                   && !name.Contains("HK", StringComparison.OrdinalIgnoreCase)
                   && !name.Contains("MO", StringComparison.OrdinalIgnoreCase))
                return SourceLocale;
            if (name.Contains("Hant", StringComparison.OrdinalIgnoreCase)
                || name.Contains("TW", StringComparison.OrdinalIgnoreCase))
                return "zh_tw";
            if (name.Contains("HK", StringComparison.OrdinalIgnoreCase) || name.Contains("MO", StringComparison.OrdinalIgnoreCase))
                return "zh_hk";
        }
        var iso = culture.TwoLetterISOLanguageName ?? "";
        if (Aliases.TryGetValue(iso, out var mapped))
            return mapped;
        var found = Locales.FirstOrDefault(l => l.Key.StartsWith(iso + "_", StringComparison.OrdinalIgnoreCase));
        return found?.Key ?? EnglishLocale;
    }

    private static Dictionary<string, string> LoadChain(string locale)
    {
        var table = new Dictionary<string, string>();
        if (locale == SourceLocale)
            return table;
        if (!IsChinese(locale))
            Overlay(table, EnglishLocale);
        foreach (var code in Chain(locale))
            Overlay(table, code);
        return table;
    }

    private static IEnumerable<string> Chain(string locale)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var walk = locale;
        var stack = new Stack<string>();
        while (!string.IsNullOrEmpty(walk) && seen.Add(walk) && walk != SourceLocale)
        {
            stack.Push(walk);
            walk = ByKey.TryGetValue(walk, out var loc) ? loc.Parent : "";
            if (walk == EnglishLocale)
                break;
        }
        while (stack.Count > 0)
            yield return stack.Pop();
    }

    private static bool IsChinese(string locale) =>
        locale is SourceLocale or "zh_tw" or "zh_hk" or "lzh";

    private static void Overlay(Dictionary<string, string> table, string language)
    {
        var assembly = typeof(I18n).Assembly;
        foreach (var name in assembly.GetManifestResourceNames()
                                     .Where(n => IsLangFile(n, language))
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
            }
        }
    }

    private static bool IsLangFile(string resource, string language)
    {
        var mark = ".Lang.";
        var at = resource.LastIndexOf(mark, StringComparison.Ordinal);
        if (at < 0 || !resource.EndsWith(".json", StringComparison.Ordinal))
            return false;
        var file = resource[(at + mark.Length)..^5];
        if (file == language || file.StartsWith(language + "_", StringComparison.Ordinal))
            return true;
        // 现有英文目录是 en_ui1 / en_main，不是 en_us
        if (language == EnglishLocale && file.StartsWith("en_", StringComparison.Ordinal))
        {
            var rest = file[3..];
            var maybe = "en_" + rest.Split('_')[0];
            return maybe != EnglishLocale && !ByKey.ContainsKey(maybe);
        }
        return false;
    }

    private static Locale[] LoadLocales()
    {
        var assembly = typeof(I18n).Assembly;
        var name = assembly.GetManifestResourceNames().First(n => n.EndsWith(".Lang.locales.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        var json = JsonNode.Parse(stream);
        return json.AsArray().Select(n => new Locale(
            n.Str("key"), n.Str("name"), n.Str("region"), n.Str("parent", ""), n.Bool("rtl"))).ToArray();
    }

    private static (string Key, string Name)[] BuildLanguageOptions()
    {
        var items = new List<(string, string)> { (FollowSystem, "跟随系统 / System") };
        items.AddRange(Locales.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .Select(l => (l.Key, $"{l.Name} ({l.Region})")));
        return items.ToArray();
    }
}
