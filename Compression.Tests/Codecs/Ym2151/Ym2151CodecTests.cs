#pragma warning disable CS1591
using Codec.Ym2151;

namespace Compression.Tests.Codecs.Ym2151;

[TestFixture]
public class Ym2151CodecTests {

  private const double Clock = 3579545.0;

  // Programs channel 0 as an algorithm-7 single-operator voice (only operator M1 is audible) at
  // the given key code, and keys it on.
  private static Ym2151Codec BuildSingleOperatorVoice(int keyCode, int keyFraction = 0, int channel = 0) {
    var ym = new Ym2151Codec(Clock);
    // RL on, FB 0, CONNECT 7 (all four operators are carriers).
    ym.WriteRegister(0x20 + channel, 0xC0 | 0x07);
    // Per-operator registers are at 0x40/0x60/0x80/0xA0/0xC0/0xE0 + (op<<3) + channel.
    for (var op = 0; op < 4; ++op) {
      var b = (op << 3) + channel;
      ym.WriteRegister(0x40 + b, 0x01);                   // DT1=0, MUL=1
      ym.WriteRegister(0x60 + b, op == 0 ? 0x00 : 0x7F);  // TL: only M1 audible
      ym.WriteRegister(0x80 + b, 0x1F);                   // KS=0, AR=31
      ym.WriteRegister(0xA0 + b, 0x00);                   // AMS-EN=0, D1R=0 (hold)
      ym.WriteRegister(0xC0 + b, 0x00);                   // DT2=0, D2R=0
      ym.WriteRegister(0xE0 + b, 0x0F);                   // D1L=0, RR=15
    }
    ym.WriteRegister(0x28 + channel, keyCode & 0x7F);     // KC
    ym.WriteRegister(0x30 + channel, (keyFraction & 0x3F) << 2); // KF in bits 2-7
    ym.WriteRegister(0x08, 0x78 | channel);               // key on all four slots of the channel
    return ym;
  }

  private static (long Left, long Right) Energy(Ym2151Codec ym, int frames) {
    long le = 0, re = 0;
    for (var i = 0; i < frames; ++i) {
      ym.RenderSample(out var l, out var r);
      le += Math.Abs(l);
      re += Math.Abs(r);
    }
    return (le, re);
  }

  private static double DominantFrequency(short[] signal, double sampleRate) {
    var minLag = (int)(sampleRate / 2000.0);
    var maxLag = (int)(sampleRate / 100.0);
    var bestLag = minLag;
    var best = double.MinValue;
    for (var lag = minLag; lag <= maxLag && lag < signal.Length; ++lag) {
      double sum = 0;
      for (var i = 0; i + lag < signal.Length; ++i)
        sum += signal[i] * (double)signal[i + lag];
      if (sum > best) {
        best = sum;
        bestLag = lag;
      }
    }
    return sampleRate / bestLag;
  }

  // ──────────── KC/KF → frequency (A440) ────────────

  /// <summary>
  /// The OPM key code 0x4C (octave 4, note A) with key fraction 0 must render the A440 reference
  /// pitch, pinning the key-code/key-fraction phase table.
  /// </summary>
  [Test]
  public void KeyCode_A4_RendersReferencePitch440() {
    var ym = BuildSingleOperatorVoice(keyCode: 0x4C); // octave 4, note index 12 → A
    var rate = ym.NativeSampleRate;
    var total = (int)(rate * 0.25);
    var mono = new short[total];
    for (var i = 0; i < total; ++i) {
      ym.RenderSample(out var l, out _);
      mono[i] = l;
    }
    var tail = mono.Skip(total / 2).ToArray();
    var measured = DominantFrequency(tail, rate);
    Assert.That(measured, Is.EqualTo(440.0).Within(8.0), $"measured {measured:F1} Hz");
  }

  /// <summary>A higher key code yields a higher rendered frequency (monotone pitch mapping).</summary>
  [Test]
  public void KeyCode_HigherCodeRaisesPitch() {
    double Pitch(int kc) {
      var ym = BuildSingleOperatorVoice(kc);
      var rate = ym.NativeSampleRate;
      var total = (int)(rate * 0.2);
      var mono = new short[total];
      for (var i = 0; i < total; ++i) {
        ym.RenderSample(out var l, out _);
        mono[i] = l;
      }
      return DominantFrequency(mono.Skip(total / 2).ToArray(), rate);
    }
    Assert.That(Pitch(0x5C), Is.GreaterThan(Pitch(0x4C)), "one octave up roughly doubles the pitch");
  }

  // ──────────── per-channel L/R panning ────────────

  /// <summary>With only the left enable set, the right speaker carries no energy.</summary>
  [Test]
  public void Panning_LeftOnlySilencesRight() {
    var ym = BuildSingleOperatorVoice(0x4C);
    ym.WriteRegister(0x20, 0x80 | 0x07); // RL = left only, CONNECT 7
    var (le, re) = Energy(ym, 4096);
    Assert.That(le, Is.GreaterThan(0L), "left speaker carries the voice");
    Assert.That(re, Is.EqualTo(0L), "right speaker is masked off");
  }

  // ──────────── noise on channel 8 ────────────

