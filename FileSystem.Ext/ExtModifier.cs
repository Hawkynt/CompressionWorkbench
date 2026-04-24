#pragma warning disable CS1591
namespace FileSystem.Ext;

/// <summary>
/// Rebuild-style modifier for ext2 images produced by <see cref="ExtWriter"/>.
/// <para>
/// True in-place mutation on ext2 would require updating the block bitmap,
/// inode table, inode bitmap, and dirent table coherently — achievable but
/// non-trivial for replace-with-different-size operations. <see cref="ExtRemover"/>
/// already handles secure in-place file removal but leaves subsequent dirents
/// unreadable (it zeros the dirent slot; <see cref="ExtReader"/> stops at the
/// first zero-inode slot). For multi-file mutate-then-validate flows this
/// class uses a rebuild strategy: read all entries via <see cref="ExtReader"/>,
/// apply replacements/deletions/additions in memory, then emit a fresh
/// <c>fsck.ext4</c>-clean image over the old bytes.
/// </para>
/// </summary>
public static class ExtModifier {
  /// <summary>
  /// Atomically applies the given mutations: <paramref name="replacements"/>
  /// override matching entries by name, <paramref name="deletions"/> drop
  /// entries by name, and any remaining entries in <paramref name="replacements"/>
  /// that didn't match an existing name are added as new files.
  /// </summary>
  public static void Mutate(
      Stream archive,
      IReadOnlyList<(string Name, byte[] Data)> replacements,
      IReadOnlyCollection<string> deletions) {
    archive.Position = 0;
    var reader = new ExtReader(archive);

    var delSet = new HashSet<string>(deletions, StringComparer.Ordinal);
    var replaceMap = replacements.ToDictionary(r => r.Name, r => r.Data, StringComparer.Ordinal);

    var final = new List<(string Name, byte[] Data)>();
    foreach (var entry in reader.Entries) {
      if (entry.IsDirectory) continue;
      if (delSet.Contains(entry.Name)) continue;
      if (replaceMap.TryGetValue(entry.Name, out var newData)) {
        final.Add((entry.Name, newData));
        replaceMap.Remove(entry.Name);
      } else {
        final.Add((entry.Name, reader.Extract(entry)));
      }
    }
    // Leftover entries in replaceMap are "new" additions.
    foreach (var (name, data) in replaceMap)
      final.Add((name, data));

    var w = new ExtWriter();
    foreach (var (name, data) in final)
      w.AddFile(name, data);
    var rebuilt = w.Build();
    archive.Position = 0;
    archive.Write(rebuilt);
    archive.SetLength(rebuilt.Length);
  }
}
