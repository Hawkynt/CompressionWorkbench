using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using FileFormat.Ctw;
using Win32Exception = System.ComponentModel.Win32Exception;

namespace Compression.Tests.Ctw;

/// <summary>
/// The <c>.ctw</c> format and <c>BB_CTW</c> against the CTW 0.1 reference utility. The fixtures are
/// files the reference Win32 binary wrote (see ReferenceVectors/README.md); their inputs come from
/// the generators below. Where the reference is configured (<c>CWB_CTW</c>) it also reads what we
/// write and we compare bytes with what it writes.
/// </summary>
[TestFixture]
public sealed class CtwReferenceTests {

  private static readonly byte[][] Words =
    [.. "the quick brown fox jumps over the lazy dog context tree weighting estimator".Split(' ').Select(System.Text.Encoding.ASCII.GetBytes)];

  private static int Next(int x) => (int)((x * 1103515245L + 12345) & 0x7fffffff);

  private static byte[] Text(int length, int seed) {
    var output = new List<byte>(length + 16);
    var x = seed;
    while (output.Count < length) {
      x = Next(x);
      output.AddRange(Words[(x >> 16) % Words.Length]);
      output.Add((byte)' ');
    }
    return [.. output.Take(length)];
  }

  private static byte[] Noise(int length, int seed) {
    var output = new byte[length];
    var x = seed;
    for (var i = 0; i < length; ++i) {
      x = Next(x);
      output[i] = (byte)(x >> 16);
    }
    return output;
  }

  private static byte[] Runs(int length) => [.. Enumerable.Range(0, length).Select(i => (byte)(i / 300 % 7))];

  private static byte[] Input(string fixture) => fixture switch {
    _ when fixture.StartsWith("text-20k", StringComparison.Ordinal) => Text(20000, 1),
    "runs-60k" => Runs(60000),
    "noise-3000" => Noise(3000, 2),
    "short-5" => "hello"u8.ToArray(),
    "one-byte" => "A"u8.ToArray(),
    "empty" => [],
    _ => throw new ArgumentOutOfRangeException(nameof(fixture)),
  };

