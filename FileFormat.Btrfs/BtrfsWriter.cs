#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Btrfs;

/// <summary>
/// Writes a minimal Btrfs filesystem image. Skips the chunk tree entirely
/// (reader has identity-mapping fallback in <c>LogicalToPhysical</c>).
/// Files are stored as INODE_ITEM + DIR_INDEX + EXTENT_DATA (inline) items in
/// a single FS tree leaf node. Roundtrips through <see cref="BtrfsReader"/>.
/// </summary>
public sealed class BtrfsWriter {
  private const int SectorSize = 4096;
  private const int NodeSize = 16384;
  private const int SbOffset = 0x10000;
  private const int RootTreeOff = 0x20000;
  private const int FsTreeOff = 0x30000;
  private const int TotalSize = 0x40000; // no extra data blocks — all extents inline

  // Key types (from reader)
  private const byte InodeItem = 1;
  private const byte DirIndex = 96;
  private const byte ExtentData = 108;
  private const byte RootItem = 132;

  private const long FsTreeObjectId = 5;
  private const long FirstFreeObjectId = 256;

  private static readonly byte[] Magic = "_BHRfS_M"u8.ToArray();

  private readonly List<(string name, byte[] data)> _files = [];

  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    if (_files.Count >= 64)
      throw new InvalidOperationException("BtrfsWriter supports at most 64 files in a single leaf node.");
    var leaf = Path.GetFileName(name);
    if (leaf.Length > 255) leaf = leaf[..255];
    _files.Add((leaf, data));
  }

  public void WriteTo(Stream output) {
    var image = new byte[TotalSize];

    // ── Superblock ──
    Magic.CopyTo(image, SbOffset + 64);
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(SbOffset + 80), RootTreeOff);
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(SbOffset + 88), 0x7FFFFFFFL); // invalid chunk tree → reader skips
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(SbOffset + 128), SectorSize);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(SbOffset + 132), NodeSize);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(SbOffset + 196), 0); // sys_chunk_array_size = 0

    // ── Root tree (leaf with one ROOT_ITEM for FS_TREE) ──
    var rootData = new byte[184]; // inode(160) + generation(8) + root_dirid(8) + bytenr(8) = 184 bytes
    BinaryPrimitives.WriteInt64LittleEndian(rootData.AsSpan(176), FsTreeOff);
    WriteLeafNode(image, RootTreeOff, [(FsTreeObjectId, RootItem, 0L, rootData)]);

    // ── FS tree ──
    BuildFsTree(image);

    output.Write(image);
  }

  private void BuildFsTree(byte[] image) {
    var items = new List<(long objId, byte type, long offset, byte[] data)>();

    // Root directory INODE_ITEM (objectid = 256)
    var rootInodeData = new byte[24]; // generation(8) + transid(8) + size(8)
    items.Add((FirstFreeObjectId, InodeItem, 0L, rootInodeData));

    for (var i = 0; i < _files.Count; i++) {
      var childInode = FirstFreeObjectId + 1 + i;
      var file = _files[i];

      // File INODE_ITEM
      var fileInode = new byte[24];
      BinaryPrimitives.WriteInt64LittleEndian(fileInode.AsSpan(16), file.data.Length); // size
      items.Add((childInode, InodeItem, 0L, fileInode));

      // DIR_INDEX in root directory
      var nameBytes = Encoding.UTF8.GetBytes(file.name);
      var dirIdx = new byte[30 + nameBytes.Length];
      BinaryPrimitives.WriteInt64LittleEndian(dirIdx.AsSpan(0), childInode); // child key.objectid
      dirIdx[8] = InodeItem; // child key.type
      BinaryPrimitives.WriteUInt16LittleEndian(dirIdx.AsSpan(25), 0); // data_len
      BinaryPrimitives.WriteUInt16LittleEndian(dirIdx.AsSpan(27), (ushort)nameBytes.Length);
      dirIdx[29] = 1; // type: 1 = regular file
      nameBytes.CopyTo(dirIdx, 30);
      // key.offset for DIR_INDEX is the index within the directory (unique per entry)
      items.Add((FirstFreeObjectId, DirIndex, 2 + i, dirIdx));

      // EXTENT_DATA (inline)
      var extent = new byte[21 + file.data.Length];
      BinaryPrimitives.WriteInt64LittleEndian(extent.AsSpan(8), file.data.Length); // ram_bytes
      extent[16] = 0; // compression = none
      extent[20] = 0; // type = inline
      file.data.CopyTo(extent, 21);
      items.Add((childInode, ExtentData, 0L, extent));
    }

    // Btrfs keys must be sorted by (objectid, type, offset) in a leaf node.
    items.Sort((a, b) => {
      var c = a.objId.CompareTo(b.objId);
      if (c != 0) return c;
      c = a.type.CompareTo(b.type);
      if (c != 0) return c;
      return a.offset.CompareTo(b.offset);
    });

    WriteLeafNode(image, FsTreeOff, items);
  }

  // ── Leaf node serialisation ──

  private static void WriteLeafNode(byte[] image, int nodeOff, List<(long objId, byte type, long offset, byte[] data)> items) {
    // Node header is 101 bytes; level=0 for leaf.
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(nodeOff + 48), nodeOff); // bytenr (self)
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(nodeOff + 88), (uint)items.Count);
    image[nodeOff + 92] = 0; // level = 0 (leaf)

    // Data grows backward from node_end; item headers grow forward from offset+101.
    var dataEnd = NodeSize;
    for (var i = 0; i < items.Count; i++) {
      var (objId, type, offset, data) = items[i];
      dataEnd -= data.Length;
      var dataOffsetInItems = dataEnd - 101; // relative to (nodeOff + 101)

      // Item header (25 bytes)
      var itemOff = nodeOff + 101 + i * 25;
      BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(itemOff), objId);
      image[itemOff + 8] = type;
      BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(itemOff + 9), offset);
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(itemOff + 17), (uint)dataOffsetInItems);
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(itemOff + 21), (uint)data.Length);

      // Data
      data.CopyTo(image, nodeOff + 101 + dataOffsetInItems);
    }
  }
}
