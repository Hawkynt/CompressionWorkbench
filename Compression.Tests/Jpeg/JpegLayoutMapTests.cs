#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;
using FileFormat.JpegArchive;

namespace Compression.Tests.Jpeg;

[TestFixture]
public class JpegLayoutMapTests {

  /// <summary>
  /// Builds a minimal valid JPEG with a known marker sequence:
  /// SOI + APP0(JFIF) + APP1(EXIF) + DQT + SOF0 + DHT + SOS + scan-data + EOI.
  /// </summary>
  private static byte[] BuildMinimalJpeg() {
    using var ms = new MemoryStream();

    // SOI
    ms.Write([0xFF, 0xD8]);

    // APP0 (JFIF) — minimal 16-byte payload
    WriteSegment(ms, 0xE0, "JFIF\0"u8.ToArray().Concat(new byte[11]).ToArray());

    // APP1 (EXIF) — "Exif\0\0" + 8 bytes of fake TIFF data
    WriteSegment(ms, 0xE1, "Exif\0\0"u8.ToArray().Concat(new byte[8]).ToArray());

    // DQT — 65 bytes: precision/table-id byte + 64 quantization values
    WriteSegment(ms, 0xDB, new byte[65]);

    // SOF0 — 11 bytes: precision(1) + height(2) + width(2) + components(1) + 3*(id+sampling+qtable)
    WriteSegment(ms, 0xC0, [0x08, 0x00, 0x08, 0x00, 0x08, 0x01, 0x11, 0x00, 0x02, 0x11, 0x01]);

    // DHT — 29 bytes: class/id(1) + counts(16) + 12 values
    var dhtPayload = new byte[29];
    dhtPayload[0] = 0x00; // DC table 0
    ms.Write([0xFF, 0xC4]);
    WriteBe16(ms, (ushort)(dhtPayload.Length + 2));
    ms.Write(dhtPayload);

    // SOS — 8 bytes header: components(1) + component-spec(2*1) + Ss(1) + Se(1) + Ah/Al(1)
    WriteSegment(ms, 0xDA, [0x01, 0x01, 0x00, 0x00, 0x3F, 0x00]);

    // Scan data — 16 arbitrary bytes (avoid 0xFF to keep it simple)
    ms.Write(new byte[16]);

    // EOI
    ms.Write([0xFF, 0xD9]);

    return ms.ToArray();
  }

  /// <summary>
  /// Builds a JPEG with APP0 before APP1-EXIF (not optimized) to test the optimizer.
  /// </summary>
  private static byte[] BuildUnoptimizedJpeg() {
    using var ms = new MemoryStream();

    // SOI
    ms.Write([0xFF, 0xD8]);

    // APP0 (JFIF) first
    WriteSegment(ms, 0xE0, "JFIF\0"u8.ToArray().Concat(new byte[11]).ToArray());

    // APP13 (IPTC) — some metadata
    WriteSegment(ms, 0xED, new byte[20]);

    // APP1 (EXIF) — NOT first after SOI
    var exifPayload = "Exif\0\0"u8.ToArray().Concat(new byte[8]).ToArray();
    WriteSegment(ms, 0xE1, exifPayload);

    // APP2 (ICC Profile)
    WriteSegment(ms, 0xE2, new byte[12]);

    // DQT
    WriteSegment(ms, 0xDB, new byte[65]);

    // SOF0
    WriteSegment(ms, 0xC0, [0x08, 0x00, 0x08, 0x00, 0x08, 0x01, 0x11, 0x00, 0x02, 0x11, 0x01]);

    // DHT
    WriteSegment(ms, 0xC4, new byte[29]);

    // SOS
    WriteSegment(ms, 0xDA, [0x01, 0x01, 0x00, 0x00, 0x3F, 0x00]);

    // Scan data
    ms.Write(new byte[16]);

    // EOI
    ms.Write([0xFF, 0xD9]);

    return ms.ToArray();
  }

