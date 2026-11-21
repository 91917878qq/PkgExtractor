using System;
using System.Collections.Generic;
using System.Buffers.Binary;
using System.Text;

namespace PkgExtractor.Core;

/// <summary>
/// Minimal MP4 (ISO BMFF) inspector: reads the movie header, the video track header and the
/// sample table so the UI can show real duration / frame rate instead of guessing.
/// Only reads what is needed - no decoding, no transcoding.
/// </summary>
public static class Mp4Probe
{
    public sealed record Info(
        double DurationSeconds,
        double FrameRate,
        int Width,
        int Height,
        string Codec,
        int SampleCount,
        bool HasAudio,
        string Brand);

    /// <summary>Returns null when the bytes are not a readable MP4.</summary>
    public static Info? Probe(byte[] data)
    {
        if (data is null || data.Length < 32) return null;
        if (!IsMp4(data)) return null;

        try
        {
            var brand = ReadBrand(data);
            var moov = FindBox(data, 0, data.Length, "moov");
            if (moov is null) return null;

            double movieDuration = 0;
            double movieScale = 1000;

            var mvhd = FindBox(data, moov.Value.PayloadStart, moov.Value.PayloadEnd, "mvhd");
            if (mvhd is not null)
                ReadMvhd(data, mvhd.Value.PayloadStart, out movieScale, out movieDuration);

            // Walk every trak and keep the first video track.
            Info? result = null;
            var hasAudio = false;

            foreach (var trak in FindAllBoxes(data, moov.Value.PayloadStart, moov.Value.PayloadEnd, "trak"))
            {
                var mdia = FindBox(data, trak.PayloadStart, trak.PayloadEnd, "mdia");
                if (mdia is null) continue;

                var hdlr = FindBox(data, mdia.Value.PayloadStart, mdia.Value.PayloadEnd, "hdlr");
                if (hdlr is null) continue;

                var handler = ReadHandlerType(data, hdlr.Value.PayloadStart, hdlr.Value.PayloadEnd);
                var mdhd = FindBox(data, mdia.Value.PayloadStart, mdia.Value.PayloadEnd, "mdhd");
                if (mdhd is null) continue;

                ReadMdhd(data, mdhd.Value.PayloadStart, out var trackScale, out var trackDuration);

                if (handler == "soun")
                {
                    hasAudio = true;
                    continue;
                }

                if (handler != "vide" || result is not null) continue;

                var minf = FindBox(data, mdia.Value.PayloadStart, mdia.Value.PayloadEnd, "minf");
                if (minf is null) continue;

                var stbl = FindBox(data, minf.Value.PayloadStart, minf.Value.PayloadEnd, "stbl");
                if (stbl is null) continue;

                var width = 0;
                var height = 0;
                var codec = "";

                var stsd = FindBox(data, stbl.Value.PayloadStart, stbl.Value.PayloadEnd, "stsd");
                if (stsd is not null)
                    ReadStsd(data, stsd.Value.PayloadStart, stsd.Value.PayloadEnd, out width, out height, out codec);

                var sampleCount = 0;
                var frameRate = 0d;

                var stts = FindBox(data, stbl.Value.PayloadStart, stbl.Value.PayloadEnd, "stts");
                if (stts is not null)
                    ReadStts(data, stts.Value.PayloadStart, stts.Value.PayloadEnd, trackScale,
                        out sampleCount, out frameRate);

                var seconds = trackScale > 0 ? trackDuration / trackScale : 0;
                if (seconds <= 0 && movieScale > 0) seconds = movieDuration / movieScale;

                result = new Info(seconds, frameRate, width, height, codec, sampleCount, false, brand);
            }

            if (result is null) return null;
            return result with { HasAudio = hasAudio };
        }
        catch
        {
            return null;
        }
    }

    public static bool IsMp4(byte[] data) =>
        data.Length >= 12 &&
        data[4] == (byte)'f' && data[5] == (byte)'t' && data[6] == (byte)'y' && data[7] == (byte)'p';

    private static string ReadBrand(byte[] d)
    {
        var moovLike = FindBox(d, 0, d.Length, "ftyp");
        if (moovLike is null) return "";

        var start = moovLike.Value.PayloadStart;
        if (start + 4 > d.Length) return "";

        return Encoding.ASCII.GetString(d, start, 4).Trim();
    }

