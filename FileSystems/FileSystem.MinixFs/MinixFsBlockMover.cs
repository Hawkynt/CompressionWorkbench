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
      // Crash barrier: data must land on disk before metadata references it.
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
        // Crash barrier: data must land on disk before metadata references it.
        image.Flush();
      }
    } finally {
      ArrayPool<byte>.Shared.Return(buffer);
    }
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

    var oldZone = (uint)(oldOffset / blockSize);
    var newZone = (uint)(newOffset / blockSize);
    var zoneCount = (int)((length + blockSize - 1) / blockSize);

    using var cache = new SectorCache(image);

    // Step 1: Claim new zones in zone bitmap.
    for (var i = 0; i < zoneCount; i++)
      SetBitStream(image, zmapOffset, (int)(newZone + (uint)i));
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
        if (ptr == 0) break;
        if (ptr >= oldZone && ptr < oldZone + (uint)zoneCount) {
          BinaryPrimitives.WriteUInt32LittleEndian(inodeBuf.AsSpan(24 + z * 4),
            newZone + (ptr - oldZone));
          changed = true;
        }
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
      ClearBitStream(image, zmapOffset, (int)(oldZone + (uint)i));
    cache.Invalidate(zmapOffset, zmapBlocks * blockSize);
    image.Flush();
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
