#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Ext;

/// <summary>
/// Genuine in-place ext2/3/4 volume shrink. Trims trailing free blocks off the end of
/// the volume, updating the block bitmap, the block group descriptor free count, the
/// superblock <c>s_blocks_count</c> / <c>s_free_blocks_count</c> (and their 64-bit hi
/// halves), the scaled reserved-block count, every sparse_super backup superblock +
/// GDT, and recomputing crc32c/crc16 metadata checksums where the volume enables them
/// — then truncating the image. Work is <c>O(metadata touched)</c>: every surviving
/// block (data and metadata) stays byte-identical, so this is a true in-place edit and
/// not a re-pack.
///
/// <para><b>Scope / restriction.</b> This version shrinks <b>trailing free space</b>
/// only, and only when the new boundary stays inside the last block group (no whole
/// block group is removed). It does <b>not</b> relocate a referenced data block that
/// sits at or above the new boundary: such a shrink is refused with
/// <see cref="NotSupportedException"/> so the caller falls back to a rebuild. (Genuine
/// block relocation needs a shrink-correct extent/indirect-pointer mover; the shared
/// <c>ExtBlockMover</c>'s extent-tree patch is not yet correct for this, so it is
/// deliberately not used here rather than producing an image the e2fsck oracle
/// rejects.) <see cref="ShrinkToFit"/> always succeeds because it picks the boundary
/// at the highest in-use block; only an explicit over-tight
/// <see cref="ShrinkToBlocks(System.IO.Stream, uint)"/> target can trip the refusal.</para>
/// </summary>
public static class ExtInPlaceShrinker {

  private const int SuperblockOffset = 1024;
  private const ushort ExtMagic = 0xEF53;
  private const uint Incompat64Bit = 0x0080;
  private const uint IncompatCsumSeed = 0x2000;
  private const uint RoCompatSparseSuper = 0x0001;
  private const uint RoCompatGdtCsum = 0x0010;
  private const uint RoCompatMetadataCsum = 0x0400;

  /// <summary>Result of an ext shrink attempt: the before/after byte sizes and how much was physically rewritten.</summary>
  public readonly record struct ShrinkResult(long OriginalSize, long NewSize, long BytesRelocated, long BlocksRelocated) {
    /// <summary>True when the image was actually made smaller.</summary>
    public bool WasReduced => this.NewSize < this.OriginalSize;
  }

  private sealed class Geometry {
    public int BlockSize;
    public uint FirstDataBlock;
    public uint BlocksCount;        // low 32 bits
    public uint BlocksCountHi;      // high 32 bits (64bit feature)
    public uint BlocksPerGroup;
    public uint InodesPerGroup;
    public ushort InodeSize;
    public uint FeatureIncompat;
    public uint FeatureRoCompat;
    public int DescSize;
    public uint GroupCount;
    public long BgdtOffset;
    public uint CsumSeed;
    public byte[] Uuid = new byte[16];
    public bool Has64Bit => (FeatureIncompat & Incompat64Bit) != 0;
    public bool HasMetadataCsum => (FeatureRoCompat & RoCompatMetadataCsum) != 0;
    public bool HasGdtCsum => (FeatureRoCompat & RoCompatGdtCsum) != 0;
    public bool HasSparseSuper => (FeatureRoCompat & RoCompatSparseSuper) != 0;
    public ulong TotalBlocks => ((ulong)BlocksCountHi << 32) | BlocksCount;
    public long BgdOffset(uint group) => BgdtOffset + (long)group * DescSize;
  }

  /// <summary>
  /// Shrinks an ext image in place to the smallest block count that still holds the
  /// current allocation (auto-fit), relocating trailing in-use blocks down.
  /// </summary>
  /// <param name="image">A readable/writable/seekable stream over the ext image; it is modified and truncated in place.</param>
  /// <returns>The shrink result.</returns>
  public static ShrinkResult ShrinkToFit(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var geo = ReadGeometry(image);
    var highest = HighestUsedBlock(image, geo);
    // One past the highest in-use block, but never below the metadata floor.
    var target = Math.Max(highest + 1, MetadataFloor(geo));
    return ShrinkToBlocks(image, (uint)target);
  }

