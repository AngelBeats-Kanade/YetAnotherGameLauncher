using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;

// 一次性工具：生成应用图标（多尺寸 PNG → PNG-in-ICO）。
// 运行：dotnet run --project tools/IconGen [-- <输出目录>]
// 产物：app-icon.ico（窗口/exe 图标）+ app-icon.png（关于页展示）。

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

/// <summary>渐变圆角方块 + 播放三角，与启动器主题同一蓝色系。</summary>
static RenderTargetBitmap RenderIcon(int size)
{
    var bitmap = new RenderTargetBitmap(new PixelSize(size, size));
    using (var dc = bitmap.CreateDrawingContext())
    {
        // 背板：对角渐变圆角方块
        dc.FillRectangle(
            new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromRgb(0x4C, 0x8D, 0xFF), 0),
                    new GradientStop(Color.FromRgb(0x1D, 0x50, 0xB0), 1),
                },
            },
            new Rect(0, 0, size, size),
            (float)(size * 0.22));

        // 播放三角（24 视口图形按图标尺寸缩放）
        using (dc.PushTransform(Matrix.CreateScale(size / 24.0, size / 24.0)))
        {
            var triangle = new StreamGeometry();
            using (var ctx = triangle.Open())
            {
                ctx.BeginFigure(new Point(8, 5), isFilled: true);
                ctx.LineTo(new Point(8, 19));
                ctx.LineTo(new Point(19, 12));
                ctx.EndFigure(true);
            }

            dc.DrawGeometry(Brushes.White, null, triangle);
        }
    }

    return bitmap;
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
