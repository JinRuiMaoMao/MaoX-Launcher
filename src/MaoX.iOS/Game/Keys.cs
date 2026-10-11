namespace MaoX.iOS.Game;

/// <summary>GLFW 按键码，以及外接键盘 HID 用法码 → GLFW 按键码。</summary>
internal static class Keys
{
    public const int MouseLeft = 0, MouseRight = 1;

    public const int Space = 32, Digit0 = 48, Digit1 = 49, A = 65, D = 68, E = 69, Q = 81, S = 83, T = 84, W = 87;
    public const int Escape = 256, Enter = 257, Tab = 258, Backspace = 259, F1 = 290, F5 = 294;
    public const int LeftShift = 340, LeftControl = 341, LeftAlt = 342, LeftSuper = 343;
    public const int RightShift = 344, RightControl = 345, RightAlt = 346, RightSuper = 347;

    public static int FromHid(int usage)
    {
        if (usage is >= 4 and <= 29)
            return A + (usage - 4);
        if (usage is >= 30 and <= 38)
            return Digit1 + (usage - 30);
        if (usage is >= 58 and <= 69)
            return F1 + (usage - 58);
        return usage switch
        {
            39 => Digit0,
            40 or 88 => Enter,
            41 => Escape,
            42 => Backspace,
            43 => Tab,
            44 => Space,
            45 => 45, // -
            46 => 61, // =
            47 => 91, // [
            48 => 93, // ]
            49 => 92, // \
            51 => 59, // ;
            52 => 39, // '
            53 => 96, // `
            54 => 44, // ,
            55 => 46, // .
            56 => 47, // /
            57 => 280, // Caps Lock
            73 => 260, // Insert
            74 => 268, // Home
            75 => 266, // Page Up
            76 => 261, // Delete
            77 => 269, // End
            78 => 267, // Page Down
            79 => 262, // →
            80 => 263, // ←
            81 => 264, // ↓
            82 => 265, // ↑
            224 => LeftControl,
            225 => LeftShift,
            226 => LeftAlt,
            227 => LeftSuper,
            228 => RightControl,
            229 => RightShift,
            230 => RightAlt,
            231 => RightSuper,
            _ => 0,
        };
    }
}
