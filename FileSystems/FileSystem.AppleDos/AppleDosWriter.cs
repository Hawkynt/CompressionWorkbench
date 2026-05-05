#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.AppleDos;

/// <summary>
/// Builds a fresh Apple DOS 3.3 <c>.dsk</c> / <c>.do</c> disk image (143 360 bytes) from
/// scratch (Write-Once, Read-Many).
/// </summary>
/// <remarks>
/// <para>
/// Layout: 35 tracks x 16 sectors x 256 bytes. VTOC lives at track 17, sector 0, and points
/// at the first catalog sector (we use track 17, sector 15 and chain backwards toward
/// sector 1). Each catalog sector holds seven 35-byte entries at offset 0x0B. Each file
/// has a T/S list (track 17 sectors are avoided for file data so the catalog stays intact)
/// that points at 122 data-sector pairs per T/S-list sector.
/// </para>
/// <para>
/// DOS 3.3 filenames are 30 bytes of high-bit-set ASCII padded with 0xA0. We upper-case and
/// truncate at 30 characters.
/// </para>
/// </remarks>
public sealed class AppleDosWriter {

  private const int TotalTracks = AppleDosReader.TracksPerDisk;           // 35
  private const int SectorsPerTrack = AppleDosReader.SectorsPerTrack;     // 16
  private const int SectorSize = AppleDosReader.SectorSize;               // 256
  private const int StandardSize = AppleDosReader.StandardSize;           // 143360
  private const int CatalogTrack = AppleDosReader.CatalogTrack;           // 17
  private const int VtocSector = AppleDosReader.VtocSector;               // 0
  /// <summary>DOS 3.3 convention: catalog chain starts at sector 15 and walks downward.</summary>
  private const int FirstCatalogSector = 15;
  private const int TsListPairsPerSector = 122;

  private readonly List<(string Name, byte FileType, byte[] Data)> _files = [];

  /// <summary>Adds a file to the disk image (default type = Binary 'B').</summary>
  public void AddFile(string name, byte[] data) => this._files.Add((name, FileType: 0x04, data));

  public void AddFile(string name, byte fileType, byte[] data) => this._files.Add((name, fileType, data));

  private static int SectorOffset(int track, int sector) =>
    track * SectorsPerTrack * SectorSize + sector * SectorSize;

