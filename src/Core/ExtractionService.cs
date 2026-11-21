using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PkgExtractor.Core;

public sealed class ExtractionOptions
{
    /// <summary>Root folder that receives one sub-folder per package.</summary>
    public string OutputDirectory { get; set; } = "";

    /// <summary>Convert .tex entries into PNG / GIF / MP4 instead of dumping raw .tex.</summary>
    public bool ConvertTextures { get; set; } = true;

    /// <summary>Also write the original .tex alongside the converted image.</summary>
    public bool KeepOriginalTex { get; set; }

    /// <summary>Decode every mipmap level instead of only the largest one.</summary>
    public bool ExtractAllMipmaps { get; set; }

    /// <summary>Create a sub-folder named after each package file.</summary>
    public bool SubfolderPerPackage { get; set; } = true;

    /// <summary>Overwrite existing files.</summary>
    public bool Overwrite { get; set; } = true;
}

public sealed record ExtractionProgress(
    int FileIndex,
    int FileCount,
    string FileName,
    string Stage,
    double FilePercent,
    string? Message);

public sealed record ExtractedFile(
    string SourcePackage,
    string EntryPath,
    string OutputPath,
    long Bytes,
    string Kind);

public sealed class ExtractionResult
{
    public List<ExtractedFile> Files { get; } = new();
    public List<string> Warnings { get; } = new();
    public List<string> Errors { get; } = new();
    public int PackageCount { get; set; }
    public int ConvertedTexCount { get; set; }
    public long TotalBytes { get; set; }
    public TimeSpan Elapsed { get; set; }
}

public sealed class ExtractionService
{
    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public async Task<ExtractionResult> ExtractAsync(
        IReadOnlyList<string> packagePaths,
        ExtractionOptions options,
        IProgress<ExtractionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        var result = new ExtractionResult { PackageCount = packagePaths.Count };

        if (packagePaths.Count == 0) return result;

        if (string.IsNullOrWhiteSpace(options.OutputDirectory))
            throw new ArgumentException("Output directory is required.", nameof(options));

        Directory.CreateDirectory(options.OutputDirectory);

        for (var i = 0; i < packagePaths.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = packagePaths[i];
            var name = Path.GetFileName(path);
            var index = i;

            await Task.Run(() =>
            {
                try
                {
                    ExtractSingle(path, name, index, packagePaths.Count, options, progress, cancellationToken, result);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"{name}: {ex.Message}");
                    progress?.Report(new ExtractionProgress(index, packagePaths.Count, name, "失败", 0, ex.Message));
                }
            }, cancellationToken).ConfigureAwait(false);
        }

