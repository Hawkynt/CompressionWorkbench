using System.Buffers.Binary;
using FileFormat.Yaz0;

namespace Compression.Tests.Yaz0;

[TestFixture]
public class Yaz0Tests {
  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SimpleText() {
    var data = "Hello, Yaz0 compression! This is a test of the Yaz0 format."u8.ToArray();
    using var compressed = new MemoryStream();
    Yaz0Stream.Compress(new MemoryStream(data), compressed);

    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    Yaz0Stream.Decompress(compressed, decompressed);

    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_AllByteValues() {
    var data = new byte[256];
    for (var i = 0; i < 256; ++i)
      data[i] = (byte)i;

    using var compressed = new MemoryStream();
    Yaz0Stream.Compress(new MemoryStream(data), compressed);

    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    Yaz0Stream.Decompress(compressed, decompressed);

    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_RepetitiveData() {
    var data = new byte[1000];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 7);

    using var compressed = new MemoryStream();
    Yaz0Stream.Compress(new MemoryStream(data), compressed);

    Assert.That(compressed.Length, Is.LessThan(data.Length),
      "Repetitive data should compress well");

    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    Yaz0Stream.Decompress(compressed, decompressed);

    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Test]
  public void Magic_IsYaz0() {
    var data = "test"u8.ToArray();
    using var compressed = new MemoryStream();
    Yaz0Stream.Compress(new MemoryStream(data), compressed);

    var bytes = compressed.ToArray();
    Assert.That(bytes[0], Is.EqualTo((byte)'Y'));
    Assert.That(bytes[1], Is.EqualTo((byte)'a'));
    Assert.That(bytes[2], Is.EqualTo((byte)'z'));
    Assert.That(bytes[3], Is.EqualTo((byte)'0'));
  }

  [Category("HappyPath")]
  [Test]
  public void Header_HasCorrectSize() {
    var data = new byte[12345];
    new Random(42).NextBytes(data);

    using var compressed = new MemoryStream();
    Yaz0Stream.Compress(new MemoryStream(data), compressed);

    var bytes = compressed.ToArray();
    var storedSize = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4));
    Assert.That(storedSize, Is.EqualTo(12345u));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SingleByte() {
    byte[] data = [0xAB];
    using var compressed = new MemoryStream();
    Yaz0Stream.Compress(new MemoryStream(data), compressed);

    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    Yaz0Stream.Decompress(compressed, decompressed);

    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_LargeRepetitive() {
    // 64KB of repeated 16-byte pattern.
    var data = new byte[65536];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 16);

    using var compressed = new MemoryStream();
    Yaz0Stream.Compress(new MemoryStream(data), compressed);

    Assert.That(compressed.Length, Is.LessThan(data.Length / 2),
      "64KB of repeated pattern should achieve at least 50% compression");

    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    Yaz0Stream.Decompress(compressed, decompressed);

    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_OverlappingMatch() {
    // Data like AAAAAA... requires the decompressor to handle overlapping copies
    // where the match source overlaps the destination.
    var data = new byte[500];
    Array.Fill(data, (byte)'A');

    using var compressed = new MemoryStream();
    Yaz0Stream.Compress(new MemoryStream(data), compressed);

    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    Yaz0Stream.Decompress(compressed, decompressed);

    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }
}
