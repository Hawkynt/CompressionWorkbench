using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace Compression.Tests.MinixV2;

[TestFixture]
public class MinixV2Tests {

  // Build a minimal Minix v2 (14-char names) image with a single file in root.
  //
  // Layout (1024-byte blocks):
  //   Block 0       boot block (zeroed)
  //   Block 1       superblock (1024 bytes, magic at +16)
  //   Block 2       inode bitmap
  //   Block 3       zone bitmap
  //   Block 4..5    inode table (64-byte inodes, 16 inodes per block)
  //                 With ninodes=8 → 1 block fits all
  //   Block 5       first data zone (root directory)
  //   Block 6       file data zone
  private static byte[] BuildMinimalV2() {
    var image = new byte[8 * 1024];
    var sb = 1024;

    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(sb + 0,  2), 8);     // ninodes
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(sb + 2,  2), 8);     // nzones
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(sb + 4,  2), 1);     // imap_blocks
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(sb + 6,  2), 1);     // zmap_blocks
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(sb + 8,  2), 5);     // firstdatazone
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sb + 12, 4), 0x10000000);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(sb + 16, 2), 0x2468); // magic v2-14

    var inodeTable = 4 * 1024;

    // Inode 1 (root): mode 0x41ED, size 48, zones[0]=5
    //   v2 inode (64 bytes):
    //     u16 mode (0), u16 nlinks (2), u16 uid (4), u16 gid (6),
    //     u32 size (8), u32 atime (12), u32 mtime (16), u32 ctime (20),
    //     u32[10] zones (24..63)
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(inodeTable + 0,  2), 0x41ED);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(inodeTable + 8,  4), 48);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(inodeTable + 24, 4), 5);

    // Inode 2 (file): mode 0x81A4, zones[0]=6
    var ino2 = inodeTable + 64;
    var content = "Hello Minix V2!"u8.ToArray();
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(ino2 + 0,  2), 0x81A4);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(ino2 + 8,  4), (uint)content.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(ino2 + 24, 4), 6);

    // Root directory data at zone 5 (block 5)
    var rootDir = 5 * 1024;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(rootDir + 0, 2), 1);
    image[rootDir + 2] = (byte)'.';
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(rootDir + 16, 2), 1);
    image[rootDir + 18] = (byte)'.';
    image[rootDir + 19] = (byte)'.';
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(rootDir + 32, 2), 2);
    Encoding.ASCII.GetBytes("hello.txt").CopyTo(image.AsSpan(rootDir + 34, 14));

    // File data at zone 6
    content.CopyTo(image.AsSpan(6 * 1024));

    return image;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileSystem.MinixV2.MinixV2FormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("MinixV2"));
    Assert.That(d.DisplayName, Is.EqualTo("Minix V2 FS"));
    Assert.That(d.Extensions, Does.Contain(".minix2"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(2));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(1040));
  }

  [Test, Category("HappyPath")]
  public void Read_MinimalSyntheticImage() {
    var img = BuildMinimalV2();
    using var ms = new MemoryStream(img);
    var r = new FileSystem.MinixV2.MinixV2Reader(ms);
    Assert.That(r.Magic, Is.EqualTo((ushort)0x2468));
    Assert.That(r.NameLength, Is.EqualTo(14));
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("hello.txt"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(15));

    var data = r.Extract(r.Entries[0]);
    Assert.That(Encoding.ASCII.GetString(data), Is.EqualTo("Hello Minix V2!"));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_Extract() {
    var img = BuildMinimalV2();
    using var ms = new MemoryStream(img);
    var d = new FileSystem.MinixV2.MinixV2FormatDescriptor();
    var entries = d.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("hello.txt"));
  }

  [Test, Category("Sad")]
  public void Defragment_Throws() {
    var d = new FileSystem.MinixV2.MinixV2FormatDescriptor();
    using var ms = new MemoryStream(BuildMinimalV2());
    Assert.Throws<NotSupportedException>(() => d.Defragment(ms));
  }

  [Test, Category("Sad")]
  public void NoCreatable_Interface() {
    var d = new FileSystem.MinixV2.MinixV2FormatDescriptor();
    Assert.That(d, Is.Not.InstanceOf<IArchiveCreatable>());
  }
}
