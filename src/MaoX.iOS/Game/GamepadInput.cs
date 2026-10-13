using CoreAnimation;
using CoreGraphics;
using Foundation;
using GameController;

namespace MaoX.iOS.Game;

/// <summary>
/// 手柄（GameController 框架，Xbox / PlayStation / Switch Pro / MFi 手柄）转成键盘鼠标，不需要游戏里装手柄模组。
/// 游戏中：左摇杆移动、按下疾跑，右摇杆转视角，A 跳跃、B 潜行、X 丢弃、Y 背包，LT 使用、RT 攻击，
/// LB / RB 和十字键左右切换快捷栏，按下右摇杆切换视角，菜单键暂停，选项键玩家列表。
/// 菜单里：左摇杆移动光标（ControlsView 画出来），A 左键、X 右键、按住左摇杆是 Shift，右摇杆滚动，B 返回、Y 关背包。
/// 和安卓版 Gamepad.java 的按键对应一致。
/// </summary>
internal sealed class GamepadInput
{
    [Flags]
    private enum Buttons
    {
        None = 0, A = 1, B = 2, X = 4, Y = 8, Lb = 16, Rb = 32, Lt = 64, Rt = 128, L3 = 256, R3 = 512,
        Menu = 1024, Options = 2048, Up = 4096, Down = 8192, Left = 16384, Right = 32768,
    }

    private const float StickDeadZone = 0.3f, LookDeadZone = 0.12f;
    /// <summary>右摇杆推到底时每秒转动的鼠标距离（游戏像素），默认灵敏度下约 165 度</summary>
    private const double LookSpeed = 1100;
    private const double CursorSpeed = 1000;

    private readonly ControlsView _controls;
    private readonly HashSet<int> _keys = [];
    private readonly HashSet<int> _mouse = [];
    private CADisplayLink _link;
    private double _lastFrame;
    private Buttons _previous;
    private bool? _grabbing;
    private double _cursorX, _cursorY, _scroll;

    public GamepadInput(ControlsView controls) => _controls = controls;

    public void Start()
    {
        _link = CADisplayLink.Create(Frame);
        _link.AddToRunLoop(NSRunLoop.Main, NSRunLoopMode.Common);
    }

    public void Stop()
    {
        _link?.Invalidate();
        _link = null;
        ReleaseAll();
    }

    private static GCExtendedGamepad Pad() =>
        GCController.Current?.ExtendedGamepad
        ?? GCController.Controllers.Select(c => c.ExtendedGamepad).FirstOrDefault(p => p != null);

    private void Frame()
    {
        var now = _link.Timestamp;
        var dt = _lastFrame == 0 ? 1 / 60.0 : Math.Clamp(now - _lastFrame, 0, 0.1);
        _lastFrame = now;
        var pad = Pad();
        if (pad == null)
        {
            if (_previous != Buttons.None || _keys.Count > 0 || _mouse.Count > 0)
                ReleaseAll();
            _previous = Buttons.None;
            return;
        }
        var buttons = Read(pad);
        float lx = pad.LeftThumbstick.XAxis.Value, ly = -pad.LeftThumbstick.YAxis.Value;
        float rx = pad.RightThumbstick.XAxis.Value, ry = -pad.RightThumbstick.YAxis.Value;
        var moving = Math.Abs(lx) > StickDeadZone || Math.Abs(ly) > StickDeadZone
                     || Math.Abs(rx) > StickDeadZone || Math.Abs(ry) > StickDeadZone;
        if (buttons != Buttons.None || moving)
            _controls.GamepadActive = true;

        var grabbing = _controls.Grabbing;
        if (grabbing != _grabbing)
        {
            ReleaseAll();
            _grabbing = grabbing;
            _cursorX = _controls.WindowWidth / 2.0;
            _cursorY = _controls.WindowHeight / 2.0;
            _previous = buttons; // 切换界面时还按着的键不算新按下
        }
        var pressed = buttons & ~_previous;
        _previous = buttons;
        if (grabbing)
            InGame(buttons, pressed, lx, ly, rx, ry, dt);
        else
            InMenu(buttons, pressed, lx, ly, ry, dt);
    }

    private static Buttons Read(GCExtendedGamepad pad)
    {
        var b = Buttons.None;
        void Set(Buttons flag, GCControllerButtonInput input)
        {
            if (input?.IsPressed == true)
                b |= flag;
        }
        Set(Buttons.A, pad.ButtonA);
        Set(Buttons.B, pad.ButtonB);
        Set(Buttons.X, pad.ButtonX);
        Set(Buttons.Y, pad.ButtonY);
        Set(Buttons.Lb, pad.LeftShoulder);
        Set(Buttons.Rb, pad.RightShoulder);
        Set(Buttons.Lt, pad.LeftTrigger);
        Set(Buttons.Rt, pad.RightTrigger);
        Set(Buttons.L3, pad.LeftThumbstickButton);
        Set(Buttons.R3, pad.RightThumbstickButton);
        Set(Buttons.Menu, pad.ButtonMenu);
        Set(Buttons.Options, pad.ButtonOptions);
        Set(Buttons.Up, pad.DPad.Up);
        Set(Buttons.Down, pad.DPad.Down);
        Set(Buttons.Left, pad.DPad.Left);
        Set(Buttons.Right, pad.DPad.Right);
        return b;
    }

