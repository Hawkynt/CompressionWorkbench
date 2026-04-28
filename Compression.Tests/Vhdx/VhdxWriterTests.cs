using System.Buffers.Binary;
using Compression.Core.Checksums;
using FileFormat.Vhdx;

namespace Compression.Tests.Vhdx;

[TestFixture]
public class VhdxWriterTests {

  // Reproduce the well-known VHDX region GUIDs locally so failing tests
  // surface a clear "your descriptor's GUID drifted" diff.
  private static readonly Guid BatRegionGuid      = new("2DC27766-F623-4200-9D64-115E9BFD4A08");
  private static readonly Guid MetadataRegionGuid = new("8B7CA206-4790-4B9A-B8FE-575F050F886E");

  private const int RegionSize = 0x10000;
  private const int Header1Offset = 0x10000;
  private const int Header2Offset = 0x20000;
  private const int RegionTable1Offset = 0x30000;

  private static byte[] BuildSampleDisk(int sizeBytes = 1 * 1024 * 1024) {
    // Synthetic but recognisable disk payload — first 1 MiB is "DEADBEEF" repeated,
    // the rest is left zero-filled (the writer pads up to a 16 MiB block boundary).
    var disk = new byte[sizeBytes];
    for (var i = 0; i + 4 <= disk.Length; i += 4)
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(i, 4), 0xDEADBEEFu);
    return disk;
  }

  // ── Round-trip via our reader ─────────────────────────────────────────

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_RoundTrip_OurReader() {
    var disk = BuildSampleDisk();
    var w = new VhdxWriter();
    w.SetDiskData(disk);
    var img = w.Build();

    // Reader walks the structure and surfaces parsed headers / region tables.
    var parsed = VhdxReader.Read(img);
    Assert.Multiple(() => {
      Assert.That(parsed.Creator, Does.Contain("CompressionWorkbench"));
      Assert.That(parsed.PrimaryHeaderInfo, Is.Not.Null);
      Assert.That(parsed.BackupHeaderInfo, Is.Not.Null);
      Assert.That(parsed.PrimaryHeaderInfo!.SequenceNumber, Is.EqualTo(1ul));
      Assert.That(parsed.BackupHeaderInfo!.SequenceNumber, Is.EqualTo(2ul));
      Assert.That(parsed.PrimaryHeaderInfo.Version, Is.EqualTo((ushort)1));
      Assert.That(parsed.PrimaryHeaderInfo.LogLength, Is.EqualTo(0u));
      Assert.That(parsed.PrimaryHeaderInfo.LogOffset, Is.EqualTo(0ul));
    });

    // The first 1 MiB of disk data lands at the start of the first BAT-mapped
    // block (file offset 1 MiB header + metadata-region + BAT alignment).
    // We don't assert that offset here (different from "byte-equal disk"),
    // but we do verify that our supplied bytes are present somewhere.
    var idx = IndexOfSubarray(img, disk, startAt: 0x100000);
    Assert.That(idx, Is.GreaterThan(0), "Disk payload bytes not located in VHDX");
    Assert.That(img.AsSpan(idx, disk.Length).ToArray(), Is.EqualTo(disk),
      "Disk payload should be byte-equal to writer input");
  }

  // ── File Type Identifier ─────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Writer_FileTypeIdentifier_HasVhdxSignature() {
    var w = new VhdxWriter();
    w.SetDiskData(BuildSampleDisk(64 * 1024));
    var img = w.Build();

    Assert.That(img[..8], Is.EqualTo("vhdxfile"u8.ToArray()),
      "First 8 bytes must be the ASCII 'vhdxfile' signature");
  }

  // ── Header CRC-32C ────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Writer_HeadersHaveValidCrc32C() {
    var w = new VhdxWriter();
    w.SetDiskData(BuildSampleDisk(64 * 1024));
    var img = w.Build();

    AssertHeaderCrc(img, Header1Offset, "Header 1");
    AssertHeaderCrc(img, Header2Offset, "Header 2");
    AssertRegionTableCrc(img, RegionTable1Offset, "Region Table 1");
  }

  private static void AssertHeaderCrc(byte[] img, int offset, string label) {
    var region = img.AsSpan(offset, RegionSize);
    Assert.That(region[..4].ToArray(), Is.EqualTo("head"u8.ToArray()),
      $"{label} signature");
    var stored = BinaryPrimitives.ReadUInt32LittleEndian(region[4..]);

    // Recompute: zero out the checksum field, run CRC-32C over the whole region.
    var copy = region.ToArray();
    BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(4, 4), 0);
    var actual = Crc32.Compute(copy, Crc32.Castagnoli);
    Assert.That(stored, Is.EqualTo(actual), $"{label} CRC-32C mismatch");
  }

  private static void AssertRegionTableCrc(byte[] img, int offset, string label) {
    var region = img.AsSpan(offset, RegionSize);
    Assert.That(region[..4].ToArray(), Is.EqualTo("regi"u8.ToArray()), $"{label} signature");
    var stored = BinaryPrimitives.ReadUInt32LittleEndian(region[4..]);

    var copy = region.ToArray();
    BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(4, 4), 0);
    var actual = Crc32.Compute(copy, Crc32.Castagnoli);
    Assert.That(stored, Is.EqualTo(actual), $"{label} CRC-32C mismatch");
  }

  // ── Region Table contents ────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Writer_RegionTable_HasBatAndMetadata() {
    var w = new VhdxWriter();
    w.SetDiskData(BuildSampleDisk(64 * 1024));
    var img = w.Build();
    var region = img.AsSpan(RegionTable1Offset, RegionSize);

    var entryCount = BinaryPrimitives.ReadUInt32LittleEndian(region[8..]);
    Assert.That(entryCount, Is.EqualTo(2u), "Region Table should expose BAT + Metadata");

    var entry0 = new Guid(region.Slice(16, 16));
    var entry1 = new Guid(region.Slice(48, 16));
    var seen = new[] { entry0, entry1 };
    Assert.That(seen, Does.Contain(BatRegionGuid));
    Assert.That(seen, Does.Contain(MetadataRegionGuid));

    // Both entries marked Required.
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(region[(16 + 28)..]), Is.EqualTo(1u));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(region[(48 + 28)..]), Is.EqualTo(1u));
  }

  // ── Region Table 2 mirrors Region Table 1 ─────────────────────────────

  [Test, Category("HappyPath")]
  public void Writer_RegionTable2_MirrorsRegionTable1() {
    var w = new VhdxWriter();
    w.SetDiskData(BuildSampleDisk(64 * 1024));
    var img = w.Build();
    var rt1 = img.AsSpan(RegionTable1Offset, RegionSize).ToArray();
    var rt2 = img.AsSpan(RegionTable1Offset + RegionSize, RegionSize).ToArray();
    Assert.That(rt2, Is.EqualTo(rt1));
  }

  // ── Descriptor.Create wires the writer for IArchiveCreatable ─────────

  [Test, Category("HappyPath")]
  public void Descriptor_Create_ProducesValidVhdx() {
    var input = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".txt");
    File.WriteAllText(input, "Hello from VHDX descriptor!");
    try {
      var inputs = new List<global::Compression.Registry.ArchiveInputInfo> {
        new(FullPath: input, ArchiveName: "HELLO.TXT", IsDirectory: false),
      };
      using var ms = new MemoryStream();
      new VhdxFormatDescriptor().Create(ms, inputs, new global::Compression.Registry.FormatCreateOptions());
      var img = ms.ToArray();

      Assert.That(img[..8], Is.EqualTo("vhdxfile"u8.ToArray()));
      var parsed = VhdxReader.Read(img);
      Assert.That(parsed.PrimaryHeaderInfo, Is.Not.Null);
    } finally {
      File.Delete(input);
    }
  }

  // ── Helpers ───────────────────────────────────────────────────────────

  /// <summary>Locate the first byte index where <paramref name="needle"/> appears in <paramref name="hay"/> at or after <paramref name="startAt"/>.</summary>
  private static int IndexOfSubarray(byte[] hay, byte[] needle, int startAt) {
    if (needle.Length == 0) return startAt;
    var max = hay.Length - needle.Length;
    for (var i = startAt; i <= max; i++) {
      var match = true;
      for (var j = 0; j < needle.Length; j++) {
        if (hay[i + j] != needle[j]) { match = false; break; }
      }
      if (match) return i;
    }
    return -1;
  }
}
