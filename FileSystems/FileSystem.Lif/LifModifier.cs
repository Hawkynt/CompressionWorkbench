#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Lif;

/// <summary>
/// Random-access in-place modifier for HP LIF (Logical Interchange Format)
/// volumes. The on-disk layout is a flat directory of fixed 32-byte entries
/// followed by contiguous file data; LIF has no allocation bitmap, so free
/// space is recovered by scanning the existing directory for live extents.
/// Files land in the lowest contiguous sector gap above the directory area
/// that fits, mirroring the BBC DFS modifier strategy. Only the directory
/// sectors and the file's own data run are touched on each call.
/// </summary>
public static class LifModifier {

  private const int SectorSize = LifReader.SectorSize;        // 256
  private const int EntriesPerSector = SectorSize / 32;       // 8
  private const int FileTypeStored = 0xE020;                  // BIN program (matches LifWriter default)

  /// <summary>
  /// Adds a file to an existing LIF image. Caller is responsible for
  /// ensuring the name does not already exist (use <see cref="RemoveFile"/>
  /// first for replace-by-name semantics). The file is placed in the
  /// lowest contiguous gap large enough to hold it; the underlying stream
  /// is grown if necessary.
  /// </summary>
  public static void AddFile(Stream image, string name, byte[] data,
                             ushort fileType = FileTypeStored) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var header = ReadHeader(image);
    var entries = ReadDirectory(image, header);

    var sanitized = SanitizeName(name, 10);
    var sectorsNeeded = Math.Max(1, (data.Length + SectorSize - 1) / SectorSize);

    // Lowest free area starts immediately after the directory.
    var firstDataSector = header.DirectoryStartSector + header.DirectorySectors;
    var startSector = FindContiguousGap(entries, sectorsNeeded, firstDataSector);

    var maxEntries = header.DirectorySectors * EntriesPerSector - 1; // reserve one slot for 0xFF terminator
    if (entries.Count >= maxEntries)
      throw new InvalidOperationException(
        $"LIF: directory full ({entries.Count} >= {maxEntries} entries).");

    // Grow the underlying stream if the new run extends past the current end.
    var requiredLength = (long)(startSector + sectorsNeeded) * SectorSize;
    if (image.Length < requiredLength) {
      image.SetLength(requiredLength);
    }

    // Write the file data run (zero-pad the tail of the last sector to avoid leaks).
    WriteRun(image, startSector, data);

    var ts = MakeBcdNow();
    entries.Add(new LifDirEntry {
      Name = sanitized,
      FileType = fileType,
      StartSector = startSector,
      LengthSectors = sectorsNeeded,
      Timestamp = ts,
      Volume = 0x8001,
      Implementation = 0,
    });

