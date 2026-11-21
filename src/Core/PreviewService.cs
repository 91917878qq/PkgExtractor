using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PkgExtractor.Core;

/// <summary>One selectable row in the preview tree.</summary>
public sealed class PreviewNode
{
    public required string EntryPath { get; init; }
    public required string FullPath { get; init; }
    public required string PackagePath { get; init; }
    public required long SourceSize { get; init; }
    public required bool IsTex { get; init; }
    public required bool LooksLikeImage { get; set; }
    public required string Kind { get; set; }

    /// <summary>Bytes to be written when the node is saved (already converted).</summary>
    public byte[]? Data { get; set; }

    /// <summary>Extension to use for the saved file, including the dot.</summary>
    public string OutputExtension { get; set; } = "";

    /// <summary>Optional extra file written alongside (original .tex).</summary>
    public byte[]? RawTex { get; set; }

    public string? Warning { get; set; }
    public bool Selected { get; set; } = true;

    public string DisplayPath => EntryPath;
    public string SizeText => FormatSize(Data?.Length ?? SourceSize);
    public string TypeText => OutputExtension.TrimStart('.').ToUpperInvariant();

    /// <summary>
    /// Relative path used when saving. The extension is replaced (not appended) so an entry
    /// like "sounds/x.mp3" does not become "x.mp3.mp3", and "materials/a.tex" becomes "a.jpg".
    /// </summary>
    public string SaveRelativePath
    {
        get
        {
            if (string.IsNullOrEmpty(OutputExtension)) return EntryPath;

            var normalized = EntryPath.Replace('\\', '/');
            var slash = normalized.LastIndexOf('/');

            var dir = slash >= 0 ? normalized[..slash] : "";
            var file = slash >= 0 ? normalized[(slash + 1)..] : normalized;

            var dot = file.LastIndexOf('.');
            var stem = dot > 0 ? file[..dot] : file;

            var saved = stem + OutputExtension;
            return string.IsNullOrEmpty(dir) ? saved : dir + "/" + saved;
        }
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
    };
}

public sealed class PreviewPackage
{
    public required string PackagePath { get; init; }
    public required string PackageName { get; init; }
    public required string Magic { get; init; }
    public List<PreviewNode> Nodes { get; } = new();
    public List<string> Warnings { get; } = new();
}

public sealed record PreviewProgress(int Index, int Count, string Message);

public static class PreviewService
{
    /// <summary>
    /// Read packages and materialise every entry in memory as its final on-disk payload.
    /// Nothing is written to disk; the caller decides what to save.
    /// </summary>
    public static Task<List<PreviewPackage>> LoadAsync(
        IReadOnlyList<string> packagePaths,
        ExtractionOptions options,
        IProgress<PreviewProgress>? progress,
        CancellationToken ct) => Task.Run(() =>
    {
        var packages = new List<PreviewPackage>(packagePaths.Count);

        for (var i = 0; i < packagePaths.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var path = packagePaths[i];
            var name = Path.GetFileName(path);
            progress?.Report(new PreviewProgress(i + 1, packagePaths.Count, $"读取 {name}"));

            var package = LoadOne(path, options, packages.Count);
            packages.Add(package);
        }

        return packages;
    }, ct);

