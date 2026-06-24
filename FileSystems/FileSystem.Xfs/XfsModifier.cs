#pragma warning disable CS1591
namespace FileSystem.Xfs;

/// <summary>
/// Rebuild-style modifier for XFS images produced by <see cref="XfsWriter"/> —
/// the fallback for the genuine in-place editor (<see cref="XfsInPlaceAdder"/>).
/// <para>
/// The in-place editor handles the common add/replace cases without re-packing
/// (free-extent btree carve/rebalance, inode-chunk growth, short-form → block →
/// leaf directory promotion, nested targets, replace-by-name). The rare cases it
/// cannot satisfy — node-form directories, a larger directory block size, a
/// multi-level btree, or content overflowing AG 0 — route here: read all entries
/// via <see cref="XfsReader"/>, apply the modifications in memory, and emit a
/// fresh image over the old bytes via <see cref="XfsWriter"/>. The result is
/// spec-compliant and passes <c>xfs_repair -n -f</c>.
/// </para>
/// </summary>
public static class XfsModifier {
  /// <summary>
  /// Rebuilds <paramref name="archive"/> with <paramref name="toAddOrReplace"/>
  /// applied. Existing entries are preserved except those whose names are
  /// overridden by the new inputs.
  /// </summary>
  public static void AddOrReplace(Stream archive, IReadOnlyList<(string Name, byte[] Data)> toAddOrReplace) {
    archive.Position = 0;
    var reader = new XfsReader(archive);
    var existing = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    foreach (var entry in reader.Entries) {
      if (entry.IsDirectory) continue;
      existing[entry.Name] = reader.Extract(entry);
    }
    foreach (var (name, data) in toAddOrReplace)
      existing[name] = data;

    var w = new XfsWriter();
    foreach (var (name, data) in existing)
      w.AddFile(name, data);

    using var ms = new MemoryStream();
    w.WriteTo(ms);
    var rebuilt = ms.ToArray();
    archive.Position = 0;
    archive.Write(rebuilt);
    archive.SetLength(rebuilt.Length);
  }

  /// <summary>
  /// Rebuilds <paramref name="archive"/> without the named entries.
  /// </summary>
  public static void Remove(Stream archive, IReadOnlyCollection<string> names) {
    archive.Position = 0;
    var reader = new XfsReader(archive);
    var nameSet = new HashSet<string>(names, StringComparer.Ordinal);
    var keep = new List<(string Name, byte[] Data)>();
    foreach (var entry in reader.Entries) {
      if (entry.IsDirectory) continue;
      if (nameSet.Contains(entry.Name)) continue;
      keep.Add((entry.Name, reader.Extract(entry)));
    }

    var w = new XfsWriter();
    foreach (var (name, data) in keep)
      w.AddFile(name, data);

    using var ms = new MemoryStream();
    w.WriteTo(ms);
    var rebuilt = ms.ToArray();
    archive.Position = 0;
    archive.Write(rebuilt);
    archive.SetLength(rebuilt.Length);
  }
}