  private static void WriteSegment(MemoryStream ms, byte marker, byte[] payload) {
    ms.Write([0xFF, marker]);
    WriteBe16(ms, (ushort)(payload.Length + 2));
    ms.Write(payload);
  }

  private static void WriteBe16(Stream ms, ushort value) {
    Span<byte> buf = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16BigEndian(buf, value);
    ms.Write(buf);
  }

  // ─────────────────────────────────────────────────────────────────
  // Layout map tests
  // ─────────────────────────────────────────────────────────────────

  [Test]
  public void Descriptor_Implements_IArchiveLayoutMap() {
    var d = new JpegArchiveDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveLayoutMap>());
  }

  [Test]
  public void EnumerateLayout_Returns_Expected_Marker_Tiles() {
    var jpeg = BuildMinimalJpeg();
    using var ms = new MemoryStream(jpeg);

    var tiles = JpegLayoutMap.Enumerate(ms).ToList();

    Assert.That(tiles, Is.Not.Empty);

    // SOI must be first
    Assert.That(tiles[0].FileName, Is.EqualTo("SOI"));
    Assert.That(tiles[0].Kind, Is.EqualTo(DefragBlockKind.MetadataReserved));
    Assert.That(tiles[0].Offset, Is.EqualTo(0));
    Assert.That(tiles[0].Length, Is.EqualTo(2));

    // Check for expected marker names
    var names = tiles.Select(t => t.FileName).ToList();
    Assert.That(names, Does.Contain("JFIF (APP0)"));
    Assert.That(names, Does.Contain("EXIF (APP1)"));
    Assert.That(names, Does.Contain("DQT (Quantization Table)"));
    Assert.That(names, Does.Contain("SOF0 (Start of Frame)"));
    Assert.That(names, Does.Contain("DHT (Huffman Table)"));
    Assert.That(names, Does.Contain("SOS (Scan Header)"));
    Assert.That(names, Does.Contain("Scan Data"));
    Assert.That(names, Does.Contain("EOI"));
  }

  [Test]
  public void EnumerateLayout_EXIF_Is_Hot() {
    var jpeg = BuildMinimalJpeg();
    using var ms = new MemoryStream(jpeg);

    var tiles = JpegLayoutMap.Enumerate(ms).ToList();
    var exifTile = tiles.First(t => t.FileName == "EXIF (APP1)");

    Assert.That(exifTile.Kind, Is.EqualTo(DefragBlockKind.Used));
    Assert.That(exifTile.Classification, Is.EqualTo(DefragBlockClass.Hot));
  }

  [Test]
  public void EnumerateLayout_ICC_Is_Cold() {
    var jpeg = BuildUnoptimizedJpeg();
    using var ms = new MemoryStream(jpeg);

    var tiles = JpegLayoutMap.Enumerate(ms).ToList();
    var iccTile = tiles.First(t => t.FileName == "ICC Profile (APP2)");

    Assert.That(iccTile.Kind, Is.EqualTo(DefragBlockKind.Used));
    Assert.That(iccTile.Classification, Is.EqualTo(DefragBlockClass.Cold));
  }

  [Test]
  public void EnumerateLayout_Comment_Is_Frozen() {
    // Build a JPEG with a COM marker.
    using var ms = new MemoryStream();
    ms.Write([0xFF, 0xD8]);
    WriteSegment(ms, 0xFE, "Hello"u8.ToArray()); // COM
    WriteSegment(ms, 0xDA, [0x01, 0x01, 0x00, 0x00, 0x3F, 0x00]); // SOS
    ms.Write(new byte[4]); // scan data
    ms.Write([0xFF, 0xD9]); // EOI

    ms.Position = 0;
    var tiles = JpegLayoutMap.Enumerate(ms).ToList();
    var comTile = tiles.First(t => t.FileName == "Comment");

    Assert.That(comTile.Kind, Is.EqualTo(DefragBlockKind.Used));
    Assert.That(comTile.Classification, Is.EqualTo(DefragBlockClass.Frozen));
  }

  [Test]
  public void EnumerateLayout_Covers_Full_File() {
    var jpeg = BuildMinimalJpeg();
    using var ms = new MemoryStream(jpeg);

    var tiles = JpegLayoutMap.Enumerate(ms).ToList();
    var totalCovered = tiles.Sum(t => t.Length);

    // Tiles should cover at least 90% of the file.
    Assert.That(totalCovered, Is.GreaterThanOrEqualTo(jpeg.Length * 0.9),
      $"Tiles cover only {totalCovered} of {jpeg.Length} bytes.");
  }

  [Test]
  public void EnumerateLayout_SOI_And_EOI_Are_MetadataReserved() {
    var jpeg = BuildMinimalJpeg();
    using var ms = new MemoryStream(jpeg);

    var tiles = JpegLayoutMap.Enumerate(ms).ToList();
    var soi = tiles.First(t => t.FileName == "SOI");
    var eoi = tiles.First(t => t.FileName == "EOI");

    Assert.That(soi.Kind, Is.EqualTo(DefragBlockKind.MetadataReserved));
    Assert.That(eoi.Kind, Is.EqualTo(DefragBlockKind.MetadataReserved));
  }

  [Test]
  public void EnumerateLayout_XMP_Distinguished_From_EXIF() {
    // Build a JPEG with both EXIF and XMP APP1 segments.
    using var ms = new MemoryStream();
    ms.Write([0xFF, 0xD8]);

    // APP1 EXIF
    WriteSegment(ms, 0xE1, "Exif\0\0"u8.ToArray().Concat(new byte[8]).ToArray());

    // APP1 XMP
    var xmpId = System.Text.Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");
    WriteSegment(ms, 0xE1, xmpId.Concat(System.Text.Encoding.UTF8.GetBytes("<x:xmpmeta/>")).ToArray());

    WriteSegment(ms, 0xDA, [0x01, 0x01, 0x00, 0x00, 0x3F, 0x00]);
    ms.Write(new byte[4]);
    ms.Write([0xFF, 0xD9]);

    ms.Position = 0;
    var tiles = JpegLayoutMap.Enumerate(ms).ToList();
    var names = tiles.Select(t => t.FileName).ToList();

    Assert.That(names, Does.Contain("EXIF (APP1)"));
    Assert.That(names, Does.Contain("XMP (APP1)"));
  }

  // ─────────────────────────────────────────────────────────────────
  // Optimizer tests
  // ─────────────────────────────────────────────────────────────────

  [Test]
  public void Optimize_Moves_EXIF_To_First_After_SOI() {
    var jpeg = BuildUnoptimizedJpeg();
    using var ms = new MemoryStream(jpeg);

    // Before optimization, EXIF is not the first segment.
    var tilesBefore = JpegLayoutMap.Enumerate(ms).ToList();
    var exifBefore = tilesBefore.First(t => t.FileName == "EXIF (APP1)");
    var jfifBefore = tilesBefore.First(t => t.FileName == "JFIF (APP0)");
    Assert.That(exifBefore.Offset, Is.GreaterThan(jfifBefore.Offset),
      "Pre-condition: EXIF should come after JFIF before optimization.");

    // Optimize.
    ms.Position = 0;
    JpegOptimizer.Optimize(ms);

    // After optimization, EXIF should be first after SOI.
    ms.Position = 0;
    var tilesAfter = JpegLayoutMap.Enumerate(ms).ToList();
    var exifAfter = tilesAfter.First(t => t.FileName == "EXIF (APP1)");

    // EXIF should start at offset 2 (right after SOI).
    Assert.That(exifAfter.Offset, Is.EqualTo(2));
  }

  [Test]
  public void Optimize_Preserves_All_Markers() {
    var jpeg = BuildUnoptimizedJpeg();
    using var ms = new MemoryStream(jpeg);

    var namesBefore = JpegLayoutMap.Enumerate(ms).Select(t => t.FileName).OrderBy(n => n).ToList();

    ms.Position = 0;
    JpegOptimizer.Optimize(ms);

    ms.Position = 0;
    var namesAfter = JpegLayoutMap.Enumerate(ms).Select(t => t.FileName).OrderBy(n => n).ToList();

    Assert.That(namesAfter, Is.EqualTo(namesBefore));
  }

  [Test]
  public void Optimize_Result_Is_Valid_JPEG() {
    var jpeg = BuildUnoptimizedJpeg();
    using var ms = new MemoryStream(jpeg);

    JpegOptimizer.Optimize(ms);

    // Verify SOI at start.
    ms.Position = 0;
    Assert.That(ms.ReadByte(), Is.EqualTo(0xFF));
    Assert.That(ms.ReadByte(), Is.EqualTo(0xD8));

    // Verify EOI at end.
    ms.Position = ms.Length - 2;
    Assert.That(ms.ReadByte(), Is.EqualTo(0xFF));
    Assert.That(ms.ReadByte(), Is.EqualTo(0xD9));
  }

  [Test]
  public void Optimize_NoOp_When_EXIF_Already_First() {
    // Build a JPEG with EXIF already first.
    using var ms = new MemoryStream();
    ms.Write([0xFF, 0xD8]);
    WriteSegment(ms, 0xE1, "Exif\0\0"u8.ToArray().Concat(new byte[8]).ToArray());
    WriteSegment(ms, 0xE0, "JFIF\0"u8.ToArray().Concat(new byte[11]).ToArray());
    WriteSegment(ms, 0xDA, [0x01, 0x01, 0x00, 0x00, 0x3F, 0x00]);
    ms.Write(new byte[4]);
    ms.Write([0xFF, 0xD9]);

    var original = ms.ToArray();

    ms.Position = 0;
    JpegOptimizer.Optimize(ms);

    ms.Position = 0;
    var optimized = ms.ToArray();

    Assert.That(optimized, Is.EqualTo(original));
  }

  [Test]
  public void Optimize_NoOp_When_No_EXIF() {
    // JPEG without any EXIF segment.
    using var ms = new MemoryStream();
    ms.Write([0xFF, 0xD8]);
    WriteSegment(ms, 0xE0, "JFIF\0"u8.ToArray().Concat(new byte[11]).ToArray());
    WriteSegment(ms, 0xDA, [0x01, 0x01, 0x00, 0x00, 0x3F, 0x00]);
    ms.Write(new byte[4]);
    ms.Write([0xFF, 0xD9]);

    var original = ms.ToArray();

    ms.Position = 0;
    JpegOptimizer.Optimize(ms);

    ms.Position = 0;
    var optimized = ms.ToArray();

    Assert.That(optimized, Is.EqualTo(original));
  }

  [Test]
  public void Descriptor_Implements_IArchiveDefragmentable() {
    var d = new JpegArchiveDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveDefragmentable>());
  }

  [Test]
  public void Defragment_Via_Descriptor_Moves_EXIF_First() {
    var jpeg = BuildUnoptimizedJpeg();
    using var ms = new MemoryStream(jpeg);

    ((IArchiveDefragmentable)new JpegArchiveDescriptor()).Defragment(ms);

    ms.Position = 0;
    var tiles = JpegLayoutMap.Enumerate(ms).ToList();
    var exif = tiles.First(t => t.FileName == "EXIF (APP1)");
    Assert.That(exif.Offset, Is.EqualTo(2));
  }

  [Test]
  public void Optimize_Preserves_File_Size_Approximately() {
    var jpeg = BuildUnoptimizedJpeg();
    var originalLength = jpeg.Length;

    using var ms = new MemoryStream(jpeg);
    JpegOptimizer.Optimize(ms);

    // Optimization is a reorder, not a compression — size should be identical.
    Assert.That(ms.Length, Is.EqualTo(originalLength));
  }
}
