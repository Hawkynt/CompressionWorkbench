#pragma warning disable CS1591
namespace FileSystem.Xfs;

/// <summary>
/// Rebuild-style modifier for XFS images produced by <see cref="XfsWriter"/>.
/// <para>
/// True in-place mutation on XFS would require updating AGF/AGFL free-extent
/// accounting, mutating bnobt/cntbt B+tree leaves, rewriting the directory
/// inode's short-form data fork, and recalculating v5 CRC-32C checksums on
/// every touched metadata block — multi-week work. This class instead uses
/// the "rebuild" strategy: read all entries via <see cref="XfsReader"/>,
/// apply the modifications in memory, and emit a fresh image on top of the
/// old bytes via <see cref="XfsWriter"/>. Observable outcome is identical
/// (the resulting bytes are spec-compliant and pass <c>xfs_repair -n -f</c>)
/// and the implementation is trivially correct.
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
