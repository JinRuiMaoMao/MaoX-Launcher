using System.Text.Json.Nodes;

namespace MaoX.Core;

/// <summary>
/// 手机触屏按键布局。按键位置是中心点占屏幕宽高的比例，换设备、换分辨率后位置大致不变；
/// 大小是相对默认大小的倍数。游戏里的 ControlsView（安卓 Java、iOS）按它摆放按键，没写到的按键用默认位置。
/// </summary>
public class ControlLayout
{
    public const double MinSize = 0.5, MaxSize = 2, MinOpacity = 0.2;

    /// <summary>可以摆放的按键，顺序也是编辑器里的绘制顺序。</summary>
    public static readonly string[] Ids = ["stick", "jump", "sneak", "inventory", "drop", "chat", "view", "pause", "keyboard"];

    public string Name { get; set; } = "";
    /// <summary>所有按键的不透明度（0.2–1）</summary>
    public double Opacity { get; set; } = 1;
    public Dictionary<string, ControlItem> Items { get; set; } = [];

    /// <summary>默认大小（dp / 点）：摇杆和圆形按键是直径。</summary>
    public static (double Width, double Height, bool Round) BaseSize(string id) => id switch
    {
        "stick" => (124, 124, true),
        "jump" => (68, 68, true),
        "sneak" => (52, 52, true),
        _ => (76, 34, false),
    };

    /// <summary>
    /// 默认布局里按键中心的位置（dp），和游戏里 ControlsView 的默认摆法一致。
    /// left / top / right / bottom 是屏幕边缘留出的距离（刘海、圆角）。
    /// </summary>
    public static (double X, double Y) DefaultCenter(string id, double width, double height,
                                                      double left = 16, double top = 16, double right = 16, double bottom = 16)
    {
        const double big = 34, small = 26, bw = 76, bh = 34, gap = 8;
        double jx = width - right - big - 20, jy = height - bottom - big - 24;
        double rowY = jy - big - gap - bh - 24 + bh / 2;
        return id switch
        {
            "stick" => (left + 62 + 24, height - bottom - 62 - 16),
            "jump" => (jx, jy),
            "sneak" => (jx - big - small - 22, jy + big - small),
            "inventory" => (width - right - bw / 2, rowY),
            "drop" => (width - right - bw * 1.5 - gap, rowY),
            "pause" => (width - right - bw / 2, top + bh / 2),
            "chat" => (left + bw / 2, top + bh / 2),
            "view" => (left + bw * 1.5 + gap, top + bh / 2),
            "keyboard" => (left + bw * 2.5 + gap * 2, top + bh / 2),
            _ => (width / 2, height / 2),
        };
    }

    public ControlItem Get(string id) => Items.GetValueOrDefault(id);

    public ControlLayout Clone(string name) => new()
    {
        Name = name,
        Opacity = Opacity,
        Items = Items.ToDictionary(p => p.Key, p => p.Value with { }),
    };

    /// <summary>传给游戏里的 ControlsView：{"opacity":…, "items":{"jump":{"x":…,"y":…,"size":…,"hidden":…}}}</summary>
    public JsonObject ToJson()
    {
        var items = new JsonObject();
        foreach (var (id, item) in Items)
            items[id] = new JsonObject { ["x"] = item.X, ["y"] = item.Y, ["size"] = item.Size, ["hidden"] = item.Hidden };
        return new JsonObject { ["opacity"] = Math.Clamp(Opacity, MinOpacity, 1), ["items"] = items };
    }
}

/// <summary>一个按键：中心点占屏幕宽高的比例（0–1）、相对默认大小的倍数、是否隐藏。</summary>
public record ControlItem
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Size { get; set; } = 1;
    public bool Hidden { get; set; }
}
