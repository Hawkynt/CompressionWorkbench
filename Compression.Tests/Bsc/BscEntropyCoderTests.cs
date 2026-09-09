using System.Buffers.Binary;
using Compression.Registry;
using FileFormat.Bsc;

namespace Compression.Tests.Bsc;

/// <summary>
/// Every libbsc QLFC coder has to survive its own output. The adaptive coder in
/// particular mixes three predictors per bit, and a mixer selected differently on
/// the encode and decode sides desynchronises the range coder rather than merely
/// costing ratio, so each coder is round-tripped on its own.
/// </summary>
public class BscEntropyCoderTests {
  private const int FileHeaderSize = 8;
  private const int FileBlockHeaderSize = 10;
  private const int InternalBlockHeaderSize = 28;

  [TestCase("Fast", 3)]
  [TestCase("Static", 1)]
  [TestCase("Adaptive", 2)]
  public void EveryQlfcCoder_RoundTrips_AndWritesExpectedMode(string entropyCoder, int expectedCoder) {
    var data = SkewedSample();
    var compressed = Compress(data, entropyCoder);
    var internalHeaderOffset = FileHeaderSize + FileBlockHeaderSize;
    var encodedBlockSize = BinaryPrimitives.ReadInt32LittleEndian(compressed.AsSpan(internalHeaderOffset));
    var mode = BinaryPrimitives.ReadInt32LittleEndian(compressed.AsSpan(internalHeaderOffset + 8));

    Assert.Multiple(() => {
      Assert.That((mode & 0x1f), Is.EqualTo(1), "compressed BSC blocks must use the BWT sorter");
      Assert.That((mode >> 5) & 0x7, Is.EqualTo(expectedCoder));
      Assert.That(compressed[internalHeaderOffset + encodedBlockSize - 1], Is.Zero,
        "managed BSC writes no auxiliary BWT indexes, so the final num_indexes byte must be zero");
      Assert.That(Decompress(compressed), Is.EqualTo(data));
      Assert.That(compressed.Length, Is.LessThan(data.Length));
    });
  }

  [Test]
  public void AdaptiveQlfcCoder_IsRepeatableAcrossModelInstances() {
    var data = SkewedSample();

    var first = Compress(data, "Adaptive");
    var second = Compress(data, "Adaptive");

    Assert.Multiple(() => {
      Assert.That(second, Is.EqualTo(first),
        "adaptive mixer-bank selection must not depend on process-wide model construction order");
      Assert.That(Decompress(first), Is.EqualTo(data));
      Assert.That(Decompress(second), Is.EqualTo(data));
    });
  }

  private static byte[] Compress(byte[] data, string entropyCoder) {
    var descriptor = new BscFormatDescriptor();
    var options = new FormatCreateOptions();
    options.FormatSpecific["BlockSize"] = "65536";
    options.FormatSpecific["SortingContexts"] = "Following";
    options.FormatSpecific["EntropyCoder"] = entropyCoder;

    using var input = new MemoryStream(data);
    using var compressed = new MemoryStream();
    descriptor.Compress(input, compressed, options);
    return compressed.ToArray();
  }

  private static byte[] Decompress(byte[] compressed) {
    var descriptor = new BscFormatDescriptor();
    using var input = new MemoryStream(compressed, writable: false);
    using var restored = new MemoryStream();
    descriptor.Decompress(input, restored);
    return restored.ToArray();
  }

  // Long runs plus sprinkled noise drive the rank and run exponent paths past a
  // single bit, which is where the exponent mixer index actually varies.
  private static byte[] SkewedSample() {
    const string pattern = "abracadabra the quick brown fox ";
    var data = new byte[40_000];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)pattern[i % pattern.Length];
    var random = new Random(1234);
    for (var i = 0; i < 2_000; ++i)
      data[random.Next(data.Length)] = (byte)random.Next(256);
    return data;
  }
}
