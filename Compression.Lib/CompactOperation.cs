using Compression.Registry;
using F = Compression.Lib.FormatDetector.Format;

namespace Compression.Lib;

/// <summary>
/// Composite maintenance verb: defragment, optimize and shrink. Every stage is
/// optional and selected from the descriptor's real capabilities.
/// </summary>
public static class CompactOperation {
  public sealed record CompactResult(long OriginalSize, long NewSize, IReadOnlyList<string> StepsRun, bool Minimal);

  public sealed class CompactOptions {
    public bool Minimal { get; init; }
    public string? Password { get; init; }
    public Action<string>? Log { get; init; }
  }

  private static readonly string[] SizeKeys = ["ImageSize", "TotalSize", "VolumeSize"];
  private static readonly string[] UnitKeys = ["ClusterSize", "BlockSize", "UnitSize", "AllocationUnit", "AllocSize"];
  private static readonly string[] CountKeys = ["RootEntries", "InodeCount", "InodeSize"];

  public static CompactResult Compact(string path, CompactOptions? options = null) {
    ArgumentException.ThrowIfNullOrEmpty(path);
    if (!File.Exists(path)) throw new FileNotFoundException("Container not found.", path);
    options ??= new CompactOptions();
    var log = options.Log ?? (_ => { });

    FormatRegistration.EnsureInitialized();
    var originalSize = new FileInfo(path).Length;
    var format = FormatDetector.Detect(path);
    var formatId = format.ToString();
    var descriptor = FormatRegistry.GetById(formatId);
    var ops = FormatRegistry.GetArchiveOps(formatId);
    var steps = new List<string>();

    if (options.Minimal) {
      if (ops is IArchiveCreatable && ops is IFormatOptionsSchema schema
          && SelectMinimalGeometry(schema) is { Count: > 0 } minimal) {
        TryMinimalRebuild(path, format, minimal, options.Password, log);
        steps.Add("minimal-geometry rebuild");
        return new CompactResult(originalSize, new FileInfo(path).Length, steps, Minimal: true);
      }
      log($"compact: '{formatId}' exposes no minimal-geometry knobs — running standard compact instead.");
    }

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

    if (formatId is "DoubleSpace" or "DriveSpace" or "DriveSpace3") {
      if (descriptor != null) {
        try {
          var r = CvfOptimizer.Optimize(path, descriptor);
          steps.Add("optimize");
          log($"optimize: re-encoded via {r.MethodUsed}.");
        } catch (Exception ex) {
          log($"optimize: skipped ({ex.GetType().Name}: {ex.Message}).");
        }
      }
    } else if (descriptor?.Capabilities.HasFlag(FormatCapabilities.SupportsOptimize) == true
               && ops is IArchiveCreatable creator
               && ops is IFormatOptionsSchema archiveSchema
               && archiveSchema.OptionsSchema.Count > 0
               && format != F.Zip
               && !FormatDetector.IsStreamFormat(format)
               && !FormatDetector.GetTarCompression(format).HasValue) {
      // Multi-entry containers with their own finite creation schema (EWF,
      // SquashFS, etc.) need the archive optimizer, not the stream optimizer.
      // It searches the declared axes and accepts only verified same-format
      // rebuilds smaller than the source; otherwise it copies through unchanged.
      var tempOut = path + ".compact-arcopt.tmp";
      try {
        var r = ArchiveCompressionOptimizer.Optimize(path, tempOut, ops, creator, archiveSchema);
        File.Move(tempOut, path, overwrite: true);
        steps.Add("optimize");
        log(r.OptimizedSize < r.OriginalSize
          ? $"optimize: {r.OriginalSize:N0} → {r.OptimizedSize:N0} bytes across {r.Probes} parameter probe(s)."
          : $"optimize: no smaller verified representation after {r.Probes} parameter probe(s).");
      } catch (Exception ex) {
        log($"optimize: skipped ({ex.GetType().Name}: {ex.Message}).");
      } finally {
        if (File.Exists(tempOut)) try { File.Delete(tempOut); } catch { }
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
        if (File.Exists(tempOut)) try { File.Delete(tempOut); } catch { }
      }
    }

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
        if (File.Exists(tempOut)) try { File.Delete(tempOut); } catch { }
      }
    }

    return new CompactResult(originalSize, new FileInfo(path).Length, steps, Minimal: false);
  }

  private static void TryMinimalRebuild(string path, F format,
      IReadOnlyDictionary<string, string> minimalGeometry, string? password, Action<string> log) {
    var sourceEntryCount = SafeFileCount(path, password);
    var tempDir = Path.Combine(Path.GetTempPath(), "cwb_compact_" + Guid.NewGuid().ToString("N")[..8]);
    var tempOut = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!,
      Path.GetFileNameWithoutExtension(path) + ".compact-min" + Path.GetExtension(path));
    try {
      Directory.CreateDirectory(tempDir);
      ArchiveOperations.Extract(path, tempDir, password, files: null);
      var inputs = ArchiveOperations.EnumerateTempInputs(tempDir);
      ArchiveOperations.Create(tempOut, inputs,
        new CompressionOptions { Password = password }, format, minimalGeometry);

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
      if (Directory.Exists(tempDir)) try { Directory.Delete(tempDir, recursive: true); } catch { }
      if (File.Exists(tempOut)) try { File.Delete(tempOut); } catch { }
    }
  }

  private static int SafeFileCount(string path, string? password) {
    try { return ArchiveOperations.List(path, password).Count(e => !e.IsDirectory); }
    catch { return 0; }
  }

  private static Dictionary<string, string> SelectMinimalGeometry(IFormatOptionsSchema schema) {
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    var hasRealKnob = false;
    foreach (var opt in schema.OptionsSchema) {
      if (opt.AllowedValues is not { Count: > 0 } allowed) continue;
      if (MatchesAny(opt.Key, SizeKeys)) {
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
    if (!hasRealKnob) return [];
    result["MinimalGeometry"] = "true";
    return result;
  }

  private static bool MatchesAny(string key, string[] candidates)
    => candidates.Any(c => string.Equals(key, c, StringComparison.OrdinalIgnoreCase));

  private static string? SmallestByBytes(IReadOnlyList<string> allowed) {
    string? best = null;
    var bestBytes = long.MaxValue;
    foreach (var value in allowed) {
      var bytes = ParseByteSize(value);
      if (bytes <= 0 || bytes >= bestBytes) continue;
      bestBytes = bytes;
      best = value;
    }
    return best;
  }

  private static string? SmallestByLeadingInt(IReadOnlyList<string> allowed) {
    string? best = null;
    var bestNumber = long.MaxValue;
    foreach (var value in allowed) {
      var number = ParseLeadingInt(value);
      if (number <= 0 || number >= bestNumber) continue;
      bestNumber = number;
      best = value;
    }
    return best;
  }

  private static long ParseByteSize(string text) {
    var value = text.Trim();
    var i = 0;
    while (i < value.Length && (char.IsDigit(value[i]) || value[i] is '.' or ',')) ++i;
    if (i == 0) return 0;
    if (!double.TryParse(value[..i].Replace(',', '.'), System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out var number)) return 0;
    var rest = value[i..].TrimStart().ToUpperInvariant();
    long multiplier = rest switch {
      var r when r.StartsWith("KB") || r.StartsWith("K") => 1024L,
      var r when r.StartsWith("MB") || r.StartsWith("M") => 1024L * 1024,
      var r when r.StartsWith("GB") || r.StartsWith("G") => 1024L * 1024 * 1024,
      var r when r.StartsWith('B') || r.Length == 0 => 1L,
      _ => 0L,
    };
    return (long)(number * multiplier);
  }

  private static long ParseLeadingInt(string text) {
    var value = text.Trim();
    var i = 0;
    while (i < value.Length && char.IsDigit(value[i])) ++i;
    return i > 0 && long.TryParse(value[..i], out var number) ? number : 0;
  }
}
