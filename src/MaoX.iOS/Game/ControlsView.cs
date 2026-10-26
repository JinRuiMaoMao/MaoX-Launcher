using CoreGraphics;
using CoreMotion;
using Foundation;
using MaoX.Core;
using ObjCRuntime;
using UIKit;

namespace MaoX.iOS.Game;

/// <summary>
/// 触屏操作层，盖在游戏画面上（和安卓版 ControlsView 一致）。
/// 游戏中：左下摇杆移动（推到边缘疾跑），右侧跳跃 / 潜行等按钮，点快捷栏切换物品，
/// 其余区域拖动转视角、轻点使用 / 放置、长按攻击 / 挖掘；支持多指同时操作。
/// 菜单里：手指就是鼠标，双指上下滑动滚动；按键和摇杆像 Pojav 一样常驻，方便关背包、点 GUI。
/// </summary>
internal sealed class ControlsView : UIView
{
    private const double LongPressSeconds = 0.3;
    private static readonly object Stick = new(), Look = new(), Hotbar = new(), Nothing = new();

    private readonly string _gameDir;
    private readonly ControlLayout _layout;
    private readonly double _opacity, _sensitivity;
    /// <summary>拆分控制：拖动只转视角，攻击 / 使用用单独的按钮；方向键代替摇杆（和基岩版一样）</summary>
    private readonly bool _split, _dpadMode, _gyro;
    private readonly CMMotionManager _motion;
    private readonly UIImpactFeedbackGenerator _haptic = new(UIImpactFeedbackStyle.Medium);
    private readonly UIImpactFeedbackGenerator _lightHaptic = new(UIImpactFeedbackStyle.Light);
    private int _touchLogs;
    private readonly List<Button> _buttons = [];
    private readonly Dictionary<NativeHandle, object> _touches = [];
    private readonly UITextField _input;
    private bool _grabbing;
    private int _windowWidth, _windowHeight;

    // 摇杆 / 方向键
    private CGRect _stickZone, _dpadRect;
    private double _stickX, _stickY, _stickRadius, _knobX, _knobY, _knobRadius = 26;
    private bool _up, _down, _left, _right, _sprint, _stickHidden;

    // 视角 / 鼠标（同一时间只有一根手指）
    private NativeHandle _lookTouch, _scrollTouch;
    private double _lastX, _lastY, _downX, _downY, _scrollLastY;
    private bool _moved, _holding;
    private NSTimer _longPress;

    // 快捷栏：滑动换格，长按丢弃
    private NativeHandle _hotbarTouch;
    private int _hotbarSlot = -1;
    private NSTimer _hotbarHold;

    /// <summary>界面点 → 游戏像素。</summary>
    public double PixelScale { get; set; } = 1;

    public bool Grabbing => _grabbing;

    public int WindowWidth => _windowWidth;

    public int WindowHeight => _windowHeight;

    private bool _gamepadActive;
    private CGPoint? _gamepadCursor;

    /// <summary>正在用手柄时隐藏触屏按键，手指碰到屏幕就重新显示。</summary>
    public bool GamepadActive
    {
        get => _gamepadActive;
        set
        {
            if (_gamepadActive == value)
                return;
            _gamepadActive = value;
            SetNeedsDisplay();
        }
    }

    /// <summary>菜单里手柄光标的位置（界面点），null 时不画。</summary>
    public CGPoint? GamepadCursor
    {
        get => _gamepadCursor;
        set
        {
            if (_gamepadCursor == value)
                return;
            _gamepadCursor = value;
            SetNeedsDisplay();
        }
    }

    public Action KeyboardHidden { get; set; }

    public bool KeyboardShown => _input.IsFirstResponder;

    private readonly Button _jump, _sneak, _inventory, _drop, _chat, _view, _pause, _keyboard, _attack, _use;

