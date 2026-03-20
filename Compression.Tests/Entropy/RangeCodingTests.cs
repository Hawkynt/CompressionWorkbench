using Compression.Core.Entropy.RangeCoding;

namespace Compression.Tests.Entropy;

[TestFixture]
public class RangeCodingTests {
  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void EncodeDecode_SingleBit_RoundTrips() {
    using var ms = new MemoryStream();
    var encoder = new RangeEncoder(ms);
    int prob = RangeEncoder.ProbInitValue;
    encoder.EncodeBit(ref prob, 1);
    encoder.Finish();

    ms.Position = 0;
    var decoder = new RangeDecoder(ms);
    int decodeProb = RangeEncoder.ProbInitValue;
    int bit = decoder.DecodeBit(ref decodeProb);
    Assert.That(bit, Is.EqualTo(1));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void EncodeDecode_MultipleBits_RoundTrips() {
    int[] bits = [0, 1, 1, 0, 1, 0, 0, 1, 1, 1, 0, 0];

    using var ms = new MemoryStream();
    var encoder = new RangeEncoder(ms);
    int prob = RangeEncoder.ProbInitValue;
    foreach (int bit in bits)
      encoder.EncodeBit(ref prob, bit);
    encoder.Finish();

    ms.Position = 0;
    var decoder = new RangeDecoder(ms);
    int decodeProb = RangeEncoder.ProbInitValue;
    int[] decoded = new int[bits.Length];
    for (int i = 0; i < bits.Length; ++i)
      decoded[i] = decoder.DecodeBit(ref decodeProb);

    Assert.That(decoded, Is.EqualTo(bits));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void EncodeDecode_DirectBits_RoundTrips() {
    using var ms = new MemoryStream();
    var encoder = new RangeEncoder(ms);
    encoder.EncodeDirectBits(0b10110, 5);
    encoder.EncodeDirectBits(0xFF, 8);
    encoder.EncodeDirectBits(0, 4);
    encoder.Finish();

    ms.Position = 0;
    var decoder = new RangeDecoder(ms);
    Assert.That(decoder.DecodeDirectBits(5), Is.EqualTo(0b10110));
    Assert.That(decoder.DecodeDirectBits(8), Is.EqualTo(0xFF));
    Assert.That(decoder.DecodeDirectBits(4), Is.EqualTo(0));
  }

  [Category("Boundary")]
  [Category("RoundTrip")]
  [Test]
  public void BitTree_EncodeDecode_RoundTrips() {
    // Test encoding/decoding all byte values 0..255
    using var ms = new MemoryStream();
    var encoder = new RangeEncoder(ms);
    var treeEncoder = new BitTreeEncoder(8);

    for (int v = 0; v < 256; ++v)
      treeEncoder.Encode(encoder, v);
    encoder.Finish();

    ms.Position = 0;
    var decoder = new RangeDecoder(ms);
    var treeDecoder = new BitTreeDecoder(8);

    for (int v = 0; v < 256; ++v) {
      int decoded = treeDecoder.Decode(decoder);
      Assert.That(decoded, Is.EqualTo(v), $"Mismatch at index {v}");
    }
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void BitTree_ReverseEncodeDecode_RoundTrips() {
    using var ms = new MemoryStream();
    var encoder = new RangeEncoder(ms);
    var treeEncoder = new BitTreeEncoder(4);

    for (int v = 0; v < 16; ++v)
      treeEncoder.ReverseEncode(encoder, v);
    encoder.Finish();

    ms.Position = 0;
    var decoder = new RangeDecoder(ms);
    var treeDecoder = new BitTreeDecoder(4);

    for (int v = 0; v < 16; ++v) {
      int decoded = treeDecoder.ReverseDecode(decoder);
      Assert.That(decoded, Is.EqualTo(v), $"Mismatch at index {v}");
    }
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void AdaptiveProbability_ConvergesForBiasedData() {
    using var ms = new MemoryStream();
    var encoder = new RangeEncoder(ms);

    // Encode mostly 0s — probability should converge toward 0
    int prob = RangeEncoder.ProbInitValue;
    for (int i = 0; i < 1000; ++i)
      encoder.EncodeBit(ref prob, 0);
    encoder.Finish();

    ms.Position = 0;
    var decoder = new RangeDecoder(ms);
    int decodeProb = RangeEncoder.ProbInitValue;
    for (int i = 0; i < 1000; ++i) {
      int bit = decoder.DecodeBit(ref decodeProb);
      Assert.That(bit, Is.EqualTo(0));
    }

    // The probability should have converged to near the maximum (2047)
    // since we're encoding all 0s
    Assert.That(decodeProb, Is.GreaterThan(1900));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void LargeData_RoundTrip() {
    var rng = new Random(42);
    int[] data = new int[2000];
    for (int i = 0; i < data.Length; ++i)
      data[i] = rng.Next(2);

    using var ms = new MemoryStream();
    var encoder = new RangeEncoder(ms);
    int prob = RangeEncoder.ProbInitValue;
    foreach (int bit in data)
      encoder.EncodeBit(ref prob, bit);
    encoder.Finish();

    ms.Position = 0;
    var decoder = new RangeDecoder(ms);
    int decodeProb = RangeEncoder.ProbInitValue;
    for (int i = 0; i < data.Length; ++i) {
      int bit = decoder.DecodeBit(ref decodeProb);
      Assert.That(bit, Is.EqualTo(data[i]), $"Mismatch at index {i}");
    }
  }

  [Category("EdgeCase")]
  [Test]
  public void EmptyStream_Works() {
    // Encode zero bits (just finish)
    using var ms = new MemoryStream();
    var encoder = new RangeEncoder(ms);
    encoder.Finish();

    Assert.That(ms.Length, Is.GreaterThan(0)); // At least the finish bytes
  }
}
