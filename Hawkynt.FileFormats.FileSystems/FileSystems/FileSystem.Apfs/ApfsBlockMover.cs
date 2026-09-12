#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.Apfs;

/// <summary>
/// Moves a file's blocks inside an APFS container and rewrites the extent
/// record that named them.
/// </summary>
/// <remarks>
/// <para>A file's position is one field — <c>phys_block_num</c> in its file
/// extent record — sitting in a leaf of the filesystem tree. Every block of the
/// container carries its own Fletcher-64, so rewriting that field means
/// rewriting one leaf's checksum and nothing else.</para>
///
/// <para>That is deliberately not what the in-place modifier does. It rebuilds
/// the trees and allocates the new nodes from the image's tail, which grows the
/// container — fine for adding a file, useless for laying one out again, where
/// the size is the one thing that must not change.</para>
/// </remarks>
public sealed class ApfsBlockMover : IFilesystemBlockMover {

  private ApfsLayout.Container? _container;

  /// <summary>Where the field naming each block sits, keyed by the block it names.</summary>
  private readonly Dictionary<ulong, (ulong LeafBlock, long FieldOffset)> _extentOf = [];

  /// <summary>Reads the container once and notes where every extent record is.</summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    this._container = ApfsLayout.Read(image);
    if (this._container == null)
      throw new InvalidDataException("APFS: the container is not one this reads.");

    this._extentOf.Clear();
    foreach (var extent in this._container.Extents)
      this._extentOf[extent.PhysBlock] = (extent.LeafBlock, extent.FieldOffset);
  }

  /// <summary>A block, which is what an extent record counts in.</summary>
  public int BlockSize => (int)(this._container?.BlockSize ?? 0);

  /// <summary>
  /// First byte a file may occupy: past the container's own head. Blocks past
  /// the file data that belong to the trees are described as reserved rather
  /// than kept behind this, because they are not all in one place.
  /// </summary>
  public long FirstDataByte =>
    this._container == null ? 0 : (long)this._container.FirstDataBlock * this._container.BlockSize;

  /// <summary>
  /// Each call rewrites the record naming the run it is given, so a file in
  /// several extents is simply several calls.
  /// </summary>
  public bool RepointsRunsIndependently => true;

  /// <summary>
  /// A run may be held outside the container while the rest of the layout
  /// moves, which is what lets a full container be rearranged at all.
  /// </summary>
  public bool SupportsHeldRuns => true;

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
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(fileName);
    if (this._container == null) this.Init(image);

    var blockSize = this._container!.BlockSize;
    if (newOffset % blockSize != 0)
      throw new NotSupportedException(
        $"APFS: {newOffset} is not on a {blockSize}-byte block boundary, which is all an extent " +
        "record can name.");

    var oldBlock = (ulong)(oldOffset / blockSize);
    var newBlock = (ulong)(newOffset / blockSize);
    if (oldBlock == newBlock) return;

    // Keyed by where the run STARTED, and never re-keyed. The pass names a run by
    // its original address even for one it lifted out and put back later, and by
    // the time it does something else has very likely been laid down there. Moving
    // the key to the run's new address made this index answer "who lives here now"
    // instead of "who started here": a run that landed on another's old address
    // took over that other's record, and the two files swapped contents. It only
    // shows when files are the same length, because that is when the layout has
    // reason to put one where another was.
    if (!this._extentOf.TryGetValue(oldBlock, out var extent))
      throw new InvalidOperationException(
        $"APFS: no extent record names block {oldBlock}, so '{fileName}' cannot be repointed.");

    Span<byte> field = stackalloc byte[8];
    BinaryPrimitives.WriteUInt64LittleEndian(field, newBlock);
    image.Position = extent.FieldOffset;
    image.Write(field);

    this.RewriteChecksum(image, extent.LeafBlock);
    image.Flush();
  }

  /// <summary>
  /// Takes a block's Fletcher-64 again, which is what says the block is intact.
  /// </summary>
  private void RewriteChecksum(Stream image, ulong block) {
    var blockSize = (int)this._container!.BlockSize;
    var at = (long)block * blockSize;
    if (at < 0 || at + blockSize > this._container.ImageLength) return;

    var bytes = new byte[blockSize];
    image.Position = at;
    image.ReadExactly(bytes);

    var checksum = ApfsFletcher64.Compute(bytes);
    BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0, 8), checksum);
    image.Position = at;
    image.Write(bytes, 0, 8);
  }
}
