using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using MaoX.Core;

namespace MaoX.Controls;

/// <summary>节日装饰：在窗口边角和空白处画每个节日自己的图案，不挡点击。</summary>
public class HolidayDecor : Control
{
    /// <summary>冒烟时固定在同一帧，避免截图闪。</summary>
    public static bool Frozen { get; set; }

    private readonly DispatcherTimer _timer;
    private string _key;
    private double _phase;

    public HolidayDecor()
    {
        IsHitTestVisible = false;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Render, (_, _) =>
        {
            if (Holiday.Active == null)
                return;
            _phase += 0.04;
            InvalidateVisual();
        });
        _timer.Stop();
    }

    public string Motif => Holiday.Active?.Key;

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
        var key = Holiday.Active?.Key;
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
        var day = Holiday.Active;
        if (day == null || Bounds.Width < 8 || Bounds.Height < 8)
            return;
        var t = Frozen ? 1.2 : _phase;
        var accent = new SolidColorBrush(ThemeManager.Accent);
        var ink = Brush(ThemeManager.Accent, ThemeManager.IsLight ? 0.42 : 0.55);
        var soft = Brush(ThemeManager.Accent, ThemeManager.IsLight ? 0.18 : 0.22);
        var paper = Brush(Colors.White, ThemeManager.IsLight ? 0.35 : 0.16);
        var w = Bounds.Width;
        var h = Bounds.Height;
        switch (day.Key)
        {
            case "newyear":
                DrawFirework(context, new Point(w * 0.86, h * 0.16), 78, t, accent, ink);
                DrawFirework(context, new Point(w * 0.72, h * 0.28), 46, t + 0.8, soft, ink);
                DrawFirework(context, new Point(w * 0.18, h * 0.22), 36, t + 1.4, ink, paper);
                Scatter(context, w, h, 14, 11, t, (ctx, p, s, a) => DrawSpark(ctx, p, s, a, ink));
                break;
            case "spring":
                DrawLantern(context, new Point(w * 0.90, 36), 34, 0.12, ink, accent);
                DrawLantern(context, new Point(w * 0.82, 18), 24, -0.08, ink, paper);
                DrawLantern(context, new Point(w * 0.12, 28), 22, 0.06, ink, accent);
                DrawEnvelope(context, new Point(w * 0.88, h * 0.78), 42, 0.18, ink, paper);
                DrawEnvelope(context, new Point(w * 0.14, h * 0.72), 28, -0.22, soft, paper);
                Scatter(context, w, h, 18, 21, t, (ctx, p, s, a) => DrawPetal(ctx, p, s, a + t * 0.4, ink));
                break;
            case "lantern":
                DrawLantern(context, new Point(w * 0.88, 40), 40, 0.08, ink, accent);
                DrawLantern(context, new Point(w * 0.78, 22), 26, -0.14, ink, paper);
                DrawLantern(context, new Point(w * 0.16, 30), 28, 0.1, ink, accent);
                DrawMoon(context, new Point(w * 0.90, h * 0.22), 54, paper, soft);
                Scatter(context, w, h, 12, 31, t, (ctx, p, s, a) => DrawSpark(ctx, p, s * 0.7, a, ink));
                break;
            case "valentine":
                DrawHeart(context, new Point(w * 0.88, h * 0.18), 56, -0.12, ink);
                DrawHeart(context, new Point(w * 0.78, h * 0.32), 28, 0.2, soft);
                DrawHeart(context, new Point(w * 0.14, h * 0.24), 30, 0.16, ink);
                Scatter(context, w, h, 16, 41, t, (ctx, p, s, a) => DrawHeart(ctx, Drift(p, t, 10), s, a, ink));
                break;
            case "womensday":
                DrawFlower(context, new Point(w * 0.88, h * 0.18), 48, t * 0.15, ink, paper);
                DrawFlower(context, new Point(w * 0.76, h * 0.30), 26, -t * 0.1, soft, paper);
                DrawFlower(context, new Point(w * 0.14, h * 0.22), 30, t * 0.12, ink, paper);
                Scatter(context, w, h, 14, 51, t, (ctx, p, s, a) => DrawPetal(ctx, p, s, a, ink));
                break;
            case "aprilfools":
                DrawJester(context, new Point(w * 0.88, h * 0.16), 52, 0.15, ink, paper);
                DrawJester(context, new Point(w * 0.14, h * 0.22), 34, -0.2, soft, paper);
                Scatter(context, w, h, 12, 61, t, (ctx, p, s, a) => DrawQuestion(ctx, p, s, a + Math.Sin(t) * 0.2, ink));
                break;
            case "labor":
                DrawGear(context, new Point(w * 0.90, h * 0.18), 58, t * 0.35, ink);
                DrawGear(context, new Point(w * 0.82, h * 0.30), 30, -t * 0.5, soft);
                DrawLeaf(context, new Point(w * 0.14, h * 0.70), 36, -0.4, ink);
                Scatter(context, w, h, 10, 71, t, (ctx, p, s, a) => DrawLeaf(ctx, p, s, a, soft));
                break;
            case "children":
                DrawBalloon(context, new Point(w * 0.90, h * 0.20), 28, -0.08, ink, paper);
                DrawBalloon(context, new Point(w * 0.84, h * 0.16), 22, 0.12, soft, paper);
                DrawBalloon(context, new Point(w * 0.14, h * 0.22), 24, 0.06, ink, paper);
                Scatter(context, w, h, 14, 81, t, (ctx, p, s, a) => DrawStar(ctx, Drift(p, t, 8), s, a, ink));
                break;
            case "dragonboat":
                DrawZongzi(context, new Point(w * 0.88, h * 0.20), 48, 0.08, ink, paper);
                DrawZongzi(context, new Point(w * 0.78, h * 0.32), 28, -0.18, soft, paper);
                DrawWave(context, new Point(w * 0.70, h * 0.86), w * 0.42, t, ink);
                DrawWave(context, new Point(w * 0.20, h * 0.90), w * 0.30, t + 1, soft);
                Scatter(context, w, h, 8, 91, t, (ctx, p, s, a) => DrawLeaf(ctx, p, s, a, ink));
                break;
            case "qixi":
                DrawStar(context, new Point(w * 0.88, h * 0.16), 36, 0, ink);
                DrawHeart(context, new Point(w * 0.80, h * 0.28), 24, 0.1, soft);
                DrawMagpie(context, new Point(w * 0.18, h * 0.22), 40, 0.2, ink);
                Scatter(context, w, h, 16, 101, t, (ctx, p, s, a) => DrawStar(ctx, p, s, a + t * 0.3, ink));
                break;
            case "midautumn":
                DrawMoon(context, new Point(w * 0.88, h * 0.18), 72, paper, ink);
                DrawRabbit(context, new Point(w * 0.80, h * 0.34), 28, ink);
                DrawCloud(context, new Point(w * 0.16, h * 0.20), 48, soft);
                Scatter(context, w, h, 10, 111, t, (ctx, p, s, a) => DrawPetal(ctx, Drift(p, t, 6), s, a, ink));
                break;
            case "national":
                DrawStar(context, new Point(w * 0.88, h * 0.16), 40, 0, ink);
                DrawStar(context, new Point(w * 0.80, h * 0.26), 18, 0.2, soft);
                DrawStar(context, new Point(w * 0.92, h * 0.30), 16, -0.1, soft);
                DrawFlag(context, new Point(w * 0.14, h * 0.20), 46, ink, paper);
                Scatter(context, w, h, 12, 121, t, (ctx, p, s, a) => DrawStar(ctx, p, s, a, ink));
                break;
            case "halloween":
                DrawMoon(context, new Point(w * 0.90, h * 0.16), 60, paper, ink);
                DrawPumpkin(context, new Point(w * 0.80, h * 0.30), 40, ink, paper);
                DrawPumpkin(context, new Point(w * 0.14, h * 0.72), 32, soft, paper);
                Scatter(context, w, h, 12, 131, t, (ctx, p, s, a) => DrawBat(ctx, Drift(p, t, 16), s, a, ink));
                break;
            case "christmas":
                DrawTree(context, new Point(w * 0.90, h * 0.22), 70, ink, paper);
                DrawTree(context, new Point(w * 0.14, h * 0.24), 40, soft, paper);
                Scatter(context, w, h, 22, 141, t, (ctx, p, s, _) => DrawSnowflake(ctx, Fall(p, t, h, 18), s, ink));
                break;
        }
    }

    private static void Scatter(DrawingContext context, double w, double h, int count, int seed, double t,
                                Action<DrawingContext, Point, double, double> draw)
    {
        var rng = new Random(seed);
        for (var i = 0; i < count; i++)
        {
            var x = 24 + rng.NextDouble() * Math.Max(8, w - 48);
            var y = 20 + rng.NextDouble() * Math.Max(8, h - 40);
            // 躲开中间正文，多落在边和角。
            if (x > w * 0.22 && x < w * 0.78 && y > h * 0.18 && y < h * 0.78)
                continue;
            var size = 8 + rng.NextDouble() * 16;
            var angle = rng.NextDouble() * Math.PI * 2 + t * 0.08;
            draw(context, new Point(x, y), size, angle);
        }
    }

    private static Point Drift(Point p, double t, double amount) =>
        new(p.X + Math.Sin(t * 0.7 + p.Y * 0.02) * amount, p.Y + Math.Cos(t * 0.5 + p.X * 0.02) * amount * 0.4);

    private static Point Fall(Point p, double t, double h, double speed)
    {
        var y = (p.Y + t * speed) % Math.Max(40, h);
        var x = p.X + Math.Sin(t * 0.8 + p.Y * 0.03) * 8;
        return new Point(x, y);
    }

    private static IBrush Brush(Color color, double opacity) =>
        new SolidColorBrush(Color.FromArgb((byte)Math.Clamp((int)(opacity * 255), 0, 255), color.R, color.G, color.B));

    private static void DrawSpark(DrawingContext context, Point c, double size, double angle, IBrush brush)
    {
        var pen = new Pen(brush, Math.Max(1, size * 0.12));
        for (var i = 0; i < 4; i++)
        {
            var a = angle + i * Math.PI / 4;
            var d = new Vector(Math.Cos(a), Math.Sin(a)) * size * 0.5;
            context.DrawLine(pen, c - d, c + d);
        }
    }

    private static void DrawFirework(DrawingContext context, Point c, double size, double t, IBrush burst, IBrush spark)
    {
        var open = 0.55 + 0.45 * ((Math.Sin(t) + 1) * 0.5);
        var pen = new Pen(burst, 1.6);
        for (var i = 0; i < 10; i++)
        {
            var a = i * Math.PI / 5 + t * 0.15;
            var d = new Vector(Math.Cos(a), Math.Sin(a)) * size * open;
            context.DrawLine(pen, c, c + d);
            context.DrawEllipse(spark, null, c + d, 2.4, 2.4);
        }
        context.DrawEllipse(burst, null, c, 3.2, 3.2);
    }

    private static void DrawLantern(DrawingContext context, Point top, double size, double sway, IBrush body, IBrush glow)
    {
        var mid = new Point(top.X + sway * size * 4, top.Y + size * 1.15);
        context.DrawLine(new Pen(body, 1.4), new Point(top.X, 0), mid + new Vector(0, -size * 0.55));
        context.DrawEllipse(body, null, mid, size * 0.42, size * 0.55);
        context.DrawEllipse(glow, null, mid, size * 0.22, size * 0.30);
        context.DrawRectangle(body, null, new Rect(mid.X - size * 0.28, mid.Y - size * 0.62, size * 0.56, size * 0.12), 2, 2);
        context.DrawLine(new Pen(body, 1.2), mid + new Vector(0, size * 0.55), mid + new Vector(0, size * 0.9));
        context.DrawEllipse(body, null, mid + new Vector(0, size * 0.95), 2.2, 2.2);
    }

    private static void DrawEnvelope(DrawingContext context, Point c, double size, double angle, IBrush body, IBrush flap)
    {
        using (context.PushTransform(Rotate(c, angle)))
        {
            var r = new Rect(c.X - size * 0.5, c.Y - size * 0.32, size, size * 0.64);
            context.DrawRectangle(body, null, r, 4, 4);
            var g = new StreamGeometry();
            using (var geo = g.Open())
            {
                geo.BeginFigure(new Point(r.X, r.Y), true);
                geo.LineTo(new Point(r.Center.X, r.Y + r.Height * 0.55));
                geo.LineTo(new Point(r.Right, r.Y));
                geo.EndFigure(true);
            }
            context.DrawGeometry(flap, null, g);
        }
    }

    private static void DrawPetal(DrawingContext context, Point c, double size, double angle, IBrush brush)
    {
        using (context.PushTransform(Rotate(c, angle)))
            context.DrawEllipse(brush, null, c, size * 0.28, size * 0.55);
    }

    private static void DrawHeart(DrawingContext context, Point c, double size, double angle, IBrush brush)
    {
        using (context.PushTransform(Rotate(c, angle)))
        {
            var g = new StreamGeometry();
            using (var geo = g.Open())
            {
                geo.BeginFigure(new Point(c.X, c.Y + size * 0.42), true);
                geo.CubicBezierTo(new Point(c.X - size * 0.62, c.Y + size * 0.02),
                    new Point(c.X - size * 0.38, c.Y - size * 0.48), new Point(c.X, c.Y - size * 0.12));
                geo.CubicBezierTo(new Point(c.X + size * 0.38, c.Y - size * 0.48),
                    new Point(c.X + size * 0.62, c.Y + size * 0.02), new Point(c.X, c.Y + size * 0.42));
                geo.EndFigure(true);
            }
            context.DrawGeometry(brush, null, g);
        }
    }

    private static void DrawFlower(DrawingContext context, Point c, double size, double angle, IBrush petal, IBrush center)
    {
        for (var i = 0; i < 5; i++)
            DrawPetal(context, c + Polar(angle + i * Math.PI * 2 / 5, size * 0.32), size * 0.7, angle + i, petal);
        context.DrawEllipse(center, null, c, size * 0.16, size * 0.16);
    }

    private static void DrawJester(DrawingContext context, Point c, double size, double angle, IBrush hat, IBrush bell)
    {
        using (context.PushTransform(Rotate(c, angle)))
        {
            var g = new StreamGeometry();
            using (var geo = g.Open())
            {
                geo.BeginFigure(new Point(c.X - size * 0.48, c.Y + size * 0.18), true);
                geo.LineTo(new Point(c.X, c.Y - size * 0.48));
                geo.LineTo(new Point(c.X + size * 0.48, c.Y + size * 0.18));
                geo.LineTo(new Point(c.X, c.Y + size * 0.02));
                geo.EndFigure(true);
            }
            context.DrawGeometry(hat, null, g);
            context.DrawEllipse(bell, null, new Point(c.X, c.Y - size * 0.50), 3.4, 3.4);
            context.DrawEllipse(bell, null, new Point(c.X - size * 0.50, c.Y + size * 0.18), 3, 3);
            context.DrawEllipse(bell, null, new Point(c.X + size * 0.50, c.Y + size * 0.18), 3, 3);
        }
    }

    private static void DrawQuestion(DrawingContext context, Point c, double size, double angle, IBrush brush)
    {
        using (context.PushTransform(Rotate(c, angle)))
        {
            var pen = new Pen(brush, Math.Max(1.6, size * 0.16), lineCap: PenLineCap.Round);
            var g = new StreamGeometry();
            using (var geo = g.Open())
            {
                geo.BeginFigure(new Point(c.X - size * 0.18, c.Y - size * 0.18), false);
                geo.CubicBezierTo(new Point(c.X - size * 0.18, c.Y - size * 0.52),
                    new Point(c.X + size * 0.32, c.Y - size * 0.52), new Point(c.X + size * 0.18, c.Y - size * 0.12));
                geo.CubicBezierTo(new Point(c.X + size * 0.08, c.Y + size * 0.04),
                    new Point(c.X, c.Y + size * 0.02), new Point(c.X, c.Y + size * 0.16));
            }
            context.DrawGeometry(null, pen, g);
            context.DrawEllipse(brush, null, new Point(c.X, c.Y + size * 0.38), 2.2, 2.2);
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
                    var inner = Polar(a - 0.16, size * 0.34);
                    var outer = Polar(a - 0.08, size * 0.50);
                    var outer2 = Polar(a + 0.08, size * 0.50);
                    var inner2 = Polar(a + 0.16, size * 0.34);
                    if (i == 0)
                        geo.BeginFigure(c + inner, true);
                    else
                        geo.LineTo(c + inner);
                    geo.LineTo(c + outer);
                    geo.LineTo(c + outer2);
                    geo.LineTo(c + inner2);
                }
                geo.EndFigure(true);
            }
            context.DrawGeometry(brush, null, g);
            context.DrawEllipse(Brushes.Transparent, new Pen(brush, 1), c, size * 0.14, size * 0.14);
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
                geo.CubicBezierTo(new Point(c.X + size * 0.45, c.Y - size * 0.1),
                    new Point(c.X + size * 0.2, c.Y + size * 0.35), new Point(c.X, c.Y + size * 0.5));
                geo.CubicBezierTo(new Point(c.X - size * 0.2, c.Y + size * 0.35),
                    new Point(c.X - size * 0.45, c.Y - size * 0.1), new Point(c.X, c.Y - size * 0.5));
                geo.EndFigure(true);
            }
            context.DrawGeometry(brush, null, g);
            context.DrawLine(new Pen(brush, 1), new Point(c.X, c.Y - size * 0.4), new Point(c.X, c.Y + size * 0.48));
        }
    }

    private static void DrawBalloon(DrawingContext context, Point c, double size, double angle, IBrush body, IBrush highlight)
    {
        using (context.PushTransform(Rotate(c, angle)))
        {
            context.DrawEllipse(body, null, c, size * 0.55, size * 0.68);
            context.DrawEllipse(highlight, null, c + new Vector(-size * 0.16, -size * 0.18), size * 0.12, size * 0.16);
            var knot = new StreamGeometry();
            using (var geo = knot.Open())
            {
                geo.BeginFigure(new Point(c.X - size * 0.12, c.Y + size * 0.66), true);
                geo.LineTo(new Point(c.X, c.Y + size * 0.52));
                geo.LineTo(new Point(c.X + size * 0.12, c.Y + size * 0.66));
                geo.EndFigure(true);
            }
            context.DrawGeometry(body, null, knot);
            context.DrawLine(new Pen(body, 1.2), new Point(c.X, c.Y + size * 0.66), new Point(c.X + size * 0.08, c.Y + size * 1.35));
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

    private static void DrawZongzi(DrawingContext context, Point c, double size, double angle, IBrush leaf, IBrush wrap)
    {
        using (context.PushTransform(Rotate(c, angle)))
        {
            var g = new StreamGeometry();
            using (var geo = g.Open())
            {
                geo.BeginFigure(new Point(c.X, c.Y - size * 0.52), true);
                geo.LineTo(new Point(c.X + size * 0.42, c.Y + size * 0.42));
                geo.LineTo(new Point(c.X - size * 0.42, c.Y + size * 0.42));
                geo.EndFigure(true);
            }
            context.DrawGeometry(leaf, null, g);
            context.DrawLine(new Pen(wrap, 1.6), new Point(c.X - size * 0.12, c.Y - size * 0.1), new Point(c.X + size * 0.16, c.Y + size * 0.28));
            context.DrawLine(new Pen(wrap, 1.6), new Point(c.X + size * 0.12, c.Y - size * 0.1), new Point(c.X - size * 0.16, c.Y + size * 0.28));
        }
    }

    private static void DrawWave(DrawingContext context, Point start, double width, double t, IBrush brush)
    {
        var g = new StreamGeometry();
        using (var geo = g.Open())
        {
            geo.BeginFigure(start, false);
            var steps = 8;
            for (var i = 1; i <= steps; i++)
            {
                var x = start.X + width * i / steps;
                var y = start.Y + Math.Sin(t * 1.4 + i * 0.9) * 7;
                geo.LineTo(new Point(x, y));
            }
        }
        context.DrawGeometry(null, new Pen(brush, 2, lineCap: PenLineCap.Round), g);
    }

    private static void DrawMagpie(DrawingContext context, Point c, double size, double angle, IBrush brush)
    {
        using (context.PushTransform(Rotate(c, angle)))
        {
            var g = new StreamGeometry();
            using (var geo = g.Open())
            {
                geo.BeginFigure(new Point(c.X - size * 0.45, c.Y + size * 0.08), true);
                geo.CubicBezierTo(new Point(c.X - size * 0.1, c.Y - size * 0.42),
                    new Point(c.X + size * 0.2, c.Y - size * 0.1), new Point(c.X + size * 0.48, c.Y - size * 0.22));
                geo.CubicBezierTo(new Point(c.X + size * 0.18, c.Y + size * 0.08),
                    new Point(c.X - size * 0.05, c.Y + size * 0.22), new Point(c.X - size * 0.45, c.Y + size * 0.08));
                geo.EndFigure(true);
            }
            context.DrawGeometry(brush, null, g);
            context.DrawEllipse(brush, null, new Point(c.X + size * 0.42, c.Y - size * 0.18), 3, 3);
        }
    }

    private static void DrawMoon(DrawingContext context, Point c, double size, IBrush fill, IBrush edge)
    {
        context.DrawEllipse(fill, null, c, size * 0.5, size * 0.5);
        context.DrawEllipse(edge, null, c + new Vector(size * 0.16, -size * 0.06), size * 0.38, size * 0.38);
    }

    private static void DrawRabbit(DrawingContext context, Point c, double size, IBrush brush)
    {
        context.DrawEllipse(brush, null, new Point(c.X - size * 0.16, c.Y - size * 0.42), size * 0.10, size * 0.28);
        context.DrawEllipse(brush, null, new Point(c.X + size * 0.10, c.Y - size * 0.40), size * 0.09, size * 0.24);
        context.DrawEllipse(brush, null, c, size * 0.28, size * 0.26);
        context.DrawEllipse(brush, null, new Point(c.X + size * 0.06, c.Y + size * 0.32), size * 0.22, size * 0.20);
    }

    private static void DrawCloud(DrawingContext context, Point c, double size, IBrush brush)
    {
        context.DrawEllipse(brush, null, c, size * 0.36, size * 0.22);
        context.DrawEllipse(brush, null, c + new Vector(-size * 0.22, 0.04 * size), size * 0.22, size * 0.16);
        context.DrawEllipse(brush, null, c + new Vector(size * 0.24, 0.02 * size), size * 0.20, size * 0.15);
    }

    private static void DrawFlag(DrawingContext context, Point c, double size, IBrush cloth, IBrush star)
    {
        context.DrawLine(new Pen(cloth, 2), new Point(c.X - size * 0.42, c.Y - size * 0.5), new Point(c.X - size * 0.42, c.Y + size * 0.5));
        context.DrawRectangle(cloth, null, new Rect(c.X - size * 0.40, c.Y - size * 0.48, size * 0.82, size * 0.52), 3, 3);
        DrawStar(context, new Point(c.X - size * 0.12, c.Y - size * 0.22), size * 0.22, 0, star);
    }

    private static void DrawPumpkin(DrawingContext context, Point c, double size, IBrush body, IBrush face)
    {
        context.DrawEllipse(body, null, c, size * 0.48, size * 0.40);
        context.DrawEllipse(body, null, c + new Vector(-size * 0.22, 0), size * 0.28, size * 0.38);
        context.DrawEllipse(body, null, c + new Vector(size * 0.22, 0), size * 0.28, size * 0.38);
        context.DrawRectangle(body, null, new Rect(c.X - size * 0.06, c.Y - size * 0.52, size * 0.12, size * 0.16), 2, 2);
        context.DrawEllipse(face, null, c + new Vector(-size * 0.14, -size * 0.06), 3.2, 3.6);
        context.DrawEllipse(face, null, c + new Vector(size * 0.14, -size * 0.06), 3.2, 3.6);
        context.DrawLine(new Pen(face, 2, lineCap: PenLineCap.Round), c + new Vector(-size * 0.12, size * 0.14), c + new Vector(size * 0.12, size * 0.14));
    }

    private static void DrawBat(DrawingContext context, Point c, double size, double angle, IBrush brush)
    {
        using (context.PushTransform(Rotate(c, angle)))
        {
            var g = new StreamGeometry();
            using (var geo = g.Open())
            {
                geo.BeginFigure(new Point(c.X, c.Y), true);
                geo.CubicBezierTo(new Point(c.X - size * 0.2, c.Y - size * 0.25),
                    new Point(c.X - size * 0.55, c.Y - size * 0.05), new Point(c.X - size * 0.7, c.Y + size * 0.12));
                geo.LineTo(new Point(c.X - size * 0.35, c.Y + size * 0.02));
                geo.LineTo(new Point(c.X, c.Y + size * 0.12));
                geo.LineTo(new Point(c.X + size * 0.35, c.Y + size * 0.02));
                geo.LineTo(new Point(c.X + size * 0.7, c.Y + size * 0.12));
                geo.CubicBezierTo(new Point(c.X + size * 0.55, c.Y - size * 0.05),
                    new Point(c.X + size * 0.2, c.Y - size * 0.25), new Point(c.X, c.Y));
                geo.EndFigure(true);
            }
            context.DrawGeometry(brush, null, g);
        }
    }

    private static void DrawTree(DrawingContext context, Point c, double size, IBrush leaf, IBrush ornament)
    {
        var g = new StreamGeometry();
        using (var geo = g.Open())
        {
            geo.BeginFigure(new Point(c.X, c.Y - size * 0.55), true);
            geo.LineTo(new Point(c.X + size * 0.22, c.Y - size * 0.18));
            geo.LineTo(new Point(c.X + size * 0.12, c.Y - size * 0.18));
            geo.LineTo(new Point(c.X + size * 0.32, c.Y + size * 0.12));
            geo.LineTo(new Point(c.X + size * 0.16, c.Y + size * 0.12));
            geo.LineTo(new Point(c.X + size * 0.40, c.Y + size * 0.42));
            geo.LineTo(new Point(c.X - size * 0.40, c.Y + size * 0.42));
            geo.LineTo(new Point(c.X - size * 0.16, c.Y + size * 0.12));
            geo.LineTo(new Point(c.X - size * 0.32, c.Y + size * 0.12));
            geo.LineTo(new Point(c.X - size * 0.12, c.Y - size * 0.18));
            geo.LineTo(new Point(c.X - size * 0.22, c.Y - size * 0.18));
            geo.EndFigure(true);
        }
        context.DrawGeometry(leaf, null, g);
        context.DrawRectangle(leaf, null, new Rect(c.X - size * 0.07, c.Y + size * 0.42, size * 0.14, size * 0.16), 2, 2);
        context.DrawEllipse(ornament, null, c + new Vector(-size * 0.12, 0.08 * size), 3, 3);
        context.DrawEllipse(ornament, null, c + new Vector(size * 0.14, 0.18 * size), 2.6, 2.6);
        context.DrawEllipse(ornament, null, c + new Vector(0, -size * 0.22), 2.8, 2.8);
    }

    private static void DrawSnowflake(DrawingContext context, Point c, double size, IBrush brush)
    {
        var pen = new Pen(brush, Math.Max(1, size * 0.12), lineCap: PenLineCap.Round);
        for (var i = 0; i < 6; i++)
        {
            var a = i * Math.PI / 3;
            var d = Polar(a, size * 0.5);
            context.DrawLine(pen, c - d, c + d);
            var arm = c + d * 0.55;
            var side = Polar(a + Math.PI / 2, size * 0.18);
            context.DrawLine(pen, arm - side, arm + d * 0.2);
            context.DrawLine(pen, arm + side, arm + d * 0.2);
        }
    }

    private static Vector Polar(double angle, double radius) =>
        new(Math.Cos(angle) * radius, Math.Sin(angle) * radius);

    private static Matrix Rotate(Point c, double angle) =>
        Matrix.CreateTranslation(-c.X, -c.Y) * Matrix.CreateRotation(angle) * Matrix.CreateTranslation(c.X, c.Y);
}
