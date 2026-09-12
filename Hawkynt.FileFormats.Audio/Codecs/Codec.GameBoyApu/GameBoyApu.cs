#pragma warning disable CS1591
namespace Codec.GameBoyApu;

/// <summary>
/// A register-level Game Boy (DMG) APU emulator. Driven by <see cref="Write"/> calls into the
/// sound register window $FF10-$FF3F (the four channels' control registers, the master
/// NR50/NR51/NR52, and the 16-byte wave RAM at $FF30-$FF3F), it synthesises the two square
/// channels (CH1 with sweep, CH2), the 4-bit wave channel (CH3) and the LFSR noise channel
/// (CH4), runs them off the 512 Hz frame sequencer (length 256 Hz, envelope 64 Hz, sweep
/// 128 Hz) and mixes them through the per-channel L/R routing of NR51 and the master volumes
/// of NR50 into a stereo signal.
/// <para>The chip steps internally at the 4.194304 MHz Game Boy clock and decimates to the
/// requested output rate (default 44100 Hz) by averaging every internal step within an output
/// sample window — a cheap anti-aliasing measure rather than a designed decimation filter.</para>
/// </summary>
public sealed class GameBoyApu {

  /// <summary>Default render rate.</summary>
  public const int OutputSampleRate = 44100;

  /// <summary>The Game Boy master clock in Hz.</summary>
  public const double ClockHz = 4_194_304.0;

  private readonly int _outputRate;

  private readonly PulseChannel _ch1 = new(hasSweep: true);
  private readonly PulseChannel _ch2 = new(hasSweep: false);
  private readonly WaveChannel _ch3 = new();
  private readonly NoiseChannel _ch4 = new();

  // NR50/NR51/NR52.
  private int _leftVolume = 7;   // 0-7
  private int _rightVolume = 7;
  private byte _routing;          // NR51 — bit n = channel n+1 right, bit n+4 = left
  private bool _powerOn = true;

  // Frame-sequencer divider: ticks at 512 Hz, i.e. every ClockHz/512 master cycles.
  private const double FrameSequencerPeriod = ClockHz / 512.0;
  private double _frameSequencerCounter;
  private int _frameStep;

  // Fractional accumulator for clock→output decimation.
  private readonly double _clocksPerSample;

  /// <summary>
  /// Initializes a new instance of <see cref="GameBoyApu"/>.
  /// </summary>
  public GameBoyApu(int outputRate = OutputSampleRate) {
    this._outputRate = outputRate;
    this._clocksPerSample = ClockHz / outputRate;
  }

  /// <summary>Reads back an APU register at absolute address <paramref name="addr"/> ($FF10-$FF3F).</summary>
  public byte Read(ushort addr) {
    if (addr is >= 0xFF30 and <= 0xFF3F)
      return this._ch3.ReadWaveRam(addr - 0xFF30);

    return addr switch {
      0xFF24 => (byte)((this._leftVolume << 4) | this._rightVolume),
      0xFF25 => this._routing,
      0xFF26 => (byte)((this._powerOn ? 0x80 : 0)
                       | (this._ch1.Enabled ? 0x01 : 0)
                       | (this._ch2.Enabled ? 0x02 : 0)
                       | (this._ch3.Enabled ? 0x04 : 0)
                       | (this._ch4.Enabled ? 0x08 : 0)
                       | 0x70),
      _ => 0xFF,
    };
  }

  /// <summary>Writes an APU register at absolute address <paramref name="addr"/> ($FF10-$FF3F).</summary>
  public void Write(ushort addr, byte value) {
    if (addr is >= 0xFF30 and <= 0xFF3F) {
      this._ch3.WriteWaveRam(addr - 0xFF30, value);
      return;
    }

    // When powered off, only NR52 power and (on DMG) wave RAM are writable.
    if (!this._powerOn && addr != 0xFF26)
      return;

    switch (addr) {
      case >= 0xFF10 and <= 0xFF14: this._ch1.Write(addr - 0xFF10, value); break;
      case >= 0xFF16 and <= 0xFF19: this._ch2.Write(addr - 0xFF15, value); break; // CH2 has no sweep reg; map $FF16→reg1
      case >= 0xFF1A and <= 0xFF1E: this._ch3.Write(addr - 0xFF1A, value); break;
      case >= 0xFF20 and <= 0xFF23: this._ch4.Write(addr - 0xFF20, value); break;
      case 0xFF24: this._rightVolume = value & 0x07; this._leftVolume = (value >> 4) & 0x07; break;
      case 0xFF25: this._routing = value; break;
      case 0xFF26:
        this._powerOn = (value & 0x80) != 0;
        if (!this._powerOn) {
          this._ch1.Disable(); this._ch2.Disable(); this._ch3.Disable(); this._ch4.Disable();
          this._routing = 0; this._leftVolume = 0; this._rightVolume = 0;
        }
        break;
    }
  }

