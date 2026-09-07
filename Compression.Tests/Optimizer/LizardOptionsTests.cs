using System.Buffers.Binary;
using System.Text;
using Compression.Lib;
using Compression.Registry;
using FileFormat.Lizard;

namespace Compression.Tests.Optimizer;

/// <summary>
/// Lizard's managed encoder exposes the interoperable fast-LZ4 codeword levels
/// 10-19 plus frame block size. These tests verify that the options affect the
/// actual wire stream, remain round-trippable, and participate in the generic
/// compression optimizer.
/// </summary>
[TestFixture]
public class LizardOptionsTests {
  private static byte[] CompressibleSample() {
    var random = new Random(7319);
    var phrases = new[] {
      "lizard formerly lz5 ",
      "compression workbench ",
      "five stream block format ",
      "fast lz codewords ",
    };

    using var output = new MemoryStream();
    for (var i = 0; i < 250; ++i) {
      var phrase = Encoding.ASCII.GetBytes(phrases[random.Next(phrases.Length)]);
      output.Write(phrase);
      output.WriteByte((byte)('A' + i % 7));
    }
    return output.ToArray();
  }

  private static byte[] CompressAt(LizardFormatDescriptor descriptor, byte[] data, string level, string blockSize = "4 MB") {
    using var input = new MemoryStream(data, writable: false);
    using var output = new MemoryStream();
    descriptor.Compress(input, output, new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> {
        ["Level"] = level,
        ["BlockSize"] = blockSize,
      },
    });
    return output.ToArray();
  }

  private static byte[] Decompress(LizardFormatDescriptor descriptor, byte[] compressed) {
    using var input = new MemoryStream(compressed, writable: false);
    using var output = new MemoryStream();
    descriptor.Decompress(input, output);
    return output.ToArray();
  }

  [TestCase("10")]
  [TestCase("11")]
  [TestCase("12")]
  [TestCase("13")]
  [TestCase("14")]
  [TestCase("15")]
  [TestCase("16")]
  [TestCase("17")]
  [TestCase("18")]
  [TestCase("19")]
  [Category("Spec")]
  public void EverySupportedLevel_IsEncodedAndRoundTrips(string level) {
    var descriptor = new LizardFormatDescriptor();
    var data = CompressibleSample();
    var compressed = CompressAt(descriptor, data, level);

    // Header is fixed-size for our writer: magic + FLG + BD + content-size + HC.
    const int frameHeaderSize = 15;
    var outerBlockSize = BinaryPrimitives.ReadUInt32LittleEndian(compressed.AsSpan(frameHeaderSize, 4));
    Assert.That(outerBlockSize & 0x80000000u, Is.Zero, "compressible sample should exercise a compressed Lizard frame block");

    var payloadStart = frameHeaderSize + 4;
    Assert.That(compressed[payloadStart], Is.EqualTo((byte)int.Parse(level)), "Lizard payload begins with compression level");
    Assert.That(compressed[payloadStart + 1], Is.Zero, "levels 10-19 use a non-Huffman internal block header");

    // Lizard compressed blocks contain five streams. For the fast-LZ4 family,
    // lengths/off16/off24 are empty, then token and literal streams follow.
    Assert.That(ReadUInt24(compressed, payloadStart + 2), Is.Zero, "lengths stream is empty");
    Assert.That(ReadUInt24(compressed, payloadStart + 5), Is.Zero, "16-bit offset stream is empty; offsets live in literals stream");
    Assert.That(ReadUInt24(compressed, payloadStart + 8), Is.Zero, "24-bit offset stream is empty");
    Assert.That(ReadUInt24(compressed, payloadStart + 11), Is.GreaterThan(0), "token stream must be present");

    Assert.That(Decompress(descriptor, compressed), Is.EqualTo(data));
  }

  [Test, Category("Spec")]
  public void Schema_ExposesOnlyManagedInteroperableLevels() {
    var descriptor = new LizardFormatDescriptor();
    var level = descriptor.OptionsSchema.Single(option => option.Key == "Level");

    Assert.That(level.Default, Is.EqualTo("17"));
    Assert.That(level.AllowedValues, Is.EqualTo(Enumerable.Range(10, 10).Select(static value => value.ToString()).ToArray()));
    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.SupportsOptimize), Is.True);
    Assert.That(descriptor.Methods.Single().SupportsOptimize, Is.True);
  }

  [Test, Category("Spec")]
  public void Optimizer_SearchesLevelAndBlockSize_AndResultRoundTrips() {
    var descriptor = new LizardFormatDescriptor();
    var data = CompressibleSample();

    var result = CompressionOptimizer.OptimizeStream(data, descriptor, descriptor);

    Assert.That(result.Parameters, Does.ContainKey("Level"));
    Assert.That(result.Parameters, Does.ContainKey("BlockSize"));
    Assert.That(result.Probes, Is.EqualTo(40), "10 levels x 4 block sizes must be searched exhaustively");
    Assert.That(result.OriginalSize, Is.EqualTo(data.Length));
    Assert.That(result.Ratio, Is.LessThan(1.0));
    Assert.That(Decompress(descriptor, result.Bytes), Is.EqualTo(data));

    foreach (var (level, blockSize) in new[] {
      ("10", "128 KB"),
      ("17", "4 MB"),
      ("19", "128 KB"),
      ("19", "4 MB"),
    }) {
      var candidate = CompressAt(descriptor, data, level, blockSize);
      Assert.That(result.CompressedSize, Is.LessThanOrEqualTo(candidate.LongLength),
        $"optimizer result must be no larger than level {level}, block size {blockSize}");
    }
  }

  [Test, Category("ErrorHandling")]
  public void RawEncoder_RejectsLIZv1AndHuffmanLevelsNotImplementedYet() {
    using var input = new MemoryStream(CompressibleSample(), writable: false);
    using var output = new MemoryStream();

    Assert.That(() => LizardStream.Compress(input, output, 20, 4 * 1024 * 1024),
      Throws.TypeOf<ArgumentOutOfRangeException>());
  }

  private static int ReadUInt24(byte[] data, int offset) =>
    data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16);
}
