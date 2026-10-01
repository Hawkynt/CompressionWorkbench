using System.Buffers.Binary;
using Compression.Core.Dictionary.Csc;
using Compression.Lib;
using Compression.Registry;
using Compression.Tests.Csc;
using FileFormat.Csc;

namespace Compression.Tests.Optimizer;

/// <summary>
/// CSC's format options are the reference <c>csc</c> tool's switches: <c>-m1</c>..<c>-m5</c> and
/// <c>-d</c>. Levels are the optimizer's search axis; the window is not, because the encoder shrinks
/// it to the input anyway.
/// </summary>
[TestFixture]
public class CscOptionsTests {

  [Test, Category("Spec")]
  public void Schema_AdvertisesLibcscLevelsAndWindow() {
    var descriptor = new CscFormatDescriptor();

    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.SupportsOptimize), Is.True);

    var level = descriptor.OptionsSchema.Single(option => option.Key == "Level");
    Assert.Multiple(() => {
      Assert.That(level.Kind, Is.EqualTo(FormatOptionKind.Integer));
      Assert.That(level.Default, Is.EqualTo("2"));
      Assert.That(level.AllowedValues, Is.EqualTo(new[] { "1", "2", "3", "4", "5" }));
      Assert.That(level.IsOptimizationAxis, Is.True);
    });

    var dictionary = descriptor.OptionsSchema.Single(option => option.Key == "DictionarySize");
    Assert.Multiple(() => {
      Assert.That(dictionary.Kind, Is.EqualTo(FormatOptionKind.Integer));
      Assert.That(dictionary.Default, Is.EqualTo("64000000"));
      Assert.That(dictionary.IsOptimizationAxis, Is.False);
    });
  }

  [Test, Category("Spec")]
  public void DefaultCompression_EqualsTheReferenceToolDefaults() {
    var descriptor = new CscFormatDescriptor();
    var data = CscSamples.Text(30_000);

    Assert.That(CompressDefault(descriptor, data), Is.EqualTo(CscCodec.Compress(data, new(Level: 2))));
  }

  [Test, Category("HappyPath")]
  public void Level_SelectsParser_AndEveryLevelRoundTrips() {
    var descriptor = new CscFormatDescriptor();
    var data = CscSamples.Text(60_000);

    var fastest = CompressAt(descriptor, data, level: "1", dictionarySize: "1048576");
    var strongest = CompressAt(descriptor, data, level: "5", dictionarySize: "1048576");

    Assert.Multiple(() => {
      Assert.That(strongest.Length, Is.LessThan(fastest.Length), "the optimal parser beats the lazy one on text");
      Assert.That(Decompress(descriptor, fastest), Is.EqualTo(data));
      Assert.That(Decompress(descriptor, strongest), Is.EqualTo(data));
    });
  }

  [Test, Category("HappyPath")]
  public void DictionarySize_SetsTheHeaderWindow() {
    var descriptor = new CscFormatDescriptor();
    var data = CscSamples.Text(200_000);

    var small = CompressAt(descriptor, data, level: "2", dictionarySize: "32768");
    var large = CompressAt(descriptor, data, level: "2", dictionarySize: "1048576");

    Assert.Multiple(() => {
      Assert.That(BinaryPrimitives.ReadUInt32BigEndian(small), Is.EqualTo(32768u + 10240));
      Assert.That(BinaryPrimitives.ReadUInt32BigEndian(large), Is.EqualTo(200_000u + 10240), "shrunk to the input");
      Assert.That(Decompress(descriptor, small), Is.EqualTo(data));
      Assert.That(Decompress(descriptor, large), Is.EqualTo(data));
    });
  }

  [TestCase("0", "1000", 1, 32768L)]
  [TestCase("9", "2000000000", 5, 1073741823L)]
  [Category("Boundary")]
  public void OutOfRangeOptions_AreClampedToTheToolLimits(string level, string dictionarySize, int expectedLevel, long expectedWindow) {
    var parsed = CscFormatDescriptor.ParseOptions(new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> { ["Level"] = level, ["DictionarySize"] = dictionarySize },
    });

    Assert.That(parsed, Is.EqualTo(new CscEncoderOptions(expectedLevel, expectedWindow)));
  }

  [Test, Category("HappyPath")]
  public void GenericLevelAndDictSize_AreHonoured() {
    var parsed = CscFormatDescriptor.ParseOptions(new FormatCreateOptions { Level = 4, DictSize = 1 << 20 });

    Assert.That(parsed, Is.EqualTo(new CscEncoderOptions(4, 1 << 20)));
  }

  [Test, Category("HappyPath")]
  public void Optimizer_SearchesTheFiveLevels_AndResultRoundTrips() {
    var descriptor = new CscFormatDescriptor();
    var data = CscSamples.Text(40_000);

    var result = CompressionOptimizer.OptimizeStream(data, descriptor, descriptor);

    Assert.Multiple(() => {
      Assert.That(result.Parameters, Does.ContainKey("Level"));
      Assert.That(result.Probes, Is.EqualTo(5));
      Assert.That(Decompress(descriptor, result.Bytes), Is.EqualTo(data));
    });

    for (var level = 1; level <= 5; ++level)
      Assert.That(result.CompressedSize, Is.LessThanOrEqualTo(CscCodec.Compress(data, new(Level: level)).Length), $"level {level}");
  }

  private static byte[] CompressDefault(CscFormatDescriptor descriptor, byte[] data) {
    using var input = new MemoryStream(data, writable: false);
    using var output = new MemoryStream();
    descriptor.Compress(input, output);
    return output.ToArray();
  }

  private static byte[] CompressAt(CscFormatDescriptor descriptor, byte[] data, string level, string dictionarySize) {
    using var input = new MemoryStream(data, writable: false);
    using var output = new MemoryStream();
    descriptor.Compress(input, output, new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> {
        ["Level"] = level,
        ["DictionarySize"] = dictionarySize,
      },
    });
    return output.ToArray();
  }

  private static byte[] Decompress(CscFormatDescriptor descriptor, byte[] compressed) {
    using var input = new MemoryStream(compressed, writable: false);
    using var output = new MemoryStream();
    descriptor.Decompress(input, output);
    return output.ToArray();
  }
}
