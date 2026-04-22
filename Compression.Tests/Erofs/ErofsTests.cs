#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileSystem.Erofs;

namespace Compression.Tests.Erofs;

[TestFixture]
public class ErofsTests {

  // Build a minimal EROFS image with one root directory containing one small inline file.
  // Layout:
  //   Block 0 (bytes 0..4095): reserved
  //     - Superblock at offset 1024 (length 128, rest zeroed)
  //   Block 1 (bytes 4096..8191): meta block
  //     - Root inode at offset 0..31 (compact, directory, inline, nid=0)
  //     - Child inode at offset 32..63 (compact, regular file, inline, nid=1)
  //     - Directory data at offset 64..
  //     - Inline file tail at offset after child-inode header
  private static byte[] MakeSyntheticErofs() {
    const int blockSize = 4096;
    var img = new byte[blockSize * 2];

    // ── Superblock @ 1024 ─────────────────────────────────────
    var sb = img.AsSpan(1024);
    BinaryPrimitives.WriteUInt32LittleEndian(sb, ErofsReader.Magic);      // magic
    sb[12] = 12;                                                           // blkszbits = log2(4096)
    BinaryPrimitives.WriteUInt16LittleEndian(sb[16..], 0);                 // root_nid = 0
    BinaryPrimitives.WriteUInt32LittleEndian(sb[36..], 1);                 // meta_blkaddr = 1

    // ── Meta block (block 1) @ offset 4096 ────────────────────
    // Root inode (nid=0, offset 4096..4127) — compact, dir, inline-layout
    var rootInode = img.AsSpan(4096);
    // format: (layout << 1) | extendedBit; layout=2 (inline), compact → 2 << 1 = 4
    BinaryPrimitives.WriteUInt16LittleEndian(rootInode, 4);
    BinaryPrimitives.WriteUInt16LittleEndian(rootInode[4..], 0x41ED);     // mode: S_IFDIR | 0755
    // size: will be filled after we decide directory body length

    // Child file inode (nid=1, offset 4128..4159) — compact, regular file, inline
    var fileInode = img.AsSpan(4128);
    BinaryPrimitives.WriteUInt16LittleEndian(fileInode, 4);                // format: inline compact
    BinaryPrimitives.WriteUInt16LittleEndian(fileInode[4..], 0x81A4);     // mode: S_IFREG | 0644
    var fileContent = "hello\n"u8.ToArray();
    BinaryPrimitives.WriteUInt32LittleEndian(fileInode[8..], (uint)fileContent.Length); // size

    // Inline file tail immediately after file-inode header at offset 4160
    fileContent.CopyTo(img.AsSpan(4160));

    // Root directory contents: two dirent entries + name bytes, placed after child-inode area.
    // Directory block body lives inline at offset (rootInodeOffset + 32) = 4128.
    // But 4128 is the CHILD inode area. The inline-data region for the root inode should
    // come right after the root inode's 32-byte header, i.e. at 4128. That collides with
    // the child inode. To avoid the collision in this synthetic test we point the root at
    // a different block area — use layout 0 (plain) for the root directory instead.
    // Re-write the root as plain-layout pointing to block 2. We'll grow the image.
    return MakeSyntheticErofsWithPlainDirectory();
  }

  private static byte[] MakeSyntheticErofsWithPlainDirectory() {
    const int blockSize = 4096;
    var img = new byte[blockSize * 3];

    // Superblock
    var sb = img.AsSpan(1024);
    BinaryPrimitives.WriteUInt32LittleEndian(sb, ErofsReader.Magic);
    sb[12] = 12;                                               // blkszbits = 12
    BinaryPrimitives.WriteUInt16LittleEndian(sb[16..], 0);     // root_nid
    BinaryPrimitives.WriteUInt32LittleEndian(sb[36..], 1);     // meta_blkaddr = 1

    // Root inode: compact, directory, PLAIN layout pointing to block 2
    var rootInode = img.AsSpan(4096);
    BinaryPrimitives.WriteUInt16LittleEndian(rootInode, 0);     // format = 0 (plain compact)
    BinaryPrimitives.WriteUInt16LittleEndian(rootInode[4..], 0x41ED);  // S_IFDIR | 0755
    // size: directory body length — filled below
    BinaryPrimitives.WriteUInt32LittleEndian(rootInode[16..], 2);  // rawBlkAddr = block 2

    // Child file inode: inline layout
    var fileInode = img.AsSpan(4096 + 32);
    BinaryPrimitives.WriteUInt16LittleEndian(fileInode, 4);      // format = 2<<1 = 4 (compact inline)
    BinaryPrimitives.WriteUInt16LittleEndian(fileInode[4..], 0x81A4);  // S_IFREG | 0644
    var fileContent = "hello\n"u8.ToArray();
    BinaryPrimitives.WriteUInt32LittleEndian(fileInode[8..], (uint)fileContent.Length);
    // Inline tail immediately after the 32-byte header
    fileContent.CopyTo(img.AsSpan(4096 + 32 + 32));

    // Directory block (block 2 @ 8192) — one entry for "hello.txt" pointing at nid=1
    const string childName = "hello.txt";
    var childNameBytes = Encoding.UTF8.GetBytes(childName);
    var dir = img.AsSpan(8192);
    // dirent: nid=1 @ +0, nameoff=12 @ +8 (first entry), file_type=1 (regular), reserved=0
    BinaryPrimitives.WriteUInt64LittleEndian(dir, 1);
    BinaryPrimitives.WriteUInt16LittleEndian(dir[8..], 12);    // nameoff = 12 (right after 1 dirent header)
    dir[10] = 1;                                                // file_type = regular
    childNameBytes.CopyTo(dir[12..]);

    // Update root inode size to the directory body length (12 + name length, NUL-padded to end of block is fine but size is what we wrote).
    var dirBodyLen = 12 + childNameBytes.Length;
    BinaryPrimitives.WriteUInt32LittleEndian(rootInode[8..], (uint)dirBodyLen);

    return img;
  }

  [Test]
  public void ReaderParsesSuperblockAndFindsRoot() {
    var img = MakeSyntheticErofsWithPlainDirectory();
    var reader = new ErofsReader(img);
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].Path, Is.EqualTo("hello.txt"));
    Assert.That(reader.Entries[0].Size, Is.EqualTo(6));
  }

  [Test]
  public void ExtractFileReturnsInlineContent() {
    var img = MakeSyntheticErofsWithPlainDirectory();
    var reader = new ErofsReader(img);
    var data = reader.ExtractFile(reader.Entries[0]);
    Assert.That(Encoding.UTF8.GetString(data), Is.EqualTo("hello\n"));
  }

  [Test]
  public void BadMagic_Throws() {
    var img = new byte[4096];
    Assert.Throws<InvalidDataException>(() => _ = new ErofsReader(img));
  }

  [Test]
  public void DescriptorListIntegrates() {
    var img = MakeSyntheticErofsWithPlainDirectory();
    using var ms = new MemoryStream(img);
    var desc = new ErofsFormatDescriptor();
    var entries = desc.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("hello.txt"));
  }

  [Test]
  public void DescriptorExtractWritesFile() {
    var img = MakeSyntheticErofsWithPlainDirectory();
    var tmp = Path.Combine(Path.GetTempPath(), "erofs_test_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(img);
      new ErofsFormatDescriptor().Extract(ms, tmp, null, null);
      var path = Path.Combine(tmp, "hello.txt");
      Assert.That(File.Exists(path), Is.True);
      Assert.That(File.ReadAllText(path), Is.EqualTo("hello\n"));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }
}
