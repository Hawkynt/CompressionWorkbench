#pragma warning disable CS1591
using Codec.Ra144;

namespace Compression.Tests.Audio;

/// <summary>
/// Pins the RealAudio 1.0 "14_4"/lpcJ decoder (<see cref="Ra144Codec"/>). Cross-checking
/// against FFmpeg output is not available in this environment, so these tests pin
/// determinism + structure: block→sample arithmetic, truncated-input tolerance, the
/// zero-block "near silence" property, and an exact byte-pattern decode (hand-captured
/// from this implementation) that guards against regressions in the synthesis path.
/// </summary>
[TestFixture]
public class Ra144Tests {

  // ── length arithmetic ────────────────────────────────────────────────────────

  [Test]
  public void OneBlock_DecodesTo160Samples() {
    var pcm = Ra144Codec.Decode(new byte[20]);
    Assert.That(pcm.Length, Is.EqualTo(160));
  }

  [Test]
  public void ThreeBlocks_DecodeTo480Samples() {
    var pcm = Ra144Codec.Decode(new byte[60]);
    Assert.That(pcm.Length, Is.EqualTo(480));
  }

  [Test]
  public void RaggedTail_IsTrimmedToBlockBoundary() {
    // 39 bytes = one full 20-byte block + a 19-byte tail that is dropped.
    var pcm = Ra144Codec.Decode(new byte[39]);
    Assert.That(pcm.Length, Is.EqualTo(160));
  }

  [Test]
  public void ShorterThanOneBlock_DecodesToEmpty() {
    Assert.That(Ra144Codec.Decode(new byte[19]).Length, Is.EqualTo(0));
    Assert.That(Ra144Codec.Decode([]).Length, Is.EqualTo(0));
  }

  // ── deterministic decode ─────────────────────────────────────────────────────

  [Test]
  public void ZeroBlock_DecodesToSilence() {
    // All-zero indices select the zero energy/gain path → no excitation → silence.
    var pcm = Ra144Codec.Decode(new byte[20]);
    Assert.That(pcm.All(s => s == 0), Is.True);
  }

  [Test]
  public void ZeroBlocksAreStateless_TwoZeroBlocksAreStillSilent() {
    var pcm = Ra144Codec.Decode(new byte[40]);
    Assert.That(pcm.Length, Is.EqualTo(320));
    Assert.That(pcm.All(s => s == 0), Is.True);
  }

  [Test]
  public void KnownBytePattern_DecodesToExactBoundedSamples() {
    // A fixed 20-byte pattern exercises the LPC/codebook/gain path with bounded energy.
    // The exact sample values are pinned to this faithful port of the reference decoder.
    var block = new byte[20];
    for (var i = 0; i < block.Length; ++i)
      block[i] = (byte)(i * 13);

    var pcm = Ra144Codec.Decode(block);

    Assert.That(pcm.Length, Is.EqualTo(160));
    // Bounded amplitude (no runaway in the synthesis filter).
    Assert.That(pcm.Max(s => Math.Abs((int)s)), Is.EqualTo(20));
    // Peak position and signal energy pin the synthesis path deterministically.
    var peakIndex = Array.FindIndex(pcm, s => Math.Abs((int)s) == 20);
    Assert.That(peakIndex, Is.EqualTo(93));
    Assert.That(pcm.Sum(s => (long)s), Is.EqualTo(360));
  }
}
