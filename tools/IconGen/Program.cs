using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;

// 一次性工具：生成应用图标（多尺寸 PNG → PNG-in-ICO）。
// 运行：dotnet run --project tools/IconGen [-- <输出目录>]
// 产物：app-icon.ico（窗口/exe 图标）+ app-icon.png（关于页/侧栏展示）。
//
// 设计：深空蓝底 + 45° 朝右上的白色火箭 + 橙色尾焰 + 星光点缀。
// "Rocket" 呼应 launcher（发射器）语义，与启动器的深色 UI / 蓝色 accent 一脉相承。

_ = AppBuilder.Configure<IconGenApp>()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .UseSkia()
    .SetupWithoutStarting();

var outDir = args.Length > 0
    ? args[0]
    : Path.Combine("src", "YetAnotherGameLauncher", "Assets");
Directory.CreateDirectory(outDir);

var sizes = new[] { 16, 24, 32, 48, 64, 128, 256 };
var images = new List<(int Size, byte[] Png)>();
foreach (var size in sizes)
{
    using var bitmap = RenderIcon(size);
    using var ms = new MemoryStream();
    bitmap.Save(ms, new PngBitmapEncoderOptions());
    images.Add((size, ms.ToArray()));
}

File.WriteAllBytes(Path.Combine(outDir, "app-icon.ico"), PackIco(images));
File.WriteAllBytes(Path.Combine(outDir, "app-icon.png"), images[^1].Png);
Console.WriteLine($"图标已生成：{Path.GetFullPath(outDir)}（{string.Join('/', sizes)} px）");

