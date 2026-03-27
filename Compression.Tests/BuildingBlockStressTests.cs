using System.Diagnostics;
using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests;

/// <summary>
/// Exhaustive round-trip stress tests for all building blocks across diverse data patterns.
/// Marked [Explicit] — not run by default. Run with: dotnet test --filter "Category=Performance"
/// Algorithms exceeding 1 second for 1 KB are flagged for optimization.
/// </summary>
[TestFixture]
[Explicit]
[Category("Performance")]
public class BuildingBlockStressTests {

  private const int SmallSize = 1024;       // 1 KB — performance threshold check
  private const int MediumSize = 64 * 1024; // 64 KB — realistic benchmark size
  private const int LargeSize = 256 * 1024; // 256 KB — stress test

  [OneTimeSetUp]
  public void Init() => FormatRegistration.EnsureInitialized();

  private static IEnumerable<TestCaseData> AllBlockPatterns() {
    FormatRegistration.EnsureInitialized();
    var blocks = BuildingBlockRegistry.All.OrderBy(b => b.DisplayName);

    var patterns = new (string Name, Func<int, byte[]> Generator)[] {
      ("Zeroes", size => new byte[size]),
      ("0xFF", size => { var b = new byte[size]; Array.Fill(b, (byte)0xFF); return b; }),
      ("Alternating_0x00_0xFF", size => {
        var b = new byte[size];
        for (var i = 0; i < size; i++) b[i] = (byte)(i % 2 == 0 ? 0x00 : 0xFF);
        return b;
      }),
      ("Alternating_0xAA_0x55", size => {
        var b = new byte[size];
        for (var i = 0; i < size; i++) b[i] = (byte)(i % 2 == 0 ? 0xAA : 0x55);
        return b;
      }),
      ("Incrementing", size => {
        var b = new byte[size];
        for (var i = 0; i < size; i++) b[i] = (byte)(i & 0xFF);
        return b;
      }),
      ("Decrementing", size => {
        var b = new byte[size];
        for (var i = 0; i < size; i++) b[i] = (byte)(255 - (i & 0xFF));
        return b;
      }),
      ("Random_Seed42", size => {
        var r = new Random(42);
        var b = new byte[size];
        r.NextBytes(b);
        return b;
      }),
      ("Random_Seed0", size => {
        var r = new Random(0);
        var b = new byte[size];
        r.NextBytes(b);
        return b;
      }),
      ("EnglishText", size => {
        var text = "The quick brown fox jumps over the lazy dog. Lorem ipsum dolor sit amet, consectetur adipiscing elit. Sed do eiusmod tempor incididunt ut labore et dolore magna aliqua. "u8;
        var b = new byte[size];
        for (var i = 0; i < size; i++) b[i] = text[i % text.Length];
        return b;
      }),
      ("BinaryStruct", size => {
        var b = new byte[size];
        var rng = new Random(123);
        for (var i = 0; i + 16 <= size; i += 16) {
          b[i] = 0x4D; b[i + 1] = 0x5A; // signature
          b[i + 2] = (byte)rng.Next(256); b[i + 3] = (byte)rng.Next(256); // random fields
          b[i + 4] = 0; b[i + 5] = 0; b[i + 6] = 0; b[i + 7] = 0; // padding
          for (var j = 8; j < 16; j++) b[i + j] = (byte)rng.Next(256);
        }
        return b;
      }),
      ("SingleByte", size => new byte[size]),
      ("TwoValues", size => {
        var b = new byte[size];
        for (var i = 0; i < size; i++) b[i] = (byte)(i % 3 == 0 ? 0x01 : 0x00);
        return b;
      }),
      ("LongRuns", size => {
        var b = new byte[size];
        var pos = 0;
        byte val = 0;
        var rng = new Random(99);
        while (pos < size) {
          var runLen = Math.Min(rng.Next(1, 256), size - pos);
          for (var j = 0; j < runLen; j++) b[pos++] = val;
          val = (byte)rng.Next(256);
        }
        return b;
      }),
      ("ShortRuns", size => {
        var b = new byte[size];
        var rng = new Random(77);
        var pos = 0;
        while (pos < size) {
          var runLen = Math.Min(rng.Next(1, 5), size - pos);
          var val = (byte)rng.Next(256);
          for (var j = 0; j < runLen; j++) b[pos++] = val;
        }
        return b;
      }),
      ("SparseNonZero", size => {
        var b = new byte[size];
        var rng = new Random(55);
        for (var i = 0; i < size / 16; i++)
          b[rng.Next(size)] = (byte)rng.Next(1, 256);
        return b;
      }),
      ("HighEntropy", size => {
        // Nearly uniform distribution
        var b = new byte[size];
        for (var i = 0; i < size; i++) b[i] = (byte)(i * 7 + 13);
        return b;
      }),
      ("RepeatingBlock_16", size => {
        var block = new byte[16];
        new Random(33).NextBytes(block);
        var b = new byte[size];
        for (var i = 0; i < size; i++) b[i] = block[i % 16];
        return b;
      }),
      ("RepeatingBlock_256", size => {
        var block = new byte[256];
        new Random(44).NextBytes(block);
        var b = new byte[size];
        for (var i = 0; i < size; i++) b[i] = block[i % 256];
        return b;
      }),
    };

    foreach (var block in blocks)
      foreach (var (name, generator) in patterns)
        yield return new TestCaseData(block, name, generator)
          .SetName($"{block.DisplayName} / {name}");
  }

