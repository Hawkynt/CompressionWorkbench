#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.Gfs1;

/// <summary>
/// Moves a file's blocks inside a GFS1 volume and repoints its inode.
/// </summary>
/// <remarks>
/// A file here is one contiguous run, and the only record of where it starts is
/// a single field in its inode. There is no chain to relink, so relocating a
/// file is the copy plus one write — which is what lets the defragmenter plan
/// moves rather than read every file out and lay a fresh volume down.
/// </remarks>
public sealed class Gfs1BlockMover : IFilesystemBlockMover {

  /// <summary>Offset of the first-block field inside an inode.</summary>
  private const int InodeFirstBlockOffset = 24;

  private long _dataStart;

  /// <summary>Notes where file data may start, past the inode table.</summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var reader = new Gfs1Reader(image);
    var lowest = long.MaxValue;
    foreach (var entry in reader.Entries)
      if (entry.FirstBlock > 0)
        lowest = Math.Min(lowest, (long)entry.FirstBlock * Gfs1Writer.BlockSize);
    this._dataStart = lowest == long.MaxValue ? Gfs1Writer.BlockSize : lowest;
  }

  /// <summary>Allocation unit in bytes.</summary>
  public int BlockSize => Gfs1Writer.BlockSize;

  /// <summary>First byte a file may occupy.</summary>
  public long FirstDataByte => this._dataStart;

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

    image.Position = 0;
    var reader = new Gfs1Reader(image);
    var inode = -1;
    foreach (var entry in reader.Entries)
      if (string.Equals(entry.Name, fileName, StringComparison.OrdinalIgnoreCase)) {
        inode = entry.Inode;
        break;
      }
    if (inode <= 0)
      throw new InvalidOperationException($"GFS1: no inode for '{fileName}'.");

    var blockSize = Gfs1Writer.BlockSize;
    var inodesPerBlock = blockSize / Gfs1Writer.InodeSize;
    var inodeStart = Gfs1Writer.SuperblockOffset / Gfs1Writer.BlockSize + 1;
    var blockOffset = (inode - 2) / inodesPerBlock;
    var slotOffset = (inode - 2) % inodesPerBlock;
    var at = ((long)inodeStart + blockOffset) * blockSize + (long)slotOffset * Gfs1Writer.InodeSize;

    var newBlock = newOffset / blockSize;
    Span<byte> field = stackalloc byte[8];
    BinaryPrimitives.WriteUInt64BigEndian(field, (ulong)newBlock);
    image.Position = at + InodeFirstBlockOffset;
    image.Write(field);
    image.Flush();
  }
}
