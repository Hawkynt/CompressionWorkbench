namespace Compression.Registry;

/// <summary>
/// Generic, round-trip-verified extract → re-create engine shared by maintenance
/// verbs. Rebuilds are staged, verified, progress-reporting, and cancellable;
/// the caller's original stream is not touched until the staged target has been
/// built successfully and cancellation is no longer accepted.
/// </summary>
public static class RebuildVerb {

  /// <summary>
  /// Extracts every live entry, re-creates the container in <paramref name="output"/>,
  /// verifies the exact live-name multiset, and reports block-map/read/write-head
  /// progress suitable for the maintenance UI.
  /// </summary>
  public static int RebuildToStream(
      Stream input,
      Stream output,
      IArchiveFormatOperations ops,
      IArchiveCreatable creator,
      IReadOnlyDictionary<string, string>? formatSpecific = null,
      IReadOnlySet<string>? syntheticNames = null,
      Action<DefragProgressEvent>? onProgress = null,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(ops);
    ArgumentNullException.ThrowIfNull(creator);
    if (!input.CanRead || !input.CanSeek)
      throw new ArgumentException("Rebuild input must be readable and seekable.", nameof(input));
    if (!output.CanWrite || !output.CanSeek)
      throw new ArgumentException("Rebuild output must be writable and seekable.", nameof(output));

    cancellationToken.ThrowIfCancellationRequested();
    input.Position = 0;
    var sourceEntries = ops.List(input, null);
    var sourceNames = LiveNameList(sourceEntries);
    var sourceFileCount = sourceNames.Count;
    var sourceLength = Math.Max(1L, input.Length);
    var liveEntries = sourceEntries
      .Where(e => !e.IsDirectory && (syntheticNames == null || !syntheticNames.Contains(e.Name)))
      .ToArray();
    var totalLogical = Math.Max(1L, liveEntries.Sum(e => Math.Max(0L, e.OriginalSize)));
    var sourceLayout = BuildSourceLayout(input, ops, sourceEntries);

    onProgress?.Invoke(new DefragProgressEvent(
      "scanning", 0, 0, -1, sourceLength, sourceLayout,
      $"Scanning {sourceFileCount:N0} live entr{(sourceFileCount == 1 ? "y" : "ies")} before staged rebuild"));

    var tmpDir = Path.Combine(Path.GetTempPath(), "cwb_rebuild_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(tmpDir);
    try {
      // Extract entry-by-entry rather than through the opaque bulk Extract call.
      // This makes large WORM/re-layout passes cancellable and gives the UI an
      // honest moving read head while bytes are consumed from the source.
      long logicalDone = 0;
      var liveIndex = 0;
      foreach (var entry in sourceEntries) {
        cancellationToken.ThrowIfCancellationRequested();
        var target = SafeExtractPath(tmpDir, entry.Name);
        if (entry.IsDirectory) {
          Directory.CreateDirectory(target);
          continue;
        }
        if (syntheticNames != null && syntheticNames.Contains(entry.Name))
          continue;

        ++liveIndex;
        var parent = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

        var entrySize = Math.Max(0L, entry.OriginalSize);
        onProgress?.Invoke(new DefragProgressEvent(
          "reading", 0.45 * logicalDone / totalLogical,
          ScaleOffset(logicalDone, totalLogical, sourceLength), -1,
          sourceLength, HighlightEntry(sourceLayout, entry.Name),
          $"Reading {liveIndex:N0}/{liveEntries.Length:N0}: {entry.Name}"));

        input.Position = 0;
        using var src = ops.OpenEntry(input, entry.Name, null);
        using var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024,
          FileOptions.SequentialScan);
        var buffer = new byte[64 * 1024];
        long entryDone = 0;
        var lastReport = Environment.TickCount64;
        while (true) {
          cancellationToken.ThrowIfCancellationRequested();
          var read = src.Read(buffer, 0, buffer.Length);
          if (read <= 0) break;
          dst.Write(buffer, 0, read);
          entryDone += read;
          var now = Environment.TickCount64;
          if (now - lastReport >= 50) {
            lastReport = now;
            var effective = logicalDone + (entrySize > 0 ? Math.Min(entrySize, entryDone) : entryDone);
            var fraction = 0.45 * Math.Clamp((double)effective / totalLogical, 0, 1);
            onProgress?.Invoke(new DefragProgressEvent(
              "reading", fraction,
              ScaleOffset(effective, totalLogical, sourceLength), -1,
              sourceLength, null,
              $"Reading {liveIndex:N0}/{liveEntries.Length:N0}: {entry.Name} ({entryDone:N0} bytes)"));
          }
        }
        dst.Flush(flushToDisk: true);
        logicalDone += entrySize > 0 ? entrySize : entryDone;
      }

      cancellationToken.ThrowIfCancellationRequested();

      var inputs = new List<ArchiveInputInfo>();
      foreach (var dir in Directory.GetDirectories(tmpDir, "*", SearchOption.AllDirectories)) {
        var rel = Path.GetRelativePath(tmpDir, dir).Replace('\\', '/');
        inputs.Add(new ArchiveInputInfo("", rel + "/", true));
      }
      foreach (var file in Directory.GetFiles(tmpDir, "*", SearchOption.AllDirectories)) {
        var rel = Path.GetRelativePath(tmpDir, file).Replace('\\', '/');
        inputs.Add(new ArchiveInputInfo(file, rel, false));
      }

      var visualSize = Math.Max(sourceLength, totalLogical);
      var targetLayout = BuildWeightedLayout(liveEntries, visualSize);
      onProgress?.Invoke(new DefragProgressEvent(
        "writing", 0.45, -1, 0, visualSize, targetLayout,
        "Building staged target — original container is still unchanged"));

      // FormatSpecific is a mutable, case-insensitive map; the caller hands in a read-only view,
      // so copy it and keep the comparer the default initializer uses.
      var options = new FormatCreateOptions {
        FormatSpecific = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
      };
      if (formatSpecific != null)
        foreach (var pair in formatSpecific)
          options.FormatSpecific[pair.Key] = pair.Value;
      output.Position = 0;
      output.SetLength(0);
      using (var progressOutput = new ProgressWriteStream(output, cancellationToken, maxPosition => {
        var fraction = 0.45 + 0.45 * Math.Clamp((double)maxPosition / sourceLength, 0, 1);
        onProgress?.Invoke(new DefragProgressEvent(
          "writing", fraction, -1, maxPosition, visualSize, null,
          $"Writing staged target: {maxPosition:N0} bytes"));
      })) {
        creator.Create(progressOutput, inputs, options);
        progressOutput.Flush();
      }

      cancellationToken.ThrowIfCancellationRequested();
      onProgress?.Invoke(new DefragProgressEvent(
        "verifying", 0.92, -1, Math.Max(0, output.Position), Math.Max(1, output.Length), null,
        "Verifying rebuilt container before commit"));

      output.Position = 0;
      List<string> rebuiltNames;
      try {
        rebuiltNames = LiveNameList(ops.List(output, null));
      } catch (Exception ex) {
        throw new InvalidOperationException(
          $"Rebuilt image could not be listed back ({ex.GetType().Name}: {ex.Message}); refusing a lossy rebuild.", ex);
      }
      if (!rebuiltNames.SequenceEqual(sourceNames, StringComparer.Ordinal))
        throw new InvalidOperationException(
          $"Rebuild changed the entry set ({sourceFileCount} → {rebuiltNames.Count}); refusing a non-identity-preserving rebuild.");

      cancellationToken.ThrowIfCancellationRequested();
      var finalLength = Math.Max(1L, output.Length);
      onProgress?.Invoke(new DefragProgressEvent(
        "staged", 0.98, -1, Math.Max(0, output.Length - 1), finalLength,
        BuildWeightedLayout(liveEntries, finalLength),
        "Staged rebuild verified; ready to commit"));
      output.Position = 0;
      return sourceFileCount;
    } finally {
      try { Directory.Delete(tmpDir, true); } catch { /* best effort */ }
    }
  }