    private static PreviewPackage LoadOne(string path, ExtractionOptions options, int ordinal)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);

        if (!PkgReader.LooksLikePkg(fs))
            throw new InvalidDataException("不是有效的 PKG / MPKG 文件（magic 不匹配）。");

        var pkg = PkgReader.Read(fs);
        var result = new PreviewPackage
        {
            PackagePath = path,
            PackageName = Path.GetFileName(path),
            Magic = pkg.Magic,
        };

        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in pkg.Entries)
        {
            var bytes = PkgReader.ReadEntryBytes(fs, entry);

            var node = new PreviewNode
            {
                EntryPath = entry.FullPath,
                FullPath = path,
                PackagePath = path,
                SourceSize = bytes.Length,
                IsTex = entry.IsTex,
                Kind = KindOf(entry.Extension),
                LooksLikeImage = IsImageExt(entry.Extension),
                OutputExtension = entry.Extension,
                Data = bytes,
            };

            if (entry.IsTex && options.ConvertTextures)
            {
                var baseName = Path.GetFileNameWithoutExtension(entry.FileName);
                if (TexConverter.TryConvert(bytes, baseName, options, out var parts, out var warning))
                {
                    var first = parts[0];
                    node.Data = first.Data;
                    node.OutputExtension = Path.GetExtension(first.FileName);
                    node.Kind = first.Kind;
                    node.LooksLikeImage = first.Kind is "image" or "video" or "gif";
                    node.Warning = warning;

                    if (options.KeepOriginalTex) node.RawTex = bytes;

                    // Additional mipmap outputs are appended as their own rows.
                    for (var k = 1; k < parts.Count; k++)
                    {
                        result.Nodes.Add(new PreviewNode
                        {
                            EntryPath = System.IO.Path.Combine(
                                System.IO.Path.GetDirectoryName(entry.FullPath) ?? "",
                                parts[k].FileName).Replace('\\', '/').TrimStart('/'),
                            FullPath = path,
                            PackagePath = path,
                            SourceSize = parts[k].Data.Length,
                            IsTex = true,
                            Kind = parts[k].Kind,
                            LooksLikeImage = true,
                            OutputExtension = System.IO.Path.GetExtension(parts[k].FileName),
                            Data = parts[k].Data,
                        });
                    }
                }
                else
                {
                    node.Warning = warning;
                    node.OutputExtension = ".tex";
                    node.Kind = "tex";
                    node.LooksLikeImage = false;
                }
            }

            // De-duplicate the display path so the tree stays unambiguous.
            var key = node.EntryPath + node.OutputExtension;
            if (!usedNames.Add(key))
            {
                var dir = System.IO.Path.GetDirectoryName(node.EntryPath)?.Replace('\\', '/') ?? "";
                var stem = System.IO.Path.GetFileNameWithoutExtension(node.EntryPath);
                var n = 2;
                string candidate;
                do
                {
                    candidate = string.IsNullOrEmpty(dir)
                        ? $"{stem} ({n}){node.OutputExtension}"
                        : $"{dir}/{stem} ({n}){node.OutputExtension}";
                    n++;
                }
                while (!usedNames.Add(candidate + node.OutputExtension));

                node = new PreviewNode
                {
                    EntryPath = candidate,
                    FullPath = node.FullPath,
                    PackagePath = node.PackagePath,
                    SourceSize = node.SourceSize,
                    IsTex = node.IsTex,
                    Kind = node.Kind,
                    LooksLikeImage = node.LooksLikeImage,
                    OutputExtension = node.OutputExtension,
                    Data = node.Data,
                    RawTex = node.RawTex,
                    Warning = node.Warning,
                };
            }

            result.Nodes.Add(node);
        }

        return result;
    }

    /// <summary>
    /// Write the selected nodes to disk. Returns the number of files produced.
    /// Shared by the preview window and the headless selftest.
    /// </summary>
    public static int Save(
        List<PreviewPackage> packages,
        string outputDirectory,
        ExtractionOptions options)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new ArgumentException("Output directory is required.", nameof(outputDirectory));

        Directory.CreateDirectory(outputDirectory);

        var written = 0;

        foreach (var package in packages)
        {
            var nodes = package.Nodes.Where(n => n.Selected).ToList();
            if (nodes.Count == 0) continue;

            var outputRoot = options.SubfolderPerPackage
                ? Path.Combine(outputDirectory, ExtractionService.SanitizeName(
                    Path.GetFileNameWithoutExtension(package.PackageName)))
                : outputDirectory;

            Directory.CreateDirectory(outputRoot);

            foreach (var node in nodes)
            {
                if (node.Data is not { Length: > 0 }) continue;

                var target = ExtractionService.ResolveSafePath(outputRoot, node.SaveRelativePath);

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                if (!options.Overwrite)
                    target = ExtractionService.UniquePath(target, false);

                File.WriteAllBytes(target, node.Data);
                written++;

                if (node.RawTex is { Length: > 0 })
                {
                    var raw = Path.ChangeExtension(target, ".tex");
                    if (!options.Overwrite) raw = ExtractionService.UniquePath(raw, false);
                    File.WriteAllBytes(raw, node.RawTex);
                    written++;
                }
            }
        }

        return written;
    }

    private static string KindOf(string extension) => extension switch
    {
        ".mp4" => "video",
        ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".tga" => "image",
        ".json" => "json",
        ".tex" => "tex",
        ".mp3" or ".ogg" or ".wav" => "audio",
        _ => "file",
    };

    private static bool IsImageExt(string extension) => extension is
        ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp";
}
