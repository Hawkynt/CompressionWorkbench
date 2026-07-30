#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;
using System.Text;

namespace FileSystem.F2fs;

/// <summary>
/// Reads F2FS filesystem images using the on-disk layout defined by the Linux kernel
/// header <c>include/linux/f2fs_fs.h</c>. Handles both traditional data-block dentries
/// and inline dentries (i_inline F2FS_INLINE_DENTRY flag).
/// </summary>
public sealed class F2fsReader : IDisposable {
  // --- Superblock / on-disk constants ---
  private const uint F2fsMagic = 0xF2F52010;
  private const int SbOffset = 1024;
  private const uint RootNodeId = 3;
  private const int SlotLen = 8;

  // Inline flag bits (kernel f2fs_fs.h).
  private const byte F2fsInlineDentry = 0x04;

  // Superblock field offsets (relative to SB-struct start, i.e. file offset 1024).
  private const int SbLogBlocksizeOff = 16;
  private const int SbNatBlkAddrOff = 84;
  private const int SbMainBlkAddrOff = 92;

  // Inode field offsets (kernel struct f2fs_inode).
  private const int InodeModeOff = 0;
  private const int InodeInlineFlagOff = 3;
  private const int InodeSizeOff = 16;
  private const int InodeIAddrOff = 360; // start of i_addr[DEF_ADDRS_PER_INODE]
  private const int AddrsPerInode = 923;

  // Node-tree geometry: (4096 - 24 footer) / 4 addresses or node ids per node block,
  // and the five i_nid[] pointers immediately after i_addr[].
  private const int AddrsPerBlock = 1018;
  private const int NidsPerBlock = 1018;
  private const int InodeINidOff = InodeIAddrOff + AddrsPerInode * 4; // 4052

  // Inline dentry region inside inode (see F2fsWriter.WriteRootInodeInline for layout).
  // Kernel reserves DEFAULT_INLINE_XATTR_ADDRS=50 __le32 slots at end of i_addr when
  // F2FS_INLINE_DENTRY is set (no F2FS_FEATURE_FLEXIBLE_INLINE_XATTR), so the usable
  // inline data region is 4 * (923 - 50 - 1) = 3488 bytes starting at i_addr[1] = offset 364.
  // bitmap[23] + reserved[7] + dentry[182][11] + filename[182][8] = 3488.
  private const int InlineDentryStart = 364;
  private const int NrInlineDentry = 182;
  private const int InlineBitmapSize = (NrInlineDentry + 7) / 8; // 23
  private const int InlineReservedSize = 7;
  private const int InlineDentryBase = InlineDentryStart + InlineBitmapSize + InlineReservedSize;
  private const int InlineNameBase = InlineDentryBase + NrInlineDentry * 11;

  private readonly ImageAccessor _data;
  private readonly List<F2fsEntry> _entries = [];

  private int _blockSize;
  private int _natBlkAddr; // in blocks
  private int _mainBlkAddr;

  public IReadOnlyList<F2fsEntry> Entries => this._entries;

