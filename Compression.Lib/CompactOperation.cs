using Compression.Registry;

namespace Compression.Lib;

/// <summary>
/// Composite maintenance verb: defragment extents, recompress payloads, repack
/// containers, then shrink. Every stage is selected only from an explicit
/// capability interface; "optimize" is not used as an umbrella capability.
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
      if (descriptor is ILayoutOptimizable layout
          && descriptor is IFormatOptionsSchema schema
          && ops != null
          && SelectMinimalGeometry(schema) is { Count: > 0 } minimal) {
        TryMinimalRebuild(path, layout, ops, minimal, options.Password, log);
        steps.Add("minimal-geometry rebuild");
        return new CompactResult(originalSize, new FileInfo(path).Length, steps, Minimal: true);
      }
      log($"compact: '{formatId}' exposes no explicit layout capability with minimal-geometry knobs — running standard compact instead.");
    }

    if (descriptor is IFilesystemExtentMap && ops is IArchiveDefragmentable defragmentable) {
      try {
        using var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite);
        defragmentable.Defragment(stream, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });
        steps.Add("defragment");
        log("defragment: consolidated live data at the start.");
      } catch (Exception ex) {
        log($"defragment: skipped ({ex.GetType().Name}: {ex.Message}).");
      }
    }

    if (descriptor is ICompressionOptimizable compression) {
      var tempOut = path + ".compact-compress.tmp";
      try {
        var before = new FileInfo(path).Length;
        using (var input = File.OpenRead(path))
        using (var output = new FileStream(tempOut, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
          compression.OptimizeCompression(input, output);

        var candidate = new FileInfo(tempOut).Length;
        if (candidate > 0 && candidate < before) {
          VerifyCompressionCandidate(path, tempOut, descriptor, ops, options.Password);
          AtomicFileWriter.ReplaceTarget(tempOut, path);
          steps.Add("compress");
          log($"compress: {before:N0} → {candidate:N0} bytes.");
        } else {
          log("compress: no smaller representation.");
        }
      } catch (Exception ex) {
        log($"compress: skipped ({ex.GetType().Name}: {ex.Message}).");
      } finally {
        AtomicFileWriter.TryDelete(tempOut);
      }
    }

    if (descriptor is IArchiveRepackable repackable) {
      var tempOut = path + ".compact-repack.tmp";
      try {
        var before = new FileInfo(path).Length;
        using (var input = File.OpenRead(path))
        using (var output = new FileStream(tempOut, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
          repackable.Repack(input, output);

        var candidate = new FileInfo(tempOut).Length;
        if (candidate > 0 && candidate < before) {
          AtomicFileWriter.ReplaceTarget(tempOut, path);
          steps.Add("repack");
          log($"repack: {before:N0} → {candidate:N0} bytes.");
        } else {
          log("repack: no smaller verified representation.");
        }
      } catch (Exception ex) {
        log($"repack: skipped ({ex.GetType().Name}: {ex.Message}).");
      } finally {
        AtomicFileWriter.TryDelete(tempOut);
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

  private static void TryMinimalRebuild(
      string path,
      ILayoutOptimizable layout,
      IArchiveFormatOperations operations,
      IReadOnlyDictionary<string, string> minimalGeometry,
      string? password,
      Action<string> log) {
    SemanticPreservationManifest before;
    using (var source = File.OpenRead(path))
      before = SemanticPreservationManifest.Capture(source, operations, password);

    var tempOut = path + $".compact-min.tmp.{Guid.NewGuid():N}";
    try {
      using (var source = File.OpenRead(path))
      using (var target = new FileStream(tempOut, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        layout.RebuildStreaming(source, target, new LayoutRebuildOptions {
          Parameters = minimalGeometry,
        });

      SemanticPreservationManifest after;
      using (var candidate = File.OpenRead(tempOut))
        after = SemanticPreservationManifest.Capture(candidate, operations, password);
      before.VerifyEquivalent(after);

      var rebuiltLen = new FileInfo(tempOut).Length;
      var originalLen = new FileInfo(path).Length;
      if (rebuiltLen >= originalLen) {
        log($"minimal rebuild: produced no reduction ({rebuiltLen:N0} ≥ {originalLen:N0} bytes); keeping original.");
        return;
      }

      AtomicFileWriter.ReplaceTarget(tempOut, path);
      log($"minimal rebuild: re-created at minimal geometry — {originalLen:N0} → {rebuiltLen:N0} bytes "
          + $"({string.Join(", ", minimalGeometry.Select(kv => $"{kv.Key}={kv.Value}"))}).");
    } finally {
      AtomicFileWriter.TryDelete(tempOut);
    }
  }

  private static void VerifyCompressionCandidate(
      string sourcePath,
      string candidatePath,
      IFormatDescriptor descriptor,
      IArchiveFormatOperations? operations,
      string? password) {
    if (descriptor is IStreamFormatOperations streamOperations) {
      using var source = File.OpenRead(sourcePath);
      using var candidate = File.OpenRead(candidatePath);
      var before = HashDecoded(source, streamOperations);
      var after = HashDecoded(candidate, streamOperations);
      if (!before.AsSpan().SequenceEqual(after))
        throw new InvalidOperationException("Compression changed the decoded payload.");
      return;
    }

    if (operations == null)
      throw new NotSupportedException(
        "Compression candidate cannot be verified: the format exposes neither stream nor archive semantics.");

    SemanticPreservationManifest beforeManifest;
    using (var source = File.OpenRead(sourcePath))
      beforeManifest = SemanticPreservationManifest.Capture(source, operations, password);
    SemanticPreservationManifest afterManifest;
    using (var candidate = File.OpenRead(candidatePath))
      afterManifest = SemanticPreservationManifest.Capture(candidate, operations, password);
    beforeManifest.VerifyEquivalent(afterManifest);
  }

  private static byte[] HashDecoded(Stream encoded, IStreamFormatOperations operations) {
    using var decoded = RebuildVerb.CreateScratchStream();
    encoded.Position = 0;
    operations.Decompress(encoded, decoded);
    decoded.Position = 0;
    return System.Security.Cryptography.SHA256.HashData(decoded);
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
