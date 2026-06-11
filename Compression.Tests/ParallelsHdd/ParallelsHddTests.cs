using System.Buffers.Binary;
using System.Text;
using FileFormat.ParallelsHdd;

namespace Compression.Tests.ParallelsHdd;

[TestFixture]
public class ParallelsHddTests {

  private const int SectorSize = 512;

  // Build a minimal Parallels HDS: 2-block disk, block size = 2 sectors (1024 bytes).
  // Block 0 is allocated and filled with a pattern; block 1 is unallocated (BAT=0).
  private static byte[] BuildSyntheticHds(byte fill) {
    const uint blockSizeSectors = 2;          // 1024 bytes per block
    const uint batEntries = 2;
    const uint imageSizeSectors = blockSizeSectors * batEntries; // 4 sectors

    // Layout: 64-byte header, then BAT (2 * u32 = 8 bytes), then data sectors.
    // Place the data area starting at sector 1 (offset 512) for clean alignment.
    const int headerAndBat = 64 + 8;
    // Data block 0 sits at sector 1.
    const uint block0StartSector = 1;
    var dataOffset = (int)block0StartSector * SectorSize;
    var blockBytes = (int)blockSizeSectors * SectorSize;
    var total = dataOffset + blockBytes; // header/BAT live inside sector 0 padding

    var buf = new byte[total];

    var magic = "WithoutFreeSpace"u8;
    magic.CopyTo(buf.AsSpan(0, 16));
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(16, 4), 2);                  // version
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(20, 4), 16);                 // heads
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(24, 4), 4);                  // cylinders
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(28, 4), blockSizeSectors);   // block size (sectors)
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(32, 4), imageSizeSectors);   // image size (sectors)
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(36, 4), batEntries);         // BAT entries

    // BAT at offset 64: entry 0 -> sector 1, entry 1 -> 0 (unallocated).
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(64, 4), block0StartSector);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(68, 4), 0);

    for (var i = 0; i < blockBytes; ++i)
      buf[dataOffset + i] = fill;

    _ = headerAndBat;
    return buf;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new ParallelsHddFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("ParallelsHdd"));
    Assert.That(d.Extensions, Contains.Item(".hds"));
    Assert.That(d.MagicSignatures, Has.Count.GreaterThanOrEqualTo(1));
  }

  [Test, Category("HappyPath")]
  public void List_ExposesFullMetadataAndDisk() {
    var img = BuildSyntheticHds(0xCD);
    var d = new ParallelsHddFormatDescriptor();
    using var ms = new MemoryStream(img);
    var entries = d.List(ms, null);
    Assert.That(entries[0].Name, Is.EqualTo("FULL.hds"));
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "disk.raw"), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Extract_ReconstructsDiskAndFullByteIdentical() {
    var img = BuildSyntheticHds(0xCD);
    var d = new ParallelsHddFormatDescriptor();
    var dir = Path.Combine(Path.GetTempPath(), "hds_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      using var ms = new MemoryStream(img);
      d.Extract(ms, dir, null, null);

      var full = File.ReadAllBytes(Path.Combine(dir, "FULL.hds"));
      Assert.That(full, Is.EqualTo(img));

      var disk = File.ReadAllBytes(Path.Combine(dir, "disk.raw"));
      Assert.That(disk.Length, Is.EqualTo(4 * SectorSize)); // 4 sectors
      // Block 0 (first 1024 bytes) == 0xCD, block 1 == zero (unallocated).
      Assert.That(disk[0], Is.EqualTo(0xCD));
      Assert.That(disk[1023], Is.EqualTo(0xCD));
      Assert.That(disk[1024], Is.EqualTo(0));
      Assert.That(disk[^1], Is.EqualTo(0));

      var meta = File.ReadAllText(Path.Combine(dir, "metadata.ini"));
      Assert.That(meta, Does.Contain("block_size_sectors=2"));
      Assert.That(meta, Does.Contain("blocks_in_use=1"));
      Assert.That(meta, Does.Contain("parse_status=ok"));
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }

  [Test, Category("Exceptional")]
  public void Malformed_DoesNotThrow() {
    var garbage = new byte[128];
    Array.Fill(garbage, (byte)0x22);
    var d = new ParallelsHddFormatDescriptor();
    var dir = Path.Combine(Path.GetTempPath(), "hds_bad_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      using var ms = new MemoryStream(garbage);
      Assert.DoesNotThrow(() => d.List(ms, null));
      ms.Position = 0;
      Assert.DoesNotThrow(() => d.Extract(ms, dir, null, null));
      var full = File.ReadAllBytes(Path.Combine(dir, "FULL.hds"));
      Assert.That(full, Is.EqualTo(garbage));
      var meta = File.ReadAllText(Path.Combine(dir, "metadata.ini"));
      Assert.That(meta, Does.Contain("parse_status=partial"));
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }
}
