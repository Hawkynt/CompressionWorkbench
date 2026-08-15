#pragma warning disable CS1591
using Compression.Core.Dictionary.Lzma;

namespace Compression.Tests.Lzma;

/// <summary>
/// Covers <see cref="LzmaBuildingBlock.DecompressRaw"/>, the entry point for LZMA1 data
/// that arrives without the 13-byte container — the shape executable packers embed, where
/// lc/lp/pb and the uncompressed size come from a header of the packer's own.
/// </summary>
[TestFixture]
public class LzmaRawStreamTests {

  private static byte[] EncodeRaw(byte[] data, int lc, int lp, int pb) {
    var encoder = new LzmaEncoder(dictionarySize: 1 << 16, lc: lc, lp: lp, pb: pb);
    using var packed = new MemoryStream();
    encoder.Encode(packed, data);
    return packed.ToArray();
  }

  private static byte[] SampleData(int length) {
    var data = new byte[length];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i * 7 % 91 + (i / 512 % 3));
    return data;
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [TestCase(3, 0, 2)]
  [TestCase(0, 0, 0)]
  [TestCase(4, 0, 2)]
  [TestCase(8, 0, 4)]
  [TestCase(0, 4, 0)]
  public void RoundTrip_HonoursSuppliedCodingParameters(int lc, int lp, int pb) {
    var data = SampleData(24000);

    var packed = EncodeRaw(data, lc, lp, pb);
    var unpacked = LzmaBuildingBlock.DecompressRaw(packed, lc, lp, pb, data.Length);

    Assert.That(unpacked, Is.EqualTo(data).AsCollection);
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_RandomData() {
    var data = new byte[8192];
    new Random(1337).NextBytes(data);

    var packed = EncodeRaw(data, 3, 0, 2);
    var unpacked = LzmaBuildingBlock.DecompressRaw(packed, 3, 0, 2, data.Length);

    Assert.That(unpacked, Is.EqualTo(data).AsCollection);
  }

  [Category("EdgeCase")]
  [Test]
  public void DecodesWhenTheStreamIsFollowedByAnImplicitZeroTail() {
    // A packer's stream sits in a section whose virtual size exceeds its raw size, so the
    // last bytes the range coder asks for may fall into the zero fill. Cutting the tail off
    // must not change the result.
    var data = SampleData(12000);
    var packed = EncodeRaw(data, 3, 0, 2);

    var truncated = packed[..(packed.Length - 3)];
    var unpacked = LzmaBuildingBlock.DecompressRaw(truncated, 3, 0, 2, data.Length);

    Assert.That(unpacked, Is.EqualTo(data).AsCollection);
  }

  [Category("EdgeCase")]
  [Test]
  public void ShortStream_Throws() {
    // Cut off far enough and the zero tail decodes into nonsense: either a distance the
    // window cannot serve or an output that ends early. Both must surface as an exception
    // rather than as a short, silently truncated result.
    var data = SampleData(12000);
    var packed = EncodeRaw(data, 3, 0, 2);

    Assert.That(() => LzmaBuildingBlock.DecompressRaw(packed[..64], 3, 0, 2, data.Length),
      Throws.InstanceOf<InvalidDataException>().Or.InstanceOf<ArgumentOutOfRangeException>());
  }

  [Category("EdgeCase")]
  [TestCase(9, 0, 2)]
  [TestCase(3, 5, 2)]
  [TestCase(3, 0, 5)]
  [TestCase(-1, 0, 2)]
  public void ParametersOutsideTheLzmaRanges_Throw(int lc, int lp, int pb) {
    Assert.That(() => LzmaBuildingBlock.DecompressRaw([1, 2, 3, 4, 5, 6, 7, 8], lc, lp, pb, 16),
      Throws.InstanceOf<ArgumentOutOfRangeException>());
  }
}
