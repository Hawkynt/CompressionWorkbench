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
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false) {
    if (length <= 0 || srcOffset == dstOffset) return;
    var buffer = ArrayPool<byte>.Shared.Rent(Math.Min((int)Math.Min(length, 64 * 1024), int.MaxValue));
    try {
      var remaining = length; var src = srcOffset; var dst = dstOffset;
      while (remaining > 0) {
        var chunk = (int)Math.Min(remaining, buffer.Length);
        image.Position = src; image.ReadExactly(buffer, 0, chunk);
        image.Position = dst; image.Write(buffer, 0, chunk);
        src += chunk; dst += chunk; remaining -= chunk;
      }
      // Crash barrier: data must land on disk before metadata references it.
      image.Flush();
      if (zeroSource) {
        Array.Clear(buffer, 0, buffer.Length);
        remaining = length; src = srcOffset;
        while (remaining > 0) {
          var chunk = (int)Math.Min(remaining, buffer.Length);
          image.Position = src; image.Write(buffer, 0, chunk);
          src += chunk; remaining -= chunk;
        }
        // Crash barrier: data must land on disk before metadata references it.
        image.Flush();
      }
    } finally { ArrayPool<byte>.Shared.Return(buffer); }
  }

  /// <inheritdoc />
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
