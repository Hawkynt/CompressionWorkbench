using Compression.Registry;

namespace Compression.Lib;

/// <summary>
/// Searches a creatable archive/container's finite option schema and keeps the
/// smallest verified same-format rebuild. Unlike <see cref="CompressionOptimizer"/>,
/// which optimizes one compressed stream, this optimizer works on multi-entry
/// containers (compression method/level, dictionary, solid-block size, etc.).
/// </summary>
public static class ArchiveCompressionOptimizer {
  public sealed record Result(long OriginalSize, long OptimizedSize, int EntriesOptimized,
                              IReadOnlyDictionary<string, string> Parameters, int Probes);

  public static Result Optimize(
      string inputPath,
      string outputPath,
      IArchiveFormatOperations ops,
      IArchiveCreatable creator,
      IFormatOptionsSchema schema,
      int maxCombinations = 256) {
    ArgumentException.ThrowIfNullOrEmpty(inputPath);
    ArgumentException.ThrowIfNullOrEmpty(outputPath);
    ArgumentNullException.ThrowIfNull(ops);
    ArgumentNullException.ThrowIfNull(creator);
    ArgumentNullException.ThrowIfNull(schema);

    var axes = SearchAxes(schema).ToArray();
    var originalSize = new FileInfo(inputPath).Length;
    var sourceEntries = CountLiveEntries(inputPath, ops);
    var bestSize = originalSize;
    var bestParameters = (IReadOnlyDictionary<string, string>)new Dictionary<string, string>();
    string? bestPath = null;
    var probes = 0;

    try {
      if (axes.Length > 0) {
        long product = 1;
        foreach (var axis in axes) {
          product = checked(Math.Min((long)maxCombinations + 1, product * axis.Values.Count));
          if (product > maxCombinations) break;
        }

        if (product <= maxCombinations) {
          foreach (var combination in EnumerateCombinations(axes))
            Probe(combination);
        } else {
          var current = axes.ToDictionary(a => a.Key, a => a.Default, StringComparer.Ordinal);
          Probe(current);
          var improved = true;
          while (improved && probes < maxCombinations) {
            improved = false;
            foreach (var axis in axes) {
              foreach (var value in axis.Values) {
                if (probes >= maxCombinations) break;
                if (current[axis.Key] == value) continue;
                var trial = new Dictionary<string, string>(current, StringComparer.Ordinal) {
                  [axis.Key] = value,
                };
                var before = bestSize;
                Probe(trial);
                if (bestSize < before) {
                  current = trial;
                  improved = true;
                }
              }
            }
          }
        }
      }

      if (bestPath == null) {
        AtomicFileWriter.WriteAtomic(outputPath, output => {
          using var input = File.OpenRead(inputPath);
          input.CopyTo(output);
        });
        return new Result(originalSize, originalSize, 0, bestParameters, probes);
      }

      AtomicFileWriter.WriteAtomic(outputPath, output => {
        using var best = File.OpenRead(bestPath);
        best.CopyTo(output);
      });
      return new Result(originalSize, bestSize, sourceEntries, bestParameters, probes);
    } finally {
      if (bestPath != null) TryDelete(bestPath);
    }

    void Probe(IReadOnlyDictionary<string, string> parameters) {
      ++probes;
      var candidatePath = Path.Combine(Path.GetTempPath(), "cwb_arcopt_" + Guid.NewGuid().ToString("N") + ".tmp");
      try {
        using (var input = File.OpenRead(inputPath))
        using (var candidate = new FileStream(candidatePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)) {
          RebuildVerb.RebuildToStream(input, candidate, ops, creator, parameters);
          candidate.Flush(flushToDisk: true);
          if (candidate.Length >= bestSize) return;
          bestSize = candidate.Length;
        }

        if (bestPath != null) TryDelete(bestPath);
        bestPath = candidatePath;
        candidatePath = "";
        bestParameters = new Dictionary<string, string>(parameters, StringComparer.Ordinal);
      } catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or IOException or ArgumentException) {
        // An individual parameter combination may be invalid for the current
        // content/profile. It is a rejected probe, not an optimization failure.
      } finally {
        if (!string.IsNullOrEmpty(candidatePath)) TryDelete(candidatePath);
      }
    }
  }

  private sealed record Axis(string Key, IReadOnlyList<string> Values, string Default);

  private static IEnumerable<Axis> SearchAxes(IFormatOptionsSchema schema) {
    foreach (var option in schema.OptionsSchema) {
      IReadOnlyList<string>? values = option.Kind switch {
        FormatOptionKind.Enum or FormatOptionKind.Integer when option.AllowedValues is { Count: > 1 }
          => option.AllowedValues,
        FormatOptionKind.Boolean => ["false", "true"],
        _ => null,
      };
      if (values is { Count: > 1 })
        yield return new Axis(option.Key, values, option.Default);
    }
  }

  private static IEnumerable<IReadOnlyDictionary<string, string>> EnumerateCombinations(IReadOnlyList<Axis> axes) {
    if (axes.Count == 0) yield break;
    var indices = new int[axes.Count];
    while (true) {
      var result = new Dictionary<string, string>(axes.Count, StringComparer.Ordinal);
      for (var i = 0; i < axes.Count; ++i) result[axes[i].Key] = axes[i].Values[indices[i]];
      yield return result;

      var position = axes.Count - 1;
      while (position >= 0 && ++indices[position] == axes[position].Values.Count) {
        indices[position] = 0;
        --position;
      }
      if (position < 0) yield break;
    }
  }

  private static int CountLiveEntries(string path, IArchiveFormatOperations ops) {
    using var input = File.OpenRead(path);
    return ops.List(input, null).Count(e => !e.IsDirectory);
  }

  private static void TryDelete(string path) {
    try { File.Delete(path); } catch { /* best effort */ }
  }
}
