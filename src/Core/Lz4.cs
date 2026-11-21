using System;

namespace PkgExtractor.Core;

/// <summary>
/// Minimal, allocation-conscious LZ4 block-format decoder.
/// The Wallpaper Engine TEX container stores mipmaps as raw LZ4 *block* streams
/// (no frame header), which is what this implements.
/// </summary>
public static class Lz4
{
    private const int MinMatch = 4;

    /// <summary>Decode a whole LZ4 block into a buffer of <paramref name="expectedSize"/> bytes.</summary>
    public static byte[] DecompressBlock(ReadOnlySpan<byte> src, int expectedSize)
    {
        if (expectedSize < 0) throw new ArgumentOutOfRangeException(nameof(expectedSize));

        var dst = new byte[expectedSize];
        var written = DecompressBlock(src, dst);
        if (written != expectedSize)
            Array.Resize(ref dst, written);
        return dst;
    }

    /// <summary>Decode an LZ4 block into <paramref name="dst"/>, returning the number of bytes written.</summary>
    public static int DecompressBlock(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        var sp = 0;
        var dp = 0;
        var srcLen = src.Length;
        var dstLen = dst.Length;

        while (true)
        {
            if (sp >= srcLen) break;

            var token = src[sp++];

            // ---- literals
            var literalLength = token >> 4;
            if (literalLength == 15)
            {
                int b;
                do
                {
                    if (sp >= srcLen) throw new InvalidDataException("LZ4: truncated literal length.");
                    b = src[sp++];
                    literalLength += b;
                } while (b == 255);
            }

            if (literalLength > 0)
            {
                if (sp + literalLength > srcLen)
                    throw new InvalidDataException("LZ4: literal run exceeds input.");
                if (dp + literalLength > dstLen)
                    throw new InvalidDataException("LZ4: literal run exceeds output buffer.");

                src.Slice(sp, literalLength).CopyTo(dst.Slice(dp));
                sp += literalLength;
                dp += literalLength;
            }

            // Last sequence carries literals only.
            if (sp >= srcLen) break;

            // ---- match
            if (sp + 2 > srcLen) throw new InvalidDataException("LZ4: truncated match offset.");
            var offset = src[sp] | (src[sp + 1] << 8);
            sp += 2;

            if (offset == 0) throw new InvalidDataException("LZ4: zero match offset.");
            if (offset > dp) throw new InvalidDataException("LZ4: match offset points before output start.");

            var matchLength = (token & 0x0F) + MinMatch;
            if ((token & 0x0F) == 15)
            {
                int b;
                do
                {
                    if (sp >= srcLen) throw new InvalidDataException("LZ4: truncated match length.");
                    b = src[sp++];
                    matchLength += b;
                } while (b == 255);
            }

            if (dp + matchLength > dstLen)
                throw new InvalidDataException("LZ4: match run exceeds output buffer.");

            var matchPos = dp - offset;
            // Overlapping copy is intentional and required by the format.
            for (var i = 0; i < matchLength; i++)
                dst[dp + i] = dst[matchPos + i];
            dp += matchLength;
        }

        return dp;
    }

    private sealed class InvalidDataException : Exception
    {
        public InvalidDataException(string message) : base(message) { }
    }
}
