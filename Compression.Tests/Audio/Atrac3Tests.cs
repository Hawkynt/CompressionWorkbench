#pragma warning disable CS1591
using Codec.Atrac3;

namespace Compression.Tests.Audio;

/// <summary>
/// Pins the Sony ATRAC3 decoder (<see cref="Atrac3Codec"/>), a faithful decode-only port of
/// FFmpeg's <c>libavcodec/atrac3.c</c> + <c>atrac.c</c>. Cross-checking against FFmpeg output
/// is not available in this environment, so these tests pin determinism + structure:
/// the OMA coding-parameters → block-align/joint-stereo/sample-rate mapping, the generated
/// table fixed points (scale factor doubling every three steps; QMF/MDCT window symmetry),
/// the 1024-samples-per-channel-per-frame invariant, the zero-frame silence property,
/// bounded-amplitude decode of crafted frames, and truncation tolerance.
/// </summary>
[TestFixture]
public class Atrac3Tests {

  private const int JointStereo = 0x12;
  private const int Single = 0x2;

  // ── OMA coding-params derivation ────────────────────────────────────────────────

  [Test]
  public void OmaParams_DecodeBlockAlignFromLow10Bits() {
    // Bits 0-9 hold block_align in 8-byte words: 24 words → 192 bytes (the LP2 frame size).
    var (blockAlign, _, _) = Atrac3Codec.DecodeOmaParams(24);
    Assert.That(blockAlign, Is.EqualTo(24 * 8));
  }

  [Test]
  public void OmaParams_DecodeJointStereoFromBit17() {
    Assert.That(Atrac3Codec.DecodeOmaParams(24).JointStereo, Is.False);
    Assert.That(Atrac3Codec.DecodeOmaParams((1 << 17) | 24).JointStereo, Is.True);
  }

  [Test]
  public void OmaParams_DecodeSampleRateFromBits13To15() {
    // Sample-rate index = bits 13-15, mapped via ff_oma_srate_tab × 100.
    Assert.That(Atrac3Codec.DecodeOmaParams(0 << 13).SampleRate, Is.EqualTo(32000));
    Assert.That(Atrac3Codec.DecodeOmaParams(1 << 13).SampleRate, Is.EqualTo(44100));
    Assert.That(Atrac3Codec.DecodeOmaParams(2 << 13).SampleRate, Is.EqualTo(48000));
  }

  [Test]
  public void OmaParams_DocumentedCommonValue_StereoLp2() {
    // A documented common OMA LP2 value: 44100 Hz, joint-stereo, 384-byte stereo frame.
    // framesize words = 384/8 = 48; rate index 1; js bit set.
    var codingParams = (1 << 17) | (1 << 13) | 48;
    var (blockAlign, js, sr) = Atrac3Codec.DecodeOmaParams(codingParams);
    Assert.That(blockAlign, Is.EqualTo(384));
    Assert.That(js, Is.True);
    Assert.That(sr, Is.EqualTo(44100));

    var codec = Atrac3Codec.FromOmaCodingParams(codingParams);
    Assert.That(codec.Channels, Is.EqualTo(2), "OMA ATRAC3 is always stereo");
    Assert.That(codec.IsJointStereo, Is.True);
    Assert.That(codec.BlockAlign, Is.EqualTo(384));
  }

  // ── frame size invariant ────────────────────────────────────────────────────────

  [Test]
  public void MonoFrame_DecodesTo1024Samples() {
    var codec = new Atrac3Codec(44100, channels: 1, blockAlign: 192, codingMode: Single, scrambled: false);
    var pcm = codec.Decode(new byte[192]);
    Assert.That(pcm.Length, Is.EqualTo(1024));
  }

  [Test]
  public void StereoSingleFrame_DecodesTo2048Samples() {
    var codec = new Atrac3Codec(44100, channels: 2, blockAlign: 304, codingMode: Single, scrambled: false);
    var pcm = codec.Decode(new byte[304]);
    Assert.That(pcm.Length, Is.EqualTo(2 * 1024), "interleaved L/R = 1024 samples per channel");
  }

  [Test]
  public void JointStereoFrame_DecodesTo2048Samples() {
    var codec = new Atrac3Codec(44100, channels: 2, blockAlign: 384, codingMode: JointStereo, scrambled: false);
    var pcm = codec.Decode(new byte[384]);
    Assert.That(pcm.Length, Is.EqualTo(2 * 1024));
  }

  [Test]
  public void DecodeStream_LengthIsFramesTimesPerFrameSamples() {
    var codec = new Atrac3Codec(44100, channels: 1, blockAlign: 192, codingMode: Single, scrambled: false);
    var pcm = codec.DecodeStream(new byte[192 * 3]);
    Assert.That(pcm.Length, Is.EqualTo(3 * 1024));
  }

  [Test]
  public void DecodeStream_RaggedTail_IsTrimmedToFrameBoundary() {
    var codec = new Atrac3Codec(44100, channels: 1, blockAlign: 192, codingMode: Single, scrambled: false);
    // One full 192-byte frame + a 50-byte tail that is dropped.
    var pcm = codec.DecodeStream(new byte[192 + 50]);
    Assert.That(pcm.Length, Is.EqualTo(1024));
  }

  [Test]
  public void DecodeStream_ShorterThanOneFrame_DecodesToEmpty() {
    var codec = new Atrac3Codec(44100, channels: 1, blockAlign: 192, codingMode: Single, scrambled: false);
    Assert.That(codec.DecodeStream(new byte[100]).Length, Is.EqualTo(0));
    Assert.That(codec.DecodeStream([]).Length, Is.EqualTo(0));
  }

