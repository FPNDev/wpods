using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace WPods.Ui;

internal enum TrayDevice
{
    Buds,
    Case,
    Max,
}

/// <summary>Everything the tray glyph depends on; a new icon is drawn only when this changes.</summary>
/// <param name="Size">Icon size in physical pixels (SM_CXSMICON at the taskbar's DPI).</param>
/// <param name="Level">Ring level 0-100; -1 = not reported (track only).</param>
internal readonly record struct TrayGlyph(int Size, bool LightTaskbar, bool Connected, TrayDevice Device, int Level, bool Charging, bool Mic);

/// <summary>
/// Draws the TrayIcon design (WPods.tray() in the design bundle) with GDI+: track ring, battery arc from 12 o'clock
/// clockwise with round caps, the device glyph (or a bolt while charging), and the mic dot cut out of everything with a gap.
/// Each rendition (16, 20, 24, 32 px) has its own pixel-fitted geometry; other sizes use the largest rendition that fits, centred.
/// </summary>
internal static unsafe partial class TrayIconRenderer
{
    private sealed record Geometry(
        float C, float R, float W, float Gap, float DotX, float DotY, float DotR,
        float HeadY, float HeadR, float HeadLeftX, float HeadRightX, float StemW, float StemLeftX, float StemRightX, float StemBottom,
        float CaseX, float CaseY, float CaseW, float CaseH, float CaseRadius, float SeamY, float SeamH,
        float BandY, float BandR, float BandW, float CupLeftX, float CupRightX, float CupY, float CupH, float CupW,
        float BoltX, float BoltY, float BoltW, float BoltH);

    private sealed record Palette(Color Ink, Color Track, Color Faint, Color Green, Color Red, Color Orange);

    private static readonly Geometry[] Renditions =
    [
        new(8, 6.75f, 1.5f, 1, 13.5f, 2.5f, 2,
            5.5f, 1.6f, 5.5f, 10.5f, 1.2f, 5.9f, 8.9f, 11.6f,
            4, 4.5f, 8, 7, 2.25f, 6.5f, 1,
            8.5f, 3.5f, 1, 3.75f, 9.75f, 7.75f, 4.5f, 2.5f,
            5.5f, 3.75f, 5, 8.5f),
        new(10, 8.5f, 1.75f, 1, 16.75f, 3.25f, 2.5f,
            7, 2, 7, 13, 1.5f, 7.5f, 11, 14.5f,
            5, 5.5f, 10, 9, 2.75f, 8, 1,
            10.5f, 4.4f, 1.25f, 4.75f, 12.25f, 9.5f, 5.75f, 3,
            7, 4.75f, 6, 10.5f),
        new(12, 10.25f, 2, 1.25f, 20, 4, 3,
            8.5f, 2.5f, 8.5f, 15.5f, 1.75f, 9.25f, 13, 17.5f,
            6, 6.5f, 12, 11, 3.25f, 9.5f, 1.25f,
            12.5f, 5.3f, 1.5f, 5.75f, 14.5f, 11.5f, 7, 3.75f,
            8.5f, 5.75f, 7, 12.5f),
        new(16, 13.75f, 2.5f, 1.5f, 26.75f, 5.25f, 4,
            11.5f, 3.5f, 11.5f, 20.5f, 2.25f, 12.5f, 17.25f, 23.5f,
            8, 9, 16, 14, 4.25f, 12.5f, 1.5f,
            16.5f, 7.25f, 1.75f, 7.5f, 19.5f, 15.25f, 9.5f, 5,
            11.5f, 7.75f, 9, 16.5f),
    ];

    private static readonly Palette ForDarkTaskbar = new(
        Color.FromArgb(0xFF, 0xFF, 0xFF), Color.FromArgb(61, 0xFF, 0xFF, 0xFF), Color.FromArgb(128, 0xFF, 0xFF, 0xFF),
        Color.FromArgb(0x30, 0xD1, 0x58), Color.FromArgb(0xFF, 0x45, 0x3A), Color.FromArgb(0xFF, 0x9F, 0x0A));

