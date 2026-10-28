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
  // Every header level is parsed by LhaHeaderParser; a member spans
  // [header start, data offset + compressed size).

  private static long FindEofOffset(Stream lha) {
    lha.Position = 0;
    while (lha.Position < lha.Length) {
      var entryStart = lha.Position;
      var header = LhaHeaderParser.Read(lha);
      if (header == null) return entryStart;
      lha.Position = header.End;
    }
    return lha.Length;
  }

  private readonly record struct EntryLocator(bool Found, long HeaderOffset, long TotalEntrySize);

  private static EntryLocator LocateEntry(Stream lha, string targetName) {
    lha.Position = 0;
    while (lha.Position < lha.Length) {
      var header = LhaHeaderParser.Read(lha);
      if (header == null) break;
      if (string.Equals(header.Path, targetName, StringComparison.OrdinalIgnoreCase))
        return new EntryLocator(true, header.HeaderOffset, header.End - header.HeaderOffset);
      lha.Position = header.End;
    }
    return new EntryLocator(false, 0, 0);
  }

  // ── Helpers ───────────────────────────────────────────────────────────

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
