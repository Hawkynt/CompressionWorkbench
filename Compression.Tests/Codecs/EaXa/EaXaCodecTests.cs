using Codec.EaXa;

namespace Compression.Tests.Codecs.EaXa;

[TestFixture]
public class EaXaCodecTests {

  // ──────────── 1. Hand-computed decode (coef 0 = pure shift) ────────────

  /// <summary>
  /// CoefIndex 0 has K0 = K1 = 0, so the predictor term vanishes and each sample is just
  /// <c>signExtend4(nibble) &lt;&lt; (12 - shift)</c>. With shift = 12 that becomes the
  /// sign-extended nibble itself. Header 0x0C (coef 0, shift 12) with the first data byte
  /// 0x12 (HIGH nibble 1 → sample 0, LOW nibble 2 → sample 1) decodes to 1, 2, then zeros.
  /// </summary>
  [Test]
  public void Decode_Coef0_Shift12_NibblesMapDirectlyToSamples() {
    var frame = new byte[EaXaCodec.FrameSize];
    frame[0] = 0x0C;          // coef 0, shift 12
    frame[1] = 0x12;          // high nibble 1 (sample 0), low nibble 2 (sample 1)

    var pcm = EaXaCodec.Decode(frame, channels: 1);

    Assert.That(pcm.Length, Is.EqualTo(EaXaCodec.SamplesPerFrame));
    Assert.That(pcm[0], Is.EqualTo((short)1));
    Assert.That(pcm[1], Is.EqualTo((short)2));
    for (var i = 2; i < EaXaCodec.SamplesPerFrame; ++i)
      Assert.That(pcm[i], Is.EqualTo((short)0), $"sample {i}");
  }

  /// <summary>
  /// CoefIndex 0, shift 0: <c>s = signExtend4(nibble) &lt;&lt; 12</c>. Nibble 1 → 4096,
  /// nibble 0xF (= -1) → -4096.
  /// </summary>
  [Test]
  public void Decode_Coef0_Shift0_LeftShiftsBy12() {
    var frame = new byte[EaXaCodec.FrameSize];
    frame[0] = 0x00;          // coef 0, shift 0
    frame[1] = 0x1F;          // high nibble 1 (sample 0), low nibble F = -1 (sample 1)

    var pcm = EaXaCodec.Decode(frame, channels: 1);

    Assert.That(pcm[0], Is.EqualTo((short)4096));
    Assert.That(pcm[1], Is.EqualTo((short)(-4096)));
  }

  // ──────────── 2. Uncompressed (0xEE) frame ────────────

  /// <summary>
  /// An <c>0xEE</c> header frame carries raw 16-bit big-endian samples in its first 14 bytes
  /// (7 samples), decoded verbatim.
  /// </summary>
  [Test]
  public void Decode_RawFrame_EmitsBigEndianSamples() {
    var frame = new byte[EaXaCodec.FrameSize];
    frame[0] = EaXaCodec.RawFrameMarker;
    // sample 0 = 0x1234, sample 1 = 0xF000 (= -4096) big-endian.
    frame[1] = 0x12; frame[2] = 0x34;
    frame[3] = 0xF0; frame[4] = 0x00;

    var pcm = EaXaCodec.Decode(frame, channels: 1);

    Assert.That(pcm[0], Is.EqualTo((short)0x1234));
    Assert.That(pcm[1], Is.EqualTo(unchecked((short)0xF000)));
  }

  // ──────────── 3. Frame / sample arithmetic ────────────

  [Test]
  public void Decode_Stereo_InterleavesPerFrame() {
    // Two frames (one group): ch0 frame coef0/shift12 high nibble 3, ch1 high nibble 5.
    var data = new byte[EaXaCodec.FrameSize * 2];
    data[0] = 0x0C; data[1] = 0x30;                          // ch0 sample0 = 3
    data[EaXaCodec.FrameSize] = 0x0C;
    data[EaXaCodec.FrameSize + 1] = 0x50;                    // ch1 sample0 = 5

    var pcm = EaXaCodec.Decode(data, channels: 2);

    Assert.That(pcm.Length, Is.EqualTo(EaXaCodec.SamplesPerFrame * 2));
    Assert.That(pcm[0], Is.EqualTo((short)3)); // L sample 0
    Assert.That(pcm[1], Is.EqualTo((short)5)); // R sample 0
  }

  [Test]
  public void Decode_TrailingPartialGroup_IsIgnored() {
    var data = new byte[EaXaCodec.FrameSize + 5];
    var pcm = EaXaCodec.Decode(data, channels: 1);
    Assert.That(pcm.Length, Is.EqualTo(EaXaCodec.SamplesPerFrame));
  }

  [Test]
  public void Encode_Empty_ReturnsEmpty() {
    Assert.That(EaXaCodec.Encode(ReadOnlySpan<short>.Empty, channels: 1).Length, Is.EqualTo(0));
  }

  [Test]
  public void Encode_PadsToWholeFrames() {
    var pcm = new short[EaXaCodec.SamplesPerFrame + 4]; // 2 frames
    var encoded = EaXaCodec.Encode(pcm, channels: 1);
    Assert.That(encoded.Length, Is.EqualTo(2 * EaXaCodec.FrameSize));
  }

  // ──────────── 4. Round-trips ────────────

  [Test]
  public void EncodeDecode_Silence_RoundTripsExactly() {
    var pcm = new short[EaXaCodec.SamplesPerFrame * 3];
    var decoded = EaXaCodec.Decode(EaXaCodec.Encode(pcm, 1), 1);
    for (var i = 0; i < pcm.Length; ++i)
      Assert.That(decoded[i], Is.EqualTo((short)0), $"sample {i}");
  }

  [Test]
  public void EncodeDecode_Sine_RoundTripsWithinTolerance() {
    const int count = EaXaCodec.SamplesPerFrame * 40;
    var pcm = new short[count];
    for (var i = 0; i < count; ++i)
      pcm[i] = (short)(Math.Sin(i * 2 * Math.PI / 64) * 12000);

    var decoded = EaXaCodec.Decode(EaXaCodec.Encode(pcm, 1), 1);

    Assert.That(decoded.Length, Is.GreaterThanOrEqualTo(count));
    var maxError = 0;
    for (var i = 0; i < count; ++i)
      maxError = Math.Max(maxError, Math.Abs(decoded[i] - pcm[i]));
    Assert.That(maxError, Is.LessThan(2048), $"max abs error {maxError}");
  }

  [Test]
  public void EncodeDecode_StereoSine_RoundTripsWithinTolerance() {
    const int frames = EaXaCodec.SamplesPerFrame * 20;
    var pcm = new short[frames * 2];
    for (var f = 0; f < frames; ++f) {
      pcm[f * 2] = (short)(Math.Sin(f * 2 * Math.PI / 50) * 10000);      // L
      pcm[f * 2 + 1] = (short)(Math.Sin(f * 2 * Math.PI / 37) * 9000);   // R
    }

    var decoded = EaXaCodec.Decode(EaXaCodec.Encode(pcm, 2), 2);

    var maxError = 0;
    for (var i = 0; i < pcm.Length; ++i)
      maxError = Math.Max(maxError, Math.Abs(decoded[i] - pcm[i]));
    Assert.That(maxError, Is.LessThan(2048), $"max abs error {maxError}");
  }
}
