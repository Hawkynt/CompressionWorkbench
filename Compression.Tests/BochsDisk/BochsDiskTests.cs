using System.Buffers.Binary;
using System.Text;
using FileFormat.BochsDisk;

namespace Compression.Tests.BochsDisk;

[TestFixture]
public class BochsDiskTests {

  private const uint ExtentBytes = 512;
  private const uint BitmapBytes = 4;
  private const uint CatalogEntries = 2;
  private const ulong DiskSize = 1024; // 2 extents

  // Build a minimal Bochs Redolog (Growing, v2): catalog[0] allocated, catalog[1] unallocated.
  private static byte[] BuildSyntheticBochs(byte fill) {
    const long catalogOffset = 512;
    var catalogBytes = (long)CatalogEntries * 4;
    var extentRegion = ((catalogOffset + catalogBytes) + 511) & ~511L; // 1024
    var perExtent = BitmapBytes + ExtentBytes; // 516
    var total = (int)(extentRegion + perExtent); // one allocated extent
    var buf = new byte[total];

    var enc = Encoding.ASCII;
    enc.GetBytes("Bochs Virtual HD Image").CopyTo(buf, 0); // 32-byte magic field
    enc.GetBytes("Redolog").CopyTo(buf, 32);               // 16-byte type
    enc.GetBytes("Growing").CopyTo(buf, 48);               // 16-byte subtype
    var p = 64;
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(p, 4), 0x00020000u); // version v2
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(p + 4, 4), CatalogEntries);
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(p + 8, 4), BitmapBytes);
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(p + 12, 4), ExtentBytes);
    BinaryPrimitives.WriteUInt64BigEndian(buf.AsSpan(p + 16, 8), DiskSize);

    // Catalog: entry 0 -> extent slot 0; entry 1 -> unallocated.
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan((int)catalogOffset, 4), 0u);
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan((int)catalogOffset + 4, 4), 0xFFFFFFFFu);

    // Extent 0 data (after its bitmap).
    var dataStart = (int)extentRegion + (int)BitmapBytes;
    for (var i = 0; i < ExtentBytes; ++i)
      buf[dataStart + i] = fill;

    return buf;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new BochsDiskFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("BochsDisk"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
  }

  [Test, Category("HappyPath")]
  public void List_ExposesFullMetadataAndDisk() {
    var img = BuildSyntheticBochs(0x77);
    var d = new BochsDiskFormatDescriptor();
    using var ms = new MemoryStream(img);
    var entries = d.List(ms, null);
    Assert.That(entries[0].Name, Is.EqualTo("FULL.redolog"));
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "disk.raw"), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Extract_ReconstructsDiskAndFullByteIdentical() {
    var img = BuildSyntheticBochs(0x77);
    var d = new BochsDiskFormatDescriptor();
    var dir = Path.Combine(Path.GetTempPath(), "bochs_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      using var ms = new MemoryStream(img);
      d.Extract(ms, dir, null, null);

      var full = File.ReadAllBytes(Path.Combine(dir, "FULL.redolog"));
      Assert.That(full, Is.EqualTo(img));

      var disk = File.ReadAllBytes(Path.Combine(dir, "disk.raw"));
      Assert.That(disk.Length, Is.EqualTo((int)DiskSize));
      Assert.That(disk[0], Is.EqualTo(0x77));
      Assert.That(disk[(int)ExtentBytes - 1], Is.EqualTo(0x77));
      Assert.That(disk[(int)ExtentBytes], Is.EqualTo(0)); // extent 1 unallocated
      Assert.That(disk[^1], Is.EqualTo(0));

      var meta = File.ReadAllText(Path.Combine(dir, "metadata.ini"));
      Assert.That(meta, Does.Contain("type=Redolog"));
      Assert.That(meta, Does.Contain("subtype=Growing"));
      Assert.That(meta, Does.Contain("disk_size=1024"));
      Assert.That(meta, Does.Contain("parse_status=ok"));
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }

  [Test, Category("Exceptional")]
  public void Malformed_DoesNotThrow() {
    var garbage = new byte[200];
    Array.Fill(garbage, (byte)0x2A);
    var d = new BochsDiskFormatDescriptor();
    var dir = Path.Combine(Path.GetTempPath(), "bochs_bad_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      using var ms = new MemoryStream(garbage);
      Assert.DoesNotThrow(() => d.List(ms, null));
      ms.Position = 0;
      Assert.DoesNotThrow(() => d.Extract(ms, dir, null, null));
      var full = File.ReadAllBytes(Path.Combine(dir, "FULL.redolog"));
      Assert.That(full, Is.EqualTo(garbage));
      var meta = File.ReadAllText(Path.Combine(dir, "metadata.ini"));
      Assert.That(meta, Does.Contain("parse_status=partial"));
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }
}
