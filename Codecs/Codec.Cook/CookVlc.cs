#pragma warning disable CS1591
namespace Codec.Cook;

/// <summary>
/// Canonical Huffman decoder reconstructing exactly what FFmpeg's
/// <c>ff_vlc_init_from_lengths</c> builds for the Cook VLCs. The reference derives a
/// length-per-symbol list from a 16-entry codes-per-length count array (<c>build_vlc</c>):
/// for each length L in 1..16, the next <c>counts[L-1]</c> entries of the symbol list get
/// that length. Codes are then assigned canonically in symbol-list order — the first code
/// is 0 and each subsequent code adds <c>1 &lt;&lt; (32 - len)</c> — and decoded MSB-first
/// (<c>get_vlc2</c>). Reproducing the same length assignment + canonical increment yields
/// bit-identical symbol decisions without porting FFmpeg's multi-level lookup tables.
/// </summary>
internal sealed class CookVlc {
  // Parallel arrays of (codeLength, code, symbol), sorted by increasing code so a longest-
  // prefix match while accumulating bits MSB-first picks the unique canonical code.
  private readonly int[] _lengths;
  private readonly uint[] _codes;
  private readonly int[] _symbols;

  /// <summary>
  /// Builds a VLC from a codes-per-length count table and a symbol list, applying
  /// <paramref name="offset"/> to every decoded symbol (the envelope VLCs use -12).
  /// </summary>
  public CookVlc(byte[] counts, int[] syms, int offset) {
    var n = 0;
    foreach (var c in counts)
      n += c;

    this._lengths = new int[n];
    this._codes = new uint[n];
    this._symbols = new int[n];

    var idx = 0;
    uint code = 0;
    for (var len = 1; len <= 16; ++len) {
      for (var k = 0; k < counts[len - 1]; ++k) {
        this._lengths[idx] = len;
        this._codes[idx] = code;
        this._symbols[idx] = syms[idx] + offset;
        ++idx;
        code += 1u << (32 - len);
      }
    }
  }

  /// <summary>
  /// Decodes one symbol MSB-first from <paramref name="reader"/> (<c>get_vlc2</c>). Reads
  /// bits one at a time, comparing the left-justified accumulator against the canonical
  /// codes of the current length until a match is found.
  /// </summary>
  public int Decode(CookBitReader reader) {
    uint acc = 0;
    var bits = 0;
    while (bits < 32) {
      acc = (acc << 1) | (uint)reader.GetBit();
      ++bits;
      // Left-justify the accumulated bits to compare against the 32-bit canonical codes.
      var justified = acc << (32 - bits);
      for (var i = 0; i < this._lengths.Length; ++i)
        if (this._lengths[i] == bits && this._codes[i] == justified)
          return this._symbols[i];
    }
    return this._symbols.Length > 0 ? this._symbols[0] : 0;
  }
}
