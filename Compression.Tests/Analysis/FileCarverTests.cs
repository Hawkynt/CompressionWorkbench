#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Analysis;

namespace Compression.Tests.Analysis;

/// <summary>
/// Tests for <see cref="FileCarver"/> — the PhotoRec-style file recovery
/// component that scans arbitrary binary data for recoverable payloads.
/// </summary>
[TestFixture]
public class FileCarverTests {

  // ── fixtures ───────────────────────────────────────────────────────

  /// <summary>Build a minimal valid JPEG (SOI + trivial marker + EOI).</summary>
  private static byte[] BuildMinimalJpeg(int padBytes = 200) {
    using var ms = new MemoryStream();
    ms.Write([0xFF, 0xD8]);                               // SOI
    ms.Write([0xFF, 0xE0, 0x00, 0x10]);                   // APP0 marker + length=16
    ms.Write("JFIF\0"u8);
    ms.Write([0x01, 0x02, 0x00, 0x00, 0x48, 0x00, 0x48, 0x00, 0x00]);  // JFIF body
    for (var i = 0; i < padBytes; ++i) ms.WriteByte((byte)(i & 0xFE));  // avoid 0xFF
    ms.Write([0xFF, 0xD9]);                               // EOI
    return ms.ToArray();
  }

  /// <summary>Build a minimal valid PNG (magic + IHDR + IDAT + IEND).</summary>
  private static byte[] BuildMinimalPng() {
    using var ms = new MemoryStream();
    ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);  // magic
    WritePngChunk(ms, "IHDR", [0, 0, 0, 1, 0, 0, 0, 1, 8, 0, 0, 0, 0]);  // 1x1 greyscale
    WritePngChunk(ms, "IDAT", [0x78, 0x9C, 0x62, 0x00, 0x00, 0x00, 0x00, 0x01]); // trivial deflate
    WritePngChunk(ms, "IEND", []);
    return ms.ToArray();
  }

  private static void WritePngChunk(Stream ms, string type, ReadOnlySpan<byte> data) {
    Span<byte> lenBuf = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(lenBuf, (uint)data.Length);
    ms.Write(lenBuf);
    ms.Write(System.Text.Encoding.ASCII.GetBytes(type));
    ms.Write(data);
    ms.Write([0, 0, 0, 0]);  // CRC placeholder (never verified by carver)
  }

  /// <summary>Build a minimal valid ZIP (one entry + central directory + EOCD).</summary>
  private static byte[] BuildMinimalZip() {
    using var ms = new MemoryStream();
    // Local file header
    ms.Write([0x50, 0x4B, 0x03, 0x04]);
    ms.Write([0x14, 0x00, 0x00, 0x00, 0x00, 0x00]);
    ms.Write(new byte[4]);    // dostime
    ms.Write(new byte[4]);    // crc
    ms.Write(new byte[4]);    // csize
    ms.Write(new byte[4]);    // usize
    ms.Write([0x05, 0x00]);   // name len
    ms.Write([0x00, 0x00]);   // extra len
    ms.Write("a.txt"u8);

    // Central directory
    var cdStart = ms.Position;
    ms.Write([0x50, 0x4B, 0x01, 0x02]);
    ms.Write([0x14, 0x00, 0x14, 0x00, 0x00, 0x00, 0x00, 0x00]);
    ms.Write(new byte[4]);
    ms.Write(new byte[4]);
    ms.Write(new byte[4]);
    ms.Write(new byte[4]);
    ms.Write([0x05, 0x00]);
    ms.Write([0x00, 0x00, 0x00, 0x00]);
    ms.Write([0x00, 0x00, 0x00, 0x00]);
    ms.Write(new byte[4]);
    ms.Write(new byte[4]);
    ms.Write("a.txt"u8);
    var cdEnd = ms.Position;

    // EOCD
    ms.Write([0x50, 0x4B, 0x05, 0x06]);
    ms.Write(new byte[4]);
    Span<byte> entryCount = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(entryCount, 1);
    ms.Write(entryCount); ms.Write(entryCount);
    Span<byte> cdSize = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(cdSize, (uint)(cdEnd - cdStart));
    ms.Write(cdSize);
    Span<byte> cdOffset = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(cdOffset, (uint)cdStart);
    ms.Write(cdOffset);
    ms.Write([0x00, 0x00]);   // comment len
    return ms.ToArray();
  }

  /// <summary>Build a minimal GZIP member (trivial deflate of empty data).</summary>
  private static byte[] BuildMinimalGzip() {
    using var ms = new MemoryStream();
    ms.Write([0x1F, 0x8B, 0x08, 0x00]);       // magic + method + flags
    ms.Write([0x00, 0x00, 0x00, 0x00]);       // mtime
    ms.Write([0x00, 0xFF]);                    // xfl + OS
    ms.Write([0x03, 0x00]);                    // empty deflate block
    ms.Write([0x00, 0x00, 0x00, 0x00]);       // CRC32 of empty
    ms.Write([0x00, 0x00, 0x00, 0x00]);       // ISIZE = 0
    return ms.ToArray();
  }

  /// <summary>Build a minimal FLAC stream (magic + minimal STREAMINFO + terminator).</summary>
  private static byte[] BuildMinimalFlac() {
    using var ms = new MemoryStream();
    ms.Write("fLaC"u8);
    ms.WriteByte(0x80);                      // last metadata block, type 0 (STREAMINFO)
    ms.Write([0x00, 0x00, 0x22]);           // length = 34
    for (var i = 0; i < 34; ++i) ms.WriteByte(0);
    return ms.ToArray();
  }

  /// <summary>Build a minimal MP4 (ftyp box).</summary>
  private static byte[] BuildMinimalMp4() {
    using var ms = new MemoryStream();
    Span<byte> size = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(size, 24);   // full box size
    ms.Write(size);
    ms.Write("ftyp"u8);
    ms.Write("isom"u8);                      // brand
    ms.Write([0x00, 0x00, 0x02, 0x00]);     // minor version
    ms.Write("mp41"u8);                      // compat brand
    return ms.ToArray();
  }

  private static byte[] BuildGarbage(int length, byte seed = 0xCC) {
    var buf = new byte[length];
    // Deterministic, avoids all common magics.
    for (var i = 0; i < length; ++i) buf[i] = (byte)((seed + (i * 37)) & 0xFE);
    // Explicitly zero out any pattern that might accidentally match a 2-byte magic.
    for (var i = 0; i + 1 < length; ++i) {
      if (buf[i] == 0xFF && (buf[i + 1] == 0xD8 || buf[i + 1] == 0xD9)) buf[i + 1] ^= 1;
      if (buf[i] == 0x50 && buf[i + 1] == 0x4B) buf[i + 1] ^= 1;
      if (buf[i] == 0x1F && buf[i + 1] == 0x8B) buf[i + 1] ^= 1;
      if (buf[i] == 0x89 && buf[i + 1] == 0x50) buf[i + 1] ^= 1;
    }
    return buf;
  }

  // ── tests ──────────────────────────────────────────────────────────

  [Test]
  public void SynthesizedCorruptImage_RecoversKnownFiles() {
    var jpeg = BuildMinimalJpeg();
    var png = BuildMinimalPng();
    var zip = BuildMinimalZip();
    using var ms = new MemoryStream();
    ms.Write(BuildGarbage(100_000));
    var jpegOffset = (int)ms.Position;
    ms.Write(jpeg);
    ms.Write(BuildGarbage(400_000, 0xAB));
    var pngOffset = (int)ms.Position;
    ms.Write(png);
    ms.Write(BuildGarbage(500_000, 0x7E));
    var zipOffset = (int)ms.Position;
    ms.Write(zip);
    ms.Write(BuildGarbage(50_000, 0x3A));

    var buffer = ms.ToArray();
    var carver = new FileCarver { Options = new CarveOptions { MinFileSize = 16, ExtractData = true } };
    var hits = carver.CarveBuffer(buffer);

    Assert.That(hits.Any(h => h.FormatId == "Jpeg" && h.Offset == jpegOffset), Is.True,
      $"expected JPEG at 0x{jpegOffset:X}, got: [{string.Join(", ", hits.Select(h => $"{h.FormatId}@0x{h.Offset:X}"))}]");
    Assert.That(hits.Any(h => h.FormatId == "Png" && h.Offset == pngOffset), Is.True,
      $"expected PNG at 0x{pngOffset:X}");
    Assert.That(hits.Any(h => h.FormatId == "Zip" && h.Offset == zipOffset), Is.True,
      $"expected ZIP at 0x{zipOffset:X}");

    // Extracted bytes should start with the expected magics.
    var jpegHit = hits.First(h => h.FormatId == "Jpeg" && h.Offset == jpegOffset);
    Assert.That(jpegHit.Data, Is.Not.Null);
    Assert.That(jpegHit.Data![0], Is.EqualTo((byte)0xFF));
    Assert.That(jpegHit.Data[1], Is.EqualTo((byte)0xD8));

    var pngHit = hits.First(h => h.FormatId == "Png" && h.Offset == pngOffset);
    Assert.That(pngHit.Data, Is.Not.Null);
    Assert.That(pngHit.Data![0], Is.EqualTo((byte)0x89));

    var zipHit = hits.First(h => h.FormatId == "Zip" && h.Offset == zipOffset);
    Assert.That(zipHit.Data, Is.Not.Null);
    Assert.That(zipHit.Data![0], Is.EqualTo((byte)'P'));
    Assert.That(zipHit.Data[1], Is.EqualTo((byte)'K'));
  }

  [Test]
  public void OverlappingHits_AreDeduplicated() {
    // Two adjacent PNGs, 10 bytes apart.
    var png = BuildMinimalPng();
    using var ms = new MemoryStream();
    ms.Write(BuildGarbage(1000));
    var first = (int)ms.Position;
    ms.Write(png);
    ms.Write(BuildGarbage(10));
    var second = (int)ms.Position;
    ms.Write(png);
    ms.Write(BuildGarbage(1000));

    var carver = new FileCarver { Options = new CarveOptions { MinFileSize = 16 } };
    var hits = carver.CarveBuffer(ms.ToArray());
    var pngs = hits.Where(h => h.FormatId == "Png").ToList();
    Assert.That(pngs.Count, Is.EqualTo(2),
      $"expected 2 PNG hits, got {pngs.Count}: {string.Join(", ", pngs.Select(p => $"@0x{p.Offset:X}/{p.Length}"))}");
    Assert.That(pngs[0].Offset, Is.EqualTo(first));
    Assert.That(pngs[1].Offset, Is.EqualTo(second));
  }

  [Test]
  public void BelowMinSize_Skipped() {
    // A JPEG fragment: valid SOI+APP0 but truncated well before 128 bytes.
    using var ms = new MemoryStream();
    ms.Write(BuildGarbage(500));
    ms.Write([0xFF, 0xD8, 0xFF, 0xE0]);        // SOI + APP0 — matches builtin magic
    ms.Write(BuildGarbage(30));                 // 30 bytes of payload
    ms.Write([0xFF, 0xD9]);                     // end-of-image (~36 bytes total)
    ms.Write(BuildGarbage(500));

    var strict = new FileCarver { Options = new CarveOptions { MinFileSize = 128 } };
    var hits = strict.CarveBuffer(ms.ToArray());
    Assert.That(hits.Any(h => h.FormatId == "Jpeg"), Is.False,
      "tiny JPEG should be skipped at MinFileSize=128");

    var lax = new FileCarver { Options = new CarveOptions { MinFileSize = 16 } };
    var hits2 = lax.CarveBuffer(ms.ToArray());
    Assert.That(hits2.Any(h => h.FormatId == "Jpeg"), Is.True,
      "tiny JPEG should be detected at MinFileSize=16");
  }

  [Test]
  public void TruncatedFile_DetectsButEndsAtBufferBoundary() {
    // A JPEG that starts but has no EOI before the buffer ends.
    using var ms = new MemoryStream();
    ms.Write(BuildGarbage(500));
    var jpegOffset = (int)ms.Position;
    ms.Write([0xFF, 0xD8]);
    ms.Write([0xFF, 0xE0, 0x00, 0x10]);       // APP0 marker
    ms.Write("JFIF\0"u8);
    // No EOI — write 500 bytes of non-FF bytes to end of buffer.
    for (var i = 0; i < 500; ++i) ms.WriteByte(0x42);

    var carver = new FileCarver { Options = new CarveOptions { MinFileSize = 16 } };
    var hits = carver.CarveBuffer(ms.ToArray());
    var jpeg = hits.FirstOrDefault(h => h.FormatId == "Jpeg");
    Assert.That(jpeg, Is.Not.Null, "expected truncated JPEG to still emit a hit");
    Assert.That(jpeg!.Offset, Is.EqualTo(jpegOffset));
    Assert.That(jpeg.Length, Is.LessThanOrEqualTo(ms.Length - jpegOffset));
    // Confidence should be reduced because we had to fall back.
    Assert.That(jpeg.Confidence, Is.LessThan(1.0));
  }

  [Test]
  public void MultiFormat_Mixed() {
    var jpeg = BuildMinimalJpeg();
    var png = BuildMinimalPng();
    var mp4 = BuildMinimalMp4();
    var zip = BuildMinimalZip();
    var gzip = BuildMinimalGzip();
    var flac = BuildMinimalFlac();

    using var ms = new MemoryStream();
    ms.Write(BuildGarbage(500));
    ms.Write(jpeg);
    ms.Write(BuildGarbage(500));
    ms.Write(png);
    ms.Write(BuildGarbage(500));
    ms.Write(mp4);
    ms.Write(BuildGarbage(500));
    ms.Write(zip);
    ms.Write(BuildGarbage(500));
    ms.Write(gzip);
    ms.Write(BuildGarbage(500));
    ms.Write(flac);
    ms.Write(BuildGarbage(500));

    var carver = new FileCarver { Options = new CarveOptions { MinFileSize = 16 } };
    var hits = carver.CarveBuffer(ms.ToArray());
    var ids = hits.Select(h => h.FormatId).ToHashSet();

    Assert.Multiple(() => {
      Assert.That(ids.Contains("Jpeg"), Is.True, "JPEG not found");
      Assert.That(ids.Contains("Png"), Is.True, "PNG not found");
      Assert.That(ids.Contains("Mp4"), Is.True, "MP4 not found");
      Assert.That(ids.Contains("Zip"), Is.True, "ZIP not found");
      Assert.That(ids.Contains("Gzip"), Is.True, "GZIP not found");
      Assert.That(ids.Contains("Flac"), Is.True, "FLAC not found");
    });
  }

  // ── output sink ────────────────────────────────────────────────────

  [Test]
  public void OutputSink_WritesFilesToDisk() {
    var jpeg = BuildMinimalJpeg();
    var png = BuildMinimalPng();
    using var ms = new MemoryStream();
    ms.Write(BuildGarbage(200));
    var jpegOffset = (int)ms.Position;
    ms.Write(jpeg);
    ms.Write(BuildGarbage(200));
    var pngOffset = (int)ms.Position;
    ms.Write(png);
    ms.Write(BuildGarbage(200));
    var data = ms.ToArray();

    var carver = new FileCarver { Options = new CarveOptions { MinFileSize = 16 } };
    var hits = carver.CarveBuffer(data);

    var outDir = Path.Combine(Path.GetTempPath(), "carve_sink_" + Guid.NewGuid().ToString("N"));
    try {
      using var src = new MemoryStream(data, writable: false);
      var written = FileCarverOutputSink.ExtractAll(src, hits, outDir);
      Assert.That(written.Count, Is.EqualTo(hits.Count));
      foreach (var path in written) Assert.That(File.Exists(path), Is.True, $"missing {path}");
      // File names should embed the offset.
      var names = written.Select(Path.GetFileName).ToList();
      Assert.That(names.Any(n => n!.Contains($"0x{jpegOffset:X8}")), Is.True,
        $"no file name embeds jpegOffset 0x{jpegOffset:X8}: {string.Join(", ", names)}");
      Assert.That(names.Any(n => n!.Contains($"0x{pngOffset:X8}")), Is.True);
    }
    finally {
      if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
    }
  }
}
