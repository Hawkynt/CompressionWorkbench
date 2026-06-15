using Codec.Opl;

namespace Compression.Tests.Codecs.Opl;

[TestFixture]
public class OplCodecTests {

  private const double Clock = 3579545.0;
  private const double Opl3Clock = 14318180.0;

  private static (short[] left, short[] right) RenderStereo(OplCodec chip, int frames) {
    var l = new short[frames];
    var r = new short[frames];
    for (var i = 0; i < frames; ++i)
      chip.RenderSample(out l[i], out r[i]);
    return (l, r);
  }

  private static short[] RenderMono(OplCodec chip, int frames) {
    var mono = new short[frames];
    for (var i = 0; i < frames; ++i)
      mono[i] = chip.RenderSample();
    return mono;
  }

  // Programs OPL channel 0 with a simple loud sustained 2-op voice (FM) and keys it on.
  private static OplCodec BuildVoice(OplCodec.Chip variant, int fnum, int block, double clock) {
    var chip = new OplCodec(variant, clock);
    // Modulator (op0 @ 0x00) and carrier (op0+3 @ 0x03).
    chip.WriteRegister(0x20, 0x21);   // mod: EG sustain, MUL=1
    chip.WriteRegister(0x23, 0x21);   // car: EG sustain, MUL=1
    chip.WriteRegister(0x40, 0x3F);   // mod KSL=0, TL=63 (silence modulator → pure carrier)
    chip.WriteRegister(0x43, 0x00);   // car KSL=0, TL=0 (loud)
    chip.WriteRegister(0x60, 0xF0);   // mod AR=15, DR=0
    chip.WriteRegister(0x63, 0xF0);   // car AR=15, DR=0
    chip.WriteRegister(0x80, 0x0F);   // mod SL=0, RR=15
    chip.WriteRegister(0x83, 0x0F);   // car SL=0, RR=15
    chip.WriteRegister(0xC0, 0x00);   // FB=0, FM connection
    chip.WriteRegister(0xA0, fnum & 0xFF);
    chip.WriteRegister(0xB0, ((fnum >> 8) & 0x03) | (block << 2) | 0x20); // key-on
    return chip;
  }

  // ──────────── 1. Shared operator ROMs ────────────

  [Test]
  public void OperatorRoms_MatchSharedDieConstants() {
    var logsin = OplCodec.LogSinRom;
    var exp = OplCodec.ExpRom;
    Assert.That(logsin.Count, Is.EqualTo(256));
    Assert.That(exp.Count, Is.EqualTo(256));

    Assert.Multiple(() => {
      Assert.That(logsin[0], Is.EqualTo((ushort)0x859));
      Assert.That(logsin[128], Is.EqualTo((ushort)0x07f));
      Assert.That(logsin[255], Is.EqualTo((ushort)0x000));
      Assert.That(exp[0], Is.EqualTo((ushort)0x000));
      Assert.That(exp[128], Is.EqualTo((ushort)0x1a8));
      Assert.That(exp[255], Is.EqualTo((ushort)0x3fa));
    });

    for (var i = 0; i < 256; ++i) {
      Assert.That(logsin[i],
        Is.EqualTo((ushort)Math.Round(-Math.Log2(Math.Sin((i + 0.5) * Math.PI / 512.0)) * 256.0)),
        $"logsin[{i}]");
      Assert.That(exp[i],
        Is.EqualTo((ushort)Math.Round((Math.Pow(2.0, i / 256.0) - 1.0) * 1024.0)),
        $"exp[{i}]");
    }
  }

  // ──────────── 2. F-num → frequency ────────────

