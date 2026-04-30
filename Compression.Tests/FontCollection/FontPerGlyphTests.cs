#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.FontCollection;

namespace Compression.Tests.FontCollection;

/// <summary>
/// Tests for the per-glyph SVG splitter (Wave C of plan jiggly-cooking-walrus).
/// Uses a hand-synthesised TTF (3 simple-outline glyphs A, B, C plus 1 composite)
/// to validate the descriptor's archive-view emission of FULL/metadata/glyphs.
/// </summary>
[TestFixture]
public class FontPerGlyphTests {

  // ── Minimal-TTF builder ──────────────────────────────────────────────────────
  // Builds a font with:
  //   gid 0  = .notdef (empty)
  //   gid 1  = simple triangle    (mapped to U+0041 'A')
  //   gid 2  = simple square      (mapped to U+0042 'B')
  //   gid 3  = simple line        (mapped to U+0043 'C')
  //   gid 4  = composite refs gid 1 (mapped to U+0044 'D')

  private static byte[] BuildSyntheticTtf() {
    var glyphRecords = new List<byte[]> {
      Array.Empty<byte>(), // .notdef — empty
      MakeTriangleGlyph(),
      MakeSquareGlyph(),
      MakeLineGlyph(),
      MakeCompositeGlyph(referencedGid: 1),
    };
    var locaOffsets = new uint[glyphRecords.Count + 1];
    var glyfBuf = new MemoryStream();
    for (var i = 0; i < glyphRecords.Count; ++i) {
      locaOffsets[i] = (uint)glyfBuf.Position;
      glyfBuf.Write(glyphRecords[i]);
      // 4-byte align
      while ((glyfBuf.Position & 3) != 0) glyfBuf.WriteByte(0);
    }
    locaOffsets[^1] = (uint)glyfBuf.Position;
    var glyfBytes = glyfBuf.ToArray();

    // 'loca' long format (32-bit offsets).
    var locaBuf = new MemoryStream();
    foreach (var off in locaOffsets) WriteU32Be(locaBuf, off);
    var locaBytes = locaBuf.ToArray();

    var headBytes = MakeHeadTable(unitsPerEm: 1024, indexToLocFormatLong: true);
    var maxpBytes = MakeMaxpV05Table(numGlyphs: (ushort)glyphRecords.Count);
    var cmapBytes = MakeCmapFormat4Table();
    var nameBytes = MakeNameTable("TestFont");

    var tables = new (string Tag, byte[] Data)[] {
      ("cmap", cmapBytes),
      ("glyf", glyfBytes),
      ("head", headBytes),
      ("loca", locaBytes),
      ("maxp", maxpBytes),
      ("name", nameBytes),
    };

    return BuildSfnt(0x00010000u, tables);
  }

  private static byte[] BuildSfnt(uint sfntVersion, (string Tag, byte[] Data)[] tables) {
    var numTables = (ushort)tables.Length;
    var headerSize = 12 + 16 * numTables;
    var totalArea = 0;
    foreach (var t in tables) totalArea += (t.Data.Length + 3) & ~3;
    var output = new byte[headerSize + totalArea];

    var ms = new MemoryStream(output);
    WriteU32Be(ms, sfntVersion);
    WriteU16Be(ms, numTables);
    WriteU16Be(ms, 0); WriteU16Be(ms, 0); WriteU16Be(ms, 0); // search range/entry selector/range shift

    var dataPos = headerSize;
    foreach (var t in tables) {
      ms.Write(Encoding.ASCII.GetBytes(t.Tag));
      WriteU32Be(ms, 0u); // checksum (not validated by our reader)
      WriteU32Be(ms, (uint)dataPos);
      WriteU32Be(ms, (uint)t.Data.Length);
      Buffer.BlockCopy(t.Data, 0, output, dataPos, t.Data.Length);
      dataPos += (t.Data.Length + 3) & ~3;
    }
    return output;
  }

