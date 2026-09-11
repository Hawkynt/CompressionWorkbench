using System.Buffers.Binary;
using FileFormat.Lzfse;

namespace Compression.Tests.Lzfse;

[TestFixture]
public class LzfseTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SimpleText() {
    var original = "Hello, LZFSE compression! This is a test of Apple's LZFSE format."u8.ToArray();
    var decompressed = RoundTrip(original);
    Assert.That(decompressed, Is.EqualTo(original));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_RepetitiveData() {
    var original = new byte[4096];
    for (var i = 0; i < original.Length; i++)
      original[i] = (byte)(i % 7);

    using var input = new MemoryStream(original);
    using var compressed = new MemoryStream();
    LzfseStream.Compress(input, compressed);

    Assert.That(compressed.Length, Is.LessThan(original.Length), "Repetitive data should compress");

    compressed.Position = 0;
    using var output = new MemoryStream();
    LzfseStream.Decompress(compressed, output);
    Assert.That(output.ToArray(), Is.EqualTo(original));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_AllByteValues() {
    var original = new byte[256];
    for (var i = 0; i < 256; i++)
      original[i] = (byte)i;
    var decompressed = RoundTrip(original);
    Assert.That(decompressed, Is.EqualTo(original));
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void RoundTrip_SingleByte() {
    var original = new byte[] { 0x42 };
    var decompressed = RoundTrip(original);
    Assert.That(decompressed, Is.EqualTo(original));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_LargeData() {
    var rng = new Random(12345);
    var original = new byte[100 * 1024];
    rng.NextBytes(original);
    for (var i = 0; i < 10000; i++)
      original[50000 + i] = (byte)(i % 4);
    var decompressed = RoundTrip(original);
    Assert.That(decompressed, Is.EqualTo(original));
  }

  [Test, Category("HappyPath")]
  public void Magic_HasLzfseBlockMagic() {
    var original = "Test data for magic check with enough content to compress"u8.ToArray();
    using var input = new MemoryStream(original);
    using var compressed = new MemoryStream();
    LzfseStream.Compress(input, compressed);

    var data = compressed.ToArray();
    Assert.That(data.Length, Is.GreaterThanOrEqualTo(4));
    var magic = BitConverter.ToUInt32(data, 0);
    Assert.That(magic, Is.AnyOf(0x6E787662u, 0x2D787662u),
      "First block should be LZVN (bvxn) or uncompressed (bvx-)");

    var endMagic = BitConverter.ToUInt32(data, data.Length - 4);
    Assert.That(endMagic, Is.EqualTo(0x24787662u), "Stream should end with bvx$ end-of-stream marker");
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void RoundTrip_EmptyInput() {
    var original = Array.Empty<byte>();
    using var input = new MemoryStream(original);
    using var compressed = new MemoryStream();
    LzfseStream.Compress(input, compressed);

    compressed.Position = 0;
    using var output = new MemoryStream();
    LzfseStream.Decompress(compressed, output);
    Assert.That(output.ToArray(), Is.EqualTo(original));
  }

  [TestCase(false)]
  [TestCase(true)]
  [Category("HappyPath"), Category("Interoperability")]
  public void AppleFseDegenerateBlock_DecodesBvx1AndBvx2(bool version2) {
    var encoded = version2 ? BuildDegenerateV2() : BuildDegenerateV1();
    using var input = new MemoryStream(encoded, writable: false);
    using var output = new MemoryStream();

    LzfseStream.Decompress(input, output);

    Assert.That(output.ToArray(), Is.EqualTo("AAAA"u8.ToArray()));
  }

  [Test, Category("ErrorHandling")]
  public void Bvx2_TruncatedFrequencyHeader_IsRejected() {
    var encoded = BuildDegenerateV2();
    var headerSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(24)));
    var truncated = encoded[..(headerSize - 1)];

    using var input = new MemoryStream(truncated, writable: false);
    using var output = new MemoryStream();

    Assert.Throws<EndOfStreamException>(() => LzfseStream.Decompress(input, output));
  }

  private static byte[] RoundTrip(byte[] original) {
    using var input = new MemoryStream(original);
    using var compressed = new MemoryStream();
    LzfseStream.Compress(input, compressed);

    compressed.Position = 0;
    using var output = new MemoryStream();
    LzfseStream.Decompress(compressed, output);
    return output.ToArray();
  }

  /// <summary>
  /// Builds a minimal valid Apple bvx1 block directly from the published wire
  /// structures. The degenerate FSE tables use one symbol per stream, so all
  /// state transitions consume zero bits while still exercising the real FSE
  /// table construction and L/M/D reconstruction path.
  /// </summary>
  private static byte[] BuildDegenerateV1() {
    const int headerSize = 772;
    var header = new byte[headerSize];
    BinaryPrimitives.WriteUInt32LittleEndian(header, 0x31787662);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 4); // raw bytes
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 8); // total payload
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), 4); // padded literals
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 1); // L/M/D tuples
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), 0); // literal payload
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), 8); // LMD payload/padding
    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), 0);
    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(40), 0);

    var frequencies = DegenerateFrequencies();
    var offset = 50; // Apple lzfse_compressed_block_header_v1::l_freq
    foreach (var frequency in frequencies) {
      BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(offset), frequency);
      offset += 2;
    }
    Assert.That(offset, Is.EqualTo(770), "V1 has two trailing structure-alignment bytes");

    return [.. header, .. new byte[8], 0x62, 0x76, 0x78, 0x24];
  }

  private static byte[] BuildDegenerateV2() {
    var frequencies = EncodeV2Frequencies(DegenerateFrequencies());
    var headerSize = 32 + frequencies.Length;
    var header = new byte[headerSize];
    BinaryPrimitives.WriteUInt32LittleEndian(header, 0x32787662);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 4);

    ulong v0 = 4UL | (1UL << 40) | (7UL << 60); // literals=4, matches=1, literal_bits=0
    ulong v1 = (8UL << 40) | (7UL << 60);       // LMD payload=8, lmd_bits=0, states=0
    ulong v2 = checked((uint)headerSize);        // all L/M/D states are zero
    BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), v0);
    BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(16), v1);
    BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(24), v2);
    frequencies.CopyTo(header, 32);

    return [.. header, .. new byte[8], 0x62, 0x76, 0x78, 0x24];
  }

  private static ushort[] DegenerateFrequencies() {
    var frequencies = new ushort[20 + 20 + 64 + 256];
    frequencies[4] = 64;                  // L = 4
    frequencies[20] = 64;                 // M = 0
    frequencies[20 + 20 + 1] = 256;       // D = 1
    frequencies[20 + 20 + 64 + (byte)'A'] = 1024;
    return frequencies;
  }

  private static byte[] EncodeV2Frequencies(ReadOnlySpan<ushort> frequencies) {
    using var output = new MemoryStream();
    uint accumulator = 0;
    var accumulatorBits = 0;

    foreach (var frequency in frequencies) {
      var (bits, bitCount) = frequency switch {
        0 => (0u, 2),
        1 => (2u, 2),
        2 => (1u, 3),
        3 => (5u, 3),
        4 => (3u, 5),
        5 => (11u, 5),
        6 => (19u, 5),
        7 => (27u, 5),
        < 24 => (7u + ((uint)frequency - 8u << 4), 8),
        <= 1047 => (((uint)frequency - 24u << 4) + 15u, 14),
        _ => throw new AssertionException($"Frequency {frequency} cannot be represented by the LZFSE V2 header."),
      };

      accumulator |= bits << accumulatorBits;
      accumulatorBits += bitCount;
      while (accumulatorBits >= 8) {
        output.WriteByte((byte)accumulator);
        accumulator >>= 8;
        accumulatorBits -= 8;
      }
    }

    if (accumulatorBits > 0)
      output.WriteByte((byte)accumulator);
    return output.ToArray();
  }
}