  private static byte[] Resource(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"CtwVectors.{name}")
      ?? throw new InvalidOperationException($"Missing resource CtwVectors.{name}");
    using var copy = new MemoryStream();
    stream.CopyTo(copy);
    return copy.ToArray();
  }

  public static IEnumerable<TestCaseData> Fixtures() {
    var manifest = System.Text.Encoding.ASCII.GetString(Resource("manifest.tsv"));
    foreach (var line in manifest.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
      var fields = line.Split('\t');
      yield return new TestCaseData(fields[0], int.Parse(fields[1], CultureInfo.InvariantCulture), fields[2])
        .SetName($"GivenCtwWrote_{fields[0]}_WhenDecodedAndReencoded_ThenBothMatch");
    }
  }

  [TestCaseSource(nameof(Fixtures)), Category("HappyPath")]
  public void CtwFileDecodes_AndOurEncoderWritesTheSameBytes(string file, int length, string sha256) {
    var ctw = Resource(file);
    var decoded = CtwStream.Decode(ctw);
    var (settings, _) = CtwStream.ReadHeader(ctw);
    Assert.Multiple(() => {
      Assert.That(decoded, Has.Length.EqualTo(length));
      Assert.That(Convert.ToHexStringLower(SHA256.HashData(decoded)), Is.EqualTo(sha256));
      Assert.That(decoded, Is.EqualTo(Input(Path.GetFileNameWithoutExtension(file))), "generator and fixture script disagree");
      Assert.That(CtwStream.Encode(decoded, settings), Is.EqualTo(ctw));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenTheDefaultFile_WhenTheBuildingBlockCompressesItsInput_ThenItIsThatFile() {
    var block = new CtwBuildingBlock();
    var file = Resource("text-20k.ctw");
    Assert.Multiple(() => {
      Assert.That(block.Compress(Text(20000, 1)), Is.EqualTo(file));
      Assert.That(block.Decompress(file), Is.EqualTo(Text(20000, 1)));
    });
  }

  [Test, Category("ExceptionalCase")]
  public void GivenDepthTwelve_WhenDecoded_ThenItIsRefused() {
    var file = Resource("text-20k.ctw");
    file[8] = 12;
    Assert.Throws<NotSupportedException>(() => CtwStream.Decode(file));
  }

  [TestCase(0), TestCase(11)]
  [Category("ExceptionalCase")]
  public void GivenATruncatedOrForeignHeader_WhenDecoded_ThenItIsRefused(int length)
    => Assert.That(() => CtwStream.Decode(new byte[length]), Throws.InstanceOf<InvalidDataException>().Or.InstanceOf<NotSupportedException>());

  [Test, Category("ExceptionalCase")]
  public void GivenAnotherVersion_WhenDecoded_ThenItIsRefused() {
    var file = Resource("text-20k.ctw");
    file[2] = 1;
    Assert.Throws<InvalidDataException>(() => CtwStream.Decode(file));
  }

  // ── the reference utility ─────────────────────────────────────────────────

  private static readonly Lazy<string?> Tool = new(() => {
    var configured = Environment.GetEnvironmentVariable("CWB_CTW");
    if (string.IsNullOrEmpty(configured)) return null;
    try {
      using var process = Process.Start(new ProcessStartInfo(configured) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
      var banner = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
      process.WaitForExit();
      return banner.Contains("CTW (Context Tree Weighting)", StringComparison.Ordinal) ? configured : null;
    } catch (Win32Exception) {
      return null;
    }
  });

  private static void RunCtw(params string[] arguments) {
    var tool = Tool.Value;
    if (tool is null) Assert.Ignore("CTW 0.1 not configured (set CWB_CTW to the reference ctw.exe).");
    var start = new ProcessStartInfo(tool!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    var stderr = process.StandardError.ReadToEndAsync();
    process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    Assert.That(process.ExitCode, Is.Zero, $"ctw {string.Join(' ', arguments)} failed:\n{stderr.Result}");
  }

  public static IEnumerable<TestCaseData> LiveInputs() {
    yield return new TestCaseData(Text(150_000, 11)).SetName("GivenText_WhenCtwReadsOurFile_ThenItIsTheInputAndCtwsBytes");
    yield return new TestCaseData(Noise(40_000, 12)).SetName("GivenNoise_WhenCtwReadsOurFile_ThenItIsTheInputAndCtwsBytes");
    yield return new TestCaseData(Runs(200_000)).SetName("GivenRuns_WhenCtwReadsOurFile_ThenItIsTheInputAndCtwsBytes");
    yield return new TestCaseData(Text(700, 13)).SetName("GivenSmallText_WhenCtwReadsOurFile_ThenItIsTheInputAndCtwsBytes");
  }

  [TestCaseSource(nameof(LiveInputs)), Category("ArchiveExternal"), Category("HappyPath")]
  public void CtwReadsOurFilesAndWritesTheSameBytes(byte[] data) {
    var directory = Path.Combine(Path.GetTempPath(), "cwb-ctw-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try {
      var ours = Path.Combine(directory, "ours.ctw");
      File.WriteAllBytes(ours, CtwStream.Encode(data, new CtwSettings()));
      var decoded = Path.Combine(directory, "ours.out");
      RunCtw("d", "-y", ours, decoded);
      Assert.That(File.ReadAllBytes(decoded), Is.EqualTo(data), "ctw decoded our file to other bytes");

      var input = Path.Combine(directory, "in");
      File.WriteAllBytes(input, data);
      var theirs = Path.Combine(directory, "theirs.ctw");
      RunCtw("e", "-y", input, theirs);
      Assert.Multiple(() => {
        Assert.That(CtwStream.Decode(File.ReadAllBytes(theirs)), Is.EqualTo(data), "we decoded ctw's file to other bytes");
        Assert.That(File.ReadAllBytes(ours), Is.EqualTo(File.ReadAllBytes(theirs)), "our bytes differ from ctw's");
      });
    } finally {
      Directory.Delete(directory, recursive: true);
    }
  }
}
