from pathlib import Path


def read(path: str) -> str:
    return Path(path).read_text(encoding='utf-8')


def write(path: str, content: str) -> None:
    Path(path).write_text(content, encoding='utf-8')


# ── Generic rebuild engine: real progress + cancellation + staged commit ─────
write('Compression.Registry/RebuildVerb.cs', r'''namespace Compression.Registry;

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
      for (var i = 0; i < sourceEntries.Count; i++) {
        cancellationToken.ThrowIfCancellationRequested();
        var entry = sourceEntries[i];
        var target = SafeExtractPath(tmpDir, entry.Name);
        if (entry.IsDirectory) {
          Directory.CreateDirectory(target);
          continue;
        }
        if (syntheticNames != null && syntheticNames.Contains(entry.Name))
          continue;

        var parent = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

        var entrySize = Math.Max(0L, entry.OriginalSize);
        onProgress?.Invoke(new DefragProgressEvent(
          "reading", 0.45 * logicalDone / totalLogical,
          ScaleOffset(logicalDone, totalLogical, sourceLength), -1,
          sourceLength, HighlightEntry(sourceLayout, entry.Name),
          $"Reading {i + 1:N0}/{sourceEntries.Count:N0}: {entry.Name}"));

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
            var effective = logicalDone + Math.Min(entrySize > 0 ? entrySize : entryDone, entryDone);
            var fraction = 0.45 * Math.Clamp((double)effective / totalLogical, 0, 1);
            onProgress?.Invoke(new DefragProgressEvent(
              "reading", fraction,
              ScaleOffset(effective, totalLogical, sourceLength), -1,
              sourceLength, null,
              $"Reading {i + 1:N0}/{sourceEntries.Count:N0}: {entry.Name} ({entryDone:N0} bytes)"));
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

      var options = new FormatCreateOptions { FormatSpecific = formatSpecific };
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

    // This is the point of no return. Do not inspect cancellation again after
    // announcing commit: callers can disable the Cancel button for this phase.
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
  /// Rebuild-based edit used by the generic modifier. The mutation and rebuilt
  /// validation happen off to the side; the original is overwritten only after
  /// a valid staged result exists.
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
  public static void PurgeViaModifier(Stream archive, IArchiveFormatOperations ops, IArchiveModifiable modifier) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(ops);
    ArgumentNullException.ThrowIfNull(modifier);
    if (!archive.CanRead || !archive.CanWrite || !archive.CanSeek)
      throw new ArgumentException("Purge requires a readable, writable, seekable stream.", nameof(archive));

    using var staged = CreateScratchStream();
    archive.Position = 0;
    archive.CopyTo(staged);
    staged.Flush();

    staged.Position = 0;
    var sourceNames = ops.List(staged, null)
      .Where(e => !e.IsDirectory)
      .Select(e => e.Name)
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .ToArray();
    if (sourceNames.Length == 0) return;

    staged.Position = 0;
    modifier.Remove(staged, sourceNames);
    staged.Position = 0;
    var remaining = ops.List(staged, null)
      .Where(e => !e.IsDirectory)
      .Select(e => e.Name)
      .ToHashSet(StringComparer.OrdinalIgnoreCase);
    var survivors = sourceNames.Where(remaining.Contains).ToArray();
    if (survivors.Length != 0)
      throw new InvalidOperationException(
        $"Purge left {survivors.Length} original live entr{(survivors.Length == 1 ? "y" : "ies")} behind; original container retained.");

    archive.Position = 0;
    archive.SetLength(0);
    staged.Position = 0;
    staged.CopyTo(archive);
    archive.Flush();
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
''')


# ── Defrag options/default implementation: cancellation reaches rebuild ──────
defrag_options = read('Compression.Registry/DefragOptions.cs')
needle = '''  public Action<DefragProgressEvent>? OnProgress { get; init; }\n'''
addition = '''  public Action<DefragProgressEvent>? OnProgress { get; init; }\n\n  /// <summary>\n  /// Cooperative cancellation for long maintenance operations. Generic staged\n  /// rebuilds honour it while reading and writing and never commit a cancelled\n  /// target. Native in-place movers may honour it at their next safe move boundary.\n  /// </summary>\n  public CancellationToken CancellationToken { get; init; }\n'''
if needle in defrag_options and 'public CancellationToken CancellationToken' not in defrag_options:
    defrag_options = defrag_options.replace(needle, addition, 1)
