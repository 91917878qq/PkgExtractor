using System;
using System.Collections.Generic;
using System.IO;

namespace PkgExtractor.Core;

/// <summary>One user-facing file produced from a TEX payload, kept in memory.</summary>
public sealed record ConvertedPart(string FileName, byte[] Data, string Kind);

/// <summary>
/// Converts .tex payloads into MP4 / GIF / PNG / embedded JPEG bytes without touching the disk,
/// so both the extractor and the preview window can share one code path.
/// </summary>
public static class TexConverter
{
    /// <summary>
    /// Decode <paramref name="texBytes"/>. Returns true when at least one part was produced.
    /// <paramref name="baseName"/> is the file name stem used for the produced parts.
    /// </summary>
    public static bool TryConvert(
        byte[] texBytes,
        string baseName,
        ExtractionOptions options,
        out List<ConvertedPart> parts,
        out string? warning)
    {
        parts = new List<ConvertedPart>();
        warning = null;

        TexFile tex;
        try
        {
            using var ms = new MemoryStream(texBytes, writable: false);
            tex = TexReader.Read(ms);
        }
        catch (Exception ex)
        {
            warning = $"TEX 解析失败：{ex.Message}";
            return false;
        }

        // Decode every mipmap once (LZ4 + DXT) so the conversions below see raw pixels.
        foreach (var image in tex.Images)
            foreach (var mip in image.Mipmaps)
                TexImageConverter.Decompress(mip, tex.Format);

        // 1. Video texture -> mp4
        if (tex.IsVideoTexture || tex.IsVideoMp4)
        {
            var video = FindVideoBytes(tex);
            if (video != null)
            {
                parts.Add(new ConvertedPart(baseName + ".mp4", video, "video"));
                return true;
            }
            warning = "标记为视频纹理，但未找到 MP4 数据。";
        }

        // 2. Animated texture -> gif
        if (tex.IsGif && tex.FrameInfo is { Frames.Count: > 0 })
        {
            var gif = TexImageConverter.ToGif(tex);
            if (gif is { Length: > 0 })
            {
                parts.Add(new ConvertedPart(baseName + ".gif", gif, "gif"));
                return true;
            }
            warning = "GIF 合成失败，回退为静态图。";
        }

        // 3. Embedded finished image (JPEG / PNG / BMP / ...) -> keep the payload verbatim.
        if (tex.Images.Count > 0 && tex.Images[0].Mipmaps.Count > 0)
        {
            var firstMip = tex.Images[0].Mipmaps[0];
            if (TexImageConverter.IsEmbeddedFile(firstMip) && firstMip.Bytes.Length > 0)
            {
                var ext = TexImageConverter.EmbeddedExtension(firstMip);
                parts.Add(new ConvertedPart(baseName + ext, firstMip.Bytes, "image"));
                return true;
            }
        }

        // 4. Static image -> png (one per mipmap level when requested)
        if (tex.Images.Count == 0)
        {
            warning = "TEX 中没有任何图像层。";
            return false;
        }

        var firstImage = tex.Images[0];
        var mipmaps = options.ExtractAllMipmaps
            ? firstImage.Mipmaps
            : firstImage.Mipmaps.Count > 0
                ? new List<TexMipmap> { firstImage.Mipmaps[0] }
                : new List<TexMipmap>();

        foreach (var mip in mipmaps)
        {
            var png = TexImageConverter.ToPng(mip, tex.Format, tex.ImageWidth, tex.ImageHeight);
            if (png is not { Length: > 0 }) continue;

            var suffix = mipmaps.Count > 1 ? $"_{mip.Width}x{mip.Height}" : "";
            parts.Add(new ConvertedPart(baseName + suffix + ".png", png, "image"));
        }

        if (parts.Count == 0)
        {
            warning = $"图像格式 {tex.Format} 无法转换为 PNG。";
            return false;
        }

        return true;
    }

    /// <summary>Human readable summary of what a TEX entry would become.</summary>
    public static string DescribeOutput(byte[] texBytes, ExtractionOptions options)
    {
        try
        {
            using var ms = new MemoryStream(texBytes, writable: false);
            var tex = TexReader.Read(ms);

            if (tex.IsVideoTexture || tex.IsVideoMp4) return "MP4";
            if (tex.IsGif && tex.FrameInfo is { Frames.Count: > 0 }) return "GIF";

            if (tex.Images.Count > 0 && tex.Images[0].Mipmaps.Count > 0)
            {
                var mip = tex.Images[0].Mipmaps[0];
                if (TexImageConverter.IsEmbeddedFile(mip))
                    return TexImageConverter.EmbeddedExtension(mip).TrimStart('.').ToUpperInvariant();
            }

            return "PNG";
        }
        catch
        {
            return "?";
        }
    }

    private static byte[]? FindVideoBytes(TexFile tex)
    {
        foreach (var image in tex.Images)
            foreach (var mip in image.Mipmaps)
                if (TexImageConverter.IsMp4(mip.Bytes))
                    return mip.Bytes;

        return null;
    }
}
