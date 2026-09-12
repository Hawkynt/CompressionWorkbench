#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.D71;

/// <summary>
/// In-place D71 modifier — same blueprint as <c>D64Modifier</c>, adapted for
/// the 1571's double-sided geometry. Performs <b>O(touched bytes)</b>
/// random-access I/O: only reads the two BAM sectors (T18S0 + T53S0), the
/// directory chain (≤19 sectors on T18), and the file's data chain.
///
/// <para>Layout reminders:
/// <list type="bullet">
///   <item>1571 = 70 tracks (35 per side). Total: 1366 sectors, 349 696 bytes.</item>
///   <item>Track 18 / sector 0: side-1 BAM bitmaps + per-track free counts for side 2 (offsets 0xDD–0xFF).</item>
///   <item>Track 53 / sector 0: side-2 BAM bitmaps (3 bytes per track, no free-count byte).</item>
///   <item>Directory chain at T18S1+ (single side, same shape as D64).</item>
///   <item>Each sector: 256 B; data sectors carry T,S link + 254 data bytes.</item>
/// </list></para>
/// </summary>
public static class D71Modifier {
  private const int SectorSize = 256;
  private const int DirTrack = 18;
  private const int Side2BamTrack = 53;
  private const int BamSector = 0;
  private const int DirStartSector = 1;
  private const int TotalTracks = 70;
  private const int Side1Tracks = 35;
  private const int DirInterleave = 3;

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
    if (track < 1 || track > TotalTracks) throw new ArgumentOutOfRangeException(nameof(track));
    if (sector < 0 || sector >= SectorsPerTrack[track]) throw new ArgumentOutOfRangeException(nameof(sector));
    var offset = 0;
    for (var t = 1; t < track; t++) offset += SectorsPerTrack[t] * SectorSize;
    return offset + sector * SectorSize;
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

  /// <summary>Adds a file to an existing D71 image with O(touched bytes) I/O.</summary>
  public static void AddFile(Stream image, string name, byte[] data, byte fileType = 0x82) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    if (name.Length > 16) throw new ArgumentException("D71 file names limited to 16 chars.", nameof(name));

    var requiredSectors = data.Length == 0 ? 0 : (data.Length + 253) / 254;

    var bam1 = ReadSector(image, DirTrack, BamSector);
    var bam2 = ReadSector(image, Side2BamTrack, BamSector);

    var allocated = AllocateSectors(bam1, bam2, requiredSectors)
      ?? throw new IOException($"D71 disk full: cannot allocate {requiredSectors} sectors.");

    var (dirT, dirS, slotIdx, dirBytes) = FindFreeDirectorySlot(image, bam1);

    WriteFileChain(image, allocated, data);

    WriteDirectoryEntry(dirBytes, slotIdx, name, fileType,
      startTrack: allocated.Count > 0 ? (byte)allocated[0].Track : (byte)0,
      startSector: allocated.Count > 0 ? (byte)allocated[0].Sector : (byte)0,
      sectorCount: allocated.Count);
    WriteSector(image, dirT, dirS, dirBytes);

