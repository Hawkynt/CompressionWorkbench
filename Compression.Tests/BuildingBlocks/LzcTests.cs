using System.Buffers.Binary;
using Compression.Core.Dictionary.Lzw;

namespace Compression.Tests.BuildingBlocks;

[TestFixture]
public sealed class LzcTests {
  private static readonly byte[] Tobe = "TOBEORNOTTOBEORTOBEORNOT"u8.ToArray();

  [TestCase(12, "1F9D8C549E0829F2448A932754020E2CA890A04184")]
  [TestCase(16, "1F9D90549E0829F2448A932754020E2CA890A04184")]
  public void GzipValidatedReferenceVector_MatchesNativeCompressBytes(int maxBits, string expectedHex) {
    var packed = LzcCodec.Compress(Tobe, maxBits);

    Assert.That(packed, Is.EqualTo(Convert.FromHexString(expectedHex)));
    Assert.That(LzcCodec.Decompress(packed, Tobe.Length, maxBits), Is.EqualTo(Tobe));
  }

  [Test]
  public void Decoder_ClearCodeDiscardsRemainderOfEightCodeGroup() {
    // Codes A, B, CLEAR fill only part of a 9-byte/8-code group. The remaining bytes are
    // alignment padding; C and D start a fresh 9-bit group. GNU gzip accepts this .Z vector.
    var packed = Convert.FromHexString("1F9D8C418400040000000000438800");

    Assert.That(LzcCodec.Decompress(packed), Is.EqualTo("ABCD"u8.ToArray()));
  }

  [TestCase(12)]
  [TestCase(16)]
  public void RoundTrip_CrossesCodeWidthsAndDictionaryLimit(int maxBits) {
    var data = new byte[100_000];
    var state = 0x12345678u;
    for (var index = 0; index < data.Length; ++index) {
      state = state * 1664525u + 1013904223u;
      data[index] = (byte)(state >> 24);
    }

    var packed = LzcCodec.Compress(data, maxBits);

    Assert.That(LzcCodec.Decompress(packed, data.Length, maxBits), Is.EqualTo(data));
  }

  [TestCase(12)]
  [TestCase(16)]
  public void RoundTrip_NonBlockModeCrossesCodeWidths(int maxBits) {
    var data = Enumerable.Range(0, 12_000).Select(i => (byte)((i * 73 + i / 17) & 0xFF)).ToArray();

    var packed = LzcCodec.Compress(data, maxBits, blockMode: false);

    Assert.That(packed[2] & 0x80, Is.Zero);
    Assert.That(LzcCodec.Decompress(packed, data.Length, maxBits), Is.EqualTo(data));
  }

  [Test]
  public void EmptyStream_HasNativeHeaderAndRoundTrips() {
    var packed = LzcCodec.Compress([]);

    Assert.That(packed, Is.EqualTo(new byte[] { 0x1F, 0x9D, 0x90 }));
    Assert.That(LzcCodec.Decompress(packed), Is.Empty);
  }

  [Test]
  public void FutureDictionaryCodeIsRejected() {
    var packed = Convert.FromHexString("1F9D8C415802"); // literal A, then undefined code 300

    Assert.Throws<InvalidDataException>(() => LzcCodec.Decompress(packed));
  }

  [Test]
  public void TruncatedFinalCodeIsRejected() {
    var packed = LzcCodec.Compress(Tobe, 12);
    Array.Resize(ref packed, packed.Length - 1);

    Assert.Throws<InvalidDataException>(() => LzcCodec.Decompress(packed, Tobe.Length, 12));
  }

  [Test]
  public void HeaderReservedBitsAreRejected() {
    var packed = LzcCodec.Compress(Tobe, 12);
    packed[2] |= 0x20;

    Assert.Throws<InvalidDataException>(() => LzcCodec.Decompress(packed));
  }

  [Test]
  public void EnclosingFormatMaxBitsMismatchIsRejected() {
    var packed = LzcCodec.Compress(Tobe, 16);

    Assert.Throws<InvalidDataException>(() => LzcCodec.Decompress(packed, Tobe.Length, 12));
  }

  [Test]
  public void BuildingBlock_EnvelopeCarriesExpandedLength() {
    var block = new LzcBuildingBlock();
    var data = Enumerable.Range(0, 20_000).Select(i => (byte)((i * 29) & 0x7F)).ToArray();

    var packed = block.Compress(data);

    Assert.That(block.Id, Is.EqualTo("BB_Lzc"));
    Assert.That(BinaryPrimitives.ReadInt32LittleEndian(packed), Is.EqualTo(data.Length));
    Assert.That(packed.AsSpan(4, 3).ToArray(), Is.EqualTo(new byte[] { 0x1F, 0x9D, 0x90 }));
    Assert.That(block.Decompress(packed), Is.EqualTo(data));
  }
}
