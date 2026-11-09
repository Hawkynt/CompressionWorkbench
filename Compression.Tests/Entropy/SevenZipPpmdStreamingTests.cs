using Compression.Core.Entropy.Ppmd;

namespace Compression.Tests.Entropy;

/// <summary>
/// The streaming 7z PPMd encoder: the same bytes as the in-memory encoder for the same input,
/// order and memory — including memory small enough to restart the model many times — and they
/// decode back.
/// </summary>
[TestFixture]
public class SevenZipPpmdStreamingTests {
  [TestCase(0, 6, 1u << 20)]
  [TestCase(1, 6, 1u << 20)]
  [TestCase(200_000, 2, 1u << 11)]
  [TestCase(200_000, 8, 1u << 16)]
  [TestCase(1_500_000, 32, 1u << 20)]
  [TestCase(300_000, 64, 1u << 24)]
  public void Given_AnyInput_When_EncodedAsAStream_Then_TheBytesEqualTheInMemoryEncoder(int size, int order, uint memory) {
    var random = new Random(size + order);
    var data = new byte[size];
    for (var i = 0; i < size; ++i)
      data[i] = (byte)(random.Next(5) == 0 ? random.Next() : "the quick brown fox "[i % 20]);

    var expected = SevenZipPpmd.Encode(data, order, memory);
    using var actual = new MemoryStream();
    SevenZipPpmd.Encode(new MemoryStream(data), data.Length, actual, order, memory);

    Assert.That(actual.ToArray(), Is.EqualTo(expected));
    Assert.That(SevenZipPpmd.Decode(SevenZipPpmd.Properties(order, memory), actual.ToArray(), size), Is.EqualTo(data));
  }

  [Test]
  [Category("EdgeCase")]
  public void Given_AnInputShorterThanDeclared_When_Encoded_Then_ItIsRefused() {
    Assert.That(() => SevenZipPpmd.Encode(new MemoryStream(new byte[10]), 20, new MemoryStream(), 6, 1u << 20), Throws.TypeOf<EndOfStreamException>());
  }

  [Test]
  [Category("EdgeCase")]
  public void Given_ACallersStream_When_Encoded_Then_ItIsLeftOpen() {
    using var output = new MemoryStream();
    SevenZipPpmd.Encode(new MemoryStream(new byte[100]), 100, output, 6, 1u << 20);
    Assert.That(output.CanWrite, Is.True);
  }
}
