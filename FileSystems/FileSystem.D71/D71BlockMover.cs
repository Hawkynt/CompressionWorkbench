#pragma warning disable CS1591
using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileSystem.D71;

/// <summary>
/// In-place D71 block mover. Moves sector-aligned extents within a 1571
/// double-sided disk image and patches the T/S chain links + dual BAM
/// bitmaps so the file remains reachable at its new location.
/// </summary>
public sealed class D71BlockMover : IFilesystemBlockMover {

  private const int SectorSize = 256;
  private const int DirTrack = 18;
  private const int Side2BamTrack = 53;
  private const int BamSector = 0;
  private const int DirStartSector = 1;
  private const int TotalTracks = 70;
  private const int Side1Tracks = 35;

  private static readonly int[] SectorsPerTrack = [
    0,
    21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21,
    19, 19, 19, 19, 19, 19, 19,
    18, 18, 18, 18, 18, 18,
    17, 17, 17, 17, 17,
    21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21,
    19, 19, 19, 19, 19, 19, 19,
    18, 18, 18, 18, 18, 18,
    17, 17, 17, 17, 17
  ];

  private static int GetSectorOffset(int track, int sector) {
    var offset = 0;
    for (var t = 1; t < track; t++) offset += SectorsPerTrack[t] * SectorSize;
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

    // 1. Patch T/S chain links in each moved sector.
    for (var i = 0; i < sectorCount; i++) {
      var (nt, ns) = newSectors[i];
      var off = GetSectorOffset(nt, ns);
      if (off + 2 > data.Length) continue;
      var linkT = data[off]; var linkS = data[off + 1];
      if (linkT == 0) continue;
      var idx = oldSectors.IndexOf((linkT, linkS));
      if (idx >= 0) { data[off] = (byte)newSectors[idx].Track; data[off + 1] = (byte)newSectors[idx].Sector; }
    }

    // 2. Patch sectors outside the moved set whose next-pointer targets an old sector.
    for (var t = 1; t <= TotalTracks; t++) {
      for (var s = 0; s < SectorsPerTrack[t]; s++) {
        if (newSectors.Contains((t, s))) continue;
        var off = GetSectorOffset(t, s);
        if (off + 2 > data.Length) continue;
        var linkT = data[off]; var linkS = data[off + 1];
        if (linkT == 0) continue;
        var idx = oldSectors.IndexOf((linkT, linkS));
        if (idx >= 0) { data[off] = (byte)newSectors[idx].Track; data[off + 1] = (byte)newSectors[idx].Sector; }
      }
    }

    // 3. Patch directory start (T,S).
    PatchDirectoryStartSector(data, fileName, oldSectors, newSectors);

    // 4. Update dual BAMs: free old, allocate new.
    var bam1Off = GetSectorOffset(DirTrack, BamSector);
    var bam2Off = GetSectorOffset(Side2BamTrack, BamSector);
    foreach (var (t, s) in oldSectors) MarkFree(data, bam1Off, bam2Off, t, s);
    foreach (var (t, s) in newSectors) MarkAllocated(data, bam1Off, bam2Off, t, s);

    image.Position = 0;
    image.Write(data, 0, data.Length);
    // Crash barrier: metadata commit durable before return.
    image.Flush();
  }

  private static void PatchDirectoryStartSector(byte[] data, string fileName,
      List<(int Track, int Sector)> oldSectors, List<(int Track, int Sector)> newSectors) {
    var t = DirTrack; var s = DirStartSector;
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
        if (idx >= 0) { data[eo + 3] = (byte)newSectors[idx].Track; data[eo + 4] = (byte)newSectors[idx].Sector; }
      }
      var nextT = data[off]; var nextS = data[off + 1];
      if (nextT == 0) break;
      t = nextT; s = nextS;
    }
  }

  // ── BAM helpers (side 1 in BAM1, side 2 in BAM2 + free-count in BAM1) ──

  private static void MarkFree(byte[] data, int bam1Off, int bam2Off, int track, int sector) {
    if (track < 1 || track > TotalTracks) return;
    if (track <= Side1Tracks) {
      var entry = bam1Off + 4 + (track - 1) * 4;
      var byteIdx = entry + 1 + sector / 8;
      var bitIdx = sector % 8;
      if ((data[byteIdx] & (1 << bitIdx)) == 0) { data[byteIdx] |= (byte)(1 << bitIdx); data[entry]++; }
    } else {
      var entry = bam2Off + (track - 36) * 3;
      var byteIdx = entry + sector / 8;
      var bitIdx = sector % 8;
      if ((data[byteIdx] & (1 << bitIdx)) == 0) { data[byteIdx] |= (byte)(1 << bitIdx); data[bam1Off + 0xDD + (track - 36)]++; }
    }
  }

  private static void MarkAllocated(byte[] data, int bam1Off, int bam2Off, int track, int sector) {
    if (track < 1 || track > TotalTracks) return;
    if (track <= Side1Tracks) {
      var entry = bam1Off + 4 + (track - 1) * 4;
      var byteIdx = entry + 1 + sector / 8;
      var bitIdx = sector % 8;
      if ((data[byteIdx] & (1 << bitIdx)) != 0) { data[byteIdx] &= (byte)~(1 << bitIdx); if (data[entry] > 0) data[entry]--; }
    } else {
      var entry = bam2Off + (track - 36) * 3;
      var byteIdx = entry + sector / 8;
      var bitIdx = sector % 8;
      if ((data[byteIdx] & (1 << bitIdx)) != 0) {
        data[byteIdx] &= (byte)~(1 << bitIdx);
        var fc = bam1Off + 0xDD + (track - 36);
        if (data[fc] > 0) data[fc]--;
      }
    }
  }
}
