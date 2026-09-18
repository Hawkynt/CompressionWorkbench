#pragma warning disable CS1591
namespace FileFormat.Cpio;

/// <summary>
/// Random-access in-place modifier for CPIO archives. Add appends a new entry
/// just before the trailer — touching only the new entry's bytes plus the
/// (small) trailer rewrite. Remove walks the entry chain to locate the target,
/// then shifts trailing bytes forward to close the gap (necessary because CPIO
/// has no central directory).
/// </summary>
/// <remarks>
/// Every variant is handled, and an edited archive keeps the variant it
/// arrived in: a binary archive stays binary, an odc archive stays odc. The
/// chain walk goes through <see cref="CpioReader"/> so the header layouts are
/// described in exactly one place, and skips payloads by seeking rather than
/// reading them.
/// </remarks>
public static class CpioModifier {

  /// <summary>
  /// Appends a regular file entry. Walks the existing entry chain to find
  /// the trailer entry, writes the new header + data + padding in its place
  /// in the archive's own variant, then re-writes the trailer and truncates
  /// to the new length.
  /// </summary>
  public static void AddFile(Stream cpio, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(cpio);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var (trailerOffset, format) = FindTrailer(cpio);
    cpio.Position = trailerOffset;

    using (var writer = new CpioWriter(cpio, format, leaveOpen: true)) {
      writer.AddFile(name, data);
      writer.Finish();
    }

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

    if (LocateEntry(cpio, name) is not { } located)
      return false;

    var (entryOffset, entrySize) = located;

    if (wipeData)
      ZeroRange(cpio, entryOffset, entrySize);

    var afterEntry = entryOffset + entrySize;
    var bytesToShift = cpio.Length - afterEntry;
    if (bytesToShift > 0) {
      var buffer = new byte[64 * 1024];
      var source = afterEntry;
      var destination = entryOffset;
      while (bytesToShift > 0) {
        var chunk = (int)Math.Min(buffer.Length, bytesToShift);
        cpio.Position = source;
        var read = 0;
        while (read < chunk) {
          var n = cpio.Read(buffer, read, chunk - read);
          if (n <= 0)
            break;
          read += n;
        }
        cpio.Position = destination;
        cpio.Write(buffer, 0, read);
        source += read;
        destination += read;
        bytesToShift -= read;
      }
    }

    cpio.SetLength(cpio.Length - entrySize);
    return true;
  }

  // ── Entry walking ─────────────────────────────────────────────────────

  /// <summary>
  /// Returns the byte offset of the trailer entry's header (inclusive) and the
  /// variant the archive is written in. An empty archive reports offset zero and
  /// the newc variant a fresh archive would get; a damaged one throws rather
  /// than appending to a chain that cannot be walked.
  /// </summary>
  private static (long Offset, CpioArchiveFormat Format) FindTrailer(Stream cpio) {
    cpio.Position = 0;
    var format = CpioReader.PeekFormat(cpio) ?? CpioArchiveFormat.NewAscii;

    using var reader = new CpioReader(cpio, leaveOpen: true);
    while (true) {
      var offset = cpio.Position;
      if (reader.ReadNextHeader() == null)
        return (offset, format);
      reader.SkipCurrentEntryData();
    }
  }

  /// <summary>
  /// Finds the named entry and reports where it starts and how many bytes it
  /// occupies, header through payload padding.
  /// </summary>
  private static (long Offset, long Size)? LocateEntry(Stream cpio, string targetName) {
    cpio.Position = 0;
    using var reader = new CpioReader(cpio, leaveOpen: true);
    while (true) {
      var offset = cpio.Position;
      if (reader.ReadNextHeader() is not { } entry)
        return null;

      reader.SkipCurrentEntryData();
      if (entry.Name == targetName)
        return (offset, cpio.Position - offset);
    }
  }

  // ── Stream helpers ────────────────────────────────────────────────────

  private static void ZeroRange(Stream stream, long offset, long length) {
    var buffer = new byte[(int)Math.Min(length, 8192)];
    stream.Position = offset;
    var remaining = length;
    while (remaining > 0) {
      var chunk = (int)Math.Min(buffer.Length, remaining);
      stream.Write(buffer, 0, chunk);
      remaining -= chunk;
    }
  }
}
