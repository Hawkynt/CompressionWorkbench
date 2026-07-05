using System.Buffers.Binary;
using System.Text;
using FileFormat.BochsDisk;

namespace Compression.Tests.BochsDisk;

[TestFixture]
public class BochsDiskBrowseTests {

  private const int SectorSize = 512;

  [SetUp]
  public void EnsureRegistered() => Compression.Lib.FormatRegistration.EnsureInitialized();

  /// <summary>Builds a raw disk: MBR + a single FAT12 partition at sector 63 holding TEST.TXT.</summary>
  private static byte[] BuildMbrFatRawDisk() {
    var fat = new FileSystem.Fat.FatWriter();
    fat.AddFile("TEST.TXT", "Hello Bochs!"u8.ToArray());
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
  /// Wraps a raw disk as a single-extent Bochs Redolog (Growing, v2). The whole
  /// disk is one catalog extent (slot 0) with a zero-length bitmap, whose data
  /// slab starts at the 512-aligned extent region.
  /// </summary>
  private static byte[] WrapBochs(byte[] rawDisk) {
    const long catalogOffset = 512;
    const long catalogBytes = 4;      // 1 entry
    var extentRegion = (catalogOffset + catalogBytes + 511) & ~511L; // 1024
    var buf = new byte[extentRegion + rawDisk.Length];

    var enc = Encoding.ASCII;
    enc.GetBytes("Bochs Virtual HD Image").CopyTo(buf, 0);
    enc.GetBytes("Redolog").CopyTo(buf, 32);
    enc.GetBytes("Growing").CopyTo(buf, 48);
    var p = 64;
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(p, 4), 0x00020000u);           // version v2
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(p + 4, 4), 1u);                // catalog entries
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(p + 8, 4), 0u);                // bitmap bytes
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(p + 12, 4), (uint)rawDisk.Length); // extent bytes = whole disk
    BinaryPrimitives.WriteUInt64BigEndian(buf.AsSpan(p + 16, 8), (ulong)rawDisk.Length); // disk size

    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan((int)catalogOffset, 4), 0u);   // catalog[0] -> slot 0
    Array.Copy(rawDisk, 0, buf, (int)extentRegion, rawDisk.Length);
    return buf;
  }

  [Test, Category("HappyPath")]
  public void List_BrowsesPartitionAndInnerFs() {
    var img = WrapBochs(BuildMbrFatRawDisk());
    using var ms = new MemoryStream(img);

    var entries = new BochsDiskFormatDescriptor().List(ms, null);

    Assert.That(entries[0].Name, Is.EqualTo("FULL.redolog"));
    Assert.That(entries.Any(x => x.Name == "disk.raw"), Is.True);
    Assert.That(entries.Any(x => x.Name.StartsWith("Partition1_", StringComparison.Ordinal)
                                 && x.Name.EndsWith("/TEST.TXT", StringComparison.OrdinalIgnoreCase)),
      Is.True, $"Expected Partition1_*/TEST.TXT — saw: {string.Join(", ", entries.Select(x => x.Name))}");
  }

  [Test, Category("HappyPath")]
  public void Extract_WritesPartitionSubdirAndByteIdenticalDiskRaw() {
    var raw = BuildMbrFatRawDisk();
    var img = WrapBochs(raw);
    var dir = Path.Combine(Path.GetTempPath(), "bochs_browse_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      using var ms = new MemoryStream(img);
      new BochsDiskFormatDescriptor().Extract(ms, dir, null, null);

      var diskRaw = File.ReadAllBytes(Path.Combine(dir, "disk.raw"));
      Assert.That(diskRaw, Is.EqualTo(raw));

      var partDirs = Directory.GetDirectories(dir, "Partition1_*");
      Assert.That(partDirs, Has.Length.EqualTo(1));
      var testFile = Directory.GetFiles(partDirs[0], "TEST.TXT", SearchOption.AllDirectories);
      Assert.That(testFile, Has.Length.EqualTo(1));
      Assert.That(File.ReadAllBytes(testFile[0]), Is.EqualTo("Hello Bochs!"u8.ToArray()));
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }
}
