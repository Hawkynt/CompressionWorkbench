#pragma warning disable CS1591
using System.Globalization;
using System.Text;

namespace FileFormat.Cpio;

/// <summary>
/// Random-access in-place modifier for CPIO archives (newc/odc "070701"/"070702").
/// Add appends a new entry just before the trailer — touches only the new entry's
/// bytes plus the (small) trailer rewrite. Remove walks the entry chain to locate
/// the target, then shifts trailing bytes forward to close the gap (necessary
/// because CPIO has no central directory).
/// </summary>
public static class CpioModifier {

  private const int HeaderSize = CpioConstants.NewAsciiHeaderSize; // 110

  /// <summary>
  /// Appends a regular file entry. Walks the existing entry chain to find
  /// the trailer entry, writes the new header + data + padding in its place,
  /// then re-writes the trailer and truncates to the new length.
  /// </summary>
  public static void AddFile(Stream cpio, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(cpio);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var trailerOffset = FindTrailerOffset(cpio);
    cpio.Position = trailerOffset;

    // Write new entry: header + name + name-padding + data + data-padding
    WriteEntry(cpio, name, data, mode: 0x81A4u, inode: 1u);

    // Re-write the trailer (inode 0, size 0, mode 0).
    WriteEntry(cpio, CpioConstants.Trailer, [], mode: 0u, inode: 0u);

    cpio.SetLength(cpio.Position);
  }

  /// <summary>
  /// Removes the named entry. Returns true if found. The trailing portion
  /// of the file is shifted forward to close the gap (CPIO has no central
  /// directory; readers walk entries sequentially, so we must compact).
  /// </summary>
  public static bool RemoveFile(Stream cpio, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(cpio);
    ArgumentNullException.ThrowIfNull(name);

    var locator = LocateEntry(cpio, name);
    if (!locator.Found) return false;

    if (wipeData)
      ZeroRange(cpio, locator.EntryOffset, locator.TotalEntrySize);

    var afterEntry = locator.EntryOffset + locator.TotalEntrySize;
    var bytesToShift = cpio.Length - afterEntry;
    if (bytesToShift > 0) {
      var buf = new byte[64 * 1024];
      var src = afterEntry;
      var dst = locator.EntryOffset;
      while (bytesToShift > 0) {
        var chunk = (int)Math.Min(buf.Length, bytesToShift);
        cpio.Position = src;
        var read = 0;
        while (read < chunk) {
          var n = cpio.Read(buf, read, chunk - read);
          if (n <= 0) break;
          read += n;
        }
        cpio.Position = dst;
        cpio.Write(buf, 0, read);
        src += read;
        dst += read;
        bytesToShift -= read;
      }
    }
    cpio.SetLength(cpio.Length - locator.TotalEntrySize);
    return true;
  }

  // ── Entry walking ─────────────────────────────────────────────────────

  /// <summary>
  /// Returns the byte offset of the trailer entry's header (inclusive).
  /// Walks the entry chain only — never reads file data, just seeks past
  /// it using each header's filesize field.
  /// </summary>
  private static long FindTrailerOffset(Stream cpio) {
    cpio.Position = 0;
    Span<byte> hdr = stackalloc byte[HeaderSize];
    while (cpio.Position + HeaderSize <= cpio.Length) {
      var entryOffset = cpio.Position;
      var read = ReadFully(cpio, hdr);
      if (read < HeaderSize) return entryOffset; // truncated; append here

      ValidateMagic(hdr);
      var fileSize = (long)ParseHex(hdr, 54, 8);
      var nameSize = (int)ParseHex(hdr, 94, 8);

      var name = ReadName(cpio, nameSize);
      var headerPlusName = HeaderSize + nameSize;
      var namePadding = (4 - (headerPlusName % 4)) % 4;
      cpio.Position += namePadding;

      if (name == CpioConstants.Trailer)
        return entryOffset;

      cpio.Position += fileSize;
      var dataPadding = (int)((4 - (fileSize % 4)) % 4);
      cpio.Position += dataPadding;
    }
    return cpio.Length; // no trailer found; append at end
  }