    private void InGame(Buttons b, Buttons pressed, float lx, float ly, float rx, float ry, double dt)
    {
        var len = Math.Sqrt(lx * lx + ly * ly);
        var active = len > StickDeadZone;
        Hold(Keys.W, active && ly < -0.38 * len);
        Hold(Keys.S, active && ly > 0.38 * len);
        Hold(Keys.A, active && lx < -0.38 * len);
        Hold(Keys.D, active && lx > 0.38 * len);
        Hold(Keys.LeftControl, b.HasFlag(Buttons.L3));
        Hold(Keys.Space, b.HasFlag(Buttons.A));
        Hold(Keys.LeftShift, b.HasFlag(Buttons.B));
        Hold(Keys.Q, b.HasFlag(Buttons.X) || b.HasFlag(Buttons.Down));
        Hold(Keys.E, b.HasFlag(Buttons.Y));
        Hold(Keys.Tab, b.HasFlag(Buttons.Options));
        HoldMouse(Keys.MouseRight, b.HasFlag(Buttons.Lt));
        HoldMouse(Keys.MouseLeft, b.HasFlag(Buttons.Rt));
        if ((pressed & (Buttons.Lb | Buttons.Left)) != 0)
            GameNative.SendScroll(0, 1);
        if ((pressed & (Buttons.Rb | Buttons.Right)) != 0)
            GameNative.SendScroll(0, -1);
        if (pressed.HasFlag(Buttons.R3) || pressed.HasFlag(Buttons.Up))
            Tap(Keys.F5);
        if (pressed.HasFlag(Buttons.Menu))
            Tap(Keys.Escape);

        double dx = Curve(rx, LookDeadZone) * LookSpeed * dt, dy = Curve(ry, LookDeadZone) * LookSpeed * dt;
        if (dx != 0 || dy != 0)
            GameNative.SendCursor(GameNative.ActionMoveMotion, (float)dx, (float)dy);
    }

    private void InMenu(Buttons b, Buttons pressed, float lx, float ly, float ry, double dt)
    {
        double dx = Curve(lx, LookDeadZone) * CursorSpeed * dt, dy = Curve(ly, LookDeadZone) * CursorSpeed * dt;
        if (dx != 0 || dy != 0)
        {
            _cursorX = Math.Clamp(_cursorX + dx, 0, Math.Max(0, _controls.WindowWidth - 1));
            _cursorY = Math.Clamp(_cursorY + dy, 0, Math.Max(0, _controls.WindowHeight - 1));
            GameNative.SendCursor(GameNative.ActionMove, (float)_cursorX, (float)_cursorY);
        }
        _controls.GamepadCursor = new CGPoint(_cursorX / _controls.PixelScale, _cursorY / _controls.PixelScale);

        Hold(Keys.LeftShift, b.HasFlag(Buttons.L3));
        HoldMouse(Keys.MouseLeft, b.HasFlag(Buttons.A));
        HoldMouse(Keys.MouseRight, b.HasFlag(Buttons.X));
        if (pressed.HasFlag(Buttons.B) || pressed.HasFlag(Buttons.Menu))
            Tap(Keys.Escape);
        if (pressed.HasFlag(Buttons.Y))
            Tap(Keys.E);
        if ((pressed & (Buttons.Lb | Buttons.Up)) != 0)
            GameNative.SendScroll(0, 1);
        if ((pressed & (Buttons.Rb | Buttons.Down)) != 0)
            GameNative.SendScroll(0, -1);

        _scroll -= Curve(ry, StickDeadZone) * 12 * dt;
        while (Math.Abs(_scroll) >= 1)
        {
            var step = Math.Sign(_scroll);
            GameNative.SendScroll(0, step);
            _scroll -= step;
        }
    }

    /// <summary>死区之外按平方曲线放大：轻推精细，推到底最快。</summary>
    private static double Curve(float value, float deadZone)
    {
        var magnitude = Math.Abs(value);
        if (magnitude <= deadZone)
            return 0;
        var t = Math.Min(1, (magnitude - deadZone) / (1 - deadZone));
        return Math.Sign(value) * t * t;
    }

    private void Hold(int key, bool down)
    {
        if (down == _keys.Contains(key))
            return;
        if (down)
            _keys.Add(key);
        else
            _keys.Remove(key);
        GameNative.SendKey(key, down);
    }

    private void HoldMouse(int button, bool down)
    {
        if (down == _mouse.Contains(button))
            return;
        if (down)
            _mouse.Add(button);
        else
            _mouse.Remove(button);
        GameNative.SendMouse(button, down);
    }

    private static void Tap(int key)
    {
        GameNative.SendKey(key, true);
        GameNative.SendKey(key, false);
    }

    private void ReleaseAll()
    {
        foreach (var key in _keys)
            GameNative.SendKey(key, false);
        foreach (var button in _mouse)
            GameNative.SendMouse(button, false);
        _keys.Clear();
        _mouse.Clear();
        _scroll = 0;
        _controls.GamepadCursor = null;
    }
}
