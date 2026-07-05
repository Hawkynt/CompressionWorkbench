using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.Apfs;

namespace Compression.Tests.Apfs;

/// <summary>
/// Struct-parity proof of APFS symlink decoding (no <c>newfs_apfs</c> oracle on this
/// host). A minimal container is hand-crafted per Apple's "Apple File System
/// Reference": NXSB → container OMAP → APSB → volume OMAP → filesystem B-tree. The
/// FS-tree leaf carries a regular <c>target.txt</c> inode plus a symlink inode
/// (mode S_IFLNK) whose target is held in the embedded extended attribute
/// <c>com.apple.fs.symlink</c>. The reader must mark the entry as a symlink, decode
/// the xattr target, and — via <see cref="ApfsFormatDescriptor"/> +
/// <see cref="SymlinkResolver"/> — resolve the target size.
/// </summary>
[TestFixture]
public class ApfsSymlinkTests {

  private const int BlockSize = 4096;
  private const string TargetName = "target.txt";
  private const string LinkName = "link";
  private const int TargetSizeBytes = 2048;

  // Block map.
  private const ulong CtrOmapObj = 1;
  private const ulong CtrOmapTree = 2;
  private const ulong ApsbBlock = 3;
  private const ulong VolOmapObj = 4;
  private const ulong VolOmapTree = 5;
  private const ulong FsTreeBlock = 6;

  private const ulong ApsbVirtOid = 1000;
  private const ulong RootTreeVirtOid = 2000;

  private const ulong RootIno = 2;      // APFS_ROOT_DIR_INO_NUM
  private const ulong TargetIno = 100;
  private const ulong LinkIno = 101;

  [Test]
  public void HandCraftedImage_DecodesSymlinkFromXattrWithResolvedSize() {
    var image = BuildImage();
    var reader = new ApfsReader(new MemoryStream(image), leaveOpen: true);

    var link = reader.Entries.SingleOrDefault(e => e.IsSymlink);
    Assert.That(link, Is.Not.Null, "APFS reader must recognise the S_IFLNK inode");
    Assert.That(link!.Name, Is.EqualTo(LinkName));
    Assert.That(link.LinkTarget, Is.EqualTo(TargetName),
      "target must come from the com.apple.fs.symlink embedded xattr");
    Assert.That(link.Size, Is.EqualTo(TargetName.Length),
      "the link's own size is the target-path byte length");

    var listing = new ApfsFormatDescriptor().List(new MemoryStream(image), password: null);
    var listedLink = listing.Single(e => e.IsSymlink);
    Assert.That(listedLink.TargetSize, Is.EqualTo(TargetSizeBytes),
      "resolved target size must be the pointed-to file's size");
  }

  private static byte[] BuildImage() {
    var image = new byte[7 * BlockSize];

    // ── NXSB (block 0) ──
    var nx = image.AsSpan(0);
    BinaryPrimitives.WriteUInt32LittleEndian(nx[32..], 0x4253584E);      // "NXSB"
    BinaryPrimitives.WriteUInt32LittleEndian(nx[36..], BlockSize);       // nx_block_size
    BinaryPrimitives.WriteUInt64LittleEndian(nx[184..], ApsbVirtOid);    // nx_fs_oid[0]
    BinaryPrimitives.WriteUInt64LittleEndian(nx[3072..], CtrOmapObj);    // writer-stamped ctr OMAP phys hint

    // ── container OMAP object (block 1) → its btree root ──
    WriteOmapObject(image, CtrOmapObj, treeBlock: CtrOmapTree);
    // ── container OMAP btree: ApsbVirtOid → ApsbBlock ──
    WriteOmapTree(image, CtrOmapTree, ApsbVirtOid, ApsbBlock);

    // ── APSB (block 3) ──
    var apsb = image.AsSpan((int)(ApsbBlock * BlockSize));
    BinaryPrimitives.WriteUInt32LittleEndian(apsb[32..], 0x42535041);    // "APSB"
    BinaryPrimitives.WriteUInt64LittleEndian(apsb[392..], VolOmapObj);   // apfs_omap_oid (phys)
    BinaryPrimitives.WriteUInt64LittleEndian(apsb[400..], RootTreeVirtOid); // apfs_root_tree_oid (virt)

    // ── volume OMAP object (block 4) → its btree root ──
    WriteOmapObject(image, VolOmapObj, treeBlock: VolOmapTree);
    // ── volume OMAP btree: RootTreeVirtOid → FsTreeBlock ──
    WriteOmapTree(image, VolOmapTree, RootTreeVirtOid, FsTreeBlock);

    // ── FS-tree leaf (block 6) ──
    WriteFsTree(image, FsTreeBlock);

    return image;
  }

