using Compression.Core.Dictionary.Snappy;
using FileFormat.Snappy;

namespace Compression.Tests.Snappy;

[TestFixture]
public class SnappyBlockTests {
  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SmallData() {
    var data = "Hello, Snappy compression!"u8.ToArray();
    var compressed = SnappyCompressor.Compress(data);
    var decompressed = SnappyDecompressor.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_RepeatedData() {
    var data = new byte[1000];
    Array.Fill(data, (byte)0xCD);
    var compressed = SnappyCompressor.Compress(data);
    Assert.That(compressed.Length, Is.LessThan(data.Length));
    var decompressed = SnappyDecompressor.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_PatternData() {
    var data = new byte[2000];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 17);
    var compressed = SnappyCompressor.Compress(data);
    var decompressed = SnappyDecompressor.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_RandomData() {
    var rng = new Random(42);
    var data = new byte[5000];
    rng.NextBytes(data);
    var compressed = SnappyCompressor.Compress(data);
    var decompressed = SnappyDecompressor.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("EdgeCase")]
  [Test]
  public void Compress_Empty_ReturnsVarintZero() {
    var compressed = SnappyCompressor.Compress([]);
    Assert.That(compressed, Has.Length.EqualTo(1));
    Assert.That(compressed[0], Is.EqualTo(0));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SingleByte() {
    byte[] data = [0xFF];
    var compressed = SnappyCompressor.Compress(data);
    var decompressed = SnappyDecompressor.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_LongRepeats() {
    var data = new byte[50000];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 5);
    var compressed = SnappyCompressor.Compress(data);
    var decompressed = SnappyDecompressor.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }
}

[TestFixture]
public class SnappyFrameTests {
  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SmallData() {
    var data = "Hello, Snappy frame format!"u8.ToArray();
    using var ms = new MemoryStream();
    new SnappyFrameWriter(ms).Write(data);

    ms.Position = 0;
    var result = new SnappyFrameReader(ms).Read();
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_LargeData() {
    var data = new byte[200000];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 251);

    using var ms = new MemoryStream();
    new SnappyFrameWriter(ms).Write(data);

    ms.Position = 0;
    var result = new SnappyFrameReader(ms).Read();
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_RandomData() {
    var rng = new Random(99);
    var data = new byte[10000];
    rng.NextBytes(data);

    using var ms = new MemoryStream();
    new SnappyFrameWriter(ms).Write(data);

    ms.Position = 0;
    var result = new SnappyFrameReader(ms).Read();
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_Empty() {
    byte[] data = [];
    using var ms = new MemoryStream();
    new SnappyFrameWriter(ms).Write(data);

    ms.Position = 0;
    var result = new SnappyFrameReader(ms).Read();
    Assert.That(result, Is.Empty);
  }
}
