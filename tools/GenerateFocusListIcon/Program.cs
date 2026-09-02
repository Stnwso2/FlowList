using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

var outputPath = args.Length == 1
    ? Path.GetFullPath(args[0])
    : throw new ArgumentException("Pass the destination .ico path.");

Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
var images = new[] { 16, 20, 24, 32, 40, 48, 64 }.Select(CreateIconImage).ToArray();

using var output = File.Create(outputPath);
using var writer = new BinaryWriter(output);
writer.Write((ushort)0);
writer.Write((ushort)1);
writer.Write((ushort)images.Length);

var offset = 6 + images.Length * 16;
foreach (var image in images)
{
    writer.Write((byte)image.Size);
    writer.Write((byte)image.Size);
    writer.Write((byte)0);
    writer.Write((byte)0);
    writer.Write((ushort)1);
    writer.Write((ushort)32);
    writer.Write(image.Data.Length);
    writer.Write(offset);
    offset += image.Data.Length;
}

foreach (var image in images) writer.Write(image.Data);

static IconImage CreateIconImage(int size)
{
    using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var graphics = Graphics.FromImage(bitmap);
    graphics.SmoothingMode = SmoothingMode.AntiAlias;
    graphics.Clear(Color.Transparent);

    using var outer = RoundedRectangle(new Rectangle(0, 0, size, size), Scale(10));
    using var outerBrush = new SolidBrush(Color.FromArgb(232, 240, 255));
    graphics.FillPath(outerBrush, outer);
    using var inner = RoundedRectangle(new Rectangle(Scale(8), Scale(7), Scale(16), Scale(18)), Scale(5));
    using var innerBrush = new SolidBrush(Color.FromArgb(37, 99, 235));
    graphics.FillPath(innerBrush, inner);
    using var check = new Pen(Color.White, Math.Max(1.2f, size / 13.3f))
    {
        StartCap = LineCap.Round,
        EndCap = LineCap.Round,
        LineJoin = LineJoin.Round,
    };
    graphics.DrawLines(check, [new Point(Scale(12), Scale(16)), new Point(Scale(14.4), Scale(18.4)), new Point(Scale(20.5), Scale(12))]);

    using var data = new MemoryStream();
    using var writer = new BinaryWriter(data);
    writer.Write(40);
    writer.Write(size);
    writer.Write(size * 2);
    writer.Write((ushort)1);
    writer.Write((ushort)32);
    writer.Write(0);
    writer.Write(size * size * 4);
    writer.Write(0);
    writer.Write(0);
    writer.Write(0);
    writer.Write(0);

    for (var y = size - 1; y >= 0; y--)
    for (var x = 0; x < size; x++)
    {
        var pixel = bitmap.GetPixel(x, y);
        writer.Write(pixel.B);
        writer.Write(pixel.G);
        writer.Write(pixel.R);
        writer.Write(pixel.A);
    }

    writer.Write(new byte[((size + 31) / 32) * 4 * size]);
    return new IconImage(size, data.ToArray());

    int Scale(double value) => (int)Math.Round(value * size / 32d, MidpointRounding.AwayFromZero);
}

static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
{
    var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
    var path = new GraphicsPath();
    path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
    path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
    path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
    path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
    path.CloseFigure();
    return path;
}

internal sealed record IconImage(int Size, byte[] Data);
