using System.Globalization;

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
            parts.Add("已玩 " + FormatDuration(total));
        if (last != null)
            parts.Add("上次游玩 " + FormatAgo(last.Value));
        return string.Join("  ·  ", parts);
    }

    public static string FormatDuration(TimeSpan span)
    {
        if (span.TotalMinutes < 1)
            return "不到 1 分钟";
        var hours = (int)span.TotalHours;
        return hours == 0 ? $"{span.Minutes} 分钟"
            : span.Minutes == 0 ? $"{hours} 小时"
            : $"{hours} 小时 {span.Minutes} 分钟";
    }

    public static string FormatAgo(DateTime time)
    {
        var days = (DateTime.Today - time.Date).Days;
        return days switch
        {
            <= 0 when (DateTime.Now - time).TotalMinutes < 5 => "刚刚",
            <= 0 => "今天 " + time.ToString("HH:mm"),
            1 => "昨天",
            < 7 => $"{days} 天前",
            _ => time.Year == DateTime.Today.Year ? time.ToString("M 月 d 日") : time.ToString("yyyy 年 M 月 d 日"),
        };
    }
}
