using Compression.Lib;
using Compression.Registry;
using FileFormat.Bzip2;

namespace Compression.Tests.Optimizer;

/// <summary>
/// BZip2 exposes a block-size knob through <see cref="IFormatOptionsSchema"/>.
/// Its parameterised <c>Compress</c> must honour the chosen block size, every
/// block size must round-trip, and <see cref="CompressionOptimizer"/> must
/// search the block sizes and return an output no larger than the default that
/// still round-trips.
/// </summary>
[TestFixture]
[Category("Slow")]
public class Bzip2OptionsTests {

  private static byte[] CompressibleSample() {
    // Larger than a single 100 KB block so the block-size knob can matter:
    // ~300 KB of repetitive-but-varied text.
    var ms = new MemoryStream();
    var rng = new Random(4242);
    var phrases = new[] { "the quick brown fox ", "jumps over the lazy dog ", "compression workbench " };
    while (ms.Length < 300 * 1024) {
      var p = System.Text.Encoding.ASCII.GetBytes(phrases[rng.Next(phrases.Length)]);
      ms.Write(p);
    }
    return ms.ToArray();
  }

  private static byte[] Decompress(Bzip2FormatDescriptor d, byte[] compressed) {
    using var inMs = new MemoryStream(compressed);
    using var outMs = new MemoryStream();
    d.Decompress(inMs, outMs);
    return outMs.ToArray();
  }

  private static byte[] CompressAt(Bzip2FormatDescriptor d, byte[] data, string blockSize) {
    using var inMs = new MemoryStream(data);
    using var outMs = new MemoryStream();
    d.Compress(inMs, outMs, new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> { ["BlockSize"] = blockSize },
    });
    return outMs.ToArray();
  }

  [Test, Category("Spec")]
  public void Bzip2_ExposesBlockSizeOption() {
    var d = new Bzip2FormatDescriptor();
    Assert.That(d, Is.InstanceOf<IFormatOptionsSchema>());
    var schema = (IFormatOptionsSchema)d;
    var option = schema.OptionsSchema.Single(o => o.Key == "BlockSize");
    Assert.That(option.Default, Is.EqualTo("9"));
    Assert.That(option.AllowedValues, Is.EqualTo(new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9" }));
  }

  [Test, Category("Spec")]
  public void Bzip2_HonoursBlockSizeOption_AndRoundTrips() {
    var d = new Bzip2FormatDescriptor();
    var data = CompressibleSample();

    var smallest = CompressAt(d, data, "1");
    var largest = CompressAt(d, data, "9");

    // The block-size digit is written into the header, so the two outputs differ.
    Assert.That(smallest[3], Is.EqualTo((byte)'1'), "block-size 1 is recorded in the header");
    Assert.That(largest[3], Is.EqualTo((byte)'9'), "block-size 9 is recorded in the header");
    Assert.That(Decompress(d, smallest), Is.EqualTo(data), "block-size 1 output round-trips");
    Assert.That(Decompress(d, largest), Is.EqualTo(data), "block-size 9 output round-trips");
  }

  [Test, Category("Spec")]
  public void Optimizer_FindsSmallest_AndResultRoundTrips() {
    var d = new Bzip2FormatDescriptor();
    var data = CompressibleSample();

    var result = CompressionOptimizer.OptimizeStream(data, d, d);

    Assert.That(result.Parameters, Does.ContainKey("BlockSize"), "the winning block size is reported");
    Assert.That(result.OriginalSize, Is.EqualTo(data.Length));
    Assert.That(result.Ratio, Is.LessThan(1.0), "compressible data shrinks");
    Assert.That(Decompress(d, result.Bytes), Is.EqualTo(data), "optimal result round-trips");

    // The optimizer minimum must not be larger than the default (block-size 9).
    var atDefault = CompressAt(d, data, "9");
    Assert.That(result.CompressedSize, Is.LessThanOrEqualTo(atDefault.Length),
      "optimizer result must be <= the default block size");

    // And it must be the minimum over the whole searched range.
    for (var bs = 1; bs <= 9; bs++)
      Assert.That(result.CompressedSize, Is.LessThanOrEqualTo(CompressAt(d, data, bs.ToString()).Length),
        $"optimizer result must be <= block size {bs}");
  }
}