write('Compression.Registry/DefragOptions.cs', defrag_options)

write('Compression.Registry/IArchiveDefragmentable.cs', r'''namespace Compression.Registry;

/// <summary>
/// Opt-in capability for physical or logical re-layout. Native mutable filesystems
/// may move extents in place; WORM/archive containers can satisfy the same verb by
/// building a verified staged target and committing it after completion.
/// </summary>
public interface IArchiveDefragmentable {
  /// <summary>Defragments using the format's default consolidate-at-start strategy.</summary>
  void Defragment(Stream archive) {
    if (this is not IArchiveFormatOperations ops || this is not IArchiveCreatable creator)
      throw new NotSupportedException(
        "The default Defragment requires IArchiveFormatOperations + IArchiveCreatable.");
    RebuildVerb.RebuildInPlace(archive, ops, creator);
  }

  /// <summary>
  /// Rewrites according to <paramref name="options"/>. The default implementation
  /// uses the progress-reporting/cancellable staged rebuild for descriptors that
  /// rely on the interface default, while preserving a descriptor's own native
  /// parameterless implementation when it has one.
  /// </summary>
  void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    if (options.Mode != DefragMode.ConsolidateAtStart)
      throw new NotSupportedException(
        $"This descriptor only supports DefragMode.ConsolidateAtStart; got {options.Mode}.");

    // If the concrete descriptor has a public native parameterless mover, retain
    // its semantics. Generic promoted descriptors have no such method and use the
    // staged rebuild below, gaining smooth progress + safe cancellation for free.
    var native = this.GetType().GetMethod(nameof(Defragment), [typeof(Stream)]);
    if (native != null && native.DeclaringType != typeof(IArchiveDefragmentable)) {
      options.CancellationToken.ThrowIfCancellationRequested();
      this.Defragment(archive);
      return;
    }

    if (this is not IArchiveFormatOperations ops || this is not IArchiveCreatable creator) {
      this.Defragment(archive);
      return;
    }
    RebuildVerb.RebuildInPlace(archive, ops, creator,
      onProgress: options.OnProgress, cancellationToken: options.CancellationToken);
  }
}
''')


