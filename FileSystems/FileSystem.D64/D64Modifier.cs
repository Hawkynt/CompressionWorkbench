#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.D64;

/// <summary>
/// In-place D64 modifier. Performs add / remove on an existing 1541 disk
/// image with strict <b>O(touched bytes)</b> I/O — only reads the BAM
/// (1 sector), the directory chain (≤19 sectors), and the affected file's
/// data chain (one sector per 254 bytes of file data). Never reads or
/// writes the entire image, so this scales to multi-TB virtual-disk images
/// even though D64 itself is 174 848 bytes.
///
/// <para>The companion <see cref="D64Writer"/> rebuilds an image from
/// scratch; this class is for the "I have an existing image, mutate it"
/// path that <c>IArchiveModifiable</c> exposes.</para>
///
/// <para>Layout reminders (for the reader of this code, not for the disk):
/// <list type="bullet">
///   <item>1541 geometry: 35 tracks; 21 / 19 / 18 / 17 sectors per zone (1-17 / 18-24 / 25-30 / 31-35).</item>
///   <item>Each sector is 256 bytes. Total image: 174 848 bytes.</item>
///   <item>BAM lives at track 18 / sector 0. Directory chain starts at track 18 / sector 1.</item>
///   <item>Each file is a chain of sectors. Each sector starts with 2 bytes (T,S of next sector,
///         or T=0 + S=byte-count+1 for the last sector). Remaining 254 bytes are file data.</item>
///   <item>Each directory sector holds 8 entries of 32 bytes. Bytes 0-1 of the sector store
///         the T,S of the next directory sector (or 0,$FF if last). Entries 1-7 have unused
///         bytes at offsets +0 and +1.</item>
/// </list></para>
/// </summary>
public static class D64Modifier {
  private const int SectorSize = 256;
  private const int DirTrack = 18;
  private const int BamSector = 0;
  private const int DirStartSector = 1;
  private const int TotalTracks = 35;
  private const int FreeCountByteOffsetInBamEntry = 0;
  private const int BamEntrySize = 4;
  private const int BamEntriesStart = 4;
  private const int DirInterleave = 3;

  private static readonly int[] SectorsPerTrack = [
    0,
    21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21,
    19, 19, 19, 19, 19, 19, 19,
    18, 18, 18, 18, 18, 18,
    17, 17, 17, 17, 17
  ];

  private static int GetSectorOffset(int track, int sector) {
    if (track < 1 || track > TotalTracks) throw new ArgumentOutOfRangeException(nameof(track));
    if (sector < 0 || sector >= SectorsPerTrack[track]) throw new ArgumentOutOfRangeException(nameof(sector));
    var offset = 0;
    for (var t = 1; t < track; t++)
      offset += SectorsPerTrack[t] * SectorSize;
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

  /// <summary>
  /// Adds a file to the existing D64 image. Performs in-place modification:
  /// allocates new sectors via BAM bit-flips, writes the file chain, writes
  /// the directory entry. Bytes touched: 1 BAM sector + ≤ ⌈log₈(entries)⌉
  /// directory sectors + ⌈len/254⌉ file data sectors.
  /// </summary>
  /// <exception cref="IOException">Disk full (BAM has fewer than required free sectors)
  /// or directory full (no free entry slot and no room to allocate a new directory sector).</exception>
  /// <exception cref="ArgumentException">File name longer than 16 characters.</exception>
  public static void AddFile(Stream image, string name, byte[] data, byte fileType = 0x82) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    if (name.Length > 16)
      throw new ArgumentException("D64 file names are limited to 16 PETSCII characters.", nameof(name));

    var requiredSectors = data.Length == 0 ? 0 : (data.Length + 253) / 254;

    // 1. Read BAM (1 sector touched).
    var bam = ReadSector(image, DirTrack, BamSector);

    // 2. Allocate `requiredSectors` data sectors. Skip directory track.
    var allocated = AllocateSectors(bam, requiredSectors)
      ?? throw new IOException($"D64 disk full: cannot allocate {requiredSectors} sectors.");

    // 3. Walk directory chain to find a free slot, or to extend it.
    var (dirSectorTrack, dirSectorIdx, slotIndex, dirSectorBytes) = FindFreeDirectorySlot(image, bam);

    // 4. Write file data sectors with chain links. (allocated.Count sectors touched.)
    WriteFileChain(image, allocated, data);

    // 5. Update directory sector with the new entry. Write that single sector back.
    WriteDirectoryEntry(dirSectorBytes, slotIndex, name, fileType,
                        startTrack: allocated.Count > 0 ? (byte)allocated[0].Track : (byte)0,
                        startSector: allocated.Count > 0 ? (byte)allocated[0].Sector : (byte)0,
                        sectorCount: allocated.Count);
    WriteSector(image, dirSectorTrack, dirSectorIdx, dirSectorBytes);

    // 6. Write updated BAM (1 sector touched).
    WriteSector(image, DirTrack, BamSector, bam);
  }

