#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.AppleDos;

/// <summary>
/// Random-access in-place modifier for Apple DOS 3.3 disk images. Reads and
/// writes only the VTOC, the catalog chain, the new file's T/S list, and the
/// new file's data sectors — never the whole image. Lets the host operate on
/// huge underlying streams without paging the entire disk into memory.
/// </summary>
public static class AppleDosModifier {

  private const int TotalTracks = AppleDosReader.TracksPerDisk;       // 35
  private const int SectorsPerTrack = AppleDosReader.SectorsPerTrack; // 16
  private const int SectorSize = AppleDosReader.SectorSize;           // 256
  private const int CatalogTrack = AppleDosReader.CatalogTrack;       // 17
  private const int VtocSector = AppleDosReader.VtocSector;           // 0
  private const int FirstCatalogSector = 15;
  private const int TsListPairsPerSector = 122;

  /// <summary>
  /// Adds a file to an existing image. Caller is responsible for ensuring the
  /// name does not already exist (use <see cref="RemoveFile"/> first for
  /// replace-by-name semantics).
  /// </summary>
  public static void AddFile(Stream image, string name, byte[] data, byte fileType = 0x04) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var sanitized = SanitizeName(name);
    var vtoc = ReadSector(image, CatalogTrack, VtocSector);
    var bitmap = DecodeBitmap(vtoc);

    // Allocate sectors: data sectors first, then T/S list sectors.
    var sectorsNeeded = data.Length == 0 ? 0 : (data.Length + SectorSize - 1) / SectorSize;
    var dataSectors = new List<(int T, int S)>(sectorsNeeded);
    for (var i = 0; i < sectorsNeeded; i++) {
      var alloc = AllocateSector(bitmap);
      if (alloc.T == 0)
        throw new InvalidOperationException("AppleDOS: out of space for data sectors.");
      dataSectors.Add(alloc);
    }

    var tslCount = Math.Max(1, (sectorsNeeded + TsListPairsPerSector - 1) / TsListPairsPerSector);
    var tslSectors = new List<(int T, int S)>(tslCount);
    for (var i = 0; i < tslCount; i++) {
      var alloc = AllocateSector(bitmap);
      if (alloc.T == 0)
        throw new InvalidOperationException("AppleDOS: out of space for T/S list.");
      tslSectors.Add(alloc);
    }

    // Write data sector bodies (stream them — only the sectors we touch).
    for (var i = 0; i < dataSectors.Count; i++) {
      var (t, s) = dataSectors[i];
      var buf = new byte[SectorSize];
      var remaining = data.Length - i * SectorSize;
      var chunk = Math.Min(SectorSize, remaining);
      Buffer.BlockCopy(data, i * SectorSize, buf, 0, chunk);
      WriteSector(image, t, s, buf);
    }

    // Build and write T/S list sectors.
    for (var idx = 0; idx < tslSectors.Count; idx++) {
      var (t, s) = tslSectors[idx];
      var buf = new byte[SectorSize];
      if (idx + 1 < tslSectors.Count) {
        buf[1] = (byte)tslSectors[idx + 1].T;
        buf[2] = (byte)tslSectors[idx + 1].S;
      }
      var sectorBase = idx * TsListPairsPerSector;
      BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(5), (ushort)sectorBase);

