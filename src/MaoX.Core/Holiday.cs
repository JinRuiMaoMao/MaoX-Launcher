namespace MaoX.Core;

/// <summary>节日主题：到日子自动换主题色和祝福语。</summary>
public sealed record HolidayTheme(string Key, string Name, string Greeting, string Accent, DateTime Sample);

public static class Holiday
{
    public static HolidayTheme Active { get; set; }

    public static readonly HolidayTheme[] All =
    [
        new("newyear", "元旦", "新年快乐", "#EAB308", new DateTime(2026, 1, 1)),
        new("spring", "春节", "新春快乐", "#E11D48", new DateTime(2026, 2, 17)),
        new("lantern", "元宵", "元宵快乐", "#F97316", new DateTime(2026, 3, 3)),
        new("valentine", "情人节", "情人节快乐", "#FB7185", new DateTime(2026, 2, 14)),
        new("womensday", "妇女节", "女神节快乐", "#E879F9", new DateTime(2026, 3, 8)),
        new("aprilfools", "愚人节", "愚人节快乐", "#F472B6", new DateTime(2026, 4, 1)),
        new("labor", "劳动节", "劳动节快乐", "#4ADE80", new DateTime(2026, 5, 1)),
        new("children", "儿童节", "儿童节快乐", "#38BDF8", new DateTime(2026, 6, 1)),
        new("dragonboat", "端午", "端午安康", "#14B8A6", new DateTime(2026, 6, 19)),
        new("qixi", "七夕", "七夕快乐", "#C084FC", new DateTime(2026, 8, 19)),
        new("midautumn", "中秋", "中秋快乐", "#F59E0B", new DateTime(2026, 9, 25)),
        new("national", "国庆", "国庆快乐", "#EF4444", new DateTime(2026, 10, 1)),
        new("halloween", "万圣节", "万圣节快乐", "#FB923C", new DateTime(2026, 10, 31)),
        new("christmas", "圣诞节", "圣诞快乐", "#22C55E", new DateTime(2026, 12, 25)),
    ];

    // 农历节日的公历日（2024–2032）。春节窗口是当天起 7 天，其余前后各 1 天。
    private static readonly Dictionary<int, DateTime> SpringDay = Dates(
        (2024, 2, 10), (2025, 1, 29), (2026, 2, 17), (2027, 2, 6), (2028, 1, 26),
        (2029, 2, 13), (2030, 2, 3), (2031, 1, 23), (2032, 2, 11));
    private static readonly Dictionary<int, DateTime> DragonBoatDay = Dates(
        (2024, 6, 10), (2025, 5, 31), (2026, 6, 19), (2027, 6, 9), (2028, 5, 28),
        (2029, 6, 16), (2030, 6, 5), (2031, 6, 25), (2032, 6, 14));
    private static readonly Dictionary<int, DateTime> QixiDay = Dates(
        (2024, 8, 10), (2025, 8, 29), (2026, 8, 19), (2027, 8, 11), (2028, 8, 29),
        (2029, 8, 18), (2030, 8, 8), (2031, 8, 27), (2032, 8, 16));
    private static readonly Dictionary<int, DateTime> MidAutumnDay = Dates(
        (2024, 9, 17), (2025, 10, 6), (2026, 9, 25), (2027, 9, 15), (2028, 10, 3),
        (2029, 9, 22), (2030, 9, 12), (2031, 10, 1), (2032, 9, 19));

    public static HolidayTheme Find(string key) => All.FirstOrDefault(t => t.Key == key);

    public static HolidayTheme Current(LauncherConfig cfg, DateTime? now = null, HolidayTheme preview = null)
    {
        if (preview != null)
            return preview;
        if (cfg?.HolidayTheme == false)
            return null;
        return Match(now ?? DateTime.Now);
    }

    public static HolidayTheme Match(DateTime now)
    {
        var d = now.Date;
        if (Around(d, LanternDay(d.Year), 1) || Around(d, LanternDay(d.Year - 1), 1))
            return Find("lantern");
        if (Around(d, Lunar(DragonBoatDay, d.Year), 1) || Around(d, Lunar(DragonBoatDay, d.Year - 1), 1))
            return Find("dragonboat");
        if (Around(d, Lunar(QixiDay, d.Year), 1) || Around(d, Lunar(QixiDay, d.Year - 1), 1))
            return Find("qixi");
        if (Around(d, Lunar(MidAutumnDay, d.Year), 1) || Around(d, Lunar(MidAutumnDay, d.Year - 1), 1))
            return Find("midautumn");
        if (InSpring(d))
            return Find("spring");
        if (d.Month == 1 && d.Day <= 3)
            return Find("newyear");
        if (d.Month == 2 && d.Day == 14)
            return Find("valentine");
        if (d.Month == 3 && d.Day == 8)
            return Find("womensday");
        if (d.Month == 4 && d.Day == 1)
            return Find("aprilfools");
        if (d.Month == 5 && d.Day <= 3)
            return Find("labor");
        if (d.Month == 6 && d.Day == 1)
            return Find("children");
        if (d.Month == 10 && d.Day <= 7)
            return Find("national");
        if (d.Month == 10 && d.Day == 31)
            return Find("halloween");
        if (d.Month == 12 && d.Day is >= 20 and <= 26)
            return Find("christmas");
        return null;
    }

    public static string AccentOrNull(DateTime? now = null) => Match(now ?? DateTime.Now)?.Accent;

    public static string ResolveAccent(LauncherConfig cfg, DateTime? now = null) =>
        Current(cfg, now)?.Accent ?? cfg?.AccentColor;

    private static bool InSpring(DateTime d)
    {
        var start = Lunar(SpringDay, d.Year);
        if (start != default && d >= start && d < start.AddDays(7))
            return true;
        var prev = Lunar(SpringDay, d.Year - 1);
        return prev != default && d >= prev && d < prev.AddDays(7);
    }

    private static DateTime LanternDay(int year)
    {
        var spring = Lunar(SpringDay, year);
        return spring == default ? default : spring.AddDays(14);
    }

    private static DateTime Lunar(Dictionary<int, DateTime> table, int year) =>
        table.TryGetValue(year, out var day) ? day : default;

    private static bool Around(DateTime day, DateTime center, int pad) =>
        center != default && day >= center.AddDays(-pad) && day <= center.AddDays(pad);

    private static Dictionary<int, DateTime> Dates(params (int Y, int M, int D)[] items) =>
        items.ToDictionary(i => i.Y, i => new DateTime(i.Y, i.M, i.D));
}