  [TestCaseSource(nameof(AllBlockPatterns))]
  [CancelAfter(30_000)]
  public void RoundTrip_1KB(IBuildingBlock block, string pattern, Func<int, byte[]> generator) {
    var data = generator(SmallSize);
    var sw = Stopwatch.StartNew();
    var compressed = block.Compress(data);
    var decompressed = block.Decompress(compressed);
    sw.Stop();

    Assert.That(decompressed, Is.EqualTo(data),
      $"{block.DisplayName}/{pattern}: round-trip mismatch at 1 KB");

    if (sw.ElapsedMilliseconds > 1000)
      Assert.Warn($"{block.DisplayName}/{pattern}: {sw.ElapsedMilliseconds}ms for 1 KB — needs optimization");

    TestContext.Out.WriteLine(
      $"{block.DisplayName}/{pattern}: {sw.ElapsedMilliseconds}ms, " +
      $"ratio={compressed.Length * 100.0 / data.Length:F1}%");
  }

  [TestCaseSource(nameof(AllBlockPatterns))]
  [CancelAfter(30_000)]
  public void RoundTrip_64KB(IBuildingBlock block, string pattern, Func<int, byte[]> generator) {
    var data = generator(MediumSize);
    var sw = Stopwatch.StartNew();
    var compressed = block.Compress(data);
    var decompressed = block.Decompress(compressed);
    sw.Stop();

    Assert.That(decompressed, Is.EqualTo(data),
      $"{block.DisplayName}/{pattern}: round-trip mismatch at 64 KB");

    TestContext.Out.WriteLine(
      $"{block.DisplayName}/{pattern}: compress={sw.ElapsedMilliseconds}ms, " +
      $"size={compressed.Length}, ratio={compressed.Length * 100.0 / data.Length:F1}%");
  }

  [TestCaseSource(nameof(AllBlockPatterns))]
  [CancelAfter(60_000)]
  public void RoundTrip_256KB(IBuildingBlock block, string pattern, Func<int, byte[]> generator) {
    var data = generator(LargeSize);
    var sw = Stopwatch.StartNew();
    var compressed = block.Compress(data);
    var decompressed = block.Decompress(compressed);
    sw.Stop();

    Assert.That(decompressed, Is.EqualTo(data),
      $"{block.DisplayName}/{pattern}: round-trip mismatch at 256 KB");

    TestContext.Out.WriteLine(
      $"{block.DisplayName}/{pattern}: compress={sw.ElapsedMilliseconds}ms, " +
      $"size={compressed.Length}, ratio={compressed.Length * 100.0 / data.Length:F1}%");
  }
}
