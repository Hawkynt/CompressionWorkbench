#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.Ti99;

/// <summary>
/// Moves a file's sectors inside a TI-99 volume and repoints its descriptor.
/// </summary>
/// <remarks>
/// <para>A TI-99 disk keeps a file's whereabouts in a File Descriptor Record —
/// its own sector, reached through the directory at sector 1. The record's
/// cluster list starts at offset 0x1C and packs a start sector and an offset
/// into three bytes: the low eight bits of the start, then its top four bits
/// sharing a byte with the offset's low nibble.</para>
///
/// <para>A file laid out in one run has one such entry, and moving it is the
/// copy plus repacking those bytes. A file with more than one is refused —
/// rewriting a chain of them is a different operation.</para>
/// </remarks>
public sealed class Ti99BlockMover : IFilesystemBlockMover {

  /// <summary>Sector holding the directory of descriptor pointers.</summary>
  private const int DirectorySector = 1;

  /// <summary>Descriptor pointers in the directory sector.</summary>
  private const int DirectoryEntries = 128;

  /// <summary>Offset of the cluster list inside a descriptor.</summary>
  private const int FdrClusterListOffset = 0x1C;

  /// <summary>Offset of the total-sectors field inside a descriptor.</summary>
  private const int FdrTotalSectorsOffset = 0x0E;

  /// <summary>Allocation unit in bytes.</summary>
  public int BlockSize => Ti99Reader.SectorSize;

  /// <summary>First byte a file may occupy: past the volume block and directory.</summary>
  public long FirstDataByte => 2L * Ti99Reader.SectorSize;

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

    var oldStart = (int)(oldOffset / Ti99Reader.SectorSize);
    var newStart = (int)(newOffset / Ti99Reader.SectorSize);
    if (oldStart == newStart) return;

    var directory = new byte[Ti99Reader.SectorSize];
    image.Position = (long)DirectorySector * Ti99Reader.SectorSize;
    image.ReadExactly(directory);

    var descriptor = new byte[Ti99Reader.SectorSize];
    for (var i = 0; i < DirectoryEntries; ++i) {
      var descriptorSector = BinaryPrimitives.ReadUInt16BigEndian(directory.AsSpan(i * 2));
      if (descriptorSector == 0) continue;

      var at = (long)descriptorSector * Ti99Reader.SectorSize;
      if (at + Ti99Reader.SectorSize > image.Length) continue;

      image.Position = at;
      image.ReadExactly(descriptor);

      // The descriptor is found by the sector it still names, so a duplicate
      // name cannot send the wrong file somewhere.
      var start = descriptor[FdrClusterListOffset]
                | ((descriptor[FdrClusterListOffset + 1] & 0x0F) << 8);
      if (start != oldStart) continue;

      var totalSectors = BinaryPrimitives.ReadUInt16BigEndian(descriptor.AsSpan(FdrTotalSectorsOffset));
      var second = descriptor[FdrClusterListOffset + 3];
      if (second != 0)
        throw new NotSupportedException(
          $"TI-99: '{fileName}' is laid out in more than one cluster entry, which this pass " +
          "cannot restate.");

      // Repack: low eight bits of the start, then its top four bits in the low
      // nibble of the next byte, leaving that byte's offset nibble alone.
      descriptor[FdrClusterListOffset] = (byte)(newStart & 0xFF);
      descriptor[FdrClusterListOffset + 1] =
        (byte)((descriptor[FdrClusterListOffset + 1] & 0xF0) | ((newStart >> 8) & 0x0F));

      image.Position = at + FdrClusterListOffset;
      image.Write(descriptor.AsSpan(FdrClusterListOffset, 2));
      image.Flush();
      _ = totalSectors;
      return;
    }

    throw new InvalidOperationException(
      $"TI-99: no descriptor names sector {oldStart}, so '{fileName}' cannot be repointed.");
  }
}
