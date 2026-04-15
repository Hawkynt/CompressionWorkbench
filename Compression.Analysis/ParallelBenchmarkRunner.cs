#pragma warning disable CS1591

using System.Collections.Concurrent;
using System.Diagnostics;
using Compression.Registry;

namespace Compression.Analysis;

/// <summary>
/// Runs building block benchmarks in parallel with configurable concurrency and per-test timeouts.
/// </summary>
public sealed class ParallelBenchmarkRunner {

  /// <summary>Result of a single building block benchmark.</summary>
  public sealed class BenchmarkEntry {
    /// <summary>Building block identifier.</summary>
    public required string BlockId { get; init; }

    /// <summary>Human-readable display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Name of the test data pattern.</summary>
    public required string PatternName { get; init; }

    /// <summary>Original data size in bytes.</summary>
    public int OriginalSize { get; init; }

    /// <summary>Compressed size in bytes, or -1 on failure.</summary>
    public int CompressedSize { get; init; }

    /// <summary>Compression ratio (compressed / original), or -1 on failure.</summary>
    public double Ratio { get; init; }

    /// <summary>Compression time in milliseconds, or -1 on failure.</summary>
    public double CompressTimeMs { get; init; }

    /// <summary>Decompression time in milliseconds, or -1 on failure.</summary>
    public double DecompressTimeMs { get; init; }

    /// <summary>Whether the round-trip was verified.</summary>
    public bool Verified { get; init; }

    /// <summary>Error message if the benchmark failed, null otherwise.</summary>
    public string? Error { get; init; }
  }

  private readonly int _maxDegreeOfParallelism;
  private readonly int _perTestTimeoutMs;

  /// <summary>
  /// Creates a parallel benchmark runner.
  /// </summary>
  /// <param name="maxDegreeOfParallelism">Maximum concurrent benchmarks. Defaults to processor count.</param>
  /// <param name="perTestTimeoutMs">Per-test timeout in milliseconds. Defaults to 10 seconds.</param>
  public ParallelBenchmarkRunner(int maxDegreeOfParallelism = 0, int perTestTimeoutMs = 10_000) {
    _maxDegreeOfParallelism = maxDegreeOfParallelism > 0 ? maxDegreeOfParallelism : Environment.ProcessorCount;
    _perTestTimeoutMs = perTestTimeoutMs;
  }

  /// <summary>
  /// Runs benchmarks for all building blocks against the given test data patterns in parallel.
  /// </summary>
  /// <param name="testPatterns">Named test data patterns (name, data).</param>
  /// <param name="blocks">Building blocks to benchmark. If null, uses all registered blocks.</param>
  /// <param name="iterations">Number of timing iterations per test. Defaults to 1.</param>
  /// <param name="ct">Cancellation token for overall cancellation.</param>
  /// <returns>List of benchmark entries for all block/pattern combinations.</returns>
  public async Task<List<BenchmarkEntry>> RunAllAsync(
    IReadOnlyList<(string Name, byte[] Data)> testPatterns,
    IReadOnlyList<IBuildingBlock>? blocks = null,
    int iterations = 1,
    CancellationToken ct = default
  ) {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    blocks ??= BuildingBlockRegistry.All;

    // Build work items
    var workItems = new List<(IBuildingBlock Block, string PatternName, byte[] Data)>();
    foreach (var block in blocks)
      foreach (var (name, data) in testPatterns)
        workItems.Add((block, name, data));

    var results = new ConcurrentBag<BenchmarkEntry>();

    var parallelOptions = new ParallelOptions {
      MaxDegreeOfParallelism = _maxDegreeOfParallelism,
      CancellationToken = ct
    };

    await Parallel.ForEachAsync(workItems, parallelOptions, async (item, token) => {
      using var testCts = CancellationTokenSource.CreateLinkedTokenSource(token);
      testCts.CancelAfter(_perTestTimeoutMs);

      var entry = await Task.Run(() => BenchmarkSingle(item.Block, item.PatternName, item.Data, iterations, testCts.Token), token).ConfigureAwait(false);
      results.Add(entry);
    }).ConfigureAwait(false);

    return results.ToList();
  }

  private static BenchmarkEntry BenchmarkSingle(IBuildingBlock block, string patternName, byte[] data, int iterations, CancellationToken token) {
    try {
      token.ThrowIfCancellationRequested();

      // Compress
      var compressed = block.Compress(data);
      token.ThrowIfCancellationRequested();

      // Verify round-trip
      var decompressed = block.Decompress(compressed);
      var verified = decompressed.Length == data.Length && decompressed.AsSpan().SequenceEqual(data);
      token.ThrowIfCancellationRequested();

      // Benchmark compression
      var compSw = Stopwatch.StartNew();
      for (var i = 0; i < iterations; i++) {
        token.ThrowIfCancellationRequested();
        block.Compress(data);
      }
      compSw.Stop();
      var compressTimeMs = compSw.Elapsed.TotalMilliseconds / iterations;

      // Benchmark decompression
      var decSw = Stopwatch.StartNew();
      for (var i = 0; i < iterations; i++) {
        token.ThrowIfCancellationRequested();
        block.Decompress(compressed);
      }
      decSw.Stop();
      var decompressTimeMs = decSw.Elapsed.TotalMilliseconds / iterations;

      var ratio = data.Length > 0 ? (double)compressed.Length / data.Length : 1.0;

      return new BenchmarkEntry {
        BlockId = block.Id,
        DisplayName = block.DisplayName,
        PatternName = patternName,
        OriginalSize = data.Length,
        CompressedSize = compressed.Length,
        Ratio = ratio,
        CompressTimeMs = compressTimeMs,
        DecompressTimeMs = decompressTimeMs,
        Verified = verified
      };
    }
    catch (OperationCanceledException) {
      return new BenchmarkEntry {
        BlockId = block.Id,
        DisplayName = block.DisplayName,
        PatternName = patternName,
        OriginalSize = data.Length,
        CompressedSize = -1,
        Ratio = -1,
        CompressTimeMs = -1,
        DecompressTimeMs = -1,
        Verified = false,
        Error = "Timeout"
      };
    }
    catch (Exception ex) {
      return new BenchmarkEntry {
        BlockId = block.Id,
        DisplayName = block.DisplayName,
        PatternName = patternName,
        OriginalSize = data.Length,
        CompressedSize = -1,
        Ratio = -1,
        CompressTimeMs = -1,
        DecompressTimeMs = -1,
        Verified = false,
        Error = ex.Message
      };
    }
  }
}
