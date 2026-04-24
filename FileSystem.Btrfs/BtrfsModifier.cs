#pragma warning disable CS1591
namespace FileSystem.Btrfs;

/// <summary>
/// Rebuild-style modifier for Btrfs images produced by <see cref="BtrfsWriter"/>.
/// <para>
/// True in-place mutation on Btrfs would require emitting a new generation of
/// the superblock + root tree + fs-tree leaf, allocating fresh blocks via the
/// extent tree, updating the chunk tree, and recalculating CRC-32C checksums
/// on every metadata block — architecturally heavy. This class instead uses
/// the "rebuild" strategy: read all entries via <see cref="BtrfsReader"/>,
/// apply the modifications in memory, and emit a fresh image on top of the
/// old bytes via <see cref="BtrfsWriter"/>. Observable outcome is identical
/// (the resulting bytes pass <c>btrfs check --readonly</c>) and the
/// implementation is trivially correct.
/// </para>
/// </summary>
public static class BtrfsModifier {
  /// <summary>
  /// Rebuilds <paramref name="archive"/> with <paramref name="toAddOrReplace"/>
  /// applied. Existing entries are preserved except those whose names are
  /// overridden by the new inputs.
  /// </summary>
  public static void AddOrReplace(Stream archive, IReadOnlyList<(string Name, byte[] Data)> toAddOrReplace) {
    archive.Position = 0;
    var reader = new BtrfsReader(archive);
    var existing = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    foreach (var entry in reader.Entries) {
      if (entry.IsDirectory) continue;
      existing[entry.Name] = reader.Extract(entry);
    }
    foreach (var (name, data) in toAddOrReplace)
      existing[name] = data;

    var w = new BtrfsWriter();
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
    var reader = new BtrfsReader(archive);
    var nameSet = new HashSet<string>(names, StringComparer.Ordinal);
    var keep = new List<(string Name, byte[] Data)>();
    foreach (var entry in reader.Entries) {
      if (entry.IsDirectory) continue;
      if (nameSet.Contains(entry.Name)) continue;
      keep.Add((entry.Name, reader.Extract(entry)));
    }

    var w = new BtrfsWriter();
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
