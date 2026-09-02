#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.OpenVms;

/// <summary>
/// Moves a file's blocks inside an OpenVMS volume and repoints its file header.
/// </summary>
/// <remarks>
/// <para>Files-11 describes a file with retrieval pointers in its header, and a
/// file laid out in one run has exactly one. The header knows how to serialise
/// itself — checksum included — so moving a file is the copy, a new start block
/// in that pointer, and writing the header back.</para>
///
/// <para>A file described by more than one pointer is refused: repointing the
/// first would leave the rest of the file where it was.</para>
/// </remarks>
public sealed class OpenVmsBlockMover : IFilesystemBlockMover {

  /// <summary>Allocation unit in bytes.</summary>
  public int BlockSize => OpenVmsLayout.BlockSize;

  /// <summary>First byte a file may occupy: past the volume's own structures.</summary>
  public long FirstDataByte => OpenVmsLayout.MetadataBytes;

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

    var oldLbn = (int)(oldOffset / OpenVmsLayout.BlockSize);
    var newLbn = (int)(newOffset / OpenVmsLayout.BlockSize);
    if (oldLbn == newLbn) return;

    var block = new byte[OpenVmsLayout.BlockSize];
    for (var fid = OpenVmsLayout.FirstUserFileId; fid <= OpenVmsLayout.MaxFiles; ++fid) {
      var at = OpenVmsLayout.FileHeaderByteOffset(fid);
      if (at + OpenVmsLayout.BlockSize > image.Length) break;

      image.Position = at;
      image.ReadExactly(block);

      OpenVmsFileHeader header;
      try {
        header = OpenVmsFileHeader.Deserialize(block);
      } catch (Exception e) when (e is InvalidDataException or ArgumentOutOfRangeException) {
        continue;
      }
      if (!header.InUse || header.Extents.Count == 0) continue;

      // The header is found by the block it still names rather than by the
      // file's name, so a duplicate name cannot send the wrong file somewhere.
      if (header.Extents[0].StartLbn != oldLbn) continue;
      if (header.Extents.Count > 1)
        throw new NotSupportedException(
          $"OpenVMS: '{fileName}' is described by {header.Extents.Count} retrieval pointers, " +
          "which this pass cannot restate.");

      var pointer = header.Extents[0];
      header.Extents[0] = new OpenVmsFileHeader.RetrievalPointer(newLbn, pointer.Count);

      // Serialize rewrites the header's checksum along with the pointer.
      image.Position = at;
      image.Write(header.Serialize());
      image.Flush();
      return;
    }

    throw new InvalidOperationException(
      $"OpenVMS: no file header names block {oldLbn}, so '{fileName}' cannot be repointed.");
  }
}
