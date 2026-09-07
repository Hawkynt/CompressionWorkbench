using Compression.Lib;
using Compression.Registry;
using FileFormat.PowerPacker;

namespace Compression.Tests.PowerPacker;

[TestFixture]
public class PowerPackerTests {

  // These vectors are constructed from the published PP20 bitstream rules, not
  // from PowerPackerStream. They pin reverse LSB-first bit order, extended
  // literal counts, and class-3 short offsets independently of our writer.
  [TestCase("50503230090A0B0B8242C24000000305", "ABC")]
  [TestCase("50503230090A0B0B8242C222A262E23C00000701", "ABCDEFG")]
  [TestCase("50503230090A0B0B881C12161200000000000918", "ABCABCABC")]
  [Category("HappyPath")]
  public void Decompress_SpecDerivedVectors(string packedHex, string expected) {
    var packed = Convert.FromHexString(packedHex);
    var result = PowerPackerStream.Decompress(packed);
    Assert.That(result, Is.EqualTo(System.Text.Encoding.ASCII.GetBytes(expected)));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SmallData() {
    var data = "Hello, PowerPacker! This is a test of the PP20 format."u8.ToArray();
    AssertRoundTrip(data, PowerPackerEfficiency.Good);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_RepetitiveData() {
    var data = new byte[4096];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 16);

    AssertRoundTrip(data, PowerPackerEfficiency.Good);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_RandomData() {
    var data = new byte[8192];
    new Random(42).NextBytes(data);
    AssertRoundTrip(data, PowerPackerEfficiency.Good);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_EmptyData() => AssertRoundTrip([], PowerPackerEfficiency.Good);

  [TestCase(PowerPackerEfficiency.Fast, "09090909")]
  [TestCase(PowerPackerEfficiency.Mediocre, "090A0A0A")]
  [TestCase(PowerPackerEfficiency.Good, "090A0B0B")]
  [TestCase(PowerPackerEfficiency.VeryGood, "090A0C0C")]
  [TestCase(PowerPackerEfficiency.Best, "090A0C0D")]
  [Category("HappyPath")]
  [Category("RoundTrip")]
  public void EveryHistoricalEfficiencyPresetRoundTripsAndWritesItsTable(
    PowerPackerEfficiency efficiency,
    string expectedTableHex) {
    var data = BuildOptimizerCorpus();
    var packed = PowerPackerStream.Compress(data, efficiency);

    Assert.Multiple(() => {
      Assert.That(packed.AsSpan(0, 4).SequenceEqual("PP20"u8), Is.True);
      Assert.That(Convert.ToHexString(packed.AsSpan(4, 4)), Is.EqualTo(expectedTableHex));
      Assert.That(packed.Length % 4, Is.Zero, "PP20 stores the bitstream as whole longwords");
      Assert.That(PowerPackerStream.Decompress(packed), Is.EqualTo(data));
    });
  }

  [Test, Category("HappyPath")]
  public void CompressOptimal_IsTheSmallestHistoricalPreset() {
    var data = BuildOptimizerCorpus();
    var expectedSize = Enum.GetValues<PowerPackerEfficiency>()
      .Select(efficiency => PowerPackerStream.Compress(data, efficiency).Length)
      .Min();

    var optimized = PowerPackerStream.CompressOptimal(data);

    Assert.Multiple(() => {
      Assert.That(optimized.Length, Is.EqualTo(expectedSize));
      Assert.That(PowerPackerStream.Decompress(optimized), Is.EqualTo(data));
    });
  }

  [Test, Category("HappyPath")]
  public void DescriptorExposesEfficiencyToTheGenericOptimizer() {
    var descriptor = new PowerPackerFormatDescriptor();
    var data = BuildOptimizerCorpus();

    var result = CompressionOptimizer.OptimizeStream(data, descriptor, descriptor);
    var expectedSize = Enum.GetValues<PowerPackerEfficiency>()
      .Select(efficiency => PowerPackerStream.Compress(data, efficiency).LongLength)
      .Min();

    Assert.Multiple(() => {
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.SupportsOptimize), Is.True);
      Assert.That(descriptor.Methods.Single().SupportsOptimize, Is.True);
      Assert.That(descriptor.OptionsSchema.Single().AllowedValues, Is.EqualTo(Enum.GetNames<PowerPackerEfficiency>()));
      Assert.That(result.Probes, Is.EqualTo(5));
      Assert.That(result.CompressedSize, Is.EqualTo(expectedSize));
      Assert.That(result.Parameters.ContainsKey("Efficiency"), Is.True);
      Assert.That(PowerPackerStream.Decompress(result.Bytes), Is.EqualTo(data));
    });
  }

  [Test, Category("HappyPath")]
  public void FormatCreateOptions_SelectsEfficiencyPreset() {
    var descriptor = new PowerPackerFormatDescriptor();
    var options = new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
        ["Efficiency"] = "Best",
      },
    };

    using var input = new MemoryStream("options"u8.ToArray());
    using var output = new MemoryStream();
    descriptor.Compress(input, output, options);

    Assert.That(Convert.ToHexString(output.ToArray().AsSpan(4, 4)), Is.EqualTo("090A0C0D"));
  }

  [Test, Category("Corruption")]
  public void InvalidEfficiencyTableIsRejected() {
    var packed = Convert.FromHexString("50503230080A0B0B8242C24000000305");
    Assert.That(() => PowerPackerStream.Decompress(packed), Throws.TypeOf<InvalidDataException>());
  }

  [Test, Category("Corruption")]
  public void NonLongwordAlignedFileIsRejected() {
    var packed = Convert.FromHexString("50503230090A0B0B8242C24000000305")[..^1];
    Assert.That(() => PowerPackerStream.Decompress(packed), Throws.TypeOf<InvalidDataException>());
  }

  private static void AssertRoundTrip(byte[] data, PowerPackerEfficiency efficiency) {
    var packed = PowerPackerStream.Compress(data, efficiency);
    Assert.That(PowerPackerStream.Decompress(packed), Is.EqualTo(data));
  }

  private static byte[] BuildOptimizerCorpus() {
    var data = new byte[12_288];
    for (var i = 0; i < 4096; ++i)
      data[i] = (byte)((i * 73 + (i >> 4)) & 0xFF);
    data.AsSpan(0, 4096).CopyTo(data.AsSpan(4096, 4096));
    data.AsSpan(0, 4096).CopyTo(data.AsSpan(8192, 4096));
    return data;
  }
}
