using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using Compression.Registry.Streaming;

namespace Compression.Tests.Fatx;

[TestFixture]
public class FatxTests {

  // Build a minimal FATX image: 1 cluster = 4 sectors = 2 KiB (smaller than
  // real 32 KiB so the test image stays small). Layout:
  //   0x0000..0x0FFF  superblock
  //   0x1000..0x1FFF  FAT16 table (we only need a few entries)
  //   0x2000..        data clusters
  // Cluster 1 = root directory. Cluster 2 = "hello.txt" data.
  private const int TestSectorsPerCluster = 4; // 4 * 512 = 2 KiB cluster
  private const int ClusterSize = TestSectorsPerCluster * 512;
  private const int DataOffset = 0x2000;

  private static byte[] BuildMinimalFatx() {
    // image = 4 clusters worth of data + headers + FAT = 0x2000 + 4*ClusterSize
    var image = new byte[0x2000 + 4 * ClusterSize];

    // Superblock: 'FATX' + volume_id=0x12345678 + spc=4 + root_cluster=1
    Encoding.ASCII.GetBytes("FATX").CopyTo(image, 0);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x04), 0x12345678);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x08), (uint)TestSectorsPerCluster);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x0C), 1);

    // FAT16 at 0x1000. Entry 1 (root dir): EOC. Entry 2 (file): EOC.
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x1000 + 1 * 2), 0xFFFF);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x1000 + 2 * 2), 0xFFFF);

    // Root directory at cluster 1 = file offset DataOffset + (1-1)*ClusterSize = 0x2000
    var rootOff = DataOffset;
    // Dir record for "hello.txt" → cluster 2, size 21
    var content = "Greetings from Xbox!\n"u8.ToArray();
    var name = "hello.txt"u8.ToArray();
    image[rootOff + 0x00] = (byte)name.Length;
    image[rootOff + 0x01] = 0x20; // archive attribute
    name.CopyTo(image.AsSpan(rootOff + 0x02));
    // padding rest of name with 0xFF
    for (var i = name.Length; i < 42; i++) image[rootOff + 0x02 + i] = 0xFF;
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(rootOff + 0x2C), 2);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(rootOff + 0x30), (uint)content.Length);
    // mark next record name_length = 0xFF (end of dir)
    image[rootOff + 0x40] = 0xFF;

    // File data at cluster 2 = DataOffset + (2-1)*ClusterSize
    content.CopyTo(image.AsSpan(DataOffset + 1 * ClusterSize));

    return image;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileSystem.Fatx.FatxFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Fatx"));
    Assert.That(d.Extensions, Does.Contain(".fatx"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(0));
    // FATX is now WORM-capable (Phase 26+): the descriptor implements
    // IArchiveCreatable and advertises CanCreate.
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
    Assert.That(d.Capabilities & FormatCapabilities.CanCreate, Is.EqualTo(FormatCapabilities.CanCreate));
  }

  [Test, Category("HappyPath")]
  public void Reader_ReadsSyntheticImage() {
    var img = BuildMinimalFatx();
    using var ms = new MemoryStream(img);
    var r = new FileSystem.Fatx.FatxReader(ms);
    Assert.That(r.SectorsPerCluster, Is.EqualTo(4u));
    Assert.That(r.RootDirCluster, Is.EqualTo(1u));
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("hello.txt"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(21));
    Assert.That(r.Entries[0].IsDirectory, Is.False);

    var data = r.Extract(r.Entries[0]);
    Assert.That(Encoding.ASCII.GetString(data), Is.EqualTo("Greetings from Xbox!\n"));
  }

  [Test, Category("HappyPath")]
  public void OpenEntry_ReturnsBoundedStream() {
    var img = BuildMinimalFatx();
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Fatx.FatxFormatDescriptor();
    using var s = d.OpenEntry(ms, "hello.txt", null);
    Assert.That(s, Is.InstanceOf<BoundedEntryStream>());
    Assert.That(s.Length, Is.EqualTo(21));
    var buf = new byte[64];
    Assert.That(s.Read(buf, 0, buf.Length), Is.EqualTo(21));
    Assert.That(s.Read(buf, 0, buf.Length), Is.EqualTo(0));
  }

  [Test, Category("Sad")]
  public void Reader_RejectsCorruptedImage() {
    var img = BuildMinimalFatx();
    img[0] = 0x00; // wreck the magic
    using var ms = new MemoryStream(img);
    Assert.Throws<InvalidDataException>(() => new FileSystem.Fatx.FatxReader(ms));
  }

  [Test, Category("Sad")]
  public void Reader_RejectsTooSmallImage() {
    var img = new byte[100];
    using var ms = new MemoryStream(img);
    Assert.Throws<InvalidDataException>(() => new FileSystem.Fatx.FatxReader(ms));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_Extract() {
    var img = BuildMinimalFatx();
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Fatx.FatxFormatDescriptor();
    var entries = d.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("hello.txt"));

    ms.Position = 0;
    var bytes = d.ExtractEntryToMemory(ms, "hello.txt", null);
    Assert.That(Encoding.ASCII.GetString(bytes), Is.EqualTo("Greetings from Xbox!\n"));
  }
}
