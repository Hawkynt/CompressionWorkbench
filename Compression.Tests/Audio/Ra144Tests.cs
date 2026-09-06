#pragma warning disable CS1591
using Codec.Ra144;

namespace Compression.Tests.Audio;

/// <summary>
/// Pins the RealAudio 1.0 "14_4"/lpcJ decoder (<see cref="Ra144Codec"/>): block→sample
/// arithmetic, truncated-input tolerance, the zero-block "near silence" property, and an
/// exact byte-pattern decode that guards against regressions in the synthesis path.
/// <para>The sample-exact cross-check against libavcodec's own decode of a real stream
/// lives in <see cref="ForeignAudioStreamTests"/>.</para>
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
    // The expectations below are libavcodec's decode of this very block, obtained by
    // splicing it into the first data packet of a RealMedia file.
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
    Assert.That(pcm.Sum(s => (long)s), Is.EqualTo(284));
  }
}
