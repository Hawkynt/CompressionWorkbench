#pragma warning disable CS1591
using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Layout;
using Compression.Registry;

namespace FileSystem.Ext1;

/// <summary>
/// In-place ext1 block mover. Moves block-aligned extents within an ext1 image
/// and patches inode block pointers + block bitmap so the file remains reachable.
/// <para>
/// Streaming: the image is never loaded whole. Reads go through a
/// <see cref="SectorCache"/>; metadata updates (inode block pointers, bitmap
/// bits) are targeted single-region writes with <see cref="Stream.Flush"/>
/// barriers between steps so a crash mid-update leaves the image in an
/// fsck-recoverable state.
/// </para>
/// <para><b>Scope:</b> ext1 is rev-0 only (no extents, no FILETYPE, 128-byte
/// inodes, single-CG profile in our writer).</para>
/// </summary>
public sealed class Ext1BlockMover : IFilesystemBlockMover {
  private int _blockSize;
  private uint _firstDataBlock;
  private uint _blocksCount;
  private uint _inodesPerGroup;
  private long _inodeTableOffset;
  private long _blockBitmapOffset;

  private const int SuperblockOffset = 1024;
  private const ushort Ext1Magic = 0xEF51;
  private const ushort InodeModeDir = 0x4000;
  private const int InodeSize = 128;
  private const uint RootInode = 2;

