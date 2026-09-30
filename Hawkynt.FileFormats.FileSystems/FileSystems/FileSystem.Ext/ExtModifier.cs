#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Checksums;

namespace FileSystem.Ext;

/// <summary>
/// In-place ext2/3/4 modifier. Performs <b>O(touched bytes)</b> random-access I/O
/// against an ext image: only the superblock, the relevant block-group descriptors,
/// the block + inode bitmaps of the touched groups, the affected inode slots, the
/// root directory's data blocks (plus any newly-grown directory block), and the
/// file's own data/metadata blocks are read and written.
///
/// <para>Genuine in-place coverage (no whole-image re-pack):</para>
/// <list type="bullet">
///   <item><b>Large files</b> — ext2/3 single + double + triple indirect blocks;
///   ext4 inode-resident extent leaves (when the inode carries the EXTENTS flag and
///   the volume advertises the EXTENTS feature). Block allocation, i_blocks/i_size,
///   group-descriptor + superblock free counts all maintained.</item>
///   <item><b>Any directory</b> — paths are resolved from the root, missing folders
///   are created, and a full linear directory grows by a block. Entries are looked up
///   and removed in hashed (htree) directories too; adding to one is refused, because
///   the entry would have to be filed under a name hash this editor does not compute.</item>
///   <item><b>Links</b> — removing one name of a hard-linked file only drops a link;
///   a shared extended-attribute block only loses a reference.</item>
///   <item><b>Multiple block groups</b> — allocation scans every group with free
///   space; the right group's descriptor + bitmaps (and, when <c>metadata_csum</c>
///   / <c>uninit_bg</c> is set, their checksums and the INODE/BLOCK_UNINIT flags +
///   <c>itable_unused</c>) are updated.</item>
///   <item><b>Checksums</b> — when <c>metadata_csum</c> is set: crc32c bitmap,
///   inode, group-descriptor and superblock checksums are recomputed; when the older
///   <c>gdt_csum</c> (uninit_bg) is set the crc16 group-descriptor checksum is used.</item>
/// </list>
/// </summary>
public static class ExtModifier {

  private const int SuperblockOffset = 1024;
  private const ushort ExtMagic = 0xEF53;
  private const ushort InodeModeRegular = 0x8000;
  private const ushort InodeModeDir = 0x4000;
  private const ushort DefaultMode = InodeModeRegular | 0x01A4; // 0644
  private const uint RootInode = 2;
  private const int MaxDirectBlocks = 12;
  private const byte FileTypeRegular = 1;
  private const ushort ExtentMagic = 0xF30A;

  // Feature flags we care about.
  private const uint IncompatFiletype = 0x0002;
  private const uint IncompatExtents = 0x0040;
  private const uint Incompat64Bit = 0x0080;
  private const uint IncompatCsumSeed = 0x2000;
  private const uint RoCompatGdtCsum = 0x0010;   // uninit_bg (crc16 group-desc csum + lazy itable)
  private const uint RoCompatMetadataCsum = 0x0400;

  // Block group descriptor flags.
  private const ushort BgInodeUninit = 0x0001;
  private const ushort BgBlockUninit = 0x0002;
  private const ushort BgInodeZeroed = 0x0004;

  /// <summary>Cached superblock-derived geometry for a single Add/Remove call.</summary>
  private sealed class Geometry {
    public int BlockSize;
    public uint FirstDataBlock;
    public uint BlocksCount;       // low 32 bits of total blocks
    public uint InodesCount;
    public uint InodesPerGroup;
    public uint BlocksPerGroup;
    public uint FirstUserInode;
    public int InodeSize;
    public uint FeatureIncompat;
    public uint FeatureRoCompat;
    public int DescSize;           // 32 or 64
    public uint GroupCount;
    public long BgdtOffset;
    public uint CsumSeed;          // crc32c seed for metadata_csum
    public byte[] Uuid = new byte[16];
    public bool HasMetadataCsum => (FeatureRoCompat & RoCompatMetadataCsum) != 0;
    public bool HasGdtCsum => (FeatureRoCompat & RoCompatGdtCsum) != 0;
    public bool HasExtentsFeature => (FeatureIncompat & IncompatExtents) != 0;

    public long BgdOffset(uint group) => BgdtOffset + (long)group * DescSize;
  }

  /// <summary>
  /// Thrown when a case genuinely cannot be handled in place (for example an htree
  /// directory that would need a new hashed entry). Nothing has been written when
  /// it is thrown.
  /// </summary>
  public sealed class InPlaceUnsupportedException(string message) : IOException(message);

  /// <summary>
  /// Adds a file at <paramref name="path" /> (separated by <c>/</c> or <c>\</c>) to
  /// an existing ext2/3/4 image, genuinely in place. Missing folders on the way are
  /// created. An existing regular file of the same name is replaced: the new
  /// content gets its own inode, the directory entry is repointed at it, and only
  /// then is the old inode released — so a replace that cannot be completed leaves
  /// the old file as it was.
  /// </summary>
  public static void AddFile(Stream image, string path, byte[] data) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(path);
    ArgumentNullException.ThrowIfNull(data);
    var parts = SplitPath(path);
    if (parts.Length == 0) throw new ArgumentException("name is empty", nameof(path));
    var name = parts[^1];

    var geom = ReadGeometry(image);
    // Everything that could refuse is checked before anything is written.
    var parentInodeNum = ResolveExisting(image, geom, parts[..^1], out var missing);
    EnsureWritableDirectory(image, geom, parentInodeNum);
    foreach (var folder in missing)
      parentInodeNum = MakeDirectory(image, geom, parentInodeNum, folder);

    var parentInode = ReadInode(image, geom, parentInodeNum);
    var dirBlocks = DirectoryBlocks(image, geom, parentInode);
    if (dirBlocks.Count == 0)
      throw new IOException("ext: directory has no data block.");

    // An entry of the same name: a regular file is replaced, anything else refused.
    uint replacedInode = 0;
    int replaceBlock = -1, replaceOffset = -1;
    byte[]? replaceBytes = null;
    foreach (var b in dirBlocks) {
      var bb = ReadBlock(image, geom, b);
      if (!FindEntry(bb, name, out var off, out _, out var ino)) continue;
      var existing = ReadInode(image, geom, ino);
      var mode = BinaryPrimitives.ReadUInt16LittleEndian(existing.AsSpan(0, 2));
      if ((mode & 0xF000) != InodeModeRegular)
        throw new IOException($"ext: '{path}' already exists and is not a regular file.");
      replacedInode = ino; replaceBlock = b; replaceOffset = off; replaceBytes = bb;
      break;
    }

    var blocksNeeded = data.Length == 0 ? 0 : (data.Length + geom.BlockSize - 1) / geom.BlockSize;
    var useExtents = geom.HasExtentsFeature;

    // ── Allocate inode + data + metadata blocks across groups ──
    var alloc = new Allocator(image, geom);
    var newInodeNum = alloc.AllocateInode()
      ?? throw new IOException("ext: no free inodes available.");

    List<uint> dataBlocks;
    byte[] iblockArea;
    uint extraInodeBlocks;
    try {
      dataBlocks = alloc.AllocateBlocks(blocksNeeded)
        ?? throw new IOException("ext: not enough free blocks for file.");
      iblockArea = useExtents
        ? BuildExtentMapping(image, alloc, geom, newInodeNum, dataBlocks, out extraInodeBlocks)
        : BuildIndirectMapping(image, alloc, geom, dataBlocks, out extraInodeBlocks);
    } catch {
      alloc.Rollback();
      throw;
    }

    // ── Find / grow a directory slot for the new entry (not needed for a replace) ──
    var dirTailReserved = geom.HasMetadataCsum ? 12 : 0;
    var targetDirBlock = -1; byte[] targetDirBytes = null!; var insertOffset = -1; var grewDir = false;
    uint dirExtraBlock = 0;
    if (replacedInode == 0) {
      var newEntrySize = ComputeDirEntrySize(name);
      foreach (var b in dirBlocks) {
        var bb = ReadBlock(image, geom, b);
        if (TrySplitLastEntryForAppend(bb, newEntrySize, out var off, dirTailReserved)) {
          targetDirBlock = b; targetDirBytes = bb; insertOffset = off; break;
        }
      }
      if (targetDirBlock < 0) {
        var dirUsesExtents = (BinaryPrimitives.ReadUInt32LittleEndian(parentInode.AsSpan(32, 4)) & 0x80000) != 0;
        var maxDirBlocks = dirUsesExtents ? 32768 : MaxDirectBlocks;
        if (dirBlocks.Count >= maxDirBlocks) {
          alloc.Rollback();
          throw new InPlaceUnsupportedException("ext: the directory needs a deeper block map to grow; in-place add unsupported.");
        }
        var nb = alloc.AllocateBlock();
        if (nb == null) { alloc.Rollback(); throw new IOException("ext: no free block to grow the directory."); }
        if (dirUsesExtents && !CanGrowExtentDirectory(parentInode, nb.Value)) {
          alloc.Rollback();
          throw new InPlaceUnsupportedException("ext: extent directory leaf full; in-place add unsupported.");
        }
        dirExtraBlock = nb.Value;
        targetDirBlock = (int)dirExtraBlock;
        targetDirBytes = new byte[geom.BlockSize];
        insertOffset = 0;
        grewDir = true;
      }
    }

    // ── Write file data blocks ──
    var written = 0;
    foreach (var b in dataBlocks) {
      var toWrite = Math.Min(geom.BlockSize, data.Length - written);
      var blockBytes = new byte[geom.BlockSize];
      if (toWrite > 0) Array.Copy(data, written, blockBytes, 0, toWrite);
      WriteBlock(image, geom, (int)b, blockBytes);
      written += toWrite;
    }

    // ── Build + write the new inode ──
    WriteInode(image, geom, newInodeNum, BuildInode(geom, DefaultMode, (uint)data.Length, 1,
      (ulong)(dataBlocks.Count + (int)extraInodeBlocks), useExtents ? 0x80000u : 0u, iblockArea));

    if (replacedInode != 0) {
      // Repoint the existing entry, then let the old inode go.
      BinaryPrimitives.WriteUInt32LittleEndian(replaceBytes!.AsSpan(replaceOffset, 4), newInodeNum);
      WriteDirBlock(image, geom, replaceBlock, replaceBytes!, parentInodeNum, isDtreeTail: true);
      DropLink(image, geom, alloc, replacedInode, wipeData: true);
    } else {
      WriteRev1DirEntry(targetDirBytes, insertOffset, newInodeNum, name, FileTypeRegular,
        isLast: true, blockEnd: targetDirBytes.Length);
      WriteDirBlock(image, geom, targetDirBlock, targetDirBytes, parentInodeNum, isDtreeTail: true);
      if (grewDir) {
        parentInode = ReadInode(image, geom, parentInodeNum);
        if ((BinaryPrimitives.ReadUInt32LittleEndian(parentInode.AsSpan(32, 4)) & 0x80000) != 0)
          GrowExtentDirectory(image, geom, parentInodeNum, parentInode, dirExtraBlock);
        else
          GrowDirectDirectory(image, geom, parentInodeNum, parentInode, dirBlocks, dirExtraBlock);
      }
    }

