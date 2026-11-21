using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PkgExtractor.Core;

/// <summary>One entry inside a PKG / MPKG container.</summary>
public sealed class PkgEntry
{
    public string FullPath { get; init; } = "";
    public long Offset { get; init; }      // absolute file offset of the payload
    public long Length { get; init; }      // payload length in bytes
    public bool IsTex { get; init; }

    public string FileName => Path.GetFileName(FullPath.Replace('\\', '/'));
    public string Extension => Path.GetExtension(FileName).ToLowerInvariant();
}

/// <summary>Parsed PKG container header.</summary>
public sealed class PkgPackage
{
    public string Magic { get; init; } = "";
    public long DataStart { get; init; }
    public long HeaderSize { get; init; }
    public IReadOnlyList<PkgEntry> Entries { get; init; } = Array.Empty<PkgEntry>();

    public bool IsMapPackage => Magic.StartsWith("PKGV", StringComparison.Ordinal);
    public bool IsMpkg => Magic.StartsWith("PKGM", StringComparison.Ordinal);
}

/// <summary>
/// Reader for the Wallpaper Engine PKGVxxxx / PKGMxxxx container.
///
/// Wire format:
///   int32  magicLength
///   char[magicLength] magic        e.g. "PKGV0014" / "PKGM0014"
///   int32  entryCount
///   repeat entryCount:
///     int32  nameLength
///     char[nameLength] fullPath
///     uint32 offset                relative to data start
///     uint32 length
///   &lt;payload area starts here&gt;
/// </summary>
public static class PkgReader
{
    private const int MaxMagicLength = 32;
    private const int MaxPathLength = 4096;

    private static readonly string[] KnownMagicPrefixes = { "PKGV", "PKGM" };

    public static bool LooksLikePkg(Stream stream)
    {
        try
        {
            var start = stream.Position;
            var magic = TryReadMagic(stream);
            stream.Position = start;
            return magic != null;
        }
        catch
        {
            return false;
        }
    }

    private static string? TryReadMagic(Stream stream)
    {
        var lenBytes = new byte[4];
        if (stream.Read(lenBytes, 0, 4) != 4) return null;

        var len = BitConverter.ToInt32(lenBytes, 0);
        if (len <= 0 || len > MaxMagicLength) return null;

        var magicBytes = new byte[len];
        if (stream.Read(magicBytes, 0, len) != len) return null;

        var magic = Encoding.UTF8.GetString(magicBytes);

        foreach (var prefix in KnownMagicPrefixes)
            if (magic.StartsWith(prefix, StringComparison.Ordinal))
                return magic;

        return null;
    }

    public static PkgPackage Read(Stream stream, bool readEntries = true)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var packageStart = stream.Position;

        var magic = TryReadMagic(stream)
                    ?? throw new InvalidDataException("Not a PKG/MPKG container: unknown magic.");

        var countBytes = new byte[4];
        if (stream.Read(countBytes, 0, 4) != 4)
            throw new InvalidDataException("Truncated PKG header: entry count missing.");

        var entryCount = BitConverter.ToInt32(countBytes, 0);
        if (entryCount < 0 || entryCount > 1_000_000)
            throw new InvalidDataException($"Unreasonable PKG entry count: {entryCount}.");

        var raw = new List<(string FullPath, long Offset, long Length)>(Math.Min(entryCount, 4096));

        for (var i = 0; i < entryCount; i++)
        {
            var nameLenBytes = new byte[4];
            if (stream.Read(nameLenBytes, 0, 4) != 4)
                throw new InvalidDataException($"Truncated PKG header at entry {i}: name length missing.");

            var nameLen = BitConverter.ToInt32(nameLenBytes, 0);
            if (nameLen < 0 || nameLen > MaxPathLength)
                throw new InvalidDataException($"Unreasonable PKG entry name length: {nameLen}.");

            var nameBytes = new byte[nameLen];
            if (stream.Read(nameBytes, 0, nameLen) != nameLen)
                throw new InvalidDataException($"Truncated PKG header at entry {i}: name missing.");

            var fullPath = Encoding.UTF8.GetString(nameBytes);

            var meta = new byte[8];
            if (stream.Read(meta, 0, 8) != 8)
                throw new InvalidDataException($"Truncated PKG header at entry {i}: offset/length missing.");

            var offset = BitConverter.ToUInt32(meta, 0);
            var length = BitConverter.ToUInt32(meta, 4);

            raw.Add((fullPath, (long)offset, (long)length));
        }

        var dataStart = stream.Position;
        var headerSize = dataStart - packageStart;

        if (!readEntries)
            return new PkgPackage { Magic = magic, DataStart = dataStart, HeaderSize = headerSize, Entries = Array.Empty<PkgEntry>() };

        // Resolve offsets to absolute positions and validate against the stream length.
        var resolved = new List<PkgEntry>(raw.Count);
        var streamLength = TryGetLength(stream);

        foreach (var e in raw)
        {
            var absolute = dataStart + e.Offset;
            if (streamLength > 0 && (absolute < dataStart || absolute > streamLength))
                continue; // skip entries pointing outside the file

            var clamped = e.Length;
            if (streamLength > 0 && absolute + clamped > streamLength)
                clamped = Math.Max(0, streamLength - absolute);

            resolved.Add(new PkgEntry
            {
                FullPath = e.FullPath,
                Offset = absolute,
                Length = clamped,
                IsTex = e.FullPath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase),
            });
        }

        return new PkgPackage
        {
            Magic = magic,
            DataStart = dataStart,
            HeaderSize = headerSize,
            Entries = resolved,
        };
    }

    private static long TryGetLength(Stream s)
    {
        try { return s.CanSeek ? s.Length : -1; }
        catch { return -1; }
    }

    public static byte[] ReadEntryBytes(Stream stream, PkgEntry entry)
    {
        if (!stream.CanSeek) throw new NotSupportedException("Stream must be seekable.");
        if (entry.Length <= 0) return Array.Empty<byte>();
        if (entry.Length > int.MaxValue) throw new InvalidDataException("Entry too large to buffer in memory.");

        stream.Position = entry.Offset;

        var buffer = new byte[entry.Length];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read <= 0) break;
            total += read;
        }

        if (total != buffer.Length)
            Array.Resize(ref buffer, total);

        return buffer;
    }
}
