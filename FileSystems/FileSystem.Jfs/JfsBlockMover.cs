#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.Jfs;

/// <summary>
/// Moves a file's extents inside a JFS volume and repoints the xad descriptors
/// that name them.
/// </summary>
/// <remarks>
/// <para>A JFS file's bytes are addressed by the extent descriptors in its
/// inode's xtree root: each one is a length and a block address packed into
/// eight bytes. Moving an extent is the copy plus that pair of words.</para>
///
/// <para>The descriptor is found by the address it still names rather than by
/// the file's name, so a file with several extents can be moved one extent at a
/// time and two files sharing a leaf name cannot send the wrong one somewhere.</para>
///
/// <para>The allocation map is not touched here. It records more than a bitmap
/// — a free count per page and a tree of free-buddy exponents above it — so the
/// caller lays the whole map down again once every move has run, from the
/// allocation the moves left behind.</para>
/// </remarks>
public sealed class JfsBlockMover : IFilesystemBlockMover {

  private int _blockSize;
  private long _firstDataByte;
  private int _usableBlocks;

  /// <summary>Reads the geometry and where file data may start.</summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);

    image.Position = 0;
    using var reader = new JfsReader(image);
    this._blockSize = reader.BlockSize;
    if (this._blockSize <= 0)
      throw new InvalidDataException("JFS: the superblock does not name a block size.");

    var superblock = new byte[16];
    image.Position = 0x8000;
    image.ReadExactly(superblock);
    var sizeInHardwareBlocks = (long)BinaryPrimitives.ReadUInt64LittleEndian(superblock.AsSpan(8));
    var usable = sizeInHardwareBlocks / (this._blockSize / JfsWriter.SectorSize);
    var volumeBlocks = image.Length / this._blockSize;
    this._usableBlocks = (int)(usable > 0 && usable <= volumeBlocks ? usable : volumeBlocks);

    var first = long.MaxValue;
    foreach (var entry in reader.Entries) {
      if (entry.IsDirectory) continue;
      foreach (var (offset, length, _) in reader.EnumerateDataExtents(entry))
        if (length > 0) first = Math.Min(first, offset);
    }
    this._firstDataByte = first == long.MaxValue
      ? (long)JfsWriter.BlockSize * 32
      : first;
  }

  /// <summary>Block size in bytes, as the superblock records it.</summary>
  public int BlockSize => this._blockSize;

  /// <summary>
  /// Blocks the aggregate uses. The fsck workspace and the log sit past them
  /// and the allocation map does not describe them.
  /// </summary>
  public int UsableBlocks => this._usableBlocks;

  /// <summary>First byte a file may occupy: past the maps, the tables and the inodes.</summary>
  public long FirstDataByte => this._firstDataByte;

  /// <summary>
  /// Each call repoints the run it is given and nothing else, so an owner
  /// scattered over several runs is simply several calls.
  /// </summary>
  public bool RepointsRunsIndependently => true;

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
        $"JFS: {newOffset} is not on a {this._blockSize}-byte block boundary, which is all an " +
        "extent descriptor can name.");

    var newBlock = newOffset / this._blockSize;
    if (oldOffset / this._blockSize == newBlock) return;

    long descriptorOffset;
    {
      image.Position = 0;
      using var reader = new JfsReader(image);
      descriptorOffset = -1;
      foreach (var entry in reader.Entries) {
        if (entry.IsDirectory) continue;
        foreach (var (offset, _, descriptor) in reader.EnumerateDataExtents(entry))
          if (offset == oldOffset) { descriptorOffset = descriptor; break; }
        if (descriptorOffset >= 0) break;
      }
    }

    if (descriptorOffset < 0)
      throw new InvalidOperationException(
        $"JFS: no extent descriptor names {oldOffset}, so '{fileName}' cannot be repointed.");

    // pxd_t: len_addr (le32) holds the length in its low 24 bits and the high
    // eight bits of the address in the top byte; addr2 (le32) holds the rest of
    // the address. The length is left exactly as it was.
    var pxd = new byte[8];
    image.Position = descriptorOffset;
    image.ReadExactly(pxd);
    var extentLength = BinaryPrimitives.ReadUInt32LittleEndian(pxd) & 0xFFFFFFu;

    if ((ulong)newBlock > 0xFF_FFFF_FFFFUL)
      throw new NotSupportedException(
        $"JFS: block {newBlock} is past the 40 bits an extent descriptor holds.");

    BinaryPrimitives.WriteUInt32LittleEndian(pxd,
      extentLength | (uint)((newBlock >> 32) << 24));
    BinaryPrimitives.WriteUInt32LittleEndian(pxd.AsSpan(4), (uint)(newBlock & 0xFFFFFFFF));
    image.Position = descriptorOffset;
    image.Write(pxd);
    image.Flush();
  }
}
