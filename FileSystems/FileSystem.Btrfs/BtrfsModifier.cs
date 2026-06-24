#pragma warning disable CS1591
namespace FileSystem.Btrfs;

/// <summary>
/// Modifier for Btrfs images produced by <see cref="BtrfsWriter"/>.
/// <para>
/// <see cref="AddOrReplace"/> first attempts a genuine copy-on-write in-place add
/// via <see cref="BtrfsInPlaceAdder"/>: it writes NEW (CoW) FS-tree / extent-tree /
/// root-tree blocks for the changed path only, repoints the superblock, bumps the
/// generation, and recomputes CRC-32C — leaving every untouched node and every
/// existing data extent byte-identical at its offset (verified with
/// <c>btrfs check</c>). That path covers the common case of adding/replacing one or
/// more small (inline, &lt; one sector) files in the root directory of a
/// single-FS-tree-leaf image.
/// </para>
/// <para>
/// Cases the in-place adder does not handle — nested sub-directory targets, files
/// at/above one sector (regular data extents), multi-leaf FS trees, full metadata
/// chunks, or non-default node/sector sizes — throw <see cref="NotSupportedException"/>
/// and fall back to the verified "rebuild" strategy below: read all entries via
/// <see cref="BtrfsReader"/>, apply the modifications in memory, and emit a fresh
/// image over the old bytes via <see cref="BtrfsWriter"/>. <see cref="Remove"/> is
/// always rebuild-based.
/// </para>
/// </summary>
public static class BtrfsModifier {
  /// <summary>
  /// Adds or replaces files in <paramref name="archive"/>. Tries genuine in-place
  /// copy-on-write first (per file, in order); if any add hits an unhandled shape
  /// the whole batch falls back to a single rebuild that applies every change.
  /// Existing entries are preserved except those whose names are overridden by the
  /// new inputs.
  /// </summary>
  public static void AddOrReplace(Stream archive, IReadOnlyList<(string Name, byte[] Data)> toAddOrReplace) {
    if (TryAddInPlace(archive, toAddOrReplace))
      return;

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

  // Attempts the genuine copy-on-write in-place add for every requested file on a
  // working copy of the image. Returns true (and writes the result back) only when
  // all files were added in place; on the first unhandled shape it discards the
  // working copy and returns false so the caller rebuilds the whole batch from the
  // untouched original. Working on a copy keeps the on-disk image consistent even
  // when a later file in the batch falls back.
  private static bool TryAddInPlace(Stream archive, IReadOnlyList<(string Name, byte[] Data)> toAddOrReplace) {
    if (toAddOrReplace.Count == 0)
      return false;

    archive.Position = 0;
    using var snapshot = new MemoryStream();
    archive.CopyTo(snapshot);
    var image = snapshot.ToArray();

    try {
      foreach (var (name, data) in toAddOrReplace)
        BtrfsInPlaceAdder.AddFile(image, name, data);
    } catch (NotSupportedException) {
      return false; // unhandled shape — caller rebuilds
    } catch (InvalidDataException) {
      return false; // not a writer-shaped image — caller rebuilds
    }

    archive.Position = 0;
    archive.Write(image);
    archive.SetLength(image.Length);
    return true;
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