# ── 7z regrouping: cancellable phase-level progress ──────────────────────────
solid = read('FileFormats/FileFormat.SevenZip/SolidBlockOptimizer.cs')
if 'public sealed record DetailedProgress' not in solid:
    solid = solid.replace(
'''  public sealed class TrialResult {\n    public required string StrategyName { get; init; }\n    public required long OutputSize { get; init; }\n    public required TimeSpan Elapsed { get; init; }\n  }\n''',
'''  public sealed class TrialResult {\n    public required string StrategyName { get; init; }\n    public required long OutputSize { get; init; }\n    public required TimeSpan Elapsed { get; init; }\n  }\n\n  /// <summary>Detailed progress for the block-map UI during extraction/regrouping.</summary>\n  public sealed record DetailedProgress(\n    string Phase, int Current, int Total, string? Name, long BytesDone, long BytesTotal);\n''')
solid = solid.replace(
'''  public static OptimizeResult Optimize(Stream archive, int maxTrials = 5, ProgressCallback? onProgress = null) {''',
'''  public static OptimizeResult Optimize(Stream archive, int maxTrials = 5, ProgressCallback? onProgress = null,\n      Action<DetailedProgress>? onDetailedProgress = null, CancellationToken cancellationToken = default) {''')
solid = solid.replace(
'''    // Step 1: Extract all entries from the input archive\n    archive.Position = 0;\n    var reader = new SevenZipReader(archive, leaveOpen: true);\n    var entries = new List<(string Name, byte[] Data, SevenZipEntry Meta)>();\n    for (var i = 0; i < reader.Entries.Count; i++) {\n      var e = reader.Entries[i];\n      if (e.IsDirectory) continue;\n      var data = reader.Extract(i);\n      entries.Add((e.Name, data, e));\n    }''',
'''    // Step 1: Extract all entries from the input archive. This is an actual\n    // progress source for the block-map read head rather than an indeterminate spinner.\n    archive.Position = 0;\n    var reader = new SevenZipReader(archive, leaveOpen: true);\n    var fileEntries = reader.Entries.Where(e => !e.IsDirectory).ToArray();\n    var totalBytes = Math.Max(1L, fileEntries.Sum(e => Math.Max(0L, e.Size)));\n    long extractedBytes = 0;\n    var entries = new List<(string Name, byte[] Data, SevenZipEntry Meta)>();\n    for (var i = 0; i < reader.Entries.Count; i++) {\n      cancellationToken.ThrowIfCancellationRequested();\n      var e = reader.Entries[i];\n      if (e.IsDirectory) continue;\n      onDetailedProgress?.Invoke(new DetailedProgress(\n        "extracting", entries.Count, fileEntries.Length, e.Name, extractedBytes, totalBytes));\n      var data = reader.Extract(i);\n      extractedBytes += data.LongLength;\n      entries.Add((e.Name, data, e));\n      onDetailedProgress?.Invoke(new DetailedProgress(\n        "extracting", entries.Count, fileEntries.Length, e.Name, extractedBytes, totalBytes));\n    }''')
solid = solid.replace(
'''    for (var i = 0; i < strategies.Count; i++) {\n      var (name, grouper) = strategies[i];\n      onProgress?.Invoke(i, strategies.Count, name);\n\n      var sw = System.Diagnostics.Stopwatch.StartNew();\n      try {\n        var groups = grouper(entries);\n        var output = BuildArchive(entries, groups);''',
'''    for (var i = 0; i < strategies.Count; i++) {\n      cancellationToken.ThrowIfCancellationRequested();\n      var (name, grouper) = strategies[i];\n      onProgress?.Invoke(i, strategies.Count, name);\n      onDetailedProgress?.Invoke(new DetailedProgress(\n        "strategy", i, strategies.Count, name, i, strategies.Count));\n\n      var sw = System.Diagnostics.Stopwatch.StartNew();\n      try {\n        var groups = grouper(entries);\n        var output = BuildArchive(entries, groups, cancellationToken,\n          (current, total, entryName) => onDetailedProgress?.Invoke(new DetailedProgress(\n            "building", current, total, entryName, current, total)));''')
solid = solid.replace(
'''  private static byte[] BuildArchive(\n      IReadOnlyList<(string Name, byte[] Data, SevenZipEntry Meta)> entries,\n      IReadOnlyList<int[]> groups) {''',
'''  private static byte[] BuildArchive(\n      IReadOnlyList<(string Name, byte[] Data, SevenZipEntry Meta)> entries,\n      IReadOnlyList<int[]> groups, CancellationToken cancellationToken,\n      Action<int, int, string?>? onProgress) {''')
solid = solid.replace(
'''    foreach (var group in groups)\n      foreach (var idx in group) {\n        var (name, data, meta) = entries[idx];''',
'''    foreach (var group in groups)\n      foreach (var idx in group) {\n        cancellationToken.ThrowIfCancellationRequested();\n        var (name, data, meta) = entries[idx];\n        onProgress?.Invoke(addOrder, entries.Count, name);''')
solid = solid.replace(
'''        entryIndexMap[idx] = addOrder++;\n      }''',
'''        entryIndexMap[idx] = addOrder++;\n        onProgress?.Invoke(addOrder, entries.Count, name);\n      }''', 1)
solid = solid.replace(
'''    writer.FinishWithBlocks(blockDescs);''',
'''    cancellationToken.ThrowIfCancellationRequested();\n    writer.FinishWithBlocks(blockDescs);\n    cancellationToken.ThrowIfCancellationRequested();''')
write('FileFormats/FileFormat.SevenZip/SolidBlockOptimizer.cs', solid)


