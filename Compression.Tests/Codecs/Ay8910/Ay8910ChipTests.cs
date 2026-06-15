#pragma warning disable CS1591
using Codec.Ay8910;

namespace Compression.Tests.Codecs.Ay8910;

[TestFixture]
public class Ay8910ChipTests {

  // Counts zero crossings of the (mono) sum of a rendered stereo buffer to estimate frequency.
  private static double EstimateFrequencyHz(short[] stereo, int frames) {
    var crossings = 0;
    var prev = 0;
    for (var f = 0; f < frames; ++f) {
      var v = stereo[f * 2] + stereo[f * 2 + 1];
      if (prev <= 0 && v > 0) ++crossings;
      prev = v;
    }
    return crossings / (frames / (double)Ay8910Chip.OutputSampleRate);
  }

  [Test]
  public void TonePeriod_ProducesExpectedFrequency() {
    // ZX clock 1.7734 MHz, period $100 (256) → f = clock / (16 * 256) ≈ 433 Hz.
    var chip = new Ay8910Chip(Ay8910Chip.ZxSpectrumClock, Ay8910Chip.StereoMode.Mono);
    chip.WriteReg(0, 0x00); // fine
    chip.WriteReg(1, 0x01); // coarse → period 0x100
    chip.WriteReg(7, 0xFE); // enable tone A only (active low: clear bit 0)
    chip.WriteReg(8, 0x0F); // full fixed volume on A

    var frames = Ay8910Chip.OutputSampleRate; // 1 second
    var buf = new short[frames * 2];
    chip.RenderSamples(buf, frames);

    // The tone generator TOGGLES at clock/(16*TP); one full square-wave cycle is two toggles,
    // so the audible fundamental is clock/(32*TP).
    var expected = Ay8910Chip.ZxSpectrumClock / (32.0 * 256.0);
    var measured = EstimateFrequencyHz(buf, frames);
    Assert.That(measured, Is.EqualTo(expected).Within(expected * 0.05));
  }

  [Test]
  public void ZeroVolume_IsSilent() {
    var chip = new Ay8910Chip(Ay8910Chip.ZxSpectrumClock, Ay8910Chip.StereoMode.Mono);
    chip.WriteReg(0, 0x00); chip.WriteReg(1, 0x01);
    chip.WriteReg(7, 0xFE); // tone A enabled
    chip.WriteReg(8, 0x00); // but amplitude 0

    var frames = 4410;
    var buf = new short[frames * 2];
    chip.RenderSamples(buf, frames);
    var peak = 0;
    foreach (var s in buf) peak = Math.Max(peak, Math.Abs(s));
    Assert.That(peak, Is.EqualTo(0), "amplitude 0 must be silent");
  }

  [Test]
  public void MixerDisablesTone_ChannelOutputsConstantDc() {
    // With tone AND noise disabled (mixer bits set) but a non-zero amplitude, the AY channel
    // outputs a constant level — there is no varying waveform to hear.
    var chip = new Ay8910Chip(Ay8910Chip.ZxSpectrumClock, Ay8910Chip.StereoMode.Mono);
    chip.WriteReg(0, 0x00); chip.WriteReg(1, 0x01);
    chip.WriteReg(8, 0x0F); // volume on A
    chip.WriteReg(7, 0xFF); // all tone + noise disabled

    var frames = 4410;
    var buf = new short[frames * 2];
    chip.RenderSamples(buf, frames);
    var distinct = new HashSet<short>();
    foreach (var s in buf) distinct.Add(s);
    Assert.That(distinct.Count, Is.EqualTo(1), "a fully disabled channel emits a single constant level");
  }

  [Test]
  public void MixerEnablesTone_ProducesOutput() {
    var chip = new Ay8910Chip(Ay8910Chip.ZxSpectrumClock, Ay8910Chip.StereoMode.Mono);
    chip.WriteReg(0, 0x00); chip.WriteReg(1, 0x01);
    chip.WriteReg(8, 0x0F);
    chip.WriteReg(7, 0xFE); // tone A enabled

    var frames = 4410;
    var buf = new short[frames * 2];
    chip.RenderSamples(buf, frames);
    var peak = 0;
    foreach (var s in buf) peak = Math.Max(peak, Math.Abs(s));
    Assert.That(peak, Is.GreaterThan(0));
  }

  [Test]
  public void VolumeTable_IsMonotonicAndLogarithmic() {
    var table = Ay8910Chip.VolumeTable;
    Assert.That(table.Count, Is.EqualTo(16));
    Assert.That(table[0], Is.EqualTo(0.0));
    Assert.That(table[15], Is.EqualTo(1.0).Within(1e-9));
    for (var i = 1; i < table.Count; ++i)
      Assert.That(table[i], Is.GreaterThan(table[i - 1]), $"step {i} must increase");
    // Successive steps grow by roughly 1.2×–1.7× across the curve (logarithmic, not linear).
    var ratio = table[8] / table[7];
    Assert.That(ratio, Is.GreaterThan(1.1));
  }

