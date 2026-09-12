#pragma warning disable CS1591
using System.Text;
using Compression.Core.Checksums;

namespace FileFormat.Arj;

/// <summary>
/// Random-access in-place modifier for ARJ archives. Add appends a new
/// entry just before the end-of-archive marker (a header with
/// basicHeaderSize == 0). Remove walks the entry chain, locates the
/// target, and shifts trailing bytes forward to compact (ARJ has no
/// central directory).
/// </summary>
public static class ArjModifier {

  /// <summary>
  /// Appends a Stored entry to the archive. Walks the existing entry chain
  /// to find the EOA marker, writes a new entry block in its place, then
  /// re-writes the EOA marker. I/O cost is one full sequential entry walk
  /// plus the new entry's bytes.
  /// </summary>
  public static void AddFile(Stream arj, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(arj);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var eoaOffset = FindEoaOffset(arj);
    arj.Position = eoaOffset;
    WriteStoredEntryBlock(arj, name, data);
    WriteEoaMarker(arj);
    arj.SetLength(arj.Position);
  }

  /// <summary>
  /// Removes the named entry. Returns true if found. Walks the chain to
  /// locate the entry, then shifts trailing bytes forward to compact.
  /// </summary>
  public static bool RemoveFile(Stream arj, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(arj);
    ArgumentNullException.ThrowIfNull(name);

    var locator = LocateEntry(arj, name);
    if (!locator.Found) return false;

    if (wipeData)
      ZeroRange(arj, locator.BlockOffset, locator.BlockSize);

    var afterEntry = locator.BlockOffset + locator.BlockSize;
    var bytesToShift = arj.Length - afterEntry;
    if (bytesToShift > 0) {
      var buf = new byte[64 * 1024];
      var src = afterEntry;
      var dst = locator.BlockOffset;
      while (bytesToShift > 0) {
        var chunk = (int)Math.Min(buf.Length, bytesToShift);
        arj.Position = src;
        var read = 0;
        while (read < chunk) {
          var n = arj.Read(buf, read, chunk - read);
          if (n <= 0) break;
          read += n;
        }
        arj.Position = dst;
        arj.Write(buf, 0, read);
        src += read;
        dst += read;
        bytesToShift -= read;
      }
    }
    arj.SetLength(arj.Length - locator.BlockSize);
    return true;
  }

  // ── Block walking ─────────────────────────────────────────────────────
  // ARJ block layout:
  //   bytes 0..1: header ID (0xEA60 LE)
  //   bytes 2..3: basicHeaderSize (uint16 LE) — 0 marks end-of-archive
  //   bytes 4..3+N: body (firstHeader 30 bytes + name + 0 + comment + 0)
  //   bytes 4+N..7+N: header CRC-32 (uint32 LE)
  //   bytes 8+N..9+N: extended header size (uint16 LE) — 0 here
  //   bytes 10+N..: compressed data (compressedSize bytes)
  // body[12..15] LE = compressedSize
  // body[6] = file type (2 = main archive header, 0/1/3 = entries)
  // body[30..] = name (NUL-terminated) then comment (NUL-terminated)

  private static long FindEoaOffset(Stream arj) {
    arj.Position = 0;
    while (arj.Position + 4 <= arj.Length) {
      var blockStart = arj.Position;
      var hid = ReadUInt16Le(arj);
      if (hid != ArjConstants.HeaderId) return blockStart; // malformed → treat as EOA
      var basicSize = ReadUInt16Le(arj);
      if (basicSize == 0) return blockStart;

      var body = ReadBytes(arj, basicSize);
      if (body.Length < basicSize) return blockStart;
      var compressedSize = ReadUInt32Le(body, 12);
      // Skip header CRC + extended header size + extended bytes (we treat ext as 0).
      arj.Position += 4; // CRC
      var extHdrSize = ReadUInt16Le(arj);
      arj.Position += extHdrSize > 0 ? extHdrSize + 4 /* ext CRC */ : 0;
      arj.Position += compressedSize;
    }
    return arj.Length;
  }

  private readonly record struct EntryLocator(bool Found, long BlockOffset, long BlockSize);

  private static EntryLocator LocateEntry(Stream arj, string targetName) {
    arj.Position = 0;
    while (arj.Position + 4 <= arj.Length) {
      var blockStart = arj.Position;
      var hid = ReadUInt16Le(arj);
      if (hid != ArjConstants.HeaderId) break;
      var basicSize = ReadUInt16Le(arj);
      if (basicSize == 0) break;

      var body = ReadBytes(arj, basicSize);
      if (body.Length < basicSize) break;
      var compressedSize = ReadUInt32Le(body, 12);
      var fileType = body[6];

      // Skip CRC + ext size + ext bytes.
      arj.Position += 4;
      var extHdrSize = ReadUInt16Le(arj);
      var extSkip = extHdrSize > 0 ? extHdrSize + 4 : 0;
      arj.Position += extSkip;

      var dataStart = arj.Position;
      arj.Position = dataStart + compressedSize;
      var blockEnd = arj.Position;

      // Skip the main archive header (file type 2, comment).
      if (fileType != ArjConstants.FileTypeComment) {
        var name = ParseCStringFromBody(body, ArjConstants.FirstHeaderMinSize);
        if (string.Equals(name, targetName, StringComparison.OrdinalIgnoreCase))
          return new EntryLocator(true, blockStart, blockEnd - blockStart);
      }
    }
    return new EntryLocator(false, 0, 0);
  }