        result.Elapsed = DateTime.UtcNow - started;
        return result;
    }

    private static void ExtractSingle(
        string packagePath,
        string packageName,
        int fileIndex,
        int fileCount,
        ExtractionOptions options,
        IProgress<ExtractionProgress>? progress,
        CancellationToken ct,
        ExtractionResult result)
    {
        using var fs = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);

        if (!PkgReader.LooksLikePkg(fs))
            throw new InvalidDataException("不是有效的 PKG / MPKG 文件（magic 不匹配）。");

        var package = PkgReader.Read(fs);

        var rootName = options.SubfolderPerPackage
            ? SanitizeName(Path.GetFileNameWithoutExtension(packageName))
            : "";

        var outputRoot = options.SubfolderPerPackage
            ? Path.Combine(options.OutputDirectory, rootName)
            : options.OutputDirectory;

        Directory.CreateDirectory(outputRoot);

        var entries = package.Entries;
        var total = entries.Count;

        for (var i = 0; i < total; i++)
        {
            ct.ThrowIfCancellationRequested();

            var entry = entries[i];
            var percent = total == 0 ? 100 : (i + 1) * 100.0 / total;

            progress?.Report(new ExtractionProgress(
                fileIndex, fileCount, packageName,
                $"解包 {entry.FileName} ({i + 1}/{total})",
                percent, null));

            try
            {
                var targetPath = ResolveSafePath(outputRoot, entry.FullPath);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

                var bytes = PkgReader.ReadEntryBytes(fs, entry);

                if (entry.IsTex && options.ConvertTextures)
                {
                    var converted = ConvertTex(bytes, targetPath, options,
                        out var written, out var kind, out var warning);

                    if (warning != null)
                        result.Warnings.Add($"{packageName} / {entry.FullPath}: {warning}");

                    if (converted)
                    {
                        result.ConvertedTexCount++;
                        foreach (var (p, n) in written)
                        {
                            result.Files.Add(new ExtractedFile(packagePath, entry.FullPath, p, n, kind));
                            result.TotalBytes += n;
                        }

                        if (options.KeepOriginalTex)
                        {
                            var rawPath = UniquePath(targetPath, options.Overwrite);
                            File.WriteAllBytes(rawPath, bytes);
                            result.Files.Add(new ExtractedFile(packagePath, entry.FullPath, rawPath, bytes.Length, "tex"));
                            result.TotalBytes += bytes.Length;
                        }

                        continue;
                    }
                }

                targetPath = UniquePath(targetPath, options.Overwrite);
                File.WriteAllBytes(targetPath, bytes);
                result.Files.Add(new ExtractedFile(packagePath, entry.FullPath, targetPath, bytes.Length,
                    entry.IsTex ? "tex" : KindOf(entry.Extension)));
                result.TotalBytes += bytes.Length;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Errors.Add($"{packageName} / {entry.FullPath}: {ex.Message}");
            }
        }

        progress?.Report(new ExtractionProgress(fileIndex, fileCount, packageName, "完成", 100, null));
    }

    /// <summary>
    /// Convert a TEX payload into user-facing media files. Returns true when at least one file was written.
    /// </summary>
    private static bool ConvertTex(
        byte[] texBytes,
        string targetPath,
        ExtractionOptions options,
        out List<(string Path, long Bytes)> written,
        out string kind,
        out string? warning)
    {
        written = new List<(string, long)>();
        kind = "image";
        warning = null;

        TexFile tex;
        try
        {
            using var ms = new MemoryStream(texBytes, writable: false);
            tex = TexReader.Read(ms);
        }
        catch (Exception ex)
        {
            warning = $"TEX 解析失败，已按原始字节写出：{ex.Message}";
            return false;
        }

        var baseDir = Path.GetDirectoryName(targetPath)!;
        var baseName = Path.GetFileNameWithoutExtension(targetPath);

        // Decode all mipmaps once (LZ4 + DXT) so downstream conversions see raw pixels.
        foreach (var image in tex.Images)
            foreach (var mip in image.Mipmaps)
                TexImageConverter.Decompress(mip, tex.Format);

        // 1. Video texture -> mp4
        if (tex.IsVideoTexture || tex.IsVideoMp4)
        {
            var video = FindVideoBytes(tex);
            if (video != null)
            {
                var p = UniquePath(Path.Combine(baseDir, baseName + ".mp4"), options.Overwrite);
                File.WriteAllBytes(p, video);
                written.Add((p, video.Length));
                kind = "video";
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
                var p = UniquePath(Path.Combine(baseDir, baseName + ".gif"), options.Overwrite);
                File.WriteAllBytes(p, gif);
                written.Add((p, gif.Length));
                kind = "gif";
                return true;
            }
            warning = "GIF 合成失败，回退为静态图。";
        }

        // 3. Embedded finished image (JPEG / PNG / BMP / ...) -> write the payload verbatim.
        if (tex.Images.Count > 0 && tex.Images[0].Mipmaps.Count > 0)
        {
            var firstMip = tex.Images[0].Mipmaps[0];
            if (TexImageConverter.IsEmbeddedFile(firstMip) && firstMip.Bytes.Length > 0)
            {
                var ext = TexImageConverter.EmbeddedExtension(firstMip);
                var p = UniquePath(Path.Combine(baseDir, baseName + ext), options.Overwrite);
                File.WriteAllBytes(p, firstMip.Bytes);
                written.Add((p, firstMip.Bytes.Length));
                kind = "image";
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
        var mipmaps = options.ExtractAllMipmaps ? firstImage.Mipmaps : TakeFirst(firstImage);

        foreach (var mip in mipmaps)
        {
            var png = TexImageConverter.ToPng(mip, tex.Format, tex.ImageWidth, tex.ImageHeight);
            if (png is not { Length: > 0 }) continue;

            var suffix = mipmaps.Count > 1 ? $"_{mip.Width}x{mip.Height}" : "";
            var p = UniquePath(Path.Combine(baseDir, baseName + suffix + ".png"), options.Overwrite);
            File.WriteAllBytes(p, png);
            written.Add((p, png.Length));
        }

        if (written.Count == 0)
        {
            warning = $"图像格式 {tex.Format} 无法转换为 PNG。";
            return false;
        }

        return true;
    }

    private static List<TexMipmap> TakeFirst(TexImage image) =>
        image.Mipmaps.Count > 0 ? new List<TexMipmap> { image.Mipmaps[0] } : new List<TexMipmap>();

    private static byte[]? FindVideoBytes(TexFile tex)
    {
        foreach (var image in tex.Images)
        {
            foreach (var mip in image.Mipmaps)
            {
                if (TexImageConverter.IsMp4(mip.Bytes))
                    return mip.Bytes;
            }
        }
        return null;
    }

    private static string KindOf(string extension) => extension switch
    {
        ".mp4" => "video",
        ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".tga" => "image",
        ".json" => "json",
        ".tex" => "tex",
        _ => "file",
    };

    public static string ResolveSafePath(string root, string entryFullPath)
    {
        var relative = entryFullPath.Replace('\\', '/').TrimStart('/');

        var parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var safe = new List<string>(parts.Length);

        foreach (var part in parts)
        {
            if (part is "." or "..") continue;
            safe.Add(SanitizeName(part));
        }

        if (safe.Count == 0) safe.Add("unnamed");

        var combined = Path.Combine(root, Path.Combine(safe.ToArray()));
        var full = Path.GetFullPath(combined);
        var rootFull = Path.GetFullPath(root);

        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"条目路径越界：{entryFullPath}");

        return full;
    }

    public static string SanitizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "unnamed";

        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (Array.IndexOf(invalid, chars[i]) >= 0) chars[i] = '_';

        var cleaned = new string(chars).Trim().TrimEnd('.');

        if (cleaned.Length == 0) cleaned = "unnamed";

        var stem = Path.GetFileNameWithoutExtension(cleaned);
        if (ReservedWindowsNames.Contains(stem)) cleaned = "_" + cleaned;

        if (cleaned.Length > 180) cleaned = cleaned[..180];

        return cleaned;
    }

    public static string UniquePath(string path, bool overwrite)
    {
        if (overwrite || !File.Exists(path)) return path;

        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);

        for (var i = 2; i < 10_000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }

        return path;
    }
}

