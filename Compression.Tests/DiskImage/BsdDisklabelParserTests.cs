using System.Buffers.Binary;
using Compression.Core.DiskImage;

namespace Compression.Tests.DiskImage;

[TestFixture]
public class BsdDisklabelParserTests {

  private const uint DiskMagic = 0x82564557;
  private const int PartitionsOffset = 148;
  private const int RecordSize = 16;

  private readonly record struct Part(uint Size, uint Offset, byte FsType);

  /// <summary>
  /// Writes a little-endian 4.4BSD disklabel into <paramref name="buffer"/> at
  /// <paramref name="labelOffset"/>, with the supplied partition records.
  /// </summary>
  private static void WriteLabel(byte[] buffer, int labelOffset, uint secSize, params Part[] parts) {
    BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(labelOffset + 0), DiskMagic);   // d_magic
    BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(labelOffset + 40), secSize);    // d_secsize
    BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(labelOffset + 132), DiskMagic); // d_magic2
    BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(labelOffset + 138), (ushort)parts.Length); // d_npartitions

    for (var i = 0; i < parts.Length; ++i) {
      var rec = labelOffset + PartitionsOffset + i * RecordSize;
      BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(rec + 0), parts[i].Size);
      BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(rec + 4), parts[i].Offset);
      buffer[rec + 12] = parts[i].FsType;
    }
  }

  [Test, Category("HappyPath")]
  public void Parse_WholeDiskLabel_EnumeratesRealPartitionsSkippingC() {
    var disk = new byte[64 * 512];
    // a = 4.2BSD, b = swap, c = whole disk (skipped), d = empty (skipped).
    WriteLabel(disk, 512, 512,
      new Part(100, 64, 7),   // a
      new Part(50, 164, 1),   // b
      new Part(1000, 0, 0),   // c (whole disk) — offset 0 => relative mode
      new Part(0, 0, 0));     // d (unused)

    using var ms = new MemoryStream(disk);
    var parts = BsdDisklabelParser.Parse(ms);

    Assert.That(parts, Has.Count.EqualTo(2));
    Assert.That(parts[0].Name, Is.EqualTo("a"));
    Assert.That(parts[0].TypeName, Is.EqualTo("4.2BSD"));
    Assert.That(parts[0].StartOffset, Is.EqualTo(64L * 512));
    Assert.That(parts[0].Size, Is.EqualTo(100L * 512));
    Assert.That(parts[0].Source, Is.EqualTo("BSD"));
    Assert.That(parts[1].Name, Is.EqualTo("b"));
    Assert.That(parts[1].TypeName, Is.EqualTo("swap"));
  }

  [Test, Category("HappyPath")]
  public void Parse_RelativeOffsets_AddsSliceStartWhenCPartitionZero() {
    var disk = new byte[64 * 512];
    // c offset 0 => relative; slice starts at sector 4 (byte 2048).
    WriteLabel(disk, 512, 512,
      new Part(100, 1, 7),    // a, relative sector 1
      new Part(0, 0, 0),      // b unused
      new Part(1000, 0, 0));  // c whole slice, offset 0 => relative
    using var ms = new MemoryStream(disk);

    var parts = BsdDisklabelParser.Parse(ms, sliceStartByteOffset: 2048);

    Assert.That(parts, Has.Count.EqualTo(1));
    // absolute sector = sliceStart(4) + p_offset(1) = 5
    Assert.That(parts[0].StartOffset, Is.EqualTo(5L * 512));
  }

  [Test, Category("HappyPath")]
  public void Parse_AbsoluteOffsets_IgnoresSliceStartWhenCPartitionNonZero() {
    var disk = new byte[128 * 512];
    // c offset non-zero => absolute; slice start must NOT be added.
    WriteLabel(disk, 512, 512,
      new Part(100, 100, 7),  // a absolute sector 100
      new Part(0, 0, 0),
      new Part(1000, 4, 0));  // c offset 4 (nonzero) => absolute
    using var ms = new MemoryStream(disk);

    var parts = BsdDisklabelParser.Parse(ms, sliceStartByteOffset: 2048);

    Assert.That(parts, Has.Count.EqualTo(1));
    Assert.That(parts[0].StartOffset, Is.EqualTo(100L * 512));
  }

  [Test, Category("HappyPath")]
  public void Parse_LabelAtOffset64_IsFound() {
    var disk = new byte[64 * 512];
    WriteLabel(disk, 64, 512,
      new Part(100, 200, 8),  // a MSDOS
      new Part(0, 0, 0),
      new Part(1000, 5, 0));  // c absolute
    using var ms = new MemoryStream(disk);

    Assert.That(BsdDisklabelParser.IsDisklabel(ms), Is.True);
    var parts = BsdDisklabelParser.Parse(ms);
    Assert.That(parts, Has.Count.EqualTo(1));
    Assert.That(parts[0].TypeName, Is.EqualTo("MSDOS"));
    Assert.That(parts[0].StartOffset, Is.EqualTo(200L * 512));
  }

  [Test, Category("Exceptional")]
  public void IsDisklabel_PlainData_ReturnsFalse() {
    var disk = new byte[4096];
    using var ms = new MemoryStream(disk);
    Assert.That(BsdDisklabelParser.IsDisklabel(ms), Is.False);
  }

  // ── Nested integration through PartitionedDiskLister ──

  [Test, Category("HappyPath")]
  public void PartitionedDiskLister_BsdSlice_SubEnumeratesDisklabelPartitions() {
    Compression.Lib.FormatRegistration.EnsureInitialized();

    var fat = new FileSystem.Fat.FatWriter();
    fat.AddFile("TEST.TXT", "bsd nested"u8.ToArray());
    var fatImage = fat.Build();
    var fatSectors = (uint)((fatImage.Length + 511) / 512);

    const uint sliceStart = 63;        // BSD slice start sector
    const uint fatStart = sliceStart + 2; // FAT sub-partition absolute start sector
    var totalSectors = (int)(fatStart + fatSectors + 1);
    var disk = new byte[totalSectors * 512];

    // FAT filesystem bytes at the sub-partition's absolute offset.
    Array.Copy(fatImage, 0, disk, (int)fatStart * 512, fatImage.Length);

    // MBR with a single FreeBSD (0xA5) slice covering the tail of the disk.
    disk[510] = 0x55; disk[511] = 0xAA;
    const int e = 0x1BE;
    disk[e + 0] = 0x80;
    disk[e + 4] = 0xA5; // FreeBSD
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(e + 8), sliceStart);
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(e + 12), (uint)(totalSectors - sliceStart));

    // BSD disklabel at slice sector 1 (absolute sector sliceStart+1).
    var labelOffset = (int)(sliceStart + 1) * 512;
    WriteLabel(disk, labelOffset, 512,
      new Part(fatSectors, fatStart, 8),                       // a: MSDOS at absolute fatStart
      new Part(0, 0, 0),                                       // b unused
      new Part((uint)(totalSectors - sliceStart), sliceStart, 0)); // c whole slice (absolute)

    using var ms = new MemoryStream(disk);
    var entries = PartitionedDiskLister.List(ms, password: null);

    Assert.That(entries, Is.Not.Null);
    var list = entries!;
    var names = string.Join(", ", list.Select(x => x.Name));
    Assert.That(list.Any(x => x.Name.StartsWith("Partition1_FreeBSD/", StringComparison.Ordinal)
                              && x.Name.EndsWith("/TEST.TXT", StringComparison.OrdinalIgnoreCase)),
      Is.True, $"Expected a nested BSD-slice FAT entry — saw: {names}");
  }
}