    WriteSector(image, DirTrack, BamSector, bam1);
    WriteSector(image, Side2BamTrack, BamSector, bam2);
  }

  /// <summary>Removes a named file with O(touched bytes) I/O. Returns true if removed.</summary>
  public static bool RemoveFile(Stream image, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);

    var locator = LocateDirectoryEntry(image, name);
    if (!locator.Found) return false;

    var bam1 = ReadSector(image, DirTrack, BamSector);
    var bam2 = ReadSector(image, Side2BamTrack, BamSector);

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

    WriteSector(image, DirTrack, BamSector, bam1);
    WriteSector(image, Side2BamTrack, BamSector, bam2);
    return true;
  }

  // ── BAM helpers (track 1-35 in BAM1, 36-70 split BAM1 free-count + BAM2 bitmap) ──

  private static bool IsFree(byte[] bam1, byte[] bam2, int track, int sector) {
    if (track is < 1 or > TotalTracks) return false;
    if (track <= Side1Tracks) {
      var entry = 4 + (track - 1) * 4;
      var byteIdx = entry + 1 + sector / 8;
      var bitIdx = sector % 8;
      return (bam1[byteIdx] & (1 << bitIdx)) != 0;
    } else {
      // Side 2: 3 bytes per track in bam2, starting at offset 0
      var entry = (track - 36) * 3;
      var byteIdx = entry + sector / 8;
      var bitIdx = sector % 8;
      return (bam2[byteIdx] & (1 << bitIdx)) != 0;
    }
  }

  private static void MarkFree(byte[] bam1, byte[] bam2, int track, int sector) {
    if (track is < 1 or > TotalTracks) return;
    if (track <= Side1Tracks) {
      var entry = 4 + (track - 1) * 4;
      var byteIdx = entry + 1 + sector / 8;
      var bitIdx = sector % 8;
      if ((bam1[byteIdx] & (1 << bitIdx)) == 0) {
        bam1[byteIdx] |= (byte)(1 << bitIdx);
        bam1[entry]++;
      }
    } else {
      var entry = (track - 36) * 3;
      var byteIdx = entry + sector / 8;
      var bitIdx = sector % 8;
      if ((bam2[byteIdx] & (1 << bitIdx)) == 0) {
        bam2[byteIdx] |= (byte)(1 << bitIdx);
        // Side 2 free-count byte lives in bam1 at offset 0xDD + (track - 36)
        bam1[0xDD + (track - 36)]++;
      }
    }
  }

  private static void MarkAllocated(byte[] bam1, byte[] bam2, int track, int sector) {
    if (track is < 1 or > TotalTracks) return;
    if (track <= Side1Tracks) {
      var entry = 4 + (track - 1) * 4;
      var byteIdx = entry + 1 + sector / 8;
      var bitIdx = sector % 8;
      if ((bam1[byteIdx] & (1 << bitIdx)) != 0) {
        bam1[byteIdx] &= (byte)~(1 << bitIdx);
        if (bam1[entry] > 0) bam1[entry]--;
      }
    } else {
      var entry = (track - 36) * 3;
      var byteIdx = entry + sector / 8;
      var bitIdx = sector % 8;
      if ((bam2[byteIdx] & (1 << bitIdx)) != 0) {
        bam2[byteIdx] &= (byte)~(1 << bitIdx);
        var s2FreeIdx = 0xDD + (track - 36);
        if (bam1[s2FreeIdx] > 0) bam1[s2FreeIdx]--;
      }
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
        if (track == DirTrack || track == Side2BamTrack) continue;
        var sectorsOnTrack = SectorsPerTrack[track];
        for (var attempt = 0; attempt < sectorsOnTrack; attempt++) {
          var sector = (lastS + 10 + attempt) % sectorsOnTrack;
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

  private static (int DirSectorTrack, int DirSectorIdx, int SlotIndex, byte[] DirSectorBytes)
    FindFreeDirectorySlot(Stream image, byte[] bam1) {
    var t = DirTrack;
    var s = DirStartSector;
    var visited = new HashSet<(int, int)>();
    while (true) {
      if (!visited.Add((t, s)))
        throw new IOException("D71 directory chain loop detected.");
      var sectorBytes = ReadSector(image, t, s);
      for (var slot = 0; slot < 8; slot++) {
        var fileType = sectorBytes[slot * 32 + 2];
        if (fileType == 0)
          return (t, s, slot, sectorBytes);
      }
      var nextT = sectorBytes[0];
      var nextS = sectorBytes[1];
      if (nextT == 0) {
        for (var attempt = 0; attempt < SectorsPerTrack[DirTrack]; attempt++) {
          var candidate = (s + DirInterleave + attempt) % SectorsPerTrack[DirTrack];
          if (candidate == BamSector) continue;
          // Check side-1 BAM only — directory always lives on side 1.
          var entry = 4 + (DirTrack - 1) * 4;
          var byteIdx = entry + 1 + candidate / 8;
          var bitIdx = candidate % 8;
          if ((bam1[byteIdx] & (1 << bitIdx)) == 0) continue;
          // Allocate (mark on side 1 BAM).
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
        throw new IOException("D71 directory full: no free slot in chain and no free sector on track 18.");
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