  /// <summary>
  /// Removes the named file from the existing D64 image. Walks the file's
  /// chain to free its sectors in the BAM, optionally wipes data bytes,
  /// and clears the directory entry's file-type byte to 0 ("scratched").
  /// Returns true if the file was found and removed, false otherwise.
  /// Bytes touched: 1 BAM sector + 1 directory sector + N file sectors.
  /// </summary>
  public static bool RemoveFile(Stream image, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);

    // 1. Walk directory to locate the entry.
    var locator = LocateDirectoryEntry(image, name);
    if (!locator.Found)
      return false;

    // 2. Read BAM, walk file chain, mark each sector free + optionally wipe.
    var bam = ReadSector(image, DirTrack, BamSector);
    var visited = new HashSet<(int, int)>();
    var (t, s) = (locator.StartTrack, locator.StartSector);
    while (t != 0 && visited.Add((t, s))) {
      var sectorData = ReadSector(image, t, s);
      var nextT = sectorData[0];
      var nextS = sectorData[1];

      MarkFree(bam, t, s);
      if (wipeData) {
        var blank = new byte[SectorSize];
        WriteSector(image, t, s, blank);
      }
      (t, s) = (nextT, nextS);
    }

    // 3. Mark directory entry as scratched (set file-type byte to 0).
    locator.DirSectorBytes![locator.SlotIndex * 32 + 2] = 0;
    WriteSector(image, locator.DirSectorTrack, locator.DirSectorIdx, locator.DirSectorBytes);

