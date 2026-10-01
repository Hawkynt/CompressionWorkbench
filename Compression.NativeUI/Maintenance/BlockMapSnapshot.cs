using Compression.Registry;

namespace Compression.NativeUI.Maintenance;

/// <summary>
/// One row of the files panel: name, size, fragment count, method, modification date and thermal
/// class. The fragment count comes from whatever layout the format offered; formats that offer none
/// show an em dash rather than a figure nothing measured.
/// </summary>
internal sealed class FileRow {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public string SizeDisplay { get; init; } = "";
  public string FragmentsDisplay { get; init; } = "";
  public string MethodDisplay { get; init; } = "";
  public DateTime? Modified { get; init; }
  public string ModifiedDisplay { get; init; } = "";
  public string Class { get; init; } = "";
}

/// <summary>
/// What the block view shows of an image at rest: its extents, its size, the files panel rows and a
/// line saying how honest the picture is.
/// </summary>
internal sealed record BlockMapSnapshot(IReadOnlyList<DefragBlockInfo>? Map, long ImageSize, IReadOnlyList<FileRow> Rows, string Status) {
  /// <summary>Cap on extents fed to the block map; adjacent same-kind regions merge beyond it.</summary>
  internal const int MaxExtents = 50_000;

  public static BlockMapSnapshot Empty(string status) => new(null, 0, [], status);

  /// <summary>
  /// Reads the layout of the image at <paramref name="path"/>. A descriptor that exposes a real
  /// extent, archive or chunk layout gets the honest picture; anything else falls back to sizes laid
  /// end to end from zero, which the status line labels as approximate.
  /// </summary>
  public static BlockMapSnapshot Read(string path, IArchiveFormatOperations? ops) {
    if (ops is null) return Empty("No layout: the format is not recognised.");

    if (ops is IFilesystemExtentMap extentMap && Try(() => FromExtentMap(path, extentMap, ops)) is { } extents) return extents;
    if (ops is IArchiveLayoutMap archiveLayout && Try(() => FromArchiveLayout(path, archiveLayout, ops)) is { } archive) return archive;
    if (ops is IFileInternalLayoutMap fileLayout && Try(() => FromFileInternalLayout(path, fileLayout)) is { } chunks) return chunks;
    return Try(() => Approximate(path, ops)) ?? Empty("No layout could be read.");

    // A walker failing mid-stream falls through to the next, so the user still sees something.
    static BlockMapSnapshot? Try(Func<BlockMapSnapshot?> read) {
      try {
        return read();
      } catch {
        return null;
      }
    }
  }

  private static BlockMapSnapshot? FromExtentMap(string path, IFilesystemExtentMap extentMap, IArchiveFormatOperations ops) {
    using var stream = File.OpenRead(path);
    var imageSize = stream.Length;
    if (imageSize <= 0) return null;

    // Runs are counted in the order the walker yields — the file's own order — before the extents
    // are sorted by offset for drawing: sorted, a file stored backwards would look contiguous.
    List<DefragBlockInfo> raw = [.. extentMap.EnumerateExtents(stream)];
    var runs = OwnerLookup.From(CountRunsByOwner(raw).Select(r => (r.Key, r.Value)));
    var filled = NormalizeExtents(raw, imageSize);
    if (filled is null) return null;

    OwnerLookup<DateTime?>? mtimeByName = null;
    List<ArchiveEntryInfo>? entries = null;
    try {
      stream.Position = 0;
      entries = ops.List(stream, password: null);
      mtimeByName = OwnerLookup.From(entries.Where(e => !e.IsDirectory).Select(e => (e.Name, e.LastModified)));
    } catch {
      // Classification is best-effort; the layout itself is what matters here.
    }

    var thresholds = mtimeByName is null ? null : ComputeMtimeQuartiles(mtimeByName.Values);
    var classified = new List<DefragBlockInfo>(filled.Count);
    foreach (var ex in filled)
      classified.Add(ex.Kind == DefragBlockKind.Used && ex.FileName is { } owner && mtimeByName is not null && mtimeByName.TryGetValue(owner, out var mtime)
        ? ex with { Classification = thresholds is null ? DefragBlockClass.Normal : Classify(mtime, thresholds, 0, 1) }
        : ex);

    IReadOnlyList<FileRow> rows = entries is null ? [] : [.. entries.Where(e => !e.IsDirectory).Select(e => new FileRow {
      Name = e.Name,
      Size = e.OriginalSize,
      SizeDisplay = MaintenanceInput.FormatSize(e.OriginalSize),
      FragmentsDisplay = runs.TryGetValue(e.Name, out var count) && count > 0 ? count.ToString("N0") : "—",
      Modified = e.LastModified,
      ModifiedDisplay = e.LastModified is { } dt ? dt.ToString("yyyy-MM-dd HH:mm") : "—",
      Class = "—",
    })];

    var usedExtents = classified.Count(ex => ex.Kind == DefragBlockKind.Used);
    var fragmentedFiles = entries is null ? 0 : runs.Values.Count(r => r > 1);
    var status = fragmentedFiles > 0
      ? $"Real on-disk layout — {usedExtents:N0} extents, {fragmentedFiles:N0} fragmented file(s)"
      : $"Real on-disk layout — {usedExtents:N0} extents (no fragmentation detected)";
    return new(Bin(classified), imageSize, rows, status);
  }

