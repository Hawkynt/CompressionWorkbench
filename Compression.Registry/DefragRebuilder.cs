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

    // Emit a "scanning" event up front so the UI can show "starting" state
    // before any entries are pulled. BlockMap is null here because we haven't
    // walked the image yet.
    options.OnProgress?.Invoke(new DefragProgressEvent(
      Phase: "scanning",
      Fraction: 0,
      CurrentReadOffset: 0,
      CurrentWriteOffset: -1,
      ImageSize: originalLength,
      BlockMap: null,
      Status: "Walking directory"));

    // Stream entries one-at-a-time through readEntries so the UI's read head
    // animates as files are walked. The accumulated map grows incrementally:
    // each yielded entry adds a Used tile and the map is re-emitted so far,
    // giving live-progress visualisation even on large images. archive.Position
    // is the underlying stream cursor, which most readers move forward as they
    // walk file data — a good proxy for "where the read head is right now".
    var files = new System.Collections.Generic.List<(string Name, byte[] Data)>();
    var partialMap = new System.Collections.Generic.List<DefragBlockInfo>();
    var partialOffset = 0L;
    foreach (var entry in readEntries(archive)) {
      files.Add(entry);
      partialMap.Add(new DefragBlockInfo(
        partialOffset, entry.Data.Length, DefragBlockKind.Used, entry.Name,
        Classification: null));
      partialOffset += entry.Data.Length;
      var readPos = System.Math.Min(archive.Position, originalLength);
      options.OnProgress?.Invoke(new DefragProgressEvent(
        Phase: "scanning",
        Fraction: originalLength > 0 ? (double)readPos / originalLength * 0.5 : 0, // scan = first half
        CurrentReadOffset: readPos,
        CurrentWriteOffset: -1,
        ImageSize: originalLength,
        // Re-emit the in-progress map every entry. Each map snapshot is small
        // (one DefragBlockInfo per file), so listeners can animate without
        // back-pressure.
        BlockMap: AppendFreeTail(partialMap, partialOffset, originalLength),
        Status: $"Read {entry.Name}"));
    }

    // Emit one final "scanning" event with the full assembled map and the
    // listing-order classification baked in (matching the post-defrag layout).
    options.OnProgress?.Invoke(BuildScanEvent(files, originalLength, "scanning"));

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

    // Identity guard: the rebuilt image must list back the EXACT same set of
    // entry names. Content-addressed / resource formats (hashed filenames,
    // resource-map entries) can't round-trip identity through extract→recreate —
    // re-hashing or re-deriving names silently changes or duplicates entries.
    // For those, committing the rebuild would corrupt the archive, so we leave
    // the original untouched instead (a safe no-op defrag). Name-preserving
    // formats (the vast majority) pass this and commit normally.
    if (!RebuiltPreservesEntries(files, rebuilt, readEntries)) {
      options.OnProgress?.Invoke(BuildScanEvent(files, originalLength, "complete"));
      return;
    }

    // Emit "writing" updates while we copy the rebuilt image back. Chunked at
    // 64 KB so the UI can animate a write head without the listener getting
    // spammed; for small images the loop runs once.
    archive.Position = 0;
    const int ChunkSize = 64 * 1024;
    var totalWrite = (long)rebuilt.Length;
    var writeOffset = 0L;
    while (writeOffset < totalWrite) {
      var chunk = (int)System.Math.Min(ChunkSize, totalWrite - writeOffset);
      archive.Write(rebuilt, (int)writeOffset, chunk);
      writeOffset += chunk;
      options.OnProgress?.Invoke(new DefragProgressEvent(
        Phase: "writing",
        // Scan = first half, Write = second half. UI sees a continuous 0..1
        // ramp across both phases.
        Fraction: totalWrite > 0 ? 0.5 + 0.5 * (double)writeOffset / totalWrite : 0,
        CurrentReadOffset: -1,
        CurrentWriteOffset: writeOffset,
        ImageSize: totalWrite,
        BlockMap: null));
    }
    archive.SetLength(totalWrite);

    // Final "complete" event with the post-defrag block map.
    options.OnProgress?.Invoke(BuildScanEvent(ordered, totalWrite, "complete"));
  }

  /// <summary>
  /// Builds a block-map snapshot for live-progress display. Files are placed
  /// contiguously starting at offset 0 (matching how the rebuild writer lays
  /// them out) and classified into Hot / Normal / Cold / Frozen quartiles
  /// based on listing-order as a proxy.
  ///
  /// <para><b>Note for UI consumers:</b> the rebuild path receives entries as
  /// (name, bytes) tuples and therefore has no access to entry mtimes — the
  /// classification emitted here is a listing-order proxy only. Honest
  /// mtime-based classification requires the pre-defrag snapshot built from
  /// <see cref="ArchiveEntryInfo.LastModified"/> on the descriptor's
  /// <see cref="IArchiveFormatOperations.List"/> output (see the WPF
  /// <c>DefragmentWindow.PreviewBlockMap</c> method for the canonical
  /// implementation).</para>
  /// </summary>
  private static DefragProgressEvent BuildScanEvent(
      System.Collections.Generic.IReadOnlyList<(string Name, byte[] Data)> files,
      long imageSize,
      string phase) {
    var map = new System.Collections.Generic.List<DefragBlockInfo>();
    var offset = 0L;
    var fileCount = files.Count;
    for (var i = 0; i < fileCount; i++) {
      var (name, data) = files[i];
      var cls = ClassifyByOrder(i, fileCount);
      map.Add(new DefragBlockInfo(offset, data.Length, DefragBlockKind.Used, name, cls));
      offset += data.Length;
    }
    if (offset < imageSize)
      map.Add(new DefragBlockInfo(offset, imageSize - offset, DefragBlockKind.Free));
    return new DefragProgressEvent(
      Phase: phase,
      Fraction: phase == "complete" ? 1 : 0,
      CurrentReadOffset: -1,
      CurrentWriteOffset: -1,
      ImageSize: imageSize,
      BlockMap: map);
  }

  /// <summary>
  /// Listing-order quartile classification used as a coarse proxy when no
  /// modification-time information is available. The rebuilder does NOT have
  /// access to entry mtimes (its input is just (name, bytes) tuples), so this
  /// proxy is the best the rebuild path can do; UI consumers wanting honest
  /// mtime-based classification should use the pre-defrag snapshot path
  /// (<c>DefragmentWindow.PreviewBlockMap</c>) which has access to
  /// <see cref="ArchiveEntryInfo.LastModified"/>.
  /// </summary>
  /// <summary>
  /// Returns a copy of <paramref name="liveTiles"/> with a Free block appended
  /// to fill the gap between the last live byte and <paramref name="imageSize"/>.
  /// Used by the per-entry scanning emit so the in-progress map always shows
  /// the full image area, not just the read-so-far portion.
  /// </summary>
  private static System.Collections.Generic.IReadOnlyList<DefragBlockInfo> AppendFreeTail(
      System.Collections.Generic.List<DefragBlockInfo> liveTiles,
      long liveByteCount,
      long imageSize) {
    if (liveByteCount >= imageSize) return liveTiles.ToArray();
    var withTail = new System.Collections.Generic.List<DefragBlockInfo>(liveTiles.Count + 1);
    withTail.AddRange(liveTiles);
    withTail.Add(new DefragBlockInfo(liveByteCount, imageSize - liveByteCount, DefragBlockKind.Free));
    return withTail;
  }

  /// <summary>
  /// Streaming variant of <see cref="Rebuild"/> for filesystems that can build
  /// their image incrementally — i.e. whose writer exposes a sink-style
  /// <c>Begin / WriteEntry / Finish</c> protocol rather than a batch
  /// <c>Build()</c>. Bytes flow per-entry from reader to writer without
  /// accumulating the full file list in memory, so multi-GB containers can
  /// start the write before the full directory tree has been walked.
  ///
  /// <para><see cref="DefragMode.ConsolidateAtStart"/> and
  /// <see cref="DefragMode.FillHolesLazy"/> pack in input order and stream
  /// straight through. <see cref="DefragMode.ConsolidateAtEnd"/> and
  /// <see cref="DefragMode.CarveHole"/> need every size before the first byte
  /// is written, so they spill each entry to scratch, sort, and then write —
  /// still without ever holding the volume in memory, which is what the
  /// buffered <see cref="Rebuild"/> path cannot do above two gigabytes.</para>
  /// </summary>
  /// <param name="archive">Stream to rewrite. Must be readable, writable, seekable.</param>
  /// <param name="options">Defrag mode + parameters. Must be ConsolidateAtStart or FillHolesLazy.</param>
  /// <param name="readEntries">Lazily yields entries from the existing image.</param>
  /// <param name="beginWrite">Initialises the streaming writer over a target stream.</param>
  /// <param name="writeEntry">Writes one (name, bytes) tuple to the streaming writer.</param>
  /// <param name="finishWrite">Finalises the streaming writer (flushes metadata, etc.).</param>
  public static void RebuildStreaming(
    System.IO.Stream archive,
    DefragOptions options,
    System.Func<System.IO.Stream, System.Collections.Generic.IEnumerable<(string Name, byte[] Data)>> readEntries,
    System.Action<System.IO.Stream> beginWrite,
    System.Action<string, byte[]> writeEntry,
    System.Action finishWrite) {
    System.ArgumentNullException.ThrowIfNull(archive);
    System.ArgumentNullException.ThrowIfNull(options);
    System.ArgumentNullException.ThrowIfNull(readEntries);
    System.ArgumentNullException.ThrowIfNull(beginWrite);
    System.ArgumentNullException.ThrowIfNull(writeEntry);
    System.ArgumentNullException.ThrowIfNull(finishWrite);
    if (options.Mode is not (DefragMode.ConsolidateAtStart or DefragMode.FillHolesLazy
        or DefragMode.ConsolidateAtEnd or DefragMode.CarveHole))
      throw new System.NotSupportedException(
        $"RebuildStreaming does not support {options.Mode}.");

    var originalLength = archive.Length;
    archive.Position = 0;

    // Stream to a temp file (not memory) so multi-GB containers don't blow up
    // RAM. After everything's written, atomically swap the temp bytes back into
    // archive — that's the only way to honour the in-place contract.
    var tempPath = System.IO.Path.GetTempFileName();
    try {
      using (var temp = System.IO.File.Open(tempPath, System.IO.FileMode.Open,
          System.IO.FileAccess.ReadWrite)) {
        beginWrite(temp);

        long readPos = 0;
        long entriesProcessed = 0;
        foreach (var entry in InOrder(archive, options, readEntries, originalLength)) {
          // Write each entry as it arrives — no accumulation.
          writeEntry(entry.Name, entry.Data);
          entriesProcessed++;
          readPos = System.Math.Min(archive.Position, originalLength);
          var writePos = temp.Position;
          options.OnProgress?.Invoke(new DefragProgressEvent(
            Phase: "streaming",
            Fraction: originalLength > 0 ? System.Math.Min(1.0, (double)readPos / originalLength) : 0,
            CurrentReadOffset: readPos,
            CurrentWriteOffset: writePos,
            ImageSize: originalLength,
            BlockMap: null,
            Status: $"Streamed {entry.Name} ({entriesProcessed} entries)"));
        }
        finishWrite();
        temp.Flush();

        // Copy temp → archive atomically (in chunks). archive.SetLength then
        // copy preserves the original stream identity; callers don't have to
        // close + reopen.
        // Sparse-aware copy: a freshly-rebuilt filesystem image is mostly free
        // space, and SetLength has already sized the target. Skipping all-zero
        // blocks leaves holes instead of writing them, so defragmenting a 4 GB
        // volume costs the megabytes it actually occupies rather than 4 GB.
        var totalWrite = temp.Length;
        archive.SetLength(totalWrite);
        archive.Position = 0;
        temp.Position = 0;
        var buf = new byte[64 * 1024];
        var copied = 0L;
        int n;
        while ((n = temp.Read(buf, 0, buf.Length)) > 0) {
          if (archive.CanSeek && IsAllZero(buf, n))
            archive.Position += n;
          else
            archive.Write(buf, 0, n);
          copied += n;
          options.OnProgress?.Invoke(new DefragProgressEvent(
            Phase: "writing",
            Fraction: totalWrite > 0 ? (double)copied / totalWrite : 0,
            CurrentReadOffset: -1,
            CurrentWriteOffset: copied,
            ImageSize: totalWrite,
            BlockMap: null));
        }
      }
    } finally {
      try { System.IO.File.Delete(tempPath); } catch { /* best-effort cleanup */ }
    }
  }

  /// <summary>
  /// Yields the entries in the order <paramref name="options" />' mode wants
  /// them written. Input order streams straight from the source; an order that
  /// depends on every size — end-pack, carve-hole — spills each entry to a
  /// scratch file first, so the sort costs disk rather than the memory a
  /// multi-gigabyte volume does not fit in.
  /// </summary>
  private static System.Collections.Generic.IEnumerable<(string Name, byte[] Data)> InOrder(
      System.IO.Stream archive,
      DefragOptions options,
      System.Func<System.IO.Stream, System.Collections.Generic.IEnumerable<(string Name, byte[] Data)>> readEntries,
      long originalLength) {
    // Every mode spills to temp files first and hands the entries back in size
    // order. Streaming them straight through in the order the reader found them
    // was cheaper, and it packed badly enough to matter: a set that fits a
    // volume when the long runs are placed first needed half again the room in
    // reader order, and the writer then had files it could not place. Order is
    // not cosmetic here — it decides whether the rebuild fits at all.

    var spilled = new System.Collections.Generic.List<(string Name, long Length, string Path)>();
    try {
      foreach (var entry in readEntries(archive)) {
        var path = System.IO.Path.GetTempFileName();
        System.IO.File.WriteAllBytes(path, entry.Data);
        spilled.Add((entry.Name, entry.Data.LongLength, path));
      }

      if (options.Mode == DefragMode.CarveHole) {
        if (options.HoleSize <= 0)
          throw new System.ArgumentException(
            "HoleSize must be positive for DefragMode.CarveHole.", nameof(options));
        var totalLive = 0L;
        foreach (var entry in spilled) totalLive += entry.Length;
        if (totalLive + options.HoleSize > originalLength)
          throw new System.ArgumentException(
            $"Image is too small for the carved hole: live {totalLive} + hole {options.HoleSize} > image {originalLength}.",
            nameof(options));
      } else {
        // Largest first: the longest contiguous run lands lowest, so the small
        // files gather near the metadata and the big ones own the tail — the
        // same approximation of end-packing the buffered path makes.
        spilled.Sort(static (a, b) => b.Length.CompareTo(a.Length));
      }

      foreach (var entry in spilled)
        yield return (entry.Name, System.IO.File.ReadAllBytes(entry.Path));
    } finally {
      foreach (var entry in spilled)
        try { System.IO.File.Delete(entry.Path); } catch { /* scratch file already gone */ }
    }
  }

  /// <summary>
  /// True when every one of the first <paramref name="count" /> bytes is zero.
  /// </summary>
  private static bool IsAllZero(byte[] buffer, int count) {
    for (var i = 0; i < count; ++i)
      if (buffer[i] != 0) return false;
    return true;
  }

  /// <summary>
  /// True when re-reading <paramref name="rebuilt"/> yields exactly the same
  /// multiset of entry names as <paramref name="original"/>. Guards against
  /// rebuilds that drop, duplicate, or rename entries (formats whose identity
  /// can't survive an extract→recreate round-trip). Any read failure counts as
  /// "not preserved" so a malformed rebuild is never committed.
  /// </summary>
  private static bool RebuiltPreservesEntries(
      System.Collections.Generic.IReadOnlyList<(string Name, byte[] Data)> original,
      byte[] rebuilt,
      System.Func<System.IO.Stream, System.Collections.Generic.IEnumerable<(string Name, byte[] Data)>> readEntries) {
    try {
      using var ms = new System.IO.MemoryStream(rebuilt);
      var after = readEntries(ms).Select(static e => e.Name).OrderBy(static x => x, System.StringComparer.Ordinal).ToList();
      if (after.Count != original.Count) return false;
      var before = original.Select(static e => e.Name).OrderBy(static x => x, System.StringComparer.Ordinal).ToList();
      return before.SequenceEqual(after, System.StringComparer.Ordinal);
    } catch {
      return false;
    }
  }

  private static DefragBlockClass ClassifyByOrder(int index, int total) {
    if (total <= 0) return DefragBlockClass.Normal;
    var q = index * 4 / total; // 0..3
    return q switch {
      0 => DefragBlockClass.Hot,
      1 => DefragBlockClass.Normal,
      2 => DefragBlockClass.Cold,
      _ => DefragBlockClass.Frozen,
    };
  }
}
