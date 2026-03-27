using System.Diagnostics;
using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.BenchmarkMatrix;

/// <summary>
/// Stress tests all building blocks at 64KB across all data patterns.
/// Identifies algorithms that are too slow for interactive benchmarking.
/// </summary>
[TestFixture]
public class BenchmarkStressTests {

  private const int Size = 65536; // 64 KB — realistic benchmark size
  private const int TimeoutMs = 30_000; // 30 seconds max per test

  private static IEnumerable<TestCaseData> AllBlockPatterns() {
    FormatRegistration.EnsureInitialized();
    var blocks = BuildingBlockRegistry.All.OrderBy(b => b.DisplayName);

    var patterns = new (string Name, Func<byte[]> Generator)[] {
      ("Zeroes", () => new byte[Size]),
      ("Alternating", () => {
        var b = new byte[Size];
        for (var i = 0; i < Size; i++) b[i] = (byte)(i % 2 == 0 ? 0xAA : 0x55);
        return b;
      }),
      ("Incrementing", () => {
        var b = new byte[Size];
        for (var i = 0; i < Size; i++) b[i] = (byte)(i & 0xFF);
        return b;
      }),
      ("Random", () => {
        var r = new Random(42);
        var b = new byte[Size];
        r.NextBytes(b);
        return b;
      }),
      ("Text", () => {
        var text = "The quick brown fox jumps over the lazy dog. Compression varies. "u8;
        var b = new byte[Size];
        for (var i = 0; i < Size; i++) b[i] = text[i % text.Length];
        return b;
      }),
      ("BinaryStruct", () => {
        var b = new byte[Size];
        var rng = new Random(123);
        for (var i = 0; i < Size; i++)
          b[i] = (i % 16) switch {
            0 or 1 or 2 or 3 => (byte)(i / 16 & 0xFF),
            4 or 5 => 0,
            6 or 7 => (byte)(i % 3),
            _ => (byte)rng.Next(256),
          };
        return b;
      }),
    };

    foreach (var block in blocks)
      foreach (var (patName, gen) in patterns)
        yield return new TestCaseData(block.Id, block.DisplayName, patName, gen)
          .SetName($"{block.DisplayName} / {patName}");
  }

  [TestCaseSource(nameof(AllBlockPatterns))]
  [CancelAfter(TimeoutMs)]
  public void CompressDecompressRoundTrip(string blockId, string displayName, string pattern, Func<byte[]> generator) {
    var block = BuildingBlockRegistry.GetById(blockId)!;
    var data = generator();

    var sw = Stopwatch.StartNew();

    // Compress
    var compressed = block.Compress(data);
    var compressMs = sw.Elapsed.TotalMilliseconds;

    // Decompress
    sw.Restart();
    var decompressed = block.Decompress(compressed);
    var decompressMs = sw.Elapsed.TotalMilliseconds;

    var ratio = (double)compressed.Length / data.Length;

    TestContext.Out.WriteLine($"{displayName}/{pattern}: compress={compressMs:F0}ms, decompress={decompressMs:F0}ms, ratio={ratio:P1}");

    Assert.That(decompressed, Has.Length.EqualTo(data.Length), "Size mismatch");
    Assert.That(decompressed, Is.EqualTo(data), "Data mismatch");
  }
}
