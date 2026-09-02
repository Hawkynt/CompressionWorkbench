#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.Cromemco;

/// <summary>
/// Moves a file inside a Cromemco RDOS volume and repoints its directory entry.
/// </summary>
/// <remarks>
/// A file here is one contiguous run of sectors, and the directory entry that
/// names it carries the sector that run starts at. Relocating a file is the copy
/// plus a two-byte write, which is what lets the defragmenter plan moves instead
/// of reading every file out and laying a fresh volume down.
/// </remarks>
public sealed class CromemcoBlockMover : IFilesystemBlockMover {

  /// <summary>Offset of the start-sector field inside a directory entry.</summary>
  private const int EntryStartSectorOffset = 12;

  private long _dataStart;

  /// <summary>Notes where file data starts on this volume.</summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var reader = new CromemcoReader(image);
    var lowest = long.MaxValue;
    foreach (var entry in reader.Entries)
      lowest = Math.Min(lowest, (long)entry.StartBlock * CromemcoReader.SectorSize);
    this._dataStart = lowest == long.MaxValue
      ? CromemcoReader.DirectoryOffset
      : lowest;
  }

  /// <summary>Allocation unit in bytes.</summary>
  public int BlockSize => CromemcoReader.SectorSize;

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

    var oldStart = (ushort)(oldOffset / CromemcoReader.SectorSize);
    var newStart = (ushort)(newOffset / CromemcoReader.SectorSize);
    if (oldStart == newStart) return;

    // The directory is a dense array at a fixed offset, so the entry to patch
    // is the one whose start sector still names where the file used to be —
    // matching on that rather than on the name keeps a duplicate name from
    // repointing the wrong entry.
    var entry = new byte[CromemcoReader.EntrySize];
    for (var index = 0; ; ++index) {
      var at = (long)CromemcoReader.DirectoryOffset + (long)index * CromemcoReader.EntrySize;
      if (at + CromemcoReader.EntrySize > image.Length) break;

      image.Position = at;
      image.ReadExactly(entry);
      if (entry[0] == 0xE5) continue;                       // deleted slot
      if (BinaryPrimitives.ReadUInt16LittleEndian(entry.AsSpan(EntryStartSectorOffset)) != oldStart)
        continue;

      BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(EntryStartSectorOffset), newStart);
      image.Position = at + EntryStartSectorOffset;
      image.Write(entry.AsSpan(EntryStartSectorOffset, 2));
      image.Flush();
      return;
    }

    throw new InvalidOperationException(
      $"Cromemco RDOS: no directory entry starts at sector {oldStart}, so '{fileName}' cannot be repointed.");
  }
}