# ── Maintenance window: block-map is always the progress surface ─────────────
xaml = read('Compression.UI/Views/DefragmentWindow.xaml')
xaml = xaml.replace(
'''        <TextBlock TextWrapping="Wrap" Foreground="DimGray"\n                   Text="Archive repack: entries will be extracted and re-created with optimal compression settings. The layout strategy modes above do not apply to archive formats." />''',
'''        <TextBlock TextWrapping="Wrap" Foreground="DimGray"\n                   Text="Archive repack/re-group: the block map stays live while a staged target is rebuilt. Green = source read head; orange = staged-target write head. The existing archive remains unchanged until the verified target is committed." />''')
xaml = xaml.replace(
'''      <Button Content="Close" Width="80" Padding="4" Click="OnClose" IsCancel="True" />''',
'''      <Button x:Name="CancelOperationBtn" Content="Cancel" Width="80" Padding="4" Margin="0,0,8,0"\n              Click="OnCancelOperation" IsEnabled="False"\n              ToolTip="Cancel the active maintenance pass. Staged rebuilds discard the target and leave the original unchanged; native in-place moves are best-effort." />\n      <Button x:Name="CloseBtn" Content="Close" Width="80" Padding="4" Click="OnClose" IsCancel="True" />''')
write('Compression.UI/Views/DefragmentWindow.xaml', xaml)

window = read('Compression.UI/Views/DefragmentWindow.xaml.cs')
field_anchor = '''  private LayoutTemplate? _selectedLayoutProfile;\n'''
fields = '''  private LayoutTemplate? _selectedLayoutProfile;\n  private CancellationTokenSource? _operationCts;\n  private bool _operationIsStaged;\n  private bool _operationCommitStarted;\n  private string? _operationName;\n'''
if field_anchor in window and '_operationCts' not in window:
    window = window.replace(field_anchor, fields, 1)

# Pass cancellation through generic/native defrag and surface every progress phase.
window = window.replace(
'''      void OnProgress(DefragProgressEvent ev) {\n        if (ev.Phase == "complete" && !string.IsNullOrEmpty(ev.Status))\n          lastCompleteStatus = ev.Status;\n        Dispatcher.BeginInvoke(() => {''',
'''      void OnProgress(DefragProgressEvent ev) {\n        if (ev.Phase == "complete" && !string.IsNullOrEmpty(ev.Status))\n          lastCompleteStatus = ev.Status;\n        Dispatcher.BeginInvoke(() => {\n          if (ev.Phase == "committing") {\n            this._operationCommitStarted = true;\n            CancelOperationBtn.IsEnabled = false;\n          }\n          if (!string.IsNullOrEmpty(ev.Status) && LayoutStatusLbl != null)\n            LayoutStatusLbl.Text = ev.Status;''')
window = window.replace(
'''    Task.Run(() => {\n      var sw = Stopwatch.StartNew();\n      Exception? err = null;\n      var origSize = new FileInfo(path).Length;\n      string? lastCompleteStatus = null;''',
'''    var cancellationToken = BeginMaintenanceOperation("Defragment", staged: false);\n    Task.Run(() => {\n      var sw = Stopwatch.StartNew();\n      Exception? err = null;\n      var cancelled = false;\n      var origSize = new FileInfo(path).Length;\n      string? lastCompleteStatus = null;''', 1)
window = window.replace(
'''          OnProgress = OnProgress,\n        });\n      } catch (Exception ex) {\n        err = ex;\n      }''',
'''          OnProgress = OnProgress,\n          CancellationToken = cancellationToken,\n        });\n      } catch (OperationCanceledException) {\n        cancelled = true;\n      } catch (Exception ex) {\n        err = ex;\n      }''', 1)
window = window.replace(
'''        if (err != null) {\n          Append($"FAILED ({sw.ElapsedMilliseconds} ms): {err.GetType().Name}: {err.Message}");\n        } else {\n          Append($"OK ({sw.ElapsedMilliseconds} ms)");''',
'''        if (cancelled) {\n          Append($"CANCELLED ({sw.ElapsedMilliseconds} ms). Native in-place movers may already have completed safe block moves; staged rebuilds leave the original unchanged.");\n        } else if (err != null) {\n          Append($"FAILED ({sw.ElapsedMilliseconds} ms): {err.GetType().Name}: {err.Message}");\n        } else {\n          Append($"OK ({sw.ElapsedMilliseconds} ms)");''', 1)
# End the operation at the end of the first defrag dispatcher completion.
needle = '''        Append("");\n      });\n    });\n  }\n\n  /// <summary>\n  /// Runs the archive optimization path:'''
replacement = '''        Append("");\n        EndMaintenanceOperation();\n      });\n    });\n  }\n\n  /// <summary>\n  /// Runs the archive optimization path:'''
window = window.replace(needle, replacement, 1)

