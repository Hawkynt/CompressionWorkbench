using System.Buffers.Binary;
using Compression.Core.Checksums;
using FileFormat.BriefLz;

namespace Compression.Tests.BriefLz;

[TestFixture]
public class BriefLzTests {

  [Test]
  public void RoundTrip_SimpleText() {
    var original = "Hello, BriefLZ compression! This is a test of the BriefLZ algorithm."u8.ToArray();
    var decompressed = RoundTrip(original);
    Assert.That(decompressed, Is.EqualTo(original));
  }

  [Test]
  public void RoundTrip_AllByteValues() {
    var original = new byte[256];
    for (var i = 0; i < 256; i++)
      original[i] = (byte)i;
    var decompressed = RoundTrip(original);
    Assert.That(decompressed, Is.EqualTo(original));
  }

  [Test]
  public void RoundTrip_RepetitiveData() {
    var original = new byte[2048];
    Array.Fill(original, (byte)'A');
    var decompressed = RoundTrip(original);
    Assert.That(decompressed, Is.EqualTo(original));
  }

  [Test]
  public void RoundTrip_MultipleReferenceSizedBlocks() {
    var original = new byte[BriefLzStream.DefaultBlockSize + 32768];
    for (var i = 0; i < original.Length; ++i)
      original[i] = (byte)(i * 31 + i / 97);
    var decompressed = RoundTrip(original);
    Assert.That(decompressed, Is.EqualTo(original));
  }

  [Test]
  public void Magic_IsBlz() {
    var original = "Test"u8.ToArray();
    using var input = new MemoryStream(original);
    using var compressed = new MemoryStream();
    BriefLzStream.Compress(input, compressed);

    var data = compressed.ToArray();
    Assert.That(data.Length, Is.GreaterThanOrEqualTo(24));
    Assert.That(data[0], Is.EqualTo((byte)'b'));
    Assert.That(data[1], Is.EqualTo((byte)'l'));
    Assert.That(data[2], Is.EqualTo((byte)'z'));
    Assert.That(data[3], Is.EqualTo(0x1A));
  }

  [Test]
  public void Header_HasCorrectCrc() {
    var original = "CRC32 check data"u8.ToArray();
    using var input = new MemoryStream(original);
    using var compressed = new MemoryStream();
    BriefLzStream.Compress(input, compressed);

    var data = compressed.ToArray();
    var headerCrc = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(20, 4));
    var expectedCrc = Crc32.Compute(original);
    Assert.That(headerCrc, Is.EqualTo(expectedCrc));
  }

  [Test]
  public void ReferenceLevel1LiteralVector_IsBitExact() {
    using var input = new MemoryStream("ABC"u8.ToArray());
    using var compressed = new MemoryStream();

    BriefLzStream.Compress(input, compressed, level: 1);

    // Reference brieflz 1.3.0 level 1:
    // first literal 'A', tag word 0x2000 stored little-endian, then B/C literals.
    Assert.That(compressed.ToArray().AsSpan(24).ToArray(),
      Is.EqualTo(new byte[] { 0x41, 0x00, 0x20, 0x42, 0x43 }));
  }

  [Test]
  public void Decompress_ReferenceMatchVector() {
    // Reference brieflz 1.3.0 payload for "AAAAA":
    // literal 'A', tag 0x8400 little-endian, offset-low 0.
    var container = BuildUncheckedContainer(
      [0x41, 0x00, 0x84, 0x00],
      uncompressedSize: 5);

    using var input = new MemoryStream(container);
    using var output = new MemoryStream();
    BriefLzStream.Decompress(input, output);

    Assert.That(output.ToArray(), Is.EqualTo("AAAAA"u8.ToArray()));
  }

  [Test]
  public void Decompress_AcceptsZeroCrcFields() {
    var container = BuildUncheckedContainer(
      [0x41, 0x00, 0x20, 0x42, 0x43],
      uncompressedSize: 3);

    using var input = new MemoryStream(container);
    using var output = new MemoryStream();
    BriefLzStream.Decompress(input, output);

    Assert.That(output.ToArray(), Is.EqualTo("ABC"u8.ToArray()));
  }

  [Test]
  public void Decompress_RejectsReferenceMalformedOffsetVector() {
    // Adapted from upstream test_brieflz.c: a match reads one byte before output.
    var container = BuildUncheckedContainer(
      [0x42, 0x00, 0x80, 0x01],
      uncompressedSize: 5);

    using var input = new MemoryStream(container);
    using var output = new MemoryStream();

    Assert.Throws<InvalidDataException>(() => BriefLzStream.Decompress(input, output));
  }

  [Test]
  public void RoundTrip_SingleByte() {
    var original = new byte[] { 0x42 };
    var decompressed = RoundTrip(original);
    Assert.That(decompressed, Is.EqualTo(original));
  }

  [Test]
  public void RoundTrip_LargeData() {
    var rng = new Random(12345);
    var original = new byte[100 * 1024];
    rng.NextBytes(original);
    for (var i = 0; i < 10000; i++)
      original[50000 + i] = (byte)(i % 4);
    var decompressed = RoundTrip(original);
    Assert.That(decompressed, Is.EqualTo(original));
  }

  private static byte[] RoundTrip(byte[] original) {
    using var input = new MemoryStream(original);
    using var compressed = new MemoryStream();
    BriefLzStream.Compress(input, compressed);

    compressed.Position = 0;
    using var output = new MemoryStream();
    BriefLzStream.Decompress(compressed, output);
    return output.ToArray();
  }

  private static byte[] BuildUncheckedContainer(byte[] payload, int uncompressedSize) {
    var result = new byte[24 + payload.Length];
    BinaryPrimitives.WriteUInt32BigEndian(result, 0x626C7A1Au);
    BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4), 1);
    BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(8), (uint)payload.Length);
    // CRC fields deliberately stay zero: blzpack defines zero as "not present".
    BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(16), (uint)uncompressedSize);
    payload.CopyTo(result, 24);
    return result;
  }
}
