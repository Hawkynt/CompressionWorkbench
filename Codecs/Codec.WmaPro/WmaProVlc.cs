#pragma warning disable CS1591

namespace Codec.WmaPro;

/// <summary>
/// Variable-length-code reader equivalent to FFmpeg's <c>ff_vlc_init_from_lengths</c> +
/// <c>get_vlc2</c> (<c>vlc.c</c> / <c>get_bits.h</c>). Each symbol is given an explicit
/// bit length; codes are assigned canonically by walking the table in order and
/// accumulating a left-justified 32-bit code (<c>code += 1 &lt;&lt; (32 - len)</c>), so
/// the code for a symbol is the top <c>len</c> bits of the accumulator at that point.
/// This matches the reference exactly even when the lengths in the table are not sorted
/// (the WMA Pro coefficient / scale-factor / vector tables interleave lengths freely),
/// which a naive "sort by length" canonical assignment would get wrong.
/// <para>
/// Decoding walks the bitstream MSB-first matching against (length, code) pairs. FFmpeg
/// builds a multi-level lookup table for speed; the bit-by-bit match here is functionally
/// identical for these (small) codebooks and avoids transcribing the table builder.
/// Symbols carry an explicit value plus a table-wide offset; the reference uses a
/// negative result (offset -1, symbol 0) to signal the vector-VLC escape, so the success
/// flag is reported separately and the raw signed symbol returned.
/// </para>
/// </summary>
internal sealed class WmaProVlc {

  // Codes grouped by bit length; for each length, a map from code value to symbol.
  private readonly Dictionary<uint, int>[] _byLength;
  private readonly int _maxBits;

  private WmaProVlc(Dictionary<uint, int>[] byLength, int maxBits) {
    this._byLength = byLength;
    this._maxBits = maxBits;
  }

  /// <summary>
  /// Builds a VLC from a <c>{symbol, length}</c> table (the WMA Pro scale-factor, coef1
  /// and vec2/vec1 tables). <paramref name="symbolOffset"/> is added to every symbol,
  /// mirroring <c>ff_vlc_init_from_lengths(..., offset, ...)</c>.
  /// </summary>
  public static WmaProVlc FromSymbolLengths(byte[][] symbolLenTab, int symbolOffset) {
    var n = symbolLenTab.Length;
    var lens = new byte[n];
    var syms = new int[n];
    for (var i = 0; i < n; ++i) {
      syms[i] = symbolLenTab[i][0] + symbolOffset;
      lens[i] = symbolLenTab[i][1];
    }
    return Build(syms, lens);
  }

  /// <summary>
  /// Builds a VLC from parallel length / symbol arrays (the WMA Pro coef0 and vec4
  /// tables, whose symbols live in a separate array). <paramref name="symbolOffset"/> is
  /// added to every symbol.
  /// </summary>
  public static WmaProVlc FromLengthsAndSymbols(byte[] lens, int[] symbols, int symbolOffset) {
    var n = lens.Length;
    var syms = new int[n];
    for (var i = 0; i < n; ++i) syms[i] = symbols[i] + symbolOffset;
    return Build(syms, lens);
  }

  private static WmaProVlc Build(int[] symbols, byte[] lens) {
    var maxBits = 0;
    foreach (var b in lens) if (b > maxBits) maxBits = b;
    var byLength = new Dictionary<uint, int>[maxBits + 1];
    for (var i = 0; i <= maxBits; ++i) byLength[i] = new Dictionary<uint, int>();

    // Canonical, left-justified code assignment exactly as ff_vlc_init_from_lengths:
    // process symbols in table order; the code for a length-`len` symbol is the top
    // `len` bits of a 32-bit accumulator that advances by (1 << (32 - len)) each step.
    uint code = 0;
    for (var i = 0; i < symbols.Length; ++i) {
      var len = lens[i];
      if (len <= 0) continue;
      var value = code >> (32 - len);
      byLength[len][value] = symbols[i];
      code += 1u << (32 - len);
    }
    return new WmaProVlc(byLength, maxBits);
  }

  /// <summary>
  /// Decodes one symbol from <paramref name="reader"/> (FFmpeg <c>get_vlc2</c>). Returns
  /// the matched symbol; <paramref name="ok"/> is false when no prefix matched within the
  /// maximum code length (a stream error). Symbols may be negative, so the success flag
  /// is reported separately rather than via a sentinel return value.
  /// </summary>
  public int Decode(WmaProBitReader reader, out bool ok) {
    uint code = 0;
    for (var len = 1; len <= this._maxBits; ++len) {
      code = (code << 1) | reader.GetBit();
      if (this._byLength[len].TryGetValue(code, out var sym)) {
        ok = true;
        return sym;
      }
    }
    ok = false;
    return 0;
  }
}
