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
///   <item><b>Bigger directories</b> — when the root directory's blocks are full a
///   new linear directory block is appended (i_size and the dir's block map grow).
///   htree (EXT4_INDEX) directories are detected and routed to the rebuild fallback.</item>
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

  // ── Rebuild-style API (atomic batch mutate via read-then-rebuild) ──────────

  public static void Mutate(
      Stream archive,
      IReadOnlyList<(string Name, byte[] Data)> replacements,
      IReadOnlyCollection<string> deletions) {
    archive.Position = 0;
    var reader = new ExtReader(archive);

    var delSet = new HashSet<string>(deletions, StringComparer.Ordinal);
    var replaceMap = replacements.ToDictionary(r => r.Name, r => r.Data, StringComparer.Ordinal);

    var final = new List<(string Name, byte[] Data)>();
    foreach (var entry in reader.Entries) {
      if (entry.IsDirectory) continue;
      if (delSet.Contains(entry.Name)) continue;
      if (replaceMap.TryGetValue(entry.Name, out var newData)) {
        final.Add((entry.Name, newData));
        replaceMap.Remove(entry.Name);
      } else {
        final.Add((entry.Name, reader.Extract(entry)));
      }
    }
    foreach (var (name, data) in replaceMap)
      final.Add((name, data));

    var w = new ExtWriter();
    foreach (var (name, data) in final)
      w.AddFile(name, data);
    var rebuilt = w.Build();
    archive.Position = 0;
    archive.Write(rebuilt);
    archive.SetLength(rebuilt.Length);
  }

  // ── In-flight API ───────────────────────────────────────────────────────

  /// <summary>
  /// Thrown when a case genuinely cannot be handled in place (e.g. htree
  /// directory growth, or a nested target path). Callers may fall back to a
  /// rebuild on this.
  /// </summary>
  public sealed class InPlaceUnsupportedException(string message) : IOException(message);

  /// <summary>
  /// Adds (or fails if an entry of the same name already exists) a file inside an
  /// existing ext2/3/4 image, genuinely in place.
  /// </summary>
  public static void AddFile(Stream image, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    if (string.IsNullOrEmpty(name)) throw new ArgumentException("name is empty", nameof(name));
    if (name.Contains('/') || name.Contains('\\'))
      throw new InPlaceUnsupportedException($"ext in-place add only targets the root directory; '{name}' is nested.");

    var geom = ReadGeometry(image);

    // Locate root dir and ensure name is unique; find a slot (possibly grow dir).
    var rootInode = ReadInode(image, geom, RootInode);
    var rootFlags = BinaryPrimitives.ReadUInt32LittleEndian(rootInode.AsSpan(32, 4));
    if ((rootFlags & 0x1000) != 0)
      throw new InPlaceUnsupportedException("ext: htree (EXT4_INDEX) root directory; in-place add unsupported.");
    var dirUsesExtents = (rootFlags & 0x80000) != 0;
    var rootBlocks = dirUsesExtents ? ReadExtentDirBlocks(image, geom, rootInode) : ReadInodeDirectBlockList(rootInode);
    if (rootBlocks.Count == 0)
      throw new IOException("ext: root directory has no data block.");

    // Uniqueness check across all root dir blocks.
    foreach (var b in rootBlocks) {
      var bb = ReadBlock(image, geom, b);
      if (FindEntry(bb, name, out _, out _, out _))
        throw new IOException($"ext: entry '{name}' already exists; remove it first to replace.");
    }

    var blocksNeeded = data.Length == 0 ? 0 : (data.Length + geom.BlockSize - 1) / geom.BlockSize;

    // Decide mapping mode for the new inode.
    var useExtents = geom.HasExtentsFeature;
    // Inode-resident extent leaf holds 4 extents (after 12-byte header in the 60-byte
    // i_block area). Each extent maps up to 32768 blocks, so one leaf covers huge files.
    // We only allocate contiguous-or-fragmented runs; if extents can't describe the
    // layout within the inode (>4 fragments) we fall back to a deeper structure or rebuild.

    // ── Allocate inode + data + metadata blocks across groups ──
    var alloc = new Allocator(image, geom);
    var newInodeNum = alloc.AllocateInode()
      ?? throw new IOException("ext: no free inodes available.");

    var dataBlocks = new List<uint>(blocksNeeded);
    try {
      for (var i = 0; i < blocksNeeded; ++i)
        dataBlocks.Add(alloc.AllocateBlock() ?? throw new IOException("ext: not enough free blocks for file."));
    } catch {
      alloc.Rollback();
      throw;
    }

    // Build the block map (extents or indirect) and any needed metadata blocks.
    byte[] iblockArea;            // 60 bytes for inode i_block[0..14]
    uint extraInodeBlocks;        // metadata blocks (indirect / extent-index) charged to i_blocks
    try {
      if (useExtents)
        iblockArea = BuildExtentMapping(alloc, geom, dataBlocks, out extraInodeBlocks);
      else
        iblockArea = BuildIndirectMapping(image, alloc, geom, dataBlocks, out extraInodeBlocks);
    } catch {
      alloc.Rollback();
      throw;
    }

    // ── Find / grow a directory slot for the new entry ──
    // When metadata_csum is set, linear dir blocks reserve the last 12 bytes for a
    // dir_entry_tail; the usable region for records is [0 .. blockSize-12).
    var dirTailReserved = geom.HasMetadataCsum ? 12 : 0;
    var newEntrySize = ComputeDirEntrySize(name);
    int targetDirBlock; byte[] targetDirBytes; int insertOffset; bool grewDir = false;
    uint dirExtraBlock = 0;
    {
      targetDirBlock = -1; targetDirBytes = null!; insertOffset = -1;
      foreach (var b in rootBlocks) {
        var bb = ReadBlock(image, geom, b);
        if (TrySplitLastEntryForAppend(bb, newEntrySize, out var off, dirTailReserved)) {
          targetDirBlock = b; targetDirBytes = bb; insertOffset = off; break;
        }
      }
      if (targetDirBlock < 0) {
        // Grow: append a new directory block (linear).
        var maxDirBlocks = dirUsesExtents ? 32768 : MaxDirectBlocks; // extent leaf maps up to 32768 blocks
        if (rootBlocks.Count >= maxDirBlocks) {
          alloc.Rollback();
          throw new InPlaceUnsupportedException("ext: root directory needs deeper structure to grow; in-place add unsupported.");
        }
        var nb = alloc.AllocateBlock();
        if (nb == null) { alloc.Rollback(); throw new IOException("ext: no free block to grow root directory."); }
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
    var inodeBytes = new byte[geom.InodeSize];
    var now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var sectorsPerBlock = (ulong)(geom.BlockSize / 512);
    BinaryPrimitives.WriteUInt16LittleEndian(inodeBytes.AsSpan(0, 2), DefaultMode);
    BinaryPrimitives.WriteUInt32LittleEndian(inodeBytes.AsSpan(4, 4), (uint)data.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(inodeBytes.AsSpan(8, 4), now);
    BinaryPrimitives.WriteUInt32LittleEndian(inodeBytes.AsSpan(12, 4), now);
    BinaryPrimitives.WriteUInt32LittleEndian(inodeBytes.AsSpan(16, 4), now);
    BinaryPrimitives.WriteUInt16LittleEndian(inodeBytes.AsSpan(26, 2), 1); // links
    var totalSectors = (ulong)(dataBlocks.Count + (int)extraInodeBlocks) * sectorsPerBlock;
    BinaryPrimitives.WriteUInt32LittleEndian(inodeBytes.AsSpan(28, 4), (uint)totalSectors); // i_blocks_lo
    uint flags = 0;
    if (useExtents) flags |= 0x80000; // EXTENTS_FL
    BinaryPrimitives.WriteUInt32LittleEndian(inodeBytes.AsSpan(32, 4), flags);
    // i_block area (60 bytes at offset 40).
    iblockArea.CopyTo(inodeBytes.AsSpan(40, 60));
    // extra_isize for 256-byte inodes (i_extra_isize @ 128).
    if (geom.InodeSize > 128)
      BinaryPrimitives.WriteUInt16LittleEndian(inodeBytes.AsSpan(128, 2), 32);
    WriteInode(image, geom, newInodeNum, inodeBytes);

    // ── Splice the new dirent into the directory block ──
    WriteRev1DirEntry(targetDirBytes, insertOffset, newInodeNum, name, FileTypeRegular,
      isLast: true, blockEnd: targetDirBytes.Length);
    WriteDirBlock(image, geom, targetDirBlock, targetDirBytes, RootInode, isDtreeTail: true);

    // If we grew the directory, append the new block to the root inode's map and bump i_size.
    if (grewDir) {
      if (dirUsesExtents)
        GrowExtentDirectory(image, geom, rootInode, rootBlocks, dirExtraBlock);
      else
        GrowRootDirectory(image, geom, rootInode, rootBlocks, dirExtraBlock);
    }

    // ── Persist allocator state (bitmaps + counts + checksums) ──
    alloc.Commit();

    // ── Recompute inode checksum for the new inode (metadata_csum). ──
    if (geom.HasMetadataCsum) {
      WriteInodeChecksum(image, geom, newInodeNum);
      // Root inode may have changed (grow); refresh its checksum too.
      WriteInodeChecksum(image, geom, RootInode);
    }
  }

  /// <summary>
  /// Removes the named entry from an existing ext image, in place. Returns false
  /// if no entry with that name exists in the root directory.
  /// </summary>
  public static bool RemoveFile(Stream image, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);

    var geom = ReadGeometry(image);
    var rootInode = ReadInode(image, geom, RootInode);
    var rootFlags = BinaryPrimitives.ReadUInt32LittleEndian(rootInode.AsSpan(32, 4));
    if ((rootFlags & 0x1000) != 0) return false; // htree dir: unsupported in-place remove
    var rootBlocks = (rootFlags & 0x80000) != 0
      ? ReadExtentDirBlocks(image, geom, rootInode)
      : ReadInodeDirectBlockList(rootInode);
    if (rootBlocks.Count == 0) return false;

    // Find the entry across all root dir blocks.
    int hitBlock = -1; byte[] hitBytes = null!; int entryOffset = -1, prevOffset = -1; uint inodeNum = 0;
    foreach (var b in rootBlocks) {
      var bb = ReadBlock(image, geom, b);
      if (FindEntry(bb, name, out entryOffset, out prevOffset, out inodeNum)) {
        hitBlock = b; hitBytes = bb; break;
      }
    }
    if (hitBlock < 0) return false;

    var inodeBytes = ReadInode(image, geom, inodeNum);
    var inodeMode = BinaryPrimitives.ReadUInt16LittleEndian(inodeBytes.AsSpan(0, 2));
    if ((inodeMode & InodeModeDir) != 0) return false; // refuse to remove directories.

    var fileSize = BinaryPrimitives.ReadUInt32LittleEndian(inodeBytes.AsSpan(4, 4));
    var flags = BinaryPrimitives.ReadUInt32LittleEndian(inodeBytes.AsSpan(32, 4));
    var alloc = new Allocator(image, geom);

    // Collect every data + metadata block this inode owns, free + (optionally) wipe.
    var owned = new List<uint>();
    if ((flags & 0x80000) != 0)
      CollectExtentBlocks(image, geom, inodeBytes, owned);
    else
      CollectIndirectBlocks(image, geom, inodeBytes, fileSize, owned);

    foreach (var ptr in owned) {
      if (ptr < geom.FirstDataBlock || ptr >= geom.BlocksCount) continue;
      alloc.FreeBlock(ptr);
      if (wipeData) WriteBlock(image, geom, (int)ptr, new byte[geom.BlockSize]);
    }

    alloc.FreeInode(inodeNum);
    WriteInode(image, geom, inodeNum, new byte[geom.InodeSize]);
    if (geom.HasMetadataCsum) WriteInodeChecksum(image, geom, inodeNum);

    // Splice dirent out of its block.
    SpliceOutDirEntry(hitBytes, entryOffset, prevOffset);
    WriteDirBlock(image, geom, hitBlock, hitBytes, RootInode, isDtreeTail: true);

    alloc.Commit();
    return true;
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

      foreach (var group in allGroups) {
        var desc = Desc(group);
        var fb = BgdFreeBlocks(desc, _g.DescSize);
        var fi = BgdFreeInodes(desc, _g.DescSize);
        SetBgdFreeBlocks(desc, _g.DescSize, (uint)((int)fb + _blockDelta.GetValueOrDefault(group)));
        SetBgdFreeInodes(desc, _g.DescSize, (uint)((int)fi + _inodeDelta.GetValueOrDefault(group)));

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

      // 3) Superblock free counts (+ 64bit hi halves) and superblock checksum.
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
  /// Builds an inode-resident ext4 extent leaf for the given data blocks,
  /// coalescing contiguous runs into extents. Returns the 60-byte i_block area.
  /// Falls back to throwing if the layout needs more than 4 extents (the
  /// inode-resident leaf capacity) so the caller can rebuild instead.
  /// </summary>
  private static byte[] BuildExtentMapping(Allocator alloc, Geometry geom, List<uint> dataBlocks, out uint extraInodeBlocks) {
    extraInodeBlocks = 0;
    var area = new byte[60];
    // ext4_extent_header: eh_magic(0), eh_entries(2), eh_max(4), eh_depth(6), eh_generation(8).
    BinaryPrimitives.WriteUInt16LittleEndian(area.AsSpan(0, 2), ExtentMagic);
    BinaryPrimitives.WriteUInt16LittleEndian(area.AsSpan(4, 2), 4); // eh_max (inode-resident leaf holds 4)
    BinaryPrimitives.WriteUInt16LittleEndian(area.AsSpan(6, 2), 0); // eh_depth = 0 (leaf)

    if (dataBlocks.Count == 0) {
      BinaryPrimitives.WriteUInt16LittleEndian(area.AsSpan(2, 2), 0);
      return area;
    }

    // Coalesce into runs.
    var runs = new List<(uint start, uint len)>();
    uint runStart = dataBlocks[0], runLen = 1;
    for (var i = 1; i < dataBlocks.Count; i++) {
      if (dataBlocks[i] == runStart + runLen && runLen < 32768) { runLen++; }
      else { runs.Add((runStart, runLen)); runStart = dataBlocks[i]; runLen = 1; }
    }
    runs.Add((runStart, runLen));

    if (runs.Count > 4)
      throw new InPlaceUnsupportedException($"ext: file maps to {runs.Count} extents; inode-resident leaf holds 4. Rebuild required.");

    BinaryPrimitives.WriteUInt16LittleEndian(area.AsSpan(2, 2), (ushort)runs.Count); // eh_entries
    uint logical = 0;
    for (var i = 0; i < runs.Count; i++) {
      var (start, len) = runs[i];
      var off = 12 + i * 12;
      BinaryPrimitives.WriteUInt32LittleEndian(area.AsSpan(off, 4), logical);       // ee_block
      BinaryPrimitives.WriteUInt16LittleEndian(area.AsSpan(off + 4, 2), (ushort)len); // ee_len
      BinaryPrimitives.WriteUInt16LittleEndian(area.AsSpan(off + 6, 2), (ushort)((ulong)start >> 32)); // ee_start_hi (0 for <16TiB)
      BinaryPrimitives.WriteUInt32LittleEndian(area.AsSpan(off + 8, 4), start);      // ee_start_lo
      logical += len;
    }
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

  private static List<int> ReadInodeDirectBlockList(byte[] inode) {
    // Only direct blocks (linear dir blocks live in the first 12 pointers for our purposes).
    var list = new List<int>();
    for (var i = 0; i < 12; i++) {
      var b = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(40 + i * 4, 4));
      if (b == 0) break;
      list.Add((int)b);
    }
    return list;
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
  /// Appends one block to an inode-resident extent-mapped directory: extends the
  /// last extent if the new block is contiguous, otherwise adds a new extent (max
  /// 4 inode-resident). Bumps i_size + i_blocks and rewrites the inode.
  /// </summary>
  private static void GrowExtentDirectory(Stream image, Geometry geom, byte[] inode, List<int> existingBlocks, uint newBlock) {
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
        // Contiguous → extend the last extent.
        BinaryPrimitives.WriteUInt16LittleEndian(inode.AsSpan(off + 4, 2), (ushort)(eeLen + 1));
        appended = true;
      }
    }
    if (!appended) {
      var max = BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(44, 2));
      if (entries >= max)
        throw new InPlaceUnsupportedException("ext: extent directory leaf full; in-place add unsupported.");
      var off = 40 + 12 + entries * 12;
      BinaryPrimitives.WriteUInt32LittleEndian(inode.AsSpan(off, 4), lastLogical);  // ee_block
      BinaryPrimitives.WriteUInt16LittleEndian(inode.AsSpan(off + 4, 2), 1);        // ee_len
      BinaryPrimitives.WriteUInt16LittleEndian(inode.AsSpan(off + 6, 2), 0);        // ee_start_hi
      BinaryPrimitives.WriteUInt32LittleEndian(inode.AsSpan(off + 8, 4), newBlock); // ee_start_lo
      BinaryPrimitives.WriteUInt16LittleEndian(inode.AsSpan(42, 2), (ushort)(entries + 1)); // eh_entries
    }
    var size = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(4, 4));
    BinaryPrimitives.WriteUInt32LittleEndian(inode.AsSpan(4, 4), size + (uint)geom.BlockSize);
    var sectors = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(28, 4));
    BinaryPrimitives.WriteUInt32LittleEndian(inode.AsSpan(28, 4), sectors + (uint)(geom.BlockSize / 512));
    WriteInode(image, geom, RootInode, inode);
  }

  private static void GrowRootDirectory(Stream image, Geometry geom, byte[] rootInode, List<int> existingBlocks, uint newBlock) {
    // Append newBlock to the first free direct slot, bump i_size by one block.
    var slot = existingBlocks.Count;
    if (slot >= 12) throw new InPlaceUnsupportedException("ext: root dir indirect growth unsupported.");
    BinaryPrimitives.WriteUInt32LittleEndian(rootInode.AsSpan(40 + slot * 4, 4), newBlock);
    var size = BinaryPrimitives.ReadUInt32LittleEndian(rootInode.AsSpan(4, 4));
    BinaryPrimitives.WriteUInt32LittleEndian(rootInode.AsSpan(4, 4), size + (uint)geom.BlockSize);
    var sectors = BinaryPrimitives.ReadUInt32LittleEndian(rootInode.AsSpan(28, 4));
    BinaryPrimitives.WriteUInt32LittleEndian(rootInode.AsSpan(28, 4), sectors + (uint)(geom.BlockSize / 512));
    WriteInode(image, geom, RootInode, rootInode);
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
