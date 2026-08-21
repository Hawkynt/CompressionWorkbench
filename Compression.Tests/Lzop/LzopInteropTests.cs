#pragma warning disable CS1591
using System.Diagnostics;
using Compression.Registry;

namespace Compression.Tests.Lzop;

/// <summary>
/// What we call LZO1X has to be LZO1X: lzop must read ours and we must read its.
/// </summary>
/// <remarks>
/// <para>It was not. What stood here decoded a private encoding carrying the LZO1X
/// name — its own doc comment said it "mirrors the token format produced by
/// Lzo1xCompressor" — so lzop rejected our files with "Compressed data violation"
/// and we rejected lzop's with "invalid back-reference distance". A <c>.lzo</c>
/// this project wrote was not a <c>.lzo</c>.</para>
///
/// <para>Every test of it was a round trip through our own encoder and decoder,
/// which a private format passes perfectly. Two halves of the same invention agree
/// with each other and with nothing else, and only the other implementation can
/// say so. That is what this asks.</para>
/// </remarks>
[TestFixture]
public class LzopInteropTests {

  private static string? Lzop() {
    foreach (var dir in new[] { "/usr/bin", "/bin", "/usr/local/bin" }) {
      var path = Path.Combine(dir, "lzop");
      if (File.Exists(path)) return path;
    }
    return null;
  }

  /// <summary>Inputs that exercise the format's awkward corners.</summary>
  private static IEnumerable<TestCaseData> Payloads() {
    yield return new TestCaseData(Array.Empty<byte>()).SetName("empty");
    yield return new TestCaseData(new byte[] { 42 }).SetName("one byte");
    yield return new TestCaseData(new byte[] { 1, 2, 3 }).SetName("three bytes, too few for a run");
    yield return new TestCaseData(Ramp(4)).SetName("four bytes, the shortest run");
    yield return new TestCaseData(Ramp(17)).SetName("seventeen bytes");
    yield return new TestCaseData(Ramp(18)).SetName("eighteen, where the run length extends");
    yield return new TestCaseData(Ramp(19)).SetName("nineteen");
    yield return new TestCaseData(new byte[5000]).SetName("five thousand zeros");
    yield return new TestCaseData(Repeating(20_000)).SetName("repetitive, so matches everywhere");
    yield return new TestCaseData(Ramp(200_000)).SetName("past a single hash window");
    yield return new TestCaseData(Random(70_000, 5)).SetName("incompressible");
  }

  private static byte[] Ramp(int n) {
    var d = new byte[n];
    for (var i = 0; i < n; ++i) d[i] = (byte)(i * 31 + 7);
    return d;
  }

  private static byte[] Repeating(int n) {
    var d = new byte[n];
    for (var i = 0; i < n; ++i) d[i] = (byte)"abcabcabd"[i % 9];
    return d;
  }

  private static byte[] Random(int n, int seed) {
    var rng = new Random(seed);
    var d = new byte[n];
    rng.NextBytes(d);
    return d;
  }

  [TestCaseSource(nameof(Payloads)), Category("Interop")]
  public void LzopReadsWhatWeWrite(byte[] payload) {
    var lzop = Lzop();
    if (lzop == null) Assert.Ignore("lzop is not installed; nothing to compare against.");

    var work = NewDir();
    try {
      var ours = Path.Combine(work, "ours.lzo");
      using (var input = new MemoryStream(payload))
      using (var output = File.Create(ours))
        StreamOps().Compress(input, output);

      var (exit, stdout, stderr) = Run(lzop!, $"-d -c \"{ours}\"");
      Assert.That(exit, Is.EqualTo(0),
        $"lzop would not read a file we wrote: {stderr.Split('\n').FirstOrDefault()}");
      Assert.That(stdout, Is.EqualTo(payload).AsCollection,
        "lzop read our file and got different bytes out");
    } finally {
      Cleanup(work);
    }
  }

  [TestCaseSource(nameof(Payloads)), Category("Interop")]
  public void WeReadWhatLzopWrites(byte[] payload) {
    var lzop = Lzop();
    if (lzop == null) Assert.Ignore("lzop is not installed; nothing to compare against.");

    var work = NewDir();
    try {
      var raw = Path.Combine(work, "raw.bin");
      File.WriteAllBytes(raw, payload);

      // lzop -1 searches for matches one way and -7 upward another; all three put
      // ordinary LZO1X on the wire, each under its own method byte, and one
      // decoder reads all of them.
      foreach (var level in new[] { 1, 6, 9 }) {
        var theirs = Path.Combine(work, $"theirs{level}.lzo");
        var (exit, _, stderr) = Run(lzop!, $"-q -{level} -f \"{raw}\" -o \"{theirs}\"");
        if (exit != 0) {
          Assert.Ignore($"lzop would not compress this at -{level}: {stderr}");
          return;
        }

        using var input = File.OpenRead(theirs);
        using var output = new MemoryStream();
        StreamOps().Decompress(input, output);
        Assert.That(output.ToArray(), Is.EqualTo(payload).AsCollection,
          $"we read a file lzop wrote at -{level} and got different bytes out");
      }
    } finally {
      Cleanup(work);
    }
  }

  private static IStreamFormatOperations StreamOps() =>
    FormatRegistry.GetStreamOps("Lzop") ?? throw new NotSupportedException("no Lzop stream ops");

  private static string NewDir() {
    var dir = Path.Combine(Path.GetTempPath(), "cwb_lzop_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);
    return dir;
  }

  private static void Cleanup(string dir) {
    try { Directory.Delete(dir, true); } catch { /* best effort */ }
  }

  private static (int Exit, byte[] StdOut, string StdErr) Run(string tool, string arguments) {
    var start = new ProcessStartInfo(tool, arguments) {
      RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
    };
    using var process = Process.Start(start)!;
    using var captured = new MemoryStream();
    process.StandardOutput.BaseStream.CopyTo(captured);
    var stderr = process.StandardError.ReadToEnd();
    process.WaitForExit(120_000);
    return (process.HasExited ? process.ExitCode : -1, captured.ToArray(), stderr);
  }
}
