#pragma warning disable CS1591
namespace Codec.HuC6280;

/// <summary>
/// A register-level emulation of the PC Engine / TurboGrafx-16 PSG — the six-channel wavetable
/// sound generator integrated into the HuC6280. Each channel carries a 32-step, 5-bit signed
/// waveform table that the program writes one sample at a time (the write index auto-increments),
/// a 12-bit frequency period, a 5-bit overall (master) volume and independent 4-bit left/right
/// channel volumes for stereo panning. Channels also support a Direct-D/A (DDA) mode that pushes
/// the written byte straight to the output, and channels 5 and 6 (indices 4 and 5) support extra
/// modes: channel 5 a noise generator (5-bit LFSR), and channel 5 acting as a low-frequency
/// oscillator (LFO) that modulates channel 4's frequency. A global left/right master volume
/// (the "MML"/balance register at port $0801) scales the final mix.
/// <para>The chip is driven by writing its register file through <see cref="WriteRegister"/>:
/// port $0800 selects the active channel (0-5), $0801 is the global L/R balance, $0802/$0803 set
/// the active channel's frequency low/high, $0804 the channel control (enable, DDA, overall
/// volume), $0805 the channel L/R volume, $0806 the waveform-table data (DDA sample), and
/// $0807/$0808 the noise enable/frequency and LFO frequency/control. The waveform step counter
/// advances at the documented PSG clock of 3.579545 MHz / 32, and the host pulls 16-bit stereo
/// samples through <see cref="RenderSamples"/>, which decimates to the requested output rate by
/// averaging the channel mix across each output-sample window.</para>
/// <para>Faithful to Mednafen <c>pce_psg.cpp</c> and the ChregPSG/Charles MacDonald PSG notes.
/// Approximations: the per-channel low-pass and the analog volume taper of the real DAC are not
/// modelled (volumes use the documented 1.5 dB-per-step exponential table); the LFO depth/trigger
/// edge cases and the precise noise LFSR tap of channel 5 follow Mednafen but the LFO is applied
/// as a simple additive period modulation of channel 4.</para>
/// </summary>
public sealed class PcePsg {

  /// <summary>Default render rate.</summary>
  public const int OutputSampleRate = 44100;

  /// <summary>PSG master clock (≈ NTSC PC Engine system clock / 2).</summary>
  public const double PsgClockHz = 3579545.0;

  private sealed class Channel {
    public int FrequencyPeriod;          // 12-bit period (registers $0802/$0803)
    public bool Enabled;                 // control bit 7
    public bool DdaMode;                 // control bit 6
    public int OverallVolume;            // 5-bit (control bits 0-4)
    public int LeftVolume;               // 4-bit
    public int RightVolume;              // 4-bit
    public readonly int[] Waveform = new int[32]; // 5-bit signed (stored 0..31, centred at 16)
    public int WaveWriteIndex;           // auto-incrementing write pointer
    public int WaveReadIndex;            // playback step
    public int DdaSample;                // last DDA sample (0..31)
    public double StepCounter;           // accumulates toward FrequencyPeriod
  }

  private readonly Channel[] _channels = new Channel[6];
  private int _selectedChannel;
  private int _masterLeft = 15;   // global L volume (MML high nibble)
  private int _masterRight = 15;  // global R volume (MML low nibble)

  // Channel 6 (index 5) noise generator.
  private bool _noiseEnabled;
  private int _noisePeriod = 1;
  private uint _noiseLfsr = 0x1FFFF;
  private double _noiseCounter;
  private int _noiseOutput;

  // Channel 5 (index 4) LFO modulating channel 4 (index 3).
  private int _lfoFrequency;
  private bool _lfoEnabled;
  private int _lfoControl;

  private readonly int _outputRate;
  private readonly double _clocksPerSample;
  private double _sampleError;

  // Exponential volume table: 5-bit (overall) and 4-bit (channel/master) attenuation steps. The
  // PSG attenuates ~1.5 dB per step; index 0 is full level, the maximum index is silence.
  private static readonly double[] VolumeTable5 = BuildVolumeTable(32);
  private static readonly double[] VolumeTable4 = BuildVolumeTable(16);

