#pragma warning disable CS1591
using Codec.G72x;

namespace Compression.Tests.Codecs.G72x;

/// <summary>
/// Pins the ITU-T G.726 @ 32 kbit/s (G.721) ADPCM decoder/encoder. The reference is a
/// backward-adaptive predictor, so correctness is verified by encode→decode round-trip
/// fidelity (ADPCM is lossy) plus exact sample-count and packing invariants rather than
/// fixed golden samples.
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
