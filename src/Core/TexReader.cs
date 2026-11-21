using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PkgExtractor.Core;

/// <summary>Container-format number of a TEXB block (TEXB0001..TEXB0004).</summary>
public enum TexContainerVersion
{
    V1 = 1,
    V2 = 2,
    V3 = 3,
    V4 = 4,
}

public sealed class TexMipmap
{
    public int Width { get; init; }
    public int Height { get; init; }
    public int DecompressedBytesCount { get; init; }
    public bool HadLz4 { get; init; }
    /// <summary>Stored bytes; replaced with raw pixels after <see cref="TexImageConverter.Decompress"/>.</summary>
    public byte[] Bytes { get; set; } = Array.Empty<byte>();
    /// <summary>Pixel/storage layout after decompression (RGBA8888 / R8 / RG88 / VideoMp4 / image).</summary>
    public MipmapFormat Format { get; set; } = MipmapFormat.Invalid;
}

public sealed class TexImage
{
    public int MipmapCount => Mipmaps.Count;
    public List<TexMipmap> Mipmaps { get; } = new();
}

/// <summary>One animation frame descriptor (TEXS container).</summary>
public sealed class TexFrame
{
    public int ImageId { get; init; }
    public float Frametime { get; init; }
    public float X { get; init; }
    public float Y { get; init; }
    public float Width { get; init; }
    public float HeightX { get; init; }
    public float HeightY { get; init; }
}

public sealed class TexFrameInfo
{
    public string Magic { get; init; } = "";
    public int GifWidth { get; init; }
    public int GifHeight { get; init; }
    public List<TexFrame> Frames { get; } = new();
}

public sealed class TexFile
{
    public string Magic1 { get; init; } = "";
    public string Magic2 { get; init; } = "";

    public TexFormat Format { get; init; }
    public TexFlags Flags { get; init; }
    public int TextureWidth { get; init; }
    public int TextureHeight { get; init; }
    public int ImageWidth { get; init; }
    public int ImageHeight { get; init; }
    public uint UnkInt0 { get; init; }

    public TexContainerVersion Version { get; init; }
    public string ContainerMagic { get; init; } = "";
    public FreeImageFormat FreeImageFormat { get; init; } = FreeImageFormat.Unknown;
    public bool IsVideoMp4 { get; init; }

    public List<TexImage> Images { get; } = new();
    public TexFrameInfo? FrameInfo { get; set; }

    public bool IsGif => (Flags & TexFlags.IsGif) != 0;
    public bool IsVideoTexture => (Flags & TexFlags.IsVideoTexture) != 0;
    public int TotalMipmaps
    {
        get
        {
            var n = 0;
            foreach (var img in Images) n += img.Mipmaps.Count;
            return n;
        }
    }
}

/// <summary>
/// Parser for Wallpaper Engine TEX files (TEXV0005 / TEXI0001 / TEXB000x [+ TEXS000x]).
/// Mirrors RePKG's TexReader / TexHeaderReader / TexImageContainerReader / TexImageReader.
/// </summary>
public static class TexReader
{
    public const string ExpectedMagic1 = "TEXV0005";
    public const string ExpectedMagic2 = "TEXI0001";

    private const int MaxImageCount = 100;
    private const int MaxMipmapCount = 32;
    private const int MaxFrameCount = 100_000;
    private const int MaxMipmapByteCount = 250_000_000;
    private const int MaxNStringLength = 64;

    public static readonly string[] VideoFtypBrands = { "ftypisom", "ftypmsnv", "ftypmp42" };

    public static bool LooksLikeTex(Stream stream)
    {
        var start = stream.Position;
        try
        {
            var m = ReadNString(stream, 16);
            return m == ExpectedMagic1;
        }
        catch
        {
            return false;
        }
        finally
        {
            stream.Position = start;
        }
    }

    public static TexFile Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var magic1 = ReadNString(stream, 16);
        if (magic1 != ExpectedMagic1)
            throw new InvalidDataException($"Invalid TEX header: expected \"{ExpectedMagic1}\", got \"{magic1}\".");

        var magic2 = ReadNString(stream, 16);
        if (magic2 != ExpectedMagic2)
            throw new InvalidDataException($"Invalid TEX header: expected \"{ExpectedMagic2}\", got \"{magic2}\".");

