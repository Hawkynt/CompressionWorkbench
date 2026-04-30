#pragma warning disable CS1591
using System.Buffers.Binary;
using FileFormat.FontCollection;

namespace Compression.Tests.FontCollection;

[TestFixture]
public class TtcTests {

  // Build a minimal synthetic .ttc containing 2 member fonts sharing a 'head' table.
  // Each member advertises two tables ('head' and 'glyf'); slicing must produce two
  // standalone SFNT files, each 12 + 2*16 + aligned('head') + aligned('glyf') bytes.
  private static byte[] MakeSyntheticTtc() {
    // Payload tables
    var headData = new byte[] {
      0x00, 0x01, 0x00, 0x00, // version
      0xDE, 0xAD, 0xBE, 0xEF, // fontRevision
      0x00, 0x00, 0x00, 0x00, // checkSumAdjustment placeholder
      0x5F, 0x0F, 0x3C, 0xF5, // magicNumber 0x5F0F3CF5
      0x00, 0x00,             // flags
      0x04, 0x00,             // unitsPerEm 1024
    };
    var glyf0 = new byte[] { 0x11, 0x22, 0x33 };          // not 4-aligned by design
    var glyf1 = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };

    // Layout: [ttcHeader 12][offset[0] uint32][offset[1] uint32]
    //         [member0 subtable 12][2 records 32][head 20][glyf0 3 + 1 pad]
    //         [member1 subtable 12][2 records 32][head 20 — REUSED reference][glyf1 4]
    // For simplicity, both members reference the same head data (shared table).

    using var ms = new MemoryStream();
    var bw = new BinaryWriter(ms);

    // TTC header: 'ttcf', version 0x00010000, numFonts=2, offset[2]
    bw.Write("ttcf"u8.ToArray());
    WriteUInt32Be(bw, 0x00010000);
    WriteUInt32Be(bw, 2);
    var offsetTablePos = ms.Position;
    WriteUInt32Be(bw, 0); WriteUInt32Be(bw, 0); // placeholders

    // Pool area: write shared 'head' data once.
    var sharedHeadPos = (uint)ms.Position;
    bw.Write(headData);

    // Glyf0 directly after head
    var glyf0Pos = (uint)ms.Position;
    bw.Write(glyf0);

    // Glyf1
    var glyf1Pos = (uint)ms.Position;
    bw.Write(glyf1);

    // Member 0 offset subtable + records
    var member0Pos = (uint)ms.Position;
    WriteUInt32Be(bw, 0x00010000); // sfntVersion TTF
    WriteUInt16Be(bw, 2);            // numTables
    WriteUInt16Be(bw, 0); WriteUInt16Be(bw, 0); WriteUInt16Be(bw, 0); // search triple (don't care for slicing)
    WriteTableRecord(bw, "head", 0x12345678, sharedHeadPos, (uint)headData.Length);
    WriteTableRecord(bw, "glyf", 0xAABBCCDD, glyf0Pos, (uint)glyf0.Length);

    // Member 1
    var member1Pos = (uint)ms.Position;
    WriteUInt32Be(bw, 0x00010000);
    WriteUInt16Be(bw, 2);
    WriteUInt16Be(bw, 0); WriteUInt16Be(bw, 0); WriteUInt16Be(bw, 0);
    WriteTableRecord(bw, "head", 0x12345678, sharedHeadPos, (uint)headData.Length);
    WriteTableRecord(bw, "glyf", 0x11223344, glyf1Pos, (uint)glyf1.Length);

    // Patch offset table
    var saved = ms.Position;
    ms.Position = offsetTablePos;
    WriteUInt32Be(bw, member0Pos);
    WriteUInt32Be(bw, member1Pos);
    ms.Position = saved;
    return ms.ToArray();
  }

  private static void WriteUInt32Be(BinaryWriter bw, uint v) {
    Span<byte> buf = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(buf, v);
    bw.Write(buf);
  }

  private static void WriteUInt16Be(BinaryWriter bw, ushort v) {
    Span<byte> buf = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16BigEndian(buf, v);
    bw.Write(buf);
  }

  private static void WriteTableRecord(BinaryWriter bw, string tag, uint checksum, uint offset, uint length) {
    bw.Write(System.Text.Encoding.ASCII.GetBytes(tag));
    WriteUInt32Be(bw, checksum);
    WriteUInt32Be(bw, offset);
    WriteUInt32Be(bw, length);
  }

  [Test]
  public void SyntheticTtcYieldsTwoMembers() {
    var members = new TtcReader().Read(MakeSyntheticTtc());
    Assert.That(members, Has.Count.EqualTo(2));
    Assert.That(members[0].Extension, Is.EqualTo(".ttf"));
  }

  [Test]
  public void EachMemberHasStandaloneSfntHeader() {
    var members = new TtcReader().Read(MakeSyntheticTtc());
    foreach (var m in members) {
      var version = BinaryPrimitives.ReadUInt32BigEndian(m.Data);
      var numTables = BinaryPrimitives.ReadUInt16BigEndian(m.Data.AsSpan(4));
      Assert.That(version, Is.EqualTo(0x00010000u));
      Assert.That(numTables, Is.EqualTo(2));
    }
  }

  [Test]
  public void OutputTablesAreContiguousFromHeaderEnd() {
    var members = new TtcReader().Read(MakeSyntheticTtc());
    foreach (var m in members) {
      // First table record's offset field should point just past the record area: 12+2*16 = 44
      var firstOffset = BinaryPrimitives.ReadUInt32BigEndian(m.Data.AsSpan(12 + 8));
      Assert.That(firstOffset, Is.EqualTo(44u));
    }
  }

  [Test]
  public void DescriptorListGivesFullAndPerFontEntries() {
    var desc = new TtcFormatDescriptor();
    using var ms = new MemoryStream(MakeSyntheticTtc());
    var entries = desc.List(ms, null);
    var names = entries.Select(e => e.Name).ToList();
    // New layout: FULL.ttc + per-member fonts under fonts/ + metadata.ini.
    // (Glyph SVGs are absent here because the synthetic TTC carries garbage glyf data
    // — the splitter records that as parse_status=partial in metadata.)
    Assert.That(names, Does.Contain("FULL.ttc"));
    Assert.That(names, Has.Some.StartsWith("fonts/0_"));
    Assert.That(names, Has.Some.StartsWith("fonts/1_"));
    Assert.That(names, Does.Contain("metadata.ini"));
  }

  [Test]
  public void RealSystemTtcOnWindowsIsSliceable() {
    // Cambria.ttc is present on Windows 7+; file contains Cambria, Cambria Math and Cambria Italic.
    var candidates = new[] {
      @"C:\Windows\Fonts\Cambria.ttc",
      @"C:\Windows\Fonts\cambria.ttc",
      @"C:\Windows\Fonts\msgothic.ttc",
    };
    var path = candidates.FirstOrDefault(File.Exists);
    if (path == null) Assert.Ignore("No .ttc system font found on this platform");

    var members = new TtcReader().Read(File.ReadAllBytes(path!));
    Assert.That(members, Is.Not.Empty);
    foreach (var m in members) {
      // Each sliced font must have a valid SFNT version
      var v = BinaryPrimitives.ReadUInt32BigEndian(m.Data);
      Assert.That(v is 0x00010000u or 0x4F54544Fu or 0x74727565u or 0x74797031u,
        $"Member {m.Index} has unexpected sfntVersion 0x{v:X8}");
    }
  }
}
