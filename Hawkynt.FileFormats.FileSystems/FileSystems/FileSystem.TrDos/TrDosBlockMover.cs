#pragma warning disable CS1591
using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileSystem.TrDos;

/// <summary>
/// In-place TR-DOS block mover. Moves sector-aligned extents within a TR-DOS
/// image and patches the directory entry's start-sector/start-track fields so
/// the file remains reachable at its new location.
///
/// <para>TR-DOS files are stored contiguously. Each 16-byte directory entry at
/// track 0 records: filename (8 bytes), type (1), params (4), sector count (1),
/// start sector (1), start track (1). Updating the start sector+track pair is
/// sufficient to redirect the file.</para>
/// </summary>
public sealed class TrDosBlockMover : IFilesystemBlockMover {

  private const int SectorSize = 256;
  private const int SectorsPerTrack = 16;
  private const int DirEntrySize = 16;
  private const int MaxDirEntries = 128;
  private const int DirSectorCount = 8;
  private const byte DeletedMarker = 0x01;

  /// <summary>Byte offset where the data region begins (past directory + disk-info sector).</summary>
  public long DataOrigin => (DirSectorCount + 1) * SectorSize; // sector 9

  /// <summary>Allocation unit size (one 256-byte sector).</summary>
  public int UnitSize => SectorSize;

  /// <summary>Converts a byte offset to a linear sector number.</summary>
  public int OffsetToSector(long offset) => (int)(offset / SectorSize);

  /// <summary>Converts a linear sector number to a byte offset.</summary>
  public long SectorToOffset(int sector) => (long)sector * SectorSize;

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
    var oldSector = OffsetToSector(oldOffset);
    var newSector = OffsetToSector(newOffset);

    // Read directory (sectors 0..7).
    var dir = new byte[DirSectorCount * SectorSize];
    image.Position = 0;
    image.ReadExactly(dir);

    // Sanitize fileName for comparison (8 chars, space-padded).
    var needle = SanitizeName(fileName);

    for (var i = 0; i < MaxDirEntries; i++) {
      var off = i * DirEntrySize;
      var b0 = dir[off];
      if (b0 == 0x00) break;        // end of directory
      if (b0 == DeletedMarker) continue;

      var startSec = dir[off + 14];
      var startTrk = dir[off + 15];
      var entryLinear = startTrk * SectorsPerTrack + startSec;

      var entryName = Encoding.ASCII.GetString(dir, off, 8).TrimEnd();

      if (entryLinear == oldSector &&
          (string.Equals(entryName, needle, StringComparison.Ordinal) ||
           fileName == "*")) {
        // Patch start sector and track.
        dir[off + 14] = (byte)(newSector % SectorsPerTrack);
        dir[off + 15] = (byte)(newSector / SectorsPerTrack);

        // Write back only the affected directory sector.
        var sectorIdx = off / SectorSize;
        image.Position = sectorIdx * SectorSize;
        image.Write(dir, sectorIdx * SectorSize, SectorSize);
        // Crash barrier: metadata commit durable before return.
        image.Flush();
        return;
      }
    }
  }

  private static string SanitizeName(string raw) {
    var s = Path.GetFileNameWithoutExtension(raw ?? "");
    if (string.IsNullOrEmpty(s)) s = "FILE";
    if (s.Length > 8) s = s[..8];
    return s.TrimEnd();
  }
}
