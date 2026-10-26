using Avalonia;
using Avalonia.Media;
using MaoX.Core;

namespace MaoX;

/// <summary>
/// 界面字体回退。FontFamily 列表只表示「第一个能加载的字体」，不会按缺字换字体，
/// 所以微软雅黑能打开时，阿拉伯文、泰文、印地文、哥特文等会变成口口或问号。
/// 缺的字形必须靠 FontManagerOptions.FontFallbacks 逐个补。
/// </summary>
public static class UiFonts
{
    public static FontManagerOptions Options { get; } = new() { FontFallbacks = Fallbacks };

    public static FontFallback[] Fallbacks { get; } = Names(Platform.IsWindows ? Windows
        : Platform.IsMac || Platform.IsIOS ? Apple
        : Platform.IsAndroid ? Android
        : Linux);

    public static AppBuilder Configure(AppBuilder builder) => builder.With(Options);

    private static readonly string[] Windows =
    [
        "Segoe UI", "Segoe UI Historic", "Microsoft YaHei UI", "Microsoft JhengHei UI",
        "Yu Gothic UI", "Meiryo UI", "Malgun Gothic", "Leelawadee UI", "Lao UI",
        "Nirmala UI", "Gadugi", "Ebrima", "Myanmar Text", "Microsoft Himalaya",
        "Javanese Text", "Segoe UI Symbol", "Segoe UI Emoji", "Arial",
    ];

    private static readonly string[] Apple =
    [
        "PingFang SC", "PingFang TC", "PingFang HK", "Hiragino Sans", "Apple SD Gothic Neo",
        "Thonburi", "Lao Sangam MN", "Geeza Pro", "Kohinoor Devanagari", "Tamil Sangam MN",
        "Kannada Sangam MN", "New Peninim MT", "Mukta Mahee", "Kailasa",
        "Noto Nastaliq Urdu", "Apple Symbols", "Helvetica Neue",
    ];

    private static readonly string[] Android =
    [
        "sans-serif", "Noto Sans CJK SC", "Noto Sans CJK TC", "Noto Sans CJK JP", "Noto Sans CJK KR",
        "Noto Naskh Arabic", "Noto Sans Arabic", "Noto Sans Thai", "Noto Sans Lao", "Noto Sans Hebrew",
        "Noto Sans Devanagari", "Noto Sans Tamil", "Noto Sans Kannada", "Noto Sans Georgian",
        "Noto Sans Armenian", "Noto Sans", "Droid Sans Fallback",
    ];

    private static readonly string[] Linux =
    [
        "Noto Sans CJK SC", "Noto Sans CJK TC", "Noto Sans CJK JP", "Noto Sans CJK KR",
        "Noto Sans Arabic", "Noto Naskh Arabic", "Noto Sans Thai", "Noto Sans Lao", "Noto Sans Hebrew",
        "Noto Sans Devanagari", "Noto Sans Tamil", "Noto Sans Kannada", "Noto Sans Georgian",
        "Noto Sans Armenian", "Noto Sans Gothic", "Noto Sans", "DejaVu Sans", "FreeSans",
        "WenQuanYi Micro Hei", "Source Han Sans SC",
    ];

    private static FontFallback[] Names(string[] families) =>
        [.. families.Select(name => new FontFallback { FontFamily = new FontFamily(name) })];
}
