#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.PngCrushAdapters;

namespace Compression.Tests.Png;

[TestFixture]
public class PngLayoutMapTests {

  /// <summary>The canonical 8-byte PNG signature.</summary>
  private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

  /// <summary>
  /// Builds a minimal valid PNG file with optional extra chunks for testing.
  /// Layout: signature + IHDR + [extra chunks] + IDAT + IEND.
  /// </summary>
  private static MemoryStream BuildTestPng(params string[] extraChunkTypes) {
    var ms = new MemoryStream();

    // PNG signature
    ms.Write(PngSignature, 0, 8);

    // IHDR: 13 bytes of header data (width 1, height 1, bit depth 8, color type 2 = RGB)
    var ihdrData = new byte[13];
    BinaryPrimitives.WriteUInt32BigEndian(ihdrData.AsSpan(0), 1); // width
    BinaryPrimitives.WriteUInt32BigEndian(ihdrData.AsSpan(4), 1); // height
    ihdrData[8] = 8;  // bit depth
    ihdrData[9] = 2;  // color type (RGB)
    WriteChunk(ms, "IHDR", ihdrData);

    // Extra chunks (before IDAT by default in this builder)
    foreach (var type in extraChunkTypes) {
      var payload = Encoding.ASCII.GetBytes($"test-{type}");
      WriteChunk(ms, type, payload);
    }

    // IDAT: minimal compressed image data (1x1 RGB, zlib compressed)
    // A single pixel (3 bytes) with filter byte 0: [0x00, R, G, B]
    // zlib compressed: 78 01 63 F8 CF C0 00 00 00 04 00 01 (from zlib deflate of [0,255,0,0])
    var idatData = new byte[] { 0x78, 0x01, 0x63, 0xF8, 0xCF, 0xC0, 0x00, 0x00, 0x00, 0x04, 0x00, 0x01 };
    WriteChunk(ms, "IDAT", idatData);

    // IEND: no data
    WriteChunk(ms, "IEND", []);

    ms.Position = 0;
    return ms;
  }

  /// <summary>
  /// Builds a PNG where chunks appear in non-optimal order:
  /// signature + IHDR + IDAT + tEXt + eXIf + IEND
  /// (metadata after IDAT instead of before).
  /// </summary>
  private static MemoryStream BuildPngWithMetadataAfterIdat() {
    var ms = new MemoryStream();
    ms.Write(PngSignature, 0, 8);

    var ihdrData = new byte[13];
    BinaryPrimitives.WriteUInt32BigEndian(ihdrData.AsSpan(0), 1);
    BinaryPrimitives.WriteUInt32BigEndian(ihdrData.AsSpan(4), 1);
    ihdrData[8] = 8;
    ihdrData[9] = 2;
    WriteChunk(ms, "IHDR", ihdrData);

    // IDAT first (before metadata)
    var idatData = new byte[] { 0x78, 0x01, 0x63, 0xF8, 0xCF, 0xC0, 0x00, 0x00, 0x00, 0x04, 0x00, 0x01 };
    WriteChunk(ms, "IDAT", idatData);

    // Metadata after IDAT
    WriteChunk(ms, "tEXt", Encoding.ASCII.GetBytes("Comment\0Test"));
    WriteChunk(ms, "eXIf", new byte[10]);

    WriteChunk(ms, "IEND", []);

    ms.Position = 0;
    return ms;
  }