  /// <summary>Advances the frame sequencer and channels by one master clock cycle.</summary>
  private void StepCycle() {
    this._frameSequencerCounter += 1.0;
    if (this._frameSequencerCounter >= FrameSequencerPeriod) {
      this._frameSequencerCounter -= FrameSequencerPeriod;
      this.ClockFrameSequencer();
    }

    this._ch1.StepTimer();
    this._ch2.StepTimer();
    this._ch3.StepTimer();
    this._ch4.StepTimer();
  }

  private void ClockFrameSequencer() {
    // 8-step sequence: length on even steps, sweep on 2/6, envelope on 7.
    var step = this._frameStep;
    if ((step & 1) == 0) {
      this._ch1.ClockLength();
      this._ch2.ClockLength();
      this._ch3.ClockLength();
      this._ch4.ClockLength();
    }
    if (step is 2 or 6)
      this._ch1.ClockSweep();
    if (step == 7) {
      this._ch1.ClockEnvelope();
      this._ch2.ClockEnvelope();
      this._ch4.ClockEnvelope();
    }
    this._frameStep = (step + 1) & 7;
  }

  /// <summary>Mixes the current channel outputs into a single stereo sample pair (-1..+1 range).</summary>
  private (double Left, double Right) MixSample() {
    var s1 = this._ch1.Output();
    var s2 = this._ch2.Output();
    var s3 = this._ch3.Output();
    var s4 = this._ch4.Output();

    double left = 0, right = 0;
    // Each channel's DAC maps its 0..15 digital level to an analog −1..+1; a disabled DAC is
    // silent (analog 0), so routing simply sums the per-channel analog outputs. NR51's low
    // nibble routes to the right, the high nibble to the left.
    var a1 = Dac(s1);
    var a2 = Dac(s2);
    var a3 = Dac(s3);
    var a4 = Dac(s4);
    if ((this._routing & 0x01) != 0) right += a1;
    if ((this._routing & 0x02) != 0) right += a2;
    if ((this._routing & 0x04) != 0) right += a3;
    if ((this._routing & 0x08) != 0) right += a4;
    if ((this._routing & 0x10) != 0) left += a1;
    if ((this._routing & 0x20) != 0) left += a2;
    if ((this._routing & 0x40) != 0) left += a3;
    if ((this._routing & 0x80) != 0) left += a4;

    // Average the four channels and scale by the master volume (1..8 = NR50 nibble + 1).
    left = left / 4.0 * ((this._leftVolume + 1) / 8.0);
    right = right / 4.0 * ((this._rightVolume + 1) / 8.0);
    return (left, right);
  }

  // Maps a channel's digital DAC output to an analog level. A disabled DAC reports -1 and is
  // silent (analog 0); an enabled DAC's 0..15 level maps linearly to -1..+1.
  private static double Dac(int digital) => digital < 0 ? 0.0 : digital / 7.5 - 1.0;

  /// <summary>
  /// Renders <paramref name="frames"/> stereo frames into <paramref name="stereoInterleaved"/>
  /// (which must hold <c>frames * 2</c> shorts). For each output frame the chip is stepped the
  /// fractional number of master cycles that one output sample spans, averaging every internal
  /// sample over that window before emitting the 16-bit pair.
  /// </summary>
  public void RenderSamples(Span<short> stereoInterleaved, int frames) {
    var error = 0.0;
    for (var f = 0; f < frames; ++f) {
      error += this._clocksPerSample;
      var steps = (int)error;
      error -= steps;
      if (steps < 1) steps = 1;

      double accLeft = 0, accRight = 0;
      for (var s = 0; s < steps; ++s) {
        this.StepCycle();
        var (l, r) = this.MixSample();
        accLeft += l;
        accRight += r;
      }
      accLeft /= steps;
      accRight /= steps;

      stereoInterleaved[f * 2] = ToSample(accLeft);
      stereoInterleaved[f * 2 + 1] = ToSample(accRight);
    }
  }

  private static short ToSample(double value) {
    var v = value * 28000.0; // leave headroom below full scale for the four summed channels
    if (v > short.MaxValue) v = short.MaxValue;
    if (v < short.MinValue) v = short.MinValue;
    return (short)v;
  }
}
