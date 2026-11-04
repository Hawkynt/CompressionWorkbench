using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using Compression.Registry;
using FileFormat.Bsc;
using Win32Exception = System.ComponentModel.Win32Exception;

namespace Compression.Tests.Bsc;

/// <summary>
/// The <c>.bsc</c> format and the <c>BB_Bsc</c> block against libbsc's own <c>bsc</c> tool. The
/// fixtures are files bsc 3.3.12 wrote (see ReferenceVectors/README.md); their inputs come from
/// the generators below, which the fixture script mirrors. Where a bsc binary is configured
/// (<c>CWB_BSC</c>, or <c>bsc</c> on the PATH) it also reads what we write.
/// </summary>
[TestFixture]
public sealed class BscLibbscReferenceTests {

  // ── deterministic inputs (mirrored by the fixture script) ─────────────────────────

  private static readonly byte[][] Words =
    [.. "the quick brown fox jumps over the lazy dog block sorting compressor libbsc qlfc lzp context".Split(' ').Select(System.Text.Encoding.ASCII.GetBytes)];

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

  private static byte[] Records(int count, int seed) {
    var output = new byte[count * 4];
    var x = seed;
    for (var i = 0; i < count; ++i) {
      x = Next(x);
      System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(i * 4), (uint)(i * 7 + ((x >> 16) & 3)));
    }
    return output;
  }

  private static byte[] Input(string fixture) => fixture switch {
    "text-20k" or "text-20k-e0" or "text-20k-e2" or "text-20k-cp" or "text-20k-nolzp" or "text-20k-m3" or "text-20k-m4" or "text-20k-m5" or "text-20k-m6" => Text(20000, 1),
    "text-100k" => Text(100000, 2),
    "text-300k" => Text(300000, 3),
    "repeats-50k" or "repeats-50k-m6" => Repeats(50000, 7),
    "repeats-300k" => Repeats(300000, 3),
    "records-40k-r" => Records(10000, 5),
    "text-60k-b25000" => Text(60000, 4),
    "noise-3000" or "noise-3000-cp" => Noise(3000, 6),
    "tiny-hello" or "tiny-hello-cp" => "hello"u8.ToArray(),
    "one-byte" => "A"u8.ToArray(),
    _ => throw new ArgumentOutOfRangeException(nameof(fixture)),
  };

  // ── fixtures ──────────────────────────────────────────────────────────────

  private static byte[] Resource(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"BscVectors.{name}")
      ?? throw new InvalidOperationException($"Missing resource BscVectors.{name}");
    using var copy = new MemoryStream();
    stream.CopyTo(copy);
    return copy.ToArray();
  }

  private sealed record Fixture(string File, int Length, string Sha256, string Options);

  private static IEnumerable<Fixture> Fixtures() {
    var manifest = System.Text.Encoding.ASCII.GetString(Resource("manifest.tsv"));
    foreach (var line in manifest.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
      var fields = line.Split('\t');
      yield return new Fixture(fields[0], int.Parse(fields[1], CultureInfo.InvariantCulture), fields[2], fields[3]);
    }
  }

  public static IEnumerable<TestCaseData> DecodableFixtures()
    => Fixtures()
      .Select(f => new TestCaseData(f.File, f.Length, f.Sha256).SetName($"GivenBscWrote_{f.File}_WhenDecoded_ThenItIsTheInput"));

  // Option sets the managed writer reproduces: entropy coder, contexts and block size.
  public static IEnumerable<TestCaseData> ReproducibleFixtures()
    => Fixtures().Where(f => f.Options is "-" or "-e0" or "-e2" or "-cp" or "-b25000")
      .Select(f => new TestCaseData(f.File, f.Options).SetName($"GivenTheInputOf_{f.File}_WhenWeCompressWithTheSameOptions_ThenTheBytesAreBscs"));

  [TestCaseSource(nameof(DecodableFixtures)), Category("HappyPath")]
  public void BscFileDecodes(string file, int length, string sha256) {
    var decoded = Decompress(Resource(file));
    var name = Path.GetFileNameWithoutExtension(file);
    Assert.Multiple(() => {
      Assert.That(decoded, Has.Length.EqualTo(length));
      Assert.That(Convert.ToHexStringLower(SHA256.HashData(decoded)), Is.EqualTo(sha256));
      Assert.That(decoded, Is.EqualTo(Input(name)), "generator and fixture script disagree");
    });
  }

  [TestCaseSource(nameof(ReproducibleFixtures)), Category("HappyPath")]
  public void OurEncoderWritesBscsBytes(string file, string options) {
    var name = Path.GetFileNameWithoutExtension(file);
    var formatOptions = new FormatCreateOptions();
    formatOptions.FormatSpecific["EntropyCoder"] = options switch { "-e0" => "Fast", "-e2" => "Adaptive", _ => "Static" };
    formatOptions.FormatSpecific["SortingContexts"] = options == "-cp" ? "Preceding" : "Following";
    if (options == "-b25000")
      formatOptions.FormatSpecific["BlockSize"] = "25000";

    Assert.That(Compress(Input(name), formatOptions), Is.EqualTo(Resource(file)));
  }

  [TestCase("text-20k"), TestCase("text-100k"), TestCase("text-300k"), TestCase("repeats-50k"), TestCase("repeats-300k")]
  [TestCase("noise-3000"), TestCase("tiny-hello"), TestCase("one-byte")]
  [Category("HappyPath")]
  public void GivenADefaultSingleBlockFile_WhenTheBuildingBlockCompressesItsInput_ThenItIsTheFilesBlock(string name) {
    var file = Resource(name + ".bsc");
    var block = new BscBuildingBlock();
    var expected = file.AsSpan(8 + 10).ToArray(); // "bsc1", block count, then one 10-byte block record
    Assert.Multiple(() => {
      Assert.That(block.Compress(Input(name)), Is.EqualTo(expected));
      Assert.That(block.Decompress(expected), Is.EqualTo(Input(name)));
    });
  }

  [Test, Category("BoundaryCase")]
  public void GivenNoInput_WhenTheBuildingBlockCompresses_ThenItIsAStoredEmptyBlock() {
    var block = new BscBuildingBlock();
    var compressed = block.Compress([]);
    Assert.Multiple(() => {
      Assert.That(compressed, Has.Length.EqualTo(28));
      Assert.That(block.Decompress(compressed), Is.Empty);
    });
  }

  [Test, Category("ExceptionalCase")]
  public void GivenACorruptedPayload_WhenTheBuildingBlockDecompresses_ThenTheChecksumRefusesIt() {
    var block = new BscBuildingBlock();
    var compressed = block.Compress(Text(5000, 9));
    compressed[^5] ^= 0x10;
    Assert.Throws<InvalidDataException>(() => block.Decompress(compressed));
  }

  [Test, Category("ExceptionalCase")]
  public void GivenFewerThanTheHeaderBytes_WhenTheBuildingBlockDecompresses_ThenItIsRefused()
    => Assert.Throws<InvalidDataException>(() => new BscBuildingBlock().Decompress(new byte[27]));

  // ── libbsc's own tool ─────────────────────────────────────────────────────────

  private static readonly Lazy<string?> Tool = new(() => {
    var configured = Environment.GetEnvironmentVariable("CWB_BSC");
    foreach (var candidate in new[] { configured, "bsc" }) {
      if (string.IsNullOrEmpty(candidate)) continue;
      try {
        using var process = Process.Start(new ProcessStartInfo(candidate) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        var banner = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (banner.Contains("Block Sorting Compressor", StringComparison.Ordinal)) return candidate;
      } catch (Win32Exception) {
        // not present
      }
    }
    return null;
  });

  private static void RunBsc(params string[] arguments) {
    var tool = Tool.Value;
    if (tool is null) Assert.Ignore("libbsc's bsc tool not found (set CWB_BSC to a bsc 3.3.x binary).");
    var start = new ProcessStartInfo(tool!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    var stderr = process.StandardError.ReadToEndAsync();
    process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    Assert.That(process.ExitCode, Is.Zero, $"bsc {string.Join(' ', arguments)} failed:\n{stderr.Result}");
  }

  public static IEnumerable<TestCaseData> LiveInputs() {
    yield return new TestCaseData("text", Text(70_000, 11), "-").SetName("GivenText_WhenBscReadsOurFile_ThenItIsTheInput");
    yield return new TestCaseData("repeats", Repeats(600_000, 12), "-").SetName("GivenLzpSubBlocks_WhenBscReadsOurFile_ThenItIsTheInput");
    yield return new TestCaseData("coder-blocks", Text(1_100_000, 18), "-").SetName("GivenQlfcSubBlocks_WhenBscReadsOurFile_ThenItIsTheInput");
    yield return new TestCaseData("lzp-blocks", Repeats(1_100_000, 19), "-").SetName("GivenFourLzpSubBlocks_WhenBscReadsOurFile_ThenItIsTheInput");
    yield return new TestCaseData("noise", Noise(80_000, 13), "-").SetName("GivenNoise_WhenBscReadsOurFile_ThenItIsTheInput");
    yield return new TestCaseData("adaptive", Repeats(90_000, 14), "-e2").SetName("GivenTheAdaptiveCoder_WhenBscReadsOurFile_ThenItIsTheInput");
    yield return new TestCaseData("fast", Text(90_000, 15), "-e0").SetName("GivenTheFastCoder_WhenBscReadsOurFile_ThenItIsTheInput");
    yield return new TestCaseData("preceding", Text(40_000, 16), "-cp").SetName("GivenPrecedingContexts_WhenBscReadsOurFile_ThenItIsTheInput");
    yield return new TestCaseData("blocks", Repeats(50_000, 17), "-b20000").SetName("GivenSeveralBlocks_WhenBscReadsOurFile_ThenItIsTheInput");
  }

  [TestCaseSource(nameof(LiveInputs)), Category("ArchiveExternal"), Category("HappyPath")]
  public void BscReadsOurFilesAndWritesTheSameBytes(string name, byte[] data, string options) {
    var directory = Path.Combine(Path.GetTempPath(), "cwb-bsc-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try {
      var formatOptions = new FormatCreateOptions();
      formatOptions.FormatSpecific["EntropyCoder"] = options switch { "-e0" => "Fast", "-e2" => "Adaptive", _ => "Static" };
      formatOptions.FormatSpecific["SortingContexts"] = options == "-cp" ? "Preceding" : "Following";
      if (options.StartsWith("-b", StringComparison.Ordinal))
        formatOptions.FormatSpecific["BlockSize"] = options[2..];

      var ours = Path.Combine(directory, name + ".ours.bsc");
      File.WriteAllBytes(ours, Compress(data, formatOptions));
      var decoded = Path.Combine(directory, name + ".out");
      RunBsc("d", ours, decoded);
      Assert.That(File.ReadAllBytes(decoded), Is.EqualTo(data), "bsc decoded our file to other bytes");

      var input = Path.Combine(directory, name + ".in");
      File.WriteAllBytes(input, data);
      var theirs = Path.Combine(directory, name + ".theirs.bsc");
      RunBsc(options == "-" ? ["e", input, theirs] : ["e", input, theirs, options]);
      Assert.Multiple(() => {
        Assert.That(Decompress(File.ReadAllBytes(theirs)), Is.EqualTo(data), "we decoded bsc's file to other bytes");
        // Compared against a single-threaded bsc (the one the fixtures used); an OpenMP build may
        // store a sub-block near the incompressible limit that this build codes. Set
        // CWB_BSC_SINGLE_THREADED=0 to compare only the decoded bytes.
        if (Environment.GetEnvironmentVariable("CWB_BSC_SINGLE_THREADED") != "0")
          Assert.That(File.ReadAllBytes(ours), Is.EqualTo(File.ReadAllBytes(theirs)), "our bytes differ from bsc's");
      });
    } finally {
      Directory.Delete(directory, recursive: true);
    }
  }

  public static IEnumerable<TestCaseData> LargeSortTransformInputs() {
    // Blocks of 8 MiB and more take the inverse Sort Transform's second and third reconstruction
    // paths; the third when one byte value fills 8 MiB of the block on its own.
    yield return new TestCaseData(Text(9_000_000, 20), "-m4").SetName("GivenAnEightMebibyteSortTransformBlock_WhenBscWroteIt_ThenWeDecodeIt");
    yield return new TestCaseData(new byte[8_500_000].Concat(Text(600_000, 21)).ToArray(), "-m5").SetName("GivenASortTransformBlockDominatedByOneByte_WhenBscWroteIt_ThenWeDecodeIt");
  }

  [TestCaseSource(nameof(LargeSortTransformInputs)), Category("ArchiveExternal"), Category("BoundaryCase")]
  public void WeReadBscsLargeSortTransformBlocks(byte[] data, string order) {
    var directory = Path.Combine(Path.GetTempPath(), "cwb-bsc-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try {
      var input = Path.Combine(directory, "in");
      File.WriteAllBytes(input, data);
      var theirs = Path.Combine(directory, "theirs.bsc");
      RunBsc("e", input, theirs, order + "p");
      Assert.That(Decompress(File.ReadAllBytes(theirs)), Is.EqualTo(data));
    } finally {
      Directory.Delete(directory, recursive: true);
    }
  }

  private static byte[] Compress(byte[] data, FormatCreateOptions options) {
    using var input = new MemoryStream(data, writable: false);
    using var output = new MemoryStream();
    new BscFormatDescriptor().Compress(input, output, options);
    return output.ToArray();
  }

  private static byte[] Decompress(byte[] compressed) {
    using var input = new MemoryStream(compressed, writable: false);
    using var output = new MemoryStream();
    new BscFormatDescriptor().Decompress(input, output);
    return output.ToArray();
  }
}
