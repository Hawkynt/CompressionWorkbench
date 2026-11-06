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
/// (see ReferenceVectors/README.md): we decode them, and with the same parameters we write the same
/// bytes. Where lzhamtest is configured (<c>CWB_LZHAMTEST</c>) it also decodes what we write, and
/// writes the bytes we write for a larger input at every level and several dictionary sizes.
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

  /// <summary>Little-endian 32-bit counters with small random steps: near-repeats that differ in a few bits.</summary>
  private static byte[] Binary(int length, int seed) {
    var output = new byte[length];
    var x = seed;
    uint value = 0;
    for (var i = 0; i + 4 <= length; i += 4) {
      x = Next(x);
      value += (uint)((x >> 16) & 7);
      System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(i), value);
    }
    return output;
  }

  /// <summary>Repeated lines, noise, words and counters back to back: 188 000 bytes.</summary>
  private static byte[] Mixed(int seed)
    => [.. Repeats(60000, seed), .. Noise(8000, seed + 1), .. Text(60000, seed + 2), .. Binary(60000, seed + 3)];

  private static byte[] Input(string fixture) => fixture switch {
    _ when fixture.StartsWith("mixed-188k-", StringComparison.Ordinal) => Mixed(7),
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

  public static IEnumerable<TestCaseData> FixturesToWrite() {
    var manifest = System.Text.Encoding.ASCII.GetString(Resource("manifest.tsv"));
    foreach (var line in manifest.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
      var fields = line.Split('\t');
      yield return new TestCaseData(fields[0], fields[3])
        .SetName($"GivenLzhamtestsParameters_{fields[0]}_WhenWeCompress_ThenTheBytesAreLzhamtests");
    }
  }

  /// <summary>Parses lzhamtest's <c>-m# -d# -h# -x</c> into our parameters.</summary>
  private static (int DictSizeLog2, int TableUpdateRate, LzhamCompressionLevel Level, bool Extreme) Options(string options) {
    int dict = 28, rate = 8;
    var level = LzhamCompressionLevel.Uber;
    var extreme = false;
    foreach (var option in options.Split(' ', StringSplitOptions.RemoveEmptyEntries)) {
      var value = option.Length > 2 ? int.Parse(option[2..], CultureInfo.InvariantCulture) : 0;
      switch (option[1]) {
        case 'm': level = (LzhamCompressionLevel)value; break;
        case 'd': dict = value; break;
        case 'h': rate = value; break;
        case 'x': extreme = true; break;
        case 'e': break;
        default: throw new ArgumentOutOfRangeException(nameof(options), option, "not an lzhamtest compression option");
      }
    }
    return (dict, rate, level, extreme);
  }

  private static byte[] CompressLikeLzhamtest(byte[] data, string options) {
    var (dict, rate, level, extreme) = Options(options);
    return LzhamStream.CompressFile(data, dict, rate, level, extreme);
  }

  [TestCaseSource(nameof(FixturesToWrite)), Category("HappyPath")]
  public void WeWriteLzhamtestsBytes(string file, string options) {
    var ours = CompressLikeLzhamtest(Input(Path.GetFileNameWithoutExtension(file)), options);
    var theirs = Resource(file);
    Assert.That(ours, Is.EqualTo(theirs), $"first difference at byte {ours.Zip(theirs).TakeWhile(p => p.First == p.Second).Count()} of {theirs.Length}");
  }

  [Test, Category("ExceptionalCase")]
  public void GivenALevelOutsideZeroToFour_WhenCompressing_ThenItIsRefused()
    => Assert.That(() => LzhamStream.CompressFile("abc"u8, 20, 8, (LzhamCompressionLevel)5), Throws.InstanceOf<ArgumentOutOfRangeException>());

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

  [TestCase("-m0 -d15"), TestCase("-m1 -d15"), TestCase("-m2 -d15"), TestCase("-m3 -d15"), TestCase("-m4 -d15"), TestCase("-m4 -x -d15")]
  [TestCase("-m0 -d20"), TestCase("-m1 -d20"), TestCase("-m2 -d20"), TestCase("-m3 -d20"), TestCase("-m4 -d20"), TestCase("-m4 -x -d20")]
  [TestCase("-m0 -d26"), TestCase("-m2 -d26"), TestCase("-m4 -d26"), TestCase("-m4 -x -d26")]
  [TestCase("-m4 -d17 -h1"), TestCase("-m2 -d18 -h20"), TestCase("-m3 -x -d16 -h4")]
  [Category("ArchiveExternal"), Category("HappyPath")]
  public void LzhamtestWritesOurBytes(string options) {
    var directory = Path.Combine(Path.GetTempPath(), "cwb-lzham-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try {
      var data = Repeats(400_000, 41).Concat(Noise(20_000, 42)).Concat(Text(300_000, 43)).Concat(Binary(200_000, 44)).Concat(new byte[70_000]).ToArray();
      var input = Path.Combine(directory, "in");
      File.WriteAllBytes(input, data);
      var theirs = Path.Combine(directory, "theirs.lzham");
      RunLzhamtest(["-t0", .. options.Split(' '), "c", input, theirs]);
      var expected = File.ReadAllBytes(theirs);
      var ours = CompressLikeLzhamtest(data, options);
      Assert.That(ours, Is.EqualTo(expected), $"first difference at byte {ours.Zip(expected).TakeWhile(p => p.First == p.Second).Count()} of {expected.Length}");
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
