using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using Compression.Registry.Streaming;

namespace Compression.Tests.MinixV1;

[TestFixture]
public class MinixV1Tests {

  // Build a minimal Minix v1 (14-char names) image with a single file in root.
  //
  // Layout (1024-byte blocks):
  //   Block 0       boot block (zeroed)
  //   Block 1       superblock (1024 bytes, fields at +0..+18, magic at +16)
  //   Block 2       inode bitmap (1 block)
  //   Block 3       zone bitmap  (1 block)
  //   Block 4..N    inode table (32-byte inodes)
  //   Block N+1     first data zone (root directory)
  //   Block N+2     file data zone
  //
  // We use ninodes=8 → inode-table = ceil(8 * 32 / 1024) = 1 block. With
  // imap=1, zmap=1, inode-table=1, the first-data-zone block is 5.
  private static byte[] BuildMinimalV1() {
    var image = new byte[8 * 1024];
    var sb = 1024;

    // superblock v1 layout:
    //   u16 s_ninodes     (0)
    //   u16 s_nzones      (2)
    //   u16 s_imap_blocks (4)
    //   u16 s_zmap_blocks (6)
    //   u16 s_firstdatazone (8)
    //   u16 s_log_zone_size (10)
    //   u32 s_max_size    (12)
    //   u16 s_magic       (16)
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(sb + 0,  2), 8);     // ninodes
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(sb + 2,  2), 8);     // nzones
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(sb + 4,  2), 1);     // imap_blocks
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(sb + 6,  2), 1);     // zmap_blocks
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(sb + 8,  2), 5);     // firstdatazone
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sb + 12, 4), 0x10000000);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(sb + 16, 2), 0x137F); // magic v1-14

    // Inode table at block 4 (offset 4096). Inode 1 = root directory.
    var inodeTable = 4 * 1024;

    // Inode 1 (root, 32 bytes at +0):
    //   mode = 0x41ED (S_IFDIR + 0755)
    //   size = 64 (two 16-byte dirents: "." (inode 1), "hello.txt" (inode 2))
    //   zones[0] = 5 (root dir data)
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(inodeTable + 0,  2), 0x41ED);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(inodeTable + 4,  4), 48);   // size
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(inodeTable + 14, 2), 5);    // zones[0]

    // Inode 2 (file): mode 0x81A4 = S_IFREG + 0644
    var ino2 = inodeTable + 32;
    var content = "Hello Minix V1!"u8.ToArray();
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(ino2 + 0,  2), 0x81A4);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(ino2 + 4,  4), (uint)content.Length);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(ino2 + 14, 2), 6);          // zones[0] = block 6

    // Root directory data at zone 5 (block 5, offset 5*1024 = 5120)
    var rootDir = 5 * 1024;
    // dirent format: u16 inode + 14-byte name
    // "." entry (skipped by reader but parsed)
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(rootDir + 0, 2), 1);
    image[rootDir + 2] = (byte)'.';
    // ".." entry (skipped)
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(rootDir + 16, 2), 1);
    image[rootDir + 18] = (byte)'.';
    image[rootDir + 19] = (byte)'.';
    // "hello.txt" entry → inode 2
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(rootDir + 32, 2), 2);
    Encoding.ASCII.GetBytes("hello.txt").CopyTo(image.AsSpan(rootDir + 34, 14));

    // File data at zone 6 (block 6, offset 6*1024 = 6144)
    content.CopyTo(image.AsSpan(6 * 1024));

    return image;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileSystem.MinixV1.MinixV1FormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("MinixV1"));
    Assert.That(d.DisplayName, Is.EqualTo("Minix V1 FS"));
    Assert.That(d.Extensions, Does.Contain(".minix1"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(2));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(1040));
  }

  [Test, Category("HappyPath")]
  public void Read_MinimalSyntheticImage() {
    var img = BuildMinimalV1();
    using var ms = new MemoryStream(img);
    var r = new FileSystem.MinixV1.MinixV1Reader(ms);
    Assert.That(r.Magic, Is.EqualTo((ushort)0x137F));
    Assert.That(r.NameLength, Is.EqualTo(14));
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("hello.txt"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(15));
    Assert.That(r.Entries[0].IsDirectory, Is.False);

    var data = r.Extract(r.Entries[0]);
    Assert.That(Encoding.ASCII.GetString(data), Is.EqualTo("Hello Minix V1!"));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_Extract() {
    var img = BuildMinimalV1();
    using var ms = new MemoryStream(img);
    var d = new FileSystem.MinixV1.MinixV1FormatDescriptor();
    var entries = d.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("hello.txt"));

    var tmp = Path.Combine(Path.GetTempPath(), $"minixv1-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tmp);
    try {
      ms.Position = 0;
      d.Extract(ms, tmp, null, null);
      var written = File.ReadAllText(Path.Combine(tmp, "hello.txt"));
      Assert.That(written, Is.EqualTo("Hello Minix V1!"));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("Sad")]
  public void Defragment_Throws() {
    var d = new FileSystem.MinixV1.MinixV1FormatDescriptor();
    using var ms = new MemoryStream(BuildMinimalV1());
    Assert.Throws<NotSupportedException>(() => d.Defragment(ms));
    Assert.Throws<NotSupportedException>(() => d.Defragment(ms, new DefragOptions()));
  }

  [Test, Category("HappyPath")]
  public void Creatable_Interface() {
    var d = new FileSystem.MinixV1.MinixV1FormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
  }

  [Test, Category("HappyPath")]
  public void OpenEntry_ReturnsBoundedStream_ReadPastSizeReturnsZero() {
    var img = BuildMinimalV1();
    using var ms = new MemoryStream(img);
    var d = new FileSystem.MinixV1.MinixV1FormatDescriptor();

    using var s = d.OpenEntry(ms, "hello.txt", null);
    Assert.That(s, Is.InstanceOf<BoundedEntryStream>(), "OpenEntry must return BoundedEntryStream");
    Assert.That(s.Length, Is.EqualTo(15));

    var buf = new byte[64];
    var n = s.Read(buf, 0, buf.Length);
    Assert.That(n, Is.EqualTo(15));
    Assert.That(Encoding.ASCII.GetString(buf, 0, n), Is.EqualTo("Hello Minix V1!"));

    // Read past bound returns 0
    Assert.That(s.Read(buf, 0, buf.Length), Is.EqualTo(0), "read past LogicalSize returns 0 (EOF)");
  }

  [Test, Category("HappyPath")]
  public void ExtractEntryToMemory_MatchesExtract() {
    var img = BuildMinimalV1();
    using var ms = new MemoryStream(img);
    var d = new FileSystem.MinixV1.MinixV1FormatDescriptor();
    var bytes = d.ExtractEntryToMemory(ms, "hello.txt", null);
    Assert.That(Encoding.ASCII.GetString(bytes), Is.EqualTo("Hello Minix V1!"));
  }

  [Test, Category("Sad")]
  public void OpenEntry_UnknownName_ReturnsEmptyBoundedStream() {
    var img = BuildMinimalV1();
    using var ms = new MemoryStream(img);
    var d = new FileSystem.MinixV1.MinixV1FormatDescriptor();
    using var s = d.OpenEntry(ms, "does-not-exist", null);
    Assert.That(s, Is.InstanceOf<BoundedEntryStream>());
    Assert.That(s.Length, Is.EqualTo(0));
    Assert.That(s.Read(new byte[16], 0, 16), Is.EqualTo(0));
  }
}