  /// <summary>
  /// Builds a PNG with chunks in non-spec order: signature + tEXt + IHDR + IDAT + IEND.
  /// IHDR is not first — the optimizer must fix this.
  /// </summary>
  private static MemoryStream BuildPngWithIhdrNotFirst() {
    var ms = new MemoryStream();
    ms.Write(PngSignature, 0, 8);

    // tEXt before IHDR (invalid order)
    WriteChunk(ms, "tEXt", Encoding.ASCII.GetBytes("Comment\0Early"));

    var ihdrData = new byte[13];
    BinaryPrimitives.WriteUInt32BigEndian(ihdrData.AsSpan(0), 1);
    BinaryPrimitives.WriteUInt32BigEndian(ihdrData.AsSpan(4), 1);
    ihdrData[8] = 8;
    ihdrData[9] = 2;
    WriteChunk(ms, "IHDR", ihdrData);

    var idatData = new byte[] { 0x78, 0x01, 0x63, 0xF8, 0xCF, 0xC0, 0x00, 0x00, 0x00, 0x04, 0x00, 0x01 };
    WriteChunk(ms, "IDAT", idatData);

    WriteChunk(ms, "IEND", []);

    ms.Position = 0;
    return ms;
  }

  private static void WriteChunk(Stream stream, string type, byte[] data) {
    Span<byte> buf = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(buf, (uint)data.Length);
    stream.Write(buf);
    stream.Write(Encoding.ASCII.GetBytes(type));
    stream.Write(data);
    // CRC (placeholder — we don't validate CRC in tests)
    stream.Write(stackalloc byte[4]);
  }

  /// <summary>Reads chunk types in order from the stream.</summary>
  private static List<string> ReadChunkOrder(Stream stream) {
    stream.Position = 8; // skip signature
    var types = new List<string>();
    var header = new byte[8];
    while (stream.Position + 12 <= stream.Length) {
      if (stream.Read(header, 0, 8) < 8) break;
      var dataLen = (long)BinaryPrimitives.ReadUInt32BigEndian(header);
      var type = Encoding.ASCII.GetString(header, 4, 4);
      types.Add(type);
      stream.Position += dataLen + 4; // skip data + CRC
      if (type == "IEND") break;
    }
    return types;
  }

  // ──────────────────────────────────────────────────────────────────────
  // Layout Map Tests
  // ──────────────────────────────────────────────────────────────────────

  [Test]
  public void EnumerateChunks_HasPngSignature() {
    using var ms = BuildTestPng();
    var chunks = PngLayoutMap.Enumerate(ms).ToList();

    var sig = chunks.FirstOrDefault(c => c.FileName == "PNG signature");
    Assert.That(sig, Is.Not.Null);
    Assert.That(sig!.Offset, Is.EqualTo(0));
    Assert.That(sig.Length, Is.EqualTo(8));
    Assert.That(sig.Kind, Is.EqualTo(DefragBlockKind.MetadataReserved));
  }

  [Test]
  public void EnumerateChunks_HasIhdr() {
    using var ms = BuildTestPng();
    var chunks = PngLayoutMap.Enumerate(ms).ToList();

    var ihdr = chunks.FirstOrDefault(c => c.FileName != null && c.FileName.Contains("IHDR"));
    Assert.That(ihdr, Is.Not.Null);
    Assert.That(ihdr!.Kind, Is.EqualTo(DefragBlockKind.MetadataReserved));
  }

  [Test]
  public void EnumerateChunks_HasIdat() {
    using var ms = BuildTestPng();
    var chunks = PngLayoutMap.Enumerate(ms).ToList();

    var idat = chunks.FirstOrDefault(c => c.FileName != null && c.FileName.Contains("IDAT"));
    Assert.That(idat, Is.Not.Null);
    Assert.That(idat!.Kind, Is.EqualTo(DefragBlockKind.Used));
    Assert.That(idat.Classification, Is.EqualTo(DefragBlockClass.Normal));
  }

  [Test]
  public void EnumerateChunks_HasIend() {
    using var ms = BuildTestPng();
    var chunks = PngLayoutMap.Enumerate(ms).ToList();

    var iend = chunks.FirstOrDefault(c => c.FileName != null && c.FileName.Contains("IEND"));
    Assert.That(iend, Is.Not.Null);
    Assert.That(iend!.Kind, Is.EqualTo(DefragBlockKind.MetadataReserved));
  }

