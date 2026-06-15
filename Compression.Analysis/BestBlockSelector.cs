using Compression.Lib;
using Compression.Registry;

namespace Compression.Analysis;

/// <summary>
/// The "auto-selector" half of the universal-compressor goal: given raw bytes,
/// it benchmarks every applicable building block against the caller's actual
/// data (reusing <see cref="ParallelBenchmarkRunner"/>), ranks them by a
/// configurable objective, then re-compresses with the winner — and, when a
/// matching schema-driven stream format exists, hands the winning codec to
/// <see cref="CompressionOptimizer"/> to tune its parameters too.
/// </summary>
/// <remarks>
/// <para>This operates at the building-block level (raw bytes), exactly as the
/// <c>benchmark</c> command does; it deliberately does not round-trip through
/// archive containers.</para>
/// </remarks>
public static class BestBlockSelector {

  /// <summary>How candidate blocks are ranked.</summary>
  public enum Objective {
    /// <summary>Smallest compressed output wins (default).</summary>
    SmallestOutput,
    /// <summary>
    /// Among blocks whose compress time is within <see cref="Options.SpeedWindowPercent"/>%
    /// of the fastest, pick the best ratio. Trades a little speed for size without
    /// paying for a codec that is dramatically slower.
    /// </summary>
    BestRatioWithinSpeedWindow,
  }

  /// <summary>Tuning knobs for a selection run.</summary>
  public sealed record Options {
    /// <summary>Ranking objective.</summary>
    public Objective Objective { get; init; } = Objective.SmallestOutput;

    /// <summary>For <see cref="Objective.BestRatioWithinSpeedWindow"/>: the speed window, in percent over the fastest verified block.</summary>
    public double SpeedWindowPercent { get; init; } = 50.0;

    /// <summary>When true, the winning block's parameters are tuned via <see cref="CompressionOptimizer"/> if a schema-bearing stream format matches it.</summary>
    public bool OptimizeWinnerParameters { get; init; } = true;

    /// <summary>Per-block benchmark timeout, in milliseconds.</summary>
    public int PerBlockTimeoutMs { get; init; } = 10_000;

    /// <summary>Optional override for the optimizer's effort when tuning the winner.</summary>
    public CompressionOptimizer.Effort OptimizerEffort { get; init; } = CompressionOptimizer.Effort.Balanced;
  }

  /// <summary>One row of the ranked candidate table.</summary>
  public sealed record Candidate(
      string BlockId,
      string DisplayName,
      long CompressedSize,
      double Ratio,
      double CompressTimeMs,
      bool Verified,
      string? Error) {
    /// <summary>True when this block compressed and round-tripped successfully.</summary>
    public bool Succeeded => this.Error is null && this.Verified && this.CompressedSize >= 0;
  }

  /// <summary>The outcome of a selection run.</summary>
  public sealed record Result(
      string WinningBlockId,
      string WinningDisplayName,
      byte[] CompressedBytes,
      long OriginalSize,
      IReadOnlyList<Candidate> Table,
      IReadOnlyDictionary<string, string>? BestParameters,
      string? OptimizedFormatId) {
    /// <summary>Compressed / original ratio of the winning output (lower is better); 0 when input was empty.</summary>
    public double Ratio => this.OriginalSize == 0 ? 0.0 : (double)this.CompressedBytes.LongLength / this.OriginalSize;
    /// <summary>Size of the winning compressed output.</summary>
    public long CompressedSize => this.CompressedBytes.LongLength;
  }

