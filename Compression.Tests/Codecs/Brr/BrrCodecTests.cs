using Codec.Brr;

namespace Compression.Tests.Codecs.Brr;

[TestFixture]
public class BrrCodecTests {

  // Builds a single 9-byte block from a header byte and 16 raw nibbles (HIGH-first).
  private static byte[] Block(byte header, params int[] nibbles) {
    var block = new byte[BrrCodec.BlockSize];
    block[0] = header;
    for (var i = 0; i < BrrCodec.SamplesPerBlock; ++i) {
      var n = i < nibbles.Length ? nibbles[i] & 0x0F : 0;
      if ((i & 1) == 0)
        block[1 + (i >> 1)] |= (byte)(n << 4); // high nibble = even sample
      else
        block[1 + (i >> 1)] |= (byte)n;        // low nibble = odd sample
    }
    return block;
  }

  // ──────────── 1. Hand-computed filter-0 block ────────────

  /// <summary>
  /// Filter 0 has no predictor term, so each sample is just <c>(signExtend4(n) &lt;&lt; range) &gt;&gt; 1</c>.
  /// With range 1 that is <c>signExtend4(n)</c> (since <c>(n &lt;&lt; 1) &gt;&gt; 1 == n</c>). Header
  /// 0x10 = range 1, filter 0, no end flag. Nibbles 1, 2, 0xF (= -1) map to 1, 2, -1.
  /// </summary>
  [Test]
  public void Decode_Filter0_Range1_NibblesMapDirectly() {
    var block = Block(0x10, 1, 2, 0xF);
    var pcm = BrrCodec.Decode(block);

    Assert.That(pcm.Length, Is.EqualTo(16));
    Assert.That(pcm[0], Is.EqualTo((short)1));
    Assert.That(pcm[1], Is.EqualTo((short)2));
    Assert.That(pcm[2], Is.EqualTo((short)(-1)));
    for (var i = 3; i < 16; ++i)
      Assert.That(pcm[i], Is.EqualTo((short)0), $"sample {i}");
  }

  /// <summary>
  /// Filter 0, range 12 (header high nibble 0xC → 0xC0): sample = <c>(s &lt;&lt; 12) &gt;&gt; 1</c>.
  /// Nibble 1 → 2048, nibble 7 → 14336, nibble 0xF (= -1) → -2048.
  /// </summary>
  [Test]
  public void Decode_Filter0_Range12_ScalesByPowerOfTwo() {
    var block = Block(0xC0, 1, 7, 0xF);
    var pcm = BrrCodec.Decode(block);

    Assert.That(pcm[0], Is.EqualTo((short)2048));
    Assert.That(pcm[1], Is.EqualTo((short)14336));
    Assert.That(pcm[2], Is.EqualTo((short)(-2048)));
  }

  // ──────────── 2. End-flag stop ────────────

  [Test]
  public void Decode_StopsAfterEndFlaggedBlock() {
    var first = Block(0x11, 1); // range 1, filter 0, end flag set (bit 0)
    var second = Block(0x10, 5);
    var stream = new byte[BrrCodec.BlockSize * 2];
    first.CopyTo(stream, 0);
    second.CopyTo(stream, BrrCodec.BlockSize);

    var pcm = BrrCodec.Decode(stream);

    // Only the first block is decoded; the second (after end flag) is dropped.
    Assert.That(pcm.Length, Is.EqualTo(16));
    Assert.That(pcm[0], Is.EqualTo((short)1));
  }

  [Test]
  public void Decode_TrailingPartialBlock_IsIgnored() {
    var data = new byte[BrrCodec.BlockSize + 4]; // one full block + 4 stray bytes
    var pcm = BrrCodec.Decode(data);
    Assert.That(pcm.Length, Is.EqualTo(16));
  }

  [Test]
  public void Decode_Empty_ReturnsEmpty()
    => Assert.That(BrrCodec.Decode(ReadOnlySpan<byte>.Empty).Length, Is.EqualTo(0));

  // ──────────── 3. 15-bit wrap ────────────

  /// <summary>
  /// Filter 1 (predictor + h1 * 15/16) with sustained maximum positive nibbles drives the
  /// running value past the 15-bit range. The S-DSP clamps to 16 bits then keeps only the
  /// low 15 bits as a signed value, so the once the value saturates to 32767 it folds:
  /// <c>(short)(32767 &lt;&lt; 1) &gt;&gt; 1 == (short)65534 &gt;&gt; 1 == -1</c>. The decoded stream must
  /// therefore contain a negative sample despite an all-positive drive — proof the wrap (not
  /// a saturation) is implemented.
  /// </summary>
  [Test]
  public void Decode_FifteenBitWrap_FoldsSaturatedValueNegative() {
    // range 12, filter 1, nibble 7 (= +7) everywhere → strong positive ramp.
    var block = Block(0xC4, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7);
    var pcm = BrrCodec.Decode(block);

    Assert.That(pcm.Any(s => s < 0), Is.True, "a saturated value must wrap to a negative sample");
  }

  // ──────────── 4. Encode → decode round-trip ────────────

  [Test]
  public void Encode_PadsToWholeBlocks_AndMarksLastBlock() {
    var pcm = new short[20]; // → 2 blocks (16 + padded 4)
    var encoded = BrrCodec.Encode(pcm);

    Assert.That(encoded.Length, Is.EqualTo(2 * BrrCodec.BlockSize));
    Assert.That(encoded[0] & 0x01, Is.EqualTo(0x00), "first block: no end flag");
    Assert.That(encoded[BrrCodec.BlockSize] & 0x01, Is.EqualTo(0x01), "last block: end flag set");
  }

  [Test]
  public void Encode_Empty_ReturnsEmpty()
    => Assert.That(BrrCodec.Encode(ReadOnlySpan<short>.Empty).Length, Is.EqualTo(0));

  [Test]
  public void EncodeDecode_Silence_RoundTripsExactly() {
    var pcm = new short[16 * 3];
    var decoded = BrrCodec.Decode(BrrCodec.Encode(pcm));
    foreach (var s in decoded)
      Assert.That(s, Is.EqualTo((short)0));
  }

  [Test]
  public void EncodeDecode_Sine_RoundTripsWithinTolerance() {
    const int count = 16 * 40;
    var pcm = new short[count];
    for (var i = 0; i < count; ++i)
      pcm[i] = (short)(Math.Sin(i * 2 * Math.PI / 48) * 10000);

    var encoded = BrrCodec.Encode(pcm);
    var decoded = BrrCodec.Decode(encoded);

    Assert.That(decoded.Length, Is.GreaterThanOrEqualTo(count));

    var maxError = 0;
    for (var i = 0; i < count; ++i)
      maxError = Math.Max(maxError, Math.Abs(decoded[i] - pcm[i]));

    Assert.That(maxError, Is.LessThan(1500), $"max abs error {maxError}");
  }
}
