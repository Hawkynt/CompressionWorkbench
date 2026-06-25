#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Cromemco;

/// <summary>
/// In-place modifier for Cromemco RDOS volumes. Performs add / remove with
/// strict <b>O(touched bytes)</b> I/O — only the 2 KB directory area (sectors
/// 2..17, where the 32-byte entries live) and the affected file's contiguous
/// data run are read or written. The rest of the image is untouched, so
/// existing files' data bytes stay byte-identical at their original offsets
/// and a same-size update never changes the image length.
///
/// <para>RDOS is extent-based with no allocation bitmap: each file is a single
/// contiguous run of 128-byte sectors described by (start block, record count)
/// in its directory entry. Free space is whatever no live entry claims. This
/// modifier therefore reconstructs the in-use sector ranges from the directory
/// to find a free contiguous run for new data, and allocates contiguously so
/// the reader's contiguous extraction keeps working.</para>
///
/// <para>Directory entry (32 bytes, from file offset 0x100):
/// user code @0 (0xE5 = deleted, 0x00 = live/empty), name @1..8, ext @9..11,
/// start block u16 LE @0x0C, records u16 LE @0x0E, bytes-in-last-sector @0x10.</para>
/// </summary>
public static class CromemcoModifier {
  private const int SectorSize = 128;
  private const int DirectoryOffset = 0x100;
  private const int EntrySize = 32;
  private const int MaxEntries = 64;
  private const int FirstDataSector = 18; // 2 (boot) + 16 (directory)
  private const byte DeletedMarker = 0xE5;

  /// <summary>True if the stream is a recognised Cromemco RDOS volume.</summary>
  public static bool IsCromemco(Stream image) {
    if (image.Length < DirectoryOffset + EntrySize) return false;
    image.Position = 0;
    if (image.ReadByte() != 0xC3) return false;
    var scan = new byte[Math.Min(64, (int)image.Length)];
    image.Position = 0;
    image.ReadExactly(scan);
    var sig = CromemcoReader.Signature;
    for (var i = 0; i + sig.Length <= scan.Length; i++)
      if (scan.AsSpan(i, sig.Length).SequenceEqual(sig)) return true;
    return false;
  }

  /// <summary>
  /// Adds a file. Allocates the lowest contiguous free sector run, writes the
  /// data there, and fills a free directory slot. Throws on a full directory
  /// or no contiguous free run.
  /// </summary>
  public static void AddFile(Stream image, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var totalSectors = (int)(image.Length / SectorSize);
    var dir = ReadDirectory(image);
    var occupied = BuildOccupied(dir, totalSectors);

    var slot = FindFreeDirSlot(dir);
    if (slot < 0) throw new IOException("Cromemco: directory full (64 entries).");

    var sectorsNeeded = data.Length == 0 ? 1 : (data.Length + SectorSize - 1) / SectorSize;
    var startBlock = FindContiguousRun(occupied, totalSectors, sectorsNeeded);
    if (startBlock < 0)
      throw new IOException($"Cromemco: no contiguous run of {sectorsNeeded} sectors.");

    // Write the data run (pad the last sector with zeros).
    image.Position = (long)startBlock * SectorSize;
    if (data.Length > 0) image.Write(data, 0, data.Length);
    var pad = sectorsNeeded * SectorSize - data.Length;
    if (pad > 0) image.Write(new byte[pad], 0, pad);

    // Fill the directory entry in the in-memory buffer.
    var (fname, ext) = SplitName(name);
    var entryOff = slot * EntrySize;
    Array.Clear(dir, entryOff, EntrySize);
    dir[entryOff] = 0x00; // live
    WriteCpmField(dir.AsSpan(entryOff + 1, 8), fname);
    WriteCpmField(dir.AsSpan(entryOff + 9, 3), ext);
    BinaryPrimitives.WriteUInt16LittleEndian(dir.AsSpan(entryOff + 12, 2), (ushort)startBlock);
    BinaryPrimitives.WriteUInt16LittleEndian(dir.AsSpan(entryOff + 14, 2), (ushort)sectorsNeeded);
    dir[entryOff + 16] = (byte)(data.Length % SectorSize);

    WriteDirectory(image, dir);
  }

  /// <summary>
  /// Removes the named file: marks its directory entry deleted (user code
  /// 0xE5), optionally wipes the data run. Returns true if found.
  /// </summary>
  public static bool RemoveFile(Stream image, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);

    var dir = ReadDirectory(image);
    var needle = JoinName(SplitName(name));