  private static BlockMapSnapshot? FromArchiveLayout(string path, IArchiveLayoutMap archiveLayout, IArchiveFormatOperations ops) {
    using var stream = File.OpenRead(path);
    var imageSize = stream.Length;
    if (imageSize <= 0) return null;

    List<DefragBlockInfo> raw = [.. archiveLayout.EnumerateLayout(stream)];
    var runs = OwnerLookup.From(CountRunsByOwner(raw).Select(r => (r.Key, r.Value)));
    var filled = NormalizeExtents(raw, imageSize);
    if (filled is null) return null;

    OwnerLookup<string>? methodByName = null;
    List<ArchiveEntryInfo>? entries = null;
    try {
      stream.Position = 0;
      entries = ops.List(stream, password: null);
      methodByName = OwnerLookup.From(entries.Where(e => !e.IsDirectory).Select(e => (e.Name, e.Method ?? "")));
    } catch {
      // Classification is best-effort.
    }

    var classified = new List<DefragBlockInfo>(filled.Count);
    foreach (var ex in filled)
      classified.Add(ex.Kind == DefragBlockKind.Used && ex.FileName is { } owner && methodByName is not null && methodByName.TryGetValue(owner, out var method)
        ? ex with { Classification = ClassifyByMethod(method) }
        : ex);

    // An entry the map does not name is not one run — it is unmeasured.
    IReadOnlyList<FileRow> rows = entries is null ? [] : [.. entries.Where(e => !e.IsDirectory).Select(e => new FileRow {
      Name = e.Name,
      Size = e.OriginalSize,
      SizeDisplay = MaintenanceInput.FormatSize(e.OriginalSize),
      FragmentsDisplay = runs.TryGetValue(e.Name, out var count) ? count.ToString("N0") : "—",
      MethodDisplay = e.Method ?? "",
      Modified = e.LastModified,
      ModifiedDisplay = e.LastModified is { } dt ? dt.ToString("yyyy-MM-dd HH:mm") : "",
      Class = ClassifyByMethod(e.Method ?? "").ToString(),
    })];

    var regionCount = classified.Count(ex => ex.Kind == DefragBlockKind.Used);
    var freeBytes = classified.Where(ex => ex.Kind == DefragBlockKind.Free).Sum(ex => ex.Length);
    var status = freeBytes > 0
      ? $"Real archive layout — {regionCount:N0} regions, {MaintenanceInput.FormatSize(freeBytes)} wasted"
      : $"Real archive layout — {regionCount:N0} regions (tightly packed)";
    return new(Bin(classified), imageSize, rows, status);
  }