  [Test]
  public void EnumerateChunks_ExifIsHot() {
    using var ms = BuildTestPng("eXIf");
    var chunks = PngLayoutMap.Enumerate(ms).ToList();

    var exif = chunks.FirstOrDefault(c => c.FileName != null && c.FileName.Contains("eXIf"));
    Assert.That(exif, Is.Not.Null);
    Assert.That(exif!.Kind, Is.EqualTo(DefragBlockKind.Used));
    Assert.That(exif.Classification, Is.EqualTo(DefragBlockClass.Hot));
  }

  [Test]
  public void EnumerateChunks_TextIsCold() {
    using var ms = BuildTestPng("tEXt");
    var chunks = PngLayoutMap.Enumerate(ms).ToList();

    var text = chunks.FirstOrDefault(c => c.FileName != null && c.FileName.Contains("tEXt"));
    Assert.That(text, Is.Not.Null);
    Assert.That(text!.Kind, Is.EqualTo(DefragBlockKind.Used));
    Assert.That(text.Classification, Is.EqualTo(DefragBlockClass.Cold));
  }

  [Test]
  public void EnumerateChunks_IccpIsCold() {
    using var ms = BuildTestPng("iCCP");
    var chunks = PngLayoutMap.Enumerate(ms).ToList();

    var iccp = chunks.FirstOrDefault(c => c.FileName != null && c.FileName.Contains("iCCP"));
    Assert.That(iccp, Is.Not.Null);
    Assert.That(iccp!.Classification, Is.EqualTo(DefragBlockClass.Cold));
  }

  [Test]
  public void EnumerateChunks_DisplayHintsAreReserved() {
    using var ms = BuildTestPng("gAMA", "pHYs", "sRGB");
    var chunks = PngLayoutMap.Enumerate(ms).ToList();

    var gama = chunks.FirstOrDefault(c => c.FileName != null && c.FileName.Contains("gAMA"));
    Assert.That(gama, Is.Not.Null);
    Assert.That(gama!.Kind, Is.EqualTo(DefragBlockKind.MetadataReserved));

    var phys = chunks.FirstOrDefault(c => c.FileName != null && c.FileName.Contains("pHYs"));
    Assert.That(phys, Is.Not.Null);
    Assert.That(phys!.Kind, Is.EqualTo(DefragBlockKind.MetadataReserved));
  }

  [Test]
  public void EnumerateChunks_CoversFullFile() {
    using var ms = BuildTestPng("tEXt", "eXIf");
    var chunks = PngLayoutMap.Enumerate(ms).ToList();

    var totalCovered = chunks.Sum(c => c.Length);
    Assert.That(totalCovered, Is.EqualTo(ms.Length),
      "All chunks should cover the full file length");
  }

  [Test]
  public void EnumerateChunks_ChunksAreContiguous() {
    using var ms = BuildTestPng("tEXt", "eXIf", "iCCP");
    var chunks = PngLayoutMap.Enumerate(ms).OrderBy(c => c.Offset).ToList();

    for (var i = 1; i < chunks.Count; i++) {
      var prev = chunks[i - 1];
      var curr = chunks[i];
      Assert.That(curr.Offset, Is.EqualTo(prev.Offset + prev.Length),
        $"Gap between chunk '{prev.FileName}' and '{curr.FileName}'");
    }
  }

  [Test]
  public void EnumerateChunks_EmptyStream_ReturnsNothing() {
    using var ms = new MemoryStream();
    var chunks = PngLayoutMap.Enumerate(ms).ToList();
    Assert.That(chunks, Is.Empty);
  }

  // ──────────────────────────────────────────────────────────────────────
  // Optimizer Tests
  // ──────────────────────────────────────────────────────────────────────

  [Test]
  public void Optimize_IhdrAlwaysFirst() {
    using var ms = BuildPngWithIhdrNotFirst();

    PngOptimizer.Optimize(ms);

    var types = ReadChunkOrder(ms);
    Assert.That(types[0], Is.EqualTo("IHDR"), "IHDR must be the first chunk after optimization");
  }

