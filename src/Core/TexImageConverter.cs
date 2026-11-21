using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PkgExtractor.Core;

/// <summary>Converts parsed TEX data into real image / video bytes (PNG, GIF, MP4, ...).</summary>
public static class TexImageConverter
{
    /// <summary>
    /// Undo LZ4 and DXT so that <see cref="TexMipmap.Bytes"/> holds raw pixels, when the payload is
    /// raw-pixel based. Image / video payloads (embedded JPEG, PNG, MP4, ...) are left untouched —
    /// their bytes are already a finished file and <see cref="TexMipmap.Format"/> already says so.
    /// </summary>
    public static void Decompress(TexMipmap mip, TexFormat format)
    {
        if (mip.HadLz4)
        {
            var expected = mip.DecompressedBytesCount;
            if (expected <= 0)
            {
                // Unknown size: grow as needed using LZ4's own accounting.
                var dst = new byte[Math.Max(64, mip.Bytes.Length * 4)];
                var written = Lz4.DecompressBlock(mip.Bytes, dst);
                Array.Resize(ref dst, written);
                mip.Bytes = dst;
            }
            else
            {
                var dst = new byte[expected];
                Lz4.DecompressBlock(mip.Bytes, dst);
                mip.Bytes = dst;
            }
        }

        // The reader already assigned a container-derived format; only fall back to the header
        // encoding when the container gave us nothing usable.
        if (mip.Format == MipmapFormat.Invalid)
            mip.Format = format switch
            {
                TexFormat.DXT1 => MipmapFormat.CompressedDXT1,
                TexFormat.DXT3 => MipmapFormat.CompressedDXT3,
                TexFormat.DXT5 => MipmapFormat.CompressedDXT5,
                TexFormat.R8 => MipmapFormat.R8,
                TexFormat.RG88 => MipmapFormat.RG88,
                _ => MipmapFormat.RGBA8888,
            };

        // Finished-file payloads stay as they are.
        if (mip.Format.IsImage() || mip.Format == MipmapFormat.VideoMp4)
            return;

        switch (mip.Format)
        {
            case MipmapFormat.CompressedDXT5:
                mip.Bytes = Dxt.Decompress(mip.Width, mip.Height, mip.Bytes, DxtKind.Dxt5);
                mip.Format = MipmapFormat.RGBA8888;
                break;
            case MipmapFormat.CompressedDXT3:
                mip.Bytes = Dxt.Decompress(mip.Width, mip.Height, mip.Bytes, DxtKind.Dxt3);
                mip.Format = MipmapFormat.RGBA8888;
                break;
            case MipmapFormat.CompressedDXT1:
                mip.Bytes = Dxt.Decompress(mip.Width, mip.Height, mip.Bytes, DxtKind.Dxt1);
                mip.Format = MipmapFormat.RGBA8888;
                break;
        }
    }

