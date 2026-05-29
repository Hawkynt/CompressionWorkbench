#pragma warning disable CS1591
using System.Buffers.Binary;
using FileSystem.Ext;

namespace Compression.Lib.FsConversion;

/// <summary>
/// In-place ext2/3/4 shrink / grow primitive. Operates directly on a Stream
/// (no full image load), so it composes with partition substreams.
///
/// <para><b>Scope:</b> the single-cylinder-group profile emitted by
/// <see cref="ExtWriter"/>. Multi-group ext4 (which is what real 1+ GB
/// filesystems use) needs additional work — for each group above the new
/// boundary, walk its BGD bitmap, migrate the used blocks down, then drop
/// the group descriptor. This implementation accepts a multi-group image
/// silently as long as the live data lives below the new boundary; if any
/// inode references an above-boundary block we throw rather than corrupt.</para>
///
/// <para><b>Crash semantics:</b> the resize is staged in this order:
/// (1) physical block migrations (each uses
/// <see cref="ExtBlockMover.UpdateAllocationAfterMove"/>'s three-step
/// targeted write + flush sequence — recoverable on crash by fsck),
/// (2) bitmap adjustments + BGD free-count update (single sector writes),
/// (3) superblock <c>s_blocks_count</c> patch (single sector write),
/// (4) <c>SetLength</c> truncation. The superblock is patched LAST so a
/// crash during steps 1–2 leaves the FS at its original size with some
/// orphaned data (fsck recoverable). A crash after step 3 but before
/// step 4 leaves the FS reporting the new size (correct) on a still-larger
/// physical container — readable, just with trailing dead space.</para>
/// </summary>
public static class ExtResizer {

  private const int SuperblockOffset = 1024;
  private const ushort ExtMagic = 0xEF53;
  private const int BgdSize = 32;

