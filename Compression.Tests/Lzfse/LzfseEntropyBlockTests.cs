#pragma warning disable CS1591
using System.Buffers.Binary;
using FileFormat.Lzfse;

namespace Compression.Tests.Lzfse;

/// <summary>
/// Covers the entropy-coded <c>bvx2</c> block on the writing side, and the reader's
/// agreement with it. The reader already had bvx1/bvx2 coverage, but only over
/// degenerate blocks whose three final FSE states are all zero; a block written from
/// real input carries non-zero states, which is what these tests exercise.
/// </summary>
[TestFixture]
public class LzfseEntropyBlockTests {

  private const uint MagicEndOfStream = 0x24787662;
  private const uint MagicUncompressed = 0x2D787662;
  private const uint MagicLzfseV2 = 0x32787662;
  private const uint MagicLzvn = 0x6E787662;

  private static byte[] Compress(byte[] original) {
    using var input = new MemoryStream(original);
    using var compressed = new MemoryStream();
    LzfseStream.Compress(input, compressed);
    return compressed.ToArray();
  }

  private static byte[] Decompress(byte[] compressed) {
    using var input = new MemoryStream(compressed);
    using var output = new MemoryStream();
    LzfseStream.Decompress(input, output);
    return output.ToArray();
  }

  /// <summary>Text-like input: many repeated phrases over a small alphabet.</summary>
  private static byte[] TextLike(int length) {
    const string phrase = "the quick brown fox jumps over the lazy dog; ";
    var result = new byte[length];
    for (var i = 0; i < length; ++i)
      result[i] = (byte)phrase[i % phrase.Length];
    return result;
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RedundantInput_IsWrittenAsAnEntropyCodedBvx2Block() {
    var original = TextLike(20_000);
    var compressed = Compress(original);

    Assert.Multiple(() => {
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(compressed), Is.EqualTo(MagicLzfseV2),
        "a 20 KB redundant block is smallest as an entropy-coded bvx2 block");
      Assert.That(compressed.Length, Is.LessThan(original.Length / 8));
      Assert.That(Decompress(compressed), Is.EqualTo(original));
    });
  }

  /// <summary>
  /// The bvx2 fixed header packs the header size into the low 32 bits of its third
  /// 64-bit word and the final L, M and D coder states above them. A real block has
  /// non-zero states there, so the size must be masked out of the word rather than
  /// narrowed from it.
  /// </summary>
  [Test, Category("Regression")]
  public void Bvx2HeaderSize_IsReadOutOfAWordWhoseUpperBitsCarryTheFseStates() {
    var compressed = Compress(TextLike(20_000));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(compressed), Is.EqualTo(MagicLzfseV2));

    var packed = BinaryPrimitives.ReadUInt64LittleEndian(compressed.AsSpan(24));
    Assert.Multiple(() => {
      Assert.That(packed >> 32, Is.Not.Zero,
        "this block must carry non-zero L/M/D states, or the test proves nothing");
      Assert.That((uint)packed, Is.InRange(32u, 752u), "header size sits in the low 32 bits");
      Assert.That(() => Decompress(compressed), Throws.Nothing);
    });
  }

  [Test, Category("RoundTrip")]
  public void MultipleBlocks_RoundTripAcrossBlockBoundaries() {
    var original = TextLike(200_000);
    var compressed = Compress(original);

    Assert.Multiple(() => {
      Assert.That(Decompress(compressed), Is.EqualTo(original));
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(compressed.AsSpan(compressed.Length - 4)),
        Is.EqualTo(MagicEndOfStream));
    });
  }

  [Test, Category("RoundTrip")]
  public void MixedRedundantAndIncompressibleBlocks_RoundTrip() {
    var random = new Random(20240918);
    var original = new byte[120_000];
    TextLike(40_000).CopyTo(original, 0);
    random.NextBytes(original.AsSpan(40_000, 40_000));
    TextLike(40_000).CopyTo(original, 80_000);

    Assert.That(Decompress(Compress(original)), Is.EqualTo(original));
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void IncompressibleInput_FallsBackWithoutGrowingTheBlockPayload() {
    var random = new Random(19981231);
    var original = new byte[30_000];
    random.NextBytes(original);

    var compressed = Compress(original);
    var magic = BinaryPrimitives.ReadUInt32LittleEndian(compressed);

    Assert.Multiple(() => {
      Assert.That(magic, Is.AnyOf(MagicUncompressed, MagicLzvn),
        "random data must not be forced through the entropy coder");
      Assert.That(compressed.Length, Is.LessThanOrEqualTo(original.Length + 64));
      Assert.That(Decompress(compressed), Is.EqualTo(original));
    });
  }

  [Test, Category("RoundTrip")]
  public void LongRangeMatches_RoundTrip() {
    var original = new byte[25_000];
    var random = new Random(7);
    random.NextBytes(original.AsSpan(0, 4_000));
    original.AsSpan(0, 4_000).CopyTo(original.AsSpan(20_000));
    TextLike(16_000).CopyTo(original, 4_000);

    Assert.That(Decompress(Compress(original)), Is.EqualTo(original));
  }

  [Test, Category("Boundary"), Category("RoundTrip")]
  public void EveryByteValue_RoundTrips() {
    var original = new byte[8 * 256];
    for (var i = 0; i < original.Length; ++i)
      original[i] = (byte)(i & 0xFF);

    Assert.That(Decompress(Compress(original)), Is.EqualTo(original));
  }
}