  // A simple glyph: 3-point triangle, all on-curve, one contour. Coordinates
  // chosen to exercise both M and L SVG path commands.
  private static byte[] MakeTriangleGlyph() {
    var ms = new MemoryStream();
    WriteI16Be(ms, 1);                // numContours
    WriteI16Be(ms, 0); WriteI16Be(ms, 0); WriteI16Be(ms, 100); WriteI16Be(ms, 100); // bbox
    WriteU16Be(ms, 2);                // endPtsOfContours[0] = 2 (3 points)
    WriteU16Be(ms, 0);                // instrLength = 0

    // 3 flags, all on-curve (0x01), x/y are int16 (no x/yShort, no x/yIsSameOrPositive).
    ms.WriteByte(0x01); ms.WriteByte(0x01); ms.WriteByte(0x01);
    // x: 0, +50 (delta), -50 (back to 0)
    WriteI16Be(ms, 0); WriteI16Be(ms, 50); WriteI16Be(ms, -50);
    // y: 0, +100, -100
    WriteI16Be(ms, 0); WriteI16Be(ms, 100); WriteI16Be(ms, -100);
    return ms.ToArray();
  }

  // Square: 4 on-curve points. Exercises only M + L (no Q).
  private static byte[] MakeSquareGlyph() {
    var ms = new MemoryStream();
    WriteI16Be(ms, 1);
    WriteI16Be(ms, 0); WriteI16Be(ms, 0); WriteI16Be(ms, 100); WriteI16Be(ms, 100);
    WriteU16Be(ms, 3);
    WriteU16Be(ms, 0);
    ms.WriteByte(0x01); ms.WriteByte(0x01); ms.WriteByte(0x01); ms.WriteByte(0x01);
    WriteI16Be(ms, 0); WriteI16Be(ms, 100); WriteI16Be(ms, 0); WriteI16Be(ms, -100);
    WriteI16Be(ms, 0); WriteI16Be(ms, 0); WriteI16Be(ms, 100); WriteI16Be(ms, 0);
    return ms.ToArray();
  }

  // Line glyph with one off-curve control → exercises Q SVG path commands.
  private static byte[] MakeLineGlyph() {
    var ms = new MemoryStream();
    WriteI16Be(ms, 1);
    WriteI16Be(ms, 0); WriteI16Be(ms, 0); WriteI16Be(ms, 100); WriteI16Be(ms, 100);
    WriteU16Be(ms, 2);                // 3 points: on, off, on
    WriteU16Be(ms, 0);
    ms.WriteByte(0x01); ms.WriteByte(0x00); ms.WriteByte(0x01);
    WriteI16Be(ms, 0); WriteI16Be(ms, 50); WriteI16Be(ms, 50);
    WriteI16Be(ms, 0); WriteI16Be(ms, 100); WriteI16Be(ms, -100);
    return ms.ToArray();
  }

  // Composite glyph (numContours = -1) referencing one component glyph.
  private static byte[] MakeCompositeGlyph(int referencedGid) {
    var ms = new MemoryStream();
    WriteI16Be(ms, -1);
    WriteI16Be(ms, 0); WriteI16Be(ms, 0); WriteI16Be(ms, 100); WriteI16Be(ms, 100);
    // flags = 0 (no MORE_COMPONENTS, args are signed bytes)
    WriteU16Be(ms, 0);
    WriteU16Be(ms, (ushort)referencedGid);
    ms.WriteByte(0x00); ms.WriteByte(0x00); // arg1, arg2 (signed bytes since CArgsAreWords=0)
    return ms.ToArray();
  }

  // Standard 54-byte 'head' table.
  private static byte[] MakeHeadTable(ushort unitsPerEm, bool indexToLocFormatLong) {
    var ms = new MemoryStream();
    WriteU32Be(ms, 0x00010000); // version 1.0
    WriteU32Be(ms, 0);           // fontRevision
    WriteU32Be(ms, 0);           // checkSumAdjustment
    WriteU32Be(ms, 0x5F0F3CF5);  // magicNumber
    WriteU16Be(ms, 0);           // flags
    WriteU16Be(ms, unitsPerEm);  // unitsPerEm
    for (var i = 0; i < 16; ++i) ms.WriteByte(0);  // created/modified (longDateTime ×2)
    WriteI16Be(ms, 0); WriteI16Be(ms, 0); WriteI16Be(ms, 100); WriteI16Be(ms, 100); // xMin/yMin/xMax/yMax
    WriteU16Be(ms, 0);  // macStyle
    WriteU16Be(ms, 7);  // lowestRecPPEM
    WriteI16Be(ms, 2);  // fontDirectionHint
    WriteI16Be(ms, indexToLocFormatLong ? (short)1 : (short)0); // indexToLocFormat
    WriteI16Be(ms, 0);  // glyphDataFormat
    return ms.ToArray();
  }

