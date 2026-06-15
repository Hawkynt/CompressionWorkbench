#pragma warning disable CS1591
namespace Codec.Ralf;

/// <summary>
/// Canonical Huffman decoder built from RALF's nibble-packed code-length tables, reproducing
/// FFmpeg's <c>init_ralf_vlc</c> exactly: each element's code length is the packed nibble plus one,
/// and codes are assigned canonically in element order (<c>prefixes[len]++</c>). Decoding reads
/// MSB-first bits and returns the matching element index (the table symbol).
/// </summary>
internal sealed class RalfVlc {
  private readonly int _maxLen;
  private readonly int[] _firstCode = new int[18];   // first canonical code for each length
  private readonly int[] _firstSymbol = new int[18]; // index of first element for each length
  private readonly int[] _countForLen = new int[18];
  private readonly int[] _symbols;                   // element indices grouped by ascending length

  /// <summary>
  /// Builds the decoder from the flat nibble-packed length bytes of one table
  /// (<paramref name="data"/> sliced at <paramref name="offset"/>) covering
  /// <paramref name="elems"/> elements.
  /// </summary>
  public RalfVlc(byte[] data, int offset, int elems) {
    var lens = new int[elems];
    var counts = new int[18];
    var nb = 0; // 0 → high nibble, 1 → low nibble
    var p = offset;
    var maxBits = 0;
    for (var i = 0; i < elems; ++i) {
      var curLen = (nb != 0 ? data[p] & 0xF : data[p] >> 4) + 1;
      ++counts[curLen];
      if (curLen > maxBits)
        maxBits = curLen;
      lens[i] = curLen;
      p += nb;
      nb ^= 1;
    }
    this._maxLen = maxBits;

    // Canonical code assignment: prefixes[1] = 0; prefixes[i+1] = (prefixes[i] + counts[i]) << 1.
    var prefixes = new int[19];
    prefixes[1] = 0;
    for (var i = 1; i <= 16; ++i)
      prefixes[i + 1] = (prefixes[i] + counts[i]) << 1;

    // Group element indices by length in ascending order so a (length, offset) pair maps to a symbol.
    this._symbols = new int[elems];
    var lenStart = new int[19];
    var running = 0;
    for (var len = 1; len <= 17; ++len) {
      lenStart[len] = running;
      this._firstSymbol[len] = running;
      this._firstCode[len] = prefixes[len];
      this._countForLen[len] = counts[len];
      running += counts[len];
    }
    var cursor = (int[])lenStart.Clone();
    for (var i = 0; i < elems; ++i) {
      var len = lens[i];
      this._symbols[cursor[len]++] = i;
    }
  }

  /// <summary>Decodes one symbol (element index) from <paramref name="gb"/>.</summary>
  public int Decode(RalfBitReader gb) {
    var code = 0;
    for (var len = 1; len <= this._maxLen; ++len) {
      code = (code << 1) | gb.GetBit();
      var count = this._countForLen[len];
      if (count > 0) {
        var rel = code - this._firstCode[len];
        if (rel >= 0 && rel < count)
          return this._symbols[this._firstSymbol[len] + rel];
      }
    }
    return 0; // unreachable for well-formed (prefix-complete) tables
  }
}