  /// <summary>Builds the complete 143 360-byte image.</summary>
  public byte[] Build() {
    // Pre-flight: reject if nominal data overflows the disk's free sectors.
    // Usable data sectors = 35 tracks x 16 sectors - VTOC(1) - catalog track reserved(16)
    // Actually track 17 is entirely reserved for VTOC + catalog (16 sectors). So usable
    // data tracks = 34. Max usable = 34 * 16 * 256 = 139 264 bytes data + T/S list overhead.
    var totalPayload = 0L;
    foreach (var f in this._files) totalPayload += f.Data.Length;
    const long maxPayloadApprox = 34L * SectorsPerTrack * SectorSize;
    if (totalPayload > maxPayloadApprox)
      throw new InvalidOperationException(
        $"AppleDOS: combined file size {totalPayload} bytes exceeds disk capacity ~{maxPayloadApprox} bytes.");

    var disk = new byte[StandardSize];
    var used = new bool[TotalTracks, SectorsPerTrack];

    // Reserve entire track 17 (VTOC + catalog chain).
    for (var s = 0; s < SectorsPerTrack; s++) used[CatalogTrack, s] = true;

    // Walk files, allocating T/S list + data sectors.
    var dirEntries = new List<(string Name, byte FileType, int TslTrack, int TslSector, int SectorCount)>();
    var nextAllocTrack = 1;
    var nextAllocSector = 0;

    foreach (var (rawName, fileType, data) in this._files) {
      var name = SanitizeName(rawName);
      if (data.Length == 0) {
        // Empty files still need a T/S list sector to be listed.
        var (tslT, tslS) = AllocateSector(used, ref nextAllocTrack, ref nextAllocSector);
        if (tslT == 0)
          throw new InvalidOperationException("AppleDOS: out of space while allocating T/S list.");
        dirEntries.Add((name, fileType, tslT, tslS, 1));
        continue;
      }

      var sectorsNeeded = (data.Length + SectorSize - 1) / SectorSize;
      var dataSectors = new List<(int T, int S)>(sectorsNeeded);
      for (var i = 0; i < sectorsNeeded; i++) {
        var (t, s) = AllocateSector(used, ref nextAllocTrack, ref nextAllocSector);
        if (t == 0)
          throw new InvalidOperationException("AppleDOS: out of space while allocating data sectors.");
        dataSectors.Add((t, s));
      }

      // T/S list sectors hold up to 122 data-sector pointers each.
      var tslCount = (sectorsNeeded + TsListPairsPerSector - 1) / TsListPairsPerSector;
      if (tslCount == 0) tslCount = 1;
      var tslSectors = new List<(int T, int S)>(tslCount);
      for (var i = 0; i < tslCount; i++) {
        var (t, s) = AllocateSector(used, ref nextAllocTrack, ref nextAllocSector);
        if (t == 0)
          throw new InvalidOperationException("AppleDOS: out of space while allocating T/S list.");
        tslSectors.Add((t, s));
      }

      // Write data sector bodies.
      for (var i = 0; i < dataSectors.Count; i++) {
        var (t, s) = dataSectors[i];
        var off = SectorOffset(t, s);
        var remaining = data.Length - i * SectorSize;
        var chunk = Math.Min(SectorSize, remaining);
        Buffer.BlockCopy(data, i * SectorSize, disk, off, chunk);
      }

      // Populate T/S lists.
      for (var tslIdx = 0; tslIdx < tslSectors.Count; tslIdx++) {
        var (t, s) = tslSectors[tslIdx];
        var off = SectorOffset(t, s);
        // Byte 0: always 0. Bytes 1-2: next T/S list sector (track, sector) or (0,0) for last.
        if (tslIdx + 1 < tslSectors.Count) {
          disk[off + 1] = (byte)tslSectors[tslIdx + 1].T;
          disk[off + 2] = (byte)tslSectors[tslIdx + 1].S;
        }
        // Bytes 5-6: sector offset into file for the first pair stored here (LE).
        var sectorBase = tslIdx * TsListPairsPerSector;
        BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(off + 5), (ushort)sectorBase);

        // Pair table at offset 0x0C.
        var pairStart = tslIdx * TsListPairsPerSector;
        var pairEnd = Math.Min(pairStart + TsListPairsPerSector, dataSectors.Count);
        for (var p = pairStart; p < pairEnd; p++) {
          var pairOff = off + 0x0C + (p - pairStart) * 2;
          disk[pairOff + 0] = (byte)dataSectors[p].T;
          disk[pairOff + 1] = (byte)dataSectors[p].S;
        }
      }

      // Total sector count stored in the catalog entry = data sectors + T/S list sectors.
      dirEntries.Add((name, fileType, tslSectors[0].T, tslSectors[0].S,
                      dataSectors.Count + tslSectors.Count));
    }

