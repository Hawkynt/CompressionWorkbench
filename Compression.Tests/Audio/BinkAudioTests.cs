#pragma warning disable CS1591
using Codec.BinkAudio;

namespace Compression.Tests.Audio;

/// <summary>
/// Pins the Bink Audio decoder (<see cref="BinkAudioCodec"/>), a decode-only port of
/// FFmpeg's <c>libavcodec/binkaudio.c</c> covering both flavours (RDFT and DCT). FFmpeg
/// reference output is not available in this environment, so these tests pin: the 96-entry
/// quantization table spot values against the exact reference formula, the 25-entry
/// critical-frequency table, the band-count/band-boundary derivation for 22050/44100 Hz,
/// the linearity of the inverse transforms, and an end-to-end all-zero-coefficient packet
/// (hand-built bitstream) decoding to silence of the exact expected length.
/// </summary>
[TestFixture]
public class BinkAudioTests {

  // ── tables ──────────────────────────────────────────────────────────────────

  [Test]
  public void QuantTable_SpotValues_MatchReferenceFormula() {
    // binkaudio.c: quant_table[i] = expf(i * 0.15289164787221953823f) * root; the table here
    // stores the root == 1 base, so it must equal exp(i * step) for the documented constant.
    const double step = 0.15289164787221953823;
    Assert.That(BinkAudioTables.QuantBase[0], Is.EqualTo(1.0).Within(1e-12));
    Assert.That(BinkAudioTables.QuantBase[1], Is.EqualTo(Math.Exp(step)).Within(1e-9));
    Assert.That(BinkAudioTables.QuantBase[10], Is.EqualTo(Math.Exp(10 * step)).Within(1e-6));
    Assert.That(BinkAudioTables.QuantBase[95], Is.EqualTo(Math.Exp(95 * step)).Within(1e-1));
    Assert.That(BinkAudioTables.QuantBase.Length, Is.EqualTo(96));
  }

  [Test]
  public void CriticalFreqs_AreThe25ReferenceEntries() {
    int[] expected = [
      100, 200, 300, 400, 510, 630, 770, 920, 1080, 1270, 1480, 1720, 2000, 2320,
      2700, 3150, 3700, 4400, 5300, 6400, 7700, 9500, 12000, 15500, 24500,
    ];
    Assert.That(BinkAudioTables.CriticalFreqs, Is.EqualTo(expected));
  }

  [Test]
  public void RleLengthTab_IsThe16ReferenceEntries() {
    byte[] expected = [2, 3, 4, 5, 6, 8, 9, 10, 11, 12, 13, 14, 15, 16, 32, 64];
    Assert.That(BinkAudioTables.RleLengthTab, Is.EqualTo(expected));
  }

  // ── frame-length / band derivation ──────────────────────────────────────────

  [Test]
  public void FrameLen_DerivesFromSampleRate() {
    // < 22050 → 9 bits (512); < 44100 → 10 bits (1024); else 11 bits (2048). DCT keeps the
    // logical channel count so frame_len is not multiplied (RDFT mono adds log2(1) == 0).
    Assert.That(new BinkAudioCodec(11025, 1, useDct: true, versionB: false).FrameLenForTest, Is.EqualTo(512));
    Assert.That(new BinkAudioCodec(22050, 1, useDct: true, versionB: false).FrameLenForTest, Is.EqualTo(1024));
    Assert.That(new BinkAudioCodec(44100, 1, useDct: true, versionB: false).FrameLenForTest, Is.EqualTo(2048));
  }

  [Test]
  public void BandCount_22050_And_44100() {
    // num_bands counts critical bands up to half the (effective) sample rate (binkaudio.c
    // loop: for num_bands=1; num_bands<25; num_bands++ if half<=crit[num_bands-1] break).
    // 22050/2 = 11025 → first crit >= 11025 is 12000 at index 22 → num_bands 23. 44100/2 =
    // 22050 exceeds every crit up to 15500, so the loop runs out at num_bands 25.
    var b22 = new BinkAudioCodec(22050, 1, useDct: true, versionB: false);
    var b44 = new BinkAudioCodec(44100, 1, useDct: true, versionB: false);
    Assert.That(b22.NumBandsForTest, Is.EqualTo(23));
    Assert.That(b44.NumBandsForTest, Is.EqualTo(25));
  }

