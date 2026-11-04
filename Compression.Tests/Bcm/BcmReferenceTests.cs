using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using Compression.Registry;
using FileFormat.Bcm;
using Win32Exception = System.ComponentModel.Win32Exception;

namespace Compression.Tests.Bcm;

/// <summary>
/// The <c>.bcm</c> format and the <c>BB_Bcm</c> block against Ilya Muravyov's bcm. The fixtures
/// are files bcm 1.65 wrote, identical to what the official 1.10 and 1.60 binaries write (see
/// ReferenceVectors/README.md); their inputs come from the generators below, which the fixture
/// script mirrors. Where a bcm 1.x binary is configured (<c>CWB_BCM</c>) it also reads what we write.
/// </summary>
[TestFixture]
public sealed class BcmReferenceTests {

  private static readonly byte[][] Words =
    [.. "the quick brown fox jumps over the lazy dog block sorting compressor bcm context mixing".Split(' ').Select(System.Text.Encoding.ASCII.GetBytes)];

  private static readonly byte[][] Lines =
    [.. Enumerable.Range(0, 24).Select(i => System.Text.Encoding.ASCII.GetBytes(
      $"line {i:00}: the quick brown fox jumps over the lazy dog while the block sorter keeps every context\n"))];

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

  private static byte[] Repeats(int length, int seed) {
    var output = new List<byte>(length + 128);
    var x = seed;
    while (output.Count < length) {
      x = Next(x);
      output.AddRange(Lines[(x >> 16) % Lines.Length]);
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

  private static byte[] Input(string fixture) => fixture switch {
    "text-20k" => Text(20000, 1),
    "text-100k" => Text(100000, 2),
    "repeats-2500k-b1" => Repeats(2500000, 3),
    "noise-3000" => Noise(3000, 4),
    "tiny-hello" => "hello"u8.ToArray(),
    "one-byte" => "A"u8.ToArray(),
    "empty" => [],
    _ => throw new ArgumentOutOfRangeException(nameof(fixture)),
  };

  private static byte[] Resource(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"BcmVectors.{name}")
      ?? throw new InvalidOperationException($"Missing resource BcmVectors.{name}");
    using var copy = new MemoryStream();
    stream.CopyTo(copy);
    return copy.ToArray();
  }

  public static IEnumerable<TestCaseData> Fixtures() {
    var manifest = System.Text.Encoding.ASCII.GetString(Resource("manifest.tsv"));
    foreach (var line in manifest.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
      var fields = line.Split('\t');
      yield return new TestCaseData(fields[0], int.Parse(fields[1], CultureInfo.InvariantCulture), fields[2], fields[3])
        .SetName($"GivenBcmWrote_{fields[0]}_WhenDecodedAndReencoded_ThenBothMatch");
    }
  }

  [TestCaseSource(nameof(Fixtures)), Category("HappyPath")]
  public void BcmFileDecodes_AndOurEncoderWritesTheSameBytes(string file, int length, string sha256, string options) {
    var bcm = Resource(file);
    var name = Path.GetFileNameWithoutExtension(file);
    var decoded = Decompress(bcm);
    var blockSize = options == "-b1" ? "1" : "16";
    Assert.Multiple(() => {
      Assert.That(decoded, Has.Length.EqualTo(length));
      Assert.That(Convert.ToHexStringLower(SHA256.HashData(decoded)), Is.EqualTo(sha256));
      Assert.That(decoded, Is.EqualTo(Input(name)), "generator and fixture script disagree");
      Assert.That(Compress(decoded, blockSize), Is.EqualTo(bcm));
    });
  }

  [TestCase("text-20k"), TestCase("text-100k"), TestCase("noise-3000"), TestCase("tiny-hello"), TestCase("one-byte"), TestCase("empty")]
  [Category("HappyPath")]
  public void GivenADefaultFile_WhenTheBuildingBlockCompressesItsInput_ThenItIsTheFileWithoutItsId(string name) {
    var expected = Resource(name + ".bcm").AsSpan(4).ToArray();
    var block = new BcmBuildingBlock();
    Assert.Multiple(() => {
      Assert.That(block.Compress(Input(name)), Is.EqualTo(expected));
      Assert.That(block.Decompress(expected), Is.EqualTo(Input(name)));
    });
  }

  [Test, Category("ExceptionalCase")]
  public void GivenABcm2File_WhenDecoded_ThenItIsRefusedAsUnsupported()
    => Assert.Throws<NotSupportedException>(() => Decompress(Resource("bcm2-text-5000.bcm")));

  [Test, Category("ExceptionalCase")]
  public void GivenAFlippedBit_WhenDecoded_ThenTheCrcRefusesIt() {
    var bcm = Resource("text-20k.bcm");
    bcm[bcm.Length / 2] ^= 0x04;
    Assert.That(() => Decompress(bcm), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("ExceptionalCase")]
  public void GivenAnotherId_WhenDecoded_ThenItIsRefused()
    => Assert.Throws<InvalidDataException>(() => Decompress("BCMX\0\0\0\0"u8.ToArray()));

  [Test, Category("BoundaryCase")]
  public void GivenAnExactMultipleOfTheBlockSize_WhenRoundTripped_ThenItIsWhole() {
    var data = Repeats(2 << 20, 9);
    Assert.That(Decompress(Compress(data, "1")), Is.EqualTo(data));
  }

  // ── bcm itself ─────────────────────────────────────────────────────────────

  private static readonly Lazy<string?> Tool = new(() => {
    var configured = Environment.GetEnvironmentVariable("CWB_BCM");
    if (string.IsNullOrEmpty(configured)) return null;
    try {
      using var process = Process.Start(new ProcessStartInfo(configured) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
      var banner = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
      process.WaitForExit();
      return banner.Contains("BWT-based file compressor, v1.", StringComparison.Ordinal) ? configured : null;
    } catch (Win32Exception) {
      return null;
    }
  });

  private static void RunBcm(params string[] arguments) {
    var tool = Tool.Value;
    if (tool is null) Assert.Ignore("bcm 1.x not configured (set CWB_BCM to a bcm 1.10..1.65 binary).");
    var start = new ProcessStartInfo(tool!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    var stderr = process.StandardError.ReadToEndAsync();
    process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    Assert.That(process.ExitCode, Is.Zero, $"bcm {string.Join(' ', arguments)} failed:\n{stderr.Result}");
  }

  public static IEnumerable<TestCaseData> LiveInputs() {
    yield return new TestCaseData(Text(300_000, 11)).SetName("GivenText_WhenBcmReadsOurFile_ThenItIsTheInputAndBcmsBytes");
    yield return new TestCaseData(Noise(200_000, 12)).SetName("GivenNoise_WhenBcmReadsOurFile_ThenItIsTheInputAndBcmsBytes");
    yield return new TestCaseData(new byte[1_000_000]).SetName("GivenZeros_WhenBcmReadsOurFile_ThenItIsTheInputAndBcmsBytes");
    yield return new TestCaseData(Enumerable.Range(0, 70_000).Select(i => (byte)(i * 7 ^ i >> 5)).ToArray()).SetName("GivenAPattern_WhenBcmReadsOurFile_ThenItIsTheInputAndBcmsBytes");
  }

  [TestCaseSource(nameof(LiveInputs)), Category("ArchiveExternal"), Category("HappyPath")]
  public void BcmReadsOurFilesAndWritesTheSameBytes(byte[] data) {
    var directory = Path.Combine(Path.GetTempPath(), "cwb-bcm-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try {
      var ours = Path.Combine(directory, "ours.bcm");
      File.WriteAllBytes(ours, Compress(data, "16"));
      var decoded = Path.Combine(directory, "ours.out");
      RunBcm("-d", "-f", ours, decoded);
      Assert.That(File.ReadAllBytes(decoded), Is.EqualTo(data), "bcm decoded our file to other bytes");

      var input = Path.Combine(directory, "in");
      File.WriteAllBytes(input, data);
      var theirs = Path.Combine(directory, "theirs.bcm");
      RunBcm("-f", input, theirs);
      Assert.Multiple(() => {
        Assert.That(Decompress(File.ReadAllBytes(theirs)), Is.EqualTo(data), "we decoded bcm's file to other bytes");
        Assert.That(File.ReadAllBytes(ours), Is.EqualTo(File.ReadAllBytes(theirs)), "our bytes differ from bcm's");
      });
    } finally {
      Directory.Delete(directory, recursive: true);
    }
  }

  private static byte[] Compress(byte[] data, string blockSizeMiB) {
    var options = new FormatCreateOptions();
    options.FormatSpecific["BlockSize"] = blockSizeMiB;
    using var input = new MemoryStream(data, writable: false);
    using var output = new MemoryStream();
    new BcmFormatDescriptor().Compress(input, output, options);
    return output.ToArray();
  }

  private static byte[] Decompress(byte[] compressed) {
    using var input = new MemoryStream(compressed, writable: false);
    using var output = new MemoryStream();
    new BcmFormatDescriptor().Decompress(input, output);
    return output.ToArray();
  }
}
