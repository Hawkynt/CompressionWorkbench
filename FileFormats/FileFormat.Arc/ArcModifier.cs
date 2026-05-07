#pragma warning disable CS1591
using System.Text;
using Compression.Core.Checksums;

namespace FileFormat.Arc;

/// <summary>
/// Random-access in-place modifier for ARC archives (System Enhancement
/// Associates / PKARC). ARC archives are a chain of variable-size entry
/// blocks terminated by an end-of-archive marker (magic 0x1A followed
/// by method byte 0x00). Add appends a new Stored (method 2) entry just
/// before the EOA marker; Remove walks the entry chain, locates the
/// target, and shifts trailing bytes forward to compact (ARC has no
/// central directory).
/// </summary>
public static class ArcModifier {

  /// <summary>
  /// Appends a Stored (method 2) entry to the archive. Walks the existing
  /// entry chain to find the EOA marker, writes a new entry block in its
  /// place, then re-writes the EOA marker. I/O cost is one full sequential
  /// entry walk plus the new entry's bytes.
  /// </summary>
  public static void AddFile(Stream arc, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(arc);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var eoaOffset = FindEoaOffset(arc);
    arc.Position = eoaOffset;
    WriteStoredEntryBlock(arc, name, data);
    WriteEoaMarker(arc);
    arc.SetLength(arc.Position);
  }

  /// <summary>
  /// Removes the named entry. Returns true if found. Walks the chain to
  /// locate the entry, then shifts trailing bytes forward to compact.
  /// </summary>
  public static bool RemoveFile(Stream arc, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(arc);
    ArgumentNullException.ThrowIfNull(name);

    var locator = LocateEntry(arc, name);
    if (!locator.Found) return false;

    if (wipeData)
      ZeroRange(arc, locator.BlockOffset, locator.BlockSize);

    var afterEntry = locator.BlockOffset + locator.BlockSize;
    var bytesToShift = arc.Length - afterEntry;
    if (bytesToShift > 0) {
      var buf = new byte[64 * 1024];
      var src = afterEntry;
      var dst = locator.BlockOffset;
      while (bytesToShift > 0) {
        var chunk = (int)Math.Min(buf.Length, bytesToShift);
        arc.Position = src;
        var read = 0;
        while (read < chunk) {
          var n = arc.Read(buf, read, chunk - read);
          if (n <= 0) break;
          read += n;
        }
        arc.Position = dst;
        arc.Write(buf, 0, read);
        src += read;
        dst += read;
        bytesToShift -= read;
      }
    }
    arc.SetLength(arc.Length - locator.BlockSize);
    return true;
  }

  // ── Block walking ─────────────────────────────────────────────────────
  // ARC entry block layout (method 2+, "new" format, 29-byte header):
  //   byte 0:        magic 0x1A
  //   byte 1:        method (0=EOA, 1=stored-old, 2..9=stored/compressed)
  //   bytes 2..14:   filename (13 bytes, NUL-terminated ASCII)
  //   bytes 15..18:  compressed size (uint32 LE)
  //   bytes 19..20:  DOS date (uint16 LE)
  //   bytes 21..22:  DOS time (uint16 LE)
  //   bytes 23..24:  CRC-16 of uncompressed data (uint16 LE)
  //   bytes 25..28:  original size (uint32 LE)  ← absent for method 1
  //   bytes 29..29+compressedSize-1: data
  // EOA marker: just 2 bytes (0x1A 0x00) — no fixed-size body follows.

  private static long FindEoaOffset(Stream arc) {
    arc.Position = 0;
    while (arc.Position + 2 <= arc.Length) {
      var blockStart = arc.Position;
      var magic = arc.ReadByte();
      if (magic != ArcConstants.Magic) return blockStart; // malformed → treat as EOA
      var method = arc.ReadByte();
      if (method == ArcConstants.MethodEndOfArchive) return blockStart;

      var headerSize = method >= ArcConstants.MethodStored
        ? ArcConstants.NewHeaderSize
        : ArcConstants.OldHeaderSize;
      var remaining = headerSize - 2;
      var headerBuf = new byte[remaining];
      var read = ReadFully(arc, headerBuf, 0, remaining);
      if (read < remaining) return blockStart;

      var compressedSize = ReadUInt32Le(headerBuf, 13); // offset 15 in full header = 13 in remaining
      arc.Position += compressedSize;
    }
    return arc.Length;
  }

  private readonly record struct EntryLocator(bool Found, long BlockOffset, long BlockSize);

