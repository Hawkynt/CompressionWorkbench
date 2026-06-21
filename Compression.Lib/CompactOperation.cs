using Compression.Registry;
using F = Compression.Lib.FormatDetector.Format;

namespace Compression.Lib;

/// <summary>
/// The composite <c>compact</c> maintenance verb: <em>defrag → optimize →
/// shrink</em>, run as one pass to produce the smallest valid container that
/// still holds the same contents.
/// <list type="bullet">
///   <item><b>defrag</b> consolidates live data so it is contiguous;</item>
///   <item><b>optimize</b> re-encodes the payload with the best methods (where
///   the format is re-encodable);</item>
///   <item><b>shrink</b> truncates the freed tail and steps the container down
///   to the smallest canonical size that still fits.</item>
/// </list>
/// <para>With <see cref="CompactOptions.Minimal"/> the standard trio is replaced
/// by a single <em>minimal-geometry rebuild</em>: the contents are extracted and
/// the container re-created at the smallest geometry the format allows (auto-fit
/// image size, smallest cluster, minimal root-directory entries). For a FAT
/// floppy that turns a fixed 1.44&#160;MB image into a few-KB image whose
/// root-directory and FAT are sized to exactly hold the data — smaller, but no
/// longer a standard mountable floppy. Formats without geometry knobs fall back
/// to the standard compact and say so via <see cref="CompactOptions.Log"/>.</para>
/// </summary>
public static class CompactOperation {

  /// <summary>Outcome of a compact pass.</summary>
  /// <param name="OriginalSize">Container size before compacting, in bytes.</param>
  /// <param name="NewSize">Container size after compacting, in bytes.</param>
  /// <param name="StepsRun">Human-readable list of the steps that actually ran.</param>
  /// <param name="Minimal">Whether the minimal-geometry rebuild was used.</param>
  public sealed record CompactResult(long OriginalSize, long NewSize, IReadOnlyList<string> StepsRun, bool Minimal);

  /// <summary>Tunables for <see cref="Compact"/>.</summary>
  public sealed class CompactOptions {
    /// <summary>
    /// When true, rebuild the container at the smallest geometry the format
    /// allows instead of the conservative defrag+optimize+shrink trio. The
    /// result is the smallest possible file but may no longer be a
    /// standard/mountable image of that type.
    /// </summary>
    public bool Minimal { get; init; }
    /// <summary>Password for encrypted source containers (read side).</summary>
    public string? Password { get; init; }
    /// <summary>Optional progress/diagnostic sink — one line per step.</summary>
    public Action<string>? Log { get; init; }
  }

  // Schema keys understood by the minimal-geometry rebuild, grouped by intent.
  private static readonly string[] SizeKeys = ["ImageSize", "TotalSize", "VolumeSize"];
  private static readonly string[] UnitKeys = ["ClusterSize", "BlockSize", "UnitSize", "AllocationUnit", "AllocSize"];
  private static readonly string[] CountKeys = ["RootEntries", "InodeCount", "InodeSize"];

