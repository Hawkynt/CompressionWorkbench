#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.Qnx4;

/// <summary>
/// Moves a file's blocks inside a QNX4 volume and repoints its inode.
/// </summary>
/// <remarks>
/// <para>QNX4 keeps a file in one contiguous extent whose first block and block
/// count sit in the inode, sixteen bytes past the name. A file that outgrows one
/// extent gains further ones through an extent block, but the ordinary case is a
/// single run — and a run is exactly what the defragmenter moves, so relocating
/// a file is the copy plus a four-byte write.</para>
///
/// <para>An inode that claims more than one extent is refused: its later extents
/// live in a block this pass does not rewrite, and repointing only the first
/// would leave the rest of the file behind.</para>
/// </remarks>
public sealed class Qnx4BlockMover : IFilesystemBlockMover {

  /// <summary>Bytes per inode entry.</summary>
  private const int InodeSize = 64;

  /// <summary>Offset of the first extent's block number inside an inode.</summary>
  private const int InodeFirstExtentOffset = Qnx4Layout.InExtentBlock;

  /// <summary>Offset of the extent count inside an inode.</summary>
  private const int InodeExtraExtentsOffset = Qnx4Layout.InNumExtents;

  /// <summary>Offset of the status byte inside an inode.</summary>
  private const int InodeStatusOffset = Qnx4Layout.InStatus;

  /// <summary>
  /// The root directory, and how many blocks it spans. Block 1 is the
  /// superblock, so the directory starts after it.
  /// </summary>
  private const uint RootDirBlock = 2;
  private const uint RootDirBlocks = 4;

  /// <summary>Allocation unit in bytes.</summary>
  public int BlockSize => Qnx4Reader.BlockSize;

  /// <summary>First byte a file may occupy: past the boot block and the root cluster.</summary>
  public long FirstDataByte => (long)(RootDirBlock + RootDirBlocks) * Qnx4Reader.BlockSize;

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

    // An extent's block number counts from one, so the value in an inode is
    // one more than the block it names.
    var oldBlock = Qnx4Layout.ExtentValueFor(oldOffset / Qnx4Reader.BlockSize);
    var newBlock = Qnx4Layout.ExtentValueFor(newOffset / Qnx4Reader.BlockSize);
    if (oldBlock == newBlock) return;

    // The inode is found by the extent it still names rather than by the file's
    // name, so a duplicate name cannot send the wrong file somewhere.
    var inode = new byte[InodeSize];
    for (var b = 0u; b < RootDirBlocks; ++b) {
      for (var slot = 0; slot < Qnx4Reader.BlockSize / InodeSize; ++slot) {
        var at = (long)(RootDirBlock + b) * Qnx4Reader.BlockSize + (long)slot * InodeSize;
        if (at + InodeSize > image.Length) return;

        image.Position = at;
        image.ReadExactly(inode);
        if (!IsLive(inode[InodeStatusOffset])) continue;
        if (BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(InodeFirstExtentOffset)) != oldBlock)
          continue;

        var extraExtents = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(InodeExtraExtentsOffset));
        if (extraExtents > 0)
          throw new NotSupportedException(
            $"QNX4: '{fileName}' spans {extraExtents + 1} extents, and this pass rewrites only " +
            "the one the inode carries.");

        Span<byte> field = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(field, newBlock);
        image.Position = at + InodeFirstExtentOffset;
        image.Write(field);
        image.Flush();
        return;
      }
    }

    throw new InvalidOperationException(
      $"QNX4: no inode names block {oldBlock}, so '{fileName}' cannot be repointed.");
  }

  /// <summary>Whether an inode's status byte marks it as in use.</summary>
  private static bool IsLive(byte status) => (status & 0x08) != 0 || (status & 0x04) != 0;
}
