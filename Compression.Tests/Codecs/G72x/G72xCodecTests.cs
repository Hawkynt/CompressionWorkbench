#pragma warning disable CS1591
using Codec.G72x;

namespace Compression.Tests.Codecs.G72x;

/// <summary>
/// Pins the ITU-T G.726 ADPCM decoder/encoder across all four rates. Decoding is held to
/// known answers from the ITU-T G.191 reference implementation; the encoder, being a
/// backward-adaptive predictor whose output is lossy, is verified by encode→decode
/// round-trip fidelity plus exact sample-count and packing invariants.
/// </summary>
[TestFixture]
public class G72xCodecTests {

  // A speech-like two-tone waveform at 8 kHz.
  private static short[] SpeechLike(int n) {
    var pcm = new short[n];
    for (var i = 0; i < n; ++i) {
      var t = i / 8000.0;
      pcm[i] = (short)(8000 * Math.Sin(2 * Math.PI * 300 * t)
                       + 3000 * Math.Sin(2 * Math.PI * 1100 * t));
    }
    return pcm;
  }

  [Test]
  public void DecodeG721_ProducesTwoSamplesPerByte() {
    var data = new byte[10];
    for (var i = 0; i < data.Length; ++i) data[i] = (byte)(i * 17);
    var pcm = G72xCodec.DecodeG721(data);
    Assert.That(pcm.Length, Is.EqualTo(20));
  }

  [Test]
  public void EncodeG721_ProducesOneBytePerTwoSamples() {
    var pcm = SpeechLike(400);
    var enc = G72xCodec.EncodeG721(pcm);
    Assert.That(enc.Length, Is.EqualTo(200));
  }

  [Test]
  public void EncodeThenDecode_PreservesSampleCount() {
    var pcm = SpeechLike(2000);
    var dec = G72xCodec.DecodeG721(G72xCodec.EncodeG721(pcm));
    Assert.That(dec.Length, Is.EqualTo(pcm.Length));
  }

  [Test]
  public void EncodeThenDecode_IsCloseToOriginal() {
    var pcm = SpeechLike(2000);
    var dec = G72xCodec.DecodeG721(G72xCodec.EncodeG721(pcm));

    long maxError = 0;
    double signal = 0, noise = 0;
    // Skip the predictor warm-up region before measuring fidelity.
    for (var i = 50; i < pcm.Length; ++i) {
      long e = Math.Abs(pcm[i] - dec[i]);
      if (e > maxError) maxError = e;
      double d = pcm[i] - dec[i];
      noise += d * d;
      signal += (double)pcm[i] * pcm[i];
    }
    var snr = 10 * Math.Log10(signal / noise);

    Assert.That(maxError, Is.LessThan(2000), $"max error {maxError} too high");
    Assert.That(snr, Is.GreaterThan(20.0), $"SNR {snr:F1} dB too low");
  }

  [Test]
  public void Decode_IsDeterministic() {
    var pcm = SpeechLike(500);
    var enc = G72xCodec.EncodeG721(pcm);
    Assert.That(G72xCodec.DecodeG721(enc), Is.EqualTo(G72xCodec.DecodeG721(enc)));
  }

  [Test]
  public void Encode_OddSampleCount_RoundsUpByteCount() {
    var pcm = SpeechLike(401);
    var enc = G72xCodec.EncodeG721(pcm);
    Assert.That(enc.Length, Is.EqualTo(201));
  }

  [Test]
  public void Decode_Silence_StaysNearZero() {
    // All-zero codewords decode to a slowly settling near-silent signal.
    var dec = G72xCodec.DecodeG721(new byte[100]);
    Assert.That(dec.Length, Is.EqualTo(200));
    foreach (var s in dec)
      Assert.That(Math.Abs((int)s), Is.LessThan(4000));
  }

  // ── Full G.726 rate set (2/3/4/5-bit = 16/24/32/40 kbit/s). ──────────────────

  /// <summary>
  /// Decoder known answers taken from the ITU-T G.191 Software Tools Library reference
  /// implementation (module <c>G726</c>, <c>G726_decode</c>), sampled at the linear
  /// reconstructed signal SR and scaled to 16 bits — that is, before the output PCM format
  /// conversion and synchronous coding adjustment of G.726 § 4.2.8, which our decoder does
  /// not perform because it emits linear PCM rather than A-law/µ-law.
  /// <para>
  /// The codeword streams were produced by the same reference implementation's encoder, so
  /// they stay inside the range a conformant encoder emits. Each case pins 32 samples.
  /// </para>
  /// </summary>
  [TestCase(2, "95555552ABB00051", new short[] {
    -60, 64, 72, 80, 92, 108, 124, 172, 232, 320, 496, 836, 1364, 2244, 1488, -1752,
    -3800, -7000, -11284, -7460, -15496, -10596, -2656, -144, 1336, 2184, 2632, 2736, 5368, 8824, 6840, 11744,
  })]
  [TestCase(3, "8DB6DB6DB67E92DDBF28964A", new short[] {
    -60, 72, 80, 92, 108, 132, 176, 240, 360, 544, 972, 1912, 3744, 2688, 1180, -344,
    -3544, -8148, -10280, -11888, -8972, -8540, -5152, -3908, -788, 2696, 2648, 2972, 6712, 5744, 5436, 7208,
  })]
  [TestCase(4, "F77777777F8A9ABCDF245555321DBAAA", new short[] {
    0, 88, 120, 172, 244, 376, 584, 1052, 2116, 440, -2744, -7232, -11368, -13116, -12192, -10632,
    -8268, -4436, -488, 3640, 6988, 9148, 10884, 12300, 10224, 8032, 5656, 1392, -2868, -6512, -8984, -11052,
  })]
  [TestCase(5, "83DEF7BDEF7BDEA387F79CED7BEFE2318C64A0CB", new short[] {
    -188, 212, 228, 280, 356, 452, 608, 920, 1436, 2280, 4408, 5212, 4348, 2144, 1640, -1288,
    -5180, -8708, -9460, -9896, -10768, -7664, -5028, -2888, 532, 2128, 3112, 3632, 5704, 6332, 5680, 9256,
  })]
  public void G726_Decode_MatchesItuReference(int bits, string hex, short[] expected) {
    var decoded = G72xCodec.DecodeG726(Convert.FromHexString(hex), bits);
    Assert.That(decoded, Is.EqualTo(expected));
  }

