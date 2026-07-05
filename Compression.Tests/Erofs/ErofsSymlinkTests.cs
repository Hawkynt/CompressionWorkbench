using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.Erofs;

namespace Compression.Tests.Erofs;

/// <summary>
/// Struct-parity proof of EROFS symlink decoding (no local <c>mkfs.erofs</c>
/// oracle). A minimal image is hand-crafted per the on-disk spec — superblock at
/// offset 1024, compact 32-byte inodes with the FLAT_INLINE datalayout, and an
/// <c>erofs_dirent</c> block — containing a root directory that holds a regular
/// <c>target.txt</c> and a symlink <c>link -&gt; target.txt</c>. The reader must
/// list the symlink (previously EROFS dropped symlink entries), expose its target,
/// and — through <see cref="ErofsFormatDescriptor"/> + <see cref="SymlinkResolver"/>
/// — resolve the target size.
/// References: linux fs/erofs/erofs_fs.h (erofs_super_block, erofs_inode_compact,
/// erofs_dirent; EROFS_INODE_FLAT_INLINE == datalayout 2).
/// </summary>
[TestFixture]
public class ErofsSymlinkTests {

  private const int BlockSize = 4096;
  private const uint MetaBlkAddr = 1;
  private const string LinkName = "link";
  private const string TargetName = "target.txt";
  private const int TargetSizeBytes = 2048;

  [Test]
  public void HandCraftedImage_ListsSymlinkWithTargetAndResolvedSize() {
    var image = BuildImage();
    var reader = new ErofsReader(image);

    var link = reader.Entries.SingleOrDefault(e => e.IsSymlink);
    Assert.That(link, Is.Not.Null, "EROFS reader must no longer drop the symlink entry");
    Assert.That(link!.Path, Is.EqualTo(LinkName));
    Assert.That(link.LinkTarget, Is.EqualTo(TargetName), "symlink target path");
    Assert.That(link.Size, Is.EqualTo(TargetName.Length),
      "the link's own size is the target-path byte length");

    using var ms = new MemoryStream(image);
    var listing = new ErofsFormatDescriptor().List(ms, password: null);
    var listedLink = listing.Single(e => e.IsSymlink);
    Assert.That(listedLink.LinkTarget, Is.EqualTo(TargetName));
    Assert.That(listedLink.TargetSize, Is.EqualTo(TargetSizeBytes),
      "resolved target size must be the pointed-to file's size");
  }

  // Layout (blockSize 4096, meta at block 1 → byte 4096):
  //   root dir inode   @ nid 0  → 4096   (mode S_IFDIR, inline dir chunk)
  //   symlink inode    @ nid 4  → 4224   (mode S_IFLNK, inline target text)
  //   target.txt inode @ nid 8  → 4352   (mode S_IFREG, size 2048)
  private static byte[] BuildImage() {
    var image = new byte[3 * BlockSize];

    // ── superblock @ 1024 ──
    var sb = image.AsSpan(1024);
    BinaryPrimitives.WriteUInt32LittleEndian(sb, ErofsReader.Magic); // magic @0
    sb[12] = 12;                                                     // blkszbits → 4096
    BinaryPrimitives.WriteUInt16LittleEndian(sb[14..], 0);           // root_nid = 0
    BinaryPrimitives.WriteUInt32LittleEndian(sb[40..], MetaBlkAddr); // meta_blkaddr = 1

    const int rootOff = (int)(MetaBlkAddr * BlockSize) + 0 * 32;   // 4096
    const int linkOff = (int)(MetaBlkAddr * BlockSize) + 4 * 32;   // 4224
    const int fileOff = (int)(MetaBlkAddr * BlockSize) + 8 * 32;   // 4352

    // ── root directory inode (FLAT_INLINE) ──
    var dir = BuildDirChunk();
    WriteInode(image, rootOff, mode: 0x41ED, size: dir.Length);
    dir.CopyTo(image.AsSpan(rootOff + 32));

    // ── symlink inode (FLAT_INLINE, target inline) ──
    var target = Encoding.UTF8.GetBytes(TargetName);
    WriteInode(image, linkOff, mode: 0xA1FF, size: target.Length);
    target.CopyTo(image.AsSpan(linkOff + 32));

    // ── target.txt inode (size only; data never read for a listing) ──
    WriteInode(image, fileOff, mode: 0x81A4, size: TargetSizeBytes);

    return image;
  }

  private static void WriteInode(byte[] image, int offset, ushort mode, int size) {
    var i = image.AsSpan(offset);
    // format @0: bit0 = version (0 = compact), bits1-3 = datalayout (2 = FLAT_INLINE).
    BinaryPrimitives.WriteUInt16LittleEndian(i, (2 << 1) | 0);
    BinaryPrimitives.WriteUInt16LittleEndian(i[4..], mode);        // i_mode @4
    BinaryPrimitives.WriteUInt32LittleEndian(i[8..], (uint)size);  // i_size @8 (compact)
    BinaryPrimitives.WriteUInt32LittleEndian(i[16..], 0xFFFFFFFF); // raw_blkaddr @16 (inline-only sentinel)
  }

  // erofs_dirent is 12 bytes: nid(u64)@0, nameoff(u16)@8, file_type(u8)@10, reserved@11.
  // Entry names follow the header array; entry[0].nameoff == header-array length.
  private static byte[] BuildDirChunk() {
    (ulong Nid, byte Type, string Name)[] entries = [
      (0, 2, "."),        // EROFS_FT_DIR
      (0, 2, ".."),
      (8, 1, TargetName), // EROFS_FT_REG_FILE, nid 8
      (4, 7, LinkName),   // EROFS_FT_SYMLINK,  nid 4
    ];

    var headerLen = entries.Length * 12;
    var names = new List<byte>();
    var nameOffsets = new int[entries.Length];
    for (var e = 0; e < entries.Length; e++) {
      nameOffsets[e] = headerLen + names.Count;
      names.AddRange(Encoding.UTF8.GetBytes(entries[e].Name));
    }

    var chunk = new byte[headerLen + names.Count];
    for (var e = 0; e < entries.Length; e++) {
      var o = e * 12;
      BinaryPrimitives.WriteUInt64LittleEndian(chunk.AsSpan(o), entries[e].Nid);
      BinaryPrimitives.WriteUInt16LittleEndian(chunk.AsSpan(o + 8), (ushort)nameOffsets[e]);
      chunk[o + 10] = entries[e].Type;
    }
    names.CopyTo(chunk, headerLen);
    return chunk;
  }
}
