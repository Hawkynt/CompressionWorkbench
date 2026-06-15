#pragma warning disable CS1591
namespace Codec.Sn76489;

/// <summary>
/// TI SN76489 / SEGA VDP PSG synthesis core. The chip carries three square-wave
/// tone channels and one noise channel, each with a 4-bit attenuator. Registers are
/// programmed through a one-byte bus protocol:
/// <list type="bullet">
///   <item>A <b>latch/data</b> byte (bit 7 set) selects a register —
///     bits 6-5 the channel (0..3), bit 4 the type (0 = tone/period, 1 = volume) —
///     and carries the low 4 data bits in bits 3-0.</item>
///   <item>A <b>data</b> byte (bit 7 clear) carries 6 more data bits (bits 5-0) for the
///     most recently latched register; for a tone period these become the high 6 bits of
///     the 10-bit value, for volume the low 4 bits are used.</item>
/// </list>
/// <para>Each tone channel toggles its output every <c>period</c> input clocks after the
/// internal /16 divide, so the output frequency is <c>clock / (32 * period)</c>. A period
/// register of 0 is treated as 0x400 on the SEGA VDP variant (so the divider never collapses
/// to a DC short); this core follows that variant.</para>
/// <para>The noise channel is driven by a 16-bit linear-feedback shift register. On the SEGA
/// VDP variant the white-noise tap mask is <c>0x0009</c> (bits 0 and 3 XOR-fed back) and
/// periodic noise feeds back bit 0 alone. The shift rate comes from the control register's
/// low two bits: 0x10/0x20/0x40 of the clock, or — for value 3 — the tone-2 period
/// ("tone2" mode). Bit 2 selects white (1) vs periodic (0) feedback.</para>
/// <para>Attenuation is a 4-bit value in 2 dB steps; 0xF mutes. The volume table is
/// <c>32767 * 10^(-attenuation * 0.1)</c> (i.e. -2 dB per step).</para>
/// <para>Game Gear stereo (the second PSG port, <c>0x4F</c> in VGM) is supported optionally
/// via <see cref="WriteStereo"/>; by default every channel plays to both speakers and the
/// mono mix is duplicated to left and right.</para>
/// </summary>
public sealed class Sn76489Codec {

  /// <summary>Output sample rate of <see cref="RenderSamples"/>.</summary>
  public const int OutputSampleRate = 44100;

  // 4-bit attenuation → linear amplitude: 32767 * 10^(-a*0.1), a=0xF → mute.
  private static readonly short[] VolumeTable = BuildVolumeTable();

  private static short[] BuildVolumeTable() {
    var table = new short[16];
    for (var a = 0; a < 15; ++a)
      table[a] = (short)Math.Round(32767.0 * Math.Pow(10.0, -a * 0.1));
    table[15] = 0;
    return table;
  }

  /// <summary>The 4-bit attenuation → amplitude table (index 0xF = mute).</summary>
  public static IReadOnlyList<short> Volumes => VolumeTable;

  private readonly double _clock;

  // Per tone channel: 10-bit period register, divider countdown, output polarity.
  private readonly int[] _tonePeriod = new int[3];
  private readonly int[] _toneCounter = new int[3];
  private readonly int[] _toneOutput = new int[3]; // +1 / -1

  // Volume (attenuation) per channel 0..3.
  private readonly int[] _attenuation = [0xF, 0xF, 0xF, 0xF];

  // Noise channel state.
  private int _noiseControl;            // low 3 bits programmed via channel 3 tone latch
  private int _noiseCounter;
  private int _noiseShift = NoiseSeed;
  private int _noiseOutput;             // +1 / -1, taken from shift register bit 0

  private const int NoiseSeed = 1 << 15;          // SEGA 16-bit LFSR initial state
  private const int NoiseWhiteTap = 0x0009;       // SEGA VDP white-noise feedback taps

  // Game Gear stereo enable bitmap: bits 0-3 = right enable for ch0-3, bits 4-7 = left.
  private int _stereo = 0xFF;

  // Bus latch: which register the next data byte updates.
  private int _latchedChannel;
  private int _latchedType;             // 0 = tone, 1 = volume

  // Fractional clock-step accumulator: how many chip cycles advance per output sample.
  private readonly double _cyclesPerSample;
  private double _cycleAccumulator;

  /// <param name="clock">PSG input clock in Hz (e.g. 3579545 for the Mega Drive).</param>
  public Sn76489Codec(double clock = 3579545.0) {
    this._clock = clock;
    // The chip prescales the clock by 16 before the tone/noise dividers; advancing the
    // internal generators at clock/16 keeps the period maths exact.
    this._cyclesPerSample = clock / 16.0 / OutputSampleRate;
  }

  /// <summary>Programs the chip through one PSG bus byte (the VGM 0x50 / GYM 0x03 payload).</summary>
  public void Write(byte value) {
    if ((value & 0x80) != 0) {
      // Latch/data byte: select register and load the low data bits.
      this._latchedChannel = (value >> 5) & 0x03;
      this._latchedType = (value >> 4) & 0x01;
      var data = value & 0x0F;
      this.ApplyLowData(data);
    } else {
      // Data byte: high bits for the latched register.
      var data = value & 0x3F;
      this.ApplyHighData(data);
    }
  }

