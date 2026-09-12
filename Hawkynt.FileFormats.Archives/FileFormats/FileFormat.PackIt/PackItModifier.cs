#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.PackIt;

/// <summary>
/// Random-access in-place modifier for PackIt (.pit) classic Macintosh
/// archives. PackIt has no explicit end-of-archive marker — readers stop
/// when the next 4 bytes are not "PMag" or "PMa4". Add appends a new
/// stored entry at EOF; Remove walks the entry chain, locates the target,
/// and shifts trailing bytes forward to compact (no central directory).
/// </summary>
public static class PackItModifier {

  /// <summary>
  /// Appends a stored ("PMag") entry at the end of the archive. Walks the
  /// existing entry chain to find the EOF (first non-magic 4 bytes), then
  /// writes the new entry there and truncates. I/O cost is one full
  /// sequential entry walk plus the new entry's bytes.
  /// </summary>
  public static void AddFile(Stream pit, string name, byte[] data,
      string fileType = "TEXT", string creator = "CWIE") {
    ArgumentNullException.ThrowIfNull(pit);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var eofOffset = FindEofOffset(pit);
    pit.Position = eofOffset;
    WriteStoredEntry(pit, name, data, fileType, creator);
    pit.SetLength(pit.Position);
  }

  /// <summary>
  /// Removes the named entry. Returns true if found. Walks the chain to
  /// locate the entry, then shifts trailing bytes forward to compact.
  /// </summary>
  public static bool RemoveFile(Stream pit, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(pit);
    ArgumentNullException.ThrowIfNull(name);

    var locator = LocateEntry(pit, name);
    if (!locator.Found) return false;

    if (wipeData)
      ZeroRange(pit, locator.EntryOffset, locator.EntrySize);

    var afterEntry = locator.EntryOffset + locator.EntrySize;
    var bytesToShift = pit.Length - afterEntry;
    if (bytesToShift > 0) {
      var buf = new byte[64 * 1024];
      var src = afterEntry;
      var dst = locator.EntryOffset;
      while (bytesToShift > 0) {
        var chunk = (int)Math.Min(buf.Length, bytesToShift);
        pit.Position = src;
        var read = 0;
        while (read < chunk) {
          var n = pit.Read(buf, read, chunk - read);
          if (n <= 0) break;
          read += n;
        }
        pit.Position = dst;
        pit.Write(buf, 0, read);
        src += read;
        dst += read;
        bytesToShift -= read;
      }
    }
    pit.SetLength(pit.Length - locator.EntrySize);
    return true;
  }

  // ── Entry walking ─────────────────────────────────────────────────────
  // PackIt entry layout (87-byte fixed header + variable forks):
  //   bytes  0..3:   magic ("PMag" or "PMa4")
  //   bytes  4..66:  filename Pascal string (1 length byte + 62 name bytes)
  //   bytes 67..70:  Mac file type (4 ASCII)
  //   bytes 71..74:  Mac creator code (4 ASCII)
  //   bytes 75..76:  Finder flags (uint16 BE)
  //   byte  77:      locked flag
  //   byte  78:      zero padding
  //   bytes 79..82:  data fork size (uint32 BE)
  //   bytes 83..86:  resource fork size (uint32 BE)
  //   bytes 87..:    data fork bytes, then resource fork bytes
  // Total entry = 87 + dataForkSize + resourceForkSize.

  private const int EntryHeaderSize = PackItReader.EntryHeaderSize; // 87
  private const int DataForkSizeOffset = 79;
  private const int ResourceForkSizeOffset = 83;

  private static long FindEofOffset(Stream pit) {
    pit.Position = 0;
    Span<byte> hdr = stackalloc byte[EntryHeaderSize];
    while (pit.Position + EntryHeaderSize <= pit.Length) {
      var entryStart = pit.Position;
      var read = ReadFully(pit, hdr);
      if (read < EntryHeaderSize) return entryStart;

      if (!IsKnownMagic(hdr)) return entryStart;

      var dataForkSize = BinaryPrimitives.ReadUInt32BigEndian(hdr.Slice(DataForkSizeOffset, 4));
      var rsrcForkSize = BinaryPrimitives.ReadUInt32BigEndian(hdr.Slice(ResourceForkSizeOffset, 4));
      pit.Position = entryStart + EntryHeaderSize + dataForkSize + rsrcForkSize;
    }
    return pit.Length;
  }

