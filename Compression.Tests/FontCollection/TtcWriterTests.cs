#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;
using FileFormat.FontCollection;

namespace Compression.Tests.FontCollection;

[TestFixture]
public class TtcWriterTests {

  // Builds a standalone synthetic TTF (sfnt 0x00010000) with a single 'head' table
  // sufficient to satisfy TtcReader's structural walk after re-bundling.
  private static byte[] MakeSyntheticTtf(uint headChecksum = 0xCAFEBABE) {
    var headData = new byte[] {
      0x00, 0x01, 0x00, 0x00,
      0xDE, 0xAD, 0xBE, 0xEF,
      0x00, 0x00, 0x00, 0x00,
      0x5F, 0x0F, 0x3C, 0xF5,
      0x00, 0x00,
      0x04, 0x00,
    };
    var numTables = 1;
    var headerSize = 12 + 16 * numTables;
    var totalSize = headerSize + ((headData.Length + 3) & ~3);
    var buf = new byte[totalSize];
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0, 4), 0x00010000); // sfnt version
    BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(4, 2), (ushort)numTables);
    // searchRange/entrySelector/rangeShift left zero — TtcReader doesn't validate them.

    // Table record: tag='head', checksum, offset, length.
    Span<byte> tag = stackalloc byte[4];
    "head"u8.CopyTo(tag);
    tag.CopyTo(buf.AsSpan(12, 4));
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(16, 4), headChecksum);
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(20, 4), (uint)headerSize); // offset
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(24, 4), (uint)headData.Length);

    headData.CopyTo(buf, headerSize);
    return buf;
  }

  private static byte[] MakeSyntheticOtf() {
    var ttf = MakeSyntheticTtf();
    // Patch sfnt version to 'OTTO'.
    BinaryPrimitives.WriteUInt32BigEndian(ttf.AsSpan(0, 4), 0x4F54544Fu);
    return ttf;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesCanCreate() {
    var d = new TtcFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(d is IArchiveCreatable, Is.True);
  }

  [Test, Category("HappyPath")]
  public void Write_EmitsTtcfMagic() {
    using var ms = new MemoryStream();
    TtcWriter.Write(ms, [MakeSyntheticTtf()]);
    var blob = ms.ToArray();
    Assert.That(blob[0..4], Is.EqualTo("ttcf"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(8, 4)), Is.EqualTo(1u));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_TwoFonts_ReadsBackThroughTtcReader() {
    var ttf = MakeSyntheticTtf(headChecksum: 0x11111111);
    var otf = MakeSyntheticOtf();

    using var ms = new MemoryStream();
    TtcWriter.Write(ms, [ttf, otf]);

    var members = new TtcReader().Read(ms.ToArray());
    Assert.That(members, Has.Count.EqualTo(2));
    Assert.That(members[0].Extension, Is.EqualTo(".ttf"));
    Assert.That(members[1].Extension, Is.EqualTo(".otf"));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Create_RoundTripsThroughList() {
    var d = new TtcFormatDescriptor();
    var inputs = new[] {
      ArchiveInputInfo.InMemory("Alpha.ttf", MakeSyntheticTtf()),
      ArchiveInputInfo.InMemory("Beta.otf", MakeSyntheticOtf()),
    };
    using var outStream = new MemoryStream();
    d.Create(outStream, inputs, new FormatCreateOptions());

    outStream.Position = 0;
    var entries = d.List(outStream, null);
    // FULL.ttc + metadata.ini + fonts/0_<n>.ttf + fonts/1_<n>.otf (+ optional glyph SVGs).
    Assert.That(entries.Any(e => e.Name == "FULL.ttc"), Is.True);
    Assert.That(entries.Any(e => e.Name.EndsWith(".ttf")), Is.True);
    Assert.That(entries.Any(e => e.Name.EndsWith(".otf")), Is.True);
  }

  // Boundary: non-SFNT inputs are rejected.
  [Test, Category("Exception")]
  public void Write_NonSfntInput_Throws() {
    using var ms = new MemoryStream();
    Assert.That(
      () => TtcWriter.Write(ms, [new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE }]),
      Throws.ArgumentException);
  }

  [Test, Category("Exception")]
  public void Write_EmptyInputs_Throws() {
    using var ms = new MemoryStream();
    Assert.That(() => TtcWriter.Write(ms, []), Throws.ArgumentException);
  }

  // Equivalence: offset table entries are absolute and lie within the produced blob.
  [Test, Category("Boundary")]
  public void Write_OffsetTable_EntriesPointInsideBlob() {
    var ttf = MakeSyntheticTtf();
    using var ms = new MemoryStream();
    TtcWriter.Write(ms, [ttf, ttf]);
    var blob = ms.ToArray();
    var offset0 = BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(12, 4));
    var offset1 = BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(16, 4));
    Assert.That(offset0, Is.GreaterThanOrEqualTo(20u));
    Assert.That(offset1, Is.GreaterThan(offset0));
    Assert.That(offset1, Is.LessThan((uint)blob.Length));
  }
}
