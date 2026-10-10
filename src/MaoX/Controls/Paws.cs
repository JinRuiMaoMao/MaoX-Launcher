using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace MaoX.Controls;

/// <summary>画 Styles/Icons.axaml 里的狼爪（爪尖朝上的 24×24 图形）。</summary>
internal static class PawShape
{
    public static void Draw(DrawingContext context, Geometry paw, IBrush brush, Point center, double size, double degrees,
                            double opacity)
    {
        var transform = Matrix.CreateTranslation(-12, -12) * Matrix.CreateScale(size / 24, size / 24)
                        * Matrix.CreateRotation(degrees * Math.PI / 180) * Matrix.CreateTranslation(center.X, center.Y);
        using (context.PushOpacity(opacity))
        using (context.PushTransform(transform))
            context.DrawGeometry(brush, null, paw);
    }
}

/// <summary>加载动画：一串狼爪印从左往右依次踩出来，再一起淡去。</summary>
public class PawLoader : Control
{
    private const int Count = 4;
    private const double CycleMs = 1700;

    public static readonly StyledProperty<IBrush> ForegroundProperty =
        AvaloniaProperty.Register<PawLoader, IBrush>(nameof(Foreground));

    public static readonly StyledProperty<double> PawSizeProperty =
        AvaloniaProperty.Register<PawLoader, double>(nameof(PawSize), 15);

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _timer;

    static PawLoader()
    {
        AffectsMeasure<PawLoader>(PawSizeProperty);
        HorizontalAlignmentProperty.OverrideDefaultValue<PawLoader>(HorizontalAlignment.Center);
    }

    public PawLoader()
    {
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => InvalidateVisual());
        _timer.Stop();
    }

    public IBrush Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double PawSize
    {
        get => GetValue(PawSizeProperty);
        set => SetValue(PawSizeProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer.Stop();
    }

    protected override Size MeasureOverride(Size availableSize) => new(PawSize * (Count * 1.3 + 0.2), PawSize * 1.9);

    public override void Render(DrawingContext context)
    {
        if (!this.TryFindResource("Icon.paw", out var resource) || resource is not Geometry paw)
            return;
        var brush = Foreground ?? (this.TryFindResource("Accent", out var accent) ? accent as IBrush : null) ?? Brushes.Gray;
        var t = _clock.Elapsed.TotalMilliseconds % CycleMs / CycleMs;
        for (var i = 0; i < Count; i++)
        {
            var start = i * 0.15;
            var opacity = t < start ? 0
                : t < start + 0.08 ? (t - start) / 0.08
                : t < 0.8 ? 1
                : Math.Max(0, 1 - (t - 0.8) / 0.15);
            if (opacity <= 0)
                continue;
            var center = new Point(PawSize * (0.6 + i * 1.3), Bounds.Height / 2 + (i % 2 == 0 ? 1 : -1) * PawSize * 0.32);
            PawShape.Draw(context, paw, brush, center, PawSize, 90, opacity);
        }
    }
}

/// <summary>背景装饰：一串很淡的狼爪印，从右下角往左上方走去，越往前越淡。</summary>
public class PawTrail : Control
{
    private const int Count = 6;

    public static readonly StyledProperty<IBrush> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<PawTrail>();

    static PawTrail()
    {
        AffectsRender<PawTrail>(ForegroundProperty);
    }

    public IBrush Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Foreground == null || !this.TryFindResource("Icon.paw", out var resource) || resource is not Geometry paw)
            return;
        var size = Math.Min(Bounds.Width, Bounds.Height) / 9;
        var position = new Point(Bounds.Width - size * 1.2, Bounds.Height - size * 1.1);
        var heading = -40.0;
        for (var i = 0; i < Count; i++)
        {
            var radians = heading * Math.PI / 180;
            var forward = new Vector(Math.Sin(radians), -Math.Cos(radians));
            var side = new Vector(-forward.Y, forward.X) * (i % 2 == 0 ? 0.42 : -0.42) * size;
            PawShape.Draw(context, paw, Foreground, position + side, size, heading, 1 - i * 0.13);
            position += forward * size * 1.55;
            heading -= 7;
        }
    }
}
