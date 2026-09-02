#pragma warning disable CS1591
using Codec.Ay8910;
using Codec.Ym2612;

namespace Codec.Ym2203;

/// <summary>
/// Yamaha YM2203 (OPN) synthesis core: three four-operator FM channels plus a YM2149/AY-3-8910
/// compatible SSG (three square waves, noise and a hardware envelope). The FM section is the
/// classic OPN block — the same die-extracted log-sine / exponential operator, eight algorithms,
/// per-operator feedback, DT/MUL phase generation and channel-3 per-operator "special" mode that
/// the YM2612 (OPN2) inherited; this implementation therefore reuses
/// <see cref="Ym2612Codec"/> driven through its port-0 register bus (channels 1-3) for the FM
/// voices, and <see cref="Ay8910Chip"/> for the SSG. Unlike OPN2 the YM2203 has no DAC, no
/// stereo L/R routing and no second port — the chip is monaural.
/// <para>Register map: <c>$00-$0F</c> are the AY-compatible SSG registers and <c>$20-$B6</c> are
/// the FM registers (an exact subset of the OPN2 port-0 map). The FM section runs at
/// <c>clock / 72</c> and the SSG at <c>clock / 16</c>; both are mixed to a single mono channel.</para>
/// <para>References: MAME ymfm (Aaron Giles) and the YM2203 application manual for the register
/// map and the OPN/SSG clock prescalers.</para>
/// </summary>
public sealed class Ym2203Codec {

  /// <summary>FM sample-rate divisor: the OPN FM section runs at <c>clock / 72</c>.</summary>
  public const int FmPrescale = 72;

  /// <summary>SSG prescaler: the AY-compatible SSG runs at <c>clock / 16</c>.</summary>
  public const int SsgPrescale = 16;

  private readonly Ym2612Codec _fm;
  private readonly Ay8910Chip _ssg;
  private readonly double _fmRate;
  private int _ssgLatch;

  /// <param name="clock">Chip input clock in Hz (3993600 on the X1, 3579545 on many arcades).</param>
  /// <summary>
  /// Initializes a new instance of <see cref="Ym2203Codec"/>.
  /// </summary>
public Ym2203Codec(double clock = 3993600.0) {
    // The OPN FM core is the OPN2 FM core; build a YM2612 clocked so its clock/144 native rate
    // equals the YM2203 clock/72 FM rate (i.e. feed it twice the clock).
    this._fm = new Ym2612Codec(clock * Ym2612Codec.Prescale / FmPrescale);
    this._fmRate = clock / FmPrescale;
    // The SSG is a YM2149; render it mono at 44.1 kHz from the same input clock.
    this._ssg = new Ay8910Chip(clock, Ay8910Chip.StereoMode.Mono);
  }

  /// <summary>The FM section's native output sample rate (<c>clock / 72</c>).</summary>
  public double FmSampleRate => this._fmRate;

  /// <summary>The SSG render rate (fixed 44.1 kHz, matching <see cref="Ay8910Chip"/>).</summary>
  public static int SsgSampleRate => Ay8910Chip.OutputSampleRate;

  /// <summary>
  /// Writes one register. <c>$00-$0F</c> address the SSG (the low nibble of the value of the
  /// even "address latch" write selects the register); <c>$20-$B6</c> address the FM section.
  /// The VGM command supplies the register number and value together.
  /// </summary>
  public void Write(int address, int value) {
    address &= 0xFF;
    value &= 0xFF;
    if (address < 0x10) {
      this._ssg.WriteReg(address, (byte)value);
      this._ssgLatch = address;
      return;
    }
    // FM registers map straight onto OPN2 port 0 (channels 1-3, channel-3 special mode, LFO is
    // absent on OPN but the registers are ignored harmlessly).
    this._fm.Write(0, address, value);
  }

  /// <summary>Renders one FM frame at the FM native rate and returns its mono level.</summary>
  public short RenderFmSample() {
    this._fm.RenderSample(out var l, out var r);
    return (short)((l + r) >> 1);
  }

  /// <summary>Renders <paramref name="count"/> mono SSG samples into <paramref name="buffer"/>.</summary>
  public void RenderSsgSamples(Span<short> buffer, int count) {
    // Ay8910Chip emits interleaved stereo; in Mono mode both sides are identical, so take left.
    var stereo = new short[count * 2];
    this._ssg.RenderSamples(stereo, count);
    for (var i = 0; i < count; ++i)
      buffer[i] = stereo[i * 2];
  }
}
