using System.Runtime.InteropServices;
using CoreAnimation;
using CoreGraphics;
using Foundation;
using MaoX.Core;
using ObjCRuntime;
using UIKit;
using static MaoX.Core.I18n;

namespace MaoX.iOS.Game;

/// <summary>游戏画面：Metal 图层交给 ANGLE / gl4es 渲染，上面盖一层触屏操作。全屏盖在启动器界面上。</summary>
internal sealed class GameViewController : UIViewController
{
    /// <summary>游戏分辨率的长边上限，全分辨率在手机上太卡。</summary>
    private const double MaxLongSide = 1920;

    public readonly record struct ScreenInfo(int Width, int Height, int MaxFps);

    private static GameViewController _current;

    private readonly string _gameDir;
    private readonly ControlLayout _layout;
    private SurfaceView _surface;
    private ControlsView _controls;
    private UILabel _room;
    private string _roomCopy;
    private DateTime _copiedUntil;
    private int _width, _height;
    private bool _launched;
    private DateTime _started = DateTime.Now;
    private GamepadInput _gamepad;

    public GameViewController(string gameDir, ScreenInfo screen, ControlLayout layout)
    {
        _gameDir = gameDir;
        _layout = layout;
        _width = screen.Width;
        _height = screen.Height;
        ModalPresentationStyle = UIModalPresentationStyle.FullScreen;
    }

    /// <summary>启动前按横屏全屏估算游戏分辨率。</summary>
    public static ScreenInfo InitialSize()
    {
        var screen = UIScreen.MainScreen;
        double w = screen.Bounds.Width, h = screen.Bounds.Height;
        var (width, height, _) = Measure(Math.Max(w, h), Math.Min(w, h), screen.Scale);
        return new ScreenInfo(width, height, (int)screen.MaximumFramesPerSecond);
    }

    private static (int Width, int Height, double Scale) Measure(double width, double height, double screenScale)
    {
        var scale = screenScale * Math.Min(1, MaxLongSide / (Math.Max(width, height) * screenScale));
        // 分辨率不能是奇数
        var w = Math.Max(2, (int)Math.Round(width * scale) & ~1);
        var h = Math.Max(2, (int)Math.Round(height * scale) & ~1);
        return (w, h, scale);
    }

    public override void LoadView()
    {
        View = new UIView { BackgroundColor = UIColor.Black, MultipleTouchEnabled = true };
        _surface = new SurfaceView();
        _controls = new ControlsView(_gameDir, new Dictionary<string, string>
        {
            ["jump"] = T("跳跃"), ["sneak"] = T("潜行"), ["inventory"] = T("背包"), ["drop"] = T("丢弃"),
            ["chat"] = T("聊天"), ["view"] = T("视角"), ["pause"] = T("暂停"), ["keyboard"] = T("键盘"),
            ["attack"] = T("攻击"), ["use"] = T("使用"),
        }, _layout)
        {
            KeyboardHidden = () => BecomeFirstResponder(),
        };
        _controls.SetWindowSize(_width, _height);
        _room = new UILabel
        {
            BackgroundColor = UIColor.FromWhiteAlpha(0, 0.55f),
            TextColor = UIColor.White,
            Font = UIFont.SystemFontOfSize(13, UIFontWeight.Semibold),
            TextAlignment = UITextAlignment.Center,
            Hidden = true,
            UserInteractionEnabled = true,
            ClipsToBounds = true,
        };
        _room.Layer.CornerRadius = 8;
        _room.AddGestureRecognizer(new UITapGestureRecognizer(CopyRoom));
        View.AddSubviews(_surface, _controls, _room);
    }

    // ------------------------------------------------------------------ 陶瓦联机

    /// <summary>游戏盖住了启动器界面，房间状态和邀请码显示在游戏画面顶部。</summary>
    private void UpdateRoom()
    {
        string text = null, copy = null;
        try
        {
            if (IosTerracotta.Instance.Started)
            {
                var state = Json.Parse(IosTerracotta.Instance.State());
                var room = state.Str("room", "");
                var url = state.Str("url", "");
                (text, copy) = state.Str("state") switch
                {
                    "host-scanning" => (T("陶瓦联机：在暂停菜单里选「对局域网开放」后会自动创建房间"), null),
                    "host-starting" => (T("陶瓦联机：正在创建房间…"), null),
                    "host-ok" => (F("陶瓦联机邀请码：{0}（点按复制）", room), room),
                    "guest-connecting" or "guest-starting" => (T("陶瓦联机：正在连接房主…"), null),
                    "guest-ok" => (F("陶瓦联机：已加入房间，服务器地址 {0}（点按复制）", url), url),
                    "exception" => (T("陶瓦联机已中断，请回到启动器重新创建或加入"), null),
                    _ => (null, null),
                };
            }
        }
        catch (Exception)
        {
            // 状态读取失败时先不显示
        }
        _roomCopy = copy;
        if (DateTime.UtcNow < _copiedUntil)
            return;
        _room.Hidden = text == null;
        if (text != null && _room.Text != text)
        {
            _room.Text = text;
            LayoutRoom();
        }
    }