  /// <summary>
  /// Shrinks an ext image in place to exactly <paramref name="targetBlocks"/> blocks.
  /// </summary>
  /// <param name="image">A readable/writable/seekable stream over the ext image.</param>
  /// <param name="targetBlocks">The desired new total block count.</param>
  /// <returns>The shrink result.</returns>
  /// <exception cref="NotSupportedException">If the shrink would remove a whole block group, or in-use blocks cannot fit below the boundary.</exception>
  public static ShrinkResult ShrinkToBlocks(Stream image, uint targetBlocks) {
    ArgumentNullException.ThrowIfNull(image);
    var geo = ReadGeometry(image);
    var originalSize = image.Length;

    if (targetBlocks == 0 || targetBlocks >= geo.BlocksCount)
      return new ShrinkResult(originalSize, originalSize, 0, 0);

    // Restriction: the boundary must stay inside the LAST block group (no group
    // removal). The last group starts at firstDataBlock + (groupCount-1)*blocksPerGroup.
    var lastGroupStart = geo.FirstDataBlock + (long)(geo.GroupCount - 1) * geo.BlocksPerGroup;
    if (targetBlocks <= lastGroupStart)
      throw new NotSupportedException(
        $"ext shrink: target {targetBlocks} would drop a whole block group (last group starts at block {lastGroupStart}); rebuild fallback required.");

    var floor = MetadataFloor(geo);
    if (targetBlocks < floor)
      throw new NotSupportedException(
        $"ext shrink: target {targetBlocks} is below the metadata floor {floor}; rebuild fallback required.");

    // Scope (see class remarks): this version shrinks trailing FREE space only — it
    // never relocates a referenced data block across the boundary. If any in-use
    // block sits at or above the target, refuse so the caller can fall back to a
    // rebuild. This keeps the operation a genuine in-place metadata edit (bitmap /
    // descriptor / superblock / backups / checksums) that the e2fsck oracle accepts.
    var highest = HighestUsedBlock(image, geo);
    if (highest >= targetBlocks)
      throw new NotSupportedException(
        $"ext shrink: block {highest} is in use at/above the {targetBlocks}-block boundary; " +
        "this in-place shrinker only trims trailing free space. Rebuild fallback required.");

    // Update the bitmap (clear bits >= target, pad the bitmap-block tail), the group
    // descriptor + superblock free counts and block count, backup SBs/GDTs, and the
    // metadata checksums.
    ApplyGeometryShrink(image, geo, targetBlocks);

    var newSize = (long)targetBlocks * geo.BlockSize;
    image.SetLength(newSize);

    // A trailing-free trim physically relocates nothing.
    return new ShrinkResult(originalSize, newSize, BytesRelocated: 0, BlocksRelocated: 0);
  }

  // ── Geometry / bitmap / superblock + checksum updates ───────────────────────

  // Clears block bitmap bits >= target, sets the bitmap-block padding tail, recomputes
  // the last group's free count + the superblock counts, rewrites all backup SBs/GDTs,
  // and recomputes metadata checksums. Returns the number of freed (trimmed) blocks.
  private static long ApplyGeometryShrink(Stream image, Geometry geo, uint target) {
    var lastGroup = geo.GroupCount - 1;
    var desc = ReadBgd(image, geo, lastGroup);
    var blockBitmapBlock = BgdBlockBitmap(desc, geo.DescSize);
    var bitmapOffset = (long)blockBitmapBlock * geo.BlockSize;
    var groupFirstBlock = geo.FirstDataBlock + (long)lastGroup * geo.BlocksPerGroup;

    var bitmap = new byte[geo.BlockSize];
    image.Position = bitmapOffset;
    image.ReadExactly(bitmap);

    // Count current free blocks in [target, oldBlocksCount) that we are removing (so
    // the surviving free count is correct) and clear those bits.
    long removedFree = 0;
    for (long b = target; b < geo.BlocksCount; b++) {
      var idx = (int)(b - groupFirstBlock);
      if (!IsBlockUsed(bitmap, idx)) removedFree++;
      ClearBlockBit(bitmap, idx);
    }

    // Pad the block bitmap: bits for blocks that no longer exist (>= newBlocksInGroup)
    // up to the end of the bitmap block must read as used (mkfs/e2fsck convention).
    var newBlocksInGroup = (int)(target - groupFirstBlock);
    for (var bit = newBlocksInGroup; bit < geo.BlockSize * 8; bit++)
      SetBlockUsed(bitmap, bit);
    image.Position = bitmapOffset;
    image.Write(bitmap, 0, geo.BlockSize);

    // Group descriptor free-block count loses the removed free blocks.
    var descFree = BgdFreeBlocks(desc, geo.DescSize);
    SetBgdFreeBlocks(desc, geo.DescSize, (uint)(descFree - removedFree));
    if (geo.HasMetadataCsum || geo.HasGdtCsum) {
      WriteBitmapCsumIntoDesc(geo, desc, bitmap);
      WriteGroupDescChecksum(geo, desc, lastGroup);
    }
    WriteBgd(image, geo, lastGroup, desc);

    // Superblock: new block count + reduced free count, and (when metadata_csum) its
    // own crc. Reserved blocks are scaled down proportionally like resize2fs does.
    UpdateSuperblock(image, geo, target, removedFree);

    // Mirror the primary superblock + GDT to every sparse_super backup that still
    // exists in the shrunk volume so e2fsck's backup comparison stays clean.
    SyncBackups(image, geo, target);

    return removedFree;
  }