    // 4. Write updated BAM.
    WriteSector(image, DirTrack, BamSector, bam);
    return true;
  }

  // ── BAM helpers ─────────────────────────────────────────────────────

  /// <summary>Returns true if the sector is marked free in the BAM bitmap.</summary>
  private static bool IsFree(byte[] bam, int track, int sector) {
    if (track < 1 || track > TotalTracks) return false;
    var entryOffset = BamEntriesStart + (track - 1) * BamEntrySize;
    // sector bit 0 = bit 0 of byte (entryOffset + 1)
    var byteIdx = entryOffset + 1 + sector / 8;
    var bitIdx = sector % 8;
    return (bam[byteIdx] & (1 << bitIdx)) != 0;
  }

  /// <summary>Marks a sector as free in the BAM (sets the bit + increments the per-track free count).</summary>
  private static void MarkFree(byte[] bam, int track, int sector) {
    if (track < 1 || track > TotalTracks) return;
    var entryOffset = BamEntriesStart + (track - 1) * BamEntrySize;
    var byteIdx = entryOffset + 1 + sector / 8;
    var bitIdx = sector % 8;
    if ((bam[byteIdx] & (1 << bitIdx)) == 0) {
      bam[byteIdx] |= (byte)(1 << bitIdx);
      bam[entryOffset + FreeCountByteOffsetInBamEntry]++;
    }
  }

  /// <summary>Marks a sector as allocated in the BAM (clears the bit + decrements the per-track free count).</summary>
  private static void MarkAllocated(byte[] bam, int track, int sector) {
    if (track < 1 || track > TotalTracks) return;
    var entryOffset = BamEntriesStart + (track - 1) * BamEntrySize;
    var byteIdx = entryOffset + 1 + sector / 8;
    var bitIdx = sector % 8;
    if ((bam[byteIdx] & (1 << bitIdx)) != 0) {
      bam[byteIdx] &= (byte)~(1 << bitIdx);
      if (bam[entryOffset + FreeCountByteOffsetInBamEntry] > 0)
        bam[entryOffset + FreeCountByteOffsetInBamEntry]--;
    }
  }

  /// <summary>
  /// Greedy sector allocator using 1541-style interleave (10 sectors). Walks
  /// tracks outward from track 1, skips the directory track. Marks each
  /// allocated sector in the BAM.
  /// </summary>
  private static List<(int Track, int Sector)>? AllocateSectors(byte[] bam, int count) {
    if (count == 0) return [];
    var allocated = new List<(int Track, int Sector)>(count);
    var (lastT, lastS) = (1, 0);
    while (allocated.Count < count) {
      var found = false;
      // Prefer same track first, then walk outward.
      for (var trackOffset = 0; trackOffset < TotalTracks; trackOffset++) {
        var track = ((lastT - 1 + trackOffset) % TotalTracks) + 1;
        if (track == DirTrack) continue;
        var sectorsOnTrack = SectorsPerTrack[track];
        // Try interleaved positions starting at lastS + 10 mod sectors-on-track
        for (var attempt = 0; attempt < sectorsOnTrack; attempt++) {
          var sector = (lastS + 10 + attempt) % sectorsOnTrack;
          if (IsFree(bam, track, sector)) {
            MarkAllocated(bam, track, sector);
            allocated.Add((track, sector));
            (lastT, lastS) = (track, sector);
            found = true;
            break;
          }
        }
        if (found) break;
      }
      if (!found) return null; // disk full
    }
    return allocated;
  }

  // ── File chain writer ───────────────────────────────────────────────

  /// <summary>
  /// Writes the file's data into the allocated sector list, prefixing each
  /// sector with the link bytes (T,S of next sector, or T=0/S=byteCount+1
  /// for the last sector).
  /// </summary>
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
        sectorBytes[0] = 0;                   // no next track
        sectorBytes[1] = (byte)(chunk + 1);   // bytes used + 1 (1541 convention)
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

  /// <summary>
  /// Walks the directory chain looking for a free slot (file-type byte 0).
  /// If no free slot exists in the existing chain, allocates a new
  /// directory sector via BAM (interleave 3) and links it from the last
  /// directory sector. Returns the chosen directory sector + slot index.
  /// </summary>
  private static (int DirSectorTrack, int DirSectorIdx, int SlotIndex, byte[] DirSectorBytes) FindFreeDirectorySlot(Stream image, byte[] bam) {
    var t = DirTrack;
    var s = DirStartSector;
    var visited = new HashSet<(int, int)>();
    byte[] sectorBytes;
    while (true) {
      if (!visited.Add((t, s)))
        throw new IOException("D64 directory chain loop detected.");
      sectorBytes = ReadSector(image, t, s);
      // Look for a free slot (file-type byte 0).
      for (var slot = 0; slot < 8; slot++) {
        var fileType = sectorBytes[slot * 32 + 2];
        if (fileType == 0)
          return (t, s, slot, sectorBytes);
      }
      var nextT = sectorBytes[0];
      var nextS = sectorBytes[1];
      if (nextT == 0) {
        // No more directory sectors. Try to allocate a new one on track 18 (interleave 3).
        for (var attempt = 0; attempt < SectorsPerTrack[DirTrack]; attempt++) {
          var candidate = (s + DirInterleave + attempt) % SectorsPerTrack[DirTrack];
          if (candidate == BamSector) continue;
          if (!IsFree(bam, DirTrack, candidate)) continue;
          // Allocate the new dir sector.
          MarkAllocated(bam, DirTrack, candidate);
          // Initialise it: blank sector with link bytes 0, $FF (= last sector).
          var newDir = new byte[SectorSize];
          newDir[0] = 0;
          newDir[1] = 0xFF;
          WriteSector(image, DirTrack, candidate, newDir);
          // Link from the previous (current) directory sector.
          sectorBytes[0] = (byte)DirTrack;
          sectorBytes[1] = (byte)candidate;
          WriteSector(image, t, s, sectorBytes);
          return (DirTrack, candidate, 0, newDir);
        }
        throw new IOException("D64 directory full: no free slot in chain and no free sector on track 18.");
      }
      (t, s) = (nextT, nextS);
    }
  }

  /// <summary>
  /// Writes a single 32-byte directory entry into the supplied directory-sector
  /// buffer. The buffer is mutated; caller is responsible for writing it back.
  /// </summary>
  private static void WriteDirectoryEntry(byte[] dirSector, int slot, string name, byte fileType,
                                          byte startTrack, byte startSector, int sectorCount) {
    var entryOff = slot * 32;
    // Bytes 0-1 are the next-sector link (entry 0) or unused (entries 1-7).
    // Don't touch bytes 0-1 for slots 1-7 — they're always 0 in canonical layouts.
    // For slot 0, bytes 0-1 are the directory chain link, which we shouldn't change here.

    dirSector[entryOff + 2] = fileType;
    dirSector[entryOff + 3] = startTrack;
    dirSector[entryOff + 4] = startSector;

    // Filename: 16 bytes at offset 5, ASCII, $A0 padded.
    var nameBytes = Encoding.ASCII.GetBytes(name.ToUpperInvariant());
    var nameLen = Math.Min(nameBytes.Length, 16);
    nameBytes.AsSpan(0, nameLen).CopyTo(dirSector.AsSpan(entryOff + 5));
    for (var i = nameLen; i < 16; i++)
      dirSector[entryOff + 5 + i] = 0xA0;

    // Bytes 21-29: REL/replacement metadata — leave at 0.
    for (var i = 21; i < 30; i++)
      dirSector[entryOff + i] = 0;

    // Bytes 30-31: file size in sectors (LE u16).
    BinaryPrimitives.WriteUInt16LittleEndian(dirSector.AsSpan(entryOff + 30), (ushort)sectorCount);
  }

  private sealed record class DirectoryLocator(
    bool Found, int DirSectorTrack, int DirSectorIdx, int SlotIndex,
    byte[]? DirSectorBytes, byte StartTrack, byte StartSector
  ) {
    public static readonly DirectoryLocator NotFound = new(false, 0, 0, 0, null, 0, 0);
  }

  /// <summary>
  /// Walks the directory chain looking for the named entry. Returns the
  /// chain location (sector + slot) so callers can mutate it without
  /// re-walking. Comparison is case-insensitive ASCII (PETSCII upper-case).
  /// </summary>
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
        // Filename bytes 5..20.
        var nameSpan = sectorBytes.AsSpan(entryOff + 5, 16);
        var nameEnd = nameSpan.IndexOf((byte)0xA0);
        if (nameEnd < 0) nameEnd = 16;
        var entryName = Encoding.ASCII.GetString(sectorBytes, entryOff + 5, nameEnd);
        if (string.Equals(entryName, nameUpper, StringComparison.OrdinalIgnoreCase)) {
          return new DirectoryLocator(true, t, s, slot, sectorBytes,
            sectorBytes[entryOff + 3], sectorBytes[entryOff + 4]);
        }
      }
      var nextT = sectorBytes[0];
      var nextS = sectorBytes[1];
      if (nextT == 0) break;
      (t, s) = (nextT, nextS);
    }
    return DirectoryLocator.NotFound;
  }
}
