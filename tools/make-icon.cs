#:project ..\src\FlashProof\FlashProof.csproj
#:package Avalonia.Headless@12.1.4
#:property TargetFramework=net10.0-windows
#:property TreatWarningsAsErrors=false

// Draws FlashProof's icon (a USB stick with a check mark) and writes src\FlashProof\Assets\FlashProof.ico, holding
// PNG images at 16 to 256 px, plus a 256 px PNG preview next to it. This file is the icon's source: change the
// drawing here and run it again from the repo root: dotnet run tools/make-icon.cs
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;

AppBuilder.Configure<Application>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();

string assets = Path.GetFullPath(Path.Combine("src", "FlashProof", "Assets"));
Directory.CreateDirectory(assets);
int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
var images = sizes.Select(size => (size, png: Render(size))).ToList();
File.WriteAllBytes(Path.Combine(assets, "FlashProof.png"), images[^1].png);
File.WriteAllBytes(Path.Combine(assets, "FlashProof.ico"), Ico(images));
Console.WriteLine($"Wrote {Path.Combine(assets, "FlashProof.ico")} ({string.Join(", ", sizes)} px).");

// The drawing, on a 256 × 256 grid: the connector on top, the body below it, a white check mark on the body, and a
// white border round the whole shape, so the blue body doesn't vanish on a blue title bar or a dark taskbar (the
// user's choice, 14 Nov 2026). The drawing is scaled down a little so the border fits the square.
static byte[] Render(int size)
{
    const double border = 16;
    double minX = 58 - border, maxX = 198 + border, minY = 11 - border, maxY = 244 + border;
    double scale = Math.Min(252 / (maxX - minX), 252 / (maxY - minY));
    double tx = (256 - (maxX - minX) * scale) / 2 - minX * scale, ty = (256 - (maxY - minY) * scale) / 2 - minY * scale;

    using var bitmap = new RenderTargetBitmap(new PixelSize(size, size));
    using (var ctx = bitmap.CreateDrawingContext())
    using (ctx.PushTransform(Matrix.CreateScale(size / 256.0, size / 256.0)))
    using (ctx.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(tx, ty)))
    {
        var connector = new Rect(86, 14, 84, 66);
        var bodyRect = new Rect(58, 72, 140, 172);
        var white = new Pen(Brushes.White, 2 * border, lineJoin: PenLineJoin.Round);
        ctx.DrawRectangle(Brushes.White, white, connector, 6 + border, 6 + border);
        ctx.DrawRectangle(Brushes.White, white, bodyRect, 22 + border, 22 + border);

        var steel = new SolidColorBrush(Color.Parse("#c3c9d2"));
        var steelEdge = new Pen(new SolidColorBrush(Color.Parse("#7d8693")), 6);
        var hole = new SolidColorBrush(Color.Parse("#5b6472"));
        var body = new SolidColorBrush(Color.Parse("#2a78d6"));
        ctx.DrawRectangle(steel, steelEdge, connector, 6, 6);
        ctx.DrawRectangle(hole, null, new Rect(104, 32, 16, 16));
        ctx.DrawRectangle(hole, null, new Rect(136, 32, 16, 16));
        ctx.DrawRectangle(body, null, bodyRect, 22, 22);
        var check = new StreamGeometry();
        using (var g = check.Open())
        {
            g.BeginFigure(new Point(90, 160), false);
            g.LineTo(new Point(117, 189));
            g.LineTo(new Point(166, 126));
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, new Pen(Brushes.White, 22, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), check);
    }
    using var stream = new MemoryStream();
    bitmap.Save(stream);
    return stream.ToArray();
}

// An .ico file whose images are PNGs (Windows Vista and later read these at every size).
static byte[] Ico(IReadOnlyList<(int size, byte[] png)> images)
{
    using var stream = new MemoryStream();
    using var w = new BinaryWriter(stream);
    w.Write((ushort)0);
    w.Write((ushort)1);
    w.Write((ushort)images.Count);
    int offset = 6 + 16 * images.Count;
    foreach (var (size, png) in images)
    {
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)0);
        w.Write((byte)0);
        w.Write((ushort)1);
        w.Write((ushort)32);
        w.Write(png.Length);
        w.Write(offset);
        offset += png.Length;
    }
    foreach (var (_, png) in images)
        w.Write(png);
    return stream.ToArray();
}
