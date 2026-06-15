using Codec.Ym2413;

namespace Compression.Tests.Codecs.Ym2413;

[TestFixture]
public class Ym2413CodecTests {

  private const double Clock = 3579545.0;

  private static short[] RenderMono(Ym2413Codec opll, int frames) {
    var mono = new short[frames];
    for (var i = 0; i < frames; ++i)
      mono[i] = opll.RenderSample();
    return mono;
  }

  // Programs channel 0 with the given instrument, full carrier volume, F-num/block, key-on.
  private static Ym2413Codec BuildVoice(int instrument, int fnum, int block, double clock = Clock) {
    var opll = new Ym2413Codec(clock);
    opll.WriteRegister(0x30, (instrument << 4) | 0x00); // instrument, volume 0 = loudest
    opll.WriteRegister(0x10, fnum & 0xFF);              // F-num low
    opll.WriteRegister(0x20, ((fnum >> 8) & 0x01) | (block << 1) | 0x10); // F-hi, block, key-on
    return opll;
  }

  // ──────────── 1. Patch ROM pinned to emu2413 ────────────

  /// <summary>
  /// The 16-row instrument ROM is the part that makes the OPLL unique; several rows are pinned
  /// against the emu2413 v1.5.9 <c>default_inst[OPLL_2413_TONE]</c> table.
  /// </summary>
  [Test]
  public void InstrumentRom_MatchesEmu2413Table() {
    var rom = Ym2413Codec.InstrumentRom;
    Assert.That(rom.Count, Is.GreaterThanOrEqualTo(16));

    Assert.Multiple(() => {
      // Row 0: user patch template — all zero.
      for (var i = 0; i < 8; ++i)
        Assert.That(rom[0][i], Is.EqualTo((byte)0x00), $"user[{i}]");

      // Row 1: Violin.
      Assert.That(rom[1], Is.EqualTo(new byte[] { 0x71, 0x61, 0x1e, 0x17, 0xd0, 0x78, 0x00, 0x17 }));
      // Row 3: Piano.
      Assert.That(rom[3], Is.EqualTo(new byte[] { 0x13, 0x01, 0x99, 0x00, 0xf2, 0xc4, 0x21, 0x23 }));
      // Row 12: Vibraphone.
      Assert.That(rom[12], Is.EqualTo(new byte[] { 0x17, 0xc1, 0x24, 0x07, 0xf8, 0xf8, 0x22, 0x12 }));
      // Row 15: Electric Guitar.
      Assert.That(rom[15], Is.EqualTo(new byte[] { 0x41, 0x41, 0x89, 0x03, 0xf1, 0xe4, 0xc0, 0x13 }));
    });
  }

  // ──────────── 2. Shared operator ROMs ────────────

  /// <summary>
  /// The log-sine and exponential ROMs are the same die constants the OPN2 uses; they must
  /// carry the published values exactly AND match the canonical formulas.
  /// </summary>
  [Test]
  public void OperatorRoms_MatchSharedDieConstants() {
    var logsin = Ym2413Codec.LogSinRom;
    var exp = Ym2413Codec.ExpRom;
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

  // ──────────── 3. F-num → frequency ────────────

  /// <summary>
  /// The OPLL phase generator runs at clock/72; the fundamental of a steady voice should track
  /// <c>fnum * clock / (72 * 2^(19-block))</c>. We pin a configuration near A440.
  /// </summary>
  [Test]
  public void Frequency_TracksFNumBlockFormula() {
    const int block = 5;
    var nativeRate = Clock / 72.0;
    // The OPLL fundamental is fnum * clock / (72 * 2^(19-block)) for a MUL=1 carrier; with the
    // 10-bit sine indexed off the top of the phase accumulator the per-sample step is
    // (fnum << block) >> 1, so freq = (fnum << block) >> 1 * nativeRate / 2^19. Solve for 440.
    // block 5 keeps the 9-bit F-num (0..511) in range for A440.
    var fnum = (int)Math.Round(440.0 * (1 << 19) / nativeRate / (1 << block) * 2);

    // A clean single-carrier user patch: modulator silenced (TL max), carrier MUL=1, no
    // feedback, sustained envelope, fast attack — produces a pure carrier sine at the
    // fundamental.
    var opll = new Ym2413Codec(Clock);
    opll.WriteRegister(0x00, 0x20);   // mod: EG sustained, MUL=0
    opll.WriteRegister(0x01, 0x21);   // car: EG sustained, MUL=1
    opll.WriteRegister(0x02, 0x3F);   // mod KSL=0, TL=63 (silent modulator)
    opll.WriteRegister(0x03, 0x00);   // car KSL=0, FB=0, sine waveforms
    opll.WriteRegister(0x04, 0xF0);   // mod AR=15, DR=0
    opll.WriteRegister(0x05, 0xF0);   // car AR=15, DR=0
    opll.WriteRegister(0x06, 0x0F);   // mod SL=0, RR=15
    opll.WriteRegister(0x07, 0x0F);   // car SL=0, RR=15
    opll.WriteRegister(0x30, 0x00);   // instrument 0 (user), volume 0 (loud)
    opll.WriteRegister(0x10, fnum & 0xFF);
    opll.WriteRegister(0x20, ((fnum >> 8) & 0x01) | (block << 1) | 0x10);

    var total = (int)(nativeRate * 0.25);
    var mono = RenderMono(opll, total);
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
      if (sum > best) {
        best = sum;
        bestLag = lag;
      }
    }
    return sampleRate / bestLag;
  }

