using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace MaoX.Controls;

/// <summary>可拖动旋转的 3D 皮肤预览（软件光栅），没有皮肤时画灰色方块人。</summary>
public class SkinPreview : Control
{
    public static readonly StyledProperty<Bitmap> SkinProperty =
        AvaloniaProperty.Register<SkinPreview, Bitmap>(nameof(Skin));

    public static readonly StyledProperty<Bitmap> CapeProperty =
        AvaloniaProperty.Register<SkinPreview, Bitmap>(nameof(Cape));

    public static readonly StyledProperty<bool> SlimProperty =
        AvaloniaProperty.Register<SkinPreview, bool>(nameof(Slim));

    public static readonly StyledProperty<double> YawProperty =
        AvaloniaProperty.Register<SkinPreview, double>(nameof(Yaw), 22);

    public static readonly StyledProperty<double> PitchProperty =
        AvaloniaProperty.Register<SkinPreview, double>(nameof(Pitch), 8);

    static SkinPreview()
    {
        AffectsRender<SkinPreview>(SkinProperty, CapeProperty, SlimProperty, YawProperty, PitchProperty);
        SkinProperty.Changed.AddClassHandler<SkinPreview>((c, _) => c._skinPixels = null);
        CapeProperty.Changed.AddClassHandler<SkinPreview>((c, _) => c._capePixels = null);
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

    public double Yaw
    {
        get => GetValue(YawProperty);
        set => SetValue(YawProperty, value);
    }

    public double Pitch
    {
        get => GetValue(PitchProperty);
        set => SetValue(PitchProperty, Math.Clamp(value, -35, 35));
    }

    private byte[] _skinPixels;
    private int _skinW, _skinH;
    private byte[] _capePixels;
    private int _capeW, _capeH;
    private WriteableBitmap _frame;
    private Point? _drag;
    private DispatcherTimer _spin;
    private bool _userRotated;

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

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _spin ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _spin.Tick -= Spin;
        _spin.Tick += Spin;
        _spin.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _spin?.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void Spin(object sender, EventArgs e)
    {
        if (_userRotated || _drag != null)
            return;
        Yaw += 0.8;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        _drag = e.GetPosition(this);
        _userRotated = true;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_drag is not { } last)
            return;
        var now = e.GetPosition(this);
        Yaw += now.X - last.X;
        Pitch += now.Y - last.Y;
        _drag = now;
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        _drag = null;
        e.Pointer.Capture(null);
    }

    public override void Render(DrawingContext context)
    {
        var w = Math.Max(1, (int)Math.Ceiling(Bounds.Width));
        var h = Math.Max(1, (int)Math.Ceiling(Bounds.Height));
        if (_frame == null || _frame.PixelSize.Width != w || _frame.PixelSize.Height != h)
        {
            _frame?.Dispose();
            _frame = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        }

        EnsurePixels(Skin, ref _skinPixels, ref _skinW, ref _skinH);
        EnsurePixels(Cape, ref _capePixels, ref _capeW, ref _capeH);

        using (var fb = _frame.Lock())
        {
            var stride = fb.RowBytes;
            var pixels = new byte[stride * h];
            var depth = new float[w * h];
            Array.Fill(depth, float.MinValue);
            DrawModel(pixels, depth, stride, w, h);
            Marshal.Copy(pixels, 0, fb.Address, pixels.Length);
        }
        context.DrawImage(_frame, new Rect(0, 0, w, h));
    }

    private void DrawModel(byte[] pixels, float[] depth, int stride, int width, int height)
    {
        var yaw = Yaw * Math.PI / 180;
        var pitch = Pitch * Math.PI / 180;
        var cy = (float)Math.Cos(yaw);
        var sy = (float)Math.Sin(yaw);
        var cp = (float)Math.Cos(pitch);
        var sp = (float)Math.Sin(pitch);
        var arm = Slim ? 3f : 4f;
        var scale = Math.Min(width, height) / 38f;
        var cx = width / 2f;
        var cyScreen = height / 2f + height * 0.04f;
        var faces = new List<Face>(48);
        AddPlayer(faces, arm);
        if (_capePixels != null)
            AddCape(faces);

        foreach (var face in faces)
        {
            var p0 = Project(face.V0, cy, sy, cp, sp, cx, cyScreen, scale);
            var p1 = Project(face.V1, cy, sy, cp, sp, cx, cyScreen, scale);
            var p2 = Project(face.V2, cy, sy, cp, sp, cx, cyScreen, scale);
            var p3 = Project(face.V3, cy, sy, cp, sp, cx, cyScreen, scale);
            if ((p1.X - p0.X) * (p2.Y - p0.Y) - (p1.Y - p0.Y) * (p2.X - p0.X) <= 0)
                continue;
            var tex = face.Cape ? (_capePixels, _capeW, _capeH) : (_skinPixels, _skinW, _skinH);
            Raster(pixels, depth, stride, width, height, p0, p1, p2, face.T0, face.T1, face.T2, tex, face.Fill);
            Raster(pixels, depth, stride, width, height, p0, p2, p3, face.T0, face.T2, face.T3, tex, face.Fill);
        }
    }

