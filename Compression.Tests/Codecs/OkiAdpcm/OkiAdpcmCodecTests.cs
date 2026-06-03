using Codec.OkiAdpcm;

namespace Compression.Tests.Codecs.OkiAdpcm;

[TestFixture]
public class OkiAdpcmCodecTests {

  // ──────────── 1. Known-sequence decode (hand-computed) ────────────

  /// <summary>
  /// Decodes two bytes (high nibble first) against hand-computed reference samples.
  /// Starting state predictor=0, index=0 (step=16):
  ///   nibble 0x4 → delta=16/8+16=18, predictor=18  → 18&lt;&lt;4 = 288, index→2 (step 19)
  ///   nibble 0x4 → delta=19/8+19=21, predictor=39  → 39&lt;&lt;4 = 624, index→4 (step 23)
  ///   nibble 0x0 → delta=23/8=2,     predictor=41  → 41&lt;&lt;4 = 656, index→3 (step 21)
  ///   nibble 0x8 → delta=21/8=2 (-), predictor=39  → 39&lt;&lt;4 = 624, index→2
  /// </summary>
  [Test]
  public void Decode_KnownNibbleSequence_ProducesExpectedSamples() {
    // 0x44 → high 0x4, low 0x4 ; 0x08 → high 0x0, low 0x8
    var data = new byte[] { 0x44, 0x08 };
    var pcm = OkiAdpcmCodec.Decode(data);

    Assert.That(pcm.Length, Is.EqualTo(4));
    Assert.Multiple(() => {
      Assert.That(pcm[0], Is.EqualTo(288));
      Assert.That(pcm[1], Is.EqualTo(624));
      Assert.That(pcm[2], Is.EqualTo(656));
      Assert.That(pcm[3], Is.EqualTo(624));
    });
  }

  [Test]
  public void Decode_YieldsTwoSamplesPerByte() {
    var data = new byte[] { 0x00, 0x11, 0x22 };
    var pcm = OkiAdpcmCodec.Decode(data);
    Assert.That(pcm.Length, Is.EqualTo(6));
  }

  // ──────────── 2. Predictor clamping ────────────

  /// <summary>A long run of maximum positive deltas clamps the predictor at +2047 (→ 32752 after &lt;&lt;4).</summary>
  [Test]
  public void Decode_RunOfMaxPositiveNibbles_ClampsPredictor() {
    var data = new byte[64];
    for (var i = 0; i < data.Length; ++i) data[i] = 0x77; // both nibbles 0x7 (max positive magnitude)
    var pcm = OkiAdpcmCodec.Decode(data);
    Assert.That(pcm[^1], Is.EqualTo(PredictorMaxScaled));
  }

  /// <summary>A long run of maximum negative deltas clamps the predictor at -2048 (→ -32768 after &lt;&lt;4).</summary>
  [Test]
  public void Decode_RunOfMaxNegativeNibbles_ClampsPredictor() {
    var data = new byte[64];
    for (var i = 0; i < data.Length; ++i) data[i] = 0xFF; // both nibbles 0xF (sign + max magnitude)
    var pcm = OkiAdpcmCodec.Decode(data);
    Assert.That(pcm[^1], Is.EqualTo(PredictorMinScaled));
  }

  private const short PredictorMaxScaled = 2047 << 4;   // 32752
  private const short PredictorMinScaled = unchecked((short)(-2048 << 4)); // -32768

  // ──────────── 3. Encode → Decode round-trip (lossy) ────────────

  [Test]
  public void EncodeDecode_SineRamp_RoundTripsWithinTolerance() {
    const int n = 4000;
    var pcm = new short[n];
    for (var i = 0; i < n; ++i) {
      // A swept-amplitude sine — ADPCM tracks smooth waveforms well.
      var amp = 6000.0 * i / n;
      pcm[i] = (short)(amp * Math.Sin(2 * Math.PI * i / 64.0));
    }

    var encoded = OkiAdpcmCodec.Encode(pcm);
    var decoded = OkiAdpcmCodec.Decode(encoded);

    Assert.That(decoded.Length, Is.GreaterThanOrEqualTo(n));
    var maxErr = 0;
    for (var i = 0; i < n; ++i)
      maxErr = Math.Max(maxErr, Math.Abs(pcm[i] - decoded[i]));

    Assert.That(maxErr, Is.LessThan(1500), $"max sample error {maxErr} exceeds ADPCM tolerance");
  }

  [Test]
  public void Encode_PacksTwoSamplesPerByte() {
    var pcm = new short[10];
    var encoded = OkiAdpcmCodec.Encode(pcm);
    Assert.That(encoded.Length, Is.EqualTo(5));
  }

  [Test]
  public void Encode_OddSampleCount_PadsToFullByte() {
    var pcm = new short[7];
    var encoded = OkiAdpcmCodec.Encode(pcm);
    Assert.That(encoded.Length, Is.EqualTo(4));
  }
}
