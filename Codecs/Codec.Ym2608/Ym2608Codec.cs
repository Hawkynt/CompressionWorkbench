#pragma warning disable CS1591
using Codec.Ay8910;
using Codec.Ym2612;

namespace Codec.Ym2608;

/// <summary>
/// Yamaha YM2608 (OPNA) synthesis core: six four-operator FM channels with stereo per-channel
/// L/R routing, a YM2149/AY-3-8910 compatible SSG, a built-in six-voice rhythm (ADPCM-A) section
/// and a single ADPCM-B "delta-T" channel. The FM block is the OPNA superset of the OPN block —
/// architecturally identical to the OPN2 (YM2612) FM section: the same die-extracted
/// log-sine / exponential operator, eight algorithms, feedback, DT/MUL phase generation, LFO and
/// per-channel L/R enables. This implementation reuses <see cref="Ym2612Codec"/> for the six FM
/// channels (port 0 = channels 1-3, port 1 = channels 4-6, exactly the OPN2 layout) and
/// <see cref="Ay8910Chip"/> for the SSG.
/// <para>The OPNA FM section runs at <c>clock / 144</c> (six channels share a 24-slot operator
/// pipeline, the same divisor as OPN2) and the SSG at <c>clock / 32</c>. FM is stereo; the SSG is
/// summed into both channels.</para>
/// <para>Gaps: the rhythm (ADPCM-A) drum voices require the chip's internal sample ROM and the
/// ADPCM-B channel requires a streamed sample (a VGM data block). When neither is supplied those
/// sections stay silent and the caller can surface a note. The register writes are still accepted
/// so the FM/SSG mix is unaffected.</para>
/// <para>References: MAME ymfm (Aaron Giles) and the YM2608 application manual for the register
/// map, the prescalers and the rhythm/ADPCM-B layout.</para>
/// </summary>
public sealed class Ym2608Codec {

  /// <summary>FM sample-rate divisor: the OPNA FM section runs at <c>clock / 144</c>.</summary>
  public const int FmPrescale = 144;

  /// <summary>SSG prescaler: the OPNA SSG runs at <c>clock / 32</c>.</summary>
  public const int SsgPrescale = 32;

  private readonly Ym2612Codec _fm;
  private readonly Ay8910Chip _ssg;
  private readonly double _fmRate;
  private bool _rhythmRequested;
  private bool _adpcmBRequested;

  /// <param name="clock">Chip input clock in Hz (7987200 on the PC-88/98).</param>
  /// <summary>
  /// Initializes a new instance of <see cref="Ym2608Codec"/>.
  /// </summary>
  public Ym2608Codec(double clock = 7987200.0) {
    this._fm = new Ym2612Codec(clock);          // OPNA FM rate == OPN2 FM rate == clock / 144
    this._fmRate = clock / FmPrescale;
    // SSG runs at clock/32 on OPNA (vs clock/16 on OPN); halve the clock so Ay8910's /16 matches.
    this._ssg = new Ay8910Chip(clock / 2.0, Ay8910Chip.StereoMode.Mono);
  }

  /// <summary>The FM section's native output sample rate (<c>clock / 144</c>).</summary>
  public double FmSampleRate => this._fmRate;

  /// <summary>The SSG render rate (fixed 44.1 kHz, matching <see cref="Ay8910Chip"/>).</summary>
  public static int SsgSampleRate => Ay8910Chip.OutputSampleRate;

  /// <summary>True once a rhythm key-on was requested (the rhythm ROM is not modelled).</summary>
  public bool RhythmRequested => this._rhythmRequested;

  /// <summary>True once an ADPCM-B start was requested (no streamed sample is modelled).</summary>
  public bool AdpcmBRequested => this._adpcmBRequested;

  /// <summary>
  /// Writes one register on the given port. Port 0 carries the SSG (<c>$00-$0F</c>), the rhythm
  /// section (<c>$10-$1F</c>) and FM channels 1-3 (<c>$20-$B6</c>); port 1 carries the ADPCM-B
  /// registers (<c>$00-$1F</c>) and FM channels 4-6 (<c>$30-$B6</c>).
  /// </summary>
  public void Write(int port, int address, int value) {
    address &= 0xFF;
    value &= 0xFF;

    if (port == 0) {
      if (address < 0x10) {
        this._ssg.WriteReg(address, (byte)value);
        return;
      }
      if (address < 0x20) {
        // Rhythm section: $10 key-on/dump, $11 total level, $18-$1D per-voice. A key-on (any of
        // the low six bits of $10 set, dump bit clear) requests the built-in ADPCM-A samples.
        if (address == 0x10 && (value & 0x80) == 0 && (value & 0x3F) != 0)
          this._rhythmRequested = true;
        return;
      }
      this._fm.Write(0, address, value);  // FM channels 1-3
      return;
    }

    // Port 1.
    if (address < 0x20) {
      // ADPCM-B (delta-T) registers; $00 bit7 = start.
      if (address == 0x00 && (value & 0x80) != 0)
        this._adpcmBRequested = true;
      return;
    }
    this._fm.Write(1, address, value);  // FM channels 4-6
  }

  /// <summary>Renders one stereo FM frame at the FM native rate.</summary>
  public void RenderFmSample(out short left, out short right) => this._fm.RenderSample(out left, out right);

  /// <summary>Renders <paramref name="count"/> mono SSG samples into <paramref name="buffer"/>.</summary>
  public void RenderSsgSamples(Span<short> buffer, int count) {
    var stereo = new short[count * 2];
    this._ssg.RenderSamples(stereo, count);
    for (var i = 0; i < count; ++i)
      buffer[i] = stereo[i * 2];
  }
}