static RenderTargetBitmap RenderIcon(int size)
{
    // 全部图形在 512×512 设计空间绘制，再整体缩放到目标尺寸
    const int D = 512;
    var bitmap = new RenderTargetBitmap(new PixelSize(size, size));
    using (var dc = bitmap.CreateDrawingContext())
    using (dc.PushTransform(Matrix.CreateScale(size / (double)D, size / (double)D)))
    {
        // ---- 背板：深空对角渐变圆角方块 ----
        dc.FillRectangle(
            new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromRgb(0x18, 0x2B, 0x4E), 0),
                    new GradientStop(Color.FromRgb(0x0B, 0x12, 0x22), 1),
                },
            },
            new Rect(0, 0, D, D),
            (float)(D * 0.23));

        // ---- 顶部冷光晕（月色），给深底一点空间感 ----
        dc.FillRectangle(
            new RadialGradientBrush
            {
                Center = new RelativePoint(0.5, 0.08, RelativeUnit.Relative),
                RadiusX = new RelativeScalar(0.75, RelativeUnit.Relative),
                RadiusY = new RelativeScalar(0.6, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x5A, 0x4C, 0x8D, 0xFF), 0),
                    new GradientStop(Color.FromArgb(0x00, 0x4C, 0x8D, 0xFF), 1),
                },
            },
            new Rect(0, 0, D, D));

        // ---- 星光（四角星）----
        DrawSparkle(dc, 108, 118, 26, 0.85);
        DrawSparkle(dc, 414, 84, 16, 0.6);
        DrawSparkle(dc, 428, 396, 20, 0.5);
        DrawSparkle(dc, 84, 402, 13, 0.45);

        // ---- 火箭（设计空间内朝上绘制，整体绕中心转 45° 使机头朝右上）----
        // 行向量约定：先平移到原点、旋转、再移回中心
        using (dc.PushTransform(
                   Matrix.CreateTranslation(-256, -256)
                   * Matrix.CreateRotation(MathF.PI / 4f)
                   * Matrix.CreateTranslation(256, 256)))
        {
            // 尾焰（先画，压在机身与尾翼之下）
            dc.DrawGeometry(
                new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                    GradientStops =
                    {
                        new GradientStop(Color.FromRgb(0xFF, 0xC5, 0x5C), 0),
                        new GradientStop(Color.FromRgb(0xFF, 0x6B, 0x35), 1),
                    },
                },
                null,
                StreamGeometry.Parse("M236 336 C246 372 256 400 256 400 C256 400 266 372 276 336 Z"));

            // 尾翼
            dc.DrawGeometry(
                new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                    GradientStops =
                    {
                        new GradientStop(Color.FromRgb(0xD9, 0xE6, 0xF8), 0),
                        new GradientStop(Color.FromRgb(0x9D, 0xB4, 0xD8), 1),
                    },
                },
                null,
                StreamGeometry.Parse("M208 226 C174 260 160 306 163 358 L209 336 Z"));
            dc.DrawGeometry(
                new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                    GradientStops =
                    {
                        new GradientStop(Color.FromRgb(0xD9, 0xE6, 0xF8), 0),
                        new GradientStop(Color.FromRgb(0x9D, 0xB4, 0xD8), 1),
                    },
                },
                null,
                StreamGeometry.Parse("M304 226 C338 260 352 306 349 358 L303 336 Z"));

            // 机身（白→浅灰蓝的立体渐变）
            dc.DrawGeometry(
                new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                    GradientStops =
                    {
                        new GradientStop(Color.FromRgb(0xFF, 0xFF, 0xFF), 0),
                        new GradientStop(Color.FromRgb(0xC6, 0xD5, 0xEC), 1),
                    },
                },
                null,
                StreamGeometry.Parse(
                    "M256 84 C218 116 204 192 204 300 L204 338 L308 338 L308 300 C308 192 294 116 256 84 Z"));

            // 舷窗：白环 + 蓝色镜面
            dc.DrawEllipse(Brushes.White, null, new Point(256, 198), 42, 42);
            dc.DrawEllipse(
                new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                    GradientStops =
                    {
                        new GradientStop(Color.FromRgb(0x4C, 0x8D, 0xFF), 0),
                        new GradientStop(Color.FromRgb(0x14, 0x3B, 0x7A), 1),
                    },
                },
                null,
                new Point(256, 198), 30, 30);

            // 机腹分隔线（细节）
            dc.DrawLine(
                new Pen(new SolidColorBrush(Color.FromArgb(0x66, 0x8F, 0xA6, 0xC8)), 6),
                new Point(212, 306), new Point(300, 306));
        }
    }

    return bitmap;

    static void DrawSparkle(DrawingContext dc, double x, double y, double r, double opacity)
    {
        var k = r * 0.24;
        var brush = new SolidColorBrush(Color.FromArgb((byte)(opacity * 255), 0xE8, 0xF1, 0xFF));
        dc.DrawGeometry(
            brush,
            null,
            StreamGeometry.Parse(
                $"M{x} {y - r} L{x + k} {y - k} L{x + r} {y} L{x + k} {y + k} L{x} {y + r} " +
                $"L{x - k} {y + k} L{x - r} {y} L{x - k} {y - k} Z"));
    }
}

/// <summary>PNG 帧打包为 ICO（Vista+ 支持 PNG 压缩帧）。</summary>
static byte[] PackIco(List<(int Size, byte[] Png)> images)
{
    using var ms = new MemoryStream();
    using var writer = new BinaryWriter(ms);
    writer.Write((ushort)0);                    // reserved
    writer.Write((ushort)1);                    // type: icon
    writer.Write((ushort)images.Count);
    var offset = 6 + 16 * images.Count;
    foreach (var (size, png) in images)
    {
        writer.Write((byte)(size >= 256 ? 0 : size));   // width
        writer.Write((byte)(size >= 256 ? 0 : size));   // height
        writer.Write((byte)0);                          // palette
        writer.Write((byte)0);                          // reserved
        writer.Write((ushort)1);                        // planes
        writer.Write((ushort)32);                       // bpp
        writer.Write(png.Length);
        writer.Write(offset);
        offset += png.Length;
    }

    foreach (var (_, png) in images)
    {
        writer.Write(png);
    }

    writer.Flush();
    return ms.ToArray();
}

/// <summary>最小 Avalonia 应用壳（仅用于 headless 引擎初始化）。</summary>
sealed class IconGenApp : Application;