    WriteDirectory(image, header, entries);
  }

  /// <summary>
  /// Removes a named file from the image. Returns true if found and removed.
  /// When <paramref name="wipeData"/> is true (the default) the file data
  /// sectors are zeroed before the directory is rewritten.
  /// </summary>
  public static bool RemoveFile(Stream image, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);

    var header = ReadHeader(image);
    var entries = ReadDirectory(image, header);
    var sanitized = SanitizeName(name, 10);

    var match = -1;
    for (var i = 0; i < entries.Count; i++) {
      if (entries[i].Name.TrimEnd() == sanitized.TrimEnd()) { match = i; break; }
    }
    if (match < 0) return false;

    var entry = entries[match];

    if (wipeData && entry.LengthSectors > 0) {
      var zero = new byte[SectorSize];
      for (var k = 0; k < entry.LengthSectors; k++) {
        var byteOff = (long)(entry.StartSector + k) * SectorSize;
        if (byteOff + SectorSize > image.Length) break;
        image.Position = byteOff;
        image.Write(zero, 0, SectorSize);
      }
    }

    entries.RemoveAt(match);
    WriteDirectory(image, header, entries);
    return true;
  }

  // ── Header / directory I/O ────────────────────────────────────────────

  private readonly record struct LifHeader(int DirectoryStartSector, int DirectorySectors);

  private sealed class LifDirEntry {
    public string Name = "";
    public ushort FileType;
    public int StartSector;
    public int LengthSectors;
    public byte[] Timestamp = new byte[6];
    public ushort Volume;
    public uint Implementation;
  }

  private static LifHeader ReadHeader(Stream image) {
    var orig = image.Position;
    var buf = new byte[32];
    image.Position = 0;
    image.ReadExactly(buf, 0, 32);
    image.Position = orig;
    var magic = BinaryPrimitives.ReadUInt16BigEndian(buf);
    if (magic != LifReader.LifMagic)
      throw new InvalidDataException($"LIF: bad magic 0x{magic:X4}, expected 0x8000.");
    var dirStart = (int)BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(8));
    var dirSectors = (int)BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(16));
    if (dirStart < 1 || dirSectors < 1)
      throw new InvalidDataException($"LIF: invalid directory geometry (start={dirStart}, sectors={dirSectors}).");
    return new LifHeader(dirStart, dirSectors);
  }

  private static List<LifDirEntry> ReadDirectory(Stream image, LifHeader h) {
    var totalEntries = h.DirectorySectors * EntriesPerSector;
    var dirByteOffset = (long)h.DirectoryStartSector * SectorSize;
    var dirBytes = new byte[h.DirectorySectors * SectorSize];
    image.Position = dirByteOffset;
    var read = 0;
    while (read < dirBytes.Length) {
      var n = image.Read(dirBytes, read, dirBytes.Length - read);
      if (n <= 0) break;
      read += n;
    }

    var list = new List<LifDirEntry>(totalEntries);
    for (var i = 0; i < totalEntries; i++) {
      var off = i * 32;
      if (off + 32 > dirBytes.Length) break;
      var first = dirBytes[off];
      if (first == 0xFF) break;                       // physical end-of-directory marker
      if (first == 0x00 || first == ' ') continue;    // empty / deleted slot

      var name = Encoding.ASCII.GetString(dirBytes, off, 10).TrimEnd(' ', '\0');
      var fileType = BinaryPrimitives.ReadUInt16BigEndian(dirBytes.AsSpan(off + 10));
      var startSec = (int)BinaryPrimitives.ReadUInt32BigEndian(dirBytes.AsSpan(off + 12));
      var lenSec = (int)BinaryPrimitives.ReadUInt32BigEndian(dirBytes.AsSpan(off + 16));
      var ts = new byte[6];
      Array.Copy(dirBytes, off + 20, ts, 0, 6);
      var vol = BinaryPrimitives.ReadUInt16BigEndian(dirBytes.AsSpan(off + 26));
      var impl = BinaryPrimitives.ReadUInt32BigEndian(dirBytes.AsSpan(off + 28));

      list.Add(new LifDirEntry {
        Name = name,
        FileType = fileType,
        StartSector = startSec,
        LengthSectors = lenSec,
        Timestamp = ts,
        Volume = vol,
        Implementation = impl,
      });
    }
    return list;
  }

  private static void WriteDirectory(Stream image, LifHeader h, List<LifDirEntry> entries) {
    var dirBytes = new byte[h.DirectorySectors * SectorSize];

    for (var i = 0; i < entries.Count && i < h.DirectorySectors * EntriesPerSector; i++) {
      var e = entries[i];
      var off = i * 32;
      var nameBytes = Encoding.ASCII.GetBytes(SanitizeName(e.Name, 10));
      Array.Copy(nameBytes, 0, dirBytes, off, 10);
      BinaryPrimitives.WriteUInt16BigEndian(dirBytes.AsSpan(off + 10), e.FileType);
      BinaryPrimitives.WriteUInt32BigEndian(dirBytes.AsSpan(off + 12), (uint)e.StartSector);
      BinaryPrimitives.WriteUInt32BigEndian(dirBytes.AsSpan(off + 16), (uint)e.LengthSectors);
      Array.Copy(e.Timestamp, 0, dirBytes, off + 20, 6);
      BinaryPrimitives.WriteUInt16BigEndian(dirBytes.AsSpan(off + 26), e.Volume);
      BinaryPrimitives.WriteUInt32BigEndian(dirBytes.AsSpan(off + 28), e.Implementation);
    }

    // Terminator entry: any slot beyond the live ones starts with 0xFF.
    var terminatorOff = entries.Count * 32;
    if (terminatorOff + 32 <= dirBytes.Length)
      dirBytes[terminatorOff] = 0xFF;

    image.Position = (long)h.DirectoryStartSector * SectorSize;
    image.Write(dirBytes, 0, dirBytes.Length);
  }

  // ── Free-gap finder ───────────────────────────────────────────────────

  /// <summary>
  /// Finds the lowest contiguous free run starting at or above
  /// <paramref name="firstDataSector"/> that holds at least
  /// <paramref name="sectorsNeeded"/> sectors. Always succeeds — the caller
  /// is responsible for growing the image to fit the returned extent.
  /// </summary>
  private static int FindContiguousGap(List<LifDirEntry> entries, int sectorsNeeded, int firstDataSector) {
    var ranges = new List<(int Start, int Length)>(entries.Count);
    foreach (var e in entries) {
      var len = Math.Max(1, e.LengthSectors);
      ranges.Add((e.StartSector, len));
    }
    ranges.Sort((a, b) => a.Start.CompareTo(b.Start));

    var cursor = firstDataSector;
    foreach (var (start, len) in ranges) {
      if (start >= cursor + sectorsNeeded) return cursor;
      if (start + len > cursor) cursor = start + len;
    }
    return cursor;
  }

  // ── Sector I/O ────────────────────────────────────────────────────────

  private static void WriteRun(Stream s, int startSector, byte[] data) {
    s.Position = (long)startSector * SectorSize;
    s.Write(data, 0, data.Length);
    var tailZeros = SectorSize - (data.Length % SectorSize);
    if (tailZeros is > 0 and < SectorSize) {
      var pad = new byte[tailZeros];
      s.Write(pad, 0, pad.Length);
    }
  }

  // ── Helpers ───────────────────────────────────────────────────────────

  private static string SanitizeName(string raw, int width) {
    var chars = new char[width];
    for (var i = 0; i < width; i++) chars[i] = ' ';
    if (string.IsNullOrEmpty(raw)) return new string(chars);
    var max = Math.Min(raw.Length, width);
    for (var i = 0; i < max; i++) {
      var c = raw[i];
      chars[i] = c is >= (char)0x20 and < (char)0x7F ? c : '?';
    }
    return new string(chars);
  }

  private static byte[] MakeBcdNow() {
    var ts = DateTime.Now;
    return [
      ToBcd((byte)(ts.Year % 100)),
      ToBcd((byte)ts.Month),
      ToBcd((byte)ts.Day),
      ToBcd((byte)ts.Hour),
      ToBcd((byte)ts.Minute),
      ToBcd((byte)ts.Second),
    ];
  }

  private static byte ToBcd(byte v) => (byte)(((v / 10) << 4) | (v % 10));
}
