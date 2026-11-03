using Compression.Core.Dictionary.PkwareDcl;
using FileFormat.Mpq;

namespace Compression.Tests.Mpq;

/// <summary>
/// The MPQ codecs on their own: PKWARE DCL implode/explode, Blizzard Huffman, IMA ADPCM, sparse,
/// and the mask dispatcher, with boundary and malformed-input cases.
/// </summary>
[TestFixture]
public class MpqCodecTests {

  private static byte[] Text(int length) {
    var words = "the peasant gathers gold while the footman guards the keep at dawn "u8.ToArray();
    var data = new byte[length];
    for (var i = 0; i < length; ++i)
      data[i] = words[(i * 7 + i / 13) % words.Length];
    return data;
  }

  private static byte[] Noise(int length, int seed) {
    var data = new byte[length];
    new Random(seed).NextBytes(data);
    return data;
  }

  [TestCase(PkwareDclMode.Binary, 1024)]
  [TestCase(PkwareDclMode.Binary, 2048)]
  [TestCase(PkwareDclMode.Binary, 4096)]
  [TestCase(PkwareDclMode.Ascii, 1024)]
  [TestCase(PkwareDclMode.Ascii, 4096)]
  public void Given_Text_When_ImplodedAndExploded_Then_ItRoundTrips(PkwareDclMode mode, int dictionary) {
    var data = Text(20_000);
    var packed = PkwareDclEncoder.Compress(data, mode, dictionary);
    Assert.That(packed.Length, Is.LessThan(data.Length / 2));
    Assert.That(PkwareDclDecoder.Decompress(packed, data.Length), Is.EqualTo(data));
  }

  [TestCase(0)]
  [TestCase(1)]
  [TestCase(2)]
  [TestCase(3)]
  [TestCase(4096)]
  [TestCase(0x206 + 1)]
  public void Given_BoundaryLengths_When_Imploded_Then_TheyRoundTrip(int length) {
    var runs = new byte[length]; // a single run exercises the longest repetitions
    var noise = Noise(length, length);
    foreach (var data in new[] { runs, noise }) {
      var packed = PkwareDclEncoder.Compress(data);
      Assert.That(PkwareDclDecoder.Decompress(packed), Is.EqualTo(data));
    }
  }

  [Test]
  public void Given_ATwoByteRepeatAtDistance256_When_Imploded_Then_ItRoundTrips() {
    var data = new byte[600];
    new Random(5).NextBytes(data);
    data[300] = data[44];
    data[301] = data[45];
    Assert.That(PkwareDclDecoder.Decompress(PkwareDclEncoder.Compress(data)), Is.EqualTo(data));
  }

  [Test]
  public void Given_AnInvalidDictionarySize_When_Imploding_Then_ItIsRejected()
    => Assert.Throws<ArgumentOutOfRangeException>(() => PkwareDclEncoder.Compress([1, 2, 3], dictionarySize: 8192));

  [TestCase(new byte[] { 0, 6 }, Description = "shorter than header + one byte")]
  [TestCase(new byte[] { 2, 6, 0 }, Description = "unknown literal mode")]
  [TestCase(new byte[] { 0, 7, 0 }, Description = "dictionary bits 7")]
  [TestCase(new byte[] { 0, 3, 0 }, Description = "dictionary bits 3")]
  [TestCase(new byte[] { 0, 6, 0x00, 0x00 }, Description = "literals without an end marker")]
  public void Given_MalformedStreams_When_Exploded_Then_TheyAreRefused(byte[] stream)
    => Assert.Throws<InvalidDataException>(() => PkwareDclDecoder.Decompress(stream));

  [Test]
  public void Given_ARepetitionBeforeTheStart_When_Exploded_Then_ItIsRefused() {
    // flag 1, length code for 3 (LenCode[1] = 0x03 in 2 bits), distance code 0 (0x03 in 2 bits), 6 low bits.
    var stream = new byte[] { 0, 6, 0b0001_1111, 0b0000_0000, 0, 0 };
    Assert.Throws<InvalidDataException>(() => PkwareDclDecoder.Decompress(stream));
  }

  [Test]
  public void Given_AStreamDecodingPastItsExpectedSize_When_Exploded_Then_ItIsRefused() {
    var packed = PkwareDclEncoder.Compress(Text(100));
    Assert.Throws<InvalidDataException>(() => PkwareDclDecoder.Decompress(packed, 99));
    Assert.Throws<InvalidDataException>(() => PkwareDclDecoder.Decompress(packed, 101));
  }

  [Test]
  public void Given_TheBuildingBlock_When_RoundTripped_Then_TheBytesSurvive() {
    var block = new PkwareDclBuildingBlock();
    var data = Text(5000);
    Assert.That(block.Id, Is.EqualTo("BB_PkwareDcl"));
    Assert.That(block.Decompress(block.Compress(data)), Is.EqualTo(data));
  }

