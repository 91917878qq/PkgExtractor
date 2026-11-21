using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace IconGen;

internal static class Program
{
    private const int SS = 8; // supersampling factor

    private static int Main(string[] args)
    {
        var outIco = args.Length > 0 ? args[0] : "app.ico";
        var outPng = args.Length > 1 ? args[1] : "preview.png";

        int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
        var images = new List<(int Size, byte[] Png)>();

        foreach (var s in sizes)
        {
            var R = s * SS;
            var rgba = Render(R);
            var small = Downsample(rgba, R, s, SS);
            images.Add((s, Png.Encode(small, s, s)));
            Console.WriteLine($"rendered {s}x{s}");
        }

        // big preview for visual inspection
        {
            const int P = 512, F = 4;
            var rgba = Render(P * F);
            var px = Downsample(rgba, P * F, P, F);
            File.WriteAllBytes(outPng, Png.Encode(px, P, P));
            Console.WriteLine($"wrote preview {outPng}");
        }

        WriteIco(outIco, images);
        Console.WriteLine($"wrote icon {outIco} ({new FileInfo(outIco).Length} bytes)");
        return 0;
    }

    // ---------------------------------------------------------------- geometry

    private static double RoundRectSdf(double px, double py, double x0, double y0, double x1, double y1, double r)
    {
        var cx = (x0 + x1) * 0.5;
        var cy = (y0 + y1) * 0.5;
        var hw = (x1 - x0) * 0.5;
        var hh = (y1 - y0) * 0.5;
        var dx = Math.Abs(px - cx) - (hw - r);
        var dy = Math.Abs(py - cy) - (hh - r);
        var ax = Math.Max(dx, 0);
        var ay = Math.Max(dy, 0);
        return Math.Sqrt(ax * ax + ay * ay) + Math.Min(Math.Max(dx, dy), 0) - r;
    }

    private static double Coverage(double sdf)
    {
        // 1px-wide antialiased edge
        var c = 0.5 - sdf;
        return c <= 0 ? 0 : c >= 1 ? 1 : c;
    }

    private static bool InTriangle(double px, double py, double ax, double ay, double bx, double by, double cx, double cy)
    {
        double d1 = (px - bx) * (ay - by) - (ax - bx) * (py - by);
        double d2 = (px - cx) * (by - cy) - (bx - cx) * (py - cy);
        double d3 = (px - ax) * (cy - ay) - (cx - ax) * (py - ay);
        var neg = d1 < 0 || d2 < 0 || d3 < 0;
        var pos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(neg && pos);
    }

    // ---------------------------------------------------------------- painting

