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
    System.StringComparer? nameComparer = null,
    IArchiveCreatable? largeVolumeCreator = null) {
    System.ArgumentNullException.ThrowIfNull(archive);
    System.ArgumentNullException.ThrowIfNull(inputs);
    System.ArgumentNullException.ThrowIfNull(readEntries);
    System.ArgumentNullException.ThrowIfNull(buildImage);
    nameComparer ??= System.StringComparer.OrdinalIgnoreCase;

    if (largeVolumeCreator != null && archive.CanSeek && archive.Length > MaxBufferedImageBytes) {
      var replaced = new System.Collections.Generic.HashSet<string>(
        System.Linq.Enumerable.Select(FormatHelpers.FilesOnly(inputs), f => Norm(f.Name)), nameComparer);
      RebuildViaCreate(archive, largeVolumeCreator, readEntries,
        keep: name => !replaced.Contains(Norm(name)), extra: inputs);
      return;
    }

    archive.Position = 0;
    // Materialise existing files first so the reader's snapshot is consistent
    // before we start mutating the stream.
    var existing = new System.Collections.Generic.List<(string Name, byte[] Data)>(readEntries(archive));

    var newPayloads = new System.Collections.Generic.List<(string Name, byte[] Data)>();
    foreach (var (name, data) in FormatHelpers.FilesOnly(inputs))
      newPayloads.Add((name, data));

    // Match on a path-normalised key so a reader that reports "/name" still dedups
    // against an input named "name" — otherwise an update leaves a duplicate entry.
    var newNames = new System.Collections.Generic.HashSet<string>(
      newPayloads.Select(p => Norm(p.Name)), nameComparer);

    var combined = new System.Collections.Generic.List<(string Name, byte[] Data)>(
      existing.Count + newPayloads.Count);
    foreach (var entry in existing) {
      if (newNames.Contains(Norm(entry.Name))) continue;  // replaced
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
    System.StringComparer? nameComparer = null,
    IArchiveCreatable? largeVolumeCreator = null) {
    System.ArgumentNullException.ThrowIfNull(archive);
    System.ArgumentNullException.ThrowIfNull(entryNames);
    System.ArgumentNullException.ThrowIfNull(readEntries);
    System.ArgumentNullException.ThrowIfNull(buildImage);
    nameComparer ??= System.StringComparer.OrdinalIgnoreCase;

    if (largeVolumeCreator != null && archive.CanSeek && archive.Length > MaxBufferedImageBytes) {
      var dropped = new System.Collections.Generic.HashSet<string>(
        System.Linq.Enumerable.Select(entryNames, Norm), nameComparer);
      RebuildViaCreate(archive, largeVolumeCreator, readEntries,
        keep: name => !dropped.Contains(Norm(name)), extra: null);
      return;
    }

    archive.Position = 0;
    var existing = new System.Collections.Generic.List<(string Name, byte[] Data)>(readEntries(archive));
    // Normalise leading-slash so callers may pass either "name" or the reader's "/name".
    var toRemove = new System.Collections.Generic.HashSet<string>(
      System.Linq.Enumerable.Select(entryNames, Norm), nameComparer);
    var kept = new System.Collections.Generic.List<(string Name, byte[] Data)>(existing.Count);
    foreach (var entry in existing) {
      if (toRemove.Contains(Norm(entry.Name))) continue;
      kept.Add(entry);
    }
    var rebuilt = buildImage(kept);
    archive.Position = 0;
    archive.Write(rebuilt);
    archive.SetLength(rebuilt.Length);
  }

  /// <summary>
  /// Streaming counterpart to <see cref="Add" /> for volumes that no byte[] can
  /// hold. The merged entry list is produced lazily and handed to
  /// <paramref name="rebuild" />, which writes the new image straight to the
  /// stream; nothing bigger than one file is ever in memory.
  /// </summary>
  /// <param name="archive">Stream to rewrite. Must be readable, writable, seekable.</param>
  /// <param name="inputs">Files to add or replace.</param>
  /// <param name="readEntries">Reads the existing image, yielding one live file at a time.</param>
  /// <param name="rebuild">Writes a fresh image over the archive from the entries it is given.</param>
  /// <param name="nameComparer">How input names match existing names for replacement.</param>
  public static void AddStreaming(
    System.IO.Stream archive,
    System.Collections.Generic.IReadOnlyList<ArchiveInputInfo> inputs,
    System.Func<System.IO.Stream, System.Collections.Generic.IEnumerable<(string Name, byte[] Data)>> readEntries,
    System.Action<System.IO.Stream, System.Func<System.IO.Stream, System.Collections.Generic.IEnumerable<(string Name, byte[] Data)>>> rebuild,
    System.StringComparer? nameComparer = null) {
    System.ArgumentNullException.ThrowIfNull(archive);
    System.ArgumentNullException.ThrowIfNull(inputs);
    System.ArgumentNullException.ThrowIfNull(readEntries);
    System.ArgumentNullException.ThrowIfNull(rebuild);
    nameComparer ??= System.StringComparer.OrdinalIgnoreCase;

    var payloads = new System.Collections.Generic.List<ArchiveInputInfo>();
    foreach (var input in inputs)
      if (!input.IsDirectory)
        payloads.Add(input);

    var replaced = new System.Collections.Generic.HashSet<string>(
      System.Linq.Enumerable.Select(payloads, p => Norm(p.ArchiveName)), nameComparer);

    rebuild(archive, source => Merge(readEntries(source), replaced, payloads));
  }

  /// <summary>
  /// Streaming counterpart to <see cref="Remove" />, for the same reason as
  /// <see cref="AddStreaming" />: the kept entries are streamed into a fresh
  /// image rather than assembled into one array.
  /// </summary>
  /// <param name="archive">Stream to rewrite. Must be readable, writable, seekable.</param>
  /// <param name="entryNames">Names to drop.</param>
  /// <param name="readEntries">Reads the existing image, yielding one live file at a time.</param>
  /// <param name="rebuild">Writes a fresh image over the archive from the entries it is given.</param>
  /// <param name="nameComparer">How the names are matched against entry names.</param>
  public static void RemoveStreaming(
    System.IO.Stream archive,
    string[] entryNames,
    System.Func<System.IO.Stream, System.Collections.Generic.IEnumerable<(string Name, byte[] Data)>> readEntries,
    System.Action<System.IO.Stream, System.Func<System.IO.Stream, System.Collections.Generic.IEnumerable<(string Name, byte[] Data)>>> rebuild,
    System.StringComparer? nameComparer = null) {
    System.ArgumentNullException.ThrowIfNull(archive);
    System.ArgumentNullException.ThrowIfNull(entryNames);
    System.ArgumentNullException.ThrowIfNull(readEntries);
    System.ArgumentNullException.ThrowIfNull(rebuild);
    nameComparer ??= System.StringComparer.OrdinalIgnoreCase;

    var dropped = new System.Collections.Generic.HashSet<string>(
      System.Linq.Enumerable.Select(entryNames, Norm), nameComparer);

    rebuild(archive, source => Keep(readEntries(source), dropped));
  }

  /// <summary>Existing entries minus the replaced ones, then the new payloads.</summary>
  private static System.Collections.Generic.IEnumerable<(string Name, byte[] Data)> Merge(
      System.Collections.Generic.IEnumerable<(string Name, byte[] Data)> existing,
      System.Collections.Generic.HashSet<string> replaced,
      System.Collections.Generic.IReadOnlyList<ArchiveInputInfo> payloads) {
    foreach (var entry in existing)
      if (!replaced.Contains(Norm(entry.Name)))
        yield return entry;

    foreach (var input in payloads)
      yield return (input.ArchiveName, input.ReadContent());
  }

  /// <summary>Existing entries minus the dropped ones.</summary>
  private static System.Collections.Generic.IEnumerable<(string Name, byte[] Data)> Keep(
      System.Collections.Generic.IEnumerable<(string Name, byte[] Data)> existing,
      System.Collections.Generic.HashSet<string> dropped) {
    foreach (var entry in existing)
      if (!dropped.Contains(Norm(entry.Name)))
        yield return entry;
  }

  /// <summary>
  /// Adds files to a volume too large to hold in memory. Every entry is
  /// extracted to scratch, the inputs are merged in by name, and the format's
  /// own <see cref="IArchiveCreatable.Create" /> lays a fresh volume out — the
  /// in-place modifiers read the whole image into an array to find their trees,
  /// which is impossible past two gigabytes.
  /// </summary>
  /// <param name="archive">Stream to rewrite. Must be readable, writable, seekable.</param>
  /// <param name="inputs">Files to add or replace.</param>
  /// <param name="ops">The format's own read side, used to unpack the volume.</param>
  /// <param name="creator">The format's own writer.</param>
  /// <param name="syntheticNames">Entries the reader surfaces that are not files
  /// on the volume (a raw-image blob, a metadata sheet); they must not be written
  /// back as files.</param>
  public static void AddLargeVolume(
    System.IO.Stream archive,
    System.Collections.Generic.IReadOnlyList<ArchiveInputInfo> inputs,
    IArchiveFormatOperations ops,
    IArchiveCreatable creator,
    System.Collections.Generic.IReadOnlySet<string>? syntheticNames = null)
    => RebuildLargeVolume(archive, ops, creator, drop: null, extra: inputs, syntheticNames);

  /// <summary>
  /// Removes entries from a volume too large to hold in memory, by the same
  /// route as <see cref="AddLargeVolume" />.
  /// </summary>
  /// <param name="archive">Stream to rewrite. Must be readable, writable, seekable.</param>
  /// <param name="entryNames">Names to drop.</param>
  /// <param name="ops">The format's own read side, used to unpack the volume.</param>
  /// <param name="creator">The format's own writer.</param>
  /// <param name="syntheticNames">Entries the reader surfaces that are not files
  /// on the volume; see <see cref="AddLargeVolume" />.</param>
  public static void RemoveLargeVolume(
    System.IO.Stream archive,
    string[] entryNames,
    IArchiveFormatOperations ops,
    IArchiveCreatable creator,
    System.Collections.Generic.IReadOnlySet<string>? syntheticNames = null) {
    System.ArgumentNullException.ThrowIfNull(entryNames);
    var drop = new System.Collections.Generic.HashSet<string>(
      System.Linq.Enumerable.Select(entryNames, Norm), System.StringComparer.OrdinalIgnoreCase);
    RebuildLargeVolume(archive, ops, creator, drop, extra: null, syntheticNames);
  }

  /// <summary>Whether a volume is past the size an in-memory edit can handle.</summary>
  public static bool NeedsLargeVolumePath(System.IO.Stream archive)
    => archive != null && archive.CanSeek && archive.Length > MaxBufferedImageBytes;

  private static void RebuildLargeVolume(
      System.IO.Stream archive,
      IArchiveFormatOperations ops,
      IArchiveCreatable creator,
      System.Collections.Generic.HashSet<string>? drop,
      System.Collections.Generic.IReadOnlyList<ArchiveInputInfo>? extra,
      System.Collections.Generic.IReadOnlySet<string>? syntheticNames = null) {
    System.ArgumentNullException.ThrowIfNull(archive);
    System.ArgumentNullException.ThrowIfNull(ops);
    System.ArgumentNullException.ThrowIfNull(creator);

    var scratch = System.IO.Directory.CreateTempSubdirectory("cwb_bigmodify_");
    var unpacked = System.IO.Path.Combine(scratch.FullName, "files");
    var imagePath = System.IO.Path.Combine(scratch.FullName, "image.bin");
    try {
      System.IO.Directory.CreateDirectory(unpacked);
      archive.Position = 0;
      ops.Extract(archive, unpacked, null, null);

      var replaced = new System.Collections.Generic.HashSet<string>(
        System.StringComparer.OrdinalIgnoreCase);
      if (extra != null)
        foreach (var input in extra)
          if (!input.IsDirectory)
            replaced.Add(Norm(input.ArchiveName));

      var carried = new System.Collections.Generic.List<ArchiveInputInfo>();
      foreach (var file in System.IO.Directory.EnumerateFiles(
          unpacked, "*", System.IO.SearchOption.AllDirectories)) {
        var name = Norm(System.IO.Path.GetRelativePath(unpacked, file));
        if (drop != null && (drop.Contains(name) || drop.Contains(Norm(System.IO.Path.GetFileName(file)))))
          continue;
        // A reader that also surfaces the raw image must not have it written
        // back as a file: the volume would carry a copy of its own former self.
        if (syntheticNames != null && syntheticNames.Contains(name)) continue;
        if (replaced.Contains(name)) continue;
        carried.Add(new ArchiveInputInfo(file, name, false));
      }
      if (extra != null)
        foreach (var input in extra)
          if (!input.IsDirectory)
            carried.Add(input);

      using (var image = System.IO.File.Create(imagePath))
        creator.Create(image, carried, new FormatCreateOptions());

      using (var image = System.IO.File.OpenRead(imagePath)) {
        archive.Position = 0;
        archive.SetLength(image.Length);
        image.CopyTo(archive);
        archive.Flush();
      }
    } finally {
      try { scratch.Delete(recursive: true); } catch { /* scratch already gone */ }
    }
  }

  /// <summary>
  /// Size past which an image is rebuilt through the format's own streaming
  /// <see cref="IArchiveCreatable.Create" /> rather than assembled in memory. A
  /// byte[] cannot exceed two gigabytes, so a buffered rebuild does not merely
  /// run slowly on a volume this size — it throws, and the edit is lost.
  /// </summary>
  private const long MaxBufferedImageBytes = 1L << 30;

  /// <summary>
  /// Rewrites <paramref name="archive" /> as a fresh volume holding the entries
  /// that <paramref name="keep" /> accepts plus <paramref name="extra" />, using
  /// the format's own writer. Every payload passes through a scratch file, so
  /// peak memory is one entry rather than the whole volume.
  /// </summary>
  private static void RebuildViaCreate(
      System.IO.Stream archive,
      IArchiveCreatable creator,
      System.Func<System.IO.Stream, System.Collections.Generic.IEnumerable<(string Name, byte[] Data)>> readEntries,
      System.Func<string, bool> keep,
      System.Collections.Generic.IReadOnlyList<ArchiveInputInfo>? extra) {
    var scratch = System.IO.Directory.CreateTempSubdirectory("cwb_modify_");
    var imagePath = System.IO.Path.Combine(scratch.FullName, "image.bin");
    try {
      var carried = new System.Collections.Generic.List<ArchiveInputInfo>();
      var index = 0;
      archive.Position = 0;
      foreach (var entry in readEntries(archive)) {
        if (!keep(entry.Name)) continue;
        // The scratch name must not collide when two entries share a leaf name,
        // and must not be interpreted as a path — hence the index prefix.
        var path = System.IO.Path.Combine(scratch.FullName, index.ToString(
          System.Globalization.CultureInfo.InvariantCulture) + ".bin");
        System.IO.File.WriteAllBytes(path, entry.Data);
        carried.Add(new ArchiveInputInfo(path, entry.Name, false));
        ++index;
      }
      if (extra != null)
        foreach (var input in extra)
          if (!input.IsDirectory)
            carried.Add(input);

      using (var image = System.IO.File.Create(imagePath))
        creator.Create(image, carried, new FormatCreateOptions());

      using (var image = System.IO.File.OpenRead(imagePath)) {
        archive.Position = 0;
        archive.SetLength(image.Length);
        image.CopyTo(archive);
        archive.Flush();
      }
    } finally {
      try { scratch.Delete(recursive: true); } catch { /* scratch already gone */ }
    }
  }

  // Path-normalised name key for add/remove matching: forward slashes, no leading
  // slash — so a reader reporting "/dir/x" and a caller passing "dir/x" agree.
  private static string Norm(string name) => name.Replace('\\', '/').TrimStart('/');
}
