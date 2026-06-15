#pragma warning disable CS1591
using Codec.HuC6280;

namespace Compression.Tests.Codecs.HuC6280;

[TestFixture]
public class PcePsgTests {

  // Selects a channel and writes its full register set: frequency, control (enable+volume),
  // L/R volume, then a 32-step waveform.
  private static void ProgramTone(PcePsg psg, int channel, int period, byte[] waveform,
      int overall = 0x1F, int left = 0x0F, int right = 0x0F) {
    psg.WriteRegister(0x00, (byte)channel);          // select channel
    psg.WriteRegister(0x02, (byte)(period & 0xFF));  // freq low
    psg.WriteRegister(0x03, (byte)((period >> 8) & 0x0F)); // freq high
    // Control: write waveform first while not enabled, then enable.
    psg.WriteRegister(0x04, (byte)(overall & 0x1F)); // overall vol, not enabled, not DDA
    foreach (var w in waveform)
      psg.WriteRegister(0x06, w);                     // waveform data (auto-increment)
    psg.WriteRegister(0x05, (byte)(((left & 0x0F) << 4) | (right & 0x0F))); // L/R vol
    psg.WriteRegister(0x04, (byte)(0x80 | (overall & 0x1F))); // enable + overall vol
  }

  private static byte[] SquareWave() {
    var w = new byte[32];
    for (var i = 0; i < 16; ++i) w[i] = 31;
    for (var i = 16; i < 32; ++i) w[i] = 0;
    return w;
  }

  [Test]
  public void WaveformWrite_AutoIncrementsAndStoresAllSteps() {
    var psg = new PcePsg(44100);
    var wave = new byte[32];
    for (var i = 0; i < 32; ++i) wave[i] = (byte)i;
    ProgramTone(psg, 0, period: 100, wave, overall: 0x1F, left: 15, right: 15);

    // Step the channel through a full waveform cycle and confirm it is not stuck silent.
    var sawNonZero = false;
    for (var t = 0; t < 100 * 40; ++t) {
      psg.StepForTest();
      var (l, _) = psg.MixForTest();
      if (Math.Abs(l) > 0.001) sawNonZero = true;
    }
    Assert.That(sawNonZero, Is.True, "a programmed waveform must produce nonzero output");
  }

  [Test]
  public void FrequencyPeriod_ControlsWaveAdvanceRate() {
    // A shorter period advances the wave-read pointer more often than a longer one over the same
    // number of ticks, so the produced fundamental is higher. We count read-index wraps via a
    // square wave's polarity flips.
    int Flips(int period) {
      var psg = new PcePsg(44100);
      ProgramTone(psg, 0, period, SquareWave(), overall: 0x1F, left: 15, right: 15);
      var flips = 0;
      var prev = 0.0;
      for (var t = 0; t < 4000; ++t) {
        psg.StepForTest();
        var (l, _) = psg.MixForTest();
        if (Math.Sign(l) != Math.Sign(prev) && l != 0) ++flips;
        prev = l;
      }
      return flips;
    }
    Assert.That(Flips(50), Is.GreaterThan(Flips(400)),
      "a shorter period must yield a higher fundamental");
  }

  [Test]
  public void LeftRightVolume_ProducesExpectedPanGains() {
    // Pan fully left: left channel volume max, right zero → left output >> right output.
    var psg = new PcePsg(44100);
    ProgramTone(psg, 0, period: 60, SquareWave(), overall: 0x1F, left: 15, right: 0);

    double sumL = 0, sumR = 0;
    for (var t = 0; t < 2000; ++t) {
      psg.StepForTest();
      var (l, r) = psg.MixForTest();
      sumL += Math.Abs(l);
      sumR += Math.Abs(r);
    }
    Assert.That(sumL, Is.GreaterThan(0));
    Assert.That(sumR, Is.LessThan(sumL * 0.01), "right output must be far quieter when panned left");
  }

  [Test]
  public void KeyOn_ProducesNonzeroRender() {
    var psg = new PcePsg(44100);
    ProgramTone(psg, 0, period: 80, SquareWave(), overall: 0x1F, left: 15, right: 15);
    var buf = new short[44100 * 2]; // 1 second stereo
    psg.RenderSamples(buf, 44100);
    var peak = buf.Max(x => Math.Abs((int)x));
    Assert.That(peak, Is.GreaterThan(100), "an enabled tone channel must not render silence");
  }

  [Test]
  public void DisabledChannel_RendersSilence() {
    var psg = new PcePsg(44100);
    // Program but never enable (control bit 7 left clear).
    psg.WriteRegister(0x00, 0);
    psg.WriteRegister(0x02, 80);
    foreach (var w in SquareWave()) psg.WriteRegister(0x06, w);
    psg.WriteRegister(0x05, 0xFF);
    var buf = new short[44100 * 2];
    psg.RenderSamples(buf, 44100);
    Assert.That(buf.All(x => x == 0), Is.True, "a channel that was never enabled is silent");
  }

  [Test]
  public void NoiseChannel_ProducesNonzeroOutput() {
    var psg = new PcePsg(44100);
    // Channel 6 (index 5): enable with volume, then turn on noise.
    psg.WriteRegister(0x00, 5);
    psg.WriteRegister(0x05, 0xFF);                  // L/R volume max
    psg.WriteRegister(0x04, 0x9F);                  // enable + overall vol max
    psg.WriteRegister(0x07, 0x90);                  // noise enable + frequency
    var buf = new short[44100 * 2];
    psg.RenderSamples(buf, 44100);
    var peak = buf.Max(x => Math.Abs((int)x));
    Assert.That(peak, Is.GreaterThan(50), "the noise channel must produce nonzero output");
  }

  [Test]
  public void DdaMode_OutputsDirectSample() {
    var psg = new PcePsg(44100);
    psg.WriteRegister(0x00, 0);
    psg.WriteRegister(0x05, 0xFF);                  // L/R volume
    psg.WriteRegister(0x04, 0xC0);                  // enable + DDA mode, overall vol 0
    psg.WriteRegister(0x06, 31);                    // DDA sample = +max
    psg.StepForTest();
    var (l, _) = psg.MixForTest();
    Assert.That(l, Is.GreaterThan(0), "DDA mode emits the written sample directly");
  }
}
