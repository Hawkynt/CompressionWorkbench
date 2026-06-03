#pragma warning disable CS1591
using static FileSystem.Apfs.ApfsConstants;

namespace FileSystem.Apfs;

/// <summary>
/// Simple tail-allocating block allocator for the in-place modifier.
/// <para>
/// The <see cref="ApfsWriter"/> never emits a spaceman / chunk-info-addr tree —
/// it always packs blocks linearly from the start of the image and grows the
/// image to satisfy demand. The modifier mirrors that policy: every new B-tree
/// node, every internal split, and every file extent is allocated from the
/// current image tail, the image is grown to make room, and the new image
/// reference is stored back into the caller's ref-byte-array.
/// </para>
/// <para>
/// This is intentionally NOT a real spaceman (which would maintain bitmaps and
/// reuse freed blocks) — that is a multi-week project. The validator does not
/// require freed-block reuse; it only requires that every block referenced from
/// a tree is reachable on disk and every checksum is valid.
/// </para>
/// </summary>
internal sealed class ApfsBlockAllocator {
  private const uint BlockSize = DEFAULT_BLOCK_SIZE;
  private ulong _nextBlock;

  public ApfsBlockAllocator(ulong initialBlocks) { this._nextBlock = initialBlocks; }

  /// <summary>The next block number that would be allocated by a future call.</summary>
  public ulong NextBlock => this._nextBlock;

  /// <summary>
  /// Allocates one block at the image tail, growing the image to fit.
  /// Returns the block index; the caller writes node content into that block.
  /// </summary>
  public ulong AllocateNode(ref byte[] image) {
    var block = this._nextBlock;
    EnsureCapacity(ref image, block + 1);
    this._nextBlock = block + 1;
    return block;
  }

  /// <summary>
  /// Allocates a contiguous block range for file data, copies the data into the
  /// region, and returns the first block number. Grows the image as needed.
  /// </summary>
  public ulong AllocateData(ref byte[] image, int blockCount, byte[] data) {
    if (blockCount <= 0) return 0;
    var first = this._nextBlock;
    EnsureCapacity(ref image, first + (ulong)blockCount);
    Buffer.BlockCopy(data, 0, image, (int)(first * BlockSize), data.Length);
    this._nextBlock = first + (ulong)blockCount;
    return first;
  }

  /// <summary>Grows the underlying byte array so block <paramref name="endBlock"/> is addressable.</summary>
  private static void EnsureCapacity(ref byte[] image, ulong endBlock) {
    var requiredBytes = (long)endBlock * BlockSize;
    if (image.Length >= requiredBytes) return;
    var grown = new byte[requiredBytes];
    Buffer.BlockCopy(image, 0, grown, 0, image.Length);
    image = grown;
  }
}
