#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.TrDos;

/// <summary>
/// Random-access in-place modifier for ZX Spectrum TR-DOS <c>.trd</c>
/// images. Only the directory sectors (0..7), the disk-info sector (8),
/// and the file's contiguous data run are read or written; the rest of
/// the disk is untouched. Files are placed in the lowest contiguous gap
/// large enough above the directory area, and deletion follows TR-DOS
/// convention (first filename byte = 0x01) so the slot can be reused.
/// </summary>
public static class TrDosModifier {

  private const int SectorSize = 256;
  private const int SectorsPerTrack = 16;
  private const int TrackSize = SectorSize * SectorsPerTrack; // 4096
  private const int DirEntrySize = 16;
  private const int MaxDirEntries = 128;        // 8 * 256 / 16
  private const int DirSectorCount = 8;          // sectors 0..7 of track 0
  private const int DiskInfoSector = 8;          // track 0, sector 8
  private const int DiskInfoOffset = DiskInfoSector * SectorSize; // 0x800
  private const byte TrDosIdByte = 0x10;
  private const byte DeletedMarker = 0x01;

  /// <summary>
  /// Adds a file to an existing TR-DOS image. Caller is responsible for
  /// ensuring the name does not already exist (use <see cref="RemoveFile"/>
  /// first for replace-by-name semantics). The file lands in the lowest
  /// contiguous free run above the directory area.
  /// </summary>
  public static void AddFile(Stream image, string name, byte type, byte[] data) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var totalSectors = ImageTotalSectors(image);
    var dir = ReadDirectory(image);
    var info = ReadSector(image, DiskInfoSector);
    if (info[0xE7] != TrDosIdByte)
      throw new InvalidDataException("TR-DOS: invalid ID byte in disk info sector.");

    var entries = ParseDirectory(dir);
    var slot = FindFreeDirSlot(dir);
    if (slot < 0)
      throw new InvalidOperationException("TR-DOS: directory full (128 entries).");

    var sectorsNeeded = Math.Max(1, (data.Length + SectorSize - 1) / SectorSize);
    var startLinear = FindContiguousGap(entries, sectorsNeeded, totalSectors);
    if (startLinear < 0)
      throw new InvalidOperationException(
        $"TR-DOS: no contiguous gap large enough for {sectorsNeeded} sectors.");

    if (data.Length > 0)
      WriteRun(image, startLinear, data);

