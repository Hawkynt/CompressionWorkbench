#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Atari8;

/// <summary>
/// Random-access in-place modifier for Atari 8-bit <c>.atr</c> images
/// (AtariDOS 2.x layout). Reads and writes only the ATR header, the VTOC
/// (sector 360), the touched directory sector(s), and the file's data
/// sectors — never the whole image. Supports SS/SD (128-byte) and DD
/// (256-byte) images.
/// </summary>
public static class Atari8Modifier {

  private const int AtrHeaderSize = Atari8Reader.AtrHeaderSize;             // 16
  private const int VtocSector = 360;
  private const int DirectoryStartSector = Atari8Reader.DirectoryStartSector;       // 361
  private const int DirectorySectorCount = Atari8Reader.DirectorySectorCount;       // 8
  private const int EntriesPerDirectorySector = Atari8Reader.EntriesPerDirectorySector; // 8
  private const int DirectoryEntrySize = Atari8Reader.DirectoryEntrySize;   // 16
  private const int MaxEntries = DirectorySectorCount * EntriesPerDirectorySector;  // 64
  private const int FirstUsableDataSector = 4;
  private const int TotalSectors = 720;

  /// <summary>
  /// Adds a file to an existing image. Caller is responsible for ensuring the
  /// name does not already exist (use <see cref="RemoveFile"/> first for
  /// replace-by-name semantics).
  /// </summary>
  public static void AddFile(Stream image, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var sectorSize = ReadSectorSize(image);
    var (baseName, ext) = SplitName(name);

    var vtoc = ReadSector(image, sectorSize, VtocSector);
    var bitmap = DecodeBitmap(vtoc);

    var slot = FindFreeDirectorySlot(image, sectorSize);
    if (slot < 0)
      throw new InvalidOperationException("AtariDOS: directory full (no free entry slot).");

    // Allocate sectors needed.
    var payload = sectorSize - 3;
    var sectorsNeeded = data.Length == 0 ? 1 : (data.Length + payload - 1) / payload;
    if (sectorsNeeded == 0) sectorsNeeded = 1;
    var sectors = new List<int>(sectorsNeeded);
    for (var i = 0; i < sectorsNeeded; i++) {
      var s = AllocateSector(bitmap);
      if (s < 0) throw new InvalidOperationException("AtariDOS: out of free sectors.");
      sectors.Add(s);
    }

    // Write data sectors with chain trailer (file# = slot index, next ptr, byte count).
    for (var i = 0; i < sectors.Count; i++) {
      var buf = new byte[sectorSize];
      var dataStart = i * payload;
      var thisChunk = Math.Min(payload, Math.Max(0, data.Length - dataStart));
      if (thisChunk > 0)
        Buffer.BlockCopy(data, dataStart, buf, 0, thisChunk);
      var nextSector = i + 1 < sectors.Count ? sectors[i + 1] : 0;
      var fileNo = slot & 0x3F;
      var nextHi = (nextSector >> 8) & 0x03;
      buf[sectorSize - 3] = (byte)((fileNo << 2) | nextHi);
      buf[sectorSize - 2] = (byte)(nextSector & 0xFF);
      buf[sectorSize - 1] = (byte)(thisChunk & 0x7F);
      WriteSector(image, sectorSize, sectors[i], buf);
    }

    // Write directory entry into its sector.
    var dirSector = DirectoryStartSector + slot / EntriesPerDirectorySector;
    var slotInSector = slot % EntriesPerDirectorySector;
    var dirBuf = ReadSector(image, sectorSize, dirSector);
    var entryOff = slotInSector * DirectoryEntrySize;
    dirBuf[entryOff + 0] = 0x42; // in-use + DOS-2 file
    BinaryPrimitives.WriteUInt16LittleEndian(dirBuf.AsSpan(entryOff + 1), (ushort)sectors.Count);
    BinaryPrimitives.WriteUInt16LittleEndian(dirBuf.AsSpan(entryOff + 3), (ushort)sectors[0]);
    for (var i = 0; i < 8; i++)
      dirBuf[entryOff + 5 + i] = (byte)(i < baseName.Length ? baseName[i] : ' ');
    for (var i = 0; i < 3; i++)
      dirBuf[entryOff + 13 + i] = (byte)(i < ext.Length ? ext[i] : ' ');
    WriteSector(image, sectorSize, dirSector, dirBuf);

    // Persist VTOC.
    EncodeBitmap(vtoc, bitmap);
    WriteSector(image, sectorSize, VtocSector, vtoc);
  }