  private void ApplyLowData(int data) {
    if (this._latchedType == 1) {
      this._attenuation[this._latchedChannel] = data;
      return;
    }

    if (this._latchedChannel < 3) {
      this._tonePeriod[this._latchedChannel] = (this._tonePeriod[this._latchedChannel] & 0x3F0) | data;
    } else {
      // Noise control register: bits 0-2 are meaningful.
      this._noiseControl = data & 0x07;
      this._noiseShift = NoiseSeed; // reprogramming the noise mode reseeds the LFSR
    }
  }

  private void ApplyHighData(int data) {
    if (this._latchedType == 1) {
      this._attenuation[this._latchedChannel] = data & 0x0F;
      return;
    }

    if (this._latchedChannel < 3) {
      this._tonePeriod[this._latchedChannel] = (this._tonePeriod[this._latchedChannel] & 0x00F) | (data << 4);
    } else {
      this._noiseControl = data & 0x07;
      this._noiseShift = NoiseSeed;
    }
  }

  /// <summary>
  /// Programs the Game Gear stereo register (VGM command <c>0x4F</c>): bits 0-3 enable the
  /// right speaker for tone0-2/noise, bits 4-7 the left speaker.
  /// </summary>
  public void WriteStereo(byte value) => this._stereo = value;

  /// <summary>
  /// Renders <paramref name="count"/> interleaved stereo frames (left, right) into
  /// <paramref name="buffer"/> at <see cref="OutputSampleRate"/>. The buffer must hold at
  /// least <c>2 * count</c> samples. Without a Game Gear stereo write every channel feeds
  /// both speakers, so left and right carry the same mono mix.
  /// </summary>
  public void RenderSamples(Span<short> buffer, int count) {
    for (var i = 0; i < count; ++i) {
      this._cycleAccumulator += this._cyclesPerSample;
      var steps = (int)this._cycleAccumulator;
      this._cycleAccumulator -= steps;
      for (var s = 0; s < steps; ++s)
        this.StepOneCycle();

      var (left, right) = this.Mix();
      buffer[i * 2] = left;
      buffer[i * 2 + 1] = right;
    }
  }

  // Advances every generator by one (prescaled) chip cycle.
  private void StepOneCycle() {
    for (var ch = 0; ch < 3; ++ch) {
      if (--this._toneCounter[ch] > 0)
        continue;
      var period = this._tonePeriod[ch];
      if (period == 0)
        period = 0x400;          // SEGA variant: 0 → 0x400 (avoids DC short)
      this._toneCounter[ch] = period;
      this._toneOutput[ch] = -this._toneOutput[ch];
      if (this._toneOutput[ch] == 0)
        this._toneOutput[ch] = 1;
    }

    if (--this._noiseCounter > 0)
      return;

    this._noiseCounter = this.NoisePeriod();
    this.ClockNoise();
  }

  private int NoisePeriod() {
    // Low two control bits select the shift rate: 0x10/0x20/0x40, or the tone-2 period.
    return (this._noiseControl & 0x03) switch {
      0 => 0x10,
      1 => 0x20,
      2 => 0x40,
      _ => this._tonePeriod[2] == 0 ? 0x400 : this._tonePeriod[2], // "tone2" mode
    };
  }

  private void ClockNoise() {
    var white = (this._noiseControl & 0x04) != 0;
    int feedback;
    if (white) {
      // XOR of the tapped bits feeds the top of the 16-bit register.
      var tapped = this._noiseShift & NoiseWhiteTap;
      feedback = Parity(tapped);
    } else {
      // Periodic noise: bit 0 feeds straight back.
      feedback = this._noiseShift & 1;
    }

    this._noiseShift = (this._noiseShift >> 1) | (feedback << 15);
    this._noiseOutput = (this._noiseShift & 1) != 0 ? 1 : -1;
  }

  private static int Parity(int value) {
    value ^= value >> 8;
    value ^= value >> 4;
    value ^= value >> 2;
    value ^= value >> 1;
    return value & 1;
  }

  private (short Left, short Right) Mix() {
    var ch0 = this._toneOutput[0] * VolumeTable[this._attenuation[0]];
    var ch1 = this._toneOutput[1] * VolumeTable[this._attenuation[1]];
    var ch2 = this._toneOutput[2] * VolumeTable[this._attenuation[2]];
    var noise = this._noiseOutput * VolumeTable[this._attenuation[3]];

    // Game Gear stereo gating; default _stereo = 0xFF feeds every channel to both speakers.
    var left = 0;
    var right = 0;
    if ((this._stereo & 0x10) != 0) left += ch0;
    if ((this._stereo & 0x20) != 0) left += ch1;
    if ((this._stereo & 0x40) != 0) left += ch2;
    if ((this._stereo & 0x80) != 0) left += noise;
    if ((this._stereo & 0x01) != 0) right += ch0;
    if ((this._stereo & 0x02) != 0) right += ch1;
    if ((this._stereo & 0x04) != 0) right += ch2;
    if ((this._stereo & 0x08) != 0) right += noise;

    // Four channels summed at full scale would overflow; scale by 1/4 to keep headroom.
    return (Clamp16(left / 4), Clamp16(right / 4));
  }

  private static short Clamp16(int value) =>
    value > 32767 ? (short)32767 : value < -32768 ? (short)-32768 : (short)value;
}
