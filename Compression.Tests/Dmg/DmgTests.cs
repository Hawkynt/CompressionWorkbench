using System.Buffers.Binary;
using System.Text;

namespace Compression.Tests.Dmg;

[TestFixture]
public class DmgTests {

  // ──────────────────────────────────────────────────────────────────────────
  // Synthetic DMG builder helpers
  // ──────────────────────────────────────────────────────────────────────────

  /// <summary>
  /// Builds a minimal valid mish block table (big-endian) for a single raw block.
  /// </summary>
  /// <param name="sectorCount">Total sectors in the partition.</param>
  /// <param name="dataFileOffset">Byte offset inside the DMG file where the raw data lives.</param>
  /// <param name="dataLength">Number of raw bytes.</param>
  private static byte[] BuildMish(ulong sectorCount, ulong dataFileOffset, ulong dataLength) {
    // mish header = 204 bytes, each block entry = 40 bytes
    // We emit 2 entries: one raw block + one terminator
    const int headerSize = 204;
    const int blockEntrySize = 40;
    const int numEntries = 2;
    var buf = new byte[headerSize + numEntries * blockEntrySize];
    var sp = buf.AsSpan();

    // Signature "mish"
    sp[0] = (byte)'m'; sp[1] = (byte)'i'; sp[2] = (byte)'s'; sp[3] = (byte)'h';
    // version = 1
    BinaryPrimitives.WriteUInt32BigEndian(sp[4..], 1);
    // firstSector = 0
    BinaryPrimitives.WriteUInt64BigEndian(sp[8..], 0);
    // sectorCount
    BinaryPrimitives.WriteUInt64BigEndian(sp[16..], sectorCount);
    // dataStart = 0 (not used for raw blocks — we use absolute compressedOffset)
    BinaryPrimitives.WriteUInt64BigEndian(sp[24..], 0);
    // decompressedBufferRequested = 0
    BinaryPrimitives.WriteUInt32BigEndian(sp[32..], 0);
    // blocksDescriptor = 0
    BinaryPrimitives.WriteUInt32BigEndian(sp[36..], 0);
    // reserved 24 bytes at offset 40 → zeroed
    // checksum section (136 bytes at offset 64) → zeroed
    // numBlockEntries at offset 200
    BinaryPrimitives.WriteUInt32BigEndian(sp[200..], numEntries);

    // Block entry 0: raw block covering all sectors
    var e0 = sp[204..];
    BinaryPrimitives.WriteUInt32BigEndian(e0[0..], 0x00000001);   // blockType = raw
    BinaryPrimitives.WriteUInt32BigEndian(e0[4..], 0);             // reserved
    BinaryPrimitives.WriteUInt64BigEndian(e0[8..], 0);             // sectorOffset = 0
    BinaryPrimitives.WriteUInt64BigEndian(e0[16..], sectorCount);  // sectorCount
    BinaryPrimitives.WriteUInt64BigEndian(e0[24..], dataFileOffset); // compressedOffset
    BinaryPrimitives.WriteUInt64BigEndian(e0[32..], dataLength);   // compressedLength

    // Block entry 1: terminator
    var e1 = sp[244..];
    BinaryPrimitives.WriteUInt32BigEndian(e1[0..], 0xFFFFFFFF);   // blockType = terminator
    BinaryPrimitives.WriteUInt32BigEndian(e1[4..], 0);
    BinaryPrimitives.WriteUInt64BigEndian(e1[8..], sectorCount);   // sectorOffset = end
    BinaryPrimitives.WriteUInt64BigEndian(e1[16..], 0);
    BinaryPrimitives.WriteUInt64BigEndian(e1[24..], 0);
    BinaryPrimitives.WriteUInt64BigEndian(e1[32..], 0);

    return buf;
  }

