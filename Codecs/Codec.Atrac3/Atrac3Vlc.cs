#pragma warning disable CS1591
namespace Codec.Atrac3;

/// <summary>
/// Canonical (Huffman) variable-length decoder equivalent to a table built by FFmpeg's
/// <c>ff_vlc_init_from_lengths</c>. Codes are assigned canonically — sorted by ascending
/// bit-length and, within a length, by the order the symbols are listed — exactly as the
/// reference. Decoding walks the bitstream MSB-first one bit at a time, which is
/// equivalent to (but simpler than) FFmpeg's multi-bit table lookup and yields identical
/// symbols.
/// </summary>
internal sealed class Atrac3Vlc {
  private readonly int[] _maxCodeForLen;  // largest assigned code value per bit-length (+1 sentinel), or -1
  private readonly int[] _firstCodeForLen;
  private readonly int[] _firstSymbolIndexForLen;
  private readonly int[] _symbols;
  private readonly int _maxLen;

  /// <summary>
  /// Builds a canonical decoder from listed (symbol, bit-length) pairs, applying
  /// <paramref name="symbolOffset"/> to every symbol (FFmpeg passes the VLC offset here).
  /// </summary>
  public Atrac3Vlc((int Symbol, int Bits)[] table, int start, int count, int symbolOffset) {
    var maxLen = 0;
    for (var i = 0; i < count; ++i)
      maxLen = Math.Max(maxLen, table[start + i].Bits);
    this._maxLen = maxLen;

    // Count codes per length.
    var lenCount = new int[maxLen + 1];
    for (var i = 0; i < count; ++i)
      ++lenCount[table[start + i].Bits];

    // Canonical first-code per length.
    this._firstCodeForLen = new int[maxLen + 2];
    this._firstSymbolIndexForLen = new int[maxLen + 2];
    this._maxCodeForLen = new int[maxLen + 2];
    this._symbols = new int[count];

    var code = 0;
    var symIndex = 0;
    for (var len = 1; len <= maxLen; ++len) {
      this._firstCodeForLen[len] = code;
      this._firstSymbolIndexForLen[len] = symIndex;
      // Symbols of this length in listed order.
      for (var i = 0; i < count; ++i) {
        if (table[start + i].Bits != len)
          continue;
        this._symbols[symIndex++] = table[start + i].Symbol + symbolOffset;
      }
      this._maxCodeForLen[len] = lenCount[len] > 0 ? code + lenCount[len] : -1;
      code = (code + lenCount[len]) << 1;
    }
  }

  /// <summary>Decodes one symbol from <paramref name="br"/>.</summary>
  public int Decode(Atrac3BitReader br) {
    var code = 0;
    for (var len = 1; len <= this._maxLen; ++len) {
      code = (code << 1) | br.GetBit();
      var maxCode = this._maxCodeForLen[len];
      if (maxCode >= 0 && code < maxCode) {
        var index = this._firstSymbolIndexForLen[len] + (code - this._firstCodeForLen[len]);
        return this._symbols[index];
      }
    }
    // Malformed / past-EOF bitstream: return the first symbol as a deterministic fallback.
    return this._symbols.Length > 0 ? this._symbols[0] : 0;
  }
}
