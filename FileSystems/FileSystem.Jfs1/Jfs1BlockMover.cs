#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.Jfs1;

/// <summary>
/// Moves a file's blocks inside a JFS1 volume and repoints its inode.
/// </summary>
/// <remarks>
/// A file here is one contiguous run, and the only record of where it starts is
/// a single field in its inode. There is no chain to relink, so relocating a
/// file is the copy plus one write — which is what lets the defragmenter plan
/// moves rather than read every file out and lay a fresh volume down.
/// </remarks>
public sealed class Jfs1BlockMover : IFilesystemBlockMover {

  /// <summary>Offset of the first-block field inside an inode.</summary>
  private const int InodeFirstBlockOffset = 16;

  private long _dataStart;
  private int _blockSize = Jfs1Writer.DefaultBlockSize;

  /// <summary>
  /// Notes the volume's block size and where file data may start. JFS1 records
  /// the block size in its superblock rather than fixing it, so it is read
  /// rather than assumed.
  /// </summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var header = new byte[(int)Math.Min(image.Length, 8192)];
    image.Position = 0;
    image.ReadExactly(header, 0, header.Length);
    var superblock = Jfs1Superblock.TryParse(header);
    this._blockSize = superblock is { BlockSize: > 0 } ? (int)superblock.BlockSize : Jfs1Writer.DefaultBlockSize;

    image.Position = 0;
    var reader = new Jfs1Reader(image);
    var lowest = long.MaxValue;
    foreach (var entry in reader.Entries)
      if (entry.FirstBlock > 0)
        lowest = Math.Min(lowest, (long)entry.FirstBlock * this._blockSize);
    this._dataStart = lowest == long.MaxValue ? this._blockSize : lowest;
  }

  /// <summary>Allocation unit in bytes.</summary>
  public int BlockSize => this._blockSize;

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
    var reader = new Jfs1Reader(image);
    var inode = -1;
    foreach (var entry in reader.Entries)
      if (string.Equals(entry.Name, fileName, StringComparison.OrdinalIgnoreCase)) {
        inode = entry.Inode;
        break;
      }
    if (inode <= 0)
      throw new InvalidOperationException($"JFS1: no inode for '{fileName}'.");

    var blockSize = this._blockSize;
    var inodesPerBlock = blockSize / Jfs1Writer.InodeSize;
    var inodeStart = 1;
    var blockOffset = (inode - 2) / inodesPerBlock;
    var slotOffset = (inode - 2) % inodesPerBlock;
    var at = ((long)inodeStart + blockOffset) * blockSize + (long)slotOffset * Jfs1Writer.InodeSize;

    var newBlock = newOffset / blockSize;
    Span<byte> field = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(field, (uint)newBlock);
    image.Position = at + InodeFirstBlockOffset;
    image.Write(field);
    image.Flush();
  }
}