  /// <summary>
  /// Builds a minimal valid mish block table with a single zero-fill block.
  /// </summary>
  private static byte[] BuildMishZeroFill(ulong sectorCount) {
    const int headerSize = 204;
    const int blockEntrySize = 40;
    const int numEntries = 2;
    var buf = new byte[headerSize + numEntries * blockEntrySize];
    var sp = buf.AsSpan();

    sp[0] = (byte)'m'; sp[1] = (byte)'i'; sp[2] = (byte)'s'; sp[3] = (byte)'h';
    BinaryPrimitives.WriteUInt32BigEndian(sp[4..], 1);
    BinaryPrimitives.WriteUInt64BigEndian(sp[8..], 0);
    BinaryPrimitives.WriteUInt64BigEndian(sp[16..], sectorCount);
    BinaryPrimitives.WriteUInt64BigEndian(sp[24..], 0);
    BinaryPrimitives.WriteUInt32BigEndian(sp[200..], numEntries);

    // Entry 0: zero-fill
    var e0 = sp[204..];
    BinaryPrimitives.WriteUInt32BigEndian(e0[0..], 0x00000000);   // blockType = zero-fill
    BinaryPrimitives.WriteUInt64BigEndian(e0[8..], 0);
    BinaryPrimitives.WriteUInt64BigEndian(e0[16..], sectorCount);
    BinaryPrimitives.WriteUInt64BigEndian(e0[24..], 0);
    BinaryPrimitives.WriteUInt64BigEndian(e0[32..], 0);

    // Entry 1: terminator
    var e1 = sp[244..];
    BinaryPrimitives.WriteUInt32BigEndian(e1[0..], 0xFFFFFFFF);
    BinaryPrimitives.WriteUInt64BigEndian(e1[8..], sectorCount);

    return buf;
  }

  /// <summary>
  /// Builds an XML plist with one blkx entry pointing to the given mish table.
  /// </summary>
  private static string BuildXmlPlist(string partitionName, byte[] mish) {
    var b64 = Convert.ToBase64String(mish);
    return $"""
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
<key>blkx</key>
<array>
<dict>
<key>Name</key><string>{partitionName}</string>
<key>Data</key><data>{b64}</data>
</dict>
</array>
</dict>
</plist>
""";
  }

  /// <summary>
  /// Builds a minimal valid 512-byte koly trailer (all big-endian).
  /// </summary>
  private static byte[] BuildKoly(long xmlOffset, long xmlLength) {
    var koly = new byte[512];
    var sp = koly.AsSpan();

    // Signature
    sp[0] = (byte)'k'; sp[1] = (byte)'o'; sp[2] = (byte)'l'; sp[3] = (byte)'y';
    // version = 4
    BinaryPrimitives.WriteUInt32BigEndian(sp[4..], 4);
    // headerSize = 512
    BinaryPrimitives.WriteUInt32BigEndian(sp[8..], 512);
    // flags = 0
    BinaryPrimitives.WriteUInt32BigEndian(sp[12..], 0);
    // runningDataForkOffset = 0
    BinaryPrimitives.WriteUInt64BigEndian(sp[16..], 0);
    // dataForkOffset = 0
    BinaryPrimitives.WriteUInt64BigEndian(sp[24..], 0);
    // dataForkLength = xmlOffset (data is before xml)
    BinaryPrimitives.WriteUInt64BigEndian(sp[32..], (ulong)xmlOffset);
    // rsrcForkOffset = 0
    BinaryPrimitives.WriteUInt64BigEndian(sp[40..], 0);
    // rsrcForkLength = 0
    BinaryPrimitives.WriteUInt64BigEndian(sp[48..], 0);
    // segmentNumber = 1
    BinaryPrimitives.WriteUInt32BigEndian(sp[56..], 1);
    // segmentCount = 1
    BinaryPrimitives.WriteUInt32BigEndian(sp[60..], 1);
    // segmentId (16 bytes) = zeros
    // dataChecksumType/Size/data = zeros
    // xmlOffset at byte 216
    BinaryPrimitives.WriteUInt64BigEndian(sp[216..], (ulong)xmlOffset);
    // xmlLength at byte 224
    BinaryPrimitives.WriteUInt64BigEndian(sp[224..], (ulong)xmlLength);

    return koly;
  }

