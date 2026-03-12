using Compression.Core.Dictionary.Lz4;
using FileFormat.Lz4;

namespace Compression.Tests.Lz4;

[TestFixture]
public class Lz4BlockTests {
  [Test]
  public void RoundTrip_SmallData() {
    byte[] data = "Hello, LZ4 compression!"u8.ToArray();
    byte[] compressed = Lz4BlockCompressor.Compress(data);
    byte[] decompressed = Lz4BlockDecompressor.Decompress(compressed, data.Length);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Test]
  public void RoundTrip_RepeatedData() {
    byte[] data = new byte[1000];
    Array.Fill(data, (byte)0xAB);
    byte[] compressed = Lz4BlockCompressor.Compress(data);
    Assert.That(compressed.Length, Is.LessThan(data.Length));
    byte[] decompressed = Lz4BlockDecompressor.Decompress(compressed, data.Length);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Test]
  public void RoundTrip_PatternData() {
    byte[] data = new byte[2000];
    for (int i = 0; i < data.Length; i++)
      data[i] = (byte)(i % 13);
    byte[] compressed = Lz4BlockCompressor.Compress(data);
    byte[] decompressed = Lz4BlockDecompressor.Decompress(compressed, data.Length);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Test]
  public void RoundTrip_RandomData() {
    var rng = new Random(42);
    byte[] data = new byte[5000];
    rng.NextBytes(data);
    byte[] compressed = Lz4BlockCompressor.Compress(data);
    byte[] decompressed = Lz4BlockDecompressor.Decompress(compressed, data.Length);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Test]
  public void Compress_Empty_ReturnsEmpty() {
    byte[] compressed = Lz4BlockCompressor.Compress([]);
    Assert.That(compressed, Is.Empty);
  }

  [Test]
  public void RoundTrip_SingleByte() {
    byte[] data = [42];
    byte[] compressed = Lz4BlockCompressor.Compress(data);
    byte[] decompressed = Lz4BlockDecompressor.Decompress(compressed, data.Length);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Test]
  public void RoundTrip_LongRepeats() {
    byte[] data = new byte[100000];
    for (int i = 0; i < data.Length; i++)
      data[i] = (byte)(i % 3);
    byte[] compressed = Lz4BlockCompressor.Compress(data);
    Assert.That(compressed.Length, Is.LessThan(data.Length / 2));
    byte[] decompressed = Lz4BlockDecompressor.Decompress(compressed, data.Length);
    Assert.That(decompressed, Is.EqualTo(data));
  }
}

[TestFixture]
public class Lz4FrameTests {
  [Test]
  public void RoundTrip_SmallData() {
    byte[] data = "Hello, LZ4 frame format!"u8.ToArray();
    using var ms = new MemoryStream();
    new Lz4FrameWriter(ms).Write(data);

    ms.Position = 0;
    byte[] result = new Lz4FrameReader(ms).Read();
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void RoundTrip_LargeData() {
    byte[] data = new byte[200000];
    for (int i = 0; i < data.Length; i++)
      data[i] = (byte)(i % 251);

    using var ms = new MemoryStream();
    new Lz4FrameWriter(ms, blockMaxSize: 65536).Write(data);

    ms.Position = 0;
    byte[] result = new Lz4FrameReader(ms).Read();
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void RoundTrip_RandomData() {
    var rng = new Random(99);
    byte[] data = new byte[10000];
    rng.NextBytes(data);

    using var ms = new MemoryStream();
    new Lz4FrameWriter(ms).Write(data);

    ms.Position = 0;
    byte[] result = new Lz4FrameReader(ms).Read();
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void RoundTrip_Empty() {
    byte[] data = [];
    using var ms = new MemoryStream();
    new Lz4FrameWriter(ms).Write(data);

    ms.Position = 0;
    byte[] result = new Lz4FrameReader(ms).Read();
    Assert.That(result, Is.Empty);
  }
}
