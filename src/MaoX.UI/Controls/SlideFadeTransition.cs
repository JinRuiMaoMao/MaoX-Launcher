using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Media;
using Avalonia.Styling;

namespace MaoX.Controls;

/// <summary>
/// 标签页切换：淡入淡出，同时沿方向轻轻滑入 / 滑出。
/// 幅度对照 Axolotl（约 30px），而不是整页滑走。
/// </summary>
public sealed class SlideFadeTransition : IPageTransition
{
    public TimeSpan Duration { get; set; } = TimeSpan.FromMilliseconds(220);
    public double Offset { get; set; } = 28;

    public async Task Start(Visual from, Visual to, bool forward, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || from == to)
            return;

        var delta = forward ? Offset : -Offset;
        var tasks = new List<Task>(2);
        if (from != null)
        {
            from.RenderTransform = new TranslateTransform();
            tasks.Add(Run(from, 1, 0, 0, -delta * 0.4, cancellationToken));
        }
        if (to != null)
        {
            to.RenderTransform = new TranslateTransform();
            tasks.Add(Run(to, 0, 1, delta, 0, cancellationToken));
        }
        if (tasks.Count > 0)
            await Task.WhenAll(tasks);

        Reset(from);
        Reset(to);
    }

    private Task Run(Visual visual, double fromOpacity, double toOpacity, double fromX, double toX,
                     CancellationToken cancellationToken)
    {
        var animation = new Animation
        {
            Duration = Duration,
            Easing = new CubicEaseOut(),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0),
                    Setters =
                    {
                        new Setter(Visual.OpacityProperty, fromOpacity),
                        new Setter(TranslateTransform.XProperty, fromX),
                    },
                },
                new KeyFrame
                {
                    Cue = new Cue(1),
                    Setters =
                    {
                        new Setter(Visual.OpacityProperty, toOpacity),
                        new Setter(TranslateTransform.XProperty, toX),
                    },
                },
            },
        };
        return animation.RunAsync(visual, cancellationToken);
    }

    private static void Reset(Visual visual)
    {
        if (visual == null)
            return;
        visual.Opacity = 1;
        visual.RenderTransform = null;
    }
}