    private static (double R, double G, double B) TileColor(double px, double py, double R)
    {
        var x0 = 0.045 * R;
        var y0 = 0.045 * R;
        var w = 0.91 * R;
        var h = 0.91 * R;

        var u = (px - x0) / w;
        var v = (py - y0) / h;
        var t = Math.Clamp(u * 0.42 + v * 0.58, 0.0, 1.0);

        // purple -> cyan diagonal gradient
        var r = Lerp(0.545, 0.078, t);
        var g = Lerp(0.353, 0.741, t);
        var b = Lerp(0.949, 0.910, t);

        // ambient top-left highlight
        var hx = (px - 0.26 * R) / (0.62 * R);
        var hy = (py - 0.20 * R) / (0.62 * R);
        var hd = Math.Sqrt(hx * hx + hy * hy);
        if (hd < 1.0)
        {
            var k = (1.0 - hd) * (1.0 - hd) * 0.30;
            r = Lerp(r, 1.0, k);
            g = Lerp(g, 1.0, k);
            b = Lerp(b, 1.0, k);
        }

        // vertical depth shading
        var depth = 1.0 - 0.16 * v;
        r *= depth;
        g *= depth;
        b *= depth;

        return (r, g, b);
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static void Blend(byte[] buf, int i, double r, double g, double b, double a)
    {
        if (a <= 0) return;
        if (a > 1) a = 1;

        var da = buf[i + 3] / 255.0;
        var outA = a + da * (1 - a);
        if (outA <= 0)
        {
            buf[i] = buf[i + 1] = buf[i + 2] = buf[i + 3] = 0;
            return;
        }

        buf[i + 0] = ToByte((r * a + (buf[i + 0] / 255.0) * da * (1 - a)) / outA);
        buf[i + 1] = ToByte((g * a + (buf[i + 1] / 255.0) * da * (1 - a)) / outA);
        buf[i + 2] = ToByte((b * a + (buf[i + 2] / 255.0) * da * (1 - a)) / outA);
        buf[i + 3] = ToByte(outA);
    }

    private static byte ToByte(double v)
    {
        var i = (int)Math.Round(v * 255.0);
        return (byte)(i < 0 ? 0 : i > 255 ? 255 : i);
    }

    private static byte[] Render(int R)
    {
        var buf = new byte[R * R * 4];

        double tx0 = 0.045 * R, ty0 = 0.045 * R, tx1 = 0.955 * R, ty1 = 0.955 * R;
        double trad = 0.225 * R;

        // ---- tile background
        for (var y = 0; y < R; y++)
        {
            var py = y + 0.5;
            for (var x = 0; x < R; x++)
            {
                var px = x + 0.5;
                var cov = Coverage(RoundRectSdf(px, py, tx0, ty0, tx1, ty1, trad));
                if (cov <= 0) continue;
                var c = TileColor(px, py, R);
                Blend(buf, (y * R + x) * 4, c.R, c.G, c.B, cov);
            }
        }

        // ---- inner rim light
        for (var y = 0; y < R; y++)
        {
            var py = y + 0.5;
            for (var x = 0; x < R; x++)
            {
                var px = x + 0.5;
                var outer = RoundRectSdf(px, py, tx0, ty0, tx1, ty1, trad);
                var inner = RoundRectSdf(px, py, tx0 + 0.010 * R, ty0 + 0.010 * R, tx1 - 0.010 * R, ty1 - 0.010 * R, trad);
                var ring = Coverage(-outer) * Coverage(inner);
                if (ring <= 0) continue;
                Blend(buf, (y * R + x) * 4, 1, 1, 1, ring * 0.14);
            }
        }

        // ---- box ring (open container)
        double bx0 = 0.270 * R, by0 = 0.398 * R, bx1 = 0.730 * R, by1 = 0.800 * R;
        double br = 0.080 * R;
        var th = 0.055 * R;

        for (var y = 0; y < R; y++)
        {
            var py = y + 0.5;
            for (var x = 0; x < R; x++)
            {
                var px = x + 0.5;
                var outer = RoundRectSdf(px, py, bx0, by0, bx1, by1, br);
                var inner = RoundRectSdf(px, py, bx0 + th, by0 + th, bx1 - th, by1 - th, Math.Max(1, br - th));
                var cov = Coverage(outer) * (1 - Coverage(inner));
                if (cov <= 0) continue;
                Blend(buf, (y * R + x) * 4, 1, 1, 1, cov * 0.97);
            }
        }

        // ---- arrow (shaft + head), punched over the box
        double sx0 = 0.4680 * R, sy0 = 0.130 * R, sx1 = 0.5320 * R, sy1 = 0.520 * R;
        var sr = 0.030 * R;

        double hxA = 0.380 * R, hyA = 0.500 * R;
        double hxB = 0.620 * R, hyB = 0.500 * R;
        double hxC = 0.500 * R, hyC = 0.690 * R;

        for (var y = 0; y < R; y++)
        {
            var py = y + 0.5;
            for (var x = 0; x < R; x++)
            {
                var px = x + 0.5;
                var shaft = Coverage(RoundRectSdf(px, py, sx0, sy0, sx1, sy1, sr));
                var head = InTriangle(px, py, hxA, hyA, hxB, hyB, hxC, hyC) ? 1.0 : 0.0;
                var cov = Math.Max(shaft, head);
                if (cov <= 0) continue;
                Blend(buf, (y * R + x) * 4, 1, 1, 1, cov);
            }
        }

        return buf;
    }

    // ---------------------------------------------------------------- post

    private static byte[] Downsample(byte[] src, int srcSize, int dstSize, int factor)
    {
        var dst = new byte[dstSize * dstSize * 4];
        var area = factor * factor;

        for (var y = 0; y < dstSize; y++)
        {
            for (var x = 0; x < dstSize; x++)
            {
                double a = 0, r = 0, g = 0, b = 0;
                for (var sy = 0; sy < factor; sy++)
                {
                    var row = (y * factor + sy) * srcSize;
                    for (var sx = 0; sx < factor; sx++)
                    {
                        var i = (row + x * factor + sx) * 4;
                        var sa = src[i + 3] / 255.0;
                        a += sa;
                        r += src[i + 0] * sa;
                        g += src[i + 1] * sa;
                        b += src[i + 2] * sa;
                    }
                }

                var di = (y * dstSize + x) * 4;
                var oa = a / area;
                if (oa <= 0.0001)
                {
                    dst[di] = dst[di + 1] = dst[di + 2] = dst[di + 3] = 0;
                    continue;
                }

                dst[di + 0] = ToByte((r / area) / 255.0 / oa);
                dst[di + 1] = ToByte((g / area) / 255.0 / oa);
                dst[di + 2] = ToByte((b / area) / 255.0 / oa);
                dst[di + 3] = ToByte(oa);
            }
        }

        return dst;
    }

    // ---------------------------------------------------------------- PNG

    private static class Png
    {
        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }

        private static uint Crc32(byte[] data, int offset, int count)
        {
            var c = 0xFFFFFFFFu;
            for (var i = 0; i < count; i++)
                c = CrcTable[(c ^ data[offset + i]) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }

        private static void WriteChunk(Stream s, string type, byte[] data)
        {
            var len = data.Length;
            s.WriteByte((byte)(len >> 24));
            s.WriteByte((byte)(len >> 16));
            s.WriteByte((byte)(len >> 8));
            s.WriteByte((byte)len);

            var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
            s.Write(typeBytes, 0, 4);
            s.Write(data, 0, data.Length);

            var crcInput = new byte[4 + data.Length];
            Buffer.BlockCopy(typeBytes, 0, crcInput, 0, 4);
            Buffer.BlockCopy(data, 0, crcInput, 4, data.Length);
            var crc = Crc32(crcInput, 0, crcInput.Length);
            s.WriteByte((byte)(crc >> 24));
            s.WriteByte((byte)(crc >> 16));
            s.WriteByte((byte)(crc >> 8));
            s.WriteByte((byte)crc);
        }

        public static byte[] Encode(byte[] rgba, int width, int height)
        {
            using var ms = new MemoryStream();

            ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);

            var ihdr = new byte[13];
            ihdr[0] = (byte)(width >> 24); ihdr[1] = (byte)(width >> 16); ihdr[2] = (byte)(width >> 8); ihdr[3] = (byte)width;
            ihdr[4] = (byte)(height >> 24); ihdr[5] = (byte)(height >> 16); ihdr[6] = (byte)(height >> 8); ihdr[7] = (byte)height;
            ihdr[8] = 8;  // bit depth
            ihdr[9] = 6;  // RGBA
            ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
            WriteChunk(ms, "IHDR", ihdr);

            var raw = new byte[height * (width * 4 + 1)];
            for (var y = 0; y < height; y++)
            {
                raw[y * (width * 4 + 1)] = 0; // filter none
                Buffer.BlockCopy(rgba, y * width * 4, raw, y * (width * 4 + 1) + 1, width * 4);
            }

            using var compressed = new MemoryStream();
            using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
                z.Write(raw, 0, raw.Length);
            WriteChunk(ms, "IDAT", compressed.ToArray());
            WriteChunk(ms, "IEND", Array.Empty<byte>());

            return ms.ToArray();
        }
    }

    // ---------------------------------------------------------------- ICO

    private static void WriteIco(string path, List<(int Size, byte[] Png)> images)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs);

        w.Write((ushort)0);            // reserved
        w.Write((ushort)1);            // type = icon
        w.Write((ushort)images.Count);

        var offset = 6 + 16 * images.Count;
        foreach (var (size, png) in images)
        {
            w.Write((byte)(size >= 256 ? 0 : size)); // width
            w.Write((byte)(size >= 256 ? 0 : size)); // height
            w.Write((byte)0);                        // palette
            w.Write((byte)0);                        // reserved
            w.Write((ushort)1);                      // planes
            w.Write((ushort)32);                     // bpp
            w.Write(png.Length);
            w.Write(offset);
            offset += png.Length;
        }

        foreach (var (_, png) in images)
            w.Write(png);
    }
}
