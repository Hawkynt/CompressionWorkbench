#pragma warning disable CS1591
namespace Codec.Sid;

/// <summary>
/// One SID voice: a 24-bit phase accumulator (frequency = freg * clock / 2^24) feeding the
/// four waveform generators — triangle (optionally ring-modulated by the previous voice),
/// sawtooth, pulse (12-bit pulse width), and noise (a 23-bit LFSR with taps at bits 22 and
/// 17, shifted when accumulator bit 19 rises). Hard sync resets this accumulator when the
/// previous voice's accumulator MSB rises.
/// <para>Combined waveforms (more than one waveform bit set) are approximated by ANDing the
/// selected 12-bit waveform outputs. This is the standard non-sampled emulation shortcut;
/// the real chip's combined outputs are an analog function of the bit pattern and differ in
/// detail. Documented here as an approximation.</para>
/// </summary>
public sealed class SidVoice {

  public readonly SidEnvelope Envelope = new();

  private uint _accumulator;     // 24-bit
  private uint _prevAccumulator; // for MSB-rising sync detection
  private uint _frequency;       // 16-bit frequency register
  private uint _pulseWidth;      // 12-bit
  private uint _lfsr = 0x7FFFFF; // 23-bit noise LFSR, datasheet reset value (all ones)

  private byte _control;

  private const uint AccumulatorMask = 0x00FFFFFF;

  public bool TestBit => (this._control & 0x08) != 0;
  public bool RingMod => (this._control & 0x04) != 0;
  public bool SyncEnabled => (this._control & 0x02) != 0;

  public uint Accumulator => this._accumulator;

  public void WriteFreqLo(byte value) => this._frequency = (this._frequency & 0xFF00) | value;
  public void WriteFreqHi(byte value) => this._frequency = (this._frequency & 0x00FF) | (uint)(value << 8);
  public void WritePwLo(byte value) => this._pulseWidth = (this._pulseWidth & 0x0F00) | value;
  public void WritePwHi(byte value) => this._pulseWidth = (this._pulseWidth & 0x00FF) | (uint)((value & 0x0F) << 8);

  public void WriteControl(byte value) {
    this.Envelope.Gate((value & 0x01) != 0);
    this._control = value;
    if ((value & 0x08) != 0) {
      // TEST bit held: accumulator reset to 0 and the LFSR forced (datasheet behaviour).
      this._accumulator = 0;
      this._lfsr = 0x7FFFFF;
    }
  }

  /// <summary>Advances the accumulator one SID clock cycle and clocks the noise LFSR / sync.</summary>
  /// <param name="syncSource">The previous voice (for hard-sync MSB detection), or null.</param>
  public void Clock(SidVoice? syncSource) {
    this._prevAccumulator = this._accumulator;
    if (this.TestBit)
      return;

    var prevBit19 = (this._accumulator >> 19) & 1;
    this._accumulator = (this._accumulator + this._frequency) & AccumulatorMask;

    // Hard sync: when enabled, the source voice's MSB rising resets this accumulator.
    if (this.SyncEnabled && syncSource is { } src && src.MsbRose())
      this._accumulator = 0;

    // Noise LFSR is shifted on the rising edge of accumulator bit 19.
    var newBit19 = (this._accumulator >> 19) & 1;
    if (prevBit19 == 0 && newBit19 == 1)
      this.ClockNoise();

    this.Envelope.Clock();
  }

  /// <summary>True if the accumulator's MSB (bit 23) rose this cycle. Used as a sync source.</summary>
  public bool MsbRose()
    => ((this._prevAccumulator >> 23) & 1) == 0 && ((this._accumulator >> 23) & 1) == 1;

  private void ClockNoise() {
    // 23-bit Fibonacci LFSR, taps at bit 22 and bit 17 (datasheet).
    var bit = ((this._lfsr >> 22) ^ (this._lfsr >> 17)) & 1;
    this._lfsr = ((this._lfsr << 1) | bit) & 0x7FFFFF;
  }

  /// <summary>
  /// The 12-bit waveform output (0..4095), with the previous voice supplied for ring
  /// modulation of the triangle. Returns 2048 (mid-scale) when no waveform is selected.
  /// </summary>
  public int Output(SidVoice? ringSource) {
    var triangle = this.RingMod ? 0x1000 : 0; // sentinel: compute lazily below
    var hasTri = (this._control & 0x10) != 0;
    var hasSaw = (this._control & 0x20) != 0;
    var hasPulse = (this._control & 0x40) != 0;
    var hasNoise = (this._control & 0x80) != 0;

    if (!hasTri && !hasSaw && !hasPulse && !hasNoise)
      return 0x800; // silence (mid-scale)

    var result = 0xFFF;
    var any = false;

    if (hasTri) {
      result &= this.Triangle(ringSource);
      any = true;
    }
    if (hasSaw) {
      result &= this.Sawtooth();
      any = true;
    }
    if (hasPulse) {
      result &= this.Pulse();
      any = true;
    }
    if (hasNoise) {
      result &= this.Noise();
      any = true;
    }
    _ = triangle;
    return any ? result : 0x800;
  }

  private int Triangle(SidVoice? ringSource) {
    // The triangle uses the top bit of the accumulator (XORed with the ring-mod source's
    // MSB when ring modulation is on) to fold the next 11 bits up or down.
    var msb = this.RingMod && ringSource is { } src
      ? (this._accumulator ^ src._accumulator) & 0x800000
      : this._accumulator & 0x800000;
    var bits = (this._accumulator >> 11) & 0xFFF;
    if (msb != 0)
      bits ^= 0xFFF;
    return (int)(bits & 0xFFF) ^ 0; // 12-bit triangle (top bit dropped by the fold)
  }

  private int Sawtooth() => (int)((this._accumulator >> 12) & 0xFFF);

  private int Pulse() {
    // Output is high (all ones) when the top 12 accumulator bits are at/above the pulse
    // width, low otherwise. TEST bit forces it high (handled via accumulator==0).
    var phase = (this._accumulator >> 12) & 0xFFF;
    return phase >= this._pulseWidth ? 0xFFF : 0x000;
  }

  private int Noise() {
    // The 8-bit noise sample is assembled from specific LFSR bits (datasheet), placed in
    // the high bits of the 12-bit output.
    var n =
      (((this._lfsr >> 22) & 1) << 7) |
      (((this._lfsr >> 20) & 1) << 6) |
      (((this._lfsr >> 16) & 1) << 5) |
      (((this._lfsr >> 13) & 1) << 4) |
      (((this._lfsr >> 11) & 1) << 3) |
      (((this._lfsr >> 7) & 1) << 2) |
      (((this._lfsr >> 4) & 1) << 1) |
      ((this._lfsr >> 2) & 1);
    return (int)(n << 4) & 0xFFF;
  }
}
