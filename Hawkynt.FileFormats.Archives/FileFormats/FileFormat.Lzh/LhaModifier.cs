#pragma warning disable CS1591
namespace FileFormat.Lzh;

/// <summary>
/// Random-access in-place modifier for LHA/LZH archives. Add appends a
/// new entry just before the implicit EOF (the LHA writer doesn't emit an
/// explicit terminator; readers stop at a header_size byte of 0 or
/// end-of-stream). Remove walks the entry chain, locates the target, and
/// shifts trailing bytes forward to compact.
/// </summary>
public static class LhaModifier {

  /// <summary>
  /// Appends a file to an LHA archive. Walks the existing header chain to
  /// find the EOF position, then writes a new -lh5- entry at that offset
  /// and truncates. I/O cost is one full sequential header walk plus the
  /// new entry's bytes.
  /// </summary>
  public static void AddFile(Stream lha, string name, byte[] data, string method = LhaConstants.MethodLh5) {
    ArgumentNullException.ThrowIfNull(lha);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var eofOffset = FindEofOffset(lha);
    lha.Position = eofOffset;

    // Use the existing writer to emit a single entry. Each WriteEntry call
    // is self-contained and produces a valid concatenable LHA chunk.
    var w = new LhaWriter(method);
    w.AddFile(name, data);
    w.WriteTo(lha);

    lha.SetLength(lha.Position);
  }

  /// <summary>
  /// Removes the named entry. Returns true if found. Walks the chain to
  /// locate the entry, then shifts trailing bytes forward to compact (LHA
  /// has no central directory, so compaction is required).
  /// </summary>
  public static bool RemoveFile(Stream lha, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(lha);
    ArgumentNullException.ThrowIfNull(name);

    var locator = LocateEntry(lha, name);
    if (!locator.Found) return false;

    if (wipeData)
      ZeroRange(lha, locator.HeaderOffset, locator.TotalEntrySize);

    var afterEntry = locator.HeaderOffset + locator.TotalEntrySize;
    var bytesToShift = lha.Length - afterEntry;
    if (bytesToShift > 0) {
      var buf = new byte[64 * 1024];
      var src = afterEntry;
      var dst = locator.HeaderOffset;
      while (bytesToShift > 0) {
        var chunk = (int)Math.Min(buf.Length, bytesToShift);
        lha.Position = src;
        var read = 0;
        while (read < chunk) {
          var n = lha.Read(buf, read, chunk - read);
          if (n <= 0) break;
          read += n;
        }
        lha.Position = dst;
        lha.Write(buf, 0, read);
        src += read;
        dst += read;
        bytesToShift -= read;
      }
    }
    lha.SetLength(lha.Length - locator.TotalEntrySize);
    return true;
  }

  // ── Header walking ────────────────────────────────────────────────────
  // Level-1 LHA layout per entry:
  //   byte 0:    header_size (= N, count of bytes after the checksum)
  //              0 = end-of-archive
  //   byte 1:    checksum
  //   bytes 2..6 (5):  method string ("-lh0-", "-lh5-", ...)
  //   bytes 7..10 (4): compressed_size (uint32 LE)
  //   ... rest of header (variable name + extras) ...
  // Total entry = 2 + N + compressed_size bytes.

  private static long FindEofOffset(Stream lha) {
    lha.Position = 0;
    Span<byte> rest = stackalloc byte[10];
    while (lha.Position < lha.Length) {
      var entryStart = lha.Position;
      var headerSize = lha.ReadByte();
      if (headerSize <= 0) return entryStart;

      // Read just enough to extract compressed_size (offset 7..10 from entry start).
      var read = ReadFully(lha, rest);
      if (read < rest.Length) return entryStart; // truncated → treat as EOF

      // rest[i] maps to file byte (entryStart + 1 + i). compressed_size lives at
      // file bytes 7..10 (inclusive) → rest indices 6..9.
      var compressedSize = (uint)(rest[6] | rest[7] << 8 | rest[8] << 16 | rest[9] << 24);
      lha.Position = entryStart + 2 + headerSize + compressedSize;
    }
    return lha.Length;
  }

  private readonly record struct EntryLocator(bool Found, long HeaderOffset, long TotalEntrySize);

  private static EntryLocator LocateEntry(Stream lha, string targetName) {
    lha.Position = 0;
    while (lha.Position < lha.Length) {
      var entryStart = lha.Position;
      var headerSize = lha.ReadByte();
      if (headerSize <= 0) break;

      var headerBuf = new byte[headerSize + 1]; // checksum byte + N payload bytes
      var read = 0;
      while (read < headerBuf.Length) {
        var n = lha.Read(headerBuf, read, headerBuf.Length - read);
        if (n <= 0) break;
        read += n;
      }
      if (read < headerBuf.Length) break;

      // headerBuf[0] = checksum; headerBuf[1+k] = payload byte k. Payload layout:
      //   0..4 method, 5..8 compressed_size, 9..12 original_size, 13..16 timestamp,
      //   17 reserved, 18 level, 19 nameLen, 20..20+nameLen-1 name.
      var compressedSize = (uint)(headerBuf[6] | headerBuf[7] << 8 | headerBuf[8] << 16 | headerBuf[9] << 24);
      var nameLen = headerBuf[20];
      var name = nameLen > 0
        ? System.Text.Encoding.ASCII.GetString(headerBuf, 21, Math.Min(nameLen, headerBuf.Length - 21))
        : "";

      var totalEntrySize = 2L + headerSize + compressedSize;

      if (string.Equals(name, targetName, StringComparison.OrdinalIgnoreCase))
        return new EntryLocator(true, entryStart, totalEntrySize);

      lha.Position = entryStart + totalEntrySize;
    }
    return new EntryLocator(false, 0, 0);
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