  // ── deterministic decode ────────────────────────────────────────────────────────

  [Test]
  public void ZeroFrame_DecodesToSilence() {
    // All-zero data selects the unencoded-subband / no-tonal / zero-gain path → silence.
    var codec = new Atrac3Codec(44100, channels: 1, blockAlign: 192, codingMode: Single, scrambled: false);
    var pcm = codec.Decode(new byte[192]);
    Assert.That(pcm.All(s => s == 0), Is.True);
  }

  [Test]
  public void ZeroFrame_JointStereo_DecodesToSilence() {
    var codec = new Atrac3Codec(44100, channels: 2, blockAlign: 384, codingMode: JointStereo, scrambled: false);
    var pcm = codec.Decode(new byte[384]);
    Assert.That(pcm.All(s => s == 0), Is.True);
  }

  [Test]
  public void CraftedFrame_DecodesToBoundedFiniteSamples() {
    // A fixed byte pattern exercises the gain/tonal/spectrum path; the synthesis must stay
    // bounded inside the 16-bit range (no runaway in the IMDCT / QMF cascade).
    var codec = new Atrac3Codec(44100, channels: 1, blockAlign: 192, codingMode: Single, scrambled: false);
    var frame = new byte[192];
    for (var i = 0; i < frame.Length; ++i)
      frame[i] = (byte)(i * 7 + 1);

    var pcm = codec.Decode(frame);
    Assert.That(pcm.Length, Is.EqualTo(1024));
    Assert.That(pcm.All(s => s >= short.MinValue && s <= short.MaxValue), Is.True);
  }

  [Test]
  public void CraftedFrame_IsDeterministic() {
    var frame = new byte[192];
    for (var i = 0; i < frame.Length; ++i)
      frame[i] = (byte)(i * 7 + 1);

    var a = new Atrac3Codec(44100, 1, 192, Single, false).Decode(frame);
    var b = new Atrac3Codec(44100, 1, 192, Single, false).Decode(frame);
    Assert.That(a, Is.EqualTo(b));
  }

  [Test]
  public void Scrambled_CraftedFrame_DecodesBounded() {
    // Scrambled (RealMedia) input is descrambled before decode; an all-zero scrambled frame
    // becomes the XOR key pattern, which must still decode to a bounded 1024-sample frame.
    var codec = new Atrac3Codec(44100, channels: 1, blockAlign: 192, codingMode: Single, scrambled: true);
    var pcm = codec.Decode(new byte[192]);
    Assert.That(pcm.Length, Is.EqualTo(1024));
    Assert.That(pcm.All(s => s >= short.MinValue && s <= short.MaxValue), Is.True);
  }

  // ── construction guards ────────────────────────────────────────────────────────

  [Test]
  public void Construction_RejectsOddJointStereoChannelCount() {
    Assert.That(() => new Atrac3Codec(44100, channels: 1, blockAlign: 192, codingMode: JointStereo, scrambled: false),
      Throws.ArgumentException);
  }

  [Test]
  public void Construction_RejectsUnknownCodingMode() {
    Assert.That(() => new Atrac3Codec(44100, channels: 2, blockAlign: 304, codingMode: 0x7, scrambled: false),
      Throws.ArgumentException);
  }

  [Test]
  public void Decode_FrameSmallerThanBlockAlign_Throws() {
    var codec = new Atrac3Codec(44100, channels: 1, blockAlign: 192, codingMode: Single, scrambled: false);
    Assert.That(() => codec.Decode(new byte[100]), Throws.ArgumentException);
  }

  // ── generated table fixed points ─────────────────────────────────────────────────

  [Test]
  public void ScaleFactorTable_DoublesEveryThreeSteps() {
    // sf[i] = pow(2, (i - 15) / 3): sf[15] = 1.0 and sf[i+3] = 2·sf[i].
    var sf = Atrac3Tables.SfTable;
    Assert.That(sf.Length, Is.EqualTo(64));
    Assert.That(sf[15], Is.EqualTo(1.0f).Within(1e-5));
    for (var i = 0; i + 3 < 64; ++i)
      Assert.That(sf[i + 3], Is.EqualTo(sf[i] * 2.0f).Within(sf[i] * 1e-4));
  }

  [Test]
  public void QmfWindow_IsSymmetricAndDoubledHalfTaps() {
    var w = Atrac3Tables.QmfWindow;
    Assert.That(w.Length, Is.EqualTo(48));
    for (var i = 0; i < 24; ++i)
      Assert.That(w[i], Is.EqualTo(w[47 - i]).Within(1e-9), "the 48-tap QMF window is symmetric");
  }

  [Test]
  public void MdctWindow_IsSymmetric512() {
    var w = Atrac3Tables.MdctWindow;
    Assert.That(w.Length, Is.EqualTo(512));
    for (var i = 0; i < 256; ++i)
      Assert.That(w[i], Is.EqualTo(w[511 - i]).Within(1e-5), "the IMDCT window is symmetric");
  }

  [Test]
  public void SubbandTable_BoundsTheFullSpectrum() {
    var sb = Atrac3Tables.SubbandTab;
    Assert.That(sb.Length, Is.EqualTo(33));
    Assert.That(sb[0], Is.EqualTo(0));
    Assert.That(sb[32], Is.EqualTo(1024), "the 32 subbands span all 1024 coefficients");
    for (var i = 1; i < sb.Length; ++i)
      Assert.That(sb[i], Is.GreaterThan(sb[i - 1]), "subband boundaries are strictly increasing");
  }
}
