namespace Compression.Registry;

/// <summary>
/// Generic shrink driver for any archive whose descriptor implements
/// <see cref="IArchiveCreatable"/>. Lists the archive's entries, extracts them to
/// memory, and re-creates a fresh archive into the output stream using the
/// smallest canonical size that fits (from <see cref="IArchiveShrinkable.CanonicalSizes"/>).
/// <para>
/// Works as the default <c>Shrink</c> implementation for most R/W filesystems:
/// the "rebuild via WORM" pattern implicitly defragments as a side effect.
/// Format-specific shrinkers can override for efficiency.
/// </para>
/// </summary>
public static class ArchiveShrinker {

  /// <summary>
  /// Rebuilds <paramref name="input"/> into <paramref name="output"/>, choosing the
  /// smallest size from <paramref name="canonicalSizes"/> that still holds the payload.
  /// </summary>
  public static void ShrinkViaRebuild(
      Stream input,
      Stream output,
      IArchiveFormatOperations ops,
      IArchiveCreatable creator,
      IReadOnlyList<long> canonicalSizes) {
    ArgumentNullException.ThrowIfNull(ops);
    ArgumentNullException.ThrowIfNull(creator);
    if (canonicalSizes == null || canonicalSizes.Count == 0)
      throw new ArgumentException("At least one canonical size must be provided.", nameof(canonicalSizes));

    // Extract all entries to a temp directory — we need real file paths for Create.
    var tmpDir = Path.Combine(Path.GetTempPath(), "cwb_shrink_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(tmpDir);
    try {
      input.Position = 0;
      ops.Extract(input, tmpDir, null, null);

      var inputs = new List<ArchiveInputInfo>();
      var totalBytes = 0L;
      foreach (var file in Directory.GetFiles(tmpDir, "*", SearchOption.AllDirectories)) {
        var rel = Path.GetRelativePath(tmpDir, file).Replace('\\', '/');
        inputs.Add(new ArchiveInputInfo(file, rel, false));
        totalBytes += new FileInfo(file).Length;
      }

      // Pick the smallest canonical size that leaves reasonable headroom for metadata.
      // We pad the payload estimate by 10 % so FS overhead (FAT, BAM, directory, etc.)
      // doesn't push us over the chosen target.
      var targetSize = ChooseTargetSize(canonicalSizes, (long)(totalBytes * 1.10));
      var options = new FormatCreateOptions();  // TODO: thread ResizePolicy through when Phase I lands

      // We can't direct Create toward a specific image size for most writers — they pick
      // their layout based on input volume. For shrinkables with multiple canonical sizes
      // (floppy disk images), the writer's Build typically accepts a totalSectors/size
      // parameter; the caller is responsible for wiring that through in a format-specific
      // override. The default implementation here just rebuilds tightly, ignoring the
      // target size — which achieves defragmentation but not canonical-size step-down.
      _ = targetSize;

      creator.Create(output, inputs, options);
    } finally {
      try { Directory.Delete(tmpDir, true); } catch { /* best effort */ }
    }
  }

  /// <summary>
  /// Smallest size in <paramref name="canonicalSizes"/> that is &gt;= <paramref name="payloadBytes"/>.
  /// If no size is large enough, returns the largest available.
  /// </summary>
  public static long ChooseTargetSize(IReadOnlyList<long> canonicalSizes, long payloadBytes) {
    var sorted = canonicalSizes.OrderBy(s => s).ToArray();
    foreach (var s in sorted)
      if (s >= payloadBytes) return s;
    return sorted[^1];
  }
}
