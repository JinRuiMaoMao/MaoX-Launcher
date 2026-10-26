using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static MaoX.Core.I18n;

namespace MaoX.Core;

/// <summary>Minecraft Wiki 首页 Latest / Upcoming，以及 minecraft.net 新闻页置顶文章。</summary>
public static class McSiteFeed
{
    public const string Wiki = "https://minecraft.wiki";
    public const string MinecraftNet = "https://www.minecraft.net";
    public const string LauncherNews = "https://launchercontent.mojang.com/v2/news.json";

    public record Item(string Title, string Text, string Url, string Image = null, string ImageLight = null);

    public record Snapshot(Item Latest, Item Upcoming, Item News);

    /// <summary>官网新闻页语言码，跟当前启动器语言走。</summary>
    public static string SiteLang() => Language switch
    {
        "zh_cn" => "zh-hans",
        "zh_tw" or "zh_hk" or "lzh" => "zh-hant",
        "ja_jp" => "ja-jp",
        "ko_kr" => "ko-kr",
        "de_de" or "de_at" or "de_ch" => "de-de",
        "fr_fr" or "fr_ca" or "fr_ch" => "fr-fr",
        "es_es" or "es_mx" or "es_ar" => "es-es",
        "pt_br" => "pt-br",
        "pt_pt" => "pt-pt",
        "ru_ru" => "ru-ru",
        "it_it" => "it-it",
        "pl_pl" => "pl-pl",
        _ => "en-us",
    };

    public static string ArticlesUrl => $"{MinecraftNet}/{SiteLang()}/articles";

    public static async Task<Snapshot> FetchAsync(CancellationToken cancel = default)
    {
        Item latest = null, upcoming = null, article = null;
        var page = Http.SendAsync(
            Wiki + "/api.php?action=parse&page=Minecraft_Wiki&prop=text&format=json&formatversion=2",
            timeout: 15, cancel: cancel);
        var articles = Http.SendAsync(ArticlesUrl, timeout: 15, cancel: cancel);
        var news = Http.SendAsync(LauncherNews, timeout: 15, cancel: cancel);
        try
        {
            var result = await page;
            if (result.Ok)
            {
                foreach (var (tag, item) in ParseDrops(result.Json().Obj("parse")?.Str("text") ?? ""))
                {
                    if (tag.Equals("Latest", StringComparison.OrdinalIgnoreCase) && latest == null)
                        latest = item;
                    else if (tag.Equals("Upcoming", StringComparison.OrdinalIgnoreCase) && upcoming == null)
                        upcoming = item;
                }
            }
        }
        catch (Exception)
        {
        }
        try
        {
            var result = await articles;
            if (result.Ok)
                article = ParseArticles(result.Text).FirstOrDefault();
        }
        catch (Exception)
        {
        }
        if (article == null)
        {
            try
            {
                var result = await news;
                if (result.Ok)
                    article = ParseNews(result.Json()).FirstOrDefault();
            }
            catch (Exception)
            {
            }
        }
        return new Snapshot(latest, upcoming, article);
    }

