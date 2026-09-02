#pragma warning disable CS1591
using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Layout;
using Compression.Registry;

namespace FileSystem.Ufs;

/// <summary>
/// In-place UFS1 block mover. Moves block-aligned extents within a UFS1 image
/// and patches the inode's direct block pointers + CG bitmap so the file remains
/// reachable at its new location.
/// <para>
/// Streaming: the image is never loaded whole. Reads go through a
/// <see cref="SectorCache"/>; metadata updates (inode block pointers, bitmap
/// bits, CG header) are targeted single-region writes with <see cref="Stream.Flush"/>
/// barriers between steps so a crash mid-update leaves the image in a
/// fsck-recoverable state.
/// </para>
/// <para><b>Scope:</b> single-cylinder-group profile (matches what
/// <see cref="UfsWriter"/> emits). Multi-CG UFS volumes would need additional
/// work — walk the CG header table for the target block's group rather than
/// assuming CG 0.</para>
/// </summary>
public sealed class UfsBlockMover : IFilesystemBlockMover {

  private const int SuperblockOffset = UfsWriter.SuperblockOffset;
  private const int SuperblockSize = UfsWriter.SuperblockSize;
  private const int FsMagicOffset = SuperblockSize - 4;
  private const uint Ufs1Magic = (uint)UfsWriter.Ufs1Magic;
  private const int CgMagic = UfsWriter.CgMagic;
  private const int InodeSize = UfsWriter.InodeSize;
  private const int RootIno = UfsWriter.RootIno;
  private const int MaxDirectBlocks = UfsWriter.MaxDirectBlocks;

  private int _blockSize;
  private int _fragSize;
  private int _fragsPerBlock;
  private int _inodesPerGroup;
  private int _fragsPerGroup;
  private int _cgBlockNo;
  private int _inodeTableFragNo;

  /// <summary>
  /// Initialises geometry from the UFS1 superblock. Must be called before any moves.
  /// Reads only the superblock (~1.4 KB) — no whole-image load.
  /// </summary>
  public void Init(Stream image) {
    Span<byte> sb = stackalloc byte[SuperblockSize];
    image.Position = SuperblockOffset;
    image.ReadExactly(sb);

    var magic = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(FsMagicOffset, 4));
    if (magic != Ufs1Magic)
      throw new InvalidDataException($"UFS: invalid superblock magic 0x{magic:X8}.");

    _blockSize = BinaryPrimitives.ReadInt32LittleEndian(sb.Slice(48, 4));
    _fragSize = BinaryPrimitives.ReadInt32LittleEndian(sb.Slice(52, 4));
    _fragsPerBlock = BinaryPrimitives.ReadInt32LittleEndian(sb.Slice(56, 4));
    _inodesPerGroup = (int)BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(184, 4));
    _fragsPerGroup = BinaryPrimitives.ReadInt32LittleEndian(sb.Slice(188, 4));
    _cgBlockNo = BinaryPrimitives.ReadInt32LittleEndian(sb.Slice(12, 4));
    _inodeTableFragNo = BinaryPrimitives.ReadInt32LittleEndian(sb.Slice(16, 4));
  }

  /// <summary>Filesystem block size in bytes.</summary>
  public int BlockSize => _blockSize;

  /// <summary>
  /// Byte offset where the file-data region begins, past the inode table.
  /// The defrag planner uses this as the lowest legal target offset; metadata
  /// regions (superblock, CG, inode bitmap, inode table) sit below it.
  /// </summary>
  public long DataOrigin {
    get {
      var inodeTableFrags = (_inodesPerGroup * (long)InodeSize + _fragSize - 1) / _fragSize;
      return (_inodeTableFragNo + inodeTableFrags) * (long)_fragSize;
    }
  }

  /// <summary>Converts a fragment number to a byte offset.</summary>
  public long FragToOffset(int frag) => (long)frag * _fragSize;

  /// <summary>Converts a byte offset to a fragment number.</summary>
  public int OffsetToFrag(long offset) => (int)(offset / _fragSize);

  /// <inheritdoc />
    /// <summary>
  /// Performs the move extent operation.
  /// </summary>
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
  ///   1. Claim new frags in the CG free-frag bitmap (targeted RMW writes).
  ///   2. Patch inode block pointers (single inode-sized targeted write).
  ///   3. Release old frags in the CG free-frag bitmap (targeted RMW writes).
  /// Crash mid-1: new frags marked allocated but file still points at old →
  /// no data loss. Crash mid-2: inode partially patched → fsck detects.
  /// Crash mid-3: file reachable via new pointers, old frags still marked
  /// allocated (orphan) → fsck frees them.
  /// </remarks>
    /// <summary>
  /// Performs the update allocation after move operation.
  /// </summary>