  [Test]
  public void Bands_FirstIsTwo_LastIsFrameLen() {
    var c = new BinkAudioCodec(22050, 1, useDct: true, versionB: false);
    var bands = c.BandsForTest;
    Assert.That(bands[0], Is.EqualTo(2));
    Assert.That(bands[c.NumBandsForTest], Is.EqualTo(c.FrameLenForTest));
    // Inner boundaries are even (the reference ANDs with ~1).
    for (var i = 1; i < c.NumBandsForTest; ++i)
      Assert.That(bands[i] & 1, Is.EqualTo(0));
  }

  // ── transforms ──────────────────────────────────────────────────────────────

  [Test]
  public void InverseRdft_AllZero_ProducesZero() {
    var coeffs = new float[514];
    var output = new float[512];
    BinkAudioTransforms.InverseRdft(coeffs, output, 512, 0.5);
    Assert.That(output, Is.All.EqualTo(0.0f));
  }

  [Test]
  public void InverseRdft_DcOnly_IsConstant() {
    // A pure DC spectrum (coeffs[0] = c, everything else 0) inverse-transforms to the
    // constant scale*c in every sample.
    var coeffs = new float[18];
    coeffs[0] = 4.0f;
    var output = new float[16];
    BinkAudioTransforms.InverseRdft(coeffs, output, 16, 0.5);
    Assert.That(output, Is.All.EqualTo(2.0f).Within(1e-5f));
  }

  [Test]
  public void InverseDctIII_AllZero_ProducesZero() {
    var coeffs = new float[10];
    var output = new float[8];
    BinkAudioTransforms.InverseDctIII(coeffs, output, 8, 1.0 / 16);
    Assert.That(output, Is.All.EqualTo(0.0f));
  }

  // ── end-to-end silence ──────────────────────────────────────────────────────

  [Test]
  public void RdftMono_AllZeroPacket_DecodesToSilenceOfExactLength() {
    var c = new BinkAudioCodec(11025, 1, useDct: false, versionB: false);
    var packet = BuildAllZeroRdftMonoPacket(c.FrameLenForTest, c.NumBandsForTest);

    var pcm = c.DecodeStream([packet]);

    // One block emits frame_len - overlap (= frame_len * 15/16) interleaved mono samples.
    var expected = c.FrameLenForTest - c.FrameLenForTest / 16;
    Assert.That(pcm.Length, Is.EqualTo(expected));
    Assert.That(pcm, Is.All.EqualTo((short)0));
  }

  /// <summary>
  /// Hand-builds a single RDFT mono packet whose every coefficient is zero. Layout per
  /// binkaudio.c: 4-byte reported-size prefix; then per channel two get_float values
  /// (5-bit power + 23-bit mantissa + sign) set to 0, num_bands × 8-bit quantizers set to 0,
  /// then a run of (RLE-bit=0 → +8 coeffs, width=0 → zero-fill) groups covering coeffs 2..N.
  /// </summary>
  private static byte[] BuildAllZeroRdftMonoPacket(int frameLen, int numBands) {
    var bw = new LeBitWriter();
    bw.Put(32, 0);                 // reported size (skipped by the decoder)

    // two zero floats: power(5)=0, mantissa(23)=0, sign(1)=0 → 29 bits each.
    bw.Put(29, 0);
    bw.Put(29, 0);

    for (var i = 0; i < numBands; ++i)
      bw.Put(8, 0);                // quantizer index 0 for every band

    var idx = 2;
    while (idx < frameLen) {
      bw.Put(1, 0);                // RLE bit clear → run of 8 coeffs
      var j = Math.Min(idx + 8, frameLen);
      bw.Put(4, 0);                // width 0 → zero-fill, no per-coeff bits
      idx = j;
    }

    return bw.ToArray();
  }

  /// <summary>Minimal LSB-first bit writer mirroring the decoder's bit order.</summary>
  private sealed class LeBitWriter {
    private readonly List<byte> _bytes = [];
    private int _cur;
    private int _bit;

    public void Put(int n, uint value) {
      for (var i = 0; i < n; ++i) {
        var b = (int)((value >> i) & 1);
        this._cur |= b << this._bit;
        if (++this._bit == 8) {
          this._bytes.Add((byte)this._cur);
          this._cur = 0;
          this._bit = 0;
        }
      }
    }

    public byte[] ToArray() {
      if (this._bit != 0) {
        this._bytes.Add((byte)this._cur);
        this._cur = 0;
        this._bit = 0;
      }
      return this._bytes.ToArray();
    }
  }
}