# Archive Optimize: staged and safe to cancel. Poll the actual sibling temp output
# created by AtomicFileWriter so read/write heads move even through codec code that
# does not expose byte callbacks itself.
archive_start = window.find('  private void OnRunArchiveOptimize() {')
archive_end = window.find('  /// <summary>\n  /// CVF-specific optimization path:', archive_start)
if archive_start >= 0 and archive_end > archive_start:
    window = window[:archive_start] + r'''  private void OnRunArchiveOptimize() {
    if (this._isSevenZipFormat && SmartSolidRepackCheck?.IsChecked == true) {
      OnRunSmartSolidRepack();
      return;
    }

    var path = this._imagePath!;
    var ops = this._archiveOps;
    var formatId = this._formatId;

    Append($"=== {DateTime.Now:HH:mm:ss}  Optimizing {Path.GetFileName(path)} ===");

    // CVF keeps its dedicated optimizer; it is already atomic but does not yet
    // expose fine-grained codec progress.
    var isCvfFormat = formatId is "DoubleSpace" or "DriveSpace" or "DriveSpace3";
    if (isCvfFormat) {
      OnRunCvfOptimize(path, formatId!);
      return;
    }

    RunBtn.IsEnabled = false;
    Progress.IsIndeterminate = false;
    Progress.Value = 0;
    var cancellationToken = BeginMaintenanceOperation("Archive optimize/repack", staged: true);

    Task.Run(() => {
      var sw = Stopwatch.StartNew();
      Exception? err = null;
      var cancelled = false;
      var origSize = new FileInfo(path).Length;
      long newSize = 0;
      var entriesOptimized = 0;
      var tempOut = path + ".opt.tmp";

      try {
        var worker = Task.Run(() => Compression.Lib.ArchiveOperations.Optimize(path, tempOut, password: null));
        while (!worker.Wait(100)) {
          var stagedBytes = FindStagedOutputLength(tempOut);
          var fraction = origSize > 0 ? Math.Clamp((double)stagedBytes / origSize, 0, 0.95) : 0;
          Dispatcher.BeginInvoke(() => {
            Progress.Value = fraction * 100;
            BlockMap.ReadHead = origSize > 0 ? Math.Min(origSize - 1, (long)(fraction * origSize)) : -1;
            BlockMap.WriteHead = stagedBytes > 0 ? stagedBytes : -1;
            if (LayoutStatusLbl != null)
              LayoutStatusLbl.Text = cancellationToken.IsCancellationRequested
                ? "Cancellation pending — current codec unit will finish, staged target will then be discarded"
                : $"Rebuilding staged target — {FormatSize(stagedBytes)} written; original unchanged";
          });
        }

        var result = worker.GetAwaiter().GetResult();
        if (cancellationToken.IsCancellationRequested) {
          cancelled = true;
          throw new OperationCanceledException(cancellationToken);
        }
        newSize = result.OptimizedSize;
        entriesOptimized = result.EntriesOptimized;

        Dispatcher.Invoke(() => {
          this._operationCommitStarted = true;
          CancelOperationBtn.IsEnabled = false;
          if (LayoutStatusLbl != null)
            LayoutStatusLbl.Text = "Staged target complete — committing; cancellation no longer safe";
        });
        Compression.Lib.AtomicFileWriter.ReplaceTarget(tempOut, path);
      } catch (OperationCanceledException) {
        cancelled = true;
      } catch (Exception ex) {
        err = ex;
      } finally {
        if (File.Exists(tempOut)) try { File.Delete(tempOut); } catch { }
      }
      sw.Stop();
      if (newSize == 0 && File.Exists(path)) newSize = new FileInfo(path).Length;

      Dispatcher.Invoke(() => {
        Progress.Value = 100;
        RunBtn.IsEnabled = true;
        BlockMap.ReadHead = -1;
        BlockMap.WriteHead = -1;
        if (cancelled) {
          Append($"CANCELLED ({sw.ElapsedMilliseconds} ms) — staged target discarded; existing archive unchanged.");
        } else if (err != null) {
          Append($"FAILED ({sw.ElapsedMilliseconds} ms): {err.GetType().Name}: {err.Message}");
        } else {
          var delta = newSize - origSize;
          var pct = origSize > 0 ? (double)delta / origSize * 100 : 0;
          Append($"OK ({sw.ElapsedMilliseconds} ms) — {entriesOptimized} entries re-encoded");
          Append($"Archive size: {origSize:N0} -> {newSize:N0} bytes (Δ {delta:+#,#;-#,#;0}, {pct:+0.0;-0.0;0.0}%)");
        }
        Append("");
        EndMaintenanceOperation();
        PreviewBlockMap(path, ops, wasMutated: !cancelled && err == null);
      });
    });
  }

  private static long FindStagedOutputLength(string target) {
    try {
      var best = File.Exists(target) ? new FileInfo(target).Length : 0L;
      var dir = Path.GetDirectoryName(target);
      if (string.IsNullOrEmpty(dir)) dir = Directory.GetCurrentDirectory();
      var pattern = Path.GetFileName(target) + ".tmp.*";
      foreach (var candidate in Directory.EnumerateFiles(dir, pattern))
        best = Math.Max(best, new FileInfo(candidate).Length);
      return best;
    } catch {
      return 0;
    }
  }

''' + window[archive_end:]