  public F2fsReader(Stream stream, bool leaveOpen = true) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.CanSeek) stream.Position = 0;
    // Blocks are pulled on demand: the metadata a walk touches is a small
    // fraction of a volume whose data area may run to gigabytes.
    this._data = new ImageAccessor(stream, leaveOpen);
    this.Parse();
  }

  /// <summary>Total size of the backing image in bytes.</summary>
  public long Length => this._data.Length;

  private void Parse() {
    if (this._data.Length < SbOffset + 200)
      throw new InvalidDataException("F2FS: image too small.");

    var sb = (long)SbOffset;
    var magic = this._data.ReadUInt32(sb);
    if (magic != F2fsMagic)
      throw new InvalidDataException("F2FS: invalid superblock magic.");

    var logBlockSize = this._data.ReadUInt32(sb + SbLogBlocksizeOff);
    this._blockSize = 1 << (int)logBlockSize;
    if (this._blockSize < 512) this._blockSize = 4096;

    this._natBlkAddr = (int)this._data.ReadUInt32(sb + SbNatBlkAddrOff);
    this._mainBlkAddr = (int)this._data.ReadUInt32(sb + SbMainBlkAddrOff);

    this.ReadDirectory(RootNodeId, "");
  }

  /// <summary>Reads one whole block, or null when it falls outside the image.</summary>
  private byte[]? ReadBlock(int blockAddr) {
    if (blockAddr <= 0) return null;
    var off = (long)blockAddr * this._blockSize;
    if (off < 0 || off + this._blockSize > this._data.Length) return null;
    return this._data.Read(off, this._blockSize);
  }

  private int LookupNat(uint nodeId) {
    // NAT entry layout: version(1) + ino(4) + block_addr(4) = 9 bytes.
    var entriesPerBlock = this._blockSize / 9;
    if (entriesPerBlock == 0) entriesPerBlock = 455;
    var natBlock = (int)(nodeId / entriesPerBlock);
    var natIdx = (int)(nodeId % entriesPerBlock);
    var natOff = (long)(this._natBlkAddr + natBlock) * this._blockSize + natIdx * 9;
    if (natOff + 9 > this._data.Length) return -1;

    return (int)this._data.ReadUInt32(natOff + 5);
  }

  private void ReadDirectory(uint nodeId, string basePath) {
    var inode = this.ReadBlock(this.LookupNat(nodeId));
    if (inode == null) return;

    var mode = BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(InodeModeOff));
    if ((mode & 0xF000) != 0x4000) return; // not directory

    var inlineFlag = inode[InodeInlineFlagOff];

    if ((inlineFlag & F2fsInlineDentry) != 0) {
      this.ParseInlineDentries(inode, basePath);
      return;
    }

    // Traditional layout: iterate the inode's data blocks, each a dentry block.
    foreach (var dataBlock in this.EnumerateDataBlocks(inode)) {
      var block = this.ReadBlock(dataBlock);
      if (block == null) continue;
      this.ParseDentryBlock(block, basePath);
    }
  }

  /// <summary>Block size of the volume, from the superblock.</summary>
  public int BlockSize => this._blockSize;

  /// <summary>First block of the main area; everything below it is metadata.</summary>
  public long MainAreaStart => (long)this._mainBlkAddr * this._blockSize;

  /// <summary>
  /// The blocks the root directory occupies: its inode and, when its dentries
  /// do not fit inline, the blocks holding them. Nothing in the listing points
  /// at these, so they have to be claimed on their own account.
  /// </summary>
  public IEnumerable<long> RootBlocks() {
    var inodeBlock = this.LookupNat(RootNodeId);
    if (inodeBlock <= 0) yield break;
    yield return inodeBlock;

    var inode = this.ReadBlock(inodeBlock);
    if (inode == null) yield break;
    for (var slot = 0; slot < 5; ++slot) {
      var nid = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(InodeINidOff + slot * 4));
      if (nid == 0) continue;
      var levels = slot switch { 0 or 1 => 1, 2 or 3 => 2, _ => 3 };
      foreach (var node in this.EnumerateNodeBlocks(nid, levels))
        yield return node;
    }
    foreach (var block in this.EnumerateDataBlocks(inode))
      if (block > 0) yield return block;
  }

  /// <summary>
  /// Where an entry's blocks are: its data blocks, and the node blocks that
  /// address them. Both have to survive a wipe — the node blocks are what turn
  /// the data back into a file.
  /// </summary>
  public IEnumerable<(long Block, bool IsData)> EnumerateBlocks(F2fsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    var inodeBlock = this.LookupNat(entry.NodeId);
    if (inodeBlock <= 0) yield break;
    yield return (inodeBlock, false);

    var inode = this.ReadBlock(inodeBlock);
    if (inode == null) yield break;
    if (entry.IsDirectory) {
      foreach (var block in this.EnumerateDataBlocks(inode))
        if (block > 0) yield return (block, false);
      yield break;
    }

    for (var slot = 0; slot < 5; ++slot) {
      var nid = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(InodeINidOff + slot * 4));
      if (nid == 0) continue;
      var levels = slot switch { 0 or 1 => 1, 2 or 3 => 2, _ => 3 };
      foreach (var node in this.EnumerateNodeBlocks(nid, levels))
        yield return (node, false);
    }

    foreach (var block in this.EnumerateDataBlocks(inode))
      if (block > 0) yield return (block, true);
  }

  /// <summary>The node blocks of a subtree, the root of it included.</summary>
  private IEnumerable<long> EnumerateNodeBlocks(uint nid, int levels) {
    var block = this.LookupNat(nid);
    if (block <= 0) yield break;
    yield return block;
    if (levels <= 1) yield break;

    var node = this.ReadBlock(block);
    if (node == null) yield break;
    for (var i = 0; i < NidsPerBlock; ++i) {
      var child = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(i * 4));
      if (child == 0) continue;
      foreach (var descendant in this.EnumerateNodeBlocks(child, levels - 1))
        yield return descendant;
    }
  }

  /// <summary>
  /// Yields a file's data-block addresses in logical order: the inode's own 923
  /// addresses, then the blocks reached through i_nid[] — two direct nodes, two
  /// indirect nodes over direct ones, and a double-indirect node. A zero address
  /// is a hole and is yielded as zero so the caller keeps its place in the file.
  /// </summary>
  private IEnumerable<int> EnumerateDataBlocks(byte[] inode) {
    for (var i = 0; i < AddrsPerInode; ++i)
      yield return (int)BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(InodeIAddrOff + i * 4));

    for (var slot = 0; slot < 5; ++slot) {
      var nid = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(InodeINidOff + slot * 4));
      if (nid == 0) continue;
      var levels = slot switch { 0 or 1 => 1, 2 or 3 => 2, _ => 3 };
      foreach (var blk in this.EnumerateNode(nid, levels))
        yield return blk;
    }
  }

  /// <summary>
  /// Walks one node block. At level 1 its entries are data-block addresses; above
  /// that they are node ids one level down.
  /// </summary>
  private IEnumerable<int> EnumerateNode(uint nid, int levels) {
    var node = this.ReadBlock(this.LookupNat(nid));
    if (node == null) yield break;

    if (levels <= 1) {
      for (var i = 0; i < AddrsPerBlock; ++i)
        yield return (int)BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(i * 4));
      yield break;
    }

    for (var i = 0; i < NidsPerBlock; ++i) {
      var child = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(i * 4));
      if (child == 0) continue;
      foreach (var blk in this.EnumerateNode(child, levels - 1))
        yield return blk;
    }
  }

  /// <summary>
  /// Parses inline dentries embedded in the inode (F2FS_INLINE_DENTRY layout).
  /// </summary>
  private void ParseInlineDentries(byte[] inode, string basePath) {
    const int bitmapOff = InlineDentryStart;
    const int dentryOff = InlineDentryBase;
    const int nameOff = InlineNameBase;
    this.ParseDentryRegion(inode, bitmapOff, dentryOff, nameOff, NrInlineDentry, basePath);
  }

  /// <summary>
  /// Walks a bitmap-plus-slots dentry region — the same shape whether it lives
  /// inline in an inode or in a dedicated dentry block.
  /// </summary>
  private void ParseDentryRegion(byte[] buffer, int bitmapOff, int dentryOff, int nameOff,
    int slotCount, string basePath) {
    var nrInlineDentry = slotCount;

    for (var i = 0; i < nrInlineDentry;) {
      var byteIdx = bitmapOff + i / 8;
      if (byteIdx >= buffer.Length) break;
      if ((buffer[byteIdx] & (1 << (i % 8))) == 0) { ++i; continue; }

      var entryOff = dentryOff + i * 11;
      if (entryOff + 11 > buffer.Length) break;

      var ino = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(entryOff + 4));
      var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(entryOff + 8));
      var fileType = buffer[entryOff + 10];

      // A name spans ceil(nameLen / SLOT_LEN) consecutive slots; advance past all of them.
      var slots = nameLen <= 0 ? 1 : (nameLen + SlotLen - 1) / SlotLen;

      if (ino == 0 || nameLen == 0 || nameLen > 255) { i += slots; continue; }

      var fnOff = nameOff + i * SlotLen;
      var fnLen = Math.Min((int)nameLen, buffer.Length - fnOff);
      if (fnLen <= 0 || fnOff + fnLen > buffer.Length) { i += slots; continue; }

      var name = Encoding.UTF8.GetString(buffer, fnOff, fnLen);
      name = name.TrimEnd('\0');
      i += slots;
      if (string.IsNullOrEmpty(name) || name == "." || name == "..") continue;

      var fullPath = string.IsNullOrEmpty(basePath) ? name : $"{basePath}/{name}";
      var isDir = fileType == 2;
      long childSize = 0;
      if (!isDir) childSize = this.ReadInodeSize(ino);

      this._entries.Add(new F2fsEntry {
        Name = fullPath,
        Size = isDir ? 0 : childSize,
        IsDirectory = isDir,
        NodeId = ino,
      });

      if (isDir) this.ReadDirectory(ino, fullPath);
    }
  }

  private void ParseDentryBlock(byte[] block, string basePath) {
    // F2FS dentry block: bitmap(27 bytes) + reserved(3) + dentry[214](11 each) + filename[214][8].
    const int nrDentry = 214;
    const int bitmapSize = (nrDentry + 7) / 8; // 27
    const int reserved = 3;
    const int dentryOff = bitmapSize + reserved;
    const int nameOff = dentryOff + nrDentry * 11;
    this.ParseDentryRegion(block, 0, dentryOff, nameOff, nrDentry, basePath);
  }

  private long ReadInodeSize(uint ino) {
    var inode = this.ReadBlock(this.LookupNat(ino));
    if (inode == null) return 0;
    return (long)BinaryPrimitives.ReadUInt64LittleEndian(inode.AsSpan(InodeSizeOff));
  }

  public byte[] Extract(F2fsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    var size = this.SizeOf(entry);
    if (size <= 0) return [];
    if (size > Array.MaxLength)
      throw new IOException(
        $"F2FS: '{entry.Name}' is {size:N0} bytes, past the array limit; use ExtractTo.");

    var result = new byte[size];
    using var target = new MemoryStream(result, writable: true);
    this.ExtractTo(entry, target);
    return result;
  }

  /// <summary>The file's logical size, straight from its inode.</summary>
  public long SizeOf(F2fsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return 0;
    var inode = this.ReadBlock(this.LookupNat(entry.NodeId));
    if (inode == null) return 0;
    return (long)BinaryPrimitives.ReadUInt64LittleEndian(inode.AsSpan(InodeSizeOff));
  }

  /// <summary>
  /// Writes <paramref name="entry" />'s contents into <paramref name="destination" />
  /// block by block, following the inode's node tree. Returns the byte count.
  /// </summary>
  public long ExtractTo(F2fsEntry entry, Stream destination) {
    ArgumentNullException.ThrowIfNull(entry);
    ArgumentNullException.ThrowIfNull(destination);
    if (entry.IsDirectory) return 0;

    var inode = this.ReadBlock(this.LookupNat(entry.NodeId));
    if (inode == null) return 0;
    var size = (long)BinaryPrimitives.ReadUInt64LittleEndian(inode.AsSpan(InodeSizeOff));
    if (size <= 0) return 0;

    // An address of zero is a hole, which reads back as zeros.
    var hole = new byte[this._blockSize];
    long written = 0;
    foreach (var dataBlock in this.EnumerateDataBlocks(inode)) {
      if (written >= size) break;
      var len = (int)Math.Min(this._blockSize, size - written);
      var off = (long)dataBlock * this._blockSize;
      if (dataBlock <= 0 || off + len > this._data.Length)
        destination.Write(hole, 0, len);
      else
        this._data.CopyTo(off, destination, len);
      written += len;
    }
    return written;
  }

  public void Dispose() => this._data.Dispose();
}