    public ControlsView(string gameDir, IReadOnlyDictionary<string, string> labels, ControlLayout layout)
    {
        _gameDir = gameDir;
        _layout = layout;
        _opacity = layout == null ? 1 : Math.Clamp(layout.Opacity, ControlLayout.MinOpacity, 1);
        _sensitivity = Math.Clamp(layout?.Sensitivity ?? 1, ControlLayout.MinSensitivity, ControlLayout.MaxSensitivity);
        _split = layout?.Mode == ControlLayout.Split;
        _dpadMode = layout?.Movement == ControlLayout.Dpad;
        _gyro = layout?.Gyro == true;
        _motion = _gyro ? new CMMotionManager() : null;
        BackgroundColor = UIColor.Clear;
        Opaque = false;
        MultipleTouchEnabled = true;
        ContentMode = UIViewContentMode.Redraw;

        // 内置键和摇杆全局常驻（和 Pojav 一样）：打开背包后指针会松开，不能把操作层藏掉
        _jump = Add("jump", labels, Keys.Space, false, true, true, null);
        _jump.Round = true;
        _sneak = Add("sneak", labels, Keys.LeftShift, true, true, true, null);
        _sneak.Round = true;
        _inventory = Add("inventory", labels, Keys.E, false, true, true, null);
        _drop = Add("drop", labels, Keys.Q, false, true, true, null);
        _chat = Add("chat", labels, 0, false, true, true, () =>
        {
            Tap(Keys.T);
            NSTimer.CreateScheduledTimer(0.15, false, _ => ShowKeyboard(true));
        });
        _view = Add("view", labels, Keys.F5, false, true, true, null);
        _pause = Add("pause", labels, Keys.Escape, false, true, true, null);
        _keyboard = Add("keyboard", labels, 0, false, true, true, () => ShowKeyboard(!KeyboardShown));
        // 拆分控制的攻击 / 使用按钮：按住的同时拖动可以继续转视角
        _attack = Add("attack", labels, ControlKeys.MouseLeft, false, true, true, null);
        _use = Add("use", labels, ControlKeys.MouseRight, false, true, true, null);
        foreach (var b in new[] { _attack, _use })
        {
            b.Round = true;
            b.Look = true;
            b.Disabled = !_split;
        }
        foreach (var c in layout?.Buttons ?? [])
        {
            _buttons.Add(new Button(this, c.Id, c.Label, [.. c.Keys ?? []], c.Toggle, c.InGame, c.InMenu, null)
            {
                Round = c.Round, Custom = c,
            });
        }

        // 屏幕外的输入框接收输入法的文字；保留一个删不掉的空格，好让退格键一直有效
        _input = new UITextField(new CGRect(0, -40, 10, 30))
        {
            Text = " ",
            AutocorrectionType = UITextAutocorrectionType.No,
            AutocapitalizationType = UITextAutocapitalizationType.None,
            SpellCheckingType = UITextSpellCheckingType.No,
            SmartQuotesType = UITextSmartQuotesType.No,
            SmartDashesType = UITextSmartDashesType.No,
            SmartInsertDeleteType = UITextSmartInsertDeleteType.No,
            ReturnKeyType = UIReturnKeyType.Send,
        };
        _input.ShouldChangeCharacters = (_, _, replacement) =>
        {
            if (string.IsNullOrEmpty(replacement))
            {
                Tap(Keys.Backspace);
                return false;
            }
            foreach (var c in replacement)
            {
                if (c == '\n')
                    Tap(Keys.Enter);
                else
                    GameNative.SendChar(c);
            }
            return false;
        };
        _input.ShouldReturn = _ =>
        {
            Tap(Keys.Enter);
            return false;
        };
        AddSubview(_input);
        if (_motion is { GyroAvailable: true })
        {
            _motion.GyroUpdateInterval = 1 / 60.0;
            _motion.StartGyroUpdates(NSOperationQueue.MainQueue, (data, _) =>
            {
                if (!_grabbing || data == null)
                    return;
                Turn(data.RotationRate.y * 12, data.RotationRate.x * 12);
            });
        }
    }

    public override void WillMoveToSuperview(UIView newSuperview)
    {
        if (newSuperview == null)
            _motion?.StopGyroUpdates();
        base.WillMoveToSuperview(newSuperview);
    }

