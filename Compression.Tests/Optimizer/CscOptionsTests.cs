using System.Buffers.Binary;
using Compression.Lib;
using Compression.Registry;
using FileFormat.Csc;

namespace Compression.Tests.Optimizer;

/// <summary>
/// CSC exposes its managed encoder's two real compression levers: hash-chain
/// search effort and LZ77 dictionary size. Both options must affect the encoder,
/// keep streams decodable, and be searched by <see cref="CompressionOptimizer"/>.
/// </summary>
[TestFixture]
public class CscOptionsTests {

  [Test, Category("Spec")]
  public void Schema_AdvertisesLevelDictionaryAndOptimization() {
    var descriptor = new CscFormatDescriptor();

    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.SupportsOptimize), Is.True);

    var level = descriptor.OptionsSchema.Single(option => option.Key == "Level");
    Assert.Multiple(() => {
      Assert.That(level.Kind, Is.EqualTo(FormatOptionKind.Integer));
      Assert.That(level.Default, Is.EqualTo("4"));
      Assert.That(level.AllowedValues, Is.EqualTo(new[] { "1", "2", "3", "4", "5" }));
    });

    var dictionary = descriptor.OptionsSchema.Single(option => option.Key == "DictionarySize");
    Assert.Multiple(() => {
      Assert.That(dictionary.Kind, Is.EqualTo(FormatOptionKind.Integer));
      Assert.That(dictionary.Default, Is.EqualTo("65536"));
      Assert.That(dictionary.AllowedValues, Is.EqualTo(new[] { "32768", "65536" }));
    });
  }

  [Test, Category("Regression")]
  public void DefaultCompression_EqualsExplicitHistoricalPreset() {
    var descriptor = new CscFormatDescriptor();
    var data = SearchDepthSensitiveSample();

    var implicitDefaults = CompressDefault(descriptor, data);
    var explicitDefaults = CompressAt(descriptor, data, level: "4", dictionarySize: "65536");

    Assert.That(explicitDefaults, Is.EqualTo(implicitDefaults),
      "adding optimizer options must not change the historical level-4/64-KiB default bitstream");
  }

  [Test, Category("HappyPath")]
  public void Level_ChangesHashChainSearch_AndBothOutputsRoundTrip() {
    var descriptor = new CscFormatDescriptor();
    var data = SearchDepthSensitiveSample();

    var fast = CompressAt(descriptor, data, level: "1", dictionarySize: "65536");
    var strong = CompressAt(descriptor, data, level: "5", dictionarySize: "65536");

    Assert.Multiple(() => {
      Assert.That(strong.Length, Is.LessThan(fast.Length),
        "the collision-heavy sample needs a deeper chain search to rediscover the long match");
      Assert.That(Decompress(descriptor, fast), Is.EqualTo(data));
      Assert.That(Decompress(descriptor, strong), Is.EqualTo(data));
    });
  }

  [Test, Category("HappyPath")]
  public void DictionarySize_ControlsHeaderAndLongDistanceMatches() {
    var descriptor = new CscFormatDescriptor();
    var data = LongDistanceSample();

    var shortWindow = CompressAt(descriptor, data, level: "5", dictionarySize: "32768");
    var wideWindow = CompressAt(descriptor, data, level: "5", dictionarySize: "65536");

    Assert.Multiple(() => {
      Assert.That(BinaryPrimitives.ReadUInt32BigEndian(shortWindow), Is.EqualTo(32768u));
      Assert.That(BinaryPrimitives.ReadUInt32BigEndian(wideWindow), Is.EqualTo(65536u));
      Assert.That(wideWindow.Length, Is.LessThan(shortWindow.Length),
        "the repeated prefix is deliberately farther than 32 KiB but still inside 64 KiB");
      Assert.That(Decompress(descriptor, shortWindow), Is.EqualTo(data));
      Assert.That(Decompress(descriptor, wideWindow), Is.EqualTo(data));
    });
  }

  [Test, Category("HappyPath")]
  public void Optimizer_FindsSmallestCandidate_AndResultRoundTrips() {
    var descriptor = new CscFormatDescriptor();
    var data = LongDistanceSample();

    var result = CompressionOptimizer.OptimizeStream(data, descriptor, descriptor);

    Assert.Multiple(() => {
      Assert.That(result.Parameters, Does.ContainKey("Level"));
      Assert.That(result.Parameters, Does.ContainKey("DictionarySize"));
      Assert.That(result.OriginalSize, Is.EqualTo(data.Length));
      Assert.That(result.Probes, Is.EqualTo(10), "5 levels × 2 dictionary sizes should be searched exhaustively");
      Assert.That(Decompress(descriptor, result.Bytes), Is.EqualTo(data));
    });

    var levels = descriptor.OptionsSchema.Single(option => option.Key == "Level").AllowedValues!;
    var dictionaries = descriptor.OptionsSchema.Single(option => option.Key == "DictionarySize").AllowedValues!;
    foreach (var level in levels)
      foreach (var dictionary in dictionaries) {
        var candidate = CompressAt(descriptor, data, level, dictionary);
        Assert.That(result.CompressedSize, Is.LessThanOrEqualTo(candidate.Length),
          $"optimizer result must be no larger than Level={level}, DictionarySize={dictionary}");
      }
  }

  private static byte[] SearchDepthSensitiveSample() {
    var anchor = new byte[180];
    anchor[0] = (byte)'A';
    anchor[1] = (byte)'B';
    anchor[2] = (byte)'C';
    anchor[3] = 0xF0;
    for (var i = 4; i < anchor.Length; ++i)
      anchor[i] = (byte)(0xD0 + (i & 0x0F));

    using var output = new MemoryStream();
    output.Write(anchor);
    for (var round = 0; round < 40; ++round) {
      // These entries share the anchor's three-byte hash but immediately diverge.
      // A shallow search exhausts its candidate budget before reaching the anchor.
      for (var decoy = 0; decoy < 20; ++decoy) {
        output.WriteByte((byte)'A');
        output.WriteByte((byte)'B');
        output.WriteByte((byte)'C');
        output.WriteByte((byte)(decoy + 1));
        output.WriteByte((byte)(0x80 + decoy));
      }
      output.Write(anchor);
    }

    return output.ToArray();
  }

  private static byte[] LongDistanceSample() {
    var prefix = new byte[768];
    new Random(12345).NextBytes(prefix);

    using var output = new MemoryStream();
    output.Write(prefix);
    for (var i = 0; i < 34 * 1024; ++i)
      output.WriteByte(0xAA);
    output.Write(prefix);
    return output.ToArray();
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
