using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using FileFormat.Lzham;
using Win32Exception = System.ComponentModel.Win32Exception;

namespace Compression.Tests.Lzham;

/// <summary>
/// The <c>.lzham</c> format and <c>BB_LZHAM</c> against LZHAM's own lzhamtest. The fixtures are files
/// lzhamtest wrote at every compression level, several dictionary sizes and table update rates
/// (see ReferenceVectors/README.md). Where lzhamtest is configured (<c>CWB_LZHAMTEST</c>) it also
/// decodes what we write. Our parse is not LZHAM's, so our bytes are not lzhamtest's.
/// </summary>
[TestFixture]
public sealed class LzhamReferenceTests {

  private static readonly byte[][] Words =
    [.. "the quick brown fox jumps over the lazy dog lzham huffman arithmetic context".Split(' ').Select(System.Text.Encoding.ASCII.GetBytes)];

  private static readonly byte[][] Lines =
    [.. Enumerable.Range(0, 24).Select(i => System.Text.Encoding.ASCII.GetBytes(
      $"line {i:00}: the quick brown fox jumps over the lazy dog while lzham keeps its tables adaptive\n"))];

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
    _ when fixture.StartsWith("text-20k-", StringComparison.Ordinal) => Text(20000, 1),
    _ when fixture.StartsWith("repeats-300k-", StringComparison.Ordinal) => Repeats(300000, 3),
    _ when fixture.StartsWith("repeats-60k-", StringComparison.Ordinal) => Repeats(60000, 4),
    _ when fixture.StartsWith("zeros-200k-", StringComparison.Ordinal) => new byte[200000],
    _ when fixture.StartsWith("noise-3000-", StringComparison.Ordinal) => Noise(3000, 5),
    _ when fixture.StartsWith("text-50k-", StringComparison.Ordinal) => Text(50000, 6),
    _ when fixture.StartsWith("one-byte-", StringComparison.Ordinal) => "A"u8.ToArray(),
    _ when fixture.StartsWith("empty-", StringComparison.Ordinal) => [],
    _ => throw new ArgumentOutOfRangeException(nameof(fixture)),
  };

  private static byte[] Resource(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"LzhamVectors.{name}")
      ?? throw new InvalidOperationException($"Missing resource LzhamVectors.{name}");
    using var copy = new MemoryStream();
    stream.CopyTo(copy);
    return copy.ToArray();
  }

  public static IEnumerable<TestCaseData> Fixtures() {
    var manifest = System.Text.Encoding.ASCII.GetString(Resource("manifest.tsv"));
    foreach (var line in manifest.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
      var fields = line.Split('\t');
      yield return new TestCaseData(fields[0], int.Parse(fields[1], CultureInfo.InvariantCulture), fields[2], int.Parse(fields[4], CultureInfo.InvariantCulture))
        .SetName($"GivenLzhamtestWrote_{fields[0]}_WhenDecoded_ThenItIsTheInput");
    }
  }

  [TestCaseSource(nameof(Fixtures)), Category("HappyPath")]
  public void LzhamtestFileDecodes(string file, int length, string sha256, int tableUpdateRate) {
    var decoded = Decompress(Resource(file), tableUpdateRate);
    Assert.Multiple(() => {
      Assert.That(decoded, Has.Length.EqualTo(length));
      Assert.That(Convert.ToHexStringLower(SHA256.HashData(decoded)), Is.EqualTo(sha256));
      Assert.That(decoded, Is.EqualTo(Input(Path.GetFileNameWithoutExtension(file))), "generator and fixture script disagree");
    });
  }

  [Test, Category("ExceptionalCase")]
  public void GivenTheWrongTableUpdateRate_WhenDecoded_ThenItIsRefusedNotMisread()
    => Assert.That(() => Decompress(Resource("text-20k-m4-d20-h1.lzham"), 8), Throws.InstanceOf<InvalidDataException>());

  [Test, Category("ExceptionalCase")]
  public void GivenAFlippedByte_WhenDecoded_ThenItIsRefused() {
    var file = Resource("text-20k-m4-d20.lzham");
    file[file.Length / 2] ^= 0x20;
    Assert.That(() => Decompress(file, 8), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("ExceptionalCase")]
  public void GivenAnotherId_WhenDecoded_ThenItIsRefused()
    => Assert.Throws<InvalidDataException>(() => Decompress("LZHM\x14\0\0\0\0\0\0\0\0"u8.ToArray(), 8));

  public static IEnumerable<TestCaseData> OurInputs() {
    yield return new TestCaseData(Array.Empty<byte>(), 0).SetName("GivenNoBytes");
    yield return new TestCaseData("A"u8.ToArray(), 0).SetName("GivenOneByte");
    yield return new TestCaseData(Text(70_000, 11), 0).SetName("GivenText");
    yield return new TestCaseData(Repeats(700_000, 12), 15).SetName("GivenManySmallBlocks");
    yield return new TestCaseData(Noise(90_000, 13), 0).SetName("GivenNoise_ThenRawBlocks");
    yield return new TestCaseData(new byte[300_000], 20).SetName("GivenZeros_ThenHugeMatches");
    yield return new TestCaseData(Repeats(1_200_000, 14), 22).SetName("GivenSeveralFullBlocks");
  }

  [TestCaseSource(nameof(OurInputs)), Category("HappyPath"), Category("RoundTrip")]
  public void OurFilesRoundTrip(byte[] data, int dictSizeLog2)
    => Assert.That(Decompress(LzhamStream.CompressFile(data, dictSizeLog2, 8), 8), Is.EqualTo(data));

  [Test, Category("HappyPath")]
  public void GivenTheBuildingBlock_WhenItRoundTrips_ThenItWritesTheLzh0Layout() {
    var block = new LzhamBuildingBlock();
    var data = Text(5000, 21);
    var compressed = block.Compress(data);
    Assert.Multiple(() => {
      Assert.That(compressed.AsSpan(0, 4).ToArray(), Is.EqualTo("LZH0"u8.ToArray()));
      Assert.That(compressed[4], Is.EqualTo(15));
      Assert.That(block.Decompress(compressed), Is.EqualTo(data));
    });
  }

  // ── lzhamtest ───────────────────────────────────────────────────────────────

  private static readonly Lazy<string?> Tool = new(() => {
    var configured = Environment.GetEnvironmentVariable("CWB_LZHAMTEST");
    if (string.IsNullOrEmpty(configured)) return null;
    try {
      using var process = Process.Start(new ProcessStartInfo(configured) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
      var banner = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
      process.WaitForExit();
      return banner.Contains("LZHAM Codec", StringComparison.Ordinal) ? configured : null;
    } catch (Win32Exception) {
      return null;
    }
  });

  private static void RunLzhamtest(params string[] arguments) {
    var tool = Tool.Value;
    if (tool is null) Assert.Ignore("lzhamtest not configured (set CWB_LZHAMTEST to LZHAM's lzhamtest built with static libraries).");
    var start = new ProcessStartInfo(tool!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    var stderr = process.StandardError.ReadToEndAsync();
    var stdout = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    Assert.That(process.ExitCode, Is.Zero, $"lzhamtest {string.Join(' ', arguments)} failed:\n{stdout}\n{stderr.Result}");
  }

  [TestCaseSource(nameof(OurInputs)), Category("ArchiveExternal"), Category("HappyPath")]
  public void LzhamtestReadsOurFiles(byte[] data, int dictSizeLog2) {
    var directory = Path.Combine(Path.GetTempPath(), "cwb-lzham-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try {
      var ours = Path.Combine(directory, "ours.lzham");
      File.WriteAllBytes(ours, LzhamStream.CompressFile(data, dictSizeLog2, 8));
      var decoded = Path.Combine(directory, "ours.out");
      RunLzhamtest("d", ours, decoded);
      Assert.That(File.ReadAllBytes(decoded), Is.EqualTo(data), "lzhamtest decoded our file to other bytes");
    } finally {
      Directory.Delete(directory, recursive: true);
    }
  }

  [TestCase("-m0"), TestCase("-m1"), TestCase("-m2"), TestCase("-m3"), TestCase("-m4"), TestCase("-m4 -x -e")]
  [Category("ArchiveExternal"), Category("HappyPath")]
  public void WeReadLzhamtestsFilesAtEveryLevel(string options) {
    var directory = Path.Combine(Path.GetTempPath(), "cwb-lzham-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try {
      var data = Repeats(400_000, 31).Concat(Noise(20_000, 32)).Concat(Text(300_000, 33)).ToArray();
      var input = Path.Combine(directory, "in");
      File.WriteAllBytes(input, data);
      var theirs = Path.Combine(directory, "theirs.lzham");
      RunLzhamtest([.. options.Split(' '), "-d22", "c", input, theirs]);
      Assert.That(Decompress(File.ReadAllBytes(theirs), 8), Is.EqualTo(data));
    } finally {
      Directory.Delete(directory, recursive: true);
    }
  }

  private static byte[] Decompress(byte[] file, int tableUpdateRate) {
    using var input = new MemoryStream(file, writable: false);
    using var output = new MemoryStream();
    LzhamStream.Decompress(input, output, tableUpdateRate);
    return output.ToArray();
  }
}
