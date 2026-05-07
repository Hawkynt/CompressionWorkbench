#pragma warning disable CS1591
namespace FileFormat.Wrapster;

/// <summary>
/// In-place modifier for Wrapster v2 archives. Wrapster's directory is
/// embedded at the start of the file and references absolute data offsets,
/// so any structural change requires rewriting the directory and shifting
/// data. Implementation reads all entries, mutates the list in memory, and
/// re-emits the archive via <see cref="WrapsterWriter"/>.
/// </summary>
/// <remarks>
/// True random-access is not possible for Wrapster: changing one entry's
/// size shifts all subsequent entries' offsets, which requires rewriting
/// every directory entry. The cost is proportional to the full archive
/// size, not just the new entry's size.
/// </remarks>
public static class WrapsterModifier {

  /// <summary>
  /// Adds (or replaces by name) a file in a Wrapster archive.
  /// </summary>
  public static void AddFile(Stream wrap, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(wrap);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var entries = ReadAll(wrap);
    var idx = entries.FindIndex(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
    if (idx >= 0)
      entries[idx] = (name, data);
    else
      entries.Add((name, data));

    Rewrite(wrap, entries);
  }

  /// <summary>
  /// Removes a named entry. Returns true if found.
  /// </summary>
  public static bool RemoveFile(Stream wrap, string name) {
    ArgumentNullException.ThrowIfNull(wrap);
    ArgumentNullException.ThrowIfNull(name);

    var entries = ReadAll(wrap);
    var before = entries.Count;
    entries.RemoveAll(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
    if (entries.Count == before) return false;

    Rewrite(wrap, entries);
    return true;
  }

  // ── Helpers ─────────────────────────────────────────────────────────────

  private static List<(string Name, byte[] Data)> ReadAll(Stream wrap) {
    wrap.Position = 0;
    var r = new WrapsterReader(wrap, leaveOpen: true);
    var list = new List<(string Name, byte[] Data)>(r.Entries.Count);
    foreach (var e in r.Entries)
      list.Add((e.Name, r.Extract(e)));
    return list;
  }

  private static void Rewrite(Stream wrap, List<(string Name, byte[] Data)> entries) {
    wrap.Position = 0;
    wrap.SetLength(0);
    var w = new WrapsterWriter();
    foreach (var (n, d) in entries)
      w.AddFile(n, d);
    w.WriteTo(wrap);
  }
}