  /// <summary>
  /// Assembles a complete minimal DMG:
  ///   [raw sector data] [xml plist] [koly trailer]
  /// </summary>
  private static byte[] BuildDmg(string partitionName, byte[] sectorData) {
    // Raw data is at offset 0
    var rawOffset = 0uL;
    var rawLength = (ulong)sectorData.Length;
    var sectorCount = rawLength / 512;

    var mish = BuildMish(sectorCount, rawOffset, rawLength);
    var xml  = BuildXmlPlist(partitionName, mish);
    var xmlBytes = Encoding.UTF8.GetBytes(xml);

    // Layout: raw sector data | xml plist | koly (512 bytes)
    var xmlOffset = (long)sectorData.Length;
    var koly = BuildKoly(xmlOffset, xmlBytes.Length);

    var dmg = new byte[sectorData.Length + xmlBytes.Length + 512];
    sectorData.CopyTo(dmg, 0);
    xmlBytes.CopyTo(dmg, xmlOffset);
    koly.CopyTo(dmg, xmlOffset + xmlBytes.Length);

    return dmg;
  }

  /// <summary>
  /// Assembles a DMG whose single partition is all zero-fill (no stored data).
  /// </summary>
  private static byte[] BuildDmgZeroFill(string partitionName, ulong sectorCount) {
    var mish = BuildMishZeroFill(sectorCount);
    var xml  = BuildXmlPlist(partitionName, mish);
    var xmlBytes = Encoding.UTF8.GetBytes(xml);

    // No raw data — xml starts at offset 0
    var xmlOffset = 0L;
    var koly = BuildKoly(xmlOffset, xmlBytes.Length);

    var dmg = new byte[xmlBytes.Length + 512];
    xmlBytes.CopyTo(dmg, 0);
    koly.CopyTo(dmg, xmlBytes.Length);

    return dmg;
  }

  // ──────────────────────────────────────────────────────────────────────────
  // Tests
  // ──────────────────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Read_SyntheticImage_ListsEntry() {
    // 1 sector = 512 bytes of known deterministic content
    var sectorData = new byte[512];
    Array.Fill(sectorData, (byte)0xAB);