    private readonly record struct Vert(float X, float Y, float Z);

    private readonly record struct Face(Vert V0, Vert V1, Vert V2, Vert V3,
        Vert T0, Vert T1, Vert T2, Vert T3, bool Cape, uint Fill);

    private static (float X, float Y, float Z) Project(Vert v, float cy, float sy, float cp, float sp,
                                                       float cx, float cyScreen, float scale)
    {
        var x = v.X * cy + v.Z * sy;
        var z = -v.X * sy + v.Z * cy;
        var y = v.Y * cp - z * sp;
        z = v.Y * sp + z * cp;
        var zoom = scale * (1.15f + z * 0.012f);
        return (cx + x * zoom, cyScreen - y * zoom, z);
    }

    private void AddPlayer(List<Face> faces, float arm)
    {
        var legacy = _skinH > 0 && _skinH * 2 == _skinW;
        Box(faces, -4, 24, -4, 4, 32, 4, 8, 8, 8, 8, 40, 8, false);
        Box(faces, -4, 12, -2, 4, 24, 2, 20, 20, 20, 36, 0, 0, !legacy);
        Box(faces, -4 - arm, 12, -2, -4, 24, 2, 44, 20, 44, 36, arm, Slim ? 3 : 4, !legacy);
        if (legacy)
            Box(faces, 4, 12, -2, 4 + arm, 24, 2, 44, 20, 0, 0, arm, Slim ? 3 : 4, false, true);
        else
            Box(faces, 4, 12, -2, 4 + arm, 24, 2, 36, 52, 52, 52, arm, Slim ? 3 : 4, true);
        Box(faces, -4, 0, -2, 0, 12, 2, 4, 20, 4, 36, 4, 4, !legacy);
        if (legacy)
            Box(faces, 0, 0, -2, 4, 12, 2, 4, 20, 0, 0, 4, 4, false, true);
        else
            Box(faces, 0, 0, -2, 4, 12, 2, 20, 52, 4, 52, 4, 4, true);
    }

    private static void AddCape(List<Face> faces)
    {
        Box(faces, -5, 8, 3, 5, 24, 4, 1, 1, 0, 0, 10, 16, false, false, true);
    }

    private static void Box(List<Face> faces, float x0, float y0, float z0, float x1, float y1, float z1,
                            float sx, float sy, float ox, float oy, float fw, float fh, bool overlay,
                            bool _ = false, bool cape = false)
    {
        var sw = cape ? 1f : (x1 - x0);
        var sd = cape ? 1f : (z1 - z0);
        FaceSide(faces, new Vert(x0, y0, z1), new Vert(x1, y0, z1), new Vert(x1, y1, z1), new Vert(x0, y1, z1),
                 sx, sy, fw, fh, cape, 0xFF8A8A8A);
        FaceSide(faces, new Vert(x1, y0, z0), new Vert(x0, y0, z0), new Vert(x0, y1, z0), new Vert(x1, y1, z0),
                 sx + fw + sw, sy, fw, fh, cape, 0xFF6E6E6E);
        FaceSide(faces, new Vert(x1, y0, z1), new Vert(x1, y0, z0), new Vert(x1, y1, z0), new Vert(x1, y1, z1),
                 sx + fw, sy, sw, fh, cape, 0xFF7C7C7C);
        FaceSide(faces, new Vert(x0, y0, z0), new Vert(x0, y0, z1), new Vert(x0, y1, z1), new Vert(x0, y1, z0),
                 sx - sw, sy, sw, fh, cape, 0xFF7C7C7C);
        FaceSide(faces, new Vert(x0, y1, z1), new Vert(x1, y1, z1), new Vert(x1, y1, z0), new Vert(x0, y1, z0),
                 sx, sy - sd, fw, sd, cape, 0xFF9A9A9A);
        FaceSide(faces, new Vert(x0, y0, z0), new Vert(x1, y0, z0), new Vert(x1, y0, z1), new Vert(x0, y0, z1),
                 sx + fw, sy - sd, fw, sd, cape, 0xFF5A5A5A);
        if (!overlay || ox <= 0)
            return;
        const float grow = 0.35f;
        Box(faces, x0 - grow, y0 - grow, z0 - grow, x1 + grow, y1 + grow, z1 + grow, ox, oy, 0, 0, fw, fh, false, false, cape);
    }

