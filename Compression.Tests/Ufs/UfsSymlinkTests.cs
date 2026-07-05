using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.Ufs;

namespace Compression.Tests.Ufs;

/// <summary>
/// Struct-parity proof of UFS1 symlink decoding (no BSD <c>newfs</c> oracle on this
/// host). A minimal UFS1 image is hand-crafted per FreeBSD's on-disk layout —
/// superblock at 8192, a single cylinder group's inode table, a root-directory
/// data block, and a FAST symlink whose target lives inline in the
/// <c>di_db</c>/<c>di_shortlink</c> union (di_size &lt; 60). The reader must decode
/// the symlink target and, via <see cref="UfsFormatDescriptor"/> +
/// <see cref="SymlinkResolver"/>, resolve the target size.
/// References: FreeBSD <c>sys/ufs/ffs/fs.h</c> (struct fs) and
/// <c>sys/ufs/ufs/dinode.h</c> (struct ufs1_dinode; MAXSYMLINKLEN = 60).
/// </summary>
[TestFixture]
public class UfsSymlinkTests {

  private const int SuperblockOffset = 8192;
  private const uint Ufs1Magic = 0x00011954;
  private const int InodeSize = 128;
  private const int FragSize = 512;
  private const int BlockSize = 4096;
  private const int Iblkno = 4;          // inode table starts at frag 4 → byte 2048
  private const int DirBlock = 8;        // root dir data at frag 8 → byte 4096
  private const string TargetName = "target.txt";
  private const string LinkName = "link";
  private const int TargetSizeBytes = 2048;

  [Test]
  public void HandCraftedImage_DecodesFastSymlinkTargetAndResolvedSize() {
    var image = BuildImage();
    using var ms = new MemoryStream(image);
    var reader = new UfsReader(ms);

    var link = reader.Entries.SingleOrDefault(e => e.IsSymlink);
    Assert.That(link, Is.Not.Null, "UFS reader must recognise the symlink");
    Assert.That(link!.Name, Is.EqualTo(LinkName));
    Assert.That(link.LinkTarget, Is.EqualTo(TargetName), "fast (inline) symlink target");
    Assert.That(link.Size, Is.EqualTo(TargetName.Length),
      "the link's own size is the target-path byte length");

    ms.Position = 0;
    var listing = new UfsFormatDescriptor().List(ms, password: null);
    var listedLink = listing.Single(e => e.IsSymlink);
    Assert.That(listedLink.TargetSize, Is.EqualTo(TargetSizeBytes),
      "resolved target size must be the pointed-to file's size");
  }

  private static byte[] BuildImage() {
    var image = new byte[16384];

    // ── superblock @ 8192 (struct fs) ──
    var sb = image.AsSpan(SuperblockOffset);
    BinaryPrimitives.WriteInt32LittleEndian(sb[16..], Iblkno);       // fs_iblkno
    BinaryPrimitives.WriteInt32LittleEndian(sb[48..], BlockSize);    // fs_bsize
    BinaryPrimitives.WriteInt32LittleEndian(sb[52..], FragSize);     // fs_fsize
    BinaryPrimitives.WriteInt32LittleEndian(sb[100..], 0);           // fs_fsbtodb
    BinaryPrimitives.WriteUInt32LittleEndian(sb[120..], BlockSize / InodeSize); // fs_inopb
    BinaryPrimitives.WriteUInt32LittleEndian(sb[184..], 2048);       // fs_ipg
    BinaryPrimitives.WriteInt32LittleEndian(sb[188..], 16384);       // fs_fpg
    BinaryPrimitives.WriteUInt32LittleEndian(sb[1372..], Ufs1Magic); // fs_magic @ end

    // Inode table @ frag Iblkno (byte 2048); ino N at 2048 + N*128.
    var dir = BuildDirBlock();
    WriteInode(image, ino: 2, mode: 0x41ED, size: dir.Length, db0: DirBlock); // root dir
    dir.CopyTo(image.AsSpan(DirBlock * FragSize));

    // Fast symlink (di_size < 60): target inline at di_db/di_shortlink (inode +40).
    var symlinkInode = InodeByteOffset(3);
    WriteInode(image, ino: 3, mode: 0xA1FF, size: TargetName.Length, db0: 0);
    Encoding.ASCII.GetBytes(TargetName).CopyTo(image.AsSpan(symlinkInode + 40));

    // Regular target file — size only (data never read for a listing).
    WriteInode(image, ino: 4, mode: 0x81A4, size: TargetSizeBytes, db0: 0);

    return image;
  }

  private static int InodeByteOffset(int ino) => Iblkno * FragSize + ino * InodeSize;

  private static void WriteInode(byte[] image, int ino, ushort mode, int size, int db0) {
    var i = image.AsSpan(InodeByteOffset(ino));
    BinaryPrimitives.WriteUInt16LittleEndian(i, mode);          // di_mode @0
    BinaryPrimitives.WriteUInt64LittleEndian(i[8..], (ulong)size); // di_size @8
    if (db0 != 0)
      BinaryPrimitives.WriteInt32LittleEndian(i[40..], db0);   // di_db[0] @40
  }

  // UFS dirent: d_ino(u32)@0, d_reclen(u16)@4, d_type(u8)@6, d_namlen(u8)@7, name@8.
  private static byte[] BuildDirBlock() {
    (uint Ino, byte Type, string Name, int Reclen)[] entries = [
      (2, 4, ".", 12),           // DT_DIR
      (2, 4, "..", 12),
      (4, 8, TargetName, 20),    // DT_REG
      (3, 10, LinkName, 12),     // DT_LNK
    ];

    var total = entries.Sum(e => e.Reclen);
    var block = new byte[total];
    var pos = 0;
    foreach (var e in entries) {
      BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(pos), e.Ino);
      BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(pos + 4), (ushort)e.Reclen);
      block[pos + 6] = e.Type;
      block[pos + 7] = (byte)e.Name.Length;
      Encoding.ASCII.GetBytes(e.Name).CopyTo(block.AsSpan(pos + 8));
      pos += e.Reclen;
    }
    return block;
  }
}
