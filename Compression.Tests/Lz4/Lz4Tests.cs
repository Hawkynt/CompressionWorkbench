using Compression.Core.Dictionary.Lz4;
using FileFormat.Lz4;

namespace Compression.Tests.Lz4;

[TestFixture]
public class Lz4BlockTests {
  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SmallData() {
    var data = "Hello, LZ4 compression!"u8.ToArray();
    var compressed = Lz4BlockCompressor.Compress(data);
    var decompressed = Lz4BlockDecompressor.Decompress(compressed, data.Length);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_RepeatedData() {
    var data = new byte[1000];
    Array.Fill(data, (byte)0xAB);
    var compressed = Lz4BlockCompressor.Compress(data);
    Assert.That(compressed.Length, Is.LessThan(data.Length));
    var decompressed = Lz4BlockDecompressor.Decompress(compressed, data.Length);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_PatternData() {
    var data = new byte[2000];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 13);
    var compressed = Lz4BlockCompressor.Compress(data);
    var decompressed = Lz4BlockDecompressor.Decompress(compressed, data.Length);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_RandomData() {
    var rng = new Random(42);
    var data = new byte[5000];
    rng.NextBytes(data);
    var compressed = Lz4BlockCompressor.Compress(data);
    var decompressed = Lz4BlockDecompressor.Decompress(compressed, data.Length);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("EdgeCase")]
  [Test]
  public void Compress_Empty_ReturnsEmpty() {
    var compressed = Lz4BlockCompressor.Compress([]);
    Assert.That(compressed, Is.Empty);
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SingleByte() {
    byte[] data = [42];
    var compressed = Lz4BlockCompressor.Compress(data);
    var decompressed = Lz4BlockDecompressor.Decompress(compressed, data.Length);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_LongRepeats() {
    var data = new byte[100000];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 3);
    var compressed = Lz4BlockCompressor.Compress(data);
    Assert.That(compressed.Length, Is.LessThan(data.Length / 2));
    var decompressed = Lz4BlockDecompressor.Decompress(compressed, data.Length);
    Assert.That(decompressed, Is.EqualTo(data));
  }
}

[TestFixture]
public class Lz4HcBlockTests {
  [Category("HappyPath")]
  [Category("RoundTrip")]
  [TestCase(Lz4CompressionLevel.Hc)]
  [TestCase(Lz4CompressionLevel.Max)]
  public void RoundTrip_PatternData(Lz4CompressionLevel level) {
    var data = new byte[5000];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 13);

    var compressed = Lz4BlockCompressor.Compress(data, level);
    var decompressed = Lz4BlockDecompressor.Decompress(compressed, data.Length);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [TestCase(Lz4CompressionLevel.Hc)]
  [TestCase(Lz4CompressionLevel.Max)]
  public void RoundTrip_RandomData(Lz4CompressionLevel level) {
    var rng = new Random(42);
    var data = new byte[10000];
    rng.NextBytes(data);

    var compressed = Lz4BlockCompressor.Compress(data, level);
    var decompressed = Lz4BlockDecompressor.Decompress(compressed, data.Length);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Hc_CompressesBetter_ThanFast() {
    var pattern = "The quick brown fox jumps over the lazy dog. "u8.ToArray();
    var data = new byte[pattern.Length * 200];
    for (var i = 0; i < 200; ++i)
      Array.Copy(pattern, 0, data, i * pattern.Length, pattern.Length);

    var fast = Lz4BlockCompressor.Compress(data, Lz4CompressionLevel.Fast);
    var hc = Lz4BlockCompressor.Compress(data, Lz4CompressionLevel.Hc);

    Assert.That(hc.Length, Is.LessThanOrEqualTo(fast.Length),
      $"HC ({hc.Length}) should be <= Fast ({fast.Length})");

    // Both should decompress correctly
    var d1 = Lz4BlockDecompressor.Decompress(fast, data.Length);
    var d2 = Lz4BlockDecompressor.Decompress(hc, data.Length);
    Assert.That(d1, Is.EqualTo(data));
    Assert.That(d2, Is.EqualTo(data));
  }

  [Category("EdgeCase")]
  [TestCase(Lz4CompressionLevel.Hc)]
  [TestCase(Lz4CompressionLevel.Max)]
  public void RoundTrip_Empty(Lz4CompressionLevel level) {
    var compressed = Lz4BlockCompressor.Compress([], level);
    Assert.That(compressed, Is.Empty);
  }
}

[TestFixture]
public class Lz4FrameTests {
  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SmallData() {
    var data = "Hello, LZ4 frame format!"u8.ToArray();
    using var ms = new MemoryStream();
    new Lz4FrameWriter(ms).Write(data);

    ms.Position = 0;
    var result = new Lz4FrameReader(ms).Read();
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
    new Lz4FrameWriter(ms, blockMaxSize: 65536).Write(data);

    ms.Position = 0;
    var result = new Lz4FrameReader(ms).Read();
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
    new Lz4FrameWriter(ms).Write(data);

    ms.Position = 0;
    var result = new Lz4FrameReader(ms).Read();
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_Empty() {
    byte[] data = [];
    using var ms = new MemoryStream();
    new Lz4FrameWriter(ms).Write(data);

    ms.Position = 0;
    var result = new Lz4FrameReader(ms).Read();
    Assert.That(result, Is.Empty);
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_WithBlockChecksums() {
    var data = new byte[5000];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 37);

    using var ms = new MemoryStream();
    new Lz4FrameWriter(ms, blockChecksum: true).Write(data);

    ms.Position = 0;
    var result = new Lz4FrameReader(ms).Read();
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("Exception")]
  [Test]
  public void Read_CorruptHeaderChecksum_Throws() {
    var data = "test header checksum"u8.ToArray();
    using var ms = new MemoryStream();
    new Lz4FrameWriter(ms).Write(data);

    // Corrupt the header checksum byte (last byte of the frame descriptor, at offset 14)
    var frame = ms.ToArray();
    frame[14] ^= 0xFF;

    Assert.Throws<InvalidDataException>(() =>
      new Lz4FrameReader(new MemoryStream(frame)).Read());
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [TestCase(Lz4CompressionLevel.Hc)]
  [TestCase(Lz4CompressionLevel.Max)]
  public void RoundTrip_Frame_Hc(Lz4CompressionLevel level) {
    var data = new byte[50000];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 251);

    using var ms = new MemoryStream();
    new Lz4FrameWriter(ms, level: level).Write(data);

    ms.Position = 0;
    var result = new Lz4FrameReader(ms).Read();
    Assert.That(result, Is.EqualTo(data));
  }
}
