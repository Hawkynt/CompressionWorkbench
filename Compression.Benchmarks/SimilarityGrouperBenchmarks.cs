#pragma warning disable CS1591

using BenchmarkDotNet.Attributes;
using Compression.Core.Statistics;

namespace Compression.Benchmarks;

/// <summary>
/// Benchmarks fingerprint computation and similarity grouping for solid-block planning.
/// Setup generates 100 files of 64 KB each with varying content patterns.
/// </summary>
[Config(typeof(InProcessConfig))]
public class SimilarityGrouperBenchmarks {

  private byte[][] _files = null!;
  private FileFingerprint[] _fingerprints = null!;

  [GlobalSetup]
  public void Setup() {
    _files = new byte[100][];
    var rng = new Random(42);

    for (var i = 0; i < 100; i++) {
      _files[i] = new byte[65536];
      if (i < 25) {
        // Highly compressible: repeated patterns
        var pattern = (byte)(i % 10 + 0x41);
        Array.Fill(_files[i], pattern);
      } else if (i < 50) {
        // Text-like: ASCII range with some repetition
        for (var j = 0; j < _files[i].Length; j++)
          _files[i][j] = (byte)(0x20 + (j + i) % 95);
      } else if (i < 75) {
        // Binary-structured: repeating 256-byte blocks
        for (var j = 0; j < _files[i].Length; j++)
          _files[i][j] = (byte)((j + i * 7) % 256);
      } else {
        // Random (incompressible)
        rng.NextBytes(_files[i]);
      }
    }

    // Pre-compute fingerprints for GroupBySimilarity benchmark
    _fingerprints = new FileFingerprint[100];
    for (var i = 0; i < 100; i++)
      _fingerprints[i] = FileSimilarityGrouper.ComputeFingerprint(_files[i]);
  }

  [Benchmark(Description = "ComputeFingerprint (100 x 64KB)")]
  public FileFingerprint[] ComputeAllFingerprints() {
    var results = new FileFingerprint[100];
    for (var i = 0; i < 100; i++)
      results[i] = FileSimilarityGrouper.ComputeFingerprint(_files[i]);
    return results;
  }

  [Benchmark(Description = "GroupBySimilarity (100 files, 10 groups)")]
  public List<List<int>> GroupFiles() {
    return FileSimilarityGrouper.GroupBySimilarity(_files, maxGroups: 10, maxGroupSize: 4 * 1024 * 1024);
  }
}