  private static double RoundTripSnr(short[] pcm, int bits, int warmup = 50) {
    var dec = G72xCodec.DecodeG726(G72xCodec.EncodeG726(pcm, bits), bits);
    double signal = 0, noise = 0;
    for (var i = warmup; i < pcm.Length; ++i) {
      double d = pcm[i] - dec[i];
      noise += d * d;
      signal += (double)pcm[i] * pcm[i];
    }
    return 10 * Math.Log10(signal / noise);
  }

  [TestCase(2)]
  [TestCase(3)]
  [TestCase(4)]
  [TestCase(5)]
  public void G726_EncodeThenDecode_PreservesSampleCount(int bits) {
    var pcm = SpeechLike(2000);
    var dec = G72xCodec.DecodeG726(G72xCodec.EncodeG726(pcm, bits), bits);
    Assert.That(dec.Length, Is.GreaterThanOrEqualTo(pcm.Length - 8));
    Assert.That(dec.Length, Is.LessThanOrEqualTo(pcm.Length));
  }

  [Test]
  public void G726_4Bit_MatchesG721() {
    var pcm = SpeechLike(1000);
    Assert.That(G72xCodec.EncodeG726(pcm, 4), Is.EqualTo(G72xCodec.EncodeG721(pcm)));
    var enc = G72xCodec.EncodeG721(pcm);
    Assert.That(G72xCodec.DecodeG726(enc, 4), Is.EqualTo(G72xCodec.DecodeG721(enc)));
  }

  // Per-rate SNR thresholds: more bits per sample → tighter reconstruction.
  [TestCase(2, 4.0)]
  [TestCase(3, 10.0)]
  [TestCase(4, 20.0)]
  [TestCase(5, 24.0)]
  public void G726_RoundTrip_MeetsSnrThreshold(int bits, double minSnr) {
    var snr = RoundTripSnr(SpeechLike(2000), bits);
    Assert.That(snr, Is.GreaterThan(minSnr), $"G.726@{bits}-bit SNR {snr:F1} dB below {minSnr} dB");
  }

  [TestCase(2)]
  [TestCase(3)]
  [TestCase(4)]
  public void G726_RoundTrip_HigherRateIsNotWorse(int bits) {
    // Sanity: increasing the rate should not degrade fidelity versus the rate below.
    var pcm = SpeechLike(2000);
    var lower = RoundTripSnr(pcm, bits);
    var higher = RoundTripSnr(pcm, bits + 1);
    Assert.That(higher, Is.GreaterThan(lower - 1.0), $"{bits + 1}-bit ({higher:F1}) worse than {bits}-bit ({lower:F1})");
  }

  [TestCase(2)]
  [TestCase(3)]
  [TestCase(5)]
  public void G726_IsDeterministic(int bits) {
    var enc = G72xCodec.EncodeG726(SpeechLike(500), bits);
    Assert.That(G72xCodec.DecodeG726(enc, bits), Is.EqualTo(G72xCodec.DecodeG726(enc, bits)));
  }

  [TestCase(2, 25)]   // 100 samples × 2 bits = 200 bits = 25 bytes
  [TestCase(3, 38)]   // 100 × 3 = 300 bits → 38 bytes (rounded up)
  [TestCase(5, 63)]   // 100 × 5 = 500 bits → 63 bytes
  public void G726_Encode_PacksExpectedByteCount(int bits, int expectedBytes) {
    var enc = G72xCodec.EncodeG726(SpeechLike(100), bits);
    Assert.That(enc.Length, Is.EqualTo(expectedBytes));
  }

  [TestCase(2)]
  [TestCase(3)]
  [TestCase(5)]
  public void G726_Decode_Silence_StaysNearZero(int bits) {
    var dec = G72xCodec.DecodeG726(new byte[100], bits);
    foreach (var s in dec)
      Assert.That(Math.Abs((int)s), Is.LessThan(4000));
  }

  [TestCase(0)]
  [TestCase(1)]
  [TestCase(6)]
  public void G726_RejectsUnsupportedRate(int bits) {
    Assert.Throws<ArgumentOutOfRangeException>(() => G72xCodec.EncodeG726(SpeechLike(10), bits));
    Assert.Throws<ArgumentOutOfRangeException>(() => G72xCodec.DecodeG726(new byte[4], bits));
  }
}
