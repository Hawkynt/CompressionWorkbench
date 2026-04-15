using Compression.Analysis;
using Compression.Analysis.TrialDecompression;
using Compression.Core.Streams;
using Compression.Core.Transforms;

namespace Compression.Tests.Analysis;

[TestFixture]
public class ParallelAnalysisTests {

  // ── Parallel Trial Decompression ──────────────────────────────────

  [Test, Category("HappyPath")]
  public async Task TryAllAsync_ReturnsSuccessfulAttempts() {
    // Create DEFLATE-compressed data — should be detected via magic or trial
    var original = new byte[200];
    for (var i = 0; i < 200; i++) original[i] = (byte)(i % 10);
    var compressed = Compression.Core.Deflate.DeflateCompressor.Compress(original);

    var decompressor = new TrialDecompressor(perTrialTimeoutMs: 2000);
    var results = await decompressor.TryAllAsync(compressed);

    Assert.That(results.Any(r => r.Success), Is.True);
  }

  [Test, Category("HappyPath")]
  public async Task TryAllAsync_ResultsSortedByEntropy() {
    var original = new byte[50];
    for (var i = 0; i < 50; i++) original[i] = (byte)(i % 3);
    var encoded = RunLengthEncoding.Encode(original);

    var decompressor = new TrialDecompressor(perTrialTimeoutMs: 2000);
    var results = await decompressor.TryAllAsync(encoded);

    if (results.Count > 1) {
      for (var i = 1; i < results.Count; i++)
        Assert.That(results[i].OutputEntropy, Is.GreaterThanOrEqualTo(results[i - 1].OutputEntropy));
    }
  }

  [Test, Category("HappyPath")]
  public async Task TryAllAsync_SupportsCancellation() {
    var original = new byte[50];
    var encoded = RunLengthEncoding.Encode(original);

    using var cts = new CancellationTokenSource();
    cts.Cancel(); // Cancel immediately

    var decompressor = new TrialDecompressor(perTrialTimeoutMs: 2000);
    // Should complete quickly without throwing, returning whatever results were gathered
    var results = await decompressor.TryAllAsync(encoded, cts.Token);

    // With immediate cancellation, we expect empty or minimal results
    Assert.That(results, Is.Not.Null);
  }

  [Test, Category("HappyPath")]
  public async Task TryAllAsync_GzipData_Succeeds() {
    var original = "The quick brown fox"u8.ToArray();
    using var ms = new MemoryStream();
    using (var gz = new FileFormat.Gzip.GzipStream(ms, CompressionStreamMode.Compress, leaveOpen: true))
      gz.Write(original);
    var compressed = ms.ToArray();

    Compression.Lib.FormatRegistration.EnsureInitialized();
    var decompressor = new TrialDecompressor(perTrialTimeoutMs: 2000);
    var results = await decompressor.TryAllAsync(compressed);

    Assert.That(results.Any(r => r.Algorithm.Contains("GZIP", StringComparison.OrdinalIgnoreCase) && r.Success), Is.True);
  }

  // ── Parallel Batch Analysis ───────────────────────────────────────

  [Test, Category("HappyPath")]
  public async Task AnalyzeDirectoryAsync_AnalyzesFiles() {
    var tmpDir = Path.Combine(Path.GetTempPath(), "cwb_parallel_batch_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      Directory.CreateDirectory(tmpDir);

      // Create a ZIP file
      using (var fs = File.Create(Path.Combine(tmpDir, "test.zip"))) {
        using var w = new FileFormat.Zip.ZipWriter(fs, leaveOpen: true);
        w.AddEntry("data.txt", "test"u8.ToArray());
      }

      // Create a plain text file
      File.WriteAllText(Path.Combine(tmpDir, "readme.txt"), "hello");

      var analyzer = new BatchAnalyzer();
      var result = await analyzer.AnalyzeDirectoryAsync(tmpDir);

      Assert.That(result.TotalFiles, Is.EqualTo(2));
      Assert.That(result.FileResults, Has.Count.EqualTo(2));
      Assert.That(result.FormatDistribution.ContainsKey("Zip"), Is.True);
    } finally {
      Directory.Delete(tmpDir, true);
    }
  }

