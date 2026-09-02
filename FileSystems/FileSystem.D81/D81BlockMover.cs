#pragma warning disable CS1591
using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileSystem.D81;

/// <summary>
/// In-place D81 block mover. Moves sector-aligned extents within a 1581
/// disk image (80 tracks x 40 sectors, uniform geometry) and patches the
/// T/S chain links + dual BAM bitmaps so the file remains reachable.
/// </summary>
public sealed class D81BlockMover : IFilesystemBlockMover {

  private const int SectorSize = 256;
  private const int SectorsPerTrack = 40;
  private const int TotalTracks = 80;
  private const int DirTrack = 40;
  private const int Bam1Sector = 1;
  private const int Bam2Sector = 2;
  private const int DirStartSector = 3;
  private const int BamEntrySize = 6;
  private const int BamEntriesStart = 16;

  private static int GetSectorOffset(int track, int sector)
    => ((track - 1) * SectorsPerTrack + sector) * SectorSize;

  private static (int Track, int Sector) OffsetToTrackSector(long byteOffset) {
    var linearSector = (int)(byteOffset / SectorSize);
    var track = linearSector / SectorsPerTrack + 1;
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

    // 1. Patch T/S chain links in moved sectors.
    for (var i = 0; i < sectorCount; i++) {
      var (nt, ns) = newSectors[i];
      var off = GetSectorOffset(nt, ns);
      if (off + 2 > data.Length) continue;
      var linkT = data[off]; var linkS = data[off + 1];
      if (linkT == 0) continue;
      var idx = oldSectors.IndexOf((linkT, linkS));
      if (idx >= 0) { data[off] = (byte)newSectors[idx].Track; data[off + 1] = (byte)newSectors[idx].Sector; }
    }

    // 2. Patch sectors outside the moved set.
    for (var t = 1; t <= TotalTracks; t++) {
      for (var s = 0; s < SectorsPerTrack; s++) {
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

    // 4. Update dual BAMs.
    var bam1Off = GetSectorOffset(DirTrack, Bam1Sector);
    var bam2Off = GetSectorOffset(DirTrack, Bam2Sector);
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

  // ── BAM helpers (BAM1 covers tracks 1-40, BAM2 covers tracks 41-80) ──

  private static (int BamOff, int EntryOff) GetBamEntry(int bam1Off, int bam2Off, int track) {
    if (track is >= 1 and <= 40)
      return (bam1Off, bam1Off + BamEntriesStart + (track - 1) * BamEntrySize);
    return (bam2Off, bam2Off + BamEntriesStart + (track - 41) * BamEntrySize);
  }

  private static void MarkFree(byte[] data, int bam1Off, int bam2Off, int track, int sector) {
    if (track < 1 || track > TotalTracks) return;
    var (_, entry) = GetBamEntry(bam1Off, bam2Off, track);
    var byteIdx = entry + 1 + sector / 8;
    var bitIdx = sector % 8;
    if ((data[byteIdx] & (1 << bitIdx)) == 0) { data[byteIdx] |= (byte)(1 << bitIdx); data[entry]++; }
  }

  private static void MarkAllocated(byte[] data, int bam1Off, int bam2Off, int track, int sector) {
    if (track < 1 || track > TotalTracks) return;
    var (_, entry) = GetBamEntry(bam1Off, bam2Off, track);
    var byteIdx = entry + 1 + sector / 8;
    var bitIdx = sector % 8;
    if ((data[byteIdx] & (1 << bitIdx)) != 0) { data[byteIdx] &= (byte)~(1 << bitIdx); if (data[entry] > 0) data[entry]--; }
  }
}
