using System.Text;
using Compression.Lib;
using Compression.Registry;
using FileFormat.Paq8;

namespace Compression.Tests.Optimizer;

/// <summary>
/// PAQ8 exposes its stored compression level through <see cref="IFormatOptionsSchema"/>.
/// The parameterised writer must honour every level, level 5 must retain the
/// historical bitstream, and <see cref="CompressionOptimizer"/> must search the
/// complete level range and return the smallest stream for the actual input.
/// </summary>
[TestFixture]
public class Paq8OptionsTests {

  private static byte[] CompressibleSample()
    => Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(
      "banana bandana abracadabra compression workbench\n", 256)));

  private static byte[] CompressAt(Paq8FormatDescriptor descriptor, byte[] data, string level) {
    using var input = new MemoryStream(data);
    using var output = new MemoryStream();
    descriptor.Compress(input, output, new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> { ["Level"] = level },
    });
    return output.ToArray();
  }

  private static byte[] Decompress(Paq8FormatDescriptor descriptor, byte[] compressed) {
    using var input = new MemoryStream(compressed);
    using var output = new MemoryStream();
    descriptor.Decompress(input, output);
    return output.ToArray();
  }

  [Test, Category("Spec")]
  public void Paq8_ExposesDocumentedLevelRange() {
    var descriptor = new Paq8FormatDescriptor();
    var option = descriptor.OptionsSchema.Single(o => o.Key == "Level");

    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.SupportsOptimize), Is.True);
    Assert.That(option.Default, Is.EqualTo("5"));
    Assert.That(option.AllowedValues, Is.EqualTo(new[] { "1", "2", "3", "4", "5", "6", "7", "8" }));
  }

  [Test, Category("Spec")]
  public void Paq8_EveryLevelIsStoredAndRoundTrips() {
    var descriptor = new Paq8FormatDescriptor();
    var data = CompressibleSample();

    foreach (var level in descriptor.OptionsSchema.Single(o => o.Key == "Level").AllowedValues!) {
      var compressed = CompressAt(descriptor, data, level);
      Assert.That(compressed[7], Is.EqualTo((byte)level[0]), $"level {level} is recorded in the header");
      Assert.That(Decompress(descriptor, compressed), Is.EqualTo(data), $"level {level} output round-trips");
    }

    var low = CompressAt(descriptor, data, "1");
    var high = CompressAt(descriptor, data, "8");
    Assert.That(low.AsSpan(10).SequenceEqual(high.AsSpan(10)), Is.False,
      "levels must tune the predictor, not merely rewrite the header digit");
  }

  [Test, Category("Spec")]
  public void Paq8_DefaultLevelRemainsBitCompatible() {
    var data = Encoding.ASCII.GetBytes("banana banana banana");
    using var input = new MemoryStream(data);
    using var output = new MemoryStream();

    Paq8Stream.Compress(input, output);

    Assert.That(output.ToArray(), Is.EqualTo(Convert.FromHexString(
      "706171386C202D350D0A323009646174610D0A1A626299326049A81CFBE84310090B436FDD73A9EC")));
  }

  [Test, Category("Spec")]
  public void Optimizer_FindsSmallestLevel_AndResultRoundTrips() {
    var descriptor = new Paq8FormatDescriptor();
    var data = CompressibleSample();

    var result = CompressionOptimizer.OptimizeStream(data, descriptor, descriptor);

    Assert.That(result.Parameters, Does.ContainKey("Level"));
    Assert.That(result.Probes, Is.EqualTo(8), "the complete PAQ8L level range is searched");
    Assert.That(result.OriginalSize, Is.EqualTo(data.Length));
    Assert.That(result.Ratio, Is.LessThan(1.0));
    Assert.That(Decompress(descriptor, result.Bytes), Is.EqualTo(data));
    Assert.That(result.Bytes[7], Is.EqualTo((byte)result.Parameters["Level"][0]));

    foreach (var level in descriptor.OptionsSchema.Single(o => o.Key == "Level").AllowedValues!)
      Assert.That(result.CompressedSize, Is.LessThanOrEqualTo(CompressAt(descriptor, data, level).Length),
        $"optimizer result must be <= level {level}");
  }
}