      var pairStart = idx * TsListPairsPerSector;
      var pairEnd = Math.Min(pairStart + TsListPairsPerSector, dataSectors.Count);
      for (var p = pairStart; p < pairEnd; p++) {
        var pairOff = 0x0C + (p - pairStart) * 2;
        buf[pairOff + 0] = (byte)dataSectors[p].T;
        buf[pairOff + 1] = (byte)dataSectors[p].S;
      }
      WriteSector(image, t, s, buf);
    }

    // Insert directory entry — find a free slot in the catalog chain, extend if needed.
    var firstCatTrack = vtoc[0x01];
    var firstCatSector = vtoc[0x02];
    var slot = FindFreeDirectorySlot(image, firstCatTrack, firstCatSector);
    if (slot.Sector == 0) {
      // Catalog is full; extend the chain by allocating a new catalog sector.
      // DOS 3.3 convention: walk downward on track 17 from sector 15. Find the
      // lowest unused catalog sector still on track 17 and link the previous tail to it.
      slot = ExtendCatalogChain(image, bitmap, firstCatTrack, firstCatSector);
    }

    var dirSectorBuf = ReadSector(image, slot.SectorTrack, slot.Sector);
    var entryOffset = 0x0B + slot.IndexInSector * 35;
    dirSectorBuf[entryOffset + 0] = (byte)tslSectors[0].T;
    dirSectorBuf[entryOffset + 1] = (byte)tslSectors[0].S;
    dirSectorBuf[entryOffset + 2] = fileType;
    for (var i = 0; i < 30; i++)
      dirSectorBuf[entryOffset + 3 + i] = i < sanitized.Length
        ? (byte)(sanitized[i] | 0x80)
        : (byte)0xA0;
    BinaryPrimitives.WriteUInt16LittleEndian(
      dirSectorBuf.AsSpan(entryOffset + 33),
      (ushort)(dataSectors.Count + tslSectors.Count));
    WriteSector(image, slot.SectorTrack, slot.Sector, dirSectorBuf);

    // Persist updated bitmap.
    EncodeBitmap(vtoc, bitmap);
    WriteSector(image, CatalogTrack, VtocSector, vtoc);
  }

  /// <summary>
  /// Removes a named file from the image. Returns true if found and removed.
  /// When <paramref name="wipeData"/> is true, data sectors are zeroed.
  /// </summary>
  public static bool RemoveFile(Stream image, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);

    var sanitized = SanitizeName(name);
    var vtoc = ReadSector(image, CatalogTrack, VtocSector);
    var bitmap = DecodeBitmap(vtoc);

    var locator = LocateDirectoryEntry(image, vtoc[0x01], vtoc[0x02], sanitized);
    if (!locator.Found) return false;

    var dirSector = ReadSector(image, locator.SectorTrack, locator.Sector);
    var entryOff = 0x0B + locator.IndexInSector * 35;
    var tslTrack = dirSector[entryOff + 0];
    var tslSector = dirSector[entryOff + 1];

    // Walk the T/S list chain, gather data + T/S sectors, free the bitmap bits.
    var visited = new HashSet<(int, int)>();
    var dataSectors = new List<(int T, int S)>();
    var tslChain = new List<(int T, int S)>();
    var t = (int)tslTrack;
    var s = (int)tslSector;
    while (t != 0 && visited.Add((t, s))) {
      if (t < 0 || t >= TotalTracks || s < 0 || s >= SectorsPerTrack) break;
      tslChain.Add((t, s));
      var sec = ReadSector(image, t, s);
      var nextT = sec[0x01];
      var nextS = sec[0x02];
      for (var i = 0; i < TsListPairsPerSector; i++) {
        var dT = sec[0x0C + i * 2 + 0];
        var dS = sec[0x0C + i * 2 + 1];
        if (dT == 0 && dS == 0) break;
        dataSectors.Add((dT, dS));
      }
      t = nextT; s = nextS;
    }

    // Wipe data sectors if requested.
    if (wipeData) {
      var zero = new byte[SectorSize];
      foreach (var (dt, ds) in dataSectors)
        WriteSector(image, dt, ds, zero);
    }

    // Free all sectors in the bitmap.
    foreach (var (dt, ds) in dataSectors) MarkFree(bitmap, dt, ds);
    foreach (var (xt, xs) in tslChain) MarkFree(bitmap, xt, xs);

    // Mark the directory entry deleted: byte 0 (tsListTrack) becomes 0xFF;
    // entry's first filename byte takes the original tsListTrack so DOS can recover.
    dirSector[entryOff + 0x20] = dirSector[entryOff + 0]; // store original T at +0x20
    dirSector[entryOff + 0] = 0xFF;
    WriteSector(image, locator.SectorTrack, locator.Sector, dirSector);

    EncodeBitmap(vtoc, bitmap);
    WriteSector(image, CatalogTrack, VtocSector, vtoc);
    return true;
  }

  // ── Sector I/O ────────────────────────────────────────────────────────

  private static long SectorOffset(int track, int sector) =>
    (long)track * SectorsPerTrack * SectorSize + (long)sector * SectorSize;

  private static byte[] ReadSector(Stream s, int track, int sector) {
    var buf = new byte[SectorSize];
    s.Position = SectorOffset(track, sector);
    var read = 0;
    while (read < SectorSize) {
      var n = s.Read(buf, read, SectorSize - read);
      if (n <= 0) break;
      read += n;
    }
    return buf;
  }

  private static void WriteSector(Stream s, int track, int sector, byte[] data) {
    s.Position = SectorOffset(track, sector);
    s.Write(data, 0, SectorSize);
  }

  // ── Bitmap helpers ────────────────────────────────────────────────────
  // Bitmap is stored in VTOC at offset 0x38, 4 bytes per track:
  //   byte 0: sectors 15..8 (MSB = sector 15, LSB = sector 8)
  //   byte 1: sectors 7..0  (MSB = sector 7,  LSB = sector 0)
  //   bytes 2-3: zero
  // Bit SET = free.

  private sealed class BitMap {
    public bool[,] Free = new bool[TotalTracks, SectorsPerTrack];
  }

  private static BitMap DecodeBitmap(byte[] vtoc) {
    var bm = new BitMap();
    for (var t = 0; t < TotalTracks; t++) {
      var off = 0x38 + t * 4;
      var b0 = vtoc[off + 0];
      var b1 = vtoc[off + 1];
      // byte 0 bit 7 = sector 8, bit 6 = sector 9, ..., bit 0 = sector 15
      for (var bit = 0; bit < 8; bit++) {
        if ((b0 & (1 << bit)) != 0) bm.Free[t, 15 - bit] = true;
        if ((b1 & (1 << bit)) != 0) bm.Free[t, 7 - bit] = true;
      }
    }
    return bm;
  }

  private static void EncodeBitmap(byte[] vtoc, BitMap bm) {
    for (var t = 0; t < TotalTracks; t++) {
      var off = 0x38 + t * 4;
      byte b0 = 0, b1 = 0;
      for (var bit = 0; bit < 8; bit++) {
        if (bm.Free[t, 15 - bit]) b0 |= (byte)(1 << bit);
        if (bm.Free[t, 7 - bit]) b1 |= (byte)(1 << bit);
      }
      vtoc[off + 0] = b0;
      vtoc[off + 1] = b1;
      vtoc[off + 2] = 0;
      vtoc[off + 3] = 0;
    }
  }

  private static (int T, int S) AllocateSector(BitMap bm) {
    for (var t = 1; t < TotalTracks; t++) {
      if (t == CatalogTrack) continue;
      for (var s = 0; s < SectorsPerTrack; s++) {
        if (bm.Free[t, s]) {
          bm.Free[t, s] = false;
          return (t, s);
        }
      }
    }
    return (0, 0);
  }

  private static void MarkFree(BitMap bm, int t, int s) {
    if (t < 0 || t >= TotalTracks || s < 0 || s >= SectorsPerTrack) return;
    bm.Free[t, s] = true;
  }

  // ── Directory navigation ──────────────────────────────────────────────

  private readonly record struct DirSlot(int SectorTrack, int Sector, int IndexInSector) {
    public bool Found => Sector != 0;
  }

  private readonly record struct DirLocator(bool Found, int SectorTrack, int Sector, int IndexInSector);

  private static DirSlot FindFreeDirectorySlot(Stream image, int firstCatTrack, int firstCatSector) {
    var t = firstCatTrack;
    var s = firstCatSector;
    var visited = new HashSet<(int, int)>();
    while (t != 0 && visited.Add((t, s))) {
      if (t < 0 || t >= TotalTracks || s < 0 || s >= SectorsPerTrack) break;
      var sec = ReadSector(image, t, s);
      for (var i = 0; i < 7; i++) {
        var eo = 0x0B + i * 35;
        var first = sec[eo + 0];
        // 0x00 = never used; 0xFF = deleted (reusable). Either is a free slot.
        if (first == 0x00 || first == 0xFF) return new DirSlot(t, s, i);
      }
      t = sec[0x01];
      s = sec[0x02];
    }
    return new DirSlot(0, 0, 0);
  }

  /// <summary>
  /// Allocates a fresh catalog sector on track 17 (the lowest unused sector
  /// below the current chain tail) and links the previous tail to it. Returns
  /// the first free entry in the new sector.
  /// </summary>
  private static DirSlot ExtendCatalogChain(Stream image, BitMap bm, int firstCatTrack, int firstCatSector) {
    // Walk to the tail of the current catalog chain.
    var t = firstCatTrack;
    var s = firstCatSector;
    var visited = new HashSet<(int, int)>();
    var lastT = t; var lastS = s;
    var lowestSeen = s;
    while (t != 0 && visited.Add((t, s))) {
      if (t < 0 || t >= TotalTracks || s < 0 || s >= SectorsPerTrack) break;
      lastT = t; lastS = s;
      lowestSeen = Math.Min(lowestSeen, s);
      var sec = ReadSector(image, t, s);
      var nextT = sec[0x01];
      var nextS = sec[0x02];
      if (nextT == 0) break;
      t = nextT; s = nextS;
    }

    // Pick a fresh catalog sector on track 17 — walk downward from one below the current lowest.
    var newSector = -1;
    for (var cand = lowestSeen - 1; cand >= 1; cand--) {
      // Catalog sector is "in use" implicitly because track 17 isn't tracked in the data bitmap.
      // We just need a sector that isn't already on the existing catalog chain.
      if (!visited.Contains((CatalogTrack, cand))) { newSector = cand; break; }
    }
    if (newSector < 1)
      throw new InvalidOperationException("AppleDOS: catalog chain exhausted (>98 entries).");

    // Link previous tail to the new sector.
    var tailBuf = ReadSector(image, lastT, lastS);
    tailBuf[0x01] = CatalogTrack;
    tailBuf[0x02] = (byte)newSector;
    WriteSector(image, lastT, lastS, tailBuf);

    // Write a fresh empty catalog sector (zeroed → all 7 entries marked never-used).
    WriteSector(image, CatalogTrack, newSector, new byte[SectorSize]);
    return new DirSlot(CatalogTrack, newSector, 0);
  }

  private static DirLocator LocateDirectoryEntry(Stream image, int firstCatTrack, int firstCatSector, string name) {
    var t = firstCatTrack;
    var s = firstCatSector;
    var visited = new HashSet<(int, int)>();
    while (t != 0 && visited.Add((t, s))) {
      if (t < 0 || t >= TotalTracks || s < 0 || s >= SectorsPerTrack) break;
      var sec = ReadSector(image, t, s);
      for (var i = 0; i < 7; i++) {
        var eo = 0x0B + i * 35;
        var first = sec[eo + 0];
        if (first == 0x00 || first == 0xFF) continue;
        // Decode filename and compare (high-bit ASCII, 0xA0 padding).
        var nameBuf = new byte[30];
        for (var j = 0; j < 30; j++) nameBuf[j] = (byte)(sec[eo + 3 + j] & 0x7F);
        var nameLen = 30;
        while (nameLen > 0 && nameBuf[nameLen - 1] == (0xA0 & 0x7F)) nameLen--;
        var entryName = Encoding.ASCII.GetString(nameBuf, 0, nameLen).TrimEnd();
        if (entryName == name) return new DirLocator(true, t, s, i);
      }
      t = sec[0x01]; s = sec[0x02];
    }
    return new DirLocator(false, 0, 0, 0);
  }

  // ── Name sanitisation (mirrors AppleDosWriter) ───────────────────────

  private static string SanitizeName(string raw) {
    if (string.IsNullOrEmpty(raw)) return "UNNAMED";
    var s = Path.GetFileName(raw).ToUpperInvariant();
    var chars = new char[s.Length];
    for (var i = 0; i < s.Length; i++) {
      var c = s[i];
      chars[i] = (c >= 0x20 && c < 0x7F && c != ',') ? c : '.';
    }
    var clean = new string(chars);
    if (clean.Length > 30) clean = clean[^30..];
    return clean;
  }
}