# Smart 7z regrouping: detailed block-map progress + safe cancellation.
smart_start = window.find('  private void OnRunSmartSolidRepack() {')
smart_end = window.find('  /// <summary>\n  /// Runs the file-internal optimization path:', smart_start)
if smart_start >= 0 and smart_end > smart_start:
    window = window[:smart_start] + r'''  private void OnRunSmartSolidRepack() {
    var path = this._imagePath!;
    var ops = this._archiveOps;

    Append($"=== {DateTime.Now:HH:mm:ss}  Smart solid-block repack: {Path.GetFileName(path)} ===");
    RunBtn.IsEnabled = false;
    Progress.IsIndeterminate = false;
    Progress.Value = 0;
    var cancellationToken = BeginMaintenanceOperation("7z solid-block regroup", staged: true);

    Task.Run(() => {
      var sw = Stopwatch.StartNew();
      Exception? err = null;
      var cancelled = false;
      var origSize = new FileInfo(path).Length;
      FileFormat.SevenZip.SolidBlockOptimizer.OptimizeResult? optimizeResult = null;

      try {
        using var fs = File.OpenRead(path);
        optimizeResult = FileFormat.SevenZip.SolidBlockOptimizer.Optimize(fs, maxTrials: 5,
          onProgress: (index, total, name) => Dispatcher.BeginInvoke(() =>
            Append($"  Trying strategy {index + 1}/{total}: {name}...")),
          onDetailedProgress: detail => Dispatcher.BeginInvoke(() => {
            double fraction = detail.Phase switch {
              "extracting" => 0.35 * detail.BytesDone / Math.Max(1.0, detail.BytesTotal),
              "strategy" => 0.35 + 0.1 * detail.Current / Math.Max(1.0, detail.Total),
              "building" => 0.45 + 0.5 * detail.Current / Math.Max(1.0, detail.Total),
              _ => 0,
            };
            Progress.Value = Math.Clamp(fraction, 0, 0.95) * 100;
            BlockMap.ReadHead = origSize > 0 ? Math.Min(origSize - 1, (long)(Math.Clamp(fraction, 0, 1) * origSize)) : -1;
            BlockMap.WriteHead = detail.Phase == "building" && origSize > 0
              ? Math.Min(origSize - 1, (long)(detail.Current / Math.Max(1.0, detail.Total) * origSize))
              : -1;
            if (LayoutStatusLbl != null)
              LayoutStatusLbl.Text = detail.Phase switch {
                "extracting" => $"Reading source entry: {detail.Name}",
                "strategy" => $"Planning solid grouping: {detail.Name}",
                "building" => $"Rebuilding staged solid blocks: {detail.Name}",
                _ => "Staged 7z regroup",
              };
          }),
          cancellationToken: cancellationToken);
      } catch (OperationCanceledException) {
        cancelled = true;
      } catch (Exception ex) {
        err = ex;
      }
      sw.Stop();

      Dispatcher.Invoke(() => {
        Progress.Value = 100;
        RunBtn.IsEnabled = true;
        BlockMap.ReadHead = -1;
        BlockMap.WriteHead = -1;

        if (cancelled) {
          Append($"CANCELLED ({sw.ElapsedMilliseconds} ms) — regrouped candidate discarded; existing 7z unchanged.");
        } else if (err != null) {
          Append($"FAILED ({sw.ElapsedMilliseconds} ms): {err.GetType().Name}: {err.Message}");
        } else if (optimizeResult != null) {
          foreach (var trial in optimizeResult.Trials)
            Append($"    {trial.StrategyName}: {FormatSize(trial.OutputSize)} ({trial.Elapsed.TotalMilliseconds:F0} ms)");

          var newSize = (long)optimizeResult.Data.Length;
          var delta = newSize - origSize;
          var pct = origSize > 0 ? (double)delta / origSize * 100 : 0;
          Append($"  Winner: {optimizeResult.WinningStrategy}");
          Append($"OK ({sw.ElapsedMilliseconds} ms)");
          Append($"Archive size: {origSize:N0} -> {newSize:N0} bytes ({delta:+#,#;-#,#;0}, {pct:+0.0;-0.0;0.0}%)");

          if (newSize < origSize) {
            this._operationCommitStarted = true;
            CancelOperationBtn.IsEnabled = false;
            if (LayoutStatusLbl != null)
              LayoutStatusLbl.Text = "Winning staged layout verified — committing";
            try {
              Compression.Lib.AtomicFileWriter.WriteAllBytesAtomic(path, optimizeResult.Data);
              Append("Optimized archive written.");
            } catch (Exception writeEx) {
              Append($"WARNING: Could not write optimized archive: {writeEx.Message}");
            }
          } else {
            Append("No strategy improved on the original size; archive unchanged.");
          }
        }
        Append("");
        EndMaintenanceOperation();
        PreviewBlockMap(path, ops, wasMutated: !cancelled && err == null);
      });
    });
  }

''' + window[smart_end:]