  // 4-bit channel/master gain: value 0 mutes the side, otherwise the exponential taper applies.
  private static double Vol4(int value) {
    value &= 0x0F;
    return value == 0 ? 0.0 : VolumeTable4[0x0F - value];
  }

  private static double[] BuildVolumeTable(int steps) {
    var t = new double[steps];
    for (var i = 0; i < steps; ++i)
      t[i] = Math.Pow(10.0, -1.5 / 20.0 * i);
    return t;
  }

  public PcePsg(int outputRate = OutputSampleRate) {
    for (var i = 0; i < this._channels.Length; ++i)
      this._channels[i] = new Channel { FrequencyPeriod = 1 };
    this._outputRate = outputRate;
    this._clocksPerSample = PsgClockHz / 32.0 / outputRate;
  }

  // ── register interface ─────────────────────────────────────────────────────────

  /// <summary>
  /// Writes one PSG register. <paramref name="port"/> is the low byte of the I/O address
  /// ($0800-$0808 → 0x00-0x08); <see cref="WritePort"/> accepts the full address.
  /// </summary>
  public void WriteRegister(int port, byte value) {
    switch (port & 0x0F) {
      case 0x00: // channel select
        this._selectedChannel = value & 0x07;
        break;
      case 0x01: // global L/R balance (MML)
        this._masterLeft = (value >> 4) & 0x0F;
        this._masterRight = value & 0x0F;
        break;
      case 0x02: // frequency low
        if (this._selectedChannel < 6) {
          var ch = this._channels[this._selectedChannel];
          ch.FrequencyPeriod = (ch.FrequencyPeriod & 0x0F00) | value;
        }
        break;
      case 0x03: // frequency high (4 bits)
        if (this._selectedChannel < 6) {
          var ch = this._channels[this._selectedChannel];
          ch.FrequencyPeriod = (ch.FrequencyPeriod & 0x00FF) | ((value & 0x0F) << 8);
        }
        break;
      case 0x04: // channel control: enable / DDA / overall volume
        if (this._selectedChannel < 6) {
          var ch = this._channels[this._selectedChannel];
          var wasEnabled = ch.Enabled;
          ch.Enabled = (value & 0x80) != 0;
          ch.DdaMode = (value & 0x40) != 0;
          ch.OverallVolume = value & 0x1F;
          // Disabling DDA-write mode resets the waveform write pointer (documented behaviour).
          if (!ch.DdaMode && (value & 0x40) == 0 && !wasEnabled)
            ch.WaveWriteIndex = 0;
        }
        break;
      case 0x05: // channel L/R volume
        if (this._selectedChannel < 6) {
          var ch = this._channels[this._selectedChannel];
          ch.LeftVolume = (value >> 4) & 0x0F;
          ch.RightVolume = value & 0x0F;
        }
        break;
      case 0x06: // waveform data / DDA sample
        if (this._selectedChannel < 6) {
          var ch = this._channels[this._selectedChannel];
          var sample = value & 0x1F;
          if (ch.DdaMode) {
            ch.DdaSample = sample;
          } else {
            ch.Waveform[ch.WaveWriteIndex & 0x1F] = sample;
            ch.WaveWriteIndex = (ch.WaveWriteIndex + 1) & 0x1F;
          }
        }
        break;
      case 0x07: // noise enable / frequency (channels 5-6 only)
        this._noiseEnabled = (value & 0x80) != 0;
        this._noisePeriod = (value & 0x1F) == 0 ? 1 : (value & 0x1F);
        break;
      case 0x08: // LFO frequency
        this._lfoFrequency = value;
        break;
      case 0x09: // LFO control
        this._lfoControl = value & 0x03;
        this._lfoEnabled = this._lfoControl != 0;
        break;
    }
  }

  /// <summary>Writes a PSG register addressed by its full I/O address ($0800-$0809).</summary>
  public void WritePort(ushort address, byte value) => this.WriteRegister(address - 0x0800, value);

  // ── stepping & mixing ───────────────────────────────────────────────────────

