using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;

namespace MaoX.Controls;

/// <summary>按名称绘制 Styles/Icons.axaml 中的 24×24 线条图标，颜色跟随文字前景色。</summary>
public class Icon : Control
{
    public static readonly StyledProperty<string> KindProperty =
        AvaloniaProperty.Register<Icon, string>(nameof(Kind));

    public static readonly StyledProperty<IBrush> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<Icon>();

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<Icon, double>(nameof(Size), 18);

    public static readonly StyledProperty<double> StrokeWidthProperty =
        AvaloniaProperty.Register<Icon, double>(nameof(StrokeWidth), 1.8);

    public static readonly StyledProperty<bool> FilledProperty =
        AvaloniaProperty.Register<Icon, bool>(nameof(Filled));

    static Icon()
    {
        AffectsRender<Icon>(KindProperty, ForegroundProperty, StrokeWidthProperty, FilledProperty);
        AffectsMeasure<Icon>(SizeProperty);
        VerticalAlignmentProperty.OverrideDefaultValue<Icon>(VerticalAlignment.Center);
        HorizontalAlignmentProperty.OverrideDefaultValue<Icon>(HorizontalAlignment.Center);
    }

    public string Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public IBrush Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public double StrokeWidth
    {
        get => GetValue(StrokeWidthProperty);
        set => SetValue(StrokeWidthProperty, value);
    }

    public bool Filled
    {
        get => GetValue(FilledProperty);
        set => SetValue(FilledProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    public override void Render(DrawingContext context)
    {
        if (string.IsNullOrEmpty(Kind) || !this.TryFindResource("Icon." + Kind, out var resource)
                                       || resource is not Geometry geometry)
            return;
        var scale = Math.Min(Bounds.Width, Bounds.Height) / 24.0;
        var offset = new Point((Bounds.Width - 24 * scale) / 2, (Bounds.Height - 24 * scale) / 2);
        // 描边宽度以 24×24 的图标坐标为单位，随图标一起缩放
        var pen = new Pen(Foreground, StrokeWidth, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset.X, offset.Y)))
            context.DrawGeometry(Filled ? Foreground : null, pen, geometry);
    }
}

/// <summary>M 与 X 融合的标志：左右两根竖线 + 贯穿的 X。</summary>
public class Logo : Control
{
    private static readonly Geometry Bars = Geometry.Parse("M0,0 H20 V100 H0 Z M80,0 H100 V100 H80 Z");
    private static readonly Geometry Cross = Geometry.Parse("M0,0 H23 L100,100 H77 Z M77,0 H100 L23,100 H0 Z");

    public static readonly StyledProperty<IBrush> BarBrushProperty =
        AvaloniaProperty.Register<Logo, IBrush>(nameof(BarBrush), new SolidColorBrush(Color.Parse("#E8ECF3")));

    public static readonly StyledProperty<IBrush> CrossBrushProperty =
        AvaloniaProperty.Register<Logo, IBrush>(nameof(CrossBrush), new SolidColorBrush(Color.Parse("#00D9FF")));

    static Logo()
    {
        AffectsRender<Logo>(BarBrushProperty, CrossBrushProperty);
    }

    public IBrush BarBrush
    {
        get => GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    public IBrush CrossBrush
    {
        get => GetValue(CrossBrushProperty);
        set => SetValue(CrossBrushProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        var scale = size / 100.0;
        var offset = Matrix.CreateTranslation((Bounds.Width - size) / 2, (Bounds.Height - size) / 2);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * offset))
        {
            context.DrawGeometry(BarBrush, null, Bars);
            context.DrawGeometry(CrossBrush, null, Cross);
        }
    }
}

/// <summary>图标 + 文字，常用于按钮内容。</summary>
public class IconLabel : StackPanel
{
    public static readonly StyledProperty<string> IconProperty =
        AvaloniaProperty.Register<IconLabel, string>(nameof(Icon));

    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<IconLabel, string>(nameof(Text));

    public static readonly StyledProperty<double> IconSizeProperty =
        AvaloniaProperty.Register<IconLabel, double>(nameof(IconSize), 16);

    private readonly Icon _icon = new();
    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center };

    static IconLabel()
    {
        OrientationProperty.OverrideDefaultValue<IconLabel>(Orientation.Horizontal);
        SpacingProperty.OverrideDefaultValue<IconLabel>(8);
        VerticalAlignmentProperty.OverrideDefaultValue<IconLabel>(VerticalAlignment.Center);
        HorizontalAlignmentProperty.OverrideDefaultValue<IconLabel>(HorizontalAlignment.Center);
    }

    public IconLabel()
    {
        Children.Add(_icon);
        Children.Add(_text);
        _icon.Size = IconSize;
    }

    public string Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public double IconSize
    {
        get => GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IconProperty)
        {
            _icon.Kind = Icon;
            _icon.IsVisible = !string.IsNullOrEmpty(Icon);
        }
        else if (change.Property == TextProperty)
        {
            _text.Text = Text;
            _text.IsVisible = !string.IsNullOrEmpty(Text);
        }
        else if (change.Property == IconSizeProperty)
        {
            _icon.Size = IconSize;
        }
    }
}
