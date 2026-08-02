#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.BcacheFs;

/// <summary>
/// Moves a file's blocks inside the payload area of a bcachefs image and
/// repoints its directory entry.
/// </summary>
/// <remarks>
/// <para>A file in the payload area is one contiguous run of blocks, and the
/// chained directory ahead of it records where that run starts in a single
/// eight-byte field. Nothing else notes the position — the superblock describes
/// the device, not which of it is taken — so relocating a file is the copy plus
/// that field.</para>
///
/// <para>The entry is found by the block it still names rather than by the
/// file's name, so two entries sharing a name cannot send the wrong one
/// somewhere.</para>
/// </remarks>
public sealed class BcacheFsBlockMover : IFilesystemBlockMover {

  private readonly List<long> _directoryBlocks = [];
  private long _firstDataByte;

  /// <summary>Walks the directory chain so its entries can be found again.</summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    this._directoryBlocks.Clear();

    var marker = new byte[32];
    image.Position = BcacheFsWriter.PayloadMarkerOffset;
    image.ReadExactly(marker);
    if (!marker.AsSpan(0, BcacheFsWriter.PayloadMarker.Length)
        .SequenceEqual(BcacheFsWriter.PayloadMarker))
      throw new NotSupportedException(
        "bcachefs: this image carries no payload directory, so nothing records where a file starts.");

    var block = BinaryPrimitives.ReadInt64LittleEndian(
      marker.AsSpan((int)(BcacheFsWriter.PayloadDirOffset - BcacheFsWriter.PayloadMarkerOffset), 8));

    var head = new byte[BcacheFsWriter.DirHeadSize];
    var seen = new HashSet<long>();
    while (block != 0 && seen.Add(block)) {
      var at = block * BcacheFsWriter.PayloadBlockSize;
      if (at < 0 || at + BcacheFsWriter.PayloadBlockSize > image.Length) break;

      image.Position = at;
      image.ReadExactly(head);
      if (!head.AsSpan(0, BcacheFsWriter.DirMagic.Length).SequenceEqual(BcacheFsWriter.DirMagic)) break;

      this._directoryBlocks.Add(block);
      block = BinaryPrimitives.ReadInt64LittleEndian(head.AsSpan(8, 8));
    }

    if (this._directoryBlocks.Count == 0)
      throw new InvalidDataException("bcachefs: the payload directory does not parse.");

    // A file starts past the last directory block: the writer lays the chain
    // down first and allocates from the block after it.
    this._firstDataByte = (this._directoryBlocks.Max() + 1) * (long)BcacheFsWriter.PayloadBlockSize;
  }

  /// <summary>The payload area's block. A directory entry names a block, not a byte.</summary>
  public int BlockSize => BcacheFsWriter.PayloadBlockSize;

  /// <summary>First byte a file may occupy: past the superblocks and the directory chain.</summary>
  public long FirstDataByte => this._firstDataByte;

  /// <inheritdoc />
  /// <summary>
  /// A run may be held outside the volume while the rest of the layout moves,
  /// which is what lets a full volume be rearranged at all.
  /// </summary>
  public bool SupportsHeldRuns => true;

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
    if (this._directoryBlocks.Count == 0) this.Init(image);

    if (newOffset % BcacheFsWriter.PayloadBlockSize != 0)
      throw new NotSupportedException(
        $"bcachefs: {newOffset} is not on a {BcacheFsWriter.PayloadBlockSize}-byte block boundary, " +
        "which is all a directory entry can name.");

    var oldBlock = oldOffset / BcacheFsWriter.PayloadBlockSize;
    var newBlock = newOffset / BcacheFsWriter.PayloadBlockSize;
    if (oldBlock == newBlock) return;

    Span<byte> field = stackalloc byte[8];
    foreach (var directoryBlock in this._directoryBlocks) {
      var blockAt = directoryBlock * (long)BcacheFsWriter.PayloadBlockSize;
      for (var i = 0; i < BcacheFsWriter.DirEntriesPerBlock; ++i) {
        var at = blockAt + BcacheFsWriter.DirHeadSize + (long)i * BcacheFsWriter.DirEntrySize
               + BcacheFsWriter.DirNameLength;
        if (at + 8 > image.Length) break;

        image.Position = at;
        image.ReadExactly(field);
        if (BinaryPrimitives.ReadInt64LittleEndian(field) != oldBlock) continue;

        BinaryPrimitives.WriteInt64LittleEndian(field, newBlock);
        image.Position = at;
        image.Write(field);
        image.Flush();
        return;
      }
    }

    throw new InvalidOperationException(
      $"bcachefs: no directory entry names block {oldBlock}, so '{fileName}' cannot be repointed.");
  }
}