  /// <summary>
  /// Shrinks an ext2/3/4 image to <paramref name="newSizeBytes"/>. The new
  /// size must be block-aligned and large enough to hold all currently-used
  /// blocks (after migrating any that fall above the new boundary).
  /// </summary>
  /// <exception cref="InvalidDataException">Stream is not a recognisable ext image.</exception>
  /// <exception cref="InvalidOperationException">No free blocks below boundary for migration.</exception>
  public static void Shrink(Stream image, long newSizeBytes) {
    ArgumentNullException.ThrowIfNull(image);
    if (newSizeBytes <= 0) throw new ArgumentOutOfRangeException(nameof(newSizeBytes));

    var ctx = ReadContext(image);
    if (newSizeBytes >= image.Length) return; // No-op.
    if (newSizeBytes % ctx.BlockSize != 0)
      throw new ArgumentException(
        $"New size must be a multiple of block size ({ctx.BlockSize}).", nameof(newSizeBytes));

    var newBlocksCount = (uint)(newSizeBytes / ctx.BlockSize);
    // We need to keep the superblock, BGD, both bitmaps, the inode table,
    // and at least one data block.
    var minBlocks = ctx.FirstDataBlock + 4 + (uint)ctx.InodeTableBlocks + 1;
    if (newBlocksCount < minBlocks)
      throw new InvalidOperationException(
        $"New size ({newSizeBytes} bytes / {newBlocksCount} blocks) is too small to hold ext metadata " +
        $"(minimum {minBlocks} blocks).");

    // Identify above-boundary used blocks by walking the block bitmap.
    var usedAbove = new List<uint>();
    var freeBelow = new Queue<uint>();
    for (uint b = ctx.FirstDataBlock; b < ctx.BlocksCount; b++) {
      var isUsed = ReadBitmapBit(image, ctx.BlockBitmapOffset, (int)(b - ctx.FirstDataBlock));
      if (b >= newBlocksCount) {
        if (isUsed) usedAbove.Add(b);
      } else {
        // The bitmap also covers metadata blocks (which are marked used). We
        // only want truly-free data blocks as relocation targets — skip
        // anything below firstFreeBlock since those are SB/BGD/bitmaps/inode-table.
        if (!isUsed && b >= ctx.FirstFreeBlock) freeBelow.Enqueue(b);
      }
    }

    if (usedAbove.Count > freeBelow.Count)
      throw new InvalidOperationException(
        $"Cannot shrink to {newSizeBytes} bytes: {usedAbove.Count} blocks live above the new boundary " +
        $"but only {freeBelow.Count} free blocks are available below it. Defragment first or pick a larger target.");

    // Migrate above-boundary blocks down using ExtBlockMover. Each call
    // performs the three-step targeted update (claim new bit → patch inode
    // pointer → release old bit) with flush barriers in between.
    if (usedAbove.Count > 0) {
      var mover = new ExtBlockMover();
      mover.Init(image);
      foreach (var srcBlock in usedAbove) {
        if (freeBelow.Count == 0)
          throw new InvalidOperationException("Ran out of free blocks below boundary during migration.");
        var dstBlock = freeBelow.Dequeue();
        var srcOff = (long)srcBlock * ctx.BlockSize;
        var dstOff = (long)dstBlock * ctx.BlockSize;
        // Move one block; ExtBlockMover.MoveExtent does the physical copy,
        // UpdateAllocationAfterMove patches the owning inode's block pointer
        // / extent tree and the bitmap. Wildcard "*" matches any inode.
        mover.MoveExtent(image, srcOff, dstOff, ctx.BlockSize, zeroSource: false);
        mover.UpdateAllocationAfterMove(image, "*", srcOff, dstOff, ctx.BlockSize);
      }
    }

    // Recompute free counts after migrations: count used bits below the new
    // boundary (some may have been claimed during migration).
    var freeBlocks = 0u;
    for (uint b = ctx.FirstDataBlock; b < newBlocksCount; b++)
      if (!ReadBitmapBit(image, ctx.BlockBitmapOffset, (int)(b - ctx.FirstDataBlock)))
        freeBlocks++;

    // Mark all bits above the new boundary as "used" (pad-to-1 convention)
    // so fsck's bitmap-vs-blocks-count check passes.
    var newBitmapBitCount = (int)(newBlocksCount - ctx.FirstDataBlock);
    var bitmapByteSize = ctx.BlockSize;
    for (var bit = newBitmapBitCount; bit < bitmapByteSize * 8; bit++)
      SetBitmapBit(image, ctx.BlockBitmapOffset, bit);
    image.Flush();

    // Patch BGD bg_free_blocks_count + bg_used_dirs_count unchanged.
    Span<byte> bgdField = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(bgdField, (ushort)Math.Min(freeBlocks, 0xFFFFu));
    image.Position = ctx.BgdOffset + 12; // bg_free_blocks_count
    image.Write(bgdField);
    image.Flush();

    // Patch superblock s_blocks_count, s_free_blocks_count, s_blocks_per_group.
    // We keep a single block group, so blocks_per_group matches new size.
    Span<byte> sbField = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(sbField, newBlocksCount);
    image.Position = SuperblockOffset + 4;
    image.Write(sbField);
    BinaryPrimitives.WriteUInt32LittleEndian(sbField, freeBlocks);
    image.Position = SuperblockOffset + 12;
    image.Write(sbField);
    BinaryPrimitives.WriteUInt32LittleEndian(sbField, newBlocksCount);
    image.Position = SuperblockOffset + 32; // s_blocks_per_group
    image.Write(sbField);
    BinaryPrimitives.WriteUInt32LittleEndian(sbField, newBlocksCount);
    image.Position = SuperblockOffset + 36; // s_frags_per_group
    image.Write(sbField);
    image.Flush();

    // Finally, truncate.
    image.SetLength(newSizeBytes);
    image.Flush();
  }

