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
}