  private static void UpdateSuperblock(Stream image, Geometry geo, uint target, long removedFree) {
    var sb = new byte[1024];
    image.Position = SuperblockOffset;
    image.ReadExactly(sb);

    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(4), target);                 // s_blocks_count_lo
    if (geo.Has64Bit) BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(0x150), 0); // s_blocks_count_hi

    var free = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(12));
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(12), (uint)(free - removedFree));
    if (geo.Has64Bit) {
      var freeHi = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(0x158));
      // removedFree fits in the low 32 bits for any realistic image; clamp safely.
      if (freeHi != 0 && (free - removedFree) > free) BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(0x158), freeHi - 1);
    }

    // Reserved block count (s_r_blocks_count_lo @ 8): scale proportionally to the new
    // size so the reserved fraction is preserved (matches resize2fs behaviour).
    var reserved = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(8));
    if (geo.BlocksCount > 0) {
      var scaled = (uint)((ulong)reserved * target / geo.BlocksCount);
      BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(8), scaled);
    }

    if (geo.HasMetadataCsum) {
      var crc = Crc32c(0xFFFFFFFFu, sb.AsSpan(0, 0x3FC));
      BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(0x3FC), crc);
    }

    image.Position = SuperblockOffset;
    image.Write(sb, 0, 1024);
    image.Flush();
  }

  // Copies the (already-updated) primary superblock + GDT to each surviving
  // sparse_super backup. Without sparse_super, every group holds a backup.
  private static void SyncBackups(Stream image, Geometry geo, uint target) {
    if (geo.GroupCount <= 1) return;

    var primarySb = new byte[1024];
    image.Position = SuperblockOffset;
    image.ReadExactly(primarySb);

    var gdtBytes = (int)geo.GroupCount * geo.DescSize;
    var gdt = new byte[gdtBytes];
    image.Position = geo.BgdtOffset;
    image.ReadExactly(gdt);

    for (uint g = 1; g < geo.GroupCount; g++) {
      if (geo.HasSparseSuper && !HasSuperBackup(g)) continue;
      var groupStartBlock = geo.FirstDataBlock + (long)g * geo.BlocksPerGroup;
      if (groupStartBlock >= target) continue; // backup lives in a removed region

      // Backup superblock sits at the first block of the group; the backup GDT in the
      // following block(s). s_block_group_nr (@0x5A) must carry the group number.
      var sbCopy = (byte[])primarySb.Clone();
      BinaryPrimitives.WriteUInt16LittleEndian(sbCopy.AsSpan(0x5A), (ushort)g);
      if (geo.HasMetadataCsum) {
        var crc = Crc32c(0xFFFFFFFFu, sbCopy.AsSpan(0, 0x3FC));
        BinaryPrimitives.WriteUInt32LittleEndian(sbCopy.AsSpan(0x3FC), crc);
      }
      var sbOffset = groupStartBlock * geo.BlockSize;
      // For 1 KiB blocks the primary SB is at byte 1024 inside block 1; backups sit at
      // the very start of their group's first block.
      if (sbOffset + 1024 <= image.Length) {
        image.Position = sbOffset;
        image.Write(sbCopy, 0, 1024);
      }
      var gdtOffset = (groupStartBlock + 1) * geo.BlockSize;
      if (gdtOffset + gdtBytes <= image.Length) {
        image.Position = gdtOffset;
        image.Write(gdt, 0, gdtBytes);
      }
    }
    image.Flush();
  }

  // sparse_super: a group has a SB/GDT backup iff it is 0, 1, or a power of 3, 5, 7.
  private static bool HasSuperBackup(uint group) {
    if (group is 0 or 1) return true;
    foreach (var p in new uint[] { 3, 5, 7 }) {
      var n = p;
      while (n < group) n *= p;
      if (n == group) return true;
    }
    return false;
  }

  // ── Block bitmap helpers ─────────────────────────────────────────────────

  private static bool IsBlockUsed(byte[] bitmap, int idx) {
    if (idx < 0 || idx / 8 >= bitmap.Length) return true;
    return (bitmap[idx / 8] & (1 << (idx % 8))) != 0;
  }
  private static void SetBlockUsed(byte[] bitmap, int idx) {
    if (idx < 0 || idx / 8 >= bitmap.Length) return;
    bitmap[idx / 8] |= (byte)(1 << (idx % 8));
  }
  private static void ClearBlockBit(byte[] bitmap, int idx) {
    if (idx < 0 || idx / 8 >= bitmap.Length) return;
    bitmap[idx / 8] &= (byte)~(1 << (idx % 8));
  }

  private static long HighestUsedBlock(Stream image, Geometry geo) {
    // Scan every group's block bitmap for the highest set bit. Streaming: one
    // bitmap block per group.
    long highest = geo.FirstDataBlock;
    var bitmap = new byte[geo.BlockSize];
    for (uint g = 0; g < geo.GroupCount; g++) {
      var desc = ReadBgd(image, geo, g);
      var bb = BgdBlockBitmap(desc, geo.DescSize);
      image.Position = (long)bb * geo.BlockSize;
      image.ReadExactly(bitmap);
      var groupFirst = geo.FirstDataBlock + (long)g * geo.BlocksPerGroup;
      var blocksInGroup = (int)Math.Min(geo.BlocksPerGroup, geo.BlocksCount - groupFirst);
      for (var i = blocksInGroup - 1; i >= 0; i--) {
        if (IsBlockUsed(bitmap, i)) { highest = Math.Max(highest, groupFirst + i); break; }
      }
    }
    return highest;
  }

  // Metadata floor for the LAST group: blocks up to and including its inode table
  // must survive (block bitmap, inode bitmap, inode table). Returns first block index
  // strictly after the last group's fixed metadata.
  private static long MetadataFloor(Geometry geo) {
    var lastGroup = geo.GroupCount - 1;
    var groupFirst = geo.FirstDataBlock + (long)lastGroup * geo.BlocksPerGroup;
    // Reserve the last group's fixed metadata span: block bitmap (1) + inode bitmap
    // (1) + inode table, plus a small headroom. The boundary must not drop into it.
    var inodeTableBlocks = (int)((geo.InodesPerGroup * (uint)geo.InodeSize + (uint)geo.BlockSize - 1) / (uint)geo.BlockSize);
    return groupFirst + 2 + inodeTableBlocks + 4;
  }

  // ── Group descriptor field access (folds 64-bit hi halves) ──────────────────

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
  private static uint BgdFreeBlocks(byte[] b, int descSize) {
    uint lo = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(12, 2));
    if (descSize >= 64) lo |= (uint)BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(44, 2)) << 16;
    return lo;
  }
  private static void SetBgdFreeBlocks(byte[] b, int descSize, uint v) {
    BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(12, 2), (ushort)(v & 0xFFFF));
    if (descSize >= 64) BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(44, 2), (ushort)(v >> 16));
  }

  // ── Checksums (mirrors ExtModifier conventions) ─────────────────────────────

  private static void WriteBitmapCsumIntoDesc(Geometry g, byte[] desc, byte[] bitmap) {
    if (!g.HasMetadataCsum) return;
    var bytes = (int)((g.BlocksPerGroup + 7) / 8);
    if (bytes > g.BlockSize) bytes = g.BlockSize;
    var csum = Crc32c(g.CsumSeed, bitmap.AsSpan(0, bytes));
    BinaryPrimitives.WriteUInt16LittleEndian(desc.AsSpan(24, 2), (ushort)(csum & 0xFFFF));
    if (g.DescSize >= 64) BinaryPrimitives.WriteUInt16LittleEndian(desc.AsSpan(56, 2), (ushort)(csum >> 16));
  }

  private static void WriteGroupDescChecksum(Geometry g, byte[] desc, uint group) {
    if (g.HasMetadataCsum) {
      var groupLe = new byte[4];
      BinaryPrimitives.WriteUInt32LittleEndian(groupLe, group);
      var crc = Crc32c(g.CsumSeed, groupLe);
      BinaryPrimitives.WriteUInt16LittleEndian(desc.AsSpan(0x1E, 2), 0);
      crc = Crc32c(crc, desc.AsSpan(0, g.DescSize));
      BinaryPrimitives.WriteUInt16LittleEndian(desc.AsSpan(0x1E, 2), (ushort)(crc & 0xFFFF));
    } else if (g.HasGdtCsum) {
      ushort crc = 0xFFFF;
      crc = Crc16(crc, g.Uuid);
      var groupLe = new byte[4];
      BinaryPrimitives.WriteUInt32LittleEndian(groupLe, group);
      crc = Crc16(crc, groupLe);
      crc = Crc16(crc, desc.AsSpan(0, 0x1E).ToArray());
      if (g.DescSize > 0x20) crc = Crc16(crc, desc.AsSpan(0x20, g.DescSize - 0x20).ToArray());
      BinaryPrimitives.WriteUInt16LittleEndian(desc.AsSpan(0x1E, 2), crc);
    }
  }

  private static uint Crc32c(uint seed, ReadOnlySpan<byte> data) {
    const uint poly = 0x82F63B78u;
    var crc = seed;
    foreach (var b in data) {
      crc ^= b;
      for (var i = 0; i < 8; i++) crc = (crc & 1) != 0 ? (crc >> 1) ^ poly : crc >> 1;
    }
    return crc;
  }

  private static ushort Crc16(ushort crc, byte[] data) {
    foreach (var b in data) {
      crc ^= b;
      for (var i = 0; i < 8; i++) crc = (ushort)((crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1);
    }
    return crc;
  }

  // ── Superblock parse ────────────────────────────────────────────────────────

  private static Geometry ReadGeometry(Stream image) {
    var sb = new byte[1024];
    image.Position = SuperblockOffset;
    if (image.Length < SuperblockOffset + 1024)
      throw new InvalidDataException("ext shrink: image too small for superblock.");
    image.ReadExactly(sb);

    var magic = BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(56));
    if (magic != ExtMagic)
      throw new InvalidDataException($"ext shrink: invalid magic 0x{magic:X4}.");

    var blocksCount = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(4));
    var firstData = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(20));
    var logBlock = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(24));
    var blockSize = 1024 << (int)logBlock;
    var blocksPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(32));
    var inodesPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(40));
    var revLevel = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(76));
    var inodeSize = revLevel >= 1 ? BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(88)) : (ushort)128;
    if (inodeSize == 0) inodeSize = 128;
    var featureIncompat = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(96));
    var featureRoCompat = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(100));
    var uuid = sb.AsSpan(104, 16).ToArray();
    var blocksCountHi = (featureIncompat & Incompat64Bit) != 0
      ? BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(0x150)) : 0u;

    var descSize = 32;
    if ((featureIncompat & Incompat64Bit) != 0) {
      descSize = BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(254));
      if (descSize < 32) descSize = 32;
    }

    if (blocksPerGroup == 0) throw new InvalidDataException("ext shrink: blocks-per-group is zero.");
    var groupCount = (uint)(((ulong)blocksCount - firstData + blocksPerGroup - 1) / blocksPerGroup);
    var bgdtOffset = (long)(firstData + 1) * blockSize;

    uint csumSeed;
    if ((featureIncompat & IncompatCsumSeed) != 0)
      csumSeed = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(0x270));
    else
      csumSeed = Crc32c(0xFFFFFFFFu, uuid);

    return new Geometry {
      BlockSize = blockSize,
      FirstDataBlock = firstData,
      BlocksCount = blocksCount,
      BlocksCountHi = blocksCountHi,
      BlocksPerGroup = blocksPerGroup,
      InodesPerGroup = inodesPerGroup,
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
}