    private void CopyRoom()
    {
        if (string.IsNullOrEmpty(_roomCopy))
            return;
        UIPasteboard.General.String = _roomCopy;
        _copiedUntil = DateTime.UtcNow.AddSeconds(2);
        _room.Text = T("已复制，发给好友吧");
        LayoutRoom();
    }

    private void LayoutRoom()
    {
        var bounds = View.Bounds;
        // UILabel 没有内边距，宽高各加一点留白
        var size = _room.SizeThatFits(new CGSize(bounds.Width - 64, 40));
        var width = Math.Min(size.Width + 24, bounds.Width - 40);
        _room.Frame = new CGRect((bounds.Width - width) / 2, View.SafeAreaInsets.Top + 8, width, size.Height + 12);
    }

    public override void ViewDidLayoutSubviews()
    {
        base.ViewDidLayoutSubviews();
        var bounds = View.Bounds;
        if (bounds.Width < 1 || bounds.Height < 1)
            return;
        _surface.Frame = bounds;
        _controls.Frame = bounds;
        LayoutRoom();
        var screenScale = (double)(View.Window?.Screen.Scale ?? UIScreen.MainScreen.Scale);
        var (w, h, scale) = Measure(bounds.Width, bounds.Height, screenScale);
        _surface.Layer.ContentsScale = (nfloat)scale;
        _controls.PixelScale = scale;
        if (w == _width && h == _height)
            return;
        _width = w;
        _height = h;
        _controls.SetWindowSize(w, h);
        if (_launched)
            GameNative.SendScreenSize(w, h);
        else
            GameNative.SetWindowSize(w, h);
    }

    /// <summary>盖到启动器界面上，再在游戏线程里启动 Java。返回 0 表示已启动。</summary>
    public Task<int> LaunchAsync(string json)
    {
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = TopController();
        if (host == null)
        {
            tcs.SetException(new InvalidOperationException(T("找不到启动器窗口")));
            return tcs.Task;
        }
        host.PresentViewController(this, false, () =>
        {
            try
            {
                View.LayoutIfNeeded();
                GameNative.SetWindowSize(_width, _height);
                _current = this;
                unsafe
                {
                    GameNative.SetGrabCallback(&OnGrab);
                    GameNative.SetExitCallback(&OnExit);
                }
                var code = GameNative.Launch(json, _surface.Layer.Handle);
                if (code == 0)
                {
                    _launched = true;
                    _started = DateTime.Now;
                    UIApplication.SharedApplication.IdleTimerDisabled = true;
                    NSTimer.CreateRepeatingScheduledTimer(2, _ => UpdateRoom());
                    _gamepad = new GamepadInput(_controls);
                    _gamepad.Start();
                }
                else
                {
                    _current = null;
                    DismissViewController(false, null);
                }
                tcs.SetResult(code);
            }
            catch (Exception e)
            {
                _current = null;
                DismissViewController(false, null);
                tcs.SetException(e);
            }
        });
        return tcs.Task;
    }

    private static UIViewController TopController()
    {
        var window = UIApplication.SharedApplication.ConnectedScenes.ToArray().OfType<UIWindowScene>()
                                  .SelectMany(s => s.Windows).FirstOrDefault(w => w.IsKeyWindow)
                     ?? UIApplication.SharedApplication.ConnectedScenes.ToArray().OfType<UIWindowScene>()
                                     .SelectMany(s => s.Windows).FirstOrDefault();
        var top = window?.RootViewController;
        while (top?.PresentedViewController != null)
            top = top.PresentedViewController;
        return top;
    }

    [UnmanagedCallersOnly]
    private static void OnGrab(int grabbing)
    {
        try
        {
            _current?._controls.SetGrabbing(grabbing != 0);
        }
        catch (Exception)
        {
            // 不能让异常穿过原生回调
        }
    }

    /// <summary>游戏退出：正常退出直接回到启动器；崩溃先看日志，再回到启动器（不要 suspend 到桌面）。</summary>
    [UnmanagedCallersOnly]
    private static void OnExit(int code)
    {
        try
        {
            UIApplication.SharedApplication.IdleTimerDisabled = false;
            _current?._gamepad?.Stop();
            var current = _current;
            if (current != null && !CleanExit(current, code))
            {
                UIViewController top = current;
                while (top.PresentedViewController != null)
                    top = top.PresentedViewController;
                var alert = UIAlertController.Create(T("游戏崩溃了"), CrashMessage(current, code), UIAlertControllerStyle.Alert);
                alert.AddAction(UIAlertAction.Create(T("回到启动器"), UIAlertActionStyle.Default, _ => ReturnToLauncher()));
                top.PresentViewController(alert, true, null);
                return;
            }
            ReturnToLauncher();
        }
        catch (Exception)
        {
            ReturnToLauncher();
        }
    }

