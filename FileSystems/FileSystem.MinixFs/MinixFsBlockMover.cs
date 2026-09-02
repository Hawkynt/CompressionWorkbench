#pragma warning disable CS1591
using System.Buffers;
using System.Buffers.Binary;
using Compression.Core.Layout;
using Compression.Registry;

namespace FileSystem.MinixFs;

/// <summary>
/// In-place Minix v3 block mover. Moves zone-aligned extents within a MinixFS
/// image and patches inode zone pointers + zone bitmap so the file remains
/// reachable.
/// <para>
/// Streaming: the image is never loaded whole. Reads go through a
/// <see cref="SectorCache"/>; metadata updates (inode zone pointers, zone
/// bitmap bits) are targeted single-region writes with <see cref="Stream.Flush"/>
/// barriers between steps so a crash mid-update leaves the image in an
/// fsck-recoverable state.
/// </para>
/// </summary>
public sealed class MinixFsBlockMover : IFilesystemBlockMover {

  private const int SuperblockOffset = 1024;
  private const ushort MagicV3 = 0x4D5A;
  private const int V3InodeSize = 64;

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
  ///   1. Claim new zones in the zone bitmap (targeted RMW writes).
  ///   2. Patch inode zone pointers across the inode table (targeted writes per
  ///      inode that needs updating).
  ///   3. Release old zones in the zone bitmap (targeted RMW writes).
  /// Crash mid-1: new zones marked allocated but inodes still point at old →
  /// no data loss. Crash mid-2: inode table partially patched → fsck detects.
  /// Crash mid-3: file reachable via new pointers, old zones still marked
  /// allocated (orphan) → fsck frees them.
  /// </remarks>
  /// <summary>
  /// Performs the update allocation after move operation.
  /// </summary>
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    // Parse superblock from disk (only 32 bytes needed).
    Span<byte> sb = stackalloc byte[32];
    image.Position = SuperblockOffset;
    image.ReadExactly(sb);

    var magic = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(24));
    if (magic != MagicV3) return;

    var totalInodes = BinaryPrimitives.ReadUInt32LittleEndian(sb);
    var imapBlocks = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(6));
    var zmapBlocks = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(8));
    var blockSizeField = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(28));
    var blockSize = blockSizeField == 0 ? 1024 : (int)blockSizeField;

    var zmapOffset = 2L * blockSize + (long)imapBlocks * blockSize;
    var inodeTableOffset = zmapOffset + (long)zmapBlocks * blockSize;

    // The zone map covers the data zones only and counts from the first of them,
    // with bit 0 reserved: absolute zone Z sits at bit Z - firstdatazone + 1.
    // Marking the absolute number instead claimed and released the wrong bits.
    var firstDataZone = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(10)); // s_firstdatazone
    int ZoneBit(uint zone) => (int)zone - firstDataZone + 1;

    var oldZone = (uint)(oldOffset / blockSize);
    var newZone = (uint)(newOffset / blockSize);
    var zoneCount = (int)((length + blockSize - 1) / blockSize);

    using var cache = new SectorCache(image);

    // Step 1: Claim new zones in zone bitmap.
    for (var i = 0; i < zoneCount; i++)
      SetBitStream(image, zmapOffset, ZoneBit(newZone + (uint)i));
    cache.Invalidate(zmapOffset, zmapBlocks * blockSize);
    image.Flush();

    // Step 2: Walk all inodes in the table looking for zone pointers that
    // reference the old range; patch each affected inode with a targeted write.
    var inodeBuf = new byte[V3InodeSize];
    for (uint ino = 1; ino <= totalInodes; ino++) {
      var inodeOff = inodeTableOffset + (long)(ino - 1) * V3InodeSize;
      if (inodeOff + V3InodeSize > cache.Length) break;
      var read = cache.Read(inodeOff, V3InodeSize);

      var mode = BinaryPrimitives.ReadUInt16LittleEndian(read.AsSpan());
      if (mode == 0) continue;

      var changed = false;
      Array.Copy(read, inodeBuf, V3InodeSize);
      for (var z = 0; z < 10; z++) {
        var ptr = BinaryPrimitives.ReadUInt32LittleEndian(inodeBuf.AsSpan(24 + z * 4));
        // A pointer of zero is a hole, not the end of the file. Stopping here
        // left every pointer behind a hole naming a zone that had moved.
        if (ptr == 0) continue;
        if (ptr >= oldZone && ptr < oldZone + (uint)zoneCount) {
          BinaryPrimitives.WriteUInt32LittleEndian(inodeBuf.AsSpan(24 + z * 4),
            newZone + (ptr - oldZone));
          changed = true;
          continue;
        }

        // Past the seven direct slots the pointer names a block of pointers,
        // and the zones that moved may be named in there rather than here. This
        // used to look only at the inode, so a file large enough to need
        // indirect addressing kept pointing at where its bytes had been.
        if (z >= DirectSlots)
          PatchIndirect(image, cache, ptr, z - DirectSlots + 1, oldZone, newZone, zoneCount, blockSize);
      }
      if (changed) {
        image.Position = inodeOff;
        image.Write(inodeBuf, 0, V3InodeSize);
        cache.Invalidate(inodeOff, V3InodeSize);
      }
    }
    image.Flush();

    // Step 3: Release old zones in zone bitmap.
    for (var i = 0; i < zoneCount; i++)
      ClearBitStream(image, zmapOffset, ZoneBit(oldZone + (uint)i));
    cache.Invalidate(zmapOffset, zmapBlocks * blockSize);
    image.Flush();
  }

  /// <summary>Zone slots in an inode that name data directly.</summary>
  private const int DirectSlots = 7;

  /// <summary>
  /// Rewrites any pointer inside an indirect block that names a zone which has
  /// moved, descending as many levels as the slot it came from implies.
  /// </summary>
  private static void PatchIndirect(Stream image, SectorCache cache, uint block, int level,
      uint oldZone, uint newZone, int zoneCount, int blockSize) {
    var offset = (long)block * blockSize;
    if (offset < 0 || offset + blockSize > cache.Length) return;

    var buffer = cache.Read(offset, blockSize);
    var changed = false;
    for (var i = 0; i < blockSize / 4; ++i) {
      var ptr = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(i * 4));
      if (ptr == 0) continue;

      if (level <= 1) {
        if (ptr < oldZone || ptr >= oldZone + (uint)zoneCount) continue;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(i * 4), newZone + (ptr - oldZone));
        changed = true;
        continue;
      }

      PatchIndirect(image, cache, ptr, level - 1, oldZone, newZone, zoneCount, blockSize);
    }

    if (!changed) return;
    image.Position = offset;
    image.Write(buffer, 0, blockSize);
    cache.Invalidate(offset, blockSize);
  }

  private static void SetBitStream(Stream image, long bitmapOffset, int bit) {
    var pos = bitmapOffset + bit / 8;
    Span<byte> b = stackalloc byte[1];
    image.Position = pos;
    image.ReadExactly(b);
    b[0] |= (byte)(1 << (bit % 8));
    image.Position = pos;
    image.Write(b);
  }

  private static void ClearBitStream(Stream image, long bitmapOffset, int bit) {
    var pos = bitmapOffset + bit / 8;
    Span<byte> b = stackalloc byte[1];
    image.Position = pos;
    image.ReadExactly(b);
    b[0] &= (byte)~(1 << (bit % 8));
    image.Position = pos;
    image.Write(b);
  }
}
