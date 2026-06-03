using Codec.AicaAdpcm;

namespace Compression.Tests.Codecs.AicaAdpcm;

[TestFixture]
public class AicaAdpcmCodecTests {

  // ──────────── 1. Known-sequence decode (hand-computed) ────────────

  /// <summary>
  /// Decodes two bytes (low nibble first) against hand-computed reference samples.
  /// Starting state predictor=0, step=127:
  ///   nibble 0x4 → diff=((2*4+1)*127)>>3 = (9*127)>>3 = 142, predictor=142, step=(127*307)>>8=152
  ///   nibble 0x4 → diff=(9*152)>>3 = 171, predictor=313, step=(152*307)>>8=182
  ///   nibble 0x8 → mag 0, sign set; diff=(1*182)>>3=22, predictor=313-22=291, step=(182*230)>>8=163
  ///   nibble 0x0 → diff=(1*163)>>3=20, predictor=291+20=311
  /// </summary>
  [Test]
  public void Decode_KnownNibbleSequence_ProducesExpectedSamples() {
    // 0x44 → low 0x4, high 0x4 ; 0x08 → low 0x8, high 0x0
    var data = new byte[] { 0x44, 0x08 };
    var pcm = AicaAdpcmCodec.Decode(data);

    Assert.That(pcm.Length, Is.EqualTo(4));
    Assert.Multiple(() => {
      Assert.That(pcm[0], Is.EqualTo(142));
      Assert.That(pcm[1], Is.EqualTo(313));
      Assert.That(pcm[2], Is.EqualTo(291));
      Assert.That(pcm[3], Is.EqualTo(311));
    });
  }

  [Test]
  public void Decode_YieldsTwoSamplesPerByte() {
    var data = new byte[] { 0x00, 0x11, 0x22 };
    var pcm = AicaAdpcmCodec.Decode(data);
    Assert.That(pcm.Length, Is.EqualTo(6));
  }

  // ──────────── 2. Predictor + step clamping ────────────

  /// <summary>A long run of maximum positive magnitudes (0x7) clamps the predictor at +32767.</summary>
  [Test]
  public void Decode_RunOfMaxPositiveNibbles_ClampsPredictor() {
    var data = new byte[256];
    for (var i = 0; i < data.Length; ++i) data[i] = 0x77; // both nibbles 0x7 (max positive magnitude)
    var pcm = AicaAdpcmCodec.Decode(data);
    Assert.That(pcm[^1], Is.EqualTo(short.MaxValue));
  }

  /// <summary>A long run of maximum negative magnitudes (0xF) clamps the predictor at -32768.</summary>
  [Test]
  public void Decode_RunOfMaxNegativeNibbles_ClampsPredictor() {
    var data = new byte[256];
    for (var i = 0; i < data.Length; ++i) data[i] = 0xFF; // both nibbles 0xF (sign + max magnitude)
    var pcm = AicaAdpcmCodec.Decode(data);
    Assert.That(pcm[^1], Is.EqualTo(short.MinValue));
  }

  /// <summary>The step never decays below its floor: a run of zero-magnitude codes holds it at 127.</summary>
  [Test]
  public void Decode_ZeroMagnitudeRun_KeepsStepAtFloor() {
    // nibble 0 keeps the predictor flat (diff = step>>3 each step) but the step adapts by 230/256<1,
    // flooring at 127 — so each step adds the same minimum increment of 127>>3 = 15.
    var data = new byte[8];
    var pcm = AicaAdpcmCodec.Decode(data);
    Assert.That(pcm[0], Is.EqualTo(15));
    for (var i = 1; i < pcm.Length; ++i)
      Assert.That(pcm[i] - pcm[i - 1], Is.EqualTo(15), $"flat-step increment at {i}");
  }

  // ──────────── 3. Encode → Decode round-trip (lossy) ────────────

  [Test]
  public void EncodeDecode_SineRamp_RoundTripsWithinTolerance() {
    const int n = 4000;
    var pcm = new short[n];
    for (var i = 0; i < n; ++i) {
      var amp = 12000.0 * i / n;
      pcm[i] = (short)(amp * Math.Sin(2 * Math.PI * i / 64.0));
    }

    var encoded = AicaAdpcmCodec.Encode(pcm);
    var decoded = AicaAdpcmCodec.Decode(encoded);

    Assert.That(decoded.Length, Is.GreaterThanOrEqualTo(n));
    var maxErr = 0;
    for (var i = 0; i < n; ++i)
      maxErr = Math.Max(maxErr, Math.Abs(pcm[i] - decoded[i]));

    Assert.That(maxErr, Is.LessThan(4000), $"max sample error {maxErr} exceeds ADPCM tolerance");
  }

  [Test]
  public void Encode_PacksTwoSamplesPerByte() {
    var pcm = new short[10];
    var encoded = AicaAdpcmCodec.Encode(pcm);
    Assert.That(encoded.Length, Is.EqualTo(5));
  }

  [Test]
  public void Encode_OddSampleCount_PadsToFullByte() {
    var pcm = new short[7];
    var encoded = AicaAdpcmCodec.Encode(pcm);
    Assert.That(encoded.Length, Is.EqualTo(4));
  }
}
