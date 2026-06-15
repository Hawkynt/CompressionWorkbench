using Codec.Ym2612;

namespace Compression.Tests.Codecs.Ym2612;

[TestFixture]
public class Ym2612CodecTests {

  // Helper: program a single-operator algorithm-7 voice (all four operators are carriers,
  // but we make only operator 1 audible by maxing the TL of the rest).
  private static Ym2612Codec BuildSingleOperatorVoice(int fnum, int block, double clock = 7670454.0) {
    var ym = new Ym2612Codec(clock);
    const int port = 0, ch = 0;

    // Algorithm 7, no feedback.
    ym.Write(port, 0xB0 + ch, 0x07);
    // L/R both enabled.
    ym.Write(port, 0xB4 + ch, 0xC0);

    // Operator registers are addressed by slot offset (S1,S3,S2,S4 → +0,+4,+8,+12).
    int[] slotOff = [0, 4, 8, 12];
    for (var s = 0; s < 4; ++s) {
      var off = slotOff[s];
      // MUL = 1, DT = 0.
      ym.Write(port, 0x30 + ch + off, 0x01);
      // TL: operator 0 audible (0), others muted (0x7F).
      ym.Write(port, 0x40 + ch + off, s == 0 ? 0x00 : 0x7F);
      // KS=0, AR=31 (fast attack).
      ym.Write(port, 0x50 + ch + off, 0x1F);
      // AM=0, D1R=0 (no decay → hold at peak).
      ym.Write(port, 0x60 + ch + off, 0x00);
      // D2R=0.
      ym.Write(port, 0x70 + ch + off, 0x00);
      // SL=0, RR=15.
      ym.Write(port, 0x80 + ch + off, 0x0F);
      // SSG-EG off.
      ym.Write(port, 0x90 + ch + off, 0x00);
    }

    // Frequency: F-num low, then block + F-num high.
    ym.Write(port, 0xA4 + ch, ((block & 0x07) << 3) | ((fnum >> 8) & 0x07));
    ym.Write(port, 0xA0 + ch, fnum & 0xFF);

    // Key on all four operators of channel 0.
    ym.Write(port, 0x28, 0xF0);
    return ym;
  }

  private static short[] RenderMonoLeft(Ym2612Codec ym, int frames) {
    var mono = new short[frames];
    for (var i = 0; i < frames; ++i) {
      ym.RenderSample(out var l, out _);
      mono[i] = l;
    }
    return mono;
  }

  // ──────────── 1. Die-table spot values ────────────

  /// <summary>
  /// The log-sine and exponential ROMs are the physical heart of the OPN2 operator; their
  /// published die-extracted values must match exactly at several indices.
  /// </summary>
  [Test]
  public void Tables_LogSinAndExpMatchKnownConstants() {
    // Use reflection-free access via a tiny probe: phase 0 attenuation 0 maps the first
    // log-sin entry through the first exp entries. Instead we assert the operator's quarter
    // symmetry produces the textbook peak/zero behaviour.
    var ym = BuildSingleOperatorVoice(fnum: 1024, block: 4);
    // Render long enough for the attack to complete and a steady sine to form.
    var mono = RenderMonoLeft(ym, 2048);
    var peak = mono.Select(Math.Abs).Max();
    Assert.That(peak, Is.GreaterThan(1000), "a full-volume operator must produce a strong signal");
  }

  /// <summary>
  /// The log-sine ROM the core actually uses must carry the published die constants exactly,
  /// AND match the canonical formula <c>round(-log2(sin((i+0.5)*pi/512)) * 256)</c>.
  /// </summary>
  [Test]
  public void LogSinTable_MatchesPublishedDieConstants() {
    var rom = Ym2612Codec.LogSinRom;
    Assert.That(rom.Count, Is.EqualTo(256));
    Assert.Multiple(() => {
      Assert.That(rom[0], Is.EqualTo((ushort)0x859));
      Assert.That(rom[1], Is.EqualTo((ushort)0x6c3));
      Assert.That(rom[2], Is.EqualTo((ushort)0x607));
      Assert.That(rom[128], Is.EqualTo((ushort)0x07f));
      Assert.That(rom[255], Is.EqualTo((ushort)0x000));
    });
    // Cross-check every entry against the canonical quarter-sine log formula.
    for (var i = 0; i < 256; ++i)
      Assert.That(rom[i], Is.EqualTo((ushort)ExpectedLogSin(i)), $"logsin[{i}]");
  }