    private readonly record struct BoxInfo(int PayloadStart, int PayloadEnd);

    /// <summary>Iterates boxes inside [start,end) and returns the first one with the given type.</summary>
    private static BoxInfo? FindBox(byte[] d, int start, int end, string type)
    {
        foreach (var box in FindAllBoxes(d, start, end, type))
            return box;
        return null;
    }

    private static IEnumerable<BoxInfo> FindAllBoxes(byte[] d, int start, int end, string type)
    {
        var p = start;

        while (p + 8 <= end)
        {
            var size = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(p, 4));
            var name = Encoding.ASCII.GetString(d, p + 4, 4);
            var headerSize = 8;

            long boxSize = size;
            if (size == 1)
            {
                if (p + 16 > end) break;
                boxSize = BinaryPrimitives.ReadInt64BigEndian(d.AsSpan(p + 8, 8));
                headerSize = 16;
            }
            else if (size == 0)
            {
                boxSize = end - p;
            }

            if (boxSize < headerSize || p + boxSize > end) break;

            if (name == type)
                yield return new BoxInfo(p + headerSize, (int)(p + boxSize));

            p += (int)boxSize;
        }
    }

    private static void ReadMvhd(byte[] d, int p, out double timescale, out double duration)
    {
        timescale = 1000;
        duration = 0;

        var version = d[p];
        if (version == 1)
        {
            if (p + 4 + 8 + 8 + 4 > d.Length) return;
            timescale = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p + 20, 4));
            duration = BinaryPrimitives.ReadInt64BigEndian(d.AsSpan(p + 24, 8));
        }
        else
        {
            if (p + 4 + 4 + 4 + 4 > d.Length) return;
            timescale = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p + 12, 4));
            duration = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p + 16, 4));
        }
    }

    private static void ReadMdhd(byte[] d, int p, out double timescale, out double duration)
    {
        timescale = 0;
        duration = 0;

        var version = d[p];
        if (version == 1)
        {
            if (p + 4 + 8 + 8 + 4 > d.Length) return;
            timescale = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p + 20, 4));
            duration = BinaryPrimitives.ReadInt64BigEndian(d.AsSpan(p + 24, 8));
        }
        else
        {
            if (p + 4 + 4 + 4 + 4 > d.Length) return;
            timescale = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p + 12, 4));
            duration = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p + 16, 4));
        }
    }

    private static string ReadHandlerType(byte[] d, int p, int end)
    {
        // hdlr: version(1)+flags(3)+predefined(4)+handler_type(4)
        if (p + 12 > end) return "";
        return Encoding.ASCII.GetString(d, p + 8, 4).Trim();
    }

    private static void ReadStsd(byte[] d, int p, int end, out int width, out int height, out string codec)
    {
        width = 0;
        height = 0;
        codec = "";

        // version/flags(4) + entry_count(4) then the first sample entry
        if (p + 8 + 8 > end) return;

        var entryStart = p + 8;
        var entrySize = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(entryStart, 4));
        if (entrySize < 16 || entryStart + entrySize > end) return;

        codec = Encoding.ASCII.GetString(d, entryStart + 4, 4).Trim();

        // VisualSampleEntry: 8 header + 6 reserved + 2 data_ref + 16 pre_defined/reserved
        // then width(2) height(2) at offset 32 from the entry start.
        var w = entryStart + 32;
        if (w + 4 <= d.Length)
        {
            width = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(w, 2));
            height = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(w + 2, 2));
        }
    }

    private static void ReadStts(byte[] d, int p, int end, double timescale,
        out int sampleCount, out double frameRate)
    {
        sampleCount = 0;
        frameRate = 0;

        if (p + 8 > end) return;

        var entries = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(p + 4, 4));
        if (entries <= 0) return;

        long totalSamples = 0;
        long totalDelta = 0;

        var q = p + 8;
        for (var i = 0; i < entries && q + 8 <= end; i++, q += 8)
        {
            var count = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(q, 4));
            var delta = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(q + 4, 4));
            if (count <= 0) continue;

            totalSamples += count;
            totalDelta += (long)delta * count;
        }

        sampleCount = (int)totalSamples;
        if (totalSamples > 0 && totalDelta > 0 && timescale > 0)
            frameRate = totalSamples * timescale / totalDelta;
    }
}
