using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;

// 一次性工具：生成应用图标（多尺寸 PNG → PNG-in-ICO）。
// 运行：dotnet run --project tools/IconGen [-- <输出目录>]
// 产物：app-icon.ico（窗口/exe 图标）+ app-icon.png（关于页/侧栏展示）。
//
// 设计：深空蓝底 + 星光 + 原创扁平 chibi 蓝发少女头像（大眼睛、腮红、微笑、呆毛）。
// 蓝发呼应启动器的蓝色 accent，可爱二次元风格契合"启动 anime game"的产品定位。

_ = AppBuilder.Configure<IconGenApp>()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .UseSkia()
    .SetupWithoutStarting();

// 子命令：convert <in> <out> —— 图像格式转换（webp→png 等），用于核验/准备素材
if (args.Length >= 2 && args[0] == "convert")
{
    using var src = new Bitmap(args[1]);
    src.Save(args[2], new PngBitmapEncoderOptions());
    Console.WriteLine($"converted {args[1]} -> {args[2]}");
    return;
}

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

        // ---- 蓝发少女 chibi（扁平二次元：后发→双马尾→脸→刘海→五官→呆毛）----
        var hairBack = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromRgb(0x2E, 0x62, 0xD8), 0),
                new GradientStop(Color.FromRgb(0x1D, 0x41, 0x96), 1),
            },
        };
        var hair = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromRgb(0x5C, 0x96, 0xFF), 0),
                new GradientStop(Color.FromRgb(0x2E, 0x62, 0xD8), 1),
            },
        };
        var skin = new SolidColorBrush(Color.FromRgb(0xFF, 0xE9, 0xD9));

        // 后发大圆（压在脸后）+ 两侧低双马尾
        dc.DrawEllipse(hairBack, null, new Point(256, 288), 158, 152);
        dc.DrawGeometry(hairBack, null, StreamGeometry.Parse(
            "M136 232 C88 300 84 390 114 456 C150 430 160 340 152 262 Z"));
        dc.DrawGeometry(hairBack, null, StreamGeometry.Parse(
            "M376 232 C424 300 428 390 398 456 C362 430 352 340 360 262 Z"));

        // 脸
        dc.DrawEllipse(skin, null, new Point(256, 314), 116, 110);

        // 刘海：上弧盖住额头 + 右→左的四束尖刘海（尖朝下垂到眼上方）
        dc.DrawGeometry(hair, null, StreamGeometry.Parse(
            "M141 300 C141 192 194 138 256 138 C318 138 371 192 371 300 " +
            "C366 324 358 338 348 346 C342 324 330 296 316 270 C310 304 298 332 284 348 " +
            "C278 322 266 290 250 268 C244 300 232 328 218 346 C210 320 196 292 184 272 " +
            "C170 284 152 292 141 300 Z"));

        // 眼睛：深蓝大眼 + 双高光
        foreach (var x in new[] { 204.0, 308.0 })
        {
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x16, 0x29, 0x4D)), null, new Point(x, 326), 27, 37);
            dc.DrawEllipse(Brushes.White, null, new Point(x - 9, 311), 9, 9);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0xB4, 0xFF, 0xFF, 0xFF)), null, new Point(x + 8, 340), 4, 4);
        }

        // 腮红 + 微笑小嘴
        var blush = new SolidColorBrush(Color.FromArgb(0x8C, 0xFF, 0xA8, 0xC0));
        dc.DrawEllipse(blush, null, new Point(164, 366), 25, 14);
        dc.DrawEllipse(blush, null, new Point(348, 366), 25, 14);
        dc.DrawGeometry(
            new SolidColorBrush(Color.FromRgb(0xD4, 0x54, 0x7A)), null,
            StreamGeometry.Parse("M238 376 A18 18 0 0 0 274 376 Z"));

        // 呆毛（顶部的卷曲发丝）+ 发丝高光
        var ahogePen = new Pen(new SolidColorBrush(Color.FromRgb(0x5C, 0x96, 0xFF)), 12, lineCap: PenLineCap.Round);
        dc.DrawGeometry(null, ahogePen, StreamGeometry.Parse("M250 144 C240 112 256 94 290 90"));
        dc.DrawLine(
            new Pen(new SolidColorBrush(Color.FromArgb(0xB4, 0x9E, 0xC8, 0xFF)), 13, lineCap: PenLineCap.Round),
            new Point(178, 206), new Point(236, 176));
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