    var dmg = BuildDmg("TestPartition", sectorData);
    using var ms = new MemoryStream(dmg);
    var r = new FileFormat.Dmg.DmgReader(ms);

    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Does.Contain("TestPartition").Or.EndWith(".img"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(512));
  }

  [Test, Category("HappyPath")]
  public void Read_SyntheticImage_ExtractReturnsData() {
    var sectorData = new byte[512];
    Array.Fill(sectorData, (byte)0xCD);

    var dmg = BuildDmg("DataPartition", sectorData);
    using var ms = new MemoryStream(dmg);
    var r = new FileFormat.Dmg.DmgReader(ms);

    Assert.That(r.Entries, Has.Count.EqualTo(1));
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted.Length, Is.EqualTo(512));
    Assert.That(extracted, Is.All.EqualTo((byte)0xCD));
  }

  [Test, Category("HappyPath")]
  public void Read_SyntheticImage_MultiSector_ExtractReturnsData() {
    // 4 sectors of deterministic data
    var sectorData = new byte[4 * 512];
    for (var i = 0; i < sectorData.Length; i++)
      sectorData[i] = (byte)(i & 0xFF);

    var dmg = BuildDmg("MultiSector", sectorData);
    using var ms = new MemoryStream(dmg);
    var r = new FileFormat.Dmg.DmgReader(ms);

    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Size, Is.EqualTo(4 * 512));

    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted.Length, Is.EqualTo(4 * 512));
    Assert.That(extracted[0], Is.EqualTo(0x00));
    Assert.That(extracted[255], Is.EqualTo(0xFF));
    Assert.That(extracted[256], Is.EqualTo(0x00)); // wraps at 256
  }

  [Test, Category("HappyPath")]
  public void Read_ZeroFillBlock_EntryHasCorrectSize() {
    var dmg = BuildDmgZeroFill("FreeSpace", sectorCount: 2);
    using var ms = new MemoryStream(dmg);
    var r = new FileFormat.Dmg.DmgReader(ms);

    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Size, Is.EqualTo(2 * 512));
  }

  [Test, Category("HappyPath")]
  public void Read_ZeroFillBlock_ExtractReturnsZeros() {
    var dmg = BuildDmgZeroFill("FreeSpace", sectorCount: 2);
    using var ms = new MemoryStream(dmg);
    var r = new FileFormat.Dmg.DmgReader(ms);

    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted.Length, Is.EqualTo(2 * 512));
    Assert.That(extracted, Is.All.EqualTo((byte)0x00));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new FileFormat.Dmg.DmgFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("Dmg"));
    Assert.That(desc.DisplayName, Is.EqualTo("DMG"));
    Assert.That(desc.DefaultExtension, Is.EqualTo(".dmg"));
    Assert.That(desc.Extensions, Contains.Item(".dmg"));
    Assert.That(desc.MagicSignatures, Is.Empty);
    Assert.That(desc.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
    Assert.That(desc.Description, Is.EqualTo("Apple disk image"));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_ReturnsSameCountAsReader() {
    var sectorData = new byte[512];
    Array.Fill(sectorData, (byte)0x77);
    var dmg = BuildDmg("Vol", sectorData);

    using var ms = new MemoryStream(dmg);
    var desc = new FileFormat.Dmg.DmgFormatDescriptor();
    var entries = desc.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].OriginalSize, Is.EqualTo(512));
  }

  [Test, Category("ErrorHandling")]
  public void BadTrailer_Throws() {
    // A file >= 512 bytes but with no "koly" signature at end
    var data = new byte[1024];
    Array.Fill(data, (byte)0xCC);
    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Dmg.DmgReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void TooSmall_Throws() {
    var data = new byte[100];
    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Dmg.DmgReader(ms));
  }

  [Test, Category("HappyPath")]
  public void Extract_NullEntry_ThrowsArgumentNull() {
    var sectorData = new byte[512];
    var dmg = BuildDmg("Vol", sectorData);
    using var ms = new MemoryStream(dmg);
    var r = new FileFormat.Dmg.DmgReader(ms);
    Assert.Throws<ArgumentNullException>(() => r.Extract(null!));
  }

  // ── WORM creation ────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_ReportsWormCapability() {
    var d = new FileFormat.Dmg.DmgFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.True);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_SinglePartition_RoundTrips() {
    // Sector-aligned payload so reader returns it without zero-padding noise.
    var payload = new byte[2048];
    new Random(7).NextBytes(payload);

    var w = new FileFormat.Dmg.DmgWriter();
    w.AddPartition("disk1.img", payload);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;

    var r = new FileFormat.Dmg.DmgReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("disk1.img"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(payload.Length));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(payload));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_MultiplePartitions_AllRoundTrip() {
    var p1 = new byte[1024];
    var p2 = new byte[3072];
    new Random(1).NextBytes(p1);
    new Random(2).NextBytes(p2);

    var w = new FileFormat.Dmg.DmgWriter();
    w.AddPartition("a.img", p1);
    w.AddPartition("b.img", p2);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;

    var r = new FileFormat.Dmg.DmgReader(ms);
    var byName = r.Entries.ToDictionary(e => e.Name);
    Assert.That(byName, Has.Count.EqualTo(2));
    Assert.That(r.Extract(byName["a.img"]), Is.EqualTo(p1));
    Assert.That(r.Extract(byName["b.img"]), Is.EqualTo(p2));
  }

  [Test, Category("HappyPath")]
  public void Writer_HasKolyTrailer() {
    var w = new FileFormat.Dmg.DmgWriter();
    w.AddPartition("x.img", new byte[512]);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    var bytes = ms.ToArray();
    // koly magic at offset (length - 512)
    Assert.That(bytes[^512..^508], Is.EqualTo(new byte[] { (byte)'k', (byte)'o', (byte)'l', (byte)'y' }));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Create_RoundTrips() {
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, new byte[1024]);
      var d = new FileFormat.Dmg.DmgFormatDescriptor();
      using var ms = new MemoryStream();
      ((Compression.Registry.IArchiveCreatable)d).Create(
        ms,
        [new Compression.Registry.ArchiveInputInfo(tmp, "data.img", false)],
        new Compression.Registry.FormatCreateOptions());
      ms.Position = 0;
      var entries = d.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(1));
      Assert.That(entries[0].Name, Is.EqualTo("data.img"));
    } finally {
      File.Delete(tmp);
    }
  }
}
