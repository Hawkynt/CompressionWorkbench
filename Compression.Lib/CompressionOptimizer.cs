using System.Diagnostics;
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
/// <para>Each distinct parameter combination is probed at most once per run
/// (coordinate descent revisits the current point), and a wall-clock /
/// combination budget can be supplied via <see cref="OptimizerOptions"/> so the
/// search can be tuned for speed (Fast) or thoroughness (Max). The objective is
/// pluggable: the default minimises output size and is byte-for-byte compatible
/// with the historical behaviour; a size-vs-speed score is also available.</para>
/// </remarks>
public static class CompressionOptimizer {

  /// <summary>Effort presets that scale the search's combination budget.</summary>
  public enum Effort {
    /// <summary>Quick scan — small combination cap, suited to interactive use.</summary>
    Fast,
    /// <summary>Balanced default — the historical 512-combination budget.</summary>
    Balanced,
    /// <summary>Exhaustive — a large cap that effectively never truncates real schemas.</summary>
    Max,
  }

  /// <summary>What the optimizer is asked to minimise.</summary>
  public enum Objective {
    /// <summary>Smallest compressed output (default; byte-for-byte compatible with prior behaviour).</summary>
    Size,
    /// <summary>A blended size-vs-speed score: smaller AND faster wins. Ties broken by size.</summary>
    SizeAndSpeed,
  }

  /// <summary>
  /// Tuning knobs for an optimization run. Defaults reproduce the legacy
  /// behaviour exactly: Balanced effort (512 combos), no wall-clock cap,
  /// size-only objective, probe caching on.
  /// </summary>
  public sealed record OptimizerOptions {
    /// <summary>Effort preset; scales <see cref="MaxCombinations"/> when that is left at its default.</summary>
    public Effort Effort { get; init; } = Effort.Balanced;

    /// <summary>
    /// Exhaustive-search budget; above it, coordinate descent is used. When null,
    /// the cap is derived from <see cref="Effort"/> (Fast=64, Balanced=512, Max=100000).
    /// </summary>
    public int? MaxCombinations { get; init; }

    /// <summary>Optional wall-clock cap. When the budget elapses the best result so far is returned.</summary>
    public TimeSpan? TimeBudget { get; init; }

    /// <summary>Objective to minimise.</summary>
    public Objective Objective { get; init; } = Objective.Size;

    /// <summary>Resolve the effective combination cap (explicit value wins, else the effort preset).</summary>
    public int ResolvedMaxCombinations => this.MaxCombinations ?? this.Effort switch {
      Effort.Fast => 64,
      Effort.Max => 100_000,
      _ => 512,
    };
  }

  /// <summary>The outcome of an optimization run.</summary>
  public sealed record Result(byte[] Bytes, IReadOnlyDictionary<string, string> Parameters, long OriginalSize) {
    /// <summary>Size of the smallest compressed output found.</summary>
    public long CompressedSize => this.Bytes.LongLength;
    /// <summary>Compressed / original ratio (lower is better); 0 when input was empty.</summary>
    public double Ratio => this.OriginalSize == 0 ? 0.0 : (double)this.CompressedSize / this.OriginalSize;
    /// <summary>Wall-clock time the winning combination took to compress, in milliseconds.</summary>
    public double CompressTimeMs { get; init; }
    /// <summary>Number of distinct parameter combinations actually compressed during the run.</summary>
    public int Probes { get; init; }
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
      byte[] input, IStreamFormatOperations ops, IFormatOptionsSchema schema, int maxCombinations = 512)
    => OptimizeStream(input, ops, schema, new OptimizerOptions { MaxCombinations = maxCombinations });

  /// <summary>
  /// Finds the best compressed output across <paramref name="schema"/>'s enumerable
  /// options under the supplied <paramref name="options"/> (effort/time budget,
  /// objective, caching).
  /// </summary>
  public static Result OptimizeStream(
      byte[] input, IStreamFormatOperations ops, IFormatOptionsSchema schema, OptimizerOptions options) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(ops);
    ArgumentNullException.ThrowIfNull(schema);
    ArgumentNullException.ThrowIfNull(options);

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

    var search = new Search(input, ops, options);

    // Nothing to tune: just compress at defaults.
    if (axes.Count == 0)
      return search.Probe(new Dictionary<string, string>());

    var maxCombos = options.ResolvedMaxCombinations;
    long product = 1;
    foreach (var a in axes) { product *= a.Values.Count; if (product > maxCombos) break; }

