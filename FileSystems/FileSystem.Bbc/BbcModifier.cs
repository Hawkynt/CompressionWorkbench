#pragma warning disable CS1591
using System.Text;

namespace FileSystem.Bbc;

/// <summary>
/// Random-access in-place modifier for BBC Micro Acorn DFS <c>.ssd</c>
/// images. The DFS catalog is just two sectors (512 bytes total); only the
/// catalog plus the file's contiguous data run are read or written. Files
/// land in the lowest free contiguous gap above the catalog, leaving the
/// rest of the disk untouched.
/// </summary>
public static class BbcModifier {

  private const int SectorSize = BbcReader.SectorSize;             // 256
  private const int SectorsPerTrack = BbcReader.SectorsPerTrack;   // 10
  private const int MaxEntries = BbcReader.MaxEntries;             // 31
  private const int FirstDataSector = 2;
  private const int DefaultTotalSectors = 40 * SectorsPerTrack;    // 400 (40-track SSD)

  /// <summary>
  /// Adds a file to an existing single-sided DFS image. Caller is responsible
  /// for ensuring the name does not already exist (use <see cref="RemoveFile"/>
  /// first for replace-by-name semantics). The file is placed in the lowest
  /// contiguous gap large enough to hold it.
  /// </summary>
  public static void AddFile(Stream image, string name, byte[] data,
                             char directory = '$', uint loadAddr = 0x1900,
                             uint execAddr = 0x1900, bool locked = false) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var totalSectors = ImageTotalSectors(image);
    var sec0 = ReadSector(image, 0);
    var sec1 = ReadSector(image, 1);

    var entries = ParseCatalog(sec0, sec1);
    if (entries.Count >= MaxEntries)
      throw new InvalidOperationException("BBC DFS: catalog full (31 entries).");

    var sanitized = SanitizeName(name);
    var sectorsNeeded = Math.Max(1, (data.Length + SectorSize - 1) / SectorSize);
    var startSector = FindContiguousGap(entries, sectorsNeeded, totalSectors);
    if (startSector < 0)
      throw new InvalidOperationException(
        $"BBC DFS: no contiguous gap large enough for {sectorsNeeded} sectors.");

    // Write the data run.
    if (data.Length > 0)
      WriteRun(image, startSector, data);

    // Insert the new entry. DFS convention is descending start_sector
    // (newest/highest first); we keep that order so external tools see a
    // standard catalog.
    entries.Add(new BbcDirEntry {
      Name = sanitized,
      Directory = directory,
      Locked = locked,
      LoadAddr = loadAddr,
      ExecAddr = execAddr,
      Length = (uint)data.Length,
      StartSector = startSector,
    });
    entries.Sort((a, b) => b.StartSector.CompareTo(a.StartSector));

