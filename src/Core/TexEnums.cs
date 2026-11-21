using System;

namespace PkgExtractor.Core;

/// <summary>Pixel layout of a TEX mipmap's stored data.</summary>
public enum TexFormat
{
    RGBA8888 = 0,
    DXT5 = 4,
    DXT3 = 6,
    DXT1 = 7,
    RG88 = 8,
    R8 = 9,
}

[Flags]
public enum TexFlags
{
    None = 0,
    NoInterpolation = 1,
    ClampUVs = 2,
    IsGif = 4,
    Unk3 = 8,
    Unk4 = 16,
    IsVideoTexture = 32,
    Unk6 = 64,
    Unk7 = 128,
}

/// <summary>Optional embedded image format stored in TEXB0003 / TEXB0004 containers.</summary>
public enum FreeImageFormat
{
    Unknown = -1,
    Bmp = 0,
    Ico = 1,
    Jpeg = 2,
    Jng = 3,
    Koala = 4,
    Lbm = 5,
    Mng = 6,
    Pbm = 7,
    PbmRaw = 8,
    Pcd = 9,
    Pcx = 10,
    Pgm = 11,
    PgmRaw = 12,
    Png = 13,
    Ppm = 14,
    PpmRaw = 15,
    Ras = 16,
    Targa = 17,
    Tiff = 18,
    Wbmp = 19,
    Psd = 20,
    Cut = 21,
    Xbm = 22,
    Xpm = 23,
    Dds = 24,
    Gif = 25,
    Hdr = 26,
    FaxG3 = 27,
    Sgi = 28,
    Exr = 29,
    J2k = 30,
    Jp2 = 31,
    Pfm = 32,
    Pict = 33,
    Raw = 34,
    Mp4 = 35,
}

/// <summary>Decoded mipmap payload layout.</summary>
public enum MipmapFormat
{
    Invalid = 0,
    RGBA8888 = 1,
    R8 = 2,
    RG88 = 3,
    CompressedDXT5 = 4,
    CompressedDXT3 = 5,
    CompressedDXT1 = 6,
    VideoMp4 = 7,

    ImageBmp = 1000,
    ImageIco,
    ImageJpeg,
    ImageJng,
    ImageKoala,
    ImageLbm,
    ImageMng,
    ImagePbm,
    ImagePbmRaw,
    ImagePcd,
    ImagePcx,
    ImagePgm,
    ImagePgmRaw,
    ImagePng,
    ImagePpm,
    ImagePpmRaw,
    ImageRas,
    ImageTarga,
    ImageTiff,
    ImageWbmp,
    ImagePsd,
    ImageCut,
    ImageXbm,
    ImageXpm,
    ImageDds,
    ImageGif,
    ImageHdr,
    ImageFaxG3,
    ImageSgi,
    ImageExr,
    ImageJ2k,
    ImageJp2,
    ImagePfm,
    ImagePict,
    ImageRaw,
}

public static class MipmapFormatExtensions
{
    public static bool IsImage(this MipmapFormat f) => (int)f >= 1000;

    public static bool IsCompressed(this MipmapFormat f) =>
        f is MipmapFormat.CompressedDXT1 or MipmapFormat.CompressedDXT3 or MipmapFormat.CompressedDXT5;

    public static bool IsRaw(this MipmapFormat f) =>
        f is MipmapFormat.RGBA8888 or MipmapFormat.R8 or MipmapFormat.RG88;

    public static string ToFileExtension(this MipmapFormat f) => f switch
    {
        MipmapFormat.VideoMp4 => ".mp4",
        MipmapFormat.ImageBmp => ".bmp",
        MipmapFormat.ImageIco => ".ico",
        MipmapFormat.ImageJpeg => ".jpg",
        MipmapFormat.ImageJng => ".jng",
        MipmapFormat.ImageMng => ".mng",
        MipmapFormat.ImagePbm => ".pbm",
        MipmapFormat.ImagePbmRaw => ".pbm",
        MipmapFormat.ImagePcd => ".pcd",
        MipmapFormat.ImagePcx => ".pcx",
        MipmapFormat.ImagePgm => ".pgm",
        MipmapFormat.ImagePgmRaw => ".pgm",
        MipmapFormat.ImagePng => ".png",
        MipmapFormat.ImagePpm => ".ppm",
        MipmapFormat.ImagePpmRaw => ".ppm",
        MipmapFormat.ImageRas => ".ras",
        MipmapFormat.ImageTarga => ".tga",
        MipmapFormat.ImageTiff => ".tif",
        MipmapFormat.ImageWbmp => ".wbmp",
        MipmapFormat.ImagePsd => ".psd",
        MipmapFormat.ImageXbm => ".xbm",
        MipmapFormat.ImageXpm => ".xpm",
        MipmapFormat.ImageDds => ".dds",
        MipmapFormat.ImageGif => ".gif",
        MipmapFormat.ImageHdr => ".hdr",
        MipmapFormat.ImageFaxG3 => ".g3",
        MipmapFormat.ImageSgi => ".sgi",
        MipmapFormat.ImageExr => ".exr",
        MipmapFormat.ImageJ2k => ".jp2",
        MipmapFormat.ImageJp2 => ".jp2",
        MipmapFormat.ImagePfm => ".pfm",
        MipmapFormat.ImagePict => ".pict",
        MipmapFormat.ImageRaw => ".raw",
        _ => ".bin",
    };
}