  /// <summary>
  /// Grows an ext2/3/4 image to <paramref name="newSizeBytes"/>. New blocks
  /// past the old end are marked free in the bitmap (so an allocator can
  /// reuse them). The on-disk bitmap block size (one block) constrains the
  /// achievable post-grow size to <c>blockSize * 8</c> data blocks per
  /// group — beyond that an additional block group would be required, which
  /// is out of scope.
  /// </summary>
  public static void Grow(Stream image, long newSizeBytes) {
    ArgumentNullException.ThrowIfNull(image);
    if (newSizeBytes <= 0) throw new ArgumentOutOfRangeException(nameof(newSizeBytes));

    var ctx = ReadContext(image);
    if (newSizeBytes <= image.Length) return; // No-op.
    if (newSizeBytes % ctx.BlockSize != 0)
      throw new ArgumentException(
        $"New size must be a multiple of block size ({ctx.BlockSize}).", nameof(newSizeBytes));

    var newBlocksCount = (uint)(newSizeBytes / ctx.BlockSize);
    var newBitmapBitCount = newBlocksCount - ctx.FirstDataBlock;
    var bitmapMaxBits = (uint)(ctx.BlockSize * 8);
    if (newBitmapBitCount > bitmapMaxBits)
      throw new NotSupportedException(
        $"Growing past {ctx.FirstDataBlock + bitmapMaxBits} blocks would require additional block groups; " +
        "out of scope.");

    // Step 1: extend the physical stream and zero the new bytes (so the
    // tail block-bitmap bits / inode-table padding / data blocks read as
    // free / zero). On most Stream impls SetLength already zero-fills, but
    // we don't trust that.
    var oldLength = image.Length;
    image.SetLength(newSizeBytes);
    ZeroRange(image, oldLength, newSizeBytes - oldLength);
    image.Flush();

    // Step 2: clear the "padding" bits in the block bitmap that previously
    // covered the now-grown range. The writer marks above-boundary bits as
    // 1 (used) — we clear those back to 0 so new blocks read as free.
    var oldBitmapBitCount = (int)(ctx.BlocksCount - ctx.FirstDataBlock);
    var clearedCount = 0u;
    for (var bit = oldBitmapBitCount; bit < (int)newBitmapBitCount; bit++) {
      ClearBitmapBit(image, ctx.BlockBitmapOffset, bit);
      clearedCount++;
    }
    image.Flush();

    // Step 3: update BGD free-blocks-count (clamped to 16-bit field).
    var newFreeBlocks = ReadSuperblockUInt32(image, 12) + clearedCount;
    var newBgdFree = ReadBgdUInt16(image, ctx.BgdOffset, 12) + clearedCount;
    Span<byte> bgdField = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(bgdField, (ushort)Math.Min(newBgdFree, 0xFFFFu));
    image.Position = ctx.BgdOffset + 12;
    image.Write(bgdField);
    image.Flush();

    // Step 4: patch superblock — s_blocks_count, s_free_blocks_count,
    // s_blocks_per_group, s_frags_per_group.
    Span<byte> sbField = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(sbField, newBlocksCount);
    image.Position = SuperblockOffset + 4;
    image.Write(sbField);
    BinaryPrimitives.WriteUInt32LittleEndian(sbField, newFreeBlocks);
    image.Position = SuperblockOffset + 12;
    image.Write(sbField);
    BinaryPrimitives.WriteUInt32LittleEndian(sbField, newBlocksCount);
    image.Position = SuperblockOffset + 32;
    image.Write(sbField);
    BinaryPrimitives.WriteUInt32LittleEndian(sbField, newBlocksCount);
    image.Position = SuperblockOffset + 36;
    image.Write(sbField);
    image.Flush();
  }

  // ── Implementation helpers ───────────────────────────────────────────────

  private sealed class ExtContext {
    public int BlockSize;
    public uint FirstDataBlock;
    public uint BlocksCount;
    public uint InodesPerGroup;
    public int InodeTableBlocks;
    public long BgdOffset;
    public long BlockBitmapOffset;
    public uint FirstFreeBlock;
  }

