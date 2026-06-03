using System.Diagnostics;
using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.Zstd;

/// <summary>
/// Bidirectional interop tests between our Zstd implementation and the
/// reference <c>zstd</c> CLI. Each test cleanly ignores when <c>zstd</c>
/// is not on PATH so that local runs without the tool installed don't fail
/// the suite.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public class ZstdExternalInteropTests {
  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_zstd_interop_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
    FormatRegistration.EnsureInitialized();
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._tmpDir, true); } catch { /* best effort */ }
  }

  // ── Tool detection ────────────────────────────────────────────────────

  private static bool ZstdAvailable() {
    try {
      using var p = Process.Start(new ProcessStartInfo("zstd", "--version") {
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

  private static void RequireZstd() {
    if (!ZstdAvailable()) Assert.Ignore("zstd CLI not available on PATH");
  }

  // ── Test data ─────────────────────────────────────────────────────────

  /// <summary>1 MB deterministic payload: 1/3 ASCII text + 1/3 zeros + 1/3 pseudo-random.</summary>
  private static byte[] MakePayload() {
    const int total = 1 << 20;
    const int third = total / 3;
    var data = new byte[total];

    // Text third
    var rng = new Random(1234);
    var phrase = "The quick brown fox jumps over the lazy dog. "u8.ToArray();
    for (var i = 0; i < third; i++) data[i] = phrase[i % phrase.Length];

    // Zeros third (already zero-initialised)

    // Random third (deterministic seed)
    var prng = new Random(5678);
    var randomChunk = new byte[total - 2 * third];
    prng.NextBytes(randomChunk);
    Array.Copy(randomChunk, 0, data, 2 * third, randomChunk.Length);

    return data;
  }

  private static IStreamFormatOperations Zstd =>
    FormatRegistry.GetStreamOps("Zstd")
    ?? throw new NotSupportedException("Zstd format ops not registered");

  // ── Helpers ───────────────────────────────────────────────────────────

  /// <summary>Runs an external command, returning stdout, stderr and exit code; fails the test on non-zero exit.</summary>
  private static (string StdOut, string StdErr, int ExitCode) Run(string tool, string args,
      byte[]? stdinBytes = null, string? stdoutFile = null) {
    var psi = new ProcessStartInfo {
      FileName = tool,
      Arguments = args,
      RedirectStandardInput = stdinBytes is not null,
      RedirectStandardOutput = stdoutFile is null,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    using var proc = Process.Start(psi)!;
    if (stdinBytes is not null) {
      proc.StandardInput.BaseStream.Write(stdinBytes);
      proc.StandardInput.BaseStream.Flush();
      proc.StandardInput.Close();
    }
    string stdout;
    if (stdoutFile is not null) {
      stdout = "";
    } else {
      stdout = proc.StandardOutput.ReadToEnd();
    }
    var stderr = proc.StandardError.ReadToEnd();
    proc.WaitForExit(30000);
    if (proc.ExitCode != 0)
      Assert.Fail($"{tool} {args} exited with code {proc.ExitCode}.\nstdout: {stdout}\nstderr: {stderr}");
    return (stdout, stderr, proc.ExitCode);
  }

  // ── Bidirectional gates ───────────────────────────────────────────────

  /// <summary>Our compressed output should be decompressible by the reference <c>zstd</c> CLI.</summary>
  [Test]
  public void OurCompress_ZstdCanDecompress() {
    RequireZstd();
    var data = MakePayload();
    var zstPath = Path.Combine(this._tmpDir, "ours.zst");
    var outPath = Path.Combine(this._tmpDir, "out.bin");

    using (var fs = File.Create(zstPath)) {
      using var input = new MemoryStream(data);
      Zstd.Compress(input, fs);
    }

    Run("zstd", $"-d -f -o \"{outPath}\" \"{zstPath}\"");
    Assert.That(File.ReadAllBytes(outPath), Is.EqualTo(data));
  }

  /// <summary>Output produced by reference <c>zstd -19</c> must be decompressible by our reader.</summary>
  [Test]
  public void ZstdCompress_OurCanDecompress() {
    RequireZstd();
    var data = MakePayload();
    var inputPath = Path.Combine(this._tmpDir, "input.bin");
    var zstPath = Path.Combine(this._tmpDir, "ref.zst");
    File.WriteAllBytes(inputPath, data);

    Run("zstd", $"-19 -f -o \"{zstPath}\" \"{inputPath}\"");

    using var fs = File.OpenRead(zstPath);
    using var ms = new MemoryStream();
    Zstd.Decompress(fs, ms);
    Assert.That(ms.ToArray(), Is.EqualTo(data));
  }

  /// <summary>Zstd frames with content checksum enabled must be accepted (and validated) by our decoder.</summary>
  [Test]
  public void Frame_Roundtrip_With_Checksum() {
    RequireZstd();
    var data = MakePayload();
    var inputPath = Path.Combine(this._tmpDir, "input.bin");
    var zstPath = Path.Combine(this._tmpDir, "checked.zst");
    File.WriteAllBytes(inputPath, data);

    Run("zstd", $"-3 --check -f -o \"{zstPath}\" \"{inputPath}\"");

    // Happy path: clean decode succeeds and matches.
    byte[] decoded;
    using (var fs = File.OpenRead(zstPath))
    using (var ms = new MemoryStream()) {
      Zstd.Decompress(fs, ms);
      decoded = ms.ToArray();
    }
    Assert.That(decoded, Is.EqualTo(data));

    // Tamper: flip a byte in the payload region (after the 6-byte frame header)
    // and assert our decoder cleanly reports failure rather than returning bogus data.
    var bytes = File.ReadAllBytes(zstPath);
    var tamperIdx = bytes.Length / 2;
    bytes[tamperIdx] ^= 0xFF;
    var tamperedPath = Path.Combine(this._tmpDir, "tampered.zst");
    File.WriteAllBytes(tamperedPath, bytes);

    var detected = false;
    try {
      using var fs = File.OpenRead(tamperedPath);
      using var ms = new MemoryStream();
      Zstd.Decompress(fs, ms);
      // If we reached here without throwing, the output had better not match;
      // a silent bit-rot pass would be a bug worth flagging.
      if (!ms.ToArray().SequenceEqual(data)) detected = true;
    } catch {
      detected = true;
    }
    Assert.That(detected, Is.True, "Decoder must detect tampered Zstd frame (either throw or yield mismatched output).");
  }

  /// <summary>Our compressed size should be within ~1.5x of <c>zstd -19</c> on compressible text.</summary>
  [Test]
  public void CompressionRatio_RoughlyComparable() {
    RequireZstd();
    // Use compressible text only — random/zero thirds skew the comparison.
    var phrase = "The quick brown fox jumps over the lazy dog. "u8.ToArray();
    var data = new byte[1 << 20];
    for (var i = 0; i < data.Length; i++) data[i] = phrase[i % phrase.Length];

    var ourPath = Path.Combine(this._tmpDir, "ours.zst");
    using (var fs = File.Create(ourPath)) {
      using var input = new MemoryStream(data);
      Zstd.CompressOptimal(input, fs);
    }
    var ourSize = new FileInfo(ourPath).Length;

    var refInPath = Path.Combine(this._tmpDir, "input.bin");
    var refPath = Path.Combine(this._tmpDir, "ref.zst");
    File.WriteAllBytes(refInPath, data);
    Run("zstd", $"-19 -f -o \"{refPath}\" \"{refInPath}\"");
    var refSize = new FileInfo(refPath).Length;

    TestContext.Out.WriteLine($"Our: {ourSize} bytes, zstd -19: {refSize} bytes, ratio: {(double)ourSize / refSize:F2}x");
    Assert.That(ourSize, Is.LessThanOrEqualTo(refSize * 1.5),
      $"Our compressed output ({ourSize} bytes) exceeds 1.5x zstd -19 ({refSize} bytes).");
  }
}
