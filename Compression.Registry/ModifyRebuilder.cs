namespace Compression.Registry;

/// <summary>
/// Generic rebuild-based <see cref="IArchiveModifiable"/> dispatch for
/// filesystems whose writer always emits a contiguous start-packed layout.
/// Per-FS code provides two delegates — the entry extractor (reads the
/// existing image) and the image builder (writes a fresh image with the
/// supplied file list) — and gets <c>Add</c> and <c>Remove</c> for free with
/// the documented <see cref="IArchiveModifiable"/> semantics, including
/// secure-wipe (the rebuild starts from zeroed bytes so removed file data
/// leaves no trace).
///
/// <para>The trade-off vs a planner-driven byte-level mutation: this rebuilds
/// the entire image on every Add/Remove call, so cost is <c>O(image size)</c>.
/// For filesystems whose on-disk pointer-rewriting is too complex to justify
/// (most retro and read-only-by-design filesystems), this is the pragmatic
/// option. Filesystems with a real planner-driven path (FAT once optimised,
/// Btrfs, etc.) implement <c>Add</c>/<c>Remove</c> themselves and don't use
/// this helper.</para>
///
/// <para>Companion to <see cref="DefragRebuilder"/> — same shape, different
/// outcome. The two share zero state.</para>
/// </summary>
public static class ModifyRebuilder {

  /// <summary>
  /// Adds (or replaces) files inside <paramref name="archive"/>. Existing
  /// entries whose name matches an input are replaced (the new bytes win);
  /// other existing entries are carried forward unchanged. The whole image
  /// is rebuilt from the merged file list.
  /// </summary>
  /// <param name="archive">Stream to rewrite. Must be readable, writable, seekable.</param>
  /// <param name="inputs">Files to add or replace.</param>
  /// <param name="readEntries">Reads the existing image and returns every
  /// live (non-directory) file as a (name, bytes) pair. Called exactly once.</param>
  /// <param name="buildImage">Builds a fresh image containing the supplied
  /// files. Called exactly once.</param>
  /// <param name="nameComparer">How input names are matched against existing
  /// entry names for replacement detection. Default: ordinal case-insensitive
  /// (matches the dominant filesystem-naming convention).</param>
  public static void Add(
    System.IO.Stream archive,
    System.Collections.Generic.IReadOnlyList<ArchiveInputInfo> inputs,
    System.Func<System.IO.Stream, System.Collections.Generic.IEnumerable<(string Name, byte[] Data)>> readEntries,
    System.Func<System.Collections.Generic.IReadOnlyList<(string Name, byte[] Data)>, byte[]> buildImage,
    System.StringComparer? nameComparer = null) {
    System.ArgumentNullException.ThrowIfNull(archive);
    System.ArgumentNullException.ThrowIfNull(inputs);
    System.ArgumentNullException.ThrowIfNull(readEntries);
    System.ArgumentNullException.ThrowIfNull(buildImage);
    nameComparer ??= System.StringComparer.OrdinalIgnoreCase;

    archive.Position = 0;
    // Materialise existing files first so the reader's snapshot is consistent
    // before we start mutating the stream.
    var existing = new System.Collections.Generic.List<(string Name, byte[] Data)>(readEntries(archive));

    var newPayloads = new System.Collections.Generic.List<(string Name, byte[] Data)>();
    foreach (var (name, data) in FormatHelpers.FilesOnly(inputs))
      newPayloads.Add((name, data));

    var newNames = new System.Collections.Generic.HashSet<string>(
      newPayloads.Select(p => p.Name), nameComparer);

    var combined = new System.Collections.Generic.List<(string Name, byte[] Data)>(
      existing.Count + newPayloads.Count);
    foreach (var entry in existing) {
      if (newNames.Contains(entry.Name)) continue;  // replaced
      combined.Add(entry);
    }
    combined.AddRange(newPayloads);

    var rebuilt = buildImage(combined);
    archive.Position = 0;
    archive.Write(rebuilt);
    archive.SetLength(rebuilt.Length);
  }

  /// <summary>
  /// Removes the named entries from <paramref name="archive"/>. The image is
  /// rebuilt from scratch with every entry whose name does NOT match one of
  /// <paramref name="entryNames"/>. Old file bytes are wiped because the new
  /// layout starts fresh — no forensic recovery should be possible.
  /// </summary>
  public static void Remove(
    System.IO.Stream archive,
    string[] entryNames,
    System.Func<System.IO.Stream, System.Collections.Generic.IEnumerable<(string Name, byte[] Data)>> readEntries,
    System.Func<System.Collections.Generic.IReadOnlyList<(string Name, byte[] Data)>, byte[]> buildImage,
    System.StringComparer? nameComparer = null) {
    System.ArgumentNullException.ThrowIfNull(archive);
    System.ArgumentNullException.ThrowIfNull(entryNames);
    System.ArgumentNullException.ThrowIfNull(readEntries);
    System.ArgumentNullException.ThrowIfNull(buildImage);
    nameComparer ??= System.StringComparer.OrdinalIgnoreCase;

    archive.Position = 0;
    var existing = new System.Collections.Generic.List<(string Name, byte[] Data)>(readEntries(archive));
    var toRemove = new System.Collections.Generic.HashSet<string>(entryNames, nameComparer);
    var kept = new System.Collections.Generic.List<(string Name, byte[] Data)>(existing.Count);
    foreach (var entry in existing) {
      if (toRemove.Contains(entry.Name)) continue;
      kept.Add(entry);
    }
    var rebuilt = buildImage(kept);
    archive.Position = 0;
    archive.Write(rebuilt);
    archive.SetLength(rebuilt.Length);
  }
}