  // Advances every channel's waveform step counter by one PSG (clock/32) tick.
  private void StepOneTick() {
    for (var i = 0; i < 6; ++i) {
      var ch = this._channels[i];
      if (ch.DdaMode)
        continue; // DDA holds its sample; no stepping.
      var period = ch.FrequencyPeriod;
      // Channel 4 (index 3) is frequency-modulated by the LFO (channel 5, index 4) when enabled.
      if (i == 3 && this._lfoEnabled) {
        var lfoCh = this._channels[4];
        var mod = lfoCh.Waveform[lfoCh.WaveReadIndex & 0x1F] - 16;
        period += mod * (this._lfoFrequency == 0 ? 1 : this._lfoFrequency);
        if (period < 1) period = 1;
      }
      ch.StepCounter += 1.0;
      if (ch.StepCounter >= period) {
        ch.StepCounter -= period;
        ch.WaveReadIndex = (ch.WaveReadIndex + 1) & 0x1F;
      }
    }

    // Noise LFSR (channel 6) advances at its own divided rate.
    this._noiseCounter += 1.0;
    var nPeriod = this._noisePeriod * 4;
    if (this._noiseCounter >= nPeriod) {
      this._noiseCounter -= nPeriod;
      var bit = ((this._noiseLfsr ^ (this._noiseLfsr >> 1)) & 1);
      this._noiseLfsr = (this._noiseLfsr >> 1) | (bit << 16);
      this._noiseOutput = (this._noiseLfsr & 1) != 0 ? 15 : -15;
    }
  }

  private (double Left, double Right) MixOneTick() {
    double left = 0, right = 0;
    for (var i = 0; i < 6; ++i) {
      var ch = this._channels[i];
      if (!ch.Enabled)
        continue;

      int sample;
      if (ch.DdaMode) {
        sample = ch.DdaSample - 16; // centre the 5-bit unsigned value
      } else if (i == 5 && this._noiseEnabled) {
        sample = this._noiseOutput;
      } else {
        sample = ch.Waveform[ch.WaveReadIndex & 0x1F] - 16;
      }

      // PSG volume registers are louder-with-higher-value; the attenuation table is indexed by
      // (max − value), so value max → index 0 (full level). A zeroed 4-bit channel/master nibble
      // hard-mutes that side (the documented behaviour — a panned channel is silent on the off side).
      var overall = VolumeTable5[0x1F - (ch.OverallVolume & 0x1F)];
      var lAtt = Vol4(ch.LeftVolume) * Vol4(this._masterLeft);
      var rAtt = Vol4(ch.RightVolume) * Vol4(this._masterRight);
      left += sample * overall * lAtt;
      right += sample * overall * rAtt;
    }
    return (left, right);
  }

  /// <summary>Renders <paramref name="count"/> interleaved 16-bit stereo samples.</summary>
  public void RenderSamples(Span<short> output, int count) {
    for (var i = 0; i < count; ++i) {
      var (l, r) = this.RenderOneSample();
      output[i * 2] = l;
      output[i * 2 + 1] = r;
    }
  }

  private (short Left, short Right) RenderOneSample() {
    this._sampleError += this._clocksPerSample;
    var ticks = (int)this._sampleError;
    this._sampleError -= ticks;
    if (ticks < 1) ticks = 1;

    double sumL = 0, sumR = 0;
    for (var t = 0; t < ticks; ++t) {
      this.StepOneTick();
      var (l, r) = this.MixOneTick();
      sumL += l;
      sumR += r;
    }

    // Scale the mix into 16-bit headroom. Six channels of ±15 at full volume → ±90; map to a
    // comfortable level well below clipping.
    const double scale = 180.0;
    var left = (sumL / ticks) * scale;
    var right = (sumR / ticks) * scale;
    return (
      (short)Math.Clamp(left, short.MinValue, short.MaxValue),
      (short)Math.Clamp(right, short.MinValue, short.MaxValue));
  }

  // Internal hooks for unit testing without a full player.
  internal void StepForTest() => this.StepOneTick();
  internal (double Left, double Right) MixForTest() => this.MixOneTick();
}
