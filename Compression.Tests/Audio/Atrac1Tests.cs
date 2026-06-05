#pragma warning disable CS1591
using Codec.Atrac1;

namespace Compression.Tests.Audio;

/// <summary>
/// Pins the Sony ATRAC1 / MiniDisc decoder (<see cref="Atrac1Codec"/>), a faithful decode-only
/// port of FFmpeg's <c>libavcodec/atrac1.c</c> + <c>atrac.c</c>. Cross-checking against FFmpeg
/// output is not available in this environment, so these tests pin determinism + structure: the
/// BFU tables, the QMF/sine windows shared with the ATRAC family, the 512-samples-per-channel
/// frame invariant, the zero-frame silence property, bounded-amplitude decode of crafted frames,
/// and truncation tolerance.
/// </summary>
[TestFixture]
public class Atrac1Tests {

  // ── frame size invariant ────────────────────────────────────────────────────────

  [Test]
  public void MonoFrame_DecodesTo512Samples() {
    var codec = new Atrac1Codec(1);
    Assert.That(codec.FrameSize, Is.EqualTo(212));
    var pcm = codec.Decode(new byte[212]);
    Assert.That(pcm.Length, Is.EqualTo(512));
  }

  [Test]
  public void StereoFrame_DecodesTo1024Samples() {
    var codec = new Atrac1Codec(2);
    Assert.That(codec.FrameSize, Is.EqualTo(424));
    var pcm = codec.Decode(new byte[424]);
    Assert.That(pcm.Length, Is.EqualTo(2 * 512), "interleaved L/R = 512 samples per channel");
  }

  [Test]
  public void DecodeStream_LengthIsFramesTimesPerFrameSamples() {
    var codec = new Atrac1Codec(1);
    var pcm = codec.DecodeStream(new byte[212 * 3]);
    Assert.That(pcm.Length, Is.EqualTo(3 * 512));
  }

  [Test]
  public void DecodeStream_RaggedTail_IsTrimmedToFrameBoundary() {
    var codec = new Atrac1Codec(1);
    var pcm = codec.DecodeStream(new byte[212 + 50]);
    Assert.That(pcm.Length, Is.EqualTo(512));
  }

  [Test]
  public void DecodeStream_ShorterThanOneFrame_DecodesToEmpty() {
    var codec = new Atrac1Codec(1);
    Assert.That(codec.DecodeStream(new byte[100]).Length, Is.EqualTo(0));
    Assert.That(codec.DecodeStream([]).Length, Is.EqualTo(0));
  }

  [Test]
  public void Decode_FrameSmallerThanFrameSize_Throws() {
    var codec = new Atrac1Codec(1);
    Assert.That(() => codec.Decode(new byte[100]), Throws.ArgumentException);
  }

  // ── deterministic decode ────────────────────────────────────────────────────────

  [Test]
  public void ZeroFrame_DecodesToSilence() {
    // All-zero data: long-mode windows, zero word lengths → empty BFUs → silence.
    var codec = new Atrac1Codec(1);
    var pcm = codec.Decode(new byte[212]);
    Assert.That(pcm.All(s => s == 0), Is.True);
  }

  [Test]
  public void ZeroFrame_Stereo_DecodesToSilence() {
    var codec = new Atrac1Codec(2);
    var pcm = codec.Decode(new byte[424]);
    Assert.That(pcm.All(s => s == 0), Is.True);
  }

  [Test]
  public void CraftedFrame_DecodesToBoundedFiniteSamples() {
    // A fixed byte pattern with a valid block-size-mode byte (low/mid even, high 0 or 3) that
    // exercises the spectrum/IMDCT/QMF cascade; the synthesis must stay bounded.
    var codec = new Atrac1Codec(1);
    var frame = new byte[212];
    frame[0] = 0x00; // valid BSM (low/mid = 0, high = 0)
    for (var i = 1; i < frame.Length; ++i)
      frame[i] = (byte)(i * 5 + 3);

    var pcm = codec.Decode(frame);
    Assert.That(pcm.Length, Is.EqualTo(512));
    Assert.That(pcm.All(s => s >= short.MinValue && s <= short.MaxValue), Is.True);
  }

  [Test]
  public void CraftedFrame_IsDeterministic() {
    var frame = new byte[212];
    frame[0] = 0x00;
    for (var i = 1; i < frame.Length; ++i)
      frame[i] = (byte)(i * 5 + 3);

    var a = new Atrac1Codec(1).Decode(frame);
    var b = new Atrac1Codec(1).Decode(frame);
    Assert.That(a, Is.EqualTo(b));
  }

  [Test]
  public void Construction_RejectsOutOfRangeChannelCount() {
    Assert.That(() => new Atrac1Codec(0), Throws.InstanceOf<ArgumentOutOfRangeException>());
    Assert.That(() => new Atrac1Codec(9), Throws.InstanceOf<ArgumentOutOfRangeException>());
  }

  // ── table fixed points ────────────────────────────────────────────────────────────

  [Test]
  public void BfuAmountTable_MatchesReference() {
    Assert.That(Atrac1Tables.BfuAmountTab1, Is.EqualTo(new[] { 20, 28, 32, 36, 40, 44, 48, 52 }));
  }

  [Test]
  public void BfuBands_PartitionTheFifty2Bfus() {
    Assert.That(Atrac1Tables.BfuBands, Is.EqualTo(new[] { 0, 20, 36, 52 }));
    // The per-BFU spectral-line counts must sum to the 512 MDCT coefficients of a sound unit.
    Assert.That(Atrac1Tables.SpecsPerBfu.Length, Is.EqualTo(52));
    Assert.That(Atrac1Tables.SpecsPerBfu.Sum(), Is.EqualTo(512));
  }

  [Test]
  public void ScaleFactorTable_DoublesEveryThreeSteps() {
    var sf = Atrac1Tables.SfTable;
    Assert.That(sf.Length, Is.EqualTo(64));
    Assert.That(sf[15], Is.EqualTo(1.0f).Within(1e-5));
    for (var i = 0; i + 3 < 64; ++i)
      Assert.That(sf[i + 3], Is.EqualTo(sf[i] * 2.0f).Within(sf[i] * 1e-4));
  }

  [Test]
  public void QmfWindow_IsSymmetricAndMatchesAtracFamily() {
    var w = Atrac1Tables.QmfWindow;
    Assert.That(w.Length, Is.EqualTo(48));
    for (var i = 0; i < 24; ++i)
      Assert.That(w[i], Is.EqualTo(w[47 - i]).Within(1e-9), "the 48-tap QMF window is symmetric");
    // The first tap is qmf_48tap_half[0] * 2 (shared ATRAC generator).
    Assert.That(w[0], Is.EqualTo(-0.00001461907f * 2.0f).Within(1e-9));
  }

  [Test]
  public void Sine32Window_MatchesSineGenerator() {
    var w = Atrac1Tables.Sine32;
    Assert.That(w.Length, Is.EqualTo(32));
    for (var i = 0; i < 32; ++i)
      Assert.That(w[i], Is.EqualTo((float)Math.Sin((i + 0.5) * (Math.PI / 64.0))).Within(1e-6));
  }
}
