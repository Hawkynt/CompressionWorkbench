using Compression.Core.Dictionary.Lzma;

namespace Compression.Tests.Dictionary;

[TestFixture]
public class Lzma2Tests {
  [Test]
  public void RoundTrip_EmptyData() {
    byte[] result = CompressDecompress([]);
    Assert.That(result, Is.Empty);
  }

  [Test]
  public void RoundTrip_SingleByte() {
    byte[] data = [42];
    byte[] result = CompressDecompress(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void RoundTrip_TextData() {
    byte[] data = "Hello, LZMA2 World! Testing the chunked encoding."u8.ToArray();
    byte[] result = CompressDecompress(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void RoundTrip_RepetitiveData() {
    byte[] pattern = "abcdefghij"u8.ToArray();
    byte[] data = new byte[pattern.Length * 200];
    for (int i = 0; i < 200; i++)
      Array.Copy(pattern, 0, data, i * pattern.Length, pattern.Length);

    byte[] result = CompressDecompress(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void RoundTrip_RandomData() {
    var rng = new Random(42);
    byte[] data = new byte[1024];
    rng.NextBytes(data);

    byte[] result = CompressDecompress(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void RoundTrip_LargeData() {
    var rng = new Random(123);
    byte[] data = new byte[51200]; // 50KB
    for (int i = 0; i < data.Length; i++) {
      if (i % 100 < 50)
        data[i] = (byte)(i % 26 + 'a');
      else
        data[i] = (byte)rng.Next(256);
    }

    byte[] result = CompressDecompress(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void UncompressedFallback_RandomData() {
    // Truly random data should use uncompressed chunks
    var rng = new Random(99);
    byte[] data = new byte[256];
    rng.NextBytes(data);

    using var compressed = new MemoryStream();
    var encoder = new Lzma2Encoder(dictionarySize: 1 << 16);
    encoder.Encode(compressed, data);

    // Should still round-trip
    compressed.Position = 0;
    var decoder = new Lzma2Decoder(compressed, 1 << 16);
    byte[] result = decoder.Decode();
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void DictionarySizeByte_EncodesCorrectly() {
    // 8 MB = 1 << 23
    var encoder = new Lzma2Encoder(dictionarySize: 1 << 23);
    Assert.That(encoder.DictionarySizeByte, Is.GreaterThan((byte)0));
  }

  [Test]
  public void RoundTrip_MultiChunk_LargeData() {
    // Data > 2MB to force multiple LZMA2 chunks (MaxUncompressedChunkSize = 2MB)
    var rng = new Random(456);
    byte[] pattern = "the quick brown fox jumps over the lazy dog. "u8.ToArray();
    byte[] data = new byte[3 * 1024 * 1024]; // 3MB
    for (int i = 0; i < data.Length; i++)
      data[i] = (i % 200 < 100) ? pattern[i % pattern.Length] : (byte)rng.Next(256);

    byte[] result = CompressDecompress(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void RoundTrip_MixedCompressibleAndRandom() {
    // Alternating compressible and incompressible blocks
    // to exercise both LZMA and uncompressed chunk paths
    using var ms = new MemoryStream();
    var rng = new Random(321);

    // Compressible block
    byte[] repeated = new byte[500];
    Array.Fill(repeated, (byte)'Z');
    ms.Write(repeated);

    // Random block (likely triggers uncompressed fallback in small sizes)
    byte[] random = new byte[500];
    rng.NextBytes(random);
    ms.Write(random);

    // More compressible data
    for (int i = 0; i < 50; i++)
      ms.Write("pattern_repeat_"u8);

    byte[] data = ms.ToArray();
    byte[] result = CompressDecompress(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void RoundTrip_SmallDictionary() {
    // Small dictionary forces more dictionary resets in chunks
    byte[] data = "Small dict LZMA2 round-trip test. "u8.ToArray();
    byte[] bigData = new byte[data.Length * 100];
    for (int i = 0; i < 100; i++)
      data.CopyTo(bigData.AsSpan(i * data.Length));

    using var compressed = new MemoryStream();
    var encoder = new Lzma2Encoder(dictionarySize: 4096);
    encoder.Encode(compressed, bigData);

    compressed.Position = 0;
    var decoder = new Lzma2Decoder(compressed, 4096);
    byte[] result = decoder.Decode();
    Assert.That(result, Is.EqualTo(bigData));
  }

  [Test]
  public void RoundTrip_StreamOverload() {
    // Exercise the Encode(Stream output, Stream input, long length) overload
    byte[] data = "Stream-based LZMA2 encoding test with known length."u8.ToArray();

    using var input = new MemoryStream(data);
    using var compressed = new MemoryStream();
    var encoder = new Lzma2Encoder(dictionarySize: 1 << 16);
    encoder.Encode(compressed, input, data.Length);

    compressed.Position = 0;
    var decoder = new Lzma2Decoder(compressed, 1 << 16);
    byte[] result = decoder.Decode();
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void RoundTrip_StreamOverload_UnknownLength() {
    // Exercise the Encode(Stream output, Stream input, -1) path
    byte[] data = "Unknown-length stream LZMA2 test data."u8.ToArray();

    using var input = new MemoryStream(data);
    using var compressed = new MemoryStream();
    var encoder = new Lzma2Encoder(dictionarySize: 1 << 16);
    encoder.Encode(compressed, input);

    compressed.Position = 0;
    var decoder = new Lzma2Decoder(compressed, 1 << 16);
    byte[] result = decoder.Decode();
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void RoundTrip_HighlyRepetitive_SmallBlocks() {
    // All-zero data — exercises both short rep and uncompressed fallback paths
    byte[] data = new byte[8192];
    byte[] result = CompressDecompress(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void Decode_TruncatedStream_Throws() {
    // LZMA2 stream that ends prematurely (no end marker)
    using var ms = new MemoryStream([0x80]); // LZMA chunk header but no payload
    var decoder = new Lzma2Decoder(ms, 1 << 16);
    Assert.Throws<EndOfStreamException>(() => decoder.Decode());
  }

  [Test]
  public void Decode_InvalidControlByte_Throws() {
    // Control byte 0x03-0x7F is invalid
    using var ms = new MemoryStream([0x50]);
    var decoder = new Lzma2Decoder(ms, 1 << 16);
    Assert.Throws<InvalidDataException>(() => decoder.Decode());
  }

  [Test]
  public void Decode_MissingProperties_Throws() {
    // LZMA chunk with resetLevel=0 but no prior properties
    // Control byte: 0x80 | (0 << 5) = 0x80, unpackedSizeHigh=0
    byte[] stream = [
      0x80,       // control: LZMA chunk, resetLevel=0, unpackedSizeHigh=0
      0x00, 0x00, // unpacked size low (0-based → size=1)
      0x00, 0x00, // packed size (0-based → size=1)
      0x00        // 1 byte of packed data
    ];
    using var ms = new MemoryStream(stream);
    var decoder = new Lzma2Decoder(ms, 1 << 16);
    Assert.Throws<InvalidDataException>(() => decoder.Decode());
  }

  private static byte[] CompressDecompress(byte[] data) {
    using var compressed = new MemoryStream();
    var encoder = new Lzma2Encoder(dictionarySize: 1 << 20);
    encoder.Encode(compressed, data);

    compressed.Position = 0;
    var decoder = new Lzma2Decoder(compressed, 1 << 20);
    return decoder.Decode();
  }
}