  [Test]
  public void NoiseLfsr_TogglesOutput() {
    var chip = new Ay8910Chip(Ay8910Chip.ZxSpectrumClock, Ay8910Chip.StereoMode.Mono);
    chip.WriteReg(6, 0x01); // fast noise period
    chip.WriteReg(8, 0x0F); // volume on A
    chip.WriteReg(7, 0xF7); // enable NOISE on A (clear bit 3), tone A disabled (bit0 set)

    var frames = 4410;
    var buf = new short[frames * 2];
    chip.RenderSamples(buf, frames);
    var distinct = new HashSet<short>();
    foreach (var s in buf) distinct.Add(s);
    Assert.That(distinct.Count, Is.GreaterThan(1), "noise must vary the output level over time");
  }

  [Test]
  public void Envelope_DecayShape_StartsHighEndsLow() {
    // Shape $00 (continue=0, attack=0): single decay 31→0 then hold.
    var chip = new Ay8910Chip(Ay8910Chip.ZxSpectrumClock, Ay8910Chip.StereoMode.Mono);
    chip.WriteReg(0, 0x00); chip.WriteReg(1, 0x01); // audible tone A
    chip.WriteReg(7, 0xFE);
    chip.WriteReg(8, 0x10); // channel A uses the envelope
    chip.WriteReg(11, 0x00); chip.WriteReg(12, 0x10); // slow-ish envelope period
    chip.WriteReg(13, 0x00); // shape: decay then hold at 0

    var frames = Ay8910Chip.OutputSampleRate; // 1 second
    var buf = new short[frames * 2];
    chip.RenderSamples(buf, frames);

    var earlyPeak = 0;
    for (var f = 0; f < frames / 10; ++f) earlyPeak = Math.Max(earlyPeak, Math.Abs(buf[f * 2]));
    var latePeak = 0;
    for (var f = frames * 9 / 10; f < frames; ++f) latePeak = Math.Max(latePeak, Math.Abs(buf[f * 2]));

    Assert.That(earlyPeak, Is.GreaterThan(0), "envelope starts at a non-zero level");
    Assert.That(latePeak, Is.LessThan(earlyPeak), "decay-and-hold shape ends quieter than it starts");
  }

  [Test]
  public void Envelope_ContinuousShape_KeepsOscillating() {
    // Shape $08 (continue=1, attack=0, alternate=0, hold=0): repeating saw-down. The level keeps
    // returning to a high value rather than holding at 0.
    var chip = new Ay8910Chip(Ay8910Chip.ZxSpectrumClock, Ay8910Chip.StereoMode.Mono);
    chip.WriteReg(0, 0x00); chip.WriteReg(1, 0x01);
    chip.WriteReg(7, 0xFE);
    chip.WriteReg(8, 0x10);
    chip.WriteReg(11, 0x00); chip.WriteReg(12, 0x04);
    chip.WriteReg(13, 0x08);

    var frames = Ay8910Chip.OutputSampleRate;
    var buf = new short[frames * 2];
    chip.RenderSamples(buf, frames);

    var latePeak = 0;
    for (var f = frames * 9 / 10; f < frames; ++f) latePeak = Math.Max(latePeak, Math.Abs(buf[f * 2]));
    Assert.That(latePeak, Is.GreaterThan(0), "a continuous (0x08) envelope keeps re-attacking, never holding at 0");
  }

  [Test]
  public void AbcStereo_PansAToLeftCToRight() {
    var chip = new Ay8910Chip(Ay8910Chip.ZxSpectrumClock, Ay8910Chip.StereoMode.Abc);
    // Channel A tone on, full volume; C silent.
    chip.WriteReg(0, 0x00); chip.WriteReg(1, 0x01);
    chip.WriteReg(8, 0x0F); // A volume
    chip.WriteReg(7, 0xFE); // enable tone A only

    var frames = 4410;
    var buf = new short[frames * 2];
    chip.RenderSamples(buf, frames);

    var leftPeak = 0; var rightPeak = 0;
    for (var f = 0; f < frames; ++f) {
      leftPeak = Math.Max(leftPeak, Math.Abs(buf[f * 2]));
      rightPeak = Math.Max(rightPeak, Math.Abs(buf[f * 2 + 1]));
    }
    Assert.That(leftPeak, Is.GreaterThan(0), "channel A feeds the left speaker in ABC mode");
    Assert.That(rightPeak, Is.EqualTo(0), "channel A must not appear on the right in ABC mode");
  }
}