    /// <summary>True when the raw bytes start with a known MP4 ftyp brand.</summary>
    public static bool IsMp4(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12) return false;
        foreach (var brand in TexReader.VideoFtypBrands)
        {
            var ok = true;
            for (var i = 0; i < brand.Length; i++)
            {
                if (bytes[4 + i] != (byte)brand[i]) { ok = false; break; }
            }
            if (ok) return true;
        }
        return false;
    }

    /// <summary>
    /// True when the mipmap payload is a finished image/video file (embedded JPEG, PNG, MP4, ...)
    /// rather than raw or block-compressed pixels. Such bytes are written out verbatim.
    /// </summary>
    public static bool IsEmbeddedFile(TexMipmap mip) =>
        mip.Format.IsImage() || mip.Format == MipmapFormat.VideoMp4;

    /// <summary>File extension (with dot) for an embedded payload, e.g. ".jpg".</summary>
    public static string EmbeddedExtension(TexMipmap mip) => mip.Format.ToFileExtension();

    /// <summary>
    /// Convert one mipmap to PNG bytes. Returns null when the mipmap cannot be expressed as a plain image.
    /// </summary>
    public static byte[]? ToPng(TexMipmap mip, TexFormat format, int targetWidth, int targetHeight)
    {
        if (mip.Width <= 0 || mip.Height <= 0) return null;

        byte[] rgba = mip.Format switch
        {
            MipmapFormat.CompressedDXT5 => Dxt.Decompress(mip.Width, mip.Height, mip.Bytes, DxtKind.Dxt5),
            MipmapFormat.CompressedDXT3 => Dxt.Decompress(mip.Width, mip.Height, mip.Bytes, DxtKind.Dxt3),
            MipmapFormat.CompressedDXT1 => Dxt.Decompress(mip.Width, mip.Height, mip.Bytes, DxtKind.Dxt1),
            MipmapFormat.RGBA8888 => NormalizeRgba(mip),
            MipmapFormat.RG88 => ExpandRg88(mip),
            MipmapFormat.R8 => ExpandR8(mip),
            _ => Array.Empty<byte>(),
        };

        if (rgba.Length == 0) return null;

        var w = mip.Width;
        var h = mip.Height;

        // Crop oversized mipmaps down to the declared image size (matches RePKG behaviour).
        if (targetWidth > 0 && targetHeight > 0 && (w != targetWidth || h != targetHeight)
            && w >= targetWidth && h >= targetHeight)
        {
            rgba = Crop(rgba, w, h, targetWidth, targetHeight);
            w = targetWidth;
            h = targetHeight;
        }

        return EncodePng(rgba, w, h);
    }

    private static byte[] NormalizeRgba(TexMipmap mip)
    {
        var need = mip.Width * mip.Height * 4;
        if (mip.Bytes.Length >= need) return mip.Bytes;

        var padded = new byte[need];
        Buffer.BlockCopy(mip.Bytes, 0, padded, 0, mip.Bytes.Length);
        return padded;
    }

    private static byte[] ExpandRg88(TexMipmap mip)
    {
        var pixels = mip.Width * mip.Height;
        var src = mip.Bytes;
        var dst = new byte[pixels * 4];
        for (var i = 0; i < pixels; i++)
        {
            var s = i * 2;
            var d = i * 4;
            var r = s < src.Length ? src[s] : (byte)0;
            var g = s + 1 < src.Length ? src[s + 1] : (byte)0;
            dst[d] = r;
            dst[d + 1] = g;
            dst[d + 2] = 0;
            dst[d + 3] = 255;
        }
        return dst;
    }

    private static byte[] ExpandR8(TexMipmap mip)
    {
        var pixels = mip.Width * mip.Height;
        var src = mip.Bytes;
        var dst = new byte[pixels * 4];
        for (var i = 0; i < pixels; i++)
        {
            var v = i < src.Length ? src[i] : (byte)0;
            var d = i * 4;
            dst[d] = v;
            dst[d + 1] = v;
            dst[d + 2] = v;
            dst[d + 3] = 255;
        }
        return dst;
    }

    private static byte[] Crop(byte[] rgba, int sw, int sh, int dw, int dh)
    {
        var dst = new byte[dw * dh * 4];
        for (var y = 0; y < dh; y++)
        {
            var srcRow = y * sw * 4;
            var dstRow = y * dw * 4;
            if (srcRow + dw * 4 > rgba.Length) break;
            Buffer.BlockCopy(rgba, srcRow, dst, dstRow, dw * 4);
        }
        return dst;
    }

    public static byte[] EncodePng(byte[] rgba, int width, int height)
    {
        var stride = width * 4;
        var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null,
            ToBgra(rgba), stride);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));

        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <summary>RGBA byte order -> BGRA byte order expected by PixelFormats.Bgra32.</summary>
    private static byte[] ToBgra(byte[] rgba)
    {
        var dst = new byte[rgba.Length];
        for (var i = 0; i + 3 < rgba.Length; i += 4)
        {
            dst[i] = rgba[i + 2];     // B
            dst[i + 1] = rgba[i + 1]; // G
            dst[i + 2] = rgba[i];     // R
            dst[i + 3] = rgba[i + 3]; // A
        }
        return dst;
    }

    /// <summary>
    /// Build an animated GIF from the TEX frame table. Returns null when the file carries no frame info.
    /// </summary>
    public static byte[]? ToGif(TexFile tex)
    {
        var info = tex.FrameInfo;
        if (info is null || info.Frames.Count == 0) return null;

        var canvasW = info.GifWidth > 0 ? info.GifWidth : tex.ImageWidth;
        var canvasH = info.GifHeight > 0 ? info.GifHeight : tex.ImageHeight;
        if (canvasW <= 0 || canvasH <= 0) return null;

        // Pre-render every referenced image once.
        var rendered = new Dictionary<int, (byte[] Rgba, int W, int H)>();

        foreach (var frame in info.Frames)
        {
            if (rendered.ContainsKey(frame.ImageId)) continue;
            if (frame.ImageId < 0 || frame.ImageId >= tex.Images.Count) continue;

            var img = tex.Images[frame.ImageId];
            if (img.Mipmaps.Count == 0) continue;

            var mip = img.Mipmaps[0];
            var rgba = mip.Format switch
            {
                MipmapFormat.CompressedDXT5 => Dxt.Decompress(mip.Width, mip.Height, mip.Bytes, DxtKind.Dxt5),
                MipmapFormat.CompressedDXT3 => Dxt.Decompress(mip.Width, mip.Height, mip.Bytes, DxtKind.Dxt3),
                MipmapFormat.CompressedDXT1 => Dxt.Decompress(mip.Width, mip.Height, mip.Bytes, DxtKind.Dxt1),
                MipmapFormat.RGBA8888 => NormalizeRgba(mip),
                MipmapFormat.RG88 => ExpandRg88(mip),
                MipmapFormat.R8 => ExpandR8(mip),
                _ => Array.Empty<byte>(),
            };

            if (rgba.Length > 0)
                rendered[frame.ImageId] = (rgba, mip.Width, mip.Height);
        }

        if (rendered.Count == 0) return null;

        var encoder = new GifBitmapEncoder();

        foreach (var frame in info.Frames)
        {
            if (!rendered.TryGetValue(frame.ImageId, out var src)) continue;

            var canvas = new byte[canvasW * canvasH * 4];

            var fx = (int)Math.Round(frame.X);
            var fy = (int)Math.Round(frame.Y);
            var fw = frame.Width > 0 ? (int)Math.Round(frame.Width) : src.W;
            var fh = frame.HeightX > 0 ? (int)Math.Round(frame.HeightX) : src.H;

            fw = Math.Min(fw, src.W);
            fh = Math.Min(fh, src.H);

            for (var y = 0; y < fh; y++)
            {
                var cy = fy + y;
                if (cy < 0 || cy >= canvasH) continue;
                for (var x = 0; x < fw; x++)
                {
                    var cx = fx + x;
                    if (cx < 0 || cx >= canvasW) continue;

                    var sIdx = (y * src.W + x) * 4;
                    var dIdx = (cy * canvasW + cx) * 4;
                    if (sIdx + 3 >= src.Rgba.Length) continue;

                    canvas[dIdx] = src.Rgba[sIdx];
                    canvas[dIdx + 1] = src.Rgba[sIdx + 1];
                    canvas[dIdx + 2] = src.Rgba[sIdx + 2];
                    canvas[dIdx + 3] = src.Rgba[sIdx + 3];
                }
            }

            var bmp = BitmapSource.Create(canvasW, canvasH, 96, 96, PixelFormats.Bgra32, null,
                ToBgra(canvas), canvasW * 4);

            var meta = new BitmapMetadata("gif");
            var delay = Math.Max(1, (int)Math.Round(frame.Frametime <= 0 ? 10 : frame.Frametime));
            meta.SetQuery("/grctlext/Delay", (ushort)delay);

            encoder.Frames.Add(BitmapFrame.Create(bmp, null, meta, null));
        }

        if (encoder.Frames.Count == 0) return null;

        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }
}