    return product <= maxCombos
      ? search.Exhaustive(axes)
      : search.CoordinateDescent(axes);
  }

  /// <summary>
  /// Holds per-run state: the probe cache (combination → result), the wall-clock
  /// deadline, and the objective comparison. A single instance is used for one
  /// <see cref="OptimizeStream(byte[], IStreamFormatOperations, IFormatOptionsSchema, OptimizerOptions)"/> call.
  /// </summary>
  private sealed class Search(byte[] input, IStreamFormatOperations ops, OptimizerOptions options) {
    private readonly Dictionary<string, Result> _cache = new(StringComparer.Ordinal);
    private readonly Stopwatch _wall = Stopwatch.StartNew();

    /// <summary>True once the wall-clock budget (if any) has elapsed.</summary>
    private bool BudgetExhausted
      => options.TimeBudget is { } budget && this._wall.Elapsed >= budget;

    /// <summary>Number of distinct combinations compressed so far.</summary>
    private int Probes => this._cache.Count;

    /// <summary>Compress one combination, reusing the cached result if seen before.</summary>
    public Result Probe(Dictionary<string, string> combo) {
      var key = CacheKey(combo);
      if (this._cache.TryGetValue(key, out var hit))
        return hit;

      using var inMs = new MemoryStream(input, writable: false);
      using var outMs = new MemoryStream();
      var sw = Stopwatch.StartNew();
      ops.Compress(inMs, outMs, new FormatCreateOptions { FormatSpecific = combo });
      sw.Stop();

      var result = new Result(outMs.ToArray(), combo, input.LongLength) {
        CompressTimeMs = sw.Elapsed.TotalMilliseconds,
        Probes = this.Probes + 1,
      };
      this._cache[key] = result;
      return result;
    }

    /// <summary>True if <paramref name="candidate"/> beats <paramref name="incumbent"/> under the objective.</summary>
    private bool IsBetter(Result candidate, Result incumbent) => options.Objective switch {
      Objective.SizeAndSpeed => Score(candidate) < Score(incumbent)
        || (Score(candidate).Equals(Score(incumbent)) && candidate.CompressedSize < incumbent.CompressedSize),
      _ => candidate.CompressedSize < incumbent.CompressedSize,
    };

    /// <summary>
    /// Blended score for <see cref="Objective.SizeAndSpeed"/>: ratio plus a small
    /// time penalty (ms scaled down so size dominates but speed breaks near-ties).
    /// Lower is better.
    /// </summary>
    private static double Score(Result r) => r.Ratio + (r.CompressTimeMs / 10_000.0);

    public Result Exhaustive(List<(string Key, IReadOnlyList<string> Values, string Default)> axes) {
      Result? best = null;
      var indices = new int[axes.Count];
      while (true) {
        var combo = new Dictionary<string, string>(axes.Count);
        for (var i = 0; i < axes.Count; i++) combo[axes[i].Key] = axes[i].Values[indices[i]];
        var r = this.Probe(combo);
        if (best is null || this.IsBetter(r, best)) best = r;

        if (this.BudgetExhausted) break;

        // Increment the mixed-radix odometer.
        var pos = axes.Count - 1;
        while (pos >= 0 && ++indices[pos] == axes[pos].Values.Count) { indices[pos] = 0; --pos; }
        if (pos < 0) break;
      }
      return this.Finalize(best!);
    }

    public Result CoordinateDescent(List<(string Key, IReadOnlyList<string> Values, string Default)> axes) {
      // Start from the schema defaults, then sweep each axis independently,
      // keeping the value that improves the objective, until a full pass yields no gain.
      var current = new Dictionary<string, string>(axes.Count);
      foreach (var a in axes) current[a.Key] = a.Default;
      var best = this.Probe(current);

      bool improved;
      do {
        improved = false;
        foreach (var (key, values, _) in axes) {
          foreach (var v in values) {
            if (current[key] == v) continue;
            if (this.BudgetExhausted) return this.Finalize(best);
            var trial = new Dictionary<string, string>(current) { [key] = v };
            var r = this.Probe(trial); // cache dedups revisits of the current point
            if (this.IsBetter(r, best)) {
              best = r; current = trial; improved = true;
            }
          }
        }
      } while (improved);
      return this.Finalize(best);
    }

    /// <summary>Stamp the run's total distinct-probe count onto the winning result.</summary>
    private Result Finalize(Result best) => best with { Probes = this.Probes };

    private static string CacheKey(Dictionary<string, string> combo)
      => string.Join("", combo.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}{kv.Value}"));
  }
}
