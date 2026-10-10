using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace MaoX.Controls;

/// <summary>平面的人物皮肤预览（正面 / 背面，含外层和披风），没有皮肤时画一个轮廓。</summary>
public class SkinPreview : Control
{
    public static readonly StyledProperty<Bitmap> SkinProperty =
        AvaloniaProperty.Register<SkinPreview, Bitmap>(nameof(Skin));

    public static readonly StyledProperty<Bitmap> CapeProperty =
        AvaloniaProperty.Register<SkinPreview, Bitmap>(nameof(Cape));

    public static readonly StyledProperty<bool> SlimProperty =
        AvaloniaProperty.Register<SkinPreview, bool>(nameof(Slim));

    public static readonly StyledProperty<bool> BackProperty =
        AvaloniaProperty.Register<SkinPreview, bool>(nameof(Back));

    static SkinPreview()
    {
        AffectsRender<SkinPreview>(SkinProperty, CapeProperty, SlimProperty, BackProperty);
    }

    public Bitmap Skin
    {
        get => GetValue(SkinProperty);
        set => SetValue(SkinProperty, value);
    }

    public Bitmap Cape
    {
        get => GetValue(CapeProperty);
        set => SetValue(CapeProperty, value);
    }

    public bool Slim
    {
        get => GetValue(SlimProperty);
        set => SetValue(SlimProperty, value);
    }

    public bool Back
    {
        get => GetValue(BackProperty);
        set => SetValue(BackProperty, value);
    }

    /// <summary>纤细（Alex）模型的手臂只有 3 像素宽，64×64 皮肤里第 54–55 列的手臂区域是透明的。</summary>
    public static bool LooksSlim(Bitmap skin)
    {
        if (skin == null || skin.PixelSize != new PixelSize(64, 64))
            return false;
        var buffer = new byte[64 * 64 * 4];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            skin.CopyPixels(new PixelRect(0, 0, 64, 64), handle.AddrOfPinnedObject(), buffer.Length, 64 * 4);
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            handle.Free();
        }
        for (var y = 20; y < 32; y++)
        {
            for (var x = 54; x < 56; x++)
            {
                if (buffer[(y * 64 + x) * 4 + 3] != 0)
                    return false;
            }
        }
        return true;
    }

    public override void Render(DrawingContext context)
    {
        var scale = Math.Floor(Math.Min(Bounds.Width / 16, Bounds.Height / 32));
        if (scale < 1)
            return;
        var origin = new Point(Math.Round((Bounds.Width - 16 * scale) / 2), Math.Round((Bounds.Height - 32 * scale) / 2));
        var arm = Slim ? 3 : 4;
        Rect Dest(double x, double y, double w, double h) =>
            new(origin.X + x * scale, origin.Y + y * scale, w * scale, h * scale);

        var skin = Skin;
        if (skin == null || skin.PixelSize.Width < 64)
        {
            var fill = this.TryFindResource("Border", out var v) && v is IBrush b ? b : Brushes.Gray;
            foreach (var rect in new[]
                     {
                         Dest(4, 0, 8, 8), Dest(4, 8, 8, 12), Dest(4 - arm, 8, arm, 12), Dest(12, 8, arm, 12),
                         Dest(4, 20, 4, 12), Dest(8, 20, 4, 12),
                     })
                context.FillRectangle(fill, rect);
            return;
        }

        var unit = skin.Size.Width / 64;
        var legacy = skin.PixelSize.Height * 2 == skin.PixelSize.Width;
        using var _ = context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None });

        void Part(Bitmap image, double u, double sx, double sy, double w, double h, double dx, double dy, bool mirror = false)
        {
            var dest = Dest(dx, dy, w, h);
            var source = new Rect(sx * u, sy * u, w * u, h * u);
            if (!mirror)
            {
                context.DrawImage(image, source, dest);
                return;
            }
            using (context.PushTransform(Matrix.CreateScale(-1, 1) * Matrix.CreateTranslation(2 * dest.X + dest.Width, 0)))
                context.DrawImage(image, source, dest);
        }

        void Layer(double sx, double sy, double w, double h, double dx, double dy, double ox, double oy)
        {
            Part(skin, unit, sx, sy, w, h, dx, dy);
            if (!legacy)
                Part(skin, unit, ox, oy, w, h, dx, dy);
        }

        if (!Back)
        {
            Part(skin, unit, 8, 8, 8, 8, 4, 0);
            Part(skin, unit, 40, 8, 8, 8, 4, 0);
            Layer(20, 20, 8, 12, 4, 8, 20, 36);
            Layer(44, 20, arm, 12, 4 - arm, 8, 44, 36);
            Layer(4, 20, 4, 12, 4, 20, 4, 36);
            if (legacy)
            {
                Part(skin, unit, 44, 20, arm, 12, 12, 8, mirror: true);
                Part(skin, unit, 4, 20, 4, 12, 8, 20, mirror: true);
            }
            else
            {
                Layer(36, 52, arm, 12, 12, 8, 52, 52);
                Layer(20, 52, 4, 12, 8, 20, 4, 52);
            }
            return;
        }

        Part(skin, unit, 24, 8, 8, 8, 4, 0);
        Part(skin, unit, 56, 8, 8, 8, 4, 0);
        Layer(32, 20, 8, 12, 4, 8, 32, 36);
        Layer(48 + arm, 20, arm, 12, 12, 8, 48 + arm, 36);
        Layer(12, 20, 4, 12, 8, 20, 12, 36);
        if (legacy)
        {
            Part(skin, unit, 48 + arm, 20, arm, 12, 4 - arm, 8, mirror: true);
            Part(skin, unit, 12, 20, 4, 12, 4, 20, mirror: true);
        }
        else
        {
            Layer(40 + arm, 52, arm, 12, 4 - arm, 8, 56 + arm, 52);
            Layer(28, 52, 4, 12, 4, 20, 12, 52);
        }
        if (Cape is { } cape && cape.PixelSize.Width >= 64)
            Part(cape, cape.Size.Width / 64, 1, 1, 10, 16, 3, 8);
    }
}