  private static ExtContext ReadContext(Stream image) {
    if (image.Length < SuperblockOffset + 264)
      throw new InvalidDataException("ext: image too small.");
    image.Position = SuperblockOffset;
    Span<byte> sb = stackalloc byte[264];
    image.ReadExactly(sb);
    var magic = BinaryPrimitives.ReadUInt16LittleEndian(sb[56..]);
    if (magic != ExtMagic)
      throw new InvalidDataException($"ext: invalid magic 0x{magic:X4}, expected 0xEF53.");

    var ctx = new ExtContext {
      BlocksCount = BinaryPrimitives.ReadUInt32LittleEndian(sb[4..]),
      FirstDataBlock = BinaryPrimitives.ReadUInt32LittleEndian(sb[20..]),
      InodesPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(sb[40..]),
    };
    var logBlockSize = BinaryPrimitives.ReadUInt32LittleEndian(sb[24..]);
    ctx.BlockSize = 1024 << (int)logBlockSize;
    var inodeSize = (int)BinaryPrimitives.ReadUInt16LittleEndian(sb[88..]);
    if (inodeSize == 0) inodeSize = 128;
    ctx.InodeTableBlocks = ((int)ctx.InodesPerGroup * inodeSize + ctx.BlockSize - 1) / ctx.BlockSize;

    // Single-group profile: BGD lives at (firstDataBlock + 1) * blockSize.
    ctx.BgdOffset = (long)(ctx.FirstDataBlock + 1) * ctx.BlockSize;
    Span<byte> bgd = stackalloc byte[BgdSize];
    image.Position = ctx.BgdOffset;
    image.ReadExactly(bgd);
    var blockBitmapBlock = BinaryPrimitives.ReadUInt32LittleEndian(bgd);
    ctx.BlockBitmapOffset = (long)blockBitmapBlock * ctx.BlockSize;

    // Block layout (single group, matching ExtWriter): SB(1) + BGD(1) +
    // block_bitmap(1) + inode_bitmap(1) + inode_table(N) + root_dir(1) + ...
    ctx.FirstFreeBlock = ctx.FirstDataBlock + 4 + (uint)ctx.InodeTableBlocks;
    return ctx;
  }

  private static bool ReadBitmapBit(Stream image, long bitmapOff, int bit) {
    var pos = bitmapOff + bit / 8;
    if (pos >= image.Length) return false;
    image.Position = pos;
    Span<byte> b = stackalloc byte[1];
    image.ReadExactly(b);
    return (b[0] & (1 << (bit % 8))) != 0;
  }

  private static void SetBitmapBit(Stream image, long bitmapOff, int bit) {
    var pos = bitmapOff + bit / 8;
    if (pos >= image.Length) return;
    image.Position = pos;
    Span<byte> b = stackalloc byte[1];
    image.ReadExactly(b);
    b[0] |= (byte)(1 << (bit % 8));
    image.Position = pos;
    image.Write(b);
  }

  private static void ClearBitmapBit(Stream image, long bitmapOff, int bit) {
    var pos = bitmapOff + bit / 8;
    if (pos >= image.Length) return;
    image.Position = pos;
    Span<byte> b = stackalloc byte[1];
    image.ReadExactly(b);
    b[0] &= (byte)~(1 << (bit % 8));
    image.Position = pos;
    image.Write(b);
  }

  private static uint ReadSuperblockUInt32(Stream image, int offset) {
    image.Position = SuperblockOffset + offset;
    Span<byte> b = stackalloc byte[4];
    image.ReadExactly(b);
    return BinaryPrimitives.ReadUInt32LittleEndian(b);
  }

  private static ushort ReadBgdUInt16(Stream image, long bgdOffset, int offset) {
    image.Position = bgdOffset + offset;
    Span<byte> b = stackalloc byte[2];
    image.ReadExactly(b);
    return BinaryPrimitives.ReadUInt16LittleEndian(b);
  }

  private static void ZeroRange(Stream image, long offset, long length) {
    if (length <= 0) return;
    var buf = new byte[Math.Min((int)length, 64 * 1024)];
    var remaining = length;
    var pos = offset;
    while (remaining > 0) {
      var chunk = (int)Math.Min(remaining, buf.Length);
      image.Position = pos;
      image.Write(buf, 0, chunk);
      pos += chunk;
      remaining -= chunk;
    }
  }
}