  private static BlockMapSnapshot? FromFileInternalLayout(string path, IFileInternalLayoutMap fileLayout) {
    using var stream = File.OpenRead(path);
    var imageSize = stream.Length;
    if (imageSize <= 0) return null;

    var filled = NormalizeExtents([.. fileLayout.EnumerateChunks(stream)], imageSize);
    if (filled is null) return null;

    // A row here is one stretch of the file, so the count belongs to the chunk's owner and comes from
    // the same layout the map was drawn from.
    var runs = CountRunsByOwner(filled, onlyKind: null);
    IReadOnlyList<FileRow> rows = [.. filled.Select(ch => new FileRow {
      Name = ch.FileName ?? ch.Kind.ToString(),
      Size = ch.Length,
      SizeDisplay = MaintenanceInput.FormatSize(ch.Length),
      FragmentsDisplay = ch.FileName is { } owner && runs.TryGetValue(owner, out var count) ? count.ToString("N0") : "—",
      MethodDisplay = ch.Kind.ToString(),
      Class = ch.Classification?.ToString() ?? "—",
    })];

    return new(filled, imageSize, rows, $"File-internal layout — {filled.Count(c => c.Kind != DefragBlockKind.Free):N0} chunks");
  }

  private static BlockMapSnapshot Approximate(string path, IArchiveFormatOperations ops) {
    using var stream = File.OpenRead(path);
    var entries = ops.List(stream, password: null);
    var imageSize = stream.Length;
    var map = new List<DefragBlockInfo>();
    var rows = new List<FileRow>();
    var offset = 0L;

    var files = entries.Where(e => !e.IsDirectory).ToList();
    var thresholds = ComputeMtimeQuartiles(files.Select(f => f.LastModified));
    for (var k = 0; k < files.Count; ++k) {
      var e = files[k];
      var size = Math.Max(1, e.OriginalSize);
      var cls = Classify(e.LastModified, thresholds, k, files.Count);
      map.Add(new(offset, size, DefragBlockKind.Used, e.Name, cls));
      rows.Add(new() {
        Name = e.Name,
        Size = e.OriginalSize,
        SizeDisplay = MaintenanceInput.FormatSize(e.OriginalSize),
        // The format reported no layout, so these tiles are sizes laid end to end rather than where
        // anything actually is. Counting runs off that would report one for everything.
        FragmentsDisplay = "—",
        Modified = e.LastModified,
        ModifiedDisplay = e.LastModified is { } dt ? dt.ToString("yyyy-MM-dd HH:mm") : "—",
        Class = cls.ToString(),
      });

      offset += size;
      if (offset >= imageSize) break;
    }

    if (offset < imageSize) map.Add(new(offset, imageSize - offset, DefragBlockKind.Free));
    return new(map, imageSize, rows, "Approximate layout: the format reports no positions, so files are drawn end to end");
  }

  /// <summary>
  /// Projects the archive's entries onto the image's byte span, weighted by size, so a staged rebuild
  /// has something to animate against before any output exists.
  /// </summary>
  public static IReadOnlyList<DefragBlockInfo>? StagedProjection(string path, IArchiveFormatOperations ops, long displaySize) {
    try {
      using var stream = File.OpenRead(path);
      var entries = ops.List(stream, null).Where(e => !e.IsDirectory).ToArray();
      if (entries.Length == 0) return null;

      var weights = entries.Select(e => Math.Max(1L, e.OriginalSize)).ToArray();
      var totalWeight = Math.Max(1L, weights.Sum());
      var size = Math.Max(1L, displaySize);
      var map = new List<DefragBlockInfo>(entries.Length);
      long cumulative = 0;
      long offset = 0;
      for (var i = 0; i < entries.Length; ++i) {
        cumulative += weights[i];
        var end = i == entries.Length - 1 ? size : (long)((double)cumulative / totalWeight * size);
        end = Math.Clamp(end, offset, size);
        if (end > offset) map.Add(new(offset, end - offset, DefragBlockKind.Used, entries[i].Name, ClassifyByMethod(entries[i].Method ?? "")));
        offset = end;
      }

      return map;
    } catch {
      // The existing picture stays usable when a projection cannot be built.
      return null;
    }
  }

  // ── helpers ─────────────────────────────────────────────────────────────────────────────────

  private static IReadOnlyList<DefragBlockInfo> Bin(List<DefragBlockInfo> extents)
    => extents.Count > MaxExtents ? BinExtents(extents, MaxExtents) : extents;

