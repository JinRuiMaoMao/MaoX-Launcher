// 生成应用图标：src/MaoX.UI/Assets/icon.ico 和 packaging/macos/MaoX.icns。
// 图形和 src/MaoX.UI/Controls/Icon.cs 里的 Logo 一致（带狼耳朵的 MX），改 Logo 后记得同步这里再重新生成。
// 用法：dotnet run packaging/MakeIcon.cs
#:package SkiaSharp@2.88.9

using System.Buffers.Binary;
using SkiaSharp;

var here = Path.GetDirectoryName(Path.GetFullPath(GetSourcePath()))!;
var root = Path.GetDirectoryName(here)!;

var background = SKColor.Parse("#151820");
var bar = SKColor.Parse("#E8ECF3");
var accent = SKColor.Parse("#00D9FF");
var bars = SKPath.ParseSvgPathData("M0,30 H20 V130 H0 Z M80,30 H100 V130 H80 Z");
var cross = SKPath.ParseSvgPathData("M0,30 H23 L100,130 H77 Z M77,30 H100 L23,130 H0 Z");
var ears = SKPath.ParseSvgPathData("M0,32 L6,1.5 Q7,-0.5 9,1 L35,32 Z M100,32 L94,1.5 Q93,-0.5 91,1 L65,32 Z");
var innerEars = SKPath.ParseSvgPathData("M7,28 L9.5,10 L26,28 Z M93,28 L90.5,10 L74,28 Z");

byte[] Render(int size)
{
    using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
    var canvas = surface.Canvas;
    canvas.Clear(SKColors.Transparent);
    using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };

    paint.Color = background;
    canvas.DrawRoundRect(new SKRect(0, 0, size, size), size * 0.19f, size * 0.19f, paint);

    var scale = size * 0.7f / 130;
    canvas.Translate((size - 100 * scale) / 2, (size - 130 * scale) / 2);
    canvas.Scale(scale);
    foreach (var (path, color) in new[] { (ears, bar), (innerEars, accent), (bars, bar), (cross, accent) })
    {
        paint.Color = color;
        canvas.DrawPath(path, paint);
    }

    using var image = surface.Snapshot();
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}

void WriteIco(string path, int[] sizes)
{
    var images = sizes.Select(Render).ToArray();
    using var output = new BinaryWriter(File.Create(path));
    output.Write((ushort)0);
    output.Write((ushort)1);
    output.Write((ushort)images.Length);
    var offset = 6 + 16 * images.Length;
    for (var i = 0; i < images.Length; i++)
    {
        output.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
        output.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
        output.Write((byte)0);
        output.Write((byte)0);
        output.Write((ushort)1);
        output.Write((ushort)32);
        output.Write(images[i].Length);
        output.Write(offset);
        offset += images[i].Length;
    }
    foreach (var image in images)
        output.Write(image);
}

void WriteIcns(string path, (string Type, int Size)[] entries)
{
    var chunks = entries.Select(entry => (entry.Type, Data: Render(entry.Size))).ToArray();
    using var output = new BinaryWriter(File.Create(path));
    void Header(string type, int length)
    {
        output.Write(System.Text.Encoding.ASCII.GetBytes(type));
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, length);
        output.Write(buffer);
    }
    Header("icns", 8 + chunks.Sum(chunk => 8 + chunk.Data.Length));
    foreach (var (type, data) in chunks)
    {
        Header(type, 8 + data.Length);
        output.Write(data);
    }
}

var ico = Path.Combine(root, "src", "MaoX.UI", "Assets", "icon.ico");
WriteIco(ico, [16, 24, 32, 48, 64, 128, 256]);
var icns = Path.Combine(here, "macos", "MaoX.icns");
WriteIcns(icns, [("icp4", 16), ("icp5", 32), ("icp6", 64), ("ic07", 128), ("ic08", 256), ("ic09", 512), ("ic10", 1024),
                 ("ic11", 32), ("ic12", 64), ("ic13", 256), ("ic14", 512)]);
Console.WriteLine($"已生成 {ico}（{new FileInfo(ico).Length} 字节）和 {icns}（{new FileInfo(icns).Length} 字节）");

static string GetSourcePath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
