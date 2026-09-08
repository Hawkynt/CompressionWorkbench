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

  [Test]
  public void Decode_RunOfMaxPositiveNibbles_ClampsPredictor() {
    var data = new byte[256];
    Array.Fill(data, (byte)0x77);
    var pcm = AicaAdpcmCodec.Decode(data);
    Assert.That(pcm[^1], Is.EqualTo(short.MaxValue));
  }

  [Test]
  public void Decode_RunOfMaxNegativeNibbles_ClampsPredictor() {
    var data = new byte[256];
    Array.Fill(data, (byte)0xFF);
    var pcm = AicaAdpcmCodec.Decode(data);
    Assert.That(pcm[^1], Is.EqualTo(short.MinValue));
  }

  [Test]
  public void Decode_ZeroMagnitudeRun_KeepsStepAtFloor() {
    var data = new byte[8];
    var pcm = AicaAdpcmCodec.Decode(data);
    Assert.That(pcm[0], Is.EqualTo(15));
    for (var i = 1; i < pcm.Length; ++i)
      Assert.That(pcm[i] - pcm[i - 1], Is.EqualTo(15), $"flat-step increment at {i}");
  }

  // ──────────── 3. Encoder decisions from the AICA manual ────────────

  /// <summary>
  /// FQ8005 table 1 chooses the three magnitude bits at exact quarters of the
  /// current quantizer width. The initial width is 127, so the first positive
  /// code changes at ceil(127*n/4). This catches the old midpoint-style encoder,
  /// which chose a self-consistent but non-AICA code at these boundaries.
  /// </summary>
  [TestCase(31, 0x0)]
  [TestCase(32, 0x1)]
  [TestCase(63, 0x1)]
  [TestCase(64, 0x2)]
  [TestCase(95, 0x2)]
  [TestCase(96, 0x3)]
  [TestCase(126, 0x3)]
  [TestCase(127, 0x4)]
  [TestCase(158, 0x4)]
  [TestCase(159, 0x5)]
  [TestCase(190, 0x5)]
  [TestCase(191, 0x6)]
  [TestCase(222, 0x6)]
  [TestCase(223, 0x7)]
  [TestCase(-31, 0x8)]
  [TestCase(-32, 0x9)]
  public void Encode_FirstSample_UsesOfficialQuarterStepThresholds(int sample, int expectedNibble) {
    var encoded = AicaAdpcmCodec.Encode([(short)sample]);
    Assert.That(encoded[0] & 0x0F, Is.EqualTo(expectedNibble));
  }

  // ──────────── 4. Encode → Decode round-trip (lossy) ────────────

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
