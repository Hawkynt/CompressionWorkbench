#pragma warning disable CS1591
using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Layout;
using Compression.Registry;

namespace FileSystem.Ext;

/// <summary>
/// In-place ext2/3/4 block mover. Moves block-aligned extents within an ext
/// image and patches inode block pointers, block bitmap, and group descriptor
/// free counts.
/// <para>
/// Streaming: the image is never loaded whole. Reads go through a
/// <see cref="SectorCache"/>; metadata updates (inode block pointers, bitmap
/// bits) are targeted single-region writes with <see cref="Stream.Flush"/>
/// barriers between steps so a crash mid-update leaves the image in an
/// fsck-recoverable state.
/// </para>
/// <para><b>Scope:</b> single-cylinder-group profile (matches what
/// <c>ExtWriter</c> emits). Multi-group ext4 (required for 50 TB volumes
/// created elsewhere) needs additional work — walk the BGD table for the
/// target block's group rather than assuming group 0.</para>
/// </summary>
public sealed class ExtBlockMover : IFilesystemBlockMover {
  private int _blockSize;
  private uint _firstDataBlock;
  private uint _blocksCount;
  private uint _inodesPerGroup;
  private uint _blocksPerGroup;
  private int _inodeSize;
  private uint _featureIncompat;
  private long _bgdOffset;
  private long _blockBitmapOffset;
  private long _inodeBitmapOffset;
  private long _inodeTableOffset;

  private const int SuperblockOffset = 1024;
  private const ushort ExtMagic = 0xEF53;
  private const ushort InodeModeDir = 0x4000;
  private const uint ExtentsFlag = 0x80000;
  private const ushort ExtentMagic = 0xF30A;
  private const uint RootInode = 2;

  /// <summary>Initialises the mover from a byte buffer (legacy callers).</summary>
  public void Init(byte[] image) {
    var sb = image.AsSpan(SuperblockOffset);
    ParseSuperblock(sb);
    _bgdOffset = (long)(_firstDataBlock + 1) * _blockSize;
    var bgd = image.AsSpan((int)_bgdOffset);
    _blockBitmapOffset = (long)BinaryPrimitives.ReadUInt32LittleEndian(bgd) * _blockSize;
    _inodeBitmapOffset = (long)BinaryPrimitives.ReadUInt32LittleEndian(bgd.Slice(4)) * _blockSize;
    _inodeTableOffset = (long)BinaryPrimitives.ReadUInt32LittleEndian(bgd.Slice(8)) * _blockSize;
  }

  /// <summary>Streaming init — reads only the superblock + first BGD (~2 KB total).</summary>
  public void Init(Stream image) {
    Span<byte> sb = stackalloc byte[1024];
    image.Position = SuperblockOffset;
    image.ReadExactly(sb);
    ParseSuperblock(sb);
    _bgdOffset = (long)(_firstDataBlock + 1) * _blockSize;
    Span<byte> bgd = stackalloc byte[32];
    image.Position = _bgdOffset;
    image.ReadExactly(bgd);
    _blockBitmapOffset = (long)BinaryPrimitives.ReadUInt32LittleEndian(bgd) * _blockSize;
    _inodeBitmapOffset = (long)BinaryPrimitives.ReadUInt32LittleEndian(bgd[4..]) * _blockSize;
    _inodeTableOffset = (long)BinaryPrimitives.ReadUInt32LittleEndian(bgd[8..]) * _blockSize;
  }

  private void ParseSuperblock(ReadOnlySpan<byte> sb) {
    _blocksCount = BinaryPrimitives.ReadUInt32LittleEndian(sb[4..]);
    var logBlock = BinaryPrimitives.ReadUInt32LittleEndian(sb[24..]);
    _blockSize = 1024 << (int)logBlock;
    _blocksPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(sb[32..]);
    _inodesPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(sb[40..]);
    _inodeSize = BinaryPrimitives.ReadUInt16LittleEndian(sb[88..]);
    if (_inodeSize == 0) _inodeSize = 128;
    _featureIncompat = BinaryPrimitives.ReadUInt32LittleEndian(sb[96..]);
    _firstDataBlock = BinaryPrimitives.ReadUInt32LittleEndian(sb[20..]);
  }

  public long FirstDataByte => (long)_firstDataBlock * _blockSize;
  public int BlockSize => _blockSize;