  /// <summary>
  /// Rebuilds into a scratch file, verifies it, and only then replaces the
  /// caller-supplied stream. Cancellation is honoured until commit starts;
  /// once commit begins it runs to completion so a cancellation cannot leave
  /// the original half-overwritten.
  /// </summary>
  public static void RebuildInPlace(
      Stream archive,
      IArchiveFormatOperations ops,
      IArchiveCreatable creator,
      IReadOnlyDictionary<string, string>? formatSpecific = null,
      Action<DefragProgressEvent>? onProgress = null,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(archive);
    using var rebuilt = CreateScratchStream();
    RebuildToStream(archive, rebuilt, ops, creator, formatSpecific,
      onProgress: onProgress, cancellationToken: cancellationToken);

    // Point of no return. Do not inspect cancellation again after announcing
    // commit; callers disable Cancel for this phase.
    onProgress?.Invoke(new DefragProgressEvent(
      "committing", 0.99, -1, 0, Math.Max(1, rebuilt.Length), null,
      "Committing verified staged target — cancellation is no longer safe"));

    archive.Position = 0;
    archive.SetLength(0);
    rebuilt.Position = 0;
    rebuilt.CopyTo(archive);
    archive.Flush();

    var finalLength = Math.Max(1L, archive.Length);
    archive.Position = 0;
    var entries = ops.List(archive, null);
    onProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, finalLength,
      BuildWeightedLayout(entries.Where(e => !e.IsDirectory).ToArray(), finalLength),
      "Rebuild committed successfully"));
  }

  /// <summary>
  /// Rebuild-based edit used by the generic modifier. Mutation and validation
  /// happen off to the side; the original is overwritten only after a valid
  /// staged result exists.
  /// </summary>
  public static void EditViaRebuild(Stream archive, IArchiveFormatOperations ops,
      IArchiveCreatable creator, Action<string> mutate) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(mutate);
    var tmpDir = Path.Combine(Path.GetTempPath(), "cwb_edit_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(tmpDir);
    try {
      archive.Position = 0;
      ops.Extract(archive, tmpDir, null, null);
      mutate(tmpDir);

      var inputs = new List<ArchiveInputInfo>();
      foreach (var dir in Directory.GetDirectories(tmpDir, "*", SearchOption.AllDirectories)) {
        var rel = Path.GetRelativePath(tmpDir, dir).Replace('\\', '/');
        inputs.Add(new ArchiveInputInfo("", rel + "/", true));
      }
      foreach (var file in Directory.GetFiles(tmpDir, "*", SearchOption.AllDirectories)) {
        var rel = Path.GetRelativePath(tmpDir, file).Replace('\\', '/');
        inputs.Add(new ArchiveInputInfo(file, rel, false));
      }

      using var rebuilt = CreateScratchStream();
      creator.Create(rebuilt, inputs, new FormatCreateOptions());
      rebuilt.Position = 0;
      _ = ops.List(rebuilt, null);

      archive.Position = 0;
      archive.SetLength(0);
      rebuilt.Position = 0;
      rebuilt.CopyTo(archive);
      archive.Flush();
    } finally {
      try { Directory.Delete(tmpDir, true); } catch { /* best effort */ }
    }
  }

  /// <summary>
  /// Transactional purge for a mutable container. The modifier operates on a
  /// staged copy and the caller's stream is replaced only after the result lists
  /// successfully with every original live entry gone.
  /// </summary>
  /// <remarks>
  /// Two things stop a first, plain attempt from being the whole story, and both
  /// are properties of the container rather than defects of the purge:
  /// <list type="bullet">
  ///   <item><description>A reader may render views of the container itself — a
  ///     whole-image entry, a metadata rendering, a raw superblock or log dump.
  ///     Asking the modifier to drop one is meaningless and finding it afterwards
  ///     proves nothing, so those names are excluded and judged against
  ///     <see cref="StructuralFloor"/>. Establishing the floor costs a create, so
  ///     it is paid only once the plain attempt has tripped over one.</description></item>
  ///   <item><description>A native modifier may own a narrower namespace than the
  ///     one its reader lists — a sector or block index rather than a file inside
  ///     the filesystem carried in it. The verb is still reachable there, through
  ///     the same extract → drop → re-create rebuild the interface offers by
  ///     default, so that is tried before the purge is reported impossible.</description></item>
  /// </list>
  /// </remarks>
  public static void PurgeViaModifier(Stream archive, IArchiveFormatOperations ops, IArchiveModifiable modifier) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(ops);
    ArgumentNullException.ThrowIfNull(modifier);
    if (!archive.CanRead || !archive.CanWrite || !archive.CanSeek)
      throw new ArgumentException("Purge requires a readable, writable, seekable stream.", nameof(archive));

    var attempt = TryPurge(archive, ops, modifier, null, false);
    if (!attempt.Purged) {
      var floor = StructuralFloor(ops);
      if (floor.Count != 0)
        attempt = Supersede(attempt, TryPurge(archive, ops, modifier, floor, false));
      if (!attempt.Purged && ops is IArchiveCreatable)
        attempt = Supersede(attempt, TryPurge(archive, ops, modifier, floor, true));
      if (!attempt.Purged) {
        attempt.Staged.Dispose();
        throw attempt.Failure ?? PurgeIncomplete(attempt.Survivors);
      }
    }

    using (attempt.Staged) {
      archive.Position = 0;
      archive.SetLength(0);
      attempt.Staged.Position = 0;
      attempt.Staged.CopyTo(archive);
      archive.Flush();
    }
  }

  /// <summary>
  /// One staged purge attempt: everything listed outside <paramref name="floor"/>
  /// is dropped, either through the descriptor's own modifier or — when
  /// <paramref name="viaRebuild"/> — through the extract → drop → re-create engine.
  /// </summary>
  private static PurgeAttempt TryPurge(Stream archive, IArchiveFormatOperations ops, IArchiveModifiable modifier,
      IReadOnlySet<string>? floor, bool viaRebuild) {
    var staged = CreateScratchStream();
    try {
      archive.Position = 0;
      archive.CopyTo(staged);
      staged.Flush();

      staged.Position = 0;
      var sourceNames = ops.List(staged, null)
        .Where(e => !e.IsDirectory)
        .Select(e => e.Name)
        .Where(name => floor == null || !floor.Contains(name))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
      if (sourceNames.Length == 0) return new PurgeAttempt(staged, [], null);

      staged.Position = 0;
      if (viaRebuild)
        // Everything goes, including the structural renderings: they are views of
        // the container, and feeding a whole-image view back in as an input would
        // re-ingest the very bytes the purge is removing.
        EditViaRebuild(staged, ops, (IArchiveCreatable)ops, tmpDir => {
          foreach (var file in Directory.GetFiles(tmpDir, "*", SearchOption.AllDirectories))
            File.Delete(file);
        });
      else
        modifier.Remove(staged, sourceNames);

      staged.Position = 0;
      var remaining = ops.List(staged, null)
        .Where(e => !e.IsDirectory)
        .Select(e => e.Name)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
      return new PurgeAttempt(staged, sourceNames.Where(remaining.Contains).ToArray(), null);
    } catch (Exception ex) {
      return new PurgeAttempt(staged, [], ex);
    }
  }

  /// <summary>
  /// Keeps <paramref name="next"/> and releases the staged copy of the attempt it
  /// replaces.
  /// </summary>
  private static PurgeAttempt Supersede(PurgeAttempt previous, PurgeAttempt next) {
    previous.Staged.Dispose();
    return next;
  }

  private sealed record PurgeAttempt(FileStream Staged, string[] Survivors, Exception? Failure) {
    public bool Purged => this.Failure == null && this.Survivors.Length == 0;
  }

  private static InvalidOperationException PurgeIncomplete(string[] survivors)
    => new($"Purge left {survivors.Length} original live entr{(survivors.Length == 1 ? "y" : "ies")} behind "
      + $"({string.Join(", ", survivors)}); original container retained.");

  /// <summary>
  /// The names this format renders from the container rather than from anything
  /// stored in it: whatever the descriptor declares through
  /// <see cref="ISyntheticEntryNames"/>, plus whatever an empty container of the
  /// same format still lists. A format that cannot be created empty, or cannot
  /// read back what it then wrote, contributes only the declared half.
  /// </summary>
  private static HashSet<string> StructuralFloor(IArchiveFormatOperations ops) {
    var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    if (ops is ISyntheticEntryNames declared)
      foreach (var name in declared.SyntheticEntryNames)
        result.Add(name);
    if (ops is not IArchiveCreatable creator) return result;
    try {
      using var empty = CreateScratchStream();
      creator.Create(empty, [], new FormatCreateOptions());
      empty.Position = 0;
      foreach (var entry in ops.List(empty, null))
        if (!entry.IsDirectory)
          result.Add(entry.Name);
    } catch {
      // No empty form, or one this reader rejects. Only what the descriptor
      // declares outright is excused.
    }
    return result;
  }

  private static List<string> LiveNameList(IEnumerable<ArchiveEntryInfo> entries)
    => entries.Where(e => !e.IsDirectory).Select(e => e.Name)
      .OrderBy(n => n, StringComparer.Ordinal).ToList();

  private static IReadOnlyList<DefragBlockInfo> BuildSourceLayout(
      Stream input, IArchiveFormatOperations ops, IReadOnlyList<ArchiveEntryInfo> entries) {
    try {
      input.Position = 0;
      var extents = ops switch {
        IFilesystemExtentMap fs => fs.EnumerateExtents(input).ToArray(),
        IArchiveLayoutMap archive => archive.EnumerateLayout(input).ToArray(),
        _ => [],
      };
      if (extents.Length > 0) return extents;
    } catch {
      // Visualization is best-effort; the actual rebuild remains fully verified.
    }
    return BuildWeightedLayout(entries.Where(e => !e.IsDirectory).ToArray(), Math.Max(1, input.Length));
  }

  private static IReadOnlyList<DefragBlockInfo> BuildWeightedLayout(
      IReadOnlyList<ArchiveEntryInfo> entries, long totalSize) {
    totalSize = Math.Max(1, totalSize);
    if (entries.Count == 0)
      return [new DefragBlockInfo(0, totalSize, DefragBlockKind.Free)];

    var weights = entries.Select(e => Math.Max(1L, e.OriginalSize)).ToArray();
    var totalWeight = Math.Max(1L, weights.Sum());
    var result = new List<DefragBlockInfo>(entries.Count);
    long cumulative = 0;
    long cursor = 0;
    for (var i = 0; i < entries.Count; i++) {
      cumulative += weights[i];
      var end = i == entries.Count - 1
        ? totalSize
        : (long)((double)cumulative / totalWeight * totalSize);
      end = Math.Clamp(end, cursor, totalSize);
      var length = end - cursor;
      if (length > 0)
        result.Add(new DefragBlockInfo(cursor, length, DefragBlockKind.Used,
          entries[i].Name, Classify(entries[i].Method)));
      cursor = end;
    }
    if (cursor < totalSize)
      result.Add(new DefragBlockInfo(cursor, totalSize - cursor, DefragBlockKind.Free));
    return result;
  }

  private static IReadOnlyList<DefragBlockInfo> HighlightEntry(
      IReadOnlyList<DefragBlockInfo> source, string entryName) {
    var changed = false;
    var result = new DefragBlockInfo[source.Count];
    for (var i = 0; i < source.Count; i++) {
      var block = source[i];
      if (block.FileName != null && string.Equals(block.FileName, entryName, StringComparison.Ordinal)) {
        result[i] = block with { Kind = DefragBlockKind.InProgress };
        changed = true;
      } else {
        result[i] = block;
      }
    }
    return changed ? result : source;
  }

  private static DefragBlockClass Classify(string? method) {
    var value = (method ?? "").ToUpperInvariant();
    if (value.Contains("STORE") || value.Contains("COPY") || value == "NONE" || value.Length == 0)
      return DefragBlockClass.Frozen;
    if (value.Contains("LZMA") || value.Contains("PPMD") || value.Contains("BZIP"))
      return DefragBlockClass.Hot;
    if (value.Contains("ZSTD") || value.Contains("LZ4"))
      return DefragBlockClass.Cold;
    return DefragBlockClass.Normal;
  }

  private static long ScaleOffset(long done, long total, long imageSize)
    => total <= 0 ? 0 : Math.Clamp((long)((double)done / total * imageSize), 0, Math.Max(0, imageSize - 1));

  private static string SafeExtractPath(string root, string archiveName) {
    var normalized = archiveName.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
    var rootFull = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
    var candidate = Path.GetFullPath(Path.Combine(root, normalized));
    if (!candidate.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(candidate, rootFull.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
      throw new InvalidDataException($"Entry path escapes rebuild staging directory: {archiveName}");
    return candidate;
  }

  /// <summary>A writable scratch stream not bounded by byte[] / MemoryStream size.</summary>
  internal static FileStream CreateScratchStream()
    => new(Path.Combine(Path.GetTempPath(), "cwb_rebuild_" + Guid.NewGuid().ToString("N") + ".tmp"),
      FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024, FileOptions.DeleteOnClose);

  /// <summary>
  /// Write-through wrapper used solely to expose actual target-byte progress and
  /// make long encoder writes cancellable without giving ownership of the target
  /// stream to the wrapper.
  /// </summary>
  private sealed class ProgressWriteStream(
      Stream inner, CancellationToken cancellationToken, Action<long> report) : Stream {
    private long _maxPosition;
    private long _lastReportTick;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count) {
      cancellationToken.ThrowIfCancellationRequested();
      inner.Write(buffer, offset, count);
      Report();
    }

    public override void Write(ReadOnlySpan<byte> buffer) {
      cancellationToken.ThrowIfCancellationRequested();
      inner.Write(buffer);
      Report();
    }

    public override void WriteByte(byte value) {
      cancellationToken.ThrowIfCancellationRequested();
      inner.WriteByte(value);
      Report();
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationTokenFromCaller = default) {
      cancellationToken.ThrowIfCancellationRequested();
      cancellationTokenFromCaller.ThrowIfCancellationRequested();
      await inner.WriteAsync(buffer, cancellationTokenFromCaller).ConfigureAwait(false);
      Report();
    }

    private void Report() {
      _maxPosition = Math.Max(_maxPosition, inner.Position);
      var now = Environment.TickCount64;
      if (now - _lastReportTick < 50) return;
      _lastReportTick = now;
      report(_maxPosition);
    }

    protected override void Dispose(bool disposing) {
      if (disposing) {
        try { inner.Flush(); } catch { }
        report(Math.Max(_maxPosition, inner.CanSeek ? inner.Position : _maxPosition));
      }
      base.Dispose(disposing);
    }
  }
}
