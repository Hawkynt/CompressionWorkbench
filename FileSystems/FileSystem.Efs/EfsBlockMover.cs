#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.Efs;

/// <summary>
/// Moves a file's blocks inside an EFS volume and repoints its inode.
/// </summary>
/// <remarks>
/// A file here is described by one extent in its inode, and the extent's block
/// number is the only record of where the file is. Relocating it is the copy
/// plus a three-byte write — EFS stores that block number in 24 bits — which is
/// what lets the defragmenter plan moves rather than lay a fresh volume down.
/// </remarks>
public sealed class EfsBlockMover : IFilesystemBlockMover {

  /// <summary>Offset of the extent's block number inside an inode.</summary>
  private const int InodeExtentBlockOffset = 33;

  private long _dataStart;

  /// <summary>Notes where file data may start, past the inode table.</summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var reader = new EfsReader(image);
    var lowest = long.MaxValue;
    foreach (var entry in reader.Entries)
      if (entry.FirstBlock > 0)
        lowest = Math.Min(lowest, (long)entry.FirstBlock * EfsWriter.BasicBlock);
    this._dataStart = lowest == long.MaxValue ? EfsWriter.BasicBlock : lowest;
  }

  /// <summary>Allocation unit in bytes.</summary>
  public int BlockSize => EfsWriter.BasicBlock;

  /// <summary>First byte a file may occupy.</summary>
  public long FirstDataByte => this._dataStart;

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
  /// <summary>
  /// Performs the update allocation after move operation.
  /// </summary>
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(fileName);

    image.Position = 0;
    var reader = new EfsReader(image);
    var inode = -1;
    foreach (var entry in reader.Entries)
      if (string.Equals(entry.Name, fileName, StringComparison.OrdinalIgnoreCase)) {
        inode = entry.Inode;
        break;
      }
    if (inode <= 0)
      throw new InvalidOperationException($"EFS: no inode for '{fileName}'.");

    var inodesPerBlock = EfsWriter.BasicBlock / EfsWriter.InodeSize;
    // Inode n sits at block n/4 of the table; 0 and 1 are reserved slots.
    var blockOffset = inode / inodesPerBlock;
    var slotOffset = inode % inodesPerBlock;
    var at = ((long)EfsWriter.InodeTableOffset + blockOffset) * EfsWriter.BasicBlock
           + (long)slotOffset * EfsWriter.InodeSize;

    // ex_bn is 24 bits, big-endian, inside the 8-byte extent descriptor.
    var newBlock = newOffset / EfsWriter.BasicBlock;
    if (newBlock > 0xFFFFFF)
      throw new NotSupportedException(
        $"EFS: block {newBlock:N0} is past the 24 bits an extent's block number holds.");

    Span<byte> field = [(byte)(newBlock >> 16), (byte)(newBlock >> 8), (byte)newBlock];
    image.Position = at + InodeExtentBlockOffset;
    image.Write(field);
    image.Flush();
  }
}