public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    var oldFrag = OffsetToFrag(oldOffset);
    var newFrag = OffsetToFrag(newOffset);
    var blockCount = (int)((length + _blockSize - 1) / _blockSize);

    // Build old → new frag mapping (block-aligned).
    var mapping = new Dictionary<int, int>(blockCount);
    for (var i = 0; i < blockCount; i++) {
      var oldBlkFrag = oldFrag + i * _fragsPerBlock;
      var newBlkFrag = newFrag + i * _fragsPerBlock;
      mapping[oldBlkFrag] = newBlkFrag;
    }

    using var cache = new SectorCache(image);

    // Read CG header (block-sized region at _cgBlockNo).
    var cgOffset = (long)_cgBlockNo * _fragSize;
    if (cgOffset + _blockSize > cache.Length) return;
    var cgBlock = cache.Read(cgOffset, _blockSize);
    var cgMagic = BinaryPrimitives.ReadInt32LittleEndian(cgBlock.AsSpan(4, 4));
    if (cgMagic != CgMagic) return;

    var freeOff = (int)BinaryPrimitives.ReadUInt32LittleEndian(cgBlock.AsSpan(96, 4));
    var clusterOff = (int)BinaryPrimitives.ReadUInt32LittleEndian(cgBlock.AsSpan(108, 4));

    // Step 1: Claim new frags (mark allocated = clear bit in free bitmap).
    foreach (var (_, newBlk) in mapping) {
      for (var f = 0; f < _fragsPerBlock; f++) {
        var bit = newBlk + f;
        var byteIdx = freeOff + bit / 8;
        if (byteIdx >= 0 && byteIdx < cgBlock.Length)
          cgBlock[byteIdx] &= (byte)~(1 << (bit % 8));
      }
      var nCBit = newBlk / _fragsPerBlock;
      var nCByteIdx = clusterOff + nCBit / 8;
      if (nCByteIdx >= 0 && nCByteIdx < cgBlock.Length)
        cgBlock[nCByteIdx] &= (byte)~(1 << (nCBit % 8));
    }
    WriteBlockStream(image, cgOffset, cgBlock);
    cache.Invalidate(cgOffset, _blockSize);
    image.Flush();

    // Step 2: Find the file's inode by scanning the root directory; patch its
    // direct block pointers. Single inode-sized targeted write per match.
    var rootInode = ReadInodeStream(cache, RootIno);
    var rootDirFrag = BinaryPrimitives.ReadInt32LittleEndian(rootInode.AsSpan(40, 4));
    if (rootDirFrag == 0) return;

    var dirBlock = ReadBlockStream(cache, rootDirFrag);
    var targetIno = FindInodeByName(dirBlock, fileName);
    if (targetIno <= 0) return;

    var inode = ReadInodeStream(cache, targetIno);
    var patched = false;
    for (var i = 0; i < MaxDirectBlocks; i++) {
      var ptr = BinaryPrimitives.ReadInt32LittleEndian(inode.AsSpan(40 + i * 4, 4));
      if (ptr == 0) continue;
      if (mapping.TryGetValue(ptr, out var newPtr)) {
        BinaryPrimitives.WriteInt32LittleEndian(inode.AsSpan(40 + i * 4, 4), newPtr);
        patched = true;
      }
    }

    if (!patched) return;

    WriteInodeStream(image, cache, targetIno, inode);
    image.Flush();

    // Step 3: Release old frags (mark free = set bit in free bitmap). Re-read
    // CG block since it may have been modified concurrently in step 1.
    cgBlock = cache.Read(cgOffset, _blockSize);
    foreach (var (oldBlk, _) in mapping) {
      for (var f = 0; f < _fragsPerBlock; f++) {
        var bit = oldBlk + f;
        var byteIdx = freeOff + bit / 8;
        if (byteIdx >= 0 && byteIdx < cgBlock.Length)
          cgBlock[byteIdx] |= (byte)(1 << (bit % 8));
      }
      var oCBit = oldBlk / _fragsPerBlock;
      var oCByteIdx = clusterOff + oCBit / 8;
      if (oCByteIdx >= 0 && oCByteIdx < cgBlock.Length)
        cgBlock[oCByteIdx] |= (byte)(1 << (oCBit % 8));
    }
    WriteBlockStream(image, cgOffset, cgBlock);
    cache.Invalidate(cgOffset, _blockSize);
    image.Flush();
  }

  // ── Directory helpers ────────────────────────────────────────────────

  private static int FindInodeByName(byte[] dirData, string name) {
    var nameBytes = Encoding.ASCII.GetBytes(name);
    var off = 0;
    while (off + 8 <= dirData.Length) {
      var ino = (int)BinaryPrimitives.ReadUInt32LittleEndian(dirData.AsSpan(off, 4));
      var recLen = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(off + 4, 2));
      if (recLen < 8 || off + recLen > dirData.Length) break;
      var namLen = dirData[off + 7];
      if (ino != 0 && namLen == nameBytes.Length &&
          off + 8 + namLen <= dirData.Length &&
          dirData.AsSpan(off + 8, namLen).SequenceEqual(nameBytes)) {
        return ino;
      }
      off += recLen;
    }
    return 0;
  }

  // ── Stream-based block / inode I/O ───────────────────────────────────

  private byte[] ReadBlockStream(SectorCache cache, int blockFrag) =>
    cache.Read((long)blockFrag * _fragSize, _blockSize);

  private static void WriteBlockStream(Stream image, long offset, byte[] data) {
    image.Position = offset;
    image.Write(data, 0, data.Length);
  }

  private byte[] ReadInodeStream(SectorCache cache, int inodeNum) {
    var offset = (long)_inodeTableFragNo * _fragSize + (long)inodeNum * InodeSize;
    return cache.Read(offset, InodeSize);
  }

  private void WriteInodeStream(Stream image, SectorCache cache, int inodeNum, byte[] data) {
    var offset = (long)_inodeTableFragNo * _fragSize + (long)inodeNum * InodeSize;
    image.Position = offset;
    image.Write(data, 0, InodeSize);
    cache.Invalidate(offset, InodeSize);
  }
}
