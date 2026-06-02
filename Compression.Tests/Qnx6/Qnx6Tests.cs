using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using Compression.Registry.Streaming;

namespace Compression.Tests.Qnx6;

[TestFixture]
public class Qnx6Tests {

  // Minimal QNX6 image:
  //   0x0000..0x1FFF  boot region (zeroed)
  //   0x2000          superblock (magic 0x68191122)
  //   block 4 = 0x1000?  inode table — but BlockSize=1024 so block 4 = 0x1000;
  //                    we make inode table at block 16 (0x4000) to keep clear.
  //   block 17        root directory data
  //   block 18        file data
  private const int BlockSize = 1024;
  private const uint InodeTableBlock = 16; // 0x4000
  private const uint RootDirBlock = 17;    // 0x4400
  private const uint FileBlock = 18;       // 0x4800

  private static byte[] BuildMinimalQnx6() {
    var image = new byte[32 * BlockSize];

    // Superblock at 0x2000
    var sb = 0x2000;
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sb + 0, 4), 0x68191122);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sb + 0x30, 4), (uint)BlockSize);
    // Inode-table root node first block ptr at sb+0x48+8
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sb + 0x48 + 8, 4), InodeTableBlock);

    // Inode 1 (root dir) at inode_table_offset + 0*128 = 0x4000
    var itOff = (int)(InodeTableBlock * BlockSize);
    var ino1 = itOff + 0 * 128;
    // di_size = 32 (one entry) - we'll use 64 (entries should be 32 bytes; first one is real, second is sentinel)
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(ino1 + 0x00, 8), 32);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(ino1 + 0x20, 2), 0x41ED); // S_IFDIR
    // First direct block = RootDirBlock
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(ino1 + 0x24, 4), RootDirBlock);

    // Inode 2 (file) at itOff + 128
    var content = "QNX6 says hi\n"u8.ToArray();
    var ino2 = itOff + 1 * 128;
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(ino2 + 0x00, 8), (ulong)content.Length);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(ino2 + 0x20, 2), 0x81A4); // S_IFREG
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(ino2 + 0x24, 4), FileBlock);

    // Root directory entries at RootDirBlock
    var rd = (int)(RootDirBlock * BlockSize);
    // Single entry: inum=2, name_len=5, name="hello"
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(rd + 0, 4), 2);
    image[rd + 4] = 5;
    Encoding.ASCII.GetBytes("hello").CopyTo(image.AsSpan(rd + 5));

    // File data
    content.CopyTo(image.AsSpan((int)(FileBlock * BlockSize)));

    return image;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileSystem.Qnx6.Qnx6FormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Qnx6"));
    Assert.That(d.Extensions, Does.Contain(".qnx6"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(0x2000));
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>(),
      "QNX6 is WORM-creatable (paired superblocks + flat inode array + 32-byte dirents).");
  }

  [Test, Category("HappyPath")]
  public void Reader_ReadsSyntheticImage() {
    var img = BuildMinimalQnx6();
    using var ms = new MemoryStream(img);
    var r = new FileSystem.Qnx6.Qnx6Reader(ms);
    Assert.That(r.Magic, Is.EqualTo(0x68191122u));
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("hello"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(13));
    var data = r.Extract(r.Entries[0]);
    Assert.That(Encoding.ASCII.GetString(data), Is.EqualTo("QNX6 says hi\n"));
  }

  [Test, Category("HappyPath")]
  public void OpenEntry_ReturnsBoundedStream() {
    var img = BuildMinimalQnx6();
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Qnx6.Qnx6FormatDescriptor();
    using var s = d.OpenEntry(ms, "hello", null);
    Assert.That(s, Is.InstanceOf<BoundedEntryStream>());
    Assert.That(s.Length, Is.EqualTo(13));
    var buf = new byte[64];
    Assert.That(s.Read(buf, 0, buf.Length), Is.EqualTo(13));
    Assert.That(s.Read(buf, 0, buf.Length), Is.EqualTo(0));
  }

  [Test, Category("Sad")]
  public void Reader_RejectsCorruptedImage() {
    var img = BuildMinimalQnx6();
    img[0x2000] ^= 0xFF;
    using var ms = new MemoryStream(img);
    Assert.Throws<InvalidDataException>(() => new FileSystem.Qnx6.Qnx6Reader(ms));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_Extract() {
    var img = BuildMinimalQnx6();
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Qnx6.Qnx6FormatDescriptor();
    var entries = d.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("hello"));
    ms.Position = 0;
    var bytes = d.ExtractEntryToMemory(ms, "hello", null);
    Assert.That(Encoding.ASCII.GetString(bytes), Is.EqualTo("QNX6 says hi\n"));
  }
}
