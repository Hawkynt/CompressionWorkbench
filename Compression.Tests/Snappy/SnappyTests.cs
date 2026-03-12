using Compression.Core.Dictionary.Snappy;
using FileFormat.Snappy;

namespace Compression.Tests.Snappy;

[TestFixture]
public class SnappyBlockTests {
  [Test]
  public void RoundTrip_SmallData() {
    byte[] data = "Hello, Snappy compression!"u8.ToArray();
    byte[] compressed = SnappyCompressor.Compress(data);
    byte[] decompressed = SnappyDecompressor.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Test]
  public void RoundTrip_RepeatedData() {
    byte[] data = new byte[1000];
    Array.Fill(data, (byte)0xCD);
    byte[] compressed = SnappyCompressor.Compress(data);
    Assert.That(compressed.Length, Is.LessThan(data.Length));
    byte[] decompressed = SnappyDecompressor.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Test]
  public void RoundTrip_PatternData() {
    byte[] data = new byte[2000];
    for (int i = 0; i < data.Length; i++)
      data[i] = (byte)(i % 17);
    byte[] compressed = SnappyCompressor.Compress(data);
    byte[] decompressed = SnappyDecompressor.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Test]
  public void RoundTrip_RandomData() {
    var rng = new Random(42);
    byte[] data = new byte[5000];
    rng.NextBytes(data);
    byte[] compressed = SnappyCompressor.Compress(data);
    byte[] decompressed = SnappyDecompressor.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Test]
  public void Compress_Empty_ReturnsVarintZero() {
    byte[] compressed = SnappyCompressor.Compress([]);
    Assert.That(compressed, Has.Length.EqualTo(1));
    Assert.That(compressed[0], Is.EqualTo(0));
  }

  [Test]
  public void RoundTrip_SingleByte() {
    byte[] data = [0xFF];
    byte[] compressed = SnappyCompressor.Compress(data);
    byte[] decompressed = SnappyDecompressor.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Test]
  public void RoundTrip_LongRepeats() {
    byte[] data = new byte[50000];
    for (int i = 0; i < data.Length; i++)
      data[i] = (byte)(i % 5);
    byte[] compressed = SnappyCompressor.Compress(data);
    byte[] decompressed = SnappyDecompressor.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }
}

[TestFixture]
public class SnappyFrameTests {
  [Test]
  public void RoundTrip_SmallData() {
    byte[] data = "Hello, Snappy frame format!"u8.ToArray();
    using var ms = new MemoryStream();
    new SnappyFrameWriter(ms).Write(data);

    ms.Position = 0;
    byte[] result = new SnappyFrameReader(ms).Read();
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void RoundTrip_LargeData() {
    byte[] data = new byte[200000];
    for (int i = 0; i < data.Length; i++)
      data[i] = (byte)(i % 251);

    using var ms = new MemoryStream();
    new SnappyFrameWriter(ms).Write(data);

    ms.Position = 0;
    byte[] result = new SnappyFrameReader(ms).Read();
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void RoundTrip_RandomData() {
    var rng = new Random(99);
    byte[] data = new byte[10000];
    rng.NextBytes(data);

    using var ms = new MemoryStream();
    new SnappyFrameWriter(ms).Write(data);

    ms.Position = 0;
    byte[] result = new SnappyFrameReader(ms).Read();
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void RoundTrip_Empty() {
    byte[] data = [];
    using var ms = new MemoryStream();
    new SnappyFrameWriter(ms).Write(data);

    ms.Position = 0;
    byte[] result = new SnappyFrameReader(ms).Read();
    Assert.That(result, Is.Empty);
  }
}
