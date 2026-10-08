using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

using System.Drawing;
using System.Windows.Forms;

namespace PenTastic;

static class AppIcon
{
    static readonly Color Ink = Color.FromArgb(0xFF, 0xD1, 0x66);
    static readonly Color Body = Color.FromArgb(0xF4, 0xF7, 0xFB);
    static readonly Color Nib = Color.FromArgb(0x8F, 0xA3, 0xC4);

    public static Bitmap Render(int size)
    {
        var b = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(b);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        using (var tile = RoundedSquare(size, size * 0.22f))
        using (var bg = new LinearGradientBrush(new Point(0, 0), new Point(size, size),
                   Color.FromArgb(0x2A, 0x3A, 0x58), Color.FromArgb(0x12, 0x1A, 0x26)))
            g.FillPath(bg, tile);

        g.ScaleTransform(size / 72f, size / 72f);
        g.TranslateTransform(-18, -18);

        using var ink = new SolidBrush(Ink);
        using (var sparkle = new GraphicsPath())
        {
            sparkle.AddBezier(34, 26, 35, 31.667f, 38.333f, 35, 44, 36);
            sparkle.AddBezier(44, 36, 38.333f, 37, 35, 40.333f, 34, 46);
            sparkle.AddBezier(34, 46, 33, 40.333f, 29.667f, 37, 24, 36);
            sparkle.AddBezier(24, 36, 29.667f, 35, 33, 31.667f, 34, 26);
            g.FillPath(ink, sparkle);
        }
        using (var stroke = new Pen(Ink, 6) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawBezier(stroke, 26, 76, 34, 60, 44, 84, 60, 60);

        using var body = new SolidBrush(Body);
        g.FillPolygon(body, [new PointF(61.76f, 49.04f), new PointF(70.96f, 58.24f), new PointF(85.8f, 43.4f), new PointF(76.6f, 34.2f)]);
        g.FillEllipse(body, 81.2f - 6.5f, 38.8f - 6.5f, 13, 13);
        using var nib = new SolidBrush(Nib);
        g.FillPolygon(nib, [new PointF(60, 60), new PointF(70.96f, 58.24f), new PointF(61.76f, 49.04f)]);
        return b;
    }

    public static Icon Create(Size size)
    {
        var ms = new MemoryStream();
        WriteIco(ms, 16, 20, 24, 32, 40, 48, 64, 256);
        ms.Position = 0;
        return new Icon(ms, size);
    }

    public static void WriteIco(Stream output, params int[] sizes)
    {
        var images = sizes.Select(s =>
        {
            using var bmp = Render(s);
            if (s < 256) return (Size: s, Data: Dib(bmp));
            using var png = new MemoryStream();
            bmp.Save(png, ImageFormat.Png);
            return (Size: s, Data: png.ToArray());
        }).ToList();

        var w = new BinaryWriter(output);
        w.Write((ushort)0);
        w.Write((ushort)1);
        w.Write((ushort)images.Count);
        int offset = 6 + 16 * images.Count;
        foreach (var (s, data) in images)
        {
            w.Write((byte)(s >= 256 ? 0 : s));
            w.Write((byte)(s >= 256 ? 0 : s));
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((ushort)1);
            w.Write((ushort)32);
            w.Write(data.Length);
            w.Write(offset);
            offset += data.Length;
        }
        foreach (var (_, data) in images) w.Write(data);
        w.Flush();
    }

    static byte[] Dib(Bitmap bmp)
    {
        int s = bmp.Width, maskStride = (s + 31) / 32 * 4;
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(40); w.Write(s); w.Write(s * 2); w.Write((ushort)1); w.Write((ushort)32);
        w.Write(0); w.Write(s * s * 4 + maskStride * s); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
        var data = bmp.LockBits(new Rectangle(0, 0, s, s), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var row = new byte[s * 4];
        for (int y = s - 1; y >= 0; y--)
        {
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
            w.Write(row);
        }
        bmp.UnlockBits(data);
        w.Write(new byte[maskStride * s]);
        return ms.ToArray();
    }

    static GraphicsPath RoundedSquare(float size, float r)
    {
        var p = new GraphicsPath();
        float d = r * 2;
        p.AddArc(0, 0, d, d, 180, 90);
        p.AddArc(size - d, 0, d, d, 270, 90);
        p.AddArc(size - d, size - d, d, d, 0, 90);
        p.AddArc(0, size - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