  /// <summary>Streaming init — reads only the superblock + first BGD (~1 KB total).</summary>
  public void Init(Stream image) {
    Span<byte> sb = stackalloc byte[1024];
    image.Position = SuperblockOffset;
    image.ReadExactly(sb);
    _blocksCount = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(4));
    var logBlock = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(24));
    _blockSize = 1024 << (int)logBlock;
    _inodesPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(40));
    _firstDataBlock = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(20));

    var bgdBlock = _firstDataBlock + 1;
    var bgdOffset = (long)bgdBlock * _blockSize;
    Span<byte> bgd = stackalloc byte[32];
    image.Position = bgdOffset;
    image.ReadExactly(bgd);
    _blockBitmapOffset = (long)BinaryPrimitives.ReadUInt32LittleEndian(bgd) * _blockSize;
    _inodeTableOffset = (long)BinaryPrimitives.ReadUInt32LittleEndian(bgd.Slice(8)) * _blockSize;
  }

  public long FirstDataByte => (long)_firstDataBlock * _blockSize;
  public int BlockSize => _blockSize;

  private uint OffsetToBlock(long offset) => (uint)(offset / _blockSize);

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false) {
    if (length <= 0 || srcOffset == dstOffset) return;

    // Overlap-safe: a run shifted forward by less than its own length
    // overwrites its own tail, and copying that front to back reads bytes
    // the copy has already replaced.
    Compression.Core.DiskImage.ExtentCopy.Move(image, srcOffset, dstOffset, length);
    if (zeroSource)
      Compression.Core.DiskImage.ExtentCopy.Zero(image, srcOffset, length);
  }

  /// <inheritdoc />
  /// <remarks>
  /// Power-fail-safe three-step update with flush barriers:
  ///   1. Claim new blocks in the bitmap (targeted RMW writes).
  ///   2. Patch inode block pointers (single inode-sized targeted write).
  ///   3. Release old blocks in the bitmap (targeted RMW writes).
  /// Crash mid-1: new blocks marked allocated but file still points at old →
  /// no data loss. Crash mid-2: inode partially patched → fsck detects.
  /// Crash mid-3: file reachable via new pointers, old blocks still marked
  /// allocated (orphan) → fsck frees them.
  /// </remarks>
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    var blockCount = (int)((length + _blockSize - 1) / _blockSize);
    var oldFirstBlock = OffsetToBlock(oldOffset);
    var newFirstBlock = OffsetToBlock(newOffset);

    using var cache = new SectorCache(image);

    // Step 1: Claim new blocks in bitmap (set bits).
    for (var i = 0; i < blockCount; i++)
      SetBitmapBitStream(image, _blockBitmapOffset, (int)(newFirstBlock + (uint)i - _firstDataBlock));
    cache.Invalidate(_blockBitmapOffset, _blockSize);
    image.Flush();

    // Step 2: Walk dirs from disk, find the target file's inode, patch its
    // direct + indirect block pointers. One inode-sized write per match.
    PatchInodeBlockPointersStream(image, cache, fileName, oldFirstBlock, newFirstBlock, blockCount);
    image.Flush();

    // Step 3: Release old blocks in bitmap (clear bits).
    for (var i = 0; i < blockCount; i++)
      ClearBitmapBitStream(image, _blockBitmapOffset, (int)(oldFirstBlock + (uint)i - _firstDataBlock));
    cache.Invalidate(_blockBitmapOffset, _blockSize);
    image.Flush();
  }

  // ── Stream-based bitmap RMW ────────────────────────────────────────────

  private static void SetBitmapBitStream(Stream image, long bitmapOff, int bit) {
    var pos = bitmapOff + bit / 8;
    Span<byte> b = stackalloc byte[1];
    image.Position = pos;
    image.ReadExactly(b);
    b[0] |= (byte)(1 << (bit % 8));
    image.Position = pos;
    image.Write(b);
  }

  private static void ClearBitmapBitStream(Stream image, long bitmapOff, int bit) {
    var pos = bitmapOff + bit / 8;
    Span<byte> b = stackalloc byte[1];
    image.Position = pos;
    image.ReadExactly(b);
    b[0] &= (byte)~(1 << (bit % 8));
    image.Position = pos;
    image.Write(b);
  }

  // ── Stream-based inode walks ───────────────────────────────────────────

  private void PatchInodeBlockPointersStream(Stream image, SectorCache cache, string fileName,
      uint oldFirst, uint newFirst, int blockCount) {
    var files = new List<(uint inode, string name)>();
    WalkDirStream(cache, RootInode, "", files, new HashSet<uint>());

    foreach (var (inode, name) in files) {
      if (!name.Equals(fileName, StringComparison.OrdinalIgnoreCase) &&
          !fileName.Equals("*", StringComparison.Ordinal)) continue;

      var inodeData = ReadInodeStream(cache, inode);
      if (inodeData == null) continue;

      PatchDirectBlockPointersStream(image, cache, inodeData, inode, oldFirst, newFirst, blockCount);
    }
  }

  /// <summary>
  /// Applies a whole old-to-new block mapping in one pass over a file's
  /// pointers, so no pointer is rewritten twice.
  /// </summary>
  /// <remarks>
  /// Patching run by run looks equivalent and is not. Each call rewrites every
  /// pointer that falls inside the run's <em>old</em> range, so when a later
  /// run's old range is where an earlier run was just moved to, the earlier
  /// run's pointers are moved a second time and the file's blocks end up in the
  /// wrong order. It reads back at its right length with one of its own blocks
  /// in the place of another — which is what consolidating towards the start
  /// produced, while consolidating towards the end, where the new ranges do not
  /// land on anything still to be read, did not.
  /// </remarks>
  private void PatchInodeBlockPointersStream(Stream image, SectorCache cache, string fileName,
      IReadOnlyDictionary<uint, uint> remap) {
    if (remap.Count == 0) return;
    var files = new List<(uint inode, string name)>();
    WalkDirStream(cache, RootInode, "", files, new HashSet<uint>());

    foreach (var (inode, name) in files) {
      if (!name.Equals(fileName, StringComparison.OrdinalIgnoreCase) &&
          !fileName.Equals("*", StringComparison.Ordinal)) continue;

      var inodeData = ReadInodeStream(cache, inode);
      if (inodeData == null) continue;

      var changed = false;
      for (var i = 0; i < 12; i++) {
        var ptr = BinaryPrimitives.ReadUInt32LittleEndian(inodeData.AsSpan(40 + i * 4));
        if (ptr == 0) break;
        if (!remap.TryGetValue(ptr, out var moved)) continue;
        BinaryPrimitives.WriteUInt32LittleEndian(inodeData.AsSpan(40 + i * 4), moved);
        changed = true;
      }

      for (var level = 0; level < 3; level++) {
        var indPtr = BinaryPrimitives.ReadUInt32LittleEndian(inodeData.AsSpan(88 + level * 4));
        if (indPtr != 0) PatchIndirectStream(image, cache, indPtr, remap, level + 1);
      }
      if (changed) WriteInodeStream(image, cache, inode, inodeData);
    }
  }

  private void PatchIndirectStream(Stream image, SectorCache cache, uint blockNum,
      IReadOnlyDictionary<uint, uint> remap, int level) {
    var off = (long)blockNum * _blockSize;
    if (off + _blockSize > cache.Length) return;
    var block = cache.Read(off, _blockSize);
    var per = _blockSize / 4;
    var changed = false;
    for (var i = 0; i < per; i++) {
      var ptr = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(i * 4));
      if (ptr == 0) break;
      if (level == 1) {
        if (!remap.TryGetValue(ptr, out var moved)) continue;
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(i * 4), moved);
        changed = true;
      } else {
        PatchIndirectStream(image, cache, ptr, remap, level - 1);
      }
    }
    if (changed) {
      image.Position = off;
      image.Write(block, 0, _blockSize);
      cache.Invalidate(off, _blockSize);
    }
  }

  private void PatchDirectBlockPointersStream(Stream image, SectorCache cache, byte[] inodeData,
      uint inodeNum, uint oldFirst, uint newFirst, int blockCount) {
    var changed = false;
    for (var i = 0; i < 12; i++) {
      var ptr = BinaryPrimitives.ReadUInt32LittleEndian(inodeData.AsSpan(40 + i * 4));
      if (ptr == 0) break;
      if (ptr >= oldFirst && ptr < oldFirst + (uint)blockCount) {
        BinaryPrimitives.WriteUInt32LittleEndian(inodeData.AsSpan(40 + i * 4),
          newFirst + (ptr - oldFirst));
        changed = true;
      }
    }
    // Indirect pointers — patch within referenced blocks via targeted writes.
    for (var level = 0; level < 3; level++) {
      var indPtr = BinaryPrimitives.ReadUInt32LittleEndian(inodeData.AsSpan(88 + level * 4));
      if (indPtr != 0)
        PatchIndirectStream(image, cache, indPtr, oldFirst, newFirst, blockCount, level + 1);
    }
    if (changed) WriteInodeStream(image, cache, inodeNum, inodeData);
  }

  private void PatchIndirectStream(Stream image, SectorCache cache, uint blockNum,
      uint oldFirst, uint newFirst, int blockCount, int level) {
    var off = (long)blockNum * _blockSize;
    if (off + _blockSize > cache.Length) return;
    var block = cache.Read(off, _blockSize);
    var per = _blockSize / 4;
    var changed = false;
    for (var i = 0; i < per; i++) {
      var ptr = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(i * 4));
      if (ptr == 0) break;
      if (level == 1) {
        if (ptr >= oldFirst && ptr < oldFirst + (uint)blockCount) {
          BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(i * 4), newFirst + (ptr - oldFirst));
          changed = true;
        }
      } else {
        PatchIndirectStream(image, cache, ptr, oldFirst, newFirst, blockCount, level - 1);
      }
    }
    if (changed) {
      image.Position = off;
      image.Write(block, 0, _blockSize);
      cache.Invalidate(off, _blockSize);
    }
  }

  private byte[]? ReadInodeStream(SectorCache cache, uint inodeNum) {
    if (inodeNum == 0) return null;
    var index = (inodeNum - 1) % _inodesPerGroup;
    var offset = _inodeTableOffset + (long)index * InodeSize;
    if (offset + InodeSize > cache.Length) return null;
    return cache.Read(offset, InodeSize);
  }

  private void WriteInodeStream(Stream image, SectorCache cache, uint inodeNum, byte[] inodeData) {
    if (inodeNum == 0) return;
    var index = (inodeNum - 1) % _inodesPerGroup;
    var offset = _inodeTableOffset + (long)index * InodeSize;
    if (offset + InodeSize > cache.Length) return;
    image.Position = offset;
    image.Write(inodeData, 0, InodeSize);
    cache.Invalidate(offset, InodeSize);
  }

  private void WalkDirStream(SectorCache cache, uint dirInode, string path,
      List<(uint, string)> files, HashSet<uint> seen) {
    if (!seen.Add(dirInode)) return;
    var inodeData = ReadInodeStream(cache, dirInode);
    if (inodeData == null) return;
    var mode = BinaryPrimitives.ReadUInt16LittleEndian(inodeData);
    if ((mode & InodeModeDir) == 0) return;
    var dirBytes = ReadInodeDataStream(cache, inodeData);

    var off = 0;
    while (off + 8 <= dirBytes.Length) {
      var ino = BinaryPrimitives.ReadUInt32LittleEndian(dirBytes.AsSpan(off));
      var recLen = BinaryPrimitives.ReadUInt16LittleEndian(dirBytes.AsSpan(off + 4));
      // rev-0: name_len is 16-bit (no file_type byte).
      var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(dirBytes.AsSpan(off + 6));
      if (recLen == 0) break;
      if (off + 8 + nameLen > dirBytes.Length) break;
      if (ino != 0 && nameLen > 0) {
        var name = Encoding.UTF8.GetString(dirBytes, off + 8, nameLen);
        if (name is not ("." or "..")) {
          var full = string.IsNullOrEmpty(path) ? name : $"{path}/{name}";
          var inoData = ReadInodeStream(cache, ino);
          if (inoData != null) {
            var m = BinaryPrimitives.ReadUInt16LittleEndian(inoData);
            if ((m & InodeModeDir) != 0)
              WalkDirStream(cache, ino, full, files, seen);
            else
              files.Add((ino, full));
          }
        }
      }
      off += recLen;
    }
  }

  private byte[] ReadInodeDataStream(SectorCache cache, byte[] inode) {
    var size = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(4));
    using var ms = new MemoryStream();
    var remaining = (long)size;
    for (var i = 0; i < 12 && remaining > 0; i++) {
      var bn = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(40 + i * 4));
      if (bn == 0) break;
      var toRead = (int)Math.Min(remaining, _blockSize);
      var off = (long)bn * _blockSize;
      if (off + toRead > cache.Length) break;
      var block = cache.Read(off, toRead);
      ms.Write(block, 0, toRead);
      remaining -= toRead;
    }
    return ms.ToArray();
  }

  // ── Scattered relink ──────────────────────────────────────────────────

  /// <inheritdoc />
  public int AllocationBlockSize => _blockSize;

  /// <inheritdoc />
  public bool SupportsScatteredRelink => true;

  /// <summary>
  /// Rewrites one file's whole allocation once every byte has moved.
  /// </summary>
  /// <remarks>
  /// <para>A chain-based filesystem has to relink a fragmented file as one
  /// chain, which is why the executor refuses to hand a fragmented owner to a
  /// mover that cannot express it. ext keeps a block <em>map</em> instead: each
  /// pointer stands on its own, in the inode or in an indirect block, and
  /// remapping the ones that moved leaves the file's order untouched. So a
  /// fragmented file is no harder here than a contiguous one — it is simply
  /// more runs to remap.</para>
  ///
  /// <para>Without this the volume was rebuilt whenever any file was in more
  /// than one piece, which on a fragmented volume is every time: the defragment
  /// verb read and rewrote every file to move a handful of blocks.</para>
  /// </remarks>
  public void UpdateAllocationScattered(Stream image, string fileName,
      IReadOnlyList<long> oldBlockOffsets, IReadOnlyList<long> newBlockOffsets,
      IReadOnlySet<long>? blocksLiveElsewhere) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(oldBlockOffsets);
    ArgumentNullException.ThrowIfNull(newBlockOffsets);
    if (newBlockOffsets.Count == 0 || oldBlockOffsets.Count != newBlockOffsets.Count) return;

    using var cache = new SectorCache(image);

    // Claim the new blocks before any pointer names them.
    foreach (var offset in newBlockOffsets)
      SetBitmapBitStream(image, _blockBitmapOffset, (int)(OffsetToBlock(offset) - _firstDataBlock));
    cache.Invalidate(_blockBitmapOffset, _blockSize);
    image.Flush();

    // One patch per contiguous stretch: the existing remap walks the inode's
    // direct pointers and every indirect block, replacing whatever falls inside
    // the old range.
    // One mapping, one pass. Patching each run separately rewrote pointers a
    // second time whenever a later run's old range was where an earlier run had
    // just been put, and the file's blocks came back out of order.
    var remap = new Dictionary<uint, uint>(newBlockOffsets.Count);
    for (var i = 0; i < newBlockOffsets.Count; ++i) {
      var from = OffsetToBlock(oldBlockOffsets[i]);
      var to = OffsetToBlock(newBlockOffsets[i]);
      if (from != to) remap[from] = to;
    }
    PatchInodeBlockPointersStream(image, cache, fileName, remap);
    image.Flush();
    cache.InvalidateAll();

    // Release only what nothing else has taken.
    var claimed = new HashSet<long>(newBlockOffsets);
    foreach (var offset in oldBlockOffsets) {
      if (claimed.Contains(offset)) continue;
      if (blocksLiveElsewhere?.Contains(offset) == true) continue;
      ClearBitmapBitStream(image, _blockBitmapOffset, (int)(OffsetToBlock(offset) - _firstDataBlock));
    }
    cache.Invalidate(_blockBitmapOffset, _blockSize);
    image.Flush();
  }

  /// <summary>Splits a block-by-block relocation into the stretches it is made of.</summary>
  private static IEnumerable<(long OldStart, long NewStart, long Count)> ContiguousRuns(
      IReadOnlyList<long> oldOffsets, IReadOnlyList<long> newOffsets, int blockSize) {
    var runOldStart = oldOffsets[0];
    var runNewStart = newOffsets[0];
    var count = 1L;

    for (var i = 1; i < oldOffsets.Count; ++i) {
      var continues = oldOffsets[i] == oldOffsets[i - 1] + blockSize
                   && newOffsets[i] == newOffsets[i - 1] + blockSize;
      if (continues) { ++count; continue; }
      yield return (runOldStart, runNewStart, count);
      runOldStart = oldOffsets[i];
      runNewStart = newOffsets[i];
      count = 1;
    }
    yield return (runOldStart, runNewStart, count);
  }

}