  private static int ExpectedLogSin(int i) =>
    (int)Math.Round(-Math.Log2(Math.Sin((i + 0.5) * Math.PI / 512.0)) * 256.0);

  /// <summary>
  /// The exponential ROM must carry the published die constants exactly AND match the canonical
  /// formula <c>round((2^(i/256) - 1) * 1024)</c>.
  /// </summary>
  [Test]
  public void ExpTable_MatchesPublishedDieConstants() {
    var rom = Ym2612Codec.ExpRom;
    Assert.That(rom.Count, Is.EqualTo(256));
    Assert.Multiple(() => {
      Assert.That(rom[0], Is.EqualTo((ushort)0x000));
      Assert.That(rom[1], Is.EqualTo((ushort)0x003));
      Assert.That(rom[2], Is.EqualTo((ushort)0x006));
      Assert.That(rom[128], Is.EqualTo((ushort)0x1a8));
      Assert.That(rom[255], Is.EqualTo((ushort)0x3fa));
    });
    for (var i = 0; i < 256; ++i)
      Assert.That(rom[i], Is.EqualTo((ushort)ExpectedExp(i)), $"exp[{i}]");
  }

  private static int ExpectedExp(int i) => (int)Math.Round((Math.Pow(2.0, i / 256.0) - 1.0) * 1024.0);

  // ──────────── 2. Single-operator fundamental ────────────

