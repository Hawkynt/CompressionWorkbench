#pragma warning disable CS1591
using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileSystem.AppleDos;

/// <summary>
/// In-place Apple DOS 3.3 block mover. Moves sector-aligned extents
/// within a .dsk image and patches the T/S list sector pointers +
/// VTOC bitmap so the file remains reachable at its new location.
/// </summary>
public sealed class AppleDosBlockMover : IFilesystemBlockMover {

  private const int TotalTracks = 35;
  private const int SectorsPerTrack = 16;
  private const int SectorSize = 256;
  private const int CatalogTrack = 17;
  private const int VtocSector = 0;
  private const int TsListPairsPerSector = 122;

  private static long SectorOffset(int track, int sector)
    => (long)track * SectorsPerTrack * SectorSize + (long)sector * SectorSize;

  private static (int Track, int Sector) OffsetToTrackSector(long byteOffset) {
    var linearSector = (int)(byteOffset / SectorSize);
    var track = linearSector / SectorsPerTrack;
    var sector = linearSector % SectorsPerTrack;
    return (track, sector);
  }

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

    var sectorCount = (int)((length + SectorSize - 1) / SectorSize);
    var oldSectors = new List<(int Track, int Sector)>(sectorCount);
    var newSectors = new List<(int Track, int Sector)>(sectorCount);
    for (var i = 0; i < sectorCount; i++) {
      oldSectors.Add(OffsetToTrackSector(oldOffset + (long)i * SectorSize));
      newSectors.Add(OffsetToTrackSector(newOffset + (long)i * SectorSize));
    }

    // Build a mapping from old (T,S) to new (T,S) for quick lookup.
    var remap = new Dictionary<(int, int), (int, int)>(sectorCount);
    for (var i = 0; i < sectorCount; i++)
      remap[oldSectors[i]] = newSectors[i];

    // 1. Walk the VTOC to find the catalog chain head.
    var vtocOff = (int)SectorOffset(CatalogTrack, VtocSector);
    var catT = (int)data[vtocOff + 0x01];
    var catS = (int)data[vtocOff + 0x02];

    // 2. Walk the catalog chain to find the named file's T/S list pointer.
    //    Then walk the T/S list chain(s) and patch data-sector pointers.
    var visited = new HashSet<(int, int)>();
    var ct = catT; var cs = catS;
    while (ct != 0 && visited.Add((ct, cs))) {
      if (ct < 0 || ct >= TotalTracks || cs < 0 || cs >= SectorsPerTrack) break;
      var secOff = (int)SectorOffset(ct, cs);
      for (var i = 0; i < 7; i++) {
        var eo = secOff + 0x0B + i * 35;
        var firstByte = data[eo];
        if (firstByte == 0x00 || firstByte == 0xFF) continue;
        // Decode filename and compare.
        var nameBuf = new byte[30];
        for (var j = 0; j < 30; j++) nameBuf[j] = (byte)(data[eo + 3 + j] & 0x7F);
        var nameLen = 30;
        while (nameLen > 0 && nameBuf[nameLen - 1] == (0xA0 & 0x7F)) nameLen--;
        var entryName = Encoding.ASCII.GetString(nameBuf, 0, nameLen).TrimEnd();
        if (!string.Equals(entryName, fileName, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(fileName, "*", StringComparison.Ordinal)) continue;

        // Found the file. Walk its T/S list chain and patch data-sector pointers.
        var tslT = (int)data[eo];
        var tslS = (int)data[eo + 1];
        PatchTsListChain(data, tslT, tslS, remap);
      }
      ct = data[secOff + 0x01];
      cs = data[secOff + 0x02];
    }

    // 3. Update VTOC bitmap: free old sectors, allocate new sectors.
    foreach (var (t, s) in oldSectors) SetBitmapBit(data, vtocOff, t, s, free: true);
    foreach (var (t, s) in newSectors) SetBitmapBit(data, vtocOff, t, s, free: false);

    image.Position = 0;
    image.Write(data, 0, data.Length);
    // Crash barrier: metadata commit durable before return.
    image.Flush();
  }

  // ── T/S list patching ─────────────────────────────────────────────

  private static void PatchTsListChain(byte[] data, int tslT, int tslS,
      Dictionary<(int, int), (int, int)> remap) {
    var visited = new HashSet<(int, int)>();
    var t = tslT; var s = tslS;
    while (t != 0 && visited.Add((t, s))) {
      if (t < 0 || t >= TotalTracks || s < 0 || s >= SectorsPerTrack) break;
      var off = (int)SectorOffset(t, s);

      // Patch data-sector pointers at offsets 0x0C..0x0C+122*2.
      for (var i = 0; i < TsListPairsPerSector; i++) {
        var pairOff = off + 0x0C + i * 2;
        var dT = data[pairOff]; var dS = data[pairOff + 1];
        if (dT == 0 && dS == 0) break;
        if (remap.TryGetValue((dT, dS), out var newTs)) {
          data[pairOff] = (byte)newTs.Item1;
          data[pairOff + 1] = (byte)newTs.Item2;
        }
      }

      // Follow T/S list chain link.
      var nextT = data[off + 0x01]; var nextS = data[off + 0x02];
      t = nextT; s = nextS;
    }
  }

  // ── VTOC bitmap helpers ───────────────────────────────────────────
  // Bitmap at VTOC offset 0x38, 4 bytes per track:
  //   byte 0: sectors 15..8 (bit 0=sector15, bit 7=sector8)
  //   byte 1: sectors 7..0  (bit 0=sector7, bit 7=sector0)
  //   bytes 2-3: zero

  private static void SetBitmapBit(byte[] data, int vtocOff, int track, int sector, bool free) {
    if (track < 0 || track >= TotalTracks || sector < 0 || sector >= SectorsPerTrack) return;
    var off = vtocOff + 0x38 + track * 4;
    int byteIdx, bitIdx;
    if (sector >= 8) {
      // byte 0: sector 15 = bit 0 ... sector 8 = bit 7
      byteIdx = off;
      bitIdx = 15 - sector;
    } else {
      // byte 1: sector 7 = bit 0 ... sector 0 = bit 7
      byteIdx = off + 1;
      bitIdx = 7 - sector;
    }
    if (free)
      data[byteIdx] |= (byte)(1 << bitIdx);
    else
      data[byteIdx] &= (byte)~(1 << bitIdx);
  }
}
