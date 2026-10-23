using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media.Transformation;
using Avalonia.Threading;

namespace MaoX.Controls;

/// <summary>标签条的滑动指示块，以及标签内容的淡入滑出。</summary>
internal static class PageTabs
{
    public static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(220);

    public static void PreparePill(Border pill)
    {
        pill.HorizontalAlignment = HorizontalAlignment.Left;
        pill.VerticalAlignment = VerticalAlignment.Top;
        pill.IsHitTestVisible = false;
        pill.Transitions =
        [
            new DoubleTransition
            {
                Property = Layoutable.WidthProperty, Duration = Duration, Easing = new CubicEaseOut(),
            },
            new DoubleTransition
            {
                Property = Layoutable.HeightProperty, Duration = Duration, Easing = new CubicEaseOut(),
            },
            new DoubleTransition
            {
                Property = Visual.OpacityProperty, Duration = Duration, Easing = new CubicEaseOut(),
            },
            new ThicknessTransition
            {
                Property = Layoutable.MarginProperty, Duration = Duration, Easing = new CubicEaseOut(),
            },
        ];
    }

    public static void MovePill(Border pill, Control tab, bool animate)
    {
        if (pill.Parent is not Visual parent || tab is not { IsVisible: true } || tab.Bounds.Width < 2)
        {
            pill.Opacity = 0;
            return;
        }
        var origin = tab.TranslatePoint(default, parent) ?? default;
        var width = tab.Bounds.Width;
        var height = tab.Bounds.Height;
        var margin = new Thickness(origin.X, origin.Y, 0, 0);
        if (!animate)
        {
            var saved = pill.Transitions;
            pill.Transitions = null;
            pill.Width = width;
            pill.Height = height;
            pill.Margin = margin;
            pill.Opacity = 1;
            pill.Transitions = saved;
            return;
        }
        pill.Width = width;
        pill.Height = height;
        pill.Margin = margin;
        pill.Opacity = 1;
    }

    public static int Direction(IReadOnlyList<string> keys, string from, string to)
    {
        var a = IndexOf(keys, from);
        var b = IndexOf(keys, to);
        return a < 0 || b < 0 || b >= a ? 1 : -1;
    }

    private static int IndexOf(IReadOnlyList<string> keys, string key)
    {
        for (var i = 0; i < keys.Count; i++)
        {
            if (keys[i] == key)
                return i;
        }
        return -1;
    }

    /// <summary>立刻显示 <paramref name="show"/>，其余淡出后隐藏。direction：1 从右滑入，-1 从左滑入。</summary>
    public static void Swap(Control show, Control[] hide, int direction)
    {
        foreach (var item in hide)
        {
            if (item != show && item.IsVisible)
                Fade(item, false, direction);
        }
        Fade(show, true, direction);
    }

    public static void ShowNow(Control show, params Control[] hide)
    {
        foreach (var item in hide)
        {
            if (item == show)
                continue;
            item.IsVisible = false;
            item.Opacity = 1;
            item.RenderTransform = null;
            item.IsHitTestVisible = true;
        }
        show.IsVisible = true;
        show.Opacity = 1;
        show.RenderTransform = null;
        show.IsHitTestVisible = true;
    }

    private static void Fade(Control control, bool show, int direction)
    {
        if (show)
        {
            if (!control.IsVisible)
            {
                control.Transitions = null;
                control.Opacity = 0;
                control.RenderTransform = Shift(direction * 18);
                control.IsVisible = true;
            }
            EnsureMotion(control);
            control.IsHitTestVisible = true;
            Dispatcher.UIThread.Post(() =>
            {
                control.Opacity = 1;
                control.RenderTransform = Shift(0);
            }, DispatcherPriority.Render);
            return;
        }

        EnsureMotion(control);
        control.IsHitTestVisible = false;
        control.Opacity = 0;
        control.RenderTransform = Shift(-direction * 12);
        DispatcherTimer.RunOnce(() =>
        {
            if (!control.IsHitTestVisible && control.Opacity == 0)
            {
                control.IsVisible = false;
                control.RenderTransform = null;
            }
        }, Duration);
    }

    private static void EnsureMotion(Control control)
    {
        if (control.Transitions is { Count: > 0 })
            return;
        control.Transitions =
        [
            new DoubleTransition
            {
                Property = Visual.OpacityProperty, Duration = Duration, Easing = new CubicEaseOut(),
            },
            new TransformOperationsTransition
            {
                Property = Visual.RenderTransformProperty, Duration = Duration, Easing = new CubicEaseOut(),
            },
        ];
    }

    private static TransformOperations Shift(double x) =>
        TransformOperations.Parse(x == 0 ? "translateX(0px)" : $"translateX({x:0.##}px)");
}