  /// <summary>
  /// Compacts the container at <paramref name="path"/> in place. Live contents
  /// are preserved byte-for-byte; only layout, encoding and (in
  /// <see cref="CompactOptions.Minimal"/> mode) geometry change.
  /// </summary>
  public static CompactResult Compact(string path, CompactOptions? options = null) {
    ArgumentException.ThrowIfNullOrEmpty(path);
    if (!File.Exists(path)) throw new FileNotFoundException("Container not found.", path);
    options ??= new CompactOptions();
    var log = options.Log ?? (_ => { });

    FormatRegistration.EnsureInitialized();
    var originalSize = new FileInfo(path).Length;
    var format = FormatDetector.Detect(path);
    var formatId = format.ToString();
    var ops = FormatRegistry.GetArchiveOps(formatId);
    var steps = new List<string>();

    // ── Minimal: a single minimal-geometry rebuild replaces the whole trio ──
    if (options.Minimal) {
      if (ops is IArchiveCreatable && ops is IFormatOptionsSchema schema
          && SelectMinimalGeometry(schema) is { Count: > 0 } minimal) {
        TryMinimalRebuild(path, format, minimal, options.Password, log);
        steps.Add("minimal-geometry rebuild");
        return new CompactResult(originalSize, new FileInfo(path).Length, steps, Minimal: true);
      }
      log($"compact: '{formatId}' exposes no minimal-geometry knobs — running standard compact instead.");
    }

    // ── 1) Defragment — consolidate live data at the start ──────────────────
    if (ops is IArchiveDefragmentable defragmentable) {
      try {
        using var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite);
        defragmentable.Defragment(stream, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });
        steps.Add("defragment");
        log("defragment: consolidated live data at the start.");
      } catch (Exception ex) {
        log($"defragment: skipped ({ex.GetType().Name}: {ex.Message}).");
      }
    }

    // ── 2) Optimize — re-encode the payload where the format is re-encodable ─
    if (formatId is "DoubleSpace" or "DriveSpace" or "DriveSpace3") {
      var descriptor = FormatRegistry.GetById(formatId);
      if (descriptor != null) {
        try {
          var r = CvfOptimizer.Optimize(path, descriptor);
          steps.Add("optimize");
          log($"optimize: re-encoded via {r.MethodUsed}.");
        } catch (Exception ex) {
          log($"optimize: skipped ({ex.GetType().Name}: {ex.Message}).");
        }
      }
    } else if (format == F.Zip || FormatDetector.IsStreamFormat(format)
               || FormatDetector.GetTarCompression(format).HasValue) {
      var tempOut = path + ".compact-opt.tmp";
      try {
        var r = ArchiveOperations.Optimize(path, tempOut, options.Password);
        File.Move(tempOut, path, overwrite: true);
        steps.Add("optimize");
        log($"optimize: re-encoded {r.EntriesOptimized} entr(ies).");
      } catch (Exception ex) {
        log($"optimize: skipped ({ex.GetType().Name}: {ex.Message}).");
      } finally {
        if (File.Exists(tempOut)) try { File.Delete(tempOut); } catch { /* best effort */ }
      }
    }

    // ── 3) Shrink — truncate freed tail / step down to the smallest size ────
    if (ops is IArchiveShrinkable shrinkable) {
      var tempOut = path + ".compact-shrink.tmp";
      try {
        long shrunkLen;
        using (var input = File.OpenRead(path))
        using (var output = File.Create(tempOut))
          shrinkable.Shrink(input, output);
        shrunkLen = new FileInfo(tempOut).Length;
        if (shrunkLen > 0 && shrunkLen < new FileInfo(path).Length) {
          File.Move(tempOut, path, overwrite: true);
          steps.Add("shrink");
          log($"shrink: reduced to {shrunkLen:N0} bytes.");
        } else {
          log("shrink: already compact (no reduction).");
        }
      } catch (Exception ex) {
        log($"shrink: skipped ({ex.GetType().Name}: {ex.Message}).");
      } finally {
        if (File.Exists(tempOut)) try { File.Delete(tempOut); } catch { /* best effort */ }
      }
    }

    return new CompactResult(originalSize, new FileInfo(path).Length, steps, Minimal: false);
  }

  /// <summary>
  /// Extracts every entry, then re-creates the container at minimal geometry
  /// using the format's creation path. The swap only happens when the rebuilt
  /// image both round-trips (lists at least as many entries as the source) and
  /// is no larger than the original — otherwise the source is left untouched.
  /// </summary>
  private static void TryMinimalRebuild(string path, F format,
      IReadOnlyDictionary<string, string> minimalGeometry, string? password, Action<string> log) {
    var sourceEntryCount = SafeFileCount(path, password);
    var tempDir = Path.Combine(Path.GetTempPath(), "cwb_compact_" + Guid.NewGuid().ToString("N")[..8]);
    // Keep the original extension so the rebuilt image still content/extension-
    // detects as the same format when we re-list it for the safety check
    // (weak-magic formats like FAT lean on the extension).
    var tempOut = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!,
      Path.GetFileNameWithoutExtension(path) + ".compact-min" + Path.GetExtension(path));
    try {
      Directory.CreateDirectory(tempDir);
      ArchiveOperations.Extract(path, tempDir, password, files: null);
      var inputs = ArchiveOperations.EnumerateTempInputs(tempDir);

      ArchiveOperations.Create(tempOut, inputs,
        new CompressionOptions { Password = password },
        format, minimalGeometry);

      var rebuiltCount = SafeFileCount(tempOut, password);
      var rebuiltLen = new FileInfo(tempOut).Length;
      var originalLen = new FileInfo(path).Length;

      if (rebuiltCount < sourceEntryCount) {
        log($"minimal rebuild: aborted — rebuilt image lists {rebuiltCount} file(s) vs {sourceEntryCount}; keeping original.");
        return;
      }
      if (rebuiltLen >= originalLen) {
        log($"minimal rebuild: produced no reduction ({rebuiltLen:N0} ≥ {originalLen:N0} bytes); keeping original.");
        return;
      }
      File.Move(tempOut, path, overwrite: true);
      log($"minimal rebuild: re-created at minimal geometry — {originalLen:N0} → {rebuiltLen:N0} bytes "
          + $"({string.Join(", ", minimalGeometry.Select(kv => $"{kv.Key}={kv.Value}"))}).");
    } finally {
      if (Directory.Exists(tempDir)) try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
      if (File.Exists(tempOut)) try { File.Delete(tempOut); } catch { /* best effort */ }
    }
  }

  private static int SafeFileCount(string path, string? password) {
    try {
      return ArchiveOperations.List(path, password).Count(e => !e.IsDirectory);
    } catch {
      return 0;
    }
  }

  /// <summary>
  /// Picks the smallest-footprint value for each geometry knob the schema
  /// exposes: auto-fit for the image size, the smallest concrete allocation
  /// unit, and the smallest root/inode count. Only keys we understand are set;
  /// everything else stays at its writer default.
  /// </summary>
  private static Dictionary<string, string> SelectMinimalGeometry(IFormatOptionsSchema schema) {
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    var hasRealKnob = false;
    foreach (var opt in schema.OptionsSchema) {
      if (opt.AllowedValues is not { Count: > 0 } allowed) continue;

      if (MatchesAny(opt.Key, SizeKeys)) {
        // Auto-fit-to-contents is the minimal image size.
        var auto = allowed.FirstOrDefault(v =>
          v.Contains("Auto", StringComparison.OrdinalIgnoreCase)
          || v.Contains("fit", StringComparison.OrdinalIgnoreCase));
        if (auto != null) { result[opt.Key] = auto; hasRealKnob = true; }
      } else if (MatchesAny(opt.Key, UnitKeys)) {
        var smallest = SmallestByBytes(allowed);
        if (smallest != null) { result[opt.Key] = smallest; hasRealKnob = true; }
      } else if (MatchesAny(opt.Key, CountKeys)) {
        var smallest = SmallestByLeadingInt(allowed);
        if (smallest != null) { result[opt.Key] = smallest; hasRealKnob = true; }
      }
    }
    // Only a format with a real geometry knob gets the minimal-rebuild path.
    // The universal opt-in flag tells the writer to drop its size headroom.
    if (!hasRealKnob) return [];
    result["MinimalGeometry"] = "true";
    return result;
  }

  private static bool MatchesAny(string key, string[] candidates)
    => candidates.Any(c => string.Equals(key, c, StringComparison.OrdinalIgnoreCase));

  /// <summary>Returns the allowed value with the smallest parsed byte size (ignoring "Auto").</summary>
  private static string? SmallestByBytes(IReadOnlyList<string> allowed) {
    string? best = null;
    var bestBytes = long.MaxValue;
    foreach (var v in allowed) {
      var b = ParseByteSize(v);
      if (b <= 0) continue;
      if (b < bestBytes) { bestBytes = b; best = v; }
    }
    return best;
  }

  /// <summary>Returns the allowed value with the smallest leading integer (ignoring "Auto").</summary>
  private static string? SmallestByLeadingInt(IReadOnlyList<string> allowed) {
    string? best = null;
    var bestN = long.MaxValue;
    foreach (var v in allowed) {
      var n = ParseLeadingInt(v);
      if (n <= 0) continue;
      if (n < bestN) { bestN = n; best = v; }
    }
    return best;
  }

  /// <summary>Parses "512 B" / "1 KB" / "32 KB" / "1.44 MB" → bytes; 0 if not a size.</summary>
  private static long ParseByteSize(string s) {
    var t = s.Trim();
    var i = 0;
    while (i < t.Length && (char.IsDigit(t[i]) || t[i] is '.' or ',')) i++;
    if (i == 0) return 0;
    if (!double.TryParse(t[..i].Replace(',', '.'), System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out var num)) return 0;
    var rest = t[i..].TrimStart().ToUpperInvariant();
    long mult = rest switch {
      var r when r.StartsWith("KB") || r.StartsWith("K") => 1024L,
      var r when r.StartsWith("MB") || r.StartsWith("M") => 1024L * 1024,
      var r when r.StartsWith("GB") || r.StartsWith("G") => 1024L * 1024 * 1024,
      var r when r.StartsWith('B') || r.Length == 0 => 1L,
      _ => 0L,
    };
    return (long)(num * mult);
  }

  private static long ParseLeadingInt(string s) {
    var t = s.Trim();
    var i = 0;
    while (i < t.Length && char.IsDigit(t[i])) i++;
    return i > 0 && long.TryParse(t[..i], out var n) ? n : 0;
  }
}