        // --- header ---
        var format = (TexFormat)ReadInt32(stream);
        var flags = (TexFlags)ReadInt32(stream);
        var textureWidth = ReadInt32(stream);
        var textureHeight = ReadInt32(stream);
        var imageWidth = ReadInt32(stream);
        var imageHeight = ReadInt32(stream);
        var unk0 = ReadUInt32(stream);

        // --- image container ---
        var containerMagic = ReadNString(stream, 16);
        if (!containerMagic.StartsWith("TEXB", StringComparison.Ordinal))
            throw new InvalidDataException($"Invalid TEX container magic: \"{containerMagic}\".");

        var versionNumber = ParseTrailingNumber(containerMagic);
        var version = versionNumber switch
        {
            1 => TexContainerVersion.V1,
            2 => TexContainerVersion.V2,
            3 => TexContainerVersion.V3,
            4 => TexContainerVersion.V4,
            _ => throw new InvalidDataException($"Unsupported TEX container version: \"{containerMagic}\"."),
        };

        var freeImageFormat = FreeImageFormat.Unknown;
        var isVideoMp4 = false;

        // NOTE: on-disk order is magic -> imageCount -> version-specific fields.
        var imageCount = ReadInt32(stream);
        if (imageCount < 0 || imageCount > MaxImageCount)
            throw new InvalidDataException($"Unreasonable TEX image count: {imageCount}.");

        if (version == TexContainerVersion.V3)
        {
            freeImageFormat = (FreeImageFormat)ReadInt32(stream);
        }
        else if (version == TexContainerVersion.V4)
        {
            var fmt = (FreeImageFormat)ReadInt32(stream);
            var isMp4 = ReadInt32(stream) == 1;
            if (fmt == FreeImageFormat.Unknown && isMp4)
                fmt = FreeImageFormat.Mp4;
            freeImageFormat = fmt;
            isVideoMp4 = isMp4;

            // RePKG downgrades V4 to V3 when the payload is not MP4 (mipmap layout is V2/V3-shaped).
            if (fmt != FreeImageFormat.Mp4)
                version = TexContainerVersion.V3;
        }

        var tex = new TexFile
        {
            Magic1 = magic1,
            Magic2 = magic2,
            Format = format,
            Flags = flags,
            TextureWidth = textureWidth,
            TextureHeight = textureHeight,
            ImageWidth = imageWidth,
            ImageHeight = imageHeight,
            UnkInt0 = unk0,
            Version = version,
            ContainerMagic = containerMagic,
            FreeImageFormat = freeImageFormat,
            IsVideoMp4 = isVideoMp4,
        };

        // The per-mipmap payload layout comes from the container's FreeImageFormat when it is set
        // (TEXB0003/TEXB0004 can embed a ready-made JPEG/PNG/... instead of raw or DXT pixels).
        var mipmapFormat = ResolveMipmapFormat(freeImageFormat, format);

        for (var i = 0; i < imageCount; i++)
            tex.Images.Add(ReadImage(stream, version, mipmapFormat));

        // --- optional frame info container (GIF) ---
        if (tex.IsGif && stream.Position < stream.Length)
        {
            var save = stream.Position;
            try
            {
                var frameMagic = ReadNString(stream, 16);
                if (frameMagic.StartsWith("TEXS", StringComparison.Ordinal))
                    tex.FrameInfo = ReadFrameInfo(stream, frameMagic);
                else
                    stream.Position = save;
            }
            catch
            {
                stream.Position = save;
            }
        }