  // ── Block writing ─────────────────────────────────────────────────────

  private static void WriteStoredEntryBlock(Stream arj, string name, byte[] data) {
    var nameBytes = Encoding.ASCII.GetBytes(name);
    var commentBytes = Array.Empty<byte>();
    var crc32 = data.Length > 0 ? Crc32.Compute(data) : 0u;

    const byte firstHeaderSize = ArjConstants.FirstHeaderMinSize;
    var bodyLength = firstHeaderSize + nameBytes.Length + 1 + commentBytes.Length + 1;
    var body = new byte[bodyLength];
    body[0] = firstHeaderSize;
    body[1] = ArjConstants.ArchiverVersion;
    body[2] = ArjConstants.MinVersionToExtract;
    body[3] = ArjConstants.OsDos;
    body[4] = 0; // flags
    body[5] = ArjConstants.MethodStore;
    body[6] = ArjConstants.FileTypeBinary;
    body[7] = 0; // reserved
    WriteUInt32Le(body, 8, MsdosTimestamp(DateTime.Now));
    WriteUInt32Le(body, 12, (uint)data.Length); // compressedSize
    WriteUInt32Le(body, 16, (uint)data.Length); // originalSize
    WriteUInt32Le(body, 20, crc32);
    WriteUInt16Le(body, 24, 0);
    WriteUInt16Le(body, 26, 0x20); // archive bit
    body[28] = 0; body[29] = 0;

    int pos = firstHeaderSize;
    Buffer.BlockCopy(nameBytes, 0, body, pos, nameBytes.Length);
    pos += nameBytes.Length;
    body[pos++] = 0;
    body[pos] = 0; // empty comment terminator

    var headerCrc = Crc32.Compute(body);

    WriteUInt16Le(arj, ArjConstants.HeaderId);
    WriteUInt16Le(arj, (ushort)body.Length);
    arj.Write(body, 0, body.Length);
    WriteUInt32Le(arj, headerCrc);
    WriteUInt16Le(arj, 0); // no extended header

    if (data.Length > 0) arj.Write(data, 0, data.Length);
  }

  private static void WriteEoaMarker(Stream arj) {
    WriteUInt16Le(arj, ArjConstants.HeaderId);
    WriteUInt16Le(arj, 0);
  }

  // ── Low-level helpers ─────────────────────────────────────────────────

  private static ushort ReadUInt16Le(Stream s) {
    var b0 = s.ReadByte();
    var b1 = s.ReadByte();
    if (b0 < 0 || b1 < 0) return 0;
    return (ushort)(b0 | b1 << 8);
  }

  private static uint ReadUInt32Le(byte[] buf, int offset) =>
    (uint)(buf[offset] | buf[offset + 1] << 8 | buf[offset + 2] << 16 | buf[offset + 3] << 24);

  private static byte[] ReadBytes(Stream s, int count) {
    var buf = new byte[count];
    var read = 0;
    while (read < count) {
      var n = s.Read(buf, read, count - read);
      if (n <= 0) break;
      read += n;
    }
    return buf.Length == read ? buf : buf[..read];
  }

  private static void WriteUInt16Le(Stream s, ushort value) {
    s.WriteByte((byte)(value & 0xFF));
    s.WriteByte((byte)(value >> 8));
  }

  private static void WriteUInt16Le(byte[] buf, int offset, ushort value) {
    buf[offset] = (byte)(value & 0xFF);
    buf[offset + 1] = (byte)(value >> 8);
  }

  private static void WriteUInt32Le(Stream s, uint value) {
    s.WriteByte((byte)(value & 0xFF));
    s.WriteByte((byte)((value >> 8) & 0xFF));
    s.WriteByte((byte)((value >> 16) & 0xFF));
    s.WriteByte((byte)(value >> 24));
  }

  private static void WriteUInt32Le(byte[] buf, int offset, uint value) {
    buf[offset] = (byte)(value & 0xFF);
    buf[offset + 1] = (byte)((value >> 8) & 0xFF);
    buf[offset + 2] = (byte)((value >> 16) & 0xFF);
    buf[offset + 3] = (byte)(value >> 24);
  }

  private static string ParseCStringFromBody(byte[] body, int offset) {
    var end = offset;
    while (end < body.Length && body[end] != 0) end++;
    return Encoding.ASCII.GetString(body, offset, end - offset);
  }

  private static uint MsdosTimestamp(DateTime dt) {
    var time = dt.Hour << 11 | dt.Minute << 5 | dt.Second / 2;
    var date = dt.Year - 1980 << 9 | dt.Month << 5 | dt.Day;
    return (uint)(date << 16 | time);
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