    private static void FaceSide(List<Face> faces, Vert a, Vert b, Vert c, Vert d,
                                 float u, float v, float uw, float vh, bool cape, uint fill)
    {
        faces.Add(new Face(a, b, c, d,
            new Vert(u, v + vh, 0), new Vert(u + uw, v + vh, 0),
            new Vert(u + uw, v, 0), new Vert(u, v, 0), cape, fill));
    }

    private static void Raster(byte[] pixels, float[] depth, int stride, int width, int height,
                               (float X, float Y, float Z) a, (float X, float Y, float Z) b,
                               (float X, float Y, float Z) c, Vert ta, Vert tb, Vert tc,
                               (byte[] Data, int W, int H) tex, uint fill)
    {
        var minX = (int)Math.Floor(Math.Min(a.X, Math.Min(b.X, c.X)));
        var maxX = (int)Math.Ceiling(Math.Max(a.X, Math.Max(b.X, c.X)));
        var minY = (int)Math.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y)));
        var maxY = (int)Math.Ceiling(Math.Max(a.Y, Math.Max(b.Y, c.Y)));
        minX = Math.Clamp(minX, 0, width - 1);
        maxX = Math.Clamp(maxX, 0, width - 1);
        minY = Math.Clamp(minY, 0, height - 1);
        maxY = Math.Clamp(maxY, 0, height - 1);
        var area = Edge(a.X, a.Y, b.X, b.Y, c.X, c.Y);
        if (Math.Abs(area) < 0.01f)
            return;
        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                var w0 = Edge(b.X, b.Y, c.X, c.Y, x + 0.5f, y + 0.5f) / area;
                var w1 = Edge(c.X, c.Y, a.X, a.Y, x + 0.5f, y + 0.5f) / area;
                var w2 = 1 - w0 - w1;
                if (w0 < 0 || w1 < 0 || w2 < 0)
                    continue;
                var z = w0 * a.Z + w1 * b.Z + w2 * c.Z;
                var di = y * width + x;
                if (z < depth[di])
                    continue;
                uint color = fill;
                if (tex.Data != null && tex.W > 0)
                {
                    var u = w0 * ta.X + w1 * tb.X + w2 * tc.X;
                    var v = w0 * ta.Y + w1 * tb.Y + w2 * tc.Y;
                    var unit = tex.W / 64f;
                    var sx = Math.Clamp((int)(u * unit), 0, tex.W - 1);
                    var sy = Math.Clamp((int)(v * unit), 0, tex.H - 1);
                    var i = (sy * tex.W + sx) * 4;
                    var a8 = tex.Data[i + 3];
                    if (a8 < 16)
                        continue;
                    color = (uint)(a8 << 24 | tex.Data[i + 2] << 16 | tex.Data[i + 1] << 8 | tex.Data[i]);
                }
                depth[di] = z;
                var o = y * stride + x * 4;
                pixels[o] = (byte)color;
                pixels[o + 1] = (byte)(color >> 8);
                pixels[o + 2] = (byte)(color >> 16);
                pixels[o + 3] = (byte)(color >> 24);
            }
        }
    }

    private static float Edge(float ax, float ay, float bx, float by, float cx, float cy) =>
        (cx - ax) * (by - ay) - (cy - ay) * (bx - ax);

    private static void EnsurePixels(Bitmap bitmap, ref byte[] pixels, ref int w, ref int h)
    {
        if (bitmap == null || bitmap.PixelSize.Width < 64)
        {
            pixels = null;
            w = h = 0;
            return;
        }
        if (pixels != null && w == bitmap.PixelSize.Width && h == bitmap.PixelSize.Height)
            return;
        w = bitmap.PixelSize.Width;
        h = bitmap.PixelSize.Height;
        pixels = new byte[w * h * 4];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), pixels.Length, w * 4);
        }
        catch (Exception)
        {
            pixels = null;
            w = h = 0;
        }
        finally
        {
            handle.Free();
        }
    }
}
