using System.Diagnostics;
using Compression.Core.Checksums;
using Compression.Core.Simd;
using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.BenchmarkMatrix;

/// <summary>
/// Performance benchmarks for key operations. Marked <see cref="ExplicitAttribute"/> so they
/// only run when manually selected, not during CI.
/// </summary>
[TestFixture]
[Explicit]
public class PerformanceBenchmarks {

  private byte[] _data1MB = null!;
  private byte[] _data10MB = null!;

  [OneTimeSetUp]
  public void Init() {
    FormatRegistration.EnsureInitialized();

    // Create repeating text data (compressible)
    var lorem = System.Text.Encoding.UTF8.GetBytes(
      "The quick brown fox jumps over the lazy dog. Lorem ipsum dolor sit amet, consectetur adipiscing elit. ");

    _data1MB = new byte[1024 * 1024];
    for (var i = 0; i < _data1MB.Length; i++)
      _data1MB[i] = lorem[i % lorem.Length];

    _data10MB = new byte[10 * 1024 * 1024];
    for (var i = 0; i < _data10MB.Length; i++)
      _data10MB[i] = lorem[i % lorem.Length];
  }

  // --- CRC32 Throughput ---

  [Test]
  public void CRC32_IEEE_Throughput_1MB() {
    // Warm up
    Crc32.Compute(_data1MB);

    var sw = Stopwatch.StartNew();
    const int iterations = 100;
    for (var i = 0; i < iterations; i++)
      Crc32.Compute(_data1MB);
    sw.Stop();

    var totalMB = iterations * _data1MB.Length / (1024.0 * 1024.0);
    var throughput = totalMB / sw.Elapsed.TotalSeconds;
    TestContext.Out.WriteLine($"CRC32 IEEE: {throughput:F1} MB/s ({iterations} x 1MB in {sw.Elapsed.TotalMilliseconds:F1}ms)");
    Assert.Pass($"CRC32 IEEE throughput: {throughput:F1} MB/s");
  }

  [Test]
  public void CRC32_Castagnoli_Throughput_1MB() {
    // Warm up
    Crc32.Compute(_data1MB, Crc32.Castagnoli);

    var sw = Stopwatch.StartNew();
    const int iterations = 100;
    for (var i = 0; i < iterations; i++)
      Crc32.Compute(_data1MB, Crc32.Castagnoli);
    sw.Stop();

    var totalMB = iterations * _data1MB.Length / (1024.0 * 1024.0);
    var throughput = totalMB / sw.Elapsed.TotalSeconds;
    TestContext.Out.WriteLine($"CRC32C: {throughput:F1} MB/s ({iterations} x 1MB in {sw.Elapsed.TotalMilliseconds:F1}ms)");
    Assert.Pass($"CRC32C throughput: {throughput:F1} MB/s");
  }

  // --- SIMD Match Length vs. Scalar ---

  [Test]
  public void SimdMatchLength_Throughput_1MB() {
    // Two identical 1MB buffers — worst case for match scanning (full match every time)
    var a = _data1MB;
    var b = (byte[])_data1MB.Clone();

    // Warm up
    SimdMatchLength.GetMatchLength((ReadOnlySpan<byte>)a, b, a.Length);

    var sw = Stopwatch.StartNew();
    const int iterations = 50;
    for (var i = 0; i < iterations; i++)
      SimdMatchLength.GetMatchLength((ReadOnlySpan<byte>)a, b, a.Length);
    sw.Stop();

    var totalMB = iterations * a.Length / (1024.0 * 1024.0);
    var throughput = totalMB / sw.Elapsed.TotalSeconds;
    TestContext.Out.WriteLine($"SimdMatchLength: {throughput:F1} MB/s ({iterations} x 1MB in {sw.Elapsed.TotalMilliseconds:F1}ms)");
    Assert.Pass($"SimdMatchLength throughput: {throughput:F1} MB/s");
  }

  // --- SIMD Histogram vs. Scalar ---

