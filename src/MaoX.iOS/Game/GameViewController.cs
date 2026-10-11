using System.Runtime.InteropServices;
using CoreAnimation;
using CoreGraphics;
using Foundation;
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
    private SurfaceView _surface;
    private ControlsView _controls;
    private int _width, _height;
    private bool _launched;

    public GameViewController(string gameDir, ScreenInfo screen)
    {
        _gameDir = gameDir;
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
        })
        {
            KeyboardHidden = () => BecomeFirstResponder(),
        };
        _controls.SetWindowSize(_width, _height);
        View.AddSubviews(_surface, _controls);
    }

    public override void ViewDidLayoutSubviews()
    {
        base.ViewDidLayoutSubviews();
        var bounds = View.Bounds;
        if (bounds.Width < 1 || bounds.Height < 1)
            return;
        _surface.Frame = bounds;
        _controls.Frame = bounds;
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
                    UIApplication.SharedApplication.IdleTimerDisabled = true;
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

    /// <summary>游戏崩溃或异常退出：先显示日志末尾，用户确认后结束进程。</summary>
    [UnmanagedCallersOnly]
    private static void OnExit(int code)
    {
        try
        {
            if (_current == null)
            {
                GameNative.Exit(code);
                return;
            }
            UIViewController top = _current;
            while (top.PresentedViewController != null)
                top = top.PresentedViewController;
            var alert = UIAlertController.Create(F("游戏已退出（代码 {0}）", code), LogTail(), UIAlertControllerStyle.Alert);
            alert.AddAction(UIAlertAction.Create(T("关闭"), UIAlertActionStyle.Default, _ => GameNative.Exit(code)));
            top.PresentViewController(alert, true, null);
        }
        catch (Exception)
        {
            GameNative.Exit(code);
        }
    }

    private static string LogTail()
    {
        try
        {
            using var file = new FileStream(GameHost.LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var length = Math.Min(file.Length, 4096);
            file.Seek(-length, SeekOrigin.End);
            var buffer = new byte[length];
            file.ReadExactly(buffer);
            var lines = System.Text.Encoding.UTF8.GetString(buffer).Split('\n');
            return string.Join("\n", lines.Skip(1).TakeLast(20)).Trim();
        }
        catch (Exception)
        {
            return "";
        }
    }

    // ------------------------------------------------------------------ 屏幕和外接键盘

    public override UIInterfaceOrientationMask GetSupportedInterfaceOrientations() => UIInterfaceOrientationMask.Landscape;

    public override bool PrefersStatusBarHidden() => true;

    public override bool PrefersHomeIndicatorAutoHidden => true;

    public override UIRectEdge PreferredScreenEdgesDeferringSystemGestures => UIRectEdge.All;

    public override bool CanBecomeFirstResponder => true;

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