  private static byte[] MakeMaxpV05Table(ushort numGlyphs) {
    var ms = new MemoryStream();
    WriteU32Be(ms, 0x00005000); // version 0.5
    WriteU16Be(ms, numGlyphs);
    return ms.ToArray();
  }

  // Format-4 cmap that maps U+0041..U+0044 → gid 1..4.
  private static byte[] MakeCmapFormat4Table() {
    // Subtable layout for format 4 with 2 segments (chars 0x0041–0x0044, 0xFFFF):
    var ms = new MemoryStream();

    // cmap header
    WriteU16Be(ms, 0);    // version
    WriteU16Be(ms, 1);    // numSubtables
    WriteU16Be(ms, 3);    // platformID = Windows
    WriteU16Be(ms, 1);    // encodingID = Unicode BMP
    WriteU32Be(ms, 12);   // offset (bytes from cmap start)

    // Subtable starts here at offset 12.
    var segCount = (ushort)2;
    var segCountX2 = (ushort)(segCount * 2);
    var entrySelector = (ushort)0;
    var searchRange = (ushort)2;
    var rangeShift = (ushort)0;

    // length: 14-byte fixed header + endCodes + reservedPad + startCodes + idDelta + idRangeOffset.
    var length = (ushort)(14 + 2 + 8 * segCount);
    WriteU16Be(ms, 4);             // format
    WriteU16Be(ms, length);        // length
    WriteU16Be(ms, 0);             // language
    WriteU16Be(ms, segCountX2);    // segCountX2
    WriteU16Be(ms, searchRange);
    WriteU16Be(ms, entrySelector);
    WriteU16Be(ms, rangeShift);

    // endCodes
    WriteU16Be(ms, 0x0044); WriteU16Be(ms, 0xFFFF);
    WriteU16Be(ms, 0); // reservedPad

    // startCodes
    WriteU16Be(ms, 0x0041); WriteU16Be(ms, 0xFFFF);

    // idDelta — chosen so 0x41+delta = 1, 0x42+delta = 2, 0x43+delta = 3, 0x44+delta = 4
    // delta = 1 - 0x41 = -64 (i.e. uint16 0xFFC0) → wraps via uint16 modulo arithmetic.
    WriteI16Be(ms, unchecked((short)(1 - 0x0041))); // -64
    WriteI16Be(ms, 1); // last segment 0xFFFF idDelta=1 (ignored since startCode==0xFFFF)

    // idRangeOffset (all 0 — direct mapping via idDelta)
    WriteU16Be(ms, 0); WriteU16Be(ms, 0);

    return ms.ToArray();
  }

  // Minimal 'name' table with one record: family name "TestFont" via Windows Unicode BMP.
  private static byte[] MakeNameTable(string familyName) {
    var nameBytes = Encoding.BigEndianUnicode.GetBytes(familyName);
    var ms = new MemoryStream();
    WriteU16Be(ms, 0);              // format
    WriteU16Be(ms, 1);              // count
    var stringStorageOffset = (ushort)(6 + 12 * 1); // header + 1 record
    WriteU16Be(ms, stringStorageOffset);

    // record: platform 3, encoding 1, language 0x0409, nameID 1 (family), length, offset
    WriteU16Be(ms, 3);
    WriteU16Be(ms, 1);
    WriteU16Be(ms, 0x0409);
    WriteU16Be(ms, 1);
    WriteU16Be(ms, (ushort)nameBytes.Length);
    WriteU16Be(ms, 0);              // offset within string storage

    ms.Write(nameBytes);
    return ms.ToArray();
  }

  private static void WriteU32Be(Stream s, uint v) {
    Span<byte> buf = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(buf, v);
    s.Write(buf);
  }
  private static void WriteU16Be(Stream s, ushort v) {
    Span<byte> buf = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16BigEndian(buf, v);
    s.Write(buf);
  }
  private static void WriteI16Be(Stream s, short v) {
    Span<byte> buf = stackalloc byte[2];
    BinaryPrimitives.WriteInt16BigEndian(buf, v);
    s.Write(buf);
  }

  // ── Tests ────────────────────────────────────────────────────────────────────

  [Test]
  public void SyntheticTtf_EmitsFullAndMetadata() {
    var bytes = BuildSyntheticTtf();
    using var ms = new MemoryStream(bytes);
    var entries = new TtfFormatDescriptor().List(ms, null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("FULL.ttf"));
    Assert.That(names, Does.Contain("metadata.ini"));
  }