  [Test]
  public void Optimize_IendAlwaysLast() {
    using var ms = BuildPngWithMetadataAfterIdat();

    PngOptimizer.Optimize(ms);

    var types = ReadChunkOrder(ms);
    Assert.That(types[^1], Is.EqualTo("IEND"), "IEND must be the last chunk after optimization");
  }

  [Test]
  public void Optimize_DefaultMovesMetadataBeforeIdat() {
    using var ms = BuildPngWithMetadataAfterIdat();

    PngOptimizer.Optimize(ms);

    var types = ReadChunkOrder(ms);
    var idatIdx = types.IndexOf("IDAT");
    var exifIdx = types.IndexOf("eXIf");
    var textIdx = types.IndexOf("tEXt");

    // eXIf defaults to before IDAT
    Assert.That(exifIdx, Is.LessThan(idatIdx),
      "eXIf should be before IDAT in default optimization");
    // tEXt defaults to before IDAT
    Assert.That(textIdx, Is.LessThan(idatIdx),
      "tEXt should be before IDAT in default optimization");
  }

  [Test]
  public void Optimize_DataFirstProfile_MovesMetadataAfterIdat() {
    using var ms = BuildTestPng("eXIf", "tEXt");

    PngOptimizer.Optimize(ms, MetadataPlacementProfile.DataFirst);

    var types = ReadChunkOrder(ms);
    var idatIdx = types.IndexOf("IDAT");
    var exifIdx = types.IndexOf("eXIf");
    var textIdx = types.IndexOf("tEXt");

    Assert.That(exifIdx, Is.GreaterThan(idatIdx),
      "eXIf should be after IDAT with DataFirst profile");
    Assert.That(textIdx, Is.GreaterThan(idatIdx),
      "tEXt should be after IDAT with DataFirst profile");
  }

  [Test]
  public void Optimize_RemoveProfile_DropsChunk() {
    using var ms = BuildTestPng("eXIf", "tEXt");
    var originalLength = ms.Length;

    var removeExif = new MetadataPlacementProfile {
      Name = "Remove EXIF",
      Rules = [new MetadataPlacementRule("eXIf", PlacementZone.Remove)],
    };

    PngOptimizer.Optimize(ms, removeExif);

    Assert.That(ms.Length, Is.LessThan(originalLength), "File should be shorter after removing eXIf");

    var types = ReadChunkOrder(ms);
    Assert.That(types, Does.Not.Contain("eXIf"), "eXIf should be removed");
    Assert.That(types, Does.Contain("tEXt"), "tEXt should still be present");
  }

  [Test]
  public void Optimize_AlreadyOptimal_IsNoOp() {
    using var ms = BuildTestPng("tEXt");
    var originalBytes = ms.ToArray();

    PngOptimizer.Optimize(ms);

    Assert.That(ms.ToArray(), Is.EqualTo(originalBytes),
      "Already-optimal PNG should not be modified");
  }

  [Test]
  public void Optimize_PreservesIdatData() {
    using var ms = BuildPngWithMetadataAfterIdat();

    // Read original IDAT data
    ms.Position = 0;
    var originalIdatData = ExtractChunkData(ms, "IDAT");

    PngOptimizer.Optimize(ms);

    // Read optimized IDAT data
    ms.Position = 0;
    var optimizedIdatData = ExtractChunkData(ms, "IDAT");

    Assert.That(optimizedIdatData, Is.EqualTo(originalIdatData),
      "IDAT image data should be preserved after optimization");
  }

