#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.Erofs;

/// <summary>
/// Moves a file's blocks inside an EROFS image and repoints its inode.
/// </summary>
/// <remarks>
/// <para>EROFS lays a file's blocks out contiguously from the raw block address
/// in its inode, so relocating a file is the copy plus one four-byte write. A
/// short file whose tail is stored inline with its inode has no run of its own
/// and needs none.</para>
///
/// <para>The inode is found by the block it still names rather than by the
/// file's path, so two entries pointing at the same run cannot send the wrong
/// one somewhere.</para>
/// </remarks>
public sealed class ErofsBlockMover : IFilesystemBlockMover {

  /// <summary>Byte offset of the superblock inside the image.</summary>
  private const int SuperblockOffset = 1024;

  /// <summary>Bytes one inode slot occupies in the metadata area.</summary>
  private const int InodeSlotSize = 32;

  /// <summary>Offset of the raw block address inside an inode, compact or extended.</summary>
  private const int InodeRawBlockAddress = 16;

  private int _blockSize;
  private long _metaOffset;
  private long _firstDataByte;

  /// <summary>Reads the geometry and where the metadata area starts.</summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);

    var superblock = new byte[128];
    if (image.Length < SuperblockOffset + superblock.Length)
      throw new InvalidDataException("EROFS: the image is too short to hold a superblock.");
    image.Position = SuperblockOffset;
    image.ReadExactly(superblock);

    var blkszbits = superblock[12];
    if (blkszbits is < 9 or > 16)
      throw new InvalidDataException($"EROFS: implausible blkszbits {blkszbits}.");
    this._blockSize = 1 << blkszbits;

    var metaBlkAddr = BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(40));
    this._metaOffset = (long)metaBlkAddr * this._blockSize;

    image.Position = 0;
    var reader = new ErofsReader(image);
    var first = long.MaxValue;
    foreach (var entry in reader.Entries)
      if (reader.TryGetDataExtent(entry, out var offset, out _))
        first = Math.Min(first, offset);
    this._firstDataByte = first == long.MaxValue ? image.Length : first;
  }

  /// <summary>Block size in bytes, as the superblock's blkszbits gives it.</summary>
  public int BlockSize => this._blockSize;

  /// <summary>First byte a file may occupy: past the superblock and the inode area.</summary>
  public long FirstDataByte => this._firstDataByte;

  /// <inheritdoc />
  /// <summary>
  /// A run may be held outside the volume while the rest of the layout moves,
  /// which is what lets a full volume be rearranged at all.
  /// </summary>
  public bool SupportsHeldRuns => true;

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
  /// <summary>
  /// Performs the update allocation after move operation.
  /// </summary>
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(fileName);
    if (this._blockSize == 0) this.Init(image);

    if (newOffset % this._blockSize != 0)
      throw new NotSupportedException(
        $"EROFS: {newOffset} is not on a {this._blockSize}-byte block boundary, which is all an " +
        "inode's raw block address can name.");

    var oldBlock = (uint)(oldOffset / this._blockSize);
    var newBlock = (uint)(newOffset / this._blockSize);
    if (oldBlock == newBlock) return;

    image.Position = 0;
    var reader = new ErofsReader(image);
    foreach (var entry in reader.Entries) {
      if (!reader.TryGetDataExtent(entry, out var offset, out _)) continue;
      if (offset != oldOffset) continue;

      var at = this._metaOffset + (long)(entry.Nid * InodeSlotSize) + InodeRawBlockAddress;
      Span<byte> field = stackalloc byte[4];
      BinaryPrimitives.WriteUInt32LittleEndian(field, newBlock);
      image.Position = at;
      image.Write(field);
      image.Flush();
      return;
    }

    throw new InvalidOperationException(
      $"EROFS: no inode names block {oldBlock}, so '{fileName}' cannot be repointed.");
  }
}
