using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static MaoX.Core.I18n;

namespace MaoX.Core;

/// <summary>Minecraft Wiki 首页 Latest / Upcoming，以及 minecraft.net 最新新闻。</summary>
public static class McSiteFeed
{
    public const string Wiki = "https://minecraft.wiki";
    public const string MinecraftNet = "https://www.minecraft.net";

    public record Item(string Title, string Text, string Url);

    public record Snapshot(List<Item> Latest, List<Item> Upcoming, List<Item> News);

    public static async Task<Snapshot> FetchAsync(CancellationToken cancel = default)
    {
        var version = Http.SendAsync(
            Wiki + "/api.php?action=parse&page=Template:Version&prop=wikitext&format=json&formatversion=2",
            timeout: 15, cancel: cancel);
        var page = Http.SendAsync(
            Wiki + "/api.php?action=parse&page=Minecraft_Wiki&prop=text&format=json&formatversion=2",
            timeout: 15, cancel: cancel);
        var news = Http.SendAsync("https://launchercontent.mojang.com/v2/news.json", timeout: 15, cancel: cancel);

        var latest = new List<Item>();
        var upcoming = new List<Item>();
        var articles = new List<Item>();
        try
        {
            var result = await version;
            if (result.Ok)
                ApplyVersions(ParseVersionMap(result.Json().Obj("parse")?.Str("wikitext") ?? ""), latest, upcoming);
        }
        catch (Exception)
        {
        }
        try
        {
            var result = await page;
            if (result.Ok)
                ApplyDrops(ParseDrops(result.Json().Obj("parse")?.Str("text") ?? ""), latest, upcoming);
        }
        catch (Exception)
        {
        }
        try
        {
            var result = await news;
            if (result.Ok)
                articles.AddRange(ParseNews(result.Json()));
        }
        catch (Exception)
        {
        }
        return new Snapshot(latest, upcoming, articles);
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
            list.Add((tag, new Item(title, "", AbsoluteWiki(href.Success ? href.Groups[1].Value : "/w/" + title.Replace(' ', '_')))));
        }
        return list;
    }

    private static string StripStyles(string html) =>
        Regex.Replace(html ?? "", @"<style\b[^>]*>.*?</style>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);

    public static List<Item> ParseNews(JsonNode json)
    {
        return json.Items("entries").Select(entry => new Item(
            (entry.Str("title") ?? "").Trim(),
            Plain(entry.Str("text") ?? ""),
            (entry.Str("readMoreLink") ?? "").Trim())).Where(item => item.Title.Length > 0).Take(8).ToList();
    }

    private static void ApplyVersions(Dictionary<string, string> map, List<Item> latest, List<Item> upcoming)
    {
        AddVersion(latest, T("Java 版"), map, "java", "Java Edition ");
        AddVersion(latest, T("基岩版"), map, "bedrock", "Bedrock Edition ");
        AddVersion(upcoming, T("Java 版"), map, "java-snap", "Java Edition ", "java-upcoming");
        AddVersion(upcoming, T("基岩版"), map, "bedrock-preview", "Bedrock Edition ", "bedrock-upcoming2", "bedrock-upcoming");
    }

    private static void AddVersion(List<Item> list, string edition, Dictionary<string, string> map, string key,
                                   string prefix, params string[] fallbacks)
    {
        var value = map.GetValueOrDefault(key);
        if (string.IsNullOrWhiteSpace(value))
        {
            foreach (var alt in fallbacks)
            {
                value = map.GetValueOrDefault(alt);
                if (!string.IsNullOrWhiteSpace(value))
                    break;
            }
        }
        if (string.IsNullOrWhiteSpace(value))
            return;
        list.Add(new Item($"{edition} · {value}", "", WikiPage(prefix + value)));
    }

    private static void ApplyDrops(List<(string Tag, Item Item)> drops, List<Item> latest, List<Item> upcoming)
    {
        foreach (var (tag, item) in drops)
        {
            if (tag.Equals("Latest", StringComparison.OrdinalIgnoreCase))
                latest.Insert(0, item);
            else if (tag.Equals("Upcoming", StringComparison.OrdinalIgnoreCase))
                upcoming.Insert(0, item);
        }
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
