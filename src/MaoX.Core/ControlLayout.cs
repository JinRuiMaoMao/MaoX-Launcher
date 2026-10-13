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
    /// <summary>用户自己添加的按键（和 Pojav / Amethyst 的自定义按键一样，可以绑定任意键）</summary>
    public List<ControlButton> Buttons { get; set; } = [];

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
        Buttons = (Buttons ?? []).Select(b => b with { Keys = [.. b.Keys ?? []] }).ToList(),
    };

    /// <summary>
    /// 传给游戏里的 ControlsView：{"opacity":…, "items":{"jump":{"x":…,"y":…,"size":…,"hidden":…}},
    /// "buttons":[{"label":…,"keys":[…],"x":…,"y":…,"w":…,"h":…,"round":…,"toggle":…,"inGame":…,"inMenu":…}]}
    /// </summary>
    public JsonObject ToJson()
    {
        var items = new JsonObject();
        foreach (var (id, item) in Items)
            items[id] = new JsonObject { ["x"] = item.X, ["y"] = item.Y, ["size"] = item.Size, ["hidden"] = item.Hidden };
        var buttons = new JsonArray();
        foreach (var b in Buttons ?? [])
        {
            var keys = new JsonArray();
            foreach (var key in b.Keys ?? [])
                keys.Add(key);
            buttons.Add(new JsonObject
            {
                ["label"] = b.Label, ["keys"] = keys, ["x"] = b.X, ["y"] = b.Y,
                ["w"] = Math.Clamp(b.Width, ControlButton.MinSide, ControlButton.MaxSide),
                ["h"] = Math.Clamp(b.Height, ControlButton.MinSide, ControlButton.MaxSide),
                ["round"] = b.Round, ["toggle"] = b.Toggle, ["inGame"] = b.InGame, ["inMenu"] = b.InMenu,
            });
        }
        return new JsonObject { ["opacity"] = Math.Clamp(Opacity, MinOpacity, 1), ["items"] = items, ["buttons"] = buttons };
    }
}

/// <summary>自定义按键：中心点占屏幕宽高的比例，宽高是 dp / 点；按下时同时按下 Keys 里的所有键（ControlKeys 的键码）。</summary>
public record ControlButton
{
    public const double MinSide = 24, MaxSide = 200;
    public const int MaxKeys = 4;

    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<int> Keys { get; set; } = [];
    public double X { get; set; } = 0.5;
    public double Y { get; set; } = 0.5;
    public double Width { get; set; } = 56;
    public double Height { get; set; } = 56;
    public bool Round { get; set; }
    /// <summary>按一下按住，再按一下松开</summary>
    public bool Toggle { get; set; }
    public bool InGame { get; set; } = true;
    public bool InMenu { get; set; }

    /// <summary>按键上显示的文字：没起名字时显示绑定的键</summary>
    public string Label => string.IsNullOrWhiteSpace(Name) ? ControlKeys.Describe(Keys) : Name.Trim();
}

/// <summary>
/// 自定义按键能绑定的键：GLFW 键码，负数是特殊功能（和 Pojav 的特殊键码一致）。
/// </summary>
public static class ControlKeys
{
    public const int Keyboard = -1, MouseLeft = -3, MouseRight = -4, MouseMiddle = -6, ScrollUp = -7, ScrollDown = -8;

    public static IReadOnlyList<(string Group, IReadOnlyList<int> Keys)> Groups { get; } =
    [
        ("常用", [32, 340, 341, 342, 258, 256, 257, 259, 280]),
        ("鼠标", [MouseLeft, MouseRight, MouseMiddle, ScrollUp, ScrollDown]),
        ("字母", [.. Enumerable.Range(65, 26)]),
        ("数字", [.. Enumerable.Range(48, 10)]),
        ("功能键", [.. Enumerable.Range(290, 12)]),
        ("方向和编辑", [265, 264, 263, 262, 260, 261, 268, 269, 266, 267]),
        ("符号", [96, 45, 61, 91, 93, 92, 59, 39, 44, 46, 47]),
        ("其他", [Keyboard, 344, 345, 346]),
    ];

    public static string Name(int key) => key switch
    {
        Keyboard => I18n.T("打开键盘"),
        MouseLeft => I18n.T("鼠标左键"),
        MouseRight => I18n.T("鼠标右键"),
        MouseMiddle => I18n.T("鼠标中键"),
        ScrollUp => I18n.T("滚轮上"),
        ScrollDown => I18n.T("滚轮下"),
        32 => I18n.T("空格"),
        340 => "Shift",
        341 => "Ctrl",
        342 => "Alt",
        344 => I18n.T("右 Shift"),
        345 => I18n.T("右 Ctrl"),
        346 => I18n.T("右 Alt"),
        258 => "Tab",
        256 => "Esc",
        257 => I18n.T("回车"),
        259 => I18n.T("退格"),
        280 => "Caps Lock",
        265 => "↑",
        264 => "↓",
        263 => "←",
        262 => "→",
        260 => "Insert",
        261 => "Delete",
        268 => "Home",
        269 => "End",
        266 => "Page Up",
        267 => "Page Down",
        >= 290 and <= 301 => "F" + (key - 289),
        >= 32 and <= 96 => ((char)key).ToString(),
        _ => "#" + key,
    };

    public static string Describe(IEnumerable<int> keys)
    {
        var names = (keys ?? []).Select(Name).ToList();
        return names.Count == 0 ? I18n.T("未设置") : string.Join(" + ", names);
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