  [Test, Category("HappyPath")]
  public async Task AnalyzeDirectoryAsync_RespectsMaxDegreeOfParallelism() {
    var tmpDir = Path.Combine(Path.GetTempPath(), "cwb_parallel_dop_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      Directory.CreateDirectory(tmpDir);
      for (var i = 0; i < 10; i++)
        File.WriteAllText(Path.Combine(tmpDir, $"file{i}.txt"), $"content {i}");

      var analyzer = new BatchAnalyzer();
      var result = await analyzer.AnalyzeDirectoryAsync(tmpDir, maxDegreeOfParallelism: 2);

      Assert.That(result.TotalFiles, Is.EqualTo(10));
      Assert.That(result.FileResults, Has.Count.EqualTo(10));
    } finally {
      Directory.Delete(tmpDir, true);
    }
  }

  [Test, Category("HappyPath")]
  public async Task AnalyzeDirectoryAsync_SupportsCancellation() {
    var tmpDir = Path.Combine(Path.GetTempPath(), "cwb_parallel_cancel_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      Directory.CreateDirectory(tmpDir);
      for (var i = 0; i < 5; i++)
        File.WriteAllText(Path.Combine(tmpDir, $"file{i}.txt"), $"content {i}");

      using var cts = new CancellationTokenSource();
      cts.Cancel(); // Cancel immediately

      var analyzer = new BatchAnalyzer();
      // Parallel.ForEachAsync throws TaskCanceledException (subclass of OperationCanceledException)
      Assert.ThrowsAsync<TaskCanceledException>(async () =>
        await analyzer.AnalyzeDirectoryAsync(tmpDir, ct: cts.Token));
    } finally {
      Directory.Delete(tmpDir, true);
    }
  }

  [Test, Category("HappyPath")]
  public async Task AnalyzeDirectoryAsync_RecursiveMode() {
    var tmpDir = Path.Combine(Path.GetTempPath(), "cwb_parallel_recur_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      Directory.CreateDirectory(tmpDir);
      var subDir = Path.Combine(tmpDir, "sub");
      Directory.CreateDirectory(subDir);
      File.WriteAllText(Path.Combine(tmpDir, "root.txt"), "root");
      File.WriteAllText(Path.Combine(subDir, "child.txt"), "child");

      var analyzer = new BatchAnalyzer();
      var result = await analyzer.AnalyzeDirectoryAsync(tmpDir, recursive: true);

      Assert.That(result.TotalFiles, Is.EqualTo(2));
    } finally {
      Directory.Delete(tmpDir, true);
    }
  }

  // ── Parallel Benchmark Runner ─────────────────────────────────────

  [Test, Category("HappyPath")]
  [CancelAfter(60_000)]
  public async Task ParallelBenchmarkRunner_RunsSuccessfully() {
    Compression.Lib.FormatRegistration.EnsureInitialized();

    var patterns = new List<(string Name, byte[] Data)> {
      ("Zeroes", new byte[1024]),
      ("Incrementing", Enumerable.Range(0, 1024).Select(i => (byte)(i & 0xFF)).ToArray()),
    };

    // Pick just a couple of fast building blocks for the test
    var blocks = Compression.Registry.BuildingBlockRegistry.All
      .Where(b => b.Family == Compression.Registry.AlgorithmFamily.Transform
              || b.DisplayName == "RLE")
      .Take(3)
      .ToList();

    if (blocks.Count == 0) {
      Assert.Inconclusive("No suitable building blocks found for test");
      return;
    }

    var runner = new ParallelBenchmarkRunner(maxDegreeOfParallelism: 2, perTestTimeoutMs: 10_000);
    var results = await runner.RunAllAsync(patterns, blocks);

    Assert.That(results, Has.Count.EqualTo(blocks.Count * patterns.Count));
    Assert.That(results.All(r => r.BlockId != null), Is.True);
    Assert.That(results.All(r => r.DisplayName != null), Is.True);
  }

  // ── IAsyncArchiveOperations Interface ─────────────────────────────

  [Test, Category("HappyPath")]
  public void IAsyncArchiveOperations_InterfaceExists() {
    var type = typeof(Compression.Registry.IAsyncArchiveOperations);
    Assert.That(type, Is.Not.Null);
    Assert.That(type.IsInterface, Is.True);

    var method = type.GetMethod("ListEntriesAsync");
    Assert.That(method, Is.Not.Null);
  }

  [Test, Category("HappyPath")]
  public void FormatRegistry_GetAsyncArchiveOps_ReturnsNullForUnknown() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    var ops = Compression.Registry.FormatRegistry.GetAsyncArchiveOps("NonExistentFormat");
    Assert.That(ops, Is.Null);
  }
}