        return tex;
    }

    /// <summary>
    /// Decides the stored layout of every mipmap payload: an explicit container image format wins,
    /// otherwise the texture encoding (raw / DXT) from the header applies.
    /// </summary>
    private static MipmapFormat ResolveMipmapFormat(FreeImageFormat imageFormat, TexFormat texFormat)
    {
        if (imageFormat != FreeImageFormat.Unknown)
            return FreeImageFormatToMipmapFormat(imageFormat);

        return texFormat switch
        {
            TexFormat.RGBA8888 => MipmapFormat.RGBA8888,
            TexFormat.DXT5 => MipmapFormat.CompressedDXT5,
            TexFormat.DXT3 => MipmapFormat.CompressedDXT3,
            TexFormat.DXT1 => MipmapFormat.CompressedDXT1,
            TexFormat.R8 => MipmapFormat.R8,
            TexFormat.RG88 => MipmapFormat.RG88,
            _ => MipmapFormat.Invalid,
        };
    }

    private static MipmapFormat FreeImageFormatToMipmapFormat(FreeImageFormat imageFormat) => imageFormat switch
    {
        FreeImageFormat.Bmp => MipmapFormat.ImageBmp,
        FreeImageFormat.Ico => MipmapFormat.ImageIco,
        FreeImageFormat.Jpeg => MipmapFormat.ImageJpeg,
        FreeImageFormat.Jng => MipmapFormat.ImageJng,
        FreeImageFormat.Koala => MipmapFormat.ImageKoala,
        FreeImageFormat.Lbm => MipmapFormat.ImageLbm,
        FreeImageFormat.Mng => MipmapFormat.ImageMng,
        FreeImageFormat.Pbm => MipmapFormat.ImagePbm,
        FreeImageFormat.PbmRaw => MipmapFormat.ImagePbmRaw,
        FreeImageFormat.Pcd => MipmapFormat.ImagePcd,
        FreeImageFormat.Pcx => MipmapFormat.ImagePcx,
        FreeImageFormat.Pgm => MipmapFormat.ImagePgm,
        FreeImageFormat.PgmRaw => MipmapFormat.ImagePgmRaw,
        FreeImageFormat.Png => MipmapFormat.ImagePng,
        FreeImageFormat.Ppm => MipmapFormat.ImagePpm,
        FreeImageFormat.PpmRaw => MipmapFormat.ImagePpmRaw,
        FreeImageFormat.Ras => MipmapFormat.ImageRas,
        FreeImageFormat.Targa => MipmapFormat.ImageTarga,
        FreeImageFormat.Tiff => MipmapFormat.ImageTiff,
        FreeImageFormat.Wbmp => MipmapFormat.ImageWbmp,
        FreeImageFormat.Psd => MipmapFormat.ImagePsd,
        FreeImageFormat.Cut => MipmapFormat.ImageCut,
        FreeImageFormat.Xbm => MipmapFormat.ImageXbm,
        FreeImageFormat.Xpm => MipmapFormat.ImageXpm,
        FreeImageFormat.Dds => MipmapFormat.ImageDds,
        FreeImageFormat.Gif => MipmapFormat.ImageGif,
        FreeImageFormat.Hdr => MipmapFormat.ImageHdr,
        FreeImageFormat.FaxG3 => MipmapFormat.ImageFaxG3,
        FreeImageFormat.Sgi => MipmapFormat.ImageSgi,
        FreeImageFormat.Exr => MipmapFormat.ImageExr,
        FreeImageFormat.J2k => MipmapFormat.ImageJ2k,
        FreeImageFormat.Jp2 => MipmapFormat.ImageJp2,
        FreeImageFormat.Pfm => MipmapFormat.ImagePfm,
        FreeImageFormat.Pict => MipmapFormat.ImagePict,
        FreeImageFormat.Raw => MipmapFormat.ImageRaw,
        FreeImageFormat.Mp4 => MipmapFormat.VideoMp4,
        _ => MipmapFormat.Invalid,
    };

    private static TexImage ReadImage(Stream stream, TexContainerVersion version, MipmapFormat mipmapFormat)
    {
        var image = new TexImage();

        var mipmapCount = ReadInt32(stream);
        if (mipmapCount < 0 || mipmapCount > MaxMipmapCount)
            throw new InvalidDataException($"Unreasonable TEX mipmap count: {mipmapCount}.");

        for (var i = 0; i < mipmapCount; i++)
        {
            var mipmap = ReadMipmap(stream, version);
            mipmap.Format = mipmapFormat;
            image.Mipmaps.Add(mipmap);
        }

        return image;
    }

    private static TexMipmap ReadMipmap(Stream stream, TexContainerVersion version)
    {
        var width = ReadInt32(stream);
        var height = ReadInt32(stream);

        if (version == TexContainerVersion.V1)
        {
            var bytes = ReadByteArray(stream);
            return new TexMipmap
            {
                Width = width,
                Height = height,
                DecompressedBytesCount = bytes.Length,
                HadLz4 = false,
                Bytes = bytes,
            };
        }

        var isLz4 = ReadInt32(stream) == 1;
        var decompressedCount = ReadInt32(stream);

        if (version == TexContainerVersion.V4)
        {
            var p1 = ReadInt32(stream);
            var p2 = ReadInt32(stream);
            var conditionJson = ReadNulString(stream, MaxNStringLength);
            var p3 = ReadInt32(stream);

            if (p1 != 1 || p2 != 2 || p3 != 1)
                throw new InvalidDataException(
                    $"Unexpected TEXB0004 mipmap parameters (got {p1}/{p2}/{p3}, expected 1/2/1, condition=\"{conditionJson}\").");
        }

        var data = ReadByteArray(stream);

        if (decompressedCount > MaxMipmapByteCount)
            throw new InvalidDataException($"Unreasonable TEX mipmap byte count: {decompressedCount}.");

        return new TexMipmap
        {
            Width = width,
            Height = height,
            DecompressedBytesCount = decompressedCount,
            HadLz4 = isLz4,
            Bytes = data,
        };
    }

    private static TexFrameInfo ReadFrameInfo(Stream stream, string magic)
    {
        var version = ParseTrailingNumber(magic);
        var frameCount = ReadInt32(stream);
        if (frameCount < 0 || frameCount > MaxFrameCount)
            throw new InvalidDataException($"Unreasonable TEX frame count: {frameCount}.");

        var gifWidth = 0;
        var gifHeight = 0;
        if (version >= 3)
        {
            gifWidth = ReadInt32(stream);
            gifHeight = ReadInt32(stream);
        }

        var info = new TexFrameInfo
        {
            Magic = magic,
            GifWidth = gifWidth,
            GifHeight = gifHeight,
        };

        for (var i = 0; i < frameCount; i++)
        {
            info.Frames.Add(new TexFrame
            {
                ImageId = ReadInt32(stream),
                Frametime = ReadSingle(stream),
                X = ReadSingle(stream),
                Y = ReadSingle(stream),
                Width = ReadSingle(stream),
                HeightX = ReadSingle(stream),
                HeightY = ReadSingle(stream),
            });
        }

        return info;
    }

    private static int ParseTrailingNumber(string magic)
    {
        // e.g. "TEXB0003" -> 3, "TEXS0002" -> 2
        var i = magic.Length - 1;
        while (i >= 0 && char.IsDigit(magic[i])) i--;
        var digits = magic[(i + 1)..];
        return int.TryParse(digits, out var v) ? v : 0;
    }

    // ---------- primitives ----------

    /// <summary>
    /// Read a NUL-terminated magic string (e.g. "TEXV0005"), consuming bytes up to and
    /// including the terminator but at most <paramref name="maxLength"/> characters.
    /// The on-disk layout is variable width — TEXV0005\0TEXI0001\0 — not a fixed 16-byte slot.
    /// </summary>
    internal static string ReadNString(Stream stream, int maxLength)
    {
        var sb = new StringBuilder(Math.Min(maxLength, 32));
        for (var i = 0; i < maxLength; i++)
        {
            var b = stream.ReadByte();
            if (b <= 0) break;
            sb.Append((char)b);
        }
        return sb.ToString();
    }

    /// <summary>Read a NUL-terminated string of at most <paramref name="maxLength"/> characters.</summary>
    private static string ReadNulString(Stream stream, int maxLength)
    {
        var sb = new StringBuilder(Math.Min(maxLength, 64));
        for (var i = 0; i < maxLength; i++)
        {
            var b = stream.ReadByte();
            if (b <= 0) break;
            sb.Append((char)b);
        }
        return sb.ToString();
    }

    private static byte[] ReadByteArray(Stream stream)
    {
        var len = ReadInt32(stream);
        if (len < 0) throw new InvalidDataException($"Negative byte array length: {len}.");
        if (len == 0) return Array.Empty<byte>();

        var buf = new byte[len];
        var total = 0;
        while (total < len)
        {
            var read = stream.Read(buf, total, len - total);
            if (read <= 0) throw new EndOfStreamException("Truncated TEX byte array.");
            total += read;
        }
        return buf;
    }

    private static int ReadInt32(Stream stream)
    {
        var b = new byte[4];
        if (stream.Read(b, 0, 4) != 4) throw new EndOfStreamException("Unexpected end of TEX stream (int32).");
        return BitConverter.ToInt32(b, 0);
    }

    private static uint ReadUInt32(Stream stream)
    {
        var b = new byte[4];
        if (stream.Read(b, 0, 4) != 4) throw new EndOfStreamException("Unexpected end of TEX stream (uint32).");
        return BitConverter.ToUInt32(b, 0);
    }

    private static float ReadSingle(Stream stream)
    {
        var b = new byte[4];
        if (stream.Read(b, 0, 4) != 4) throw new EndOfStreamException("Unexpected end of TEX stream (float).");
        return BitConverter.ToSingle(b, 0);
    }
}