  private readonly record struct EntryLocator(bool Found, long EntryOffset, long EntrySize);

  private static EntryLocator LocateEntry(Stream pit, string targetName) {
    pit.Position = 0;
    Span<byte> hdr = stackalloc byte[EntryHeaderSize];
    while (pit.Position + EntryHeaderSize <= pit.Length) {
      var entryStart = pit.Position;
      var read = ReadFully(pit, hdr);
      if (read < EntryHeaderSize) break;
      if (!IsKnownMagic(hdr)) break;

      var nameLength = hdr[4];
      if (nameLength > 62) nameLength = 62;
      var name = nameLength > 0
        ? Encoding.Latin1.GetString(hdr.Slice(5, nameLength))
        : string.Empty;

      var dataForkSize = BinaryPrimitives.ReadUInt32BigEndian(hdr.Slice(DataForkSizeOffset, 4));
      var rsrcForkSize = BinaryPrimitives.ReadUInt32BigEndian(hdr.Slice(ResourceForkSizeOffset, 4));
      var entrySize = (long)EntryHeaderSize + dataForkSize + rsrcForkSize;

      if (string.Equals(name, targetName, StringComparison.OrdinalIgnoreCase))
        return new EntryLocator(true, entryStart, entrySize);

      pit.Position = entryStart + entrySize;
    }
    return new EntryLocator(false, 0, 0);
  }

  private static bool IsKnownMagic(ReadOnlySpan<byte> hdr) =>
    hdr[..4].SequenceEqual(PackItConstants.MagicStored) ||
    hdr[..4].SequenceEqual(PackItConstants.MagicCompressed);

  // ── Entry writing ─────────────────────────────────────────────────────

  private static void WriteStoredEntry(Stream pit, string name, byte[] data, string fileType, string creator) {
    Span<byte> header = stackalloc byte[EntryHeaderSize];
    header.Clear();

    // Magic "PMag" (stored).
    PackItConstants.MagicStored.CopyTo(header);

    // Pascal filename: 1 length byte + up to 62 name bytes at offsets 4..66.
    var nameBytes = Encoding.Latin1.GetBytes(name);
    var nameLen = Math.Min(nameBytes.Length, PackItConstants.FileNameMaxLength); // 62
    header[4] = (byte)nameLen;
    nameBytes.AsSpan(0, nameLen).CopyTo(header[5..]);

    // File type at 67..70.
    WriteAscii4(header.Slice(67, 4), fileType);
    // Creator at 71..74.
    WriteAscii4(header.Slice(71, 4), creator);
    // Finder flags (75..76), locked (77), padding (78) — already zeroed.

    // Data fork size (BE) at 79..82, resource fork size (BE) at 83..86.
    BinaryPrimitives.WriteUInt32BigEndian(header.Slice(DataForkSizeOffset, 4), (uint)data.Length);
    BinaryPrimitives.WriteUInt32BigEndian(header.Slice(ResourceForkSizeOffset, 4), 0u);

    pit.Write(header);
    if (data.Length > 0)
      pit.Write(data);
    // Resource fork: zero-length, nothing to write.
  }

  private static void WriteAscii4(Span<byte> dest, string value) {
    var bytes = Encoding.ASCII.GetBytes(value);
    var len = Math.Min(bytes.Length, 4);
    bytes.AsSpan(0, len).CopyTo(dest);
    for (var i = len; i < 4; ++i)
      dest[i] = (byte)' ';
  }

  // ── Helpers ───────────────────────────────────────────────────────────

  private static int ReadFully(Stream s, Span<byte> buf) {
    var read = 0;
    while (read < buf.Length) {
      var n = s.Read(buf[read..]);
      if (n <= 0) break;
      read += n;
    }
    return read;
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