  /// <summary>
  /// Splits the modification times into four equal-count buckets. The most recent quarter is hot,
  /// the oldest is frozen.
  /// </summary>
  internal static (DateTime? Cold, DateTime? Normal, DateTime? Hot)? ComputeMtimeQuartiles(IEnumerable<DateTime?> times) {
    var sorted = times.Where(t => t.HasValue).Select(t => t!.Value).Order().ToList();
    if (sorted.Count == 0) return null;

    var n = sorted.Count;
    return (sorted[Math.Min(n - 1, n / 4)], sorted[Math.Min(n - 1, n / 2)], sorted[Math.Min(n - 1, 3 * n / 4)]);
  }

  internal static DefragBlockClass Classify(DateTime? modified, (DateTime? Cold, DateTime? Normal, DateTime? Hot)? thresholds, int index, int count) {
    if (thresholds is not { } t)
      // No dates anywhere — fall back to listing-order quartile so the chart still has structure.
      return (index * 4 / Math.Max(1, count)) switch {
        0 => DefragBlockClass.Hot,
        1 => DefragBlockClass.Normal,
        2 => DefragBlockClass.Cold,
        _ => DefragBlockClass.Frozen,
      };

    // An undated entry in a dated set stands out as frozen rather than being guessed at.
    if (modified is not { } when) return DefragBlockClass.Frozen;
    if (when < t.Cold) return DefragBlockClass.Frozen;
    if (when < t.Normal) return DefragBlockClass.Cold;
    return when < t.Hot ? DefragBlockClass.Normal : DefragBlockClass.Hot;
  }

  /// <summary>Maps a compression method name to a thermal class for colour-coding archive entries.</summary>
  internal static DefragBlockClass ClassifyByMethod(string method) {
    var m = method.ToUpperInvariant();
    if (m.Contains("STORE") || m.Contains("COPY") || m is "NONE" or "") return DefragBlockClass.Frozen;
    if (m.Contains("LZMA") || m.Contains("PPMD") || m.Contains("BZIP")) return DefragBlockClass.Hot;
    if (m.Contains("ZSTD")) return DefragBlockClass.Cold;
    return DefragBlockClass.Normal;
  }

  /// <summary>
  /// Sorts extents by offset, clips them to the image, fills the gaps as free space and trims
  /// overlaps so a later extent at the same offset wins. Returns null when nothing usable remains.
  /// </summary>
  internal static List<DefragBlockInfo>? NormalizeExtents(List<DefragBlockInfo> raw, long imageSize) {
    if (raw.Count == 0) return null;
    raw.Sort(static (a, b) => a.Offset.CompareTo(b.Offset));

    var clipped = new List<DefragBlockInfo>(raw.Count);
    foreach (var ex in raw) {
      if (ex.Offset >= imageSize) continue;
      var length = Math.Min(ex.Length, imageSize - ex.Offset);
      if (length <= 0) continue;
      clipped.Add(ex with { Length = length });
    }
    if (clipped.Count == 0) return null;

    var filled = new List<DefragBlockInfo>(clipped.Count * 2);
    var cursor = 0L;
    foreach (var ex in clipped) {
      if (ex.Offset > cursor)
        filled.Add(new(cursor, ex.Offset - cursor, DefragBlockKind.Free));
      else if (ex.Offset < cursor) {
        if (ex.Offset + ex.Length <= cursor) continue;
        var trimmed = ex with { Length = ex.Length - (cursor - ex.Offset), Offset = cursor };
        filled.Add(trimmed);
        cursor = trimmed.Offset + trimmed.Length;
        continue;
      }

      filled.Add(ex);
      cursor = ex.Offset + ex.Length;
    }

    if (cursor < imageSize) filled.Add(new(cursor, imageSize - cursor, DefragBlockKind.Free));
    return filled;
  }

