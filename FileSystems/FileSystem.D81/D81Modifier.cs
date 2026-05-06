#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.D81;

/// <summary>
/// In-place D81 modifier — same blueprint as <c>D64Modifier</c> / <c>D71Modifier</c>,
/// adapted for the 1581's 3.5" geometry. Performs <b>O(touched bytes)</b>
/// random-access I/O: only reads the two BAM sectors (T40S1 + T40S2),
/// the directory chain (T40S3+), and the file's data chain.
///
/// <para>Layout reminders:
/// <list type="bullet">
///   <item>1581 = 80 tracks × 40 sectors = 3200 sectors = 819 200 bytes (uniform).</item>
///   <item>Track 40 / sector 0: header.</item>
///   <item>Track 40 / sector 1: BAM bitmaps for tracks 1-40 (6 bytes per track: 1 free + 5 bitmap).</item>
///   <item>Track 40 / sector 2: BAM bitmaps for tracks 41-80 (same shape).</item>
///   <item>Track 40 / sector 3+: directory chain.</item>
///   <item>Each sector: 256 B; data sectors carry T,S link + 254 data bytes.</item>
/// </list></para>
/// </summary>
public static class D81Modifier {
  private const int SectorSize = 256;
  private const int SectorsPerTrack = 40;
  private const int TotalTracks = 80;
  private const int DirTrack = 40;
  private const int HeaderSector = 0;
  private const int Bam1Sector = 1;
  private const int Bam2Sector = 2;
  private const int DirStartSector = 3;
  private const int BamEntrySize = 6;
  private const int BamEntriesStart = 16;

  private static int GetSectorOffset(int track, int sector) {
    if (track is < 1 or > TotalTracks) throw new ArgumentOutOfRangeException(nameof(track));
    if (sector is < 0 || sector >= SectorsPerTrack) throw new ArgumentOutOfRangeException(nameof(sector));
    return ((track - 1) * SectorsPerTrack + sector) * SectorSize;
  }

  private static byte[] ReadSector(Stream image, int track, int sector) {
    var buf = new byte[SectorSize];
    image.Position = GetSectorOffset(track, sector);
    image.ReadExactly(buf);
    return buf;
  }

  private static void WriteSector(Stream image, int track, int sector, ReadOnlySpan<byte> data) {
    if (data.Length != SectorSize) throw new ArgumentException("sector data must be 256 bytes", nameof(data));
    image.Position = GetSectorOffset(track, sector);
    image.Write(data);
  }

  /// <summary>Adds a file with O(touched bytes) I/O.</summary>
  public static void AddFile(Stream image, string name, byte[] data, byte fileType = 0x82) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    if (name.Length > 16) throw new ArgumentException("D81 file names limited to 16 chars.", nameof(name));

    var requiredSectors = data.Length == 0 ? 0 : (data.Length + 253) / 254;

    var bam1 = ReadSector(image, DirTrack, Bam1Sector);
    var bam2 = ReadSector(image, DirTrack, Bam2Sector);

    var allocated = AllocateSectors(bam1, bam2, requiredSectors)
      ?? throw new IOException($"D81 disk full: cannot allocate {requiredSectors} sectors.");

    var (dirT, dirS, slotIdx, dirBytes) = FindFreeDirectorySlot(image, bam1);

    WriteFileChain(image, allocated, data);

    WriteDirectoryEntry(dirBytes, slotIdx, name, fileType,
      startTrack: allocated.Count > 0 ? (byte)allocated[0].Track : (byte)0,
      startSector: allocated.Count > 0 ? (byte)allocated[0].Sector : (byte)0,
      sectorCount: allocated.Count);
    WriteSector(image, dirT, dirS, dirBytes);