    alloc.Commit();
    if (geom.HasMetadataCsum) {
      WriteInodeChecksum(image, geom, newInodeNum);
      WriteInodeChecksum(image, geom, parentInodeNum);
    }
  }

  /// <summary>
  /// Removes the entry at <paramref name="path" /> from an existing ext image, in
  /// place. Returns false when no such entry exists. A directory must be empty;
  /// a file with further hard links only loses this name.
  /// </summary>
  public static bool RemoveFile(Stream image, string path, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(path);
    var parts = SplitPath(path);
    if (parts.Length == 0) return false;

    var geom = ReadGeometry(image);
    var parentInodeNum = ResolveExisting(image, geom, parts[..^1], out var missing);
    if (missing.Length != 0) return false;
    var parentInode = ReadInode(image, geom, parentInodeNum);
    EnsureReadableDirectory(parentInode);

    int hitBlock = -1; byte[] hitBytes = null!; int entryOffset = -1, prevOffset = -1; uint inodeNum = 0;
    foreach (var b in DirectoryBlocks(image, geom, parentInode)) {
      var bb = ReadBlock(image, geom, b);
      if (FindEntry(bb, parts[^1], out entryOffset, out prevOffset, out inodeNum)) {
        hitBlock = b; hitBytes = bb; break;
      }
    }
    if (hitBlock < 0) return false;

    var inodeBytes = ReadInode(image, geom, inodeNum);
    var mode = BinaryPrimitives.ReadUInt16LittleEndian(inodeBytes.AsSpan(0, 2));
    var isDir = (mode & 0xF000) == InodeModeDir;
    if (isDir && !IsEmptyDirectory(image, geom, inodeBytes))
      throw new IOException($"ext: directory '{path}' is not empty.");

    var alloc = new Allocator(image, geom);
    SpliceOutDirEntry(hitBytes, entryOffset, prevOffset);
    WriteDirBlock(image, geom, hitBlock, hitBytes, parentInodeNum, isDtreeTail: true);

    if (isDir) {
      FreeInodeAndBlocks(image, geom, alloc, inodeNum, inodeBytes, wipeData);
      alloc.CountDirectory(inodeNum, -1);
      // The removed folder's ".." no longer counts as a link to the parent.
      parentInode = ReadInode(image, geom, parentInodeNum);
      var links = BinaryPrimitives.ReadUInt16LittleEndian(parentInode.AsSpan(26, 2));
      if (links > 2) BinaryPrimitives.WriteUInt16LittleEndian(parentInode.AsSpan(26, 2), (ushort)(links - 1));
      WriteInode(image, geom, parentInodeNum, parentInode);
    } else {
      DropLink(image, geom, alloc, inodeNum, wipeData);
    }

    alloc.Commit();
    if (geom.HasMetadataCsum) WriteInodeChecksum(image, geom, parentInodeNum);
    return true;
  }

  // ── Relocation (used by ExtBlockMover) ────────────────────────────────────

  /// <summary>
  /// Repoints the file at <paramref name="path" /> after its blocks
  /// [<paramref name="oldFirst" />, +<paramref name="count" />) were copied to
  /// <paramref name="newFirst" />: every pointer or extent naming them is rewritten,
  /// the new blocks are claimed and the vacated ones released in their groups'
  /// bitmaps, and every checksum covering what changed is recomputed.
  /// </summary>
  /// <exception cref="InvalidOperationException">The path does not name a file
  /// that maps the run, or an extent only partly covered by the run would have to
  /// be split. Nothing has been written.</exception>
  internal static void RepointDataRun(Stream image, string path, uint oldFirst, uint newFirst, int count, bool releaseOld) {
    if (count <= 0 || oldFirst == newFirst) return;
    var geom = ReadGeometry(image);
    var inodeNum = ResolvePathInode(image, geom, path)
      ?? throw new InvalidOperationException($"ext: '{path}' does not name a file on the volume.");
    var inode = ReadInode(image, geom, inodeNum);
    var flags = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(32, 4));
    var writes = new List<(uint Block, byte[] Bytes, bool ExtentNode)>();
    bool changed;
    if ((flags & 0x80000) != 0 && geom.HasExtentsFeature)
      changed = PatchExtentNode(image, geom, inode.AsSpan(40, 60), oldFirst, newFirst, count, writes);
    else
      changed = PatchPointerMap(image, geom, inode, oldFirst, newFirst, count, writes);
    if (!changed)
      throw new InvalidOperationException($"ext: '{path}' does not map blocks {oldFirst}..{oldFirst + count - 1}.");

    var alloc = new Allocator(image, geom);
    for (var i = 0u; i < count; i++) alloc.MarkUsed(newFirst + i);

    var generation = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(100, 4));
    foreach (var (block, bytes, extentNode) in writes) {
      if (extentNode && geom.HasMetadataCsum) StampExtentTail(geom, inodeNum, generation, bytes);
      WriteBlock(image, geom, (int)block, bytes);
    }
    WriteInode(image, geom, inodeNum, inode);
    if (geom.HasMetadataCsum) WriteInodeChecksum(image, geom, inodeNum);

    if (releaseOld) {
      var newEnd = newFirst + (uint)count;
      for (var i = 0u; i < count; i++) {
        var b = oldFirst + i;
        if (b >= newFirst && b < newEnd) continue;   // the run landed partly on itself
        alloc.FreeBlock(b);
      }
    }
    alloc.Commit();
  }

  /// <summary>The inode a path names, or null.</summary>
  private static uint? ResolvePathInode(Stream image, Geometry geom, string path) {
    var parts = SplitPath(path);
    if (parts.Length == 0) return null;
    uint parent;
    try {
      parent = ResolveExisting(image, geom, parts[..^1], out var missing);
      if (missing.Length != 0) return null;
    } catch (IOException) {
      return null;
    }
    foreach (var b in DirectoryBlocks(image, geom, ReadInode(image, geom, parent)))
      if (FindEntry(ReadBlock(image, geom, b), parts[^1], out _, out _, out var ino))
        return ino;
    return null;
  }

  /// <summary>
  /// Rewrites the extents of one extent-tree node that fall inside the moved run;
  /// child nodes are read, patched and queued for writing. The node passed in is
  /// patched in place.
  /// </summary>
  private static bool PatchExtentNode(Stream image, Geometry geom, Span<byte> node, uint oldFirst, uint newFirst,
      int count, List<(uint Block, byte[] Bytes, bool ExtentNode)> writes) {
    if (BinaryPrimitives.ReadUInt16LittleEndian(node) != ExtentMagic) return false;
    var entries = BinaryPrimitives.ReadUInt16LittleEndian(node[2..]);
    var depth = BinaryPrimitives.ReadUInt16LittleEndian(node[6..]);
    var oldEnd = (long)oldFirst + count;
    var changed = false;
    for (var i = 0; i < entries; i++) {
      var off = 12 + i * 12;
      if (off + 12 > node.Length) break;
      if (depth == 0) {
        var len = BinaryPrimitives.ReadUInt16LittleEndian(node[(off + 4)..]);
        if (len > 32768) len -= 32768;          // uninitialised extent: same blocks
        var start = ((long)BinaryPrimitives.ReadUInt16LittleEndian(node[(off + 6)..]) << 32)
                    | BinaryPrimitives.ReadUInt32LittleEndian(node[(off + 8)..]);
        var end = start + len;
        if (end <= oldFirst || start >= oldEnd) continue;
        if (start < oldFirst || end > oldEnd)
          throw new InvalidOperationException(
            $"ext: the move covers only part of an extent ({start}..{end - 1}); splitting it in place is unsupported.");
        var moved = newFirst + (start - oldFirst);
        BinaryPrimitives.WriteUInt16LittleEndian(node[(off + 6)..], (ushort)(moved >> 32));
        BinaryPrimitives.WriteUInt32LittleEndian(node[(off + 8)..], (uint)(moved & 0xFFFFFFFF));
        changed = true;
      } else {
        var leaf = ((long)BinaryPrimitives.ReadUInt16LittleEndian(node[(off + 8)..]) << 32)
                   | BinaryPrimitives.ReadUInt32LittleEndian(node[(off + 4)..]);
        if (leaf <= 0 || leaf >= geom.BlocksCount) continue;
        var child = ReadBlock(image, geom, (int)leaf);
        if (PatchExtentNode(image, geom, child, oldFirst, newFirst, count, writes)) {
          writes.Add(((uint)leaf, child, true));
          changed = true;
        }
      }
    }
    return changed;
  }

  /// <summary>
  /// Rewrites every pointer that names a block inside the moved run — data pointers
  /// and pointers to the file's own indirect blocks alike, since a file's block map
  /// moves with it. Pointer blocks are read where they are now (a moved one has
  /// already been copied to its new home) and queued for writing when they change;
  /// the inode is patched in place.
  /// </summary>
  private static bool PatchPointerMap(Stream image, Geometry geom, byte[] inode, uint oldFirst, uint newFirst, int count,
      List<(uint Block, byte[] Bytes, bool ExtentNode)> writes) {
    var oldEnd = oldFirst + (uint)count;
    uint Translate(uint b) => b >= oldFirst && b < oldEnd ? newFirst + (b - oldFirst) : b;
    var changed = false;
    for (var i = 0; i < 12; i++) {
      var ptr = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(40 + i * 4, 4));
      if (ptr == 0 || Translate(ptr) == ptr) continue;
      BinaryPrimitives.WriteUInt32LittleEndian(inode.AsSpan(40 + i * 4, 4), Translate(ptr));
      changed = true;
    }
    for (var level = 1; level <= 3; level++) {
      var field = 84 + level * 4;
      var ptr = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(field, 4));
      if (ptr == 0 || ptr >= geom.BlocksCount) continue;
      var moved = Translate(ptr);
      if (moved != ptr) {
        BinaryPrimitives.WriteUInt32LittleEndian(inode.AsSpan(field, 4), moved);
        changed = true;
      }
      changed |= PatchIndirect(image, geom, moved, level, Translate, writes);
    }
    return changed;
  }

  private static bool PatchIndirect(Stream image, Geometry geom, uint block, int level, Func<uint, uint> translate,
      List<(uint Block, byte[] Bytes, bool ExtentNode)> writes) {
    var buf = ReadBlock(image, geom, (int)block);
    var changed = false;
    var own = false;
    for (var i = 0; i < geom.BlockSize / 4; i++) {
      var ptr = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i * 4, 4));
      if (ptr == 0 || ptr >= geom.BlocksCount) continue;
      var moved = translate(ptr);
      if (moved != ptr) {
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(i * 4, 4), moved);
        own = true;
      }
      if (level > 1) changed |= PatchIndirect(image, geom, moved, level - 1, translate, writes);
    }
    if (own) writes.Add((block, buf, false));
    return changed || own;
  }

  /// <summary>
  /// The checksum of an extent-tree block (<c>ext4_extent_tail</c>): crc32c seeded
  /// with the owning inode, over the node up to its eh_max entries.
  /// </summary>
  private static void StampExtentTail(Geometry geom, uint inodeNum, uint generation, byte[] node) {
    var max = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(4, 2));
    var tailOff = 12 + 12 * max;
    if (tailOff + 4 > node.Length) return;
    var idxLe = new byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(idxLe, inodeNum);
    var genLe = new byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(genLe, generation);
    var seed = Crc32c(Crc32c(geom.CsumSeed, idxLe), genLe);
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(tailOff, 4), Crc32c(seed, node.AsSpan(0, tailOff)));
  }

  /// <summary>
  /// Repoints a group's block bitmap (field 0), inode bitmap (4) or inode table (8)
  /// after it was copied to <paramref name="newBlock" />: the descriptor field is
  /// rewritten in the primary table and every backup, each copy's checksum is
  /// recomputed, the new blocks are claimed and the vacated ones released.
  /// </summary>
  internal static void RepointGroupMetadata(Stream image, int fieldOffset, uint group, uint oldBlock, uint newBlock,
      uint blocks, Func<uint, bool> isLive) {
    var geom = ReadGeometry(image);
    var alloc = new Allocator(image, geom);
    alloc.SetDescriptorField(group, fieldOffset, newBlock);
    for (var i = 0u; i < blocks; i++) alloc.MarkUsed(newBlock + i);
    for (var i = 0u; i < blocks; i++) {
      var b = oldBlock + i;
      if (b >= newBlock && b < newBlock + blocks) continue;
      if (!isLive(b)) alloc.FreeBlock(b);
    }
    alloc.Commit();
  }

  // ── Paths and directories ─────────────────────────────────────────────────

  private static string[] SplitPath(string path)
    => path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);

  /// <summary>
  /// Walks <paramref name="folders" /> from the root as far as they exist; returns
  /// the deepest existing folder's inode and, in <paramref name="missing" />, the
  /// folders below it that do not exist yet.
  /// </summary>
  private static uint ResolveExisting(Stream image, Geometry geom, string[] folders, out string[] missing) {
    var current = RootInode;
    for (var i = 0; i < folders.Length; ++i) {
      var dir = ReadInode(image, geom, current);
      EnsureReadableDirectory(dir);
      uint found = 0;
      foreach (var b in DirectoryBlocks(image, geom, dir)) {
        if (FindEntry(ReadBlock(image, geom, b), folders[i], out _, out _, out var ino)) { found = ino; break; }
      }
      if (found == 0) { missing = folders[i..]; return current; }
      var child = ReadInode(image, geom, found);
      if ((BinaryPrimitives.ReadUInt16LittleEndian(child.AsSpan(0, 2)) & 0xF000) != InodeModeDir)
        throw new IOException($"ext: '{string.Join('/', folders[..(i + 1)])}' is not a directory.");
      current = found;
    }
    missing = [];
    return current;
  }

  private static void EnsureReadableDirectory(byte[] dir) {
    if ((BinaryPrimitives.ReadUInt16LittleEndian(dir.AsSpan(0, 2)) & 0xF000) != InodeModeDir)
      throw new IOException("ext: not a directory.");
    if ((BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(32, 4)) & 0x10000000) != 0)
      throw new InPlaceUnsupportedException("ext: the directory keeps its entries inline in the inode; in-place edit unsupported.");
  }

  /// <summary>
  /// A directory a new entry can be linked into: not hashed (an htree needs the
  /// entry filed under its name hash, which this editor does not compute).
  /// </summary>
  private static void EnsureWritableDirectory(Stream image, Geometry geom, uint dirInode) {
    var dir = ReadInode(image, geom, dirInode);
    EnsureReadableDirectory(dir);
    if ((BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(32, 4)) & 0x1000) != 0)
      throw new InPlaceUnsupportedException("ext: hashed (htree) directory; adding an entry in place is unsupported.");
    _ = DirectoryBlocks(image, geom, dir);   // refuses block maps it cannot walk
  }

  /// <summary>Every data block of a directory, in logical order.</summary>
  private static List<int> DirectoryBlocks(Stream image, Geometry geom, byte[] dir) {
    var flags = BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(32, 4));
    if ((flags & 0x80000) != 0) return ReadExtentDirBlocks(image, geom, dir);
    var size = BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(4, 4));
    var owned = new List<uint>();
    var count = (int)((size + (uint)geom.BlockSize - 1) / (uint)geom.BlockSize);
    var list = new List<int>(count);
    for (var i = 0; i < 12 && list.Count < count; i++) {
      var b = BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(40 + i * 4, 4));
      if (b == 0) break;
      list.Add((int)b);
    }
    var ind = BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(88, 4));
    if (list.Count < count && ind != 0) {
      var buf = ReadBlock(image, geom, (int)ind);
      for (var i = 0; i < geom.BlockSize / 4 && list.Count < count; i++) {
        var b = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i * 4, 4));
        if (b == 0) break;
        list.Add((int)b);
      }
    }
    if (list.Count < count)
      throw new InPlaceUnsupportedException("ext: directory maps its blocks through double indirection; in-place edit unsupported.");
    return list;
  }

  private static bool IsEmptyDirectory(Stream image, Geometry geom, byte[] dir) {
    foreach (var b in DirectoryBlocks(image, geom, dir)) {
      var block = ReadBlock(image, geom, b);
      var off = 0;
      while (off + 8 <= block.Length) {
        var ino = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(off, 4));
        var recLen = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(off + 4, 2));
        var nameLen = block[off + 6];
        if (recLen < 8) break;
        if (ino != 0) {
          var isDot = nameLen == 1 && block[off + 8] == (byte)'.';
          var isDotDot = nameLen == 2 && block[off + 8] == (byte)'.' && block[off + 9] == (byte)'.';
          if (!isDot && !isDotDot) return false;
        }
        off += recLen;
      }
    }
    return true;
  }

  /// <summary>
  /// Creates an empty folder <paramref name="name" /> in <paramref name="parentInode" />:
  /// a directory inode with one block holding "." and "..", an entry in the parent,
  /// and the parent's link count and the group's directory count raised.
  /// </summary>
  private static uint MakeDirectory(Stream image, Geometry geom, uint parentInode, string name) {
    EnsureWritableDirectory(image, geom, parentInode);
    var parent = ReadInode(image, geom, parentInode);
    var parentBlocks = DirectoryBlocks(image, geom, parent);
    var tail = geom.HasMetadataCsum ? 12 : 0;
    var entrySize = ComputeDirEntrySize(name);
    int slotBlock = -1; byte[] slotBytes = null!; var slotOffset = -1;
    foreach (var b in parentBlocks) {
      var bb = ReadBlock(image, geom, b);
      if (TrySplitLastEntryForAppend(bb, entrySize, out var off, tail)) { slotBlock = b; slotBytes = bb; slotOffset = off; break; }
    }
    if (slotBlock < 0)
      throw new InPlaceUnsupportedException("ext: no room for a new folder entry without growing the directory.");

    var alloc = new Allocator(image, geom);
    var inode = alloc.AllocateInode() ?? throw new IOException("ext: no free inodes available.");
    var blocks = alloc.AllocateBlocks(1);
    if (blocks == null) { alloc.Rollback(); throw new IOException("ext: no free block for a new folder."); }
    var block = blocks[0];
    var useExtents = geom.HasExtentsFeature;
    var area = useExtents
      ? BuildExtentMapping(image, alloc, geom, inode, blocks, out var extra)
      : BuildIndirectMapping(image, alloc, geom, blocks, out extra);

    var content = new byte[geom.BlockSize];
    WriteRev1DirEntry(content, 0, inode, ".", 2, isLast: false, blockEnd: content.Length);
    WriteRev1DirEntry(content, 12, parentInode, "..", 2, isLast: true, blockEnd: content.Length);
    WriteInode(image, geom, inode, BuildInode(geom, (ushort)(InodeModeDir | 0x1ED), (uint)geom.BlockSize, 2,
      1 + extra, useExtents ? 0x80000u : 0u, area));
    WriteDirBlock(image, geom, (int)block, content, inode, isDtreeTail: true);

    WriteRev1DirEntry(slotBytes, slotOffset, inode, name, 2, isLast: true, blockEnd: slotBytes.Length);
    WriteDirBlock(image, geom, slotBlock, slotBytes, parentInode, isDtreeTail: true);

    parent = ReadInode(image, geom, parentInode);
    var links = BinaryPrimitives.ReadUInt16LittleEndian(parent.AsSpan(26, 2));
    BinaryPrimitives.WriteUInt16LittleEndian(parent.AsSpan(26, 2), (ushort)(links + 1));
    WriteInode(image, geom, parentInode, parent);

    alloc.CountDirectory(inode, +1);
    alloc.Commit();
    if (geom.HasMetadataCsum) {
      WriteInodeChecksum(image, geom, inode);
      WriteInodeChecksum(image, geom, parentInode);
    }
    return inode;
  }

  /// <summary>A fresh inode image: mode, size, link count, block usage, flags and block map; times are now.</summary>
  private static byte[] BuildInode(Geometry geom, ushort mode, uint size, ushort links, ulong blocks, uint flags, byte[] iblockArea) {
    var inodeBytes = new byte[geom.InodeSize];
    var now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    BinaryPrimitives.WriteUInt16LittleEndian(inodeBytes.AsSpan(0, 2), mode);
    BinaryPrimitives.WriteUInt32LittleEndian(inodeBytes.AsSpan(4, 4), size);
    BinaryPrimitives.WriteUInt32LittleEndian(inodeBytes.AsSpan(8, 4), now);
    BinaryPrimitives.WriteUInt32LittleEndian(inodeBytes.AsSpan(12, 4), now);
    BinaryPrimitives.WriteUInt32LittleEndian(inodeBytes.AsSpan(16, 4), now);
    BinaryPrimitives.WriteUInt16LittleEndian(inodeBytes.AsSpan(26, 2), links);
    BinaryPrimitives.WriteUInt32LittleEndian(inodeBytes.AsSpan(28, 4), (uint)(blocks * (ulong)(geom.BlockSize / 512)));
    BinaryPrimitives.WriteUInt32LittleEndian(inodeBytes.AsSpan(32, 4), flags);
    iblockArea.CopyTo(inodeBytes.AsSpan(40, 60));
    if (geom.InodeSize > 128)
      BinaryPrimitives.WriteUInt16LittleEndian(inodeBytes.AsSpan(128, 2), 32);
    return inodeBytes;
  }

  /// <summary>
  /// Takes one link away from <paramref name="inodeNum" />: the inode survives with
  /// its remaining names, or, at the last link, it and everything it owns are freed.
  /// </summary>
  private static void DropLink(Stream image, Geometry geom, Allocator alloc, uint inodeNum, bool wipeData) {
    var inodeBytes = ReadInode(image, geom, inodeNum);
    var links = BinaryPrimitives.ReadUInt16LittleEndian(inodeBytes.AsSpan(26, 2));
    if (links > 1) {
      BinaryPrimitives.WriteUInt16LittleEndian(inodeBytes.AsSpan(26, 2), (ushort)(links - 1));
      BinaryPrimitives.WriteUInt32LittleEndian(inodeBytes.AsSpan(12, 4), (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
      WriteInode(image, geom, inodeNum, inodeBytes);
      if (geom.HasMetadataCsum) WriteInodeChecksum(image, geom, inodeNum);
      return;
    }
    FreeInodeAndBlocks(image, geom, alloc, inodeNum, inodeBytes, wipeData);
  }

  /// <summary>
  /// Releases an inode and every block it owns — data, block-map blocks and its
  /// extended-attribute block (shared ones only lose a reference).
  /// </summary>
  private static void FreeInodeAndBlocks(Stream image, Geometry geom, Allocator alloc, uint inodeNum, byte[] inodeBytes, bool wipeData) {
    var mode = BinaryPrimitives.ReadUInt16LittleEndian(inodeBytes.AsSpan(0, 2));
    var fileSize = BinaryPrimitives.ReadUInt32LittleEndian(inodeBytes.AsSpan(4, 4));
    var iBlocks = BinaryPrimitives.ReadUInt32LittleEndian(inodeBytes.AsSpan(28, 4));
    var flags = BinaryPrimitives.ReadUInt32LittleEndian(inodeBytes.AsSpan(32, 4));
    var type = mode & 0xF000;
    var fileAcl = BinaryPrimitives.ReadUInt32LittleEndian(inodeBytes.AsSpan(104, 4));

    // Only regular files, directories and symlinks whose target is not stored in
    // the inode own blocks; device numbers, fast symlink targets and inline data
    // live in i_block itself and must not be read as block pointers.
    var ownsBlocks = type is 0x8000 or InodeModeDir || (type == 0xA000 && iBlocks != 0 && fileSize >= 60);
    if ((flags & 0x10000000) != 0) ownsBlocks = false;
    var owned = new List<uint>();
    if (ownsBlocks) {
      if ((flags & 0x80000) != 0) CollectExtentBlocks(image, geom, inodeBytes, owned);
      else CollectIndirectBlocks(image, geom, inodeBytes, fileSize, owned);
    }
    foreach (var ptr in owned) {
      if (ptr < geom.FirstDataBlock || ptr >= geom.BlocksCount) continue;
      alloc.FreeBlock(ptr);
      if (wipeData) WriteBlock(image, geom, (int)ptr, new byte[geom.BlockSize]);
    }
    if (fileAcl != 0 && fileAcl >= geom.FirstDataBlock && fileAcl < geom.BlocksCount)
      ReleaseXattrBlock(image, geom, alloc, fileAcl, wipeData);

    alloc.FreeInode(inodeNum);
    WriteInode(image, geom, inodeNum, new byte[geom.InodeSize]);
    if (geom.HasMetadataCsum) WriteInodeChecksum(image, geom, inodeNum);
  }

  /// <summary>
  /// Drops one reference to an extended-attribute block; the last reference frees it.
  /// </summary>
  private static void ReleaseXattrBlock(Stream image, Geometry geom, Allocator alloc, uint block, bool wipeData) {
    var buf = ReadBlock(image, geom, (int)block);
    if (BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(0, 4)) != 0xEA020000) return;  // not an xattr block
    var refs = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(4, 4));
    if (refs > 1) {
      BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4, 4), refs - 1);
      if (geom.HasMetadataCsum) {
        // h_checksum @ 0x10 = crc32c(fs seed, le64 block number, block with the field zeroed).
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x10, 4), 0);
        var blockLe = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(blockLe, block);
        var crc = Crc32c(Crc32c(geom.CsumSeed, blockLe), buf);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x10, 4), crc);
      }
      WriteBlock(image, geom, (int)block, buf);
      return;
    }
    alloc.FreeBlock(block);
    if (wipeData) WriteBlock(image, geom, (int)block, new byte[geom.BlockSize]);
  }

  // ── Geometry / superblock ────────────────────────────────────────────────

  private static Geometry ReadGeometry(Stream image) {
    var sb = new byte[1024];
    image.Position = SuperblockOffset;
    image.ReadExactly(sb);
    var magic = BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(56, 2));
    if (magic != ExtMagic)
      throw new InvalidDataException($"ext: invalid magic 0x{magic:X4}, expected 0xEF53.");

    var inodesCount = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(0, 4));
    var blocksCount = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(4, 4));
    var firstData = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(20, 4));
    var logBlock = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(24, 4));
    var blockSize = 1024 << (int)logBlock;
    var blocksPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(32, 4));
    var inodesPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(40, 4));
    var revLevel = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(76, 4));
    var firstUserInode = revLevel >= 1 ? BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(84, 4)) : 11u;
    var inodeSize = revLevel >= 1 ? BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(88, 2)) : (ushort)128;
    if (inodeSize == 0) inodeSize = 128;
    if (firstUserInode == 0) firstUserInode = 11;
    var featureCompat = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(92, 4));
    var featureIncompat = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(96, 4));
    var featureRoCompat = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(100, 4));
    var uuid = sb.AsSpan(104, 16).ToArray();

    var descSize = 32;
    if ((featureIncompat & Incompat64Bit) != 0) {
      descSize = BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(254, 2));
      if (descSize < 32) descSize = 32;
    }

    var groupCount = (uint)(((ulong)blocksCount - firstData + blocksPerGroup - 1) / blocksPerGroup);
    var bgdtOffset = (long)(firstData + 1) * blockSize;

    // Checksum seed: s_checksum_seed @ 0x270 (624) when metadata_csum_seed feature
    // is set; otherwise crc32c(~0, uuid).
    uint csumSeed;
    if ((featureIncompat & IncompatCsumSeed) != 0)
      csumSeed = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(0x270, 4));
    else
      csumSeed = Crc32c(0xFFFFFFFFu, uuid);

    return new Geometry {
      BlockSize = blockSize,
      FirstDataBlock = firstData,
      BlocksCount = blocksCount,
      InodesCount = inodesCount,
      InodesPerGroup = inodesPerGroup,
      BlocksPerGroup = blocksPerGroup,
      FirstUserInode = firstUserInode,
      InodeSize = inodeSize,
      FeatureIncompat = featureIncompat,
      FeatureRoCompat = featureRoCompat,
      DescSize = descSize,
      GroupCount = groupCount,
      BgdtOffset = bgdtOffset,
      CsumSeed = csumSeed,
      Uuid = uuid,
    };
  }

  // ── BGD field access (folds 64-bit hi halves) ────────────────────────────

  private static byte[] ReadBgd(Stream image, Geometry g, uint group) {
    var buf = new byte[g.DescSize];
    image.Position = g.BgdOffset(group);
    image.ReadExactly(buf);
    return buf;
  }
  private static void WriteBgd(Stream image, Geometry g, uint group, byte[] buf) {
    image.Position = g.BgdOffset(group);
    image.Write(buf, 0, g.DescSize);
  }
  private static ulong BgdBlockBitmap(byte[] b, int descSize) {
    ulong lo = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(0, 4));
    if (descSize >= 64) lo |= (ulong)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(32, 4)) << 32;
    return lo;
  }
  private static ulong BgdInodeBitmap(byte[] b, int descSize) {
    ulong lo = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4, 4));
    if (descSize >= 64) lo |= (ulong)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(36, 4)) << 32;
    return lo;
  }
  private static ulong BgdInodeTable(byte[] b, int descSize) {
    ulong lo = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(8, 4));
    if (descSize >= 64) lo |= (ulong)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(40, 4)) << 32;
    return lo;
  }
  private static uint BgdFreeBlocks(byte[] b, int descSize) {
    uint lo = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(12, 2));
    if (descSize >= 64) lo |= (uint)BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(44, 2)) << 16;
    return lo;
  }
  private static void SetBgdFreeBlocks(byte[] b, int descSize, uint v) {
    BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(12, 2), (ushort)(v & 0xFFFF));
    if (descSize >= 64) BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(44, 2), (ushort)(v >> 16));
  }
  private static uint BgdFreeInodes(byte[] b, int descSize) {
    uint lo = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(14, 2));
    if (descSize >= 64) lo |= (uint)BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(46, 2)) << 16;
    return lo;
  }
  private static void SetBgdFreeInodes(byte[] b, int descSize, uint v) {
    BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(14, 2), (ushort)(v & 0xFFFF));
    if (descSize >= 64) BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(46, 2), (ushort)(v >> 16));
  }
  private static ushort BgdFlags(byte[] b) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(18, 2));
  private static void SetBgdFlags(byte[] b, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(18, 2), v);
  private static uint BgdItableUnused(byte[] b, int descSize) {
    uint lo = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(28, 2));
    if (descSize >= 64) lo |= (uint)BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(48, 2)) << 16;
    return lo;
  }
  private static void SetBgdItableUnused(byte[] b, int descSize, uint v) {
    BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(28, 2), (ushort)(v & 0xFFFF));
    if (descSize >= 64) BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(48, 2), (ushort)(v >> 16));
  }

  // ── Allocator: scans all groups, batches bitmap/desc/csum writes ──────────

  private sealed class Allocator {
    private readonly Stream _image;
    private readonly Geometry _g;
    // Per-group dirty bitmaps + desc, lazily loaded.
    private readonly Dictionary<uint, byte[]> _blockBitmaps = new();
    private readonly Dictionary<uint, byte[]> _inodeBitmaps = new();
    private readonly Dictionary<uint, byte[]> _descs = new();
    private readonly Dictionary<uint, int> _blockDelta = new();  // freeBlocks delta (negative = allocated)
    private readonly Dictionary<uint, int> _inodeDelta = new();
    private readonly Dictionary<uint, int> _dirDelta = new();
    private int _sbFreeBlocksDelta;
    private int _sbFreeInodesDelta;

    public Allocator(Stream image, Geometry g) { _image = image; _g = g; }

    private byte[] BlockBitmap(uint group) {
      if (!_blockBitmaps.TryGetValue(group, out var bm)) {
        var desc = Desc(group);
        var off = (long)BgdBlockBitmap(desc, _g.DescSize) * _g.BlockSize;
        bm = new byte[_g.BlockSize];
        _image.Position = off;
        _image.ReadExactly(bm);
        // If BLOCK_UNINIT, the on-disk bitmap is not authoritative; synthesize it
        // by marking only the group's own metadata blocks (handled by clearing the
        // flag on commit). We still read it (it's usually all-zero) and rely on
        // the flag clear; for safety, when uninit we mark group-metadata bits used.
        if ((BgdFlags(desc) & BgBlockUninit) != 0)
          InitBlockBitmapForGroup(group, bm);
        _blockBitmaps[group] = bm;
      }
      return bm;
    }
    private byte[] InodeBitmap(uint group) {
      if (!_inodeBitmaps.TryGetValue(group, out var bm)) {
        var desc = Desc(group);
        var off = (long)BgdInodeBitmap(desc, _g.DescSize) * _g.BlockSize;
        bm = new byte[_g.BlockSize];
        _image.Position = off;
        _image.ReadExactly(bm);
        if ((BgdFlags(desc) & BgInodeUninit) != 0)
          Array.Clear(bm); // uninit → all free; flag cleared on commit
        _inodeBitmaps[group] = bm;
      }
      return bm;
    }
    private byte[] Desc(uint group) {
      if (!_descs.TryGetValue(group, out var d)) {
        d = ReadBgd(_image, _g, group);
        _descs[group] = d;
      }
      return d;
    }

    // For a BLOCK_UNINIT group the kernel computes which blocks are used from the
    // layout. Group blocks: [super+gdt+reserved-gdt] only at sparse-super groups;
    // plus block bitmap, inode bitmap, inode table. With flex_bg those metadata
    // blocks may live in another group, so for the *current* group the only
    // guaranteed-used blocks are any that physically fall in this group's range.
    private void InitBlockBitmapForGroup(uint group, byte[] bm) {
      var groupStart = _g.FirstDataBlock + (ulong)group * _g.BlocksPerGroup;
      var groupBlocks = (ulong)_g.BlocksPerGroup;
      // Cap to image end for the last group.
      var maxBlocks = (ulong)_g.BlocksCount - groupStart;
      if (groupBlocks > maxBlocks) groupBlocks = maxBlocks;
      // Walk every group's descriptor and mark any metadata block that lands here.
      for (uint og = 0; og < _g.GroupCount; og++) {
        var d = Desc(og);
        MarkIfInRange(bm, groupStart, groupBlocks, BgdBlockBitmap(d, _g.DescSize), 1);
        MarkIfInRange(bm, groupStart, groupBlocks, BgdInodeBitmap(d, _g.DescSize), 1);
        var itbl = BgdInodeTable(d, _g.DescSize);
        var itblBlocks = (ulong)((_g.InodesPerGroup * (uint)_g.InodeSize + (uint)_g.BlockSize - 1) / (uint)_g.BlockSize);
        MarkIfInRange(bm, groupStart, groupBlocks, itbl, itblBlocks);
      }
      // Mark super + gdt + reserved-gdt for groups that have a backup (sparse_super:
      // groups 0,1,powers of 3,5,7). The kernel only puts these in such groups.
      if (HasSuperBackup(group)) {
        var gdtBlocks = (ulong)((_g.GroupCount * (uint)_g.DescSize + (uint)_g.BlockSize - 1) / (uint)_g.BlockSize);
        var resGdt = ReadReservedGdtBlocks();
        MarkIfInRange(bm, groupStart, groupBlocks, groupStart, 1 + gdtBlocks + resGdt);
      }
    }
    private ulong ReadReservedGdtBlocks() {
      var buf = new byte[2];
      _image.Position = SuperblockOffset + 206; // s_reserved_gdt_blocks
      _image.ReadExactly(buf);
      return BinaryPrimitives.ReadUInt16LittleEndian(buf);
    }
    private bool HasSuperBackup(uint group) {
      if (group == 0 || group == 1) return true;
      foreach (var b in new[] { 3u, 5u, 7u }) {
        var p = b;
        while (p < group) p *= b;
        if (p == group) return true;
      }
      return false;
    }
    private void MarkIfInRange(byte[] bm, ulong groupStart, ulong groupBlocks, ulong blockStart, ulong count) {
      for (ulong i = 0; i < count; i++) {
        var blk = blockStart + i;
        if (blk >= groupStart && blk < groupStart + groupBlocks) {
          var bit = (int)(blk - groupStart);
          bm[bit / 8] |= (byte)(1 << (bit % 8));
        }
      }
    }

    public uint? AllocateBlock() {
      for (uint group = 0; group < _g.GroupCount; group++) {
        var bm = BlockBitmap(group);
        var groupStart = (ulong)_g.FirstDataBlock + (ulong)group * _g.BlocksPerGroup;
        var maxInGroup = (int)Math.Min((ulong)_g.BlocksPerGroup, (ulong)_g.BlocksCount - groupStart);
        for (var bit = 0; bit < maxInGroup; bit++) {
          if ((bm[bit / 8] & (1 << (bit % 8))) != 0) continue;
          bm[bit / 8] |= (byte)(1 << (bit % 8));
          _blockDelta[group] = _blockDelta.GetValueOrDefault(group) - 1;
          _sbFreeBlocksDelta--;
          return (uint)(groupStart + (ulong)bit);
        }
      }
      return null;
    }

    /// <summary>
    /// Allocates <paramref name="count" /> blocks, as one contiguous run inside a
    /// group when there is one, else wherever free blocks are. Null when the volume
    /// has fewer free blocks than asked for.
    /// </summary>
    public List<uint>? AllocateBlocks(int count) {
      var result = new List<uint>(count);
      if (count == 0) return result;
      for (uint group = 0; group < _g.GroupCount; group++) {
        var bm = BlockBitmap(group);
        var groupStart = (ulong)_g.FirstDataBlock + (ulong)group * _g.BlocksPerGroup;
        var maxInGroup = (int)Math.Min((ulong)_g.BlocksPerGroup, (ulong)_g.BlocksCount - groupStart);
        var run = 0;
        for (var bit = 0; bit < maxInGroup; bit++) {
          if ((bm[bit / 8] & (1 << (bit % 8))) != 0) { run = 0; continue; }
          if (++run < count) continue;
          var first = bit - count + 1;
          for (var b = first; b <= bit; b++) {
            bm[b / 8] |= (byte)(1 << (b % 8));
            result.Add((uint)(groupStart + (ulong)b));
          }
          _blockDelta[group] = _blockDelta.GetValueOrDefault(group) - count;
          _sbFreeBlocksDelta -= count;
          return result;
        }
      }
      for (var i = 0; i < count; i++) {
        var b = this.AllocateBlock();
        if (b == null) return null;
        result.Add(b.Value);
      }
      return result;
    }

    /// <summary>Marks a specific block in use (a no-op when it already is).</summary>
    public void MarkUsed(uint block) {
      var group = (uint)(((ulong)block - _g.FirstDataBlock) / _g.BlocksPerGroup);
      if (block < _g.FirstDataBlock || group >= _g.GroupCount) return;
      var groupStart = (ulong)_g.FirstDataBlock + (ulong)group * _g.BlocksPerGroup;
      var bit = (int)((ulong)block - groupStart);
      var bm = BlockBitmap(group);
      if ((bm[bit / 8] & (1 << (bit % 8))) != 0) return;
      bm[bit / 8] |= (byte)(1 << (bit % 8));
      _blockDelta[group] = _blockDelta.GetValueOrDefault(group) - 1;
      _sbFreeBlocksDelta--;
    }

    private readonly List<(uint Group, int Field, uint Value)> _fieldUpdates = [];

    /// <summary>
    /// Points a group's block bitmap (0), inode bitmap (4) or inode table (8) at
    /// <paramref name="value" />, in the primary descriptor now and in every backup
    /// table on commit.
    /// </summary>
    public void SetDescriptorField(uint group, int field, uint value) {
      var desc = Desc(group);
      BinaryPrimitives.WriteUInt32LittleEndian(desc.AsSpan(field, 4), value);
      if (_g.DescSize >= 64) BinaryPrimitives.WriteUInt32LittleEndian(desc.AsSpan(0x20 + field, 4), 0);
      _fieldUpdates.Add((group, field, value));
      _descTouched.Add(group);
    }

    private readonly HashSet<uint> _descTouched = [];

    /// <summary>Records a directory created (+1) or removed (-1) in the inode's group.</summary>
    public void CountDirectory(uint inode, int delta) {
      var group = (inode - 1) / _g.InodesPerGroup;
      _dirDelta[group] = _dirDelta.GetValueOrDefault(group) + delta;
    }

    public void FreeBlock(uint block) {
      var group = (uint)(((ulong)block - _g.FirstDataBlock) / _g.BlocksPerGroup);
      if (group >= _g.GroupCount) return;
      var groupStart = (ulong)_g.FirstDataBlock + (ulong)group * _g.BlocksPerGroup;
      var bit = (int)((ulong)block - groupStart);
      var bm = BlockBitmap(group);
      if ((bm[bit / 8] & (1 << (bit % 8))) == 0) return; // already free
      bm[bit / 8] &= (byte)~(1 << (bit % 8));
      _blockDelta[group] = _blockDelta.GetValueOrDefault(group) + 1;
      _sbFreeBlocksDelta++;
    }

    public uint? AllocateInode() {
      var firstUserBit = (int)(_g.FirstUserInode - 1);
      for (uint group = 0; group < _g.GroupCount; group++) {
        var bm = InodeBitmap(group);
        var baseInode = group * _g.InodesPerGroup;
        for (var localBit = 0; localBit < (int)_g.InodesPerGroup; localBit++) {
          var globalBit = (int)baseInode + localBit;
          if (globalBit < firstUserBit) continue; // reserved
          if ((bm[localBit / 8] & (1 << (localBit % 8))) != 0) continue;
          bm[localBit / 8] |= (byte)(1 << (localBit % 8));
          _inodeDelta[group] = _inodeDelta.GetValueOrDefault(group) - 1;
          _sbFreeInodesDelta--;
          // itable_unused is recomputed from the inode bitmap on commit.
          return (uint)(globalBit + 1);
        }
      }
      return null;
    }

    public void FreeInode(uint inode) {
      var group = (inode - 1) / _g.InodesPerGroup;
      if (group >= _g.GroupCount) return;
      var localBit = (int)((inode - 1) % _g.InodesPerGroup);
      var bm = InodeBitmap(group);
      if ((bm[localBit / 8] & (1 << (localBit % 8))) == 0) return;
      bm[localBit / 8] &= (byte)~(1 << (localBit % 8));
      _inodeDelta[group] = _inodeDelta.GetValueOrDefault(group) + 1;
      _sbFreeInodesDelta++;
    }

    public void Rollback() {
      // Nothing persisted yet; just drop caches.
      _blockBitmaps.Clear(); _inodeBitmaps.Clear(); _descs.Clear();
    }

    public void Commit() {
      // 1) Write bitmaps + recompute their checksums.
      foreach (var (group, bm) in _blockBitmaps) {
        var desc = Desc(group);
        var off = (long)BgdBlockBitmap(desc, _g.DescSize) * _g.BlockSize;
        _image.Position = off;
        _image.Write(bm, 0, _g.BlockSize);
      }
      foreach (var (group, bm) in _inodeBitmaps) {
        var desc = Desc(group);
        var off = (long)BgdInodeBitmap(desc, _g.DescSize) * _g.BlockSize;
        _image.Position = off;
        _image.Write(bm, 0, _g.BlockSize);
      }

      // 2) Update descriptors: free counts, flags (clear UNINIT for touched groups),
      //    itable_unused, dir count, bitmap checksums, desc checksum.
      var allGroups = new HashSet<uint>();
      foreach (var k in _blockBitmaps.Keys) allGroups.Add(k);
      foreach (var k in _inodeBitmaps.Keys) allGroups.Add(k);
      foreach (var k in _blockDelta.Keys) allGroups.Add(k);
      foreach (var k in _inodeDelta.Keys) allGroups.Add(k);
      foreach (var k in _dirDelta.Keys) allGroups.Add(k);
      foreach (var k in _descTouched) allGroups.Add(k);

      foreach (var group in allGroups) {
        var desc = Desc(group);
        var fb = BgdFreeBlocks(desc, _g.DescSize);
        var fi = BgdFreeInodes(desc, _g.DescSize);
        SetBgdFreeBlocks(desc, _g.DescSize, (uint)((int)fb + _blockDelta.GetValueOrDefault(group)));
        SetBgdFreeInodes(desc, _g.DescSize, (uint)((int)fi + _inodeDelta.GetValueOrDefault(group)));
        if (_dirDelta.TryGetValue(group, out var dirs) && dirs != 0) {
          // bg_used_dirs_count_lo @ 0x10, _hi @ 0x30 on 64-byte descriptors.
          uint used = BinaryPrimitives.ReadUInt16LittleEndian(desc.AsSpan(0x10, 2));
          if (_g.DescSize >= 64) used |= (uint)BinaryPrimitives.ReadUInt16LittleEndian(desc.AsSpan(0x30, 2)) << 16;
          used = (uint)Math.Max(0, (long)used + dirs);
          BinaryPrimitives.WriteUInt16LittleEndian(desc.AsSpan(0x10, 2), (ushort)used);
          if (_g.DescSize >= 64) BinaryPrimitives.WriteUInt16LittleEndian(desc.AsSpan(0x30, 2), (ushort)(used >> 16));
        }

        var flags = BgdFlags(desc);
        if (_blockBitmaps.ContainsKey(group)) flags &= unchecked((ushort)~BgBlockUninit);
        if (_inodeBitmaps.ContainsKey(group)) flags &= unchecked((ushort)~BgInodeUninit);
        SetBgdFlags(desc, flags);

        // itable_unused: when uninit_bg / metadata_csum, recompute from the inode bitmap
        // as inodesPerGroup minus the count of leading used inodes (highest used index).
        if ((_g.HasGdtCsum || _g.HasMetadataCsum) && _inodeBitmaps.TryGetValue(group, out var ibm)) {
          var highestUsed = 0;
          for (var i = (int)_g.InodesPerGroup - 1; i >= 0; i--) {
            if ((ibm[i / 8] & (1 << (i % 8))) != 0) { highestUsed = i + 1; break; }
          }
          var unused = (uint)((int)_g.InodesPerGroup - highestUsed);
          var cur = BgdItableUnused(desc, _g.DescSize);
          if (unused < cur) SetBgdItableUnused(desc, _g.DescSize, unused);
        }

        // Bitmap checksums (metadata_csum only): crc32c over the full bitmap block.
        if (_g.HasMetadataCsum) {
          if (_blockBitmaps.TryGetValue(group, out var bbm))
            WriteBitmapCsumIntoDesc(desc, group, bbm, isBlock: true);
          if (_inodeBitmaps.TryGetValue(group, out var iibm))
            WriteBitmapCsumIntoDesc(desc, group, iibm, isBlock: false);
        }

        // Group descriptor checksum.
        WriteGroupDescChecksum(desc, group);
        WriteBgd(_image, _g, group, desc);
      }

      // 3) A moved bitmap or inode table is recorded in every backup descriptor table
      //    too; fsck falls back to them, so they must say the same.
      if (_fieldUpdates.Count > 0) {
        var gdtBlocks = (_g.GroupCount * (uint)_g.DescSize + (uint)_g.BlockSize - 1) / (uint)_g.BlockSize;
        for (uint backup = 1; backup < _g.GroupCount; backup++) {
          if (!HasSuperBackup(backup) && (_g.FeatureRoCompat & 0x1) != 0) continue;
          var tableStart = ((long)_g.FirstDataBlock + (long)backup * _g.BlocksPerGroup + 1) * _g.BlockSize;
          foreach (var group in _fieldUpdates.Select(u => u.Group).Distinct()) {
            var at = tableStart + (long)group * _g.DescSize;
            if (at + _g.DescSize > _image.Length || (long)group * _g.DescSize >= gdtBlocks * _g.BlockSize) continue;
            var copy = new byte[_g.DescSize];
            _image.Position = at;
            _image.ReadExactly(copy);
            foreach (var (g2, field, value) in _fieldUpdates.Where(u => u.Group == group)) {
              BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(field, 4), value);
              if (_g.DescSize >= 64) BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(0x20 + field, 4), 0);
            }
            WriteGroupDescChecksum(copy, group);
            _image.Position = at;
            _image.Write(copy, 0, _g.DescSize);
          }
        }
      }

      // 4) Superblock free counts (+ 64bit hi halves) and superblock checksum.
      ApplySuperblockDeltas();
    }

    private void WriteBitmapCsumIntoDesc(byte[] desc, uint group, byte[] bitmap, bool isBlock) {
      // crc32c over the meaningful bitmap region: inodes_per_group/8 bytes for the
      // inode bitmap, clusters(=blocks)_per_group/8 for the block bitmap. The kernel
      // (ext4_{block,inode}_bitmap_csum_set) checksums exactly that many bytes.
      int bytes = isBlock
        ? (int)((_g.BlocksPerGroup + 7) / 8)
        : (int)((_g.InodesPerGroup + 7) / 8);
      if (bytes > _g.BlockSize) bytes = _g.BlockSize;
      var csum = Crc32c(_g.CsumSeed, bitmap.AsSpan(0, bytes).ToArray());
      if (isBlock) {
        // bg_block_bitmap_csum_lo @ 0x18 (24), _hi @ 0x38 (56) if descSize>=64.
        BinaryPrimitives.WriteUInt16LittleEndian(desc.AsSpan(24, 2), (ushort)(csum & 0xFFFF));
        if (_g.DescSize >= 64) BinaryPrimitives.WriteUInt16LittleEndian(desc.AsSpan(56, 2), (ushort)(csum >> 16));
      } else {
        // bg_inode_bitmap_csum_lo @ 0x1A (26), _hi @ 0x3A (58) if descSize>=64.
        BinaryPrimitives.WriteUInt16LittleEndian(desc.AsSpan(26, 2), (ushort)(csum & 0xFFFF));
        if (_g.DescSize >= 64) BinaryPrimitives.WriteUInt16LittleEndian(desc.AsSpan(58, 2), (ushort)(csum >> 16));
      }
    }

    private void WriteGroupDescChecksum(byte[] desc, uint group) {
      if (_g.HasMetadataCsum) {
        // crc32c(seed, group_le32) then crc32c over the descriptor with the csum
        // field (offset 0x1E, 16-bit) zeroed.
        var groupLe = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(groupLe, group);
        var crc = Crc32c(_g.CsumSeed, groupLe);
        BinaryPrimitives.WriteUInt16LittleEndian(desc.AsSpan(0x1E, 2), 0);
        crc = Crc32c(crc, desc.AsSpan(0, _g.DescSize).ToArray());
        BinaryPrimitives.WriteUInt16LittleEndian(desc.AsSpan(0x1E, 2), (ushort)(crc & 0xFFFF));
      } else if (_g.HasGdtCsum) {
        // crc16 over uuid + group_le32 + descriptor[0..0x1E] + descriptor[0x20..descSize].
        ushort crc = 0xFFFF;
        crc = Crc16(crc, _g.Uuid);
        var groupLe = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(groupLe, group);
        crc = Crc16(crc, groupLe);
        crc = Crc16(crc, desc.AsSpan(0, 0x1E).ToArray());
        if (_g.DescSize > 0x20)
          crc = Crc16(crc, desc.AsSpan(0x20, _g.DescSize - 0x20).ToArray());
        BinaryPrimitives.WriteUInt16LittleEndian(desc.AsSpan(0x1E, 2), crc);
      }
    }

    private void ApplySuperblockDeltas() {
      var buf = new byte[1024];
      _image.Position = SuperblockOffset;
      _image.ReadExactly(buf);

      // s_free_blocks_count_lo @12, hi @ 0x158 (344) when 64bit.
      ulong freeBlocks = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(12, 4));
      if ((_g.FeatureIncompat & Incompat64Bit) != 0)
        freeBlocks |= (ulong)BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(0x158, 4)) << 32;
      freeBlocks = (ulong)((long)freeBlocks + _sbFreeBlocksDelta);
      BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(12, 4), (uint)(freeBlocks & 0xFFFFFFFF));
      if ((_g.FeatureIncompat & Incompat64Bit) != 0)
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x158, 4), (uint)(freeBlocks >> 32));

      uint freeInodes = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(16, 4));
      freeInodes = (uint)((int)freeInodes + _sbFreeInodesDelta);
      BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(16, 4), freeInodes);

      // Superblock checksum (metadata_csum): crc32c(~0, sb[0..0x3FC]) stored @ 0x3FC.
      if (_g.HasMetadataCsum) {
        var crc = Crc32c(0xFFFFFFFFu, buf.AsSpan(0, 0x3FC).ToArray());
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x3FC, 4), crc);
      }
      _image.Position = SuperblockOffset;
      _image.Write(buf, 0, 1024);
    }
  }

  // ── File block mapping ────────────────────────────────────────────────────

  /// <summary>
  /// Builds the ext4 extent map for the given data blocks, coalescing contiguous
  /// runs. Up to four extents live in the inode itself; more go into one leaf block
  /// the inode indexes (depth 1), which holds as many as fit a block. Returns the
  /// 60-byte i_block area.
  /// </summary>
  private static byte[] BuildExtentMapping(Stream image, Allocator alloc, Geometry geom, uint inodeNum,
      List<uint> dataBlocks, out uint extraInodeBlocks) {
    extraInodeBlocks = 0;
    var area = new byte[60];
    BinaryPrimitives.WriteUInt16LittleEndian(area.AsSpan(0, 2), ExtentMagic);
    BinaryPrimitives.WriteUInt16LittleEndian(area.AsSpan(4, 2), 4);   // eh_max (inode-resident)
    if (dataBlocks.Count == 0) return area;

    var runs = new List<(uint start, uint len)>();
    uint runStart = dataBlocks[0], runLen = 1;
    for (var i = 1; i < dataBlocks.Count; i++) {
      if (dataBlocks[i] == runStart + runLen && runLen < 32768) { runLen++; }
      else { runs.Add((runStart, runLen)); runStart = dataBlocks[i]; runLen = 1; }
    }
    runs.Add((runStart, runLen));

    static void WriteExtents(Span<byte> node, List<(uint start, uint len)> runs) {
      uint logical = 0;
      for (var i = 0; i < runs.Count; i++) {
        var (start, len) = runs[i];
        var off = 12 + i * 12;
        BinaryPrimitives.WriteUInt32LittleEndian(node.Slice(off, 4), logical);
        BinaryPrimitives.WriteUInt16LittleEndian(node.Slice(off + 4, 2), (ushort)len);
        BinaryPrimitives.WriteUInt16LittleEndian(node.Slice(off + 6, 2), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(node.Slice(off + 8, 4), start);
        logical += len;
      }
    }

    if (runs.Count <= 4) {
      BinaryPrimitives.WriteUInt16LittleEndian(area.AsSpan(2, 2), (ushort)runs.Count);
      WriteExtents(area, runs);
      return area;
    }

    var leafMax = (geom.BlockSize - 12 - (geom.HasMetadataCsum ? 4 : 0)) / 12;
    if (runs.Count > leafMax)
      throw new InPlaceUnsupportedException($"ext: file maps to {runs.Count} extents; one index level holds {leafMax}.");
    var leafBlock = alloc.AllocateBlock() ?? throw new IOException("ext: no free block for the extent leaf.");
    extraInodeBlocks = 1;

    var leaf = new byte[geom.BlockSize];
    BinaryPrimitives.WriteUInt16LittleEndian(leaf.AsSpan(0, 2), ExtentMagic);
    BinaryPrimitives.WriteUInt16LittleEndian(leaf.AsSpan(2, 2), (ushort)runs.Count);
    BinaryPrimitives.WriteUInt16LittleEndian(leaf.AsSpan(4, 2), (ushort)leafMax);
    BinaryPrimitives.WriteUInt16LittleEndian(leaf.AsSpan(6, 2), 0);
    WriteExtents(leaf, runs);
    if (geom.HasMetadataCsum) {
      // ext4_extent_tail after eh_max entries: crc32c(inode seed, node up to the tail).
      var idxLe = new byte[4];
      BinaryPrimitives.WriteUInt32LittleEndian(idxLe, inodeNum);
      var seed = Crc32c(Crc32c(geom.CsumSeed, idxLe), new byte[4]);   // generation 0
      var tailOff = 12 + 12 * leafMax;
      BinaryPrimitives.WriteUInt32LittleEndian(leaf.AsSpan(tailOff, 4), Crc32c(seed, leaf.AsSpan(0, tailOff)));
    }
    WriteBlock(image, geom, (int)leafBlock, leaf);

    // The inode holds one index entry pointing at the leaf.
    BinaryPrimitives.WriteUInt16LittleEndian(area.AsSpan(2, 2), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(area.AsSpan(6, 2), 1);   // eh_depth = 1
    BinaryPrimitives.WriteUInt32LittleEndian(area.AsSpan(12, 4), 0);  // ei_block
    BinaryPrimitives.WriteUInt32LittleEndian(area.AsSpan(16, 4), leafBlock); // ei_leaf_lo
    BinaryPrimitives.WriteUInt16LittleEndian(area.AsSpan(20, 2), 0);  // ei_leaf_hi
    return area;
  }

  /// <summary>
  /// Builds a classic direct + indirect + double-indirect + triple-indirect block
  /// map for the given data blocks. Allocates indirect metadata blocks from the
  /// same allocator (charged to extraInodeBlocks) and writes them to the image.
  /// Returns the 60-byte i_block area.
  /// </summary>
  private static byte[] BuildIndirectMapping(Stream image, Allocator alloc, Geometry geom, List<uint> dataBlocks, out uint extraInodeBlocks) {
    extraInodeBlocks = 0;
    var area = new byte[60];
    var ptrsPerBlock = geom.BlockSize / 4;
    var n = dataBlocks.Count;

    // Direct 0..11
    var direct = Math.Min(12, n);
    for (var i = 0; i < direct; i++)
      BinaryPrimitives.WriteUInt32LittleEndian(area.AsSpan(i * 4, 4), dataBlocks[i]);
    var idx = direct;

    // Single indirect (i_block[12] @ offset 48 in the area).
    if (idx < n) {
      var ind = alloc.AllocateBlock() ?? throw new IOException("ext: no free block for single-indirect.");
      extraInodeBlocks++;
      var indBuf = new byte[geom.BlockSize];
      var c = 0;
      for (; idx < n && c < ptrsPerBlock; idx++, c++)
        BinaryPrimitives.WriteUInt32LittleEndian(indBuf.AsSpan(c * 4, 4), dataBlocks[idx]);
      WriteBlock(image, geom, (int)ind, indBuf);
      BinaryPrimitives.WriteUInt32LittleEndian(area.AsSpan(48, 4), ind);
    }

    // Double indirect (i_block[13] @ offset 52).
    if (idx < n) {
      var dind = alloc.AllocateBlock() ?? throw new IOException("ext: no free block for double-indirect.");
      extraInodeBlocks++;
      var dindBuf = new byte[geom.BlockSize];
      var dc = 0;
      while (idx < n && dc < ptrsPerBlock) {
        var ind = alloc.AllocateBlock() ?? throw new IOException("ext: no free block for double-indirect leaf.");
        extraInodeBlocks++;
        var indBuf = new byte[geom.BlockSize];
        var c = 0;
        for (; idx < n && c < ptrsPerBlock; idx++, c++)
          BinaryPrimitives.WriteUInt32LittleEndian(indBuf.AsSpan(c * 4, 4), dataBlocks[idx]);
        WriteBlock(image, geom, (int)ind, indBuf);
        BinaryPrimitives.WriteUInt32LittleEndian(dindBuf.AsSpan(dc * 4, 4), ind);
        dc++;
      }
      WriteBlock(image, geom, (int)dind, dindBuf);
      BinaryPrimitives.WriteUInt32LittleEndian(area.AsSpan(52, 4), dind);
    }

    // Triple indirect (i_block[14] @ offset 56).
    if (idx < n) {
      var tind = alloc.AllocateBlock() ?? throw new IOException("ext: no free block for triple-indirect.");
      extraInodeBlocks++;
      var tindBuf = new byte[geom.BlockSize];
      var tc = 0;
      while (idx < n && tc < ptrsPerBlock) {
        var dind = alloc.AllocateBlock() ?? throw new IOException("ext: no free block for triple-indirect L2.");
        extraInodeBlocks++;
        var dindBuf = new byte[geom.BlockSize];
        var dc = 0;
        while (idx < n && dc < ptrsPerBlock) {
          var ind = alloc.AllocateBlock() ?? throw new IOException("ext: no free block for triple-indirect leaf.");
          extraInodeBlocks++;
          var indBuf = new byte[geom.BlockSize];
          var c = 0;
          for (; idx < n && c < ptrsPerBlock; idx++, c++)
            BinaryPrimitives.WriteUInt32LittleEndian(indBuf.AsSpan(c * 4, 4), dataBlocks[idx]);
          WriteBlock(image, geom, (int)ind, indBuf);
          BinaryPrimitives.WriteUInt32LittleEndian(dindBuf.AsSpan(dc * 4, 4), ind);
          dc++;
        }
        WriteBlock(image, geom, (int)dind, dindBuf);
        BinaryPrimitives.WriteUInt32LittleEndian(tindBuf.AsSpan(tc * 4, 4), dind);
        tc++;
      }
      WriteBlock(image, geom, (int)tind, tindBuf);
      BinaryPrimitives.WriteUInt32LittleEndian(area.AsSpan(56, 4), tind);
    }

    if (idx < n)
      throw new InPlaceUnsupportedException("ext: file exceeds triple-indirect capacity.");
    return area;
  }

  private static void CollectIndirectBlocks(Stream image, Geometry geom, byte[] inode, uint size, List<uint> owned) {
    var ptrsPerBlock = geom.BlockSize / 4;
    for (var i = 0; i < 12; i++) {
      var b = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(40 + i * 4, 4));
      if (b != 0) owned.Add(b);
    }
    var ind = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(88, 4));
    if (ind != 0) { owned.Add(ind); CollectIndirectLevel(image, geom, ind, 1, owned); }
    var dind = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(92, 4));
    if (dind != 0) { owned.Add(dind); CollectIndirectLevel(image, geom, dind, 2, owned); }
    var tind = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(96, 4));
    if (tind != 0) { owned.Add(tind); CollectIndirectLevel(image, geom, tind, 3, owned); }
  }
  private static void CollectIndirectLevel(Stream image, Geometry geom, uint blockNum, int level, List<uint> owned) {
    var buf = ReadBlock(image, geom, (int)blockNum);
    var ptrsPerBlock = geom.BlockSize / 4;
    for (var i = 0; i < ptrsPerBlock; i++) {
      var ptr = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i * 4, 4));
      if (ptr == 0) continue;
      owned.Add(ptr);
      if (level > 1) CollectIndirectLevel(image, geom, ptr, level - 1, owned);
    }
  }

  private static void CollectExtentBlocks(Stream image, Geometry geom, byte[] inode, List<uint> owned) {
    CollectExtentNode(image, geom, inode.AsSpan(40, 60).ToArray(), owned);
  }
  private static void CollectExtentNode(Stream image, Geometry geom, byte[] node, List<uint> owned) {
    if (BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(0, 2)) != ExtentMagic) return;
    var entries = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(2, 2));
    var depth = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(6, 2));
    for (var i = 0; i < entries; i++) {
      var off = 12 + i * 12;
      if (off + 12 > node.Length) break;
      if (depth == 0) {
        var len = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(off + 4, 2)) & 0x7FFF;
        var lo = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(off + 8, 4));
        for (var b = 0; b < len; b++) owned.Add(lo + (uint)b);
      } else {
        var leafLo = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(off + 4, 4));
        owned.Add(leafLo);
        var child = ReadBlock(image, geom, (int)leafLo);
        CollectExtentNode(image, geom, child, owned);
      }
    }
  }

  // ── Inode IO ─────────────────────────────────────────────────────────────

  private static long InodeByteOffset(Stream image, Geometry geom, uint inodeNum) {
    var group = (inodeNum - 1) / geom.InodesPerGroup;
    var index = (inodeNum - 1) % geom.InodesPerGroup;
    var desc = ReadBgd(image, geom, group);
    var tableBlock = BgdInodeTable(desc, geom.DescSize);
    return (long)tableBlock * geom.BlockSize + (long)index * geom.InodeSize;
  }
  private static byte[] ReadInode(Stream image, Geometry geom, uint inodeNum) {
    if (inodeNum == 0) throw new ArgumentOutOfRangeException(nameof(inodeNum));
    var buf = new byte[geom.InodeSize];
    image.Position = InodeByteOffset(image, geom, inodeNum);
    image.ReadExactly(buf);
    return buf;
  }
  private static void WriteInode(Stream image, Geometry geom, uint inodeNum, ReadOnlySpan<byte> data) {
    image.Position = InodeByteOffset(image, geom, inodeNum);
    image.Write(data);
  }

  /// <summary>
  /// Returns the physical data blocks of an extent-mapped directory inode in
  /// logical order. Only inode-resident leaf extents (depth 0) are walked; a
  /// deeper extent tree routes to the rebuild fallback.
  /// </summary>
  private static List<int> ReadExtentDirBlocks(Stream image, Geometry geom, byte[] inode) {
    var node = inode.AsSpan(40, 60).ToArray();
    if (BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(0, 2)) != ExtentMagic)
      throw new InPlaceUnsupportedException("ext: directory has no valid extent header.");
    var depth = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(6, 2));
    if (depth != 0)
      throw new InPlaceUnsupportedException("ext: multi-level extent directory; in-place add unsupported.");
    var entries = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(2, 2));
    var list = new List<int>();
    for (var i = 0; i < entries; i++) {
      var off = 12 + i * 12;
      var len = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(off + 4, 2)) & 0x7FFF;
      var lo = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(off + 8, 4));
      for (var b = 0; b < len; b++) list.Add((int)(lo + (uint)b));
    }
    return list;
  }

  /// <summary>
  /// Whether an inode-resident extent-mapped directory can take <paramref name="newBlock" />:
  /// either it extends the last extent or a leaf slot is free.
  /// </summary>
  private static bool CanGrowExtentDirectory(byte[] inode, uint newBlock) {
    var entries = BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(42, 2));
    var max = BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(44, 2));
    if (entries > 0) {
      var off = 40 + 12 + (entries - 1) * 12;
      var eeLen = BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(off + 4, 2)) & 0x7FFF;
      var eeStart = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(off + 8, 4));
      if (eeStart + (uint)eeLen == newBlock && eeLen < 32768) return true;
    }
    return entries < max;
  }

  /// <summary>
  /// Appends one block to an inode-resident extent-mapped directory: extends the
  /// last extent if the new block is contiguous, otherwise adds a new extent. Bumps
  /// i_size + i_blocks and rewrites the inode.
  /// </summary>
  private static void GrowExtentDirectory(Stream image, Geometry geom, uint dirInodeNum, byte[] inode, uint newBlock) {
    var entries = BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(42, 2));
    var lastLogical = 0u;
    var appended = false;
    if (entries > 0) {
      var off = 40 + 12 + (entries - 1) * 12;
      var eeBlock = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(off, 4));
      var eeLen = BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(off + 4, 2)) & 0x7FFF;
      var eeStart = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(off + 8, 4));
      lastLogical = eeBlock + (uint)eeLen;
      if (eeStart + (uint)eeLen == newBlock && eeLen < 32768) {
        BinaryPrimitives.WriteUInt16LittleEndian(inode.AsSpan(off + 4, 2), (ushort)(eeLen + 1));
        appended = true;
      }
    }
    if (!appended) {
      var off = 40 + 12 + entries * 12;
      BinaryPrimitives.WriteUInt32LittleEndian(inode.AsSpan(off, 4), lastLogical);
      BinaryPrimitives.WriteUInt16LittleEndian(inode.AsSpan(off + 4, 2), 1);
      BinaryPrimitives.WriteUInt16LittleEndian(inode.AsSpan(off + 6, 2), 0);
      BinaryPrimitives.WriteUInt32LittleEndian(inode.AsSpan(off + 8, 4), newBlock);
      BinaryPrimitives.WriteUInt16LittleEndian(inode.AsSpan(42, 2), (ushort)(entries + 1));
    }
    BumpDirectorySize(geom, inode);
    WriteInode(image, geom, dirInodeNum, inode);
  }

  private static void GrowDirectDirectory(Stream image, Geometry geom, uint dirInodeNum, byte[] inode, List<int> existingBlocks, uint newBlock) {
    var slot = existingBlocks.Count;
    if (slot >= 12) throw new InPlaceUnsupportedException("ext: directory growth through indirect blocks is unsupported.");
    BinaryPrimitives.WriteUInt32LittleEndian(inode.AsSpan(40 + slot * 4, 4), newBlock);
    BumpDirectorySize(geom, inode);
    WriteInode(image, geom, dirInodeNum, inode);
  }

  private static void BumpDirectorySize(Geometry geom, byte[] inode) {
    var size = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(4, 4));
    BinaryPrimitives.WriteUInt32LittleEndian(inode.AsSpan(4, 4), size + (uint)geom.BlockSize);
    var sectors = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(28, 4));
    BinaryPrimitives.WriteUInt32LittleEndian(inode.AsSpan(28, 4), sectors + (uint)(geom.BlockSize / 512));
  }

  // ── Inode checksum (metadata_csum) ────────────────────────────────────────

  private static void WriteInodeChecksum(Stream image, Geometry geom, uint inodeNum) {
    var inode = ReadInode(image, geom, inodeNum);
    // Per-inode seed: crc32c(fs_seed, inode_index_le32) then crc32c(., gen_le32).
    var idxLe = new byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(idxLe, inodeNum);
    var crc = Crc32c(geom.CsumSeed, idxLe);
    var gen = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(100, 4)); // i_generation
    var genLe = new byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(genLe, gen);
    crc = Crc32c(crc, genLe);

    // Zero the csum fields: l_i_checksum_lo @ 0x7C (124, in osd2, 16-bit) and
    // i_checksum_hi @ 0x82 (130) when inode_size>128 and i_extra_isize covers it.
    var loSaved = BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(0x7C, 2));
    BinaryPrimitives.WriteUInt16LittleEndian(inode.AsSpan(0x7C, 2), 0);
    var hasHi = geom.InodeSize > 128 &&
                BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(128, 2)) >= 4; // extra_isize covers offset 0x82
    ushort hiSaved = 0;
    if (hasHi) {
      hiSaved = BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(0x82, 2));
      BinaryPrimitives.WriteUInt16LittleEndian(inode.AsSpan(0x82, 2), 0);
    }

    crc = Crc32c(crc, inode);

    BinaryPrimitives.WriteUInt16LittleEndian(inode.AsSpan(0x7C, 2), (ushort)(crc & 0xFFFF));
    if (hasHi)
      BinaryPrimitives.WriteUInt16LittleEndian(inode.AsSpan(0x82, 2), (ushort)(crc >> 16));
    WriteInode(image, geom, inodeNum, inode);
  }

  // ── Directory block IO with tail-checksum support (metadata_csum) ─────────

  private static byte[] ReadBlock(Stream image, Geometry geom, int blockNum) {
    var buf = new byte[geom.BlockSize];
    image.Position = (long)blockNum * geom.BlockSize;
    image.ReadExactly(buf);
    return buf;
  }
  private static void WriteBlock(Stream image, Geometry geom, int blockNum, ReadOnlySpan<byte> data) {
    image.Position = (long)blockNum * geom.BlockSize;
    image.Write(data);
  }

  /// <summary>
  /// Writes a directory data block. When metadata_csum is set, linear directory
  /// blocks carry a 12-byte dir_entry_tail (fake entry, inode=0, rec_len=12,
  /// name_len=0, file_type=0xDE) holding a crc32c of the block. We must preserve
  /// or (re)build that tail and its checksum.
  /// </summary>
  private static void WriteDirBlock(Stream image, Geometry geom, int blockNum, byte[] block, uint dirInode, bool isDtreeTail) {
    if (geom.HasMetadataCsum)
      StampDirTailChecksum(image, geom, block, dirInode);
    WriteBlock(image, geom, blockNum, block);
  }

  private static void StampDirTailChecksum(Stream image, Geometry geom, byte[] block, uint dirInode) {
    // Find / create the tail entry: last 12 bytes are the dir_entry_tail iff a
    // record chain lands exactly at blockSize-12 with rec_len=12.
    var tailOff = geom.BlockSize - 12;
    // Ensure the dirent chain ends at tailOff: walk and pad the last real record
    // so its rec_len stops at tailOff, then place the tail.
    var off = 0; var lastOff = -1;
    while (off + 8 <= tailOff) {
      var recLen = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(off + 4, 2));
      if (recLen == 0) break;
      lastOff = off;
      if (off + recLen >= tailOff) break;
      off += recLen;
    }
    if (lastOff >= 0)
      BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(lastOff + 4, 2), (ushort)(tailOff - lastOff));

    // Write the tail entry skeleton.
    BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(tailOff, 4), 0);   // inode = 0
    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(tailOff + 4, 2), 12); // rec_len = 12
    block[tailOff + 6] = 0;       // name_len = 0
    block[tailOff + 7] = 0xDE;    // file_type = EXT4_FT_DIR_CSUM marker
    // Checksum: crc32c(crc32c(crc32c(fs_seed, ino_le32), gen_le32), block[0 .. blockSize-12])
    // i.e. the meaningful directory data up to (but excluding) the 12-byte tail entry.
    var idxLe = new byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(idxLe, dirInode);
    var crc = Crc32c(geom.CsumSeed, idxLe);
    var dirInodeBytes = ReadInode(image, geom, dirInode);
    var genLe = new byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(genLe, BinaryPrimitives.ReadUInt32LittleEndian(dirInodeBytes.AsSpan(100, 4)));
    crc = Crc32c(crc, genLe);
    crc = Crc32c(crc, block.AsSpan(0, geom.BlockSize - 12).ToArray());
    BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(geom.BlockSize - 4, 4), crc);
  }

  // ── Directory entry helpers ───────────────────────────────────────────────

  private static bool FindEntry(byte[] dirData, string name, out int entryOffset, out int prevOffset, out uint inodeNum) {
    entryOffset = -1; prevOffset = -1; inodeNum = 0;
    var nameBytes = Encoding.UTF8.GetBytes(name);
    var off = 0; var prev = -1;
    while (off + 8 <= dirData.Length) {
      var ino = BinaryPrimitives.ReadUInt32LittleEndian(dirData.AsSpan(off, 4));
      var recLen = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(off + 4, 2));
      var nameLen = dirData[off + 6];
      if (recLen == 0 || off + recLen > dirData.Length) return false;
      if (ino != 0 && nameLen == nameBytes.Length &&
          dirData.AsSpan(off + 8, nameLen).SequenceEqual(nameBytes)) {
        entryOffset = off; prevOffset = prev; inodeNum = ino; return true;
      }
      prev = off; off += recLen;
    }
    return false;
  }

  private static int ComputeDirEntrySize(string name) {
    var nameBytes = Encoding.UTF8.GetByteCount(name);
    return (8 + nameBytes + 3) & ~3;
  }

  /// <summary>
  /// Tries to shrink the last in-use dirent's rec_len so trailing slack (at
  /// least newEntrySize) opens up. Accounts for a metadata_csum dir tail (the
  /// last 12 bytes are reserved). Returns the append offset.
  /// </summary>
  private static bool TrySplitLastEntryForAppend(byte[] dirData, int newEntrySize, out int appendOffset, int tailReserved = 0) {
    appendOffset = -1;
    var limit = dirData.Length;
    var off = 0; var lastOff = -1;
    while (off + 8 <= limit) {
      var recLen = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(off + 4, 2));
      var ino = BinaryPrimitives.ReadUInt32LittleEndian(dirData.AsSpan(off, 4));
      if (recLen == 0 || off + recLen > limit) return false;
      // A dir_entry_tail has inode==0 && nameLen==0 at the very end; treat as tail boundary.
      var nameLen = dirData[off + 6];
      if (ino == 0 && nameLen == 0 && recLen == 12 && off + 12 == dirData.Length) break;
      lastOff = off;
      off += recLen;
      if (off >= limit) break;
    }
    if (lastOff < 0) {
      // Empty block (freshly grown). The whole block (minus any csum tail) is available.
      if (newEntrySize > dirData.Length - tailReserved) return false;
      appendOffset = 0;
      return true;
    }

    var lastNameLen = dirData[lastOff + 6];
    var lastMin = (8 + lastNameLen + 3) & ~3;
    var lastRecLen = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(lastOff + 4, 2));
    // The last real record may currently extend over the tail-reserved region;
    // cap the usable slack so the new entry never lands inside the csum tail.
    var usableEnd = Math.Min(lastOff + lastRecLen, dirData.Length - tailReserved);
    var slack = usableEnd - (lastOff + lastMin);
    if (slack < newEntrySize) return false;
    BinaryPrimitives.WriteUInt16LittleEndian(dirData.AsSpan(lastOff + 4, 2), (ushort)lastMin);
    appendOffset = lastOff + lastMin;
    return true;
  }

  private static void SpliceOutDirEntry(byte[] dirData, int entryOffset, int prevOffset) {
    var thisRecLen = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(entryOffset + 4, 2));
    if (prevOffset >= 0) {
      var prevRecLen = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(prevOffset + 4, 2));
      var combined = prevRecLen + thisRecLen;
      if (combined > ushort.MaxValue) combined = ushort.MaxValue;
      BinaryPrimitives.WriteUInt16LittleEndian(dirData.AsSpan(prevOffset + 4, 2), (ushort)combined);
      Array.Clear(dirData, entryOffset, thisRecLen);
    } else {
      BinaryPrimitives.WriteUInt32LittleEndian(dirData.AsSpan(entryOffset, 4), 0);
      Array.Clear(dirData, entryOffset + 6, thisRecLen - 6);
    }
  }

  private static void WriteRev1DirEntry(byte[] dirData, int pos, uint inode, string name, byte fileType, bool isLast, int blockEnd) {
    var nameBytes = Encoding.UTF8.GetBytes(name);
    var entrySize = (8 + nameBytes.Length + 3) & ~3;
    var recLen = isLast ? blockEnd - pos : entrySize;
    if (recLen < entrySize)
      throw new IOException("ext: not enough room for new dirent.");
    BinaryPrimitives.WriteUInt32LittleEndian(dirData.AsSpan(pos, 4), inode);
    BinaryPrimitives.WriteUInt16LittleEndian(dirData.AsSpan(pos + 4, 2), (ushort)recLen);
    dirData[pos + 6] = (byte)nameBytes.Length;
    dirData[pos + 7] = fileType;
    nameBytes.CopyTo(dirData, pos + 8);
    for (var i = pos + 8 + nameBytes.Length; i < pos + entrySize && i < dirData.Length; ++i)
      dirData[i] = 0;
  }

  // ── CRC helpers ───────────────────────────────────────────────────────────

  /// <summary>crc32c (Castagnoli, reflected) with the given seed, NO final inversion — the ext4 convention.</summary>
  private static uint Crc32c(uint seed, byte[] data) {
    const uint poly = 0x82F63B78u;
    var crc = seed;
    foreach (var b in data) {
      crc ^= b;
      for (var i = 0; i < 8; i++)
        crc = (crc & 1) != 0 ? (crc >> 1) ^ poly : (crc >> 1);
    }
    return crc;
  }
  private static uint Crc32c(uint seed, ReadOnlySpan<byte> data) => Crc32c(seed, data.ToArray());

  /// <summary>crc16 (the ext gdt_csum variant) seeded with the running value.</summary>
  private static ushort Crc16(ushort crc, byte[] data) {
    foreach (var b in data) {
      crc ^= b;
      for (var i = 0; i < 8; i++)
        crc = (ushort)((crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : (crc >> 1));
    }
    return crc;
  }
}