    for (var i = 0; i < MaxEntries; i++) {
      var entryOff = i * EntrySize;
      var userCode = dir[entryOff];
      if (userCode == DeletedMarker) continue;
      if (userCode == 0x00 && IsAllZeroOrSpace(dir.AsSpan(entryOff + 1, 11))) break; // densely packed
      var entryName = JoinName((ReadCpmName(dir.AsSpan(entryOff + 1, 8)), ReadCpmName(dir.AsSpan(entryOff + 9, 3))));
      if (!string.Equals(entryName, needle, StringComparison.OrdinalIgnoreCase)) continue;

      if (wipeData) {
        var startBlock = BinaryPrimitives.ReadUInt16LittleEndian(dir.AsSpan(entryOff + 12, 2));
        var records = BinaryPrimitives.ReadUInt16LittleEndian(dir.AsSpan(entryOff + 14, 2));
        var off = (long)startBlock * SectorSize;
        var bytes = (long)records * SectorSize;
        if (startBlock >= FirstDataSector && off + bytes <= image.Length) {
          image.Position = off;
          image.Write(new byte[bytes], 0, (int)bytes);
        }
      }

      dir[entryOff] = DeletedMarker;
      WriteDirectory(image, dir);
      return true;
    }
    return false;
  }

  // ── Directory I/O ───────────────────────────────────────────────────

  private static byte[] ReadDirectory(Stream image) {
    var buf = new byte[MaxEntries * EntrySize];
    image.Position = DirectoryOffset;
    image.ReadExactly(buf);
    return buf;
  }

  private static void WriteDirectory(Stream image, byte[] dir) {
    image.Position = DirectoryOffset;
    image.Write(dir, 0, dir.Length);
  }

  /// <summary>First slot that is deleted (0xE5) or empty (0x00). Reusing a
  /// 0xE5 slot is safe — the reader skips it and keeps scanning; an empty
  /// 0x00 slot is the dense-pack append point.</summary>
  private static int FindFreeDirSlot(byte[] dir) {
    for (var i = 0; i < MaxEntries; i++) {
      var userCode = dir[i * EntrySize];
      if (userCode == DeletedMarker) return i;
      if (userCode == 0x00 && IsAllZeroOrSpace(dir.AsSpan(i * EntrySize + 1, 11))) return i;
    }
    return -1;
  }

  // ── Allocation ──────────────────────────────────────────────────────

  /// <summary>Marks the sectors claimed by every live entry as occupied,
  /// plus the reserved boot + directory area (sectors 0..17).</summary>
  private static bool[] BuildOccupied(byte[] dir, int totalSectors) {
    var occupied = new bool[totalSectors];
    for (var s = 0; s < FirstDataSector && s < totalSectors; s++) occupied[s] = true;
    for (var i = 0; i < MaxEntries; i++) {
      var entryOff = i * EntrySize;
      var userCode = dir[entryOff];
      if (userCode == DeletedMarker) continue;
      if (userCode == 0x00 && IsAllZeroOrSpace(dir.AsSpan(entryOff + 1, 11))) break;
      var startBlock = BinaryPrimitives.ReadUInt16LittleEndian(dir.AsSpan(entryOff + 12, 2));
      var records = BinaryPrimitives.ReadUInt16LittleEndian(dir.AsSpan(entryOff + 14, 2));
      for (var s = 0; s < records; s++) {
        var si = startBlock + s;
        if (si >= 0 && si < totalSectors) occupied[si] = true;
      }
    }
    return occupied;
  }

  /// <summary>Lowest contiguous free run of <paramref name="count"/> sectors.</summary>
  private static int FindContiguousRun(bool[] occupied, int totalSectors, int count) {
    var run = 0;
    var start = -1;
    for (var s = FirstDataSector; s < totalSectors; s++) {
      if (!occupied[s]) {
        if (run == 0) start = s;
        run++;
        if (run == count) return start;
      } else {
        run = 0;
        start = -1;
      }
    }
    return -1;
  }

  // ── Name helpers ────────────────────────────────────────────────────

  private static (string Name, string Ext) SplitName(string raw) {
    var safe = (raw ?? "").Replace('\\', '/');
    var slash = safe.LastIndexOf('/');
    if (slash >= 0) safe = safe[(slash + 1)..];
    safe = safe.ToUpperInvariant();
    var dot = safe.LastIndexOf('.');
    string name, ext;
    if (dot > 0) { name = safe[..dot]; ext = safe[(dot + 1)..]; }
    else { name = safe; ext = ""; }
    if (name.Length > 8) name = name[..8];
    if (ext.Length > 3) ext = ext[..3];
    return (name, ext);
  }

  private static string JoinName((string Name, string Ext) parts)
    => string.IsNullOrEmpty(parts.Ext) ? parts.Name : $"{parts.Name}.{parts.Ext}";

  private static void WriteCpmField(Span<byte> dst, string value) {
    dst.Fill(0x20);
    var n = Math.Min(value.Length, dst.Length);
    for (var i = 0; i < n; i++) {
      var c = value[i];
      dst[i] = c < 0x80 ? (byte)c : (byte)'?';
    }
  }

  private static string ReadCpmName(ReadOnlySpan<byte> span) {
    Span<char> chars = stackalloc char[span.Length];
    var len = 0;
    foreach (var b in span) {
      var c = (byte)(b & 0x7F);
      if (c == 0 || c == 0x20) break;
      chars[len++] = (char)c;
    }
    return new string(chars[..len]);
  }

  private static bool IsAllZeroOrSpace(ReadOnlySpan<byte> span) {
    foreach (var b in span)
      if (b != 0 && b != 0x20) return false;
    return true;
  }
}
