#pragma warning disable CS1591

namespace FileFormat.PackIt;

/// <summary>
/// In-place edits of a PackIt archive. PackIt has no directory: entries are a chain ended by
/// <c>"PEnd"</c>, so an add rewrites the marker after the new entry and a remove shifts the tail.
/// </summary>
public static class PackItModifier {

  /// <summary>
  /// Appends a stored ("PMag") entry after the last entry and rewrites the <c>"PEnd"</c> marker behind it.
  /// </summary>
  /// <exception cref="InvalidDataException">The archive cannot be walked to its end (truncated or encrypted entry).</exception>
  public static void AddFile(Stream pit, string name, byte[] data,
      string fileType = "TEXT", string creator = "CWIE") {
    ArgumentNullException.ThrowIfNull(pit);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var end = FindEntriesEnd(pit);
    pit.Position = end;
    PackItWriter.WriteStoredEntry(pit, name, data, [], fileType, creator, modified: null);
    pit.Write(PackItConstants.MagicEnd);
    pit.SetLength(pit.Position);
  }

  /// <summary>
  /// Removes the named entry. Returns true if found. Locates the entry by walking the chain, then shifts
  /// the trailing bytes forward over it.
  /// </summary>
  public static bool RemoveFile(Stream pit, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(pit);
    ArgumentNullException.ThrowIfNull(name);

    if (pit.Length == 0)
      return false;
    pit.Position = 0;
    PackItEntry? target;
    using (var reader = new PackItReader(pit, leaveOpen: true))
      target = reader.Entries.FirstOrDefault(e => !e.IsEncrypted && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
    if (target is null)
      return false;

    if (wipeData)
      ZeroRange(pit, target.EntryOffset, target.EntryLength);

    var afterEntry = target.EntryOffset + target.EntryLength;
    var bytesToShift = pit.Length - afterEntry;
    var buf = new byte[64 * 1024];
    var src = afterEntry;
    var dst = target.EntryOffset;
    while (bytesToShift > 0) {
      var chunk = (int)Math.Min(buf.Length, bytesToShift);
      pit.Position = src;
      pit.ReadExactly(buf, 0, chunk);
      pit.Position = dst;
      pit.Write(buf, 0, chunk);
      src += chunk;
      dst += chunk;
      bytesToShift -= chunk;
    }
    pit.SetLength(pit.Length - target.EntryLength);
    return true;
  }

  /// <summary>Returns the offset just past the last entry, which is where <c>"PEnd"</c> sits (or belongs).</summary>
  private static long FindEntriesEnd(Stream pit) {
    if (pit.Length == 0)
      return 0;
    pit.Position = 0;
    using var reader = new PackItReader(pit, leaveOpen: true);
    if (reader.IsIncomplete)
      throw new InvalidDataException("PackIt archive cannot be walked to its end; refusing to append after an unknown tail.");
    return reader.Entries.Count == 0 ? 0 : reader.Entries[^1].EntryOffset + reader.Entries[^1].EntryLength;
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