  // ──────────── 4. Key-on then release ────────────

  [Test]
  public void KeyOn_ProducesSignal_ThenReleaseDecaysTowardSilence() {
    var opll = BuildVoice(instrument: 4, fnum: 300, block: 4);

    // After key-on a sustained voice produces a strong signal.
    var sustained = RenderMono(opll, 4096).Select(s => (int)Math.Abs(s)).Max();
    Assert.That(sustained, Is.GreaterThan(100), "key-on yields audible output");

    // Key off → release; amplitude must fall toward silence.
    opll.WriteRegister(0x20, (300 >> 8) | (4 << 1)); // clear key-on bit (0x10)
    var fadeStart = RenderMono(opll, 1024).Select(s => (int)Math.Abs(s)).Max();
    var fadeLater = RenderMono(opll, 60000).Select(s => (int)Math.Abs(s)).Max();
    Assert.That(fadeLater, Is.LessThan(fadeStart), "release decays the envelope");
  }

  // ──────────── 5. Carrier volume attenuation ────────────

  [Test]
  public void CarrierVolume_HigherAttenuationLowersOutput() {
    int PeakForVolume(int volume) {
      var opll = new Ym2413Codec(Clock);
      opll.WriteRegister(0x30, (4 << 4) | (volume & 0x0F)); // Flute, given volume
      opll.WriteRegister(0x10, 0x80);
      opll.WriteRegister(0x20, (4 << 1) | 0x10);
      return RenderMono(opll, 4096).Select(s => (int)Math.Abs(s)).Max();
    }

    var loud = PeakForVolume(0);
    var mid = PeakForVolume(7);
    var quiet = PeakForVolume(14);
    Assert.That(loud, Is.GreaterThan(mid));
    Assert.That(mid, Is.GreaterThan(quiet));
  }

  // ──────────── 6. Envelope rate arithmetic ────────────

  /// <summary>
  /// A faster attack rate must reach peak loudness in fewer samples than a slow one — the
  /// envelope-rate timing must be monotonic in the rate field.
  /// </summary>
  [Test]
  public void AttackRate_FasterReachesPeakSooner() {
    int SamplesToPeak(int attackRate) {
      var opll = new Ym2413Codec(Clock);
      // User patch (instrument 0): set carrier AR (patch byte 5 high nibble), everything else
      // tuned so the carrier is audible and sustained.
      opll.WriteRegister(0x00, 0x00);                 // mod flags + MUL=0
      opll.WriteRegister(0x01, 0x01);                 // car flags + MUL=1
      opll.WriteRegister(0x02, 0x00);                 // mod KSL/TL = 0
      opll.WriteRegister(0x03, 0x00);                 // car KSL, FB=0
      opll.WriteRegister(0x04, 0xF0);                 // mod AR=15, DR=0
      opll.WriteRegister(0x05, (attackRate << 4) | 0x00); // car AR, DR=0
      opll.WriteRegister(0x06, 0x00);                 // mod SL=0, RR=0
      opll.WriteRegister(0x07, 0x00);                 // car SL=0, RR=0
      opll.WriteRegister(0x30, (0 << 4) | 0x00);      // instrument 0 (user), volume 0
      opll.WriteRegister(0x10, 0x80);
      opll.WriteRegister(0x20, (4 << 1) | 0x10);

      var prev = 0;
      for (var i = 0; i < 60000; ++i) {
        var s = Math.Abs((int)opll.RenderSample());
        if (s > prev) prev = s;
        if (s > 1000) return i;
      }
      return int.MaxValue;
    }

    var fast = SamplesToPeak(15);
    var slow = SamplesToPeak(4);
    Assert.That(fast, Is.LessThan(slow), "higher AR reaches loud output sooner");
  }

  // ──────────── 7. Rhythm mode ────────────

  /// <summary>Rhythm mode bass-drum key-on must produce output.</summary>
  [Test]
  public void RhythmMode_BassDrumKeyOnProducesOutput() {
    var opll = new Ym2413Codec(Clock);

    // Give channel 6 a frequency so the BD operators have a phase increment.
    opll.WriteRegister(0x16, 0x20);                 // ch6 F-num low
    opll.WriteRegister(0x26, (2 << 1) | 0x01);      // ch6 block 2, F-num bit8
    opll.WriteRegister(0x36, 0x00);                 // ch6 volume loud

    // Enable rhythm mode and key the bass drum (bit4).
    opll.WriteRegister(0x0E, 0x20 | 0x10);

    Assert.That(opll.RhythmMode, Is.True);
    var peak = RenderMono(opll, 8192).Select(s => (int)Math.Abs(s)).Max();
    Assert.That(peak, Is.GreaterThan(0), "rhythm bass drum is audible after key-on");
  }

  // ──────────── 8. Quiescent silence ────────────

  [Test]
  public void NoKeyOn_ProducesSilence() {
    var opll = new Ym2413Codec(Clock);
    var energy = RenderMono(opll, 4096).Sum(s => (long)Math.Abs(s));
    Assert.That(energy, Is.EqualTo(0L), "a chip with no keyed voice is silent");
  }
}
