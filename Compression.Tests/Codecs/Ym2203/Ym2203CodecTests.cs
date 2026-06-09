#pragma warning disable CS1591
using Codec.Ym2203;

namespace Compression.Tests.Codecs.Ym2203;

[TestFixture]
public class Ym2203CodecTests {

  private const double Clock = 3993600.0;

  // Programs FM channel `ch` (0..2) as an algorithm-7 single-operator voice and keys it on.
  private static void ProgramFmVoice(Ym2203Codec ym, int ch) {
    ym.Write(0xB0 + ch, 0x07);                       // alg 7
    int[] slotOff = [0, 4, 8, 12];
    for (var s = 0; s < 4; ++s) {
      var o = slotOff[s];
      ym.Write(0x30 + ch + o, 0x01);                 // DT=0, MUL=1
      ym.Write(0x40 + ch + o, s == 0 ? 0x00 : 0x7F); // TL: only op 0 audible
      ym.Write(0x50 + ch + o, 0x1F);                 // KS=0, AR=31
      ym.Write(0x80 + ch + o, 0x0F);                 // SL=0, RR=15
    }
    ym.Write(0xA4 + ch, (4 << 3) | 0x04);            // block 4, F-num high
    ym.Write(0xA0 + ch, 0x00);                       // F-num low
    var keyMask = ch switch { 0 => 0x00, 1 => 0x01, _ => 0x02 };
    ym.Write(0x28, 0xF0 | keyMask);                  // key on all four operators of the channel
  }

  // ──────────── FM (reuses the OPN2/Ym2612 core) ────────────

  /// <summary>The three FM channels render non-silent audio through the reused OPN core.</summary>
  [Test]
  public void Fm_RendersNonSilentMono() {
    var ym = new Ym2203Codec(Clock);
    ProgramFmVoice(ym, 0);
    long energy = 0;
    for (var i = 0; i < 4096; ++i)
      energy += Math.Abs(ym.RenderFmSample());
    Assert.That(energy, Is.GreaterThan(0L), "FM channel produces audio");
  }

  // ──────────── SSG (reuses the Ay8910 core) ────────────

  /// <summary>
  /// A tone written through the AY-compatible SSG registers (0x00-0x0F) produces non-silent audio,
  /// confirming the SSG section is driven by the reused <c>Ay8910Chip</c>.
  /// </summary>
  [Test]
  public void Ssg_ToneWriteProducesAudio() {
    var ym = new Ym2203Codec(Clock);
    ym.Write(0x00, 0x00); // tone A fine
    ym.Write(0x01, 0x01); // tone A coarse → period 0x100
    ym.Write(0x07, 0xFE); // mixer: enable tone A (active low)
    ym.Write(0x08, 0x0F); // channel A full volume

    var buf = new short[4410];
    ym.RenderSsgSamples(buf, 4410);
    long energy = 0;
    foreach (var s in buf)
      energy += Math.Abs(s);
    Assert.That(energy, Is.GreaterThan(0L), "SSG tone renders audio");
  }

  /// <summary>With every SSG amplitude at zero the SSG section is silent.</summary>
  [Test]
  public void Ssg_ZeroVolumeIsSilent() {
    var ym = new Ym2203Codec(Clock);
    ym.Write(0x00, 0x00);
    ym.Write(0x01, 0x01);
    ym.Write(0x07, 0xFE);
    ym.Write(0x08, 0x00); // volume 0

    var buf = new short[2048];
    ym.RenderSsgSamples(buf, 2048);
    var peak = 0;
    foreach (var s in buf)
      peak = Math.Max(peak, Math.Abs(s));
    Assert.That(peak, Is.EqualTo(0), "amplitude 0 is silent");
  }

  // ──────────── channel-3 special mode ────────────

  /// <summary>
  /// Engaging channel-3 special mode (reg 0x27 bit 6) and writing the per-operator F-num registers
  /// (0xA8-0xAF) changes channel 3's rendered output relative to the normal single-F-num mode.
  /// </summary>
  [Test]
  public void Channel3SpecialMode_PerOperatorFrequencyChangesOutput() {
    short[] Render(bool special) {
      var ym = new Ym2203Codec(Clock);
      // Use a 4-operator chain so each operator's frequency matters.
      ym.Write(0xB0 + 2, 0x00); // channel 3, algorithm 0
      int[] slotOff = [0, 4, 8, 12];
      for (var s = 0; s < 4; ++s) {
        var o = slotOff[s];
        ym.Write(0x30 + 2 + o, 0x01);
        ym.Write(0x40 + 2 + o, 0x10);
        ym.Write(0x50 + 2 + o, 0x1F);
        ym.Write(0x80 + 2 + o, 0x0F);
      }
      ym.Write(0xA4 + 2, (4 << 3) | 0x04);
      ym.Write(0xA0 + 2, 0x00);
      if (special) {
        ym.Write(0x27, 0x40); // channel-3 special mode
        // Per-operator F-num/block for operators 0..2 (registers 0xA8-0xAA / 0xAC-0xAE).
        for (var op = 0; op < 3; ++op) {
          ym.Write(0xAC + op, (5 << 3) | 0x05);
          ym.Write(0xA8 + op, 0x40);
        }
      }
      ym.Write(0x28, 0xF2); // key on channel 3
      var mono = new short[4096];
      for (var i = 0; i < mono.Length; ++i)
        mono[i] = ym.RenderFmSample();
      return mono;
    }

    var normal = Render(false);
    var special = Render(true);
    var differences = normal.Zip(special, (a, b) => a != b ? 1 : 0).Sum();
    Assert.That(differences, Is.GreaterThan(0), "per-operator frequency changes the timbre");
  }
}