  [Test]
  public void SimdHistogram_Throughput_1MB() {
    // Warm up
    SimdHistogram.ComputeHistogram(_data1MB);

    var sw = Stopwatch.StartNew();
    const int iterations = 100;
    for (var i = 0; i < iterations; i++)
      SimdHistogram.ComputeHistogram(_data1MB);
    sw.Stop();

    var totalMB = iterations * _data1MB.Length / (1024.0 * 1024.0);
    var throughput = totalMB / sw.Elapsed.TotalSeconds;
    TestContext.Out.WriteLine($"SimdHistogram: {throughput:F1} MB/s ({iterations} x 1MB in {sw.Elapsed.TotalMilliseconds:F1}ms)");
    Assert.Pass($"SimdHistogram throughput: {throughput:F1} MB/s");
  }

  // --- Building Block Compress/Decompress at various sizes ---

  private static readonly string[] TopBlockIds = ["BB_Deflate", "BB_Lz4", "BB_Snappy", "BB_Brotli", "BB_Lzma"];

  private static IEnumerable<TestCaseData> TopBlocks_1MB() {
    FormatRegistration.EnsureInitialized();
    foreach (var id in TopBlockIds) {
      var block = BuildingBlockRegistry.GetById(id);
      if (block != null)
        yield return new TestCaseData(block).SetName($"1MB_{block.DisplayName}");
    }
  }

  private static IEnumerable<TestCaseData> TopBlocks_10MB() {
    FormatRegistration.EnsureInitialized();
    foreach (var id in TopBlockIds) {
      var block = BuildingBlockRegistry.GetById(id);
      if (block != null)
        yield return new TestCaseData(block).SetName($"10MB_{block.DisplayName}");
    }
  }

  [TestCaseSource(nameof(TopBlocks_1MB))]
  [CancelAfter(120000)]
  public void BuildingBlock_CompressDecompress_1MB(IBuildingBlock block) {
    // Warm up
    var warmUp = block.Compress(_data1MB.AsSpan(0, 4096));
    block.Decompress(warmUp);

    var sw = Stopwatch.StartNew();
    var compressed = block.Compress(_data1MB);
    var compressMs = sw.Elapsed.TotalMilliseconds;

    sw.Restart();
    var decompressed = block.Decompress(compressed);
    var decompressMs = sw.Elapsed.TotalMilliseconds;

    var ratio = (double)compressed.Length / _data1MB.Length;
    TestContext.Out.WriteLine(
      $"{block.DisplayName} 1MB: compress={compressMs:F1}ms ({_data1MB.Length / 1024.0 / compressMs * 1000:F1} KB/s), " +
      $"decompress={decompressMs:F1}ms ({_data1MB.Length / 1024.0 / decompressMs * 1000:F1} KB/s), " +
      $"ratio={ratio:P2}");

    Assert.That(decompressed, Is.EqualTo(_data1MB), $"{block.DisplayName} round-trip failed on 1MB");
  }

  [TestCaseSource(nameof(TopBlocks_10MB))]
  [CancelAfter(300000)]
  public void BuildingBlock_CompressDecompress_10MB(IBuildingBlock block) {
    // Warm up
    var warmUp = block.Compress(_data1MB.AsSpan(0, 4096));
    block.Decompress(warmUp);

    var sw = Stopwatch.StartNew();
    var compressed = block.Compress(_data10MB);
    var compressMs = sw.Elapsed.TotalMilliseconds;

    sw.Restart();
    var decompressed = block.Decompress(compressed);
    var decompressMs = sw.Elapsed.TotalMilliseconds;

    var ratio = (double)compressed.Length / _data10MB.Length;
    TestContext.Out.WriteLine(
      $"{block.DisplayName} 10MB: compress={compressMs:F1}ms ({_data10MB.Length / 1024.0 / 1024.0 / (compressMs / 1000.0):F1} MB/s), " +
      $"decompress={decompressMs:F1}ms ({_data10MB.Length / 1024.0 / 1024.0 / (decompressMs / 1000.0):F1} MB/s), " +
      $"ratio={ratio:P2}");

    Assert.That(decompressed, Is.EqualTo(_data10MB), $"{block.DisplayName} round-trip failed on 10MB");
  }

