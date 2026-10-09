using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace MaoX.Controls;

/// <summary>账号头像：有皮肤时画像素风的脸（含帽子层），否则画带首字母的圆形。</summary>
public class Avatar : Control
{
    public static readonly StyledProperty<string> NameTextProperty =
        AvaloniaProperty.Register<Avatar, string>(nameof(NameText));

    public static readonly StyledProperty<Bitmap> SkinProperty =
        AvaloniaProperty.Register<Avatar, Bitmap>(nameof(Skin));

    static Avatar()
    {
        AffectsRender<Avatar>(NameTextProperty, SkinProperty);
    }

    public string NameText
    {
        get => GetValue(NameTextProperty);
        set => SetValue(NameTextProperty, value);
    }

    public Bitmap Skin
    {
        get => GetValue(SkinProperty);
        set => SetValue(SkinProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        var rect = new Rect((Bounds.Width - size) / 2, (Bounds.Height - size) / 2, size, size);
        var accent = Brush("Accent", "#00D9FF");
        if (Skin != null && Skin.PixelSize.Width >= 64)
        {
            var radius = size * 0.22;
            using (context.PushClip(new RoundedRect(rect, radius)))
            using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None }))
            {
                var unit = Skin.PixelSize.Width / 64.0;
                context.DrawImage(Skin, new Rect(8 * unit, 8 * unit, 8 * unit, 8 * unit), rect);
                context.DrawImage(Skin, new Rect(40 * unit, 8 * unit, 8 * unit, 8 * unit), rect);
            }
            return;
        }
        var center = rect.Center;
        context.DrawEllipse(Brush("AccentDim", "#0C2F3A"), new Pen(accent, 1.2), center, size / 2 - 0.6, size / 2 - 0.6);
        var letter = string.IsNullOrEmpty(NameText) ? "?" : NameText[..1].ToUpperInvariant();
        var text = new FormattedText(letter, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                     new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold), size * 0.42,
                                     accent);
        context.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
    }

    private IBrush Brush(string key, string fallback) =>
        this.TryFindResource(key, out var value) && value is IBrush brush ? brush : new SolidColorBrush(Color.Parse(fallback));
}

/// <summary>窗口底部的细进度条，任务开始时显示不确定动画，收到进度后显示百分比。</summary>
public class ProgressLine : Control
{
    private double _value;
    private bool _indeterminate;
    private double _phase;
    private readonly DispatcherTimer _timer;

    public ProgressLine()
    {
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) =>
        {
            _phase = (_phase + 0.012) % 1.4;
            InvalidateVisual();
        });
    }

    public void Start()
    {
        _indeterminate = true;
        _value = 0;
        _timer.Start();
        InvalidateVisual();
    }

    public void Set(double value)
    {
        _indeterminate = false;
        _timer.Stop();
        _value = Math.Clamp(value, 0, 1);
        InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_indeterminate)
            _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer.Stop();
    }

    public override void Render(DrawingContext context)
    {
        var accent = this.TryFindResource("Accent", out var v) && v is IBrush b ? b : Brushes.Cyan;
        var w = Bounds.Width;
        if (_indeterminate)
        {
            var start = (_phase - 0.4) * w;
            var rect = new Rect(Math.Max(0, start), 0, Math.Max(0, Math.Min(w, start + 0.4 * w) - Math.Max(0, start)),
                                Bounds.Height);
            if (rect.Width > 0)
                context.FillRectangle(accent, rect);
        }
        else if (_value > 0)
        {
            context.FillRectangle(accent, new Rect(0, 0, w * _value, Bounds.Height));
        }
    }
}
