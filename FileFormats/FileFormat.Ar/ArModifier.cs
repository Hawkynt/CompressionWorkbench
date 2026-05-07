#pragma warning disable CS1591
using System.Text;

namespace FileFormat.Ar;

/// <summary>
/// Random-access in-place modifier for Unix ar archives. Add appends a new
/// entry at EOF — touches only the new entry's bytes plus a quick header
/// chain walk to find the end. Remove walks the header chain to locate the
/// target, then shifts trailing bytes forward to close the gap (necessary
/// because AR has no central directory).
/// </summary>
public static class ArModifier {

  private const int GlobalHeaderSize = ArConstants.GlobalHeaderSize; // 8
  private const int EntryHeaderSize = ArConstants.EntryHeaderSize;   // 60

  /// <summary>
  /// Appends a regular file entry. Walks the existing header chain to find
  /// EOF, writes the new 60-byte header + data + alignment pad, and truncates
  /// the stream to the new length.
  /// </summary>
  public static void AddFile(Stream ar, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(ar);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    // AR's long-name extension stores the long name in a leading "//" string
    // table. The simplest in-place strategy is to refuse names exceeding the
    // inline limit — extending the table would require shifting every entry
    // after it. Truncate to the 16-byte field instead would corrupt the name.
    // ArWriter already uses inline "name/" terminator for short names; match
    // that convention here.
    if (name.Length > ArConstants.MaxInlineNameLength)
      throw new NotSupportedException(
        $"In-place AR add does not support names longer than {ArConstants.MaxInlineNameLength} characters " +
        "(GNU long-name table cannot be extended in place without rewriting the archive).");

    var endOffset = FindEndOffset(ar);
    ar.Position = endOffset;

    WriteEntryHeader(ar, name + "/", DateTimeOffset.Now, 0, 0, 0x1A4 /* 0644 */, data.Length);
    ar.Write(data);
    if ((data.Length & 1) != 0)
      ar.WriteByte(ArConstants.PaddingByte);

    ar.SetLength(ar.Position);
  }

  /// <summary>
  /// Removes the named entry. Returns true if found. The trailing portion
  /// of the file is shifted forward to close the gap (AR has no central
  /// directory; readers walk headers sequentially, so we must compact).
  /// </summary>
  public static bool RemoveFile(Stream ar, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(ar);
    ArgumentNullException.ThrowIfNull(name);

    var locator = LocateEntry(ar, name);
    if (!locator.Found) return false;

    if (wipeData)
      ZeroRange(ar, locator.HeaderOffset, locator.TotalEntrySize);

    var afterEntry = locator.HeaderOffset + locator.TotalEntrySize;
    var bytesToShift = ar.Length - afterEntry;
    if (bytesToShift > 0) {
      // Forward shift: copy from afterEntry → headerOffset.
      var buf = new byte[64 * 1024];
      var src = afterEntry;
      var dst = locator.HeaderOffset;
      while (bytesToShift > 0) {
        var chunk = (int)Math.Min(buf.Length, bytesToShift);
        ar.Position = src;
        var read = 0;
        while (read < chunk) {
          var n = ar.Read(buf, read, chunk - read);
          if (n <= 0) break;
          read += n;
        }
        ar.Position = dst;
        ar.Write(buf, 0, read);
        src += read;
        dst += read;
        bytesToShift -= read;
      }
    }
    ar.SetLength(ar.Length - locator.TotalEntrySize);
    return true;
  }

  // ── Header walking ────────────────────────────────────────────────────

  /// <summary>
  /// Returns the byte offset just past the last entry. Walks header-chain
  /// only — never reads data blocks, just seeks past them using each
  /// header's size field. Validates the global magic at offset 0.
  /// </summary>
  private static long FindEndOffset(Stream ar) {
    if (ar.Length < GlobalHeaderSize)
      throw new InvalidDataException("Stream is shorter than the AR global header.");

    ar.Position = 0;
    Span<byte> magic = stackalloc byte[GlobalHeaderSize];
    ReadFully(ar, magic);
    if (!magic.SequenceEqual(ArConstants.GlobalMagic))
      throw new InvalidDataException("Stream does not contain a valid ar archive (bad global magic).");

    Span<byte> hdr = stackalloc byte[EntryHeaderSize];
    while (ar.Position + EntryHeaderSize <= ar.Length) {
      var headerOffset = ar.Position;
      var read = ReadFully(ar, hdr);
      if (read < EntryHeaderSize) return headerOffset; // truncated, treat as end

      if (hdr[58] != ArConstants.EntryMagic[0] || hdr[59] != ArConstants.EntryMagic[1])
        throw new InvalidDataException("Invalid ar entry header (bad entry magic) while walking to end.");

      var dataSize = ParseSize(hdr);
      var padded = dataSize + (dataSize & 1);
      ar.Position = headerOffset + EntryHeaderSize + padded;
    }
    return ar.Length;
  }

  private readonly record struct EntryLocator(bool Found, long HeaderOffset, long TotalEntrySize);