    private static readonly Palette ForLightTaskbar = new(
        Color.FromArgb(0x1B, 0x1B, 0x1B), Color.FromArgb(41, 0, 0, 0), Color.FromArgb(107, 0, 0, 0),
        Color.FromArgb(0x24, 0x8A, 0x3D), Color.FromArgb(0xD7, 0x00, 0x15), Color.FromArgb(0xC9, 0x34, 0x00));

    // Bolt polygon in its box (fractions of width, height).
    private static readonly PointF[] Bolt = [new(0.62f, 0), new(0.04f, 0.56f), new(0.46f, 0.56f), new(0.36f, 1), new(0.96f, 0.42f), new(0.54f, 0.42f), new(0.72f, 0)];

    /// <summary>Draws the glyph; the caller owns the returned HICON (DestroyIcon).</summary>
    public static nint Render(TrayGlyph glyph)
    {
        int index = glyph.Size >= 32 ? 3 : glyph.Size >= 24 ? 2 : glyph.Size >= 20 ? 1 : 0;
        Geometry g = Renditions[index];
        int rendition = 16 + index * 4 + (index == 3 ? 4 : 0);
        int size = Math.Max(glyph.Size, rendition);
        float offset = (size - rendition) / 2;
        Palette k = glyph.LightTaskbar ? ForLightTaskbar : ForDarkTaskbar;
        bool on = glyph.Connected;
        int level = Math.Clamp(glyph.Level, 0, 100);
        bool mic = on && glyph.Mic;

        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.TranslateTransform(offset, offset);
            var ring = new RectangleF(g.C - g.R, g.C - g.R, 2 * g.R, 2 * g.R);
            using (var track = new Pen(k.Track, g.W))
            {
                graphics.DrawEllipse(track, ring);
            }
            if (on && level > 0)
            {
                Color arcColor = glyph.Charging ? k.Green : level < 10 ? k.Red : k.Ink;
                float circumference = 2 * MathF.PI * g.R;
                float length = Math.Max(circumference * level / 100, g.W);
                using var arc = new Pen(arcColor, g.W) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                if (length >= circumference)
                {
                    graphics.DrawEllipse(arc, ring);
                }
                else
                {
                    graphics.DrawArc(arc, ring, -90, 360 * length / circumference);
                }
            }
            Color ink = on ? k.Ink : k.Faint;
            if (on && glyph.Charging)
            {
                using var brush = new SolidBrush(k.Green);
                var points = new PointF[Bolt.Length];
                for (int i = 0; i < Bolt.Length; i++)
                {
                    points[i] = new PointF(g.BoltX + Bolt[i].X * g.BoltW, g.BoltY + Bolt[i].Y * g.BoltH);
                }
                graphics.FillPolygon(brush, points);
            }
            else
            {
                DrawDevice(graphics, g, glyph.Device, ink);
            }
        }
        if (mic)
        {
            using var hole = new GraphicsPath();
            float r = g.DotR + g.Gap;
            hole.AddEllipse(offset + g.DotX - r, offset + g.DotY - r, 2 * r, 2 * r);
            Erase(bitmap, hole);
            using Graphics graphics = Graphics.FromImage(bitmap);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using var dot = new SolidBrush(k.Orange);
            graphics.FillEllipse(dot, offset + g.DotX - g.DotR, offset + g.DotY - g.DotR, 2 * g.DotR, 2 * g.DotR);
        }
        return ToIcon(bitmap);
    }

    private static void DrawDevice(Graphics graphics, Geometry g, TrayDevice device, Color ink)
    {
        using var brush = new SolidBrush(ink);
        switch (device)
        {
            case TrayDevice.Case:
            {
                // One rounded body with the lid seam cut out (even-odd).
                using var path = new GraphicsPath(FillMode.Alternate);
                AddRoundedRect(path, g.CaseX, g.CaseY, g.CaseW, g.CaseH, g.CaseRadius);
                path.AddRectangle(new RectangleF(g.CaseX, g.SeamY, g.CaseW, g.SeamH));
                graphics.FillPath(brush, path);
                break;
            }
            case TrayDevice.Max:
            {
                using (var band = new Pen(ink, g.BandW) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    graphics.DrawArc(band, g.C - g.BandR, g.BandY - g.BandR, 2 * g.BandR, 2 * g.BandR, 180, 180);
                }
                using var cups = new GraphicsPath(FillMode.Winding);
                AddRoundedRect(cups, g.CupLeftX, g.CupY, g.CupW, g.CupH, g.CupW * 0.42f);
                AddRoundedRect(cups, g.CupRightX, g.CupY, g.CupW, g.CupH, g.CupW * 0.42f);
                graphics.FillPath(brush, cups);
                break;
            }
            default:
            {
                // Heads and stems in one clockwise path, so overlaps unite under the winding rule.
                using var buds = new GraphicsPath(FillMode.Winding);
                AddCircle(buds, g.HeadLeftX, g.HeadY, g.HeadR);
                AddRoundedRect(buds, g.StemLeftX, g.HeadY, g.StemW, g.StemBottom - g.HeadY, g.StemW / 2);
                AddCircle(buds, g.HeadRightX, g.HeadY, g.HeadR);
                AddRoundedRect(buds, g.StemRightX, g.HeadY, g.StemW, g.StemBottom - g.HeadY, g.StemW / 2);
                graphics.FillPath(brush, buds);
                break;
            }
        }
    }

    private static void AddCircle(GraphicsPath path, float cx, float cy, float r)
    {
        path.StartFigure();
        path.AddArc(cx - r, cy - r, 2 * r, 2 * r, 0, 360);
        path.CloseFigure();
    }

    // Clockwise, like AddCircle.
    private static void AddRoundedRect(GraphicsPath path, float x, float y, float w, float h, float r)
    {
        r = Math.Min(r, Math.Min(w / 2, h / 2));
        float d = 2 * r;
        path.StartFigure();
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
    }

    // Multiplies alpha by (1 - antialiased coverage of hole), like the design's SVG mask.
    private static void Erase(Bitmap bitmap, GraphicsPath hole)
    {
        int size = bitmap.Width;
        using var mask = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(mask))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.FillPath(Brushes.White, hole);
        }
        var area = new Rectangle(0, 0, size, size);
        BitmapData target = bitmap.LockBits(area, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        BitmapData source = mask.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        for (int y = 0; y < size; y++)
        {
            byte* t = (byte*)target.Scan0 + y * target.Stride;
            byte* s = (byte*)source.Scan0 + y * source.Stride;
            for (int x = 0; x < size; x++)
            {
                t[x * 4 + 3] = (byte)(t[x * 4 + 3] * (255 - s[x * 4 + 3]) / 255);
            }
        }
        mask.UnlockBits(source);
        bitmap.UnlockBits(target);
    }

    // 32-bit icon with straight alpha (the AND mask is ignored for 32-bit colour bitmaps).
    private static nint ToIcon(Bitmap bitmap)
    {
        int size = bitmap.Width;
        var header = new BitmapInfoHeader
        {
            Size = (uint)sizeof(BitmapInfoHeader),
            Width = size,
            Height = -size,
            Planes = 1,
            BitCount = 32,
        };
        nint color = CreateDIBSection(0, header, 0, out void* bits, 0, 0);
        nint mask = CreateBitmap(size, size, 1, 1, null);
        try
        {
            BitmapData data = bitmap.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            for (int y = 0; y < size; y++)
            {
                Buffer.MemoryCopy((byte*)data.Scan0 + y * data.Stride, (byte*)bits + y * size * 4, size * 4, size * 4);
            }
            bitmap.UnlockBits(data);
            var info = new IconInfo { IsIcon = 1, Mask = mask, Color = color };
            return CreateIconIndirect(info);
        }
        finally
        {
            DeleteObject(color);
            DeleteObject(mask);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int IsIcon;
        public uint HotspotX;
        public uint HotspotY;
        public nint Mask;
        public nint Color;
    }

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateDIBSection(nint hdc, in BitmapInfoHeader header, uint usage, out void* bits, nint section, uint offset);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateBitmap(int width, int height, uint planes, uint bitCount, void* bits);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint handle);

    [LibraryImport("user32.dll")]
    private static partial nint CreateIconIndirect(in IconInfo info);
}
