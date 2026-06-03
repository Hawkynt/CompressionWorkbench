using System.Buffers.Binary;
using Codec.WwiseIma;

namespace Compression.Tests.Codecs.WwiseIma;

[TestFixture]
public class WwiseImaCodecTests {

  // ──────────── 1. Hand-computed decode ────────────

  /// <summary>
  /// A mono block with blockAlign 8 (4-byte header + one 4-byte data group). The header
  /// seeds predictor 100 / step-index 0, emitted verbatim as the block's first sample. The
  /// first data nibble (LOW nibble of byte 0) is 0 → step[0]=7, diff = 7&gt;&gt;3 = 0, sign +
  /// → predictor stays 100, so sample 1 is also 100.
  /// </summary>
  [Test]
  public void Decode_HeaderPredictorIsFirstSample_ZeroNibbleHoldsValue() {
    var block = new byte[8];
    BinaryPrimitives.WriteInt16LittleEndian(block, 100); // predictor
    block[2] = 0;                                        // step index
    block[3] = 0;                                        // reserved
    // data bytes 4..7 already zero → all nibbles 0.

    var pcm = WwiseImaCodec.Decode(block, channels: 1, blockAlign: 8);

    Assert.That(pcm.Length, Is.EqualTo(9)); // 4 data bytes * 2 + 1
    Assert.That(pcm[0], Is.EqualTo((short)100), "header predictor");
    Assert.That(pcm[1], Is.EqualTo((short)100), "nibble 0 → no change");
  }

  /// <summary>
  /// Same block but the first nibble is 4. With step-index 0 (step = 7): diff = 7&gt;&gt;3 = 0,
  /// plus (nibble&amp;4) → diff += 7 = 7, sign + → predictor 100 + 7 = 107.
  /// </summary>
  [Test]
  public void Decode_Nibble4_AddsStep() {
    var block = new byte[8];
    BinaryPrimitives.WriteInt16LittleEndian(block, 100);
    block[2] = 0;
    block[4] = 0x04; // LOW nibble of first data byte = 4

    var pcm = WwiseImaCodec.Decode(block, channels: 1, blockAlign: 8);

    Assert.That(pcm[1], Is.EqualTo((short)107));
  }

  // ──────────── 2. Layout arithmetic ────────────

  [Test]
  public void Decode_RejectsBlockAlignNotSplittingIntoGroups() {
    var data = new byte[16];
    // mono, blockAlign 7 → perChannelData = 3, not a multiple of 4.
    Assert.Throws<ArgumentException>(() => WwiseImaCodec.Decode(data, channels: 1, blockAlign: 7));
  }

  [Test]
  public void Decode_Stereo_InterleavesChannels() {
    // blockAlign 16: per channel = 16/2 - 4 = 4 data bytes (one group). Headers: L then R.
    var block = new byte[16];
    BinaryPrimitives.WriteInt16LittleEndian(block.AsSpan(0), 1000);  // L predictor
    BinaryPrimitives.WriteInt16LittleEndian(block.AsSpan(4), 2000);  // R predictor

    var pcm = WwiseImaCodec.Decode(block, channels: 2, blockAlign: 16);

    Assert.That(pcm[0], Is.EqualTo((short)1000)); // L first sample
    Assert.That(pcm[1], Is.EqualTo((short)2000)); // R first sample
  }

  // ──────────── 3. Round-trips ────────────

  [Test]
  public void EncodeDecode_Sine_RoundTripsWithinTolerance() {
    const int blockAlign = 0x24; // mono; per-channel data = 0x24-4 = 32 bytes
    var samplesPerBlock = (blockAlign - 4) * 2 + 1; // 65
    var count = samplesPerBlock * 8;
    var pcm = new short[count];
    for (var i = 0; i < count; ++i)
      pcm[i] = (short)(Math.Sin(i * 2 * Math.PI / 48) * 10000);

    var encoded = WwiseImaCodec.Encode(pcm, channels: 1, blockAlign: blockAlign);
    var decoded = WwiseImaCodec.Decode(encoded, channels: 1, blockAlign: blockAlign);

    Assert.That(decoded.Length, Is.GreaterThanOrEqualTo(count));
    var maxError = 0;
    for (var i = 0; i < count; ++i)
      maxError = Math.Max(maxError, Math.Abs(decoded[i] - pcm[i]));
    Assert.That(maxError, Is.LessThan(2048), $"max abs error {maxError}");
  }

  [Test]
  public void EncodeDecode_StereoSine_RoundTripsWithinTolerance() {
    const int blockAlign = 0x48; // stereo; per-channel data = 0x48/2 - 4 = 32 bytes
    var samplesPerBlock = (blockAlign / 2 - 4) * 2 + 1; // 65
    var frames = samplesPerBlock * 6;
    var pcm = new short[frames * 2];
    for (var f = 0; f < frames; ++f) {
      pcm[f * 2] = (short)(Math.Sin(f * 2 * Math.PI / 40) * 9000);
      pcm[f * 2 + 1] = (short)(Math.Cos(f * 2 * Math.PI / 55) * 8000);
    }

    var encoded = WwiseImaCodec.Encode(pcm, channels: 2, blockAlign: blockAlign);
    var decoded = WwiseImaCodec.Decode(encoded, channels: 2, blockAlign: blockAlign);

    var maxError = 0;
    for (var i = 0; i < pcm.Length; ++i)
      maxError = Math.Max(maxError, Math.Abs(decoded[i] - pcm[i]));
    Assert.That(maxError, Is.LessThan(2048), $"max abs error {maxError}");
  }

  [Test]
  public void Encode_Empty_ReturnsEmpty() {
    Assert.That(WwiseImaCodec.Encode(ReadOnlySpan<short>.Empty, 1, 0x24).Length, Is.EqualTo(0));
  }
}