  /// <summary>
  /// Bins an oversized extent list down to <paramref name="maxCount"/> by merging adjacent same-kind
  /// extents, then — if still over budget — by equal-sized byte bins taking each bin's dominant kind.
  /// </summary>
  internal static List<DefragBlockInfo> BinExtents(List<DefragBlockInfo> input, int maxCount) {
    if (input.Count <= maxCount) return input;

    var merged = new List<DefragBlockInfo>(input.Count);
    foreach (var ex in input) {
      if (merged.Count > 0 && merged[^1] is var last && last.Kind == ex.Kind && last.Offset + last.Length == ex.Offset && last.FileName == ex.FileName)
        merged[^1] = last with { Length = last.Length + ex.Length };
      else
        merged.Add(ex);
    }
    if (merged.Count <= maxCount) return merged;

    var imageSize = merged[^1].Offset + merged[^1].Length;
    var bytesPerBin = (double)imageSize / maxCount;
    var binned = new List<DefragBlockInfo>(maxCount);
    var binStart = 0L;
    var binIndex = 0;
    var binKind = DefragBlockKind.Free;
    var binBestLength = 0L;
    string? binBestName = null;

    foreach (var ex in merged) {
      while (ex.Offset + ex.Length > binStart + bytesPerBin && binIndex < maxCount - 1) {
        if (ex.Length > binBestLength) {
          binBestLength = ex.Length;
          binKind = ex.Kind;
          binBestName = ex.FileName;
        }

        binned.Add(new(binStart, (long)bytesPerBin, binKind, binBestName));
        binStart += (long)bytesPerBin;
        ++binIndex;
        binKind = DefragBlockKind.Free;
        binBestLength = 0;
        binBestName = null;
      }

      if (ex.Length > binBestLength) {
        binBestLength = ex.Length;
        binKind = ex.Kind;
        binBestName = ex.FileName;
      }
    }

    binned.Add(new(binStart, imageSize - binStart, binKind, binBestName));
    return binned;
  }

  /// <summary>
  /// How many separate stretches of the container each owner's data occupies. Counting the entries a
  /// map yields would measure the map rather than the layout: some merge an owner's consecutive
  /// blocks and some emit one per block. Stretches that end exactly where the next begins are one run.
  /// </summary>
  internal static Dictionary<string, int> CountRunsByOwner(IEnumerable<DefragBlockInfo> extents, DefragBlockKind? onlyKind = DefragBlockKind.Used) {
    var runs = new Dictionary<string, int>(StringComparer.Ordinal);
    var endOf = new Dictionary<string, long>(StringComparer.Ordinal);

    foreach (var extent in extents) {
      if (onlyKind is { } wanted && extent.Kind != wanted) continue;
      if (extent.FileName is not { } owner) continue;

      if (!endOf.TryGetValue(owner, out var previousEnd) || previousEnd != extent.Offset) {
        runs.TryGetValue(owner, out var count);
        runs[owner] = count + 1;
      }

      endOf[owner] = extent.Offset + extent.Length;
    }

    return runs;
  }
}

/// <summary>
/// Finds what a layout map says about a file by the name the listing gives it. A format's layout
/// walker and its lister do not always spell a name alike — FAT's walker keeps the stored 8.3 name
/// in capitals while the lister applies the lowercase flags, so <c>BIG.BIN</c> in the map is
/// <c>big.bin</c> in the list — so a name that does not match exactly is matched ignoring case and
/// separators, but only when that match is unambiguous.
/// </summary>
internal sealed class OwnerLookup<T> {
  private readonly Dictionary<string, T> _exact = new(StringComparer.Ordinal);
  private readonly Dictionary<string, (T Value, bool Ambiguous)> _folded = new(StringComparer.OrdinalIgnoreCase);

  internal OwnerLookup(IEnumerable<(string Name, T Value)> pairs) {
    foreach (var (name, value) in pairs) {
      var key = Normalize(name);
      if (!this._exact.TryAdd(key, value)) continue;
      this._folded[key] = this._folded.TryGetValue(key, out var seen) ? (seen.Value, true) : (value, false);
    }
  }

  public IEnumerable<T> Values => this._exact.Values;

  public bool TryGetValue(string name, out T value) {
    var key = Normalize(name);
    if (this._exact.TryGetValue(key, out value!)) return true;
    if (this._folded.TryGetValue(key, out var folded) && !folded.Ambiguous) {
      value = folded.Value;
      return true;
    }

    value = default!;
    return false;
  }

  private static string Normalize(string name) => name.Replace('\\', '/').TrimStart('/');
}

internal static class OwnerLookup {
  public static OwnerLookup<T> From<T>(IEnumerable<(string Name, T Value)> pairs) => new(pairs);
}