  // --- Memory allocation tracking ---

  [Test]
  public void MemoryAllocation_CRC32_1MB() {
    // Warm up
    Crc32.Compute(_data1MB);

    var before = GC.GetTotalAllocatedBytes(precise: true);
    const int iterations = 10;
    for (var i = 0; i < iterations; i++)
      Crc32.Compute(_data1MB);
    var after = GC.GetTotalAllocatedBytes(precise: true);

    var allocPerIteration = (after - before) / (double)iterations;
    TestContext.Out.WriteLine($"CRC32 IEEE: {allocPerIteration:F0} bytes allocated per 1MB compute");
    Assert.Pass($"CRC32 allocation: {allocPerIteration:F0} bytes/call");
  }

  [Test]
  public void MemoryAllocation_Histogram_1MB() {
    // Warm up
    SimdHistogram.ComputeHistogram(_data1MB);

    var before = GC.GetTotalAllocatedBytes(precise: true);
    const int iterations = 10;
    for (var i = 0; i < iterations; i++)
      SimdHistogram.ComputeHistogram(_data1MB);
    var after = GC.GetTotalAllocatedBytes(precise: true);

    var allocPerIteration = (after - before) / (double)iterations;
    TestContext.Out.WriteLine($"SimdHistogram: {allocPerIteration:F0} bytes allocated per 1MB compute");
    Assert.Pass($"Histogram allocation: {allocPerIteration:F0} bytes/call");
  }

  [TestCaseSource(nameof(TopBlocks_1MB))]
  [CancelAfter(120000)]
  public void MemoryAllocation_BuildingBlock_1MB(IBuildingBlock block) {
    // Warm up
    var compressed = block.Compress(_data1MB);
    block.Decompress(compressed);

    // Measure compress allocation
    var before = GC.GetTotalAllocatedBytes(precise: true);
    compressed = block.Compress(_data1MB);
    var afterCompress = GC.GetTotalAllocatedBytes(precise: true);

    // Measure decompress allocation
    block.Decompress(compressed);
    var afterDecompress = GC.GetTotalAllocatedBytes(precise: true);

    var compressAlloc = afterCompress - before;
    var decompressAlloc = afterDecompress - afterCompress;
    TestContext.Out.WriteLine(
      $"{block.DisplayName}: compress alloc={compressAlloc:N0} bytes, decompress alloc={decompressAlloc:N0} bytes " +
      $"(compressed size={compressed.Length:N0}, ratio={compressed.Length / (double)_data1MB.Length:P2})");
    Assert.Pass($"{block.DisplayName} alloc: compress={compressAlloc:N0}, decompress={decompressAlloc:N0}");
  }

  // --- Large file benchmark (100MB) ---

  [Test]
  [CancelAfter(600000)]
  public void CRC32_IEEE_Throughput_100MB() {
    var data100MB = new byte[100 * 1024 * 1024];
    var lorem = System.Text.Encoding.UTF8.GetBytes(
      "The quick brown fox jumps over the lazy dog. Lorem ipsum dolor sit amet. ");
    for (var i = 0; i < data100MB.Length; i++)
      data100MB[i] = lorem[i % lorem.Length];

    // Warm up
    Crc32.Compute(data100MB.AsSpan(0, 1024 * 1024));

    var sw = Stopwatch.StartNew();
    const int iterations = 10;
    for (var i = 0; i < iterations; i++)
      Crc32.Compute(data100MB);
    sw.Stop();

    var totalMB = iterations * data100MB.Length / (1024.0 * 1024.0);
    var throughput = totalMB / sw.Elapsed.TotalSeconds;
    TestContext.Out.WriteLine($"CRC32 IEEE 100MB: {throughput:F1} MB/s ({iterations} iterations in {sw.Elapsed.TotalSeconds:F2}s)");
    Assert.Pass($"CRC32 IEEE 100MB throughput: {throughput:F1} MB/s");
  }
}
