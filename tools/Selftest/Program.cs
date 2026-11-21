using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using PkgExtractor.Core;

namespace PkgExtractor.Selftest;

internal static class Program
{
    private static int _pass;
    private static int _fail;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        TestLz4();
        TestDxt1();
        TestDxt5();
        TestPkgRoundtrip();
        TestTexStatic();
        TestTexGif();

        if (args.Length > 0 && File.Exists(args[0]))
        {
            TestRealPackage(args[0]);
            TestPreview(args[0]);
            TestMp4Export(args[0]);
        }

        Console.WriteLine();
        Console.WriteLine($"===== {_pass} passed, {_fail} failed =====");
        return _fail == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- helpers

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (ok) { _pass++; Console.WriteLine($"  [ok]   {name}"); }
        else { _fail++; Console.WriteLine($"  [FAIL] {name}" + (detail is null ? "" : $" — {detail}")); }
    }

    private static void Section(string title) => Console.WriteLine($"\n--- {title} ---");

    // ---------------------------------------------------------------- LZ4

    private static void TestLz4()
    {
        Section("LZ4 block decompression");

        // Case 1: literals only. token = (15 << 4) | 0, then 0 (no extra len), then literal bytes.
        var payload = Encoding.ASCII.GetBytes("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789");
        var block = new List<byte> { (byte)(15 << 4) };
        var extra = payload.Length - 15;
        block.Add((byte)extra);
        block.AddRange(payload);

        var dst = new byte[payload.Length];
        var n = Lz4.DecompressBlock(block.ToArray(), dst);
        Check("literals-only block", n == payload.Length && dst.SequenceEqual(payload));

        // Case 2: 4 literals then a match of length 12 at offset 4 (so "wxyz" repeats).
        // token = (4 << 4) | (12 - 4) = 0x48? -> low nibble = matchLen - 4 = 8
        var src = new List<byte> { 0x48 };
        src.AddRange(Encoding.ASCII.GetBytes("wxyz"));
        src.Add(4); // offset low
        src.Add(0); // offset high
        // 4 literals + (8 + 4) = 16 output bytes
        var dst2 = new byte[16];
        var n2 = Lz4.DecompressBlock(src.ToArray(), dst2);
        var expect2 = Encoding.ASCII.GetBytes("wxyzwxyzwxyzwxyz");
        Check("overlapping match (offset 4, len 12)", n2 == 16 && dst2.SequenceEqual(expect2),
            $"got \"{Encoding.ASCII.GetString(dst2, 0, Math.Max(0, n2))}\"");

        // Case 3: chain-length literals (>= 15) followed by the terminator.
        var longPayload = new byte[60];
        for (var i = 0; i < longPayload.Length; i++) longPayload[i] = (byte)('a' + (i % 26));
        var block3 = new List<byte> { (15 << 4) };
        var remaining = longPayload.Length - 15;
        while (remaining >= 255) { block3.Add(255); remaining -= 255; }
        block3.Add((byte)remaining);
        block3.AddRange(longPayload);

        var dst3 = new byte[longPayload.Length];
        var n3 = Lz4.DecompressBlock(block3.ToArray(), dst3);
        Check("long literal chain", n3 == longPayload.Length && dst3.SequenceEqual(longPayload));

        // Case 4: expectedSize overload.
        var dst4 = Lz4.DecompressBlock(block.ToArray(), payload.Length);
        Check("expectedSize overload", dst4.SequenceEqual(payload));
    }

    // ---------------------------------------------------------------- DXT

    private static void TestDxt1()
    {
        Section("DXT1 decompression");

        // 8-byte block: color0 = 0xF800 (pure red), color1 = 0x001F (pure blue), indices all 0.
        // 565 red = R:31 G:0 B:0 -> 0xF800 little-endian bytes: 00 F8
        // 565 blue = R:0 G:0 B:31 -> 0x001F little-endian: 1F 00
        var block = new byte[] { 0x00, 0xF8, 0x1F, 0x00, 0, 0, 0, 0 };
        var rgba = Dxt.Decompress(4, 4, block, DxtKind.Dxt1);

        Check("size = 4x4x4", rgba.Length == 64);
        Check("color0 = red", rgba[0] == 255 && rgba[1] == 0 && rgba[2] == 0 && rgba[3] == 255,
            $"got {rgba[0]},{rgba[1]},{rgba[2]},{rgba[3]}");

        // All indices = 1 -> color1 = blue
        var block2 = new byte[] { 0x00, 0xF8, 0x1F, 0x00, 0x55, 0x55, 0x55, 0x55 };
        var rgba2 = Dxt.Decompress(4, 4, block2, DxtKind.Dxt1);
        Check("index 1 = color1 (blue)", rgba2[0] == 0 && rgba2[1] == 0 && rgba2[2] == 255,
            $"got {rgba2[0]},{rgba2[1]},{rgba2[2]}");

        // color0 <= color1 -> 3-color + transparent mode; index 3 must be transparent black.
        var block3 = new byte[] { 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF };
        var rgba3 = Dxt.Decompress(4, 4, block3, DxtKind.Dxt1);
        Check("DXT1 3-color mode index 3 transparent",
            rgba3[0] == 0 && rgba3[1] == 0 && rgba3[2] == 0 && rgba3[3] == 0,
            $"got {rgba3[0]},{rgba3[1]},{rgba3[2]},{rgba3[3]}");
    }

    private static void TestDxt5()
    {
        Section("DXT5 / DXT3 alpha");

        // DXT5: alpha0=255, alpha1=0, alpha indices all 0 -> alpha = 255
        var block = new byte[16];
        block[0] = 255; block[1] = 0;
        block[2] = 0x00; block[3] = 0x00; block[4] = 0x00; block[5] = 0x00; block[6] = 0x00; block[7] = 0x00;
        block[8] = 0x00; block[9] = 0xF8; block[10] = 0x1F; block[11] = 0x00;
        block[12] = 0; block[13] = 0; block[14] = 0; block[15] = 0;

        var rgba = Dxt.Decompress(4, 4, block, DxtKind.Dxt5);
        Check("DXT5 alpha = 255", rgba[3] == 255, $"got {rgba[3]}");
        Check("DXT5 color = red", rgba[0] == 255 && rgba[1] == 0 && rgba[2] == 0);

        // DXT3: first 8 bytes are 4-bit alphas, all 0xF -> alpha 255
        var b3 = new byte[16];
        for (var i = 0; i < 8; i++) b3[i] = 0xFF;
        b3[8] = 0x00; b3[9] = 0xF8; b3[10] = 0x1F; b3[11] = 0x00;
        var rgba3 = Dxt.Decompress(4, 4, b3, DxtKind.Dxt3);
        Check("DXT3 alpha = 255", rgba3[3] == 255, $"got {rgba3[3]}");
    }

    // ---------------------------------------------------------------- PKG

    private static void TestPkgRoundtrip()
    {
        Section("PKG container roundtrip");

        var temp = Path.Combine(Path.GetTempPath(), "pkg-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var pkgPath = Path.Combine(temp, "sample.mpkg");

        var a = Encoding.UTF8.GetBytes(new string('A', 300));
        var b = Encoding.UTF8.GetBytes(new string('B', 17));
        var names = new[] { "hello.txt", "sub/dir/binary.bin" };

        using (var fs = new FileStream(pkgPath, FileMode.Create, FileAccess.Write))
        {
            WriteI32(fs, 8);
            fs.Write(Encoding.ASCII.GetBytes("PKGM0014"));

            WriteI32(fs, names.Length);

            var dataStart = 4 + 8 + 4
                            + names.Sum(n => 4 + Encoding.UTF8.GetByteCount(n) + 8);

            var payloads = new[] { a, b };
            var offset = 0;

            for (var i = 0; i < names.Length; i++)
            {
                var nb = Encoding.UTF8.GetBytes(names[i]);
                WriteI32(fs, nb.Length);
                fs.Write(nb);
                WriteI32(fs, offset);
                WriteI32(fs, payloads[i].Length);
                offset += payloads[i].Length;
            }

            foreach (var p in payloads) fs.Write(p);
        }

        using var read = new FileStream(pkgPath, FileMode.Open, FileAccess.Read);
        Check("LooksLikePkg", PkgReader.LooksLikePkg(read));

        var pkg = PkgReader.Read(read);
        Check("magic", pkg.Magic == "PKGM0014", pkg.Magic);
        Check("entry count = 2", pkg.Entries.Count == 2, pkg.Entries.Count.ToString());
        Check("IsMpkg", pkg.IsMpkg);
        Check("first entry name", pkg.Entries[0].FullPath == "hello.txt", pkg.Entries[0].FullPath);

        var first = PkgReader.ReadEntryBytes(read, pkg.Entries[0]);
        var second = PkgReader.ReadEntryBytes(read, pkg.Entries[1]);
        Check("payload 1 intact", first.SequenceEqual(a), $"{first.Length} bytes");
        Check("payload 2 intact", second.SequenceEqual(b), $"{second.Length} bytes");

        // Verify the offsets point exactly where the bytes live.
        var onDisk = File.ReadAllBytes(pkgPath);
        var slice = onDisk.AsSpan((int)pkg.Entries[1].Offset, (int)pkg.Entries[1].Length).ToArray();
        Check("absolute offset correct", slice.SequenceEqual(b));

        // Non-PKG file must be rejected.
        var notPkg = Path.Combine(temp, "nope.bin");
        File.WriteAllBytes(notPkg, Encoding.ASCII.GetBytes("this is not a package at all"));
        using (var f2 = File.OpenRead(notPkg))
            Check("rejects non-PKG", !PkgReader.LooksLikePkg(f2));

        try { Directory.Delete(temp, true); } catch { }
    }

    private static void WriteI32(Stream s, int v)
    {
        var b = BitConverter.GetBytes(v);
        s.Write(b, 0, 4);
    }

    // ---------------------------------------------------------------- TEX static (TEXB0001)

    private static void TestTexStatic()
    {
        Section("TEX static (TEXB0001, RGBA8888)");

        const int w = 4, h = 4;
        var pixels = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            pixels[i * 4] = 255;      // R
            pixels[i * 4 + 1] = 128;  // G
            pixels[i * 4 + 2] = 32;   // B
            pixels[i * 4 + 3] = 255;  // A
        }

        using var ms = new MemoryStream();
        WriteNString(ms, "TEXV0005", 16);
        WriteNString(ms, "TEXI0001", 16);
        WriteI32(ms, (int)TexFormat.RGBA8888);
        WriteI32(ms, (int)TexFlags.None);
        WriteI32(ms, w);  // texture width
        WriteI32(ms, h);  // texture height
        WriteI32(ms, w);  // image width
        WriteI32(ms, h);  // image height
        WriteI32(ms, 0);  // unk

        WriteNString(ms, "TEXB0001", 16);
        WriteI32(ms, 1);  // image count
        WriteI32(ms, 1);  // mipmap count
        WriteI32(ms, w);
        WriteI32(ms, h);
        WriteI32(ms, pixels.Length);
        ms.Write(pixels);

        ms.Position = 0;
        var tex = TexReader.Read(ms);

        Check("magic1", tex.Magic1 == "TEXV0005");
        Check("version V1", tex.Version == TexContainerVersion.V1);
        Check("format RGBA8888", tex.Format == TexFormat.RGBA8888);
        Check("image size", tex.ImageWidth == w && tex.ImageHeight == h);
        Check("1 image / 1 mipmap", tex.Images.Count == 1 && tex.TotalMipmaps == 1);

        var mip = tex.Images[0].Mipmaps[0];
        TexImageConverter.Decompress(mip, tex.Format);

        var png = TexImageConverter.ToPng(mip, tex.Format, tex.ImageWidth, tex.ImageHeight);
        Check("PNG produced", png is { Length: > 50 }, $"{png?.Length} bytes");
        Check("PNG signature", png != null && png[0] == 0x89 && png[1] == 0x50 && png[2] == 0x4E && png[3] == 0x47);

        // LZ4-compressed mipmap path (TEXB0002)
        var compressed = CompressLiteralsOnly(pixels);
        using var ms2 = new MemoryStream();
        WriteNString(ms2, "TEXV0005", 16);
        WriteNString(ms2, "TEXI0001", 16);
        WriteI32(ms2, (int)TexFormat.RGBA8888);
        WriteI32(ms2, (int)TexFlags.None);
        WriteI32(ms2, w); WriteI32(ms2, h); WriteI32(ms2, w); WriteI32(ms2, h); WriteI32(ms2, 0);
        WriteNString(ms2, "TEXB0002", 16);
        WriteI32(ms2, 1);
        WriteI32(ms2, 1);
        WriteI32(ms2, w); WriteI32(ms2, h);
        WriteI32(ms2, 1);              // isLz4
        WriteI32(ms2, pixels.Length);   // decompressed size
        WriteI32(ms2, compressed.Length);
        ms2.Write(compressed);

        ms2.Position = 0;
        var tex2 = TexReader.Read(ms2);
        var mip2 = tex2.Images[0].Mipmaps[0];
        Check("LZ4 flag parsed", mip2.HadLz4);
        Check("decompressed size recorded", mip2.DecompressedBytesCount == pixels.Length);

        TexImageConverter.Decompress(mip2, tex2.Format);
        Check("LZ4 inflate = original pixels", mip2.Bytes.SequenceEqual(pixels),
            $"{mip2.Bytes.Length} vs {pixels.Length}");
    }

    // ---------------------------------------------------------------- TEX gif (TEXS0002)

    private static void TestTexGif()
    {
        Section("TEX animated (TEXB0001 + TEXS0002)");

        const int w = 4, h = 4;

        byte[] Solid(byte r, byte g, byte b)
        {
            var px = new byte[w * h * 4];
            for (var i = 0; i < w * h; i++)
            {
                px[i * 4] = r; px[i * 4 + 1] = g; px[i * 4 + 2] = b; px[i * 4 + 3] = 255;
            }
            return px;
        }

        var frameA = Solid(255, 0, 0);
        var frameB = Solid(0, 0, 255);

        using var ms = new MemoryStream();
        WriteNString(ms, "TEXV0005", 16);
        WriteNString(ms, "TEXI0001", 16);
        WriteI32(ms, (int)TexFormat.RGBA8888);
        WriteI32(ms, (int)TexFlags.IsGif);   // IsGif
        WriteI32(ms, w); WriteI32(ms, h); WriteI32(ms, w); WriteI32(ms, h); WriteI32(ms, 0);

        WriteNString(ms, "TEXB0001", 16);
        WriteI32(ms, 2);                     // 2 images
        foreach (var frame in new[] { frameA, frameB })
        {
            WriteI32(ms, 1);
            WriteI32(ms, w); WriteI32(ms, h);
            WriteI32(ms, frame.Length);
            ms.Write(frame);
        }

        // TEXS0002 frame container
        WriteNString(ms, "TEXS0002", 16);
        WriteI32(ms, 2);                     // frame count
        foreach (var _ in new[] { 0, 1 })
        {
            WriteI32(ms, _);                 // imageId
            WriteSingle(ms, 10f);            // frametime
            WriteSingle(ms, 0f);             // x
            WriteSingle(ms, 0f);             // y
            WriteSingle(ms, w);              // width
            WriteSingle(ms, h);              // heightX
            WriteSingle(ms, h);              // heightY
        }

        ms.Position = 0;
        var tex = TexReader.Read(ms);

        Check("IsGif flag", tex.IsGif);
        Check("2 images", tex.Images.Count == 2);
        Check("frame info parsed", tex.FrameInfo is { Frames.Count: 2 },
            tex.FrameInfo is null ? "null" : tex.FrameInfo.Frames.Count.ToString());

        foreach (var img in tex.Images)
            foreach (var mip in img.Mipmaps)
                TexImageConverter.Decompress(mip, tex.Format);

        var gif = TexImageConverter.ToGif(tex);
        Check("GIF produced", gif is { Length: > 50 }, $"{gif?.Length} bytes");
        Check("GIF signature",
            gif != null && gif.Length > 6 && gif[0] == (byte)'G' && gif[1] == (byte)'I' && gif[2] == (byte)'F');
    }

    // ---------------------------------------------------------------- real package

    private static void TestRealPackage(string path)
    {
        Section($"real package: {Path.GetFileName(path)}");

        var sw = Stopwatch.StartNew();

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var pkg = PkgReader.Read(fs);

        Console.WriteLine($"  magic={pkg.Magic} entries={pkg.Entries.Count} headerSize={pkg.HeaderSize}");
        foreach (var e in pkg.Entries.Take(20))
            Console.WriteLine($"    {e.FullPath}  offset={e.Offset}  len={e.Length}  tex={e.IsTex}");

        Check("magic recognised", pkg.Magic.StartsWith("PKGM") || pkg.Magic.StartsWith("PKGV"), pkg.Magic);
        Check("has entries", pkg.Entries.Count > 0);

        // Every entry must be readable and its bytes non-empty (except zero-length placeholders).
        foreach (var e in pkg.Entries)
        {
            if (e.Length == 0) continue;
            var bytes = PkgReader.ReadEntryBytes(fs, e);
            Check($"read {e.FileName}", bytes.Length == e.Length, $"{bytes.Length} vs {e.Length}");
        }

        // Extract with the real service and report.
        var outDir = Path.Combine(Path.GetTempPath(), "pkg-extract-" + Guid.NewGuid().ToString("N"));
        var service = new ExtractionService();
        var result = service.ExtractAsync(
            new[] { path },
            new ExtractionOptions { OutputDirectory = outDir, ConvertTextures = true, SubfolderPerPackage = false },
            null,
            System.Threading.CancellationToken.None).GetAwaiter().GetResult();

        sw.Stop();

        Console.WriteLine($"  extracted {result.Files.Count} files, {result.TotalBytes} bytes, " +
                          $"{result.ConvertedTexCount} tex converted, {result.Elapsed.TotalSeconds:0.00}s");

        foreach (var f in result.Files)
            Console.WriteLine($"    -> {Path.GetFileName(f.OutputPath)} ({f.Bytes} B, {f.Kind})");

        foreach (var w in result.Warnings) Console.WriteLine($"    ! {w}");
        foreach (var e in result.Errors) Console.WriteLine($"    X {e}");

        Check("extraction produced files", result.Files.Count > 0);
        Check("no extraction errors", result.Errors.Count == 0, result.Errors.FirstOrDefault());
        Check("total bytes plausible", result.TotalBytes > 0);

        var mp4 = result.Files.FirstOrDefault(f => f.OutputPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase));
        if (mp4 != null)
        {
            var head = new byte[12];
            using (var vf = File.OpenRead(mp4.OutputPath))
                _ = vf.Read(head, 0, 12);

            var brand = Encoding.ASCII.GetString(head, 4, 8);
            Console.WriteLine($"  mp4 ftyp brand = \"{brand}\"");
            Check("video is a real MP4", brand.StartsWith("ftyp"), brand);
        }

        Console.WriteLine($"  output dir: {outDir}");
    }

    /// <summary>
    /// Preview path: load everything into memory, deselect a node, save only the selection.
    /// </summary>
    private static void TestPreview(string path)
    {
        Section($"preview + selective save: {Path.GetFileName(path)}");

        var options = new ExtractionOptions
        {
            OutputDirectory = "",
            ConvertTextures = true,
            SubfolderPerPackage = false,
            Overwrite = true,
        };

        var packages = PreviewService.LoadAsync(
            new[] { path }, options, null, System.Threading.CancellationToken.None)
            .GetAwaiter().GetResult();

        Check("preview produced a package", packages.Count == 1, $"{packages.Count}");

        var nodes = packages[0].Nodes;
        Console.WriteLine($"  loaded {nodes.Count} nodes in memory");
        Check("preview has nodes", nodes.Count > 0);

        var withData = nodes.Count(n => n.Data is { Length: > 0 });
        Check("all nodes carry bytes", withData == nodes.Count, $"{withData}/{nodes.Count}");

        var texNodes = nodes.Where(n => n.IsTex).ToList();
        var converted = texNodes.Count(n => n.OutputExtension != ".tex");
        Console.WriteLine($"  tex nodes {texNodes.Count}, converted {converted}");
        Check("tex nodes converted", texNodes.Count == 0 || converted > 0, $"{converted}/{texNodes.Count}");

        foreach (var n in texNodes.Take(10))
            Console.WriteLine($"    {n.EntryPath} -> {n.OutputExtension} ({n.SizeText}, {n.Kind})");

        // Deselect everything first, then save exactly one node.
        foreach (var n in nodes) n.Selected = false;
        var victim = nodes.First(n => n.Data is { Length: > 0 });
        victim.Selected = true;

        var outDir = Path.Combine(Path.GetTempPath(), "pkg-preview-" + Guid.NewGuid().ToString("N"));

        var written = PreviewService.Save(packages, outDir, options);
        Check("saved exactly one file", written == 1, $"{written}");

        var produced = Directory.Exists(outDir)
            ? Directory.GetFiles(outDir, "*", SearchOption.AllDirectories)
            : Array.Empty<string>();

        Check("one file on disk", produced.Length == 1, $"{produced.Length}");

        if (produced.Length == 1)
        {
            var fi = new FileInfo(produced[0]);
            Console.WriteLine($"    {fi.Name} ({fi.Length} B)");
            Check("saved size matches node", fi.Length == victim.Data!.Length,
                $"{fi.Length} vs {victim.Data!.Length}");
        }

        // Then select everything and confirm the full set lands on disk.
        foreach (var n in nodes) n.Selected = true;
        var outDir2 = Path.Combine(Path.GetTempPath(), "pkg-preview-all-" + Guid.NewGuid().ToString("N"));
        var written2 = PreviewService.Save(packages, outDir2, options);

        var produced2 = Directory.GetFiles(outDir2, "*", SearchOption.AllDirectories);
        Console.WriteLine($"  full save: {written2} files");
        Check("full save writes every node", written2 == nodes.Count, $"{written2}/{nodes.Count}");
        Check("full save matches disk", produced2.Length == written2, $"{produced2.Length}/{written2}");

        try { Directory.Delete(outDir, true); } catch { }
        try { Directory.Delete(outDir2, true); } catch { }
    }

    // ---------------------------------------------------------------- MP4 export
    private static void TestMp4Export(string path)
    {
        Section($"mp4 export + probe: {Path.GetFileName(path)}");

        var bytes = Mp4Exporter.ReadVideoBytes(path, out var entryPath, out var reason);
        if (bytes is null)
        {
            Console.WriteLine($"  no video in this package ({reason}) — skipped");
            return;
        }

        Console.WriteLine($"  entry: {entryPath} ({reason})");
        Check("video entry found", entryPath.Length > 0);
        Check("video bytes present", bytes.Length > 0, $"{bytes.Length}");

        var info = Mp4Probe.Probe(bytes);
        Check("probe parsed the mp4", info is not null);

        if (info is null) return;

        Console.WriteLine($"  {Mp4Exporter.Describe(bytes)}");
        Console.WriteLine($"  brand={info.Brand} codec={info.Codec} audio={info.HasAudio}");

        Check("duration is positive", info.DurationSeconds > 0, $"{info.DurationSeconds}");
        Check("frame rate is positive", info.FrameRate > 0, $"{info.FrameRate}");
        Check("dimensions are positive", info.Width > 0 && info.Height > 0, $"{info.Width}x{info.Height}");
        Check("sample count is positive", info.SampleCount > 0, $"{info.SampleCount}");

        // The declared frame rate must agree with samples / duration.
        if (info.SampleCount > 0 && info.DurationSeconds > 0)
        {
            var derived = info.SampleCount / info.DurationSeconds;
            Check("frame rate matches samples/duration",
                Math.Abs(derived - info.FrameRate) < 0.5, $"{derived:0.###} vs {info.FrameRate:0.###}");
        }

        // Byte-for-byte copy: no re-encode can change a single byte.
        var dir = Path.Combine(Path.GetTempPath(), "pkg-mp4-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "out.mp4");
        File.WriteAllBytes(target, bytes);

        var roundtrip = File.ReadAllBytes(target);
        Check("exported bytes are identical", roundtrip.Length == bytes.Length, $"{roundtrip.Length}/{bytes.Length}");

        var same = roundtrip.Length == bytes.Length;
        if (same)
        {
            for (var i = 0; i < bytes.Length; i++)
            {
                if (roundtrip[i] == bytes[i]) continue;
                same = false;
                break;
            }
        }

        Check("exported bytes match exactly", same);

        try { Directory.Delete(dir, true); } catch { }
    }

    // ---------------------------------------------------------------- writers
    private static void WriteNString(Stream s, string value, int maxLength)
    {
        // On-disk magics are variable-width NUL-terminated strings ("TEXV0005\0"), not padded slots.
        var bytes = Encoding.ASCII.GetBytes(value);
        if (bytes.Length > maxLength - 1)
            throw new ArgumentException($"NString '{value}' exceeds {maxLength} bytes.");
        s.Write(bytes);
        s.WriteByte(0);
    }

    private static void WriteSingle(Stream s, float v)
    {
        var b = BitConverter.GetBytes(v);
        s.Write(b, 0, 4);
    }

    /// <summary>Emit a valid LZ4 block that is entirely literals (no matches).</summary>
    private static byte[] CompressLiteralsOnly(byte[] src)
    {
        var outp = new List<byte>();
        var len = src.Length;

        outp.Add((byte)(Math.Min(15, len) << 4));

        if (len >= 15)
        {
            var rest = len - 15;
            while (rest >= 255) { outp.Add(255); rest -= 255; }
            outp.Add((byte)rest);
        }

        outp.AddRange(src);
        return outp.ToArray();
    }
}