    WriteCatalog(disk, dirEntries);
    WriteVtoc(disk, used);
    return disk;
  }

  /// <summary>Find next free sector (skipping track 17). Advances the rolling cursor.</summary>
  private static (int Track, int Sector) AllocateSector(bool[,] used, ref int startTrack, ref int startSector) {
    for (var tTries = 0; tTries < TotalTracks; tTries++) {
      var t = (startTrack + tTries) % TotalTracks;
      if (t == 0) continue;           // track 0 often reserved for boot — skip
      if (t == CatalogTrack) continue;
      for (var sTries = 0; sTries < SectorsPerTrack; sTries++) {
        var s = (startSector + sTries) % SectorsPerTrack;
        if (!used[t, s]) {
          used[t, s] = true;
          // advance cursor one past this allocation for next call
          startTrack = t;
          startSector = (s + 1) % SectorsPerTrack;
          if (startSector == 0) startTrack = (t + 1) % TotalTracks;
          return (t, s);
        }
      }
      startSector = 0; // no slots on this track; drop the offset on the next track
    }
    return (0, 0);
  }

  /// <summary>Sanitize a filename: upper-case, replace unrepresentable chars with '.', tail-truncate to 30.</summary>
  private static string SanitizeName(string raw) {
    if (string.IsNullOrEmpty(raw)) return "UNNAMED";
    var s = Path.GetFileName(raw).ToUpperInvariant();
    // Apple DOS 3.3 allows 0x20..0x7E minus the few that confuse the catalog (comma, control chars).
    // Replace anything outside printable ASCII with '.'; drop leading dots.
    var chars = new char[s.Length];
    for (var i = 0; i < s.Length; i++) {
      var c = s[i];
      chars[i] = (c >= 0x20 && c < 0x7F && c != ',') ? c : '.';
    }
    var clean = new string(chars);
    // DOS 3.3 cap = 30 bytes. If too long, keep the TAIL (matches user's "take last N chars" rule).
    if (clean.Length > 30) clean = clean[^30..];
    return clean;
  }

  private static void WriteCatalog(byte[] disk, List<(string Name, byte FileType, int TslTrack, int TslSector, int SectorCount)> entries) {
    // We walk the catalog chain starting at track 17 sector 15, going downward by one each time
    // (standard DOS 3.3 layout). Each catalog sector holds 7 entries.
    var catalogSectors = new List<int>();
    var neededSectors = Math.Max(1, (entries.Count + 6) / 7);
    for (var i = 0; i < neededSectors; i++) {
      var s = FirstCatalogSector - i;
      if (s <= 0)
        throw new InvalidOperationException("AppleDOS: too many directory entries (>98).");
      catalogSectors.Add(s);
    }

    for (var csIdx = 0; csIdx < catalogSectors.Count; csIdx++) {
      var sec = catalogSectors[csIdx];
      var off = SectorOffset(CatalogTrack, sec);

      // Header: byte 0 reserved, bytes 1-2 = next catalog (track, sector) or (0,0).
      if (csIdx + 1 < catalogSectors.Count) {
        disk[off + 1] = CatalogTrack;
        disk[off + 2] = (byte)catalogSectors[csIdx + 1];
      } else {
        disk[off + 1] = 0;
        disk[off + 2] = 0;
      }

      var firstEntry = csIdx * 7;
      var lastEntry = Math.Min(firstEntry + 7, entries.Count);
      for (var e = firstEntry; e < lastEntry; e++) {
        var entry = entries[e];
        var eo = off + 0x0B + (e - firstEntry) * 35;
        disk[eo + 0] = (byte)entry.TslTrack;
        disk[eo + 1] = (byte)entry.TslSector;
        disk[eo + 2] = entry.FileType;
        // Filename: 30 bytes, high-bit ASCII, space-padded with 0xA0.
        for (var i = 0; i < 30; i++) {
          if (i < entry.Name.Length) disk[eo + 3 + i] = (byte)(entry.Name[i] | 0x80);
          else disk[eo + 3 + i] = 0xA0;
        }
        BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(eo + 33), (ushort)entry.SectorCount);
      }
    }
  }

  private static void WriteVtoc(byte[] disk, bool[,] used) {
    var off = SectorOffset(CatalogTrack, VtocSector);

    disk[off + 0x00] = 0x00;                  // reserved
    disk[off + 0x01] = CatalogTrack;          // first catalog track
    disk[off + 0x02] = (byte)FirstCatalogSector;
    disk[off + 0x03] = 3;                     // DOS release
    disk[off + 0x06] = 254;                   // disk volume number
    disk[off + 0x27] = (byte)TsListPairsPerSector;  // pairs per T/S-list sector
    disk[off + 0x30] = (byte)1;               // last-allocated track
    disk[off + 0x31] = 1;                     // allocation direction (+1)
    disk[off + 0x34] = (byte)TotalTracks;     // tracks per disk
    disk[off + 0x35] = (byte)SectorsPerTrack; // sectors per track
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(off + 0x36), (ushort)SectorSize);

    // Per-track bitmap: 4 bytes each starting at offset 0x38. Bit SET = free.
    // Layout: byte0=sectors 15..8 (MSB=15), byte1=sectors 7..0 (MSB=7), byte2=0, byte3=0.
    for (var t = 0; t < TotalTracks; t++) {
      var bmOff = off + 0x38 + t * 4;
      byte hi = 0, lo = 0;
      for (var s = 8; s < 16; s++)
        if (!used[t, s]) hi |= (byte)(1 << (s - 8));
      for (var s = 0; s < 8; s++)
        if (!used[t, s]) lo |= (byte)(1 << s);
      // Reader doesn't validate this layout — but real DOS cares about specific bit order.
      // Standard DOS 3.3 stores bits 15-8 in byte 0 MSB-first, bits 7-0 in byte 1 MSB-first.
      byte b0 = 0, b1 = 0;
      for (var s = 0; s < 8; s++) {
        if ((hi & (1 << s)) != 0) b0 |= (byte)(1 << (7 - s));
        if ((lo & (1 << s)) != 0) b1 |= (byte)(1 << (7 - s));
      }
      disk[bmOff + 0] = b0;
      disk[bmOff + 1] = b1;
      disk[bmOff + 2] = 0;
      disk[bmOff + 3] = 0;
    }
  }

  /// <summary>Escape hatch for callers that prefer to operate on an already-prepared List/Stream.</summary>
  public static byte[] BuildFrom(IEnumerable<(string Name, byte[] Data)> files) {
    var w = new AppleDosWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    return w.Build();
  }
}
