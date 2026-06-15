#pragma warning disable CS1591

namespace Codec.Musepack;

/// <summary>
/// Canonical-Huffman variable-length-code decoder, the managed equivalent of
/// FFmpeg's <c>ff_vlc_init_from_lengths</c> + <c>get_vlc2</c> as used by the
/// Musepack SV8 decoder. Each book is described by a 16-entry length-count
/// histogram (number of symbols of code length 1..16) plus the symbol values in
/// canonical order. FFmpeg builds the per-length symbol list by walking the
/// histogram from the longest length down to the shortest; codes are then the
/// usual canonical assignment, consumed most-significant-bit-first.
/// </summary>
internal sealed class MpcVlc {

  // Parallel arrays sorted by (length asc, listing order): the canonical code,
  // its bit length, and the decoded symbol. Decoding walks bit-by-bit and matches
  // the accumulated prefix against the canonical code for the current length.
  private readonly int[] _codes;
  private readonly byte[] _lengths;
  private readonly int[] _symbols;
  private readonly int _maxLength;

  /// <summary>Number of symbols carried by this book (consumed from the shared symbol pool).</summary>
  public int SymbolCount { get; }

  /// <summary>
  /// Builds a VLC from a length-count histogram and the symbol pool. Symbols are
  /// taken in the FFmpeg order: longest code length first, shortest last — exactly
  /// how <c>build_vlc</c> populates its temporary <c>len[]</c> before calling
  /// <c>ff_vlc_init_from_lengths</c>. <paramref name="symbolOffset"/> is the bias
  /// FFmpeg applies to the stored symbol (negative for the q3/q4/q5..q8 books).
  /// </summary>
  public MpcVlc(IReadOnlyList<byte> lengthCounts, IReadOnlyList<byte> symbolPool, int poolStart, int symbolOffset) {
    var total = 0;
    for (var i = 0; i < 16; ++i)
      total += lengthCounts[i];
    this.SymbolCount = total;

    // FFmpeg's build_vlc emits symbols longest-length-first; capture the same
    // (symbol, length) pairing so the canonical codes line up bit-for-bit.
    var lenForEntry = new byte[total];
    var idx = 0;
    for (var len = 16; len >= 1; --len)
      for (var n = 0; n < lengthCounts[len - 1]; ++n)
        lenForEntry[idx++] = (byte)len;

    var symForEntry = new int[total];
    for (var i = 0; i < total; ++i)
      symForEntry[i] = symbolPool[poolStart + i] + symbolOffset;

    // Canonical code assignment: order by (length asc, original listing index).
    // Within ff_vlc_init_from_lengths the entries keep their listing order for a
    // given length, so stable-sort by length only.
    var order = new int[total];
    for (var i = 0; i < total; ++i)
      order[i] = i;
    Array.Sort(order, (a, b) => {
      var byLen = lenForEntry[a].CompareTo(lenForEntry[b]);
      return byLen != 0 ? byLen : a.CompareTo(b);
    });

    this._codes = new int[total];
    this._lengths = new byte[total];
    this._symbols = new int[total];

    var code = 0;
    var prevLen = 0;
    var maxLen = 0;
    for (var i = 0; i < total; ++i) {
      var e = order[i];
      var len = lenForEntry[e];
      if (prevLen != 0)
        code = (code + 1) << (len - prevLen);
      this._codes[i] = code;
      this._lengths[i] = len;
      this._symbols[i] = symForEntry[e];
      prevLen = len;
      if (len > maxLen)
        maxLen = len;
    }
    this._maxLength = maxLen;
  }

  /// <summary>Reads one symbol from <paramref name="reader"/>, MSB-first.</summary>
  public int Read(MpcBitReader reader) {
    var prefix = 0;
    var bits = 0;
    // The entries are length-sorted; track the index window for the current length
    // so the prefix only needs to be compared against codes of equal length.
    var start = 0;
    for (var len = 1; len <= this._maxLength; ++len) {
      prefix = (prefix << 1) | reader.GetBit();
      ++bits;
      for (var i = start; i < this._codes.Length && this._lengths[i] == len; ++i) {
        if (this._codes[i] == prefix)
          return this._symbols[i];
      }
      // Advance start past every entry whose length we've now fully examined.
      while (start < this._lengths.Length && this._lengths[start] <= len)
        ++start;
    }
    throw new InvalidDataException("Musepack: invalid VLC code (no matching prefix).");
  }
}
