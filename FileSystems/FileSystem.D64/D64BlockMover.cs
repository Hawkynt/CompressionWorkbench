#pragma warning disable CS1591
using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileSystem.D64;

/// <summary>
/// In-place D64 block mover. Moves sector-aligned extents within a 1541
/// disk image and patches the T/S chain links + BAM bitmap so the file
/// remains reachable at its new location.
/// </summary>
public sealed class D64BlockMover : IFilesystemBlockMover {

  private const int SectorSize = 256;
  private const int DirTrack = 18;
  private const int BamSector = 0;
  private const int DirStartSector = 1;
  private const int TotalTracks = 35;
  private const int BamEntrySize = 4;
  private const int BamEntriesStart = 4;

  private static readonly int[] SectorsPerTrack = [
    0,
    21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21,
    19, 19, 19, 19, 19, 19, 19,
    18, 18, 18, 18, 18, 18,
    17, 17, 17, 17, 17
  ];

  // ── Sector offset helpers ─────────────────────────────────────────

  private static int GetSectorOffset(int track, int sector) {
    var offset = 0;
    for (var t = 1; t < track; t++)
      offset += SectorsPerTrack[t] * SectorSize;
    return offset + sector * SectorSize;
  }

  private static (int Track, int Sector) OffsetToTrackSector(long byteOffset) {
    var sectorIndex = (int)(byteOffset / SectorSize);
    var idx = 0;
    for (var t = 1; t <= TotalTracks; t++) {
      if (sectorIndex < idx + SectorsPerTrack[t])
        return (t, sectorIndex - idx);
      idx += SectorsPerTrack[t];
    }
    throw new ArgumentOutOfRangeException(nameof(byteOffset));
  }

  // ── IFilesystemBlockMover ─────────────────────────────────────────

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

    var oldSet = new HashSet<(int, int)>(oldSectors);

    // 1. Patch T/S chain links in each moved sector: rewrite the (T,S) next-pointer
    //    of every sector whose next-pointer was in the old range to the new range.
    for (var i = 0; i < sectorCount; i++) {
      var (nt, ns) = newSectors[i];
      var off = GetSectorOffset(nt, ns);
      if (off + 2 > data.Length) continue;
      var linkT = data[off];
      var linkS = data[off + 1];
      if (linkT == 0) continue; // last sector in chain — no link to patch
      var linkTs = (linkT, linkS);
      var idx = oldSectors.IndexOf(linkTs);
      if (idx >= 0) {
        data[off] = (byte)newSectors[idx].Track;
        data[off + 1] = (byte)newSectors[idx].Sector;
      }
    }

    // 2. Walk the entire image for sectors NOT in the moved set whose next-pointer
    //    targets a sector in the old range. This catches the sector preceding
    //    the moved extent in the chain.
    for (var t = 1; t <= TotalTracks; t++) {
      for (var s = 0; s < SectorsPerTrack[t]; s++) {
        if (newSectors.Contains((t, s))) continue; // already patched above
        var off = GetSectorOffset(t, s);
        if (off + 2 > data.Length) continue;
        var linkT = data[off];
        var linkS = data[off + 1];
        if (linkT == 0) continue;
        var linkTs = (linkT, linkS);
        var idx = oldSectors.IndexOf(linkTs);
        if (idx >= 0) {
          data[off] = (byte)newSectors[idx].Track;
          data[off + 1] = (byte)newSectors[idx].Sector;
        }
      }
    }

    // 3. Patch directory entry: update start (T,S) if it points into the old range.
    PatchDirectoryStartSector(data, fileName, oldSectors, newSectors);

    // 4. Update BAM: free old sectors, allocate new sectors.
    var bamOff = GetSectorOffset(DirTrack, BamSector);
    foreach (var (t, s) in oldSectors) MarkFree(data, bamOff, t, s);
    foreach (var (t, s) in newSectors) MarkAllocated(data, bamOff, t, s);

    image.Position = 0;
    image.Write(data, 0, data.Length);
    // Crash barrier: metadata commit durable before return.
    image.Flush();
  }

  // ── Directory patching ────────────────────────────────────────────

  private static void PatchDirectoryStartSector(byte[] data, string fileName,
      List<(int Track, int Sector)> oldSectors, List<(int Track, int Sector)> newSectors) {
    var t = DirTrack;
    var s = DirStartSector;
    var visited = new HashSet<(int, int)>();
    var nameUpper = fileName.ToUpperInvariant();
    while (t != 0 && visited.Add((t, s))) {
      var off = GetSectorOffset(t, s);
      if (off + SectorSize > data.Length) break;
      for (var slot = 0; slot < 8; slot++) {
        var eo = off + slot * 32;
        var fileType = data[eo + 2];
        if ((fileType & 0x07) == 0) continue;
        var nameSpan = data.AsSpan(eo + 5, 16);
        var nameEnd = nameSpan.IndexOf((byte)0xA0);
        if (nameEnd < 0) nameEnd = 16;
        var entryName = Encoding.ASCII.GetString(data, eo + 5, nameEnd);
        if (!string.Equals(entryName, nameUpper, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(fileName, "*", StringComparison.Ordinal)) continue;
        var startTs = ((int)data[eo + 3], (int)data[eo + 4]);
        var idx = oldSectors.IndexOf(startTs);
        if (idx >= 0) {
          data[eo + 3] = (byte)newSectors[idx].Track;
          data[eo + 4] = (byte)newSectors[idx].Sector;
        }
      }
      var nextT = data[off];
      var nextS = data[off + 1];
      if (nextT == 0) break;
      t = nextT; s = nextS;
    }
  }

  // ── BAM helpers ───────────────────────────────────────────────────

  private static void MarkFree(byte[] data, int bamOff, int track, int sector) {
    if (track < 1 || track > TotalTracks) return;
    var entry = bamOff + BamEntriesStart + (track - 1) * BamEntrySize;
    var byteIdx = entry + 1 + sector / 8;
    var bitIdx = sector % 8;
    if ((data[byteIdx] & (1 << bitIdx)) == 0) {
      data[byteIdx] |= (byte)(1 << bitIdx);
      data[entry]++;
    }
  }

  private static void MarkAllocated(byte[] data, int bamOff, int track, int sector) {
    if (track < 1 || track > TotalTracks) return;
    var entry = bamOff + BamEntriesStart + (track - 1) * BamEntrySize;
    var byteIdx = entry + 1 + sector / 8;
    var bitIdx = sector % 8;
    if ((data[byteIdx] & (1 << bitIdx)) != 0) {
      data[byteIdx] &= (byte)~(1 << bitIdx);
      if (data[entry] > 0) data[entry]--;
    }
  }
}