  /// <summary>
  /// Enabling the noise generator routes a non-tonal signal through operator 4 of channel 8;
  /// the channel must produce non-silent output even with a pure noise (no FM modulation).
  /// </summary>
  [Test]
  public void Noise_ReplacesChannel8Operator4() {
    var ym = new Ym2151Codec(Clock);
    const int channel = 7;
    ym.WriteRegister(0x20 + channel, 0xC0 | 0x07);
    for (var op = 0; op < 4; ++op) {
      var b = (op << 3) + channel;
      ym.WriteRegister(0x40 + b, 0x01);
      ym.WriteRegister(0x60 + b, op == 3 ? 0x00 : 0x7F); // only operator 4 (slot 3) audible
      ym.WriteRegister(0x80 + b, 0x1F);
      ym.WriteRegister(0xE0 + b, 0x0F);
    }
    ym.WriteRegister(0x28 + channel, 0x4C);
    ym.WriteRegister(0x0F, 0x80 | 0x10); // noise enable, frequency 16
    ym.WriteRegister(0x08, 0x78 | channel);

    var (le, _) = Energy(ym, 4096);
    Assert.That(le, Is.GreaterThan(0L), "channel-8 noise renders audio");
  }

  // ──────────── LFO PM affects output ────────────

  /// <summary>
  /// Engaging the LFO with a non-zero PM depth and PMS sensitivity perturbs the rendered tone, so
  /// the output differs from the same voice with the LFO disabled.
  /// </summary>
  [Test]
  public void Lfo_PhaseModulationAltersOutput() {
    short[] Render(bool lfo) {
      var ym = BuildSingleOperatorVoice(0x4C);
      if (lfo) {
        ym.WriteRegister(0x18, 0x80);        // LFRQ
        ym.WriteRegister(0x19, 0xFF);        // PMD (bit7 set → loads PM depth) = max
        ym.WriteRegister(0x1B, 0x02);        // triangle waveform
        ym.WriteRegister(0x38, 0x70);        // PMS = max, AMS = 0
      }
      var n = 8192;
      var mono = new short[n];
      for (var i = 0; i < n; ++i) {
        ym.RenderSample(out var l, out _);
        mono[i] = l;
      }
      return mono;
    }

    var plain = Render(false);
    var modulated = Render(true);
    var differences = plain.Zip(modulated, (a, b) => a != b ? 1 : 0).Sum();
    Assert.That(differences, Is.GreaterThan(0), "PM changes the waveform");
  }

  // ──────────── algorithm + feedback routing ────────────

  /// <summary>
  /// Algorithm 0 (a 4-operator chain) and algorithm 7 (four parallel carriers) route operators
  /// differently, so the same register programming renders distinct output under each.
  /// </summary>
  [Test]
  public void Algorithms_DifferentRoutingsProduceDifferentOutput() {
    short[] RenderAlg(int alg) {
      var ym = new Ym2151Codec(Clock);
      ym.WriteRegister(0x20, 0xC0 | alg);
      for (var op = 0; op < 4; ++op) {
        var b = op << 3;
        ym.WriteRegister(0x40 + b, 0x01);
        ym.WriteRegister(0x60 + b, 0x10);  // all operators audible-ish
        ym.WriteRegister(0x80 + b, 0x1F);
        ym.WriteRegister(0xE0 + b, 0x0F);
      }
      ym.WriteRegister(0x28, 0x4C);
      ym.WriteRegister(0x08, 0x78);
      var n = 2048;
      var mono = new short[n];
      for (var i = 0; i < n; ++i) {
        ym.RenderSample(out var l, out _);
        mono[i] = l;
      }
      return mono;
    }

    var alg0 = RenderAlg(0);
    var alg7 = RenderAlg(7);
    var differences = alg0.Zip(alg7, (a, b) => a != b ? 1 : 0).Sum();
    Assert.That(differences, Is.GreaterThan(0), "algorithm routing changes the sound");
  }

  /// <summary>A non-zero feedback on operator 1 alters its self-modulated output.</summary>
  [Test]
  public void Feedback_NonZeroChangesOutput() {
    short[] RenderFb(int fb) {
      var ym = new Ym2151Codec(Clock);
      ym.WriteRegister(0x20, 0xC0 | (fb << 3) | 0x07);
      for (var op = 0; op < 4; ++op) {
        var b = op << 3;
        ym.WriteRegister(0x40 + b, 0x01);
        ym.WriteRegister(0x60 + b, op == 0 ? 0x00 : 0x7F);
        ym.WriteRegister(0x80 + b, 0x1F);
        ym.WriteRegister(0xE0 + b, 0x0F);
      }
      ym.WriteRegister(0x28, 0x4C);
      ym.WriteRegister(0x08, 0x78);
      var n = 2048;
      var mono = new short[n];
      for (var i = 0; i < n; ++i) {
        ym.RenderSample(out var l, out _);
        mono[i] = l;
      }
      return mono;
    }

    var noFb = RenderFb(0);
    var withFb = RenderFb(7);
    var differences = noFb.Zip(withFb, (a, b) => a != b ? 1 : 0).Sum();
    Assert.That(differences, Is.GreaterThan(0), "feedback self-modulates operator 1");
  }
}
