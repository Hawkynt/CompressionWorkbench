#pragma warning disable CS1591
using Codec.Ra288;

namespace Compression.Tests.Audio;

/// <summary>
/// Pins the RealAudio 2.0 28.8 ("28_8") decoder (<see cref="Ra288Codec"/>), a faithful decode-only
/// port of FFmpeg's <c>libavcodec/ra288.c</c> + the G.728 hybrid-window helper. Cross-checking
/// against FFmpeg output is not available in this environment, so these tests pin determinism +
/// structure: the 38-byte-frame → 160-sample arithmetic, truncation tolerance, codebook spot
/// values, the backward-adaptation determinism walk, and bounded-amplitude decode of crafted data.
/// </summary>
[TestFixture]
public class Ra288Tests {

  // ── frame arithmetic ──────────────────────────────────────────────────────────────

  [Test]
  public void OneFrame_DecodesTo160Samples() {
    Assert.That(Ra288Codec.CodedFrameSize, Is.EqualTo(38));
    var pcm = new Ra288Codec().Decode(new byte[38]);
    Assert.That(pcm.Length, Is.EqualTo(160), "32 sub-blocks × 5 samples");
  }

  [Test]
  public void ThreeFrames_DecodeTo480Samples() {
    var pcm = new Ra288Codec().Decode(new byte[38 * 3]);
    Assert.That(pcm.Length, Is.EqualTo(480));
  }

  [Test]
  public void RaggedTail_IsTrimmedToFrameBoundary() {
    // 38 + 20 bytes: one full frame plus a dropped tail.
    var pcm = new Ra288Codec().Decode(new byte[38 + 20]);
    Assert.That(pcm.Length, Is.EqualTo(160));
  }

  [Test]
  public void ShorterThanOneFrame_DecodesToEmpty() {
    Assert.That(new Ra288Codec().Decode(new byte[37]).Length, Is.EqualTo(0));
    Assert.That(new Ra288Codec().Decode([]).Length, Is.EqualTo(0));
  }

  // ── deterministic decode ────────────────────────────────────────────────────────

  [Test]
  public void ZeroFrame_DecodesToBoundedSamples() {
    // Gain index 0 (amplitude 0.515625) + codebook index 0 give a small excitation that the
    // backward-adapted log-gain shapes over the frame; the output must stay inside the 16-bit
    // range and be deterministic.
    var pcm = new Ra288Codec().Decode(new byte[38]);
    Assert.That(pcm.Length, Is.EqualTo(160));
    Assert.That(pcm.All(s => s >= short.MinValue && s <= short.MaxValue), Is.True);
    var again = new Ra288Codec().Decode(new byte[38]);
    Assert.That(pcm, Is.EqualTo(again));
  }

  [Test]
  public void CraftedFrame_DecodesToBoundedFiniteSamples() {
    var frame = new byte[38];
    for (var i = 0; i < frame.Length; ++i)
      frame[i] = (byte)(i * 11 + 7);

    var pcm = new Ra288Codec().Decode(frame);
    Assert.That(pcm.Length, Is.EqualTo(160));
    Assert.That(pcm.All(s => s >= short.MinValue && s <= short.MaxValue), Is.True);
  }

  [Test]
  public void Decode_IsDeterministic() {
    var frame = new byte[38];
    for (var i = 0; i < frame.Length; ++i)
      frame[i] = (byte)(i * 11 + 7);

    var a = new Ra288Codec().Decode(frame);
    var b = new Ra288Codec().Decode(frame);
    Assert.That(a, Is.EqualTo(b));
  }

  [Test]
  public void BackwardAdaptation_IsCarriedAcrossFrames() {
    // Decoding two frames in one call must equal decoding them with two sequential calls on the
    // same instance (the backward filter / history is stateful within an instance).
    var frame = new byte[38];
    for (var i = 0; i < frame.Length; ++i)
      frame[i] = (byte)(i * 13 + 1);
    var two = new byte[76];
    frame.CopyTo(two, 0);
    frame.CopyTo(two, 38);

    var combined = new Ra288Codec().Decode(two);

    var codec = new Ra288Codec();
    var first = codec.Decode(frame);
    var second = codec.Decode(frame);
    var sequential = first.Concat(second).ToArray();

    Assert.That(combined, Is.EqualTo(sequential));
    // The second frame must differ from the first because the backward LPC adapted in between.
    Assert.That(second, Is.Not.EqualTo(first));
  }

  // ── table spot checks ──────────────────────────────────────────────────────────────

  [Test]
  public void AmpTable_MatchesReference() {
    Assert.That(Ra288Tables.AmpTable.Length, Is.EqualTo(8));
    Assert.That(Ra288Tables.AmpTable[0], Is.EqualTo(0.515625f));
    Assert.That(Ra288Tables.AmpTable[3], Is.EqualTo(2.76342773f));
    Assert.That(Ra288Tables.AmpTable[4], Is.EqualTo(-0.515625f));
  }

  [Test]
  public void CodeTable_HasExpectedShapeAndSpotValues() {
    Assert.That(Ra288Tables.CodeTable.Length, Is.EqualTo(128));
    Assert.That(Ra288Tables.CodeTable[0], Is.EqualTo(new[] { 668, -2950, -1254, -1790, -2553 }));
    Assert.That(Ra288Tables.CodeTable[1], Is.EqualTo(new[] { -5032, -4577, -1045, 2908, 3318 }));
    Assert.That(Ra288Tables.CodeTable[127], Is.EqualTo(new[] { 606, 2018, -1316, 4064, 398 }));
  }

  [Test]
  public void Windows_HaveReferenceLengthsAndPeaks() {
    Assert.That(Ra288Tables.SynWindow.Length, Is.EqualTo(111));
    Assert.That(Ra288Tables.GainWindow.Length, Is.EqualTo(38));
    Assert.That(Ra288Tables.SynBwTab.Length, Is.EqualTo(36));
    Assert.That(Ra288Tables.GainBwTab.Length, Is.EqualTo(10));
    // The gain window peaks at 1.0 at index 21.
    Assert.That(Ra288Tables.GainWindow[21], Is.EqualTo(1.0f));
  }
}
