#pragma warning disable CS1591
using System.Buffers.Binary;
using static FileSystem.Apfs.ApfsConstants;

namespace FileSystem.Apfs;

/// <summary>
/// Reads and updates what a container knows about its own free space.
/// </summary>
/// <remarks>
/// <para>The space manager keeps the allocation bitmap: one bitmap block per
/// chunk, a chunk-info block naming them, and a free count in each. A writer
/// that hands blocks out without touching any of it leaves a container whose
/// accounting disagrees with its contents, which is the first thing a checker
/// looks at.</para>
///
/// <para>Blocks are handed out in order and never given back here, so the state
/// is entirely described by how many are in use.</para>
/// </remarks>
internal static class ApfsSpaceManagerState {

  private const int BlockSize = 4096;
  private const int BlocksPerChunk = 32768;

  /// <summary>Writes every chunk's bitmap and free count from a set of used blocks.</summary>
  private static void WriteBitmaps(byte[] image, int sm, HashSet<ulong> used) {
    var total = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(sm + 48));
    var chunks = (int)BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(sm + 56));
    var addrOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(sm + 80));

    var usedInContainer = 0UL;
    foreach (var b in used) if (b < total) usedInContainer++;
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(sm + 72), total - usedInContainer);

    var cib = (long)BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(sm + addrOffset));
    var cibAt = (int)(cib * BlockSize);
    if (cib <= 0 || cibAt + BlockSize > image.Length) { ApfsFletcher64.Stamp(image.AsSpan(sm, BlockSize)); return; }

    for (var chunk = 0; chunk < chunks; ++chunk) {
      var entry = cibAt + 40 + chunk * 32;
      if (entry + 32 > image.Length) break;

      var first = (long)chunk * BlocksPerChunk;
      var count = Math.Min(BlocksPerChunk, (long)total - first);
      var bitmap = (long)BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(entry + 24));
      var bitmapAt = (int)(bitmap * BlockSize);
      if (bitmap <= 0 || bitmapAt + BlockSize > image.Length) continue;

      image.AsSpan(bitmapAt, BlockSize).Clear();
      var inChunk = 0L;
      for (var i = 0L; i < count; ++i) {
        if (!used.Contains((ulong)(first + i))) continue;
        image[bitmapAt + (int)(i >> 3)] |= (byte)(1 << (int)(i & 7));
        inChunk++;
      }
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(entry + 20), (uint)(count - inChunk));
    }

    ApfsFletcher64.Stamp(image.AsSpan(cibAt, BlockSize));
    ApfsFletcher64.Stamp(image.AsSpan(sm, BlockSize));
  }

  /// <summary>Where the space manager sits, found through the checkpoint map.</summary>
  private static long FindSpaceman(byte[] image) {
    var descBase = BinaryPrimitives.ReadInt64LittleEndian(image.AsSpan(112));
    if (descBase <= 0) return -1;

    var map = (int)(descBase * BlockSize);
    if (map + BlockSize > image.Length) return -1;

    var count = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(map + 36));
    for (var i = 0; i < count; ++i) {
      var at = map + 40 + i * 40;
      if (at + 40 > image.Length) break;
      if (BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(at))
          != (OBJECT_TYPE_SPACEMAN | OBJ_EPHEMERAL)) continue;
      return (long)BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(at + 32));
    }
    return -1;
  }

  /// <summary>
  /// Moves every ephemeral object the checkpoint names into a new transaction.
  /// </summary>
  /// <remarks>
  /// They live outside the trees a mutation rebuilds, so nothing else touches
  /// them — and a container whose space manager still belongs to the transaction
  /// before last is one apfsprogs calls "not part of latest transaction".
  /// </remarks>
  internal static void RestampEphemeral(byte[] image, ulong newXid) {
    var descBase = BinaryPrimitives.ReadInt64LittleEndian(image.AsSpan(112));
    if (descBase <= 0) return;

    var map = (int)(descBase * BlockSize);
    if (map + BlockSize > image.Length) return;

    var count = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(map + 36));
    for (var i = 0; i < count; ++i) {
      var at = map + 40 + i * 40;
      if (at + 40 > image.Length) break;

      var paddr = (long)BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(at + 32));
      var objectAt = (int)(paddr * BlockSize);
      if (paddr <= 0 || objectAt + BlockSize > image.Length) continue;

      BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(objectAt + 16), newXid);
      ApfsFletcher64.Stamp(image.AsSpan(objectAt, BlockSize));
    }
  }

  /// <summary>The first block past everything currently in use.</summary>
  /// <remarks>
  /// A container with room left in it should be filled before it is made bigger:
  /// growing it past the block count its own superblock declares produces a
  /// volume whose blocks are outside itself.
  /// </remarks>
  internal static ulong FirstFreeBlock(byte[] image, ulong fallback) {
    var spaceman = FindSpaceman(image);
    if (spaceman < 0) return fallback;

    var sm = (int)(spaceman * BlockSize);
    if (sm + 400 > image.Length) return fallback;

    // Past the last block in use, read out of the bitmap. Not the first free one:
    // once anything has been removed the free space is in pieces, and a file
    // needing several blocks in a row would be laid straight over whatever
    // follows the first gap. Starting past everything cannot collide, and the
    // bitmap written afterwards still says truthfully which blocks are free.
    var total = (long)BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(sm + 48));
    var chunks = (int)BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(sm + 56));
    var addrOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(sm + 80));

    var cib = (long)BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(sm + addrOffset));
    var cibAt = (int)(cib * BlockSize);
    if (cib <= 0 || cibAt + BlockSize > image.Length) return fallback;

    var highest = -1L;
    for (var chunk = 0; chunk < chunks; ++chunk) {
      var entry = cibAt + 40 + chunk * 32;
      if (entry + 32 > image.Length) break;

      var first = (long)chunk * BlocksPerChunk;
      var count = Math.Min(BlocksPerChunk, total - first);
      var bitmap = (long)BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(entry + 24));
      var bitmapAt = (int)(bitmap * BlockSize);
      if (bitmap <= 0 || bitmapAt + BlockSize > image.Length) continue;

      for (var i = count - 1; i >= 0; --i)
        if ((image[bitmapAt + (int)(i >> 3)] & (1 << (int)(i & 7))) != 0) {
          highest = Math.Max(highest, first + i);
          break;
        }
    }
    return highest < 0 ? fallback : (ulong)(highest + 1);
  }

  /// <summary>
  /// Writes the allocation bitmap from what the container actually holds: the
  /// metadata it always keeps, plus the blocks named here.
  /// </summary>
  /// <remarks>
  /// A high-water mark will not do once anything has been removed. Blocks that
  /// no tree names any more have to read as free, or the bitmap says the volume
  /// is fuller than it is — which is what apfsprogs calls a "bad allocation
  /// bitmap".
  /// </remarks>
  internal static void MarkUsedSet(byte[] image, IEnumerable<ulong> blocks) {
    var spaceman = FindSpaceman(image);
    if (spaceman < 0) return;

    var sm = (int)(spaceman * BlockSize);
    if (sm + 400 > image.Length) return;

    // Everything up to the end of the internal pool is the container's own and
    // is always in use.
    var ipBase = (long)BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(sm + 176));
    var ipBlocks = (long)BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(sm + 152));
    var prefix = ipBase + ipBlocks;

    var used = new HashSet<ulong>();
    for (var i = 0L; i < prefix; ++i) used.Add((ulong)i);
    foreach (var block in blocks) used.Add(block);

    WriteBitmaps(image, sm, used);
  }

  /// <summary>
  /// Records that everything below <paramref name="usedBlocks" /> is now taken.
  /// </summary>
  internal static void MarkUsed(byte[] image, ulong usedBlocks) {
    var spaceman = FindSpaceman(image);
    if (spaceman < 0) return;

    var sm = (int)(spaceman * BlockSize);
    if (sm + 400 > image.Length) return;

    var total = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(sm + 48));
    var chunks = (int)BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(sm + 56));
    var addrOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(sm + 80));
    if (usedBlocks > total) return;

    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(sm + 72), total - usedBlocks);

    var cib = (long)BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(sm + addrOffset));
    var cibAt = (int)(cib * BlockSize);
    if (cib <= 0 || cibAt + BlockSize > image.Length) { ApfsFletcher64.Stamp(image.AsSpan(sm, BlockSize)); return; }

    for (var chunk = 0; chunk < chunks; ++chunk) {
      var entry = cibAt + 40 + chunk * 32;
      if (entry + 32 > image.Length) break;

      var first = (long)chunk * BlocksPerChunk;
      var count = Math.Min(BlocksPerChunk, (long)total - first);
      var used = Math.Clamp((long)usedBlocks - first, 0, count);
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(entry + 20), (uint)(count - used));

      var bitmap = (long)BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(entry + 24));
      var bitmapAt = (int)(bitmap * BlockSize);
      if (bitmap <= 0 || bitmapAt + BlockSize > image.Length) continue;

      image.AsSpan(bitmapAt, BlockSize).Clear();
      for (var i = 0L; i < used; ++i)
        image[bitmapAt + (int)(i >> 3)] |= (byte)(1 << (int)(i & 7));
    }

    ApfsFletcher64.Stamp(image.AsSpan(cibAt, BlockSize));
    ApfsFletcher64.Stamp(image.AsSpan(sm, BlockSize));
  }
}