  private static EntryLocator LocateEntry(Stream arc, string targetName) {
    arc.Position = 0;
    while (arc.Position + 2 <= arc.Length) {
      var blockStart = arc.Position;
      var magic = arc.ReadByte();
      if (magic != ArcConstants.Magic) break;
      var method = arc.ReadByte();
      if (method == ArcConstants.MethodEndOfArchive) break;

      var headerSize = method >= ArcConstants.MethodStored
        ? ArcConstants.NewHeaderSize
        : ArcConstants.OldHeaderSize;
      var remaining = headerSize - 2;
      var headerBuf = new byte[remaining];
      var read = ReadFully(arc, headerBuf, 0, remaining);
      if (read < remaining) break;

      var fileName = ReadNullTerminatedAscii(headerBuf, 0, ArcConstants.FileNameLength);
      var compressedSize = ReadUInt32Le(headerBuf, 13);
      arc.Position += compressedSize;
      var blockEnd = arc.Position;

      if (string.Equals(fileName, targetName, StringComparison.OrdinalIgnoreCase))
        return new EntryLocator(true, blockStart, blockEnd - blockStart);
    }
    return new EntryLocator(false, 0, 0);
  }

  // ── Block writing ─────────────────────────────────────────────────────

  private static void WriteStoredEntryBlock(Stream arc, string name, byte[] data) {
    // Truncate filename to 12 chars to fit the 13-byte NUL-terminated field.
    if (name.Length > 12) name = name[..12];

    var crc = data.Length > 0 ? Crc16.Compute(data) : (ushort)0;
    var (dosDate, dosTime) = DateTimeToDos(DateTime.Now);

    // 29-byte new-format header (method 2 = Stored).
    var header = new byte[ArcConstants.NewHeaderSize];
    header[0] = ArcConstants.Magic;
    header[1] = ArcConstants.MethodStored;
    var nameBytes = Encoding.ASCII.GetBytes(name);
    var nameLen = Math.Min(nameBytes.Length, ArcConstants.FileNameLength - 1);
    nameBytes.AsSpan(0, nameLen).CopyTo(header.AsSpan(2));
    // Remaining filename bytes stay 0 (NUL-terminated, padded).
    WriteUInt32Le(header, 15, (uint)data.Length); // compressedSize
    WriteUInt16Le(header, 19, dosDate);
    WriteUInt16Le(header, 21, dosTime);
    WriteUInt16Le(header, 23, crc);
    WriteUInt32Le(header, 25, (uint)data.Length); // originalSize

    arc.Write(header, 0, header.Length);
    if (data.Length > 0) arc.Write(data, 0, data.Length);
  }

  private static void WriteEoaMarker(Stream arc) {
    arc.WriteByte(ArcConstants.Magic);
    arc.WriteByte(ArcConstants.MethodEndOfArchive);
  }

  // ── Low-level helpers ─────────────────────────────────────────────────

  private static int ReadFully(Stream s, byte[] buffer, int offset, int count) {
    var total = 0;
    while (total < count) {
      var n = s.Read(buffer, offset + total, count - total);
      if (n <= 0) break;
      total += n;
    }
    return total;
  }

  private static ushort ReadUInt16Le(byte[] buffer, int offset) =>
    (ushort)(buffer[offset] | buffer[offset + 1] << 8);

  private static uint ReadUInt32Le(byte[] buffer, int offset) =>
    (uint)(buffer[offset] | buffer[offset + 1] << 8 | buffer[offset + 2] << 16 | buffer[offset + 3] << 24);

  private static void WriteUInt16Le(byte[] buffer, int offset, ushort value) {
    buffer[offset] = (byte)(value & 0xFF);
    buffer[offset + 1] = (byte)(value >> 8);
  }

  private static void WriteUInt32Le(byte[] buffer, int offset, uint value) {
    buffer[offset] = (byte)(value & 0xFF);
    buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
    buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
    buffer[offset + 3] = (byte)(value >> 24);
  }

  private static string ReadNullTerminatedAscii(byte[] buffer, int offset, int maxLength) {
    var end = offset;
    while (end < offset + maxLength && buffer[end] != 0) ++end;
    return Encoding.ASCII.GetString(buffer, offset, end - offset);
  }

  private static (ushort date, ushort time) DateTimeToDos(DateTime dt) {
    var year = dt.Year - 1980;
    if (year < 0) year = 0;
    if (year > 127) year = 127;
    var date = (ushort)(((year & 0x7F) << 9) | ((dt.Month & 0x0F) << 5) | (dt.Day & 0x1F));
    var time = (ushort)(((dt.Hour & 0x1F) << 11) | ((dt.Minute & 0x3F) << 5) | ((dt.Second / 2) & 0x1F));
    return (date, time);
  }

  private static void ZeroRange(Stream s, long offset, long length) {
    var buf = new byte[(int)Math.Min(length, 8192)];
    s.Position = offset;
    var remaining = length;
    while (remaining > 0) {
      var chunk = (int)Math.Min(buf.Length, remaining);
      s.Write(buf, 0, chunk);
      remaining -= chunk;
    }
  }
}