    WriteCatalog(image, sec0, sec1, entries, totalSectors);
  }

  /// <summary>
  /// Removes a named file from the image. Returns true if found and removed.
  /// When <paramref name="wipeData"/> is true, the data sectors are zeroed.
  /// </summary>
  public static bool RemoveFile(Stream image, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);

    var totalSectors = ImageTotalSectors(image);
    var sec0 = ReadSector(image, 0);
    var sec1 = ReadSector(image, 1);
    var entries = ParseCatalog(sec0, sec1);

    var sanitized = SanitizeName(name);
    var match = -1;
    for (var i = 0; i < entries.Count; i++) {
      if (entries[i].Name.TrimEnd() == sanitized) { match = i; break; }
    }
    if (match < 0) return false;

    var entry = entries[match];

    if (wipeData && entry.Length > 0) {
      var sectorsUsed = Math.Max(1, ((int)entry.Length + SectorSize - 1) / SectorSize);
      var zero = new byte[SectorSize];
      for (var k = 0; k < sectorsUsed; k++)
        WriteSector(image, entry.StartSector + k, zero);
    }

    entries.RemoveAt(match);
    WriteCatalog(image, sec0, sec1, entries, totalSectors);
    return true;
  }

  // ── Catalog parsing / writing ─────────────────────────────────────────

  private sealed class BbcDirEntry {
    public string Name = "";
    public char Directory = '$';
    public bool Locked;
    public uint LoadAddr;
    public uint ExecAddr;
    public uint Length;
    public int StartSector;
  }

  private static List<BbcDirEntry> ParseCatalog(byte[] sec0, byte[] sec1) {
    var count = sec1[5] / 8;
    if (count > MaxEntries) count = MaxEntries;
    var list = new List<BbcDirEntry>(count);
    for (var i = 0; i < count; i++) {
      var nameOff = 8 + i * 8;
      var metaOff = 8 + i * 8;
      var nameBuf = new byte[7];
      Array.Copy(sec0, nameOff, nameBuf, 0, 7);
      var name = Encoding.ASCII.GetString(nameBuf).TrimEnd();
      var dirByte = sec0[nameOff + 7];
      var locked = (dirByte & 0x80) != 0;
      var dir = (char)(dirByte & 0x7F);
      if (dir < 0x20 || dir > 0x7E) dir = '$';

      var loadLo = (uint)(sec1[metaOff + 0] | (sec1[metaOff + 1] << 8));
      var execLo = (uint)(sec1[metaOff + 2] | (sec1[metaOff + 3] << 8));
      var lengthLo = (uint)(sec1[metaOff + 4] | (sec1[metaOff + 5] << 8));
      var packed = sec1[metaOff + 6];
      var startLo = sec1[metaOff + 7];
      var startHi = packed & 0x03;
      var loadHi = (packed >> 2) & 0x03;
      var lengthHi = (packed >> 4) & 0x03;
      var execHi = (packed >> 6) & 0x03;
      var loadAddr = ((uint)loadHi << 16) | loadLo;
      var execAddr = ((uint)execHi << 16) | execLo;
      var length = ((uint)lengthHi << 16) | lengthLo;
      if ((loadHi & 0x02) != 0) loadAddr |= 0xFF000000;
      if ((execHi & 0x02) != 0) execAddr |= 0xFF000000;

      list.Add(new BbcDirEntry {
        Name = name,
        Directory = dir,
        Locked = locked,
        LoadAddr = loadAddr,
        ExecAddr = execAddr,
        Length = length,
        StartSector = (startHi << 8) | startLo,
      });
    }
    return list;
  }

  private static void WriteCatalog(Stream image, byte[] sec0, byte[] sec1,
                                   List<BbcDirEntry> entries, int totalSectors) {
    // Clear the entry-table regions, leaving title bytes and metadata header intact.
    for (var i = 8; i < SectorSize; i++) sec0[i] = 0;
    for (var i = 8; i < SectorSize; i++) sec1[i] = 0;

    sec1[5] = (byte)(entries.Count * 8);
    sec1[7] = (byte)(totalSectors & 0xFF);
    // Preserve boot option (bits 4-5) by clearing only bits 0-1 then setting hi-2 of total sectors.
    var boot = sec1[6] & 0x30;
    sec1[6] = (byte)(boot | ((totalSectors >> 8) & 0x03));

    for (var i = 0; i < entries.Count; i++) {
      var e = entries[i];
      var nameOff = 8 + i * 8;
      var padded = e.Name.PadRight(7).Substring(0, 7);
      for (var j = 0; j < 7; j++) sec0[nameOff + j] = (byte)padded[j];
      var dirByte = (byte)(e.Directory & 0x7F);
      if (e.Locked) dirByte |= 0x80;
      sec0[nameOff + 7] = dirByte;

      var m = 8 + i * 8;
      sec1[m + 0] = (byte)(e.LoadAddr & 0xFF);
      sec1[m + 1] = (byte)((e.LoadAddr >> 8) & 0xFF);
      sec1[m + 2] = (byte)(e.ExecAddr & 0xFF);
      sec1[m + 3] = (byte)((e.ExecAddr >> 8) & 0xFF);
      sec1[m + 4] = (byte)(e.Length & 0xFF);
      sec1[m + 5] = (byte)((e.Length >> 8) & 0xFF);
      var loadHi = (int)((e.LoadAddr >> 16) & 0x03);
      var execHi = (int)((e.ExecAddr >> 16) & 0x03);
      var lengthHi = (int)((e.Length >> 16) & 0x03);
      var startHi = (e.StartSector >> 8) & 0x03;
      sec1[m + 6] = (byte)(startHi | (loadHi << 2) | (lengthHi << 4) | (execHi << 6));
      sec1[m + 7] = (byte)(e.StartSector & 0xFF);
    }

    WriteSector(image, 0, sec0);
    WriteSector(image, 1, sec1);
  }

  // ── Free-gap finder ───────────────────────────────────────────────────

  /// <summary>
  /// Finds the lowest contiguous free run starting at or above sector
  /// <see cref="FirstDataSector"/> that holds at least <paramref name="sectorsNeeded"/>
  /// sectors. Returns -1 if none exists.
  /// </summary>
  private static int FindContiguousGap(List<BbcDirEntry> entries, int sectorsNeeded, int totalSectors) {
    // Build (start, sectorsUsed) from existing entries and sort ascending.
    var ranges = new List<(int Start, int Length)>(entries.Count);
    foreach (var e in entries) {
      var len = Math.Max(1, ((int)e.Length + SectorSize - 1) / SectorSize);
      ranges.Add((e.StartSector, len));
    }
    ranges.Sort((a, b) => a.Start.CompareTo(b.Start));

    var cursor = FirstDataSector;
    foreach (var (start, len) in ranges) {
      if (start >= cursor + sectorsNeeded) return cursor;
      if (start + len > cursor) cursor = start + len;
    }
    if (cursor + sectorsNeeded <= totalSectors) return cursor;
    return -1;
  }

  // ── Sector I/O ────────────────────────────────────────────────────────

  private static int ImageTotalSectors(Stream image) {
    var origPos = image.Position;
    image.Position = 0;
    Span<byte> sec1 = stackalloc byte[8];
    image.Position = SectorSize; // sector 1 starts at byte 256
    image.ReadExactly(sec1);
    image.Position = origPos;
    var hi = sec1[6] & 0x03;
    var lo = sec1[7];
    var n = (hi << 8) | lo;
    return n > 0 ? n : DefaultTotalSectors;
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
    return buf;
  }

  private static void WriteSector(Stream s, int sector, byte[] data) {
    s.Position = (long)sector * SectorSize;
    s.Write(data, 0, SectorSize);
  }

  private static void WriteRun(Stream s, int startSector, byte[] data) {
    s.Position = (long)startSector * SectorSize;
    s.Write(data, 0, data.Length);
    // Zero-pad the tail of the last sector to avoid leaking previous bytes.
    var tailZeros = SectorSize - (data.Length % SectorSize);
    if (tailZeros is > 0 and < SectorSize) {
      var pad = new byte[tailZeros];
      s.Write(pad, 0, pad.Length);
    }
  }

  // ── Name sanitisation (mirrors BbcWriter) ────────────────────────────

  private static string SanitizeName(string raw) {
    if (string.IsNullOrEmpty(raw)) return "FILE";
    var s = Path.GetFileNameWithoutExtension(raw).ToUpperInvariant();
    var chars = new char[s.Length];
    for (var i = 0; i < s.Length; i++) {
      var c = s[i];
      chars[i] = c >= 0x21 && c < 0x7F && c != '"' && c != '#' && c != '*' &&
                 c != '.' && c != ':' && c != '?' ? c : '_';
    }
    var clean = new string(chars);
    if (clean.Length == 0) return "FILE";
    if (clean.Length > 7) clean = clean[^7..];
    return clean;
  }
}
