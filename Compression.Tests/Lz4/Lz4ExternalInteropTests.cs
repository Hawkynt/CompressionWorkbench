using System.Diagnostics;
using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.Lz4;

/// <summary>
/// Bidirectional interop tests between our LZ4 frame implementation and the
/// reference <c>lz4</c> CLI. Each test cleanly ignores when <c>lz4</c> is not
/// on PATH so that local runs without the tool installed don't fail the suite.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public class Lz4ExternalInteropTests {
  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_lz4_interop_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
    FormatRegistration.EnsureInitialized();
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._tmpDir, true); } catch { /* best effort */ }
  }

  // ── Tool detection ────────────────────────────────────────────────────

  private static bool Lz4Available() {
    try {
      using var p = Process.Start(new ProcessStartInfo("lz4", "--version") {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
      });
      if (p is null) return false;
      p.WaitForExit(2000);
      return p.ExitCode == 0;
    } catch { return false; }
  }

  private static void RequireLz4() {
    if (!Lz4Available()) Assert.Ignore("lz4 CLI not available on PATH");
  }

  // ── Test data ─────────────────────────────────────────────────────────

  /// <summary>1 MB deterministic payload: 1/3 ASCII text + 1/3 zeros + 1/3 pseudo-random.</summary>
  private static byte[] MakePayload() {
    const int total = 1 << 20;
    const int third = total / 3;
    var data = new byte[total];

    var phrase = "The quick brown fox jumps over the lazy dog. "u8.ToArray();
    for (var i = 0; i < third; i++) data[i] = phrase[i % phrase.Length];
    // Zeros third left untouched.
    var prng = new Random(5678);
    var randomChunk = new byte[total - 2 * third];
    prng.NextBytes(randomChunk);
    Array.Copy(randomChunk, 0, data, 2 * third, randomChunk.Length);

    return data;
  }

  private static IStreamFormatOperations Lz4 =>
    FormatRegistry.GetStreamOps("Lz4")
    ?? throw new NotSupportedException("Lz4 format ops not registered");

  // ── Helpers ───────────────────────────────────────────────────────────

  private static (string StdOut, string StdErr, int ExitCode) Run(string tool, string args) {
    var psi = new ProcessStartInfo {
      FileName = tool,
      Arguments = args,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    using var proc = Process.Start(psi)!;
    var stdout = proc.StandardOutput.ReadToEnd();
    var stderr = proc.StandardError.ReadToEnd();
    proc.WaitForExit(30000);
    if (proc.ExitCode != 0)
      Assert.Fail($"{tool} {args} exited with code {proc.ExitCode}.\nstdout: {stdout}\nstderr: {stderr}");
    return (stdout, stderr, proc.ExitCode);
  }

  // ── Bidirectional gates ───────────────────────────────────────────────

  /// <summary>Our compressed output should be decompressible by the reference <c>lz4</c> CLI.</summary>
  [Test]
  public void OurCompress_Lz4CanDecompress() {
    RequireLz4();
    var data = MakePayload();
    var lz4Path = Path.Combine(this._tmpDir, "ours.lz4");
    var outPath = Path.Combine(this._tmpDir, "out.bin");

    using (var fs = File.Create(lz4Path)) {
      using var input = new MemoryStream(data);
      Lz4.Compress(input, fs);
    }

    Run("lz4", $"-d -f \"{lz4Path}\" \"{outPath}\"");
    Assert.That(File.ReadAllBytes(outPath), Is.EqualTo(data));
  }

  /// <summary>Output produced by reference <c>lz4 -9</c> must be decompressible by our reader.</summary>
  [Test]
  public void Lz4Compress_OurCanDecompress() {
    RequireLz4();
    var data = MakePayload();
    var inputPath = Path.Combine(this._tmpDir, "input.bin");
    var lz4Path = Path.Combine(this._tmpDir, "ref.lz4");
    File.WriteAllBytes(inputPath, data);

    Run("lz4", $"-9 -f \"{inputPath}\" \"{lz4Path}\"");

    using var fs = File.OpenRead(lz4Path);
    using var ms = new MemoryStream();
    Lz4.Decompress(fs, ms);
    Assert.That(ms.ToArray(), Is.EqualTo(data));
  }

  /// <summary>Frames produced with a non-default block size (64KB) must be readable — exercises the BD byte parsing.</summary>
  [Test]
  public void FrameFormat_BlockSize_Compatibility() {
    RequireLz4();
    var data = MakePayload();
    var inputPath = Path.Combine(this._tmpDir, "input.bin");
    var lz4Path = Path.Combine(this._tmpDir, "bs64k.lz4");
    File.WriteAllBytes(inputPath, data);

    // --block-size=4 selects max 64 KB blocks (LZ4 frame spec block-size IDs: 4=64KB, 5=256KB, 6=1MB, 7=4MB).
    Run("lz4", $"-f -B4 \"{inputPath}\" \"{lz4Path}\"");

    using var fs = File.OpenRead(lz4Path);
    using var ms = new MemoryStream();
    Lz4.Decompress(fs, ms);
    Assert.That(ms.ToArray(), Is.EqualTo(data));
  }

  /// <summary>Our compressed size should be within ~1.5x of <c>lz4 -9</c> on compressible text.</summary>
  [Test]
  public void CompressionRatio_RoughlyComparable() {
    RequireLz4();
    var phrase = "The quick brown fox jumps over the lazy dog. "u8.ToArray();
    var data = new byte[1 << 20];
    for (var i = 0; i < data.Length; i++) data[i] = phrase[i % phrase.Length];

    var ourPath = Path.Combine(this._tmpDir, "ours.lz4");
    using (var fs = File.Create(ourPath)) {
      using var input = new MemoryStream(data);
      Lz4.CompressOptimal(input, fs);
    }
    var ourSize = new FileInfo(ourPath).Length;

    var refInPath = Path.Combine(this._tmpDir, "input.bin");
    var refPath = Path.Combine(this._tmpDir, "ref.lz4");
    File.WriteAllBytes(refInPath, data);
    Run("lz4", $"-9 -f \"{refInPath}\" \"{refPath}\"");
    var refSize = new FileInfo(refPath).Length;

    TestContext.Out.WriteLine($"Our: {ourSize} bytes, lz4 -9: {refSize} bytes, ratio: {(double)ourSize / refSize:F2}x");
    Assert.That(ourSize, Is.LessThanOrEqualTo(refSize * 1.5),
      $"Our compressed output ({ourSize} bytes) exceeds 1.5x lz4 -9 ({refSize} bytes).");
  }
}
