using System.Buffers.Binary;
using System.Text;
using Compression.Core.Dictionary.Lzw;

namespace Compression.Tests.BuildingBlocks;

[TestFixture]
public sealed class NuLzwTests {
  [Test]
  public void Crc16Xmodem_MatchesCheckValue() {
    var crc = NuLzwCodec.Crc16Xmodem(Encoding.ASCII.GetBytes("123456789"));
    Assert.That(crc, Is.EqualTo(0x31C3));
  }

  [TestCase(NuLzwVariant.Lzw1)]
  [TestCase(NuLzwVariant.Lzw2)]
  public void EmptyStream_HasCanonicalHeaderAndRoundTrips(NuLzwVariant variant) {
    var packed = NuLzwCodec.Compress([], variant);
    Assert.That(packed, Is.EqualTo(variant == NuLzwVariant.Lzw1
      ? new byte[] { 0x00, 0x00, 0xFE, 0xDB }
      : new byte[] { 0xFE, 0xDB }));
    Assert.That(NuLzwCodec.Decompress(packed, variant, 0), Is.Empty);
  }

  [TestCase(NuLzwVariant.Lzw1, 1)]
  [TestCase(NuLzwVariant.Lzw1, 4095)]
  [TestCase(NuLzwVariant.Lzw1, 4096)]
  [TestCase(NuLzwVariant.Lzw1, 4097)]
  [TestCase(NuLzwVariant.Lzw1, 16385)]
  [TestCase(NuLzwVariant.Lzw2, 1)]
  [TestCase(NuLzwVariant.Lzw2, 4095)]
  [TestCase(NuLzwVariant.Lzw2, 4096)]
  [TestCase(NuLzwVariant.Lzw2, 4097)]
  [TestCase(NuLzwVariant.Lzw2, 16385)]
  public void RoundTrip_RunHeavyAndPartialChunks(NuLzwVariant variant, int length) {
    var data = Enumerable.Range(0, length)
      .Select(i => (byte)((i / 37) % 11 == 0 ? 0xDB : (i / 113) % 7))
      .ToArray();

    var packed = NuLzwCodec.Compress(data, variant);
    var unpacked = NuLzwCodec.Decompress(packed, variant, data.Length);

    Assert.That(unpacked, Is.EqualTo(data));
  }

  [TestCase(NuLzwVariant.Lzw1)]
  [TestCase(NuLzwVariant.Lzw2)]
  public void RoundTrip_IncompressibleChunks(NuLzwVariant variant) {
    var data = new byte[12289];
    var state = 0x12345678u;
    for (var i = 0; i < data.Length; i++) {
      state = state * 1664525u + 1013904223u;
      data[i] = (byte)(state >> 24);
    }

    var packed = NuLzwCodec.Compress(data, variant);
    Assert.That(NuLzwCodec.Decompress(packed, variant, data.Length), Is.EqualTo(data));
  }

  [Test]
  public void Lzw2_PersistentDictionaryCrossesManyChunksAndCodeWidths() {
    var data = new byte[96 * 1024 + 321];
    var state = 0xCAFEBABEu;
    for (var i = 0; i < data.Length; i++) {
      state = state * 1103515245u + 12345u;
      data[i] = (byte)((state >> 24) & 0x1F);
    }

    var packed = NuLzwCodec.Compress(data, NuLzwVariant.Lzw2);
    var unpacked = NuLzwCodec.Decompress(packed, NuLzwVariant.Lzw2, data.Length);

    Assert.That(unpacked, Is.EqualTo(data));
    Assert.That(packed.Length, Is.LessThan(data.Length));
  }

  [Test]
  public void Lzw1_CrcCoversZeroPaddedFinalChunk() {
    var data = Enumerable.Repeat((byte)0x41, 5000).ToArray();
    var packed = NuLzwCodec.Compress(data, NuLzwVariant.Lzw1);
    packed[0] ^= 0x01;

    Assert.Throws<InvalidDataException>(() =>
      NuLzwCodec.Decompress(packed, NuLzwVariant.Lzw1, data.Length));
  }

  [Test]
  public void Lzw2_IgnoresTrailingShrinkItPadByte() {
    var data = Enumerable.Range(0, 9000).Select(i => (byte)(i % 13)).ToArray();
    var packed = NuLzwCodec.Compress(data, NuLzwVariant.Lzw2);
    var padded = packed.Concat(new byte[] { 0x00 }).ToArray();

    Assert.That(NuLzwCodec.Decompress(padded, NuLzwVariant.Lzw2, data.Length), Is.EqualTo(data));
  }

  [Test]
  public void Lzw2_IgnoresBogusCompressedLengthHintFromBadMacArchives() {
    var data = Enumerable.Range(0, 4096).Select(i => (byte)(i % 7)).ToArray();
    var packed = NuLzwCodec.Compress(data, NuLzwVariant.Lzw2);

    var postRle = BinaryPrimitives.ReadUInt16LittleEndian(packed.AsSpan(2, 2));
    Assert.That(postRle & 0x8000, Is.Not.Zero, "Fixture must select the LZW/2 chunk form.");

    // The LZW/2 word at +4 is only a recovery hint. Historical Macintosh-created
    // archives exist with this value byte-swapped or otherwise wrong. ShrinkIt-compatible
    // decoding stops when the declared expanded output has been produced, not at this hint.
    BinaryPrimitives.WriteUInt16LittleEndian(packed.AsSpan(4, 2), 1);

    Assert.That(NuLzwCodec.Decompress(packed, NuLzwVariant.Lzw2, data.Length), Is.EqualTo(data));
  }

  [Test]
  public void BuildingBlock_EnvelopeCarriesExpandedLength() {
    var block = new NuLzwBuildingBlock();
    var data = Enumerable.Range(0, 10000).Select(i => (byte)((i * 7) & 0x3F)).ToArray();

    var packed = block.Compress(data);
    var unpacked = block.Decompress(packed);

    Assert.That(block.Id, Is.EqualTo("BB_NuLzw"));
    Assert.That(unpacked, Is.EqualTo(data));
  }

  [Test]
  public void TruncatedBitstreamIsRejected() {
    var data = Enumerable.Range(0, 8192).Select(i => (byte)(i % 17)).ToArray();
    var packed = NuLzwCodec.Compress(data, NuLzwVariant.Lzw2);
    Array.Resize(ref packed, packed.Length - 7);

    Assert.Throws<InvalidDataException>(() =>
      NuLzwCodec.Decompress(packed, NuLzwVariant.Lzw2, data.Length));
  }
}