    public static Dictionary<string, string> ParseVersionMap(string wikitext)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(wikitext ?? "", @"\|\s*([a-z0-9_]+(?:-[a-z0-9_]+)*)\s*=\s*([^\n|]*?)\s*(?=\n|\||$)"))
        {
            var value = match.Groups[2].Value.Trim();
            if (value.Length > 0)
                map[match.Groups[1].Value] = value;
        }
        return map;
    }

    public static List<(string Tag, Item Item)> ParseDrops(string html)
    {
        html = StripStyles(html);
        var list = new List<(string, Item)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(html,
                     @"class=""[^""]*mp-highlight-title[^""]*""[^>]*>(.*?)</div>.*?class=""[^""]*mp-highlight-tag[^""]*""[^>]*>(.*?)</div>",
                     RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            var titleHtml = match.Groups[1].Value;
            var title = Plain(titleHtml);
            var tag = Plain(match.Groups[2].Value);
            if (title.Length == 0 || tag.Length == 0 || title.Contains('{') || title.Length > 80
                || !seen.Add(tag + "\0" + title))
                continue;
            var href = Regex.Match(titleHtml, @"href=""([^""]+)""", RegexOptions.IgnoreCase);
            var start = Math.Max(0, match.Index - 2800);
            var window = html[start..match.Index];
            list.Add((tag, new Item(title, "",
                AbsoluteWiki(href.Success ? href.Groups[1].Value : "/w/" + title.Replace(' ', '_')),
                FirstImage(window, "mp-dark-only") ?? FirstImage(window, null),
                FirstImage(window, "mp-light-only"))));
        }
        return list;
    }

    private static string FirstImage(string html, string hint)
    {
        var pattern = hint == null
            ? @"<img[^>]+src=""([^""]+)"""
            : Regex.Escape(hint) + @"[\s\S]{0,500}?<img[^>]+src=""([^""]+)""";
        string found = null;
        foreach (Match match in Regex.Matches(html ?? "", pattern, RegexOptions.IgnoreCase))
        {
            var src = System.Net.WebUtility.HtmlDecode(match.Groups[1].Value);
            if (src.Contains("Sprite", StringComparison.OrdinalIgnoreCase))
                continue;
            found = AbsoluteWiki(src);
        }
        return found;
    }

    private static string StripStyles(string html) =>
        Regex.Replace(html ?? "", @"<style\b[^>]*>.*?</style>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);

    public static List<Item> ParseNews(JsonNode json)
    {
        return json.Items("entries").Select(entry =>
        {
            var image = entry.Obj("newsPageImage")?.Str("url") ?? entry.Obj("playPageImage")?.Str("url");
            if (!string.IsNullOrWhiteSpace(image) && image.StartsWith('/'))
                image = "https://launchercontent.mojang.com" + image;
            return new Item((entry.Str("title") ?? "").Trim(), Plain(entry.Str("text") ?? ""),
                            (entry.Str("readMoreLink") ?? "").Trim(), image);
        }).Where(item => item.Title.Length > 0).Take(1).ToList();
    }

    /// <summary>按官网 /articles 置顶卡片顺序解析，第一张就是页面上最前面那条。</summary>
    public static List<Item> ParseArticles(string html)
    {
        var list = new List<Item>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match card in Regex.Matches(html ?? "",
                     @"class=""MC_tiledHeroA_card""[\s\S]*?(?=class=""MC_tiledHeroA_card""|data-mc-mount=""ResultGroupAApp""|$)",
                     RegexOptions.IgnoreCase))
        {
            var block = card.Value;
            var title = Plain(Regex.Match(block, @"<h2\b[^>]*>([\s\S]*?)</h2>", RegexOptions.IgnoreCase).Groups[1].Value);
            var href = Regex.Match(block, @"href=""(/[^""]*/article/[^""]+)""", RegexOptions.IgnoreCase);
            if (title.Length == 0 || !href.Success || !seen.Add(href.Groups[1].Value))
                continue;
            var blurb = Regex.Match(block, @"class=""MC_tiledHeroA_blurb""[^>]*>([\s\S]*?)</div>", RegexOptions.IgnoreCase);
            var img = Regex.Match(block, @"<img[^>]+src=""([^""]+)""", RegexOptions.IgnoreCase);
            var image = img.Success ? AbsoluteNet(System.Net.WebUtility.HtmlDecode(img.Groups[1].Value)) : null;
            list.Add(new Item(title, blurb.Success ? Plain(blurb.Groups[1].Value) : "", AbsoluteNet(href.Groups[1].Value), image));
        }
        return list;
    }

    public static string WikiPage(string title)
    {
        title = (title ?? "").Trim().Replace(' ', '_');
        return AbsoluteWiki("/w/" + title);
    }

    public static string AbsoluteWiki(string href)
    {
        href = (href ?? "").Trim();
        if (href.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return href;
        if (!href.StartsWith('/'))
            href = "/" + href;
        return Wiki + href;
    }

    public static string AbsoluteNet(string href)
    {
        href = (href ?? "").Trim();
        if (href.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return href;
        if (!href.StartsWith('/'))
            href = "/" + href;
        return MinecraftNet + href;
    }

    public static string Plain(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return "";
        var text = Regex.Replace(html, "<[^>]+>", " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length > 180 ? text[..180] + "…" : text;
    }
}