  /// <summary>
  /// Removes a named file from the image. Returns true if found and removed.
  /// When <paramref name="wipeData"/> is true, data sectors are zeroed.
  /// </summary>
  public static bool RemoveFile(Stream image, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);

    var sectorSize = ReadSectorSize(image);
    var (baseName, ext) = SplitName(name);

    var vtoc = ReadSector(image, sectorSize, VtocSector);
    var bitmap = DecodeBitmap(vtoc);

    var locator = LocateDirectoryEntry(image, sectorSize, baseName, ext);
    if (!locator.Found) return false;

    var dirBuf = ReadSector(image, sectorSize, locator.DirSector);
    var entryOff = locator.SlotInSector * DirectoryEntrySize;
    var startSector = BinaryPrimitives.ReadUInt16LittleEndian(dirBuf.AsSpan(entryOff + 3));

    // Walk chain, collect sectors to free.
    var dataSectors = new List<int>();
    var visited = new HashSet<int>();
    var current = (int)startSector;
    while (current != 0 && visited.Add(current)) {
      if (current < 1 || current > TotalSectors) break;
      dataSectors.Add(current);
      var sec = ReadSector(image, sectorSize, current);
      var b0 = sec[sectorSize - 3];
      var b1 = sec[sectorSize - 2];
      var next = ((b0 & 0x03) << 8) | b1;
      if (next == 0) break;
      current = next;
    }

    if (wipeData) {
      var zero = new byte[sectorSize];
      foreach (var s in dataSectors) WriteSector(image, sectorSize, s, zero);
    }

    foreach (var s in dataSectors) MarkFree(bitmap, s);

    // Mark directory entry deleted: clear in-use, set deleted bit.
    dirBuf[entryOff + 0] = 0x80;
    WriteSector(image, sectorSize, locator.DirSector, dirBuf);

