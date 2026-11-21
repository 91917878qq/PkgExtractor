using System;

namespace PkgExtractor.Core;

public enum DxtKind
{
    Dxt1,
    Dxt3,
    Dxt5,
}

/// <summary>
/// S3TC (DXT1 / DXT3 / DXT5) block decompressor producing tightly packed RGBA8888.
/// Algorithm ported from libsquish's reference decoder.
/// </summary>
public static class Dxt
{
    public static byte[] Decompress(int width, int height, ReadOnlySpan<byte> data, DxtKind kind)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException("DXT: non-positive dimensions.");

        var rgba = new byte[checked(width * height * 4)];
        var bytesPerBlock = kind == DxtKind.Dxt1 ? 8 : 16;
        var block = new byte[64];

        var sourcePos = 0;
        for (var y = 0; y < height; y += 4)
        {
            for (var x = 0; x < width; x += 4)
            {
                if (sourcePos + bytesPerBlock > data.Length)
                    return rgba; // truncated input: leave remaining pixels transparent

                data.Slice(sourcePos, bytesPerBlock).CopyTo(block);
                DecompressBlockInto(rgba, width, height, block, x, y, kind);
                sourcePos += bytesPerBlock;
            }
        }

        return rgba;
    }

    private static void DecompressBlockInto(byte[] rgba, int width, int height, byte[] block, int blockX, int blockY, DxtKind kind)
    {
        var pixels = new byte[64]; // 16 px * RGBA
        DecodeBlock(pixels, block, kind);

        for (var py = 0; py < 4; py++)
        {
            var sy = blockY + py;
            if (sy >= height) continue;

            for (var px = 0; px < 4; px++)
            {
                var sx = blockX + px;
                if (sx >= width) continue;

                var src = (py * 4 + px) * 4;
                var dst = (sy * width + sx) * 4;

                rgba[dst + 0] = pixels[src + 0];
                rgba[dst + 1] = pixels[src + 1];
                rgba[dst + 2] = pixels[src + 2];
                rgba[dst + 3] = pixels[src + 3];
            }
        }
    }

    private static void DecodeBlock(byte[] outRgba, byte[] block, DxtKind kind)
    {
        var colorOffset = kind == DxtKind.Dxt1 ? 0 : 8;
        DecodeColor(outRgba, block, colorOffset, kind == DxtKind.Dxt1);

        if (kind == DxtKind.Dxt3) DecodeAlphaDxt3(outRgba, block);
        else if (kind == DxtKind.Dxt5) DecodeAlphaDxt5(outRgba, block);
    }

    private static void DecodeAlphaDxt3(byte[] rgba, byte[] block)
    {
        for (var i = 0; i < 8; i++)
        {
            int quant = block[i];
            var lo = (byte)(quant & 0x0F);
            var hi = (byte)(quant & 0xF0);

            rgba[8 * i + 3] = (byte)(lo | (lo << 4));
            rgba[8 * i + 7] = (byte)(hi | (hi >> 4));
        }
    }

    private static void DecodeAlphaDxt5(byte[] rgba, byte[] block)
    {
        int alpha0 = block[0];
        int alpha1 = block[1];

        var codes = new byte[8];
        codes[0] = (byte)alpha0;
        codes[1] = (byte)alpha1;

        if (alpha0 <= alpha1)
        {
            for (var i = 1; i < 5; i++)
                codes[1 + i] = (byte)(((5 - i) * alpha0 + i * alpha1) / 5);
            codes[6] = 0;
            codes[7] = 255;
        }
        else
        {
            for (var i = 1; i < 7; i++)
                codes[i + 1] = (byte)(((7 - i) * alpha0 + i * alpha1) / 7);
        }

        var indices = new byte[16];
        var blockSrcPos = 2;
        var indicesPos = 0;

        for (var i = 0; i < 2; i++)
        {
            var value = 0;
            for (var j = 0; j < 3; j++)
                value |= block[blockSrcPos++] << (8 * j);

            for (var j = 0; j < 8; j++)
                indices[indicesPos++] = (byte)((value >> (3 * j)) & 0x07);
        }

        for (var i = 0; i < 16; i++)
            rgba[4 * i + 3] = codes[indices[i]];
    }

    private static void DecodeColor(byte[] rgba, byte[] block, int blockIndex, bool isDxt1)
    {
        var codes = new byte[16];
        var a = Unpack565(block, blockIndex, 0, codes, 0);
        var b = Unpack565(block, blockIndex, 2, codes, 4);

        for (var i = 0; i < 3; i++)
        {
            int c = codes[i];
            int d = codes[4 + i];

            if (isDxt1 && a <= b)
            {
                codes[8 + i] = (byte)((c + d) / 2);
                codes[12 + i] = 0;
            }
            else
            {
                codes[8 + i] = (byte)((2 * c + d) / 3);
                codes[12 + i] = (byte)((c + 2 * d) / 3);
            }
        }

        codes[8 + 3] = 255;
        codes[12 + 3] = (byte)(isDxt1 && a <= b ? 0 : 255);

        var indices = new byte[16];
        for (var i = 0; i < 4; i++)
        {
            int packed = block[blockIndex + 4 + i];

            indices[0 + i * 4] = (byte)(packed & 0x3);
            indices[1 + i * 4] = (byte)((packed >> 2) & 0x3);
            indices[2 + i * 4] = (byte)((packed >> 4) & 0x3);
            indices[3 + i * 4] = (byte)((packed >> 6) & 0x3);
        }

        for (var i = 0; i < 16; i++)
        {
            var offset = 4 * indices[i];
            rgba[4 * i + 0] = codes[offset + 0];
            rgba[4 * i + 1] = codes[offset + 1];
            rgba[4 * i + 2] = codes[offset + 2];
            rgba[4 * i + 3] = codes[offset + 3];
        }
    }

    private static int Unpack565(byte[] block, int blockIndex, int packedOffset, byte[] colour, int colourOffset)
    {
        var value = block[blockIndex + packedOffset] | (block[blockIndex + 1 + packedOffset] << 8);

        var red = (byte)((value >> 11) & 0x1F);
        var green = (byte)((value >> 5) & 0x3F);
        var blue = (byte)(value & 0x1F);

        colour[0 + colourOffset] = (byte)((red << 3) | (red >> 2));
        colour[1 + colourOffset] = (byte)((green << 2) | (green >> 4));
        colour[2 + colourOffset] = (byte)((blue << 3) | (blue >> 2));
        colour[3 + colourOffset] = 255;

        return value;
    }
}
