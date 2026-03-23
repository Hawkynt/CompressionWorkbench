using Compression.Core.Transforms;

namespace Compression.Tests.Transforms;

[TestFixture]
public class MtfTests {
  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void Encode_Decode_RoundTrip_EmptyData() {
    byte[] data = [];
    var encoded = MoveToFrontTransform.Encode(data);
    var result = MoveToFrontTransform.Decode(encoded);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void Encode_Decode_RoundTrip_SingleByte() {
    byte[] data = [42];
    var encoded = MoveToFrontTransform.Encode(data);
    var result = MoveToFrontTransform.Decode(encoded);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Encode_Decode_RoundTrip_Text() {
    var data = "Hello, World!"u8.ToArray();
    var encoded = MoveToFrontTransform.Encode(data);
    var result = MoveToFrontTransform.Decode(encoded);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Encode_Decode_RoundTrip_Repetitive() {
    var data = new byte[1000];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 3);

    var encoded = MoveToFrontTransform.Encode(data);
    var result = MoveToFrontTransform.Decode(encoded);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Encode_Decode_RoundTrip_Random() {
    var rng = new Random(42);
    var data = new byte[1024];
    rng.NextBytes(data);

    var encoded = MoveToFrontTransform.Encode(data);
    var result = MoveToFrontTransform.Decode(encoded);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void Encode_Decode_RoundTrip_AllZeros() {
    var data = new byte[1000];

    var encoded = MoveToFrontTransform.Encode(data);
    var result = MoveToFrontTransform.Decode(encoded);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("EdgeCase")]
  [Test]
  public void Encode_RepetitiveData_ProducesZeros() {
    // After BWT, repetitive data produces runs of same bytes,
    // which MTF turns into zeros
    var data = new byte[100];
    Array.Fill(data, (byte)'a');

    var encoded = MoveToFrontTransform.Encode(data);

    // First byte is the index of 'a', rest should be 0
    var zeroCount = encoded.Count(b => b == 0);
    Assert.That(zeroCount, Is.EqualTo(99)); // All but first
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void BwtThenMtf_RoundTrip() {
    var data = "the quick brown fox jumps over the lazy dog"u8.ToArray();

    // Forward: BWT then MTF
    var (bwtData, bwtIndex) = BurrowsWheelerTransform.Forward(data);
    var mtfData = MoveToFrontTransform.Encode(bwtData);

    // Inverse: MTF decode then BWT inverse
    var bwtRecovered = MoveToFrontTransform.Decode(mtfData);
    var result = BurrowsWheelerTransform.Inverse(bwtRecovered, bwtIndex);

    Assert.That(result, Is.EqualTo(data));
  }
}