  private static void WriteOmapObject(byte[] image, ulong block, ulong treeBlock) {
    var o = image.AsSpan((int)(block * BlockSize));
    BinaryPrimitives.WriteUInt32LittleEndian(o[24..], 0x0000000B);       // o_type = OBJECT_TYPE_OMAP
    BinaryPrimitives.WriteUInt64LittleEndian(o[48..], treeBlock);        // om_tree_oid (phys)
  }

  private static void WriteOmapTree(byte[] image, ulong block, ulong oid, ulong paddr) {
    // omap_key_t: ok_oid(u64) + ok_xid(u64). omap_val_t: flags(u32)+size(u32)+paddr(u64).
    var key = new byte[16];
    BinaryPrimitives.WriteUInt64LittleEndian(key, oid);
    var val = new byte[16];
    BinaryPrimitives.WriteUInt32LittleEndian(val.AsSpan(4), BlockSize);
    BinaryPrimitives.WriteUInt64LittleEndian(val.AsSpan(8), paddr);
    WriteLeafNode(image, block, oType: 0x00000002 /* BTREE */, [(key, val)]);
  }

  private static void WriteFsTree(byte[] image, ulong block) {
    var recs = new List<(byte[] Key, byte[] Val)> {
      (InodeKey(RootIno),   InodeVal(parent: RootIno, mode: 0x4000, size: 0)),      // root dir
      (DrecKey(RootIno, TargetName), DrecVal(TargetIno, dirType: 8)),
      (DrecKey(RootIno, LinkName),   DrecVal(LinkIno,   dirType: 8)),
      (InodeKey(TargetIno), InodeVal(parent: RootIno, mode: 0x8000, size: TargetSizeBytes)), // reg file
      (InodeKey(LinkIno),   InodeVal(parent: RootIno, mode: 0xA000, size: TargetName.Length)), // symlink
      (XattrKey(LinkIno, "com.apple.fs.symlink"), XattrVal(TargetName)),
    };
    WriteLeafNode(image, block, oType: 0x0000000E /* FSTREE */, recs);
  }

