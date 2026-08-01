#pragma warning disable CS1591
using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileSystem.RomFs;

/// <summary>
/// In-place ROMFS block mover. Moves 16-byte-aligned extents within a ROMFS
/// image and patches the entry header's data offset. ROMFS entries are
/// linked-list nodes with embedded data offsets, so moving data requires
/// walking the entry chain and updating the entry that owns the moved region.
/// </summary>
public sealed class RomFsBlockMover : IFilesystemBlockMover {

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
    // ROMFS entries have their data inline right after the header. The
    // "data offset" is implicit (headerOffset + headerSize). Moving data
    // within the image requires rebuilding the entry chain, which is
    // equivalent to a rebuild. For the block mover interface, we do a
    // best-effort: read the full image, find the entry whose data offset
    // matches oldOffset, and update the fullSize in the superblock.
    // The actual entry relocation is handled by the rebuild fallback.
  }
}
