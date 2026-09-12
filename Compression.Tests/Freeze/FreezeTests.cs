using Compression.Lib;
using Compression.Registry;
using FileFormat.Freeze;

namespace Compression.Tests.Freeze;

[TestFixture]
public class FreezeTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SimpleText()
    => Assert.That(RoundTrip("Hello, Freeze compression! This is a test of the Freeze format."u8.ToArray()),
      Is.EqualTo("Hello, Freeze compression! This is a test of the Freeze format."u8.ToArray()));

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_RepetitiveData() {
    var original = new byte[4096];
    for (var i = 0; i < original.Length; i++)
      original[i] = (byte)(i % 7);

    using var input = new MemoryStream(original);
    using var compressed = new MemoryStream();
    FreezeStream.Compress(input, compressed);
    Assert.That(compressed.Length, Is.LessThan(original.Length), "Repetitive data should compress");

    compressed.Position = 0;
    using var output = new MemoryStream();
    FreezeStream.Decompress(compressed, output);
    Assert.That(output.ToArray(), Is.EqualTo(original));
  }

  [TestCase(FreezeCompatibility.Freeze2x)]
  [TestCase(FreezeCompatibility.Freeze1x)]
  [Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_AllCompatibilityTargets(FreezeCompatibility compatibility) {
    var original = Enumerable.Range(0, 24000).Select(static i => (byte)((i * 17 + i / 31) & 0xFF)).ToArray();
    using var input = new MemoryStream(original, writable: false);
    using var compressed = new MemoryStream();
    FreezeStream.Compress(input, compressed, new FreezeCompressionOptions {
      TargetCompatibility = compatibility,
      Parsing = FreezeParsingStrategy.Lazy,
      SearchDepth = 256,
    });

    compressed.Position = 0;
    using var output = new MemoryStream();
    FreezeStream.Decompress(compressed, output);
    Assert.That(output.ToArray(), Is.EqualTo(original));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_AllByteValues() {
    var original = Enumerable.Range(0, 256).Select(static i => (byte)i).ToArray();
    Assert.That(RoundTrip(original), Is.EqualTo(original));
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void RoundTrip_SingleByte() {
    byte[] original = [0x42];
    Assert.That(RoundTrip(original), Is.EqualTo(original));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_LargeData() {
    var rng = new Random(12345);
    var original = new byte[100 * 1024];
    rng.NextBytes(original);
    for (var i = 0; i < 10000; i++)
      original[50000 + i] = (byte)(i % 4);
    Assert.That(RoundTrip(original), Is.EqualTo(original));
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void RoundTrip_EmptyInput() {
    var original = Array.Empty<byte>();
    Assert.That(RoundTrip(original), Is.EqualTo(original));
  }

  [Test, Category("Interoperability")]
  public void DefaultHeader_IsHistoricalFreeze2Header() {
    var compressed = Compress(new FreezeFormatDescriptor(), "header check"u8.ToArray(), new FormatCreateOptions());
    Assert.That(compressed[..5], Is.EqualTo(new byte[] { 0x1F, 0x9F, 0x4A, 0x10, 0x0A }),
      "Freeze 2.x stores magic followed by its three-byte default position-Huffman table; no original-size field exists.");
  }

  [Test, Category("Interoperability")]
  public void Freeze1Header_IsHistoricalTwoByteHeader() {
    var compressed = Compress(new FreezeFormatDescriptor(), "header check"u8.ToArray(), Options(
      (FormatOptionKeys.TargetCompatibility, nameof(FreezeCompatibility.Freeze1x))));
    Assert.That(compressed[..2], Is.EqualTo(new byte[] { 0x1F, 0x9E }));
    Assert.That(compressed[2], Is.Not.EqualTo(0x4A), "Freeze 1.x does not carry Freeze 2.x's three-byte position-table header.");
  }

  private static IEnumerable<TestCaseData> Freeze2ReferenceVectors() {
    // Independently derived from the published Freeze 2.x behavior and validated
    // with a melt-compatible behavioral oracle. The repeated-data vector exercises
    // LZ match length and position coding, not just literals.
    yield return new TestCaseData(Array.Empty<byte>(), "1F9F4A100A8100").SetName("Freeze2_ReferenceVector_Empty");
    yield return new TestCaseData("A"u8.ToArray(), "1F9F4A100A21C080").SetName("Freeze2_ReferenceVector_A");
    yield return new TestCaseData("Hello"u8.ToArray(), "1F9F4A100A2519CDDFE38C08").SetName("Freeze2_ReferenceVector_Hello");
    yield return new TestCaseData("abcabcabcabc"u8.ToArray(), "1F9F4A100A31990CB0901408").SetName("Freeze2_ReferenceVector_LzMatch");
  }

  private static IEnumerable<TestCaseData> Freeze1ReferenceVectors() {
    // Clean-room vectors generated from the documented Freeze 1.x COMPAT decoder
    // parameters: 1F 9E magic, N=4096, F=60, N_CHAR=315, fixed Table1 and six
    // low position bits. The same independent model reproduces every 2.x vector above.
    yield return new TestCaseData(Array.Empty<byte>(), "1F9E8A").SetName("Freeze1_ReferenceVector_Empty");
    yield return new TestCaseData("A"u8.ToArray(), "1F9EE5C500").SetName("Freeze1_ReferenceVector_A");
    yield return new TestCaseData("Hello"u8.ToArray(), "1F9EE97BFED85F98A0").SetName("Freeze1_ReferenceVector_Hello");
    yield return new TestCaseData("abcabcabcabc"u8.ToArray(), "1F9EF5FB3DB22028A0").SetName("Freeze1_ReferenceVector_LzMatch");
  }

  [TestCaseSource(nameof(Freeze2ReferenceVectors)), Category("Interoperability")]
  public void Freeze2GreedyDefaultTable_MatchesReferenceVectors(byte[] original, string expectedHex) {
    var descriptor = new FreezeFormatDescriptor();
    var actual = Compress(descriptor, original, Options(
      (FormatOptionKeys.TargetCompatibility, nameof(FreezeCompatibility.Freeze2x)),
      ("Parsing", nameof(FreezeParsingStrategy.Greedy)),
      ("SearchDepth", FreezeCompressionOptions.DefaultSearchDepth.ToString()),
      ("PositionTable", nameof(FreezePositionTableMode.Default))));
    Assert.That(actual, Is.EqualTo(Convert.FromHexString(expectedHex)));
  }

  [TestCaseSource(nameof(Freeze1ReferenceVectors)), Category("Interoperability")]
  public void Freeze1GreedyFixedTable_MatchesReferenceVectors(byte[] original, string expectedHex) {
    var descriptor = new FreezeFormatDescriptor();
    var actual = Compress(descriptor, original, Options(
      (FormatOptionKeys.TargetCompatibility, nameof(FreezeCompatibility.Freeze1x)),
      ("Parsing", nameof(FreezeParsingStrategy.Greedy)),
      ("SearchDepth", FreezeCompressionOptions.DefaultSearchDepth.ToString()),
      ("PositionTable", nameof(FreezePositionTableMode.Default))));
    Assert.That(actual, Is.EqualTo(Convert.FromHexString(expectedHex)));
  }

  [TestCaseSource(nameof(Freeze2ReferenceVectors)), Category("Interoperability")]
  public void Reader_DecodesFreeze2ReferenceVectors(byte[] expected, string compressedHex) {
    var descriptor = new FreezeFormatDescriptor();
    Assert.That(Decompress(descriptor, Convert.FromHexString(compressedHex)), Is.EqualTo(expected));
  }

  [TestCaseSource(nameof(Freeze1ReferenceVectors)), Category("Interoperability")]
  public void Reader_DecodesFreeze1ReferenceVectors(byte[] expected, string compressedHex) {
    var descriptor = new FreezeFormatDescriptor();
    Assert.That(Decompress(descriptor, Convert.FromHexString(compressedHex)), Is.EqualTo(expected));
  }

  [Test, Category("MalformedInput")]
  public void Decompress_TruncatedFreeze2Header_Throws() {
    using var input = new MemoryStream([0x1F, 0x9F, 0x4A, 0x10]);
    using var output = new MemoryStream();
    Assert.That(() => FreezeStream.Decompress(input, output), Throws.TypeOf<InvalidDataException>());
  }

  [Test, Category("MalformedInput")]
  public void Decompress_InvalidPositionTableFlags_Throws() {
    using var input = new MemoryStream([0x1F, 0x9F, 0x4A, 0x90, 0x0A]);
    using var output = new MemoryStream();
    Assert.That(() => FreezeStream.Decompress(input, output), Throws.TypeOf<InvalidDataException>());
  }

  [Test, Category("MalformedInput")]
  public void Decompress_TruncatedPayload_Throws() {
    using var input = new MemoryStream([0x1F, 0x9F, 0x4A, 0x10, 0x0A]);
    using var output = new MemoryStream();
    Assert.That(() => FreezeStream.Decompress(input, output), Throws.TypeOf<InvalidDataException>());
  }

  [Test, Category("MalformedInput")]
  public void Decompress_UnknownFreezeGeneration_Throws() {
    using var input = new MemoryStream([0x1F, 0x9D, 0x00]);
    using var output = new MemoryStream();
    Assert.That(() => FreezeStream.Decompress(input, output), Throws.TypeOf<InvalidDataException>());
  }

  [Test, Category("Compatibility")]
  public void Freeze1_RejectsPerStreamPositionTableOptimization() {
    using var input = new MemoryStream("test test test"u8.ToArray());
    using var output = new MemoryStream();
    Assert.That(() => FreezeStream.Compress(input, output, new FreezeCompressionOptions {
      TargetCompatibility = FreezeCompatibility.Freeze1x,
      PositionTable = FreezePositionTableMode.Optimized,
    }), Throws.TypeOf<NotSupportedException>());
  }

  [Test, Category("Optimization")]
  public void Descriptor_AdvertisesCompatibilityConstraintAndFiniteOptimizerAxes() {
    var descriptor = new FreezeFormatDescriptor();
    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.SupportsOptimize), Is.True);

    var schema = (IFormatOptionsSchema)descriptor;
    Assert.That(schema.OptionsSchema.Select(static option => option.Key),
      Is.EqualTo(new[] { FormatOptionKeys.TargetCompatibility, "Parsing", "SearchDepth", "PositionTable" }));

    var compatibility = schema.OptionsSchema.Single(static option => option.Key == FormatOptionKeys.TargetCompatibility);
    Assert.That(compatibility.IsOptimizationAxis, Is.False,
      "Compatibility is a caller constraint; the optimizer must never upgrade the wire format to save bytes.");

    var positionTable = schema.OptionsSchema.Single(static option => option.Key == "PositionTable");
    Assert.That(positionTable.DependsOn, Does.Contain(nameof(FreezeCompatibility.Freeze2x)));
  }

  [Test, Category("Optimization"), Category("RoundTrip")]
  public void Optimizer_DefaultTarget_ExploresFreeze2AxesAndKeepsSmallest() {
    var original = OptimizerPayload();
    var descriptor = new FreezeFormatDescriptor();
    var baseline = Compress(descriptor, original, Options(
      ("Parsing", nameof(FreezeParsingStrategy.Lazy)),
      ("SearchDepth", FreezeCompressionOptions.DefaultSearchDepth.ToString()),
      ("PositionTable", nameof(FreezePositionTableMode.Default))));

    var result = CompressionOptimizer.OptimizeStream(original, descriptor, descriptor,
      new CompressionOptimizer.OptimizerOptions { Effort = CompressionOptimizer.Effort.Max });

    Assert.That(result.Probes, Is.EqualTo(24),
      "TargetCompatibility is fixed at the schema default; 2 parse strategies × 6 search depths × 2 position-table modes are searched.");
    Assert.That(result.CompressedSize, Is.LessThanOrEqualTo(baseline.LongLength));
    Assert.That(result.Bytes[..2], Is.EqualTo(new byte[] { 0x1F, 0x9F }));
    Assert.That(Decompress(descriptor, result.Bytes), Is.EqualTo(original));
  }

  [Test, Category("Optimization"), Category("Compatibility"), Category("RoundTrip")]
  public void Optimizer_Freeze1Target_PreservesCompatibilityAndSkipsUnavailableAxis() {
    var original = OptimizerPayload();
    var descriptor = new FreezeFormatDescriptor();
    var fixedOptions = Options((FormatOptionKeys.TargetCompatibility, nameof(FreezeCompatibility.Freeze1x)));

    var result = CompressionOptimizer.OptimizeStream(original, descriptor, descriptor,
      new CompressionOptimizer.OptimizerOptions {
        Effort = CompressionOptimizer.Effort.Max,
        BaseOptions = fixedOptions,
      });

    Assert.That(result.Probes, Is.EqualTo(12),
      "Freeze 1.x keeps the compatibility target fixed and searches only parse strategy × search depth; its fixed position table is not an axis.");
    Assert.That(result.Parameters[FormatOptionKeys.TargetCompatibility], Is.EqualTo(nameof(FreezeCompatibility.Freeze1x)));
    Assert.That(result.Bytes[..2], Is.EqualTo(new byte[] { 0x1F, 0x9E }));
    Assert.That(Decompress(descriptor, result.Bytes), Is.EqualTo(original));
  }

  [Test, Category("Optimization")]
  public void SchemaDefaults_MatchNoOptionsOutput() {
    var original = OptimizerPayload();
    var descriptor = new FreezeFormatDescriptor();

    using var input = new MemoryStream(original, writable: false);
    using var expected = new MemoryStream();
    descriptor.Compress(input, expected);

    var actual = Compress(descriptor, original, Options(
      (FormatOptionKeys.TargetCompatibility, nameof(FreezeCompatibility.Freeze2x)),
      ("Parsing", nameof(FreezeParsingStrategy.Lazy)),
      ("SearchDepth", FreezeCompressionOptions.DefaultSearchDepth.ToString()),
      ("PositionTable", nameof(FreezePositionTableMode.Default))));

    Assert.That(actual, Is.EqualTo(expected.ToArray()));
  }

  [Test, Category("Optimization"), Category("RoundTrip")]
  public void LazyDeepSearchWithOptimizedPositionTable_RoundTrips() {
    var original = OptimizerPayload();
    using var input = new MemoryStream(original, writable: false);
    using var compressed = new MemoryStream();
    FreezeStream.Compress(input, compressed, new FreezeCompressionOptions {
      TargetCompatibility = FreezeCompatibility.Freeze2x,
      Parsing = FreezeParsingStrategy.Lazy,
      SearchDepth = 512,
      PositionTable = FreezePositionTableMode.Optimized,
    });

    compressed.Position = 0;
    using var output = new MemoryStream();
    FreezeStream.Decompress(compressed, output);
    Assert.That(output.ToArray(), Is.EqualTo(original));
  }

  private static byte[] OptimizerPayload() {
    var rng = new Random(0xF2EE2E);
    var fragments = new byte[48][];
    for (var i = 0; i < fragments.Length; i++) {
      fragments[i] = new byte[rng.Next(12, 80)];
      rng.NextBytes(fragments[i]);
    }

    using var output = new MemoryStream();
    for (var i = 0; i < 1200; i++) {
      var fragment = fragments[(i * 17 + i / 11) % fragments.Length];
      output.Write(fragment);
      if (i % 9 == 0)
        output.Write(fragments[(i * 7 + 3) % fragments.Length]);
      if (i % 31 == 0)
        output.WriteByte((byte)i);
    }
    return output.ToArray();
  }

  private static FormatCreateOptions Options(params (string Key, string Value)[] options)
    => new() { FormatSpecific = options.ToDictionary(static pair => pair.Key, static pair => pair.Value) };

  private static byte[] Compress(IStreamFormatOperations operations, byte[] original, FormatCreateOptions options) {
    using var input = new MemoryStream(original, writable: false);
    using var output = new MemoryStream();
    operations.Compress(input, output, options);
    return output.ToArray();
  }

  private static byte[] Decompress(IStreamFormatOperations operations, byte[] compressed) {
    using var input = new MemoryStream(compressed, writable: false);
    using var output = new MemoryStream();
    operations.Decompress(input, output);
    return output.ToArray();
  }

  private static byte[] RoundTrip(byte[] original) {
    using var input = new MemoryStream(original, writable: false);
    using var compressed = new MemoryStream();
    FreezeStream.Compress(input, compressed);

    compressed.Position = 0;
    using var output = new MemoryStream();
    FreezeStream.Decompress(compressed, output);
    return output.ToArray();
  }
}
