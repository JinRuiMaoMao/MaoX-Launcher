using System.Globalization;
using static MaoX.Core.I18n;

namespace MaoX.Core;

/// <summary>每个版本的游戏时长与最近游玩时间，保存在版本文件夹的 maox.json（play_seconds、last_played）。</summary>
public static class PlayTime
{
    private static readonly object Sync = new();

    public static void Record(GameLauncher gl, string version, DateTime start, DateTime end)
    {
        var seconds = (long)(end - start).TotalSeconds;
        if (seconds <= 0 || !Directory.Exists(gl.VersionDir(version)))
            return;
        lock (Sync)
        {
            var settings = gl.VersionSettings(version);
            settings["play_seconds"] = settings.Long("play_seconds") + seconds;
            settings["last_played"] = end.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
            gl.SaveVersionSettings(version, settings);
        }
    }

    public static (TimeSpan Total, DateTime? LastPlayed) Get(GameLauncher gl, string version)
    {
        var settings = gl.VersionSettings(version);
        DateTime? last = DateTime.TryParse(settings.Str("last_played"), CultureInfo.InvariantCulture,
                                           DateTimeStyles.RoundtripKind, out var t)
            ? t.ToLocalTime()
            : null;
        return (TimeSpan.FromSeconds(settings.Long("play_seconds")), last);
    }

    /// <summary>例如"已玩 3 小时 20 分钟 · 上次游玩 昨天"；从没玩过时返回空字符串。</summary>
    public static string Describe(GameLauncher gl, string version)
    {
        var (total, last) = Get(gl, version);
        if (last == null && total == TimeSpan.Zero)
            return "";
        var parts = new List<string>();
        if (total > TimeSpan.Zero)
            parts.Add(F("已玩 {0}", FormatDuration(total)));
        if (last != null)
            parts.Add(F("上次游玩 {0}", FormatAgo(last.Value)));
        return string.Join("  ·  ", parts);
    }

    public static string FormatDuration(TimeSpan span)
    {
        if (span.TotalMinutes < 1)
            return T("不到 1 分钟");
        var hours = (int)span.TotalHours;
        return hours == 0 ? F("{0} 分钟", span.Minutes)
            : span.Minutes == 0 ? F("{0} 小时", hours)
            : F("{0} 小时 {1} 分钟", hours, span.Minutes);
    }

    public static string FormatAgo(DateTime time)
    {
        var days = (DateTime.Today - time.Date).Days;
        return days switch
        {
            <= 0 when (DateTime.Now - time).TotalMinutes < 5 => T("刚刚"),
            <= 0 => F("今天 {0}", time.ToString("HH:mm")),
            1 => T("昨天"),
            < 7 => F("{0} 天前", days),
            _ => time.Year == DateTime.Today.Year ? time.ToString(T("M 月 d 日")) : time.ToString(T("yyyy 年 M 月 d 日")),
        };
    }
}