    // Build directory entry.
    var entry = new byte[DirEntrySize];
    var sanitized = SanitizeName(name);
    Encoding.ASCII.GetBytes(sanitized).CopyTo(entry, 0);
    entry[8] = SanitizeType(type);
    BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(9), 0); // param1
    var dataSize = data.Length > ushort.MaxValue ? ushort.MaxValue : (ushort)data.Length;
    BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(11), dataSize); // param2
    entry[13] = (byte)sectorsNeeded;
    entry[14] = (byte)(startLinear % SectorsPerTrack);
    entry[15] = (byte)(startLinear / SectorsPerTrack);

    // Splice into the in-memory directory image and flush only the affected sector.
    var entryOffset = slot * DirEntrySize;
    Array.Copy(entry, 0, dir, entryOffset, DirEntrySize);
    WriteSector(image, entryOffset / SectorSize, SectorSlice(dir, entryOffset / SectorSize));

    // Update disk-info sector: file count, free sector/track cursor, free count.
    var endLinear = ComputeFreeCursor(entries, startLinear, sectorsNeeded);
    info[0xE1] = (byte)(endLinear % SectorsPerTrack);
    info[0xE2] = (byte)(endLinear / SectorsPerTrack);
    info[0xE4] = (byte)Math.Min(255, CountActive(dir) + 0); // dir already updated
    var freeAfter = Math.Max(0, totalSectors - endLinear);
    BinaryPrimitives.WriteUInt16LittleEndian(info.AsSpan(0xE5), (ushort)Math.Min(ushort.MaxValue, freeAfter));
    WriteSector(image, DiskInfoSector, info);
  }

  /// <summary>
  /// Removes a file from the image. Uses TR-DOS convention: sets the
  /// first byte of the filename to 0x01. When <paramref name="wipeData"/>
  /// is true, the data sectors are zeroed. Returns true if the file was
  /// found and removed.
  /// </summary>
  public static bool RemoveFile(Stream image, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);

    var dir = ReadDirectory(image);
    var info = ReadSector(image, DiskInfoSector);
    if (info[0xE7] != TrDosIdByte)
      throw new InvalidDataException("TR-DOS: invalid ID byte in disk info sector.");

    var sanitized = SanitizeName(name);
    var slot = FindEntry(dir, sanitized);
    if (slot < 0) return false;

    var entryOffset = slot * DirEntrySize;
    var lengthSectors = dir[entryOffset + 13];
    var startSector = dir[entryOffset + 14];
    var startTrack = dir[entryOffset + 15];
    var startLinear = startTrack * SectorsPerTrack + startSector;

    if (wipeData && lengthSectors > 0) {
      var zero = new byte[SectorSize];
      for (var k = 0; k < lengthSectors; k++)
        WriteSector(image, startLinear + k, zero);
    }

    // Mark deleted: filename[0] = 0x01.
    dir[entryOffset] = DeletedMarker;
    WriteSector(image, entryOffset / SectorSize, SectorSlice(dir, entryOffset / SectorSize));

    // Bump deleted count and decrement file count.
    var deleted = info[0xF4];
    info[0xF4] = (byte)Math.Min(255, deleted + 1);
    if (info[0xE4] > 0) info[0xE4]--;
    WriteSector(image, DiskInfoSector, info);
    return true;
  }

  // ── Directory helpers ───────────────────────────────────────────────

  private sealed class DirEntry {
    public int StartLinear;   // track*16 + sector
    public int LengthSectors;
  }

  private static List<DirEntry> ParseDirectory(byte[] dir) {
    var list = new List<DirEntry>();
    for (var i = 0; i < MaxDirEntries; i++) {
      var off = i * DirEntrySize;
      var b0 = dir[off];
      if (b0 == 0x00) break;       // end of directory
      if (b0 == DeletedMarker) continue;
      int len = dir[off + 13];
      int sec = dir[off + 14];
      int trk = dir[off + 15];
      list.Add(new DirEntry {
        StartLinear = trk * SectorsPerTrack + sec,
        LengthSectors = Math.Max(1, len),
      });
    }
    return list;
  }

  private static int FindFreeDirSlot(byte[] dir) {
    var firstEnd = -1;
    for (var i = 0; i < MaxDirEntries; i++) {
      var off = i * DirEntrySize;
      var b0 = dir[off];
      if (b0 == DeletedMarker) return i;       // reuse deleted slot
      if (b0 == 0x00 && firstEnd < 0) firstEnd = i;
    }
    return firstEnd;
  }

  private static int FindEntry(byte[] dir, string sanitizedName) {
    var needle = sanitizedName.TrimEnd();
    for (var i = 0; i < MaxDirEntries; i++) {
      var off = i * DirEntrySize;
      var b0 = dir[off];
      if (b0 == 0x00) break;
      if (b0 == DeletedMarker) continue;
      var name = Encoding.ASCII.GetString(dir, off, 8).TrimEnd();
      if (string.Equals(name, needle, StringComparison.Ordinal)) return i;
    }
    return -1;
  }

  private static int CountActive(byte[] dir) {
    var n = 0;
    for (var i = 0; i < MaxDirEntries; i++) {
      var off = i * DirEntrySize;
      var b0 = dir[off];
      if (b0 == 0x00) break;
      if (b0 == DeletedMarker) continue;
      n++;
    }
    return n;
  }

  // ── Free-gap finder ────────────────────────────────────────────────

  /// <summary>
  /// Lowest contiguous free run, in linear sector units, that is large
  /// enough to hold <paramref name="sectorsNeeded"/> sectors. Files
  /// always live above the directory + disk-info area (linear sector 9+).
  /// </summary>
  private static int FindContiguousGap(List<DirEntry> entries, int sectorsNeeded, int totalSectors) {
    var ranges = entries.Select(e => (Start: e.StartLinear, Length: e.LengthSectors))
                        .OrderBy(r => r.Start)
                        .ToList();

    var cursor = DirSectorCount + 1; // sectors 0..7 dir + sector 8 disk-info → start at 9
    foreach (var (start, len) in ranges) {
      if (start >= cursor + sectorsNeeded) return cursor;
      if (start + len > cursor) cursor = start + len;
    }
    if (cursor + sectorsNeeded <= totalSectors) return cursor;
    return -1;
  }

  private static int ComputeFreeCursor(List<DirEntry> entries, int newStart, int newLength) {
    var max = newStart + newLength;
    foreach (var e in entries) {
      var end = e.StartLinear + e.LengthSectors;
      if (end > max) max = end;
    }
    return max;
  }

  // ── Sector I/O ─────────────────────────────────────────────────────

  private static byte[] ReadDirectory(Stream image) {
    var buf = new byte[DirSectorCount * SectorSize];
    image.Position = 0;
    var read = 0;
    while (read < buf.Length) {
      var n = image.Read(buf, read, buf.Length - read);
      if (n <= 0) break;
      read += n;
    }
    if (read < buf.Length)
      throw new InvalidDataException("TR-DOS: image too small for directory.");
    return buf;
  }

  private static byte[] ReadSector(Stream s, int sector) {
    var buf = new byte[SectorSize];
    s.Position = (long)sector * SectorSize;
    var read = 0;
    while (read < SectorSize) {
      var n = s.Read(buf, read, SectorSize - read);
      if (n <= 0) break;
      read += n;
    }
    if (read < SectorSize)
      throw new InvalidDataException($"TR-DOS: image too small for sector {sector}.");
    return buf;
  }

  private static void WriteSector(Stream s, int sector, byte[] data) {
    s.Position = (long)sector * SectorSize;
    s.Write(data, 0, SectorSize);
  }

  private static void WriteRun(Stream s, int startSector, byte[] data) {
    s.Position = (long)startSector * SectorSize;
    s.Write(data, 0, data.Length);
    var tail = SectorSize - (data.Length % SectorSize);
    if (tail is > 0 and < SectorSize) {
      var pad = new byte[tail];
      s.Write(pad, 0, pad.Length);
    }
  }

  private static byte[] SectorSlice(byte[] dir, int sectorIndex) {
    var slice = new byte[SectorSize];
    Array.Copy(dir, sectorIndex * SectorSize, slice, 0, SectorSize);
    return slice;
  }

  private static int ImageTotalSectors(Stream image) {
    var len = image.Length;
    if (len <= 0) throw new InvalidDataException("TR-DOS: empty image stream.");
    return (int)(len / SectorSize);
  }

  // ── Name / type sanitisation ───────────────────────────────────────

  /// <summary>
  /// TR-DOS filenames are 8 ASCII chars, space-padded. We strip any
  /// extension the caller supplied, upper-case nothing (TR-DOS is case-
  /// preserving), and pad/truncate to 8 chars.
  /// </summary>
  private static string SanitizeName(string raw) {
    var s = Path.GetFileNameWithoutExtension(raw ?? "");
    if (string.IsNullOrEmpty(s)) s = "FILE";
    var chars = new char[s.Length];
    for (var i = 0; i < s.Length; i++) {
      var c = s[i];
      chars[i] = c is >= (char)0x20 and < (char)0x7F && c != '"' ? c : '_';
    }
    var clean = new string(chars);
    if (clean.Length > 8) clean = clean[..8];
    return clean.PadRight(8);
  }

  private static byte SanitizeType(byte type) {
    // Accept any printable byte; default to 'C' (code) for unrecognised values.
    if (type is >= 0x20 and < 0x7F) return type;
    return (byte)'C';
  }
}