  private readonly record struct EntryLocator(bool Found, long EntryOffset, long TotalEntrySize);

  private static EntryLocator LocateEntry(Stream cpio, string targetName) {
    cpio.Position = 0;
    Span<byte> hdr = stackalloc byte[HeaderSize];
    while (cpio.Position + HeaderSize <= cpio.Length) {
      var entryOffset = cpio.Position;
      var read = ReadFully(cpio, hdr);
      if (read < HeaderSize) break;

      ValidateMagic(hdr);
      var fileSize = (long)ParseHex(hdr, 54, 8);
      var nameSize = (int)ParseHex(hdr, 94, 8);

      var name = ReadName(cpio, nameSize);
      var headerPlusName = HeaderSize + nameSize;
      var namePadding = (4 - (headerPlusName % 4)) % 4;
      cpio.Position += namePadding;

      if (name == CpioConstants.Trailer) break;

      var dataPadding = (int)((4 - (fileSize % 4)) % 4);
      var totalEntrySize = HeaderSize + nameSize + namePadding + fileSize + dataPadding;

      if (name == targetName)
        return new EntryLocator(true, entryOffset, totalEntrySize);

      cpio.Position += fileSize + dataPadding;
    }
    return new EntryLocator(false, 0, 0);
  }

  // ── Writing ───────────────────────────────────────────────────────────

  private static void WriteEntry(Stream cpio, string name, ReadOnlySpan<byte> data, uint mode, uint inode) {
    var nameBytes = Encoding.ASCII.GetBytes(name + '\0');
    var nameSize = nameBytes.Length;

    var header = string.Format(
      CultureInfo.InvariantCulture,
      "{0}{1:X8}{2:X8}{3:X8}{4:X8}{5:X8}{6:X8}{7:X8}{8:X8}{9:X8}{10:X8}{11:X8}{12:X8}{13:X8}",
      CpioConstants.NewAsciiMagic,
      inode, mode, 0u, 0u, 1u, 0u,
      (uint)data.Length,
      0u, 0u, 0u, 0u,
      (uint)nameSize,
      0u);

    var headerBytes = Encoding.ASCII.GetBytes(header);
    cpio.Write(headerBytes);
    cpio.Write(nameBytes);

    var headerPlusName = HeaderSize + nameSize;
    var namePadding = (4 - (headerPlusName % 4)) % 4;
    for (var i = 0; i < namePadding; ++i)
      cpio.WriteByte(0);

    if (data.Length > 0) {
      cpio.Write(data);
      var dataPadding = (4 - (data.Length % 4)) % 4;
      for (var i = 0; i < dataPadding; ++i)
        cpio.WriteByte(0);
    }
  }

  // ── Header parsing ────────────────────────────────────────────────────

  private static void ValidateMagic(ReadOnlySpan<byte> hdr) {
    var magic = Encoding.ASCII.GetString(hdr[..6]);
    if (magic != CpioConstants.NewAsciiMagic && magic != CpioConstants.NewCrcMagic)
      throw new InvalidDataException($"Invalid cpio magic: {magic}");
  }

  private static uint ParseHex(ReadOnlySpan<byte> hdr, int offset, int length) {
    var hex = Encoding.ASCII.GetString(hdr.Slice(offset, length));
    return uint.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
  }

  private static string ReadName(Stream cpio, int nameSize) {
    var nameBuf = new byte[nameSize];
    var read = 0;
    while (read < nameSize) {
      var n = cpio.Read(nameBuf, read, nameSize - read);
      if (n <= 0) break;
      read += n;
    }
    // Name is null-terminated; trim the trailing NUL.
    return Encoding.ASCII.GetString(nameBuf, 0, nameSize > 0 ? nameSize - 1 : 0);
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
