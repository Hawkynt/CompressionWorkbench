#pragma warning disable CS1591
using Codec.Ym2608;

namespace Compression.Tests.Codecs.Ym2608;

[TestFixture]
public class Ym2608CodecTests {

  private const double Clock = 7987200.0;

  // Programs FM channel 4 (port 1, channel index 0) as an algorithm-7 single-operator voice with
  // the given L/R routing byte, then keys it on.
  private static Ym2608Codec BuildPort1Voice(int rlByte) {
    var ym = new Ym2608Codec(Clock);
    ym.Write(1, 0xB0, 0x07);        // alg 7
    ym.Write(1, 0xB4, rlByte);      // L/R + AMS/PMS
    int[] slotOff = [0, 4, 8, 12];
    for (var s = 0; s < 4; ++s) {
      var o = slotOff[s];
      ym.Write(1, 0x30 + o, 0x01);
      ym.Write(1, 0x40 + o, s == 0 ? 0x00 : 0x7F);
      ym.Write(1, 0x50 + o, 0x1F);
      ym.Write(1, 0x80 + o, 0x0F);
    }
    ym.Write(1, 0xA4, (4 << 3) | 0x04);
    ym.Write(1, 0xA0, 0x00);
    ym.Write(0, 0x28, 0xF4);        // key on channel 4 (chSel = 4)
    return ym;
  }

  private static (long Left, long Right) FmEnergy(Ym2608Codec ym, int frames) {
    long le = 0, re = 0;
    for (var i = 0; i < frames; ++i) {
      ym.RenderFmSample(out var l, out var r);
      le += Math.Abs(l);
      re += Math.Abs(r);
    }
    return (le, re);
  }

  // ──────────── six FM channels, stereo ────────────

  /// <summary>A port-1 FM channel renders non-silent stereo through the reused OPN2 core.</summary>
  [Test]
  public void Fm_Port1ChannelRendersStereo() {
    var ym = BuildPort1Voice(0xC0); // both L and R
    var (le, re) = FmEnergy(ym, 4096);
    Assert.That(le, Is.GreaterThan(0L));
    Assert.That(re, Is.GreaterThan(0L));
  }

  /// <summary>Per-channel L/R routing (reg 0xB4) silences the disabled speaker.</summary>
  [Test]
  public void Fm_LeftOnlyRoutingSilencesRight() {
    var ym = BuildPort1Voice(0x80); // left only
    var (le, re) = FmEnergy(ym, 4096);
    Assert.That(le, Is.GreaterThan(0L), "left carries the voice");
    Assert.That(re, Is.EqualTo(0L), "right is masked off");
  }

  // ──────────── SSG mix (reuses Ay8910) ────────────

  /// <summary>A tone on the SSG (port-0 registers 0x00-0x0F) renders non-silent audio.</summary>
  [Test]
  public void Ssg_ToneWriteProducesAudio() {
    var ym = new Ym2608Codec(Clock);
    ym.Write(0, 0x00, 0x00);
    ym.Write(0, 0x01, 0x01);
    ym.Write(0, 0x07, 0xFE);
    ym.Write(0, 0x08, 0x0F);

    var buf = new short[4410];
    ym.RenderSsgSamples(buf, 4410);
    long energy = 0;
    foreach (var s in buf)
      energy += Math.Abs(s);
    Assert.That(energy, Is.GreaterThan(0L), "SSG tone renders audio");
  }

  // ──────────── gated rhythm / ADPCM-B ────────────

  /// <summary>
  /// A rhythm key-on (port-0 reg 0x10 with a voice bit set and the dump bit clear) is recorded as
  /// a gated request — the rhythm ROM is not modelled — without disturbing FM/SSG rendering.
  /// </summary>
  [Test]
  public void Rhythm_KeyOnIsGatedAndFlagged() {
    var ym = new Ym2608Codec(Clock);
    Assert.That(ym.RhythmRequested, Is.False);
    ym.Write(0, 0x10, 0x01); // key-on bass drum
    Assert.That(ym.RhythmRequested, Is.True, "rhythm key-on is recorded as gated");
  }

  /// <summary>An ADPCM-B start (port-1 reg 0x00 bit 7) is recorded as a gated request.</summary>
  [Test]
  public void AdpcmB_StartIsGatedAndFlagged() {
    var ym = new Ym2608Codec(Clock);
    Assert.That(ym.AdpcmBRequested, Is.False);
    ym.Write(1, 0x00, 0x80); // start ADPCM-B
    Assert.That(ym.AdpcmBRequested, Is.True, "ADPCM-B start is recorded as gated");
  }
}