  private uint OffsetToBlock(long offset) => (uint)(offset / _blockSize);

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false) {
    if (length <= 0 || srcOffset == dstOffset) return;
    var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min(length, 64 * 1024));
    try {
      var remaining = length;
      var src = srcOffset;
      var dst = dstOffset;
      while (remaining > 0) {
        var chunk = (int)Math.Min(remaining, buffer.Length);
        image.Position = src;
        image.ReadExactly(buffer, 0, chunk);
        image.Position = dst;
        image.Write(buffer, 0, chunk);
        src += chunk;
        dst += chunk;
        remaining -= chunk;
      }
      image.Flush();
      if (zeroSource) {
        Array.Clear(buffer, 0, buffer.Length);
        remaining = length;
        src = srcOffset;
        while (remaining > 0) {
          var chunk = (int)Math.Min(remaining, buffer.Length);
          image.Position = src;
          image.Write(buffer, 0, chunk);
          src += chunk;
          remaining -= chunk;
        }
        image.Flush();
      }
    } finally {
      ArrayPool<byte>.Shared.Return(buffer);
    }
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
    // block-pointer fields (or extent tree). One inode-sized write per match.
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
    WalkDirStream(image, cache, RootInode, "", files, new HashSet<uint>());

    foreach (var (inode, name) in files) {
      if (!name.Equals(fileName, StringComparison.OrdinalIgnoreCase) &&
          !fileName.Equals("*", StringComparison.Ordinal)) continue;

      var inodeData = ReadInodeStream(cache, inode);
      if (inodeData == null) continue;

      var flags = BinaryPrimitives.ReadUInt32LittleEndian(inodeData.AsSpan(32));
      var usesExtents = (flags & ExtentsFlag) != 0 && (_featureIncompat & (1u << 6)) != 0;

      if (usesExtents)
        PatchExtentTreeStream(image, cache, inodeData, inode, oldFirst, newFirst, blockCount);
      else
        PatchDirectBlockPointersStream(image, cache, inodeData, inode, oldFirst, newFirst, blockCount);
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
    if (off + _blockSize > image.Length) return;
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

  private void PatchExtentTreeStream(Stream image, SectorCache cache, byte[] inodeData,
      uint inodeNum, uint oldFirst, uint newFirst, int blockCount) {
    var ehMagic = BinaryPrimitives.ReadUInt16LittleEndian(inodeData.AsSpan(40));
    if (ehMagic != ExtentMagic) return;
    var entries = BinaryPrimitives.ReadUInt16LittleEndian(inodeData.AsSpan(42));
    var depth = BinaryPrimitives.ReadUInt16LittleEndian(inodeData.AsSpan(46));
    var changed = PatchExtentNodeStream(image, cache, inodeData, 40, entries, depth,
      oldFirst, newFirst, blockCount);
    if (changed) WriteInodeStream(image, cache, inodeNum, inodeData);
  }

  private bool PatchExtentNodeStream(Stream image, SectorCache cache, byte[] node, int hdrOffset,
      int entries, int depth, uint oldFirst, uint newFirst, int blockCount) {
    var changed = false;
    if (depth == 0) {
      for (var i = 0; i < entries; i++) {
        var off = hdrOffset + 12 + i * 12;
        if (off + 12 > node.Length) break;
        var startHi = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(off + 6));
        var startLo = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(off + 8));
        // 48-bit physical block: keep it 64-bit so the ee_start_hi write below
        // shifts correctly. A 32-bit value here would make `newStart >> 32` a
        // no-op (C# masks the shift to 31), writing the LOW 16 bits into
        // ee_start_hi and producing a wild physical start that fsck rejects.
        var startBlock = ((long)startHi << 32) | startLo;
        if (startBlock >= oldFirst && startBlock < (long)oldFirst + blockCount) {
          var newStart = (long)newFirst + (startBlock - oldFirst);
          BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(off + 6), (ushort)(newStart >> 32));
          BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(off + 8), (uint)(newStart & 0xFFFFFFFF));
          changed = true;
        }
      }
    } else {
      for (var i = 0; i < entries; i++) {
        var off = hdrOffset + 12 + i * 12;
        if (off + 12 > node.Length) break;
        var leafLo = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(off + 4));
        var leafHi = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(off + 8));
        var leafBlock = ((long)leafHi << 32) | leafLo;
        var blockOff = leafBlock * _blockSize;
        if (blockOff + _blockSize > image.Length) continue;
        var child = cache.Read(blockOff, _blockSize);
        if (BinaryPrimitives.ReadUInt16LittleEndian(child) != ExtentMagic) continue;
        var ce = BinaryPrimitives.ReadUInt16LittleEndian(child.AsSpan(2));
        var cd = BinaryPrimitives.ReadUInt16LittleEndian(child.AsSpan(6));
        if (PatchExtentNodeStream(image, cache, child, 0, ce, cd, oldFirst, newFirst, blockCount)) {
          image.Position = blockOff;
          image.Write(child, 0, _blockSize);
          cache.Invalidate(blockOff, _blockSize);
          changed = true;
        }
      }
    }
    return changed;
  }

  private byte[]? ReadInodeStream(SectorCache cache, uint inodeNum) {
    if (inodeNum == 0) return null;
    var index = (inodeNum - 1) % _inodesPerGroup;
    var offset = _inodeTableOffset + (long)index * _inodeSize;
    if (offset + _inodeSize > cache.Length) return null;
    return cache.Read(offset, _inodeSize);
  }

  private void WriteInodeStream(Stream image, SectorCache cache, uint inodeNum, byte[] inodeData) {
    if (inodeNum == 0) return;
    var index = (inodeNum - 1) % _inodesPerGroup;
    var offset = _inodeTableOffset + (long)index * _inodeSize;
    if (offset + _inodeSize > image.Length) return;
    image.Position = offset;
    image.Write(inodeData, 0, _inodeSize);
    cache.Invalidate(offset, _inodeSize);
  }

  private void WalkDirStream(Stream image, SectorCache cache, uint dirInode, string path,
      List<(uint, string)> files, HashSet<uint> seen) {
    if (!seen.Add(dirInode)) return;
    var inodeData = ReadInodeStream(cache, dirInode);
    if (inodeData == null) return;
    var mode = BinaryPrimitives.ReadUInt16LittleEndian(inodeData);
    if ((mode & InodeModeDir) == 0) return;
    var dirBytes = ReadInodeDataStream(image, cache, inodeData);

    var off = 0;
    while (off + 8 <= dirBytes.Length) {
      var ino = BinaryPrimitives.ReadUInt32LittleEndian(dirBytes.AsSpan(off));
      var recLen = BinaryPrimitives.ReadUInt16LittleEndian(dirBytes.AsSpan(off + 4));
      var nameLen = dirBytes[off + 6];
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
              WalkDirStream(image, cache, ino, full, files, seen);
            else
              files.Add((ino, full));
          }
        }
      }
      off += recLen;
    }
  }

  private byte[] ReadInodeDataStream(Stream image, SectorCache cache, byte[] inode) {
    var size = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(4));
    var flags = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(32));
    var usesExtents = (flags & ExtentsFlag) != 0 && (_featureIncompat & (1u << 6)) != 0;
    using var ms = new MemoryStream();
    if (usesExtents) ReadExtentTreeStream(image, cache, inode, size, ms);
    else ReadBlockPointersStream(image, cache, inode, size, ms);
    return ms.ToArray();
  }

  private void ReadBlockPointersStream(Stream image, SectorCache cache, byte[] inode, uint size, MemoryStream ms) {
    var remaining = (long)size;
    for (var i = 0; i < 12 && remaining > 0; i++) {
      var bn = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(40 + i * 4));
      if (bn == 0) break;
      var toRead = (int)Math.Min(remaining, _blockSize);
      var off = (long)bn * _blockSize;
      if (off + toRead > image.Length) break;
      var block = cache.Read(off, toRead);
      ms.Write(block, 0, toRead);
      remaining -= toRead;
    }
  }

  private void ReadExtentTreeStream(Stream image, SectorCache cache, byte[] inode, uint size, MemoryStream ms) {
    var remaining = (long)size;
    var ehMagic = BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(40));
    if (ehMagic != ExtentMagic) return;
    var entries = BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(42));
    var depth = BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(46));
    ReadExtentNodeStream(image, cache, inode, 40, entries, depth, ms, ref remaining);
  }

  private void ReadExtentNodeStream(Stream image, SectorCache cache, byte[] node, int hdrOff,
      int entries, int depth, MemoryStream ms, ref long remaining) {
    if (depth != 0) return; // leaf-only profile (matches what our reader uses)
    for (var i = 0; i < entries && remaining > 0; i++) {
      var off = hdrOff + 12 + i * 12;
      if (off + 12 > node.Length) break;
      var len = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(off + 4)) & 0x7FFF;
      var startHi = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(off + 6));
      var startLo = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(off + 8));
      var startBlock = ((long)startHi << 32) | startLo;
      for (var b = 0; b < len && remaining > 0; b++) {
        var blockOff = (startBlock + b) * _blockSize;
        if (blockOff + _blockSize > image.Length) break;
        var toRead = (int)Math.Min(remaining, _blockSize);
        var block = cache.Read(blockOff, toRead);
        ms.Write(block, 0, toRead);
        remaining -= toRead;
      }
    }
  }
}
