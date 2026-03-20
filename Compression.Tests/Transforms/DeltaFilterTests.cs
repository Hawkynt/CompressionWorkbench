using Compression.Core.Transforms;

namespace Compression.Tests.Transforms;

[TestFixture]
public class DeltaFilterTests {
  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Encode_Decode_RoundTrip_Distance1() {
    var data = new byte[] { 10, 20, 30, 25, 15, 5 };
    var encoded = DeltaFilter.Encode(data);
    var decoded = DeltaFilter.Decode(encoded);
    Assert.That(decoded, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Encode_Decode_RoundTrip_Distance2() {
    var data = new byte[] { 10, 20, 30, 25, 15, 5, 100, 200 };
    var encoded = DeltaFilter.Encode(data, distance: 2);
    var decoded = DeltaFilter.Decode(encoded, distance: 2);
    Assert.That(decoded, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Encode_Decode_RoundTrip_Distance4() {
    var data = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };
    var encoded = DeltaFilter.Encode(data, distance: 4);
    var decoded = DeltaFilter.Decode(encoded, distance: 4);
    Assert.That(decoded, Is.EqualTo(data));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void Encode_Decode_RoundTrip_Empty() {
    var encoded = DeltaFilter.Encode(ReadOnlySpan<byte>.Empty);
    var decoded = DeltaFilter.Decode(encoded);
    Assert.That(decoded, Is.Empty);
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Encode_Decode_RoundTrip_Random() {
    var data = new byte[1024];
    new Random(42).NextBytes(data);

    var encoded = DeltaFilter.Encode(data);
    var decoded = DeltaFilter.Decode(encoded);
    Assert.That(decoded, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Test]
  public void Encode_SequentialData_ProducesConstants() {
    var data = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 };
    var encoded = DeltaFilter.Encode(data);
    // First byte unchanged (0), rest should all be 1
    Assert.That(encoded[0], Is.EqualTo(0));
    for (int i = 1; i < encoded.Length; ++i)
      Assert.That(encoded[i], Is.EqualTo(1));
  }

  [Category("EdgeCase")]
  [Test]
  public void Encode_ConstantData_ProducesZeros() {
    var data = new byte[] { 42, 42, 42, 42, 42 };
    var encoded = DeltaFilter.Encode(data);
    Assert.That(encoded[0], Is.EqualTo(42));
    for (int i = 1; i < encoded.Length; ++i)
      Assert.That(encoded[i], Is.EqualTo(0));
  }

  [Category("EdgeCase")]
  [Test]
  public void Encode_SingleByte() {
    var data = new byte[] { 99 };
    var encoded = DeltaFilter.Encode(data);
    Assert.That(encoded, Is.EqualTo(new byte[] { 99 }));
  }

  [Category("Boundary")]
  [Category("RoundTrip")]
  [Test]
  public void Encode_Decode_RoundTrip_Distance_LargerThanData() {
    var data = new byte[] { 10, 20, 30 };
    // Distance 5 is larger than data length, so all bytes are copied unchanged
    var encoded = DeltaFilter.Encode(data, distance: 5);
    Assert.That(encoded, Is.EqualTo(data));
    var decoded = DeltaFilter.Decode(encoded, distance: 5);
    Assert.That(decoded, Is.EqualTo(data));
  }

  [Category("Exception")]
  [Test]
  public void Encode_InvalidDistance_Throws() {
    Assert.That(() => DeltaFilter.Encode(new byte[] { 1, 2, 3 }, distance: 0),
      Throws.TypeOf<ArgumentOutOfRangeException>());
  }

  [Category("Exception")]
  [Test]
  public void Decode_InvalidDistance_Throws() {
    Assert.That(() => DeltaFilter.Decode(new byte[] { 1, 2, 3 }, distance: 0),
      Throws.TypeOf<ArgumentOutOfRangeException>());
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Encode_Decode_RoundTrip_Random_Distance4() {
    var data = new byte[512];
    new Random(77).NextBytes(data);

    var encoded = DeltaFilter.Encode(data, distance: 4);
    var decoded = DeltaFilter.Decode(encoded, distance: 4);
    Assert.That(decoded, Is.EqualTo(data));
  }

  [Category("Boundary")]
  [Category("RoundTrip")]
  [Test]
  public void Encode_ByteOverflow_HandledCorrectly() {
    // Test that byte arithmetic wraps around correctly
    var data = new byte[] { 200, 10 };
    var encoded = DeltaFilter.Encode(data);
    // 10 - 200 = -190 -> wraps to 66 as a byte (256 - 190)
    Assert.That(encoded[0], Is.EqualTo(200));
    Assert.That(encoded[1], Is.EqualTo(unchecked((byte)(10 - 200))));

    var decoded = DeltaFilter.Decode(encoded);
    Assert.That(decoded, Is.EqualTo(data));
  }
}