  [Test]
  public void Optimize_CriticalChunksBeforeIdat() {
    // Build PNG with PLTE and gAMA that appear after IDAT
    var ms = new MemoryStream();
    ms.Write(PngSignature, 0, 8);

    var ihdrData = new byte[13];
    BinaryPrimitives.WriteUInt32BigEndian(ihdrData.AsSpan(0), 1);
    BinaryPrimitives.WriteUInt32BigEndian(ihdrData.AsSpan(4), 1);
    ihdrData[8] = 8;
    ihdrData[9] = 2;
    WriteChunk(ms, "IHDR", ihdrData);

    var idatData = new byte[] { 0x78, 0x01, 0x63, 0xF8, 0xCF, 0xC0, 0x00, 0x00, 0x00, 0x04, 0x00, 0x01 };
    WriteChunk(ms, "IDAT", idatData);

    // Critical chunks after IDAT (wrong position)
    WriteChunk(ms, "gAMA", new byte[4]);
    WriteChunk(ms, "PLTE", new byte[3]); // 1 palette entry (3 bytes)

    WriteChunk(ms, "IEND", []);
    ms.Position = 0;

    PngOptimizer.Optimize(ms);

    var types = ReadChunkOrder(ms);
    var idatIdx = types.IndexOf("IDAT");
    var gamaIdx = types.IndexOf("gAMA");
    var plteIdx = types.IndexOf("PLTE");

    Assert.That(gamaIdx, Is.LessThan(idatIdx),
      "gAMA (critical) should be before IDAT after optimization");
    Assert.That(plteIdx, Is.LessThan(idatIdx),
      "PLTE (critical) should be before IDAT after optimization");
  }

  // ──────────────────────────────────────────────────────────────────────
  // Descriptor Tests
  // ──────────────────────────────────────────────────────────────────────

  [Test]
  public void Descriptor_ImplementsIFileInternalLayoutMap() {
    var d = new PngFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IFileInternalLayoutMap>());
  }

  [Test]
  public void Descriptor_ImplementsIFileInternalChunkMover() {
    var d = new PngFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IFileInternalChunkMover>());
  }

  // ──────────────────────────────────────────────────────────────────────
  // MetadataPlacementProfile Tests
  // ──────────────────────────────────────────────────────────────────────

  [Test]
  public void MetadataFirst_HasExpectedRules() {
    var profile = MetadataPlacementProfile.MetadataFirst;
    Assert.That(profile.GetZone("eXIf"), Is.EqualTo(PlacementZone.BeforeData));
    Assert.That(profile.GetZone("moov"), Is.EqualTo(PlacementZone.BeforeData));
    Assert.That(profile.GetZone("idx1"), Is.EqualTo(PlacementZone.BeforeData));
  }

  [Test]
  public void DataFirst_HasExpectedRules() {
    var profile = MetadataPlacementProfile.DataFirst;
    Assert.That(profile.GetZone("eXIf"), Is.EqualTo(PlacementZone.AfterData));
    Assert.That(profile.GetZone("moov"), Is.EqualTo(PlacementZone.AfterData));
    Assert.That(profile.GetZone("idx1"), Is.EqualTo(PlacementZone.AfterData));
  }

  [Test]
  public void Default_ReturnsNullForAllTypes() {
    var profile = MetadataPlacementProfile.Default;
    Assert.That(profile.GetZone("eXIf"), Is.Null);
    Assert.That(profile.GetZone("moov"), Is.Null);
    Assert.That(profile.GetZone("anything"), Is.Null);
  }

  // ──────────────────────────────────────────────────────────────────────
  // Helpers
  // ──────────────────────────────────────────────────────────────────────

  private static byte[] ExtractChunkData(Stream stream, string targetType) {
    stream.Position = 8; // skip signature
    var header = new byte[8];
    while (stream.Position + 12 <= stream.Length) {
      if (stream.Read(header, 0, 8) < 8) break;
      var dataLen = (int)BinaryPrimitives.ReadUInt32BigEndian(header);
      var type = Encoding.ASCII.GetString(header, 4, 4);
      if (type == targetType) {
        var data = new byte[dataLen];
        stream.ReadExactly(data, 0, dataLen);
        return data;
      }
      stream.Position += dataLen + 4; // skip data + CRC
      if (type == "IEND") break;
    }
    return [];
  }
}