  /// <summary>
  /// Benchmarks every applicable building block on <paramref name="input"/>, picks the
  /// winner under <paramref name="options"/>, and returns the winner's compressed bytes
  /// plus the full ranked table.
  /// </summary>
  /// <param name="input">Raw bytes to compress.</param>
  /// <param name="options">Selection knobs; null = defaults (smallest output, tune winner).</param>
  /// <param name="blocks">Candidate blocks; null = all registered building blocks.</param>
  /// <param name="ct">Cancellation token.</param>
  public static async Task<Result> SelectAsync(
      byte[] input,
      Options? options = null,
      IReadOnlyList<IBuildingBlock>? blocks = null,
      CancellationToken ct = default) {
    ArgumentNullException.ThrowIfNull(input);
    options ??= new Options();
    FormatRegistration.EnsureInitialized();
    blocks ??= BuildingBlockRegistry.All;

    var runner = new ParallelBenchmarkRunner(perTestTimeoutMs: options.PerBlockTimeoutMs);
    var entries = await runner.RunAllAsync(
      [("input", input)], blocks, iterations: 1, ct).ConfigureAwait(false);

    var table = entries
      .Select(e => new Candidate(e.BlockId, e.DisplayName, e.CompressedSize, e.Ratio, e.CompressTimeMs, e.Verified, e.Error))
      .OrderBy(c => c.Succeeded ? c.CompressedSize : long.MaxValue)
      .ThenBy(c => c.CompressTimeMs)
      .ToList();

    var winner = PickWinner(table, options)
      ?? throw new InvalidOperationException("No building block succeeded on this input.");

    // Re-compress with the winning block to obtain the actual bytes (the
    // benchmark only recorded sizes/timings, not the payloads).
    var block = BuildingBlockRegistry.GetById(winner.BlockId)
      ?? throw new InvalidOperationException($"Winning block '{winner.BlockId}' is no longer registered.");
    var compressed = block.Compress(input);

    IReadOnlyDictionary<string, string>? bestParams = null;
    string? optimizedFormatId = null;

    if (options.OptimizeWinnerParameters
        && TryFindSchemaFormat(winner.DisplayName, out var ops, out var schema, out var formatId)) {
      var opt = CompressionOptimizer.OptimizeStream(
        input, ops!, schema!, new CompressionOptimizer.OptimizerOptions { Effort = options.OptimizerEffort });
      bestParams = opt.Parameters;
      optimizedFormatId = formatId;
      // Keep the smaller of the two encodings (block raw vs schema-tuned stream).
      if (opt.Bytes.LongLength < compressed.LongLength)
        compressed = opt.Bytes;
    }

    return new Result(winner.BlockId, winner.DisplayName, compressed, input.LongLength, table, bestParams, optimizedFormatId);
  }

  /// <summary>Synchronous convenience wrapper around <see cref="SelectAsync"/>.</summary>
  public static Result Select(
      byte[] input,
      Options? options = null,
      IReadOnlyList<IBuildingBlock>? blocks = null)
    => SelectAsync(input, options, blocks).GetAwaiter().GetResult();

  private static Candidate? PickWinner(IReadOnlyList<Candidate> table, Options options) {
    var succeeded = table.Where(c => c.Succeeded).ToList();
    if (succeeded.Count == 0)
      return null;

    switch (options.Objective) {
      case Objective.BestRatioWithinSpeedWindow: {
        var fastest = succeeded.Min(c => c.CompressTimeMs);
        var threshold = fastest * (1.0 + options.SpeedWindowPercent / 100.0);
        var inWindow = succeeded.Where(c => c.CompressTimeMs <= threshold).ToList();
        var pool = inWindow.Count > 0 ? inWindow : succeeded;
        return pool.OrderBy(c => c.CompressedSize).ThenBy(c => c.CompressTimeMs).First();
      }
      default:
        // table is already sorted smallest-first.
        return succeeded.OrderBy(c => c.CompressedSize).ThenBy(c => c.CompressTimeMs).First();
    }
  }

  /// <summary>
  /// Best-effort link from a winning building block to a schema-bearing stream
  /// format so its parameters can be tuned. Matches on the format's display name
  /// (case-insensitive), which is how building blocks and stream formats are
  /// labelled in the benchmark table.
  /// </summary>
  private static bool TryFindSchemaFormat(
      string blockDisplayName,
      out IStreamFormatOperations? ops,
      out IFormatOptionsSchema? schema,
      out string? formatId) {
    ops = null; schema = null; formatId = null;
    foreach (var desc in FormatRegistry.All) {
      if (!string.Equals(desc.DisplayName, blockDisplayName, StringComparison.OrdinalIgnoreCase))
        continue;
      if (FormatRegistry.GetStreamOps(desc.Id) is { } streamOps
          && desc is IFormatOptionsSchema s && s.OptionsSchema.Count > 0) {
        ops = streamOps; schema = s; formatId = desc.Id;
        return true;
      }
    }
    return false;
  }
}
