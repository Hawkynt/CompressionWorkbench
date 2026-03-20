using Compression.Core.Transforms;

namespace Compression.Tests.Transforms;

[TestFixture]
public class RleTests {
  [Category("EdgeCase")]
  [Test]
  public void Encode_EmptyData_ReturnsEmpty() {
    Assert.That(RunLengthEncoding.Encode([]), Is.Empty);
  }

  [Category("EdgeCase")]
  [Test]
  public void Decode_EmptyData_ReturnsEmpty() {
    Assert.That(RunLengthEncoding.Decode([]), Is.Empty);
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SingleByte() {
    byte[] data = [42];
    byte[] encoded = RunLengthEncoding.Encode(data);
    byte[] decoded = RunLengthEncoding.Decode(encoded);
    Assert.That(decoded, Is.EqualTo(data));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_AllSameBytes() {
    byte[] data = new byte[100];
    Array.Fill(data, (byte)0xAB);
    byte[] encoded = RunLengthEncoding.Encode(data);
    byte[] decoded = RunLengthEncoding.Decode(encoded);
    Assert.That(decoded, Is.EqualTo(data));
    Assert.That(encoded.Length, Is.EqualTo(2));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_NoRepeats() {
    byte[] data = [1, 2, 3, 4, 5];
    byte[] encoded = RunLengthEncoding.Encode(data);
    byte[] decoded = RunLengthEncoding.Decode(encoded);
    Assert.That(decoded, Is.EqualTo(data));
    Assert.That(encoded.Length, Is.EqualTo(10));
  }

  [Category("Boundary")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_LongRun_SplitsAt255() {
    byte[] data = new byte[300];
    Array.Fill(data, (byte)0xFF);
    byte[] encoded = RunLengthEncoding.Encode(data);
    byte[] decoded = RunLengthEncoding.Decode(encoded);
    Assert.That(decoded, Is.EqualTo(data));
    Assert.That(encoded.Length, Is.EqualTo(4));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_MixedData() {
    byte[] data = [0, 0, 0, 1, 1, 2, 2, 2, 2, 3];
    byte[] encoded = RunLengthEncoding.Encode(data);
    byte[] decoded = RunLengthEncoding.Decode(encoded);
    Assert.That(decoded, Is.EqualTo(data));
  }

  [Category("ThemVsUs")]
  [Test]
  public void Encode_Format_CorrectPairs() {
    byte[] data = [0xAA, 0xAA, 0xAA, 0xBB, 0xBB];
    byte[] encoded = RunLengthEncoding.Encode(data);
    Assert.That(encoded, Is.EqualTo(new byte[] { 3, 0xAA, 2, 0xBB }));
  }

  [Category("Exception")]
  [Test]
  public void Decode_OddLength_ThrowsInvalidData() {
    Assert.Throws<InvalidDataException>(() => RunLengthEncoding.Decode([1, 2, 3]));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_RandomData() {
    var rng = new Random(42);
    byte[] data = new byte[1000];
    rng.NextBytes(data);
    byte[] encoded = RunLengthEncoding.Encode(data);
    byte[] decoded = RunLengthEncoding.Decode(encoded);
    Assert.That(decoded, Is.EqualTo(data));
  }
}
