#pragma warning disable CS1591

namespace Codec.Ac3;

/// <summary>
/// AC-3 mantissa dequantization (ATSC A/52 §7.3). For each transform bin a bit-allocation pointer
/// (bap) selects the quantizer. baps 1, 2 and 4 group several mantissas into one bitstream word
/// (3-in-a-5-bit word, 3-in-a-7-bit word, 2-in-a-7-bit word respectively); baps 3 and 5 read a
/// small fixed-width index directly; baps 6..15 are linear two's-complement values. bap 0 is
/// "no bits": the bin is zero, or filled with deterministic dither (LFSR) when dithering is on.
/// </summary>
public sealed class Ac3Mantissas {

  private readonly Ac3BitReader _r;
  private uint _dither = 1;            // LFSR seed (deterministic; matches FFmpeg dither_gen behaviour)

  // Pending grouped-mantissa state (one queue per grouped bap).
  private int _grp3Count;             // bap 1 (3-level): 3 mantissas per 5-bit word
  private float _grp3a, _grp3b;
  private int _grp5Count;            // bap 2 (5-level): 3 mantissas per 7-bit word
  private float _grp5a, _grp5b;
  private int _grp11Count;           // bap 4 (11-level): 2 mantissas per 7-bit word
  private float _grp11a;

    /// <summary>
  /// Initializes a new instance of <see cref="Ac3Mantissas"/>.
  /// </summary>
public Ac3Mantissas(Ac3BitReader r) => this._r = r;

  /// <summary>
  /// Returns the next dequantized mantissa for a bin with the given <paramref name="bap"/>.
  /// <paramref name="dither"/> enables LFSR dither for bap 0. The returned value is the normalized
  /// (±1-range) mantissa before exponent scaling.
  /// </summary>
  public float Next(int bap, bool dither) {
    switch (bap) {
      case 0:
        return dither ? this.NextDither() : 0f;

      case 1: { // 3-level, grouped 3-per-5-bit-word
        if (this._grp3Count == 0) {
          var word = (int)this._r.ReadBits(5);
          // word = ((m0*3)+m1)*3+m2 in base-3.
          var m0 = word / 9;
          var m1 = (word / 3) % 3;
          var m2 = word % 3;
          this._grp3a = Ac3Tables.Quant3[m1];
          this._grp3b = Ac3Tables.Quant3[m2];
          this._grp3Count = 2;
          return Ac3Tables.Quant3[m0];
        }
        --this._grp3Count;
        return this._grp3Count == 1 ? this._grp3a : this._grp3b;
      }

      case 2: { // 5-level, grouped 3-per-7-bit-word
        if (this._grp5Count == 0) {
          var word = (int)this._r.ReadBits(7);
          var m0 = word / 25;
          var m1 = (word / 5) % 5;
          var m2 = word % 5;
          this._grp5a = Ac3Tables.Quant5[m1];
          this._grp5b = Ac3Tables.Quant5[m2];
          this._grp5Count = 2;
          return Ac3Tables.Quant5[m0];
        }
        --this._grp5Count;
        return this._grp5Count == 1 ? this._grp5a : this._grp5b;
      }

      case 3: { // 7-level, direct 3-bit index
        var idx = (int)this._r.ReadBits(3);
        return Ac3Tables.Quant7[idx];
      }

      case 4: { // 11-level, grouped 2-per-7-bit-word
        if (this._grp11Count == 0) {
          var word = (int)this._r.ReadBits(7);
          var m0 = word / 11;
          var m1 = word % 11;
          this._grp11a = Ac3Tables.Quant11[m1];
          this._grp11Count = 1;
          return Ac3Tables.Quant11[m0];
        }
        --this._grp11Count;
        return this._grp11a;
      }

      case 5: { // 15-level, direct 4-bit index
        var idx = (int)this._r.ReadBits(4);
        return Ac3Tables.Quant15[idx];
      }

      default: { // baps 6..15: linear, qbits two's-complement, scaled to ±1.
        var qbits = Ac3Tables.QuantizationBits[bap];
        var raw = this._r.ReadSigned(qbits);
        return raw / (float)(1 << (qbits - 1));
      }
    }
  }

  // Deterministic 16-bit LFSR dither generator (matches FFmpeg dither_int16/dither_gen polynomial),
  // mapped to a normalized mantissa in (-1, 1).
  private float NextDither() {
    var state = this._dither;
    // Galois LFSR with taps matching the AC-3 dither polynomial used by reference decoders.
    var lsb = state & 1;
    state >>= 1;
    if (lsb != 0)
      state ^= 0xB400u;
    this._dither = state;
    var s = (short)state;
    return s / 32768f;
  }
}
