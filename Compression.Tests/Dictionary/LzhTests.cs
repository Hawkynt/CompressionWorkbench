using Compression.Core.Dictionary.Lzh;

namespace Compression.Tests.Dictionary;

[TestFixture]
public class LzhTests {
  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SmallData_Lh5() {
    var data = "Hello, LZH compression world!"u8.ToArray();
    var encoder = new LzhEncoder(LzhConstants.Lh5PositionBits);
    var compressed = encoder.Encode(data);

    using var ms = new MemoryStream(compressed);
    var decoder = new LzhDecoder(ms, LzhConstants.Lh5PositionBits);
    var decompressed = decoder.Decode(data.Length);

    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_RepeatedData_Lh5() {
    var data = new byte[1000];
    Array.Fill(data, (byte)0xAB);
    var encoder = new LzhEncoder(LzhConstants.Lh5PositionBits);
    var compressed = encoder.Encode(data);

    Assert.That(compressed.Length, Is.LessThan(data.Length), "Repeated data should compress well");

    using var ms = new MemoryStream(compressed);
    var decoder = new LzhDecoder(ms, LzhConstants.Lh5PositionBits);
    var decompressed = decoder.Decode(data.Length);

    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_PatternData_Lh5() {
    var data = new byte[2000];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 13);

    var encoder = new LzhEncoder(LzhConstants.Lh5PositionBits);
    var compressed = encoder.Encode(data);

    using var ms = new MemoryStream(compressed);
    var decoder = new LzhDecoder(ms, LzhConstants.Lh5PositionBits);
    var decompressed = decoder.Decode(data.Length);

    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_RandomData_Lh5() {
    var rng = new Random(42);
    var data = new byte[500];
    rng.NextBytes(data);

    var encoder = new LzhEncoder(LzhConstants.Lh5PositionBits);
    var compressed = encoder.Encode(data);

    using var ms = new MemoryStream(compressed);
    var decoder = new LzhDecoder(ms, LzhConstants.Lh5PositionBits);
    var decompressed = decoder.Decode(data.Length);

    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SmallData_Lh7() {
    var data = "Testing LZH with 64KB window size."u8.ToArray();
    var encoder = new LzhEncoder(LzhConstants.Lh7PositionBits);
    var compressed = encoder.Encode(data);

    using var ms = new MemoryStream(compressed);
    var decoder = new LzhDecoder(ms, LzhConstants.Lh7PositionBits);
    var decompressed = decoder.Decode(data.Length);

    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("EdgeCase")]
  [Test]
  public void Encode_EmptyData_ReturnsEmpty() {
    var encoder = new LzhEncoder();
    var compressed = encoder.Encode([]);
    Assert.That(compressed, Is.Empty);
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SingleByte() {
    byte[] data = [42];
    var encoder = new LzhEncoder();
    var compressed = encoder.Encode(data);

    using var ms = new MemoryStream(compressed);
    var decoder = new LzhDecoder(ms);
    var decompressed = decoder.Decode(data.Length);

    Assert.That(decompressed, Is.EqualTo(data));
  }
}
