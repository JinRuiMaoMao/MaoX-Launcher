using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using MaoX.Core;

namespace MaoX.Controls;

/// <summary>节日装饰：边角放能认出节日的物件。可单独关掉。</summary>
public class HolidayDecor : Control
{
    /// <summary>冒烟时固定在同一帧，避免截图闪。</summary>
    public static bool Frozen { get; set; }

    private static readonly FontFamily SealFont = new("Microsoft YaHei UI, Microsoft YaHei, PingFang SC, Noto Sans CJK SC, sans-serif");

    private readonly DispatcherTimer _timer;
    private string _key;
    private double _phase;

    public HolidayDecor()
    {
        IsHitTestVisible = false;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Render, (_, _) =>
        {
            if (!Showing)
                return;
            _phase += 0.04;
            InvalidateVisual();
        });
        _timer.Stop();
    }

    public string Motif => Showing ? Holiday.Active.Key : null;

    public bool Showing => Holiday.Active != null && Holiday.ShowDecor;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ThemeManager.Changed += OnTheme;
        OnTheme();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ThemeManager.Changed -= OnTheme;
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnTheme()
    {
        var key = Showing ? Holiday.Active.Key : null;
        if (key != _key)
        {
            _key = key;
            _phase = Frozen ? 1.2 : 0;
        }
        if (key == null)
            _timer.Stop();
        else if (!_timer.IsEnabled)
            _timer.Start();
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (!Showing || Bounds.Width < 8 || Bounds.Height < 8)
            return;
        var t = Frozen ? 1.2 : _phase;
        var w = Bounds.Width;
        var h = Bounds.Height;
        switch (Holiday.Active.Key)
        {
            case "newyear":
                DrawFirework(context, new Point(w * 0.88, h * 0.16), 88, t, Gold, Red);
                DrawFirework(context, new Point(w * 0.74, h * 0.30), 46, t + 0.9, Red, Gold);
                DrawSeal(context, new Point(w * 0.14, h * 0.20), 54, "春", Red, Gold);
                break;
            case "spring":
                DrawLantern(context, new Point(w * 0.90, 8), 46, 0.06, Red, Gold);
                DrawLantern(context, new Point(w * 0.80, 4), 34, -0.08, Red, Gold);
                DrawLantern(context, new Point(w * 0.13, 10), 30, 0.04, Red, Gold);
                DrawSeal(context, new Point(w * 0.88, h * 0.78), 64, "福", Red, Gold);
                DrawEnvelope(context, new Point(w * 0.14, h * 0.74), 40, -0.18, Red, Gold);
                break;
            case "lantern":
                DrawLantern(context, new Point(w * 0.90, 10), 50, 0.07, Orange, Gold);
                DrawLantern(context, new Point(w * 0.79, 6), 36, -0.1, Red, Gold);
                DrawLantern(context, new Point(w * 0.14, 12), 34, 0.05, Orange, Gold);
                DrawFullMoon(context, new Point(w * 0.88, h * 0.24), 58);
                DrawSeal(context, new Point(w * 0.14, h * 0.76), 48, "元宵", Orange, Paper);
                break;
            case "valentine":
                DrawHeart(context, new Point(w * 0.88, h * 0.18), 72, -0.1, Rose);
                DrawHeart(context, new Point(w * 0.78, h * 0.34), 36, 0.18, Pink);
                DrawHeart(context, new Point(w * 0.14, h * 0.22), 40, 0.12, Rose);
                DrawHeart(context, new Point(w * 0.20, h * 0.78), 28, -0.2, Pink);
                break;
            case "womensday":
                DrawFlower(context, new Point(w * 0.88, h * 0.18), 64, t * 0.1, Pink, Gold);
                DrawFlower(context, new Point(w * 0.76, h * 0.32), 34, -t * 0.08, Rose, Paper);
                DrawFlower(context, new Point(w * 0.14, h * 0.22), 40, t * 0.09, Pink, Gold);
                break;
            case "aprilfools":
                DrawJester(context, new Point(w * 0.88, h * 0.18), 70, 0.08, Pink, Gold, Rose);
                DrawJester(context, new Point(w * 0.14, h * 0.22), 44, -0.16, Gold, Pink, Paper);
                DrawQuestion(context, new Point(w * 0.80, h * 0.36), 28, 0.2, Pink);
                DrawQuestion(context, new Point(w * 0.18, h * 0.78), 24, -0.15, Rose);
                break;
            case "labor":
                DrawGear(context, new Point(w * 0.89, h * 0.18), 70, t * 0.25, Leaf);
                DrawGear(context, new Point(w * 0.80, h * 0.32), 36, -t * 0.4, Green);
                DrawLeaf(context, new Point(w * 0.14, h * 0.72), 42, -0.5, Leaf);
                break;
            case "children":
                DrawBalloon(context, new Point(w * 0.90, h * 0.18), 34, -0.08, Sky, Paper);
                DrawBalloon(context, new Point(w * 0.83, h * 0.14), 26, 0.12, Pink, Paper);
                DrawBalloon(context, new Point(w * 0.14, h * 0.20), 28, 0.06, Gold, Paper);
                DrawStar(context, new Point(w * 0.78, h * 0.34), 18, 0.2, Sky);
                DrawStar(context, new Point(w * 0.20, h * 0.78), 16, 0, Pink);
                break;
            case "dragonboat":
                DrawZongzi(context, new Point(w * 0.88, h * 0.20), 62, 0.06);
                DrawZongzi(context, new Point(w * 0.76, h * 0.34), 36, -0.16);
                DrawWave(context, new Point(w * 0.58, h * 0.88), w * 0.48, t, Teal);
                DrawWave(context, new Point(w * 0.16, h * 0.92), w * 0.34, t + 1.1, Leaf);
                break;
            case "qixi":
                DrawMagpie(context, new Point(w * 0.16, h * 0.20), 54, 0.15, Violet);
                DrawHeart(context, new Point(w * 0.86, h * 0.20), 36, -0.1, Pink);
                DrawStar(context, new Point(w * 0.90, h * 0.14), 22, 0, Gold);
                DrawStar(context, new Point(w * 0.78, h * 0.30), 16, 0.3, Violet);
                DrawStar(context, new Point(w * 0.20, h * 0.78), 18, 0.1, Gold);
                break;
            case "midautumn":
                DrawFullMoon(context, new Point(w * 0.88, h * 0.18), 86);
                DrawRabbit(context, new Point(w * 0.80, h * 0.36), 34);
                DrawSeal(context, new Point(w * 0.14, h * 0.20), 52, "月", Gold, Paper);
                break;
            case "national":
                DrawFlag(context, new Point(w * 0.14, h * 0.20), 70);
                DrawStar(context, new Point(w * 0.88, h * 0.16), 36, 0, Gold);
                DrawStar(context, new Point(w * 0.80, h * 0.28), 16, 0.2, Gold);
                DrawStar(context, new Point(w * 0.92, h * 0.30), 14, -0.1, Gold);
                break;
            case "halloween":
                DrawCrescent(context, new Point(w * 0.90, h * 0.16), 64, Paper);
                DrawPumpkin(context, new Point(w * 0.80, h * 0.34), 58);
                DrawPumpkin(context, new Point(w * 0.14, h * 0.74), 40);
                DrawBat(context, new Point(w * 0.18, h * 0.20), 36, 0.15, Ink);
                DrawBat(context, new Point(w * 0.72, h * 0.18), 24, -0.2, Ink);
                break;
            case "christmas":
                DrawTree(context, new Point(w * 0.89, h * 0.22), 92);
                DrawTree(context, new Point(w * 0.14, h * 0.24), 52);
                DrawSnowflake(context, new Point(w * 0.78, h * 0.12), 18, Paper);
                DrawSnowflake(context, new Point(w * 0.22, h * 0.70), 16, Paper);
                DrawSnowflake(context, Fall(new Point(w * 0.70, h * 0.40), t, h, 16), 14, Paper);
                DrawSnowflake(context, Fall(new Point(w * 0.30, h * 0.20), t, h, 14), 12, Paper);
                DrawSnowflake(context, Fall(new Point(w * 0.84, h * 0.55), t, h, 18), 13, Paper);
                break;
        }
    }

    private static readonly IBrush Red = Solid(0xE8, 0xDC, 0x26, 0x26);
    private static readonly IBrush Rose = Solid(0xE0, 0xE1, 0x1D, 0x48);
    private static readonly IBrush Pink = Solid(0xD8, 0xFB, 0x71, 0x85);
    private static readonly IBrush Orange = Solid(0xE6, 0xEA, 0x58, 0x0C);
    private static readonly IBrush Gold = Solid(0xE8, 0xEA, 0xB3, 0x08);
    private static readonly IBrush Green = Solid(0xD8, 0x22, 0xC5, 0x5E);
    private static readonly IBrush Leaf = Solid(0xD4, 0x16, 0xA3, 0x4A);
    private static readonly IBrush Teal = Solid(0xD4, 0x14, 0xB8, 0xA6);
    private static readonly IBrush Sky = Solid(0xD8, 0x38, 0xBD, 0xF8);
    private static readonly IBrush Violet = Solid(0xD8, 0xC0, 0x84, 0xFC);
    private static readonly IBrush Paper = Solid(0xD0, 0xFF, 0xF7, 0xED);
    private static readonly IBrush Ink = Solid(0xC8, 0x1A, 0x1A, 0x1A);
    private static readonly IBrush Stem = Solid(0xD0, 0x3F, 0x62, 0x1C);
    private static readonly IBrush Face = Solid(0xE0, 0x1C, 0x19, 0x10);

    private static SolidColorBrush Solid(byte a, byte r, byte g, byte b) =>
        new(Color.FromArgb(ThemeManager.IsLight ? (byte)Math.Min(255, a + 20) : a, r, g, b));

    private static Point Fall(Point p, double t, double h, double speed)
    {
        var y = (p.Y + t * speed) % Math.Max(40, h);
        return new Point(p.X + Math.Sin(t * 0.8 + p.Y * 0.03) * 8, y);
    }

    private static void DrawSeal(DrawingContext context, Point c, double size, string text, IBrush plate, IBrush ink)
    {
        using (context.PushTransform(Rotate(c, -0.12)))
        {
            var pad = size * 0.08;
            context.DrawRectangle(plate, new Pen(ink, 2),
                new Rect(c.X - size * 0.5, c.Y - size * 0.5, size, size), 8, 8);
            context.DrawRectangle(null, new Pen(ink, 1.2),
                new Rect(c.X - size * 0.5 + pad, c.Y - size * 0.5 + pad, size - pad * 2, size - pad * 2), 5, 5);
            var label = Glyph(text, size * (text.Length > 1 ? 0.28 : 0.48), ink);
            context.DrawText(label, new Point(c.X - label.Width / 2, c.Y - label.Height / 2));
        }
    }

    private static FormattedText Glyph(string text, double size, IBrush brush) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(SealFont, FontStyle.Normal, FontWeight.Bold), size, brush);

    private static void DrawFirework(DrawingContext context, Point c, double size, double t, IBrush burst, IBrush spark)
    {
        var open = 0.62 + 0.38 * ((Math.Sin(t) + 1) * 0.5);
        var pen = new Pen(burst, 2.2, lineCap: PenLineCap.Round);
        for (var i = 0; i < 12; i++)
        {
            var a = i * Math.PI / 6 + t * 0.12;
            var d = Polar(a, size * open);
            context.DrawLine(pen, c + d * 0.12, c + d);
            context.DrawEllipse(spark, null, c + d, 3.2, 3.2);
        }
        context.DrawEllipse(burst, null, c, 4.5, 4.5);
    }

    private static void DrawLantern(DrawingContext context, Point top, double size, double sway, IBrush body, IBrush trim)
    {
        var mid = new Point(top.X + sway * size * 5, top.Y + size * 1.25);
        context.DrawLine(new Pen(trim, 1.6), new Point(top.X, 0), mid + new Vector(0, -size * 0.62));
        context.DrawEllipse(body, null, mid, size * 0.48, size * 0.62);
        context.DrawEllipse(Solid(0x66, 0xFF, 0xF1, 0xC4), null, mid, size * 0.22, size * 0.30);
        context.DrawRectangle(trim, null, new Rect(mid.X - size * 0.32, mid.Y - size * 0.70, size * 0.64, size * 0.14), 3, 3);
        context.DrawRectangle(trim, null, new Rect(mid.X - size * 0.26, mid.Y + size * 0.54, size * 0.52, size * 0.10), 3, 3);
        context.DrawLine(new Pen(trim, 1.6), mid + new Vector(0, size * 0.64), mid + new Vector(0, size * 1.05));
        context.DrawEllipse(trim, null, mid + new Vector(0, size * 1.12), 3.2, 3.2);
        context.DrawEllipse(body, null, mid + new Vector(0, size * 1.28), 2.2, 4.5);
    }

    private static void DrawEnvelope(DrawingContext context, Point c, double size, double angle, IBrush body, IBrush flap)
    {
        using (context.PushTransform(Rotate(c, angle)))
        {
            var r = new Rect(c.X - size * 0.55, c.Y - size * 0.34, size * 1.1, size * 0.72);
            context.DrawRectangle(body, null, r, 5, 5);
            var g = new StreamGeometry();
            using (var geo = g.Open())
            {
                geo.BeginFigure(new Point(r.X, r.Y), true);
                geo.LineTo(new Point(r.Center.X, r.Y + r.Height * 0.52));
                geo.LineTo(new Point(r.Right, r.Y));
                geo.EndFigure(true);
            }
            context.DrawGeometry(flap, null, g);
            var fu = Glyph("福", size * 0.28, flap);
            context.DrawText(fu, new Point(c.X - fu.Width / 2, c.Y - fu.Height * 0.15));
        }
    }

    private static void DrawHeart(DrawingContext context, Point c, double size, double angle, IBrush brush)
    {
        using (context.PushTransform(Rotate(c, angle)))
        {
            var g = new StreamGeometry();
            using (var geo = g.Open())
            {
                geo.BeginFigure(new Point(c.X, c.Y + size * 0.44), true);
                geo.CubicBezierTo(new Point(c.X - size * 0.68, c.Y + size * 0.02),
                    new Point(c.X - size * 0.40, c.Y - size * 0.52), new Point(c.X, c.Y - size * 0.10));
                geo.CubicBezierTo(new Point(c.X + size * 0.40, c.Y - size * 0.52),
                    new Point(c.X + size * 0.68, c.Y + size * 0.02), new Point(c.X, c.Y + size * 0.44));
                geo.EndFigure(true);
            }
            context.DrawGeometry(brush, null, g);
            context.DrawEllipse(Paper, null, c + new Vector(-size * 0.16, -size * 0.06), size * 0.08, size * 0.05);
        }
    }

    private static void DrawFlower(DrawingContext context, Point c, double size, double angle, IBrush petal, IBrush center)
    {
        for (var i = 0; i < 5; i++)
        {
            var p = c + Polar(angle + i * Math.PI * 2 / 5, size * 0.34);
            using (context.PushTransform(Rotate(p, angle + i)))
                context.DrawEllipse(petal, null, p, size * 0.22, size * 0.38);
        }
        context.DrawEllipse(center, null, c, size * 0.16, size * 0.16);
    }

    private static void DrawJester(DrawingContext context, Point c, double size, double angle, IBrush left, IBrush mid, IBrush right)
    {
        using (context.PushTransform(Rotate(c, angle)))
        {
            DrawHatPoint(context, c, size, -0.55, -0.05, left);
            DrawHatPoint(context, c, size, 0, -0.62, mid);
            DrawHatPoint(context, c, size, 0.55, -0.05, right);
            context.DrawEllipse(Gold, null, c + new Vector(0, size * 0.18), size * 0.42, size * 0.10);
        }
    }

    private static void DrawHatPoint(DrawingContext context, Point c, double size, double dx, double dy, IBrush brush)
    {
        var tip = c + new Vector(size * dx, size * dy);
        var g = new StreamGeometry();
        using (var geo = g.Open())
        {
            geo.BeginFigure(c + new Vector(-size * 0.12, size * 0.16), true);
            geo.LineTo(tip);
            geo.LineTo(c + new Vector(size * 0.12, size * 0.16));
            geo.EndFigure(true);
        }
        context.DrawGeometry(brush, null, g);
        context.DrawEllipse(Gold, null, tip, 4.2, 4.2);
    }

    private static void DrawQuestion(DrawingContext context, Point c, double size, double angle, IBrush brush)
    {
        using (context.PushTransform(Rotate(c, angle)))
        {
            var pen = new Pen(brush, Math.Max(2, size * 0.16), lineCap: PenLineCap.Round);
            var g = new StreamGeometry();
            using (var geo = g.Open())
            {
                geo.BeginFigure(new Point(c.X - size * 0.18, c.Y - size * 0.16), false);
                geo.CubicBezierTo(new Point(c.X - size * 0.18, c.Y - size * 0.56),
                    new Point(c.X + size * 0.34, c.Y - size * 0.56), new Point(c.X + size * 0.16, c.Y - size * 0.10));
                geo.CubicBezierTo(new Point(c.X + size * 0.06, c.Y + size * 0.06),
                    new Point(c.X, c.Y + size * 0.04), new Point(c.X, c.Y + size * 0.18));
            }
            context.DrawGeometry(null, pen, g);
            context.DrawEllipse(brush, null, new Point(c.X, c.Y + size * 0.40), 2.6, 2.6);
        }
    }

    private static void DrawGear(DrawingContext context, Point c, double size, double angle, IBrush brush)
    {
        using (context.PushTransform(Rotate(c, angle)))
        {
            var g = new StreamGeometry();
            using (var geo = g.Open())
            {
                const int teeth = 8;
                for (var i = 0; i < teeth; i++)
                {
                    var a = i * Math.PI * 2 / teeth;
                    var pts = new[]
                    {
                        c + Polar(a - 0.16, size * 0.34),
                        c + Polar(a - 0.08, size * 0.50),
                        c + Polar(a + 0.08, size * 0.50),
                        c + Polar(a + 0.16, size * 0.34),
                    };
                    if (i == 0)
                        geo.BeginFigure(pts[0], true);
                    else
                        geo.LineTo(pts[0]);
                    geo.LineTo(pts[1]);
                    geo.LineTo(pts[2]);
                    geo.LineTo(pts[3]);
                }
                geo.EndFigure(true);
            }
            context.DrawGeometry(brush, null, g);
            context.DrawEllipse(null, new Pen(brush, 3), c, size * 0.14, size * 0.14);
        }
    }

    private static void DrawLeaf(DrawingContext context, Point c, double size, double angle, IBrush brush)
    {
        using (context.PushTransform(Rotate(c, angle)))
        {
            var g = new StreamGeometry();
            using (var geo = g.Open())
            {
                geo.BeginFigure(new Point(c.X, c.Y - size * 0.5), true);
                geo.CubicBezierTo(new Point(c.X + size * 0.48, c.Y - size * 0.08),
                    new Point(c.X + size * 0.22, c.Y + size * 0.38), new Point(c.X, c.Y + size * 0.5));
                geo.CubicBezierTo(new Point(c.X - size * 0.22, c.Y + size * 0.38),
                    new Point(c.X - size * 0.48, c.Y - size * 0.08), new Point(c.X, c.Y - size * 0.5));
                geo.EndFigure(true);
            }
            context.DrawGeometry(brush, null, g);
            context.DrawLine(new Pen(Stem, 1.4), new Point(c.X, c.Y - size * 0.38), new Point(c.X, c.Y + size * 0.46));
        }
    }

    private static void DrawBalloon(DrawingContext context, Point c, double size, double angle, IBrush body, IBrush highlight)
    {
        using (context.PushTransform(Rotate(c, angle)))
        {
            context.DrawEllipse(body, null, c, size * 0.58, size * 0.72);
            context.DrawEllipse(highlight, null, c + new Vector(-size * 0.16, -size * 0.20), size * 0.12, size * 0.16);
            var knot = new StreamGeometry();
            using (var geo = knot.Open())
            {
                geo.BeginFigure(new Point(c.X - size * 0.12, c.Y + size * 0.70), true);
                geo.LineTo(new Point(c.X, c.Y + size * 0.54));
                geo.LineTo(new Point(c.X + size * 0.12, c.Y + size * 0.70));
                geo.EndFigure(true);
            }
            context.DrawGeometry(body, null, knot);
            context.DrawLine(new Pen(body, 1.4), new Point(c.X, c.Y + size * 0.70), new Point(c.X + size * 0.1, c.Y + size * 1.45));
        }
    }

    private static void DrawStar(DrawingContext context, Point c, double size, double angle, IBrush brush)
    {
        using (context.PushTransform(Rotate(c, angle)))
        {
            var g = new StreamGeometry();
            using (var geo = g.Open())
            {
                for (var i = 0; i < 5; i++)
                {
                    var outer = c + Polar(-Math.PI / 2 + i * Math.PI * 2 / 5, size * 0.5);
                    var inner = c + Polar(-Math.PI / 2 + (i + 0.5) * Math.PI * 2 / 5, size * 0.20);
                    if (i == 0)
                        geo.BeginFigure(outer, true);
                    else
                        geo.LineTo(outer);
                    geo.LineTo(inner);
                }
                geo.EndFigure(true);
            }
            context.DrawGeometry(brush, null, g);
        }
    }

    private static void DrawZongzi(DrawingContext context, Point c, double size, double angle)
    {
        using (context.PushTransform(Rotate(c, angle)))
        {
            var leaf = new StreamGeometry();
            using (var geo = leaf.Open())
            {
                geo.BeginFigure(new Point(c.X, c.Y - size * 0.55), true);
                geo.LineTo(new Point(c.X + size * 0.46, c.Y + size * 0.44));
                geo.LineTo(new Point(c.X - size * 0.46, c.Y + size * 0.44));
                geo.EndFigure(true);
            }
            context.DrawGeometry(Leaf, null, leaf);
            var wrap = new StreamGeometry();
            using (var geo = wrap.Open())
            {
                geo.BeginFigure(new Point(c.X, c.Y - size * 0.38), true);
                geo.LineTo(new Point(c.X + size * 0.28, c.Y + size * 0.30));
                geo.LineTo(new Point(c.X - size * 0.28, c.Y + size * 0.30));
                geo.EndFigure(true);
            }
            context.DrawGeometry(Green, null, wrap);
            context.DrawLine(new Pen(Gold, 2), new Point(c.X - size * 0.10, c.Y - size * 0.08), new Point(c.X + size * 0.16, c.Y + size * 0.24));
            context.DrawLine(new Pen(Gold, 2), new Point(c.X + size * 0.10, c.Y - size * 0.08), new Point(c.X - size * 0.16, c.Y + size * 0.24));
        }
    }

    private static void DrawWave(DrawingContext context, Point start, double width, double t, IBrush brush)
    {
        var g = new StreamGeometry();
        using (var geo = g.Open())
        {
            geo.BeginFigure(start, false);
            var steps = 10;
            for (var i = 1; i <= steps; i++)
            {
                var x = start.X + width * i / steps;
                var y = start.Y + Math.Sin(t * 1.3 + i * 0.85) * 8;
                geo.LineTo(new Point(x, y));
            }
        }
        context.DrawGeometry(null, new Pen(brush, 2.4, lineCap: PenLineCap.Round), g);
    }

    private static void DrawMagpie(DrawingContext context, Point c, double size, double angle, IBrush brush)
    {
        using (context.PushTransform(Rotate(c, angle)))
        {
            var wing = new StreamGeometry();
            using (var geo = wing.Open())
            {
                geo.BeginFigure(new Point(c.X - size * 0.48, c.Y + size * 0.10), true);
                geo.CubicBezierTo(new Point(c.X - size * 0.08, c.Y - size * 0.46),
                    new Point(c.X + size * 0.22, c.Y - size * 0.08), new Point(c.X + size * 0.50, c.Y - size * 0.24));
                geo.CubicBezierTo(new Point(c.X + size * 0.16, c.Y + size * 0.10),
                    new Point(c.X - size * 0.06, c.Y + size * 0.24), new Point(c.X - size * 0.48, c.Y + size * 0.10));
                geo.EndFigure(true);
            }
            context.DrawGeometry(brush, null, wing);
            context.DrawEllipse(Ink, null, new Point(c.X + size * 0.42, c.Y - size * 0.18), 3.4, 3.4);
            context.DrawEllipse(Paper, null, new Point(c.X + size * 0.38, c.Y - size * 0.08), 4.5, 3.2);
        }
    }

    private static void DrawFullMoon(DrawingContext context, Point c, double size)
    {
        context.DrawEllipse(Solid(0x40, 0xEA, 0xB3, 0x08), null, c, size * 0.62, size * 0.62);
        context.DrawEllipse(Paper, null, c, size * 0.48, size * 0.48);
        context.DrawEllipse(Solid(0x50, 0xD6, 0xC4, 0x9A), null, c + new Vector(-size * 0.12, -size * 0.06), size * 0.10, size * 0.08);
        context.DrawEllipse(Solid(0x40, 0xD6, 0xC4, 0x9A), null, c + new Vector(size * 0.14, size * 0.10), size * 0.07, size * 0.06);
    }

    private static void DrawCrescent(DrawingContext context, Point c, double size, IBrush brush)
    {
        var moon = new EllipseGeometry(new Rect(c.X - size * 0.5, c.Y - size * 0.5, size, size));
        var cut = new EllipseGeometry(new Rect(c.X - size * 0.18, c.Y - size * 0.58, size * 0.92, size * 0.92));
        context.DrawGeometry(brush, null, new CombinedGeometry(GeometryCombineMode.Exclude, moon, cut));
    }

    private static void DrawRabbit(DrawingContext context, Point c, double size)
    {
        context.DrawEllipse(Paper, null, new Point(c.X - size * 0.16, c.Y - size * 0.46), size * 0.10, size * 0.30);
        context.DrawEllipse(Paper, null, new Point(c.X + size * 0.10, c.Y - size * 0.44), size * 0.09, size * 0.26);
        context.DrawEllipse(Pink, null, new Point(c.X - size * 0.16, c.Y - size * 0.44), size * 0.04, size * 0.16);
        context.DrawEllipse(Paper, null, c, size * 0.30, size * 0.28);
        context.DrawEllipse(Paper, null, new Point(c.X + size * 0.06, c.Y + size * 0.34), size * 0.24, size * 0.22);
        context.DrawEllipse(Ink, null, new Point(c.X - size * 0.08, c.Y - size * 0.04), 2.2, 2.2);
        context.DrawEllipse(Ink, null, new Point(c.X + size * 0.10, c.Y - size * 0.04), 2.2, 2.2);
    }

    private static void DrawFlag(DrawingContext context, Point c, double size)
    {
        context.DrawLine(new Pen(Gold, 2.4), new Point(c.X - size * 0.48, c.Y - size * 0.55), new Point(c.X - size * 0.48, c.Y + size * 0.55));
        context.DrawRectangle(Red, null, new Rect(c.X - size * 0.46, c.Y - size * 0.52, size * 0.96, size * 0.62), 3, 3);
        DrawStar(context, new Point(c.X - size * 0.18, c.Y - size * 0.28), size * 0.22, 0, Gold);
        DrawStar(context, new Point(c.X + size * 0.02, c.Y - size * 0.42), size * 0.10, 0.3, Gold);
        DrawStar(context, new Point(c.X + size * 0.12, c.Y - size * 0.30), size * 0.09, 0.1, Gold);
        DrawStar(context, new Point(c.X + size * 0.12, c.Y - size * 0.16), size * 0.09, -0.1, Gold);
        DrawStar(context, new Point(c.X + size * 0.02, c.Y - size * 0.06), size * 0.10, -0.3, Gold);
    }

    private static void DrawPumpkin(DrawingContext context, Point c, double size)
    {
        context.DrawRectangle(Stem, null, new Rect(c.X - size * 0.07, c.Y - size * 0.58, size * 0.14, size * 0.20), 3, 3);
        context.DrawEllipse(Orange, null, c + new Vector(-size * 0.22, 0), size * 0.30, size * 0.40);
        context.DrawEllipse(Orange, null, c + new Vector(size * 0.22, 0), size * 0.30, size * 0.40);
        context.DrawEllipse(Orange, null, c, size * 0.42, size * 0.44);
        var eye = new StreamGeometry();
        using (var geo = eye.Open())
        {
            geo.BeginFigure(c + new Vector(-size * 0.18, -size * 0.04), true);
            geo.LineTo(c + new Vector(-size * 0.08, -size * 0.16));
            geo.LineTo(c + new Vector(-size * 0.04, -size * 0.02));
            geo.EndFigure(true);
        }
        context.DrawGeometry(Face, null, eye);
        var eye2 = new StreamGeometry();
        using (var geo = eye2.Open())
        {
            geo.BeginFigure(c + new Vector(size * 0.18, -size * 0.04), true);
            geo.LineTo(c + new Vector(size * 0.08, -size * 0.16));
            geo.LineTo(c + new Vector(size * 0.04, -size * 0.02));
            geo.EndFigure(true);
        }
        context.DrawGeometry(Face, null, eye2);
        var mouth = new StreamGeometry();
        using (var geo = mouth.Open())
        {
            geo.BeginFigure(c + new Vector(-size * 0.18, size * 0.14), true);
            geo.LineTo(c + new Vector(-size * 0.08, size * 0.22));
            geo.LineTo(c + new Vector(0, size * 0.14));
            geo.LineTo(c + new Vector(size * 0.08, size * 0.22));
            geo.LineTo(c + new Vector(size * 0.18, size * 0.14));
            geo.LineTo(c + new Vector(size * 0.10, size * 0.26));
            geo.LineTo(c + new Vector(0, size * 0.20));
            geo.LineTo(c + new Vector(-size * 0.10, size * 0.26));
            geo.EndFigure(true);
        }
        context.DrawGeometry(Face, null, mouth);
    }

    private static void DrawBat(DrawingContext context, Point c, double size, double angle, IBrush brush)
    {
        using (context.PushTransform(Rotate(c, angle)))
        {
            var g = new StreamGeometry();
            using (var geo = g.Open())
            {
                geo.BeginFigure(new Point(c.X, c.Y), true);
                geo.CubicBezierTo(new Point(c.X - size * 0.2, c.Y - size * 0.28),
                    new Point(c.X - size * 0.55, c.Y - size * 0.04), new Point(c.X - size * 0.78, c.Y + size * 0.14));
                geo.LineTo(new Point(c.X - size * 0.38, c.Y + size * 0.02));
                geo.LineTo(new Point(c.X, c.Y + size * 0.14));
                geo.LineTo(new Point(c.X + size * 0.38, c.Y + size * 0.02));
                geo.LineTo(new Point(c.X + size * 0.78, c.Y + size * 0.14));
                geo.CubicBezierTo(new Point(c.X + size * 0.55, c.Y - size * 0.04),
                    new Point(c.X + size * 0.2, c.Y - size * 0.28), new Point(c.X, c.Y));
                geo.EndFigure(true);
            }
            context.DrawGeometry(brush, null, g);
            context.DrawEllipse(brush, null, c + new Vector(0, -size * 0.04), size * 0.10, size * 0.10);
        }
    }

    private static void DrawTree(DrawingContext context, Point c, double size)
    {
        var layers = new StreamGeometry();
        using (var geo = layers.Open())
        {
            geo.BeginFigure(new Point(c.X, c.Y - size * 0.58), true);
            geo.LineTo(new Point(c.X + size * 0.22, c.Y - size * 0.20));
            geo.LineTo(new Point(c.X + size * 0.12, c.Y - size * 0.20));
            geo.LineTo(new Point(c.X + size * 0.34, c.Y + size * 0.10));
            geo.LineTo(new Point(c.X + size * 0.16, c.Y + size * 0.10));
            geo.LineTo(new Point(c.X + size * 0.44, c.Y + size * 0.42));
            geo.LineTo(new Point(c.X - size * 0.44, c.Y + size * 0.42));
            geo.LineTo(new Point(c.X - size * 0.16, c.Y + size * 0.10));
            geo.LineTo(new Point(c.X - size * 0.34, c.Y + size * 0.10));
            geo.LineTo(new Point(c.X - size * 0.12, c.Y - size * 0.20));
            geo.LineTo(new Point(c.X - size * 0.22, c.Y - size * 0.20));
            geo.EndFigure(true);
        }
        context.DrawGeometry(Green, null, layers);
        context.DrawRectangle(Stem, null, new Rect(c.X - size * 0.08, c.Y + size * 0.42, size * 0.16, size * 0.18), 2, 2);
        DrawStar(context, c + new Vector(0, -size * 0.62), size * 0.16, 0, Gold);
        context.DrawEllipse(Red, null, c + new Vector(-size * 0.14, 0.06 * size), 4.2, 4.2);
        context.DrawEllipse(Gold, null, c + new Vector(size * 0.16, 0.16 * size), 3.8, 3.8);
        context.DrawEllipse(Sky, null, c + new Vector(0.02 * size, -0.16 * size), 3.6, 3.6);
        context.DrawEllipse(Rose, null, c + new Vector(-size * 0.18, 0.28 * size), 3.4, 3.4);
    }

    private static void DrawSnowflake(DrawingContext context, Point c, double size, IBrush brush)
    {
        var pen = new Pen(brush, Math.Max(1.4, size * 0.14), lineCap: PenLineCap.Round);
        for (var i = 0; i < 6; i++)
        {
            var a = i * Math.PI / 3;
            var d = Polar(a, size * 0.5);
            context.DrawLine(pen, c - d, c + d);
            var arm = c + d * 0.55;
            var side = Polar(a + Math.PI / 2, size * 0.18);
            context.DrawLine(pen, arm - side, arm + d * 0.22);
            context.DrawLine(pen, arm + side, arm + d * 0.22);
        }
    }

    private static Vector Polar(double angle, double radius) =>
        new(Math.Cos(angle) * radius, Math.Sin(angle) * radius);

    private static Matrix Rotate(Point c, double angle) =>
        Matrix.CreateTranslation(-c.X, -c.Y) * Matrix.CreateRotation(angle) * Matrix.CreateTranslation(c.X, c.Y);
}
