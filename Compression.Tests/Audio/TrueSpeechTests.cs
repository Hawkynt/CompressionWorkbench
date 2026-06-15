#pragma warning disable CS1591
using Codec.TrueSpeech;

namespace Compression.Tests.Audio;

/// <summary>
/// Pins the DSP Group TrueSpeech decoder (<see cref="TrueSpeechCodec"/>). Cross-checking
/// against FFmpeg output is not available in this environment, so these tests pin
/// determinism + structure: frame→sample arithmetic (240 samples per 32-byte frame),
/// truncated-input tolerance, and an exact byte-pattern decode (hand-captured from this
/// implementation) that guards the read/correlate/pulse/synth pipeline against regressions.
/// </summary>
[TestFixture]
public class TrueSpeechTests {

  // ── length arithmetic ────────────────────────────────────────────────────────

  [Test]
  public void OneFrame_DecodesTo240Samples() {
    var pcm = TrueSpeechCodec.Decode(new byte[32]);
    Assert.That(pcm.Length, Is.EqualTo(240));
  }

  [Test]
  public void ThreeFrames_DecodeTo720Samples() {
    var pcm = TrueSpeechCodec.Decode(new byte[96]);
    Assert.That(pcm.Length, Is.EqualTo(720));
  }

  [Test]
  public void RaggedTail_IsTrimmedToFrameBoundary() {
    // 63 bytes = one full 32-byte frame + a 31-byte tail that is dropped.
    var pcm = TrueSpeechCodec.Decode(new byte[63]);
    Assert.That(pcm.Length, Is.EqualTo(240));
  }

  [Test]
  public void ShorterThanOneFrame_DecodesToEmpty() {
    Assert.That(TrueSpeechCodec.Decode(new byte[31]).Length, Is.EqualTo(0));
    Assert.That(TrueSpeechCodec.Decode([]).Length, Is.EqualTo(0));
  }

  // ── deterministic decode ─────────────────────────────────────────────────────

  [Test]
  public void ZeroFrame_DecodesToBoundedNearSilence() {
    // All-zero indices select the lowest codebook entries; the synthesis filter produces
    // a tiny bounded residual rather than runaway output.
    var pcm = TrueSpeechCodec.Decode(new byte[32]);
    Assert.That(pcm.Length, Is.EqualTo(240));
    Assert.That(pcm.Max(s => Math.Abs((int)s)), Is.EqualTo(6));
    Assert.That(pcm.Sum(s => (long)s), Is.EqualTo(31));
  }

  [Test]
  public void KnownBytePattern_DecodesToExactBoundedSamples() {
    // A fixed 32-byte pattern exercises the full read/correlate/pulse/synth pipeline.
    // The exact statistics are pinned to this faithful port of the reference decoder.
    var frame = new byte[32];
    for (var i = 0; i < frame.Length; ++i)
      frame[i] = (byte)(i * 7 + 1);

    var pcm = TrueSpeechCodec.Decode(frame);

    Assert.That(pcm.Length, Is.EqualTo(240));
    // Synthesis output is clamped well within the int16 range (reference clamps to ±0x7FFE).
    Assert.That(pcm.Max(s => Math.Abs((int)s)), Is.EqualTo(6419));
    Assert.That(pcm.Sum(s => (long)s), Is.EqualTo(328793));
    Assert.That(pcm[..6], Is.EqualTo(new short[] { 4, 0, 2, -1, -1, 0 }));
  }
}