  [TestCase(0)]
  [TestCase(1)]
  [TestCase(2)]
  [TestCase(3)]
  [TestCase(4)]
  [TestCase(5)]
  [TestCase(6)]
  [TestCase(7)]
  [TestCase(8)]
  public void Given_EveryHuffmanDataType_When_RoundTripped_Then_TheBytesSurvive(int dataType) {
    var data = Text(3000).Concat(Noise(500, dataType)).ToArray();
    var packed = MpqHuffman.Compress(data, dataType);
    Assert.That(packed[0], Is.EqualTo(dataType));
    Assert.That(MpqHuffman.Decompress(packed, data.Length), Is.EqualTo(data));
  }

  [Test]
  public void Given_AnUnknownHuffmanDataType_When_Decoded_Then_ItIsRefused()
    => Assert.Throws<InvalidDataException>(() => MpqHuffman.Decompress([0x09, 0xFF, 0xFF], 10));

  [Test]
  public void Given_ATruncatedHuffmanStream_When_Decoded_Then_ItIsRefused() {
    var packed = MpqHuffman.Compress(Text(400), 2);
    Assert.Throws<InvalidDataException>(() => MpqHuffman.Decompress(packed.AsSpan(0, packed.Length / 2), 400));
  }

  [TestCase(1)]
  [TestCase(2)]
  public void Given_Pcm_When_AdpcmEncodedAndDecoded_Then_TheSignalStaysClose(int channels) {
    var samples = new byte[4000 * channels];
    for (var i = 0; i < samples.Length / 2; ++i) {
      var value = (short)(8000 * Math.Sin(i / (5.0 * channels)));
      samples[2 * i] = (byte)value;
      samples[2 * i + 1] = (byte)(value >> 8);
    }
    var packed = MpqAdpcm.Compress(samples, channels, 5);
    Assert.That(packed.Length, Is.LessThan(samples.Length / 2 + 16));
    var decoded = MpqAdpcm.Decompress(packed, samples.Length, channels);
    Assert.That(decoded.Length, Is.EqualTo(samples.Length));
    for (var i = 0; i < samples.Length / 2; i += 97) {
      var original = (short)(samples[2 * i] | samples[2 * i + 1] << 8);
      var restored = (short)(decoded[2 * i] | decoded[2 * i + 1] << 8);
      Assert.That(Math.Abs(original - restored), Is.LessThan(1200), $"sample {i}");
    }
  }

  [Test]
  public void Given_SparseData_When_RoundTripped_Then_TheBytesSurvive() {
    var data = new byte[9000];
    for (var i = 0; i < data.Length; i += 700)
      data[i] = (byte)(i % 251 + 1);
    data[^1] = 7; // a non-zero tail inside the last three bytes
    var packed = MpqSparse.Compress(data)!;
    Assert.That(packed, Is.Not.Null);
    Assert.That(MpqSparse.Decompress(packed, data.Length), Is.EqualTo(data));
  }

  [Test]
  public void Given_IncompressibleData_When_SparseEncoded_Then_ItDeclines()
    => Assert.That(MpqSparse.Compress(Noise(100, 3)), Is.Null);

  [Test]
  public void Given_ASparseStreamClaimingTooMuch_When_Decoded_Then_ItIsRefused()
    => Assert.Throws<InvalidDataException>(() => MpqSparse.Decompress([0, 0, 1, 0, 0x7F], 100));

  [TestCase(MpqCompression.Zlib, 0)]
  [TestCase(MpqCompression.Pkware, 0)]
  [TestCase(MpqCompression.Bzip2, 0)]
  [TestCase(MpqCompression.Sparse, 0)]
  [TestCase((byte)(MpqCompression.Sparse | MpqCompression.Zlib), 1)]
  [TestCase((byte)(MpqCompression.Sparse | MpqCompression.Bzip2), 1)]
  [TestCase(MpqCompression.Lzma, 1)]
  [TestCase((byte)(MpqCompression.Pkware | MpqCompression.Zlib | MpqCompression.Huffman), 0)]
  public void Given_AMask_When_ASectorIsCompressedAndDecompressed_Then_ItRoundTrips(byte mask, int version) {
    var data = Text(4096);
    for (var i = 0; i < 4096; i += 300)
      Array.Clear(data, i, 40);
    var packed = MpqCompression.Compress(data, mask);
    Assert.That(packed.Length, Is.LessThan(data.Length));
    Assert.That(MpqCompression.Decompress(packed, data.Length, version), Is.EqualTo(data));
  }

  [TestCase((byte)0x04, 0, Description = "no method has bit 2")]
  [TestCase((byte)0x0A, 1, Description = "PKWARE + zlib is not a version-2 mask")]
  [TestCase((byte)0x00, 0, Description = "empty mask")]
  public void Given_AnUndefinedMask_When_Decompressed_Then_ItIsRefused(byte mask, int version)
    => Assert.Throws<NotSupportedException>(() => MpqCompression.Decompress([mask, 1, 2, 3], 64, version));

  [Test]
  public void Given_ADamagedZlibSector_When_Decompressed_Then_ItThrowsInsteadOfReturningRaw() {
    var packed = MpqCompression.Compress(Text(4096), MpqCompression.Zlib);
    packed[packed.Length / 2] ^= 0xFF;
    Assert.Throws<InvalidDataException>(() => MpqCompression.Decompress(packed, 4096, 0));
  }
}