  [Test]
  public void SyntheticTtf_EmitsGlyphSvgsForMappedCodepoints() {
    var bytes = BuildSyntheticTtf();
    using var ms = new MemoryStream(bytes);
    var entries = new TtfFormatDescriptor().List(ms, null);
    var names = entries.Select(e => e.Name).ToList();
    // Family name from the 'name' table → folder "TestFont".
    Assert.That(names, Does.Contain("glyphs/TestFont/U+0041.svg"), "expected SVG for 'A'");
    Assert.That(names, Does.Contain("glyphs/TestFont/U+0042.svg"), "expected SVG for 'B'");
    Assert.That(names, Does.Contain("glyphs/TestFont/U+0043.svg"), "expected SVG for 'C'");
  }

  [Test]
  public void SyntheticTtf_CompositeGlyphSkipped() {
    var bytes = BuildSyntheticTtf();
    using var ms = new MemoryStream(bytes);
    var entries = new TtfFormatDescriptor().List(ms, null);
    var names = entries.Select(e => e.Name).ToList();
    // gid 4 (composite) is mapped from U+0044 — no SVG should be emitted.
    Assert.That(names, Does.Not.Contain("glyphs/TestFont/U+0044.svg"));

    // metadata should record one composite skip.
    var outDir = Path.Combine(Path.GetTempPath(), "ttf_glyph_test_" + Guid.NewGuid().ToString("N"));
    try {
      ms.Position = 0;
      new TtfFormatDescriptor().Extract(ms, outDir, null, ["metadata.ini"]);
      var meta = File.ReadAllText(Path.Combine(outDir, "metadata.ini"));
      Assert.That(meta, Does.Contain("skipped_composite = 1"));
    } finally {
      if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
    }
  }

  [Test]
  public void SyntheticTtf_SvgContainsValidRootAndPath() {
    var bytes = BuildSyntheticTtf();
    using var ms = new MemoryStream(bytes);
    var outDir = Path.Combine(Path.GetTempPath(), "ttf_glyph_test_" + Guid.NewGuid().ToString("N"));
    try {
      new TtfFormatDescriptor().Extract(ms, outDir, null, ["U+0041.svg"]);
      var path = Path.Combine(outDir, "glyphs/TestFont/U+0041.svg");
      Assert.That(File.Exists(path), $"expected {path} to be written");
      var svg = File.ReadAllText(path);
      Assert.That(svg, Does.StartWith("<svg "));
      Assert.That(svg, Does.Contain("<path "));
      Assert.That(svg, Does.Contain("d='"));
      Assert.That(svg, Does.Contain("M"), "expected at least one M (move) command");
      Assert.That(svg, Does.Match(@"[LQ]"), "expected at least one L or Q command");
    } finally {
      if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
    }
  }

  [Test]
  public void SyntheticTtf_QuadraticGlyphEmitsQCommand() {
    // Glyph C (gid 3) has an off-curve middle point → Q in SVG path.
    var bytes = BuildSyntheticTtf();
    using var ms = new MemoryStream(bytes);
    var outDir = Path.Combine(Path.GetTempPath(), "ttf_glyph_test_" + Guid.NewGuid().ToString("N"));
    try {
      new TtfFormatDescriptor().Extract(ms, outDir, null, ["U+0043.svg"]);
      var svg = File.ReadAllText(Path.Combine(outDir, "glyphs/TestFont/U+0043.svg"));
      Assert.That(svg, Does.Contain("Q"), "off-curve point → expected Q (quadratic) path command");
    } finally {
      if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
    }
  }

  [Test]
  public void GarbageInput_DoesNotThrow_AndMarksParsePartial() {
    // Random 64 bytes guaranteed not to be a valid SFNT.
    var garbage = new byte[64];
    new Random(42).NextBytes(garbage);
    using var ms = new MemoryStream(garbage);
    var desc = new TtfFormatDescriptor();
    var entries = desc.List(ms, null);
    // Must not throw — and FULL + metadata.ini must still be present.
    Assert.That(entries.Any(e => e.Name == "FULL.ttf"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);

    var outDir = Path.Combine(Path.GetTempPath(), "ttf_glyph_test_" + Guid.NewGuid().ToString("N"));
    try {
      ms.Position = 0;
      desc.Extract(ms, outDir, null, ["metadata.ini"]);
      var meta = File.ReadAllText(Path.Combine(outDir, "metadata.ini"));
      Assert.That(meta, Does.Contain("parse_status = partial"));
    } finally {
      if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
    }
  }
}