    private static void ReturnToLauncher()
    {
        var current = _current;
        _current = null;
        if (current == null)
        {
            GameNative.Relaunch();
            return;
        }
        current.DismissViewController(false, () => GameNative.Relaunch());
    }

    private static bool CleanExit(GameViewController current, int code)
    {
        try
        {
            var report = AnalyzeExit(current, code);
            return report.MenuQuit || (code == 0 && report.Reasons.All(r => r.Kind != CrashAnalyzer.Mod));
        }
        catch (Exception)
        {
            return code == 0;
        }
    }

    private static string CrashMessage(GameViewController current, int code)
    {
        try
        {
            var report = AnalyzeExit(current, code);
            var lines = report.Reasons.Select(r => r.Text).Take(4).ToList();
            if (lines.Count == 0 && !string.IsNullOrWhiteSpace(report.Detail))
                lines.Add(report.Detail);
            if (lines.Count == 0)
                lines.Add(CrashAnalyzer.ExitCaption(code));
            return string.Join("\n", lines);
        }
        catch (Exception)
        {
            return CrashAnalyzer.ExitCaption(code);
        }
    }

    private static CrashReport AnalyzeExit(GameViewController current, int code) =>
        CrashAnalyzer.Analyze(ReadLogLines(), current._gameDir, current._started, code);

    private static List<string> ReadLogLines()
    {
        try
        {
            using var file = new FileStream(GameHost.LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var length = Math.Min(file.Length, 256 * 1024);
            if (length <= 0)
                return [];
            file.Seek(-length, SeekOrigin.End);
            var buffer = new byte[length];
            file.ReadExactly(buffer);
            return System.Text.Encoding.UTF8.GetString(buffer).Split('\n').TakeLast(2000).ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    // ------------------------------------------------------------------ 屏幕和外接键盘

    public override UIInterfaceOrientationMask GetSupportedInterfaceOrientations() => UIInterfaceOrientationMask.Landscape;

    public override bool PrefersStatusBarHidden() => true;

    public override bool PrefersHomeIndicatorAutoHidden => true;

    public override UIRectEdge PreferredScreenEdgesDeferringSystemGestures => UIRectEdge.All;

    public override bool CanBecomeFirstResponder => _controls is not { KeyboardShown: true };

    public override void ViewDidAppear(bool animated)
    {
        base.ViewDidAppear(animated);
        BecomeFirstResponder();
    }

    public override void PressesBegan(NSSet<UIPress> presses, UIPressesEvent evt)
    {
        if (!HandlePresses(presses, true))
            base.PressesBegan(presses, evt);
    }

    public override void PressesEnded(NSSet<UIPress> presses, UIPressesEvent evt)
    {
        if (!HandlePresses(presses, false))
            base.PressesEnded(presses, evt);
    }

    public override void PressesCancelled(NSSet<UIPress> presses, UIPressesEvent evt)
    {
        if (!HandlePresses(presses, false))
            base.PressesCancelled(presses, evt);
    }

    private bool HandlePresses(NSSet<UIPress> presses, bool down)
    {
        if (!_launched)
            return false;
        var handled = false;
        foreach (var press in presses)
        {
            var key = press.Key;
            var code = key == null ? 0 : Keys.FromHid((int)key.KeyCode);
            if (code == 0)
                continue;
            handled = true;
            GameNative.SendKey(code, down);
            // 软键盘没打开时，外接键盘打的字也要发给游戏（聊天框）
            var chars = key.Characters;
            if (down && !_controls.KeyboardShown && !string.IsNullOrEmpty(chars) && !char.IsControl(chars[0])
                && (key.ModifierFlags & (UIKeyModifierFlags.Command | UIKeyModifierFlags.Control)) == 0)
            {
                foreach (var c in chars)
                    GameNative.SendChar(c);
            }
        }
        return handled;
    }

    /// <summary>图层用 CAMetalLayer，ANGLE 和 MoltenVK 直接往里画。</summary>
    private sealed class SurfaceView : UIView
    {
        [Export("layerClass")]
        public static Class GetLayerClass() => new(typeof(CAMetalLayer));

        public SurfaceView()
        {
            BackgroundColor = UIColor.Black;
            Opaque = true;
            UserInteractionEnabled = false;
            Layer.DrawsAsynchronously = true;
        }
    }
}