    WriteSector(image, DirTrack, Bam1Sector, bam1);
    WriteSector(image, DirTrack, Bam2Sector, bam2);
  }

  /// <summary>Removes a named file with O(touched bytes) I/O.</summary>
  public static bool RemoveFile(Stream image, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);

    var locator = LocateDirectoryEntry(image, name);
    if (!locator.Found) return false;

    var bam1 = ReadSector(image, DirTrack, Bam1Sector);
    var bam2 = ReadSector(image, DirTrack, Bam2Sector);

    var visited = new HashSet<(int, int)>();
    var (t, s) = (locator.StartTrack, locator.StartSector);
    while (t != 0 && visited.Add((t, s))) {
      var sectorData = ReadSector(image, t, s);
      var nextT = sectorData[0];
      var nextS = sectorData[1];
      MarkFree(bam1, bam2, t, s);
      if (wipeData)
        WriteSector(image, t, s, new byte[SectorSize]);
      (t, s) = (nextT, nextS);
    }

    locator.DirSectorBytes![locator.SlotIndex * 32 + 2] = 0;
    WriteSector(image, locator.DirSectorTrack, locator.DirSectorIdx, locator.DirSectorBytes);

    WriteSector(image, DirTrack, Bam1Sector, bam1);
    WriteSector(image, DirTrack, Bam2Sector, bam2);
    return true;
  }

  // ── BAM helpers ──────────────────────────────────────────────────────

  private static (byte[] Bam, int EntryOff) GetBamEntry(byte[] bam1, byte[] bam2, int track) {
    if (track is >= 1 and <= 40)
      return (bam1, BamEntriesStart + (track - 1) * BamEntrySize);
    if (track is >= 41 and <= 80)
      return (bam2, BamEntriesStart + (track - 41) * BamEntrySize);
    throw new ArgumentOutOfRangeException(nameof(track));
  }

  private static bool IsFree(byte[] bam1, byte[] bam2, int track, int sector) {
    var (bam, entry) = GetBamEntry(bam1, bam2, track);
    var byteIdx = entry + 1 + sector / 8;
    var bitIdx = sector % 8;
    return (bam[byteIdx] & (1 << bitIdx)) != 0;
  }

  private static void MarkFree(byte[] bam1, byte[] bam2, int track, int sector) {
    var (bam, entry) = GetBamEntry(bam1, bam2, track);
    var byteIdx = entry + 1 + sector / 8;
    var bitIdx = sector % 8;
    if ((bam[byteIdx] & (1 << bitIdx)) == 0) {
      bam[byteIdx] |= (byte)(1 << bitIdx);
      bam[entry]++;
    }
  }

  private static void MarkAllocated(byte[] bam1, byte[] bam2, int track, int sector) {
    var (bam, entry) = GetBamEntry(bam1, bam2, track);
    var byteIdx = entry + 1 + sector / 8;
    var bitIdx = sector % 8;
    if ((bam[byteIdx] & (1 << bitIdx)) != 0) {
      bam[byteIdx] &= (byte)~(1 << bitIdx);
      if (bam[entry] > 0) bam[entry]--;
    }
  }

  private static List<(int Track, int Sector)>? AllocateSectors(byte[] bam1, byte[] bam2, int count) {
    if (count == 0) return [];
    var allocated = new List<(int Track, int Sector)>(count);
    var (lastT, lastS) = (1, 0);
    while (allocated.Count < count) {
      var found = false;
      for (var trackOffset = 0; trackOffset < TotalTracks; trackOffset++) {
        var track = ((lastT - 1 + trackOffset) % TotalTracks) + 1;
        if (track == DirTrack) continue;
        for (var attempt = 0; attempt < SectorsPerTrack; attempt++) {
          var sector = (lastS + 10 + attempt) % SectorsPerTrack;
          if (IsFree(bam1, bam2, track, sector)) {
            MarkAllocated(bam1, bam2, track, sector);
            allocated.Add((track, sector));
            (lastT, lastS) = (track, sector);
            found = true;
            break;
          }
        }
        if (found) break;
      }
      if (!found) return null;
    }
    return allocated;
  }

  private static void WriteFileChain(Stream image, IReadOnlyList<(int Track, int Sector)> allocated, byte[] data) {
    if (allocated.Count == 0) return;
    var pos = 0;
    for (var i = 0; i < allocated.Count; i++) {
      var (track, sector) = allocated[i];
      var sectorBytes = new byte[SectorSize];
      var remaining = data.Length - pos;
      var chunk = Math.Min(254, remaining);
      var isLast = i == allocated.Count - 1;
      if (isLast) {
        sectorBytes[0] = 0;
        sectorBytes[1] = (byte)(chunk + 1);
      } else {
        sectorBytes[0] = (byte)allocated[i + 1].Track;
        sectorBytes[1] = (byte)allocated[i + 1].Sector;
      }
      data.AsSpan(pos, chunk).CopyTo(sectorBytes.AsSpan(2));
      WriteSector(image, track, sector, sectorBytes);
      pos += chunk;
    }
  }

  // ── Directory walker ────────────────────────────────────────────────

  private static (int DirSectorTrack, int DirSectorIdx, int SlotIndex, byte[] DirSectorBytes)
    FindFreeDirectorySlot(Stream image, byte[] bam1) {
    var t = DirTrack;
    var s = DirStartSector;
    var visited = new HashSet<(int, int)>();
    while (true) {
      if (!visited.Add((t, s)))
        throw new IOException("D81 directory chain loop detected.");
      var sectorBytes = ReadSector(image, t, s);
      for (var slot = 0; slot < 8; slot++) {
        var fileType = sectorBytes[slot * 32 + 2];
        if (fileType == 0)
          return (t, s, slot, sectorBytes);
      }
      var nextT = sectorBytes[0];
      var nextS = sectorBytes[1];
      if (nextT == 0) {
        // Allocate a new directory sector on track 40 (same track).
        for (var attempt = 0; attempt < SectorsPerTrack; attempt++) {
          var candidate = (s + 1 + attempt) % SectorsPerTrack;
          if (candidate is HeaderSector or Bam1Sector or Bam2Sector) continue;
          var entry = BamEntriesStart + (DirTrack - 1) * BamEntrySize;
          var byteIdx = entry + 1 + candidate / 8;
          var bitIdx = candidate % 8;
          if ((bam1[byteIdx] & (1 << bitIdx)) == 0) continue;
          bam1[byteIdx] &= (byte)~(1 << bitIdx);
          if (bam1[entry] > 0) bam1[entry]--;
          var newDir = new byte[SectorSize];
          newDir[0] = 0;
          newDir[1] = 0xFF;
          WriteSector(image, DirTrack, candidate, newDir);
          sectorBytes[0] = (byte)DirTrack;
          sectorBytes[1] = (byte)candidate;
          WriteSector(image, t, s, sectorBytes);
          return (DirTrack, candidate, 0, newDir);
        }
        throw new IOException("D81 directory full: no free slot in chain and no free sector on track 40.");
      }
      (t, s) = (nextT, nextS);
    }
  }

  private static void WriteDirectoryEntry(byte[] dirSector, int slot, string name, byte fileType,
                                          byte startTrack, byte startSector, int sectorCount) {
    var entryOff = slot * 32;
    dirSector[entryOff + 2] = fileType;
    dirSector[entryOff + 3] = startTrack;
    dirSector[entryOff + 4] = startSector;
    var nameBytes = Encoding.ASCII.GetBytes(name.ToUpperInvariant());
    var nameLen = Math.Min(nameBytes.Length, 16);
    nameBytes.AsSpan(0, nameLen).CopyTo(dirSector.AsSpan(entryOff + 5));
    for (var i = nameLen; i < 16; i++)
      dirSector[entryOff + 5 + i] = 0xA0;
    for (var i = 21; i < 30; i++)
      dirSector[entryOff + i] = 0;
    BinaryPrimitives.WriteUInt16LittleEndian(dirSector.AsSpan(entryOff + 30), (ushort)sectorCount);
  }

  private sealed record class DirectoryLocator(
    bool Found, int DirSectorTrack, int DirSectorIdx, int SlotIndex,
    byte[]? DirSectorBytes, byte StartTrack, byte StartSector
  ) {
    public static readonly DirectoryLocator NotFound = new(false, 0, 0, 0, null, 0, 0);
  }

  private static DirectoryLocator LocateDirectoryEntry(Stream image, string targetName) {
    var t = DirTrack;
    var s = DirStartSector;
    var visited = new HashSet<(int, int)>();
    var nameUpper = targetName.ToUpperInvariant();
    while (visited.Add((t, s))) {
      var sectorBytes = ReadSector(image, t, s);
      for (var slot = 0; slot < 8; slot++) {
        var entryOff = slot * 32;
        var fileType = sectorBytes[entryOff + 2];
        if ((fileType & 0x07) == 0) continue;
        var nameSpan = sectorBytes.AsSpan(entryOff + 5, 16);
        var nameEnd = nameSpan.IndexOf((byte)0xA0);
        if (nameEnd < 0) nameEnd = 16;
        var entryName = Encoding.ASCII.GetString(sectorBytes, entryOff + 5, nameEnd);
        if (string.Equals(entryName, nameUpper, StringComparison.OrdinalIgnoreCase))
          return new DirectoryLocator(true, t, s, slot, sectorBytes,
            sectorBytes[entryOff + 3], sectorBytes[entryOff + 4]);
      }
      var nextT = sectorBytes[0];
      var nextS = sectorBytes[1];
      if (nextT == 0) break;
      (t, s) = (nextT, nextS);
    }
    return DirectoryLocator.NotFound;
  }
}
