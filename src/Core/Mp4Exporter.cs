using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace PkgExtractor.Core;

/// <summary>
/// Finds the video payload inside a Wallpaper Engine package and writes it out untouched.
/// The bytes are copied verbatim, so frame rate, time base and duration stay exactly as the
/// author encoded them - no re-encode, no re-timing, no cropping.
/// </summary>
public static class Mp4Exporter
{
    public sealed record VideoEntry(string EntryPath, long Bytes, string Reason);

    public static bool IsVideoName(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".webm", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".mov", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Extracts the video bytes of one package. Returns null when it has no video.</summary>
    public static byte[]? ReadVideoBytes(string packagePath, out string entryPath, out string reason)
    {
        entryPath = "";
        reason = "";

        using var fs = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);

        if (!PkgReader.LooksLikePkg(fs)) return null;

        var package = PkgReader.Read(fs);

        // Try candidates in priority order and keep going until one really produces MP4 bytes.
        // A wallpaper scene usually has dozens of plain image textures, so a texture merely being
        // the biggest one proves nothing.
        foreach (var candidate in OrderCandidates(package, fs))
        {
            var entry = package.Entries.FirstOrDefault(e =>
                string.Equals(e.FullPath, candidate, StringComparison.OrdinalIgnoreCase));

            if (entry is null) continue;

            byte[] bytes;
            try { bytes = PkgReader.ReadEntryBytes(fs, entry); }
            catch { continue; }

            if (bytes.Length == 0) continue;

            if (entry.IsTex)
            {
                var payload = UnwrapVideoTexture(bytes);
                if (payload is null) continue;

                entryPath = entry.FullPath;
                reason = "视频纹理（内嵌 MP4）";
                return payload;
            }

            if (!Mp4Probe.IsMp4(bytes)) continue;

            entryPath = entry.FullPath;
            reason = "包内视频文件";
            return bytes;
        }

        reason = "包内没有视频数据";
        return null;
    }

    /// <summary>Candidate entry paths, most likely video first.</summary>
    private static IEnumerable<string> OrderCandidates(PkgPackage package, FileStream stream)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. A file the project/scene metadata explicitly points at.
        foreach (var metaName in new[] { "project.json", "scene.json" })
        {
            var meta = package.Entries.FirstOrDefault(e =>
                string.Equals(e.FullPath.Replace('\\', '/'), metaName, StringComparison.OrdinalIgnoreCase));

            if (meta is null) continue;

            string? declared = null;
            try
            {
                var text = Encoding.UTF8.GetString(PkgReader.ReadEntryBytes(stream, meta));
                using var doc = JsonDocument.Parse(text, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                });
                declared = FindStringProperty(doc.RootElement, "file");
            }
            catch
            {
                // A broken project.json must not stop the export.
            }

            if (string.IsNullOrWhiteSpace(declared)) continue;

            var match = package.Entries.FirstOrDefault(e =>
                e.FullPath.Replace('\\', '/')
                    .Equals(declared.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));

            if (match is not null && seen.Add(match.FullPath))
                yield return match.FullPath;
        }

        // 2. Real video containers, largest first.
        foreach (var e in package.Entries.Where(e => IsVideoName(e.FullPath)).OrderByDescending(e => e.Length))
            if (seen.Add(e.FullPath))
                yield return e.FullPath;

        // 3. Textures, largest first - some wallpapers keep the video inside a .tex.
        foreach (var e in package.Entries.Where(e => e.IsTex).OrderByDescending(e => e.Length))
            if (seen.Add(e.FullPath))
                yield return e.FullPath;
    }

    /// <summary>Returns the MP4 payload of a video texture, or null when it carries none.</summary>
    private static byte[]? UnwrapVideoTexture(byte[] texBytes)
    {
        try
        {
            using var ms = new MemoryStream(texBytes, writable: false);
            var tex = TexReader.Read(ms);

            foreach (var image in tex.Images)
                foreach (var mip in image.Mipmaps)
                    TexImageConverter.Decompress(mip, tex.Format);

            foreach (var image in tex.Images)
                foreach (var mip in image.Mipmaps)
                    if (TexImageConverter.IsMp4(mip.Bytes))
                        return mip.Bytes;
        }
        catch
        {
            // Not a parseable texture -> definitely not a video.
        }

        return null;
    }

    private static string? FindStringProperty(JsonElement element, string name)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if (prop.NameEquals(name) && prop.Value.ValueKind == JsonValueKind.String)
                        return prop.Value.GetString();

                    var nested = FindStringProperty(prop.Value, name);
                    if (nested is not null) return nested;
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    var nested = FindStringProperty(item, name);
                    if (nested is not null) return nested;
                }
                break;
        }

        return null;
    }

    /// <summary>Human readable frame-rate summary for the UI.</summary>
    public static string Describe(byte[] data)
    {
        var info = Mp4Probe.Probe(data);
        if (info is null) return "无法解析视频信息";

        var parts = new List<string>
        {
            $"{info.DurationSeconds:0.###}s",
            info.FrameRate > 0 ? $"{info.FrameRate:0.##} fps" : "帧率未知",
            $"{info.Width}×{info.Height}",
        };

        if (!string.IsNullOrWhiteSpace(info.Codec)) parts.Add(info.Codec);
        if (info.SampleCount > 0) parts.Add($"{info.SampleCount} 帧");
        if (info.HasAudio) parts.Add("含音轨");

        return string.Join(" · ", parts);
    }
}