    private sealed class Button(ControlsView owner, string id, string label, int[] keys, bool toggle, bool inGame,
                                bool inMenu, Action action)
    {
        public readonly string Id = id;
        public readonly string Label = label;
        public CGRect Rect;
        public bool Round, Pressed, Latched, Hidden, Disabled;
        /// <summary>按住时手指拖动也转视角（攻击 / 使用按钮）</summary>
        public bool Look;
        public double LastX, LastY;
        public double Size = 1;
        /// <summary>用户添加的按键（null 是内置按键）</summary>
        public ControlButton Custom;

        public bool Toggle => toggle;

        public bool Visible => !Hidden && !Disabled && !owner._gamepadActive && (owner._grabbing ? inGame : inMenu);

        public void Press(bool down)
        {
            if (action != null)
            {
                if (down)
                    action();
                Pressed = down;
                return;
            }
            if (toggle)
            {
                if (!down)
                    return;
                Latched = !Latched;
                Pressed = Latched;
                Send(Latched);
                return;
            }
            Pressed = down;
            Send(down);
        }

        private void Send(bool down)
        {
            foreach (var key in keys)
                owner.SendCode(key, down);
        }
    }

    private Button Add(string id, IReadOnlyDictionary<string, string> labels, int key, bool toggle, bool inGame,
                       bool inMenu, Action action)
    {
        var b = new Button(this, id, labels[id], [key], toggle, inGame, inMenu, action);
        _buttons.Add(b);
        return b;
    }

    /// <summary>按下 / 松开一个键：GLFW 键码，或 ControlKeys 里的鼠标、滚轮、打开键盘。</summary>
    private void SendCode(int code, bool down)
    {
        switch (code)
        {
            case >= 0:
                GameNative.SendKey(code, down);
                break;
            case ControlKeys.MouseLeft:
                GameNative.SendMouse(Keys.MouseLeft, down);
                break;
            case ControlKeys.MouseRight:
                GameNative.SendMouse(Keys.MouseRight, down);
                break;
            case ControlKeys.MouseMiddle:
                GameNative.SendMouse(Keys.MouseMiddle, down);
                break;
            case ControlKeys.ScrollUp when down:
                GameNative.SendScroll(0, 1);
                break;
            case ControlKeys.ScrollDown when down:
                GameNative.SendScroll(0, -1);
                break;
            case ControlKeys.Keyboard when down:
                ShowKeyboard(!KeyboardShown);
                break;
        }
    }

    private static void Tap(int key)
    {
        GameNative.SendKey(key, true);
        GameNative.SendKey(key, false);
    }

    public void SetWindowSize(int width, int height)
    {
        _windowWidth = width;
        _windowHeight = height;
    }

    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        var insets = SafeAreaInsets;
        double w = Bounds.Width, h = Bounds.Height;
        double left = Math.Max(16, (double)insets.Left), right = Math.Max(16, (double)insets.Right);
        double top = Math.Max(12, (double)insets.Top), bottom = Math.Max(12, (double)insets.Bottom);
        const double big = 34, small = 26;
        _stickRadius = 62;
        _stickX = left + _stickRadius + 24;
        _stickY = h - bottom - _stickRadius - 16;
        _knobX = _stickX;
        _knobY = _stickY;
        _stickZone = new CGRect(0, h * 0.4, w * 0.3, h * 0.6);

        double jx = w - right - big - 20, jy = h - bottom - big - 24;
        Circle(_jump, jx, jy, big);
        Circle(_sneak, jx - big - small - 22, jy + big - small, small);

        const double bw = 76, bh = 34, gap = 8;
        var rowTop = jy - big - gap - bh - 24;
        Box(_inventory, w - right - bw, rowTop, bw, bh);
        Box(_drop, w - right - bw * 2 - gap, rowTop, bw, bh);
        // 和 ControlLayout.DefaultCenter 一致
        double ax = w - right - 36, ay = rowTop - 14 - 32;
        Circle(_attack, ax, ay, 32);
        Circle(_use, ax - 78, ay + 12, 32);

