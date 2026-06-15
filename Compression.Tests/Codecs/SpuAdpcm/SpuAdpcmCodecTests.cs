using Codec.SpuAdpcm;

namespace Compression.Tests.Codecs.SpuAdpcm;

[TestFixture]
public class SpuAdpcmCodecTests {

  // ──────────── 1. Hand-computed decode (filter 0 = pure shift) ────────────

  /// <summary>
  /// Filter 0 has K1 = K2 = 0, so the predictor term vanishes and each sample is simply
  /// <c>signExtend4(nibble) &lt;&lt; 12 &gt;&gt; shift</c>. With shift = 12 the right shift cancels
  /// the left shift, so every sample equals its sign-extended nibble. A crafted block with
  /// header 0x0C (filter 0, shift 12) and nibbles 1,2 in the first data byte therefore
  /// decodes to samples 1, 2, then zeros.
  /// </summary>
  [Test]
  public void Decode_Filter0_Shift12_NibblesMapDirectlyToSamples() {
    var block = new byte[16];
    block[0] = 0x0C;            // filter 0, shift 12
    block[1] = 0x00;            // flags
    block[2] = 0x21;            // low nibble = 1 (sample 0), high nibble = 2 (sample 1)

    var pcm = SpuAdpcmCodec.Decode(block);

    Assert.That(pcm.Length, Is.EqualTo(28));
    Assert.That(pcm[0], Is.EqualTo((short)1));
    Assert.That(pcm[1], Is.EqualTo((short)2));
    for (var i = 2; i < 28; ++i)
      Assert.That(pcm[i], Is.EqualTo((short)0), $"sample {i}");
  }

  /// <summary>
  /// Filter 0, shift 0: <c>s = signExtend4(nibble) &lt;&lt; 12</c>. Nibble 1 → 0x1000 = 4096,
  /// nibble 0xF (= -1) → -4096.
  /// </summary>
  [Test]
  public void Decode_Filter0_Shift0_LeftShiftsBy12() {
    var block = new byte[16];
    block[0] = 0x00;            // filter 0, shift 0
    block[2] = 0xF1;            // low nibble 1 (sample 0), high nibble F = -1 (sample 1)

    var pcm = SpuAdpcmCodec.Decode(block);

    Assert.That(pcm[0], Is.EqualTo((short)4096));
    Assert.That(pcm[1], Is.EqualTo((short)(-4096)));
  }

  // ──────────── 2. Block / sample arithmetic ────────────

  [Test]
  public void Decode_ThreeBlocks_Yield84Samples() {
    var data = new byte[3 * 16];
    var pcm = SpuAdpcmCodec.Decode(data);
    Assert.That(pcm.Length, Is.EqualTo(3 * 28));
  }

  [Test]
  public void Decode_TrailingPartialBlock_IsIgnored() {
    var data = new byte[16 + 5]; // one full block + 5 stray bytes
    var pcm = SpuAdpcmCodec.Decode(data);
    Assert.That(pcm.Length, Is.EqualTo(28));
  }

  [Test]
  public void Encode_PadsToWholeBlocks_AndMarksLastBlock() {
    var pcm = new short[30]; // → 2 blocks (28 + padded 2)
    var encoded = SpuAdpcmCodec.Encode(pcm);

    Assert.That(encoded.Length, Is.EqualTo(2 * 16));
    Assert.That(encoded[1], Is.EqualTo((byte)0x00), "first block flags");
    Assert.That(encoded[16 + 1], Is.EqualTo((byte)0x01), "last block end marker");
  }

  [Test]
  public void Encode_Empty_ReturnsEmpty() {
    Assert.That(SpuAdpcmCodec.Encode(ReadOnlySpan<short>.Empty).Length, Is.EqualTo(0));
  }

  // ──────────── 3. Clamping ────────────

  /// <summary>
  /// Filter 1 (K1 = 60) builds up a strong positive predictor; combined with positive
  /// residuals every decoded sample must stay within the signed-16 range.
  /// </summary>
  [Test]
  public void Decode_StaysWithinShortRange() {
    var block = new byte[16];
    block[0] = 0x10; // filter 1, shift 0 → maximum amplification
    for (var i = 2; i < 16; ++i) block[i] = 0x77; // nibble 7 (= +7) everywhere

    var pcm = SpuAdpcmCodec.Decode(block);
    foreach (var s in pcm)
      Assert.That(s, Is.InRange((short)-32768, (short)32767));
  }

  // ──────────── 4. Encode → decode round-trip (lossy, tolerance) ────────────

  [Test]
  public void EncodeDecode_Sine_RoundTripsWithinTolerance() {
    const int count = 28 * 40;
    var pcm = new short[count];
    for (var i = 0; i < count; ++i)
      pcm[i] = (short)(Math.Sin(i * 2 * Math.PI / 64) * 12000);

    var encoded = SpuAdpcmCodec.Encode(pcm);
    var decoded = SpuAdpcmCodec.Decode(encoded);

    Assert.That(decoded.Length, Is.GreaterThanOrEqualTo(count));

    var maxError = 0;
    for (var i = 0; i < count; ++i)
      maxError = Math.Max(maxError, Math.Abs(decoded[i] - pcm[i]));

    Assert.That(maxError, Is.LessThan(1024), $"max abs error {maxError}");
  }

  [Test]
  public void EncodeDecode_Silence_RoundTripsExactly() {
    var pcm = new short[28 * 3];
    var decoded = SpuAdpcmCodec.Decode(SpuAdpcmCodec.Encode(pcm));
    for (var i = 0; i < pcm.Length; ++i)
      Assert.That(decoded[i], Is.EqualTo((short)0), $"sample {i}");
  }
}