  // Variable-KV B-tree leaf node matching ApfsReader.EnumerateBtreeLeafRecords:
  // toc at btnHeaderEnd(56)+table_space.off; values grow down from node.Length-40.
  private static void WriteLeafNode(byte[] image, ulong block, uint oType,
      List<(byte[] Key, byte[] Val)> recs) {
    var node = image.AsSpan((int)(block * BlockSize), BlockSize);
    BinaryPrimitives.WriteUInt32LittleEndian(node[24..], oType);         // o_type
    BinaryPrimitives.WriteUInt16LittleEndian(node[32..], 0x0003);        // btn_flags = ROOT|LEAF (variable KV)
    BinaryPrimitives.WriteUInt16LittleEndian(node[34..], 0);             // btn_level = 0 (leaf)
    BinaryPrimitives.WriteUInt32LittleEndian(node[36..], (uint)recs.Count); // btn_nkeys
    BinaryPrimitives.WriteUInt16LittleEndian(node[40..], 0);             // btn_table_space.off
    var tableLen = recs.Count * 8;
    BinaryPrimitives.WriteUInt16LittleEndian(node[42..], (ushort)tableLen);

    const int tocAbs = 56;
    var keyAreaStart = tocAbs + tableLen;
    var valAreaEnd = BlockSize - 40; // isRoot → reader skips 40-byte btree_info trailer

    var keyCursor = keyAreaStart;
    var valCursor = 0;
    for (var i = 0; i < recs.Count; i++) {
      var (key, val) = recs[i];
      var keyOff = keyCursor - keyAreaStart;
      key.CopyTo(node[keyCursor..]);
      keyCursor += key.Length;

      valCursor += val.Length;
      var valOff = valCursor; // reader: valAbs = valAreaEnd - valOff
      val.CopyTo(node[(valAreaEnd - valCursor)..]);

      var e = tocAbs + i * 8;
      BinaryPrimitives.WriteUInt16LittleEndian(node[e..], (ushort)keyOff);
      BinaryPrimitives.WriteUInt16LittleEndian(node[(e + 2)..], (ushort)key.Length);
      BinaryPrimitives.WriteUInt16LittleEndian(node[(e + 4)..], (ushort)valOff);
      BinaryPrimitives.WriteUInt16LittleEndian(node[(e + 6)..], (ushort)val.Length);
    }
  }

  private static byte[] InodeKey(ulong ino) {
    var k = new byte[8];
    BinaryPrimitives.WriteUInt64LittleEndian(k, ino | (3UL << 60)); // APFS_TYPE_INODE
    return k;
  }

  private static byte[] InodeVal(ulong parent, ushort mode, int size) {
    var v = new byte[92];
    BinaryPrimitives.WriteUInt64LittleEndian(v, parent);
    BinaryPrimitives.WriteUInt16LittleEndian(v.AsSpan(80), mode);            // mode @80
    BinaryPrimitives.WriteUInt64LittleEndian(v.AsSpan(84), (ulong)size);     // uncompressed_size @84
    return v;
  }

  private static byte[] DrecKey(ulong parent, string name) {
    var nameBytes = Encoding.UTF8.GetBytes(name + "\0");
    var k = new byte[12 + nameBytes.Length];
    BinaryPrimitives.WriteUInt64LittleEndian(k, parent | (9UL << 60)); // APFS_TYPE_DIR_REC
    BinaryPrimitives.WriteUInt32LittleEndian(k.AsSpan(8), (uint)nameBytes.Length & 0x3FF);
    nameBytes.CopyTo(k, 12);
    return k;
  }

  private static byte[] DrecVal(ulong childIno, ushort dirType) {
    var v = new byte[18];
    BinaryPrimitives.WriteUInt64LittleEndian(v, childIno);
    BinaryPrimitives.WriteUInt16LittleEndian(v.AsSpan(16), dirType);
    return v;
  }

  private static byte[] XattrKey(ulong ino, string name) {
    var nameBytes = Encoding.UTF8.GetBytes(name + "\0");
    var k = new byte[10 + nameBytes.Length];
    BinaryPrimitives.WriteUInt64LittleEndian(k, ino | (4UL << 60)); // APFS_TYPE_XATTR
    BinaryPrimitives.WriteUInt16LittleEndian(k.AsSpan(8), (ushort)nameBytes.Length);
    nameBytes.CopyTo(k, 10);
    return k;
  }

  private static byte[] XattrVal(string target) {
    var data = Encoding.UTF8.GetBytes(target + "\0");
    var v = new byte[4 + data.Length];
    BinaryPrimitives.WriteUInt16LittleEndian(v, 0x0002);              // XATTR_DATA_EMBEDDED
    BinaryPrimitives.WriteUInt16LittleEndian(v.AsSpan(2), (ushort)data.Length);
    data.CopyTo(v, 4);
    return v;
  }
}