    EncodeBitmap(vtoc, bitmap);
    WriteSector(image, sectorSize, VtocSector, vtoc);
    return true;
  }

  // ── Sector I/O ────────────────────────────────────────────────────────

  /// <summary>
  /// Computes the file offset for a 1-based sector. Mirrors
  /// <see cref="Atari8Reader"/>'s quirk: in DD images sectors 1-3 are still
  /// 128 bytes long (boot sectors), and the 256-byte region begins at sector 4.
  /// </summary>
  private static long SectorOffset(int sectorSize, int sector1Based) {
    if (sectorSize == 256 && sector1Based <= 3)
      return AtrHeaderSize + (long)(sector1Based - 1) * 128;
    if (sectorSize == 256) {
      var headStart = AtrHeaderSize + 3L * 128;
      var idx = sector1Based - 1 - 3;
      return headStart + (long)idx * 256;
    }
    return AtrHeaderSize + (long)(sector1Based - 1) * 128;
  }

  private static byte[] ReadSector(Stream s, int sectorSize, int sector1Based) {
    var size = sectorSize == 256 && sector1Based <= 3 ? 128 : sectorSize;
    var buf = new byte[size];
    s.Position = SectorOffset(sectorSize, sector1Based);
    var read = 0;
    while (read < size) {
      var n = s.Read(buf, read, size - read);
      if (n <= 0) break;
      read += n;
    }
    return buf;
  }

  private static void WriteSector(Stream s, int sectorSize, int sector1Based, byte[] data) {
    var size = sectorSize == 256 && sector1Based <= 3 ? 128 : sectorSize;
    s.Position = SectorOffset(sectorSize, sector1Based);
    s.Write(data, 0, Math.Min(size, data.Length));
  }

  private static int ReadSectorSize(Stream s) {
    Span<byte> hdr = stackalloc byte[6];
    var origPos = s.Position;
    s.Position = 0;
    s.ReadExactly(hdr);
    s.Position = origPos;
    if (hdr[0] != 0x96 || hdr[1] != 0x02)
      throw new InvalidDataException("ATR: missing 0x0296 magic.");
    var raw = BinaryPrimitives.ReadUInt16LittleEndian(hdr.Slice(4, 2));
    return raw == 0 ? 128 : raw;
  }

  // ── Bitmap helpers ────────────────────────────────────────────────────
  // VTOC bitmap at sector 360, bytes 10..99. Bit SET = free.
  // Bit for sector N: byte (N/8)+10, mask 0x80 >> (N%8).

  private static bool[] DecodeBitmap(byte[] vtoc) {
    var bits = new bool[TotalSectors + 1]; // index 1..720
    for (var s = 1; s <= TotalSectors; s++) {
      var byteIdx = 10 + s / 8;
      var bitMask = 0x80 >> (s % 8);
      if (byteIdx < vtoc.Length && (vtoc[byteIdx] & bitMask) != 0)
        bits[s] = true;
    }
    return bits;
  }

  private static void EncodeBitmap(byte[] vtoc, bool[] bits) {
    var free = 0;
    for (var b = 10; b <= 99 && b < vtoc.Length; b++) vtoc[b] = 0;
    for (var s = 1; s <= TotalSectors; s++) {
      if (!bits[s]) continue;
      free++;
      var byteIdx = 10 + s / 8;
      var bitMask = (byte)(0x80 >> (s % 8));
      if (byteIdx < vtoc.Length) vtoc[byteIdx] |= bitMask;
    }
    BinaryPrimitives.WriteUInt16LittleEndian(vtoc.AsSpan(3), (ushort)free);
  }

  private static int AllocateSector(bool[] bitmap) {
    for (var s = FirstUsableDataSector; s <= TotalSectors; s++) {
      if (s == VtocSector) continue;
      if (s >= DirectoryStartSector && s < DirectoryStartSector + DirectorySectorCount) continue;
      if (s > 0x3FF) break; // 10-bit chain pointer cap
      if (bitmap[s]) {
        bitmap[s] = false;
        return s;
      }
    }
    return -1;
  }

  private static void MarkFree(bool[] bitmap, int sector) {
    if (sector >= 1 && sector <= TotalSectors) bitmap[sector] = true;
  }

  // ── Directory navigation ──────────────────────────────────────────────

  private static int FindFreeDirectorySlot(Stream image, int sectorSize) {
    for (var i = 0; i < DirectorySectorCount; i++) {
      var sec = ReadSector(image, sectorSize, DirectoryStartSector + i);
      for (var j = 0; j < EntriesPerDirectorySector; j++) {
        var flags = sec[j * DirectoryEntrySize];
        if (flags == 0x00 || (flags & 0x80) != 0) // never-used or deleted
          return i * EntriesPerDirectorySector + j;
      }
    }
    return -1;
  }

  private readonly record struct DirLocator(bool Found, int DirSector, int SlotInSector);

  private static DirLocator LocateDirectoryEntry(Stream image, int sectorSize, string baseName, string ext) {
    for (var i = 0; i < DirectorySectorCount; i++) {
      var sec = ReadSector(image, sectorSize, DirectoryStartSector + i);
      for (var j = 0; j < EntriesPerDirectorySector; j++) {
        var entryOff = j * DirectoryEntrySize;
        var flags = sec[entryOff];
        if ((flags & 0x40) == 0) continue; // not in-use
        if ((flags & 0x80) != 0) continue; // deleted
        var n = Encoding.ASCII.GetString(sec, entryOff + 5, 8).TrimEnd();
        var x = Encoding.ASCII.GetString(sec, entryOff + 13, 3).TrimEnd();
        if (n == baseName && x == ext)
          return new DirLocator(true, DirectoryStartSector + i, j);
      }
    }
    return new DirLocator(false, 0, 0);
  }

  // ── Name handling (mirrors Atari8Writer) ─────────────────────────────

  private static (string BaseName, string Ext) SplitName(string raw) {
    if (string.IsNullOrEmpty(raw)) return ("UNNAMED", "");
    var file = Path.GetFileName(raw).ToUpperInvariant();
    var dot = file.LastIndexOf('.');
    string baseName, ext;
    if (dot < 0) { baseName = file; ext = ""; }
    else { baseName = file[..dot]; ext = file[(dot + 1)..]; }
    baseName = SanitizeAtascii(baseName);
    ext = SanitizeAtascii(ext);
    if (baseName.Length > 8) baseName = baseName[^8..];
    if (ext.Length > 3) ext = ext[..3];
    if (baseName.Length == 0) baseName = "UNNAMED";
    return (baseName, ext);
  }

  private static string SanitizeAtascii(string s) {
    var chars = new char[s.Length];
    for (var i = 0; i < s.Length; i++) {
      var c = s[i];
      chars[i] = c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') ? c : '_';
    }
    var clean = new string(chars).TrimStart('_');
    if (clean.Length > 0 && char.IsDigit(clean[0])) clean = "F" + clean;
    return clean;
  }
}