  /// <summary>
  /// The OPL phase generator runs at clock/72; a steady carrier tracks
  /// <c>freq = (fnum &lt;&lt; block) &gt;&gt; 1 * nativeRate / 2^19</c>. Pin a configuration near A440.
  /// </summary>
  [Test]
  public void Frequency_TracksFNumBlockFormula() {
    const int block = 4;
    var nativeRate = Clock / OplCodec.Prescale;
    var fnum = (int)Math.Round(440.0 * (1 << 19) / nativeRate / (1 << block) * 2);

    var chip = BuildVoice(OplCodec.Chip.Opl2, fnum, block, Clock);
    var total = (int)(nativeRate * 0.25);
    var mono = RenderMono(chip, total);
    var tail = mono.Skip(total / 2).ToArray();
    var measured = DominantFrequency(tail, nativeRate);
    Assert.That(measured, Is.EqualTo(440.0).Within(30.0), $"measured {measured:F1} Hz");
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
      if (sum > best) { best = sum; bestLag = lag; }
    }
    return sampleRate / bestLag;
  }

  // ──────────── 3. OPL2 waveform table correctness ────────────

  /// <summary>
  /// The OPL2 four waveforms: half-sine zeroes the negative half, abs-sine mirrors it positive,
  /// quarter-sine keeps only the rising quarters. We probe the raw operator at fixed phases.
  /// </summary>
  [Test]
  public void Opl2Waveforms_HalfAbsQuarterSineShapes() {
    // A single loud carrier with the modulator silenced; sweep one period and inspect the shape.
    short[] RenderWave(int wave) {
      var chip = new OplCodec(OplCodec.Chip.Opl2, Clock);
      chip.WriteRegister(0x01, 0x20);   // enable waveform select (OPL2)
      chip.WriteRegister(0x20, 0x21);
      chip.WriteRegister(0x23, 0x21);
      chip.WriteRegister(0x40, 0x3F);   // silence modulator
      chip.WriteRegister(0x43, 0x00);
      chip.WriteRegister(0x60, 0xF0);
      chip.WriteRegister(0x63, 0xF0);
      chip.WriteRegister(0x80, 0x00);
      chip.WriteRegister(0x83, 0x00);
      chip.WriteRegister(0xE3, wave);   // carrier waveform
      chip.WriteRegister(0xA0, 0x40);
      chip.WriteRegister(0xB0, 0x10 | 0x20); // mid block, key-on
      // Let the attack settle, then capture a chunk.
      RenderMono(chip, 256);
      return RenderMono(chip, 4096);
    }

    var full = RenderWave(0);
    var half = RenderWave(1);
    var abs = RenderWave(2);
    var quarter = RenderWave(3);

    Assert.Multiple(() => {
      // Full sine swings both ways.
      Assert.That(full.Min(), Is.LessThan(0), "full sine has a negative half");
      Assert.That(full.Max(), Is.GreaterThan(0));
      // Half-sine: never negative.
      Assert.That(half.Min(), Is.GreaterThanOrEqualTo((short)0), "half-sine mutes negatives");
      Assert.That(half.Max(), Is.GreaterThan(0));
      // Abs-sine: never negative either, but more energy than half-sine (both halves present).
      Assert.That(abs.Min(), Is.GreaterThanOrEqualTo((short)0), "abs-sine is non-negative");
      Assert.That(abs.Sum(s => (long)s), Is.GreaterThan(half.Sum(s => (long)s)),
        "abs-sine carries more energy than half-sine");
      // Quarter-sine: non-negative and present.
      Assert.That(quarter.Min(), Is.GreaterThanOrEqualTo((short)0));
      Assert.That(quarter.Max(), Is.GreaterThan(0));
    });
  }

  [Test]
  public void Opl1_RejectsWaveformSelect_OnlySine() {
    // The original OPL (YM3526) has no waveform-select; writing 0xE0 must not produce a half-sine.
    var chip = new OplCodec(OplCodec.Chip.Opl, Clock);
    chip.WriteRegister(0x20, 0x21);
    chip.WriteRegister(0x23, 0x21);
    chip.WriteRegister(0x40, 0x3F);
    chip.WriteRegister(0x43, 0x00);
    chip.WriteRegister(0x60, 0xF0);
    chip.WriteRegister(0x63, 0xF0);
    chip.WriteRegister(0x80, 0x00);
    chip.WriteRegister(0x83, 0x00);
    chip.WriteRegister(0xE3, 0x01);   // attempt half-sine — ignored on OPL
    chip.WriteRegister(0xA0, 0x40);
    chip.WriteRegister(0xB0, 0x10 | 0x20);
    RenderMono(chip, 256);
    var wave = RenderMono(chip, 4096);
    Assert.That(wave.Min(), Is.LessThan(0), "OPL is sine-only: the negative half survives");
  }

  // ──────────── 4. Key-on → release ────────────

  [Test]
  public void KeyOn_ProducesSignal_ThenReleaseDecays() {
    var chip = BuildVoice(OplCodec.Chip.Opl2, fnum: 300, block: 4, Clock);
    var sustained = RenderMono(chip, 4096).Select(s => (int)Math.Abs(s)).Max();
    Assert.That(sustained, Is.GreaterThan(100), "key-on yields audible output");

    chip.WriteRegister(0xB0, (300 >> 8) | (4 << 2)); // clear key-on bit (0x20)
    var fadeStart = RenderMono(chip, 1024).Select(s => (int)Math.Abs(s)).Max();
    var fadeLater = RenderMono(chip, 60000).Select(s => (int)Math.Abs(s)).Max();
    Assert.That(fadeLater, Is.LessThan(fadeStart), "release decays the envelope");
  }

  // ──────────── 5. Total-level attenuation ────────────

  [Test]
  public void TotalLevel_HigherAttenuationLowersOutput() {
    int PeakForTl(int tl) {
      var chip = new OplCodec(OplCodec.Chip.Opl2, Clock);
      chip.WriteRegister(0x20, 0x21);
      chip.WriteRegister(0x23, 0x21);
      chip.WriteRegister(0x40, 0x3F);          // silence modulator
      chip.WriteRegister(0x43, tl & 0x3F);     // carrier TL
      chip.WriteRegister(0x60, 0xF0);
      chip.WriteRegister(0x63, 0xF0);
      chip.WriteRegister(0x80, 0x00);
      chip.WriteRegister(0x83, 0x00);
      chip.WriteRegister(0xA0, 0x80);
      chip.WriteRegister(0xB0, (4 << 2) | 0x20);
      return RenderMono(chip, 4096).Select(s => (int)Math.Abs(s)).Max();
    }
    var loud = PeakForTl(0);
    var mid = PeakForTl(16);
    var quiet = PeakForTl(40);
    Assert.That(loud, Is.GreaterThan(mid));
    Assert.That(mid, Is.GreaterThan(quiet));
  }

  // ──────────── 6. Envelope rate arithmetic ────────────

  [Test]
  public void AttackRate_FasterReachesPeakSooner() {
    int SamplesToPeak(int attackRate) {
      var chip = new OplCodec(OplCodec.Chip.Opl2, Clock);
      chip.WriteRegister(0x20, 0x21);
      chip.WriteRegister(0x23, 0x21);
      chip.WriteRegister(0x40, 0x3F);
      chip.WriteRegister(0x43, 0x00);
      chip.WriteRegister(0x60, 0xF0);
      chip.WriteRegister(0x63, (attackRate << 4) | 0x00); // carrier AR
      chip.WriteRegister(0x80, 0x00);
      chip.WriteRegister(0x83, 0x00);
      chip.WriteRegister(0xA0, 0x80);
      chip.WriteRegister(0xB0, (4 << 2) | 0x20);
      for (var i = 0; i < 200000; ++i)
        if (Math.Abs((int)chip.RenderSample()) > 1000)
          return i;
      return int.MaxValue;
    }
    var fast = SamplesToPeak(15);
    var slow = SamplesToPeak(4);
    Assert.That(fast, Is.LessThan(slow), "higher AR reaches loud output sooner");
  }

  // ──────────── 7. FM vs AM connection ────────────

  [Test]
  public void Connection_FmAndAmProduceDifferentOutput() {
    short[] Render(bool additive) {
      var chip = new OplCodec(OplCodec.Chip.Opl2, Clock);
      chip.WriteRegister(0x20, 0x21);
      chip.WriteRegister(0x23, 0x21);
      chip.WriteRegister(0x40, 0x00);   // loud modulator (matters for both modes)
      chip.WriteRegister(0x43, 0x00);
      chip.WriteRegister(0x60, 0xF0);
      chip.WriteRegister(0x63, 0xF0);
      chip.WriteRegister(0x80, 0x00);
      chip.WriteRegister(0x83, 0x00);
      chip.WriteRegister(0xC0, additive ? 0x01 : 0x00);
      chip.WriteRegister(0xA0, 0x80);
      chip.WriteRegister(0xB0, (4 << 2) | 0x20);
      RenderMono(chip, 256);
      return RenderMono(chip, 4096);
    }
    var fm = Render(false);
    var am = Render(true);
    var diff = 0L;
    for (var i = 0; i < fm.Length; ++i)
      diff += Math.Abs(fm[i] - am[i]);
    Assert.That(diff, Is.GreaterThan(0), "FM and additive connections differ");
  }

  // ──────────── 8. Feedback ────────────

  [Test]
  public void Feedback_NonzeroChangesModulatorOutput() {
    short[] Render(int feedback) {
      var chip = new OplCodec(OplCodec.Chip.Opl2, Clock);
      chip.WriteRegister(0x20, 0x21);
      chip.WriteRegister(0x23, 0x21);
      chip.WriteRegister(0x40, 0x00);   // loud modulator so feedback matters
      chip.WriteRegister(0x43, 0x00);
      chip.WriteRegister(0x60, 0xF0);
      chip.WriteRegister(0x63, 0xF0);
      chip.WriteRegister(0x80, 0x00);
      chip.WriteRegister(0x83, 0x00);
      chip.WriteRegister(0xC0, (feedback << 1) | 0x00); // FM connection, given FB
      chip.WriteRegister(0xA0, 0x80);
      chip.WriteRegister(0xB0, (4 << 2) | 0x20);
      RenderMono(chip, 256);
      return RenderMono(chip, 4096);
    }
    var noFb = Render(0);
    var withFb = Render(6);
    var diff = 0L;
    for (var i = 0; i < noFb.Length; ++i)
      diff += Math.Abs(noFb[i] - withFb[i]);
    Assert.That(diff, Is.GreaterThan(0), "feedback alters the modulator");
  }

  // ──────────── 9. OPL3 4-operator mode ────────────

  [Test]
  public void Opl3FourOp_PairsChannelsAndProducesOutput() {
    var chip = new OplCodec(OplCodec.Chip.Opl3, Opl3Clock);
    chip.WriteRegister(1, 0x05, 0x01);   // enable OPL3
    chip.WriteRegister(1, 0x04, 0x01);   // 4-op pair bit 0 (ch0 + ch3)

    // Program all four operators of the ch0/ch3 4-op voice loud & sustained.
    foreach (var addr in new[] { 0x00, 0x03, 0x08, 0x0B }) {
      chip.WriteRegister(0x20 + addr, 0x21);  // EG sustain, MUL=1
      chip.WriteRegister(0x40 + addr, 0x00);  // loud
      chip.WriteRegister(0x60 + addr, 0xF0);  // fast attack
      chip.WriteRegister(0x80 + addr, 0x00);
    }
    chip.WriteRegister(0xC0, 0x30);      // ch0: FB=0, FM, L+R enabled
    chip.WriteRegister(0xC3, 0x30);      // ch3 (partner): L+R enabled
    chip.WriteRegister(0xA0, 0x80);
    chip.WriteRegister(0xB0, (4 << 2) | 0x20); // key-on the 4-op voice via ch0

    var (l, _) = RenderStereo(chip, 8192);
    Assert.That(l.Sum(s => (long)Math.Abs(s)), Is.GreaterThan(0L), "4-op voice is audible");
  }

  // ──────────── 10. OPL3 L/R panning ────────────

  [Test]
  public void Opl3Panning_LeftOnlyRegSilencesRight() {
    var chip = new OplCodec(OplCodec.Chip.Opl3, Opl3Clock);
    chip.WriteRegister(1, 0x05, 0x01);   // enable OPL3
    chip.WriteRegister(0x20, 0x21);
    chip.WriteRegister(0x23, 0x21);
    chip.WriteRegister(0x40, 0x3F);
    chip.WriteRegister(0x43, 0x00);
    chip.WriteRegister(0x60, 0xF0);
    chip.WriteRegister(0x63, 0xF0);
    chip.WriteRegister(0x80, 0x00);
    chip.WriteRegister(0x83, 0x00);
    chip.WriteRegister(0xC0, 0x10);      // FB=0, FM, LEFT only (bit4 set, bit5 clear)
    chip.WriteRegister(0xA0, 0x80);
    chip.WriteRegister(0xB0, (4 << 2) | 0x20);

    var (l, r) = RenderStereo(chip, 8192);
    Assert.That(l.Sum(s => (long)Math.Abs(s)), Is.GreaterThan(0L), "left carries the voice");
    Assert.That(r.Sum(s => (long)Math.Abs(s)), Is.EqualTo(0L), "right is panned off");
  }

  // ──────────── 11. Rhythm mode ────────────

  [Test]
  public void RhythmMode_BassDrumKeyOnProducesOutput() {
    var chip = new OplCodec(OplCodec.Chip.Opl2, Clock);
    // Give channel 6 (BD) a frequency and loud, fast operators.
    chip.WriteRegister(0x30, 0x21);   // ch6 modulator (op @ 0x10)? — use BD operator addresses
    // BD uses ch6: modulator op address 0x10, carrier 0x13.
    chip.WriteRegister(0x30, 0x00);
    foreach (var addr in new[] { 0x10, 0x13 }) {
      chip.WriteRegister(0x20 + addr, 0x21);
      chip.WriteRegister(0x40 + addr, 0x00);
      chip.WriteRegister(0x60 + addr, 0xF0);
      chip.WriteRegister(0x80 + addr, 0x00);
    }
    chip.WriteRegister(0xA6, 0x40);
    chip.WriteRegister(0xB6, (2 << 2) | 0x01);
    chip.WriteRegister(0xBD, 0x20 | 0x10); // rhythm on + bass-drum key

    Assert.That(chip.RhythmMode, Is.True);
    var peak = RenderMono(chip, 8192).Select(s => (int)Math.Abs(s)).Max();
    Assert.That(peak, Is.GreaterThan(0), "rhythm bass drum is audible");
  }

  // ──────────── 12. Quiescent silence ────────────

  [Test]
  public void NoKeyOn_ProducesSilence() {
    var chip = new OplCodec(OplCodec.Chip.Opl2, Clock);
    var (l, r) = RenderStereo(chip, 4096);
    Assert.That(l.Sum(s => (long)Math.Abs(s)) + r.Sum(s => (long)Math.Abs(s)),
      Is.EqualTo(0L), "a chip with no keyed voice is silent");
  }

  // ──────────── 13. Y8950 ADPCM gating ────────────

  [Test]
  public void Y8950_AdpcmGatedWithoutSampleMemory() {
    var chip = new OplCodec(OplCodec.Chip.Y8950, Clock);
    Assert.That(chip.AdpcmActive, Is.False, "ADPCM is gated until sample memory is loaded");
    chip.LoadAdpcmMemory(new byte[] { 0x11, 0x22, 0x33 });
    Assert.That(chip.AdpcmActive, Is.True, "ADPCM activates once memory is supplied");
    // FM still renders regardless.
    var voice = BuildVoice(OplCodec.Chip.Y8950, fnum: 300, block: 4, Clock);
    Assert.That(RenderMono(voice, 4096).Select(s => (int)Math.Abs(s)).Max(),
      Is.GreaterThan(0), "Y8950 FM voices even with ADPCM gated");
  }
}