# General operation lifecycle + cancellation warnings. Staged rebuild cancellation
# is safe; native in-place operations warn because completed moves cannot be rolled back.
old_close = '  private void OnClose(object sender, RoutedEventArgs e) => Close();'
helpers = r'''  private CancellationToken BeginMaintenanceOperation(string name, bool staged) {
    this._operationCts?.Dispose();
    this._operationCts = new CancellationTokenSource();
    this._operationName = name;
    this._operationIsStaged = staged;
    this._operationCommitStarted = false;
    if (CancelOperationBtn != null) CancelOperationBtn.IsEnabled = true;
    if (BrowseBtn != null) BrowseBtn.IsEnabled = false;
    if (CloseBtn != null) CloseBtn.IsEnabled = true;
    if (LayoutStatusLbl != null && staged)
      LayoutStatusLbl.Text = $"{name}: staged target active — original unchanged until commit";
    return this._operationCts.Token;
  }

  private void EndMaintenanceOperation() {
    this._operationCts?.Dispose();
    this._operationCts = null;
    this._operationName = null;
    this._operationIsStaged = false;
    this._operationCommitStarted = false;
    if (CancelOperationBtn != null) CancelOperationBtn.IsEnabled = false;
    if (BrowseBtn != null) BrowseBtn.IsEnabled = true;
  }

  private void OnCancelOperation(object sender, RoutedEventArgs e) {
    var cts = this._operationCts;
    if (cts == null || cts.IsCancellationRequested) return;
    if (this._operationCommitStarted) {
      Append("Cancellation ignored: verified target commit has already started; finishing the commit is safer than interrupting it.");
      CancelOperationBtn.IsEnabled = false;
      return;
    }

    if (!this._operationIsStaged) {
      var result = MessageBox.Show(this,
        $"Cancel {this._operationName ?? "maintenance"}?\n\n"
        + "This is a native/in-place operation. Blocks already moved stay moved; cancellation is best-effort at the next safe boundary. The image should remain valid, but its layout may be partially changed.",
        "Cancel in-place maintenance", MessageBoxButton.YesNo, MessageBoxImage.Warning);
      if (result != MessageBoxResult.Yes) return;
      Append("Cancellation requested — in-place moves already completed will not be rolled back.");
    } else {
      Append("Cancellation requested — staged target will be discarded; existing archive remains unchanged.");
    }
    CancelOperationBtn.IsEnabled = false;
    cts.Cancel();
  }

  private void OnClose(object sender, RoutedEventArgs e) {
    if (this._operationCts != null) {
      OnCancelOperation(sender, e);
      if (this._operationCts != null) return;
    }
    Close();
  }'''
