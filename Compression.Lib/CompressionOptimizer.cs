using Compression.Registry;

namespace Compression.Lib;

/// <summary>
/// Searches a compression format's declared option space (its
/// <see cref="IFormatOptionsSchema"/>) for the parameter combination that yields
/// the smallest output on the caller's actual data, then returns those bytes and
/// the winning parameters. This is the "compression optimizer that hunts for the
/// best parameter combination" half of the universal-compressor goal.
/// </summary>
/// <remarks>
/// <para>When the full Cartesian product of the schema's enumerable options is
/// small (≤ <c>maxCombinations</c>) every combination is tried exhaustively;
/// otherwise a coordinate-descent pass tunes one option at a time (starting from
/// the schema defaults), which scales to large multi-knob schemas without an
/// exponential blow-up.</para>
/// </remarks>
public static class CompressionOptimizer {

  /// <summary>The outcome of an optimization run.</summary>
  public sealed record Result(byte[] Bytes, IReadOnlyDictionary<string, string> Parameters, long OriginalSize) {
    /// <summary>Size of the smallest compressed output found.</summary>
    public long CompressedSize => this.Bytes.LongLength;
    /// <summary>Compressed / original ratio (lower is better); 0 when input was empty.</summary>
    public double Ratio => this.OriginalSize == 0 ? 0.0 : (double)this.CompressedSize / this.OriginalSize;
  }

  /// <summary>
  /// Finds the smallest compressed output across <paramref name="schema"/>'s
  /// enumerable options, compressing <paramref name="input"/> with
  /// <paramref name="ops"/> for each candidate combination.
  /// </summary>
  /// <param name="input">Raw bytes to compress.</param>
  /// <param name="ops">The format's stream compressor.</param>
  /// <param name="schema">The format's option schema (same descriptor object as <paramref name="ops"/>).</param>
  /// <param name="maxCombinations">Exhaustive-search budget; above it, coordinate descent is used.</param>
  public static Result OptimizeStream(
      byte[] input, IStreamFormatOperations ops, IFormatOptionsSchema schema, int maxCombinations = 512) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(ops);
    ArgumentNullException.ThrowIfNull(schema);

    // Each axis = an option with a finite, enumerable candidate set.
    var axes = new List<(string Key, IReadOnlyList<string> Values, string Default)>();
    foreach (var opt in schema.OptionsSchema) {
      var values = opt.Kind switch {
        FormatOptionKind.Enum or FormatOptionKind.Integer when opt.AllowedValues is { Count: > 0 } => opt.AllowedValues,
        FormatOptionKind.Boolean => (IReadOnlyList<string>)["true", "false"],
        _ => null, // String / open Integer: not searchable
      };
      if (values is { Count: > 1 })
        axes.Add((opt.Key, values, opt.Default));
    }

    // Nothing to tune: just compress at defaults.
    if (axes.Count == 0)
      return Compress(input, ops, new Dictionary<string, string>());

    long product = 1;
    foreach (var a in axes) { product *= a.Values.Count; if (product > maxCombinations) break; }

    return product <= maxCombinations
      ? Exhaustive(input, ops, axes)
      : CoordinateDescent(input, ops, axes);
  }

  private static Result Exhaustive(
      byte[] input, IStreamFormatOperations ops,
      List<(string Key, IReadOnlyList<string> Values, string Default)> axes) {
    Result? best = null;
    var indices = new int[axes.Count];
    while (true) {
      var combo = new Dictionary<string, string>(axes.Count);
      for (var i = 0; i < axes.Count; i++) combo[axes[i].Key] = axes[i].Values[indices[i]];
      var r = Compress(input, ops, combo);
      if (best is null || r.CompressedSize < best.CompressedSize) best = r;

      // Increment the mixed-radix odometer.
      var pos = axes.Count - 1;
      while (pos >= 0 && ++indices[pos] == axes[pos].Values.Count) { indices[pos] = 0; --pos; }
      if (pos < 0) break;
    }
    return best!;
  }

  private static Result CoordinateDescent(
      byte[] input, IStreamFormatOperations ops,
      List<(string Key, IReadOnlyList<string> Values, string Default)> axes) {
    // Start from the schema defaults, then sweep each axis independently,
    // keeping the value that shrinks the output, until a full pass yields no gain.
    var current = new Dictionary<string, string>(axes.Count);
    foreach (var a in axes) current[a.Key] = a.Default;
    var best = Compress(input, ops, current);

    bool improved;
    do {
      improved = false;
      foreach (var (key, values, _) in axes)
        foreach (var v in values) {
          if (current[key] == v) continue;
          var trial = new Dictionary<string, string>(current) { [key] = v };
          var r = Compress(input, ops, trial);
          if (r.CompressedSize < best.CompressedSize) {
            best = r; current = trial; improved = true;
          }
        }
    } while (improved);
    return best;
  }

  private static Result Compress(byte[] input, IStreamFormatOperations ops, Dictionary<string, string> combo) {
    using var inMs = new MemoryStream(input, writable: false);
    using var outMs = new MemoryStream();
    ops.Compress(inMs, outMs, new FormatCreateOptions { FormatSpecific = combo });
    return new Result(outMs.ToArray(), combo, input.LongLength);
  }
}
