using FileFormat.Zstd;

namespace Compression.Tests.Zstd;

/// <summary>
/// The Huffman tree description of Zstandard literals (RFC 8878, 4.2.1.1): the last weight is
/// implicit and completes the sum of 2^(Weight-1) to the next power of two strictly above it.
/// </summary>
[TestFixture]
public sealed class ZstdHuffmanWeightTests {
  /// <summary>
  /// Direct weights 1, 1, 2 sum to 4, itself a power of two: Max_Number_of_Bits is 3 and the
  /// implicit fourth weight is 3 (codes 3, 3, 2 and 1 bits long). The one-byte stream 0b11 is the
  /// sentinel and the 1-bit code of symbol 3.
  /// </summary>
  [Test]
  [Category("Boundary")]
  public void Given_WeightsSummingToAPowerOfTwo_When_Decoded_Then_TheLastWeightCompletesTheNextPower() {
    byte[] section = [127 + 3, 0x11, 0x20, 0b0000_0011];
    var (literals, table) = ZstdHuffmanLiterals.Decode(section, 1, fourStreams: false);
    Assert.That(literals, Is.EqualTo(new byte[] { 3 }));
    Assert.That(table.MaxBits, Is.EqualTo(3));
  }

  /// <summary>Weights 1, 1, 1 sum to 3: the next power is 4 and the implicit weight is 1.</summary>
  [Test]
  [Category("HappyPath")]
  public void Given_WeightsBelowAPowerOfTwo_When_Decoded_Then_TheLastWeightFillsTheGap() {
    // Codes: all four symbols 2 bits; symbol 0 is code 00. Stream: sentinel then 00.
    byte[] section = [127 + 3, 0x11, 0x10, 0b0000_0100];
    var (literals, table) = ZstdHuffmanLiterals.Decode(section, 1, fourStreams: false);
    Assert.That(literals, Is.EqualTo(new byte[] { 0 }));
    Assert.That(table.MaxBits, Is.EqualTo(2));
  }

  /// <summary>Weights whose gap to the next power of two is not itself a power of two are corrupt.</summary>
  [Test]
  [Category("EdgeCase")]
  public void Given_AGapThatIsNoPowerOfTwo_When_Decoded_Then_InvalidData() {
    // 2 + 1 + 2 = 5: the next power is 8, the gap 3 is no single weight.
    byte[] section = [127 + 3, 0x21, 0x20, 0b0000_0001];
    Assert.Throws<InvalidDataException>(() => ZstdHuffmanLiterals.Decode(section, 1, fourStreams: false));
  }
}
