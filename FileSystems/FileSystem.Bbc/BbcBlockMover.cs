#pragma warning disable CS1591
using System.Buffers;
using System.Text;
using Compression.Registry;

namespace FileSystem.Bbc;

/// <summary>
/// In-place BBC Micro DFS block mover. Moves sector-aligned extents within
/// an SSD image and patches the catalog entry's start-sector field. BBC DFS
/// files are contiguous, so a move simply updates the start-sector in the
/// two-sector catalog (sectors 0 and 1).
/// </summary>
public sealed class BbcBlockMover : IFilesystemBlockMover {

  private const int SectorSize = 256;

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
    image.Position = 0;
    using var ms = new MemoryStream();
    image.CopyTo(ms);
    var data = ms.ToArray();

    var oldStartSector = (int)(oldOffset / SectorSize);
    var newStartSector = (int)(newOffset / SectorSize);

    // BBC DFS catalog: sector 0 holds filenames (8 bytes per entry starting at offset 8),
    // sector 1 holds metadata (8 bytes per entry starting at offset 8).
    // Entry count is at sec1[5]/8.
    var count = data[SectorSize + 5] / 8;
    if (count > 31) count = 31;

    for (var i = 0; i < count; i++) {
      var nameOff = 8 + i * 8;
      var metaOff = SectorSize + 8 + i * 8;

      // Decode entry start-sector.
      var packed = data[metaOff + 6];
      var startLo = data[metaOff + 7];
      var startHi = packed & 0x03;
      var entrySector = (startHi << 8) | startLo;

      // Match by start-sector (more reliable than name, since BBC names are 7 chars).
      if (entrySector != oldStartSector) continue;

      // Optionally verify name matches.
      if (!string.Equals(fileName, "*", StringComparison.Ordinal)) {
        var nameBuf = Encoding.ASCII.GetString(data, nameOff, 7).TrimEnd();
        var dirByte = data[nameOff + 7];
        var dir = (char)(dirByte & 0x7F);
        var fullName = $"{dir}.{nameBuf}";
        var bareName = nameBuf;
        if (!string.Equals(bareName, fileName, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(fullName, fileName, StringComparison.OrdinalIgnoreCase))
          continue;
      }

      // Patch start-sector to new location.
      data[metaOff + 7] = (byte)(newStartSector & 0xFF);
      data[metaOff + 6] = (byte)((packed & 0xFC) | ((newStartSector >> 8) & 0x03));
    }

    // BBC DFS has no free-space bitmap — free space is implicitly
    // "everything not covered by a catalog entry". No bitmap to update.

    image.Position = 0;
    image.Write(data, 0, data.Length);
    // Crash barrier: metadata commit durable before return.
    image.Flush();
  }
}
