#pragma warning disable CS1591
namespace Codec.Sid;

/// <summary>
/// The SID per-voice ADSR envelope generator, modelled on the reSID-documented behaviour:
/// an 8-bit envelope counter clocked by a 15-bit rate counter whose period comes from the
/// datasheet attack/decay/release rate table, plus an exponential segment table that slows
/// the decay/release as the level falls (the analog capacitor discharge curve). The
/// segments break at envelope levels 255/93/54/26/14/6, where the rate-counter divisor
/// steps 1 → 2 → 4 → 8 → 16 → 30.
/// <para>The rate table is the datasheet 2 ms..8 s set referenced to the 1 MHz-class SID
/// clock. The generator is stepped once per SID clock cycle.</para>
/// </summary>
public sealed class SidEnvelope {

  private enum State { Attack, DecaySustain, Release }

  // reSID rate-counter periods (the number of clocks between rate-counter ticks) indexed
  // by the 4-bit attack/decay/release nibble. These are the datasheet values at 1 MHz.
  private static readonly ushort[] RatePeriods = [
    9, 32, 63, 95, 149, 220, 267, 313,
    392, 977, 1954, 3126, 3907, 11720, 19532, 31251,
  ];

  // Exponential decay segments: when the envelope counter reaches one of these levels the
  // effective period is multiplied by the paired divisor, approximating the analog curve.
  private static readonly byte[] SegmentLevels = [255, 93, 54, 26, 14, 6, 0];
  private static readonly byte[] SegmentDivisors = [1, 2, 4, 8, 16, 30, 30];

  private State _state = State.Release;
  private bool _gate;

  private byte _attack;
  private byte _decay;
  private byte _sustain;   // 0..255 (sustain nibble replicated into both nibbles)
  private byte _release;

  private int _rateCounter;
  private byte _envelope;   // 0..255 current envelope level

  /// <summary>The current envelope level, 0..255.</summary>
  public byte Level => this._envelope;

  /// <summary>ATDC register write: high nibble = attack rate, low nibble = decay rate.</summary>
  public void WriteAttackDecay(byte value) {
    this._attack = (byte)(value >> 4);
    this._decay = (byte)(value & 0x0F);
  }

  /// <summary>SURE register write: high nibble = sustain level, low nibble = release rate.</summary>
  public void WriteSustainRelease(byte value) {
    var sr = (byte)(value >> 4);
    this._sustain = (byte)(sr << 4 | sr); // sustain level = SR replicated into both nibbles
    this._release = (byte)(value & 0x0F);
  }

  /// <summary>Gate bit (bit 0 of the control register). A rising edge starts the attack; a falling edge starts the release.</summary>
  public void Gate(bool on) {
    if (on && !this._gate) {
      this._state = State.Attack;
      this._rateCounter = 0;
    } else if (!on && this._gate) {
      this._state = State.Release;
      this._rateCounter = 0;
    }
    this._gate = on;
  }

  private int CurrentDivisor() {
    // The active exponential segment divisor for the current envelope level.
    for (var i = 0; i < SegmentLevels.Length - 1; ++i)
      if (this._envelope <= SegmentLevels[i] && this._envelope > SegmentLevels[i + 1])
        return SegmentDivisors[i];
    return SegmentDivisors[^1];
  }

  /// <summary>Advances the envelope by one SID clock cycle.</summary>
  public void Clock() {
    var rate = this._state switch {
      State.Attack => RatePeriods[this._attack],
      State.DecaySustain => RatePeriods[this._decay],
      _ => RatePeriods[this._release],
    };

    // Attack is linear; decay/release follow the exponential segment divisor.
    var divisor = this._state == State.Attack ? 1 : this.CurrentDivisor();

    if (++this._rateCounter < rate * divisor)
      return;
    this._rateCounter = 0;

    switch (this._state) {
      case State.Attack:
        if (this._envelope < 0xFF)
          ++this._envelope;
        if (this._envelope == 0xFF)
          this._state = State.DecaySustain;
        break;
      case State.DecaySustain:
        if (this._envelope > this._sustain)
          --this._envelope;
        break;
      case State.Release:
        if (this._envelope > 0)
          --this._envelope;
        break;
    }
  }
}