if old_close in window:
    window = window.replace(old_close, helpers, 1)
write('Compression.UI/Views/DefragmentWindow.xaml.cs', window)


# ── Contract tests: cancellation before commit must preserve the source ───────
write('Compression.Tests/Operations/RebuildProgressCancellationTests.cs', r'''using Compression.Registry;

namespace Compression.Tests.Operations;

[TestFixture]
public sealed class RebuildProgressCancellationTests {
  [Test]
  public void RebuildInPlace_CancelDuringTargetWrite_LeavesOriginalUntouched() {
    var original = Enumerable.Range(0, 4096).Select(i => (byte)(i * 31)).ToArray();
    using var archive = new MemoryStream();
    archive.Write(original);
    archive.Position = 0;

    var descriptor = new FakeDescriptor();
    using var cts = new CancellationTokenSource();
    var phases = new List<string>();

    Assert.Throws<OperationCanceledException>(() =>
      RebuildVerb.RebuildInPlace(archive, descriptor, descriptor,
        onProgress: e => {
          phases.Add(e.Phase);
          if (e.Phase == "writing" && e.CurrentWriteOffset > 0)
            cts.Cancel();
        }, cancellationToken: cts.Token));

    CollectionAssert.Contains(phases, "scanning");
    CollectionAssert.Contains(phases, "reading");
    CollectionAssert.Contains(phases, "writing");
    Assert.That(phases, Does.Not.Contain("committing"));
    CollectionAssert.AreEqual(original, archive.ToArray(),
      "A cancelled staged rebuild must never overwrite the source stream.");
  }

  [Test]
  public void RebuildInPlace_ReportsColoredTargetAndCommitPhases() {
    using var archive = new MemoryStream(new byte[4096], writable: true);
    var descriptor = new FakeDescriptor();
    var events = new List<DefragProgressEvent>();

    RebuildVerb.RebuildInPlace(archive, descriptor, descriptor, onProgress: events.Add);

    Assert.That(events.Select(e => e.Phase), Does.Contain("writing"));
    Assert.That(events.Select(e => e.Phase), Does.Contain("verifying"));
    Assert.That(events.Select(e => e.Phase), Does.Contain("committing"));
    Assert.That(events.Select(e => e.Phase), Does.Contain("complete"));
    var map = events.First(e => e.Phase == "writing").BlockMap;
    Assert.That(map, Is.Not.Null.And.Not.Empty);
    Assert.That(map!.Any(b => b.Kind == DefragBlockKind.Used && b.Classification.HasValue), Is.True);
  }

  private sealed class FakeDescriptor : IArchiveFormatOperations, IArchiveCreatable {
    private static readonly byte[] Payload = Enumerable.Range(0, 256 * 1024)
      .Select(i => (byte)(i * 17)).ToArray();

    public List<ArchiveEntryInfo> List(Stream stream, string? password)
      => [new ArchiveEntryInfo(0, "payload.bin", Payload.Length, Payload.Length,
        "deflate", false, false, null)];

    public void Extract(Stream stream, string outputDir, string? password, string[]? files)
      => File.WriteAllBytes(Path.Combine(outputDir, "payload.bin"), Payload);

    public Stream OpenEntry(Stream archive, string entryName, string? password)
      => new MemoryStream(Payload, writable: false);

    public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
      var data = inputs.Single(i => !i.IsDirectory).ReadContent();
      const int Chunk = 16 * 1024;
      for (var offset = 0; offset < data.Length; offset += Chunk)
        output.Write(data, offset, Math.Min(Chunk, data.Length - offset));
    }
  }
}
''')

# Remove this one-shot UX transformer and workflow from the product commit.
Path('.github/maintenance-ux.py').unlink(missing_ok=True)
Path('.github/workflows/maintenance-capabilities-once.yml').unlink(missing_ok=True)
