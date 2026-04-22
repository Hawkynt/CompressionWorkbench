#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Analysis;

namespace Compression.Tests.Analysis;

[TestFixture]
public class PayloadCarverTests {

  // Builds a fixture buffer: [0x200 bytes deterministic non-magic garbage]
  //                         [tiny ZIP] [0x200 bytes garbage] [tiny GZIP member]
  // Uses only formats that have a registered MagicSignature in our SignatureDatabase.
  private static (byte[] Buffer, long ZipOffset, long GzipOffset) BuildFixture() {
    using var ms = new MemoryStream();
    var garbage = new byte[0x200];
    for (var i = 0; i < garbage.Length; ++i) garbage[i] = 0xCC;
    ms.Write(garbage, 0, 0x100);

    // Minimal ZIP: one empty file entry + central directory + EOCD.
    var zipOffset = ms.Position;
    var zipBytes = BuildMinimalZip();
    ms.Write(zipBytes);

    // Garbage
    ms.Write(garbage, 0, 0x200);

    // Minimal GZIP member: 1F 8B 08 + FLG=0 + 4-byte mtime + XFL + OS + empty deflate + CRC + ISIZE.
    var gzipOffset = ms.Position;
    ms.Write([0x1F, 0x8B, 0x08, 0x00]);                 // magic + method + flags
    ms.Write([0x00, 0x00, 0x00, 0x00]);                 // mtime
    ms.Write([0x00, 0xFF]);                             // xfl + OS unknown
    // Empty stored deflate block: 0x03 0x00 = final stored block of length 0 (actually 01 00 00 FF FF is fixed-Huffman empty; simpler to use dynamic-block empty = 0x03 0x00).
    ms.Write([0x03, 0x00]);
    ms.Write([0x00, 0x00, 0x00, 0x00]);                 // CRC32 of empty = 0
    ms.Write([0x00, 0x00, 0x00, 0x00]);                 // ISIZE = 0

    return (ms.ToArray(), zipOffset, gzipOffset);
  }

  private static byte[] BuildMinimalZip() {
    using var ms = new MemoryStream();
    // Local file header for one entry: "PK\x03\x04" + fields; entry name "a.txt", zero-byte body.
    Write(ms, [0x50, 0x4B, 0x03, 0x04]);
    Write(ms, [0x14, 0x00, 0x00, 0x00, 0x00, 0x00]);  // version 2.0, no flags, no compression
    Write(ms, new byte[4]);                              // dostime
    Write(ms, new byte[4]);                              // CRC-32
    Write(ms, new byte[4]);                              // compressed size
    Write(ms, new byte[4]);                              // uncompressed size
    Write(ms, [0x05, 0x00]);                             // file name length = 5
    Write(ms, [0x00, 0x00]);                             // extra length = 0
    Write(ms, "a.txt"u8.ToArray());

    // Central directory entry: "PK\x01\x02"
    var cdStart = ms.Position;
    Write(ms, [0x50, 0x4B, 0x01, 0x02]);
    Write(ms, [0x14, 0x00, 0x14, 0x00, 0x00, 0x00, 0x00, 0x00]);
    Write(ms, new byte[4]);                              // dostime
    Write(ms, new byte[4]);                              // CRC
    Write(ms, new byte[4]);                              // comp size
    Write(ms, new byte[4]);                              // uncomp size
    Write(ms, [0x05, 0x00]);                             // name length
    Write(ms, [0x00, 0x00, 0x00, 0x00]);                 // extra + comment length
    Write(ms, [0x00, 0x00, 0x00, 0x00]);                 // disk start + internal attr
    Write(ms, new byte[4]);                              // external attr
    Write(ms, new byte[4]);                              // local header offset
    Write(ms, "a.txt"u8.ToArray());
    var cdEnd = ms.Position;

    // EOCD: "PK\x05\x06"
    Write(ms, [0x50, 0x4B, 0x05, 0x06]);
    Write(ms, new byte[4]);                              // disk numbers
    Span<byte> entryCount = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(entryCount, 1);
    Write(ms, entryCount);
    Write(ms, entryCount);
    Span<byte> cdSize = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(cdSize, (uint)(cdEnd - cdStart));
    Write(ms, cdSize);
    Span<byte> cdOffset = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(cdOffset, (uint)cdStart);
    Write(ms, cdOffset);
    Write(ms, [0x00, 0x00]);                             // comment length = 0
    return ms.ToArray();
  }

  private static void Write(Stream ms, ReadOnlySpan<byte> bytes) => ms.Write(bytes);

  [Test]
  public void Carve_FindsEmbeddedZipAndGzip() {
    var (buffer, zipOffset, gzipOffset) = BuildFixture();
    var results = PayloadCarver.Carve(buffer);

    Assert.That(results.Any(r => r.Offset == zipOffset && r.FormatId == "Zip"), Is.True,
      $"expected ZIP at offset {zipOffset:X}");
    Assert.That(results.Any(r => r.Offset == gzipOffset && r.FormatId == "Gzip"), Is.True,
      $"expected GZIP at offset {gzipOffset:X}");
  }

  [Test]
  public void Carve_ExtractedDataMatchesSource() {
    var (buffer, zipOffset, _) = BuildFixture();
    var results = PayloadCarver.Carve(buffer);
    var zip = results.First(r => r.FormatId == "Zip" && r.Offset == zipOffset);

    Assert.That(zip.Data, Is.Not.Null);
    Assert.That(zip.Data![0], Is.EqualTo((byte)'P'));
    Assert.That(zip.Data[1], Is.EqualTo((byte)'K'));
    // Last 4+ bytes should contain the EOCD signature.
    var hasEocd = false;
    for (var i = 0; i + 4 <= zip.Data.Length; ++i) {
      if (zip.Data[i] == 'P' && zip.Data[i + 1] == 'K' &&
          zip.Data[i + 2] == 0x05 && zip.Data[i + 3] == 0x06) { hasEocd = true; break; }
    }
    Assert.That(hasEocd, Is.True);
  }

  [Test]
  public void Carve_RespectsMinConfidence() {
    var (buffer, _, _) = BuildFixture();
    var strict = PayloadCarver.Carve(buffer, new PayloadCarver.CarveOptions(MinConfidence: 0.99));
    var lax = PayloadCarver.Carve(buffer, new PayloadCarver.CarveOptions(MinConfidence: 0.0));
    Assert.That(lax.Count, Is.GreaterThanOrEqualTo(strict.Count));
  }

  [Test]
  public void Carve_FormatFilter_FiltersHits() {
    var (buffer, _, _) = BuildFixture();
    var zipOnly = PayloadCarver.Carve(buffer,
      new PayloadCarver.CarveOptions(FormatFilter: ["Zip"]));
    Assert.That(zipOnly.All(r => r.FormatId == "Zip"), Is.True);
  }

  [Test]
  public void Extract_WritesFiles() {
    var (buffer, _, _) = BuildFixture();
    var results = PayloadCarver.Carve(buffer);
    var outDir = Path.Combine(Path.GetTempPath(), "carve_test_" + Guid.NewGuid().ToString("N"));
    try {
      var files = PayloadCarver.Extract(results, outDir);
      Assert.That(files.Count, Is.EqualTo(results.Count));
      foreach (var f in files) Assert.That(File.Exists(f), Is.True);
    } finally {
      if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
    }
  }
}
