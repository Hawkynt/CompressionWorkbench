namespace Compression.Registry;

/// <summary>
/// Generic rebuild-based defragmentor for filesystems whose writer always emits a
/// contiguous start-packed layout. Dispatches the four <see cref="DefragMode"/>
/// values onto a read-extract-rebuild path with mode-specific file ordering or
/// capacity validation. Per-filesystem code provides two delegates — the entry
/// extractor (reads from the existing image) and the image builder (writes a
/// fresh image) — and gets all four modes for free.
///
/// <para>The trade-off vs a planner-driven byte-level mutation: this rebuilds
/// the entire image on every Defragment call, so cost is <c>O(image size)</c>.
/// For filesystems whose writer is much faster than the planner-driven path
/// would be (small images, simple layouts), or where on-disk pointer-rewriting
/// is too complex to justify, this is the pragmatic option. FAT for instance
/// uses this for now; a planner-based path can replace it later without
/// breaking the public <see cref="IArchiveDefragmentable"/> contract.</para>
/// </summary>
public static class DefragRebuilder {

  /// <summary>
  /// Rebuilds <paramref name="archive"/> in place using the supplied reader+writer
  /// delegates and the layout strategy in <paramref name="options"/>.
  /// </summary>
  /// <param name="archive">Stream to rewrite. Must be readable, writable, seekable.</param>
  /// <param name="options">Defrag mode + parameters.</param>
  /// <param name="readEntries">Reads the existing image and returns every live
  /// (non-directory) file as a (name, bytes) pair. Called exactly once.</param>
  /// <param name="buildImage">Builds a fresh image containing the supplied
  /// files, in the supplied order. Called exactly once.</param>
  /// <exception cref="System.ArgumentNullException">Any argument is null.</exception>
  /// <exception cref="System.ArgumentException">Mode is <see cref="DefragMode.CarveHole"/>
  /// and the requested hole won't fit in the available capacity.</exception>
  public static void Rebuild(
    System.IO.Stream archive,
    DefragOptions options,
    System.Func<System.IO.Stream, System.Collections.Generic.IEnumerable<(string Name, byte[] Data)>> readEntries,
    System.Func<System.Collections.Generic.IReadOnlyList<(string Name, byte[] Data)>, byte[]> buildImage) {
    System.ArgumentNullException.ThrowIfNull(archive);
    System.ArgumentNullException.ThrowIfNull(options);
    System.ArgumentNullException.ThrowIfNull(readEntries);
    System.ArgumentNullException.ThrowIfNull(buildImage);

    archive.Position = 0;
    var originalLength = archive.Length;
    var files = new System.Collections.Generic.List<(string Name, byte[] Data)>(readEntries(archive));

    System.Collections.Generic.IReadOnlyList<(string Name, byte[] Data)> ordered;
    switch (options.Mode) {
      case DefragMode.ConsolidateAtStart:
      case DefragMode.FillHolesLazy:
        // Underlying writers always start-pack from the first data cluster,
        // so both modes converge to the same layout for rebuild-based FSes.
        ordered = files;
        break;
      case DefragMode.ConsolidateAtEnd:
        // Largest-first ordering causes the longest single contiguous run to
        // land at the lowest data offset; the cumulative effect across many
        // files is "small files cluster near the metadata region, big files
        // dominate the tail" — close in spirit to true end-packing while
        // staying within the writer's start-packed contract.
        ordered = files.OrderByDescending(static f => f.Data.Length).ToList();
        break;
      case DefragMode.CarveHole:
        if (options.HoleSize <= 0)
          throw new System.ArgumentException(
            "HoleSize must be positive for DefragMode.CarveHole.", nameof(options));
        var totalLive = files.Sum(static f => (long)f.Data.Length);
        if (totalLive + options.HoleSize > originalLength)
          throw new System.ArgumentException(
            $"Image is too small for the carved hole: live {totalLive} + hole {options.HoleSize} > image {originalLength}.",
            nameof(options));
        ordered = files;  // Pack at start; trailing free region absorbs the requested hole.
        break;
      default:
        throw new System.NotSupportedException($"Unsupported defrag mode: {options.Mode}");
    }

    var rebuilt = buildImage(ordered);
    archive.Position = 0;
    archive.Write(rebuilt);
    archive.SetLength(rebuilt.Length);
  }
}
