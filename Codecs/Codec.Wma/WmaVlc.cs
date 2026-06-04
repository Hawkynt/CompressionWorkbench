#pragma warning disable CS1591

namespace Codec.Wma;

/// <summary>
/// Variable-length-code reader equivalent to FFmpeg's <c>vlc_init</c> +
/// <c>get_vlc2</c>: each symbol has an explicit code value and bit length, and decoding
/// walks the bitstream MSB-first matching against (length, code) pairs. FFmpeg builds a
/// multi-level lookup table for speed; this reads bit-by-bit which is functionally
/// identical for the (small) WMA codebooks and avoids transcribing the table builder.
/// Symbols may carry an explicit value (the high-gain table) or default to their table
/// index (coefficient and exponent tables).
/// </summary>
internal sealed class WmaVlc {

  // Codes grouped by bit length; for each length, a map from code value to symbol.
  private readonly Dictionary<uint, int>[] _byLength;
  private readonly int _maxBits;

  private WmaVlc(Dictionary<uint, int>[] byLength, int maxBits) {
    this._byLength = byLength;
    this._maxBits = maxBits;
  }

  /// <summary>
  /// Builds a VLC from parallel code/bit arrays (symbol = array index). Mirrors
  /// <c>vlc_init(..., bits, codes, ...)</c> as used for the coefficient and AAC
  /// scalefactor (exponent) tables.
  /// </summary>
  public static WmaVlc FromCodes(uint[] codes, byte[] bits) {
    var symbols = new int[codes.Length];
    for (var i = 0; i < symbols.Length; ++i) symbols[i] = i;
    return Build(codes, bits, symbols);
  }

  /// <summary>
  /// Builds a VLC from a <c>{symbol, bits}</c> table where codes are assigned in
  /// canonical (length-then-order) fashion, mirroring
  /// <c>ff_vlc_init_from_lengths</c> used for the WMA high-gain table. The returned
  /// symbol is <c>tab[i][0] + symbolOffset</c>.
  /// </summary>
  public static WmaVlc FromLengths(byte[][] symbolBitsTab, int symbolOffset) {
    var n = symbolBitsTab.Length;
    var codes = new uint[n];
    var bits = new byte[n];
    var symbols = new int[n];
    // Canonical code assignment in table order, exactly as ff_vlc_init_from_lengths:
    // codes are handed out sequentially, left-justified, shifted per length.
    uint code = 0;
    var prevLen = 0;
    for (var i = 0; i < n; ++i) {
      var len = symbolBitsTab[i][1];
      if (i > 0) code = (code + 1) << (len - prevLen);
      codes[i] = code;
      bits[i] = len;
      symbols[i] = symbolBitsTab[i][0] + symbolOffset;
      prevLen = len;
    }
    return Build(codes, bits, symbols);
  }

  private static WmaVlc Build(uint[] codes, byte[] bits, int[] symbols) {
    var maxBits = 0;
    foreach (var b in bits) if (b > maxBits) maxBits = b;
    var byLength = new Dictionary<uint, int>[maxBits + 1];
    for (var i = 0; i <= maxBits; ++i) byLength[i] = new Dictionary<uint, int>();
    for (var i = 0; i < codes.Length; ++i) {
      var len = bits[i];
      if (len == 0) continue;
      byLength[len][codes[i]] = symbols[i];
    }
    return new WmaVlc(byLength, maxBits);
  }

  /// <summary>
  /// Decodes one symbol from <paramref name="reader"/> (FFmpeg <c>get_vlc2</c>).
  /// Returns the matched symbol; <paramref name="ok"/> is false when no prefix matched
  /// within the maximum code length (a stream error). Symbols may be negative, so the
  /// success flag is reported separately rather than via a sentinel return value.
  /// </summary>
  public int Decode(WmaBitReader reader, out bool ok) {
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