  private static EntryLocator LocateEntry(Stream ar, string targetName) {
    if (ar.Length < GlobalHeaderSize)
      return new EntryLocator(false, 0, 0);

    ar.Position = 0;
    Span<byte> magic = stackalloc byte[GlobalHeaderSize];
    ReadFully(ar, magic);
    if (!magic.SequenceEqual(ArConstants.GlobalMagic))
      throw new InvalidDataException("Stream does not contain a valid ar archive (bad global magic).");

    string? gnuStringTable = null;
    Span<byte> hdr = stackalloc byte[EntryHeaderSize];
    while (ar.Position + EntryHeaderSize <= ar.Length) {
      var headerOffset = ar.Position;
      var read = ReadFully(ar, hdr);
      if (read < EntryHeaderSize) break;

      if (hdr[58] != ArConstants.EntryMagic[0] || hdr[59] != ArConstants.EntryMagic[1])
        break;

      var rawName = ReadField(hdr, 0, 16);
      var dataSize = ParseSize(hdr);
      var padded = dataSize + (dataSize & 1);
      var totalEntrySize = EntryHeaderSize + padded;

      // Capture the GNU string table when we encounter it ("//"). It is not
      // a user entry — keep walking but don't match against it.
      if (rawName == ArConstants.GnuStringTableName) {
        var tableData = new byte[dataSize];
        ar.Position = headerOffset + EntryHeaderSize;
        ReadFully(ar, tableData);
        gnuStringTable = Encoding.ASCII.GetString(tableData);
        ar.Position = headerOffset + totalEntrySize;
        continue;
      }

      var resolved = ResolveEntryName(rawName, gnuStringTable);
      if (resolved == targetName)
        return new EntryLocator(true, headerOffset, totalEntrySize);

      ar.Position = headerOffset + totalEntrySize;
    }
    return new EntryLocator(false, 0, 0);
  }

  // ── Name resolution (mirrors ArReader) ────────────────────────────────

  private static string ResolveEntryName(string rawName, string? gnuStringTable) {
    if (rawName.Length > 1 && rawName[0] == ArConstants.GnuLongNamePrefix &&
        char.IsAsciiDigit(rawName[1])) {
      if (gnuStringTable == null)
        return rawName;
      if (int.TryParse(rawName[1..], out var offset) && offset < gnuStringTable.Length) {
        var end = gnuStringTable.IndexOf("/\n", offset, StringComparison.Ordinal);
        return end >= 0
          ? gnuStringTable[offset..end]
          : gnuStringTable[offset..].TrimEnd('\n', '/');
      }
      return rawName;
    }
    return rawName.TrimEnd('/', ' ');
  }

  // ── Header field parsing ──────────────────────────────────────────────

  private static long ParseSize(ReadOnlySpan<byte> hdr) {
    // Size field at offset 48, length 10, ASCII decimal, space-padded.
    long n = 0;
    var any = false;
    for (var i = 48; i < 48 + 10; i++) {
      var c = hdr[i];
      if (c == (byte)' ' || c == 0) {
        if (any) break;
        continue;
      }
      if (c < (byte)'0' || c > (byte)'9')
        throw new InvalidDataException("Invalid ar entry header (non-decimal size field).");
      n = n * 10 + (c - (byte)'0');
      any = true;
    }
    if (!any || n < 0)
      throw new InvalidDataException("Invalid ar entry header (malformed size field).");
    return n;
  }

  private static string ReadField(ReadOnlySpan<byte> header, int offset, int length) =>
    Encoding.ASCII.GetString(header.Slice(offset, length)).TrimEnd(' ');

  // ── Header writing (mirrors ArWriter) ─────────────────────────────────

  private static void WriteEntryHeader(
    Stream stream,
    string nameField,
    DateTimeOffset modifiedTime,
    int ownerId,
    int groupId,
    int fileMode,
    long dataSize) {
    Span<byte> header = stackalloc byte[EntryHeaderSize];
    header.Clear();

    WriteAsciiField(header,  0, 16, nameField);
    WriteAsciiField(header, 16, 12, modifiedTime.ToUnixTimeSeconds().ToString());
    WriteAsciiField(header, 28,  6, ownerId.ToString());
    WriteAsciiField(header, 34,  6, groupId.ToString());
    WriteAsciiField(header, 40,  8, Convert.ToString(fileMode, 8));
    WriteAsciiField(header, 48, 10, dataSize.ToString());

    header[58] = ArConstants.EntryMagic[0];
    header[59] = ArConstants.EntryMagic[1];

    stream.Write(header);
  }

  private static void WriteAsciiField(Span<byte> header, int offset, int length, string value) {
    header.Slice(offset, length).Fill((byte)' ');
    var valueBytes = Encoding.ASCII.GetBytes(value);
    var copyLen = Math.Min(valueBytes.Length, length);
    valueBytes.AsSpan(0, copyLen).CopyTo(header.Slice(offset, copyLen));
  }

  // ── Stream helpers ────────────────────────────────────────────────────

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
    if (length <= 0) return;
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
