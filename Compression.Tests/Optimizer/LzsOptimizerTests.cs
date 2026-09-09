using Compression.Core.Dictionary.Lzs;
using Compression.Lib;
using Compression.Registry;
using FileFormat.Lzs;

namespace Compression.Tests.Optimizer;

[TestFixture]
public class LzsOptimizerTests {
  [Test, Category("Spec")]
  public void BuildingBlock_LengthFive_UsesRfc2395Code() {
    var codec = new LzsBuildingBlock();

    // RFC 2395 grammar for AAAAAA:
    // literal A | offset 1, length 5 | end marker, then zero padding.
    var compressed = codec.Compress("AAAAAA"u8);

    Assert.That(compressed, Is.EqualTo(new byte[] {
      0x06, 0x00, 0x00, 0x00,
      0x20, 0xE0, 0x73, 0x00,
    }));
    Assert.That(codec.Decompress(compressed), Is.EqualTo("AAAAAA"u8.ToArray()));
  }

  [Test, Category("Spec")]
  public void BuildingBlock_InvalidBackReference_IsRejected() {
    // Declared output size 1, followed immediately by a short offset 1 while
    // the history is still empty. Padding after the offset is irrelevant.
    byte[] malformed = [0x01, 0x00, 0x00, 0x00, 0xC0, 0x80];

    Assert.That(
      () => new LzsBuildingBlock().Decompress(malformed),
      Throws.TypeOf<InvalidDataException>());
  }

  [Test, Category("Compression")]
  public void Descriptor_AdvertisesFiniteOptimizerLevels() {
    var descriptor = new LzsFormatDescriptor();

    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.SupportsOptimize), Is.True);
    Assert.That(descriptor.Methods.Single().SupportsOptimize, Is.True);
    Assert.That(descriptor.OptionsSchema, Has.Count.EqualTo(1));
    Assert.That(descriptor.OptionsSchema[0].AllowedValues, Is.EqualTo(new[] { "Fast", "Balanced", "Maximum" }));
  }

  [Test, Category("HappyPath")]
  public void AllLevels_RoundTrip() {
    var descriptor = new LzsFormatDescriptor();
    var data = CompressibleSample();

    foreach (var level in new[] { "Fast", "Balanced", "Maximum" }) {
      var compressed = CompressAt(descriptor, data, level);
      Assert.That(Decompress(descriptor, compressed), Is.EqualTo(data), level);
    }
  }

  [Test, Category("Compression")]
  public void CompressOptimal_IsNeverLargerThanDefault() {
    var descriptor = new LzsFormatDescriptor();
    var payloads = new[] {
      Array.Empty<byte>(),
      "adbacdaddbacdadddaddbacddaddbacdadddaddbacddabdcdaddbacdaddddadb"u8.ToArray(),
      CompressibleSample(),
    };

    foreach (var data in payloads) {
      var normal = CompressDefault(descriptor, data);
      var optimal = CompressOptimal(descriptor, data);
      Assert.That(optimal.Length, Is.LessThanOrEqualTo(normal.Length), $"payload length {data.Length}");
      Assert.That(Decompress(descriptor, optimal), Is.EqualTo(data));
    }
  }

  [Test, Category("Compression")]
  public void CompressOptimal_FindsSmallerLazyParse() {
    var descriptor = new LzsFormatDescriptor();
    var data = LazyImprovementSample();

    var balanced = CompressDefault(descriptor, data);
    var optimal = CompressOptimal(descriptor, data);

    Assert.That(optimal.Length, Is.LessThan(balanced.Length));
    Assert.That(Decompress(descriptor, optimal), Is.EqualTo(data));
  }

  [Test, Category("Compression")]
  public void GenericOptimizer_FindsSmallestDeclaredLevel() {
    var descriptor = new LzsFormatDescriptor();
    var data = LazyImprovementSample();

    var result = CompressionOptimizer.OptimizeStream(data, descriptor, descriptor);

    Assert.That(result.Parameters["Level"], Is.EqualTo("Maximum"));
    Assert.That(result.Probes, Is.EqualTo(3));
    Assert.That(Decompress(descriptor, result.Bytes), Is.EqualTo(data));

    foreach (var level in new[] { "Fast", "Balanced", "Maximum" })
      Assert.That(result.CompressedSize, Is.LessThanOrEqualTo(CompressAt(descriptor, data, level).Length), level);
  }

  private static byte[] LazyImprovementSample() => [
    3, 4, 3, 5, 0, 4, 1, 2, 4, 0, 0, 3, 0, 1, 2, 0,
    5, 2, 5, 5, 4, 4, 0, 5, 5, 5, 4, 4, 0, 5, 0, 0,
    2, 0, 3, 5, 1, 3, 4, 2, 2, 4, 1, 4, 5, 5, 3, 3,
    4, 4, 3, 3, 1, 5, 2, 1, 5, 1, 4, 3, 3, 2, 5, 2,
  ];

  private static byte[] CompressibleSample() {
    var data = new byte[8192];
    var pattern = "LZS optimizer: RFC 2395 sliding-window parsing with repeated phrases and local variations. "u8;
    for (var i = 0; i < data.Length; ++i)
      data[i] = pattern[i % pattern.Length];

    // Break selected repetitions so the match finder sees both short and long alternatives.
    for (var i = 257; i < data.Length; i += 521)
      data[i] ^= (byte)(i >> 4);
    return data;
  }

  private static byte[] CompressAt(LzsFormatDescriptor descriptor, byte[] data, string level) {
    using var input = new MemoryStream(data, writable: false);
    using var output = new MemoryStream();
    descriptor.Compress(input, output, new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> { ["Level"] = level },
    });
    return output.ToArray();
  }

  private static byte[] CompressDefault(LzsFormatDescriptor descriptor, byte[] data) {
    using var input = new MemoryStream(data, writable: false);
    using var output = new MemoryStream();
    descriptor.Compress(input, output);
    return output.ToArray();
  }

  private static byte[] CompressOptimal(LzsFormatDescriptor descriptor, byte[] data) {
    using var input = new MemoryStream(data, writable: false);
    using var output = new MemoryStream();
    descriptor.CompressOptimal(input, output);
    return output.ToArray();
  }

  private static byte[] Decompress(LzsFormatDescriptor descriptor, byte[] compressed) {
    using var input = new MemoryStream(compressed, writable: false);
    using var output = new MemoryStream();
    descriptor.Decompress(input, output);
    return output.ToArray();
  }
}