        Box(_pause, w - right - bw, top, bw, bh);
        Box(_chat, left, top, bw, bh);
        Box(_view, left + bw + gap, top, bw, bh);
        Box(_keyboard, left + (bw + gap) * 2, top, bw, bh);
        ApplyLayout(w, h);
        _dpadRect = new CGRect(_stickX - _stickRadius, _stickY - _stickRadius, _stickRadius * 2, _stickRadius * 2);
        if (_dpadMode)
            _stickZone = _stickHidden ? CGRect.Empty : _dpadRect;
        SetNeedsDisplay();
    }

    /// <summary>启动器里编辑过的布局：位置是中心点占屏幕宽高的比例，大小是默认大小的倍数。</summary>
    private void ApplyLayout(double w, double h)
    {
        _knobRadius = 26;
        if (_layout == null)
            return;
        foreach (var b in _buttons)
        {
            if (b.Custom is { } c)
            {
                double cw = Math.Clamp(c.Width, ControlButton.MinSide, ControlButton.MaxSide);
                double ch = Math.Clamp(c.Height, ControlButton.MinSide, ControlButton.MaxSide);
                b.Rect = new CGRect(c.X * w - cw / 2, c.Y * h - ch / 2, cw, ch);
                b.Size = Math.Min(cw, ch) / 44;
                continue;
            }
            if (_layout.Get(b.Id) is not { } item)
                continue;
            b.Size = Math.Clamp(item.Size, ControlLayout.MinSize, ControlLayout.MaxSize);
            b.Hidden = item.Hidden;
            double bw = b.Rect.Width * b.Size, bh = b.Rect.Height * b.Size;
            b.Rect = new CGRect(item.X * w - bw / 2, item.Y * h - bh / 2, bw, bh);
        }
        if (_layout.Get("stick") is not { } stick)
            return;
        var size = Math.Clamp(stick.Size, ControlLayout.MinSize, ControlLayout.MaxSize);
        _stickRadius *= size;
        _knobRadius *= size;
        _knobX = _stickX = stick.X * w;
        _knobY = _stickY = stick.Y * h;
        _stickHidden = stick.Hidden;
        var reach = _stickRadius * 1.8;
        _stickZone = _stickHidden ? CGRect.Empty : new CGRect(_stickX - reach, _stickY - reach, reach * 2, reach * 2);
    }

    /// <summary>按布局的不透明度调整颜色。</summary>
    private UIColor Fade(UIColor color) => color.ColorWithAlpha(color.CGColor.Alpha * (nfloat)_opacity);

    private static void Circle(Button b, double cx, double cy, double r) => b.Rect = new CGRect(cx - r, cy - r, r * 2, r * 2);

    private static void Box(Button b, double x, double y, double w, double h) => b.Rect = new CGRect(x, y, w, h);

    public void SetGrabbing(bool grabbing)
    {
        if (_grabbing == grabbing)
            return;
        GameNative.Log($"grabbing={grabbing}");
        ReleaseAll();
        _grabbing = grabbing;
        _gamepadCursor = null;
        if (grabbing)
            ShowKeyboard(false);
        else
            GameNative.SendCursor(GameNative.ActionDown, _windowWidth / 2f, _windowHeight / 2f);
        SetNeedsDisplay();
    }

    private void ReleaseAll()
    {
        _longPress?.Invalidate();
        _longPress = null;
        _hotbarHold?.Invalidate();
        _hotbarHold = null;
        _hotbarTouch = NativeHandle.Zero;
        if (_holding)
            GameNative.SendMouse(Keys.MouseLeft, false);
        _holding = false;
        _lookTouch = NativeHandle.Zero;
        _scrollTouch = NativeHandle.Zero;
        SetStick(0, 0);
        foreach (var b in _buttons)
        {
            if (b.Pressed && !b.Toggle)
                b.Press(false);
            b.Pressed = b.Latched;
        }
        _touches.Clear();
    }

    // ------------------------------------------------------------------ 触摸

    public override void TouchesBegan(NSSet touches, UIEvent evt)
    {
        GamepadActive = false;
        foreach (var touch in touches.Cast<UITouch>())
        {
            var p = touch.LocationInView(this);
            TouchDown(touch.Handle, p.X, p.Y);
        }
        SetNeedsDisplay();
    }

    public override void TouchesMoved(NSSet touches, UIEvent evt)
    {
        foreach (var touch in touches.Cast<UITouch>())
        {
            var p = touch.LocationInView(this);
            TouchMove(touch.Handle, p.X, p.Y);
        }
        SetNeedsDisplay();
    }

    public override void TouchesEnded(NSSet touches, UIEvent evt)
    {
        foreach (var touch in touches.Cast<UITouch>())
            TouchUp(touch.Handle, false);
        SetNeedsDisplay();
    }

    public override void TouchesCancelled(NSSet touches, UIEvent evt)
    {
        foreach (var touch in touches.Cast<UITouch>())
            TouchUp(touch.Handle, true);
        SetNeedsDisplay();
    }

    private void TouchDown(NativeHandle id, double x, double y)
    {
        if (_touchLogs < 12)
        {
            _touchLogs++;
            GameNative.Log($"touch {x:0},{y:0} grabbing={_grabbing} window={_windowWidth}x{_windowHeight} " +
                           $"scale={PixelScale:0.###} view={Bounds.Width:0}x{Bounds.Height:0}");
        }
        foreach (var b in _buttons)
        {
            if (b.Visible && b.Rect.Contains(new CGPoint(x, y)))
            {
                _touches[id] = b;
                b.LastX = x;
                b.LastY = y;
                b.Press(true);
                return;
            }
        }
        if (_grabbing)
        {
            var slot = HotbarSlot(x, y);
            if (slot >= 0)
            {
                _touches[id] = Hotbar;
                _hotbarTouch = id;
                SelectSlot(slot);
                return;
            }
        }
        if (!_stickHidden && _stickZone.Contains(new CGPoint(x, y)))
        {
            _touches[id] = Stick;
            if (_dpadMode)
                UpdateDpad(x, y, true);
            else
                UpdateStick(x, y);
            return;
        }
        if (_lookTouch != NativeHandle.Zero)
        {
            // 菜单里第二根手指：上下滑动滚动
            if (!_grabbing && _scrollTouch == NativeHandle.Zero)
            {
                GameNative.SendMouse(Keys.MouseLeft, false);
                _scrollTouch = id;
                _scrollLastY = y;
            }
            _touches[id] = Nothing;
            return;
        }
        _touches[id] = Look;
        _lookTouch = id;
        _downX = _lastX = x;
        _downY = _lastY = y;
        _moved = false;
        _holding = false;
        if (_grabbing && !_split)
        {
            _longPress?.Invalidate();
            _longPress = NSTimer.CreateScheduledTimer(LongPressSeconds, false, _ =>
            {
                _longPress = null;
                if (!_moved && _lookTouch != NativeHandle.Zero && _grabbing)
                {
                    _holding = true;
                    _haptic.ImpactOccurred();
                    GameNative.SendMouse(Keys.MouseLeft, true);
                }
            });
        }
        else if (!_grabbing)
        {
            GameNative.SendCursor(GameNative.ActionDown, (float)(x * PixelScale), (float)(y * PixelScale));
            GameNative.SendMouse(Keys.MouseLeft, true);
        }
    }

    private void TouchMove(NativeHandle id, double x, double y)
    {
        _touches.TryGetValue(id, out var target);
        if (target == Stick)
        {
            if (_dpadMode)
                UpdateDpad(x, y, false);
            else
                UpdateStick(x, y);
        }
        else if (target == Hotbar)
        {
            var slot = HotbarSlot(x, y);
            if (slot >= 0 && slot != _hotbarSlot)
                SelectSlot(slot);
        }
        else if (target is Button { Look: true } button)
        {
            if (_grabbing)
                Turn(x - button.LastX, y - button.LastY);
            button.LastX = x;
            button.LastY = y;
        }
        else if (id == _scrollTouch)
        {
            var dy = y - _scrollLastY;
            if (Math.Abs(dy) > 24)
            {
                GameNative.SendScroll(0, dy > 0 ? 1 : -1);
                _scrollLastY = y;
            }
        }
        else if (target == Look && id == _lookTouch)
        {
            if (Math.Abs(x - _downX) > 8 || Math.Abs(y - _downY) > 8)
                _moved = true;
            if (_grabbing)
                Turn(x - _lastX, y - _lastY);
            else
            {
                GameNative.SendCursor(GameNative.ActionMove, (float)(x * PixelScale), (float)(y * PixelScale));
            }
            _lastX = x;
            _lastY = y;
        }
    }

    private void TouchUp(NativeHandle id, bool cancel)
    {
        _touches.Remove(id, out var target);
        if (target is Button button)
        {
            button.Press(false);
        }
        else if (target == Stick)
        {
            SetStick(0, 0);
        }
        else if (target == Hotbar)
        {
            _hotbarHold?.Invalidate();
            _hotbarHold = null;
            _hotbarTouch = NativeHandle.Zero;
        }
        else if (id == _scrollTouch)
        {
            _scrollTouch = NativeHandle.Zero;
        }
        else if (target == Look && id == _lookTouch)
        {
            _longPress?.Invalidate();
            _longPress = null;
            if (_grabbing)
            {
                if (_holding)
                {
                    GameNative.SendMouse(Keys.MouseLeft, false);
                }
                else if (!_moved && !cancel && !_split)
                {
                    GameNative.SendMouse(Keys.MouseRight, true);
                    NSTimer.CreateScheduledTimer(0.05, false, _ => GameNative.SendMouse(Keys.MouseRight, false));
                }
            }
            else
            {
                GameNative.SendMouse(Keys.MouseLeft, false);
            }
            _holding = false;
            _lookTouch = NativeHandle.Zero;
        }
    }

    private void UpdateStick(double x, double y)
    {
        var dx = (x - _stickX) / _stickRadius;
        var dy = (y - _stickY) / _stickRadius;
        var len = Math.Sqrt(dx * dx + dy * dy);
        SetStick(dx, dy);
        var clamp = Math.Min(1, len) / Math.Max(len, 0.0001);
        _knobX = _stickX + dx * clamp * _stickRadius;
        _knobY = _stickY + dy * clamp * _stickRadius;
    }

    /// <summary>
    /// 基岩版方向键：3×3 格，上下左右是 W / S / A / D，中间切换潜行；按住前进时左上、右上两格变成斜向前进。
    /// 手指可以在格子之间滑动；双击前进疾跑由游戏本身处理。
    /// </summary>
    private void UpdateDpad(double x, double y, bool begin)
    {
        var cell = _dpadRect.Width / 3;
        int col = (int)Math.Floor((x - _dpadRect.X) / cell), row = (int)Math.Floor((y - _dpadRect.Y) / cell);
        var inside = col is >= 0 and <= 2 && row is >= 0 and <= 2;
        if (inside && begin && col == 1 && row == 1)
            _sneak.Press(true);
        var forward = inside && row == 0 && (col == 1 || _up);
        SetKey(Keys.W, forward, ref _up);
        SetKey(Keys.S, inside && row == 2 && col == 1, ref _down);
        SetKey(Keys.A, inside && col == 0 && (row == 1 || forward), ref _left);
        SetKey(Keys.D, inside && col == 2 && (row == 1 || forward), ref _right);
    }

    /// <summary>手指在界面上移动 (dx, dy) 点时转动视角。</summary>
    private void Turn(double dx, double dy)
    {
        var sens = 1.2 * PixelScale * _sensitivity;
        GameNative.SendCursor(GameNative.ActionMoveMotion, (float)(dx * sens), (float)(dy * sens));
    }

    /// <summary>切换快捷栏格子；按住不放 0.5 秒丢出一个物品（和基岩版一样）。</summary>
    private void SelectSlot(int slot)
    {
        _hotbarSlot = slot;
        Tap(Keys.Digit1 + slot);
        _lightHaptic.ImpactOccurred();
        _hotbarHold?.Invalidate();
        _hotbarHold = NSTimer.CreateScheduledTimer(0.5, false, _ =>
        {
            _hotbarHold = null;
            if (_grabbing && _touches.TryGetValue(_hotbarTouch, out var target) && target == Hotbar)
            {
                Tap(Keys.Q);
                _haptic.ImpactOccurred();
            }
        });
    }

    private void SetStick(double dx, double dy)
    {
        var len = Math.Sqrt(dx * dx + dy * dy);
        var active = len > 0.25;
        SetKey(Keys.W, active && dy < -0.38 * len, ref _up);
        SetKey(Keys.S, active && dy > 0.38 * len, ref _down);
        SetKey(Keys.A, active && dx < -0.38 * len, ref _left);
        SetKey(Keys.D, active && dx > 0.38 * len, ref _right);
        SetKey(Keys.LeftControl, _up && len > 1.15, ref _sprint);
        if (!active)
        {
            _knobX = _stickX;
            _knobY = _stickY;
        }
    }

    private static void SetKey(int key, bool down, ref bool state)
    {
        if (state == down)
            return;
        state = down;
        GameNative.SendKey(key, down);
    }

    /// <summary>快捷栏宽 182、高 22 个界面像素，居中贴底；界面缩放按 options.txt 的 guiScale（0 为自动）计算。</summary>
    private int HotbarSlot(double x, double y)
    {
        int w = _windowWidth, h = _windowHeight;
        var gui = GuiScale(w, h);
        double px = x * PixelScale, py = y * PixelScale;
        var left = (w - 182.0 * gui) / 2;
        if (py < h - 22.0 * gui || px < left || px > left + 182.0 * gui)
            return -1;
        return Math.Clamp((int)((px - left) / (20.0 * gui)), 0, 8);
    }

    private int _guiScaleSetting;
    private DateTime _optionsModified = DateTime.MinValue;

    private int GuiScale(int w, int h)
    {
        var options = Path.Combine(_gameDir, "options.txt");
        var modified = File.Exists(options) ? File.GetLastWriteTimeUtc(options) : DateTime.MinValue;
        if (modified != _optionsModified)
        {
            _optionsModified = modified;
            _guiScaleSetting = 0;
            try
            {
                foreach (var line in File.ReadLines(options))
                {
                    if (line.StartsWith("guiScale:", StringComparison.Ordinal))
                        int.TryParse(line[9..].Trim(), out _guiScaleSetting);
                }
            }
            catch (Exception)
            {
                // 读不到就按自动缩放
            }
        }
        var scale = 1;
        var max = _guiScaleSetting > 0 ? _guiScaleSetting : int.MaxValue;
        while (scale < max && w / (scale + 1) >= 320 && h / (scale + 1) >= 240)
            scale++;
        return scale;
    }

    // ------------------------------------------------------------------ 绘制

    private static readonly UIColor AccentFill = UIColor.FromRGBA(0, 217, 255, 0x88);
    private static readonly UIColor AccentStroke = UIColor.FromRGBA(0, 217, 255, 0xDD);
    private static readonly UIColor IdleFill = UIColor.FromRGBA(0, 0, 0, 0x55);
    private static readonly UIColor IdleStroke = UIColor.FromRGBA(255, 255, 255, 0x88);
    public override void Draw(CGRect rect)
    {
        foreach (var b in _buttons)
        {
            if (!b.Visible)
                continue;
            var on = b.Pressed || b.Latched;
            var path = UIBezierPath.FromRoundedRect(b.Rect, b.Round ? nfloat.Min(b.Rect.Width, b.Rect.Height) / 2 : 8);
            path.LineWidth = 1.5f;
            Fade(on ? AccentFill : IdleFill).SetFill();
            path.Fill();
            Fade(on ? AccentStroke : IdleStroke).SetStroke();
            path.Stroke();
            var fontSize = 13 * Math.Clamp(b.Size, 0.7, 1.4);
            var text = Label(b.Label, fontSize);
            var size = text.Size;
            if (size.Width > b.Rect.Width - 6)
            {
                text = Label(b.Label, Math.Max(7, fontSize * (b.Rect.Width - 6) / size.Width));
                size = text.Size;
            }
            text.DrawString(new CGPoint(b.Rect.GetMidX() - size.Width / 2, b.Rect.GetMidY() - size.Height / 2));
        }
        if (!_grabbing && _gamepadActive && _gamepadCursor is { } cursor)
        {
            var arrow = new UIBezierPath();
            arrow.MoveTo(cursor);
            arrow.AddLineTo(new CGPoint(cursor.X, cursor.Y + 20));
            arrow.AddLineTo(new CGPoint(cursor.X + 5, cursor.Y + 15));
            arrow.AddLineTo(new CGPoint(cursor.X + 9, cursor.Y + 23));
            arrow.AddLineTo(new CGPoint(cursor.X + 12, cursor.Y + 21.5));
            arrow.AddLineTo(new CGPoint(cursor.X + 8, cursor.Y + 13.5));
            arrow.AddLineTo(new CGPoint(cursor.X + 14, cursor.Y + 13.5));
            arrow.ClosePath();
            arrow.LineWidth = 1.5f;
            UIColor.White.SetFill();
            arrow.Fill();
            UIColor.Black.SetStroke();
            arrow.Stroke();
        }
        if (!_gamepadActive && !_stickHidden && _dpadMode)
        {
            DrawDpad();
        }
        else if (!_gamepadActive && !_stickHidden)
        {
            var ring = UIBezierPath.FromOval(new CGRect(_stickX - _stickRadius, _stickY - _stickRadius,
                                                        _stickRadius * 2, _stickRadius * 2));
            ring.LineWidth = 1.5f;
            Fade(UIColor.FromRGBA(0, 0, 0, 0x33)).SetFill();
            ring.Fill();
            Fade(UIColor.FromRGBA(255, 255, 255, 0x66)).SetStroke();
            ring.Stroke();
            var knob = UIBezierPath.FromOval(new CGRect(_knobX - _knobRadius, _knobY - _knobRadius,
                                                        _knobRadius * 2, _knobRadius * 2));
            Fade(_sprint ? UIColor.FromRGBA(0, 217, 255, 0x99) : UIColor.FromRGBA(255, 255, 255, 0x77)).SetFill();
            knob.Fill();
        }
    }

    private void DrawDpad()
    {
        var cell = _dpadRect.Width / 3;
        void Cell(int col, int row, bool on, double angle)
        {
            var r = new CGRect(_dpadRect.X + col * cell + 2, _dpadRect.Y + row * cell + 2, cell - 4, cell - 4);
            var path = UIBezierPath.FromRoundedRect(r, 8);
            path.LineWidth = 1.5f;
            Fade(on ? AccentFill : IdleFill).SetFill();
            path.Fill();
            Fade(on ? AccentStroke : IdleStroke).SetStroke();
            path.Stroke();
            // 指向 angle 方向（0 是上，顺时针）的三角形
            double cx = r.GetMidX(), cy = r.GetMidY(), s = cell * 0.18;
            var arrow = new UIBezierPath();
            for (var i = 0; i < 3; i++)
            {
                var a = angle + i * 2 * Math.PI / 3;
                var p = new CGPoint(cx + Math.Sin(a) * s, cy - Math.Cos(a) * s);
                if (i == 0)
                    arrow.MoveTo(p);
                else
                    arrow.AddLineTo(p);
            }
            arrow.ClosePath();
            Fade(UIColor.White).SetFill();
            arrow.Fill();
        }
        Cell(1, 0, _up && !_left && !_right, 0);
        Cell(0, 1, _left && !_up, -Math.PI / 2);
        Cell(2, 1, _right && !_up, Math.PI / 2);
        Cell(1, 2, _down, Math.PI);
        if (_up)
        {
            Cell(0, 0, _left, -Math.PI / 4);
            Cell(2, 0, _right, Math.PI / 4);
        }
        var center = new CGRect(_dpadRect.GetMidX() - cell * 0.32, _dpadRect.GetMidY() - cell * 0.32, cell * 0.64, cell * 0.64);
        var dot = UIBezierPath.FromOval(center);
        dot.LineWidth = 1.5f;
        Fade(_sneak.Latched ? AccentFill : IdleFill).SetFill();
        dot.Fill();
        Fade(_sneak.Latched ? AccentStroke : IdleStroke).SetStroke();
        dot.Stroke();
    }

    private NSAttributedString Label(string label, double fontSize) => new(label, new UIStringAttributes
    {
        Font = UIFont.BoldSystemFontOfSize((nfloat)fontSize),
        ForegroundColor = Fade(UIColor.White),
    });

    // ------------------------------------------------------------------ 输入法

    public void ShowKeyboard(bool show)
    {
        if (show == KeyboardShown)
            return;
        if (show)
        {
            _input.Text = " ";
            _input.BecomeFirstResponder();
        }
        else
        {
            _input.ResignFirstResponder();
            KeyboardHidden?.Invoke();
        }
        SetNeedsDisplay();
    }
}