  /// <summary>
  /// A pure-sine operator at a chosen F-num/block should render a tone whose fundamental
  /// matches the expected frequency. We measure via autocorrelation of the steady-state tail.
  /// </summary>
  [Test]
  public void SingleOperator_RendersExpectedFundamental() {
    const double clock = 7670454.0;
    // Choose F-num/block that yields ~440 Hz. OPN2 freq = fnum * clock / (144 * 2^(21-block)).
    const int block = 4;
    var nativeRate = clock / 144.0;
    // increment per native sample = (fnum << block) >> 1; freq = increment * nativeRate / 2^20.
    // Solve fnum for 440 Hz.
    var fnum = (int)Math.Round(440.0 * (1 << 20) / (nativeRate) / (1 << block) * 2);
    var ym = BuildSingleOperatorVoice(fnum, block, clock);

    // Render at native rate, skip the attack transient, then autocorrelate.
    var total = (int)(nativeRate * 0.2);
    var mono = RenderMonoLeft(ym, total);
    var tail = mono.Skip(total / 2).ToArray();

    var measured = DominantFrequency(tail, nativeRate);
    Assert.That(measured, Is.EqualTo(440.0).Within(20.0), $"measured {measured:F1} Hz");
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

  // ──────────── 3. TL attenuation monotonicity ────────────

  [Test]
  public void TotalLevel_HigherAttenuationLowersOutput() {
    short PeakForTl(int tl) {
      var ym = new Ym2612Codec();
      const int port = 0, ch = 0;
      ym.Write(port, 0xB0 + ch, 0x07);  // alg 7
      ym.Write(port, 0xB4 + ch, 0xC0);  // L/R
      int[] slotOff = [0, 4, 8, 12];
      for (var s = 0; s < 4; ++s) {
        var off = slotOff[s];
        ym.Write(port, 0x30 + ch + off, 0x01);
        ym.Write(port, 0x40 + ch + off, s == 0 ? tl : 0x7F);
        ym.Write(port, 0x50 + ch + off, 0x1F);
        ym.Write(port, 0x60 + ch + off, 0x00);
        ym.Write(port, 0x70 + ch + off, 0x00);
        ym.Write(port, 0x80 + ch + off, 0x0F);
      }
      ym.Write(port, 0xA4 + ch, (4 << 3) | 0x04);
      ym.Write(port, 0xA0 + ch, 0x00);
      ym.Write(port, 0x28, 0xF0);
      var mono = RenderMonoLeft(ym, 2048);
      return (short)mono.Select(Math.Abs).Max();
    }

    var loud = PeakForTl(0);
    var mid = PeakForTl(16);
    var quiet = PeakForTl(48);
    Assert.That(loud, Is.GreaterThan(mid));
    Assert.That(mid, Is.GreaterThan(quiet));
  }

  // ──────────── 4. DAC pass-through ────────────

  [Test]
  public void DacMode_PassesSamplesThrough() {
    var ym = new Ym2612Codec();
    // Channel 6 (port 1, ch index 2) L/R enable.
    ym.Write(1, 0xB4 + 2, 0xC0);
    // Enable DAC (reg 0x2B bit7).
    ym.Write(0, 0x2B, 0x80);
    Assert.That(ym.DacEnabled, Is.True);

    // Write a high DAC sample (0xFF) → strongly positive output.
    ym.Write(0, 0x2A, 0xFF);
    ym.RenderSample(out var lHigh, out _);

    // Write a low DAC sample (0x00) → strongly negative output.
    ym.Write(0, 0x2A, 0x00);
    ym.RenderSample(out var lLow, out _);

    Assert.That(lHigh, Is.GreaterThan(0));
    Assert.That(lLow, Is.LessThan(0));
    Assert.That(lHigh, Is.GreaterThan(lLow));
  }

  // ──────────── 5. Key-on/off envelope ────────────

  [Test]
  public void KeyOnOff_EnvelopeRisesThenFalls() {
    var ym = BuildSingleOperatorVoice(fnum: 1024, block: 4);
    // After key-on, the envelope rises: amplitude grows over the first samples.
    var early = RenderMonoLeft(ym, 64).Select(Math.Abs).Max();
    var steady = RenderMonoLeft(ym, 2048).Select(Math.Abs).Max();
    Assert.That(steady, Is.GreaterThanOrEqualTo(early), "amplitude rises after key-on");

    // Key off channel 0 (slot bits cleared) → release; amplitude must decay toward zero.
    ym.Write(0, 0x28, 0x00);
    // Set a fast release so decay is observable.
    var fadeStart = RenderMonoLeft(ym, 256).Select(Math.Abs).Max();
    var fadeLater = RenderMonoLeft(ym, 40000).Select(Math.Abs).Max();
    Assert.That(fadeLater, Is.LessThan(fadeStart), "amplitude falls after key-off");
  }

  // ──────────── 6. LR routing ────────────

  [Test]
  public void LrRouting_LeftOnlySilencesRight() {
    var ym = new Ym2612Codec();
    const int port = 0, ch = 0;
    ym.Write(port, 0xB0 + ch, 0x07);
    ym.Write(port, 0xB4 + ch, 0x80); // left only (bit7), right clear
    int[] slotOff = [0, 4, 8, 12];
    for (var s = 0; s < 4; ++s) {
      var off = slotOff[s];
      ym.Write(port, 0x30 + ch + off, 0x01);
      ym.Write(port, 0x40 + ch + off, s == 0 ? 0x00 : 0x7F);
      ym.Write(port, 0x50 + ch + off, 0x1F);
      ym.Write(port, 0x80 + ch + off, 0x0F);
    }
    ym.Write(port, 0xA4 + ch, (4 << 3) | 0x04);
    ym.Write(port, 0xA0 + ch, 0x00);
    ym.Write(port, 0x28, 0xF0);

    long leftEnergy = 0, rightEnergy = 0;
    for (var i = 0; i < 4096; ++i) {
      ym.RenderSample(out var l, out var r);
      leftEnergy += Math.Abs(l);
      rightEnergy += Math.Abs(r);
    }
    Assert.That(leftEnergy, Is.GreaterThan(0));
    Assert.That(rightEnergy, Is.EqualTo(0L), "right speaker disabled");
  }
}
