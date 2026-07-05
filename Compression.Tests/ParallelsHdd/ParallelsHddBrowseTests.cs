using System.Buffers.Binary;
using Compression.Analysis;
using FileFormat.ParallelsHdd;

namespace Compression.Tests.ParallelsHdd;

[TestFixture]
public class ParallelsHddBrowseTests {

  private const int SectorSize = 512;

  [SetUp]
  public void EnsureRegistered() => Compression.Lib.FormatRegistration.EnsureInitialized();

  /// <summary>Builds a raw disk: MBR + a single FAT12 partition at sector 63 holding TEST.TXT.</summary>
  private static byte[] BuildMbrFatRawDisk() {
    var fat = new FileSystem.Fat.FatWriter();
    fat.AddFile("TEST.TXT", "Hello Parallels!"u8.ToArray());
    var img = fat.Build();

    const int partStart = 63;
    var partSectors = (img.Length + 511) / 512;
    var total = partStart + partSectors + 1;
    var raw = new byte[total * SectorSize];
    Array.Copy(img, 0, raw, partStart * SectorSize, img.Length);

    raw[510] = 0x55; raw[511] = 0xAA;
    const int e = 0x1BE;
    raw[e + 0] = 0x80;
    raw[e + 4] = 0x01; // FAT12
    raw[e + 8] = partStart;
    BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(e + 12), (uint)partSectors);
    return raw;
  }

  /// <summary>
  /// Wraps a raw disk as a single-block Parallels expanding image: the whole disk
  /// is one allocated block whose data slab starts at sector 1 (offset 512).
  /// </summary>
  private static byte[] WrapParallels(byte[] rawDisk) {
    var totalSectors = (uint)(rawDisk.Length / SectorSize);
    var buf = new byte[SectorSize + rawDisk.Length];
    "WithoutFreeSpace"u8.CopyTo(buf.AsSpan(0, 16));
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(16, 4), 2);            // version
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(20, 4), 16);           // heads
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(24, 4), 4);            // cylinders
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(28, 4), totalSectors); // block size (sectors) = whole disk
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(32, 4), totalSectors); // image size (sectors)
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(36, 4), 1);            // BAT entries
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(64, 4), 1);            // BAT[0] -> sector 1
    Array.Copy(rawDisk, 0, buf, SectorSize, rawDisk.Length);
    return buf;
  }

  [Test, Category("HappyPath")]
  public void List_BrowsesPartitionAndInnerFs() {
    var hds = WrapParallels(BuildMbrFatRawDisk());
    using var ms = new MemoryStream(hds);

    var entries = new ParallelsHddFormatDescriptor().List(ms, null);

    // Legacy raw views preserved.
    Assert.That(entries[0].Name, Is.EqualTo("FULL.hds"));
    Assert.That(entries.Any(x => x.Name == "disk.raw"), Is.True);
    // Partition + inner FS now browse.
    Assert.That(entries.Any(x => x.Name.StartsWith("Partition1_", StringComparison.Ordinal)
                                 && x.Name.EndsWith("/TEST.TXT", StringComparison.OrdinalIgnoreCase)),
      Is.True, $"Expected Partition1_*/TEST.TXT — saw: {string.Join(", ", entries.Select(x => x.Name))}");
  }

  [Test, Category("HappyPath")]
  public void Extract_WritesPartitionSubdirAndByteIdenticalDiskRaw() {
    var raw = BuildMbrFatRawDisk();
    var hds = WrapParallels(raw);
    var dir = Path.Combine(Path.GetTempPath(), "hds_browse_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      using var ms = new MemoryStream(hds);
      new ParallelsHddFormatDescriptor().Extract(ms, dir, null, null);

      var diskRaw = File.ReadAllBytes(Path.Combine(dir, "disk.raw"));
      Assert.That(diskRaw, Is.EqualTo(raw), "disk.raw must reconstruct the guest disk byte-identically");

      var partDirs = Directory.GetDirectories(dir, "Partition1_*");
      Assert.That(partDirs, Has.Length.EqualTo(1));
      var testFile = Directory.GetFiles(partDirs[0], "TEST.TXT", SearchOption.AllDirectories);
      Assert.That(testFile, Has.Length.EqualTo(1));
      Assert.That(File.ReadAllBytes(testFile[0]), Is.EqualTo("Hello Parallels!"u8.ToArray()));
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void List_LargeSparseDisk_ReportsSizeAboveIntMaxWithoutAllocating() {
    // A 3 GiB guest disk with a single unallocated block: proves disk.raw is
    // surfaced via the lazy stream (size > int.MaxValue) with no 2 GiB byte[].
    const uint imageSizeSectors = 6_000_000; // 6M * 512 = ~2.86 GiB
    var buf = new byte[SectorSize];
    "WithoutFreeSpace"u8.CopyTo(buf.AsSpan(0, 16));
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(16, 4), 2);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(28, 4), imageSizeSectors); // one block = whole disk
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(32, 4), imageSizeSectors);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(36, 4), 1);                // 1 BAT entry
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(64, 4), 0);                // unallocated -> zeros

    using var ms = new MemoryStream(buf);
    var entries = new ParallelsHddFormatDescriptor().List(ms, null);

    var diskRaw = entries.FirstOrDefault(x => x.Name == "disk.raw");
    Assert.That(diskRaw, Is.Not.Null);
    Assert.That(diskRaw!.OriginalSize, Is.GreaterThan((long)int.MaxValue),
      "Guest disk larger than 2 GiB must be reportable via the lazy stream");
  }

  [Test, Category("HappyPath")]
  public void AutoExtractor_ParallelsImage_SurfacesPartitionTable() {
    var hds = WrapParallels(BuildMbrFatRawDisk());
    using var ms = new MemoryStream(hds);

    var result = new AutoExtractor().Extract(ms);

    Assert.That(result, Is.Not.Null);
    Assert.That(result!.FormatId, Is.EqualTo("ParallelsHdd"));
    Assert.That(result.PartitionTable, Is.Not.Null,
      "ParallelsHdd must now participate in recursive partition-table descent");
    Assert.That(result.PartitionTable!.Partitions.Any(p => p.TypeName.Contains("FAT12")), Is.True);
  }
}
