using Compression.Core.Transforms;

namespace Compression.Tests.Transforms;

[TestFixture]
public class MtfTests {
  [Test]
  public void Encode_Decode_RoundTrip_EmptyData() {
    byte[] data = [];
    byte[] encoded = MoveToFrontTransform.Encode(data);
    byte[] result = MoveToFrontTransform.Decode(encoded);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void Encode_Decode_RoundTrip_SingleByte() {
    byte[] data = [42];
    byte[] encoded = MoveToFrontTransform.Encode(data);
    byte[] result = MoveToFrontTransform.Decode(encoded);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void Encode_Decode_RoundTrip_Text() {
    byte[] data = "Hello, World!"u8.ToArray();
    byte[] encoded = MoveToFrontTransform.Encode(data);
    byte[] result = MoveToFrontTransform.Decode(encoded);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void Encode_Decode_RoundTrip_Repetitive() {
    byte[] data = new byte[1000];
    for (int i = 0; i < data.Length; i++)
      data[i] = (byte)(i % 3);

    byte[] encoded = MoveToFrontTransform.Encode(data);
    byte[] result = MoveToFrontTransform.Decode(encoded);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void Encode_Decode_RoundTrip_Random() {
    var rng = new Random(42);
    byte[] data = new byte[1024];
    rng.NextBytes(data);

    byte[] encoded = MoveToFrontTransform.Encode(data);
    byte[] result = MoveToFrontTransform.Decode(encoded);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void Encode_Decode_RoundTrip_AllZeros() {
    byte[] data = new byte[1000];

    byte[] encoded = MoveToFrontTransform.Encode(data);
    byte[] result = MoveToFrontTransform.Decode(encoded);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void Encode_RepetitiveData_ProducesZeros() {
    // After BWT, repetitive data produces runs of same bytes,
    // which MTF turns into zeros
    byte[] data = new byte[100];
    Array.Fill(data, (byte)'a');

    byte[] encoded = MoveToFrontTransform.Encode(data);

    // First byte is the index of 'a', rest should be 0
    var zeroCount = encoded.Count(b => b == 0);
    Assert.That(zeroCount, Is.EqualTo(99)); // All but first
  }

  [Test]
  public void BwtThenMtf_RoundTrip() {
    byte[] data = "the quick brown fox jumps over the lazy dog"u8.ToArray();

    // Forward: BWT then MTF
    var (bwtData, bwtIndex) = BurrowsWheelerTransform.Forward(data);
    byte[] mtfData = MoveToFrontTransform.Encode(bwtData);

    // Inverse: MTF decode then BWT inverse
    byte[] bwtRecovered = MoveToFrontTransform.Decode(mtfData);
    byte[] result = BurrowsWheelerTransform.Inverse(bwtRecovered, bwtIndex);

    Assert.That(result, Is.EqualTo(data));
  }
}
